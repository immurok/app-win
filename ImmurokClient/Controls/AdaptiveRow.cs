using System.Windows;
using System.Windows.Controls;

namespace ImmurokClient.Controls;

/// <summary>
/// 「主内容 + 右侧操作」的自适应行：宽度够时操作控件贴右、主内容占剩余宽度；
/// 主内容会被压到 <see cref="MinMainWidth"/> 以下时，操作控件折到主内容下面、靠左。
///
/// 用在页头（标题 + 刷新 / 添加按钮）、设置卡片（标题说明 + 按钮 / 开关）、添加表单
/// （输入框 + 添加按钮）。窗口最窄 720、又是德语 / 俄语这类长文案时，固定的左右两栏会把
/// 标题挤成一个词一行，这个面板就是为那种情况准备的。
///
/// 约定：Children[0] 是主内容，Children[1]（可选）是操作控件；多余的子元素不参与布局。
/// </summary>
public class AdaptiveRow : Panel
{
    public static readonly DependencyProperty MinMainWidthProperty = DependencyProperty.Register(
        nameof(MinMainWidth), typeof(double), typeof(AdaptiveRow),
        new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(AdaptiveRow),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty StackedGapProperty = DependencyProperty.Register(
        nameof(StackedGap), typeof(double), typeof(AdaptiveRow),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TrailingAlignmentProperty = DependencyProperty.Register(
        nameof(TrailingAlignment), typeof(VerticalAlignment), typeof(AdaptiveRow),
        new FrameworkPropertyMetadata(VerticalAlignment.Center, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>并排时主内容至少要有的宽度；不够就改成上下排。</summary>
    public double MinMainWidth
    {
        get => (double)GetValue(MinMainWidthProperty);
        set => SetValue(MinMainWidthProperty, value);
    }

    /// <summary>并排时两者的间距。</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>上下排时两者的间距。</summary>
    public double StackedGap
    {
        get => (double)GetValue(StackedGapProperty);
        set => SetValue(StackedGapProperty, value);
    }

    /// <summary>并排时操作控件的垂直对齐（卡片里居中，页头里靠上对齐标题）。</summary>
    public VerticalAlignment TrailingAlignment
    {
        get => (VerticalAlignment)GetValue(TrailingAlignmentProperty);
        set => SetValue(TrailingAlignmentProperty, value);
    }

    private bool _stacked;

    private UIElement? Main => InternalChildren.Count > 0 ? InternalChildren[0] : null;
    private UIElement? Trailing => InternalChildren.Count > 1 ? InternalChildren[1] : null;

    protected override Size MeasureOverride(Size available)
    {
        UIElement? main = Main, trailing = Trailing;
        if (main is null) return default;

        var unbounded = new Size(double.PositiveInfinity, available.Height);
        trailing?.Measure(unbounded);
        double tw = trailing?.DesiredSize.Width ?? 0;

        // 没有操作控件（或它整体 Collapsed）：主内容独占一行。
        if (trailing is null || tw <= 0)
        {
            _stacked = false;
            main.Measure(available);
            return main.DesiredSize;
        }

        _stacked = !double.IsInfinity(available.Width) && available.Width - tw - Gap < MinMainWidth;

        if (_stacked)
        {
            trailing.Measure(available); // 换到整行宽度下重新量，过长的可以自己收缩
            main.Measure(available);
            return new Size(
                Math.Max(main.DesiredSize.Width, trailing.DesiredSize.Width),
                main.DesiredSize.Height + StackedGap + trailing.DesiredSize.Height);
        }

        double mainWidth = double.IsInfinity(available.Width)
            ? double.PositiveInfinity
            : Math.Max(0, available.Width - tw - Gap);
        main.Measure(new Size(mainWidth, available.Height));
        return new Size(
            main.DesiredSize.Width + Gap + tw,
            Math.Max(main.DesiredSize.Height, trailing.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        UIElement? main = Main, trailing = Trailing;
        if (main is null) return final;

        double tw = trailing?.DesiredSize.Width ?? 0;
        double th = trailing?.DesiredSize.Height ?? 0;

        if (trailing is null || tw <= 0)
        {
            main.Arrange(new Rect(0, 0, final.Width, final.Height));
            trailing?.Arrange(new Rect(0, 0, 0, 0));
            return final;
        }

        if (_stacked)
        {
            double mh = main.DesiredSize.Height;
            main.Arrange(new Rect(0, 0, final.Width, mh));
            trailing.Arrange(new Rect(0, mh + StackedGap, Math.Min(tw, final.Width), th));
            return final;
        }

        tw = Math.Min(tw, final.Width);
        double y = TrailingAlignment switch
        {
            VerticalAlignment.Top => 0,
            VerticalAlignment.Bottom => final.Height - th,
            _ => (final.Height - th) / 2,
        };
        main.Arrange(new Rect(0, 0, Math.Max(0, final.Width - tw - Gap), final.Height));
        trailing.Arrange(new Rect(final.Width - tw, Math.Max(0, y), tw, th));
        return final;
    }
}
