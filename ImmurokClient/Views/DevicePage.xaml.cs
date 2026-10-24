using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ImmurokClient.Localization;
using ImmurokClient.Services;
using ImmurokCommon.Protocol;

namespace ImmurokClient.Views;

public partial class DevicePage : Page
{
    private static string T(string k, params object[] a) => Loc.Instance.T(k, a);

    private byte _activeSlot;
    private bool _connected;

    /// <summary>INFO 里的固件版本，决定点版本行时发不发 CONNPARAMS（1.7.2 起才有）。</summary>
    private string? _firmware;
    private bool _readingConnParams;

    /// <summary>本地是否存着 shared_key（PAIR:STATUS 的答案）。只代表「我们这边有一把钥匙」。</summary>
    private bool _paired;

    /// <summary>
    /// 这把钥匙设备还认不认。本地有 shared_key 不等于配对有效：设备上本机所在的活跃槽
    /// 可能已经被清空（另一台主机点了「解绑另一台」、或设备被复位过），这时本地那把钥匙
    /// 已经作废。界面上一切「是否已配对」的判断都要用这个，而不是 <see cref="_paired"/>——
    /// 否则本机会永远显示「已配对」、连配对按钮都不给，彻底卡死。
    /// 设备不在线时无从复核，退回等于 <see cref="_paired"/>。
    /// </summary>
    private bool _bindingValid;

    // 配对提示要分两种：设备一个槽都没占 → 只按物理键；已被另一台占了一个槽 → 固件先挂
    // 指纹门再挂按键门（hidkbd.c 的 slot2_enroll 分支）。两条路径给 app 的响应都是
    // WAIT_BUTTON，分不出来，只能靠槽位状态判断，所以在这里记下来。
    // 读不到槽位（旧固件/查询失败）时 _slotsKnown 为 false，退回不区分的合并文案。
    private bool _slotsKnown;
    private bool _deviceBound;

    /// <summary>
    /// 由 <see cref="MainWindow.GoToPairing"/> 置位：本次进入设备页是「未配对引导」带过来的，
    /// 加载完成后把焦点落到配对按钮上。页面实例会被 NavigationView 缓存复用，故用静态标志传递。
    /// </summary>
    public static bool FocusPairingOnLoad;

    public DevicePage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            Loc.Instance.LanguageChanged -= OnLanguageChanged;
            Loc.Instance.LanguageChanged += OnLanguageChanged;
            await RefreshAsync();
            if (FocusPairingOnLoad)
            {
                FocusPairingOnLoad = false;
                PairBtn.BringIntoView();
                PairBtn.Focus();
            }
        };
        Unloaded += (_, _) => Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private async void OnLanguageChanged() => await RefreshAsync();

    private async Task RefreshAsync()
    {
        // 上一次操作的结果不跨刷新保留（PairAsync 会在刷新之后重新写上自己的结果）。
        SetPairResult("");

        // 连接状态
        string? status = await AppServices.Pipe.StatusAsync();
        bool connected = false;
        if (status is null)
        {
            ConnText.Text = T("msg.dev.no_service");
        }
        else
        {
            string[] p = status.Split(IpcProtocol.Sep);
            connected = p.Length >= 2 && p[1] == "1";
            string name = p.Length >= 3 ? p[2] : "";
            ConnText.Text = connected ? T("msg.dev.connected", name) : T("msg.dev.not_connected");
        }

        // 设备信息
        InfoText.Text = "";
        _firmware = null;
        ConnParamsPopup.IsOpen = false;
        if (connected)
        {
            string? info = await AppServices.Pipe.InfoAsync();
            if (info is not null && info.StartsWith(IpcProtocol.Ok))
            {
                string[] ip = info.Split(IpcProtocol.Sep);
                if (ip.Length >= 5)
                {
                    string batt = ip[2] == "-1" ? T("common.unknown") : $"{ip[2]}%";
                    InfoText.Text = T("msg.dev.info", batt, ip[3], ip[4]);
                    _firmware = ip[3];
                }
            }
        }

        // 配对状态
        string? pair = await AppServices.Pipe.PairStatusAsync();
        bool paired = pair?.Contains("PAIRED") == true && pair.Contains("UNPAIRED") != true;
        _paired = paired;
        _connected = connected;
        _slotsKnown = false;
        _deviceBound = false;
        // 先按本地绑定乐观判定；设备在线时 RefreshDualHostAsync 会拿槽位状态复核，可能推翻。
        _bindingValid = paired;

        // 未配对时先给合并文案兜底，拿到槽位状态后换成对应那一种。
        PairHint.Text = connected ? T("device.pair.hint") : T("device.pair.need_connect");
        ApplyPairCard();

        // 双主机
        await RefreshDualHostAsync(connected, paired);
    }

    /// <summary>
    /// 按 <see cref="_paired"/> / <see cref="_bindingValid"/> 渲染配对卡片的状态行与按钮。
    /// 槽位状态复核完要再调一次——那时才知道设备认不认本地这把钥匙。
    ///
    /// 没有有效绑定时按钮一律可见可点，不拿「设备已连接」当前置条件：配第二台主机时设备
    /// 常常还连在第一台上、或刚在系统蓝牙里配好还没被服务接上，按钮此时若是灰的或干脆不画，
    /// 用户看到的就是「根本没有配对入口」。改成点了再说明缺什么。
    /// </summary>
    private void ApplyPairCard()
    {
        bool stale = _paired && !_bindingValid;
        PairText.Text = stale ? T("msg.dev.paired_stale")
            : _bindingValid ? T("msg.dev.paired")
            : T("msg.dev.unpaired");

        PairBtn.Visibility = _bindingValid ? Visibility.Collapsed : Visibility.Visible;
        PairBtn.IsEnabled = !_bindingValid;
        // 绑定有效时这段提示讲的是「怎么配对」，没有按钮了也就没有意义，整段收起。
        PairHint.Visibility = _bindingValid ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RefreshDualHostAsync(bool connected, bool paired)
    {
        DualHostCard.Visibility = Visibility.Collapsed;

        // 设备不在身边、但本机仍有绑定：仍要给出「解绑本机」入口（只清本地绑定数据），
        // 否则用户无法改配新设备。槽位状态要查设备，此时未知，隐藏槽位行。
        if (!connected)
        {
            if (!paired) return;
            DualDesc.Text = T("device.dual.desc_offline");
            Slot1Row.Visibility = Visibility.Collapsed;
            Slot2Row.Visibility = Visibility.Collapsed;
            // 槽位行看不见，行内的悬停按钮也就无从触达：离线时用独立的「解绑本机」按钮兜底。
            UnbindSelfBtn.Visibility = Visibility.Visible;
            UnbindSelfBtn.IsEnabled = true;
            SetDualHint(T("msg.unbind.local_only_hint"));
            DualHostCard.Visibility = Visibility.Visible;
            return;
        }

        DualDesc.Text = T("device.dual.desc");
        Slot1Row.Visibility = Visibility.Visible;
        Slot2Row.Visibility = Visibility.Visible;
        UnbindSelfBtn.Visibility = Visibility.Collapsed; // 在线时解绑入口都在槽位行里

        string? s = await AppServices.Pipe.SlotStatusAsync();
        // OK:<supported>:<bitmap>:<active>
        if (s is null || !s.StartsWith(IpcProtocol.Ok)) return;
        string[] parts = s.Split(IpcProtocol.Sep);
        if (parts.Length < 4) return;
        bool supported = parts[1] == "1";
        if (!supported) return; // 旧固件不显示双主机卡片

        int.TryParse(parts[2], out int bitmap);
        int.TryParse(parts[3], out int active);
        _activeSlot = (byte)active;

        bool slot1 = (bitmap & 0b01) != 0;
        bool slot2 = (bitmap & 0b10) != 0;
        Slot1State.Text = SlotLabel(slot1, active == 1);
        Slot2State.Text = SlotLabel(slot2, active == 2);

        _slotsKnown = true;
        _deviceBound = slot1 || slot2;

        // 设备认不认本机，看的是「本机所在的活跃槽」占没占。本地有 shared_key 但这个槽是
        // 空的（典型：设备绑在槽 1 给了另一台，本机是活跃的槽 2 却还没登记），说明本地这份
        // 绑定在设备侧已经不存在了。不把它判为失效的话，卡片会一直显示「已配对」并把配对
        // 按钮藏起来 —— 正是「槽 2 空着却没有配对入口」的成因。
        bool selfSlotOccupied = active == 1 ? slot1 : active == 2 && slot2;
        if (_paired && !selfSlotOccupied) _bindingValid = false;

        // 每个槽自己知道该给哪种按钮：活跃槽=解绑本机，另一台占用的槽=解绑另一台。
        // 空槽给「在此配对」的两种情形：① 它就是本机的活跃槽（设备正是拿这个槽认我们，
        // 配对必然落在它上面）；② 另一个槽已被别人占了（配第二台主机的常见入口）。
        // 两槽全空又没有活跃槽的新设备不给，免得同样的动作在两行里各出现一次。
        bool canPair = !_bindingValid;
        ConfigureSlotButton(Slot1ActionBtn, 1, slot1, active == 1, _bindingValid,
            pairHere: canPair && (active == 1 || slot2));
        ConfigureSlotButton(Slot2ActionBtn, 2, slot2, active == 2, _bindingValid,
            pairHere: canPair && (active == 2 || slot1));
        UpdateSlotReveal();

        // 按设备上的实际占用情况说清楚这次配对要做什么：
        // 两槽全空 = 首次配对，固件只挂按键门；已占一个 = 登记第二台，固件先指纹门再按键门。
        if (canPair)
        {
            string how = (slot1, slot2) switch
            {
                (true, true) => T("device.pair.slots_full"),
                (true, false) => T("device.pair.slot_hint", 2),
                (false, true) => T("device.pair.slot_hint", 1),
                _ => T("device.pair.hint.fresh"),
            };
            // 本地绑定作废时，先解释「为什么明明配过却要重配」，再讲怎么配。
            PairHint.Text = _paired
                ? T(active == 0 ? "device.pair.stale_reset" : "device.pair.stale", active) + "\n" + how
                : how;
        }
        ApplyPairCard();

        DualHostCard.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 按槽位状态决定这一行的按钮：活跃槽（本机）给「解绑本机」；另一台占用的槽给
    /// 「解绑另一台」；空槽按 <paramref name="pairHere"/> 决定给不给「在此配对」。
    /// Tag 记「动作:槽号」，点击时据此分派。
    /// </summary>
    private static void ConfigureSlotButton(
        Wpf.Ui.Controls.Button btn, byte slot, bool occupied, bool isSelf, bool bindingValid, bool pairHere)
    {
        if (!occupied)
        {
            if (!pairHere) { btn.Visibility = Visibility.Collapsed; return; }

            btn.Visibility = Visibility.Visible;
            btn.Content = T("device.pair.here");
            btn.Tag = $"pair:{slot}";
            btn.IsEnabled = true;
            return;
        }

        btn.Visibility = Visibility.Visible;
        btn.Content = T(isSelf ? "device.unbind.self" : "device.unbind.other");
        btn.Tag = $"unbind:{slot}";
        btn.IsEnabled = !isSelf || bindingValid; // 「解绑本机」要本机确实有有效绑定
    }

    /// <summary>
    /// 设置提示文字，没有内容时整体收起。空 TextBlock 仍会占一整行加上外边距，
    /// 平时没提示可说的，那段空白就挂在卡片底部。
    /// </summary>
    private void SetDualHint(string text)
    {
        DualHint.Text = text;
        DualHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSlotRowHover(object sender, System.Windows.Input.MouseEventArgs e) => UpdateSlotReveal();

    /// <summary>
    /// 悬停哪一行就淡入哪一行的解绑按钮；同步 IsHitTestVisible，避免点到看不见的按钮。
    /// 「在此配对」不参与藏显——它是主要动作，藏起来就等于没有配对入口。
    /// </summary>
    private void UpdateSlotReveal()
    {
        Reveal(Slot1ActionBtn, Slot1Row.IsMouseOver);
        Reveal(Slot2ActionBtn, Slot2Row.IsMouseOver);

        static void Reveal(Wpf.Ui.Controls.Button btn, bool hovering)
        {
            if (IsPairAction(btn)) { btn.Opacity = 1; btn.IsHitTestVisible = true; return; }
            bool on = hovering && btn.Visibility == Visibility.Visible;
            btn.Opacity = on ? 1 : 0;
            btn.IsHitTestVisible = on;
        }
    }

    private static bool IsPairAction(Wpf.Ui.Controls.Button btn)
        => (btn.Tag as string)?.StartsWith("pair:") == true;

    /// <summary>槽位行内的按钮：按 Tag 里的「动作:槽号」分派到配对 / 解绑本机 / 解绑另一台。</summary>
    private async void OnSlotActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn) return;
        if (btn.Tag is not string tag) return;
        string[] t = tag.Split(':');
        if (t.Length < 2 || !byte.TryParse(t[1], out byte slot)) return;

        if (t[0] == "pair") { await PairAsync(); return; }
        if (slot == _activeSlot) await UnbindSelfAsync();
        else await UnbindOtherAsync(slot);
    }

    /// <summary>
    /// 槽位状态文案。空槽也分两种：本机正落在这个槽上（设备就是拿它认我们，只是还没登记）
    /// 要说清楚，否则「空」旁边冒出个「在此配对」会让人不知道配的是谁。
    /// </summary>
    private static string SlotLabel(bool occupied, bool active)
    {
        if (!occupied) return active ? T("msg.slot.empty_self") : T("msg.slot.empty");
        return active ? T("msg.slot.self_active") : T("msg.slot.other");
    }

    /// <summary>
    /// 点固件版本那一行：读链路层实际连接参数并弹出 tips（与 macOS / Linux 同一条 GET_CONN_PARAMS）。
    /// 每次现读——两端随时可能重新协商，监督超时太短正是绑定失败（0xFF）的常见原因，旧值会误导。
    /// </summary>
    private async void OnInfoTextClick(object sender, MouseButtonEventArgs e)
    {
        if (!_connected || string.IsNullOrEmpty(InfoText.Text) || _readingConnParams) return;

        // 固件 < 1.7.2 没有 0x03：不发，免得白等设备超时，直接给「需要 1.7.2+」提示。
        if (!SupportsConnParams(_firmware))
        {
            ShowConnParams(T("device.connparams.failed"));
            return;
        }

        _readingConnParams = true;
        ShowConnParams(T("common.querying"));
        string? r = await AppServices.Pipe.ConnParamsAsync();
        _readingConnParams = false;
        ShowConnParams(FormatConnParams(r) ?? T("device.connparams.failed"));
    }

    private void ShowConnParams(string text)
    {
        ConnParamsText.Text = text;
        ConnParamsPopup.IsOpen = true;
    }

    /// <summary>OK:&lt;interval&gt;:&lt;latency&gt;:&lt;timeout&gt;，BLE 原生单位：interval ×1.25ms、latency 连接事件数、timeout ×10ms。</summary>
    private static string? FormatConnParams(string? r)
    {
        if (r is null || !r.StartsWith(IpcProtocol.Ok + IpcProtocol.Sep)) return null;
        string[] p = r.Split(IpcProtocol.Sep);
        if (p.Length < 4 || !ushort.TryParse(p[1], out ushort interval)
            || !ushort.TryParse(p[2], out ushort latency) || !ushort.TryParse(p[3], out ushort timeout))
            return null;
        double intervalMs = interval * 1.25;
        // 空闲时的有效事件间隔：latency 允许外设连续跳过的极限
        double idleMs = intervalMs * (latency + 1);
        return T("device.connparams.message",
            intervalMs.ToString("F2", CultureInfo.InvariantCulture), interval, latency, timeout * 10,
            idleMs.ToString("F0", CultureInfo.InvariantCulture));
    }

    /// <summary>固件版本可能带 build 段（"1.7.2.abc"），取前三段数字比较；解析不了按不支持。</summary>
    private static bool SupportsConnParams(string? fw)
    {
        if (string.IsNullOrEmpty(fw)) return false;
        string[] seg = fw.TrimStart('v', 'V').Split('.');
        if (seg.Length < 3) return false;
        int[] v = new int[3];
        for (int i = 0; i < 3; i++)
        {
            string digits = new string(seg[i].TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out v[i])) return false;
        }
        return (v[0], v[1], v[2]).CompareTo((1, 7, 2)) >= 0;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnPairClick(object sender, RoutedEventArgs e) => await PairAsync();

    private async Task PairAsync()
    {
        if (_bindingValid) return;

        // 设备没连上就别发命令空等：直接说清楚缺什么、去哪儿补。
        // （服务侧也会回 ERR:NOT_CONNECTED，但那要等一个来回，且文案帮不上忙。）
        if (!_connected)
        {
            SetPairResult(T("msg.pair.notconnected"));
            MessageBox.Show(Window.GetWindow(this), T("msg.pair.need_connect_body"),
                T("device.pair"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 槽位状态在 RefreshDualHostAsync 里已经读过，这里只用来决定引导窗画几步。
        // （Service 会自己再查一次来决定起始阶段，两边判据一致；不一致时以服务端为准，
        //   引导窗收到 WaitFingerprint 会把指纹那一步补出来。）
        bool twoStep = _slotsKnown && _deviceBound;
        PairBtn.IsEnabled = false;
        Slot1ActionBtn.IsEnabled = Slot2ActionBtn.IsEnabled = false;
        SetPairResult("");

        // 整个配对过程在引导窗里走完（内部消费 PAIR:START 流），返回终态。
        PairOutcome outcome = PairDialog.Run(Window.GetWindow(this), twoStep);

        string result = PairDialog.Describe(outcome);

        // RefreshAsync 会按最新状态重设三个按钮的可用性并收起步骤，无需在此手工恢复；
        // 但它也会重写 PairText / 清 PairResult，所以结果文案放在刷新之后。
        await RefreshAsync();
        SetPairResult(result);
    }

    /// <summary>配对操作结果行；无内容时收起，免得卡片底部凭空多一段空白。</summary>
    private void SetPairResult(string text)
    {
        PairResult.Text = text;
        PairResult.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnUnbindSelfClick(object sender, RoutedEventArgs e) => await UnbindSelfAsync();

    private async Task UnbindSelfAsync()
    {
        // 两种情况的后果不同，确认文案要分开：
        // 已连接 → 通知设备清槽（设备会重启）；未连接 → 只清本地绑定，设备侧槽位仍占用。
        var confirm = MessageBox.Show(
            _connected ? T("msg.unbind.self_confirm") : T("msg.unbind.local_only_confirm"),
            T("device.unbind.self"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        SetDualHint(_connected ? T("msg.unbind.self_progress") : T("msg.unbind.local_only_progress"));
        string? r = await AppServices.Pipe.SlotClearOwnAsync();

        if (r?.StartsWith($"{IpcProtocol.Ok}:LOCAL") == true)
            SetDualHint(T("msg.unbind.local_only_ok"));
        else if (r?.StartsWith(IpcProtocol.Ok) == true)
            SetDualHint(T("msg.unbind.self_ok"));
        else if (r == IpcProtocol.Reject)
            // 设备拒绝（本机不是活跃槽）——多半是本机绑的还是旧设备、现在连上的却是新设备。
            // 此时本地那把 shared_key 对当前设备无效，但只有用户能确认要不要丢弃它，故显式询问。
            await OfferLocalUnbindAsync();
        else
            SetDualHint(T("msg.unbind.fail", r ?? ""));

        await RefreshAsync();
    }

    /// <summary>设备拒绝解绑后，询问是否仅清除本地绑定数据（以便改配新设备）。</summary>
    private async Task OfferLocalUnbindAsync()
    {
        SetDualHint(T("msg.unbind.rejected"));
        var choice = MessageBox.Show(
            T("msg.unbind.rejected_offer"),
            T("device.unbind.self"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.OK) return;

        string? r = await AppServices.Pipe.PairResetAsync();
        SetDualHint(r?.StartsWith(IpcProtocol.Ok) == true
            ? T("msg.unbind.local_only_ok")
            : T("msg.unbind.fail", r ?? ""));
    }

    private async Task UnbindOtherAsync(byte slot)
    {
        if (slot == 0) return;
        // 解绑另一台要在设备上过指纹门，弹认证窗（含 30s 倒计时）。
        string? r = FpAuthDialog.Run(Window.GetWindow(this),
            T("msg.fpauth.unbind_other", slot),
            () => AppServices.Pipe.SlotClearAsync(slot));
        SetDualHint(r == IpcProtocol.Ok ? T("msg.unbind.other_ok") : T("msg.unbind.other_fail", r ?? ""));
        await RefreshAsync();
    }
}
