using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ImmurokClient.Helpers;

/// <summary>
/// 后台代码拼界面时的小工具：样式 / 画刷一律从 Themes/Immurok.xaml 与 WPF-UI 主题取，
/// 不在代码里写色值和字号（否则浅色主题、系统强调色都跟不上）。
/// </summary>
internal static class Ui
{
    // 主题画刷键（WPF-UI 提供，随深浅色切换）
    public const string Primary = "TextFillColorPrimaryBrush";
    public const string Secondary = "TextFillColorSecondaryBrush";
    public const string Tertiary = "TextFillColorTertiaryBrush";
    public const string Accent = "SystemAccentColorPrimaryBrush";
    public const string OnAccent = "TextOnAccentFillColorPrimaryBrush";
    public const string Success = "SystemFillColorSuccessBrush";
    public const string Caution = "SystemFillColorCautionBrush";
    public const string Critical = "SystemFillColorCriticalBrush";
    public const string SuccessBackground = "SystemFillColorSuccessBackgroundBrush";
    public const string CautionBackground = "SystemFillColorCautionBackgroundBrush";
    public const string SubtleFill = "SubtleFillColorSecondaryBrush";
    public const string ControlStroke = "ControlStrongStrokeColorDefaultBrush";

    /// <summary>取 Themes/Immurok.xaml 里的具名样式。</summary>
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    /// <summary>带样式的文本。</summary>
    public static TextBlock Text(string text, string styleKey = "Body") =>
        new() { Text = text, Style = Style(styleKey) };

    /// <summary>卡片内的一行容器（Themes/Immurok.xaml 的 ListRow）。</summary>
    public static Border Row(UIElement child) => new() { Style = Style("ListRow"), Child = child };

    /// <summary>
    /// 行内的纯图标按钮（重命名、删除这类次要操作）：平时不抢视线，含义放在 ToolTip 和
    /// 无障碍名称里——读屏与 UIA 自动化靠后者认按钮。
    /// </summary>
    public static Button IconButton(SymbolRegular symbol, string tooltip)
    {
        var b = new Button
        {
            Icon = new SymbolIcon { Symbol = symbol },
            Appearance = ControlAppearance.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent, // 平时无边框，悬停才有底色
            ToolTip = tooltip,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, tooltip);
        return b;
    }

    /// <summary>状态图标：成功 = 实心绿勾，失败 = 红叉。</summary>
    public static SymbolIcon StatusIcon(bool ok)
    {
        var icon = new SymbolIcon
        {
            Symbol = ok ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.DismissCircle24,
            Filled = true,
        };
        icon.SetResourceReference(Control.ForegroundProperty, ok ? Success : Critical);
        return icon;
    }
}
