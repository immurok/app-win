using System.Windows;
using System.Windows.Controls;
using ImmurokClient.Helpers;
using ImmurokClient.Localization;
using ImmurokClient.Services;
using ImmurokCommon.Protocol;

namespace ImmurokClient.Views;

public partial class StatusPage : Page
{
    private static string T(string k, params object[] a) => Loc.Instance.T(k, a);

    public StatusPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            Loc.Instance.LanguageChanged -= OnLanguageChanged;
            Loc.Instance.LanguageChanged += OnLanguageChanged;
            await RefreshAsync();
        };
        Unloaded += (_, _) => Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private async void OnLanguageChanged() => await RefreshAsync();

    private async Task RefreshAsync()
    {
        ChecklistPanel.Children.Clear();

        string? status = await AppServices.Pipe.StatusAsync();
        bool serviceUp = status is not null;
        bool connected = status?.Split(IpcProtocol.Sep) is { Length: >= 2 } p && p[1] == "1";

        string? pair = await AppServices.Pipe.PairStatusAsync();
        bool paired = pair?.Contains("PAIRED") == true && pair.Contains("UNPAIRED") != true;

        string? pass = await AppServices.Pipe.PassStatusAsync();
        bool passConfigured = pass?.Contains("CONFIGURED") == true;

        bool cpInstalled = IsCredentialProviderInstalled();

        AddItem(T("msg.status.service"), serviceUp);
        AddItem(T("msg.status.connected"), connected);
        AddItem(T("msg.status.paired"), paired);
        AddItem(T("msg.status.pass"), passConfigured);
        AddItem(T("msg.status.cp"), cpInstalled, cpInstalled ? null : T("msg.status.cp_hint"));

        // IPC 加固各项（设计稿 §4）：与顶部横幅同一数据源。老版本服务不认这条命令时不显示。
        var sec = SecurityStatusInfo.Parse(await AppServices.Pipe.SecurityStatusAsync());
        if (sec is not null)
        {
            foreach (var item in sec.Items)
            {
                string? hint = item.Ok ? (item.HintKey is { } h ? T(h) : null)
                                       : (item.BannerKey is { } b ? T(b) : null);
                AddItem(T(item.LabelKey), item.Ok, hint);
            }
        }
    }

    /// <summary>读注册表判断 CP 是否已注册（只读 HKLM，无需管理员）。</summary>
    private static bool IsCredentialProviderInstalled()
    {
        const string clsid = "{C433E3A5-FA34-42B1-A94C-5D793D2EEA83}";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\" + clsid);
            return key is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 清单的一行：状态图标 + 名称，提示另起一行、自动换行（以前拼在名称后面不换行，
    /// 长提示在窄窗口里直接被裁掉）。行与行之间画分隔线。
    /// </summary>
    private void AddItem(string label, bool ok, string? hint = null)
    {
        if (ChecklistPanel.Children.Count > 0)
            ChecklistPanel.Children.Add(new Border { Style = Ui.Style("Divider"), Margin = new Thickness(0) });

        var g = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        g.Children.Add(new Viewbox
        {
            Child = Ui.StatusIcon(ok), Width = 20, Height = 20,
            Margin = new Thickness(2, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(label));
        if (hint is not null)
        {
            var h = Ui.Text(hint, ok ? "Caption" : "CaptionCaution");
            h.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(h);
        }
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        ChecklistPanel.Children.Add(g);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
}
