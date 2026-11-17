using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Security;

/// <summary>
/// 登录密码（可选带用户名）存于 Windows Credential Manager（Generic Credential）。
/// 对应 macOS 的 Keychain <c>com.immurok.password</c>。解锁时由 Service 读取后经 CP 管道下发。
/// </summary>
public sealed class CredentialStore
{
    private const string TargetName = "immurok/login";
    private readonly ILogger<CredentialStore> _log;

    public CredentialStore(ILogger<CredentialStore> log) => _log = log;

    public bool HasPassword() => TryRead(out _, out _);

    public void Save(string username, string password)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetName,
                UserName = username,
                CredentialBlob = handle.AddrOfPinnedObject(),
                CredentialBlobSize = (uint)blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWrite(ref cred, 0))
                _log.LogError("CredWrite 失败: {Err}", Marshal.GetLastWin32Error());
        }
        finally
        {
            handle.Free();
            Array.Clear(blob);
        }
    }

    /// <summary>读取用户名与密码。失败返回 false。</summary>
    public bool TryRead(out string username, out string password)
    {
        username = "";
        password = "";
        if (!CredRead(TargetName, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
            return false;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            username = cred.UserName ?? "";
            if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
            {
                byte[] blob = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
                password = Encoding.Unicode.GetString(blob);
                Array.Clear(blob);
            }
            return true;
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public void Clear()
    {
        if (!CredDelete(TargetName, CRED_TYPE_GENERIC, 0))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND)
                _log.LogWarning("CredDelete 失败: {Err}", err);
        }
    }

    /// <summary>
    /// 不依赖 DI 的删除入口，供卸载清理使用（<c>ImmurokService.exe --clear-credential</c>）。
    /// 凭据以 CRED_PERSIST_LOCAL_MACHINE 写在服务账户（LocalSystem）名下，只有同一身份能删；
    /// 调用方（Program.cs）负责保证当前进程是 SYSTEM，否则删的是别人的凭据集。
    /// 本就不存在也视为成功。
    /// </summary>
    public static bool DeleteStored()
        => CredDelete(TargetName, CRED_TYPE_GENERIC, 0)
           || Marshal.GetLastWin32Error() == ERROR_NOT_FOUND;

    // ---- P/Invoke ----
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
