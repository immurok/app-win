using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ImmurokService.Ble;
using ImmurokService.Platform;
using ImmurokService.Security;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Ipc;

/// <summary>
/// imk 命令行工具的服务端：命名管道 <c>\\.\pipe\immurok-cli</c>，行式文本协议（对齐 macOS CLISocketServer）。
/// 每个连接一问一答后关闭：
///   LIST:&lt;ssh|otp|api&gt;          -&gt; OK\n&lt;name&gt;\n&lt;name&gt;...      （无条目则仅 OK）
///   GET:&lt;cat&gt;:&lt;name&gt;           -&gt; OK:&lt;value&gt; | ERROR:&lt;reason&gt;
///     · otp -&gt; 当前 6 位验证码（指纹门）
///     · api -&gt; 明文密钥值
///     · ssh -&gt; OpenSSH 公钥行（私钥永不导出）
///   APPROVE:&lt;command&gt;          -&gt; OK | DENY   （imk agent 运行前的裸指纹认证）
///   CANCEL                     -&gt; OK          （取消进行中的指纹门；imk Ctrl+C 时用）
/// imk 读到 EOF（服务端写完即关闭）为止。
/// </summary>
public sealed class CliServer : IAsyncDisposable
{
    private const string PipeName = "immurok-cli";

    private readonly ILogger<CliServer> _log;
    private readonly BleManager _ble;
    private readonly AppSettings _settings;
    private readonly OwnerStore _owner;
    private readonly CallerPolicy _policy;
    private readonly KeyQueries _keys;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool IsRunning => _loop is not null;

    /// <summary>第一个实例创建失败（管道名被其他进程占用）时为 true，供 SECURITY:STATUS 报 contended。</summary>
    public bool Contended { get; private set; }

    public CliServer(ILogger<CliServer> log, BleManager ble, AppSettings settings, OwnerStore owner, CallerPolicy policy,
        SecurityStatus status, KeyQueries keys)
    {
        _keys = keys;
        _log = log;
        _ble = ble;
        _settings = settings;
        _owner = owner;
        _policy = policy;
        status.RegisterPipe(PipeName, () => Contended);
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.LogInformation("imk CLI 服务已启动: \\\\.\\pipe\\{Pipe}", PipeName);
    }

    public async Task StopAsync()
    {
        if (_loop is null) return;
        _cts?.Cancel();
        try { await _loop; } catch { /* ignore */ }
        _loop = null;
        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        bool first = true;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                try
                {
                    pipe = CreatePipe(first);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // 同 PipeServer：名字被占不静默，5s 重试，进程不退出。
                    failures++;
                    Contended = true;
                    _log.LogError(ex, "imk CLI 管道创建失败（第 {N} 次，名字可能被其他进程占用）", failures);
                    await Task.Delay(5000, ct).ContinueWith(_ => { }, CancellationToken.None);
                    continue;
                }
                first = false;
                if (failures > 0)
                {
                    _log.LogInformation("imk CLI 管道名已恢复可用（此前失败 {N} 次）", failures);
                    failures = 0;
                }
                Contended = false;
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _ = HandleAsync(pipe, ct);
            }
            catch (OperationCanceledException) { pipe?.Dispose(); break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "imk CLI accept 异常");
                pipe?.Dispose();
                await Task.Delay(1000, ct).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }

    /// <summary>ACL 见 <see cref="PipeAcl"/>；第一个实例带 FirstPipeInstance（设计稿 §3.2）。</summary>
    private static NamedPipeServerStream CreatePipe(bool first)
        => NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            0, 0, PipeAcl.Build(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)));

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var enc = new UTF8Encoding(false);
            CallerIdentity? caller = PipeCaller.Identify(pipe);
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            string response = await DispatchAsync(line?.Trim() ?? "", caller).ConfigureAwait(false);
            byte[] bytes = enc.GetBytes(response);
            await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "imk CLI 连接处理结束");
        }
        finally { pipe.Dispose(); }
    }

    private async Task<string> DispatchAsync(string cmd, CallerIdentity? caller)
    {
        // APPROVE / CANCEL 只认安装目录里的 imk.exe（§3.3）；LIST / GET 不看映像路径，但 GET 只给 owner（见下）。
        if ((cmd == "CANCEL" || cmd.StartsWith("APPROVE:", StringComparison.Ordinal))
            && _policy.Classify(caller) != CallerKind.TrustedClient)
        {
            _log.LogWarning("imk 命令被拒：调用方不可信（{Caller} image={Image}）", caller?.ToString() ?? "unknown", caller?.ImagePath ?? "?");
            return "ERROR:CALLER_NOT_TRUSTED";
        }

        // CANCEL 无需连接检查：取消进行中的指纹门（imk Ctrl+C 时用）。
        if (cmd == "CANCEL")
        {
            await _ble.CancelGateAsync().ConfigureAwait(false);
            return "OK";
        }

        if (!_ble.IsConnected) return "ERROR:NOT_CONNECTED";

        if (cmd.StartsWith("APPROVE:", StringComparison.Ordinal))
        {
            // imk agent 运行前授权：命令串仅供日志（Windows 无浮层，展示在 imk 终端）。
            string commandStr = cmd[8..];
            if (!_settings.ImkAgentEnabled)
            {
                _log.LogInformation("imk agent 授权请求被拒：功能未开启");
                return "DENY";
            }
            // 触摸只授权给 owner 的请求（设计稿 §3.5）。
            if (DenyIfNotOwner(caller, "APPROVE") is { } denied) return denied;
            _log.LogInformation("imk agent 授权请求：{Cmd}", commandStr);
            bool ok = await _ble.AuthenticateAsync().ConfigureAwait(false);
            return ok ? "OK" : "DENY";
        }

        if (cmd.StartsWith("LIST:", StringComparison.Ordinal))
        {
            int cat = KeyQueries.CatOf(cmd[5..]);
            if (cat < 0) return "ERROR:BAD_CATEGORY";
            // 列表也只给 owner（2026-09-06 用户决定）：值拿不到，名字也没必要给另一个账号看。
            if (DenyIfNotOwner(caller, "LIST") is { } denied) return denied;
            return await HandleListAsync((byte)cat).ConfigureAwait(false);
        }
        if (cmd.StartsWith("GET:", StringComparison.Ordinal))
        {
            // GET:<cat>:<name>（name 可含冒号，故只切两段）
            string rest = cmd[4..];
            int sep = rest.IndexOf(':');
            if (sep <= 0) return "ERROR:BAD_REQUEST";
            int cat = KeyQueries.CatOf(rest[..sep]);
            string name = rest[(sep + 1)..];
            if (cat < 0) return "ERROR:BAD_CATEGORY";
            if (name.Length == 0) return "ERROR:BAD_REQUEST";
            // 读秘密只给 owner（2026-09-06 多用户测试后补上）。原设计「靠设备指纹门、不靠身份」站不住：
            // 切换用户后另一账号的进程还活着，而指纹门预算是服务级的——owner 为别的事触摸一次，
            // 对方 60s 内不用触摸就能读走。与 SSH agent 管道 ACL、AUTH / APPROVE 的 owner 校验对齐。
            // 设备连灯都不亮。
            if (DenyIfNotOwner(caller, "GET") is { } denied) return denied;
            return await HandleGetAsync((byte)cat, name).ConfigureAwait(false);
        }
        return "ERROR:UNKNOWN_COMMAND";
    }

    /// <summary>
    /// owner 校验：非 owner 返回 <c>DENY:NOT_OWNER</c>，放行返回 null。
    /// 无 owner 记录时告警放行（升级过渡，配对或设置密码后会补上）；身份取不到按非 owner 拒绝。
    /// </summary>
    private string? DenyIfNotOwner(CallerIdentity? caller, string what)
    {
        if (!_owner.IsSet)
        {
            _log.LogWarning("imk {What}：尚无 owner 记录，放行（配对或设置密码后会补上）", what);
            return null;
        }
        if (caller is not null && _owner.IsOwner(caller.Sid) == true) return null;
        _log.LogWarning("imk {What} 被拒：调用方不是 owner（{Caller}）", what, caller?.ToString() ?? "unknown");
        return "DENY:NOT_OWNER";
    }

    private async Task<string> HandleListAsync(byte cat)
    {
        var names = await _keys.ListNamesAsync(cat).ConfigureAwait(false);
        return names.Count == 0 ? "OK" : "OK\n" + string.Join("\n", names);
    }

    private async Task<string> HandleGetAsync(byte cat, string name)
    {
        var (ok, value) = await _keys.GetAsync(cat, name).ConfigureAwait(false);
        return (ok ? "OK:" : "ERROR:") + value;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
