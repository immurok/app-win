namespace ImmurokCommon.Ble;

/// <summary>
/// 写入 CMD 特征的命令操作码。权威值来自 <c>BLEManager.swift</c> 的 <c>ImmurokCommand</c> 枚举。
/// 封包格式：<c>[command:1][length:1][payload...]</c>。
/// </summary>
public enum ImmurokCommand : byte
{
    GetStatus    = 0x01,
    GetBattRaw   = 0x02,
    GetConnParams = 0x03, // 链路层实际连接参数，固件 1.7.2+；配对前也可发

    EnrollStart  = 0x10,
    EnrollCancel = 0x11,
    DeleteFp     = 0x12,
    FpList       = 0x13,

    FpMatchAck   = 0x22,

    PairInit     = 0x30,
    PairConfirm  = 0x31,
    PairStatus   = 0x32,
    AuthRequest  = 0x33,
    PairButton   = 0x34,
    GateCancel   = 0x37,
    Challenge    = 0x38,

    SlotStatus   = 0x39,
    SlotClear    = 0x3C,

    // 片上密钥库（SSH/TOTP/API）—— 本期不实现，仅保留常量以备后续
    KeyCount     = 0x60,
    KeyRead      = 0x61,
    KeyWrite     = 0x62,
    KeyDelete    = 0x63,
    KeyCommit    = 0x64,
    KeySign      = 0x65,
    KeyGetPub    = 0x66,
    KeyGenerate  = 0x67,
    KeyResult    = 0x68,
    KeyOtpGet    = 0x69,
}

/// <summary>
/// 响应/通知里的状态码。权威值来自 <c>BLEManager.swift</c> 的 <c>ImmurokStatus</c>。
/// 注意：不同命令响应里状态码可能出现在 <c>payload[0]</c> 或 <c>payload[1]</c>，解析处按命令区分。
/// </summary>
public enum ImmurokStatus : byte
{
    Ok              = 0x00,
    ErrTimeout      = 0x06,
    ErrFpNotMatch   = 0x07,
    WaitFingerprint = 0x11,
    ErrWaitButton   = 0xF0, // PAIR_INIT：等待设备物理按键确认
    ErrNeedsReset   = 0xF1, // PAIR_INIT：设备仍有指纹，需先复位
    ErrBusy         = 0xFD,
    ErrInvalidParam = 0xFE,
    ErrUnknown      = 0xFF,
}

/// <summary>指纹录入进度事件。通知帧 <c>payload[0]==0x11 &amp;&amp; len==4</c> 时，<c>payload[1]</c> 为事件码。</summary>
public enum FpEnrollEvent : byte
{
    Waiting    = 0x00,
    Captured   = 0x01,
    Processing = 0x02,
    LiftFinger = 0x03,
    Complete   = 0x04,
    Overlap    = 0x06, // 重叠过多，移指重按
    Failed     = 0xFF, // 含 0xFD / 0xFE
}

/// <summary>RSP 特征上的异步通知首字节。</summary>
public static class NotifyOpcode
{
    public const byte SignedFpMatch = 0x21;   // [0x21][page_id:2B LE][hmac:8B] = 11B
    public const byte FpMatchEvent  = 0x23;   // 1B
    public const byte EnrollProgress = 0x11;  // 4B
    public const byte PairStatus    = 0xF0;   // 6B
    public const byte LinkParams    = 0xE1;   // 1B：BLE 连接参数不达标
}
