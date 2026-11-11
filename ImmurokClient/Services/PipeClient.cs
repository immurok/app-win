using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using ImmurokCommon.Protocol;

namespace ImmurokClient.Services;

/// <summary>
/// 连接 Service 的命名管道客户端（文本协议）。每次请求短连接：连接→发一帧→收一帧→关闭。
///
/// <para>请求之间不串行化。以前这里有一把 SemaphoreSlim(1,1)，后果是：一条要等设备指纹门的
/// 请求（最长 30s）会把同实例上的所有请求堵在后面——页面停在「查询中」、按钮点了没反应，
/// 连 <see cref="CancelGateAsync"/> 也排在门后面，等待窗的「取消」实际到不了服务端。
/// 服务端管道是多实例的（AUTH 占着连接 30s 期间并发 STATUS 照常应答，2026-09-06 实测），
/// 并发短连接没有问题。</para>
/// </summary>
public sealed class PipeClient
{
    public async Task<string?> SendAsync(string request, int timeoutMs = 15000, CancellationToken ct = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeNames.ClientService,
                PipeDirection.InOut, PipeOptions.Asynchronous);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            await IpcFraming.WriteFrameAsync(client, request, cts.Token).ConfigureAwait(false);
            return await IpcFraming.ReadFrameAsync(client, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null; // Service 未运行 / 超时
        }
    }

    // ---- 便捷封装 ----

    /// <summary>IPC 加固健康状态（老版本服务回 UNKNOWN_COMMAND，调用方按「无数据」处理）。</summary>
    public Task<string?> SecurityStatusAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Security}:{IpcProtocol.SecurityStatus}", timeoutMs: 5000, ct: ct);

    public Task<string?> StatusAsync(CancellationToken ct = default)
        => SendAsync(IpcProtocol.Status, ct: ct);

    public Task<string?> InfoAsync(CancellationToken ct = default)
        => SendAsync(IpcProtocol.Info, ct: ct);

    /// <summary>OK:&lt;interval&gt;:&lt;latency&gt;:&lt;timeout&gt;（BLE 原生单位）。</summary>
    public Task<string?> ConnParamsAsync(CancellationToken ct = default)
        => SendAsync(IpcProtocol.ConnParams, ct: ct);

    /// <summary>流式 OTA 推送：发整个 .imfw（base64），回调百分比，返回终态帧。</summary>
    public async Task<string> OtaPushStreamAsync(byte[] imfw, Action<int> onPercent, CancellationToken ct = default)
    {
        try
        {
            string b64 = Convert.ToBase64String(imfw);
            using var client = new NamedPipeClientStream(".", PipeNames.ClientService,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000, ct).ConfigureAwait(false);
            await IpcFraming.WriteFrameAsync(client, $"{IpcProtocol.Ota}:{IpcProtocol.OtaPush}:{b64}", ct)
                .ConfigureAwait(false);
            while (true)
            {
                string? frame = await IpcFraming.ReadFrameAsync(client, ct).ConfigureAwait(false);
                if (frame is null) return "ERROR:DISCONNECTED";
                if (frame.StartsWith("PROGRESS:", StringComparison.Ordinal))
                {
                    if (int.TryParse(frame.AsSpan(9), out int pct)) onPercent(pct);
                    continue;
                }
                return frame;
            }
        }
        catch (Exception ex) { return $"ERROR:{ex.Message}"; }
    }

    public Task<string?> PairStatusAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Pair}:{IpcProtocol.PairStatus}", ct: ct);

    /// <summary>
    /// 流式配对：连接后发 PAIR:START，循环读取阶段帧（PROGRESS:...）回调 onProgress，
    /// 直到收到终态帧（OK:PAIRED / ERROR:*）并返回之。
    ///
    /// 配对要挂到用户按完键为止（可达一分多钟），中间有多个阶段帧，所以不走 <see cref="SendAsync"/>
    /// 的「一帧问一帧答」。服务端管道是多实例的，独立连接没问题。
    /// </summary>
    public async Task<string> PairStartStreamAsync(Action<string> onProgress, CancellationToken ct = default)
    {
        using var client = new NamedPipeClientStream(".", PipeNames.ClientService,
            PipeDirection.InOut, PipeOptions.Asynchronous);

        // 连不上 = 服务没在跑，这和「配对本身失败」是两码事，给个可分辨的码。
        try { await client.ConnectAsync(5000, ct).ConfigureAwait(false); }
        catch (Exception) { return "ERROR:NOSERVICE"; }

        try
        {
            await IpcFraming.WriteFrameAsync(client, $"{IpcProtocol.Pair}:{IpcProtocol.PairStart}", ct)
                .ConfigureAwait(false);

            while (true)
            {
                string? frame = await IpcFraming.ReadFrameAsync(client, ct).ConfigureAwait(false);
                if (frame is null) return "ERROR:DISCONNECTED";
                if (frame.StartsWith("PROGRESS:", StringComparison.Ordinal))
                {
                    onProgress(frame);
                    continue;
                }
                return frame; // 终态
            }
        }
        catch (Exception ex)
        {
            return $"ERROR:{ex.Message}";
        }
    }

    public Task<string?> PairResetAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Pair}:{IpcProtocol.PairReset}", ct: ct);

    // ---- 双主机 ----
    public Task<string?> SlotStatusAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Slot}:{IpcProtocol.SlotStatus}", ct: ct);

    public Task<string?> SlotClearOwnAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Slot}:{IpcProtocol.SlotClearOwn}", timeoutMs: 15000, ct: ct);

    /// <summary>解绑另一台（需在设备上触摸已登记指纹）。</summary>
    public Task<string?> SlotClearAsync(byte slot, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Slot}:{IpcProtocol.SlotClear}:{slot}", timeoutMs: 35000, ct: ct);

    public Task<string?> FpListAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Fp}:{IpcProtocol.FpList}", ct: ct);

    public Task<string?> FpSlotsAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Fp}:{IpcProtocol.FpSlots}", ct: ct);

    /// <summary>
    /// 流式录入：连接后发 FP:ENROLL，循环读取进度帧（PROGRESS:...）回调 onProgress，
    /// 直到收到终态帧（OK:COMPLETE / ERROR:*）并返回之。
    /// </summary>
    public async Task<string> EnrollStreamAsync(byte slot, Action<string> onProgress, CancellationToken ct = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeNames.ClientService,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000, ct).ConfigureAwait(false);
            await IpcFraming.WriteFrameAsync(client, $"{IpcProtocol.Fp}:{IpcProtocol.FpEnroll}:{slot}", ct)
                .ConfigureAwait(false);

            while (true)
            {
                string? frame = await IpcFraming.ReadFrameAsync(client, ct).ConfigureAwait(false);
                if (frame is null) return "ERROR:DISCONNECTED";
                if (frame.StartsWith("PROGRESS:", StringComparison.Ordinal))
                {
                    onProgress(frame);
                    continue;
                }
                return frame; // 终态
            }
        }
        catch (Exception ex)
        {
            return $"ERROR:{ex.Message}";
        }
    }

    /// <summary>删除指纹（设备端要过 30s 指纹门，故本地超时留出余量）。</summary>
    public Task<string?> FpDeleteAsync(byte slot, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Fp}:{IpcProtocol.FpDelete}:{slot}", timeoutMs: 40000, ct: ct);

    /// <summary>读功能开关 -> OK:&lt;unlock&gt;:&lt;lock&gt;:&lt;ssh&gt;:&lt;agent&gt;:&lt;otp&gt;（各 0/1）。</summary>
    public Task<string?> FeatureGetAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Feature}:{IpcProtocol.FeatureGet}", ct: ct);

    /// <summary>写功能开关（不需设备触摸）。ssh 会连带启停 agent，故留长一点超时。</summary>
    public Task<string?> FeatureSetAsync(string name, bool on, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Feature}:{IpcProtocol.FeatureSet}:{name}:{(on ? 1 : 0)}", timeoutMs: 20000, ct: ct);

    /// <summary>取消进行中的指纹门（认证弹窗点「取消」）。</summary>
    public Task<string?> CancelGateAsync(CancellationToken ct = default)
        => SendAsync(IpcProtocol.CancelGate, timeoutMs: 5000, ct: ct);

    public Task<string?> PassStatusAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Pass}:{IpcProtocol.PassStatus}", ct: ct);

    public Task<string?> PassSetAsync(string user, string password, CancellationToken ct = default)
    {
        string bu = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(user));
        string bp = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password));
        // 服务侧要求一次设备指纹确认（最长 30s），超时要留够；调用方应套在 FpAuthDialog 里。
        return SendAsync($"{IpcProtocol.Pass}:{IpcProtocol.PassSet}:{bu}:{bp}", timeoutMs: 35000, ct: ct);
    }

    public Task<string?> PassClearAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Pass}:{IpcProtocol.PassClear}", ct: ct);

    // ---- 密钥库 ----
    public Task<string?> KeyListAsync(byte cat, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Key}:{IpcProtocol.KeyList}:{cat}", timeoutMs: 30000, ct: ct);

    public Task<string?> KeyOtpAsync(byte idx, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Key}:{IpcProtocol.KeyOtp}:{idx}", timeoutMs: 35000, ct: ct);

    /// <summary>读 API 密钥的值（含指纹门；Keys 页用它复制到剪贴板）。-> OK:&lt;b64value&gt; | DENY。</summary>
    public Task<string?> KeyApiAsync(byte idx, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Key}:{IpcProtocol.KeyApi}:{idx}", timeoutMs: 35000, ct: ct);

    public Task<string?> KeyDeleteAsync(byte cat, byte idx, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Key}:{IpcProtocol.KeyDelete}:{cat}:{idx}", timeoutMs: 35000, ct: ct);

    public Task<string?> KeySshPubAsync(byte idx, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Key}:{IpcProtocol.KeySshPub}:{idx}", ct: ct);

    public Task<string?> KeyAddOtpAsync(string name, string service, string secretB32, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Key}:{IpcProtocol.KeyAddOtp}:{B64(name)}:{B64(service)}:{B64(secretB32)}", timeoutMs: 35000, ct: ct);

    public Task<string?> KeyAddApiAsync(string name, string value, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Key}:{IpcProtocol.KeyAddApi}:{B64(name)}:{B64(value)}", timeoutMs: 35000, ct: ct);

    public Task<string?> KeySshGenAsync(string name, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Key}:{IpcProtocol.KeySshGen}:{B64(name)}", timeoutMs: 35000, ct: ct);

    /// <summary>重命名条目（OTP 可同时改服务名；SSH/API 的 service 传空）。</summary>
    public Task<string?> KeyUpdateAsync(byte cat, byte idx, string name, string service, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Key}:{IpcProtocol.KeyUpdate}:{cat}:{idx}:{B64(name)}:{B64(service)}", timeoutMs: 35000, ct: ct);

    private static string B64(string s)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    // ---- SSH agent ----
    public Task<string?> SshAgentStatusAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.SshAgent}:STATUS", ct: ct);

    /// <summary>启停 SSH agent（不需设备触摸；每次签名各自过指纹门）。</summary>
    public Task<string?> SshAgentSetAsync(bool on, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.SshAgent}:{(on ? "ON" : "OFF")}", timeoutMs: 20000, ct: ct);

    // ---- WSL2 支持 ----
    public Task<string?> WslStatusAsync(CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Wsl}:STATUS", ct: ct);

    /// <summary>开 / 关 WSL 桥服务端。ON 回 <c>OK:&lt;token&gt;</c>，OFF 回 <c>OK</c>（不需设备触摸）。</summary>
    public Task<string?> WslSetAsync(bool on, CancellationToken ct = default)
        => SendAsync($"{IpcProtocol.Wsl}:{(on ? "ON" : "OFF")}", timeoutMs: 20000, ct: ct);
}
