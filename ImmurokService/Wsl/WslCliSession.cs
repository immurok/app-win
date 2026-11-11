using System.Text;
using ImmurokService.Ble;
using ImmurokService.Ipc;
using ImmurokService.Platform;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Wsl;

/// <summary>
/// WSL 行协议会话：实现 Linux daemon（app-linux-rs <c>socket.rs</c>）行协议的一个子集，回包格式与
/// daemon 完全一致，这样 WSL 里跑的是原样的 Linux <c>imk</c> 和 <c>pam_immurok.so</c>。
/// 一条连接一问一答后关闭。
///
///   AUTH:&lt;user&gt;:&lt;service&gt;[:hints] -&gt; OK:AUTHENTICATED | OK:PRE_AUTH | DENY:NOT_CONNECTED | DENY:FP_DENIED
///                              带 :hints 时，最终应答之前每次指纹不匹配先回一行 RETRY:&lt;剩余次数&gt;
///   AGENT_APPROVE:&lt;cmd&gt;      -&gt; OK:APPROVED | DENY:REJECTED | DENY:SERVICE_DISABLED | ERROR:NOT_CONNECTED
///   LIST:&lt;ssh|otp|api&gt;       -&gt; OK:N\n&lt;行&gt;...\n\n   （ssh 行：&lt;name&gt;\tecdsa-sha2-nistp256 &lt;b64&gt;）
///   GET:&lt;cat&gt;:&lt;name&gt;         -&gt; OK:&lt;value&gt; | ERROR:&lt;reason&gt;
///   STATUS                   -&gt; OK:CONNECTED | OK:DISCONNECTED
///   GATE:CANCEL              -&gt; OK:GATE_CANCELLED
///   其他                      -&gt; ERROR:UNSUPPORTED_IN_WSL
///
/// 会改变安全状态的命令（配对、设密码、开关、OTA）一律不在这里开放：WSL 侧只是使用者。
/// 持有有效 token 的连接按 owner 处理（token 只在 owner 打开开关时灌进 owner 的 WSL）。
/// </summary>
public sealed class WslCliSession
{
    /// <summary>与 Linux daemon 的 MAX_REQUEST_BYTES 一致。</summary>
    private const int MaxRequestBytes = 64 * 1024;
    private static readonly TimeSpan PreAuthWindow = TimeSpan.FromSeconds(10);

    private readonly ILogger<WslCliSession> _log;
    private readonly BleManager _ble;
    private readonly AppSettings _settings;
    private readonly KeyQueries _keys;

    // AGENT_APPROVE 通过后的预授权窗口：紧跟着的 sudo（AUTH）不用按第二次，与 Linux daemon 语义一致。
    private long _preAuthUntilTicks;

    public WslCliSession(ILogger<WslCliSession> log, BleManager ble, AppSettings settings, KeyQueries keys)
    {
        _log = log;
        _ble = ble;
        _settings = settings;
        _keys = keys;
    }

    internal async Task RunAsync(HvSocketStream stream, WslPreamble pre, CancellationToken ct)
    {
        string? request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
        if (request is null) return;
        string response = await DispatchAsync(request, stream, pre, ct).ConfigureAwait(false);
        if (!response.EndsWith('\n')) response += "\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 读一条请求。imk 以换行结尾；pam_immurok.so 发的 <c>AUTH:user:service</c> 不带换行也不关连接
    /// （Linux daemon 是单次 read），所以：读到换行就停，否则在首批数据之后短暂静默即视为收完。
    /// </summary>
    private static async Task<string?> ReadRequestAsync(HvSocketStream stream, CancellationToken ct)
    {
        byte[] buf = new byte[MaxRequestBytes];
        int total = 0;
        using var first = CancellationTokenSource.CreateLinkedTokenSource(ct);
        first.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (total < buf.Length)
            {
                CancellationToken readCt = first.Token;
                using var idle = total > 0 ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
                if (idle is not null) { idle.CancelAfter(150); readCt = idle.Token; }
                int n;
                try { n = await stream.ReadAsync(buf.AsMemory(total), readCt).ConfigureAwait(false); }
                catch (OperationCanceledException) when (total > 0 && !ct.IsCancellationRequested) { break; }
                if (n == 0) break;
                total += n;
                if (Array.IndexOf(buf, (byte)'\n', 0, total) >= 0) break;
            }
        }
        catch (OperationCanceledException) { return null; }
        if (total == 0) return null;
        string text = Encoding.UTF8.GetString(buf, 0, total);
        int nl = text.IndexOf('\n');
        return (nl >= 0 ? text[..nl] : text).Trim('\r', ' ');
    }

    private async Task<string> DispatchAsync(string cmd, HvSocketStream stream, WslPreamble pre, CancellationToken ct)
    {
        if (cmd == "STATUS")
            return _ble.IsConnected ? "OK:CONNECTED" : "OK:DISCONNECTED";

        if (cmd == "GATE:CANCEL")
        {
            await _ble.CancelGateAsync().ConfigureAwait(false);
            return "OK:GATE_CANCELLED";
        }

        if (cmd.StartsWith("AUTH:", StringComparison.Ordinal))
        {
            // AUTH:<user>:<service>。user 不参与判断（WSL 内所有用户都是同一个 Windows 用户），也不进日志。
            string[] parts = cmd.Split(':');
            string service = parts.Length >= 3 ? parts[2] : "?";
            if (!_ble.IsConnected) return "DENY:NOT_CONNECTED";
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _preAuthUntilTicks))
            {
                Interlocked.Exchange(ref _preAuthUntilTicks, 0); // 一次触摸只顶一次 sudo
                _log.LogInformation("WSL {Distro}：{Service} 认证命中预授权窗口", pre.Distro, Short(service));
                return "OK:PRE_AUTH";
            }
            _log.LogInformation("WSL {Distro}：{Service} 认证请求（请触摸指纹）", pre.Distro, Short(service));
            // 第四段 "hints"：PAM 模块声明自己认识 RETRY 进度行（app-linux-rs pam/auth_reply.h）。
            // 没声明的旧模块把收到的第一条消息当最终应答，发 RETRY 给它等于直接判拒，所以只对声明了的发。
            bool wantsRetry = parts.Length >= 4 && parts[3] == "hints";
            var outcome = await GateAsync(stream, ct, wantsRetry).ConfigureAwait(false);
            _log.LogInformation("WSL {Distro}：{Service} 认证结果 {Outcome}", pre.Distro, Short(service), outcome);
            return outcome == BleManager.GateOutcome.Ok ? "OK:AUTHENTICATED" : "DENY:FP_DENIED";
        }

        if (cmd.StartsWith("AGENT_APPROVE:", StringComparison.Ordinal))
        {
            // 命令串只给用户看（imk 自己在终端打印），服务端不记。
            if (!_settings.ImkAgentEnabled)
            {
                _log.LogInformation("WSL imk agent 授权请求被拒：功能未开启");
                return "DENY:SERVICE_DISABLED";
            }
            if (!_ble.IsConnected) return "ERROR:NOT_CONNECTED";
            _log.LogInformation("WSL {Distro}：imk agent 授权请求（请触摸指纹）", pre.Distro);
            var outcome = await GateAsync(stream, ct).ConfigureAwait(false);
            _log.LogInformation("WSL {Distro}：imk agent 授权结果 {Outcome}", pre.Distro, outcome);
            if (outcome != BleManager.GateOutcome.Ok) return "DENY:REJECTED";
            Interlocked.Exchange(ref _preAuthUntilTicks, (DateTime.UtcNow + PreAuthWindow).Ticks);
            return "OK:APPROVED";
        }

        if (cmd.StartsWith("LIST:", StringComparison.Ordinal))
        {
            int cat = KeyQueries.CatOf(cmd[5..]);
            if (cat < 0) return "ERROR:UNKNOWN_CATEGORY";
            if (!_ble.IsConnected) return "ERROR:NOT_CONNECTED";
            var lines = new List<string>();
            if (cat == BleManager.CatSsh)
            {
                foreach (var (idx, name, pubBe) in await _ble.SshListWithPubAsync().ConfigureAwait(false))
                {
                    string n = string.IsNullOrEmpty(name) ? $"immurok-{idx}" : name;
                    lines.Add($"{n}\tecdsa-sha2-nistp256 {Convert.ToBase64String(BleManager.SshBlob(pubBe))}");
                }
            }
            else
            {
                lines.AddRange(await _keys.ListNamesAsync((byte)cat).ConfigureAwait(false));
            }
            var sb = new StringBuilder($"OK:{lines.Count}\n");
            foreach (string l in lines) sb.Append(l).Append('\n');
            sb.Append('\n');
            return sb.ToString();
        }

        if (cmd.StartsWith("GET:", StringComparison.Ordinal))
        {
            // GET:<cat>:<name>（name 可含冒号，只切两段）
            string rest = cmd[4..];
            int sep = rest.IndexOf(':');
            if (sep <= 0 || sep == rest.Length - 1) return "ERROR:USAGE";
            int cat = KeyQueries.CatOf(rest[..sep]);
            if (cat < 0) return "ERROR:UNKNOWN_CATEGORY";
            if (!_ble.IsConnected) return "ERROR:NOT_CONNECTED";
            // 读秘密的指纹门期间同样盯着对端：imk 被 Ctrl+C 就取消设备上的门。
            using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task watch = WatchAsync(stream, watchCts.Token);
            try
            {
                var (ok, value) = await _keys.GetAsync((byte)cat, rest[(sep + 1)..]).ConfigureAwait(false);
                return (ok ? "OK:" : "ERROR:") + value;
            }
            finally { watchCts.Cancel(); }
        }

        return "ERROR:UNSUPPORTED_IN_WSL";
    }

    /// <summary>
    /// 走一次设备指纹门（AUTH_REQUEST，固件永远要求新触摸）。等待期间对端断开（PAM 里 Ctrl+C / 按键
    /// 回落密码、imk 被杀）就取消门——这是取消到达设备的唯一路径。
    /// </summary>
    /// <param name="sendRetry">
    /// 每次指纹不匹配时先回一行 <c>RETRY:&lt;剩余次数&gt;</c>，PAM 模块据此把 spinner 换成「没认出来，还剩 N 次」。
    /// 最终应答仍由调用方在门结束后写。
    /// </param>
    private async Task<BleManager.GateOutcome> GateAsync(HvSocketStream stream, CancellationToken ct, bool sendRetry = false)
    {
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task watch = WatchAsync(stream, watchCts.Token);
        // 在门事件的回调线程上同步写：几个字节，且必须排在最终应答之前。写失败说明对端已断，由 watch 去处理。
        void OnFail(int remaining)
        {
            // 次数用尽时门随即结束，紧跟着就是最终的 DENY：不必再说「还剩 0 次，请再按」。
            if (remaining <= 0) return;
            try { stream.Write(Encoding.ASCII.GetBytes($"RETRY:{Math.Clamp(remaining, 0, 99)}\n")); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        if (sendRetry) _ble.FpGateAttemptFailed += OnFail;
        try { return await _ble.AuthenticateDetailedAsync().ConfigureAwait(false); }
        finally
        {
            if (sendRetry) _ble.FpGateAttemptFailed -= OnFail;
            watchCts.Cancel();
        }
    }

    private async Task WatchAsync(HvSocketStream stream, CancellationToken ct)
    {
        try
        {
            await stream.WaitPeerClosedAsync(ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested) _ = _ble.CancelGateAsync();
        }
        catch (OperationCanceledException) { /* 门正常结束 */ }
        catch { if (!ct.IsCancellationRequested) _ = _ble.CancelGateAsync(); }
    }

    /// <summary>PAM 服务名进日志前收紧（它来自 WSL 侧，不可信）。</summary>
    private static string Short(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, 32));
        foreach (char c in s)
        {
            if (sb.Length >= 32) break;
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '?');
        }
        return sb.ToString();
    }
}
