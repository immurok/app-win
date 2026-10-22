using System;
using System.IO;
using System.Threading.Tasks;
using ImmurokCommon.Git;
using ImmurokCommon.Protocol;
using ImmurokClient.Localization;

namespace ImmurokClient.Services;

/// <summary>
/// SSH agent 一键开 / 关的客户端半边：先让服务接管 / 还原系统 ssh-agent 并起停管道，
/// 成功后再以当前用户身份把 git 指向 / 还原为原生 OpenSSH。两个页面的开关都走这里。
/// </summary>
public static class SshAgentSwitch
{
    /// <param name="ServiceReply">服务原始应答；null = 服务无响应。</param>
    /// <param name="ServiceStatus">OK 后面的状态尾缀（TAKEN / CONTENDED / RESTORED …）；服务失败时 null。</param>
    /// <param name="Git">git 那半边的结果；没做（服务失败或缺原生 OpenSSH）时 null。</param>
    public sealed record Result(string? ServiceReply, string? ServiceStatus, GitSshResult? Git, bool NativeSshMissing)
    {
        public bool Ok => ServiceStatus is not null;
    }

    private sealed class PrevStore : IGitSshPrevStore
    {
        public GitSshPrev? Prev
        {
            get => ClientSettings.GitSshPrev;
            set => ClientSettings.GitSshPrev = value;
        }
    }

    public static async Task<Result> SetAsync(bool on)
    {
        string? r = await AppServices.Pipe.SshAgentSetAsync(on);
        if (r is null || !r.StartsWith(IpcProtocol.Ok, StringComparison.Ordinal))
            return new Result(r, null, null, false);

        string status = r.Length > IpcProtocol.Ok.Length + 1 ? r[(IpcProtocol.Ok.Length + 1)..] : "";

        string openSsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH");
        string ssh = Path.Combine(openSsh, "ssh.exe");
        string keygen = Path.Combine(openSsh, "ssh-keygen.exe");
        var cfg = new GitSshConfig(new GitCli(), new PrevStore(), ssh.Replace('\\', '/'), keygen.Replace('\\', '/'));

        // 开启时没有原生 OpenSSH 就别改 git（改了也只会指向不存在的程序）；关闭时照常还原。
        if (on && !File.Exists(ssh))
            return new Result(r, status, null, true);

        GitSshResult git = await Task.Run(() => on ? cfg.Apply() : cfg.Restore());
        return new Result(r, status, git, false);
    }

    /// <summary>把结果拼成给人看的一句话（服务半边 + git 半边）。</summary>
    public static string Describe(Result res, bool on)
    {
        var loc = Loc.Instance;
        if (!res.Ok) return loc.T("common.op_fail", res.ServiceReply ?? "");

        string svc = loc.T(res.ServiceStatus switch
        {
            "TAKEN" => "msg.ssh.svc.taken",
            "ABSENT" => "msg.ssh.svc.absent",
            "TAKEOVER_FAILED" => "msg.ssh.svc.takeover_failed",
            "CONTENDED" => "msg.ssh.svc.contended",
            "RESTORED" => "msg.ssh.svc.restored",
            "NONE" => "msg.ssh.svc.none",
            "RESTORE_FAILED" => "msg.ssh.svc.restore_failed",
            _ => on ? "msg.setup.ssh_on" : "msg.setup.ssh_off",
        });

        string? git = res.NativeSshMissing ? loc.T("msg.ssh.git.no_native") : res.Git switch
        {
            GitSshResult.Configured => loc.T("msg.ssh.git.configured"),
            GitSshResult.Restored => loc.T("msg.ssh.git.restored"),
            GitSshResult.GitMissing => loc.T("msg.ssh.git.missing"),
            GitSshResult.Failed => loc.T("msg.ssh.git.failed"),
            _ => null,
        };

        return git is null ? svc : $"{svc} {git}";
    }
}
