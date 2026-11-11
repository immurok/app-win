using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImmurokClient.Controls;
using ImmurokClient.Helpers;
using ImmurokClient.Localization;
using ImmurokClient.Services;
using Wpf.Ui.Controls;
using WpfButton = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock; // 消除与 Wpf.Ui.Controls.TextBlock 的二义

namespace ImmurokClient.Views;

/// <summary>
/// 密码注入页：管理「定位目标应用 + 模板锁定密码框 + 专属密码」的注入项（增删改）。
/// 本迭代只负责配置与捕获；注入引擎与指纹触发在下一步接入。
/// </summary>
public partial class PasswordInjectionPage : Page
{
    private static string T(string k, params object[] a) => Loc.Instance.T(k, a);

    public PasswordInjectionPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Loc.Instance.LanguageChanged -= OnLanguageChanged;
            Loc.Instance.LanguageChanged += OnLanguageChanged;
            RefreshList();
        };
        Unloaded += (_, _) => Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => RefreshList();

    /// <summary>
    /// 内置目标按钮。已经加过的置灰（按身份判重，不看名字），免得点出两条一样的项。
    /// </summary>
    private void RefreshPresets()
    {
        var rows = new System.Collections.Generic.List<UIElement>();
        foreach (var p in InjectionPresets.All)
        {
            bool added = InjectionPresets.AlreadyAdded(p);
            var btn = new WpfButton
            {
                Content = added ? T("inject.presets.added", p.DisplayName) : p.DisplayName,
                Icon = new SymbolIcon { Symbol = added ? SymbolRegular.Checkmark24 : SymbolRegular.Add24 },
                Appearance = ControlAppearance.Secondary,
                Margin = new Thickness(0, 0, 8, 8), // 换行后行间也要留距（ItemsControl 底部用 -8 抵掉最后一行）
                IsEnabled = !added,
            };
            var preset = p;
            btn.Click += (_, _) => AddPreset(preset);
            rows.Add(btn);
        }
        PresetPanel.ItemsSource = rows;
    }

    /// <summary>
    /// 用内置身份预填一条项目，直接打开编辑框——用户只需要填密码。
    /// 不静默落盘：没有密码的注入项什么也做不了，凭空多出一条卡片只会让人困惑。
    /// </summary>
    private void AddPreset(InjectionPreset preset)
    {
        var created = InjectionEditDialog.Show(Window.GetWindow(this), preset.ToItem(), isNew: true);
        if (created is null) return;
        PasswordInjectionStore.AddOrUpdate(created);
        StatusText.Text = T("inject.saved");
        RefreshList();
    }

    private void RefreshList()
    {
        RefreshPresets();
        ListPanel.Items.Clear();
        var items = PasswordInjectionStore.Items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var it in items)
        {
            try { ListPanel.Items.Add(BuildRow(it)); }
            catch { /* 单行渲染失败不影响其它项 */ }
        }
    }

    /// <summary>一条注入项一张设置卡片：名称 + 身份副标题，右侧是（可选的）路径匹配徽标与编辑 / 删除。</summary>
    private UIElement BuildRow(PasswordInjectionItem it)
    {
        string subtitle = it.BestIdentity switch
        {
            AppMatchKind.Package => it.PackageFamilyName,
            AppMatchKind.Signature => it.Publisher,
            AppMatchKind.Path => SafeExeName(it.AppId),
            _ => T("inject.nocapture"),
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // 只有「只能靠 exe 路径认应用」才值得挂警告徽标：升级换目录就失配，而且路径
        // 本身不构成身份主张。包族名/签名匹配都稳，挂个黄标只会制造噪音。
        if (it.BestIdentity == AppMatchKind.Path)
        {
            var badgeText = Ui.Text(T("inject.badge.pathonly"), "CaptionCaution");
            badgeText.TextWrapping = TextWrapping.NoWrap;
            actions.Children.Add(new Border
            {
                Style = Ui.Style("CautionBadge"),
                Margin = new Thickness(0, 0, 8, 0),
                Child = badgeText,
            });
        }

        var editBtn = Ui.IconButton(SymbolRegular.Edit24, T("inject.edit"));
        editBtn.Click += (_, _) => EditItem(it);
        var delBtn = Ui.IconButton(SymbolRegular.Delete24, T("common.delete"));
        delBtn.Click += (_, _) => DeleteItem(it);
        actions.Children.Add(editBtn);
        actions.Children.Add(delBtn);

        return new SettingCard
        {
            Icon = SymbolRegular.Password24,
            Title = it.Name,
            Description = subtitle,
            Action = actions,
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    private static string SafeExeName(string path)
    {
        try { return string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path); }
        catch { return path; }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var created = InjectionEditDialog.Show(Window.GetWindow(this));
        if (created is not null)
        {
            PasswordInjectionStore.AddOrUpdate(created);
            StatusText.Text = T("inject.saved");
            RefreshList();
        }
    }

    private void EditItem(PasswordInjectionItem it)
    {
        var edited = InjectionEditDialog.Show(Window.GetWindow(this), it);
        if (edited is not null)
        {
            PasswordInjectionStore.AddOrUpdate(edited);
            StatusText.Text = T("inject.saved");
            RefreshList();
        }
    }

    private void DeleteItem(PasswordInjectionItem it)
    {
        if (!MessageDialog.Confirm(Window.GetWindow(this), T("inject.del.title"),
                T("inject.del.confirm", it.Name), MessageKind.Danger, T("common.delete")))
            return;

        PasswordInjectionStore.Remove(it.Id);
        StatusText.Text = "";
        RefreshList();
    }
}
