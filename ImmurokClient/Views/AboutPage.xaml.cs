using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ImmurokClient.Localization;
using ImmurokClient.Services;
using ImmurokCommon.Protocol;
using Wpf.Ui.Appearance;

namespace ImmurokClient.Views;

public partial class AboutPage : Page
{
    private static string T(string k, params object[] a) => Loc.Instance.T(k, a);

    private bool _busy;
    private string? _deviceVersion;   // 归一化后的设备固件版本
    private bool _updateReady;        // 已检测到可用更新

    public AboutPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            Loc.Instance.LanguageChanged -= OnLocLanguageChanged;
            Loc.Instance.LanguageChanged += OnLocLanguageChanged;
            ApplicationThemeManager.Changed -= OnThemeChanged;
            ApplicationThemeManager.Changed += OnThemeChanged;
            UpdateLogo();
            ShowAppUpdate(AppUpdate.Available);   // 自动检查已发现的新版本，进页面就能看到
            await LoadCurrentVersionAsync();
        };
        Unloaded += (_, _) =>
        {
            Loc.Instance.LanguageChanged -= OnLocLanguageChanged;
            ApplicationThemeManager.Changed -= OnThemeChanged;
        };
    }

    private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent) => UpdateLogo();

    /// <summary>页头 logo 跟随主题：暗色用白色版，亮色用黑色版（与标题栏图标同一对资源）。</summary>
    private void UpdateLogo()
    {
        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        Logo.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri($"pack://application:,,,/Assets/{(dark ? "immurok-white.png" : "immurok.png")}"));
    }

    private async void OnLocLanguageChanged()
    {
        ShowAppUpdate(AppUpdate.Available);
        await LoadCurrentVersionAsync();
    }

    // ---- 应用更新（只提示，不下载安装）----

    /// <summary>有新版本就显示提示和「前往下载」；没有则收起整块。</summary>
    private void ShowAppUpdate(AppRelease? release)
    {
        if (release is null)
        {
            AppUpdatePanel.Visibility = Visibility.Collapsed;
            AppDownloadBtn.Visibility = Visibility.Collapsed;
            return;
        }
        AppUpdateStatus.Text = T("msg.appupdate.available", release.Version, AppVersion);
        AppDownloadBtn.Visibility = Visibility.Visible;
        AppUpdatePanel.Visibility = Visibility.Visible;
    }

    private async void OnAppCheckClick(object sender, RoutedEventArgs e)
    {
        AppCheckBtn.IsEnabled = false;
        AppDownloadBtn.Visibility = Visibility.Collapsed;
        AppUpdateStatus.Text = T("msg.appupdate.checking");
        AppUpdatePanel.Visibility = Visibility.Visible;
        try
        {
            AppUpdateResult r = await AppUpdate.CheckAsync();
            switch (r.State)
            {
                case AppUpdateState.Available:
                    ShowAppUpdate(r.Release);
                    break;
                case AppUpdateState.UpToDate:
                    AppUpdateStatus.Text = T("msg.appupdate.latest", AppVersion);
                    break;
                default:
                    AppUpdateStatus.Text = T("msg.appupdate.fail");
                    break;
            }
        }
        finally { AppCheckBtn.IsEnabled = true; }
    }

    private void OnAppDownloadClick(object sender, RoutedEventArgs e) => AppUpdate.OpenReleasePage(AppUpdate.Available);

    /// <summary>
    /// 应用版本号取自程序集（源头是 Directory.Build.props 的 &lt;Version&gt;）。
    /// 原来这串是写死在多语言文案里的，升版本时必然漏掉——关于页一直显示 0.1.0。
    /// </summary>
    internal static string AppVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]          // 去掉 SourceLink 追加的 +<commit>
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "";

    /// <summary>读取当前设备固件版本填入卡片（未连接则置未知）。</summary>
    private async Task LoadCurrentVersionAsync()
    {
        AppVersionText.Text = T("about.version", AppVersion);
        _deviceVersion = null;
        string? info = await AppServices.Pipe.InfoAsync();
        if (info is not null && info.StartsWith(IpcProtocol.Ok))
        {
            string[] p = info.Split(IpcProtocol.Sep);
            if (p.Length >= 4 && p[3] != "-" && p[3].Length > 0)
                _deviceVersion = FwVersion.Normalize(p[3]);
        }
        CurrentVerText.Text = _deviceVersion ?? T("common.unknown");
    }

    private async void OnCheckClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await LoadCurrentVersionAsync();
        if (_deviceVersion is null)
        {
            OtaStatus.Text = T("msg.ota.check_no_device");
            return;
        }

        _busy = true;
        CheckBtn.IsEnabled = false;
        UpdateBtn.Visibility = Visibility.Collapsed;
        NotesBar.IsOpen = false;
        _updateReady = false;
        OtaStatus.Text = T("msg.ota.checking");
        try
        {
            FwCheckResult r = await AppServices.Firmware.CheckAsync(_deviceVersion);
            LatestVerText.Text = r.LatestVersion ?? T("common.unknown");

            if (r.Available)
            {
                _updateReady = true;
                UpdateBtn.Visibility = Visibility.Visible;
                OtaStatus.Text = T("msg.ota.update_available", r.LatestVersion ?? "");
                if (!string.IsNullOrWhiteSpace(r.Notes))
                {
                    NotesBar.Message = r.Notes;
                    NotesBar.IsOpen = true;
                }
            }
            else if (r.Plan == FwPlan.Unknown)
            {
                OtaStatus.Text = T("msg.ota.check_unknown");
            }
            else
            {
                OtaStatus.Text = T("msg.ota.up_to_date");
            }
        }
        catch (Exception)
        {
            OtaStatus.Text = T("msg.ota.check_fail");
        }
        finally
        {
            _busy = false;
            CheckBtn.IsEnabled = true;
        }
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !_updateReady || _deviceVersion is null) return;

        if (!MessageDialog.Confirm(Window.GetWindow(this), T("about.ota"), T("msg.ota.confirm"))) return;

        _busy = true;
        CheckBtn.IsEnabled = false;
        UpdateBtn.IsEnabled = false;
        OtaProgress.Visibility = Visibility.Visible;
        OtaProgress.Value = 0;
        OtaStatus.Text = T("msg.ota.progress");
        try
        {
            FwUpdateResult res = await AppServices.Firmware.RunUpdateAsync(
                _deviceVersion,
                stage => Dispatcher.Invoke(() => OtaStatus.Text = StageText(stage)),
                pct => Dispatcher.Invoke(() => { OtaProgress.Value = pct; }));

            if (res.Ok)
            {
                OtaStatus.Text = T("msg.ota.done");
                UpdateBtn.Visibility = Visibility.Collapsed;
                _updateReady = false;
                await LoadCurrentVersionAsync();
            }
            else
            {
                OtaStatus.Text = res.ErrorCode switch
                {
                    "battery" => T("msg.ota.battery_low", FirmwareUpdateService.MinBatteryPct),
                    "no_channel" => T("msg.ota.no_channel"),
                    "timeout" => T("msg.ota.timeout"),
                    "download" => T("msg.ota.download_fail"),
                    "bridge" => T("msg.ota.bridge_fail"),
                    "reconnect" => T("msg.ota.reconnect_fail"),
                    _ => T("msg.ota.fail", res.ErrorCode ?? ""),
                };
            }
        }
        finally
        {
            _busy = false;
            CheckBtn.IsEnabled = true;
            UpdateBtn.IsEnabled = true;
        }
    }

    private static string StageText(FwStage stage) => stage switch
    {
        FwStage.Downloading => T("msg.ota.stage_download"),
        FwStage.DownloadingBridge => T("msg.ota.stage_download_bridge"),
        FwStage.Pushing => T("msg.ota.stage_push"),
        FwStage.PushingBridge => T("msg.ota.stage_push_bridge"),
        FwStage.WaitingReconnect => T("msg.ota.stage_reconnect"),
        _ => T("msg.ota.progress"),
    };
}
