using System.Threading.Tasks;
using System.Windows;
using ImmurokClient.Localization;

namespace ImmurokClient.Services;

/// <summary>
/// 「本机是否已与设备配对」的统一判定与未配对引导。
///
/// 未配对时几乎所有依赖设备的操作（指纹录入/删除、密钥库读写、屏幕解锁开关…）都会在
/// Service 侧因缺少 shared_key 而失败，且回来的是 ERROR/REJECT 这类无从解读的码。
/// 与其让每个入口各自翻译一遍失败原因，不如在点击时先拦一道：明说「未配对」，
/// 并把用户直接送到「设备」页的配对入口。
/// </summary>
public static class PairingGuard
{
    private static string T(string k, params object[] a) => Loc.Instance.T(k, a);

    /// <summary>查询本机配对状态。服务无响应时按「未配对」处理（此时任何设备操作也做不了）。</summary>
    public static async Task<bool> IsPairedAsync()
    {
        string? r = await AppServices.Pipe.PairStatusAsync();
        return r?.Contains("PAIRED") == true && r.Contains("UNPAIRED") != true;
    }

    /// <summary>
    /// 需要配对的入口统一走这里。已配对返回 true；未配对弹窗说明并询问是否去配对，
    /// 用户确认则跳到「设备」页，返回 false（调用方直接 return 即可）。
    /// </summary>
    public static async Task<bool> EnsurePairedAsync(Window? owner)
    {
        if (await IsPairedAsync()) return true;

        bool go = Views.MessageDialog.Confirm(owner, T("msg.pair.required_title"), T("msg.pair.required_body"),
            Views.MessageKind.Info);

        if (go) NavigateToPairing();
        return false;
    }

    /// <summary>跳到「设备」页并把焦点落在配对按钮上。</summary>
    public static void NavigateToPairing()
        => (Application.Current?.MainWindow as MainWindow)?.GoToPairing();
}
