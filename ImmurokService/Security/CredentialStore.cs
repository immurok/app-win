using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Security;

/// <summary>
/// 登录密码（可选带用户名）存于 Windows Credential Manager（Generic Credential）。
/// 对应 macOS 的 Keychain <c>com.immurok.password</c>。解锁时由 Service 读取后经 CP 管道下发。
///
/// 凭据 blob 不再是明文：密码用随机 AES-256-GCM 密钥加密，该密钥再用 TPM 里不可导出的 RSA 机器密钥
/// （Microsoft Platform Crypto Provider）包裹（格式见 <see cref="CredentialBlob"/>）。拷走磁盘 / 备份 /
/// 离线解 DPAPI 都拿不到密码，必须在本机 TPM 上解。挡不住在线的 SYSTEM（它也能用这把 TPM 密钥）。
/// 没有 TPM 时退回旧的 DPAPI 明文 blob，<see cref="Mode"/> 报 <c>dpapi</c>；旧安装的明文凭据在首次读取时迁移。
/// </summary>
public sealed class CredentialStore
{
    private const string TargetName = "immurok/login";
    internal const string TpmKeyName = "immurok-login-wrap";
    private static readonly CngProvider Pcp = new("Microsoft Platform Crypto Provider");

    private readonly ILogger<CredentialStore> _log;
    private readonly object _lock = new();
    private bool _migrationTried;

    public CredentialStore(ILogger<CredentialStore> log) => _log = log;

    /// <summary><c>tpm</c> / <c>dpapi</c>（未包裹的旧格式）/ <c>none</c>。供 SECURITY:STATUS。</summary>
    public string Mode
    {
        get
        {
            byte[]? blob = ReadBlob(out _);
            if (blob is null) return "none";
            string mode = CredentialBlob.IsWrapped(blob) ? "tpm" : "dpapi";
            Array.Clear(blob);
            return mode;
        }
    }

    public bool HasPassword() => TryRead(out _, out _);

    public void Save(string username, string password)
    {
        byte[] plain = Encoding.Unicode.GetBytes(password);
        try
        {
            lock (_lock)
            {
                byte[]? sealedBlob = TrySeal(plain);
                if (sealedBlob is null)
                    _log.LogWarning("TPM 不可用，登录密码按旧格式（仅 DPAPI）存储");
                WriteBlob(username, sealedBlob ?? plain);
            }
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>读取用户名与密码。失败（含 TPM 解不开）返回 false。</summary>
    public bool TryRead(out string username, out string password)
    {
        username = "";
        password = "";
        lock (_lock)
        {
            byte[]? blob = ReadBlob(out string user);
            if (blob is null) return false;
            username = user;
            try
            {
                if (!CredentialBlob.IsWrapped(blob))
                {
                    password = Encoding.Unicode.GetString(blob);
                    // 旧版明文凭据：每个进程只试一次迁移，TPM 不可用时不反复打 TPM。
                    if (!_migrationTried && blob.Length > 0)
                    {
                        _migrationTried = true;
                        byte[]? sealedBlob = TrySeal(blob);
                        if (sealedBlob is not null)
                        {
                            WriteBlob(user, sealedBlob);
                            _log.LogInformation("登录密码已迁移为 TPM 包裹存储");
                        }
                    }
                    return true;
                }

                using RSA? rsa = OpenTpmKey(create: false);
                byte[]? plain = rsa is null ? null : CredentialBlob.Open(blob, (pad, w) => rsa.Decrypt(w, Padding(pad)));
                if (plain is null)
                {
                    // TPM 被清除 / 换主板 / 数据被改：与「未设置密码」同路径，用户重新设一次即可。
                    _log.LogError("登录密码无法用本机 TPM 解开，需重新设置");
                    return false;
                }
                password = Encoding.Unicode.GetString(plain);
                Array.Clear(plain);
                return true;
            }
            finally
            {
                Array.Clear(blob);
            }
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
    /// 本就不存在也视为成功。顺带删掉 TPM 包裹密钥（尽力而为，不影响返回值）。
    /// </summary>
    public static bool DeleteStored()
    {
        bool ok = CredDelete(TargetName, CRED_TYPE_GENERIC, 0)
                  || Marshal.GetLastWin32Error() == ERROR_NOT_FOUND;
        try
        {
            if (CngKey.Exists(TpmKeyName, Pcp, CngKeyOpenOptions.MachineKey))
                using (CngKey key = CngKey.Open(TpmKeyName, Pcp, CngKeyOpenOptions.MachineKey)) key.Delete();
        }
        catch (CryptographicException) { /* 没有 TPM / 已删除 */ }
        return ok;
    }

    // ---- TPM ----

    /// <summary>
    /// 用 TPM 密钥封装，并当场解一次核对（不同 TPM / 驱动对 OAEP 哈希的支持不一，先 SHA-256 后 SHA-1）。
    /// 任何一步失败返回 null，由调用方退回旧格式——宁可降级也不能把用户锁在外面。
    /// </summary>
    private byte[]? TrySeal(byte[] plain)
    {
        using RSA? rsa = OpenTpmKey(create: true);
        if (rsa is null) return null;
        foreach (byte pad in new[] { CredentialBlob.OaepSha256, CredentialBlob.OaepSha1 })
        {
            try
            {
                byte[] blob = CredentialBlob.Seal(plain, pad, dek => rsa.Encrypt(dek, Padding(pad)));
                byte[]? check = CredentialBlob.Open(blob, (p, w) => rsa.Decrypt(w, Padding(p)));
                bool same = check is not null && check.AsSpan().SequenceEqual(plain);
                if (check is not null) Array.Clear(check);
                if (same) return blob;
            }
            catch (CryptographicException ex)
            {
                _log.LogDebug(ex, "TPM 封装失败（padding {Pad}）", pad);
            }
        }
        return null;
    }

    private RSA? OpenTpmKey(bool create)
    {
        try
        {
            CngKey key;
            if (CngKey.Exists(TpmKeyName, Pcp, CngKeyOpenOptions.MachineKey))
                key = CngKey.Open(TpmKeyName, Pcp, CngKeyOpenOptions.MachineKey);
            else if (create)
                key = CngKey.Create(CngAlgorithm.Rsa, TpmKeyName, new CngKeyCreationParameters
                {
                    Provider = Pcp,
                    KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                    ExportPolicy = CngExportPolicies.None,
                    Parameters = { new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None) },
                });
            else
                return null;
            using (key) return new RSACng(key); // RSACng 复制句柄，原 CngKey 可释放
        }
        catch (CryptographicException ex)
        {
            _log.LogDebug(ex, "打开 / 创建 TPM 密钥失败");
            return null;
        }
    }

    private static RSAEncryptionPadding Padding(byte pad) => pad switch
    {
        CredentialBlob.OaepSha256 => RSAEncryptionPadding.OaepSHA256,
        CredentialBlob.OaepSha1 => RSAEncryptionPadding.OaepSHA1,
        _ => throw new CryptographicException($"unknown padding {pad}"),
    };

    // ---- Credential Manager ----

    private void WriteBlob(string username, byte[] blob)
    {
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
        }
    }

    private static byte[]? ReadBlob(out string username)
    {
        username = "";
        if (!CredRead(TargetName, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
            return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            username = cred.UserName ?? "";
            byte[] blob = new byte[cred.CredentialBlob != IntPtr.Zero ? cred.CredentialBlobSize : 0];
            if (blob.Length > 0)
                Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
            return blob;
        }
        finally
        {
            CredFree(credPtr);
        }
    }

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

/// <summary>
/// 包裹格式 v1：<c>magic(8) | padding(1) | wrappedLen(2, LE) | wrapped DEK | nonce(12) | tag(16) | ciphertext</c>，
/// 前 11 字节作 GCM 的 AAD。纯逻辑，不碰 TPM（包裹 / 解包由调用方传入），以便在任意平台单测。
/// </summary>
internal static class CredentialBlob
{
    public const byte OaepSha256 = 1;
    public const byte OaepSha1 = 2;

    // 开头是两个连续的 UTF-16 高代理项：旧格式（Encoding.Unicode 编码的密码）里不会出现，可与之区分。
    private static readonly byte[] Magic = [0x00, 0xD8, 0x00, 0xD8, (byte)'I', (byte)'M', (byte)'K', (byte)'1'];
    private const int HeaderLen = 11, NonceLen = 12, TagLen = 16;

    public static bool IsWrapped(ReadOnlySpan<byte> blob)
        => blob.Length >= HeaderLen && blob[..Magic.Length].SequenceEqual(Magic);

    public static byte[] Seal(ReadOnlySpan<byte> plaintext, byte padding, Func<byte[], byte[]> wrapKey)
    {
        byte[] dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            byte[] wrapped = wrapKey(dek);
            if (wrapped.Length > ushort.MaxValue) throw new CryptographicException("wrapped key too large");
            byte[] blob = new byte[HeaderLen + wrapped.Length + NonceLen + TagLen + plaintext.Length];
            Magic.CopyTo(blob, 0);
            blob[8] = padding;
            blob[9] = (byte)wrapped.Length;
            blob[10] = (byte)(wrapped.Length >> 8);
            wrapped.CopyTo(blob, HeaderLen);
            int n = HeaderLen + wrapped.Length;
            Span<byte> nonce = blob.AsSpan(n, NonceLen);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(dek, TagLen);
            aes.Encrypt(nonce, plaintext, blob.AsSpan(n + NonceLen + TagLen), blob.AsSpan(n + NonceLen, TagLen),
                        blob.AsSpan(0, HeaderLen));
            return blob;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>解开；格式不对 / 解包失败 / 认证失败一律返回 null。</summary>
    public static byte[]? Open(ReadOnlySpan<byte> blob, Func<byte, byte[], byte[]> unwrapKey)
    {
        if (!IsWrapped(blob)) return null;
        int wrappedLen = blob[9] | (blob[10] << 8);
        int n = HeaderLen + wrappedLen;
        if (blob.Length < n + NonceLen + TagLen) return null;
        byte[]? dek = null;
        try
        {
            dek = unwrapKey(blob[8], blob.Slice(HeaderLen, wrappedLen).ToArray());
            if (dek.Length != 32) return null;
            byte[] plain = new byte[blob.Length - n - NonceLen - TagLen];
            using var aes = new AesGcm(dek, TagLen);
            aes.Decrypt(blob.Slice(n, NonceLen), blob[(n + NonceLen + TagLen)..], blob.Slice(n + NonceLen, TagLen),
                        plain, blob[..HeaderLen]);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            if (dek is not null) CryptographicOperations.ZeroMemory(dek);
        }
    }
}
