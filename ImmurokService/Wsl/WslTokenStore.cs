using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Wsl;

/// <summary>
/// WSL 桥的共享 token（32 字节随机数的 64 位十六进制）。Windows 侧存
/// <c>%ProgramData%\immurok\wsl-token</c>（数据目录 ACL 只有 SYSTEM / Administrators 能进），
/// WSL 侧由客户端安装时写进 <c>/etc/immurok/token</c>（root 0600）。每条 Hyper-V socket 连接的
/// 前导行都要带它：能连上监听的只有 WSL 虚拟机，但虚拟机里任何进程都能连 vsock，
/// token 把「不是 root 起的桥」挡在外面。
/// </summary>
public sealed class WslTokenStore
{
    private readonly ILogger<WslTokenStore> _log;
    private readonly string _path;
    private readonly object _gate = new();
    private string? _token;
    private bool _loaded;

    public WslTokenStore(ILogger<WslTokenStore> log)
    {
        _log = log;
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "immurok", "wsl-token");
    }

    /// <summary>当前 token；没有则为 null。</summary>
    public string? Current
    {
        get
        {
            lock (_gate)
            {
                if (_loaded) return _token;
                _loaded = true;
                try
                {
                    if (File.Exists(_path))
                    {
                        string t = File.ReadAllText(_path).Trim();
                        _token = IsValid(t) ? t : null;
                    }
                }
                catch (Exception ex) { _log.LogWarning(ex, "读取 WSL token 失败"); }
                return _token;
            }
        }
    }

    /// <summary>取 token，没有就生成并落盘。重复打开开关不换 token，已装好的发行版不用重装。</summary>
    public string Ensure()
    {
        lock (_gate)
        {
            if (Current is { } existing) return existing;
            string t = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, t);
            _token = t;
            _log.LogInformation("已生成 WSL token");
            return t;
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            _token = null;
            _loaded = true;
            try { File.Delete(_path); }
            catch (Exception ex) { _log.LogWarning(ex, "删除 WSL token 失败"); }
        }
    }

    /// <summary>定长比较，不因内容不同而提前返回。</summary>
    public bool Matches(string candidate)
    {
        string? t = Current;
        if (t is null || !IsValid(candidate)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(t), Encoding.ASCII.GetBytes(candidate));
    }

    private static bool IsValid(string t)
    {
        if (t.Length != 64) return false;
        foreach (char c in t)
            if (!Uri.IsHexDigit(c) || char.IsUpper(c)) return false;
        return true;
    }
}
