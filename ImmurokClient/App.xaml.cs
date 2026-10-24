using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using ImmurokCommon.Protocol;

namespace ImmurokClient;

public partial class App : Application
{
    private static Mutex? _mutex;
    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private Views.SetupWizardWindow? _wizard;
    private bool _forceExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "immurok-client-singleton", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);

        // 全局异常兜底：记录到 %AppData%\immurok\client-error.log 并弹窗，避免静默崩溃。
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 多语言：按已保存的语言初始化，未设置则默认英语（与 macOS 支持语言一致）。
        ImmurokClient.Localization.Loc.Instance.SetLanguage(
            ImmurokClient.Services.ClientSettings.Language ?? ImmurokClient.Localization.Loc.DefaultLanguage);

        // 托盘常驻：关闭主窗口只是隐藏到托盘，退出走托盘菜单。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _window = new MainWindow();
        _window.Closing += (_, args) =>
        {
            if (!_forceExit)
            {
                args.Cancel = true;
                _window.Hide();
            }
        };

        _tray = new TaskbarIcon
        {
            ToolTipText = "immurok",
            // 必须指向 .ico（H.NotifyIcon 要把流转成 System.Drawing.Icon，PNG 会抛 ArgumentException）
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/immurok.ico")),
        };
        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = Localization.Loc.Instance.T("tray.open") };
        openItem.Click += (_, _) => ShowWindow();
        var wizardItem = new MenuItem { Header = Localization.Loc.Instance.T("tray.wizard") };
        wizardItem.Click += (_, _) => ShowSetupWizard();
        var exitItem = new MenuItem { Header = Localization.Loc.Instance.T("tray.exit") };
        exitItem.Click += (_, _) =>
        {
            _forceExit = true;
            _tray?.Dispose();
            Shutdown();
        };
        menu.Items.Add(openItem);
        menu.Items.Add(wizardItem);
        menu.Items.Add(exitItem);
        _tray.ContextMenu = menu;
        _tray.TrayMouseDoubleClick += (_, _) => ShowWindow();
        _tray.ForceCreate();

        // 首屏：该走向导就只弹向导；否则已绑定的机器上启动（通常是开机自启）不弹主窗口，只驻托盘。
        _ = ShowStartupUiAsync();

        // 开机自启：首次运行默认开启；之后只在安装路径变化时校正，不覆盖用户的选择。
        // 注入必须由本进程（用户会话）完成，客户端不常驻就等于功能静默失效。
        Services.AutoStart.SyncOnStartup();

        // 指纹触发注入：后台轮询服务端信号，命中前台注入项时自动填入密码。
        Services.AppServices.Injection.Start();
    }

    /// <summary>
    /// 首屏决策（docs/setup-wizard-flow.md「何时弹出」的 Windows 版）：
    /// 用户从未点过向导的「完成 / 稍后再说」，或服务连不上、锁屏组件（CP）没登记，就弹设置向导；
    /// 否则沿用老规则——未绑定才显示主窗口，已绑定只驻托盘。
    /// </summary>
    private async System.Threading.Tasks.Task ShowStartupUiAsync()
    {
        // 开机自启时服务可能比客户端晚几秒就绪：多问几次再下「服务没起来」的结论，
        // 否则每次开机都会误弹一次向导。
        string? pair = null;
        for (int i = 0; i < 5 && pair is null; i++)
        {
            if (i > 0) await System.Threading.Tasks.Task.Delay(2000);
            pair = await Services.AppServices.Pipe.SendAsync(
                $"{IpcProtocol.Pair}:{IpcProtocol.PairStatus}", timeoutMs: 3000);
        }
        bool serviceUp = pair is not null;
        bool paired = pair?.Contains("PAIRED") == true && !pair.Contains("UNPAIRED");

        if (!Services.ClientSettings.SetupCompleted || !serviceUp
            || !Services.SystemComponents.IsCredentialProviderRegistered())
        {
            ShowSetupWizard();
            return;
        }
        if (!paired) ShowWindow();
    }

    /// <summary>打开设置向导（已开着就提到前台）。托盘菜单、设置页、启动时共用。</summary>
    public void ShowSetupWizard()
    {
        if (_wizard is not null)
        {
            _wizard.WindowState = WindowState.Normal;
            _wizard.Activate();
            return;
        }
        _wizard = new Views.SetupWizardWindow();
        _wizard.Closed += (_, _) => _wizard = null;
        _wizard.Show();
        _wizard.Activate();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.AppServices.Injection.Stop();
        _tray?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 全局未处理异常兜底：写日志 + 弹窗给出可见错误，并标记已处理避免直接崩溃。
    /// 之前缺这个处理器，UI 里任何异常都会静默终止进程（如「点确定后没反应/没保存」）。
    /// </summary>
    private void OnDispatcherUnhandledException(
        object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "immurok");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "client-error.log"),
                $"[{DateTime.Now:O}] {args.Exception}\n\n");
        }
        catch { /* 记录失败就算了 */ }

        try
        {
            MessageBox.Show(args.Exception.ToString(), "immurok",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* 弹窗失败也不再抛 */ }

        args.Handled = true; // 不让未处理异常直接终止进程
    }
}
