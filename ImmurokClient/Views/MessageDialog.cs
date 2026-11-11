using System.Windows;
using System.Windows.Controls;
using ImmurokClient.Helpers;
using ImmurokClient.Localization;
using Wpf.Ui.Controls;
using WpfButton = Wpf.Ui.Controls.Button;

namespace ImmurokClient.Views;

internal enum MessageKind
{
    /// <summary>说明 / 引导（蓝色信息图标，确定为强调色）。</summary>
    Info,
    /// <summary>需要留意后果的确认（黄色警告图标，确定为强调色）。</summary>
    Warning,
    /// <summary>删除、解绑这类不可撤销的动作（黄色警告图标，确定为红色）。</summary>
    Danger,
}

/// <summary>
/// 与应用同一套外观（Mica + 主题色）的消息 / 确认框，替代 Win32 的 <c>MessageBox</c>——
/// 那个是系统经典样式，不跟深色主题，夹在 Fluent 窗口之间很突兀。
/// 同步模态（ShowDialog），调用点的写法和原来的 MessageBox 一样直白。
/// </summary>
internal static class MessageDialog
{
    /// <summary>确定 / 取消。返回用户是否点了确定。</summary>
    public static bool Confirm(Window? owner, string title, string message,
        MessageKind kind = MessageKind.Warning, string? okText = null)
        => Show(owner, title, message, kind, okText, withCancel: true);

    /// <summary>只有「确定」的提示。</summary>
    public static void Inform(Window? owner, string title, string message, MessageKind kind = MessageKind.Info)
        => Show(owner, title, message, kind, null, withCancel: false);

    private static bool Show(Window? owner, string title, string message, MessageKind kind, string? okText, bool withCancel)
    {
        // 主窗口可能从未显示过（开机自启只驻托盘）：没显示过的窗口不能当 Owner，会抛异常。
        if (owner is not { IsVisible: true }) owner = null;

        var win = new FluentWindow
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ExtendsContentIntoTitleBar = true,
            WindowBackdropType = WindowBackdropType.Mica,
            ShowInTaskbar = false,
            // FluentWindow 的默认样式带 MinWidth=460 / MinHeight=320：内容矮于 320 的小对话框
            // 会在底部多出一截空白，SizeToContent 形同虚设。清掉下限，让窗口贴合内容。
            MinWidth = 0,
            MinHeight = 0,
        };
        if (owner is not null) win.Owner = owner;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var panel = new StackPanel { Margin = new Thickness(24, 8, 24, 24) };
        Grid.SetRow(panel, 1);
        root.Children.Add(panel);

        // 图标 + 正文
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new SymbolIcon
        {
            Symbol = kind == MessageKind.Info ? SymbolRegular.Info24 : SymbolRegular.Warning24,
            Filled = true,
        };
        icon.SetResourceReference(Control.ForegroundProperty, kind == MessageKind.Info ? Ui.Accent : Ui.Caution);
        body.Children.Add(new Viewbox
        {
            Child = icon, Width = 28, Height = 28,
            Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top,
        });

        var text = Ui.Text(message);
        text.LineHeight = 20;
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        body.Children.Add(text);
        panel.Children.Add(body);

        // 按钮：确定在左、取消在右（与应用内其它对话框一致）
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0),
        };
        var okBtn = new WpfButton
        {
            Content = okText ?? Loc.Instance.T("common.ok"),
            Appearance = kind == MessageKind.Danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            MinWidth = 96,
            IsDefault = true,
            // 只有一个按钮时 Esc 也能关
            IsCancel = !withCancel,
        };
        buttons.Children.Add(okBtn);

        bool ok = false;
        okBtn.Click += (_, _) => { ok = true; win.Close(); };

        if (withCancel)
        {
            var cancelBtn = new WpfButton
            {
                Content = Loc.Instance.T("common.cancel"),
                Appearance = ControlAppearance.Secondary,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true,
            };
            cancelBtn.Click += (_, _) => win.Close();
            buttons.Children.Add(cancelBtn);
            // 危险动作默认焦点落在「取消」上：误按回车不至于直接删掉 / 解绑。
            if (kind == MessageKind.Danger)
            {
                okBtn.IsDefault = false;
                win.Loaded += (_, _) => cancelBtn.Focus();
            }
        }
        panel.Children.Add(buttons);

        win.Content = root;
        win.ShowDialog();
        return ok;
    }
}
