using System.Runtime.InteropServices;

// immurok 终端指纹提示助手
//
// 用法：ImmurokConsolePrompt.exe <clientPid>
// 附到 ssh 客户端进程的控制台，在终端显示"请按指纹"动画（对齐 macOS/Linux 体验）。
// 通过 stdin 接收状态：
//   FAIL <n>   按错指纹，剩余 n 次
//   SIGNING    指纹已过，正在签名
//   ERROR <c>  最终失败：把错误写成一整行留在终端（不擦除）并退出。c 见文末 ErrorText
//   END        结束（擦除并退出）
// stdin 关闭或收到 END / ERROR 即退出。父进程独立，不影响 Service 自己的控制台。

if (args.Length < 1 || !uint.TryParse(args[0], out uint pid))
    return 1;

// 必须先脱离自己的控制台，才能附到目标进程的控制台。
FreeConsole();
if (!AttachConsole(pid))
    return 2; // 目标无控制台 / 跨会话（Session 0）等，静默放弃

IntPtr conout = CreateFileW("CONOUT$", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
if (conout == INVALID_HANDLE_VALUE)
{
    FreeConsole();
    return 3;
}
// 开启 VT，让 ANSI 转义生效。
if (GetConsoleMode(conout, out uint mode))
    SetConsoleMode(conout, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);

// 文案、帧、节奏与 macOS TerminalSpinner 一致。WriteConsoleW 输出，不受控制台代码页影响。
const string message = "Please verify your fingerprint...";
string[] frames = { "⠖", "⠲", "⢲", "⢰", "⣰", "⣠", "⣄", "⣆", "⡆", "⡖" };

// 共享状态（stdin 线程更新，渲染线程读取）。
object gate = new();
bool done = false;
bool signing = false;
int failRemaining = -1;
long failUntil = 0;

var render = new Thread(() =>
{
    int f = 0, signTick = 0;
    while (true)
    {
        string line;
        lock (gate)
        {
            if (done) break;
            long now = Environment.TickCount64;
            if (failRemaining >= 0 && now < failUntil)
            {
                string s = failRemaining == 1 ? "" : "s";
                line = $"\x1b[31m✗ Not matched, {failRemaining} attempt{s} left\x1b[0m";
            }
            else if (signing)
            {
                int n = signTick++ / 4 % 3 + 1; // 约 0.3 s 换一次点数
                line = $"\x1b[32m✓ Approved, now signing{new string('.', n)}{new string(' ', 3 - n)}\x1b[0m";
            }
            else
            {
                line = $"\x1b[33m{frames[f % frames.Length]} {message}\x1b[0m";
            }
        }
        WriteConsole(conout, "\r\x1b[K" + line);
        f++;
        Thread.Sleep(80);
    }
});
render.IsBackground = true;
render.Start();

// 读 stdin 指令。
string? cmd;
string? error = null;
while ((cmd = Console.In.ReadLine()) != null)
{
    cmd = cmd.Trim();
    if (cmd.StartsWith("FAIL", StringComparison.Ordinal))
    {
        string[] p = cmd.Split(' ');
        int rem = p.Length > 1 && int.TryParse(p[1], out int n) ? n : 0;
        lock (gate) { failRemaining = rem; failUntil = Environment.TickCount64 + 500; }
    }
    else if (cmd == "SIGNING")
    {
        lock (gate) { signing = true; }
    }
    else if (cmd.StartsWith("ERROR", StringComparison.Ordinal))
    {
        error = ErrorText(cmd.Length > 6 ? cmd[6..] : "");
        break;
    }
    else if (cmd == "END")
    {
        break;
    }
}

lock (gate) { done = true; }
render.Join(300);
// 正常结束擦除本行；失败时把错误留在终端并换行，ssh / git 自己的报错接在下一行。
WriteConsole(conout, error is null ? "\r\x1b[K" : $"\r\x1b[K\x1b[31m✗ immurok: {error}\x1b[0m\r\n");
FreeConsole();
return 0;

static string ErrorText(string code) => code switch
{
    "LINK" => "Bluetooth link not ready for signing (connection parameters too weak). "
            + "Retry; if it keeps failing, turn Bluetooth off and on.",
    "REJECTED" => "Fingerprint not matched, signing denied.",
    "TIMEOUT" => "Time out waiting for fingerprint.",
    "DISCONNECTED" => "Device not connected.",
    _ => "Signing failed.",
};

// ---- P/Invoke ----
static void WriteConsole(IntPtr h, string s) => WriteConsoleW(h, s, (uint)s.Length, out _, IntPtr.Zero);

partial class Program
{
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2;
    const uint OPEN_EXISTING = 3;
    const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleMode(IntPtr h, uint mode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WriteConsoleW(IntPtr h, string buf, uint chars, out uint written, IntPtr reserved);
}
