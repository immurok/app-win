namespace ImmurokService.Wsl;

/// <summary>
/// 每条 WSL 连接开头的前导行：
/// <c>IMK-WSL/1 token=&lt;64hex&gt; uid=&lt;peer uid&gt; distro=&lt;WSL_DISTRO_NAME&gt; [pid= pids= comm= age=]</c>。
/// <c>hints=1</c> 表示桥认识指纹门进度提示帧（<see cref="WslHint"/>），服务才会在 ssh-agent 连接上发它。
/// 只有 token 参与授权；其余字段只用于日志和（将来的）签名来源分类。字段顺序无关，不认识的键忽略。
/// </summary>
internal sealed record WslPreamble(string Token, string Uid, string Distro, string Pid, string Comm, bool Hints)
{
    public const string Magic = "IMK-WSL/1";
    public const int MaxLength = 2048;

    public static WslPreamble? Parse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != Magic) return null;
        string token = "", uid = "", distro = "", pid = "", comm = "";
        bool hints = false;
        foreach (string p in parts.AsSpan(1))
        {
            int eq = p.IndexOf('=');
            if (eq <= 0) continue;
            string v = p[(eq + 1)..];
            switch (p[..eq])
            {
                case "token": token = v; break;
                case "uid": uid = v; break;
                case "distro": distro = Sanitize(v); break;
                case "pid": pid = v; break;
                case "comm": comm = Sanitize(v); break;
                case "hints": hints = v == "1"; break;
            }
        }
        return token.Length == 0 ? null : new WslPreamble(token, uid, distro, pid, comm, hints);
    }

    /// <summary>进日志前收紧：只留可打印的窄字符集，限长。</summary>
    private static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder(Math.Min(s.Length, 64));
        foreach (char c in s)
        {
            if (sb.Length >= 64) break;
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '?');
        }
        return sb.ToString();
    }
}
