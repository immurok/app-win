using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ImmurokClient.Localization;
using ImmurokClient.Services;
using ImmurokCommon.Protocol;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
// 与 Wpf.Ui.Controls 的同名类型二义，显式取 WPF 那个。
using TextBlock = System.Windows.Controls.TextBlock;
using Button = Wpf.Ui.Controls.Button;

namespace ImmurokClient.Views;

/// <summary>
/// 首次运行设置向导：从小白视角带用户走完「蓝牙连接 → 绑定本机 → 解锁密码 → 录入指纹」。
/// 设计稿是主仓库 docs/setup-wizard-flow.md（macOS v1.6.6），本类是 Windows 落地，差异：
/// <list type="bullet">
/// <item>没有「辅助功能」步：Windows 锁屏走 Credential Provider，不靠键盘模拟。</item>
/// <item>「安装检查」看的是服务能否连上、CP 是否在 HKLM 登记，而不是 PAM。</item>
/// </list>
/// 设计原则（术语切分、少字多图、点「开始绑定」之前不画设备图、窗口置顶）改动时不要回退。
/// </summary>
public partial class SetupWizardWindow : FluentWindow
{
    private static string T(string k, params object[] a) => Loc.Instance.T(k, a);

    private enum Step { Welcome, Install, Connect, Pair, Password, Fingerprint, Finish }

    private const int AuthSlotCount = 5; // 认证槽 0–4；第 6 号（5）是切换指纹，向导不占用
    private const int QuickTimeoutMs = 2500; // 本地状态查询：服务没起来时别让每轮轮询挂 15s
    private const int BleTimeoutMs = 8000;   // SLOT:STATUS / FP:SLOTS 是真实 BLE 往返

    // ---- 状态快照（每秒轮询刷新） ----
    private bool _serviceUp;
    private bool _cpRegistered;
    private bool _connected;
    private string _deviceName = "";
    private bool _paired;          // 本地有 shared_key（PAIR:STATUS）
    private bool? _slotValid;      // 设备还认不认这把钥匙；null = 尚未复核
    private int _slotFailures;
    private bool _passConfigured;
    private int? _fpBitmap;        // FP:SLOTS；null = 未知
    private bool? _btOn;
    private DateTime _nextFpQuery = DateTime.MinValue;

    // ---- 向导状态 ----
    private readonly List<Step> _steps = new();
    private int _index;
    /// <summary>用户点了「上一步」或往回点侧栏：暂停自动前进，直到再点「下一步」。</summary>
    private bool _suppressAutoAdvance;
    private bool _ready;
    private bool _polling;
    private bool _slowBusy;
    private bool _busy;            // 本页动作进行中（绑定 / 设密码 / 录入 / 启动服务）
    private string? _message;      // 本页最近一次动作的结果，换步即清
    private bool _messageOk;
    private string? _renderKey;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public SetupWizardWindow()
    {
        InitializeComponent();
        StepHost.Content = new ProgressRing { IsIndeterminate = true, Width = 32, Height = 32, Margin = new Thickness(0, 120, 0, 0) };
        NextBtn.Visibility = PrevBtn.Visibility = Visibility.Collapsed;

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _timer.Stop();
            ApplicationThemeManager.Changed -= OnThemeChanged;
            Loc.Instance.LanguageChanged -= OnLanguageChanged;
        };
        _timer.Tick += async (_, _) =>
        {
            await PollAsync();
            Refresh();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 向导可能先于主窗口出现（首启只弹向导），主题得自己套一次。
        ApplicationThemeManager.Apply(MainWindow.DetectSystemTheme(), WindowBackdropType.Mica, updateAccent: true);
        SystemThemeWatcher.Watch(this);
        ApplicationThemeManager.Changed += OnThemeChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        UpdateLogo();

        await PollAsync();
        // 首次指纹 / 槽位查询最多等 2 秒：HID 刚连上时它们是真实 BLE 往返，可能很慢，
        // 不能让整个向导卡在转圈上；没等到的下一轮再补。
        await Task.WhenAny(SlowLaneAsync(), Task.Delay(2000));

        BuildSteps();
        JumpToFirstIncomplete();
        _ready = true;
        Refresh();
        _timer.Start();
    }

    private void OnThemeChanged(ApplicationTheme theme, Color accent)
    {
        UpdateLogo();
        _renderKey = null; // 示意图的颜色在绘制时取，重建一次即可
        Refresh();
    }

    private void OnLanguageChanged()
    {
        _renderKey = null;
        Refresh();
    }

    private void UpdateLogo()
    {
        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        Logo.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{(dark ? "immurok-white.png" : "immurok.png")}"));
    }

    // ───────────────────────── 状态轮询 ─────────────────────────

    /// <summary>
    /// 快路：STATUS / PAIR:STATUS / PASS:STATUS 都是服务本地应答，每秒一次。
    /// SLOT:STATUS 与 FP:SLOTS 是真实 BLE 往返，放慢路单独跑——绑在一起的话，HID 刚连上、
    /// 服务 GATT 会话还没就绪时，一次慢查询会让蓝牙页卡在「等待连接」（Linux 端踩过）。
    /// </summary>
    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var statusT = AppServices.Pipe.SendAsync(IpcProtocol.Status, QuickTimeoutMs);
            var pairT = AppServices.Pipe.SendAsync($"{IpcProtocol.Pair}:{IpcProtocol.PairStatus}", QuickTimeoutMs);
            var passT = AppServices.Pipe.SendAsync($"{IpcProtocol.Pass}:{IpcProtocol.PassStatus}", QuickTimeoutMs);
            await Task.WhenAll(statusT, pairT, passT);

            string? status = statusT.Result;
            _serviceUp = status is not null;
            string[] sp = status?.Split(IpcProtocol.Sep) ?? Array.Empty<string>();
            bool connected = sp.Length >= 2 && sp[1] == "1";
            _deviceName = sp.Length >= 3 && sp[2].Length > 0 ? sp[2] : "immurok IK-1";

            string? pair = pairT.Result;
            bool paired = pair?.Contains("PAIRED") == true && !pair.Contains("UNPAIRED");

            // 断开后设备侧的东西都要重新问：换了一台设备、或在另一台电脑上被解绑，都只能重连后才知道。
            if (!connected) { _slotValid = null; _fpBitmap = null; _slotFailures = 0; }
            if (paired != _paired) { _slotValid = null; _slotFailures = 0; }
            _connected = connected;
            _paired = paired;
            _passConfigured = passT.Result?.Contains("CONFIGURED") == true;
            _cpRegistered = SystemComponents.IsCredentialProviderRegistered();

            // 蓝牙开关只用于蓝牙页换一句提示，别的页不查。
            _btOn = _ready && CurrentStep == Step.Connect && !connected
                ? await SystemComponents.IsBluetoothOnAsync()
                : null;
        }
        finally { _polling = false; }

        _ = SlowLaneAsync();
    }

    private async Task SlowLaneAsync()
    {
        if (_slowBusy || !_connected) return;
        _slowBusy = true;
        try
        {
            // 本地有钥匙不等于绑定有效：设备上本机所在的活跃槽可能已被清空（另一台电脑点了
            // 「解除另一台」、或设备恢复过出厂）。不复核的话向导会把作废的绑定当成已完成。
            if (_paired && _slotValid is null)
            {
                string? s = await AppServices.Pipe.SendAsync($"{IpcProtocol.Slot}:{IpcProtocol.SlotStatus}", BleTimeoutMs);
                string[] p = s?.Split(IpcProtocol.Sep) ?? Array.Empty<string>();
                if (s?.StartsWith(IpcProtocol.Ok) == true && p.Length >= 4)
                {
                    int.TryParse(p[2], out int bitmap);
                    int.TryParse(p[3], out int active);
                    // OK:<supported>:<bitmap>:<active>；旧固件不支持双主机，无从复核，按有效处理。
                    _slotValid = p[1] != "1" || (active == 1 ? (bitmap & 1) != 0 : active == 2 && (bitmap & 2) != 0);
                }
                // 查询本身一直失败时别把绑定步永远挡住：与设备页一致，退回本地判断。
                else if (++_slotFailures >= 3) _slotValid = true;
            }

            if (_fpBitmap is null && DateTime.UtcNow >= _nextFpQuery)
            {
                string? r = await AppServices.Pipe.SendAsync($"{IpcProtocol.Fp}:{IpcProtocol.FpSlots}", BleTimeoutMs);
                string[] p = r?.Split(IpcProtocol.Sep) ?? Array.Empty<string>();
                if (r?.StartsWith(IpcProtocol.Ok) == true && p.Length >= 2 && int.TryParse(p[1], out int bm))
                    _fpBitmap = bm;
                else
                    _nextFpQuery = DateTime.UtcNow.AddSeconds(5); // 未绑定时设备会拒绝，别每秒打一次 BLE
            }
        }
        finally { _slowBusy = false; }
        if (_ready) Refresh();
    }

    private int FpCount => _fpBitmap is int b ? BitOperations.PopCount((uint)(b & ((1 << AuthSlotCount) - 1))) : 0;

    /// <summary>界面上「已绑定」的判断：设备没连上时无从复核，信本地；连上了就等槽位复核的结论。</summary>
    private bool Linked => _paired && (_slotValid == true || (!_connected && _slotValid is null));

    // ───────────────────────── 步骤表 ─────────────────────────

    private Step CurrentStep => _index >= 0 && _index < _steps.Count ? _steps[_index] : Step.Finish;

    /// <summary>「安装检查」是否插入在向导打开时决定一次，之后步骤列表不变，免得侧栏在操作中途跳动。</summary>
    private void BuildSteps()
    {
        _steps.Clear();
        _steps.Add(Step.Welcome);
        if (!_serviceUp || !_cpRegistered) _steps.Add(Step.Install);
        _steps.AddRange(new[] { Step.Connect, Step.Pair, Step.Password, Step.Fingerprint, Step.Finish });
    }

    private bool IsSatisfied(Step s) => s switch
    {
        Step.Install => _serviceUp && _cpRegistered,
        Step.Connect => _connected,
        Step.Pair => Linked,
        Step.Password => _passConfigured,
        Step.Fingerprint => FpCount > 0,
        _ => true,
    };

    /// <summary>
    /// 未完成也能「跳过此步」的步骤。安装检查只在服务能连上、仅缺 CP 登记时可跳过
    /// （先走完连接 / 绑定，修复靠重跑安装包）；服务都没起来后面每一步都走不通，不可跳过。
    /// </summary>
    private bool IsSkippable(Step s) => s switch
    {
        Step.Password or Step.Fingerprint => true,
        Step.Install => _serviceUp,
        _ => false,
    };

    private static bool IsTask(Step s) => s is not (Step.Welcome or Step.Finish);

    /// <summary>启动时跳到第一个未完成的步骤；全部完成直接到完成页；连蓝牙都没做过才从欢迎页开始。</summary>
    private void JumpToFirstIncomplete()
    {
        int first = _steps.FindIndex(s => IsTask(s) && !IsSatisfied(s));
        if (first < 0) { _index = _steps.Count - 1; return; }
        _index = _steps[first] is Step.Connect or Step.Install ? 0 : first;
    }

    private void GoTo(int index)
    {
        if (index < 0 || index >= _steps.Count || index == _index) return;
        _index = index;
        _message = null;
        _renderKey = null;
        Refresh();
    }

    // ───────────────────────── 渲染 ─────────────────────────

    private void Refresh()
    {
        if (!_ready) return;

        // 自动前进：当前步一满足就走；欢迎页和完成页不自动走；回看时暂停。
        if (!_suppressAutoAdvance && !_busy && IsTask(CurrentStep) && IsSatisfied(CurrentStep)
            && _index < _steps.Count - 1)
        {
            _index++;
            _message = null;
        }

        RenderSidebar();
        RenderFooter();

        // 页面只在它关心的状态变化时重建：每秒重建会让示意图动画不停从头起、按钮焦点丢失。
        // 换步骤时重建正是想要的（动画从头起动）。
        string key = $"{CurrentStep}|{_serviceUp}|{_cpRegistered}|{_connected}|{_deviceName}|{Linked}|" +
                     $"{_passConfigured}|{FpCount}|{_btOn}|{_busy}|{_message}|{_messageOk}";
        if (key == _renderKey) return;
        _renderKey = key;
        StepHost.Content = BuildPage(CurrentStep);
    }

    private void RenderSidebar()
    {
        SidebarPanel.Children.Clear();
        for (int i = 0; i < _steps.Count; i++)
        {
            Step s = _steps[i];
            bool done = IsTask(s) && IsSatisfied(s);
            bool current = i == _index;

            var row = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            FrameworkElement dot = StepDot(done, current);
            row.Children.Add(dot);

            var label = new TextBlock
            {
                Text = T(StepTitleKey(s)),
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(8, 5, 0, 5),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty,
                current ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            int target = i;
            // 点侧栏可回看，往回点等价「上一步」（暂停自动前进）。
            row.MouseLeftButtonUp += (_, _) => { _suppressAutoAdvance = target < _index; GoTo(target); };
            SidebarPanel.Children.Add(row);
        }
    }

    /// <summary>侧栏状态点：完成 = 绿底对勾；当前 = 强调色圆环加实心点；其余 = 灰圈。</summary>
    private static FrameworkElement StepDot(bool done, bool current)
    {
        var g = new Grid { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        if (done)
        {
            g.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) });
            g.Children.Add(new TextBlock
            {
                Text = "✓", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else if (current)
        {
            var ring = new Ellipse { StrokeThickness = 1.5 };
            ring.SetResourceReference(Shape.StrokeProperty, "SystemAccentColorPrimaryBrush");
            var inner = new Ellipse { Width = 6, Height = 6 };
            inner.SetResourceReference(Shape.FillProperty, "SystemAccentColorPrimaryBrush");
            g.Children.Add(ring);
            g.Children.Add(inner);
        }
        else
        {
            var ring = new Ellipse { StrokeThickness = 1.2, Opacity = 0.5 };
            ring.SetResourceReference(Shape.StrokeProperty, "TextFillColorSecondaryBrush");
            g.Children.Add(ring);
        }
        return g;
    }

    private void RenderFooter()
    {
        Step s = CurrentStep;
        LaterBtn.Visibility = s == Step.Finish ? Visibility.Collapsed : Visibility.Visible;
        PrevBtn.Visibility = _index > 0 && s != Step.Finish ? Visibility.Visible : Visibility.Collapsed;
        PrevBtn.IsEnabled = LaterBtn.IsEnabled = NextBtn.IsEnabled = !_busy;

        NextBtn.Visibility = Visibility.Visible;
        NextBtn.IsDefault = true;
        if (s == Step.Finish) { NextBtn.Content = T("wizard.done"); NextBtn.Appearance = ControlAppearance.Primary; }
        else if (IsSatisfied(s)) { NextBtn.Content = T("wizard.next"); NextBtn.Appearance = ControlAppearance.Primary; }
        else if (IsSkippable(s))
        {
            // 可跳过的步骤未完成时，右下角是「跳过此步」而不是「下一步」。
            NextBtn.Content = T("wizard.skip.step");
            NextBtn.Appearance = ControlAppearance.Secondary;
            NextBtn.IsDefault = false;
        }
        else NextBtn.Visibility = Visibility.Collapsed;
    }

    private static string StepTitleKey(Step s) => s switch
    {
        Step.Welcome => "wizard.step.welcome",
        Step.Install => "wizard.step.install",
        Step.Connect => "wizard.step.connect",
        Step.Pair => "wizard.step.pair",
        Step.Password => "wizard.step.password",
        Step.Fingerprint => "wizard.step.fingerprint",
        _ => "wizard.step.finish",
    };

    // ───────────────────────── 底栏动作 ─────────────────────────

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (CurrentStep == Step.Finish) { Finish(); return; }
        _suppressAutoAdvance = false;
        GoTo(_index + 1);
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        _suppressAutoAdvance = true;
        GoTo(_index - 1);
    }

    /// <summary>「稍后再说」视为完成向导：写完成标记，之后启动不再自动弹（服务 / CP 缺失除外）。</summary>
    private void OnLaterClick(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        ClientSettings.SetupCompleted = true;
        Close();
    }

    // ───────────────────────── 页面 ─────────────────────────
    // 每页结构固定：顶部图标 + 标题，中间是图示或一句说明，然后一个主按钮，
    // 完成后按钮换成绿色对勾的状态标签。解释性小字一律不加。

    private UIElement BuildPage(Step s)
    {
        var page = new StackPanel { MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Center };
        switch (s)
        {
            case Step.Welcome: WelcomePage(page); break;
            case Step.Install: InstallPage(page); break;
            case Step.Connect: ConnectPage(page); break;
            case Step.Pair: PairPage(page); break;
            case Step.Password: PasswordPage(page); break;
            case Step.Fingerprint: FingerprintPage(page); break;
            default: FinishPage(page); break;
        }
        if (_message is not null) page.Children.Add(StatusLabel(_messageOk, _message));
        return page;
    }

    private void WelcomePage(StackPanel p)
    {
        p.Children.Add(Header(SymbolRegular.HandWave24, T("wizard.welcome")));
        p.Children.Add(Body(T("wizard.intro")));
        var features = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
        features.Children.Add(FeatureRow(SymbolRegular.LockOpen24, T("wizard.feature.unlock")));
        features.Children.Add(FeatureRow(SymbolRegular.Code24, T("wizard.feature.ssh")));
        features.Children.Add(FeatureRow(SymbolRegular.Password24, T("wizard.feature.inject")));
        p.Children.Add(features);
        p.Children.Add(InfoCard(T("wizard.welcome.needs")));
        p.Children.Add(Body(T("wizard.welcome.time"), small: true));
    }

    private void InstallPage(StackPanel p)
    {
        bool ok = _serviceUp && _cpRegistered;
        p.Children.Add(Header(ok ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24, T("wizard.install.title"),
            ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"));

        if (!_serviceUp)
        {
            p.Children.Add(StatusLabel(false, T("wizard.install.service_down")));
            p.Children.Add(InfoCard(T("wizard.install.service_down.hint")));
            p.Children.Add(PrimaryButton(T("wizard.install.start_service"), SymbolRegular.Play24, !_busy, OnStartServiceClick));
        }
        else if (!_cpRegistered)
        {
            p.Children.Add(StatusLabel(false, T("wizard.install.cp_missing")));
            p.Children.Add(InfoCard(T("wizard.install.cp_missing.hint")));
        }
        else p.Children.Add(StatusLabel(true, T("wizard.install.ok")));
    }

    private void ConnectPage(StackPanel p)
    {
        p.Children.Add(Header(SymbolRegular.Bluetooth24, T("wizard.connect.title")));

        // 这一页只放图、按钮和等待指示，不放解释性小字。
        if (!_serviceUp)
        {
            p.Children.Add(StatusLabel(false, T("wizard.install.service_down")));
        }
        else if (_connected)
        {
            p.Children.Add(StatusLabel(true, T("wizard.connect.connected", _deviceName)));
        }
        else if (_btOn == false)
        {
            p.Children.Add(StatusLabel(false, T("wizard.connect.bt.off")));
            p.Children.Add(PrimaryButton(T("wizard.connect.bt.open"), SymbolRegular.Bluetooth24, true,
                (_, _) => SystemComponents.OpenBluetoothSettings()));
        }
        else
        {
            // 设备以 BLE HID 键盘身份工作，首次必须在 Windows 设置里手动添加一次，App 无法代劳。
            p.Children.Add(Spaced(new DeviceIllustration(DeviceIllustration.Scene.BluetoothConnect)));
            p.Children.Add(PrimaryButton(T("wizard.connect.bt.open"), SymbolRegular.Bluetooth24, true,
                (_, _) => SystemComponents.OpenBluetoothSettings()));
            p.Children.Add(Waiting(T("wizard.connect.searching")));
        }
    }

    private void PairPage(StackPanel p)
    {
        p.Children.Add(Header(SymbolRegular.Link24, T("wizard.pair.title")));
        p.Children.Add(Body(T("wizard.pair.body")));

        if (Linked)
        {
            p.Children.Add(StatusLabel(true, T("wizard.pair.done")));
            return;
        }
        // 这里不画「按按钮」的示意图：点「开始绑定」之前就把图亮出来，用户会先去按设备按钮，
        // 而设备此时根本没在等。图示只在点击之后的绑定引导框里、设备真正等待动作时出现。
        p.Children.Add(PrimaryButton(T("wizard.pair.start"), SymbolRegular.Link24, _connected && !_busy, OnLinkClick));
        if (!_connected) p.Children.Add(StatusLabel(false, T("wizard.pair.need.connect")));
    }

    private void PasswordPage(StackPanel p)
    {
        p.Children.Add(Header(SymbolRegular.Key24, T("wizard.password.title")));
        p.Children.Add(Body(T("wizard.password.body")));
        p.Children.Add(InfoCard(T("wizard.password.privacy")));

        if (_passConfigured)
        {
            p.Children.Add(StatusLabel(true, T("wizard.password.done")));
            return;
        }
        // 写凭据要过设备上的指纹门，未绑定时必然失败，按钮置灰。
        p.Children.Add(PrimaryButton(T("wizard.password.set"), SymbolRegular.Key24, Linked && !_busy, OnSetPasswordClick));
        if (!Linked) p.Children.Add(StatusLabel(false, T("wizard.password.need.pair")));
    }

    private void FingerprintPage(StackPanel p)
    {
        p.Children.Add(Header(SymbolRegular.Fingerprint24, T("wizard.fp.title")));
        p.Children.Add(Body(T("wizard.fp.body")));

        if (FpCount > 0)
        {
            p.Children.Add(StatusLabel(true, T("wizard.fp.done", FpCount)));
            return;
        }
        p.Children.Add(PrimaryButton(T("wizard.fp.start"), SymbolRegular.Fingerprint24,
            _connected && Linked && !_busy, OnEnrollClick));
        if (!_connected) p.Children.Add(StatusLabel(false, T("wizard.pair.need.connect")));
        else if (!Linked) p.Children.Add(StatusLabel(false, T("wizard.password.need.pair")));
        p.Children.Add(InfoCard(T("wizard.fp.hint")));
    }

    private void FinishPage(StackPanel p)
    {
        p.Children.Add(Header(SymbolRegular.CheckmarkCircle24, T("wizard.complete.title"), "SystemFillColorSuccessBrush"));
        p.Children.Add(Body(T("wizard.complete.message")));

        var summary = new StackPanel();
        summary.Children.Add(SummaryRow(T("wizard.step.connect"), _connected));
        summary.Children.Add(SummaryRow(T("wizard.step.pair"), Linked));
        summary.Children.Add(SummaryRow(T("wizard.step.password"), _passConfigured));
        summary.Children.Add(SummaryRow(T("wizard.step.fingerprint"), FpCount > 0));
        summary.Children.Add(SummaryRow(T("wizard.complete.install"), _serviceUp && _cpRegistered));
        p.Children.Add(Card(summary));

        var tryIt = new StackPanel();
        tryIt.Children.Add(new TextBlock { Text = T("wizard.try.title"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        tryIt.Children.Add(FeatureRow(SymbolRegular.LockClosed24, T("wizard.try.unlock"), wrap: true));
        p.Children.Add(Card(tryIt));

        p.Children.Add(Body(T("wizard.tray.note"), small: true));
    }

    // ───────────────────────── 页面动作 ─────────────────────────

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _message = null;
        Refresh();
        try { await action(); }
        finally
        {
            _busy = false;
            await PollAsync();
            Refresh();
        }
    }

    private void SetMessage(bool ok, string? text)
    {
        _messageOk = ok;
        _message = text;
    }

    private async void OnStartServiceClick(object sender, RoutedEventArgs e) => await RunBusyAsync(async () =>
    {
        if (!await SystemComponents.StartServiceElevatedAsync())
        {
            SetMessage(false, T("wizard.install.start_fail"));
            return;
        }
        // 服务起来到管道就绪有一小段时间，等它答上第一句再刷新。
        for (int i = 0; i < 10; i++)
        {
            if (await AppServices.Pipe.SendAsync(IpcProtocol.Status, QuickTimeoutMs) is not null) return;
            await Task.Delay(500);
        }
        SetMessage(false, T("wizard.install.start_fail"));
    });

    /// <summary>与设备页的「绑定这台电脑」是同一个引导框、同一套结果文案。</summary>
    private async void OnLinkClick(object sender, RoutedEventArgs e) => await RunBusyAsync(async () =>
    {
        PairOutcome outcome = await PairDialog.RunAsync(this);
        if (PairDialog.Succeeded(outcome))
        {
            // 刚绑定成功，设备必然认本机，不必再等槽位复核。
            _paired = true;
            _slotValid = true;
            _fpBitmap = null;
            return;
        }
        SetMessage(false, PairDialog.Describe(outcome));
    });

    private async void OnSetPasswordClick(object sender, RoutedEventArgs e) => await RunBusyAsync(() =>
    {
        var (ok, pwd) = PasswordDialog.Show(this, T("wizard.password.set"));
        if (!ok || string.IsNullOrEmpty(pwd)) return Task.CompletedTask;

        // 写凭据要在设备上触摸确认：统一的指纹等待窗（30s 倒计时 + 取消），取消 / 超时返回 null。
        string? r = FpAuthDialog.Run(this, T("msg.fpauth.pass_set"),
            () => AppServices.Pipe.PassSetAsync(Environment.UserName, pwd));
        if (r == IpcProtocol.Ok) _passConfigured = true;
        else SetMessage(false, T("msg.setup.save_fail", r ?? T("msg.setup.pass_cancelled")));
        return Task.CompletedTask;
    });

    private async void OnEnrollClick(object sender, RoutedEventArgs e) => await RunBusyAsync(async () =>
    {
        // 点击时现查一次槽位，不信缓存：缓存可能是几秒前的，录到已占用的槽会被固件拒绝。
        string? r = await AppServices.Pipe.SendAsync($"{IpcProtocol.Fp}:{IpcProtocol.FpSlots}", BleTimeoutMs);
        string[] parts = r?.Split(IpcProtocol.Sep) ?? Array.Empty<string>();
        if (r?.StartsWith(IpcProtocol.Ok) != true || parts.Length < 2 || !int.TryParse(parts[1], out int bitmap))
        {
            SetMessage(false, r?.Contains("NOT_CONNECTED") == true || r is null
                ? T("common.dev_disconnected") : T("common.read_fail", r));
            return;
        }
        _fpBitmap = bitmap;
        if (FpCount > 0) return; // 别处已经录过了

        // 只取认证槽（0–4），不占用第 6 号切换槽。
        byte slot = 0;
        while (slot < AuthSlotCount && (bitmap & (1 << slot)) != 0) slot++;

        string terminal = EnrollDialog.Show(this, slot, T("enroll.title"));
        _fpBitmap = null;
        _nextFpQuery = DateTime.MinValue;
        if (terminal.Contains("COMPLETE"))
        {
            // 立刻拿到新计数，免得页面在「开始录入」上停一秒才跳。
            string? after = await AppServices.Pipe.SendAsync($"{IpcProtocol.Fp}:{IpcProtocol.FpSlots}", BleTimeoutMs);
            string[] ap = after?.Split(IpcProtocol.Sep) ?? Array.Empty<string>();
            if (after?.StartsWith(IpcProtocol.Ok) == true && ap.Length >= 2 && int.TryParse(ap[1], out int nb)) _fpBitmap = nb;
            return;
        }
        SetMessage(false, terminal switch
        {
            var x when x.Contains("TIMEOUT") => T("msg.fp.enroll_timeout"),
            var x when x.Contains("NOT_CONNECTED") => T("common.dev_disconnected"),
            "CANCELLED" => T("msg.enroll.cancelled"),
            _ => T("msg.fp.enroll_fail", terminal),
        });
    });

    // ───────────────────────── 视图小件 ─────────────────────────

    private static UIElement Header(SymbolRegular symbol, string title, string brushKey = "SystemAccentColorPrimaryBrush")
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 16), HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new SymbolIcon { Symbol = symbol };
        icon.SetResourceReference(ForegroundProperty, brushKey);
        // SymbolIcon 在代码里设 FontSize 不放大（控件模板自己定字号），用 Viewbox 缩放到 48。
        sp.Children.Add(new Viewbox { Child = icon, Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) });
        sp.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        return sp;
    }

    private static TextBlock Body(string text, bool small = false)
    {
        var t = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = small ? 12 : 14,
            Margin = new Thickness(0, 0, 0, 16),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    private static UIElement FeatureRow(SymbolRegular symbol, string text, bool wrap = false)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new SymbolIcon { Symbol = symbol, FontSize = 18, VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(ForegroundProperty, "SystemAccentColorPrimaryBrush");
        g.Children.Add(icon);
        var t = new TextBlock { Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    private static UIElement Card(UIElement content) => new Border
    {
        Child = content,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14, 12, 14, 12),
        Margin = new Thickness(0, 0, 0, 16),
        Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
    };

    private static UIElement InfoCard(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return Card(t);
    }

    private static UIElement StatusLabel(bool ok, string text)
    {
        string brushKey = ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush";
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new SymbolIcon
        {
            Symbol = ok ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.ErrorCircle24,
            Filled = ok,
            FontSize = 20,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(ForegroundProperty, brushKey);
        g.Children.Add(icon);
        var t = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 400, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    private static UIElement PrimaryButton(string text, SymbolRegular symbol, bool enabled, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text,
            Icon = new SymbolIcon { Symbol = symbol },
            Appearance = ControlAppearance.Primary,
            IsEnabled = enabled,
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(20, 8, 20, 8),
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 16),
        };
        b.Click += onClick;
        return b;
    }

    private static UIElement Waiting(string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
        sp.Children.Add(new ProgressRing { IsIndeterminate = true, Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0) });
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        sp.Children.Add(t);
        return sp;
    }

    private static UIElement SummaryRow(string text, bool ok)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new SymbolIcon { Symbol = ok ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.DismissCircle24, Filled = ok, FontSize = 18 };
        icon.SetResourceReference(ForegroundProperty, ok ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
        g.Children.Add(icon);
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    private static UIElement Spaced(FrameworkElement e)
    {
        e.Margin = new Thickness(0, 0, 0, 16);
        return e;
    }
}
