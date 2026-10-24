using System.Collections.Concurrent;
using System.Threading.Channels;
using ImmurokCommon.Ble;
using ImmurokCommon.Models;
using ImmurokCommon.Protocol;
using ImmurokService.Ble;
using ImmurokService.Ota;
using ImmurokService.Platform;
using ImmurokService.Security;
using ImmurokService.Ssh;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Ipc;

/// <summary>
/// 处理 Client ↔ Service 文本协议请求（复用 macOS PAMSocketServer 协议）。
/// 每个请求返回一行响应字符串。
/// </summary>
public sealed class CommandHandlers
{
    private readonly ILogger<CommandHandlers> _log;
    private readonly BleManager _ble;
    private readonly ImmurokSecurity _security;
    private readonly CredentialStore _creds;
    private readonly SshAgentFeature _sshFeature;
    private readonly AppSettings _settings;
    private readonly FpInjectionSignal _injectionSignal;
    private readonly OwnerStore _owner;
    private readonly CallerPolicy _policy;
    private readonly ScreenUnlocker _unlocker;
    private readonly DataDirSecurity _dataDir;
    private readonly PairingStore _pairing;
    private readonly SecurityStatus _status;

    /// <summary>
    /// 「调用方不可信」日志限流：同一映像 + 同一命令 5 分钟只记一条，期间被压掉的条数附在下一条里。
    /// 未签名构建的客户端会被反复拒（长轮询、页面刷新），不限流时日志文件持续膨胀。
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime Last, int Suppressed)> _denyLog = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DenyLogInterval = TimeSpan.FromMinutes(5);

    public CommandHandlers(ILogger<CommandHandlers> log, BleManager ble, ImmurokSecurity security,
        CredentialStore creds, SshAgentFeature sshFeature, AppSettings settings, FpInjectionSignal injectionSignal,
        OwnerStore owner, CallerPolicy policy,
        ScreenUnlocker unlocker, DataDirSecurity dataDir, PairingStore pairing, SecurityStatus status)
    {
        _policy = policy;
        _unlocker = unlocker;
        _dataDir = dataDir;
        _pairing = pairing;
        _status = status;
        _log = log;
        _ble = ble;
        _security = security;
        _creds = creds;
        _sshFeature = sshFeature;
        _settings = settings;
        _injectionSignal = injectionSignal;
        _owner = owner;
    }

    // ---- 命令分级（设计稿 §3.3 第二层）。key = VERB 或 VERB:SUB。 ----

    /// <summary>只读、不泄密：任何本机进程都可以发。</summary>
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.Ordinal)
    {
        "STATUS", "INFO", "CONNPARAMS", "SECURITY:STATUS", "FEATURE:GET", "PASS:STATUS", "PAIR:STATUS", "SLOT:STATUS",
        "FP:LIST", "FP:SLOTS", "FP:STATUS", "KEY:LIST", "KEY:SSHPUB", "OTA:INFO", "OTA:VERSION", "SSHAGENT:STATUS",
    };

    /// <summary>靠设备指纹门（+ GateBudget）而不靠身份：本来就是给 imk 用的。</summary>
    private static readonly HashSet<string> DeviceGatedForAll = new(StringComparer.Ordinal) { "KEY:OTP" };

    /// <summary>
    /// 固件自己不设门、又会产生持久写入的命令：Service 先发 AUTH_REQUEST 逼一次真实触摸。
    /// 固件已设门的（FP:ENROLL/DELETE、KEY:DELETE/ADD*/SSHGEN、SLOT:CLEAR、PAIR:START 的配对流程本身）
    /// 不重复要门，否则用户每个动作要摸两次。
    /// 只有 PASS:SET（写凭据管理器）和 OTA:PUSH（刷固件）要门。功能开关（FEATURE:SET）和 SSHAGENT:ON
    /// 不要门（2026-09-06 用户决定）：开关本身不放行任何秘密——解锁推密码、SSH 签名、读 OTP 各自仍要触摸，
    /// 攻击者替用户打开开关什么也拿不到，加门只是每次开关多摸一次。关功能、清密码、解绑设备
    /// （PASS:CLEAR、FEATURE:SET:*:0、SSHAGENT:OFF、PAIR:RESET）同样不要门，只受 owner 校验（§3.5）。
    /// </summary>
    private static bool NeedsHostGate(string key) => key switch
    {
        "PASS:SET" => true,
        "OTA:PUSH" => true,
        _ => false,
    };

    private void LogCallerDenied(string key, CallerIdentity? caller)
    {
        string image = caller?.ImagePath ?? "?";
        DateTime now = DateTime.UtcNow;
        int suppressed = 0;
        bool emit = false;
        _denyLog.AddOrUpdate($"{image}|{key}",
            _ => { emit = true; return (now, 0); },
            (_, e) =>
            {
                if (now - e.Last < DenyLogInterval) { emit = false; return (e.Last, e.Suppressed + 1); }
                emit = true; suppressed = e.Suppressed; return (now, 0);
            });
        if (!emit) return;
        if (suppressed > 0)
            _log.LogWarning("{Key}：调用方不可信，拒绝（{Caller} image={Image}；此前 {Interval} 分钟内另有 {Count} 次未记）",
                key, caller?.ToString() ?? "unknown", image, (int)DenyLogInterval.TotalMinutes, suppressed);
        else
            _log.LogWarning("{Key}：调用方不可信，拒绝（{Caller} image={Image}）", key, caller?.ToString() ?? "unknown", image);
    }

    private static string CommandKey(string[] parts)
    {
        string verb = parts[0];
        return parts.Length >= 2 && verb is "FEATURE" or "PASS" or "PAIR" or "SLOT" or "FP" or "KEY" or "OTA" or "SSHAGENT" or "INJECT" or "SECURITY"
            ? $"{verb}:{parts[1]}"
            : verb;
    }

    /// <summary>
    /// 授权检查，在分派（含流式命令）之前调用。返回 null = 放行；否则为拒绝应答。
    /// 第一层 <see cref="CallerPolicy"/> 是纵深防御（可被注入绕过），第二层的门才是边界。
    /// 拒绝一律回明确错误码、不断连（§9.9）。
    /// </summary>
    public async Task<string?> AuthorizeAsync(string request, CallerIdentity? caller, CancellationToken ct)
    {
        string[] parts = request.Split(IpcProtocol.Sep);
        string key = CommandKey(parts);

        if (ReadOnlyCommands.Contains(key) || DeviceGatedForAll.Contains(key)) return null;

        if (_policy.Classify(caller) != CallerKind.TrustedClient)
        {
            LogCallerDenied(key, caller);
            return IpcProtocol.ErrCallerNotTrusted;
        }

        if (!NeedsHostGate(key)) return null;

        if (!_ble.IsConnected) return IpcProtocol.ErrNotConnected;
        _log.LogInformation("{Key}：要求设备指纹确认", key);
        var outcome = await _ble.AuthenticateDetailedAsync().ConfigureAwait(false);
        switch (outcome)
        {
            case BleManager.GateOutcome.Ok:
                return null;
            case BleManager.GateOutcome.Timeout:
                _log.LogWarning("{Key}：指纹确认超时 / 取消，拒绝", key);
                return IpcProtocol.DenyGateTimeout;
            default:
                _log.LogWarning("{Key}：指纹确认未通过，拒绝", key);
                return IpcProtocol.DenyGateRejected;
        }
    }

    /// <param name="caller">对端进程身份；取不到为 null。只用于拒绝，不用于放行。</param>
    public async Task<string> HandleAsync(string request, CallerIdentity? caller, CancellationToken ct)
    {
        _log.LogDebug("IPC 请求: {Request}", request.Split(':')[0]);
        string[] parts = request.Split(IpcProtocol.Sep);
        string verb = parts[0];

        try
        {
            return verb switch
            {
                IpcProtocol.Status => HandleStatus(),
                IpcProtocol.Info   => await HandleInfoAsync(),
                IpcProtocol.ConnParams => await HandleConnParamsAsync(),
                IpcProtocol.Fp     => await HandleFpAsync(parts, ct),
                IpcProtocol.Pair   => await HandlePairAsync(parts, caller, ct),
                IpcProtocol.Slot   => await HandleSlotAsync(parts),
                IpcProtocol.Key    => await HandleKeyAsync(parts, caller),
                IpcProtocol.SshAgent => await HandleSshAgentAsync(parts),
                IpcProtocol.Pass   => HandlePass(parts, caller),
                IpcProtocol.Auth   => await HandleAuthAsync(parts, caller, ct),
                // 取消进行中的指纹门：不做连接检查，尽力而为（客户端认证弹窗点取消时用）。
                IpcProtocol.CancelGate => await HandleCancelGateAsync(),
                // 功能开关：不要求设备在线（纯本地设置）。
                IpcProtocol.Feature => await HandleFeatureAsync(parts),
                // 指纹注入信号长轮询：挂起等信号或超时，客户端零空转。
                IpcProtocol.Inject => await HandleInjectAsync(parts, ct),
                IpcProtocol.Ota    => IpcProtocol.ErrOtaNotAvailable, // OTA 待阶段5实现
                // IPC 加固健康状态（只读，任何人可查；只放枚举值，不放路径）。
                IpcProtocol.Security => parts.Length >= 2 && parts[1] == IpcProtocol.SecurityStatus
                    ? HandleSecurityStatus() : IpcProtocol.ErrUnknownCommand,
                _ => IpcProtocol.ErrUnknownCommand,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 服务停止：INJECT:POLL 这类长轮询会被取消，是正常关停路径，不是异常。
            _log.LogDebug("请求因服务停止而取消: {Verb}", verb);
            return IpcProtocol.Error;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "处理请求异常: {Verb}", verb);
            return IpcProtocol.Error;
        }
    }

    /// <summary>设备页点固件版本那一行时读取，只读、不泄密，配对前也能用（固件不要求加密）。</summary>
    private async Task<string> HandleConnParamsAsync()
    {
        if (!_ble.IsConnected) return IpcProtocol.ErrNotConnected;
        var p = await _ble.GetConnParamsAsync();
        return p is { } v ? $"{IpcProtocol.Ok}:{v.Interval}:{v.Latency}:{v.Timeout}" : IpcProtocol.Error;
    }

    /// <summary>
    /// 功能开关读写。纯本地设置，不要求设备在线。
    /// ssh 需要连带启停 agent 进程，其余只落设置、由各功能入口自行判定。
    /// </summary>
    private async Task<string> HandleFeatureAsync(string[] parts)
    {
        if (parts.Length < 2) return IpcProtocol.ErrInvalidFormat;

        if (parts[1] == IpcProtocol.FeatureGet)
        {
            static string B(bool v) => v ? "1" : "0";
            return $"{IpcProtocol.Ok}:{B(_settings.UnlockEnabled)}:{B(_settings.LockEnabled)}:" +
                   $"{B(_settings.SshAgentEnabled)}:{B(_settings.ImkAgentEnabled)}:{B(_settings.OtpEnabled)}";
        }

        if (parts[1] == IpcProtocol.FeatureSet)
        {
            // FEATURE:SET:<name>:<0|1>
            if (parts.Length < 4) return IpcProtocol.ErrInvalidFormat;
            bool on = parts[3] == "1";
            switch (parts[2])
            {
                case IpcProtocol.FeatureUnlock: _settings.UnlockEnabled = on; break;
                case IpcProtocol.FeatureLock:   _settings.LockEnabled = on; break;
                case IpcProtocol.FeatureAgent:  _settings.ImkAgentEnabled = on; break;
                case IpcProtocol.FeatureOtp:    _settings.OtpEnabled = on; break;
                case IpcProtocol.FeatureSsh:
                    if (on) await _sshFeature.EnableAsync(); else await _sshFeature.DisableAsync();
                    break;
                default: return IpcProtocol.ErrInvalidFormat;
            }
            _log.LogInformation("功能开关 {Name} -> {On}", parts[2], on ? "ON" : "OFF");
            return IpcProtocol.Ok;
        }

        return IpcProtocol.ErrUnknownCommand;
    }

    /// <summary>
    /// owner 校验（设计稿 §3.5）。返回 true 表示要拒绝，<paramref name="response"/> 为拒绝应答。
    /// 没有 owner 记录时告警放行——升级过渡，配对或 PASS:SET 一次就补上，不锁死单用户机器。
    /// 调用方身份取不到时按「不是 owner」拒绝：身份只能用来拒绝，不能用来放行。
    /// </summary>
    private bool DenyIfNotOwner(CallerIdentity? caller, string what, out string response)
    {
        response = IpcProtocol.DenyNotOwner;
        if (!_owner.IsSet)
        {
            _log.LogWarning("{What}：尚无 owner 记录，放行（配对或设置密码后会补上）", what);
            return false;
        }
        if (caller is null)
        {
            _log.LogWarning("{What}：取不到调用方身份，拒绝", what);
            return true;
        }
        if (_owner.IsOwner(caller.Sid) == true) return false;
        _log.LogWarning("{What}：调用方不是 owner，拒绝（{Caller}）", what, caller);
        return true;
    }

    /// <summary>把调用方记为 owner（配对成功 / PASS:SET）。</summary>
    private void RecordOwner(CallerIdentity? caller, string what)
    {
        if (caller is null)
        {
            _log.LogWarning("{What}：取不到调用方身份，无法记录 owner", what);
            return;
        }
        _owner.Save(caller.Sid, caller.Account);
    }

    /// <summary>
    /// SECURITY:STATUS（设计稿 §4）。各项是给人看的健康提示，不是安全判定的输入（§9.2）：
    ///   cp_pipe: ok / squatted / unknown（还没触发过解锁）
    ///   service_pipe: ok / contended（某条管道的首实例创建失败，名字被占）
    ///   caller_check: signed / path-only（Service 未签名）/ off（--allow-unsigned-clients）
    ///   data_acl: ok / loose（数据目录 DACL 里仍有 Users / Everyone）
    ///   pairing_scope: user / machine（未迁移）/ unreadable（服务账户被改过）/ none（未配对）
    ///   owner: set / unset
    /// </summary>
    private string HandleSecurityStatus()
    {
        _ = _security.IsPaired; // 刷新 KeyScope
        string cp = _unlocker.LastPeerCheck switch
        {
            ScreenUnlocker.PeerCheck.Ok => "ok",
            ScreenUnlocker.PeerCheck.Squatted => "squatted",
            _ => "unknown",
        };
        string pipe = _status.AnyPipeContended ? "contended" : "ok";
        string acl = _dataDir.IsLoose() ? "loose" : "ok";
        string scope = _pairing.KeyScope switch
        {
            PairingStore.Scope.User => "user",
            PairingStore.Scope.Machine => "machine",
            PairingStore.Scope.Unreadable => "unreadable",
            _ => "none",
        };
        return $"{IpcProtocol.Ok}:cp_pipe={cp};service_pipe={pipe};caller_check={_policy.Mode};" +
               $"data_acl={acl};pairing_scope={scope};owner={(_owner.IsSet ? "set" : "unset")}";
    }

    /// <summary>取消进行中的指纹门，让设备停止闪灯等待，等待中的操作立即以失败收尾。</summary>
    private async Task<string> HandleCancelGateAsync()
    {
        await _ble.CancelGateAsync();
        return IpcProtocol.Ok;
    }

    /// <summary>
    /// 指纹注入信号长轮询。INJECT:POLL -&gt; 挂起最多 25 秒，期间来了指纹信号立即以
    /// OK:&lt;pageId&gt;（并消费掉）返回，否则超时回 OK。客户端拿到响应后立刻发起下一发，
    /// 从而零空转、近乎推送级延迟。管道为多实例并发，挂起不影响其它 IPC。
    /// </summary>
    private async Task<string> HandleInjectAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length >= 2 && parts[1] == IpcProtocol.InjectPoll)
        {
            await _injectionSignal.WaitForSignalAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            return _injectionSignal.Consume(out ushort pageId)
                ? $"{IpcProtocol.Ok}{IpcProtocol.Sep}{pageId}"
                : IpcProtocol.Ok;
        }
        return IpcProtocol.ErrInvalidFormat;
    }

    private string HandleStatus()
    {
        // STATUS → STATUS:1:deviceName | STATUS:0:
        return _ble.IsConnected
            ? $"{IpcProtocol.Status}:1:{_ble.DeviceName}"
            : $"{IpcProtocol.Status}:0:";
    }

    /// <summary>INFO → OK:&lt;paired 0/1&gt;:&lt;battery 或 -1&gt;:&lt;fw 或 -&gt;:&lt;fpcount&gt;</summary>
    private async Task<string> HandleInfoAsync()
    {
        if (!_ble.IsConnected) return IpcProtocol.ErrNotConnected;
        var info = await _ble.GetDeviceInfoAsync();
        if (info is null) return IpcProtocol.Error;
        var (paired, battery, fw, fpCount) = info.Value;
        return $"{IpcProtocol.Ok}:{(paired ? 1 : 0)}:{battery ?? -1}:{fw ?? "-"}:{fpCount}";
    }

    private async Task<string> HandleFpAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 2) return IpcProtocol.ErrInvalidFormat;
        if (!_ble.IsConnected) return IpcProtocol.ErrNotConnected;

        switch (parts[1])
        {
            case IpcProtocol.FpList:
            {
                List<FingerprintSlot>? slots = await _ble.GetFingerprintListAsync();
                if (slots is null) return IpcProtocol.ErrNotConnected;
                int count = slots.Count(s => s.Enrolled);
                return $"{IpcProtocol.Ok}:{count}";
            }
            case IpcProtocol.FpSlots:
            {
                byte? bitmap = await _ble.GetFingerprintBitmapAsync();
                if (bitmap is null) return IpcProtocol.ErrNotConnected;
                return $"{IpcProtocol.Ok}:{bitmap.Value}";
            }
            case IpcProtocol.FpEnroll:
            {
                if (parts.Length < 3 || !byte.TryParse(parts[2], out byte slot))
                    return IpcProtocol.ErrInvalidSlot;
                bool ok = await _ble.StartEnrollmentAsync(slot);
                return ok ? $"{IpcProtocol.Ok}:ENROLL_STARTED" : IpcProtocol.ErrEnrollFailed;
            }
            case IpcProtocol.FpDelete:
            {
                if (parts.Length < 3 || !byte.TryParse(parts[2], out byte slot))
                    return IpcProtocol.ErrInvalidSlot;
                bool ok = await _ble.DeleteFingerprintAsync(slot);
                return ok ? $"{IpcProtocol.Ok}:DELETED" : IpcProtocol.ErrDeleteFailed;
            }
            case IpcProtocol.FpStatus:
                return $"{IpcProtocol.Ok}:IDLE";
            default:
                return IpcProtocol.ErrUnknownCommand;
        }
    }

    private async Task<string> HandlePairAsync(string[] parts, CallerIdentity? caller, CancellationToken ct)
    {
        if (parts.Length < 2) return IpcProtocol.ErrInvalidFormat;

        switch (parts[1])
        {
            case IpcProtocol.PairStatus:
                return _security.IsPaired ? $"{IpcProtocol.Ok}:PAIRED" : $"{IpcProtocol.Ok}:UNPAIRED";

            case IpcProtocol.PairStart:
            {
                // 重新配对等于换设备 / 换主人：已有 owner 时只有 owner 能做；要换人先 PAIR:RESET。
                if (DenyIfNotOwner(caller, "PAIR:START", out string deny)) return deny;
                if (!_ble.IsConnected) return IpcProtocol.ErrNotConnected;
                PairFailureReason reason = await _ble.StartPairingAsync(ct: ct);
                if (reason == PairFailureReason.None)
                {
                    RecordOwner(caller, "PAIR:START");
                    return $"{IpcProtocol.Ok}:PAIRED";
                }
                return PairFailureFrame(reason);
            }
            case IpcProtocol.PairReset:
            {
                if (DenyIfNotOwner(caller, "PAIR:RESET", out string deny)) return deny;
                _ble.ResetPairing();
                _owner.Clear();
                return $"{IpcProtocol.Ok}:RESET";
            }

            default:
                return IpcProtocol.ErrUnknownCommand;
        }
    }

    /// <summary><c>ERROR:&lt;REASON&gt;</c>；Generic 附上 BleManager 记下的具体阶段与设备回包，界面原样显示便于排查。</summary>
    private string PairFailureFrame(PairFailureReason reason)
    {
        string frame = $"{IpcProtocol.Error}:{reason.ToString().ToUpperInvariant()}";
        return reason == PairFailureReason.Generic && _ble.LastPairFailureDetail is { } d ? $"{frame}:{d}" : frame;
    }

    /// <summary>
    /// 流式配对：把设备的 0x34 阶段通知实时推给客户端，最后一帧为终态。
    /// 帧格式：<c>PROGRESS:&lt;PairStage&gt;</c>，指纹按错时附剩余次数
    /// <c>PROGRESS:FingerprintRejected:&lt;remaining&gt;</c>；终态 <c>OK:PAIRED</c> / <c>ERROR:*</c>
    /// （与非流式 <c>PAIR:START</c> 的终态一致，客户端解析逻辑不用分家）。
    /// </summary>
    public async Task HandlePairStartStreamAsync(CallerIdentity? caller, Func<string, Task> writeFrame, CancellationToken ct)
    {
        if (DenyIfNotOwner(caller, "PAIR:START", out string deny))
        {
            await writeFrame(deny);
            return;
        }
        if (!_ble.IsConnected)
        {
            await writeFrame(IpcProtocol.ErrNotConnected);
            return;
        }

        // 起始阶段要靠槽位状态判断：设备上已有另一台主机时固件先挂指纹门。
        // 读不到就按「只等按键」处理——旧固件本来就是单主机，只有这一步。
        bool fpGateFirst = false;
        var slots = await _ble.GetHostSlotStatusAsync();
        if (slots is { Supported: true, Bitmap: > 0 }) fpGateFirst = true;

        var channel = Channel.CreateUnbounded<string>();
        void OnProgress(BleManager.PairStage stage, int remaining)
            => channel.Writer.TryWrite(remaining >= 0
                ? $"PROGRESS:{stage}:{remaining}"
                : $"PROGRESS:{stage}");

        _ble.PairProgress += OnProgress;
        // 进度帧要边产生边发，所以另起一个泵；配对本身在当前流程里跑。
        // 两者不会同时写管道：先 Complete 关掉泵、await 它退出，再写终态帧。
        Task pump = Task.Run(async () =>
        {
            await foreach (string m in channel.Reader.ReadAllAsync(CancellationToken.None))
                await writeFrame(m);
        }, CancellationToken.None);

        string terminal;
        try
        {
            PairFailureReason reason = await _ble.StartPairingAsync(fpGateFirst, ct: ct);
            if (reason == PairFailureReason.None)
            {
                RecordOwner(caller, "PAIR:START");
                terminal = $"{IpcProtocol.Ok}:PAIRED";
            }
            else
            {
                terminal = PairFailureFrame(reason);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "流式配对异常");
            terminal = IpcProtocol.Error;
        }
        finally
        {
            _ble.PairProgress -= OnProgress;
            channel.Writer.TryComplete();
        }

        try { await pump; } catch (Exception ex) { _log.LogDebug(ex, "配对进度泵结束"); }
        await writeFrame(terminal);
    }

    /// <summary>
    /// 流式指纹录入：一路把设备的 0x11 进度事件推给客户端，最后一帧为终态。
    /// 帧格式：<c>PROGRESS:&lt;Event&gt;:&lt;current&gt;:&lt;total&gt;</c>；终态 <c>OK:COMPLETE</c> / <c>ERROR:*</c>。
    /// </summary>
    public async Task HandleEnrollStreamAsync(string request, Func<string, Task> writeFrame, CancellationToken ct)
    {
        string[] parts = request.Split(IpcProtocol.Sep);
        if (parts.Length < 3 || !byte.TryParse(parts[2], out byte slot))
        {
            await writeFrame(IpcProtocol.ErrInvalidSlot);
            return;
        }
        if (!_ble.IsConnected)
        {
            await writeFrame(IpcProtocol.ErrNotConnected);
            return;
        }

        var channel = Channel.CreateUnbounded<string>();
        void OnProgress(FpEnrollEvent ev, int cur, int total)
        {
            channel.Writer.TryWrite($"PROGRESS:{ev}:{cur}:{total}");
            if (ev == FpEnrollEvent.Complete)
            {
                channel.Writer.TryWrite($"{IpcProtocol.Ok}:COMPLETE");
                channel.Writer.TryComplete();
            }
            else if (ev == FpEnrollEvent.Failed)
            {
                channel.Writer.TryWrite(IpcProtocol.ErrEnrollFailed);
                channel.Writer.TryComplete();
            }
        }

        _ble.EnrollProgress += OnProgress;
        try
        {
            bool started = await _ble.StartEnrollmentAsync(slot);
            if (!started)
            {
                await writeFrame(IpcProtocol.ErrEnrollFailed);
                return;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(90000);
            try
            {
                await foreach (string msg in channel.Reader.ReadAllAsync(timeoutCts.Token))
                    await writeFrame(msg);
            }
            catch (OperationCanceledException)
            {
                await _ble.CancelEnrollmentAsync();
                await writeFrame(IpcProtocol.Timeout);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "流式录入异常");
            await writeFrame(IpcProtocol.Error);
        }
        finally
        {
            _ble.EnrollProgress -= OnProgress;
        }
    }

    /// <summary>
    /// 流式 OTA 推送。请求 <c>OTA:PUSH:&lt;base64 整个 .imfw&gt;</c>；
    /// 推送中回 <c>PROGRESS:&lt;百分比整数&gt;</c>，终态 <c>OK:DONE</c> / <c>ERROR:&lt;原因&gt;</c>。
    /// </summary>
    public async Task HandleOtaPushStreamAsync(string request, Func<string, Task> writeFrame, CancellationToken ct)
    {
        // OTA:PUSH:<base64>
        int idx = request.IndexOf(":PUSH:", StringComparison.Ordinal);
        if (idx < 0) { await writeFrame(IpcProtocol.ErrInvalidFormat); return; }
        string b64 = request[(idx + 6)..];
        byte[] imfw;
        try { imfw = Convert.FromBase64String(b64); }
        catch { await writeFrame(IpcProtocol.ErrInvalidFormat); return; }

        if (!_ble.OtaAvailable) { await writeFrame(IpcProtocol.ErrOtaNotAvailable); return; }

        var engine = new OtaEngine(_ble, _log);
        int lastPct = -1;
        var progressQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void OnProgress(double f)
        {
            int pct = (int)(f * 100);
            if (pct != lastPct) { lastPct = pct; progressQueue.Enqueue($"PROGRESS:{pct}"); }
        }

        // 边推送边把进度刷给客户端。
        var pushTask = engine.PushAsync(imfw, OnProgress, ct);
        while (!pushTask.IsCompleted)
        {
            while (progressQueue.TryDequeue(out string? msg)) await writeFrame(msg);
            await Task.WhenAny(pushTask, Task.Delay(200, CancellationToken.None));
        }
        while (progressQueue.TryDequeue(out string? msg)) await writeFrame(msg);

        try
        {
            var result = await pushTask;
            await writeFrame(result.Ok ? $"{IpcProtocol.Ok}:DONE" : $"{IpcProtocol.Error}:{result.Message}");
        }
        catch (OperationCanceledException) { await writeFrame(IpcProtocol.Timeout); }
        catch (Exception ex)
        {
            _log.LogError(ex, "OTA 推送异常");
            await writeFrame($"{IpcProtocol.Error}:{ex.Message}");
        }
    }

    /// <summary>密钥库：SSH / OTP / API 列表、TOTP 取码、删除、SSH 公钥导出。</summary>
    private async Task<string> HandleKeyAsync(string[] parts, CallerIdentity? caller)
    {
        if (parts.Length < 2) return IpcProtocol.ErrInvalidFormat;
        if (!_ble.IsConnected) return IpcProtocol.ErrNotConnected;

        switch (parts[1])
        {
            case IpcProtocol.KeyList:
            {
                if (parts.Length < 3 || !byte.TryParse(parts[2], out byte cat))
                    return IpcProtocol.ErrInvalidFormat;
                // 列表也只给 owner（2026-09-06 用户决定）：值拿不到，名字也没必要给另一个账号看。
                if (DenyIfNotOwner(caller, "KEY:LIST", out string deny)) return deny;
                int count = await _ble.GetKeyCountAsync(cat);
                var entries = new List<string>();
                for (byte i = 0; i < count; i++)
                {
                    if (cat == BleManager.CatOtp)
                    {
                        var ns = await _ble.ReadOtpNameServiceAsync(i);
                        if (ns is null) continue;
                        entries.Add($"{i},{B64(ns.Value.Name)},{B64(ns.Value.Service)}");
                    }
                    else
                    {
                        string? name = await _ble.ReadKeyNameAsync(cat, i);
                        if (name is null) continue;
                        entries.Add($"{i},{B64(name)},");
                    }
                }
                return $"{IpcProtocol.Ok}:{string.Join(";", entries)}";
            }
            case IpcProtocol.KeyOtp:
            {
                // 读秘密只给 owner（2026-09-06 多用户测试后补上）：切换用户后另一账号的进程还活着，
                // 而指纹门预算是服务级的——owner 为别的事触摸一次，对方 60s 内不用触摸就能读走。
                // 与 SSH agent 管道（ACL 只给 owner）、AUTH / APPROVE 的 owner 校验对齐。
                if (DenyIfNotOwner(caller, "KEY:OTP", out string deny)) return deny;
                if (!_settings.OtpEnabled) return IpcProtocol.Deny; // 功能开关：OTP 已关闭
                if (parts.Length < 3 || !byte.TryParse(parts[2], out byte idx))
                    return IpcProtocol.ErrInvalidFormat;
                string? code = await _ble.GetOtpCodeAsync(idx);
                return code is null ? IpcProtocol.Deny : $"{IpcProtocol.Ok}:{code}";
            }
            case IpcProtocol.KeyDelete:
            {
                if (parts.Length < 4 || !byte.TryParse(parts[2], out byte cat) || !byte.TryParse(parts[3], out byte idx))
                    return IpcProtocol.ErrInvalidFormat;
                bool ok = await _ble.DeleteKeyAsync(cat, idx);
                return ok ? IpcProtocol.Ok : IpcProtocol.Deny;
            }
            case IpcProtocol.KeySshPub:
            {
                if (parts.Length < 3 || !byte.TryParse(parts[2], out byte idx))
                    return IpcProtocol.ErrInvalidFormat;
                byte[]? pub = await _ble.SshGetPubAsync(idx);
                if (pub is null) return IpcProtocol.Error;
                string name = await _ble.ReadKeyNameAsync(BleManager.CatSsh, idx) ?? "immurok";
                return $"{IpcProtocol.Ok}:{B64(BleManager.SshAuthorizedKey(pub, name))}:{B64(BleManager.SshFingerprint(pub))}";
            }
            case IpcProtocol.KeyAddOtp:
            {
                // KEY:ADDOTP:<b64name>:<b64service>:<b64secretBase32>
                if (parts.Length < 5) return IpcProtocol.ErrInvalidFormat;
                string name = FromB64(parts[2]);
                string service = FromB64(parts[3]);
                byte[]? secret = Base32Decode(FromB64(parts[4]));
                if (secret is null || secret.Length == 0) return IpcProtocol.ErrInvalidFormat;
                byte[] secretField = new byte[32];
                Array.Copy(secret, 0, secretField, 0, Math.Min(32, secret.Length));
                byte[] blob = Concat(BleManager.BuildField(name, 30), BleManager.BuildField(service, 30), secretField);
                bool ok = await _ble.WriteKeyEntryAsync(BleManager.CatOtp, 0xFF, blob);
                return ok ? IpcProtocol.Ok : IpcProtocol.Deny;
            }
            case IpcProtocol.KeyAddApi:
            {
                // KEY:ADDAPI:<b64name>:<b64value>
                if (parts.Length < 4) return IpcProtocol.ErrInvalidFormat;
                string name = FromB64(parts[2]);
                string value = FromB64(parts[3]);
                byte[] blob = Concat(BleManager.BuildField(name, 32), BleManager.BuildField(value, 128));
                bool ok = await _ble.WriteKeyEntryAsync(BleManager.CatApi, 0xFF, blob);
                return ok ? IpcProtocol.Ok : IpcProtocol.Deny;
            }
            case IpcProtocol.KeySshGen:
            {
                // KEY:SSHGEN:<b64name>
                if (parts.Length < 3) return IpcProtocol.ErrInvalidFormat;
                string name = FromB64(parts[2]);
                var res = await _ble.SshGenerateAsync(name);
                if (res is null) return IpcProtocol.Deny;
                return $"{IpcProtocol.Ok}:{B64(BleManager.SshAuthorizedKey(res.Value.PubBe, name))}:{B64(BleManager.SshFingerprint(res.Value.PubBe))}";
            }
            case IpcProtocol.KeyUpdate:
            {
                // KEY:UPDATE:<cat>:<idx>:<b64name>:<b64service> —— 仅改 name(+OTP service)，
                // 只写 name 字段；密文区靠固件 staging(1.2.8+) 从 flash 预载保留。
                if (parts.Length < 5 || !byte.TryParse(parts[2], out byte cat) || !byte.TryParse(parts[3], out byte idx))
                    return IpcProtocol.ErrInvalidFormat;
                string name = FromB64(parts[2 + 2]);      // parts[4]
                string service = parts.Length > 5 ? FromB64(parts[5]) : "";
                byte[] payload = cat switch
                {
                    BleManager.CatSsh => BleManager.BuildField(name, 16),
                    BleManager.CatOtp => Concat(BleManager.BuildField(name, 30), BleManager.BuildField(service, 30)),
                    _                 => BleManager.BuildField(name, 32), // API
                };
                bool ok = await _ble.WriteKeyEntryAsync(cat, idx, payload);
                return ok ? IpcProtocol.Ok : IpcProtocol.Deny;
            }
            default:
                return IpcProtocol.ErrUnknownCommand;
        }
    }

    private static string FromB64(string s)
    {
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return ""; }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        byte[] r = new byte[total];
        int off = 0;
        foreach (var p in parts) { Array.Copy(p, 0, r, off, p.Length); off += p.Length; }
        return r;
    }

    /// <summary>RFC 4648 Base32 解码（忽略空格/连字符/大小写/填充）。失败返回 null。</summary>
    private static byte[]? Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var cleaned = new System.Text.StringBuilder();
        foreach (char ch in input.Trim())
        {
            if (ch == '=' || ch == ' ' || ch == '-') continue;
            char up = char.ToUpperInvariant(ch);
            if (alphabet.IndexOf(up) < 0) return null;
            cleaned.Append(up);
        }
        string s = cleaned.ToString();
        if (s.Length == 0) return null;

        var output = new List<byte>(s.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (char ch in s)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(ch);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }
        return output.ToArray();
    }

    private static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    /// <summary>SSH agent 开关。</summary>
    private async Task<string> HandleSshAgentAsync(string[] parts)
    {
        if (parts.Length < 2) return IpcProtocol.ErrInvalidFormat;
        switch (parts[1])
        {
            case "STATUS":
                return $"{IpcProtocol.Ok}:{(_settings.SshAgentEnabled ? "ON" : "OFF")}";
            case "ON":
                // OK:TAKEN | ABSENT | TAKEOVER_FAILED | CONTENDED（见 SshAgentFeature）
                return $"{IpcProtocol.Ok}:{await _sshFeature.EnableAsync()}";
            case "OFF":
                // OK:RESTORED | NONE | RESTORE_FAILED
                return $"{IpcProtocol.Ok}:{await _sshFeature.DisableAsync()}";
            default:
                return IpcProtocol.ErrUnknownCommand;
        }
    }

    /// <summary>
    /// 双主机槽位管理。
    /// 注意：连接检查按子命令分别做——CLEAROWN 在设备不在线时也必须可用（只清本地绑定），
    /// 否则本机会被旧绑定卡死、无法改配新设备。
    /// </summary>
    private async Task<string> HandleSlotAsync(string[] parts)
    {
        if (parts.Length < 2) return IpcProtocol.ErrInvalidFormat;

        switch (parts[1])
        {
            case IpcProtocol.SlotStatus:
            {
                if (!_ble.IsConnected) return IpcProtocol.ErrNotConnected;
                var s = await _ble.GetHostSlotStatusAsync();
                if (s is null) return IpcProtocol.Error;
                var (supported, bitmap, active) = s.Value;
                return $"{IpcProtocol.Ok}:{(supported ? 1 : 0)}:{bitmap}:{active}";
            }
            case IpcProtocol.SlotClearOwn:
            {
                // 不要求已连接：设备在线则通知设备清槽，不在线则只清本地绑定。
                var res = await _ble.ClearOwnSlotAsync();
                return res switch
                {
                    BleManager.ClearOwnResult.ClearedOnDevice => $"{IpcProtocol.Ok}:DEVICE",
                    BleManager.ClearOwnResult.ClearedLocalOnly => $"{IpcProtocol.Ok}:LOCAL",
                    _ => IpcProtocol.Reject,
                };
            }
            case IpcProtocol.SlotClear:
            {
                // 解绑「另一台」需要设备在线并过指纹门。
                if (!_ble.IsConnected) return IpcProtocol.ErrNotConnected;
                if (parts.Length < 3 || !byte.TryParse(parts[2], out byte slot))
                    return IpcProtocol.ErrInvalidFormat;
                bool ok = await _ble.ClearOtherSlotAsync(slot);
                return ok ? IpcProtocol.Ok : IpcProtocol.Deny;
            }
            default:
                return IpcProtocol.ErrUnknownCommand;
        }
    }

    /// <summary>登录密码配置（存 Windows 凭据管理器，供 CP 解锁时读取）。</summary>
    private string HandlePass(string[] parts, CallerIdentity? caller)
    {
        if (parts.Length < 2) return IpcProtocol.ErrInvalidFormat;
        switch (parts[1])
        {
            case IpcProtocol.PassStatus:
                return _creds.HasPassword() ? $"{IpcProtocol.Ok}:CONFIGURED" : $"{IpcProtocol.Ok}:NOTSET";

            case IpcProtocol.PassSet:
            {
                // PASS:SET:<b64user>:<b64pass>
                if (DenyIfNotOwner(caller, "PASS:SET", out string deny)) return deny;
                if (parts.Length < 4) return IpcProtocol.ErrInvalidFormat;
                string user, pass;
                try
                {
                    user = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                    pass = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
                }
                catch { return IpcProtocol.ErrInvalidFormat; }
                if (string.IsNullOrEmpty(pass)) return IpcProtocol.ErrInvalidFormat;
                // 未带用户名时用调用方账户（DOMAIN\user）。不能用 Environment.UserName：
                // Service 跑在 SYSTEM 下，那会存成 "SYSTEM"。
                if (string.IsNullOrEmpty(user)) user = caller?.Account ?? _owner.Account ?? "";
                if (string.IsNullOrEmpty(user))
                {
                    _log.LogWarning("PASS:SET 未带用户名且取不到调用方账户，拒绝");
                    return IpcProtocol.ErrInvalidFormat;
                }
                _creds.Save(user, pass);
                RecordOwner(caller, "PASS:SET");
                _log.LogInformation("登录密码已保存");
                return IpcProtocol.Ok;
            }

            case IpcProtocol.PassClear:
            {
                if (DenyIfNotOwner(caller, "PASS:CLEAR", out string deny)) return deny;
                _creds.Clear();
                return IpcProtocol.Ok;
            }

            default:
                return IpcProtocol.ErrUnknownCommand;
        }
    }

    /// <summary>
    /// AUTH:username:service —— 等待一次签名指纹匹配（30s 超时），用于未来的权限授权场景。
    /// 屏幕解锁走独立的 CP 管道，不经此路径。
    /// </summary>
    private async Task<string> HandleAuthAsync(string[] parts, CallerIdentity? caller, CancellationToken ct)
    {
        // 触摸只授权给 owner 的请求：否则同机另一个账号发 AUTH，机主的一次触摸就把授权给了他。
        if (DenyIfNotOwner(caller, "AUTH", out string deny)) return deny;
        if (!_ble.IsConnected) return IpcProtocol.ErrNotConnected;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMatch(ushort _) => tcs.TrySetResult(true);
        _ble.SignedFingerprintMatched += OnMatch;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(30000);
            await tcs.Task.WaitAsync(cts.Token);
            return IpcProtocol.Ok;
        }
        catch (OperationCanceledException)
        {
            return IpcProtocol.Timeout;
        }
        finally
        {
            _ble.SignedFingerprintMatched -= OnMatch;
        }
    }
}
