using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ImmurokClient.Helpers;
using ImmurokClient.Localization;
using ImmurokClient.Services;
using ImmurokCommon.Protocol;
using Wpf.Ui.Controls;
using WpfButton = Wpf.Ui.Controls.Button;

namespace ImmurokClient.Views;

public partial class KeysPage : Page
{
    private static string T(string k, params object[] a) => Loc.Instance.T(k, a);

    private const byte CatSsh = 0, CatOtp = 1, CatApi = 2;

    /// <summary>TOTP 周期（秒）。验证码显示到当前周期结束为止——过期的码留在屏幕上只会误导。</summary>
    private const int TotpPeriodSeconds = 30;

    private bool _busy;

    // 管理模式：只有点了页头「管理」才显示每行的复选框 / 重命名 / 删除（_manageOnly）；
    // 同时收起各行的主操作按钮（取码 / 公钥 / 复制，_normalOnly）和页头的「刷新」。
    private bool _manage;
    private readonly List<UIElement> _manageOnly = new();
    private readonly List<UIElement> _normalOnly = new();

    // 批量删除选择集（跨类别，按 类+索引 标识）
    private readonly HashSet<(byte cat, byte idx)> _selected = new();

    private record KeyEntry(byte Index, string Name, string Extra);

    public KeysPage()
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

    private async void OnLanguageChanged()
    {
        ManageBtn.Content = T(_manage ? "keys.manage_done" : "keys.manage");
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _manageOnly.Clear();
        _normalOnly.Clear();
        _selected.Clear();
        UpdateDelSelectedBtn();
        await PairBanner.RefreshAsync();
        await LoadCategoryAsync(CatOtp, OtpPanel, OtpEmpty);
        await LoadCategoryAsync(CatSsh, SshPanel, SshEmpty);
        await LoadCategoryAsync(CatApi, ApiPanel, ApiEmpty);
    }

    private async Task LoadCategoryAsync(byte cat, ItemsControl panel, System.Windows.Controls.TextBlock empty)
    {
        string? r = await AppServices.Pipe.KeyListAsync(cat);
        var rows = new List<UIElement>();
        foreach (var e in ParseList(r)) rows.Add(BuildRow(cat, e));
        panel.ItemsSource = rows;

        // 列表为空要分清「真的没有」和「读不到」：设备断开 / 服务没起来时显示「无」是在说谎。
        empty.Text = r is null ? T("common.no_service_resp")
            : r.StartsWith(IpcProtocol.Ok) ? T("common.none")
            : r.Contains("NOT_CONNECTED") ? T("common.dev_disconnected")
            : T("common.read_fail", r);
        empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static List<KeyEntry> ParseList(string? r)
    {
        var list = new List<KeyEntry>();
        if (r is null || !r.StartsWith(IpcProtocol.Ok)) return list;
        // OK:<idx,b64name,b64extra>;...
        int c = r.IndexOf(IpcProtocol.Sep);
        string payload = c >= 0 ? r[(c + 1)..] : "";
        if (payload.Length == 0) return list;
        foreach (string ent in payload.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = ent.Split(',');
            if (f.Length < 2 || !byte.TryParse(f[0], out byte idx)) continue;
            string name = FromB64(f[1]);
            string extra = f.Length > 2 ? FromB64(f[2]) : "";
            list.Add(new KeyEntry(idx, name, extra));
        }
        return list;
    }

    // ---- 结果条 ----

    /// <summary>页头下方的结果条。成功绿、失败红；空消息即收起。</summary>
    private void Notify(string message, bool ok = true)
    {
        ResultBar.Message = message;
        ResultBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ResultBar.IsOpen = !string.IsNullOrEmpty(message);
    }

    // ---- 行 ----

    /// <summary>
    /// 一行一个条目：[复选框] 名称 + 附加信息 …… [主操作] [重命名] [删除]。
    /// 主操作（取码 / 复制公钥 / 复制 API 值）是文字按钮；重命名 / 删除是次要操作，收成图标按钮。
    /// 复选框、重命名、删除只在管理模式下显示（<see cref="_manageOnly"/>）。
    /// </summary>
    private UIElement BuildRow(byte cat, KeyEntry e)
    {
        Visibility manageVis = _manage ? Visibility.Visible : Visibility.Collapsed;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // 复选框
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 名称
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // 操作

        // 选择复选框（批量删除）
        var cb = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 0,
            IsChecked = _selected.Contains((cat, e.Index)),
            Visibility = manageVis,
        };
        _manageOnly.Add(cb);
        System.Windows.Automation.AutomationProperties.SetName(cb, e.Name);
        cb.Checked += (_, _) => { _selected.Add((cat, e.Index)); UpdateDelSelectedBtn(); };
        cb.Unchecked += (_, _) => { _selected.Remove((cat, e.Index)); UpdateDelSelectedBtn(); };
        Grid.SetColumn(cb, 0);
        grid.Children.Add(cb);

        // 名称为主、附加信息（服务名等）降一级灰度；过长时各自省略，不把操作按钮顶出去。
        var label = new Grid { VerticalAlignment = VerticalAlignment.Center };
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var nameText = Ui.Text(e.Name);
        nameText.TextWrapping = TextWrapping.NoWrap;
        nameText.TextTrimming = TextTrimming.CharacterEllipsis;
        label.Children.Add(nameText);
        if (!string.IsNullOrEmpty(e.Extra))
        {
            var extraText = Ui.Text(e.Extra, "BodySecondary");
            extraText.TextWrapping = TextWrapping.NoWrap;
            extraText.TextTrimming = TextTrimming.CharacterEllipsis;
            extraText.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(extraText, 1);
            label.Children.Add(extraText);
        }
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(actions, 2);

        if (cat == CatOtp) AddOtpActions(actions, e);
        else if (cat == CatSsh) AddSshActions(actions, e);
        else if (cat == CatApi) AddApiActions(actions, e);

        var editBtn = Ui.IconButton(SymbolRegular.Edit24, T("keys.rename"));
        editBtn.Click += async (_, _) => await RenameAsync(cat, e);
        editBtn.Visibility = manageVis;
        _manageOnly.Add(editBtn);
        actions.Children.Add(editBtn);

        var delBtn = Ui.IconButton(SymbolRegular.Delete24, T("common.delete"));
        delBtn.Click += async (_, _) => await DeleteAsync(cat, e);
        delBtn.Visibility = manageVis;
        _manageOnly.Add(delBtn);
        actions.Children.Add(delBtn);

        grid.Children.Add(actions);
        return Ui.Row(grid);
    }

    /// <summary>
    /// TOTP 行：取码按钮 + 取到后行内显示的验证码（等宽、强调色、三位一组）。
    /// 验证码只显示到它所在的 TOTP 周期结束：数字右侧有个圆形倒计时，到点清掉行内数字和圆环，
    /// 结果条里那条带码的提示也一并收起。
    /// </summary>
    private void AddOtpActions(StackPanel actions, KeyEntry e)
    {
        var codeText = Ui.Text("", "Subtitle");
        codeText.FontSize = 16;
        codeText.TextWrapping = TextWrapping.NoWrap;
        codeText.VerticalAlignment = VerticalAlignment.Center;
        codeText.Margin = new Thickness(0, 0, 8, 0);
        codeText.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        codeText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");

        // 验证码右侧的圆形倒计时：底环 + 随剩余时间缩短的弧（12 点方向起、顺时针）。
        const double ringSize = 18, ringStroke = 2.5, ringRadius = (ringSize - ringStroke) / 2;
        var ringTrack = new System.Windows.Shapes.Ellipse { StrokeThickness = ringStroke };
        ringTrack.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "ControlStrokeColorDefaultBrush");
        var ringArc = new System.Windows.Shapes.Path
        {
            StrokeThickness = ringStroke,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
        };
        ringArc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentTextFillColorPrimaryBrush");
        var ring = new Grid
        {
            Width = ringSize,
            Height = ringSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Visibility = Visibility.Collapsed,
        };
        ring.Children.Add(ringTrack);
        ring.Children.Add(ringArc);

        void SetRing(double fraction)
        {
            // 整圆用 ArcSegment 画不出来（起点 = 终点），封顶到略小于一圈。
            double angle = Math.Clamp(fraction, 0, 0.9999) * 2 * Math.PI;
            double cx = ringSize / 2, cy = ringSize / 2;
            var end = new Point(cx + ringRadius * Math.Sin(angle), cy - ringRadius * Math.Cos(angle));
            var fig = new System.Windows.Media.PathFigure { StartPoint = new Point(cx, cy - ringRadius) };
            fig.Segments.Add(new System.Windows.Media.ArcSegment(end, new Size(ringRadius, ringRadius), 0,
                angle > Math.PI, System.Windows.Media.SweepDirection.Clockwise, true));
            var geo = new System.Windows.Media.PathGeometry();
            geo.Figures.Add(fig);
            ringArc.Data = geo;
        }

        string? shownNotice = null;
        long expiresAtMs = 0;
        long periodMs = TotpPeriodSeconds * 1000L;
        var clearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        clearTimer.Tick += (_, _) =>
        {
            long remainMs = expiresAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (remainMs > 0)
            {
                SetRing((double)remainMs / periodMs);
                return;
            }
            clearTimer.Stop();
            codeText.Text = "";
            ring.Visibility = Visibility.Collapsed;
            // 结果条还停在这条带验证码的提示上才收；期间有了别的提示就不动它。
            if (shownNotice is not null && ResultBar.Message == shownNotice) ResultBar.IsOpen = false;
            shownNotice = null;
        };
        // 页面刷新 / 离开后这一行已不在界面上，别让计时器空转。
        ring.Unloaded += (_, _) => clearTimer.Stop();

        var codeBtn = new WpfButton
        {
            Content = T("msg.keys.getcode"),
            MinWidth = 72,
            Margin = new Thickness(0, 0, 8, 0),
            Appearance = ControlAppearance.Secondary,
        };
        // 认证窗内部同步阻塞（ShowDialog）；async 只为先过一道未配对拦截。
        codeBtn.Click += async (_, _) =>
        {
            if (!await PairingGuard.EnsurePairedAsync(Window.GetWindow(this))) return;
            string? cr = FpAuthDialog.Run(Window.GetWindow(this),
                T("msg.fpauth.otp_code", e.Name),
                () => AppServices.Pipe.KeyOtpAsync(e.Index));
            if (cr is null || !cr.StartsWith(IpcProtocol.Ok))
            {
                Notify(T("msg.keys.getcode_fail"), ok: false);
                return;
            }

            string code = cr[(cr.IndexOf(IpcProtocol.Sep) + 1)..];
            codeText.Text = GroupDigits(code);
            // 距当前周期结束还剩多久就显示多久（周期中途取的码可能只剩几秒有效）；圆环同步走。
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            expiresAtMs = nowMs - nowMs % periodMs + periodMs;
            SetRing((double)(expiresAtMs - nowMs) / periodMs);
            ring.Visibility = Visibility.Visible;
            clearTimer.Stop();
            clearTimer.Start();

            string msg = T("msg.keys.otp_code", e.Name, code);
            try { Clipboard.SetText(code); msg += T("common.copied_suffix"); } catch { }
            Notify(msg);
            shownNotice = msg;
        };
        actions.Children.Add(codeText);
        actions.Children.Add(ring);
        AddNormalOnly(actions, codeBtn);
    }

    /// <summary>SSH 行：复制公钥（authorized_keys 格式）到剪贴板。</summary>
    private void AddSshActions(StackPanel actions, KeyEntry e)
    {
        var pubBtn = new WpfButton
        {
            Content = T("msg.keys.pubkey"),
            Icon = new SymbolIcon { Symbol = SymbolRegular.Copy24 },
            MinWidth = 72,
            Margin = new Thickness(0, 0, 8, 0),
            Appearance = ControlAppearance.Secondary,
        };
        pubBtn.Click += async (_, _) =>
        {
            if (!await PairingGuard.EnsurePairedAsync(Window.GetWindow(this))) return;
            string? pr = await AppServices.Pipe.KeySshPubAsync(e.Index);
            // OK:<b64authkey>:<b64fp>
            string[] pp = pr?.Split(IpcProtocol.Sep) ?? Array.Empty<string>();
            if (pr is null || !pr.StartsWith(IpcProtocol.Ok) || pp.Length < 3)
            {
                Notify(T("msg.keys.pub_fail"), ok: false);
                return;
            }
            try { Clipboard.SetText(FromB64(pp[1])); } catch { }
            Notify(T("msg.keys.ssh_pub", e.Name, FromB64(pp[2])));
        };
        AddNormalOnly(actions, pubBtn);
    }

    /// <summary>
    /// API 行：复制值到剪贴板（过指纹门）。和 SSH 行的「公钥」一样只复制、不在界面上显示。
    /// </summary>
    private void AddApiActions(StackPanel actions, KeyEntry e)
    {
        var copyBtn = new WpfButton
        {
            Content = T("msg.keys.copy"),
            Icon = new SymbolIcon { Symbol = SymbolRegular.Copy24 },
            MinWidth = 72,
            Margin = new Thickness(0, 0, 8, 0),
            Appearance = ControlAppearance.Secondary,
        };
        // 认证窗内部同步阻塞（ShowDialog）；async 只为先过一道未配对拦截。
        copyBtn.Click += async (_, _) =>
        {
            if (!await PairingGuard.EnsurePairedAsync(Window.GetWindow(this))) return;
            string? r = FpAuthDialog.Run(Window.GetWindow(this),
                T("msg.fpauth.api_read", e.Name),
                () => AppServices.Pipe.KeyApiAsync(e.Index));
            if (r is null || !r.StartsWith(IpcProtocol.Ok + IpcProtocol.Sep))
            {
                Notify(T("msg.keys.api_read_fail"), ok: false);
                return;
            }
            try
            {
                Clipboard.SetText(FromB64(r[(r.IndexOf(IpcProtocol.Sep) + 1)..]));
                Notify(T("msg.keys.api_copied", e.Name));
            }
            catch { Notify(T("msg.keys.api_read_fail"), ok: false); }
        };
        AddNormalOnly(actions, copyBtn);
    }

    // ---- 管理模式 ----

    /// <summary>加入一个只在非管理模式下显示的主操作按钮。</summary>
    private void AddNormalOnly(StackPanel actions, UIElement el)
    {
        el.Visibility = _manage ? Visibility.Collapsed : Visibility.Visible;
        _normalOnly.Add(el);
        actions.Children.Add(el);
    }

    private void OnManageClick(object sender, RoutedEventArgs e)
    {
        _manage = !_manage;
        ManageBtn.Content = T(_manage ? "keys.manage_done" : "keys.manage");
        ManageBtn.Appearance = _manage ? ControlAppearance.Primary : ControlAppearance.Secondary;
        foreach (UIElement el in _manageOnly)
        {
            el.Visibility = _manage ? Visibility.Visible : Visibility.Collapsed;
            // 退出管理模式即放弃勾选：看不见的复选框不该还留着待删除的选择。
            if (!_manage && el is CheckBox cb) cb.IsChecked = false;
        }
        foreach (UIElement el in _normalOnly)
            el.Visibility = _manage ? Visibility.Collapsed : Visibility.Visible;
        RefreshBtn.Visibility = _manage ? Visibility.Collapsed : Visibility.Visible;
        if (!_manage) _selected.Clear();
        UpdateDelSelectedBtn();
    }

    /// <summary>6 位码显示成「123 456」，8 位「1234 5678」；其它长度原样。</summary>
    private static string GroupDigits(string code) => code.Length switch
    {
        6 => code[..3] + " " + code[3..],
        8 => code[..4] + " " + code[4..],
        _ => code,
    };

    /// <summary>批量删除按钮只在有勾选时出现，文案带上已选数量。</summary>
    private void UpdateDelSelectedBtn()
    {
        DelSelectedBtn.Visibility = _manage && _selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DelSelectedBtn.Content = $"{T("keys.delete_selected")} ({_selected.Count})";
        DelSelectedBtn.IsEnabled = !_busy;
    }

    // ---- 重命名 ----

    private async Task RenameAsync(byte cat, KeyEntry e)
    {
        if (_busy) return;
        if (!await PairingGuard.EnsurePairedAsync(Window.GetWindow(this))) return;
        bool isOtp = cat == CatOtp;

        var fields = new List<FormField> { new(T("ph.name"), e.Name) };
        if (isOtp) fields.Add(new FormField(T("ph.service"), e.Extra, Required: false));
        string[]? v = FormDialog.Show(Window.GetWindow(this), T("keys.rename_title"), fields,
            requiredMessage: T("msg.keys.need_name"));
        if (v is null) return;

        string name = v[0].Trim();
        string svc = isOtp ? v[1].Trim() : "";
        _busy = true;
        UpdateDelSelectedBtn();
        try
        {
            string? r = FpAuthDialog.Run(Window.GetWindow(this),
                T("msg.fpauth.rename", e.Name),
                () => AppServices.Pipe.KeyUpdateAsync(cat, e.Index, name, svc));
            if (r == IpcProtocol.Ok) Notify(T("keys.rename_ok", name));
            else Notify(T("keys.rename_fail"), ok: false);
            await RefreshAsync();
        }
        finally { _busy = false; UpdateDelSelectedBtn(); }
    }

    // ---- 删除 ----

    private async Task DeleteAsync(byte cat, KeyEntry e)
    {
        if (_busy) return;
        if (!await PairingGuard.EnsurePairedAsync(Window.GetWindow(this))) return;
        if (!MessageDialog.Confirm(Window.GetWindow(this), T("msg.keys.del_title"),
                T("msg.keys.del_confirm", e.Name), MessageKind.Danger, T("common.delete")))
            return;

        _busy = true;
        UpdateDelSelectedBtn();
        try
        {
            string? r = FpAuthDialog.Run(Window.GetWindow(this),
                T("msg.fpauth.key_delete", e.Name),
                () => AppServices.Pipe.KeyDeleteAsync(cat, e.Index));
            if (r == IpcProtocol.Ok) Notify(T("msg.keys.del_ok", e.Name));
            else Notify(T("msg.keys.del_fail"), ok: false);
            await RefreshAsync();
        }
        finally { _busy = false; UpdateDelSelectedBtn(); }
    }

    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!await PairingGuard.EnsurePairedAsync(Window.GetWindow(this))) return;
        var sel = _selected.ToList();
        if (sel.Count == 0) { Notify(T("keys.none_selected"), ok: false); return; }
        if (!MessageDialog.Confirm(Window.GetWindow(this), T("msg.keys.del_title"),
                T("keys.del_sel_confirm", sel.Count), MessageKind.Danger, T("common.delete")))
            return;

        _busy = true;
        UpdateDelSelectedBtn();
        try
        {
            int total = sel.Count;
            // 每删一项设备都要单独过一次指纹门，因此用一个认证窗贯穿全程，
            // 每项开始时重置倒计时（step 回调），而不是弹 N 个窗。
            int done = FpAuthDialog.RunSteps(Window.GetWindow(this),
                T("msg.fpauth.key_delete_batch", 1, total),
                async step =>
                {
                    int n = 0, i = 0;
                    // 同类内按索引降序删（固件 swap-delete：删高索引不影响低索引）。
                    foreach (var grp in sel.GroupBy(s => s.cat))
                    {
                        foreach (var (cat, idx) in grp.OrderByDescending(s => s.idx))
                        {
                            step(T("msg.fpauth.key_delete_batch", ++i, total));
                            string? r = await AppServices.Pipe.KeyDeleteAsync(cat, idx);
                            if (r == IpcProtocol.Ok) n++;
                        }
                    }
                    return n;
                });
            _selected.Clear();
            // 一项都没删掉（取消 / 超时）或只删了一部分，都不算成功。
            Notify(T("keys.del_sel_done", done), ok: done == total);
            await RefreshAsync();
        }
        finally { _busy = false; UpdateDelSelectedBtn(); }
    }

    private static string FromB64(string s)
    {
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return s; }
    }

    // ---- 添加 / 生成（对话框里填，密钥字段掩码输入）----

    private async void OnAddOtpClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!await PairingGuard.EnsurePairedAsync(Window.GetWindow(this))) return;

        string[]? v = FormDialog.Show(Window.GetWindow(this), T("keys.totp.add"),
            new[]
            {
                new FormField(T("ph.name")),
                new FormField(T("ph.service"), Required: false),
                new FormField(T("ph.secret"), Secret: true),
            },
            okText: T("common.add"), requiredMessage: T("msg.keys.need_name_secret"));
        if (v is null) return;

        string name = v[0].Trim(), svc = v[1].Trim(), secret = v[2].Trim();
        _busy = true;
        UpdateDelSelectedBtn();
        try
        {
            string? r = FpAuthDialog.Run(Window.GetWindow(this),
                T("msg.fpauth.key_add", name),
                () => AppServices.Pipe.KeyAddOtpAsync(name, svc, secret));
            if (r == IpcProtocol.Ok)
            {
                Notify(T("msg.keys.add_totp_ok", name));
                await RefreshAsync();
            }
            else Notify(T("msg.keys.add_totp_fail"), ok: false);
        }
        finally { _busy = false; UpdateDelSelectedBtn(); }
    }

    private async void OnAddApiClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!await PairingGuard.EnsurePairedAsync(Window.GetWindow(this))) return;

        string[]? v = FormDialog.Show(Window.GetWindow(this), T("keys.api.add"),
            new[]
            {
                new FormField(T("ph.name")),
                new FormField(T("ph.value"), Secret: true),
            },
            okText: T("common.add"), requiredMessage: T("msg.keys.need_name_value"));
        if (v is null) return;

        string name = v[0].Trim(), value = v[1]; // 值不 Trim：首尾空白也可能是密钥的一部分
        _busy = true;
        UpdateDelSelectedBtn();
        try
        {
            string? r = FpAuthDialog.Run(Window.GetWindow(this),
                T("msg.fpauth.key_add", name),
                () => AppServices.Pipe.KeyAddApiAsync(name, value));
            if (r == IpcProtocol.Ok)
            {
                Notify(T("msg.keys.add_api_ok", name));
                await RefreshAsync();
            }
            else Notify(T("msg.keys.add_fail"), ok: false);
        }
        finally { _busy = false; UpdateDelSelectedBtn(); }
    }

    private async void OnGenSshClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!await PairingGuard.EnsurePairedAsync(Window.GetWindow(this))) return;

        string[]? v = FormDialog.Show(Window.GetWindow(this), T("keys.ssh"),
            new[] { new FormField(T("ph.name")) },
            okText: T("common.generate"), description: T("keys.ssh.gen"),
            requiredMessage: T("msg.keys.need_name"));
        if (v is null) return;

        string name = v[0].Trim();
        _busy = true;
        UpdateDelSelectedBtn();
        try
        {
            string? r = FpAuthDialog.Run(Window.GetWindow(this),
                T("msg.fpauth.ssh_gen", name),
                () => AppServices.Pipe.KeySshGenAsync(name));
            if (r is not null && r.StartsWith(IpcProtocol.Ok))
            {
                // OK:<b64authkey>:<b64fp>；公钥顺手放进剪贴板，文案里已说明。
                string[] pp = r.Split(IpcProtocol.Sep);
                string fp = pp.Length >= 3 ? FromB64(pp[2]) : "";
                if (pp.Length >= 2)
                {
                    try { Clipboard.SetText(FromB64(pp[1])); } catch { }
                }
                Notify(T("msg.keys.gen_ok", name, fp));
                await RefreshAsync();
            }
            else Notify(T("msg.keys.gen_fail"), ok: false);
        }
        finally { _busy = false; UpdateDelSelectedBtn(); }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
}
