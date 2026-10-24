using System;
using System.Threading;
using System.Threading.Tasks;
using ImmurokCommon.Protocol;

namespace ImmurokClient.Services;

/// <summary>
/// 后台长轮询服务端的「指纹注入信号」（INJECT:POLL）。服务端把请求挂起最多 25 秒，
/// 期间来了指纹信号立即回 "OK:&lt;pageId&gt;"，否则超时回 "OK"；两种情况都立刻发起下一发。
/// 相比定时轮询，平时零空转、延迟近乎推送级；一次触摸经服务端一次性消费，不会重复注入。
/// </summary>
public sealed class InjectionListener
{
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 专用管道客户端，与 UI 的 <see cref="AppServices.Pipe"/> 分开，纯粹为了职责清晰。
    /// （PipeClient 早先有一把 SemaphoreSlim(1,1) 串行化请求，本监听每发挂 25 秒会把 UI 请求
    /// 全堵在后面，所以才必须分实例；那把锁已经去掉，现在共用也不会堵。）
    /// 服务端管道是多实例的，并发连接没问题。
    /// </summary>
    private readonly PipeClient _pipe = new();

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_pipe, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private static async Task LoopAsync(PipeClient pipe, CancellationToken ct)
    {
        string poll = $"{IpcProtocol.Inject}:{IpcProtocol.InjectPoll}";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 服务端最多挂起 25 秒，这里给 30 秒超时留足余量。
                // 命中信号回 "OK:<pageId>"，超时回 "OK"；无论哪种，立即发起下一发（零等待）。
                string? r = await pipe.SendAsync(poll, 30000, ct).ConfigureAwait(false);
                if (r is not null && r.StartsWith(IpcProtocol.Ok + ":", StringComparison.Ordinal))
                    InjectionEngine.TryInjectForeground();
                else if (r != IpcProtocol.Ok)
                {
                    // 服务秒回错误（如未签名构建被拒 ERROR:CALLER_NOT_TRUSTED）时不能零等待重发，
                    // 否则紧打转、服务端日志每秒几十条。被拒是装法问题，重试也不会变，退避久一点。
                    int delay = r == IpcProtocol.ErrCallerNotTrusted ? 60000 : 1000;
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // 服务未连 / 出错：短暂退避后重试，避免管道不可用时紧打转。
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch { /* 取消 */ }
            }
        }
    }
}
