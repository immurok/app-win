using System.Diagnostics;
using System.Text;

namespace ImmurokCommon.Wsl;

/// <summary>一个已注册的 WSL 发行版。</summary>
public sealed record WslDistro(string Name, int Version, bool IsDefault);

/// <summary>
/// 枚举当前用户的 WSL 发行版（<c>wsl.exe -l -v</c>）。发行版按 Windows 用户注册在 HKCU，
/// 所以必须在用户会话里跑，Session 0 的服务调不动——这就是安装编排放在客户端的原因。
/// </summary>
public static class WslDistroList
{
    /// <summary>System32 下的 wsl.exe；不存在返回 null（没装 WSL）。</summary>
    public static string? WslExePath()
    {
        string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>可以装桥的发行版：WSL2，且不是 Docker Desktop 的内部发行版。</summary>
    public static async Task<List<WslDistro>> ListInstallableAsync(CancellationToken ct = default)
    {
        string? exe = WslExePath();
        if (exe is null) return new();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // wsl.exe 自己的输出（不是 Linux 命令的输出）是 UTF-16LE。
            StandardOutputEncoding = Encoding.Unicode,
        };
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("-v");
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new();
            Task<string> stdout = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return Parse(await stdout.ConfigureAwait(false)).Where(IsInstallable).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new();
        }
    }

    public static bool IsInstallable(WslDistro d)
        => d.Version == 2 && !d.Name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析 <c>wsl -l -v</c> 的输出。表头会本地化，不认表头文字：每行去掉默认标记 <c>*</c> 后按空白切开，
    /// 最后一列是纯数字（VERSION）才算数据行，第一列是名字（发行版名不含空白）。
    /// </summary>
    public static List<WslDistro> Parse(string output)
    {
        var list = new List<WslDistro>();
        foreach (string raw in output.Split('\n'))
        {
            // 旧版 wsl.exe 的输出里夹着 NUL 和 BOM，一并去掉。
            string line = raw.Replace("\0", "").Replace("﻿", "").Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;
            bool isDefault = line[0] == '*';
            if (isDefault) line = line[1..];
            string[] cols = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 3) continue;
            if (!int.TryParse(cols[^1], out int version)) continue;
            list.Add(new WslDistro(cols[0], version, isDefault));
        }
        return list;
    }
}
