using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Ipc;

public enum CallerKind { TrustedClient, Other }

/// <param name="AllowUnsignedClients">Service 启动参数 <c>--allow-unsigned-clients</c>：开发构建放行一切；安装包不传。</param>
public sealed record CallerPolicyOptions(bool AllowUnsignedClients);

/// <summary>
/// 调用方进程分级（设计稿 §3.3 第一层）。**这是纵深防御，不是边界**：同用户、同完整性级别的进程可以
/// 注入合法客户端后再连，从 pid 看到的就是一个路径正确、签名齐全的进程（§9.1）。真正的边界只有
/// 设备上的那次触摸，所以会改变安全状态的命令都各自带门，这里只决定「要不要理你」。
///
/// 判定：映像路径必须在 Service 自己的安装目录下（Program Files 只有管理员可写，挡的是「用户在自己
/// 可写的目录放一个签名合法的旧版客户端」）；Service 自身带 Authenticode 签名时，调用方还必须签名
/// 有效且发布者证书指纹一致（<c>WinVerifyTrust</c> 验的是磁盘文件，进程内存可能早已被注入——这就是
/// 为什么它只是减速带）。Service 自身未签名（当前的构建就是）时退化为只看路径，
/// <see cref="Mode"/> 报 <c>path-only</c>。
/// </summary>
public sealed class CallerPolicy
{
    private readonly ILogger<CallerPolicy> _log;
    private readonly bool _allowUnsigned;
    private readonly string _installDir;
    private readonly string? _selfThumbprint;
    private readonly ConcurrentDictionary<string, (DateTime Mtime, bool Trusted)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>off</c>（--allow-unsigned-clients）/ <c>path-only</c>（Service 未签名）/ <c>signed</c>。供 SECURITY:STATUS。</summary>
    public string Mode => _allowUnsigned ? "off" : _selfThumbprint is null ? "path-only" : "signed";

    public CallerPolicy(ILogger<CallerPolicy> log, CallerPolicyOptions options)
    {
        _log = log;
        _allowUnsigned = options.AllowUnsignedClients;
        _installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar;

        string? self = Environment.ProcessPath;
        _selfThumbprint = self is not null && Authenticode.IsValid(self) ? Thumbprint(self) : null;

        if (_allowUnsigned)
            _log.LogWarning("调用方校验已关闭（--allow-unsigned-clients），仅限开发构建");
        else if (_selfThumbprint is null)
            _log.LogWarning("Service 自身未签名，调用方校验只按安装目录路径: {Dir}", _installDir);
        else
            _log.LogInformation("调用方校验：安装目录 {Dir} + 发布者签名 {Thumb}", _installDir, _selfThumbprint);
    }

    public CallerKind Classify(CallerIdentity? caller)
    {
        if (_allowUnsigned) return CallerKind.TrustedClient;
        if (caller?.ImagePath is null) return CallerKind.Other;

        string path = caller.ImagePath;
        if (!path.StartsWith(_installDir, StringComparison.OrdinalIgnoreCase)) return CallerKind.Other;
        if (_selfThumbprint is null) return CallerKind.TrustedClient; // path-only

        try
        {
            DateTime mtime = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var c) && c.Mtime == mtime)
                return c.Trusted ? CallerKind.TrustedClient : CallerKind.Other;

            bool ok = Authenticode.IsValid(path)
                      && string.Equals(Thumbprint(path), _selfThumbprint, StringComparison.OrdinalIgnoreCase);
            _cache[path] = (mtime, ok);
            if (!ok) _log.LogWarning("调用方签名校验未通过: {Path}", path);
            return ok ? CallerKind.TrustedClient : CallerKind.Other;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "调用方签名校验异常: {Path}", path);
            return CallerKind.Other;
        }
    }

    private static string? Thumbprint(string path)
    {
        // SYSLIB0057：X509CertificateLoader 不支持从 PE 的 Authenticode 签名里取证书，没有替代 API。
#pragma warning disable SYSLIB0057
        try { return X509Certificate.CreateFromSignedFile(path).GetCertHashString(); }
#pragma warning restore SYSLIB0057
        catch { return null; }
    }
}

/// <summary><c>WinVerifyTrust</c>（WINTRUST_ACTION_GENERIC_VERIFY_V2，无 UI，不查吊销）。</summary>
internal static class Authenticode
{
    public static bool IsValid(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int rc = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return rc == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pFile);
        }
    }

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WINTRUST_DATA data);
}
