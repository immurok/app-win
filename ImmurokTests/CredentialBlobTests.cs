using System.Security.Cryptography;
using System.Text;
using ImmurokService.Security;
using Xunit;

namespace ImmurokTests;

/// <summary>TPM 包裹格式的纯逻辑：软件 RSA 代替 TPM 密钥（TPM 只负责 wrap/unwrap 这一步）。</summary>
public class CredentialBlobTests
{
    private static readonly RSA Rsa = RSA.Create(2048);

    private static byte[] Seal(string password, byte pad = CredentialBlob.OaepSha256)
        => CredentialBlob.Seal(Encoding.Unicode.GetBytes(password), pad,
            dek => Rsa.Encrypt(dek, pad == CredentialBlob.OaepSha1 ? RSAEncryptionPadding.OaepSHA1 : RSAEncryptionPadding.OaepSHA256));

    private static byte[]? Open(byte[] blob, RSA? rsa = null)
        => CredentialBlob.Open(blob, (pad, w) => (rsa ?? Rsa).Decrypt(w,
            pad == CredentialBlob.OaepSha1 ? RSAEncryptionPadding.OaepSHA1 : RSAEncryptionPadding.OaepSHA256));

    [Theory]
    [InlineData("hunter2")]
    [InlineData("")]
    [InlineData("пароль 密码 🔐 with a much longer tail to exceed a single RSA block: 0123456789abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789")]
    public void Roundtrips_both_paddings(string password)
    {
        foreach (byte pad in new[] { CredentialBlob.OaepSha256, CredentialBlob.OaepSha1 })
        {
            byte[] blob = Seal(password, pad);
            Assert.True(CredentialBlob.IsWrapped(blob));
            Assert.Equal(password, Encoding.Unicode.GetString(Open(blob)!));
        }
    }

    [Fact]
    public void Ciphertext_does_not_contain_plaintext()
    {
        byte[] plain = Encoding.Unicode.GetBytes("CorrectHorseBatteryStaple");
        byte[] blob = Seal("CorrectHorseBatteryStaple");
        Assert.Equal(-1, blob.AsSpan().IndexOf(plain));
    }

    [Fact]
    public void Legacy_plaintext_is_not_mistaken_for_wrapped()
    {
        // 连「故意以两个高代理项开头」的密码也不行：Encoding.Unicode 把孤立代理项替换成 U+FFFD。
        foreach (string p in new[] { "", "a", "IMK1IMK1", "\uD800\uD800" + "IMK1xxxxxx", "Pa55w0rd!" })
            Assert.False(CredentialBlob.IsWrapped(Encoding.Unicode.GetBytes(p)));
    }

    [Fact]
    public void Tampering_any_byte_fails_closed()
    {
        byte[] blob = Seal("hunter2");
        for (int i = 0; i < blob.Length; i++)
        {
            byte[] t = (byte[])blob.Clone();
            t[i] ^= 0x01;
            byte[]? r;
            try { r = Open(t); } catch (CryptographicException) { r = null; }
            Assert.True(r is null, $"byte {i} tamper was accepted");
        }
    }

    [Fact]
    public void Wrong_key_or_truncation_returns_null()
    {
        byte[] blob = Seal("hunter2");
        using var other = RSA.Create(2048);
        Assert.Null(Open(blob, other));
        Assert.Null(Open(blob[..20]));
        Assert.Null(Open(blob[..^1]));
    }
}
