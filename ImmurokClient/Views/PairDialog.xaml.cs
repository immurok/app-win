using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ImmurokClient.Localization;
using ImmurokClient.Services;
using ImmurokCommon.Protocol;
using Wpf.Ui.Controls;
// 与 Wpf.Ui.Controls.TextBlock 二义，显式取 WPF 那个（XAML 里用的也是它）。
using TextBlock = System.Windows.Controls.TextBlock;

namespace ImmurokClient.Views;

/// <summary>配对结果。<paramref name="FingerprintDone"/> 用于区分超时卡在哪一步。</summary>
public sealed record PairOutcome(string Terminal, bool FingerprintDone);

/// <summary>
/// 配对引导窗。把「现在该做什么」从设备页的一行小字提升成分步弹窗：
/// 设备已绑另一台主机时固件要求先指纹门、再按键门（hidkbd.c 的 slot2_enroll 分支），
/// 两步之间只有设备的 0x34 通知能说清进度，光靠静态文案用户不知道自己走到哪了。
///
/// <para>步骤推进由 Service 的 PAIR:START 流驱动（源头是设备通知），所以勾是真的
/// 走到了那一步，不是按时间猜的。</para>
///
/// <para>用法：<c>var r = PairDialog.Run(owner, twoStep);</c> —— 弹窗期间跑完整个配对，
/// 终态返回即自动关窗。</para>
/// </summary>
public partial class PairDialog : FluentWindow
{
    private static string T(string k, params object[] a) => Loc.Instance.T(k, a);

    /// <summary>用户主动关窗时的终态串，与 EnrollDialog 的取消约定一致。</summary>
    public const string Cancelled = "CANCELLED";

    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
    private static readonly Brush Done = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush Idle = new SolidColorBrush(Color.FromArgb(0x28, 0x80, 0x80, 0x80));
    private static readonly Brush OnAccent = Brushes.White;

    private enum Stage { WaitFingerprint, WaitButton, Computing }
    private enum StepState { Pending, Active, Done }

    private readonly bool _twoStep;
    private DeviceIllustration? _illustration;
    private DeviceIllustration.Scene _scene;
    private Stage _stage;
    private bool _cancelled;
    private bool _finished;

    private PairDialog(bool twoStep)
    {
        InitializeComponent();
        _twoStep = twoStep;
        _stage = twoStep ? Stage.WaitFingerprint : Stage.WaitButton;

        HeadText.Text = T("pair.dlg.head", twoStep ? 2 : 1);
        Step1Card.Visibility = twoStep ? Visibility.Visible : Visibility.Collapsed;
        Step1Title.Text = T("pair.step.fp");
        Step1Hint.Text = T("pair.step.fp.hint");
        Step2Title.Text = T("pair.step.button");
        Step2Hint.Text = T("pair.step.button.hint");
        // 只有一步时它就是第 1 步，序号跟着改，别让用户对着一个孤零零的「2」发愣。
        Step2Num.Text = twoStep ? "2" : "1";

        StatusText.Text = T("msg.pair.running");
        ApplyStage();
    }

    /// <summary>
    /// 弹窗并跑完配对。返回终态帧（<c>OK:PAIRED</c> / <c>ERROR:*</c>）；
    /// 用户中途关窗返回 <see cref="Cancelled"/>。
    /// </summary>
    public static PairOutcome Run(Window? owner, bool twoStep)
    {
        var dlg = new PairDialog(twoStep);
        if (owner is not null) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        string terminal = Cancelled;
        // 挂 ContentRendered 而不是 Loaded：服务秒回错误时会在 Loaded 里就 Close，
        // WPF-UI TitleBar 随后的 ContentRendered 处理拿到零 Hwnd 抛 ArgumentException。
        dlg.ContentRendered += async (_, _) =>
        {
            // 进度回调来自管道读取线程；用 BeginInvoke 投递，不让读取循环等 UI 排队。
            string r = await AppServices.Pipe.PairStartStreamAsync(
                frame => dlg.Dispatcher.BeginInvoke(new Action(() => dlg.OnProgress(frame))));

            // 用户已经关窗时不要覆盖「取消」，也不要再去动一个关掉的窗口。
            if (dlg._cancelled) return;
            terminal = r;
            dlg._finished = true;
            dlg.Close();
        };
        dlg.ShowDialog();

        return new PairOutcome(
            dlg._cancelled ? Cancelled : terminal,
            // 走到按键步（或更后）就说明指纹那一关过了。
            dlg._stage != Stage.WaitFingerprint);
    }

    /// <summary>
    /// 终态 → 给用户看的一句结果。设备页和设置向导共用，两处同一个按钮、同一个框、同一套文案。
    /// 超时到底卡在哪一步，看设备最后推到的阶段，不靠猜。
    /// </summary>
    public static string Describe(PairOutcome outcome) => outcome.Terminal switch
    {
        var x when x.Contains("PAIRED") => T("msg.pair.ok"),
        Cancelled => T("msg.pair.cancelled"),
        var x when x.Contains("NOSERVICE") => T("msg.pair.noservice"),
        var x when x.Contains("NOT_CONNECTED") => T("msg.pair.notconnected"),
        var x when x.Contains("NEEDSRESET") => T("msg.pair.needsreset"),
        var x when x.Contains("LINKPARAMS") => T("msg.pair.linkparams"),
        var x when x.Contains("WAITBUTTON")
            => T(outcome.FingerprintDone ? "msg.pair.waitbutton" : "msg.pair.waitbutton_fp"),
        // 其余失败附上服务的原始终态（ERROR:GENERIC:<阶段与设备回包> / ERROR:CALLER_NOT_TRUSTED 等），
        // 只写「绑定失败」用户和我们都无从排查。
        _ => $"{T("msg.pair.fail")}（{outcome.Terminal}）",
    };

    public static bool Succeeded(PairOutcome outcome) => outcome.Terminal.Contains("PAIRED");

    /// <summary>
    /// 不带槽位缓存的入口（设置向导用）：先查 SLOT:STATUS 决定画几步，再弹框跑完绑定。
    /// 判断必须在 PAIR:START 之前完成，否则 WaitButton 可能先到、画错分支；
    /// 查不到（旧固件 / 失败）按首次绑定画一步，服务端推来 WaitFingerprint 时引导框会自己补上。
    /// </summary>
    public static async System.Threading.Tasks.Task<PairOutcome> RunAsync(Window? owner)
    {
        bool twoStep = false;
        string? s = await AppServices.Pipe.SlotStatusAsync();
        // OK:<supported>:<bitmap>:<active>
        if (s?.StartsWith(IpcProtocol.Ok) == true)
        {
            string[] p = s.Split(IpcProtocol.Sep);
            twoStep = p.Length >= 4 && p[1] == "1" && int.TryParse(p[2], out int bitmap) && bitmap != 0;
        }
        return Run(owner, twoStep);
    }

    /// <summary>PROGRESS:&lt;stage&gt;[:&lt;remaining&gt;]，由 Service 转发的设备 0x34 通知。</summary>
    private void OnProgress(string frame)
    {
        if (_cancelled) return;
        string[] p = frame.Split(IpcProtocol.Sep);
        if (p.Length < 2) return;

        switch (p[1])
        {
            case nameof(Stage.WaitFingerprint):
                // 服务端的槽位判定比调用方传进来的更新：它说要指纹，就把这一步显出来。
                Step1Card.Visibility = Visibility.Visible;
                Step2Num.Text = "2";
                _stage = Stage.WaitFingerprint;
                StatusText.Text = T("msg.pair.running");
                break;
            case nameof(Stage.WaitButton):
                _stage = Stage.WaitButton;
                StatusText.Text = T("msg.pair.running");
                break;
            case nameof(Stage.Computing):
                _stage = Stage.Computing;
                StatusText.Text = T("msg.pair.computing");
                break;
            case "FingerprintRejected":
                // 阶段不变，设备还在等；只报还能试几次。
                int.TryParse(p.Length >= 3 ? p[2] : "", out int left);
                StatusText.Text = T("msg.pair.fp_rejected", left);
                return;
            default:
                return; // 未知阶段（固件比客户端新）：忽略，别把界面搞乱
        }
        ApplyStage();
    }

    private void ApplyStage()
    {
        // 示意图：换场景时重建（动画从头起），同场景只切定格。交换密钥阶段保留底面图并定格。
        var scene = _stage == Stage.WaitFingerprint
            ? DeviceIllustration.Scene.TouchSensor
            : DeviceIllustration.Scene.PressButton;
        if (_illustration is null || _scene != scene)
        {
            _scene = scene;
            _illustration = new DeviceIllustration(scene);
            IllustrationHost.Content = _illustration;
        }
        _illustration.SetAnimated(_stage != Stage.Computing);
        CancelHint.Visibility = _stage == Stage.WaitButton ? Visibility.Visible : Visibility.Collapsed;

        SetStep(Step1Badge, Step1Num, Step1Title, Step1Hint, "1",
            _stage == Stage.WaitFingerprint ? StepState.Active : StepState.Done);
        SetStep(Step2Badge, Step2Num, Step2Title, Step2Hint, _twoStep ? "2" : "1", _stage switch
        {
            Stage.WaitFingerprint => StepState.Pending,
            Stage.WaitButton => StepState.Active,
            _ => StepState.Done,
        });

        // 按键那一步客户端取消不了（设备侧的放弃只认长按），所以文案要换个说法。
        CloseBtn.Content = T(_stage == Stage.WaitButton ? "pair.dlg.close" : "common.cancel");
    }

    private static void SetStep(
        Border badge, TextBlock num, TextBlock title, TextBlock hint, string number, StepState state)
    {
        switch (state)
        {
            case StepState.Done:
                badge.Background = Done;
                num.Text = "✓";
                num.Foreground = OnAccent;
                badge.BeginAnimation(OpacityProperty, null);
                badge.Opacity = 1;
                title.Opacity = hint.Opacity = 0.55;
                title.FontWeight = FontWeights.Normal;
                break;

            case StepState.Active:
                badge.Background = Accent;
                num.Text = number;
                num.Foreground = OnAccent;
                title.Opacity = 1;
                hint.Opacity = 0.7;
                title.FontWeight = FontWeights.SemiBold;
                // 呼吸：告诉用户「就是现在，去动设备」。
                badge.BeginAnimation(OpacityProperty, new DoubleAnimation(
                    0.45, 1.0, new Duration(TimeSpan.FromSeconds(0.9)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                });
                break;

            default:
                badge.Background = Idle;
                num.Text = number;
                num.ClearValue(TextBlock.ForegroundProperty);
                badge.BeginAnimation(OpacityProperty, null);
                badge.Opacity = 1;
                title.Opacity = hint.Opacity = 0.45;
                title.FontWeight = FontWeights.Normal;
                break;
        }
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_finished) { Close(); return; }

        _cancelled = true;
        CloseBtn.IsEnabled = false;
        _illustration?.SetAnimated(false);
        Step1Badge.BeginAnimation(OpacityProperty, null);
        Step2Badge.BeginAnimation(OpacityProperty, null);

        // 指纹门能真取消（CANCELGATE → 设备停止闪灯等待）；按键门不行——固件那边只有
        // 「在设备上长按 1–3 秒」才会放弃（hidkbd.c:1774）。所以这里尽力发一次，
        // 关窗前把长按这条出路告诉用户，别让人对着还在闪的设备干等到超时。
        StatusText.Text = T("fpauth.cancelling");
        await AppServices.Pipe.CancelGateAsync();
        Close();
    }
}
