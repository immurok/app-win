using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ImmurokClient.Services;

/// <summary>注入项是靠哪种身份命中的。强度依次递减。</summary>
public enum AppMatchKind
{
    None = 0,
    /// <summary>MSIX/Store 包族名。身份由系统背书，跨版本稳定。</summary>
    Package,
    /// <summary>Authenticode 签名主体 CN + 稳定文件名。跨升级、跨安装路径稳定。</summary>
    Signature,
    /// <summary>exe 完整路径。最弱：升级换目录就失配，且路径本身不构成身份主张。</summary>
    Path,
}

/// <summary>
/// 目标应用的身份快照。
///
/// 为什么不只用 exe 路径：路径是从 HWND 能拿到的终点，但它既不稳定也不是身份。
/// Electron/Squirrel 系把版本号写进目录（<c>…\Discord\app-1.0.9044\Discord.exe</c>），
/// MSIX 应用装在 <c>…\WindowsApps\&lt;PFN&gt;_&lt;版本&gt;_&lt;架构&gt;__&lt;pubid&gt;\</c> 下，
/// 两者每次升级都会换路径、注入静默失配；而 per-user 安装目录当前用户可写，
/// 纯路径匹配对未签名应用等于没有防线。所以这里把能拿到的身份一次全取出来，
/// 由 <see cref="Match"/> 按强度择优。
/// </summary>
public sealed record AppIdentity(
    string ExePath,
    string ExeName,
    string OriginalFilename,   // PE VERSIONINFO 里烧死的原始文件名，磁盘上改名也不变
    string PackageFamilyName,  // MSIX/Store 包族名，不含版本
    string Aumid,              // Application User Model ID（打包应用必有；桌面应用只有主动设过的才有）
    string Publisher,          // 签名证书主体 CN（未签名为空）
    string Thumbprint)         // 签名证书指纹（未签名为空）
{
    public static readonly AppIdentity Empty = new("", "", "", "", "", "", "");

    public bool IsPackaged => PackageFamilyName.Length > 0;
    public bool IsSigned => Publisher.Length > 0;

    /// <summary>签名匹配用的稳定文件名：优先 PE 里的 OriginalFilename，退回磁盘文件名。</summary>
    public string StableFileName => OriginalFilename.Length > 0 ? OriginalFilename : ExeName;

    // ---- 采集 ----

    public static AppIdentity FromProcess(int pid)
    {
        if (pid <= 0) return Empty;

        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return Empty;

        string exe, pfn, aumid;
        try
        {
            exe = QueryPath(h);
            pfn = QueryAppModelString(h, GetPackageFamilyName);
            aumid = QueryAppModelString(h, GetApplicationUserModelId);
        }
        finally { CloseHandle(h); }

        if (exe.Length == 0) return Empty;

        var (publisher, thumb) = ReadAuthenticode(exe);
        return new AppIdentity(
            exe, SafeFileName(exe), ReadOriginalFilename(exe), pfn, aumid, publisher, thumb);
    }

    /// <summary>
    /// 从窗口句柄解析出**真正的**宿主进程 pid。
    /// 传统 UWP 的窗口挂在 ApplicationFrameHost.exe 名下，直接 GetWindowThreadProcessId
    /// 拿到的是宿主而不是应用本身——那样连包族名都取不到。此时下钻到
    /// Windows.UI.Core.CoreWindow 子窗口，取它的 pid。
    /// </summary>
    public static int ResolveHostPid(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out int pid);
        if (pid <= 0) return 0;

        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return pid;
        string exe;
        try { exe = QueryPath(h); } finally { CloseHandle(h); }

        if (!SafeFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            return pid;

        int inner = 0;
        EnumChildWindows(hwnd, (child, _) =>
        {
            var cls = new StringBuilder(64);
            GetClassNameW(child, cls, cls.Capacity);
            if (cls.ToString() != "Windows.UI.Core.CoreWindow") return true; // 继续找
            GetWindowThreadProcessId(child, out int cpid);
            if (cpid > 0 && cpid != pid) { inner = cpid; return false; }
            return true;
        }, IntPtr.Zero);

        return inner > 0 ? inner : pid;
    }

    // ---- 匹配 ----

    /// <summary>
    /// 这条注入项能否以 <paramref name="kind"/> 这种方式命中当前应用。
    /// 调用方按 Package → Signature → Path 的顺序遍历，保证强身份优先。
    /// </summary>
    public static bool Matches(PasswordInjectionItem item, AppIdentity id, AppMatchKind kind) => kind switch
    {
        AppMatchKind.Package =>
            item.PackageFamilyName.Length > 0 && id.PackageFamilyName.Length > 0 &&
            string.Equals(item.PackageFamilyName, id.PackageFamilyName, StringComparison.OrdinalIgnoreCase),

        // 签名主体 CN 相同 + 文件名相同。文件名两侧都可能只有其中一种来源
        // （老项目没存 OriginalFilename），所以对 OriginalFilename / ExeName 取并集比较。
        AppMatchKind.Signature =>
            item.Publisher.Length > 0 && id.Publisher.Length > 0 &&
            string.Equals(item.Publisher, id.Publisher, StringComparison.OrdinalIgnoreCase) &&
            FileNameMatches(item, id),

        AppMatchKind.Path =>
            item.AppId.Length > 0 && id.ExePath.Length > 0 &&
            string.Equals(item.AppId, id.ExePath, StringComparison.OrdinalIgnoreCase),

        _ => false,
    };

    private static bool FileNameMatches(PasswordInjectionItem item, AppIdentity id)
    {
        string a = item.OriginalFilename.Length > 0 ? item.OriginalFilename
                 : item.ExeName.Length > 0 ? item.ExeName
                 : SafeFileName(item.AppId);
        if (a.Length == 0) return false;
        return a.Equals(id.OriginalFilename, StringComparison.OrdinalIgnoreCase)
            || a.Equals(id.ExeName, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 读取细节 ----

    private static string SafeFileName(string path)
    {
        try { return path.Length == 0 ? "" : Path.GetFileName(path); }
        catch { return ""; }
    }

    private static string ReadOriginalFilename(string exePath)
    {
        try { return FileVersionInfo.GetVersionInfo(exePath).OriginalFilename ?? ""; }
        catch { return ""; }
    }

    /// <summary>取签名者证书主体 CN + 指纹。只抽取信息，不验证信任链。</summary>
    public static (string Publisher, string Thumbprint) ReadAuthenticode(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return ("", "");
        try
        {
            // SYSLIB0057：X509CertificateLoader 不支持从 PE 的 Authenticode 签名里取证书，没有替代 API。
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(exePath));
#pragma warning restore SYSLIB0057
            return (cert.GetNameInfo(X509NameType.SimpleName, false) ?? "", cert.Thumbprint ?? "");
        }
        catch { return ("", ""); } // 未签名 / 读不到证书
    }

    private static string QueryPath(IntPtr hProcess)
    {
        var sb = new StringBuilder(1024);
        uint sz = (uint)sb.Capacity;
        return QueryFullProcessImageNameW(hProcess, 0, sb, ref sz) ? sb.ToString() : "";
    }

    private delegate int AppModelQuery(IntPtr hProcess, ref uint len, StringBuilder? buf);

    /// <summary>
    /// GetPackageFamilyName / GetApplicationUserModelId 共用的两段式调用。
    /// 非打包进程返回 APPMODEL_ERROR_NO_PACKAGE(15700) / NO_APPLICATION(15703)，是正常结果。
    /// </summary>
    private static string QueryAppModelString(IntPtr hProcess, AppModelQuery fn)
    {
        try
        {
            uint len = 0;
            int rc = fn(hProcess, ref len, null);
            if (rc != ERROR_INSUFFICIENT_BUFFER || len == 0) return "";
            var sb = new StringBuilder((int)len);
            return fn(hProcess, ref len, sb) == 0 ? sb.ToString() : "";
        }
        catch (EntryPointNotFoundException) { return ""; } // 理论上 Win8+ 都有，保险
        catch { return ""; }
    }

    // ---- P/Invoke ----
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder buf, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr hProcess, ref uint len, StringBuilder? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint len, StringBuilder? id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder buf, int max);
}
