using System.Windows;
using System.Windows.Controls;

namespace ImmurokClient.Controls;

/// <summary>
/// 页面内容容器：统一页边距，内容靠左、宽度随窗口伸缩但不超过 <see cref="MaxContentWidth"/>。
///
/// 不用「StackPanel + MaxWidth + HorizontalAlignment=Left」的原因：靠左对齐的面板宽度取决于
/// 最宽的子元素，内容都很短的页面（状态、功能开关）卡片会缩成一小条，开关也贴不到右边。
/// 这里直接把可用宽度（封顶后）交给子元素，卡片始终铺满。
/// </summary>
public class PageContainer : Decorator
{
    public static readonly DependencyProperty MaxContentWidthProperty = DependencyProperty.Register(
        nameof(MaxContentWidth), typeof(double), typeof(PageContainer),
        new FrameworkPropertyMetadata(760.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(PageContainer),
        new FrameworkPropertyMetadata(new Thickness(36, 24, 36, 32), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MaxContentWidth
    {
        get => (double)GetValue(MaxContentWidthProperty);
        set => SetValue(MaxContentWidthProperty, value);
    }

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    private double ContentWidth(double available)
    {
        Thickness p = Padding;
        double w = double.IsInfinity(available) ? MaxContentWidth : available - p.Left - p.Right;
        return Math.Max(0, Math.Min(w, MaxContentWidth));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        Thickness p = Padding;
        double w = ContentWidth(constraint.Width);
        double h = double.IsInfinity(constraint.Height)
            ? double.PositiveInfinity
            : Math.Max(0, constraint.Height - p.Top - p.Bottom);

        double childH = 0;
        if (Child is { } child)
        {
            child.Measure(new Size(w, h));
            childH = child.DesiredSize.Height;
        }
        return new Size(w + p.Left + p.Right, childH + p.Top + p.Bottom);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Thickness p = Padding;
        if (Child is { } child)
        {
            double w = ContentWidth(arrangeSize.Width);
            double h = Math.Max(child.DesiredSize.Height, arrangeSize.Height - p.Top - p.Bottom);
            child.Arrange(new Rect(p.Left, p.Top, w, Math.Max(0, h)));
        }
        return arrangeSize;
    }
}
