using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ImmurokCli;

// imk —— immurok 命令行工具（Windows）。
//   imk list <ssh|otp|api>            列出密钥名
//   imk get  imk://category/name      把密钥值输出到 stdout（指纹门控）
//   imk run [--env-file F] [--agent[=NAME]] -- CMD...
//                                     解析 imk:// 引用 / .env 注入环境变量后运行命令
//                                     --agent 会先弹 GUI 授权窗口，按指纹通过才运行
//   imk version | help
//
// 与服务通信：命名管道 \\.\pipe\immurok-cli，行式文本（一问一答，服务端写完即关闭）。
internal static class Program
{
    private const string PipeName = "immurok-cli";

    // GUI 授权窗口的共享状态（UI 线程创建，主线程更新）。
    private static Window? _win;
    private static TextBlock? _status;
    private static ProgressBar? _bar;
    private static volatile bool _userCancelled;
    private static volatile bool _closing;   // 程序化关闭中，避免 Closing 处理器误发 CANCEL
    private static volatile bool _guiFailed;  // 弹窗初始化失败 → 退回终端
    private static volatile bool _timedOut;   // 倒计时归零 → 超时
    private static DispatcherTimer? _timer;   // 30s 倒计时（UI 线程）
    private const int ApproveTimeoutSec = 30;

    private static int Main(string[] args)
    {
        if (args.Length == 0) return PrintUsage();
        return args[0] switch
        {
            "list" => CmdList(args[1..]),
            "get" => CmdGet(args[1..]),
            "run" => CmdRun(args[1..]),
            "version" or "--version" or "-v" => Print($"imk {AppVersion}"),
            "help" or "--help" or "-h" => PrintUsage(),
            _ => Unknown(args[0]),
        };
    }

    /// <summary>
    /// 版本号取自程序集（源头是 Directory.Build.props 的 &lt;Version&gt;），不要在这里再写死一份——
    /// 之前硬编码的 "0.2.0" 在 props 升到新版本后就对不上了。
    /// </summary>
    private static string AppVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]          // 去掉 SourceLink 追加的 +<commit>
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "unknown";

    private static int Unknown(string c) { Err($"imk: unknown command '{c}'"); PrintUsage(); return 1; }
    private static int Print(string s) { Console.WriteLine(s); return 0; }
    private static void Err(string s) => Console.Error.WriteLine(s);

    private static int PrintUsage()
    {
        Err("""
        Usage: imk <command> [options]

        Commands:
          list <ssh|otp|api>               List key names
          get imk://category/name          Output secret value to stdout
          run [opts] -- CMD                Inject secrets / run CMD
                                           opts: --env-file FILE
                                                 --agent[=NAME]  (GUI fingerprint
                                                                  approval before
                                                                  running; sets
                                                                  IMK_AGENT marker)
          version                          Print version

        Examples:
          imk list api
          imk get imk://api/openai
          $env:TOKEN = imk get imk://api/github
          imk run --env-file .env -- python app.py
          imk run --agent=claude -- git push
        """);
        return 0;
    }

    // ---- 与服务通信 ----

    private static string SendCli(string command)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        try { pipe.Connect(3000); }
        catch (Exception) { throw new IOException("cannot connect to immurok service (is it running?)"); }

        var enc = new UTF8Encoding(false);
        byte[] req = enc.GetBytes(command + "\n");
        pipe.Write(req, 0, req.Length);
        pipe.Flush();

        using var ms = new MemoryStream();
        byte[] buf = new byte[4096];
        int n;
        while ((n = pipe.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        return enc.GetString(ms.ToArray()).TrimEnd('\n');
    }

    private static string? CheckError(string resp)
    {
        // 服务端的明确拒绝码（DENY:<reason>），和 ERROR:* 一样要给出人话；裸 "DENY" 是设备指纹没过，由各命令自己处理。
        if (resp.StartsWith("DENY:", StringComparison.Ordinal)) return DenyReason(resp);
        if (!resp.StartsWith("ERROR:", StringComparison.Ordinal)) return null;
        string reason = resp[6..];
        return reason switch
        {
            "NOT_CONNECTED" => "device not connected",
            "NOT_FOUND" => "key not found",
            "DENY" => "denied (fingerprint not matched or timed out)",
            "BAD_CATEGORY" => "invalid category (use ssh|otp|api)",
            _ => reason,
        };
    }

    private static string DenyReason(string resp) => resp switch
    {
        "DENY:NOT_OWNER" => "denied: this immurok is paired by another user account on this PC; only that account can use it",
        "DENY:GATE_TIMEOUT" => "denied: fingerprint not touched in time",
        "DENY:GATE_REJECTED" => "denied: fingerprint rejected on the device",
        "DENY:NO_FINGERPRINT" => "denied: no fingerprint enrolled on the device yet; add one in the immurok app first",
        _ => $"denied ({resp[5..]})",
    };

    // ---- list ----

    private static int CmdList(string[] a)
    {
        if (a.Length < 1) { Err("Usage: imk list <ssh|otp|api>"); return 1; }
        string cat = a[0];
        if (cat is not ("ssh" or "otp" or "api")) { Err("Category must be ssh, otp or api"); return 1; }
        string resp;
        try { resp = SendCli($"LIST:{cat}"); }
        catch (Exception ex) { Err($"Error: {ex.Message}"); return 1; }

        string? e = CheckError(resp);
        if (e is not null) { Err($"Error: {e}"); return 1; }
        if (!resp.StartsWith("OK", StringComparison.Ordinal)) { Err("Unexpected response"); return 1; }

        var lines = resp.Split('\n');
        for (int i = 1; i < lines.Length; i++)
            if (lines[i].Length > 0) Console.WriteLine(lines[i]);
        return 0;
    }

    // ---- get ----

    private static int CmdGet(string[] a)
    {
        if (a.Length < 1) { Err("Usage: imk get imk://category/name"); return 1; }
        if (!TryParseRef(a[0], out string cat, out string name)) { Err($"Invalid reference: {a[0]} (expected imk://category/name)"); return 1; }

        string resp;
        try { resp = SendCli($"GET:{cat}:{name}"); }
        catch (Exception ex) { Err($"Error: {ex.Message}"); return 1; }

        string? e = CheckError(resp);
        if (e is not null) { Err($"Error: {e}"); return 1; }
        if (!resp.StartsWith("OK:", StringComparison.Ordinal)) { Err("Unexpected response"); return 1; }

        string value = resp[3..];
        if (!Console.IsOutputRedirected) Console.WriteLine(value);
        else Console.Out.Write(value);
        return 0;
    }

    private static bool TryParseRef(string reference, out string cat, out string name)
    {
        cat = ""; name = "";
        string body = reference.StartsWith("imk://", StringComparison.Ordinal) ? reference[6..] : reference;
        int slash = body.IndexOf('/');
        if (slash <= 0 || slash >= body.Length - 1) return false;
        cat = body[..slash];
        name = body[(slash + 1)..];
        return cat is "ssh" or "otp" or "api";
    }

    // ---- run ----

    private static int CmdRun(string[] a)
    {
        var envFiles = new List<string>();
        string? agentName = null;
        var cmd = new List<string>();
        bool parsingFlags = true;

        for (int i = 0; i < a.Length; i++)
        {
            if (parsingFlags)
            {
                if (a[i] == "--") { parsingFlags = false; continue; }
                if (a[i] == "--env-file") { if (++i >= a.Length) { Err("--env-file requires an argument"); return 1; } envFiles.Add(a[i]); continue; }
                if (a[i].StartsWith("--env-file=", StringComparison.Ordinal)) { envFiles.Add(a[i]["--env-file=".Length..]); continue; }
                if (a[i] == "--agent") { agentName = "imk"; continue; }
                if (a[i].StartsWith("--agent=", StringComparison.Ordinal)) { agentName = a[i]["--agent=".Length..]; continue; }
            }
            cmd.Add(a[i]);
        }

        if (cmd.Count == 0) { Err("Usage: imk run [--env-file FILE] [--agent[=NAME]] -- COMMAND [ARGS...]"); return 1; }

        // agent 模式：高危动作需用户在场确认——运行前先弹窗要指纹，过了才继续。
        if (agentName is not null)
        {
            int ap = AgentApprove(agentName, cmd);
            if (ap != 0) return ap; // 77 拒绝 / 130 取消 / 1 错误
        }

        var psi = new ProcessStartInfo { FileName = cmd[0], UseShellExecute = false };
        for (int i = 1; i < cmd.Count; i++) psi.ArgumentList.Add(cmd[i]);

        foreach (string f in envFiles)
        {
            try { foreach (var (k, v) in ParseEnvFile(f)) psi.Environment[k] = v; }
            catch (Exception ex) { Err($"Error loading {f}: {ex.Message}"); return 1; }
        }

        if (agentName is not null) psi.Environment["IMK_AGENT"] = agentName;

        // 解析所有 imk:// 引用
        foreach (string k in psi.Environment.Keys.ToList())
        {
            string? val = psi.Environment[k];
            if (val is null || !val.StartsWith("imk://", StringComparison.Ordinal)) continue;
            if (!TryParseRef(val, out string cat, out string name)) { Err($"Invalid reference: {k}={val}"); return 1; }
            string resp;
            try { resp = SendCli($"GET:{cat}:{name}"); }
            catch (Exception ex) { Err($"Error resolving {k}: {ex.Message}"); return 1; }
            string? e = CheckError(resp);
            if (e is not null) { Err($"Failed to resolve {k}={val}: {e}"); return 1; }
            if (!resp.StartsWith("OK:", StringComparison.Ordinal)) { Err($"Unexpected response resolving {k}"); return 1; }
            psi.Environment[k] = resp[3..];
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) { Err($"Failed to run {cmd[0]}"); return 1; }
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex) { Err($"Failed to run {cmd[0]}: {ex.Message}"); return 1; }
    }

    private static IEnumerable<(string, string)> ParseEnvFile(string path)
    {
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
                value = value.Length >= 2 ? value[1..^1] : value;
            if (key.Length > 0) yield return (key, value);
        }
    }

    // ---- agent 授权 ----

    /// <summary>返回 0=通过，77=拒绝，130=取消，1=错误。优先 GUI，弹窗失败则退回终端。</summary>
    private static int AgentApprove(string agentName, List<string> cmd)
    {
        string cmdString = string.Join(' ', cmd);
        try { return AgentApproveGui(agentName, cmdString); }
        catch { return AgentApproveTerminal(agentName, cmdString); }
    }

    private static int AgentApproveGui(string agentName, string cmdString)
    {
        _userCancelled = false;
        _closing = false;
        _guiFailed = false;
        _timedOut = false;
        var ready = new ManualResetEventSlim(false);

        var ui = new Thread(() =>
        {
          try
          {
            bool dark = IsDarkTheme();
            var cardBg   = Brush(dark ? "#FF2B2B2B" : "#FFFFFFFF");
            var cardEdge = Brush(dark ? "#FF3C3C3C" : "#FFE3E3E3");
            var fg       = Brush(dark ? "#FFF2F2F2" : "#FF1A1A1A");
            var fg2      = Brush(dark ? "#FFA6A6A6" : "#FF6A6A6A");
            var boxBg    = Brush(dark ? "#FF1F1F1F" : "#FFF4F4F5");
            var accent   = Brush("#FF3FB950");

            _win = new Window
            {
                Title = "immurok",
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,       // 去掉标题栏
                AllowsTransparency = true,            // 圆角 + 阴影需要透明窗体
                Background = Brushes.Transparent,
                Topmost = true,
            };

            // 卡片（圆角 + 描边 + 投影）
            var card = new Border
            {
                Background = cardBg,
                BorderBrush = cardEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(16),           // 给投影留空间
                Padding = new Thickness(24),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black },
            };
            var panel = new StackPanel();

            // 顶部：logo + 应用名
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            var logo = LoadLogo(dark);
            if (logo is not null)
                header.Children.Add(new Image { Source = logo, Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = "immurok", Foreground = fg2, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(header);

            panel.Children.Add(new TextBlock { Text = "Authorization required", Foreground = fg, FontSize = 19, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(new TextBlock { Text = $"Agent “{agentName}” wants to run:", Foreground = fg2, FontSize = 13, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });

            panel.Children.Add(new Border
            {
                Background = boxBg,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 18),
                Child = new TextBlock { Text = cmdString, FontFamily = new FontFamily("Consolas"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = fg },
            });

            // 30s 倒计时进度条（与设备指纹门 30s 对齐）
            _bar = new ProgressBar
            {
                Minimum = 0, Maximum = ApproveTimeoutSec, Value = ApproveTimeoutSec,
                Height = 3, Foreground = accent, Background = boxBg, BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 10),
            };
            panel.Children.Add(_bar);
            _status = new TextBlock { Text = $"Touch your immurok to authorize… ({ApproveTimeoutSec}s)", Foreground = fg2, FontSize = 13, Margin = new Thickness(0, 0, 0, 18), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_status);

            int remaining = ApproveTimeoutSec;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                remaining--;
                if (remaining <= 0)
                {
                    _timer?.Stop();
                    _timedOut = true;
                    if (_bar is not null) _bar.Value = 0;
                    if (_status is not null) { _status.Text = "Timed out"; _status.Foreground = fg2; }
                    try { SendCli("CANCEL"); } catch { }   // 让设备门立即返回，解阻塞 APPROVE
                }
                else
                {
                    if (_bar is not null) _bar.Value = remaining;
                    if (_status is not null) _status.Text = $"Touch your immurok to authorize… ({remaining}s)";
                }
            };
            _timer.Start();

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 104, Height = 34,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = boxBg, Foreground = fg, BorderBrush = cardEdge, BorderThickness = new Thickness(1),
                FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand,
            };
            cancelBtn.Click += (_, _) => { _userCancelled = true; try { SendCli("CANCEL"); } catch { } };
            panel.Children.Add(cancelBtn);

            card.Child = panel;
            _win.Content = card;
            card.MouseLeftButtonDown += (_, _) => { try { _win?.DragMove(); } catch { } }; // 无标题栏 → 可拖动
            _win.Closing += (_, _) => { if (!_closing && !_userCancelled) { _userCancelled = true; try { SendCli("CANCEL"); } catch { } } };
            _win.Show();
            _win.Activate();
            ready.Set();
            Dispatcher.Run();
          }
          catch { _guiFailed = true; _win = null; ready.Set(); }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();

        // 弹窗没起来（无桌面会话 / 初始化失败）→ 退回终端提示。
        if (!ready.Wait(4000) || _guiFailed || _win is null)
            return AgentApproveTerminal(agentName, cmdString);

        // 阻塞发 APPROVE（设备指纹门）。Cancel 会经 CANCEL 让设备门返回，从而解阻塞。
        string resp;
        try { resp = SendCli($"APPROVE:{cmdString}"); }
        catch (Exception ex) { CloseWin(); Err($"imk: agent approval failed: {ex.Message}"); return 1; }

        bool cancelled = _userCancelled;         // \u5173\u95ed\u7a97\u53e3\u524d\u5b9a\u683c\u72b6\u6001
        bool timedOut = _timedOut;
        bool ok = resp == "OK" && !cancelled && !timedOut;
        // \u670d\u52a1\u7aef\u660e\u786e\u62d2\u7edd\uff08DENY:NOT_OWNER \u7b49\uff09\u662f\u79d2\u56de\u7684\uff1a\u7a97\u53e3\u4e00\u95ea\u5c31\u6ca1\uff0c\u7528\u6237\u770b\u4e0d\u5230\u539f\u56e0\u3002
        // \u628a\u539f\u56e0\u5199\u8fdb\u7a97\u53e3\u5e76\u591a\u505c\u4e00\u4f1a\u513f\uff0c\u7ec8\u7aef\u4e0a\u4e5f\u518d\u6253\u4e00\u904d\u3002
        string? denied = resp.StartsWith("DENY:", StringComparison.Ordinal) ? DenyReason(resp) : null;
        try
        {
            _win.Dispatcher.Invoke(() =>
            {
                _timer?.Stop();
                if (_bar is not null && !ok) _bar.Value = 0;
                if (_status is not null)
                {
                    _status.Text = cancelled ? "Cancelled" : timedOut ? "Timed out"
                        : ok ? "\u2713 Authorized"
                        : denied is not null ? $"\u2717 {denied}" : "\u2717 Denied";
                    _status.Foreground = Brush(ok ? "#FF3FB950" : (cancelled || timedOut ? "#FFB0B0B0" : "#FFE05252"));
                }
            });
        }
        catch { /* ignore */ }
        Thread.Sleep(ok ? 500 : timedOut ? 700 : denied is not null ? 2500 : 300);
        _closing = true;
        CloseWin();

        if (cancelled) return 130;
        if (timedOut) { Err("imk: agent approval timed out"); return 77; }
        string? apErr = CheckError(resp);
        if (apErr is not null) { Err($"imk: agent approval failed: {apErr}"); return 1; }
        return ok ? 0 : 77;
    }

    private static void CloseWin()
    {
        var w = _win;
        if (w is null) return;
        try { w.Dispatcher.Invoke(() => { try { _timer?.Stop(); w.Close(); } catch { } }); } catch { }
        try { w.Dispatcher.InvokeShutdown(); } catch { }
        _win = null; _status = null; _bar = null; _timer = null;
    }

    /// <summary>无 GUI 时的终端授权提示。</summary>
    private static int AgentApproveTerminal(string agentName, string cmdString)
    {
        Console.Error.WriteLine($"[immurok] Agent '{agentName}' requests to run:");
        Console.Error.WriteLine($"          {cmdString}");
        Console.Error.WriteLine("[immurok] Touch your immurok to authorize this run... (Ctrl+C to cancel)");

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            try { SendCli("CANCEL"); } catch { }
            Console.Error.WriteLine();
            Console.Error.WriteLine("imk: cancelled.");
            Environment.Exit(130);
        };
        Console.CancelKeyPress += onCancel;
        string resp;
        try { resp = SendCli($"APPROVE:{cmdString}"); }
        catch (Exception ex) { Console.CancelKeyPress -= onCancel; Err($"imk: agent approval failed: {ex.Message}"); return 1; }
        Console.CancelKeyPress -= onCancel;

        string? apErr = CheckError(resp);
        if (apErr is not null) { Err($"imk: agent approval failed: {apErr}"); return 1; }
        if (resp != "OK") { Err("imk: agent command rejected (fingerprint not matched or timed out)"); return 77; }
        Console.Error.WriteLine("[immurok] Approved.");
        return 0;
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>从内嵌资源加载 logo（暗色用白版，亮色用黑版）。用清单流，无需 Application/pack URI。</summary>
    private static BitmapImage? LoadLogo(bool dark)
    {
        try
        {
            string res = dark ? "ImmurokCli.Assets.immurok-white.png" : "ImmurokCli.Assets.immurok.png";
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res);
            if (s is null) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = s;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* ignore */ }
        return true;
    }
}
