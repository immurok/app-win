using System.Windows;
using System.Windows.Controls;
using ImmurokClient.Helpers;
using ImmurokClient.Localization;
using Wpf.Ui.Controls;
using WpfButton = Wpf.Ui.Controls.Button;
using WpfTextBox = Wpf.Ui.Controls.TextBox;

namespace ImmurokClient.Views;

/// <summary>表单对话框的一个字段。</summary>
/// <param name="Label">字段上方的标签。</param>
/// <param name="Initial">初始值。</param>
/// <param name="Secret">密钥 / 口令类：掩码输入，带「显示」按钮。</param>
/// <param name="Required">必填；为空时不让确定。</param>
internal sealed record FormField(string Label, string Initial = "", bool Secret = false, bool Required = true);

/// <summary>
/// 代码构建的模态表单：若干文本 / 掩码字段 + 确定 / 取消。密钥库的添加、生成、重命名都走它
/// （取代只支持两个明文字段的 InputDialog——那个窗口还漏了 <c>win.Content</c>，弹出来是空的）。
/// </summary>
internal static class FormDialog
{
    /// <summary>
    /// 返回各字段的值（与 <paramref name="fields"/> 同序）；取消返回 null。
    /// 必填字段为空时在窗内显示 <paramref name="requiredMessage"/> 并把焦点放回去，不关窗。
    /// </summary>
    public static string[]? Show(Window? owner, string title, IReadOnlyList<FormField> fields,
        string? okText = null, string? description = null, string? requiredMessage = null)
    {
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
            // FluentWindow 默认样式带 MinWidth=460 / MinHeight=320，小对话框底部会多一截空白。
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

        if (!string.IsNullOrEmpty(description))
        {
            var d = Ui.Text(description, "BodySecondary");
            d.Margin = new Thickness(0, 0, 0, 16);
            panel.Children.Add(d);
        }

        // 每个字段：标签 + 输入框。统一用取值 / 聚焦两个委托，屏蔽 TextBox 与 PasswordBox 的差异。
        var getters = new List<Func<string>>();
        var focusers = new List<Action>();
        for (int i = 0; i < fields.Count; i++)
        {
            FormField f = fields[i];
            var label = Ui.Text(f.Label, "FieldLabel");
            if (i > 0) label.Margin = new Thickness(0, 16, 0, 4);
            panel.Children.Add(label);

            if (f.Secret)
            {
                var pb = new Wpf.Ui.Controls.PasswordBox { Password = f.Initial };
                System.Windows.Automation.AutomationProperties.SetName(pb, f.Label);
                panel.Children.Add(pb);
                getters.Add(() => pb.Password);
                focusers.Add(() => pb.Focus());
            }
            else
            {
                var tb = new WpfTextBox { Text = f.Initial };
                System.Windows.Automation.AutomationProperties.SetName(tb, f.Label);
                panel.Children.Add(tb);
                getters.Add(() => tb.Text);
                focusers.Add(() => { tb.Focus(); tb.SelectAll(); });
            }
        }

        var err = Ui.Text("", "CaptionCritical");
        err.Margin = new Thickness(0, 8, 0, 0);
        err.Visibility = Visibility.Collapsed;
        panel.Children.Add(err);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0),
        };
        var okBtn = new WpfButton
        {
            Content = okText ?? Loc.Instance.T("common.ok"),
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

        string[]? result = null;
        okBtn.Click += (_, _) =>
        {
            var values = getters.Select(g => g()).ToArray();
            for (int i = 0; i < fields.Count; i++)
            {
                if (!fields[i].Required || !string.IsNullOrWhiteSpace(values[i])) continue;
                err.Text = requiredMessage ?? fields[i].Label;
                err.Visibility = Visibility.Visible;
                focusers[i]();
                return;
            }
            result = values;
            win.Close();
        };
        cancelBtn.Click += (_, _) => win.Close();

        win.Content = root;
        win.Loaded += (_, _) => { if (focusers.Count > 0) focusers[0](); };
        win.ShowDialog();
        return result;
    }
}
