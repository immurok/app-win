using System.Diagnostics;
using System.Text;

namespace ImmurokCommon.Wsl;

/// <summary>在一个发行版里安装 / 卸载的结果。</summary>
/// <param name="ExitCode">脚本退出码；-1 = wsl.exe 起不来或超时。</param>
/// <param name="Output">脚本的 stdout + stderr（给人看的失败原因取最后一行）。</param>
public sealed record WslInstallResult(string Distro, bool Ok, int ExitCode, string Output)
{
    /// <summary>失败时给用户看的一句话：输出的最后一个非空行。</summary>
    public string LastLine
    {
        get
        {
            string[] lines = Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length == 0 ? $"exit {ExitCode}" : lines[^1];
        }
    }
}

/// <summary>
/// 把 WSL 支持装进 / 移出一个发行版：以 root 在发行版里解开 payload tar 并跑
/// <c>wsl-install.sh</c> / <c>wsl-uninstall.sh</c>（脚本在 app-win/wsl/，全部幂等）。
/// payload 从 stdin 灌入，不经 <c>/mnt/c</c>：用户关了 automount 也能装。
/// token 走环境变量（WSLENV 透传），不出现在 Windows 侧的命令行里。
/// </summary>
public static class WslInstaller
{
    public const string PayloadFileName = "immurok-wsl-payload.tar.gz";
    private const string LibDir = "/usr/local/lib/immurok";
    private const string TokenEnv = "IMK_WSL_TOKEN";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>安装目录里的 payload：<c>&lt;exe 目录&gt;\wsl\immurok-wsl-payload.tar.gz</c>。</summary>
    public static string DefaultPayloadPath => Path.Combine(AppContext.BaseDirectory, "wsl", PayloadFileName);

    /// <summary>安装脚本的 shell 命令行（单独拆出来给单元测试）。发行版名经 $1 传入，不拼进脚本文本。</summary>
    public static string InstallScript(bool sshAgent) =>
        $"tar xzf - -C / && exec {LibDir}/wsl-install.sh --distro \"$1\" --ssh-agent {(sshAgent ? "on" : "off")}";

    public const string UninstallScript =
        $"if [ -x {LibDir}/wsl-uninstall.sh ]; then exec {LibDir}/wsl-uninstall.sh; fi";

    public static async Task<WslInstallResult> InstallAsync(
        string distro, string payloadPath, string token, bool sshAgent, CancellationToken ct = default)
    {
        byte[] payload;
        try { payload = await File.ReadAllBytesAsync(payloadPath, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WslInstallResult(distro, false, -1, ex.Message);
        }
        return await RunAsRootAsync(distro, InstallScript(sshAgent), payload, token, ct).ConfigureAwait(false);
    }

    public static Task<WslInstallResult> UninstallAsync(string distro, CancellationToken ct = default)
        => RunAsRootAsync(distro, UninstallScript, null, null, ct);

    private static async Task<WslInstallResult> RunAsRootAsync(
        string distro, string script, byte[]? stdin, string? token, CancellationToken ct)
    {
        string? exe = WslDistroList.WslExePath();
        if (exe is null) return new WslInstallResult(distro, false, -1, "wsl.exe not found");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Linux 命令的输出是 UTF-8（wsl.exe 自己的报错是 UTF-16，极少见，出现时只是显示成乱码）。
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in new[] { "-d", distro, "-u", "root", "--exec", "/bin/sh", "-c", script, "immurok-wsl", distro })
            psi.ArgumentList.Add(a);
        if (token is not null)
        {
            psi.Environment[TokenEnv] = token;
            string? prev = Environment.GetEnvironmentVariable("WSLENV");
            psi.Environment["WSLENV"] = string.IsNullOrEmpty(prev) ? TokenEnv : $"{prev}:{TokenEnv}";
        }

        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null) return new WslInstallResult(distro, false, -1, "failed to start wsl.exe");
            Task<string> stdout = p.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = p.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                if (stdin is not null)
                    await p.StandardInput.BaseStream.WriteAsync(stdin, timeout.Token).ConfigureAwait(false);
            }
            catch (IOException) { /* 脚本提前退出（例如 tar 报错）：原因在输出里 */ }
            p.StandardInput.Close();

            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            return new WslInstallResult(distro, p.ExitCode == 0, p.ExitCode, output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p?.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
            return new WslInstallResult(distro, false, -1, "timed out");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new WslInstallResult(distro, false, -1, ex.Message);
        }
        finally { p?.Dispose(); }
    }
}
