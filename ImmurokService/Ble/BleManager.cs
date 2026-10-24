using System.Runtime.InteropServices.WindowsRuntime;
using ImmurokCommon.Ble;
using ImmurokCommon.Models;
using ImmurokService.Security;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace ImmurokService.Ble;

/// <summary>
/// WinRT GATT 通信核心，移植自 macOS <c>BLEManager.swift</c>。
///
/// 连接模型（Windows 特有，与 macOS 一致的思路）：immurok 设备是**已配对的 BLE HID 键盘**，
/// 由系统自动保持连接。因此这里**不扫描广播**，而是枚举本机已配对/已连接的蓝牙设备，
/// 找到暴露 immurok 自定义服务 UUID 的那台，直接访问其 GATT（CMD/RSP 特征）。
///
/// 封包格式（写 CMD 特征）：<c>[command:1][length:1][payload...]</c>。
/// 通知从 RSP 特征以 Notify 到达；异步事件（0x21/0x23/0x11/0xF0/0xE1）单独路由，
/// 其余视为在途命令的响应。
/// </summary>
public sealed class BleManager : IAsyncDisposable
{
    private readonly ILogger<BleManager> _log;
    private readonly ImmurokSecurity _security;

    private DeviceWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _cmdChar;
    private GattCharacteristic? _rspChar;

    // OTA：独立服务/特征，与主命令通道分开。
    // 注意：OTA 特征是 Read/Write 型，无 Notify —— macOS 端采用「写入后轮询读取」
    // （固件异步操作未完成时返回空数据，主机每 0.2s 重读），此处照搬该模型。
    private GattDeviceService? _otaService;
    private GattCharacteristic? _otaChar;
    private readonly SemaphoreSlim _otaGate = new(1, 1);

    // 命令串行化：确保同一时刻只有一条命令在途（对齐 macOS commandInFlight）。
    private readonly SemaphoreSlim _cmdGate = new(1, 1);
    private TaskCompletionSource<byte[]?>? _pendingResponse;
    private ImmurokCommand _pendingCommandCode; // 在途命令码（用于「首个进度帧兼作 ENROLL_START ack」）
    private readonly object _pendingLock = new();

    // 连接串行化：避免 DeviceWatcher.Added 与启动查找并发重复连接。
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    // ---- 回连看门狗 ----
    // DeviceWatcher 的 Added/Updated 原本是唯一的重连触发源，但它靠不住：
    //   · 系统睡眠/休眠恢复、蓝牙适配器重置后 watcher 会静默变成 Stopped/Aborted，
    //     之后再也不回调，服务就一直断着直到重启；
    //   · Updated 只在被监听的属性**变化**时触发，错过那个沿就永远等下去；
    //   · TryConnectDeviceAsync 中途失败（GATT 忙、特征枚举空）后没有任何重试。
    // 实测即使走运能自愈也要 30–40 秒。这里加一个低频轮询兜底，自己去找、去连。
    private Timer? _reconnectTimer;
    private int _reconnectBusy;          // 0/1：tick 防重入
    private int _backoffIndex;
    private DateTime _lastTickUtc;
    private int _lastArmedSec;
    /// <summary>
    /// 退避节奏（秒）。断开后先密集试几次（设备多半就在手边），长时间找不到就拉到 60s ——
    /// 那时设备已经不在身边，快慢没有意义，只剩待机功耗有意义。
    /// </summary>
    private static readonly int[] BackoffSec = { 2, 2, 3, 5, 8, 13, 20, 30, 60 };
    /// <summary>在线时的巡检间隔。只做「链路还活着吗 / 是不是刚睡醒」两项检查，不扫描。</summary>
    private const int ConnectedTickSec = 5;
    /// <summary>每隔多少轮退避扫描做一次「强制探测」（连系统报告未连接的设备也试）。</summary>
    private const int ForceProbeEvery = 5;
    private int _scanRounds;
    /// <summary>上次成功连上的设备 id，跨断开保留，用于优先探测。</summary>
    private string? _lastKnownDeviceId;

    // 配对：设备按键后异步送来 33B 公钥。
    private TaskCompletionSource<byte[]>? _pendingPairPubKey;

    // 统一指纹门：命令回 [0x11 WAIT_FP] 后，等设备的最终结果通知。
    //   0x07 = 按错指纹（累计 3 次失败判负）；0x06 = 超时/终止；0x10 = 门已通过(继续等结果)；
    //   其余 = 结果字节（如 SLOT_CLEAR 的 [status]、OTP 的 [OK][6 位]）。
    private TaskCompletionSource<byte[]?>? _pendingFpGate;

    // 读秘密命令的主机侧指纹门预算（设计稿 §3.6 过渡方案），见 GateBudget 的注释。
    private readonly GateBudget _gateBudget = new();
    public GateBudget GateBudget => _gateBudget;
    private int _fpGateFails;

    /// <summary>进入指纹门（设备开始等待触摸）。用于终端提示。</summary>
    public event Action? FpGateRequired;

    /// <summary>指纹已通过、开始签名（设备回 0x10）。</summary>
    public event Action? FpGateApproved;

    /// <summary>指纹门期间按错指纹的回调（剩余次数）。</summary>
    public event Action<int>? FpGateAttemptFailed;

    private volatile bool _connected;
    private string? _connectedDeviceId;

    public bool IsConnected => _connected;
    public string? DeviceName { get; private set; }

    // ---- 对外事件（供 Worker / 上层订阅）----

    /// <summary>收到并通过 HMAC 验签的签名指纹匹配（0x21）。参数：pageId。</summary>
    public event Action<ushort>? SignedFingerprintMatched;

    /// <summary>
    /// 设备的长按锁屏请求（0x23，手指在传感器上按住 ≥1.6s）。与 0x21 指纹匹配相互独立，
    /// 无论指纹是否匹配都会触发，由消费方决定忽略还是执行（见 Worker.OnLockRequested）。
    /// </summary>
    public event Action? LockRequested;

    /// <summary>指纹录入进度事件（0x11）。参数：事件、当前次数 current、总次数 total。</summary>
    public event Action<FpEnrollEvent, int, int>? EnrollProgress;

    /// <summary>配对流程的实时阶段。</summary>
    public enum PairStage
    {
        /// <summary>登记第二台主机的第 1 步：设备在等已登记指纹。</summary>
        WaitFingerprint,
        /// <summary>等物理按键。首次配对是唯一一步；登记第二台主机时是第 2 步。</summary>
        WaitButton,
        /// <summary>已按键，设备正在算 ECDH。</summary>
        Computing,
        /// <summary>指纹门里按错了手指，设备仍在等（参数=剩余次数）。</summary>
        FingerprintRejected,
    }

    /// <summary>配对进度。参数：阶段、剩余重试次数（仅 FingerprintRejected 有意义，其余为 -1）。</summary>
    public event Action<PairStage, int>? PairProgress;

    /// <summary>当前配对指纹门里已按错的次数（对齐固件 FP_GATE_MAX_RETRIES=3）。</summary>
    private int _pairFpFails;
    private const int PairFpMaxRetries = 3;

    /// <summary>连接状态变化。</summary>
    public event Action<bool>? ConnectionChanged;

    public BleManager(ILogger<BleManager> log, ImmurokSecurity security)
    {
        _log = log;
        _security = security;
    }

    // ============ 发现 / 连接（枚举已配对设备）============

    /// <summary>
    /// 启动设备发现：先立即查一遍已配对设备，再用 DeviceWatcher 持续监听
    /// 设备的出现/消失（用于重连）。
    /// </summary>
    public void StartScan()
    {
        if (_watcher is not null) return;

        // 选择器：已配对的 BLE 设备。逐个探测是否暴露 immurok 服务。
        string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        _watcher = DeviceInformation.CreateWatcher(
            selector,
            new[] { "System.Devices.Aep.IsConnected" },
            DeviceInformationKind.AssociationEndpoint);

        _watcher.Added += OnDeviceAdded;
        _watcher.Updated += OnDeviceUpdated;
        _watcher.Removed += OnDeviceRemoved;
        _watcher.Start();
        _log.LogInformation("开始查找已配对的 immurok 设备…");

        // 启动即查一遍，命中立即连（不必等 watcher 回调）。
        _ = ScanOnceAsync(verbose: true, probeAll: true);

        StartReconnectWatchdog();
    }

    // ============ 回连看门狗 ============

    private void StartReconnectWatchdog()
    {
        if (_reconnectTimer is not null) return;
        _lastTickUtc = DateTime.UtcNow;
        _lastArmedSec = BackoffSec[0];
        _reconnectTimer = new Timer(OnReconnectTick, null,
            TimeSpan.FromSeconds(_lastArmedSec), Timeout.InfiniteTimeSpan);
    }

    private void ArmReconnect(int seconds)
    {
        _lastArmedSec = seconds;
        try { _reconnectTimer?.Change(TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* 服务停止中 */ }
    }

    private async void OnReconnectTick(object? _)
    {
        // 单次触发 + 每拍重新武装，所以正常不会重入；扫描慢过一拍时这道闸兜底。
        if (Interlocked.Exchange(ref _reconnectBusy, 1) == 1) return;
        try
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan gap = now - _lastTickUtc;
            _lastTickUtc = now;

            // 睡眠/休眠检测：进程被冻住时定时器不走，醒来后这一拍的实际间隔远大于预期。
            // 不用 SystemEvents.PowerModeChanged —— 服务跑在 Session 0，那套广播到不到手
            // 取决于系统版本，靠不住；时间跳变是自证的。
            if (gap > TimeSpan.FromSeconds(_lastArmedSec + 20))
            {
                _log.LogInformation("检测到系统从睡眠/休眠恢复（本拍实际间隔 {Sec}s），重建设备监听并立即回连",
                    (int)gap.TotalSeconds);
                _backoffIndex = 0;
                // 醒来时链路多半已经没了，但 ConnectionStatusChanged 未必补发过来。
                MarkDisconnectedIfLinkDead(force: true);
                RestartWatcher();
                return; // RestartWatcher 内部已经查了一遍
            }

            // 漏掉的断开：ConnectionStatusChanged 偶尔不来（尤其恢复之后），
            // 直接读设备的连接状态复核，免得抱着一条死链路当成在线。
            MarkDisconnectedIfLinkDead(force: false);

            if (_connected) { _backoffIndex = 0; return; }

            // watcher 停摆后不会再有任何回调，必须重建。
            DeviceWatcherStatus? st = _watcher?.Status;
            if (_watcher is null || st is DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted)
            {
                _log.LogWarning("设备监听已停止（Status={Status}），重建", st);
                RestartWatcher();
                return;
            }

            // 稳态只探测系统报告已连接的 AEP（近乎零成本）；每 ForceProbeEvery 轮强制全探一次，
            // 兜住「设备在身边、Windows 却还没把 HID 链路拉起来」这种需要我们主动捅一下的情况。
            bool probeAll = _scanRounds++ % ForceProbeEvery == 0;
            await ScanOnceAsync(verbose: false, probeAll).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "回连轮询异常");
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectBusy, 0);
            if (_connected)
            {
                _backoffIndex = 0;
                ArmReconnect(ConnectedTickSec);
            }
            else
            {
                ArmReconnect(BackoffSec[Math.Min(_backoffIndex, BackoffSec.Length - 1)]);
                if (_backoffIndex < BackoffSec.Length - 1) _backoffIndex++;
            }
        }
    }

    /// <summary>
    /// 复核链路是否真的还活着。<paramref name="force"/> 用于睡眠恢复：此时
    /// ConnectionStatus 也可能还没刷新，与其信它不如一律按断开重来。
    /// </summary>
    private void MarkDisconnectedIfLinkDead(bool force)
    {
        if (!_connected) return;
        bool dead = force || _device is null;
        if (!dead)
        {
            try { dead = _device!.ConnectionStatus == BluetoothConnectionStatus.Disconnected; }
            catch { dead = true; } // 对象已失效
        }
        if (!dead) return;

        _log.LogWarning("看门狗复核：链路已失效，按断开处理");
        HandleDisconnect();
    }

    private void RestartWatcher()
    {
        StopScan();
        StartScan(); // 重建 watcher 并立即查一遍
    }

    public void StopScan()
    {
        if (_watcher is null) return;
        _watcher.Added -= OnDeviceAdded;
        _watcher.Updated -= OnDeviceUpdated;
        _watcher.Removed -= OnDeviceRemoved;
        try { _watcher.Stop(); } catch { /* ignore */ }
        _watcher = null;
    }

    private const string AepIsConnected = "System.Devices.Aep.IsConnected";

    /// <summary>
    /// 枚举已配对 BLE 设备，探测 immurok 服务并尝试连接。
    /// 启动时跑一次，之后由回连看门狗按退避节奏反复跑，所以要控制成本：
    ///
    /// <para><paramref name="verbose"/>=false 时日志压到 Debug，否则断开期间几秒刷一屏。</para>
    ///
    /// <para><paramref name="probeAll"/>=false 时**只探测系统报告已连接的 AEP**。
    /// FindAllAsync 读的是 PnP 存储，不动射频，基本免费；真正贵的是
    /// FromIdAsync + GetGattServicesAsync(Uncached)——对不在身边的设备，它会发起一次
    /// 连接尝试直到超时，在待机状态下反复做就是实打实的射频功耗。设备不在身边时
    /// 常态就退化成一次注册表查询，由看门狗每隔几轮再强制探一次兜底。</para>
    /// </summary>
    private async Task ScanOnceAsync(bool verbose, bool probeAll)
    {
        LogLevel lvl = verbose ? LogLevel.Information : LogLevel.Debug;
        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            DeviceInformationCollection infos = await DeviceInformation.FindAllAsync(
                selector, new[] { AepIsConnected });

            // 上次连上的那台优先探测：稳态下一次就命中，不必把耳机鼠标挨个做服务发现。
            var ordered = infos.OrderByDescending(i => i.Id == _lastKnownDeviceId).ToList();

            int probed = 0;
            foreach (DeviceInformation info in ordered)
            {
                if (_connected) break;
                bool aepConnected = info.Properties.TryGetValue(AepIsConnected, out object? v) && v is true;
                if (!probeAll && !aepConnected) continue;
                probed++;
                await TryConnectDeviceAsync(info.Id, info.Name);
            }

            _log.Log(lvl, "已配对 BLE 设备 {Count} 台，探测 {Probed} 台（probeAll={All}）",
                infos.Count, probed, probeAll);
            if (!_connected)
                _log.Log(verbose ? LogLevel.Warning : LogLevel.Debug,
                    "未在已配对设备中找到 immurok（请确认设备已在系统蓝牙里配对并连接）");
        }
        catch (Exception ex)
        {
            _log.Log(lvl, ex, "查找 immurok 设备失败");
        }
    }

    private async void OnDeviceAdded(DeviceWatcher sender, DeviceInformation info)
    {
        if (_connected) return;
        await TryConnectDeviceAsync(info.Id, info.Name);
    }

    private async void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        // 设备从断开变为已连接时，尝试接上。
        if (_connected) return;
        await TryConnectDeviceAsync(update.Id, null);
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (_connected && update.Id == _connectedDeviceId)
        {
            _log.LogWarning("已连接的 immurok 设备被移除");
            HandleDisconnect();
        }
    }

    /// <summary>
    /// 尝试把某个已配对设备当作 immurok 连接：打开设备 → 查 immurok 服务 UUID →
    /// 若存在则认定为目标设备，拿 CMD/RSP 特征并订阅通知。
    /// </summary>
    private async Task TryConnectDeviceAsync(string deviceId, string? name)
    {
        await _connectGate.WaitAsync();
        try
        {
            if (_connected) return;

            BluetoothLEDevice? device = await BluetoothLEDevice.FromIdAsync(deviceId);
            if (device is null) return;

            // 一次性枚举全部服务（对齐 macOS discoverServices 的做法）——同一设备上
            // 连续多次 GetGattServicesForUuidAsync 会触发 0x80070016 ERROR_BAD_COMMAND。
            GattDeviceServicesResult svc = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (svc.Status != GattCommunicationStatus.Success)
            {
                device.Dispose();
                return;
            }

            GattDeviceService? service = null;
            GattDeviceService? otaService = null;
            foreach (GattDeviceService s in svc.Services)
            {
                if (s.Uuid == BleConstants.ImmurokService) service = s;
                else if (s.Uuid == BleConstants.OtaService) otaService = s;
                else s.Dispose(); // 不用的服务立即释放
            }

            // 无 immurok 服务 = 不是我们的设备。
            if (service is null)
            {
                otaService?.Dispose();
                device.Dispose();
                return;
            }

            _log.LogInformation("找到 immurok 设备: {Name} ({Id})", device.Name, deviceId);

            // 请求对该 GATT 服务的访问（部分场景下不请求会拿不到特征）。
            try
            {
                DeviceAccessStatus access = await service.RequestAccessAsync();
                if (access != DeviceAccessStatus.Allowed)
                    _log.LogWarning("RequestAccessAsync: {Access}", access);
            }
            catch (Exception ex) { _log.LogWarning(ex, "RequestAccessAsync 异常"); }

            // 首次枚举特征偶尔因 GATT 缓存/连接尚未稳定返回空，重试若干次并打印诊断。
            GattCharacteristic? cmd = null;
            GattCharacteristic? rsp = null;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                cmd = await GetCharacteristicAsync(service, BleConstants.CmdCharacteristic);
                rsp = await GetCharacteristicAsync(service, BleConstants.RspCharacteristic);
                if (cmd is not null && rsp is not null) break;

                // 诊断：列出该服务下实际存在的所有特征 UUID。
                GattCharacteristicsResult all = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (all.Status == GattCommunicationStatus.Success)
                    _log.LogWarning("第 {A}/4 次未取到 CMD/RSP；该服务实际特征: [{List}]",
                        attempt, string.Join(", ", all.Characteristics.Select(c => c.Uuid.ToString())));
                else
                    _log.LogWarning("第 {A}/4 次枚举全部特征失败: {Status}", attempt, all.Status);

                await Task.Delay(600);
            }

            if (cmd is null || rsp is null)
            {
                _log.LogError("未找到 CMD/RSP 特征（若反复如此，检查是否有僵尸 ImmurokService 进程占用设备，或重置设备蓝牙）");
                otaService?.Dispose();
                service.Dispose();
                device.Dispose();
                return;
            }

            // 订阅 RSP 通知。
            rsp.ValueChanged += OnRspValueChanged;
            GattCommunicationStatus cccd = await rsp.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (cccd != GattCommunicationStatus.Success)
            {
                _log.LogError("订阅 RSP 通知失败: {Status}", cccd);
                rsp.ValueChanged -= OnRspValueChanged;
                otaService?.Dispose();
                service.Dispose();
                device.Dispose();
                return;
            }

            _device = device;
            _service = service;
            _cmdChar = cmd;
            _rspChar = rsp;
            _connectedDeviceId = deviceId;
            _lastKnownDeviceId = deviceId; // 跨断开保留，下次扫描优先探它
            DeviceName = device.Name;
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            _connected = true;

            // OTA 特征（可选，不影响主功能）。服务已在上面的单次枚举中取得；
            // 只缓存不订阅 —— OTA 特征无 Notify，响应走「写入后轮询读取」（对齐 macOS otaWriteAndRead）。
            if (otaService is not null)
            {
                try
                {
                    _otaService = otaService;
                    _otaChar = await GetCharacteristicAsync(otaService, BleConstants.OtaCharacteristic);
                    if (_otaChar is not null)
                        _log.LogInformation("OTA 特征就绪");
                }
                catch (Exception ex) { _log.LogWarning(ex, "OTA 特征发现失败（不影响主功能）"); }
            }

            _log.LogInformation("已连接 immurok: {Name}", DeviceName);
            ConnectionChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "连接设备失败: {Id}", deviceId);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private static async Task<GattCharacteristic?> GetCharacteristicAsync(GattDeviceService service, Guid uuid)
    {
        GattCharacteristicsResult r = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        return r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0
            ? r.Characteristics[0] : null;
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _log.LogWarning("设备连接状态变为断开");
            HandleDisconnect();
        }
    }

    private void HandleDisconnect()
    {
        bool was = _connected;
        _connected = false;
        _connectedDeviceId = null;

        if (_rspChar is not null) { try { _rspChar.ValueChanged -= OnRspValueChanged; } catch { } _rspChar = null; }
        _cmdChar = null;
        _service?.Dispose();
        _service = null;
        _otaChar = null;
        _otaService?.Dispose();
        _otaService = null;
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }

        // 完成所有在途等待，避免悬挂。
        lock (_pendingLock) { _pendingResponse?.TrySetResult(null); _pendingResponse = null; }
        _pendingPairPubKey?.TrySetCanceled();
        _pendingFpGate?.TrySetResult(null);
        _pendingFpGate = null;
        _fpGateFails = 0;
        _gateBudget.Reset();

        if (was) ConnectionChanged?.Invoke(false);

        // DeviceWatcher 的 Added/Updated 可能带我们回来，但不能只指望它（见看门狗注释）。
        // 断开即回到最快的重试节奏：别让上一轮长时间找不到攒下的退避拖慢这次回连。
        // _scanRounds 一并归零，保证断开后的头一次扫描就是强制全探。
        _backoffIndex = 0;
        _scanRounds = 0;
        ArmReconnect(BackoffSec[0]);
    }

    // ============ 命令发送 ============

    /// <summary>
    /// 发送命令并等待响应。封包 <c>[cmd][len][payload]</c>。同一时刻只允许一条命令在途。
    /// 返回响应字节；超时或断开返回 null。
    /// </summary>
    public async Task<byte[]?> SendCommandAsync(
        ImmurokCommand command, byte[]? payload = null, int timeoutMs = 5000, CancellationToken ct = default)
    {
        payload ??= Array.Empty<byte>();
        if (!_connected || _cmdChar is null)
            return null;

        await _cmdGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock) { _pendingResponse = tcs; _pendingCommandCode = command; }

            byte[] packet = new byte[2 + payload.Length];
            packet[0] = (byte)command;
            packet[1] = (byte)payload.Length;
            Array.Copy(payload, 0, packet, 2, payload.Length);

            _log.LogDebug("TX cmd=0x{Cmd:X2} [{Hex}]", (byte)command, Convert.ToHexString(packet));

            // WriteWithResponse 要等对端 ATT ACK。设备执行完命令立刻重启的场景（SLOT_CLEAR
            // 自清槽、FACTORY_RESET）里这个 ACK 永远不会回来，链路断开时 WinRT 直接抛
            // OperationCanceledException/COMException，而不是给一个失败的 GattWriteResult。
            // 让它冒出去会把整条 IPC 请求变成 ERROR，调用方（如 ClearOwnSlotAsync）后面
            // 「清本地绑定」的收尾就跑不到了——本机于是留着一把设备已经作废的 shared_key。
            // 本方法对外的契约本就是「超时或断开返回 null」，这里补齐实现。
            GattWriteResult w;
            try
            {
                w = await _cmdChar.WriteValueWithResultAsync(packet.AsBuffer(), GattWriteOption.WriteWithResponse);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "写 CMD 0x{Cmd:X2} 时链路中断", (byte)command);
                lock (_pendingLock) { if (_pendingResponse == tcs) _pendingResponse = null; }
                return null;
            }

            if (w.Status != GattCommunicationStatus.Success)
            {
                _log.LogError("写 CMD 失败: {Status}", w.Status);
                lock (_pendingLock) { if (_pendingResponse == tcs) _pendingResponse = null; }
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("命令 0x{Cmd:X2} 超时", (byte)command);
                lock (_pendingLock) { if (_pendingResponse == tcs) _pendingResponse = null; }
                return null;
            }
        }
        finally
        {
            _cmdGate.Release();
        }
    }

    // ============ 通知分发 ============

    private void OnRspValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[] data = args.CharacteristicValue.ToArray();
        if (data.Length == 0) return;

        _log.LogDebug("RX [{Hex}]", Convert.ToHexString(data));

        // 异步事件优先分发；否则视为在途命令响应。
        switch (data[0])
        {
            case NotifyOpcode.SignedFpMatch when data.Length == 11:
                HandleSignedFpMatch(data);
                return;

            case NotifyOpcode.EnrollProgress when data.Length == 4:
                HandleEnrollProgress(data);
                return;

            case NotifyOpcode.PairStatus when data.Length == 6:
                // [0xF0, ...] 实为 BLE 连接参数更新通知，非配对；记录即可。
                _log.LogDebug("连接参数更新通知 0xF0");
                return;

            case NotifyOpcode.FpMatchEvent:
                // 0x23 是设备的「长按锁屏请求」（手指按住 ≥1.6s 触发），不是普通的指纹匹配事件。
                // ⚠️ 固件在每次触摸的上升沿之后固定 1.6s 就发这一帧，与指纹是否匹配无关，
                //    因此消费方必须做抑制（见 Worker.OnLockRequested）。
                // 不限定长度：早先要求 len==1，一旦固件带了 payload 就匹配不上，
                // 会一路掉到「在途命令响应」被静默吞掉，表现为长按毫无反应。
                _log.LogInformation("收到长按锁屏请求 0x23 [{Hex}]", Convert.ToHexString(data));
                LockRequested?.Invoke();
                return;

            case (byte)ImmurokCommand.PairButton when data.Length == 2: // 0x34
                HandlePairButtonEvent(data[1]);
                return;
        }

        // 统一指纹门结果分发（SLOT_CLEAR 跨槽 / KEY_DELETE / KEY_COMMIT / OTP 取码 等）。
        if (_pendingFpGate is not null
            && data[0] != NotifyOpcode.FpMatchEvent && data[0] != NotifyOpcode.LinkParams)
        {
            if (data.Length == 1 && data[0] == (byte)ImmurokStatus.ErrFpNotMatch) // 0x07 按错
            {
                _fpGateFails++;
                FpGateAttemptFailed?.Invoke(3 - _fpGateFails);
                if (_fpGateFails >= 3)
                {
                    var g = _pendingFpGate; _pendingFpGate = null;
                    g.TrySetResult(null);
                }
                return;
            }
            if (data.Length == 1 && data[0] == (byte)ImmurokStatus.ErrTimeout) // 0x06 终止
            {
                var g = _pendingFpGate; _pendingFpGate = null;
                g.TrySetResult(null);
                return;
            }
            if (data.Length == 1 && data[0] == 0x10) // FP_GATE_APPROVED：门过了但结果随后到，继续等
            {
                _gateBudget.Touched();
                FpGateApproved?.Invoke();
                return;
            }

            // 其余 = 结果。走到这里说明设备上刚过了一次真实指纹门（按错是 0x07、终止是 0x06）。
            _gateBudget.Touched();
            var gr = _pendingFpGate; _pendingFpGate = null;
            gr.TrySetResult(data);
            return;
        }

        // 配对：设备按键后送来的 33B 公钥（部分固件路径把公钥放在 [0x30][33B]）。
        var pairTcs = _pendingPairPubKey;
        if (pairTcs is not null && data.Length >= 34 && data[0] == (byte)ImmurokCommand.PairInit)
        {
            _pendingPairPubKey = null;
            pairTcs.TrySetResult(data[1..34]);
            return;
        }

        // 登记第二台主机时固件先挂指纹门，按错手指发 [0x07]、连错 3 次或门超时发 [0x06]
        // （hidkbd.c:3210 / 3197）。这两帧不带命令码，_pendingFpGate 在配对期间又是空的，
        // 原来会一路掉到下面的「在途命令响应」被当成未识别通知丢掉：用户按错手指界面毫无
        // 反应，设备放弃后还要干等外层 60s 才报失败。
        if (pairTcs is not null && data.Length == 1)
        {
            if (data[0] == (byte)ImmurokStatus.ErrFpNotMatch) // 0x07：还能再试
            {
                _pairFpFails++;
                int left = PairFpMaxRetries - _pairFpFails;
                _log.LogWarning("配对指纹门：不匹配，剩余 {Left} 次", left);
                PairProgress?.Invoke(PairStage.FingerprintRejected, left < 0 ? 0 : left);
                return;
            }
            if (data[0] == (byte)ImmurokStatus.ErrTimeout) // 0x06：设备已放弃指纹门
            {
                _log.LogWarning("配对指纹门：设备已终止（按错次数用尽或超时）");
                _pendingPairPubKey = null;
                pairTcs.TrySetException(new TimeoutException("pair fingerprint gate aborted"));
                return;
            }
        }

        // 其余：在途命令响应。
        lock (_pendingLock)
        {
            var tcs = _pendingResponse;
            _pendingResponse = null;
            if (tcs is null)
            {
                // 没有在途命令却收到帧 —— 这是一条我们尚未识别的设备事件。
                // 抬到 Information：不出声的话，固件发了什么永远查不出来。
                _log.LogInformation("收到未识别的设备通知（无在途命令）: [{Hex}]", Convert.ToHexString(data));
                return;
            }
            tcs.TrySetResult(data);
        }
    }

    private void HandleSignedFpMatch(byte[] data)
    {
        var (pageId, valid) = _security.VerifyFingerprintMatch(data);
        if (valid)
        {
            _log.LogInformation("签名指纹匹配通过 pageId={PageId}", pageId);
            _ = SendCommandAsync(ImmurokCommand.FpMatchAck, timeoutMs: 3000);
            SignedFingerprintMatched?.Invoke(pageId);
        }
        else
        {
            _log.LogWarning("签名指纹匹配验签失败，忽略");
        }
    }

    private void HandleEnrollProgress(byte[] data)
    {
        // 格式：[0x11, status, current, total]
        byte status = data[1];
        int current = data[2];
        int total = data[3];
        FpEnrollEvent ev = status switch
        {
            0x00 => FpEnrollEvent.Waiting,
            0x01 => FpEnrollEvent.Captured,
            0x02 => FpEnrollEvent.Processing,
            0x03 => FpEnrollEvent.LiftFinger,
            0x04 => FpEnrollEvent.Complete,
            0x06 => FpEnrollEvent.Overlap,
            0xFD or 0xFE or 0xFF => FpEnrollEvent.Failed,
            _ => (current == total && total > 0) ? FpEnrollEvent.Complete : FpEnrollEvent.Waiting,
        };

        // 首个进度帧兼作 ENROLL_START 的 ack（固件不单独回 ack）。
        lock (_pendingLock)
        {
            if (_pendingResponse is not null && _pendingCommandCode == ImmurokCommand.EnrollStart)
            {
                var tcs = _pendingResponse;
                _pendingResponse = null;
                tcs.TrySetResult(new byte[] { (byte)ImmurokStatus.Ok });
            }
        }

        _log.LogDebug("录入进度: {Event} {Cur}/{Total}", ev, current, total);
        EnrollProgress?.Invoke(ev, current, total);
    }

    /// <summary>
    /// 配对按键事件 <c>[0x34][status]</c>：
    /// 0x03=指纹已通过改等按键（登记第二台主机时出现）、0x01=已按键正在 ECDH、
    /// 0x00=30s 超时、0x02=取消。0x00/0x02 直接让配对等待失败；公钥 [0x30][33B] 才是成功信号。
    /// </summary>
    private void HandlePairButtonEvent(byte status)
    {
        switch (status)
        {
            case 0x03:
                _log.LogInformation("配对：指纹已通过，请按设备物理按键（登记第二台主机）");
                PairProgress?.Invoke(PairStage.WaitButton, -1);
                break;
            case 0x01:
                _log.LogInformation("配对：已按键，正在进行 ECDH…");
                PairProgress?.Invoke(PairStage.Computing, -1);
                break;
            case 0x00:
                _log.LogWarning("配对：按键 30s 超时");
                _pendingPairPubKey?.TrySetException(new TimeoutException("pair button timeout"));
                _pendingPairPubKey = null;
                break;
            case 0x02:
                _log.LogWarning("配对：已取消");
                _pendingPairPubKey?.TrySetException(new OperationCanceledException("pair cancelled"));
                _pendingPairPubKey = null;
                break;
        }
    }

    // ============ 高层操作 ============

    public async Task<bool> GetStatusAsync()
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.GetStatus);
        return r is { Length: >= 1 } && r[0] == (byte)ImmurokStatus.Ok;
    }

    /// <summary>
    /// 主机槽位状态（双主机）。响应 <c>[0x39][OK][bitmap][active]</c>：bit0=槽1、bit1=槽2；
    /// active=当前活跃槽（即本机）。旧固件回 <c>[0x39][0xFE]</c> 表示不支持双主机。
    /// 返回 Supported=false 表示不支持；null 表示无响应/未连接。
    /// </summary>
    public async Task<(bool Supported, byte Bitmap, byte Active)?> GetHostSlotStatusAsync()
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.SlotStatus);
        if (r is not { Length: >= 2 }) return null;
        if (r[1] != (byte)ImmurokStatus.Ok || r.Length < 4)
            return (false, 0, 0); // 旧固件/不支持
        return (true, r[2], r[3]);
    }

    /// <summary>
    /// 链路层当前实际生效的连接参数（GET_CONN_PARAMS 0x03，固件 1.7.2+），不是 PPCP 里的期望值。
    /// 响应 <c>[0x03][OK][interval:2 BE][latency:2 BE][timeout:2 BE]</c>，BLE 原生单位
    /// （interval ×1.25ms、latency 连接事件数、timeout ×10ms）。旧固件 / 无响应返回 null。
    /// </summary>
    public async Task<(ushort Interval, ushort Latency, ushort Timeout)?> GetConnParamsAsync()
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.GetConnParams, timeoutMs: 3000);
        if (r is not { Length: >= 8 } || r[0] != (byte)ImmurokCommand.GetConnParams || r[1] != (byte)ImmurokStatus.Ok)
            return null;
        return ((ushort)(r[2] << 8 | r[3]), (ushort)(r[4] << 8 | r[5]), (ushort)(r[6] << 8 | r[7]));
    }

    /// <summary>解绑本机的结果。</summary>
    public enum ClearOwnResult
    {
        /// <summary>设备在线：已通知设备清槽，本地也已清。</summary>
        ClearedOnDevice,
        /// <summary>设备不在线：只清了本地绑定数据，设备侧的槽仍占用。</summary>
        ClearedLocalOnly,
        /// <summary>设备明确拒绝（活跃槽不是本机）。</summary>
        Rejected,
    }

    /// <summary>
    /// 解绑本机所在的槽（SLOT_CLEAR 无 payload，无指纹门）。设备清槽后**重启**，
    /// 因此「断开/无响应/超时」= 成功；仅明确收到 0xF2（NOT_PAIRED，活跃槽不是本机）才是拒绝。
    /// 成功后清本地 shared_key。
    ///
    /// <para>待解绑的设备不在身边（未连接）时不能直接失败：否则本机会被旧绑定永久卡住、
    /// 无法改配新设备。此时退化为「只清本地绑定数据」——本机随后即可与新设备配对，
    /// 旧设备上的槽位等它下次连上再由用户清理。</para>
    /// </summary>
    public async Task<ClearOwnResult> ClearOwnSlotAsync()
    {
        if (!IsConnected)
        {
            _security.ClearPairing();
            _log.LogInformation("解绑本机：设备未连接，仅清除本地绑定数据");
            return ClearOwnResult.ClearedLocalOnly;
        }

        // 只有「设备明确回了拒绝」才保留本地绑定；断开、超时、写失败一概按已清处理。
        // 设备收到 0x3C 后当场清槽 + 擦 BLE bond + 重启，回复多半送不出来，这条路径才是常态。
        // 这里若因为拿不到确认就跳过 ClearPairing，本机会留着一把对端已作废的 shared_key，
        // 界面显示「已绑定的设备未连接」，而且再也解不掉（设备侧已经没这个槽了）。
        byte[]? r;
        try
        {
            r = await SendCommandAsync(ImmurokCommand.SlotClear, timeoutMs: 10000);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "解绑本机：发送 SLOT_CLEAR 期间链路中断，按设备已清处理");
            r = null;
        }

        if (r is { Length: >= 2 } && r[1] == 0xF2)
            return ClearOwnResult.Rejected; // 设备明确拒绝：本机不是活跃槽

        _security.ClearPairing();
        _log.LogInformation("解绑本机完成，本地绑定数据已清除");
        return ClearOwnResult.ClearedOnDevice;
    }

    /// <summary>
    /// 解绑另一台主机的槽（SLOT_CLEAR 带 [slot]，有指纹门）。设备先回 [0x11]，
    /// 用户触摸已登记指纹后回 1 字节状态。设备不重启，连接不断。
    /// </summary>
    public async Task<bool> ClearOtherSlotAsync(byte slot)
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.SlotClear, new[] { slot });
        if (r is not { Length: >= 1 }) return false;

        if (r[0] == (byte)ImmurokStatus.WaitFingerprint) // 0x11 → 等指纹
        {
            byte[]? res = await RunFpGateAsync(30000).ConfigureAwait(false);
            return res is { Length: >= 1 } && res[0] == (byte)ImmurokStatus.Ok;
        }
        // 无门路径 [0x3C][status]
        return r.Length >= 2 ? r[1] == 0x00 : r[0] == 0x00;
    }

    /// <summary>
    /// 取消当前指纹门：给设备发 GATE_CANCEL(0x37) 停止闪灯等待，并让本地等待立即失败。
    /// 用于客户端断开（Ctrl+C）时释放设备。
    /// </summary>
    public async Task CancelGateAsync()
    {
        if (_pendingFpGate is null) return;
        _log.LogInformation("取消指纹门（客户端断开）");
        try { await SendCommandAsync(ImmurokCommand.GateCancel, timeoutMs: 3000); }
        catch { /* 尽力而为 */ }
        var g = _pendingFpGate;
        _pendingFpGate = null;
        g?.TrySetResult(null);
    }

    /// <summary>命令回 WAIT_FP 后，等待指纹门结果。返回结果字节；失败/超时返回 null。</summary>
    private async Task<byte[]?> RunFpGateAsync(int timeoutMs)
    {
        _fpGateFails = 0;
        var g = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFpGate = g;
        FpGateRequired?.Invoke();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return await g.Task.WaitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { _pendingFpGate = null; return null; }
    }

    /// <summary>
    /// 指纹位图。命令 <c>FP_LIST(0x13)</c>，响应 <c>[status][bitmap]</c>，bit0-4 对应槽 0-4（最多 5 枚）。
    /// 返回 null 表示读取失败（未连接/超时）。
    /// </summary>
    public async Task<byte?> GetFingerprintBitmapAsync()
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.FpList);
        if (r is { Length: >= 2 } && r[0] == (byte)ImmurokStatus.Ok)
            return r[1];
        return null;
    }

    /// <summary>指纹槽列表（最多 5 槽）。读取失败返回 null。</summary>
    public async Task<List<FingerprintSlot>?> GetFingerprintListAsync()
    {
        byte? bitmap = await GetFingerprintBitmapAsync();
        if (bitmap is null) return null;
        var slots = new List<FingerprintSlot>();
        for (byte i = 0; i < 5; i++)
            slots.Add(new FingerprintSlot { SlotId = i, Enrolled = (bitmap.Value & (1 << i)) != 0 });
        return slots;
    }

    public async Task<bool> StartEnrollmentAsync(byte slotId)
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.EnrollStart, new[] { slotId });
        if (r is not { Length: >= 1 }) return false;
        byte s = r[0];
        return s == (byte)ImmurokStatus.Ok || s == (byte)ImmurokStatus.WaitFingerprint;
    }

    public async Task<bool> CancelEnrollmentAsync()
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.EnrollCancel);
        return r is { Length: >= 1 } && r[0] == (byte)ImmurokStatus.Ok;
    }

    /// <summary>
    /// 删除指纹（含指纹门）。设备回 0x11 时**必须**等门结果再返回：
    /// 早先直接把 WAIT_FP 当成功返回，客户端会在用户还没触摸传感器时就刷新列表，
    /// 于是显示「已删除」但槽位仍在——删除看起来没生效。
    /// </summary>
    public async Task<bool> DeleteFingerprintAsync(byte slotId)
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.DeleteFp, new[] { slotId });
        if (r is not { Length: >= 1 }) return false;
        if (r[0] == (byte)ImmurokStatus.Ok) return true;
        if (r[0] == (byte)ImmurokStatus.WaitFingerprint)
        {
            byte[]? res = await RunFpGateAsync(30000).ConfigureAwait(false);
            return res is { Length: >= 1 } && res[0] == (byte)ImmurokStatus.Ok;
        }
        return false;
    }

    /// <summary>
    /// ECDH 配对。流程：PAIR_INIT → 设备回 WAIT_BUTTON(0xF0) → 用户按设备键 →
    /// 设备送 33B 公钥 → 我方 PAIR_CONFIRM(附我方公钥) → 完成 HKDF 并存 shared_key。
    /// </summary>
    /// <param name="fpGateFirst">
    /// 设备上已有另一台主机时为 true：固件会先挂指纹门、通过后才挂按键门
    /// （hidkbd.c 的 slot2_enroll 分支）。两条路径的 PAIR_INIT 响应都是 WAIT_BUTTON，
    /// 从响应里分不出来，只能由调用方按槽位状态告诉我们，用于发出正确的起始阶段。
    /// </param>
    public async Task<PairFailureReason> StartPairingAsync(
        bool fpGateFirst = false, int retries = 3, CancellationToken ct = default)
    {
        LastPairFailureDetail = null;
        if (!_connected) return PairFail("NOT_CONNECTED");
        _pairFpFails = 0;

        byte[]? initResp = await SendCommandAsync(ImmurokCommand.PairInit, timeoutMs: 5000, ct: ct);
        if (initResp is not { Length: >= 1 })
            return PairFail("INIT_NO_RESPONSE");

        if (initResp[0] == NotifyOpcode.LinkParams)
        {
            if (retries <= 0) return PairFailureReason.LinkParams;
            await Task.Delay(5000, ct);
            return await StartPairingAsync(fpGateFirst, retries - 1, ct);
        }

        if (initResp.Length < 2 || initResp[0] != (byte)ImmurokCommand.PairInit)
            return PairFail($"INIT_UNEXPECTED_{Convert.ToHexString(initResp)}");

        byte[] devicePubKey;

        if (initResp.Length == 2)
        {
            byte code = initResp[1];
            if (code == (byte)ImmurokStatus.ErrNeedsReset) return PairFailureReason.NeedsReset;
            if (code != (byte)ImmurokStatus.ErrWaitButton) return PairFail($"INIT_STATUS_{code:X2}");

            // 设备已进入等待：告诉界面现在该做什么。后续 0x34 通知会把阶段推着往前走。
            PairProgress?.Invoke(
                fpGateFirst ? PairStage.WaitFingerprint : PairStage.WaitButton, -1);

            var pairTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPairPubKey = pairTcs;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(60000);
                devicePubKey = await pairTcs.Task.WaitAsync(cts.Token);
            }
            catch (Exception ex)
            {
                // 超时(0x34 00)/取消(0x34 02)/外层 60s 兜底 均归为按键步骤未完成。
                _pendingPairPubKey = null;
                _log.LogWarning("配对等待结束: {Msg}", ex.Message);
                return PairFailureReason.WaitButtonTimeout;
            }
        }
        else if (initResp.Length >= 34)
        {
            devicePubKey = initResp[1..34];
        }
        else
        {
            return PairFail($"INIT_SHORT_{Convert.ToHexString(initResp)}");
        }

        byte[] appPubKey = _security.StartPairing();
        byte[]? confirmResp = await SendCommandAsync(ImmurokCommand.PairConfirm, appPubKey, timeoutMs: 8000, ct: ct);
        if (confirmResp is not { Length: >= 2 }
            || confirmResp[0] != (byte)ImmurokCommand.PairConfirm
            || confirmResp[1] != (byte)ImmurokStatus.Ok)
        {
            return PairFail(confirmResp is null
                ? "CONFIRM_NO_RESPONSE"
                : $"CONFIRM_{Convert.ToHexString(confirmResp)}");
        }

        bool ok = _security.CompletePairing(devicePubKey);
        return ok ? PairFailureReason.None : PairFail("KEY_DERIVE_FAILED");
    }

    /// <summary>
    /// 最近一次配对落到 <see cref="PairFailureReason.Generic"/> 的具体位置（阶段 + 设备原始回包十六进制），
    /// 跟在 <c>ERROR:GENERIC:</c> 后面回给客户端显示。只含协议字节，不含密钥材料。
    /// </summary>
    public string? LastPairFailureDetail { get; private set; }

    private PairFailureReason PairFail(string detail)
    {
        LastPairFailureDetail = detail;
        _log.LogWarning("配对失败: {Detail}", detail);
        return PairFailureReason.Generic;
    }

    public void ResetPairing() => _security.ClearPairing();

    // ============ 设备信息（GET_STATUS 0x01）============

    /// <summary>设备信息：是否已配对（设备侧）、电量%、固件版本、指纹数。</summary>
    public async Task<(bool Paired, int? Battery, string? Firmware, int FpCount)?> GetDeviceInfoAsync()
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.GetStatus);
        if (r is not { Length: >= 3 } || r[0] != (byte)ImmurokStatus.Ok)
            return null;

        byte bitmap = r[1];
        bool paired = r[2] != 0;
        int? battery = r.Length >= 4 ? r[3] : null;
        string? fw = null;
        if (r.Length >= 9)
        {
            int build = (r[7] << 8) | r[8];
            fw = $"{r[4]}.{r[5]}.{r[6]}.{build:x}";
        }
        else if (r.Length >= 7)
        {
            fw = $"{r[4]}.{r[5]}.{r[6]}";
        }
        int fpCount = 0;
        for (int i = 0; i < 5; i++) if ((bitmap & (1 << i)) != 0) fpCount++;
        return (paired, battery, fw, fpCount);
    }

    // ============ OTA 传输 ============

    public bool OtaAvailable => _connected && _otaChar is not null;

    /// <summary>
    /// 写 OTA 特征后轮询读取响应（对齐 macOS <c>otaWriteAndRead</c>）：
    /// 固件异步操作（如 ERASE）未完成时读到空数据，每 200ms 重读，直到非空或超时。
    /// </summary>
    public async Task<byte[]?> OtaWriteReadAsync(byte[] data, int timeoutMs)
    {
        var otaChar = _otaChar;
        if (otaChar is null) return null;
        await _otaGate.WaitAsync().ConfigureAwait(false);
        try
        {
            GattWriteResult w = await otaChar.WriteValueWithResultAsync(data.AsBuffer(), GattWriteOption.WriteWithResponse);
            if (w.Status != GattCommunicationStatus.Success)
            {
                _log.LogError("OTA 写失败: {Status}", w.Status);
                return null;
            }

            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                try
                {
                    GattReadResult r = await otaChar.ReadValueAsync(BluetoothCacheMode.Uncached);
                    if (r.Status == GattCommunicationStatus.Success)
                    {
                        byte[] resp = r.Value.ToArray();
                        if (resp.Length > 0)
                        {
                            _log.LogDebug("OTA RX [{Hex}]", Convert.ToHexString(resp));
                            return resp;
                        }
                        // 空数据 = 固件异步操作仍在进行，继续轮询。
                    }
                }
                catch (Exception ex) { _log.LogDebug(ex, "OTA 轮询读异常，重试"); }
                await Task.Delay(200).ConfigureAwait(false);
            }
            _log.LogWarning("OTA 读超时 ({Timeout}ms)", timeoutMs);
            return null;
        }
        finally { _otaGate.Release(); }
    }

    /// <summary>
    /// 写 OTA 特征的 PROM 数据块。
    ///
    /// <para><paramref name="sync"/>=false 用 Write Command（无响应），最快但**无背压**：
    /// WinRT 把包交给系统蓝牙栈缓冲后立即返回，并不代表已经上天线。macOS 侧靠
    /// CoreBluetooth 的 <c>canSendWriteWithoutResponse</c> 天然限流，Windows 无对等 API，
    /// 于是全部数据块会在几秒内排队完毕、进度瞬间冲到 100%，而实际射频传输还要 70~80s。</para>
    ///
    /// <para><paramref name="sync"/>=true 改用 Write Request（有响应）作为**顺序屏障**：
    /// ATT 保证同一连接上的操作按序处理，因此该响应返回时，此前排队的所有 Write Command
    /// 都已被对端消费掉。每隔若干块插一次即可把队列深度钳住，让进度反映真实传输进度。
    /// 应用层命令字节完全相同（仍是 0x80 数据块），固件侧处理逻辑不变；该特征本就支持
    /// 有响应写（INFO/ERASE/HEADER/END 走的就是这条路径）。</para>
    /// </summary>
    public async Task<bool> OtaWriteChunkAsync(byte[] data, bool sync = false)
    {
        if (_otaChar is null) return false;
        try
        {
            GattWriteResult w = await _otaChar.WriteValueWithResultAsync(
                data.AsBuffer(),
                sync ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse);
            return w.Status == GattCommunicationStatus.Success;
        }
        catch (Exception ex) { _log.LogError(ex, "OTA 数据块写失败 (sync={Sync})", sync); return false; }
    }

    /// <summary>写 OTA 特征（无响应，用于 PROM 数据块）。</summary>
    public Task<bool> OtaWriteNoResponseAsync(byte[] data) => OtaWriteChunkAsync(data, sync: false);

    // ============ 密钥库（SSH=0 / OTP=1 / API=2）============

    public const byte CatSsh = 0, CatOtp = 1, CatApi = 2;

    private static int NameLen(byte cat) => cat switch { CatSsh => 16, CatOtp => 30, _ => 32 };

    /// <summary>某类密钥条目数（KEY_COUNT）。响应 [OK][count][checksum:4B]。</summary>
    public async Task<int> GetKeyCountAsync(byte cat)
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.KeyCount, new[] { cat });
        if (r is { Length: >= 2 } && r[0] == (byte)ImmurokStatus.Ok) return r[1];
        return 0;
    }

    /// <summary>读条目名（KEY_READ offset 0）。响应 [OK][total][off][data...]。</summary>
    public async Task<string?> ReadKeyNameAsync(byte cat, byte idx)
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.KeyRead, new[] { cat, idx, (byte)0 });
        if (r is not { Length: > 3 } || r[0] != (byte)ImmurokStatus.Ok) return null;
        int n = Math.Min(NameLen(cat), r.Length - 3);
        return CStr(r, 3, n);
    }

    /// <summary>读 OTP 条目名 + 服务（name 0-29, service 30-59）。</summary>
    public async Task<(string Name, string Service)?> ReadOtpNameServiceAsync(byte idx)
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.KeyRead, new[] { CatOtp, idx, (byte)0 });
        if (r is not { Length: > 3 } || r[0] != (byte)ImmurokStatus.Ok) return null;
        int dataLen = r.Length - 3;
        string name = CStr(r, 3, Math.Min(30, dataLen));
        string svc = dataLen > 30 ? CStr(r, 33, Math.Min(30, dataLen - 30)) : "";
        return (name, svc);
    }

    /// <summary>读取整条条目（分块 KEY_READ，off 递增至设备报告的可读大小）。用于 imk get 读 API 值。</summary>
    public async Task<byte[]?> ReadKeyEntryAsync(byte cat, byte idx)
    {
        if (!await EnsureSecretBudgetAsync("KEY_READ").ConfigureAwait(false)) return null;
        byte[]? acc = null;
        int total = 0, off = 0;
        for (int guard = 0; guard < 64; guard++)
        {
            byte[]? r = await SendCommandAsync(ImmurokCommand.KeyRead, new[] { cat, idx, (byte)off });
            if (r is { Length: >= 1 } && r[0] == (byte)ImmurokStatus.WaitFingerprint)
                r = await RunFpGateAsync(30000);
            if (r is not { Length: > 3 } || r[0] != (byte)ImmurokStatus.Ok) return null;
            int devTotal = r[1], roff = r[2], chunk = r.Length - 3;
            if (acc is null) { total = devTotal > 0 ? devTotal : 160; acc = new byte[total]; }
            int copy = Math.Min(chunk, total - roff);
            if (copy <= 0) return null;
            Array.Copy(r, 3, acc, roff, copy);
            off = roff + copy;
            if (off >= total) return acc;
        }
        return acc;
    }

    /// <summary>
    /// 读秘密命令（OTP 取码 / API 值 / 签名）转发前的预算检查。预算耗尽就先发 AUTH_REQUEST——
    /// 固件对它永远要求新触摸——逼一次真实触摸；用户没摸 / 按错 / 超时返回 false，调用方按拒绝处理。
    /// 不能只依赖固件自己的门：它的 cooldown 会被每次通过续期（见 <see cref="Ble.GateBudget"/>）。
    /// </summary>
    private async Task<bool> EnsureSecretBudgetAsync(string what)
    {
        if (_gateBudget.TryUse()) return true;
        _log.LogInformation("{What}：指纹门预算耗尽，要求重新触摸", what);
        if (!await AuthenticateAsync().ConfigureAwait(false))
        {
            _log.LogInformation("{What}：重新触摸未通过，拒绝", what);
            return false;
        }
        return _gateBudget.TryUse();
    }

    public enum GateOutcome { Ok, Rejected, Timeout }

    /// <summary>裸指纹认证（AUTH_REQUEST 0x33，含指纹门）。用于 imk agent 运行高危命令前的用户在场确认。</summary>
    public async Task<bool> AuthenticateAsync(int timeoutMs = 30000)
        => await AuthenticateDetailedAsync(timeoutMs).ConfigureAwait(false) == GateOutcome.Ok;

    /// <summary>
    /// 同 <see cref="AuthenticateAsync"/>，但区分「超时 / 取消」与「按错 / 被拒」——拒绝要有话说（设计稿 §9.9）。
    /// 固件对 AUTH_REQUEST 永远要求一次新触摸（hidkbd.c:5908），不看 cooldown。
    /// </summary>
    public async Task<GateOutcome> AuthenticateDetailedAsync(int timeoutMs = 30000)
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.AuthRequest, Array.Empty<byte>());
        if (r is not { Length: >= 1 }) return GateOutcome.Timeout;
        if (r[0] == (byte)ImmurokStatus.Ok) return GateOutcome.Ok;
        if (r[0] == (byte)ImmurokStatus.WaitFingerprint)
        {
            byte[]? res = await RunFpGateAsync(timeoutMs);
            if (res is null) return GateOutcome.Timeout;
            return res is { Length: >= 1 } && res[0] == (byte)ImmurokStatus.Ok ? GateOutcome.Ok : GateOutcome.Rejected;
        }
        return GateOutcome.Rejected;
    }

    /// <summary>取 TOTP 当前验证码（KEY_OTP_GET，含指纹门）。返回 6 位数字或 null。</summary>
    public async Task<string?> GetOtpCodeAsync(byte idx)
    {
        if (!await EnsureSecretBudgetAsync("KEY_OTP_GET").ConfigureAwait(false)) return null;
        uint ts = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] payload = { idx, (byte)ts, (byte)(ts >> 8), (byte)(ts >> 16), (byte)(ts >> 24) };
        byte[]? r = await SendCommandAsync(ImmurokCommand.KeyOtpGet, payload);
        if (r is not { Length: >= 1 }) return null;

        if (r[0] == (byte)ImmurokStatus.Ok && r.Length >= 7)
            return Ascii(r, 1, 6);
        if (r[0] == (byte)ImmurokStatus.WaitFingerprint)
        {
            byte[]? res = await RunFpGateAsync(30000);
            if (res is { Length: >= 7 } && res[0] == (byte)ImmurokStatus.Ok)
                return Ascii(res, 1, 6);
        }
        return null;
    }

    /// <summary>删除密钥条目（KEY_DELETE，含指纹门）。</summary>
    public async Task<bool> DeleteKeyAsync(byte cat, byte idx)
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.KeyDelete, new[] { cat, idx });
        if (r is not { Length: >= 1 }) return false;
        if (r[0] == (byte)ImmurokStatus.Ok) return true;
        if (r[0] == (byte)ImmurokStatus.WaitFingerprint)
        {
            byte[]? res = await RunFpGateAsync(30000);
            return res is { Length: >= 1 } && res[0] == (byte)ImmurokStatus.Ok;
        }
        return false;
    }

    /// <summary>读结果缓冲区（KEY_RESULT 分块）。</summary>
    private async Task<byte[]?> ReadResultBufferAsync()
    {
        byte[]? acc = null;
        int off = 0;
        for (int guard = 0; guard < 64; guard++)
        {
            byte[]? r = await SendCommandAsync(ImmurokCommand.KeyResult, new[] { (byte)off });
            if (r is not { Length: > 3 } || r[0] != (byte)ImmurokStatus.Ok) return null;
            int total = r[1];
            int roff = r[2];
            int chunk = r.Length - 3;
            acc ??= new byte[total];
            int copy = Math.Min(chunk, total - roff);
            if (copy <= 0) return null;
            Array.Copy(r, 3, acc, roff, copy);
            off = roff + copy;
            if (off >= total) return acc;
        }
        return acc;
    }

    /// <summary>取 SSH 公钥（64B 大端 x||y）。KEY_GETPUB 后读结果缓冲区。</summary>
    public async Task<byte[]?> SshGetPubAsync(byte idx)
    {
        byte[]? r = await SendCommandAsync(ImmurokCommand.KeyGetPub, new[] { (byte)0, idx });
        if (r is not { Length: >= 1 } || r[0] != (byte)ImmurokStatus.Ok) return null;
        byte[]? le = await ReadResultBufferAsync();
        if (le is not { Length: 64 }) return null;
        return ToBigEndian64(le);
    }

    /// <summary>SSH 公钥的 OpenSSH 指纹：SHA256:&lt;base64 无填充&gt;。输入 64B 大端 x||y。</summary>
    public static string SshFingerprint(byte[] pubBe64)
    {
        byte[] blob = BuildSshBlob(pubBe64);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(blob);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    /// <summary>SSH 公钥 authorized_keys 一行：ecdsa-sha2-nistp256 &lt;base64 blob&gt; &lt;comment&gt;。</summary>
    public static string SshAuthorizedKey(byte[] pubBe64, string comment)
        => $"ecdsa-sha2-nistp256 {Convert.ToBase64String(BuildSshBlob(pubBe64))} {comment}";

    private static byte[] BuildSshBlob(byte[] pubBe64)
    {
        // [len]"ecdsa-sha2-nistp256"[len]"nistp256"[len=65]0x04||x||y  = 104B
        static void PutStr(List<byte> b, byte[] s)
        {
            b.Add((byte)(s.Length >> 24)); b.Add((byte)(s.Length >> 16));
            b.Add((byte)(s.Length >> 8)); b.Add((byte)s.Length);
            b.AddRange(s);
        }
        var blob = new List<byte>(104);
        PutStr(blob, "ecdsa-sha2-nistp256"u8.ToArray());
        PutStr(blob, "nistp256"u8.ToArray());
        blob.Add(0); blob.Add(0); blob.Add(0); blob.Add(65);
        blob.Add(0x04);
        blob.AddRange(pubBe64);
        return blob.ToArray();
    }

    private static byte[] ToBigEndian64(byte[] le)
    {
        var be = new byte[64];
        for (int i = 0; i < 32; i++) be[i] = le[31 - i];       // x
        for (int i = 0; i < 32; i++) be[32 + i] = le[63 - i];  // y
        return be;
    }

    private static string CStr(byte[] b, int start, int len)
    {
        int end = start;
        int limit = Math.Min(start + len, b.Length);
        while (end < limit && b[end] != 0) end++;
        return System.Text.Encoding.UTF8.GetString(b, start, end - start);
    }

    private static string Ascii(byte[] b, int start, int len)
        => System.Text.Encoding.ASCII.GetString(b, start, Math.Min(len, b.Length - start));

    /// <summary>写入完整条目（分块 KEY_WRITE + KEY_COMMIT）。idx=0xFF 为新增。提交走指纹门。</summary>
    public async Task<bool> WriteKeyEntryAsync(byte cat, byte idx, byte[] data)
    {
        const int chunk = 59;
        int off = 0;
        while (off < data.Length)
        {
            int len = Math.Min(chunk, data.Length - off);
            byte[] payload = new byte[3 + len];
            payload[0] = cat; payload[1] = idx; payload[2] = (byte)off;
            Array.Copy(data, off, payload, 3, len);
            byte[]? r = await SendCommandAsync(ImmurokCommand.KeyWrite, payload);
            if (r is not { Length: >= 1 } || r[0] != (byte)ImmurokStatus.Ok) return false;
            off += len;
        }
        // 提交（可能指纹门）
        byte[]? c = await SendCommandAsync(ImmurokCommand.KeyCommit, new[] { cat, idx });
        if (c is not { Length: >= 1 }) return false;
        if (c[0] == (byte)ImmurokStatus.Ok) return true;
        if (c[0] == (byte)ImmurokStatus.WaitFingerprint)
        {
            byte[]? res = await RunFpGateAsync(30000);
            return res is { Length: >= 1 } && res[0] == (byte)ImmurokStatus.Ok;
        }
        return false;
    }

    /// <summary>片上生成 SSH（ECDSA P-256）密钥（KEY_GENERATE，指纹门）。返回 (idx, 64B 大端公钥)。</summary>
    public async Task<(byte Idx, byte[] PubBe)?> SshGenerateAsync(string name)
    {
        byte[] payload = new byte[17];
        payload[0] = CatSsh;
        byte[] nb = System.Text.Encoding.UTF8.GetBytes(name);
        Array.Copy(nb, 0, payload, 1, Math.Min(16, nb.Length));

        byte[]? r = await SendCommandAsync(ImmurokCommand.KeyGenerate, payload, timeoutMs: 15000);
        if (r is not { Length: >= 1 }) return null;

        byte newIdx = 0;
        if (r[0] == (byte)ImmurokStatus.Ok)
        {
            if (r.Length >= 3) newIdx = r[2];
        }
        else if (r[0] == (byte)ImmurokStatus.WaitFingerprint)
        {
            byte[]? res = await RunFpGateAsync(30000);
            if (res is null) return null;
            int count = await GetKeyCountAsync(CatSsh);
            newIdx = (byte)Math.Max(0, count - 1);
        }
        else return null;

        byte[]? le = await ReadResultBufferAsync();
        if (le is not { Length: 64 }) return null;
        return (newIdx, ToBigEndian64(le));
    }

    /// <summary>ECDSA P-256 签名（KEY_SIGN，指纹门）。输入 32B 哈希，返回 64B 大端 r||s。</summary>
    public async Task<byte[]?> SshSignAsync(byte idx, byte[] hash32)
    {
        if (hash32.Length != 32) return null;
        if (!await EnsureSecretBudgetAsync("KEY_SIGN").ConfigureAwait(false)) return null;
        byte[] payload = new byte[3 + 32];
        payload[0] = CatSsh; payload[1] = idx; payload[2] = 0;
        Array.Copy(hash32, 0, payload, 3, 32);

        byte[]? r = await SendCommandAsync(ImmurokCommand.KeySign, payload, timeoutMs: 15000);
        if (r is not { Length: >= 1 }) return null;
        if (r[0] == (byte)ImmurokStatus.Ok) { /* 直接完成 */ }
        else if (r[0] == (byte)ImmurokStatus.WaitFingerprint || r[0] == 0x10)
        {
            byte[]? g = await RunFpGateAsync(45000);
            if (g is null) return null;
        }
        else return null;

        byte[]? le = await ReadResultBufferAsync();
        if (le is not { Length: 64 }) return null;
        return ToBigEndian64(le);
    }

    /// <summary>列出全部 SSH 密钥（含 64B 大端公钥），供 SSH agent 构建身份列表。</summary>
    public async Task<List<(byte Idx, string Name, byte[] PubBe)>> SshListWithPubAsync()
    {
        var list = new List<(byte, string, byte[])>();
        int count = await GetKeyCountAsync(CatSsh);
        for (byte i = 0; i < count; i++)
        {
            string name = await ReadKeyNameAsync(CatSsh, i) ?? "";
            byte[]? pub = await SshGetPubAsync(i);
            if (pub is not null) list.Add((i, name, pub));
        }
        return list;
    }

    /// <summary>SSH 公钥 blob（104B），供 agent 作身份 keyBlob / 匹配签名请求。</summary>
    public static byte[] SshBlob(byte[] pubBe64) => BuildSshBlob(pubBe64);

    /// <summary>零填充定长字段。</summary>
    public static byte[] BuildField(string value, int size)
    {
        byte[] f = new byte[size];
        byte[] u = System.Text.Encoding.UTF8.GetBytes(value);
        Array.Copy(u, 0, f, 0, Math.Min(size, u.Length));
        return f;
    }

    public async ValueTask DisposeAsync()
    {
        if (_reconnectTimer is not null)
        {
            await _reconnectTimer.DisposeAsync();
            _reconnectTimer = null;
        }
        StopScan();
        if (_rspChar is not null) { try { _rspChar.ValueChanged -= OnRspValueChanged; } catch { } }
        _service?.Dispose();
        _otaService?.Dispose();
        _device?.Dispose();
        _cmdGate.Dispose();
        _connectGate.Dispose();
        _otaGate.Dispose();
        await Task.CompletedTask;
    }
}
