using System.Net.Sockets;
using ImmurokService.Platform;
using ImmurokService.Ssh;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Wsl;

/// <summary>
/// WSL2 支持的服务端：给每台运行中的 WSL 虚拟机在两个 Hyper-V socket 服务上监听，
/// WSL 内的 <c>immurok-wsl-bridge</c>（root）把 <c>/run/immurok/pam.sock</c> 与 <c>agent.sock</c>
/// 的连接逐条转过来。设计见 <c>docs/superpowers/specs/2026-09-06-wsl2-support-design.md</c>。
///
///   vsock 端口 0x494D4B01 —— Linux daemon 行协议子集（sudo 的 PAM、imk） → <see cref="WslCliSession"/>
///   vsock 端口 0x494D4B02 —— ssh-agent 协议（ssh / git）                  → <see cref="SshAgentServer.ServeStreamAsync"/>
///
/// 每条连接先过前导行（<see cref="WslPreamble"/>）里的 token。没有长连接、没有会话状态：
/// <c>wsl --shutdown</c>、服务重启、开关反复切换都不需要重连逻辑，只要监听跟着虚拟机走。
/// 监听必须 bind 到具体虚拟机 ID（通配收不到 WSL 的连接），所以这里每 2 秒用 HCS 对一次账。
/// </summary>
public sealed class WslBridgeServer : IAsyncDisposable
{
    public const uint CliPort = 0x494D4B01;
    public const uint SshPort = 0x494D4B02;
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PreambleTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<WslBridgeServer> _log;
    private readonly AppSettings _settings;
    private readonly WslTokenStore _token;
    private readonly WslCliSession _cli;
    private readonly SshAgentServer _sshAgent;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, IntPtr[]> _listeners = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _lastEnumError;
    private DateTime _lastBadPreambleLog = DateTime.MinValue;

    public bool IsRunning => _loop is not null;

    public WslBridgeServer(ILogger<WslBridgeServer> log, AppSettings settings, WslTokenStore token,
        WslCliSession cli, SshAgentServer sshAgent)
    {
        _log = log;
        _settings = settings;
        _token = token;
        _cli = cli;
        _sshAgent = sshAgent;
    }

    /// <summary>开关为开且有 token 时启动（服务启动时调用）。</summary>
    public void StartIfEnabled()
    {
        if (_settings.WslEnabled && _token.Current is not null) Start();
    }

    /// <summary>打开开关：落设置、确保有 token、启动监听。返回 token（客户端拿去装进 WSL）。</summary>
    public string Enable()
    {
        string token = _token.Ensure();
        _settings.WslEnabled = true;
        Start();
        return token;
    }

    /// <summary>关闭开关：停监听、删 token（WSL 里残留的桥从此连不上）。</summary>
    public async Task DisableAsync()
    {
        _settings.WslEnabled = false;
        await StopAsync().ConfigureAwait(false);
        _token.Delete();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null) return;
            _cts = new CancellationTokenSource();
            _lastEnumError = 0;
            CancellationToken ct = _cts.Token;
            _loop = Task.Run(() => ReconcileLoopAsync(ct));
        }
        _ = _sshAgent.RefreshIdentitiesAsync();
        _log.LogInformation("WSL 桥服务已启动");
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
            if (loop is null) return;
            _cts?.Cancel();
        }
        try { await loop.ConfigureAwait(false); } catch { /* ignore */ }
        lock (_gate)
        {
            foreach (IntPtr[] ls in _listeners.Values)
                foreach (IntPtr l in ls) HvSocket.Close(l);
            _listeners.Clear();
            _loop = null;
            _cts?.Dispose();
            _cts = null;
        }
        _log.LogInformation("WSL 桥服务已停止");
    }

    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Reconcile(ct); }
            catch (Exception ex) { _log.LogError(ex, "WSL 虚拟机对账异常"); }
            await Task.Delay(ReconcileInterval, ct).ContinueWith(_ => { }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>让监听集合与运行中的 WSL 虚拟机一致：新虚拟机建监听，消失的关掉。</summary>
    private void Reconcile(CancellationToken ct)
    {
        List<Guid>? vms = WslVms.Enumerate(out int hr);
        if (vms is null)
        {
            // 枚举失败不动现有监听。同一个错误只记一次（控制台调试模式下普通用户每 2 秒被拒一次）。
            if (hr != _lastEnumError)
            {
                _lastEnumError = hr;
                _log.LogWarning("枚举 WSL 虚拟机失败（HRESULT 0x{Hr:X8}）；服务需以 SYSTEM / 管理员运行", hr);
            }
            return;
        }
        _lastEnumError = 0;

        lock (_gate)
        {
            if (ct.IsCancellationRequested) return;
            foreach (Guid gone in _listeners.Keys.Where(k => !vms.Contains(k)).ToList())
            {
                foreach (IntPtr l in _listeners[gone]) HvSocket.Close(l);
                _listeners.Remove(gone);
                _log.LogInformation("WSL 虚拟机已停止，撤下监听");
            }
            foreach (Guid vm in vms)
            {
                if (_listeners.ContainsKey(vm)) continue;
                IntPtr cli = IntPtr.Zero;
                try
                {
                    cli = HvSocket.Listen(vm, HvSocket.ServiceIdForPort(CliPort));
                    IntPtr ssh = HvSocket.Listen(vm, HvSocket.ServiceIdForPort(SshPort));
                    _listeners[vm] = new[] { cli, ssh };
                    StartAcceptThread(vm, cli, ssh: false, ct);
                    StartAcceptThread(vm, ssh, ssh: true, ct);
                    _log.LogInformation("WSL 虚拟机上线，已建监听");
                }
                catch (SocketException ex)
                {
                    if (cli != IntPtr.Zero) HvSocket.Close(cli);
                    _log.LogError(ex, "WSL Hyper-V socket 监听失败");
                }
            }
        }
    }

    private void StartAcceptThread(Guid vm, IntPtr listener, bool ssh, CancellationToken ct)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                HvSocketStream? conn = HvSocket.Accept(listener);
                if (conn is null) break; // 监听被关（虚拟机消失 / 服务停止）或 accept 出错
                _ = Task.Run(() => HandleAsync(conn, ssh, ct));
            }
            // 不是我们主动关的：把这台虚拟机从表里摘掉，下一轮对账重建监听。
            lock (_gate)
            {
                if (!ct.IsCancellationRequested && _listeners.TryGetValue(vm, out IntPtr[]? ls) && ls.Contains(listener))
                {
                    foreach (IntPtr l in ls) HvSocket.Close(l);
                    _listeners.Remove(vm);
                }
            }
        })
        { IsBackground = true, Name = ssh ? "wsl-ssh-accept" : "wsl-cli-accept" };
        t.Start();
    }

    private async Task HandleAsync(HvSocketStream conn, bool ssh, CancellationToken ct)
    {
        try
        {
            WslPreamble? pre;
            using (var pcts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                pcts.CancelAfter(PreambleTimeout);
                pre = WslPreamble.Parse(await conn.ReadLineAsync(WslPreamble.MaxLength, pcts.Token).ConfigureAwait(false));
            }
            if (pre is null || !_token.Matches(pre.Token))
            {
                // 不回任何东西直接断开。限速记日志：虚拟机里任何进程都能连 vsock 来敲门。
                if (DateTime.UtcNow - _lastBadPreambleLog > TimeSpan.FromMinutes(1))
                {
                    _lastBadPreambleLog = DateTime.UtcNow;
                    _log.LogWarning("WSL 连接被拒：前导行缺失或 token 不符");
                }
                return;
            }

            if (ssh)
            {
                // ServeStreamAsync 结束时自己释放流；clientPid=0：WSL 里的 ssh 没有 Windows 控制台可附。
                // 桥声明了 hints=1 才发进度提示帧（见 WslHint）。写失败说明连接已断，由主流程去发现。
                Action<byte, byte>? hint = null;
                if (pre.Hints)
                {
                    hint = (code, arg) =>
                    {
                        try { conn.Write(WslHint.Frame(code, arg)); }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                    };
                }
                await _sshAgent.ServeStreamAsync(conn, 0, conn.WaitPeerClosedAsync, ct, hint).ConfigureAwait(false);
            }
            else
            {
                await _cli.RunAsync(conn, pre, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            _log.LogDebug("WSL 连接结束: {Reason}", ex.GetType().Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WSL 连接处理异常");
        }
        finally { conn.Dispose(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
