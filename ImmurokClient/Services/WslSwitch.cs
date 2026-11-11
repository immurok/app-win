using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImmurokCommon.Protocol;
using ImmurokCommon.Wsl;
using ImmurokClient.Localization;

namespace ImmurokClient.Services;

/// <summary>
/// 「WSL2 支持」一键开 / 关的客户端半边（设计稿 docs/superpowers/specs/2026-09-06-wsl2-support-design.md §5）。
/// 开：让服务开监听并给出 token → 在每个 WSL2 发行版里装桥、imk、PAM 模块并配置 sudo / ssh。
/// 关：先把每个发行版里的东西撤干净，再让服务停监听、删 token。
/// 安装编排在客户端而不在服务：发行版按 Windows 用户注册，wsl.exe 只能在用户会话里跑。
/// </summary>
public static class WslSwitch
{
    public enum Failure { None, NoWsl, NoDistro, NoPayload, Service }

    /// <param name="ServiceReply">服务原始应答（失败时给人看）；与服务无关的失败为 null。</param>
    public sealed record Result(Failure Failure, string? ServiceReply, IReadOnlyList<WslInstallResult> Distros)
    {
        /// <summary>开关最终应该停在「开」还是回拨：服务那半边成了就算开，单个发行版失败不回拨（可重试）。</summary>
        public bool Ok => Failure == Failure.None;
    }

    public static async Task<bool> IsOnAsync()
        => (await AppServices.Pipe.WslStatusAsync())?.Contains("ON") == true;

    public static async Task<Result> SetAsync(bool on)
    {
        var none = Array.Empty<WslInstallResult>();
        if (WslDistroList.WslExePath() is null) return new Result(Failure.NoWsl, null, none);
        List<WslDistro> distros = await WslDistroList.ListInstallableAsync();

        if (!on)
        {
            // 先撤 WSL 里的东西（此时桥还连得上服务，无所谓）；发行版已注销的自然不在列表里。
            var removed = new List<WslInstallResult>();
            foreach (WslDistro d in distros)
                removed.Add(await WslInstaller.UninstallAsync(d.Name));
            string? off = await AppServices.Pipe.WslSetAsync(false);
            return off == IpcProtocol.Ok
                ? new Result(Failure.None, off, removed)
                : new Result(Failure.Service, off, removed);
        }

        if (distros.Count == 0) return new Result(Failure.NoDistro, null, none);
        string payload = WslInstaller.DefaultPayloadPath;
        if (!File.Exists(payload)) return new Result(Failure.NoPayload, null, none);

        // WSL:ON -> OK:<token>
        string? r = await AppServices.Pipe.WslSetAsync(true);
        string prefix = IpcProtocol.Ok + IpcProtocol.Sep;
        if (r is null || !r.StartsWith(prefix, StringComparison.Ordinal) || r.Length != prefix.Length + 64)
            return new Result(Failure.Service, r, none);
        string token = r[prefix.Length..];

        // WSL 里的 ssh 是否交给设备：跟 Windows 侧 SSH agent 开关无关（那个管的是系统管道名的接管），
        // 打开 WSL 支持就一并配置——在 WSL 里用 ssh / git 是这个功能的主要用途。
        var results = new List<WslInstallResult>();
        foreach (WslDistro d in distros)
            results.Add(await WslInstaller.InstallAsync(d.Name, payload, token, sshAgent: true));
        return new Result(Failure.None, r, results);
    }

    /// <summary>把结果拼成给人看的一句话。</summary>
    public static string Describe(Result res, bool on)
    {
        var loc = Loc.Instance;
        switch (res.Failure)
        {
            case Failure.NoWsl: return loc.T("msg.wsl.no_wsl");
            case Failure.NoDistro: return loc.T("msg.wsl.no_distro");
            case Failure.NoPayload: return loc.T("msg.wsl.no_payload");
            case Failure.Service: return loc.T("common.op_fail", res.ServiceReply ?? "");
        }
        if (!on) return loc.T("msg.wsl.off");
        return string.Join("  ", res.Distros.Select(d => d.Ok
            ? loc.T("msg.wsl.distro_ok", d.Distro)
            : loc.T("msg.wsl.distro_fail", d.Distro, d.LastLine)));
    }
}
