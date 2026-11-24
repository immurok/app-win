using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ImmurokCommon.Security;

/// <summary>
/// <c>WinVerifyTrust</c>（WINTRUST_ACTION_GENERIC_VERIFY_V2，无 UI，不查吊销）+ 签名者证书信息。
/// Service 的调用方分级和客户端的自升级（校验下载的安装包）共用。
/// </summary>
public static class Authenticode
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

    /// <summary>签名者证书指纹；未签名 / 读不到为 null。只抽取，不验证信任链（先调 <see cref="IsValid"/>）。</summary>
    public static string? Thumbprint(string path) => WithCert(path, c => c.GetCertHashString());

    /// <summary>
    /// 签名者证书主体（完整 DN）；未签名 / 读不到为 null。
    /// Azure Artifact Signing 的证书几天一换，跨版本比发布者只能比主体，不能比指纹。
    /// </summary>
    public static string? Subject(string path) => WithCert(path, c => c.Subject);

    private static string? WithCert(string path, Func<X509Certificate, string> pick)
    {
        // SYSLIB0057：X509CertificateLoader 不支持从 PE 的 Authenticode 签名里取证书，没有替代 API。
#pragma warning disable SYSLIB0057
        try { return pick(X509Certificate.CreateFromSignedFile(path)); }
#pragma warning restore SYSLIB0057
        catch { return null; }
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
