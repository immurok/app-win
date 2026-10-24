using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace ImmurokClient.Services;

/// <summary>
/// 设置向导「安装检查」「蓝牙连接」两步要看的本机状态：Credential Provider 是否注册、
/// 服务能否拉起、蓝牙开关。都是客户端本地就能查的，不经服务。
/// </summary>
public static class SystemComponents
{
    /// <summary>与 packaging/immurok.iss 的 ServiceName 一致。</summary>
    public const string ServiceName = "ImmurokService";

    /// <summary>CP 的 CLSID，与 ImmurokCredentialProvider/guid.h、immurok.iss 的 CpClsid 一致。</summary>
    private const string CpClsid = "{C433E3A5-FA34-42B1-A94C-5D793D2EEA83}";

    /// <summary>
    /// LogonUI 只认 HKLM\…\Authentication\Credential Providers 下登记过的 CLSID。
    /// 安装包写这一项；没有它锁屏上就不会出现 immurok 磁贴，指纹解锁静默失效。
    /// 普通用户对 HKLM 有读权限，查得到。
    /// </summary>
    public static bool IsCredentialProviderRegistered()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{CpClsid}");
            return key is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 提权执行 <c>sc start</c>（弹 UAC）。用户拒绝 UAC 或启动失败返回 false；
    /// 服务本来就在跑（1056）算成功。
    /// </summary>
    public static async Task<bool> StartServiceElevatedAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\sc.exe"),
                Arguments = $"start {ServiceName}",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode is 0 or 1056;
        }
        catch (Win32Exception) { return false; } // UAC 被拒
        catch { return false; }
    }

    /// <summary>
    /// 蓝牙开关：true = 开，false = 有蓝牙无线电但关着，null = 没有蓝牙适配器或查不到。
    /// 只用于给「等待连接」换一句更准的提示，不作为任何判断依据。
    /// </summary>
    public static async Task<bool?> IsBluetoothOnAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bt = radios.Where(r => r.Kind == RadioKind.Bluetooth).ToList();
            if (bt.Count == 0) return null;
            return bt.Any(r => r.State == RadioState.On);
        }
        catch { return null; }
    }

    /// <summary>打开 Windows 设置 › 蓝牙和其他设备。</summary>
    public static void OpenBluetoothSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch { /* 设置应用不可用就算了 */ }
    }
}
