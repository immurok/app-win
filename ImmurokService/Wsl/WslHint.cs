namespace ImmurokService.Wsl;

/// <summary>
/// 指纹门进度提示帧：服务 → WSL 桥，夹在 ssh-agent 连接的应答方向里。
/// 桥把它画到发起签名的 ssh 的终端上，然后**截掉**，不转给 ssh。
///
/// 为什么需要：门里发生的事（按错、还剩几次、已通过正在签名、最后为什么失败）只有服务知道，
/// 桥只看得到签名请求和最终应答；而能摸到 WSL 终端的只有桥。
///
/// 帧格式沿用 ssh-agent 的 <c>[uint32 BE 长度][类型][正文]</c>，类型取协议未分配的 0xF1，
/// 正文两个字节 <c>[code][arg]</c>。只有枚举码过线，文案归桥所有：终端会解释写进去的字节，
/// 桥又是 root，不能给任何自由文本留通道。
///
/// 只在桥的前导行声明了 <c>hints=1</c> 时才发：不认识这种帧的旧桥会把它原样转给 ssh，
/// ssh 会当成签名应答读，直接坏掉。
/// </summary>
internal static class WslHint
{
    public const byte FrameType = 0xF1;

    /// <summary>门已打开，等触摸。</summary>
    public const byte Waiting = 1;
    /// <summary>指纹不匹配；arg = 剩余次数。</summary>
    public const byte Mismatch = 2;
    /// <summary>触摸通过，设备正在签名。</summary>
    public const byte Signing = 3;
    /// <summary>最终失败：指纹被拒（次数用尽）。紧跟其后的是 SSH_AGENT_FAILURE。</summary>
    public const byte FailRejected = 4;
    /// <summary>最终失败：等触摸超时。</summary>
    public const byte FailTimeout = 5;
    /// <summary>最终失败：其他原因（链路参数、断连、设备报错）。</summary>
    public const byte FailOther = 6;

    public static byte[] Frame(byte code, byte arg = 0) => new byte[] { 0, 0, 0, 3, FrameType, code, arg };
}
