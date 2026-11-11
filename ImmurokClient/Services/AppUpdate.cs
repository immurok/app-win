using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ImmurokClient.Services;

/// <summary>GitHub 上比本机新的一个发布版本。</summary>
public sealed record AppRelease(string Version, string Url);

public enum AppUpdateState { UpToDate, Available, Failed }

public sealed record AppUpdateResult(AppUpdateState State, AppRelease? Release);

/// <summary>
/// 应用自身的升级检查：查 GitHub 上 immurok/app-win 的最新 Release，与本机版本比较。
/// **只提示，不下载、不安装**（2026-11-11 用户决定）——有新版时给出 Release 页面链接，由用户自己下载安装包。
/// 自动检查每天一次（<see cref="StartDailyCheck"/>），关于页可以手动再查。
/// </summary>
public static class AppUpdate
{
    /// <summary>匿名接口：自动跳过草稿与预发布；每 IP 每小时 60 次，按天查绰绰有余。</summary>
    private const string LatestReleaseApi = "https://api.github.com/repos/immurok/app-win/releases/latest";

    /// <summary>只会打开这个前缀下的链接——接口返回的 html_url 不符就退回它本身，不拿来路不明的地址去开浏览器。</summary>
    private const string ReleasesPage = "https://github.com/immurok/app-win/releases";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = CreateHttp();
    private static DispatcherTimer? _timer;
    private static bool _checking;

    /// <summary>最近一次检查发现的新版本；没有新版（或还没查过）为 null。</summary>
    public static AppRelease? Available { get; private set; }

    /// <summary>本机应用版本（"0.7.0"）。</summary>
    public static string CurrentVersion => Views.AboutPage.AppVersion;

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub API 要求带 User-Agent；只报产品名和版本，不带任何用户 / 机器信息。
        h.DefaultRequestHeaders.UserAgent.ParseAdd("immurok-win/" + (CurrentVersion.Length > 0 ? CurrentVersion : "0"));
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>
    /// 查一次最新 Release。网络不通 / 被限流 / 返回内容认不出来都算 <see cref="AppUpdateState.Failed"/>，不抛异常。
    /// </summary>
    public static async Task<AppUpdateResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await Http.GetStringAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
            string latestText = tag.TrimStart('v', 'V');
            FwVersion? latest = FwVersion.Parse(latestText);
            FwVersion? current = FwVersion.Parse(FwVersion.Normalize(CurrentVersion));
            if (latest is null || current is null) return new AppUpdateResult(AppUpdateState.Failed, null);

            ClientSettings.LastAppUpdateCheckUtc = DateTime.UtcNow;

            if (latest.Value <= current.Value)
            {
                Available = null;
                return new AppUpdateResult(AppUpdateState.UpToDate, null);
            }

            string url = root.TryGetProperty("html_url", out JsonElement u) ? u.GetString() ?? "" : "";
            if (!url.StartsWith(ReleasesPage + "/", StringComparison.Ordinal)) url = ReleasesPage;
            Available = new AppRelease(latestText, url);
            return new AppUpdateResult(AppUpdateState.Available, Available);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException)
        {
            return new AppUpdateResult(AppUpdateState.Failed, null);
        }
    }

    /// <summary>在默认浏览器里打开该版本的 Release 页面。</summary>
    public static void OpenReleasePage(AppRelease? release)
    {
        string url = release?.Url ?? ReleasesPage;
        if (!url.StartsWith(ReleasesPage, StringComparison.Ordinal)) url = ReleasesPage;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 没有默认浏览器之类：无事可做 */ }
    }

    /// <summary>
    /// 启动每天一次的自动检查（UI 线程调用）。客户端常驻托盘、可能连开很多天，所以不是只在启动时查：
    /// 启动 1 分钟后看一眼，之后每小时看一眼「距上次成功检查是否已满 24 小时」，满了才真去请求。
    /// 检查失败不记时间，下一个整点再试。
    /// <paramref name="onNewVersion"/> 在发现**还没提示过**的新版本时回调一次（同一版本不会天天提示）。
    /// </summary>
    public static void StartDailyCheck(Action<AppRelease> onNewVersion)
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(1);
            if (_checking) return;
            DateTime? last = ClientSettings.LastAppUpdateCheckUtc;
            if (last is not null && DateTime.UtcNow - last.Value < CheckInterval) return;

            _checking = true;
            try
            {
                AppUpdateResult r = await CheckAsync();
                if (r.State == AppUpdateState.Available && r.Release is not null
                    && ClientSettings.AppUpdateNotifiedVersion != r.Release.Version)
                {
                    ClientSettings.AppUpdateNotifiedVersion = r.Release.Version;
                    onNewVersion(r.Release);
                }
            }
            finally { _checking = false; }
        };
        _timer.Start();
    }
}
