using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ImmurokClient.Localization;
using ImmurokClient.Services;

namespace ImmurokClient.Views;

public partial class SetupPage : Page
{
    private static string T(string k, params object[] a) => Loc.Instance.T(k, a);

    private sealed record LangItem(string Code, string Name);

    private bool _langInit;

    public SetupPage()
    {
        InitializeComponent();
        UserText.Text = Environment.UserName;
        InitLanguages();
        Loaded += async (_, _) =>
        {
            Loc.Instance.LanguageChanged -= OnLocLanguageChanged;
            Loc.Instance.LanguageChanged += OnLocLanguageChanged;
            await RefreshAsync();
        };
        Unloaded += (_, _) => Loc.Instance.LanguageChanged -= OnLocLanguageChanged;
    }

    private async void OnLocLanguageChanged() => await RefreshAsync();

    private void InitLanguages()
    {
        _langInit = true;
        LangCombo.ItemsSource = Loc.SupportedLanguages.Select(l => new LangItem(l.Code, l.Name)).ToList();
        LangCombo.SelectedValue = Loc.Instance.CurrentLanguage;
        _langInit = false;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_langInit) return;
        if (LangCombo.SelectedValue is not string code) return;
        Loc.Instance.SetLanguage(code);   // 即时刷新所有 {loc:Tr} 绑定
        ClientSettings.Language = code;    // 持久化
    }

    private bool _loading;
    private bool _configured;

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        string? r = await AppServices.Pipe.PassStatusAsync();
        _configured = r?.Contains("CONFIGURED") == true;
        PassStateText.Text = r switch
        {
            var s when s?.Contains("CONFIGURED") == true => T("msg.setup.pass_configured"),
            var s when s?.Contains("NOTSET") == true => T("msg.setup.pass_notset"),
            null => T("msg.setup.pass_noservice"),
            _ => T("msg.setup.pass_status", r),
        };

        // 已配置：显示隐藏的清除按钮（悬停才可见）；未配置：始终显示设置按钮。
        ClearBtn.Visibility = _configured ? Visibility.Visible : Visibility.Collapsed;
        SetBtn.Visibility = _configured ? Visibility.Collapsed : Visibility.Visible;
        UpdateClearButtonReveal(PassStatusRow.IsMouseOver);

        _loading = true;
        string? sa = await AppServices.Pipe.SshAgentStatusAsync();
        SshAgentToggle.IsChecked = sa?.Contains("ON") == true;
        // 自启是纯本地设置（HKCU\Run），不经服务，服务离线时也照常可读可改。
        AutoStartToggle.IsChecked = AutoStart.IsEnabled;
        AutoStartFail.Visibility = Visibility.Collapsed;
        _loading = false;
    }

    /// <summary>
    /// 已配置时，清除按钮随「状态整行」的悬停淡入淡出（并同步命中测试，避免误点隐藏按钮）。
    /// 悬停区是包住整行的透明 Border，按钮本身也在其中，所以从文字移到按钮上不会中断。
    /// </summary>
    private void OnPassRowHover(object sender, System.Windows.Input.MouseEventArgs e)
        => UpdateClearButtonReveal(PassStatusRow.IsMouseOver);

    private void UpdateClearButtonReveal(bool hovering)
    {
        if (!_configured) { ClearBtn.Opacity = 0; ClearBtn.IsHitTestVisible = false; return; }
        ClearBtn.Opacity = hovering ? 1 : 0;
        ClearBtn.IsHitTestVisible = hovering;
    }

    /// <summary>开机自启开关：写不进注册表时把开关拨回去并提示，不让界面显示未生效的状态。</summary>
    private void OnAutoStartToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = AutoStartToggle.IsChecked == true;
        if (AutoStart.SetEnabled(on))
        {
            AutoStartFail.Visibility = Visibility.Collapsed;
        }
        else
        {
            AutoStartFail.Visibility = Visibility.Visible;
            _loading = true;
            AutoStartToggle.IsChecked = !on;
            _loading = false;
        }
    }

    private async void OnSshAgentToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = SshAgentToggle.IsChecked == true;
        SshAgentToggle.IsEnabled = false;
        SshAgentHint.Text = T("msg.ssh.working");
        try
        {
            var res = await SshAgentSwitch.SetAsync(on);
            SshAgentHint.Text = SshAgentSwitch.Describe(res, on);
            if (!res.Ok)
            {
                _loading = true;
                SshAgentToggle.IsChecked = !on;
                _loading = false;
            }
        }
        finally { SshAgentToggle.IsEnabled = true; }
    }

    private async void OnSetClick(object sender, RoutedEventArgs e)
    {
        var (ok, pwd) = PasswordDialog.Show(Window.GetWindow(this), T("setup.pass.set"));
        if (!ok || string.IsNullOrEmpty(pwd)) return;

        // 写凭据要在设备上触摸确认：用统一的指纹等待窗（30s 倒计时 + 取消），取消 / 超时返回 null。
        string? r = FpAuthDialog.Run(Window.GetWindow(this),
            T("msg.fpauth.pass_set"),
            () => AppServices.Pipe.PassSetAsync(Environment.UserName, pwd));
        PassHint.Text = r == "OK" ? T("msg.setup.saved") : T("msg.setup.save_fail", r ?? T("msg.setup.pass_cancelled"));
        await RefreshAsync();
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        // 清密码不要门，但受 owner 校验：非 owner 会回 DENY:NOT_OWNER，不能一律提示「已清除」。
        string? r = await AppServices.Pipe.PassClearAsync();
        PassHint.Text = r == "OK" ? T("msg.setup.cleared") : T("common.op_fail", r ?? "");
        await RefreshAsync();
    }
}
