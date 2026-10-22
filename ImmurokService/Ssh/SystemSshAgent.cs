using System.ServiceProcess;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Ssh;

/// <summary>对一个 Windows 服务的最小控制面，便于测试时替换。</summary>
public interface IWindowsServiceControl
{
    bool Exists { get; }
    ServiceStartMode StartMode { get; }
    bool IsRunning { get; }
    void SetStartMode(ServiceStartMode mode);
    void Stop();
    void Start();
}

/// <summary>接管前系统 ssh-agent 的状态，用于一键关闭时还原。</summary>
public sealed record SystemSshAgentPrev(string StartMode, bool WasRunning);

/// <summary>接管记录的持久化（落在 settings.json）。</summary>
public interface ISystemSshAgentPrevStore
{
    SystemSshAgentPrev? Prev { get; set; }
}

public enum TakeoverResult { Taken, Absent, Failed }
public enum RestoreResult { Restored, Nothing, Failed }

/// <summary>
/// 接管 / 还原 Windows 内置 <c>ssh-agent</c> 服务：它与我们的 <see cref="SshAgentServer"/> 抢同一个
/// 管道名 <c>\\.\pipe\openssh-ssh-agent</c>。开启时停掉并禁用，记下原来的启动类型；关闭时按记录还原，
/// 让用户有「一键开 / 一键关」的体验，不用手动跑 PowerShell。
///
/// 记录只写一次（重复开启不覆盖），还原失败时保留记录以便下次重试。
/// </summary>
public sealed class SystemSshAgent
{
    private readonly IWindowsServiceControl _svc;
    private readonly ISystemSshAgentPrevStore _store;
    private readonly ILogger<SystemSshAgent> _log;

    public SystemSshAgent(IWindowsServiceControl svc, ISystemSshAgentPrevStore store, ILogger<SystemSshAgent> log)
    {
        _svc = svc;
        _store = store;
        _log = log;
    }

    /// <summary>停掉并禁用系统 ssh-agent。没装 OpenSSH 时返回 <see cref="TakeoverResult.Absent"/>。</summary>
    public TakeoverResult Takeover()
    {
        try
        {
            if (!_svc.Exists) return TakeoverResult.Absent;

            _store.Prev ??= new SystemSshAgentPrev(_svc.StartMode.ToString(), _svc.IsRunning);

            if (_svc.IsRunning) _svc.Stop();
            if (_svc.StartMode != ServiceStartMode.Disabled) _svc.SetStartMode(ServiceStartMode.Disabled);

            _log.LogInformation("已接管系统 ssh-agent（原状态 {Prev}）", _store.Prev);
            return TakeoverResult.Taken;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "接管系统 ssh-agent 失败");
            return TakeoverResult.Failed;
        }
    }

    /// <summary>按接管记录还原系统 ssh-agent；无记录则不动任何东西。</summary>
    public RestoreResult Restore()
    {
        var prev = _store.Prev;
        if (prev is null) return RestoreResult.Nothing;

        try
        {
            if (!_svc.Exists)
            {
                _store.Prev = null;
                return RestoreResult.Nothing;
            }

            if (Enum.TryParse(prev.StartMode, out ServiceStartMode mode) && _svc.StartMode != mode)
                _svc.SetStartMode(mode);
            if (prev.WasRunning && !_svc.IsRunning) _svc.Start();

            _store.Prev = null;
            _log.LogInformation("已还原系统 ssh-agent（{Prev}）", prev);
            return RestoreResult.Restored;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "还原系统 ssh-agent 失败，保留记录待重试");
            return RestoreResult.Failed;
        }
    }
}
