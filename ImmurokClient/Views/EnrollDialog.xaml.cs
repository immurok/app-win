using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ImmurokClient.Localization;
using ImmurokClient.Services;
using Wpf.Ui.Controls;

namespace ImmurokClient.Views;

/// <summary>
/// 指纹录入引导窗（对齐 macOS 的录入引导）：指纹示意图 + 按压位置指示 + 抬起手指提醒 + 分段进度。
///
/// <para>设备每次采集都会推 <c>PROGRESS:&lt;Event&gt;:&lt;current&gt;:&lt;total&gt;</c>，
/// 其中 current = 已完成的采集数，因此「下一次该按的位置」的步骤下标就是 current。</para>
/// </summary>
public partial class EnrollDialog : FluentWindow
{
    private static string T(string k, params object[] a) => Loc.Instance.T(k, a);

    /// <summary>该步要按的位置。</summary>
    private enum Dir { Center, Left, Right, Up, Down }

    /// <summary>6 次采集的引导序列：正中 → 左 → 右 → 上 → 下 → 回到正中。</summary>
    private static readonly (string Key, Dir D)[] Steps6 =
    {
        ("center_first", Dir.Center),
        ("left_first",   Dir.Left),
        ("right_first",  Dir.Right),
        ("up",           Dir.Up),
        ("down",         Dir.Down),
        ("center_again", Dir.Center),
    };

    /// <summary>采集次数非 6 时的通用轮转序列。</summary>
    private static readonly (string Key, Dir D)[] StepsRing =
    {
        ("center_first", Dir.Center),
        ("left_first",   Dir.Left),
        ("right_first",  Dir.Right),
        ("up",           Dir.Up),
        ("down",         Dir.Down),
    };

    private readonly byte _slot;
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;
    private bool _closed;

    /// <summary>录入终态帧（OK:COMPLETE / ERROR:* / CANCELLED）。</summary>
    public string Terminal { get; private set; } = "CANCELLED";

    private EnrollDialog(byte slot)
    {
        InitializeComponent();
        _slot = slot;

        // 起始态：等待第一次按压。
        ApplyStep(0, 0);
        HintText.Text = T("msg.enroll.place_finger");
        CountText.Text = "";
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => { _closed = true; if (!_finished) _cts.Cancel(); };
    }

    /// <summary>弹出引导窗并完成一次录入，返回终态帧。</summary>
    public static string Show(System.Windows.Window? owner, byte slot, string title)
    {
        var dlg = new EnrollDialog(slot) { Title = title };
        if (owner is not null) dlg.Owner = owner;
        else dlg.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        dlg.Bar.Title = title;
        dlg.ShowDialog();
        return dlg.Terminal;
    }

    private async System.Threading.Tasks.Task RunAsync()
    {
        StartPulse();
        string terminal;
        try
        {
            terminal = await AppServices.Pipe.EnrollStreamAsync(
                _slot,
                frame => { if (!_closed) Dispatcher.Invoke(() => OnFrame(frame)); },
                _cts.Token);
        }
        catch (OperationCanceledException) { return; }

        // 用户已关窗取消：保持 Terminal = CANCELLED，不要把取消导致的 ERROR 当成失败上报。
        if (_closed) return;

        _finished = true;
        Terminal = terminal;
        StopPulse();

        if (terminal.Contains("COMPLETE")) ShowSuccess();
        else ShowFailure(terminal);
    }

    // ---- 进度帧 ----

    private void OnFrame(string frame)
    {
        // PROGRESS:<Event>:<current>:<total>
        string[] p = frame.Split(':');
        string ev = p.Length > 1 ? p[1] : "";
        int cur = p.Length > 2 && int.TryParse(p[2], out int c) ? c : 0;
        int total = p.Length > 3 && int.TryParse(p[3], out int t) ? t : 0;

        UpdateProgress(cur, total);

        switch (ev)
        {
            case "Captured":
                // 识别成功的这一刻手指还在传感器上 —— 立刻提示抬起，
                // 不要等设备的 LiftFinger（那一帧是"已检测到手指离开"，来得太晚，
                // 会变成用户抬手之后界面才闪一下说"请抬起手指"）。
                SetLiftState(true);
                StepText.Text = T("msg.enroll.lift_finger");
                HintText.Text = T("msg.enroll.captured", cur, total);
                break;

            case "LiftFinger":
                // 设备确认手指已离开 —— 直接给出下一次该按的位置。
                SetLiftState(false);
                ApplyStep(cur, total);
                HintText.Text = T("msg.enroll.place_finger");
                break;

            case "Processing":
                SetLiftState(false);
                StepText.Text = T("msg.enroll.processing");
                HintText.Text = "";
                break;

            case "Overlap":
                SetLiftState(false);
                ApplyStep(cur, total);
                HintText.Text = T("msg.enroll.overlap");
                break;

            case "Waiting":
            default:
                SetLiftState(false);
                ApplyStep(cur, total);
                if (HintText.Text.Length == 0) HintText.Text = T("msg.enroll.place_finger");
                break;
        }
    }

    /// <summary>按步骤下标摆好：阶段名、位置文案、高亮位移、方向箭头。</summary>
    private void ApplyStep(int index, int total)
    {
        var seq = total == Steps6.Length ? Steps6 : StepsRing;
        var (key, dir) = seq[Math.Clamp(index, 0, seq.Length - 1) % seq.Length];

        // 阶段名直接由方向推出：末尾回到正中时也能正确显示，不受序列长度影响。
        PhaseText.Text = dir == Dir.Center ? T("msg.enroll.phase_center") : T("msg.enroll.phase_edge");
        StepText.Text = T("msg.enroll.step_" + key);

        // 高亮圆点平移到该按的位置；箭头绕中心转到对应方位（正中不显示箭头）。
        const double Shift = 38;
        (double x, double y, double angle, bool arrow) = dir switch
        {
            Dir.Left => (-Shift, 0.0, 270.0, true),
            Dir.Right => (Shift, 0.0, 90.0, true),
            Dir.Up => (0.0, -Shift, 0.0, true),
            Dir.Down => (0.0, Shift, 180.0, true),
            _ => (0.0, 0.0, 0.0, false),
        };
        HighlightShift.X = x;
        HighlightShift.Y = y;
        ArrowRotate.Angle = angle;
        ArrowLayer.Visibility = arrow ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>抬起手指态：淡化指纹与高亮，收起方向箭头。</summary>
    private void SetLiftState(bool lifting)
    {
        FpGlyph.Opacity = lifting ? 0.3 : 1.0;
        Highlight.Visibility = lifting ? Visibility.Collapsed : Visibility.Visible;
        if (lifting) ArrowLayer.Visibility = Visibility.Collapsed;
    }

    private void UpdateProgress(int cur, int total)
    {
        if (total <= 0) return;
        CaptureProgress.Value = Math.Clamp(cur * 100.0 / total, 0, 100);
        CountText.Text = T("msg.enroll.count", cur, total);
        BuildDots(cur, total);
    }

    /// <summary>分段圆点：已完成的填实，未完成的留空。</summary>
    private void BuildDots(int done, int total)
    {
        if (Dots.Children.Count != total)
        {
            Dots.Children.Clear();
            for (int i = 0; i < total; i++)
                Dots.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 9,
                    Height = 9,
                    Margin = new Thickness(4, 0, 4, 0),
                    Fill = Brushes.Transparent,
                    StrokeThickness = 1.5,
                });
        }
        // 已完成的点用系统强调色填实（跟随用户在系统里选的颜色），未完成的留空圈。
        for (int i = 0; i < Dots.Children.Count; i++)
        {
            if (Dots.Children[i] is not System.Windows.Shapes.Ellipse e) continue;
            bool on = i < done;
            if (on) e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, Helpers.Ui.Accent);
            else e.Fill = Brushes.Transparent;
            e.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                on ? Helpers.Ui.Accent : Helpers.Ui.ControlStroke);
        }
    }

    // ---- 终态 ----

    private void ShowSuccess()
    {
        Plate.Visibility = Visibility.Visible;
        FpGlyph.Visibility = Visibility.Collapsed;
        Highlight.Visibility = Visibility.Collapsed;
        ArrowLayer.Visibility = Visibility.Collapsed;
        DoneGlyph.Visibility = Visibility.Visible;
        PhaseText.Text = "";
        StepText.Text = T("msg.enroll.success");
        HintText.Text = T("msg.enroll.success_hint");
        CaptureProgress.Value = 100;
        CloseBtn.Content = T("common.ok");
        CloseBtn.Appearance = ControlAppearance.Primary;
    }

    private void ShowFailure(string terminal)
    {
        Highlight.Visibility = Visibility.Collapsed;
        ArrowLayer.Visibility = Visibility.Collapsed;
        FpGlyph.Opacity = 0.3;
        PhaseText.Text = "";
        StepText.Text = T("msg.enroll.failed");
        HintText.Text = terminal.Contains("TIMEOUT") ? T("msg.enroll.failed_timeout")
            : terminal.Contains("NOT_CONNECTED") ? T("common.dev_disconnected")
            : T("msg.enroll.failed_hint");
        CloseBtn.Content = T("common.ok");
    }

    // ---- 高亮呼吸动画（提示"现在该按了"）----

    private void StartPulse()
    {
        var anim = new DoubleAnimation(0.45, 1.0, new Duration(TimeSpan.FromSeconds(0.9)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Highlight.BeginAnimation(OpacityProperty, anim);
    }

    private void StopPulse()
    {
        Highlight.BeginAnimation(OpacityProperty, null);
        Highlight.Opacity = 1.0;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
