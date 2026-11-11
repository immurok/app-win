using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImmurokClient.Views;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ImmurokClient;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // 标题栏带版本号（源头同关于页：Directory.Build.props 的 <Version>）。
        // 窗口 Title 保持 "immurok"，任务栏 / Alt-Tab 不带版本。
        AppTitleBar.Title = $"immurok {Views.AboutPage.AppVersion}";
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 启动时按注册表权威判定系统深/浅色再应用。
        // （ApplicationThemeManager.ApplySystemTheme() 在启动早期偶发误判，导致首屏主题不对，
        //   切换一次才恢复——这里直接读 AppsUseLightTheme 规避。）
        ApplicationThemeManager.Apply(DetectSystemTheme(), WindowBackdropType.Mica, updateAccent: true);

        // 监听系统主题切换，实时更新应用主题、Mica 背景与标题栏图标。
        SystemThemeWatcher.Watch(this);
        ApplicationThemeManager.Changed += OnThemeChanged;

        UpdateAppIcon();

        // 切换 tab 时右侧面板回到顶部（NavigationView 的内容宿主 ScrollViewer 会保留上一页偏移）。
        RootNavigation.Navigated += (_, _) =>
            // 延到布局完成后再复位，确保新页面与其 ScrollViewer 已就位。
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ResetContentScroll));

        RootNavigation.Navigate(typeof(DevicePage));
    }

    /// <summary>「功能」页安全横幅的「查看详情」落点：状态页。</summary>
    public void GoToStatus() => RootNavigation.Navigate(typeof(StatusPage));

    private void OnThemeChanged(ApplicationTheme currentTheme, Color accent) => UpdateAppIcon();

    /// <summary>
    /// 未配对引导的落点：切到「设备」页并让配对按钮拿到焦点（焦点框就是视觉指引）。
    /// 页面实例可能被 NavigationView 缓存复用，所以用静态标志传递「这次要聚焦配对」，
    /// 由 DevicePage 在 Loaded 时消费——已经停在设备页时也照样生效。
    /// </summary>
    public void GoToPairing()
    {
        DevicePage.FocusPairingOnLoad = true;
        RootNavigation.Navigate(typeof(DevicePage));
    }

    /// <summary>
    /// 把 NavigationView 内所有 ScrollViewer 复位到顶部。左侧导航面板项少不滚动，
    /// 复位无副作用；真正生效的是承载右侧页面内容的那个宿主 ScrollViewer。
    /// </summary>
    private void ResetContentScroll()
    {
        foreach (var sv in FindScrollViewers(RootNavigation))
        {
            sv.ScrollToVerticalOffset(0);
            sv.ScrollToHorizontalOffset(0);
        }
    }

    private static IEnumerable<ScrollViewer> FindScrollViewers(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) yield return sv;
            foreach (var nested in FindScrollViewers(child)) yield return nested;
        }
    }

    /// <summary>logo 跟随主题：暗色用白色版，亮色用黑色版。同时更新标题栏与任务栏图标。</summary>
    private void UpdateAppIcon()
    {
        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        string asset = dark ? "immurok-white.png" : "immurok.png";
        var src = new BitmapImage(new Uri($"pack://application:,,,/Assets/{asset}"));
        AppIcon.Source = src; // 标题栏左上角
        Icon = src;           // 任务栏 / Alt-Tab（Window.Icon，运行时可动态切换）
    }

    /// <summary>读注册表判定系统主题（AppsUseLightTheme：0=深色，1=浅色）。</summary>
    internal static ApplicationTheme DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0 ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }
        catch { /* 读不到就按暗色兜底 */ }
        return ApplicationTheme.Dark;
    }
}
