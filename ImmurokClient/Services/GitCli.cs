using System;
using System.Diagnostics;
using System.IO;
using ImmurokCommon.Git;

namespace ImmurokClient.Services;

/// <summary>
/// <see cref="IGitConfig"/> 的进程实现：以当前用户身份跑 <c>git config --global</c>。
/// 必须在客户端做，服务是 SYSTEM，写不到用户的 .gitconfig。
/// </summary>
public sealed class GitCli : IGitConfig
{
    private readonly string? _git = Locate();

    public bool Available => _git is not null;

    public string? Get(string key)
    {
        var (code, output) = Run("config", "--global", "--get", key);
        // 1 = 键不存在；其余非零当异常。
        if (code == 1) return null;
        if (code != 0) throw new InvalidOperationException($"git config --get {key} exit {code}");
        return output.Trim();
    }

    public void Set(string key, string value)
    {
        var (code, _) = Run("config", "--global", key, value);
        if (code != 0) throw new InvalidOperationException($"git config {key} exit {code}");
    }

    public void Unset(string key)
    {
        var (code, _) = Run("config", "--global", "--unset", key);
        // 5 = 本来就没有，视同成功。
        if (code != 0 && code != 5) throw new InvalidOperationException($"git config --unset {key} exit {code}");
    }

    private (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(_git!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git 启动失败");
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        return (p.ExitCode, output);
    }

    /// <summary>PATH 里找 git.exe，找不到再试 Git for Windows 默认安装位置。</summary>
    private static string? Locate()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim(), "git.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* 非法路径段，跳过 */ }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var sub in new[] { @"Git\cmd\git.exe", @"Programs\Git\cmd\git.exe" })
            {
                string p = Path.Combine(root, sub);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }
}
