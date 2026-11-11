using System.Windows;
using System.Windows.Controls;
using ImmurokClient.Localization;
using Wpf.Ui.Controls;
using WpfButton = Wpf.Ui.Controls.Button;

namespace ImmurokClient.Views;

/// <summary>
/// 代码构建的登录密码输入对话框：两个掩码字段（输入 + 确认），一致且非空才允许确定。
/// 纯代码，随应用主题渲染（与 FormDialog 同一套外观）。
/// </summary>
internal static class PasswordDialog
{
    /// <summary>返回 (是否确定, 密码)。取消或校验失败返回 (false, "")。</summary>
    public static (bool ok, string password) Show(Window? owner, string title)
    {
        var win = new FluentWindow
        {
            Title = title,
            Width = 400,
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

        panel.Children.Add(Helpers.Ui.Text(Loc.Instance.T("dialog.pass.enter"), "FieldLabel"));
        // WPF-UI 的 PasswordBox：与其它输入框同一套外观，自带「显示密码」按钮。
        var pb1 = new Wpf.Ui.Controls.PasswordBox();
        panel.Children.Add(pb1);

        var confirmLabel = Helpers.Ui.Text(Loc.Instance.T("dialog.pass.confirm"), "FieldLabel");
        confirmLabel.Margin = new Thickness(0, 16, 0, 4);
        panel.Children.Add(confirmLabel);
        var pb2 = new Wpf.Ui.Controls.PasswordBox();
        panel.Children.Add(pb2);

        var errText = Helpers.Ui.Text("", "CaptionCritical");
        errText.Margin = new Thickness(0, 8, 0, 0);
        errText.Visibility = Visibility.Collapsed;
        panel.Children.Add(errText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0),
        };
        var okBtn = new WpfButton
        {
            Content = Loc.Instance.T("common.ok"),
            Appearance = ControlAppearance.Primary,
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        var cancelBtn = new WpfButton
        {
            Content = Loc.Instance.T("common.cancel"),
            Appearance = ControlAppearance.Secondary,
            MinWidth = 96,
            IsCancel = true,
        };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);

        bool ok = false;
        string result = "";
        okBtn.Click += (_, _) =>
        {
            string p1 = pb1.Password;
            string p2 = pb2.Password;
            if (string.IsNullOrEmpty(p1))
            {
                errText.Text = Loc.Instance.T("dialog.pass.empty");
                errText.Visibility = Visibility.Visible;
                return;
            }
            if (p1 != p2)
            {
                errText.Text = Loc.Instance.T("dialog.pass.mismatch");
                errText.Visibility = Visibility.Visible;
                return;
            }
            ok = true;
            result = p1;
            win.Close();
        };
        cancelBtn.Click += (_, _) => { ok = false; win.Close(); };

        win.Content = root;
        win.Loaded += (_, _) => pb1.Focus();
        win.ShowDialog();

        return (ok, result);
    }
}
