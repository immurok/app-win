using ImmurokService.Platform;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Ssh;

/// <summary>
/// SSH agent 的一键开 / 关：开 = 记开关 + 接管系统 ssh-agent + 起管道；关 = 停管道 + 还原系统 ssh-agent。
/// 两个 IPC 入口（SSHAGENT:ON/OFF、FEATURE:SET:ssh）和服务启动都走这里，别各写一份。
/// 客户端拿到应答后再做它那半边（git 指向原生 OpenSSH），那部分只能以用户身份写 .gitconfig。
/// </summary>
public sealed class SshAgentFeature
{
    /// <summary>ON 的应答尾缀：管道已监听且系统 agent 已停 / 未装 / 停失败；或管道被占。</summary>
    public const string Taken = "TAKEN";
    public const string Absent = "ABSENT";
    public const string TakeoverFailed = "TAKEOVER_FAILED";
    public const string Contended = "CONTENDED";
    /// <summary>OFF 的应答尾缀。</summary>
    public const string Restored = "RESTORED";
    public const string NothingToRestore = "NONE";
    public const string RestoreFailed = "RESTORE_FAILED";

    private readonly AppSettings _settings;
    private readonly SystemSshAgent _system;
    private readonly SshAgentServer _server;
    private readonly ILogger<SshAgentFeature> _log;

    public SshAgentFeature(AppSettings settings, SystemSshAgent system, SshAgentServer server, ILogger<SshAgentFeature> log)
    {
        _settings = settings;
        _system = system;
        _server = server;
        _log = log;
    }

    /// <summary>开启并返回状态尾缀（见常量）。</summary>
    public async Task<string> EnableAsync()
    {
        _settings.SshAgentEnabled = true;
        var takeover = _system.Takeover();
        _server.Start();
        bool bound = await _server.WaitBoundAsync(TimeSpan.FromSeconds(3));
        _ = _server.RefreshIdentitiesAsync();

        string status = !bound ? Contended : takeover switch
        {
            TakeoverResult.Taken => Taken,
            TakeoverResult.Absent => Absent,
            _ => TakeoverFailed,
        };
        _log.LogInformation("SSH agent 开启：{Status}", status);
        return status;
    }

    /// <summary>关闭并返回状态尾缀（见常量）。先停管道再还原，否则系统 agent 起不来。</summary>
    public async Task<string> DisableAsync()
    {
        _settings.SshAgentEnabled = false;
        await _server.StopAsync();
        string status = _system.Restore() switch
        {
            RestoreResult.Restored => Restored,
            RestoreResult.Nothing => NothingToRestore,
            _ => RestoreFailed,
        };
        _log.LogInformation("SSH agent 关闭：{Status}", status);
        return status;
    }

    /// <summary>服务启动：开关为 ON 就重新接管（Windows 更新可能把系统 ssh-agent 拉回来）。</summary>
    public void StartIfEnabled()
    {
        if (!_settings.SshAgentEnabled) return;
        _system.Takeover();
        _server.Start();
    }
}
