using System.Text.Json;
using ImmurokService.Ssh;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Platform;

/// <summary>服务设置持久化（%ProgramData%\immurok\settings.json）。</summary>
public sealed class AppSettings : ISystemSshAgentPrevStore
{
    /// <summary>
    /// 落盘结构。功能开关一律用 <c>bool?</c>：老版本的 settings.json 里没有这些字段，
    /// 反序列化成 false 会把原本一直可用的解锁/OTP/agent 直接关掉。null 表示「文件里没写」，
    /// 由各属性回落到自己的默认值。
    /// </summary>
    private sealed class Data
    {
        public bool SshAgentEnabled { get; set; }
        public bool? UnlockEnabled { get; set; }
        public bool? LockEnabled { get; set; }
        public bool? ImkAgentEnabled { get; set; }
        public bool? OtpEnabled { get; set; }
        public bool? WslEnabled { get; set; }
        /// <summary>开启 SSH agent 时系统 ssh-agent 服务的原状态；null = 没接管过（或已还原）。</summary>
        public SystemSshAgentPrev? SystemSshAgentPrev { get; set; }
    }

    private readonly ILogger<AppSettings> _log;
    private readonly string _path;
    private Data _data = new();

    public AppSettings(ILogger<AppSettings> log)
    {
        _log = log;
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "immurok");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Load();
    }

    /// <summary>SSH agent（默认关：需要用户显式启用，会接管系统 ssh-agent 的管道名）。</summary>
    public bool SshAgentEnabled
    {
        get => _data.SshAgentEnabled;
        set { _data.SshAgentEnabled = value; Save(); }
    }

    /// <summary>接管系统 ssh-agent 前的状态（<see cref="SystemSshAgent"/> 用）。</summary>
    public SystemSshAgentPrev? Prev
    {
        get => _data.SystemSshAgentPrev;
        set { _data.SystemSshAgentPrev = value; Save(); }
    }

    /// <summary>指纹解锁屏幕（默认开：这是产品主功能）。</summary>
    public bool UnlockEnabled
    {
        get => _data.UnlockEnabled ?? true;
        set { _data.UnlockEnabled = value; Save(); }
    }

    /// <summary>
    /// 指纹锁屏（默认关）。⚠️ 尚未实现：当前 BLE 协议里没有可用的长按/锁屏信号，
    /// 0x23 只是 1 字节的裸「指纹匹配」通知，无法区分长按。此开关仅作占位，
    /// 客户端界面上禁用并标注「暂未支持」，等固件确认信号后再接实际逻辑。
    /// </summary>
    public bool LockEnabled
    {
        get => _data.LockEnabled ?? false;
        set { _data.LockEnabled = value; Save(); }
    }

    /// <summary>imk agent 指纹授权（默认开）。关闭后 imk 的 APPROVE 一律拒绝。</summary>
    public bool ImkAgentEnabled
    {
        get => _data.ImkAgentEnabled ?? true;
        set { _data.ImkAgentEnabled = value; Save(); }
    }

    /// <summary>TOTP 取码（默认开）。关闭后客户端与 imk 都取不到验证码。</summary>
    public bool OtpEnabled
    {
        get => _data.OtpEnabled ?? true;
        set { _data.OtpEnabled = value; Save(); }
    }

    /// <summary>
    /// WSL2 支持（默认关）。打开后服务在 Hyper-V socket 上给 WSL 虚拟机监听，
    /// WSL 内的 sudo / imk / ssh 经桥走设备指纹门（见 <see cref="ImmurokService.Wsl.WslBridgeServer"/>）。
    /// </summary>
    public bool WslEnabled
    {
        get => _data.WslEnabled ?? false;
        set { _data.WslEnabled = value; Save(); }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) { _log.LogWarning(ex, "读取设置失败"); }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_data)); }
        catch (Exception ex) { _log.LogError(ex, "写入设置失败"); }
    }
}
