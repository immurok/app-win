using System;
using System.IO;
using System.Text.Json;
using ImmurokCommon.Git;

namespace ImmurokClient.Services;

/// <summary>
/// 客户端本地设置（仅界面偏好，不含机密），存于 %AppData%\immurok\client.json。
/// 语言、自启初始化标记、横幅折叠、设置向导完成标记、SSH agent 接管前的 git 配置原值、应用升级检查的时间与已提示版本。
/// </summary>
public static class ClientSettings
{
    private sealed class Data
    {
        public string? Language { get; set; }
        public bool AutoStartInitialized { get; set; }
        public bool SecurityBannerCollapsed { get; set; }
        public bool SetupCompleted { get; set; }
        /// <summary>开启 SSH agent 时 git 两个键的原值；null = 没接管过（或已还原）。</summary>
        public GitSshPrev? GitSshPrev { get; set; }
        /// <summary>上次**成功**查询应用新版本的时间（UTC）；null = 从没查成过。</summary>
        public DateTime? LastAppUpdateCheckUtc { get; set; }
        /// <summary>已经用托盘通知提示过的应用新版本号，同一版本不重复提示。</summary>
        public string? AppUpdateNotifiedVersion { get; set; }
    }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "immurok");
    private static string FilePath => Path.Combine(Dir, "client.json");

    private static Data _data = Load();

    private static Data Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
        }
        catch { /* 忽略损坏文件 */ }
        return new Data();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 忽略写入失败 */ }
    }

    /// <summary>已保存的语言码；未设置返回 null（调用方回退默认英语）。</summary>
    public static string? Language
    {
        get => _data.Language;
        set { _data.Language = value; Save(); }
    }

    /// <summary>IPC 加固横幅（全绿时）是否已被用户收起为盾牌。有异常时横幅不可收起，此项不生效。</summary>
    public static bool SecurityBannerCollapsed
    {
        get => _data.SecurityBannerCollapsed;
        set { _data.SecurityBannerCollapsed = value; Save(); }
    }

    /// <summary>
    /// 用户是否点过设置向导的「完成」或「稍后再说」。置位后启动不再自动弹向导，
    /// 除非服务或锁屏组件缺失（<see cref="App"/> 启动时判断）。
    /// </summary>
    public static bool SetupCompleted
    {
        get => _data.SetupCompleted;
        set { _data.SetupCompleted = value; Save(); }
    }

    /// <summary>是否已做过「首次运行默认开启自启」。置位后不再自动改写用户的选择。</summary>
    public static bool AutoStartInitialized
    {
        get => _data.AutoStartInitialized;
        set { _data.AutoStartInitialized = value; Save(); }
    }

    /// <summary>上次成功查询应用新版本的时间（UTC），<see cref="AppUpdate"/> 据此保证每天只自动查一次。</summary>
    public static DateTime? LastAppUpdateCheckUtc
    {
        get => _data.LastAppUpdateCheckUtc;
        set { _data.LastAppUpdateCheckUtc = value; Save(); }
    }

    /// <summary>已经用托盘通知提示过的应用新版本号（同一版本只提示一次）。</summary>
    public static string? AppUpdateNotifiedVersion
    {
        get => _data.AppUpdateNotifiedVersion;
        set { _data.AppUpdateNotifiedVersion = value; Save(); }
    }

    /// <summary>SSH agent 接管前 git 的 core.sshCommand / gpg.ssh.program 原值（<see cref="SshAgentSwitch"/> 用）。</summary>
    public static GitSshPrev? GitSshPrev
    {
        get => _data.GitSshPrev;
        set { _data.GitSshPrev = value; Save(); }
    }
}
