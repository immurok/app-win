using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImmurokClient.Localization;

namespace ImmurokClient.Views;

/// <summary>
/// 设置向导与绑定引导框里的设备示意图（移植自 macOS DeviceIllustration / Linux device_illustration.rs，
/// 规范见 docs/setup-wizard-flow.md「设备示意图规范」）。
///
/// <para>为什么有它：引导框写「按底部按钮」，用户按的却是顶面的指纹传感器，然后反馈「无法绑定」。
/// 文字解决不了，一张底面图加按钮上的扩散光环可以。底面视角与用户手册的部件图一致，
/// 用户把实物翻过来就能一一对上。</para>
///
/// <para>全部用 DrawingContext 画，不放位图：颜色取主题前景与强调色，深浅色自动跟随。
/// 文字不画进画布，而是叠在上面的真 TextBlock，跟随系统字体与语言。画布固定 340×190，
/// 向导页和 460 宽的引导框都放得下。</para>
/// </summary>
public sealed class DeviceIllustration : Grid
{
    public const double CanvasW = 340;
    public const double CanvasH = 190;

    public enum Scene
    {
        /// <summary>Windows「添加设备」窗口简笔画 + 设备顶面，下边缘蓝灯慢闪。</summary>
        BluetoothConnect,
        /// <summary>设备底面，蓝色光环从按钮向外扩散。</summary>
        PressButton,
        /// <summary>设备顶面，手指按在传感器上（第二台主机绑定的第一步）。</summary>
        TouchSensor,
    }

    private readonly Surface _surface;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    public DeviceIllustration(Scene scene, bool animated = true)
    {
        Width = CanvasW;
        Height = CanvasH;
        HorizontalAlignment = HorizontalAlignment.Center;
        _surface = new Surface(scene) { Animated = animated };
        Children.Add(_surface);

        var labels = new Canvas();
        Children.Add(labels);
        switch (scene)
        {
            case Scene.BluetoothConnect:
                Put(labels, Caption(Loc.Instance.T("illus.bt.title")), 18, 60);
                Put(labels, Caption("immurok IK-1"), 26, 102);
                break;
            case Scene.PressButton:
                Put(labels, Instruction(Loc.Instance.T("illus.button.flip")), 20, 162);
                break;
            case Scene.TouchSensor:
                Put(labels, Instruction(Loc.Instance.T("illus.fp.touch")), 20, 170);
                break;
        }

        _timer.Tick += (_, _) => { if (_surface.Animated) _surface.InvalidateVisual(); };
        // 动画只在可见期间跑；Unloaded 自动停，关掉的窗口不会拖着定时器。
        // 起动放到下一个 dispatcher 轮次（与 macOS 同样的处理）：在 Loaded 里当场起动，
        // 会把窗口呈现时的位移一起算进第一帧。
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsLoaded) return;
            _surface.Clock.Restart();
            _timer.Start();
        }));
        Unloaded += (_, _) => { _timer.Stop(); _surface.Clock.Reset(); };
    }

    /// <summary>冻结或恢复循环动画（设备忙于交换密钥时定格）。</summary>
    public void SetAnimated(bool on)
    {
        _surface.Animated = on;
        _surface.InvalidateVisual();
    }

    private static void Put(Canvas c, UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        c.Children.Add(e);
    }

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return t;
    }

    /// <summary>图内只留一行动作指令，不加解释性标注。</summary>
    private static TextBlock Instruction(string text)
    {
        var t = new TextBlock
        {
            Text = text,
            Width = CanvasW - 40,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return t;
    }

    /// <summary>真正的画布。只负责几何，文字由外层叠加。</summary>
    private sealed class Surface : FrameworkElement
    {
        private readonly Scene _scene;
        public bool Animated;
        public readonly Stopwatch Clock = new();

        public Surface(Scene scene)
        {
            _scene = scene;
            Width = CanvasW;
            Height = CanvasH;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var pal = Palette.Of(this);
            var ph = Phases.At(Clock.Elapsed.TotalSeconds, Animated);
            switch (_scene)
            {
                case Scene.BluetoothConnect:
                    BluetoothWindowMock(dc, pal, 6, 44, 190, 96);
                    Arrow(dc, pal, new Point(204, 92), new Point(232, 92));
                    TopFace(dc, pal, 288, 88, 96, glow: null, led: ph.LedOn);
                    break;
                case Scene.PressButton:
                    // 不画手指：手指会挡住正要指给用户看的按钮。
                    BottomFace(dc, pal, 170, 84, 140, Animated ? ph.Ripple : 0);
                    break;
                case Scene.TouchSensor:
                {
                    // 手指从上方落下，指腹中心对准传感器；按压循环沿手指方向推移几像素。
                    double dy = 2 - 7 * ph.Pressed;
                    Geometry finger = FingerBody(170, 94 + dy, 64);
                    // 手指要「盖住」下面的线条：先挖掉手指区域再画设备，
                    // 不依赖背景色（Mica 窗口背景是透明的，填背景色会露馅）。
                    dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                        new RectangleGeometry(new Rect(0, 0, CanvasW, CanvasH)), finger));
                    TopFace(dc, pal, 170, 100, 140, glow: Color.FromRgb(0x33, 0xB8, 0x5C), led: null);
                    dc.Pop();
                    FingerOutline(dc, pal, 170, 94 + dy, 64);
                    break;
                }
            }
        }
    }

    // ── 动画相位 ────────────────────────────────────────────────────────

    private readonly record struct Phases(double Ripple, double Pressed, bool LedOn)
    {
        public static Phases At(double t, bool animated)
        {
            if (!animated) return new Phases(0, 0, true);
            double r = t % 1.4 / 1.4;
            double ripple = 1 - (1 - r) * (1 - r); // ease-out：扩散并淡出，循环不往返
            double pressed = 0.5 - 0.5 * Math.Cos(r * 2 * Math.PI);
            bool led = t % 1.8 < 0.9;
            return new Phases(ripple, pressed, led);
        }
    }

    // ── 颜色 ────────────────────────────────────────────────────────────

    private sealed class Palette
    {
        public Color Fg;
        public Color Accent;

        public static Palette Of(FrameworkElement e) => new()
        {
            Fg = ColorOf(e, "TextFillColorPrimaryBrush", Colors.White),
            Accent = ColorOf(e, "SystemAccentColorPrimaryBrush", Color.FromRgb(0x00, 0x78, 0xD4)),
        };

        private static Color ColorOf(FrameworkElement e, string key, Color fallback)
            => e.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

        public Brush FgBrush(double alpha = 1) => Brush(Fg, alpha);
        public Brush AccentBrush(double alpha = 1) => Brush(Accent, alpha);
        /// <summary>macOS 图里的 secondary：前景约 55%。</summary>
        public Brush Secondary => Brush(Fg, 0.55);

        public static Brush Brush(Color c, double alpha)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(c.A * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        public static Pen Pen(Brush b, double w)
        {
            var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            p.Freeze();
            return p;
        }
    }

    // ── 部件 ────────────────────────────────────────────────────────────

    // 底面各部件按边长的比例放置，且每个部件必须落在包住它的轮廓里：
    // 按钮 0.25+0.055、拨键 0.24+0.065 都在 0.34 的中央圆盖内；螺丝 0.36+0.03 在 0.44 的内圈内。
    // （macOS 早期用过 0.27 / r=0.075 的按钮，真机屏幕上捅出了圆盖。）
    private const double ButtonOffset = 0.25;
    private const double SwitchOffset = 0.24;
    private const double ButtonDiameter = 0.11;
    private const double CoverRadius = 0.34;
    private const double ScrewOffset = 0.36;
    private const double ScrewRadius = 0.03;

    /// <summary>
    /// 底面（手册部件图视角）：圆角方形外壳、内圈、四颗螺丝、中央圆盖、上方电源拨键、下方圆形按钮。
    /// <paramref name="ripple"/> 为 0 时保留一圈静态光环，定格的图也照样指着按钮。
    /// </summary>
    private static void BottomFace(DrawingContext dc, Palette pal, double cx, double cy, double s, double ripple)
    {
        double r = s * 0.22, inset = s * 0.06;
        double x = cx - s / 2, y = cy - s / 2;
        Pen outline = Palette.Pen(pal.FgBrush(), 2);
        Pen thin = Palette.Pen(pal.Secondary, 1.2);

        dc.DrawRoundedRectangle(null, outline, new Rect(x, y, s, s), r, r);
        double ir = r - inset * 0.8;
        dc.DrawRoundedRectangle(null, thin, new Rect(x + inset, y + inset, s - 2 * inset, s - 2 * inset), ir, ir);

        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
            dc.DrawEllipse(null, thin, new Point(cx + sx * s * ScrewOffset, cy + sy * s * ScrewOffset), s * ScrewRadius, s * ScrewRadius);

        dc.DrawEllipse(null, thin, new Point(cx, cy), s * CoverRadius, s * CoverRadius);

        // 电源拨键（上）：胶囊轮廓 + 实心拨钮。
        double sw = s * 0.07, sh = s * 0.13;
        dc.DrawRoundedRectangle(null, thin, new Rect(cx - sw / 2, cy - s * SwitchOffset - sh / 2, sw, sh), sw / 2, sw / 2);
        dc.DrawEllipse(pal.Secondary, null, new Point(cx, cy - s * SwitchOffset - s * 0.03), s * 0.0225, s * 0.0225);

        // 按钮（下）：光环向外扩散并淡出。
        var bc = new Point(cx, cy + s * ButtonOffset);
        double br = s * ButtonDiameter / 2;
        double halo = br * (1 + ripple * 2.2);
        dc.DrawEllipse(null, Palette.Pen(pal.AccentBrush((1 - ripple) * 0.9), 2), bc, halo, halo);
        dc.DrawEllipse(pal.AccentBrush(0.25), outline, bc, br, br);
    }

    /// <summary>
    /// 顶面：圆角方形外壳 + 传感器双圈。<paramref name="glow"/> 给传感器上色（等指纹时为绿）；
    /// <paramref name="led"/> 画下边缘外侧的指示灯，亮或暗。
    /// </summary>
    private static void TopFace(DrawingContext dc, Palette pal, double cx, double cy, double s, Color? glow, bool? led)
    {
        double r = s * 0.24, sensor = s * 0.32;
        dc.DrawRoundedRectangle(null, Palette.Pen(pal.FgBrush(), 2), new Rect(cx - s / 2, cy - s / 2, s, s), r, r);

        if (glow is Color g)
            dc.DrawEllipse(Palette.Brush(g, 0.25), null, new Point(cx, cy), sensor * 0.7, sensor * 0.7);
        dc.DrawEllipse(null, Palette.Pen(pal.FgBrush(), 1.6), new Point(cx, cy), sensor / 2, sensor / 2);
        dc.DrawEllipse(null, Palette.Pen(pal.Secondary, 1.2), new Point(cx, cy), sensor * 0.36, sensor * 0.36);

        if (led is bool on)
        {
            double w = s * 0.14, h = s * 0.05;
            dc.DrawRoundedRectangle(Palette.Brush(Color.FromRgb(0x00, 0x7A, 0xFF), on ? 1 : 0.15), null,
                new Rect(cx - w / 2, cy + s * 0.5 + s * 0.07 - h / 2, w, h), h / 2, h / 2);
        }
    }

    /// <summary>指尖朝下的手指轮廓（闭合，用作遮挡区域），指腹中心在 (cx, padY)，上端伸出画面。</summary>
    private static Geometry FingerBody(double cx, double padY, double length)
    {
        double half = length * 0.42 / 2, top = padY - length;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx - half, top), true, true);
            c.LineTo(new Point(cx - half, padY), true, false);
            c.ArcTo(new Point(cx + half, padY), new Size(half, half), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(cx + half, top), true, false);
        }
        g.Freeze();
        return g;
    }

    private static void FingerOutline(DrawingContext dc, Palette pal, double cx, double padY, double length)
    {
        double w = length * 0.42, half = w / 2, top = padY - length;
        // 开口在上方：两条边 + 圆指尖，不画顶边。
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx - half, top), false, false);
            c.LineTo(new Point(cx - half, padY), true, false);
            c.ArcTo(new Point(cx + half, padY), new Size(half, half), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(cx + half, top), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, Palette.Pen(pal.FgBrush(), 2), g);

        // 指甲：指尖内一道浅弧（20°→160°）。
        double nr = w * 0.3, ny = padY - w * 0.12;
        Point P(double deg) => new(cx + nr * Math.Cos(deg * Math.PI / 180), ny + nr * Math.Sin(deg * Math.PI / 180));
        var nail = new StreamGeometry();
        using (var c = nail.Open())
        {
            c.BeginFigure(P(20), false, false);
            c.ArcTo(P(160), new Size(nr, nr), 0, false, SweepDirection.Clockwise, true, false);
        }
        nail.Freeze();
        dc.DrawGeometry(null, Palette.Pen(pal.Secondary, 1.2), nail);
    }

    /// <summary>
    /// Windows「添加设备」窗口简笔画：右上角三个窗口按钮、标题（文字由外层放置）、
    /// 一行强调色描边的设备条目，和一个指向它的鼠标指针。
    /// </summary>
    private static void BluetoothWindowMock(DrawingContext dc, Palette pal, double x, double y, double w, double h)
    {
        dc.DrawRoundedRectangle(null, Palette.Pen(pal.FgBrush(), 2), new Rect(x, y, w, h), 8, 8);

        // 最小化 / 最大化 / 关闭：Windows 的按钮在右上角。
        Pen sec = Palette.Pen(pal.Secondary, 1.2);
        double by = y + 10;
        dc.DrawLine(sec, new Point(x + w - 50, by + 3), new Point(x + w - 42, by + 3));
        dc.DrawRectangle(null, sec, new Rect(x + w - 34, by - 1, 8, 8));
        dc.DrawLine(sec, new Point(x + w - 18, by - 1), new Point(x + w - 10, by + 7));
        dc.DrawLine(sec, new Point(x + w - 10, by - 1), new Point(x + w - 18, by + 7));

        // 设备条目：强调色描边 = 就点这一行。
        var row = new Rect(x + 10, y + 51, w - 20, 30);
        dc.DrawRoundedRectangle(pal.AccentBrush(0.12), Palette.Pen(pal.AccentBrush(), 1.5), row, 6, 6);

        // 鼠标指针，落在条目右侧。
        double px = x + w - 46, py = y + 62;
        var cursor = new StreamGeometry();
        using (var c = cursor.Open())
        {
            c.BeginFigure(new Point(px, py), true, true);
            c.LineTo(new Point(px, py + 16), true, false);
            c.LineTo(new Point(px + 4.5, py + 12), true, false);
            c.LineTo(new Point(px + 7.5, py + 18.5), true, false);
            c.LineTo(new Point(px + 10, py + 17.5), true, false);
            c.LineTo(new Point(px + 7, py + 11), true, false);
            c.LineTo(new Point(px + 12, py + 11), true, false);
        }
        cursor.Freeze();
        dc.DrawGeometry(pal.FgBrush(), Palette.Pen(pal.AccentBrush(), 1), cursor);
    }

    private static void Arrow(DrawingContext dc, Palette pal, Point from, Point to)
    {
        Pen p = Palette.Pen(pal.Secondary, 1.5);
        dc.DrawLine(p, from, to);
        dc.DrawLine(p, new Point(to.X - 7, to.Y - 6), to);
        dc.DrawLine(p, new Point(to.X - 7, to.Y + 6), to);
    }
}
