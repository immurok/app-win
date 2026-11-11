using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ImmurokService.Wsl;

/// <summary>
/// Hyper-V socket（AF_HYPERV）的最小封装：WSL2 内的 AF_VSOCK 连接（CID 2 = 主机）在 Windows 侧
/// 就落在这里。<c>System.Net.Sockets.Socket</c> 不认识这个地址族（包成 Socket 后 NetworkStream
/// 构造直接抛），所以监听、accept、收发全部走 Winsock P/Invoke。
///
/// 2026-11-11 spike 结论（Windows 11 26200 / WSL 2.7）：
///   · 通配 VmId（HV_GUID_WILDCARD / HV_GUID_CHILDREN）监听收不到 WSL 的连接，提权也一样；
///     必须 bind 到具体的 WSL 虚拟机 ID（见 <see cref="WslVms"/>）。
///   · GuestCommunicationServices 注册表登记不是必需的。
///   · 普通用户即可监听。
/// Linux vsock 端口到服务 GUID 的映射是固定模板 <c>{port:08x}-facb-11e6-bd58-64006a7986d3</c>。
/// </summary>
internal static class HvSocket
{
    private const int AF_HYPERV = 34;
    private const int SOCK_STREAM = 1;
    private const int HV_PROTOCOL_RAW = 1;
    private const uint WSA_FLAG_OVERLAPPED = 0x01;
    private const short POLLRDNORM = 0x0100;
    private const int MSG_PEEK = 0x2;
    private static readonly IntPtr InvalidSocket = new(-1);

    /// <summary>vsock 端口对应的 Hyper-V 服务 GUID。</summary>
    public static Guid ServiceIdForPort(uint port) => new($"{port:x8}-facb-11e6-bd58-64006a7986d3");

    /// <summary>在 (vmId, serviceId) 上建监听 socket。失败抛 <see cref="SocketException"/>。</summary>
    public static IntPtr Listen(Guid vmId, Guid serviceId)
    {
        EnsureWinsock();
        IntPtr s = WSASocketW(AF_HYPERV, SOCK_STREAM, HV_PROTOCOL_RAW, IntPtr.Zero, 0, WSA_FLAG_OVERLAPPED);
        if (s == InvalidSocket) throw new SocketException(Marshal.GetLastWin32Error());
        var addr = new SockaddrHv { Family = AF_HYPERV, VmId = vmId, ServiceId = serviceId };
        if (bind(s, ref addr, Marshal.SizeOf<SockaddrHv>()) != 0 || listen(s, 16) != 0)
        {
            int err = Marshal.GetLastWin32Error();
            closesocket(s);
            throw new SocketException(err);
        }
        return s;
    }

    /// <summary>阻塞 accept。监听 socket 被 <see cref="Close"/> 后返回 null。</summary>
    public static HvSocketStream? Accept(IntPtr listener)
    {
        var peer = new SockaddrHv();
        int len = Marshal.SizeOf<SockaddrHv>();
        IntPtr c = accept(listener, ref peer, ref len);
        return c == InvalidSocket ? null : new HvSocketStream(c);
    }

    public static void Close(IntPtr s) => closesocket(s);

    private static int _winsockReady;

    /// <summary>借 .NET 的 Socket 完成一次 WSAStartup（进程内只需一次）。</summary>
    private static void EnsureWinsock()
    {
        if (Interlocked.Exchange(ref _winsockReady, 1) == 1) return;
        using var dummy = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }

    // ---- HvSocketStream 用的原语 ----

    /// <summary>等到可读（含对端关闭 / 出错）返回 true；超时返回 false。</summary>
    internal static bool PollReadable(IntPtr s, int timeoutMs)
    {
        var fd = new WsaPollFd { Fd = s, Events = POLLRDNORM };
        int n = WSAPoll(ref fd, 1, timeoutMs);
        if (n < 0) return true; // 出错：让随后的 recv 报出来
        return n > 0;
    }

    internal static int Recv(IntPtr s, Span<byte> buf, bool peek = false)
    {
        return recv(s, ref MemoryMarshal.GetReference(buf), buf.Length, peek ? MSG_PEEK : 0);
    }

    internal static int Send(IntPtr s, ReadOnlySpan<byte> buf)
    {
        return send(s, ref MemoryMarshal.GetReference(buf), buf.Length, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrHv
    {
        public ushort Family;
        public ushort Reserved;
        public Guid VmId;
        public Guid ServiceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WsaPollFd
    {
        public IntPtr Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern IntPtr WSASocketW(int af, int type, int protocol, IntPtr info, uint group, uint flags);
    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int bind(IntPtr s, ref SockaddrHv addr, int len);
    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int listen(IntPtr s, int backlog);
    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern IntPtr accept(IntPtr s, ref SockaddrHv addr, ref int len);
    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int closesocket(IntPtr s);
    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAPoll(ref WsaPollFd fds, uint count, int timeoutMs);
    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int recv(IntPtr s, ref byte buf, int len, int flags);
    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int send(IntPtr s, ref byte buf, int len, int flags);
}

/// <summary>
/// 一条已接入的 Hyper-V socket 连接。读是「轮询可读 + recv」，所以能被 CancellationToken 真正取消
/// （不会留下一个悬着的 recv 去偷下一帧的字节）；写是阻塞 send。每条连接的读写各占一个线程池线程，
/// WSL 桥的连接数很少（一次 sudo / 一次签名一条），不值得上 IOCP。
/// </summary>
internal sealed class HvSocketStream : Stream
{
    private const int PollSliceMs = 100;
    private IntPtr _s;
    private int _closed;

    internal HvSocketStream(IntPtr s) => _s = s;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadCore(buffer.AsSpan(offset, count), CancellationToken.None);

    public override void Write(byte[] buffer, int offset, int count)
        => WriteCore(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => new(Task.Run(() => ReadCore(buffer.Span, ct), ct));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => new(Task.Run(() => WriteCore(buffer.Span), ct));

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    /// <summary>
    /// 等对端关闭（或连接出错）。只窥视不消费：期间对端若发来数据，原样留给后续的读。
    /// 指纹门等待期间用它发现「PAM 里 Ctrl+C / ssh 被杀」，好取消设备上的门。
    /// </summary>
    public Task WaitPeerClosedAsync(CancellationToken ct) => Task.Run(() =>
    {
        Span<byte> one = stackalloc byte[1];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _closed) != 0) return;
            if (!HvSocket.PollReadable(_s, PollSliceMs)) continue;
            int n = HvSocket.Recv(_s, one, peek: true);
            if (n <= 0) return;          // 0 = 对端关闭；<0 = 连接坏了
            Thread.Sleep(PollSliceMs);   // 有数据待读：不是断开，别空转
        }
    }, ct);

    /// <summary>
    /// 逐字节读一行（不含换行符），绝不多读——前导行后面紧跟着的是原始协议字节流，不能吞。
    /// 超长或对端先关闭返回 null。
    /// </summary>
    public Task<string?> ReadLineAsync(int maxBytes, CancellationToken ct) => Task.Run(() =>
    {
        var line = new byte[maxBytes];
        Span<byte> one = stackalloc byte[1];
        for (int i = 0; i < maxBytes; i++)
        {
            if (ReadCore(one, ct) == 0) return null;
            if (one[0] == (byte)'\n') return System.Text.Encoding.UTF8.GetString(line, 0, i);
            line[i] = one[0];
        }
        return (string?)null;
    }, ct);

    private int ReadCore(Span<byte> buf, CancellationToken ct)
    {
        if (buf.Length == 0) return 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(nameof(HvSocketStream));
            if (!HvSocket.PollReadable(_s, PollSliceMs)) continue;
            int n = HvSocket.Recv(_s, buf);
            if (n < 0) throw new IOException("hvsocket recv 失败", new Win32Exception(Marshal.GetLastWin32Error()));
            return n;
        }
    }

    private void WriteCore(ReadOnlySpan<byte> buf)
    {
        while (buf.Length > 0)
        {
            if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(nameof(HvSocketStream));
            int n = HvSocket.Send(_s, buf);
            if (n <= 0) throw new IOException("hvsocket send 失败", new Win32Exception(Marshal.GetLastWin32Error()));
            buf = buf[n..];
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            HvSocket.Close(_s);
            _s = new IntPtr(-1);
        }
        base.Dispose(disposing);
    }
}
