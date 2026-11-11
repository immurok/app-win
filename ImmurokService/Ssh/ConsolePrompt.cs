using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ImmurokService.Ssh;

/// <summary>
/// 终端指纹提示：拉起 ImmurokConsolePrompt.exe 助手进程，让它附到 ssh 客户端的控制台显示
/// "请按指纹"动画（对齐 macOS/Linux 体验）。用独立进程避免动到 Service 自己的控制台。
///
/// 服务以 LocalSystem 跑在 Session 0，控制台是按会话隔离的：服务直接 Process.Start 出来的助手
/// 也在 Session 0，AttachConsole 到用户会话里的 ssh.exe 必然失败（之前终端一直没有提示就是这个原因）。
/// 所以用 ssh.exe 自己的令牌 CreateProcessAsUser，让助手和 ssh 同用户、同会话、同完整性级别；
/// 句柄继承用 PROC_THREAD_ATTRIBUTE_HANDLE_LIST 限定为只给 stdin 读端，不把 SYSTEM 的句柄漏给用户进程。
/// 控制台调试模式（服务以普通用户跑、没有 SeAssignPrimaryTokenPrivilege）退回普通 Process.Start。
/// 找不到助手 / 无法附控制台时静默降级。
///
/// 收尾用 <see cref="Finish"/>：关闭助手 stdin → 助手读到 EOF 后擦除本行并退出 → 本方法等它退出，
/// 从而保证"擦除"发生在调用方向 ssh 回包之前，不会残留提示文字。
/// </summary>
public sealed class ConsolePrompt : IDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private bool _finished;

    private ConsolePrompt(Process p, StreamWriter stdin) { _proc = p; _stdin = stdin; }

    public static ConsolePrompt? TryStart(uint clientPid, ILogger log)
    {
        if (clientPid == 0) return null;
        string exe = Path.Combine(AppContext.BaseDirectory, "ImmurokConsolePrompt.exe");
        if (!File.Exists(exe)) { log.LogDebug("终端提示助手缺失: {Exe}", exe); return null; }
        try
        {
            return StartAsClient(exe, clientPid) ?? StartPlain(exe, clientPid);
        }
        catch (Exception ex) { log.LogDebug(ex, "启动终端提示失败"); return null; }
    }

    private static ConsolePrompt? StartPlain(string exe, uint clientPid)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = clientPid.ToString(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        var p = Process.Start(psi);
        return p is null ? null : new ConsolePrompt(p, p.StandardInput);
    }

    /// <summary>
    /// 用 ssh 客户端进程的令牌在它的会话里起助手。拿不到令牌 / 没有分配主令牌的特权时返回 null，
    /// 由调用方退回 <see cref="StartPlain"/>。
    /// </summary>
    private static ConsolePrompt? StartAsClient(string exe, uint clientPid)
    {
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, clientPid);
        if (hProc == IntPtr.Zero) return null;
        IntPtr hTok = IntPtr.Zero, hPrimary = IntPtr.Zero, env = IntPtr.Zero, attrs = IntPtr.Zero, handleList = IntPtr.Zero;
        var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        bool ok = false;
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY, out hTok)) return null;
            if (!DuplicateTokenEx(hTok, TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID,
                    IntPtr.Zero, SecurityImpersonation, TokenPrimary, out hPrimary)) return null;
            if (!CreateEnvironmentBlock(out env, hPrimary, false)) env = IntPtr.Zero;

            IntPtr hRead = pipe.ClientSafePipeHandle.DangerousGetHandle();

            // 只继承 stdin 读端。
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrs = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
            handleList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(handleList, hRead);
            if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                    handleList, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.lpDesktop = @"winsta0\default";
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = hRead;
            si.lpAttributeList = attrs;

            string cmdLine = $"\"{exe}\" {clientPid}";
            uint flags = CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | (env != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0);
            if (!CreateProcessAsUserW(hPrimary, exe, cmdLine, IntPtr.Zero, IntPtr.Zero, true, flags, env,
                    Path.GetDirectoryName(exe), ref si, out PROCESS_INFORMATION pi))
            {
                // 1314 = ERROR_PRIVILEGE_NOT_HELD：控制台调试模式，退回普通启动（同会话，能附上）。
                if (Marshal.GetLastWin32Error() == ERROR_PRIVILEGE_NOT_HELD) return null;
                throw new Win32Exception();
            }
            CloseHandle(pi.hThread);
            Process p;
            try { p = Process.GetProcessById((int)pi.dwProcessId); }
            finally { CloseHandle(pi.hProcess); }

            pipe.DisposeLocalCopyOfClientHandle();
            ok = true;
            return new ConsolePrompt(p, new StreamWriter(pipe) { AutoFlush = true });
        }
        finally
        {
            if (!ok) pipe.Dispose();
            if (attrs != IntPtr.Zero) { DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (hPrimary != IntPtr.Zero) CloseHandle(hPrimary);
            if (hTok != IntPtr.Zero) CloseHandle(hTok);
            CloseHandle(hProc);
        }
    }

    public void Fail(int remaining) => Send($"FAIL {remaining}");
    public void Signing() => Send("SIGNING");

    /// <summary>最终失败：助手把错误写成一整行留在终端（不擦除）后退出。文案在助手里。</summary>
    public void Error(Ble.BleManager.SignFailure failure) => Send(failure switch
    {
        Ble.BleManager.SignFailure.LinkParams => "ERROR LINK",
        Ble.BleManager.SignFailure.FingerprintRejected => "ERROR REJECTED",
        Ble.BleManager.SignFailure.Timeout => "ERROR TIMEOUT",
        Ble.BleManager.SignFailure.Disconnected => "ERROR DISCONNECTED",
        _ => "ERROR FAILED",
    });

    private void Send(string s)
    {
        if (_finished) return;
        try { _stdin.WriteLine(s); _stdin.Flush(); }
        catch { /* 助手已退出 */ }
    }

    /// <summary>关闭 stdin 让助手擦除并退出，并等待其退出（擦除完成）。</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        try { _stdin.Close(); } catch { /* ignore */ }
        try { if (!_proc.WaitForExit(700)) _proc.Kill(); } catch { /* ignore */ }
        try { _proc.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose() => Finish();

    // ---- P/Invoke ----
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001, TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008,
        TOKEN_ADJUST_DEFAULT = 0x0080, TOKEN_ADJUST_SESSIONID = 0x0100;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;
    private const uint CREATE_NO_WINDOW = 0x08000000, EXTENDED_STARTUPINFO_PRESENT = 0x00080000,
        CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    private const int ERROR_PRIVILEGE_NOT_HELD = 1314;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr env);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr,
        IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string app, string cmdLine,
        IntPtr procAttrs, IntPtr threadAttrs, bool inherit, uint flags, IntPtr env, string? cwd,
        ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
}
