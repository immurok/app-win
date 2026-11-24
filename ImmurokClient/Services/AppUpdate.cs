using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ImmurokCommon.Security;

namespace ImmurokClient.Services;

/// <summary>
/// GitHub 上比本机新的一个发布版本。<paramref name="Installer"/> 是本机架构的安装包；
/// Release 里没有（或名字对不上）时为 null，只能去 Release 页面手动下载。
/// </summary>
public sealed record AppRelease(string Version, string Url, AppInstaller? Installer);

/// <param name="Sha256">GitHub 资产的 digest（小写 hex）；老 Release 没有这个字段时为 null。</param>
public sealed record AppInstaller(string Name, string Url, long Size, string? Sha256);

public enum AppUpdateState { UpToDate, Available, Failed }

public sealed record AppUpdateResult(AppUpdateState State, AppRelease? Release);

public enum AppInstallStage { Downloading, Verifying, Launching }

/// <summary>自升级失败的原因；<see cref="Cancelled"/> 是下载 / 校验途中被取消。</summary>
public enum AppInstallError { None, NoInstaller, Download, Verify, NotSigned, Cancelled, Launch }

/// <summary>
/// 应用自身的升级：查 GitHub 上 immurok/app-win 的最新 Release，与本机版本比较；
/// 有新版时可以在应用内下载本机架构的安装包、校验后静默安装，装完由安装包重新拉起客户端
/// （2026-11-24 用户要求；之前是「只提示，不下载、不安装」）。
/// 自动检查每天一次（<see cref="StartDailyCheck"/>）仍然只提示，下载安装只在用户点了「立即更新」后进行。
///
/// 安装包要以管理员身份运行，所以校验是这里的重点：下载内容与 GitHub 给的大小 / SHA-256 一致，
/// Authenticode 有效，且签名主体与正在运行的客户端相同（证书几天一换，不能比指纹）。
/// 客户端自身未签名（开发构建）时不做自升级，退回打开 Release 页面。
/// </summary>
public static class AppUpdate
{
    /// <summary>匿名接口：自动跳过草稿与预发布；每 IP 每小时 60 次，按天查绰绰有余。</summary>
    private const string LatestReleaseApi = "https://api.github.com/repos/immurok/app-win/releases/latest";

    /// <summary>只会打开这个前缀下的链接——接口返回的 html_url 不符就退回它本身，不拿来路不明的地址去开浏览器。</summary>
    private const string ReleasesPage = "https://github.com/immurok/app-win/releases";

    /// <summary>安装包只从这个前缀下载（GitHub 再 302 到自家 CDN）。</summary>
    private const string DownloadPrefix = ReleasesPage + "/download/";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = CreateHttp(TimeSpan.FromSeconds(15));
    // 安装包几十 MB，慢网下 15 秒不够；超时交给调用方的取消令牌。
    private static readonly HttpClient Download = CreateHttp(Timeout.InfiniteTimeSpan);
    private static DispatcherTimer? _timer;
    private static bool _checking;

    /// <summary>最近一次检查发现的新版本；没有新版（或还没查过）为 null。</summary>
    public static AppRelease? Available { get; private set; }

    /// <summary>本机应用版本（"0.7.0"）。</summary>
    public static string CurrentVersion => Views.AboutPage.AppVersion;

    /// <summary>与 packaging\build-installer.ps1 的 -Arch 一致：安装包按架构分发，x64 包装不上 ARM64。</summary>
    private static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    private static HttpClient CreateHttp(TimeSpan timeout)
    {
        var h = new HttpClient { Timeout = timeout };
        // GitHub 要求带 User-Agent；只报产品名和版本，不带任何用户 / 机器信息。
        h.DefaultRequestHeaders.UserAgent.ParseAdd("immurok-win/" + (CurrentVersion.Length > 0 ? CurrentVersion : "0"));
        return h;
    }

    /// <summary>
    /// 查一次最新 Release。网络不通 / 被限流 / 返回内容认不出来都算 <see cref="AppUpdateState.Failed"/>，不抛异常。
    /// </summary>
    public static async Task<AppUpdateResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
            Available = new AppRelease(latestText, url, FindInstaller(root, latestText));
            return new AppUpdateResult(AppUpdateState.Available, Available);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException)
        {
            return new AppUpdateResult(AppUpdateState.Failed, null);
        }
    }

    /// <summary>按 build-installer.ps1 的命名（immurok-{版本}-{架构}-setup.exe）找本机架构的安装包。</summary>
    private static AppInstaller? FindInstaller(JsonElement root, string version)
    {
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        string want = $"immurok-{version}-{Arch}-setup.exe";
        foreach (JsonElement a in assets.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
            if (!string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) continue;

            string dl = a.TryGetProperty("browser_download_url", out JsonElement d) ? d.GetString() ?? "" : "";
            if (!dl.StartsWith(DownloadPrefix, StringComparison.Ordinal)) return null;
            long size = a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long v) ? v : 0;
            string? sha = a.TryGetProperty("digest", out JsonElement g) && g.GetString() is string digest
                          && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..].ToLowerInvariant()
                : null;
            return new AppInstaller(name, dl, size, sha);
        }
        return null;
    }

    /// <summary>客户端自身有有效签名时才能自升级（拿它的签名主体去比对下载的安装包）。</summary>
    public static bool CanSelfUpdate => SelfSubject is not null;

    private static readonly Lazy<string?> SelfSubjectLazy = new(() =>
    {
        string? self = Environment.ProcessPath;
        return self is not null && Authenticode.IsValid(self) ? Authenticode.Subject(self) : null;
    });

    private static string? SelfSubject => SelfSubjectLazy.Value;

    /// <summary>
    /// 下载本机架构的安装包 → 校验 → 以管理员身份静默安装。成功返回 <see cref="AppInstallError.None"/>，
    /// 此时安装包已经在跑：它会结束本进程、装完再以当前用户身份重新拉起客户端。
    /// 进度回调在线程池上触发。
    /// </summary>
    public static async Task<AppInstallError> DownloadAndInstallAsync(
        AppRelease release, Action<AppInstallStage> onStage, Action<int> onProgress, CancellationToken ct)
    {
        AppInstaller? inst = release.Installer;
        if (inst is null) return AppInstallError.NoInstaller;
        string? subject = SelfSubject;
        if (subject is null) return AppInstallError.NotSigned;

        string dir = Path.Combine(Path.GetTempPath(), "immurok-update");
        string path = Path.Combine(dir, inst.Name);
        try
        {
            // 上次留下的安装包（装完没人删）一并清掉
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AppInstallError.Download;
        }

        onStage(AppInstallStage.Downloading);
        try
        {
            using HttpResponseMessage resp = await Download.GetAsync(inst.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? inst.Size;
            await using Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buf = new byte[81920];
            long done = 0;
            int lastPct = -1, n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                int pct = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
                if (pct != lastPct) { lastPct = pct; onProgress(pct); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AppInstallError.Cancelled;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return AppInstallError.Download;
        }

        // 校验到启动期间一直持有只读、禁写禁删的句柄：同用户进程没法在「验过」和「提权运行」之间换掉文件。
        // 句柄随本进程退出关闭，那时安装包的映像早已映射，同样改不了。
        onStage(AppInstallStage.Verifying);
        FileStream hold;
        try { hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return AppInstallError.Verify; }

        bool ok;
        try
        {
            ok = (inst.Size <= 0 || hold.Length == inst.Size)
                 && (inst.Sha256 is null
                     || Convert.ToHexStringLower(await SHA256.HashDataAsync(hold, ct).ConfigureAwait(false)) == inst.Sha256)
                 && Authenticode.IsValid(path)
                 && string.Equals(Authenticode.Subject(path), subject, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            hold.Dispose();
            return AppInstallError.Cancelled;
        }
        if (!ok)
        {
            hold.Dispose();
            return AppInstallError.Verify;
        }

        onStage(AppInstallStage.Launching);
        try
        {
            // /SILENT：只显示进度条，不走向导；/RELAUNCH=1：装完以当前用户身份重新拉起客户端（immurok.iss）。
            // 不加 /SUPPRESSMSGBOXES：缺 .NET 又下载失败之类的错误要让用户看到。
            // 本进程不自己退出：安装包确认能装（运行时就绪）后才会结束服务和客户端，中途失败客户端照常在跑。
            // 不用 Verb=runas：Inno 安装包的 manifest 是 asInvoker，自己再提权（UAC 在那时弹），这样它记得
            // 「原始用户」，[Run] 里 runasoriginaluser 才能以普通用户身份重新拉起客户端；直接以管理员身份
            // 启动它，拉起的客户端也会是管理员进程。代价是 UAC 上点「否」这边无从得知，客户端照常在跑。
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /NORESTART /RELAUNCH=1",
            });
            _launchedInstaller = hold;   // 不释放：留到进程退出
            return AppInstallError.None;
        }
        catch (Exception)
        {
            hold.Dispose();
            return AppInstallError.Launch;
        }
    }

    private static FileStream? _launchedInstaller;

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
