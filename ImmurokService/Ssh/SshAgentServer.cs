using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using ImmurokService.Ble;
using ImmurokService.Ipc;
using ImmurokService.Security;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Ssh;

/// <summary>
/// Windows OpenSSH agent 服务端：占用命名管道 <c>\\.\pipe\openssh-ssh-agent</c>，
/// 让 ssh / git 用设备里的 SSH 私钥签名。移植自 macOS <c>SSHAgentServer.swift</c>（Unix socket → 命名管道）。
///
/// 支持：REQUEST_IDENTITIES(11)→IDENTITIES_ANSWER(12)、SIGN_REQUEST(13)→SIGN_RESPONSE(14)，其余→FAILURE(5)。
/// 前提：需先停用 Windows 内置 ssh-agent 服务（否则该管道名被占用）。
/// </summary>
public sealed class SshAgentServer : IAsyncDisposable
{
    private const string PipeName = "openssh-ssh-agent"; // 完整名 \\.\pipe\openssh-ssh-agent
    private const byte SSH_AGENT_FAILURE = 5;
    private const byte SSH_AGENTC_REQUEST_IDENTITIES = 11;
    private const byte SSH_AGENT_IDENTITIES_ANSWER = 12;
    private const byte SSH_AGENTC_SIGN_REQUEST = 13;
    private const byte SSH_AGENT_SIGN_RESPONSE = 14;

    private readonly ILogger<SshAgentServer> _log;
    private readonly BleManager _ble;
    private readonly OwnerStore _owner;

    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private bool _warnedNoOwner;
    /// <summary>最近一次建管道尝试的结果：0 = 进行中 / 未知，1 = 已监听，-1 = 管道名被占。</summary>
    private volatile int _bindState;

    /// <summary>第一个实例创建失败（管道名被占，通常是 Windows 内置 ssh-agent 没停）时为 true。</summary>
    public bool Contended { get; private set; }

    // 身份缓存：keyBlob(104B) + 名称 + 设备槽位。
    private volatile List<(byte Idx, string Name, byte[] Blob)> _identities = new();

    public bool IsRunning => _acceptLoop is not null;

    public SshAgentServer(ILogger<SshAgentServer> log, BleManager ble, OwnerStore owner, SecurityStatus status)
    {
        _log = log;
        _ble = ble;
        _owner = owner;
        status.RegisterPipe(PipeName, () => Contended);
    }

    public void Start()
    {
        if (_acceptLoop is not null) return;
        _cts = new CancellationTokenSource();
        _bindState = 0;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _ = RefreshIdentitiesAsync();
        _log.LogInformation("SSH agent 已启动: \\\\.\\pipe\\{Pipe}", PipeName);
    }

    public async Task StopAsync()
    {
        if (_acceptLoop is null) return;
        _cts?.Cancel();
        try { await _acceptLoop; } catch { /* ignore */ }
        _acceptLoop = null;
        _cts?.Dispose();
        _log.LogInformation("SSH agent 已停止");
    }

    /// <summary>
    /// 等一次建管道尝试出结果：true = 已监听，false = 管道名被占（<see cref="Contended"/>）或超时。
    /// 给一键开启的应答用，让客户端能立刻告诉用户是否真的接管成功。若 agent 早已在跑且处于
    /// 被占重试中（每 2s 一次），接管系统 ssh-agent 后下一次重试会把状态翻成已监听，这里等得到。
    /// </summary>
    public async Task<bool> WaitBoundAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        // 已经是「被占」时，等它下一次重试的结论，不拿旧结论回答。
        bool skipStale = _bindState == -1;
        while (DateTime.UtcNow < deadline)
        {
            int st = _bindState;
            if (st == 1) return true;
            if (st == -1 && !skipStale) return false;
            if (st != -1) skipStale = false;
            await Task.Delay(100);
        }
        return _bindState == 1;
    }

    /// <summary>刷新设备 SSH 身份缓存。</summary>
    public async Task RefreshIdentitiesAsync()
    {
        try
        {
            if (!_ble.IsConnected) return;
            var keys = await _ble.SshListWithPubAsync();
            var ids = new List<(byte, string, byte[])>();
            foreach (var (idx, name, pubBe) in keys)
                ids.Add((idx, string.IsNullOrEmpty(name) ? $"immurok-{idx}" : name, BleManager.SshBlob(pubBe)));
            _identities = ids;
            _log.LogInformation("SSH 身份缓存刷新：{Count} 个", ids.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "刷新 SSH 身份失败"); }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                _bindState = 0;
                pipe = CreatePipe(first);
                first = false;
                Contended = false;
                _bindState = 1;
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _ = ServePipeAsync(pipe, ct);
            }
            catch (OperationCanceledException) { pipe?.Dispose(); break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 管道名被占用（Windows 内置 ssh-agent 未停）等。不静默，Contended 供状态页。
                Contended = true;
                _bindState = -1;
                _log.LogError(ex, "SSH agent 管道创建失败（是否未停用 Windows ssh-agent 服务？）");
                pipe?.Dispose();
                await Task.Delay(2000, ct).ContinueWith(_ => { }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "SSH agent accept 异常");
                pipe?.Dispose();
                await Task.Delay(1000, ct).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// ssh-agent 管道 ACL：owner（设备主人）读写，SYSTEM 与本进程用户完全控制（<see cref="PipeAcl"/>）。
    /// 之前用的是无 ACL 重载，即 SYSTEM token 的默认 DACL（SYSTEM + Administrators）——普通用户 token
    /// 连 <c>ssh-add -l</c> 都是 Permission denied，功能对标准用户实际上是坏的。没有 owner 记录时退回
    /// Authenticated Users 读写并告警一次。对标 Linux 的活动会话校验（设计稿 §3.4）。
    /// 每次 accept 都重建实例，配对换了 owner 之后下一条连接就用新 ACL。
    /// </summary>
    private NamedPipeServerStream CreatePipe(bool first)
    {
        SecurityIdentifier? rw = _owner.Sid;
        if (rw is null)
        {
            if (!_warnedNoOwner)
            {
                _log.LogWarning("尚无 owner 记录，SSH agent 管道暂向所有本机用户开放（配对或设置密码后收紧）");
                _warnedNoOwner = true;
            }
            rw = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        }
        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            0, 0, PipeAcl.Build(rw));
    }

    private Task ServePipeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        byte[] one = new byte[1];
        // 对端关闭时读返回 0 或抛异常；有数据也按断开算（签名期间 ssh 不会再发东西）。
        return ServeStreamAsync(pipe, GetClientPid(pipe),
            async c => { await pipe.ReadAsync(one.AsMemory(0, 1), c).ConfigureAwait(false); }, ct);
    }

    /// <summary>
    /// 在任意双向流上跑 ssh-agent 协议，直到对端关闭；结束时释放流。命名管道与 WSL 的
    /// Hyper-V socket（<see cref="ImmurokService.Wsl.WslBridgeServer"/>）共用这一份实现。
    /// </summary>
    /// <param name="clientPid">对端的 Windows 进程 ID，用于把指纹提示附到它的控制台；0 = 没有（WSL），不显示终端提示。</param>
    /// <param name="waitPeerClosed">签名等指纹期间用来发现对端断开：对端关闭时完成，可被取消。</param>
    /// <param name="hint">
    /// 指纹门进度的去向（WSL：发 <see cref="ImmurokService.Wsl.WslHint"/> 提示帧给桥，由桥画到 WSL 终端）；
    /// null = 不发。参数是 (code, arg)。在门事件的回调线程上同步调用，实现里不能抛。
    /// </param>
    public async Task ServeStreamAsync(Stream pipe, uint clientPid, Func<CancellationToken, Task> waitPeerClosed,
        CancellationToken ct, Action<byte, byte>? hint = null)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? msg = await ReadMessageAsync(pipe, ct).ConfigureAwait(false);
                if (msg is null || msg.Length < 1) break;
                byte type = msg[0];
                byte[] payload = msg[1..];

                switch (type)
                {
                    case SSH_AGENTC_REQUEST_IDENTITIES:
                        await SendAsync(pipe, await BuildIdentitiesAnswerAsync(), ct);
                        break;
                    case SSH_AGENTC_SIGN_REQUEST:
                        await HandleSignAsync(pipe, payload, clientPid, waitPeerClosed, hint, ct);
                        break;
                    default:
                        await SendAsync(pipe, new byte[] { SSH_AGENT_FAILURE }, ct);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "SSH agent 连接处理结束");
        }
        finally { pipe.Dispose(); }
    }

    private async Task<byte[]> BuildIdentitiesAnswerAsync()
    {
        // 管道 agent 没开（只开了 WSL）时没人预热缓存：空了就现刷一次。
        if (_identities.Count == 0) await RefreshIdentitiesAsync().ConfigureAwait(false);
        var ids = _identities;
        var body = new MemoryStream();
        body.WriteByte(SSH_AGENT_IDENTITIES_ANSWER);
        WriteU32(body, (uint)ids.Count);
        foreach (var (_, name, blob) in ids)
        {
            WriteString(body, blob);
            WriteString(body, System.Text.Encoding.UTF8.GetBytes(name));
        }
        return body.ToArray();
    }

    private async Task HandleSignAsync(Stream pipe, byte[] payload, uint clientPid,
        Func<CancellationToken, Task> waitPeerClosed, Action<byte, byte>? hint, CancellationToken ct)
    {
        int off = 0;
        byte[]? keyBlob = ReadString(payload, ref off);
        byte[]? data = ReadString(payload, ref off);
        if (keyBlob is null || data is null) { await SendFailAsync(pipe, ct); return; }

        // 匹配身份；缓存未命中则刷新一次再试。
        byte? idx = FindIdx(keyBlob);
        if (idx is null) { await RefreshIdentitiesAsync(); idx = FindIdx(keyBlob); }
        if (idx is null) { _log.LogWarning("SSH 签名：未找到匹配密钥"); await SendFailAsync(pipe, ct); return; }

        _log.LogInformation("SSH 签名请求：槽 {Idx}，{Len} 字节（请触摸指纹）", idx, data.Length);

        byte[] hash = SHA256.HashData(data);

        // 终端指纹提示 + 三次按错自动退出（退出由指纹门内建，这里只负责显示进度）。
        ConsolePrompt? prompt = null;
        // 同样的进度有两个去向：Windows 上的 ssh.exe 走 ConsolePrompt 附它的控制台；WSL 里的 ssh 走 hint 交给桥。
        void OnGate()
        {
            if (clientPid != 0) prompt ??= ConsolePrompt.TryStart(clientPid, _log);
            hint?.Invoke(Wsl.WslHint.Waiting, 0);
        }
        void OnApproved() { prompt?.Signing(); hint?.Invoke(Wsl.WslHint.Signing, 0); }
        void OnFail(int rem) { prompt?.Fail(rem); hint?.Invoke(Wsl.WslHint.Mismatch, (byte)Math.Clamp(rem, 0, 255)); }
        _ble.FpGateRequired += OnGate;
        _ble.FpGateApproved += OnApproved;
        _ble.FpGateAttemptFailed += OnFail;

        // 监听 ssh 客户端断开（Ctrl+C）：一断就取消设备指纹门，别让设备空等闪灯。
        using var watchCts = new CancellationTokenSource();
        Task watch = WatchClientDisconnectAsync(waitPeerClosed, watchCts.Token, () => _ = _ble.CancelGateAsync());

        byte[]? sig;
        BleManager.SignFailure failure;
        try
        {
            (sig, failure) = await _ble.SshSignAsync(idx.Value, hash);
        }
        finally
        {
            watchCts.Cancel();
            _ble.FpGateRequired -= OnGate;
            _ble.FpGateApproved -= OnApproved;
            _ble.FpGateAttemptFailed -= OnFail;
        }

        if (sig is not { Length: 64 })
        {
            _log.LogInformation("SSH 签名失败: {Failure}", failure);
            // Ctrl+C（客户端已断开）：shell 已在本行打印新提示符，什么都不写，只让助手擦除退出。
            // 其余失败在终端留一行明确的错误（没进过指纹门的失败，例如设备未连接 / 直接 0xE1，现起助手），
            // 等助手退出后再回包，保证错误行先于 ssh 自己的报错。
            if (failure != BleManager.SignFailure.Cancelled)
            {
                if (clientPid != 0) prompt ??= ConsolePrompt.TryStart(clientPid, _log);
                prompt?.Error(failure);
            }
            prompt?.Finish();
            // 失败原因先于 FAILURE 应答送到桥，桥据此在 WSL 终端留一行。取消（对端已断）不发。
            if (failure != BleManager.SignFailure.Cancelled)
            {
                hint?.Invoke(failure switch
                {
                    BleManager.SignFailure.FingerprintRejected => Wsl.WslHint.FailRejected,
                    BleManager.SignFailure.Timeout => Wsl.WslHint.FailTimeout,
                    _ => Wsl.WslHint.FailOther,
                }, 0);
            }
            await SendFailAsync(pipe, ct);
            return;
        }

        // 构造 ecdsa 签名 blob： [string "ecdsa-sha2-nistp256"][string ([mpint r][mpint s])]
        byte[] r = sig[..32];
        byte[] s = sig[32..];
        var ecdsa = new MemoryStream();
        WriteMpint(ecdsa, r);
        WriteMpint(ecdsa, s);
        var sigBlob = new MemoryStream();
        WriteString(sigBlob, System.Text.Encoding.ASCII.GetBytes("ecdsa-sha2-nistp256"));
        WriteString(sigBlob, ecdsa.ToArray());

        var body = new MemoryStream();
        body.WriteByte(SSH_AGENT_SIGN_RESPONSE);
        WriteString(body, sigBlob.ToArray());

        // 先擦除终端提示并等助手退出（确保擦除完成、不残留），再回写响应给 ssh。
        prompt?.Finish();
        bool sent = await SendAsync(pipe, body.ToArray(), ct);
        _log.Log(sent ? LogLevel.Information : LogLevel.Debug,
            sent ? "SSH 签名完成" : "SSH 签名完成但客户端已断开，响应未送达");
    }

    private byte? FindIdx(byte[] keyBlob)
    {
        foreach (var (idx, _, blob) in _identities)
            if (blob.AsSpan().SequenceEqual(keyBlob)) return idx;
        return null;
    }

    /// <summary>取管道客户端（ssh.exe）的进程 ID，供终端提示附控制台。</summary>
    private static uint GetClientPid(NamedPipeServerStream pipe)
    {
        try
        {
            return GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) ? pid : 0;
        }
        catch { return 0; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    private Task SendFailAsync(Stream pipe, CancellationToken ct)
        => SendAsync(pipe, new byte[] { SSH_AGENT_FAILURE }, ct);

    /// <summary>
    /// 签名期间监听客户端断开：ssh 被 Ctrl+C 杀掉后其连接关闭，<paramref name="waitPeerClosed"/> 完成或抛异常，
    /// 触发回调（取消设备指纹门）。签名正常完成时由 watchCts 取消本任务。
    /// </summary>
    private static async Task WatchClientDisconnectAsync(Func<CancellationToken, Task> waitPeerClosed, CancellationToken ct, Action onDisconnect)
    {
        try
        {
            await waitPeerClosed(ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested) onDisconnect();
        }
        catch (OperationCanceledException) { /* 签名正常完成 */ }
        catch { if (!ct.IsCancellationRequested) onDisconnect(); }
    }

    // ---- 线协议：[uint32 BE 长度][body] ----

    private static async Task<byte[]?> ReadMessageAsync(Stream pipe, CancellationToken ct)
    {
        byte[] lenBuf = new byte[4];
        if (!await ReadExactAsync(pipe, lenBuf, 4, ct)) return null;
        int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
        if (len <= 0 || len > 256 * 1024) return null;
        byte[] body = new byte[len];
        if (!await ReadExactAsync(pipe, body, len, ct)) return null;
        return body;
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }

    /// <summary>发送一帧；客户端已断开则返回 false（不抛异常）。</summary>
    private static async Task<bool> SendAsync(Stream pipe, byte[] body, CancellationToken ct)
    {
        try
        {
            byte[] len = { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length };
            await pipe.WriteAsync(len.AsMemory(0, 4), ct).ConfigureAwait(false);
            await pipe.WriteAsync(body.AsMemory(0, body.Length), ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return false; // ssh 已断开
        }
    }

    private static void WriteU32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static void WriteString(Stream s, byte[] data)
    {
        WriteU32(s, (uint)data.Length);
        s.Write(data, 0, data.Length);
    }

    /// <summary>SSH mpint：正整数，若最高位为 1 则前置 0x00。</summary>
    private static void WriteMpint(Stream s, byte[] be)
    {
        int start = 0;
        while (start < be.Length - 1 && be[start] == 0) start++; // 去前导零
        bool pad = (be[start] & 0x80) != 0;
        int len = be.Length - start + (pad ? 1 : 0);
        WriteU32(s, (uint)len);
        if (pad) s.WriteByte(0);
        s.Write(be, start, be.Length - start);
    }

    private static byte[]? ReadString(byte[] buf, ref int off)
    {
        if (off + 4 > buf.Length) return null;
        int len = (buf[off] << 24) | (buf[off + 1] << 16) | (buf[off + 2] << 8) | buf[off + 3];
        off += 4;
        if (len < 0 || off + len > buf.Length) return null;
        byte[] r = new byte[len];
        Array.Copy(buf, off, r, 0, len);
        off += len;
        return r;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
