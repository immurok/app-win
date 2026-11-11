using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace ImmurokClient.Controls;

/// <summary>
/// Windows 11「设置」风格的卡片：左侧图标 + 标题 / 说明，右侧一个操作控件（开关、按钮、下拉框），
/// 下方可选一块正文（列表、表单、提示）。外观模板在 Themes/Immurok.xaml，颜色全部取主题画刷，
/// 所以深浅色、高对比度、系统强调色变化都不用各页自己管。
///
/// 正文不自带上边距：内容整体 Collapsed 时不能留一段空白，由正文根元素自己写 Margin。
/// </summary>
public class SettingCard : ContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(SettingCard), new PropertyMetadata(SymbolRegular.Empty));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingCard), new PropertyMetadata(null));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata(null));

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(object), typeof(SettingCard), new PropertyMetadata(null, OnActionChanged));

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>标题行右侧的操作控件。</summary>
    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    // 挂进逻辑树：右侧控件里的 DynamicResource / 继承属性要能沿逻辑树找到页面。
    private static void OnActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (SettingCard)d;
        if (e.OldValue is DependencyObject) card.RemoveLogicalChild(e.OldValue);
        if (e.NewValue is DependencyObject) card.AddLogicalChild(e.NewValue);
    }
}
