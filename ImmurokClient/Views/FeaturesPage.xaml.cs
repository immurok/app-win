using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ImmurokClient.Localization;
using ImmurokClient.Services;
using ImmurokCommon.Protocol;
using Wpf.Ui.Controls;

namespace ImmurokClient.Views;

/// <summary>
/// 功能开关页：解锁 / 锁屏 / SSH agent / WSL2 支持 / imk agent / OTP。
/// 开关值全部存在 Service 侧（%ProgramData%\immurok\settings.json），各功能入口自行判定，
/// 所以这里只负责读写，不缓存业务状态。
/// </summary>
public partial class FeaturesPage : Page
{
    private static string T(string k, params object[] a) => Loc.Instance.T(k, a);

    /// <summary>加载中：抑制 Click 触发的回写，避免把读回来的值又写一遍。</summary>
    private bool _loading;

    public FeaturesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            Loc.Instance.LanguageChanged -= OnLanguageChanged;
            Loc.Instance.LanguageChanged += OnLanguageChanged;
            await RefreshAsync();
        };
        Unloaded += (_, _) => Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private async void OnLanguageChanged() => await RefreshAsync();

    private async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            await PairBanner.RefreshAsync();
            await SecBanner.RefreshAsync();

            string? r = await AppServices.Pipe.FeatureGetAsync();
            if (r is null || !r.StartsWith(IpcProtocol.Ok))
            {
                StatusText.Text = T("common.no_service_resp");
                SetAllEnabled(false);
                return;
            }

            // OK:<unlock>:<lock>:<ssh>:<agent>:<otp>
            string[] p = r.Split(IpcProtocol.Sep);
            if (p.Length < 6) { StatusText.Text = T("common.no_service_resp"); return; }

            SetAllEnabled(true);
            UnlockToggle.IsChecked = p[1] == "1";
            LockToggle.IsChecked   = p[2] == "1";
            SshToggle.IsChecked    = p[3] == "1";
            AgentToggle.IsChecked  = p[4] == "1";
            OtpToggle.IsChecked    = p[5] == "1";
            WslToggle.IsChecked    = await WslSwitch.IsOnAsync();
            StatusText.Text = "";

            // 解锁依赖已配置的登录密码，没配就点亮提示（开关仍可开，只是不会生效）。
            string? pass = await AppServices.Pipe.PassStatusAsync();
            UnlockWarn.Visibility = pass?.Contains("CONFIGURED") == true
                ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _loading = false; }
    }

    private void SetAllEnabled(bool on)
    {
        UnlockToggle.IsEnabled = on;
        LockToggle.IsEnabled = on;
        SshToggle.IsEnabled = on;
        AgentToggle.IsEnabled = on;
        OtpToggle.IsEnabled = on;
        WslToggle.IsEnabled = on;
    }

    /// <summary>
    /// WSL2 支持开关：开 = 服务开监听 + 在每个 WSL2 发行版里装桥并配置 sudo / ssh；关 = 全部撤掉。
    /// 发行版要先被拉起来，整个过程可能十几秒，期间开关禁用。关闭不要求已配对（要能撤干净）。
    /// </summary>
    private async void OnWslToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = WslToggle.IsChecked == true;

        void Revert()
        {
            _loading = true;
            WslToggle.IsChecked = !on;
            _loading = false;
        }

        if (on && !await PairingGuard.EnsurePairedAsync(Window.GetWindow(this)))
        {
            Revert();
            StatusText.Text = T("msg.pair.required_banner");
            return;
        }

        WslToggle.IsEnabled = false;
        StatusText.Text = T("msg.wsl.working");
        try
        {
            var res = await WslSwitch.SetAsync(on);
            StatusText.Text = WslSwitch.Describe(res, on);
            if (!res.Ok) Revert();
        }
        catch (System.Exception ex)
        {
            StatusText.Text = T("common.op_fail", ex.Message);
            Revert();
        }
        finally { WslToggle.IsEnabled = true; }
    }

    /// <summary>五个开关共用：功能名放在 Tag 上，直接透传给 Service。</summary>
    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not ToggleSwitch sw || sw.Tag is not string name) return;

        bool on = sw.IsChecked == true;

        // 五个功能全靠设备完成认证，未配对时开了也只是个摆设：拦下来说明原因并引导去配对，
        // 同时把开关拨回原位——别让界面显示一个不会生效的「已开启」。
        if (on && !await PairingGuard.EnsurePairedAsync(Window.GetWindow(this)))
        {
            _loading = true;
            sw.IsChecked = false;
            _loading = false;
            StatusText.Text = T("msg.pair.required_banner");
            return;
        }

        sw.IsEnabled = false;
        try
        {
            // SSH agent 多一半客户端工作（接管系统 agent 的结果说明 + git 指向原生 OpenSSH），走专用开关。
            if (name == IpcProtocol.FeatureSsh)
            {
                StatusText.Text = T("msg.ssh.working");
                var res = await SshAgentSwitch.SetAsync(on);
                StatusText.Text = SshAgentSwitch.Describe(res, on);
                if (!res.Ok)
                {
                    _loading = true;
                    sw.IsChecked = !on;
                    _loading = false;
                }
                return;
            }

            string? r = await AppServices.Pipe.FeatureSetAsync(name, on);
            if (r == IpcProtocol.Ok)
            {
                StatusText.Text = T(on ? "msg.features.on" : "msg.features.off", T("features." + name));
            }
            else
            {
                // 写失败就把开关拨回去，不要让界面显示一个并没生效的状态。
                StatusText.Text = T("common.op_fail", r ?? "");
                _loading = true;
                sw.IsChecked = !on;
                _loading = false;
            }
        }
        finally { sw.IsEnabled = true; }

        if (name == IpcProtocol.FeatureUnlock) await RefreshAsync();
    }
}
