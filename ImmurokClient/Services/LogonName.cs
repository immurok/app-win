using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ImmurokClient.Services;

/// <summary>
/// 当前用户用于「密码登录 / 解锁」的账户名，随 PASS:SET 交给 Service，最终由 Credential Provider
/// 拆成域 + 用户名提交给 LSA。
///
/// <list type="bullet">
/// <item>本机账户 / 微软账户：裸用户名（CP 用计算机名当域，行为与以前一致）。</item>
/// <item>AD 域账户：<c>DOMAIN\user</c>。</item>
/// <item>Entra ID（Azure AD）加入：<c>AzureAD\user@contoso.com</c>——云账户按 UPN 登录，
/// <c>AzureAD\显示用的短名</c> LSA 不认；取不到 UPN 时退回短名。</item>
/// </list>
///
/// 以前一律传 <see cref="Environment.UserName"/>，CP 再套上计算机名，域账户于是被当成
/// 同名本机账户去验密码，必然「密码不正确」（GitHub app-win #2）。
/// </summary>
public static class LogonName
{
    private const string EntraDomain = "AzureAD";
    private const int NameUserPrincipal = 8;

    public static string Current()
    {
        string user = Environment.UserName;
        string domain = Environment.UserDomainName;
        if (string.IsNullOrEmpty(domain) ||
            string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return user;

        if (string.Equals(domain, EntraDomain, StringComparison.OrdinalIgnoreCase) &&
            UserPrincipalName() is { Length: > 0 } upn)
            return $"{domain}\\{upn}";

        return $"{domain}\\{user}";
    }

    private static string? UserPrincipalName()
    {
        try
        {
            var sb = new StringBuilder(512);
            uint size = (uint)sb.Capacity;
            return GetUserNameExW(NameUserPrincipal, sb, ref size) && sb.ToString().Contains('@')
                ? sb.ToString()
                : null;
        }
        catch (Exception) { return null; }
    }

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetUserNameExW(int nameFormat, StringBuilder lpNameBuffer, ref uint nSize);
}
