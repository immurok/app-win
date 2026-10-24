namespace ImmurokCommon.Protocol;

/// <summary>
/// Client ↔ Service 文本行协议常量。权威定义来自 macOS <c>PAMSocketServer.swift</c>。
/// 帧格式：<c>[4 字节小端长度][UTF-8 文本]</c>（见 <see cref="IpcFraming"/>）。
/// </summary>
public static class IpcProtocol
{
    // ---- 请求动词 ----
    public const string Status      = "STATUS";
    public const string Info        = "INFO";          // -> OK:<paired>:<battery>:<fw>:<fpcount>
    /// <summary>链路层当前生效的连接参数（BLE 原生单位）。CONNPARAMS -&gt; OK:&lt;interval×1.25ms&gt;:&lt;latency&gt;:&lt;timeout×10ms&gt;；旧固件 ERROR。</summary>
    public const string ConnParams  = "CONNPARAMS";
    public const string Auth        = "AUTH";          // AUTH:username:service
    public const string Fp          = "FP";            // FP:LIST | FP:ENROLL:slot | FP:DELETE:slot | FP:STATUS
    public const string Ota         = "OTA";           // OTA:INFO|ERASE|HEADER|WRITE|VERIFY|END|VERSION
    public const string Pair        = "PAIR";          // PAIR:STATUS | PAIR:START | PAIR:RESET
    public const string Slot        = "SLOT";          // SLOT:STATUS | SLOT:CLEAROWN | SLOT:CLEAR:<n>（双主机）
    public const string Pass        = "PASS";          // PASS:STATUS | PASS:SET:<b64user>:<b64pass> | PASS:CLEAR
    public const string Key         = "KEY";           // KEY:LIST:<cat> | KEY:OTP:<idx> | KEY:DELETE:<cat>:<idx> | KEY:SSHPUB:<idx>
    public const string SshAgent    = "SSHAGENT";      // SSHAGENT:STATUS -> OK:ON|OFF ; SSHAGENT:ON -> OK:TAKEN|ABSENT|TAKEOVER_FAILED|CONTENDED ; SSHAGENT:OFF -> OK:RESTORED|NONE|RESTORE_FAILED
    public const string AgentApprove = "AGENT_APPROVE"; // 本期暂不实现
    /// <summary>取消进行中的指纹门（认证弹窗点「取消」时用），让设备立刻停止闪灯等待。-&gt; OK</summary>
    public const string CancelGate  = "CANCELGATE";
    /// <summary>功能开关。FEATURE:GET -&gt; OK:&lt;unlock&gt;:&lt;lock&gt;:&lt;ssh&gt;:&lt;agent&gt;:&lt;otp&gt;（各 0/1）；FEATURE:SET:&lt;name&gt;:&lt;0|1&gt; -&gt; OK</summary>
    public const string Feature     = "FEATURE";
    /// <summary>指纹注入信号轮询。INJECT:POLL -&gt; OK:&lt;pageId&gt;（有一次非锁屏态指纹匹配可用于注入，取用并消费）| OK（无）。</summary>
    public const string Inject      = "INJECT";
    public const string Security    = "SECURITY";      // SECURITY:STATUS -> OK:cp_pipe=..;service_pipe=..;caller_check=..;data_acl=..;pairing_scope=..;owner=..
    public const string SecurityStatus = "STATUS";
    public const string InjectPoll  = "POLL";

    // FEATURE 子命令与功能名
    public const string FeatureGet    = "GET";
    public const string FeatureSet    = "SET";
    public const string FeatureUnlock = "unlock";
    public const string FeatureLock   = "lock";
    public const string FeatureSsh    = "ssh";
    public const string FeatureAgent  = "agent";
    public const string FeatureOtp    = "otp";

    // KEY 子命令（密钥库：cat 0=SSH 1=OTP 2=API）
    public const string KeyList   = "LIST";    // -> OK:<idx,b64name,b64extra>;...（extra=OTP服务/空）
    public const string KeyOtp    = "OTP";     // KEY:OTP:<idx> -> OK:<6位> | DENY
    public const string KeyDelete = "DELETE";  // KEY:DELETE:<cat>:<idx> -> OK | DENY
    public const string KeySshPub = "SSHPUB";  // KEY:SSHPUB:<idx> -> OK:<b64 authkey行>:<b64 指纹>
    public const string KeyAddOtp = "ADDOTP";  // KEY:ADDOTP:<b64name>:<b64service>:<b64secretBase32> -> OK | DENY
    public const string KeyAddApi = "ADDAPI";  // KEY:ADDAPI:<b64name>:<b64value> -> OK | DENY
    public const string KeySshGen = "SSHGEN";  // KEY:SSHGEN:<b64name> -> OK:<b64 authkey行>:<b64 指纹> | DENY
    public const string KeyUpdate = "UPDATE";  // KEY:UPDATE:<cat>:<idx>:<b64name>:<b64service> -> OK | DENY（仅改 name[+service]，密文靠固件 staging 保留）

    // FP 子命令
    public const string FpList   = "LIST";
    public const string FpSlots  = "SLOTS";  // FP:SLOTS -> OK:<bitmap 整数>
    public const string FpEnroll = "ENROLL";
    public const string FpDelete = "DELETE";
    public const string FpStatus = "STATUS";

    // PASS 子命令
    public const string PassStatus = "STATUS"; // -> OK:CONFIGURED | OK:NOTSET
    public const string PassSet    = "SET";    // PASS:SET:<b64user>:<b64pass> -> OK
    public const string PassClear  = "CLEAR";  // -> OK

    // PAIR 子命令
    public const string PairStatus = "STATUS";
    public const string PairStart  = "START";
    public const string PairReset  = "RESET";

    // SLOT 子命令（双主机）
    public const string SlotStatus   = "STATUS";   // -> OK:<supported 0/1>:<bitmap>:<active>
    public const string SlotClearOwn = "CLEAROWN"; // -> OK | REJECT
    public const string SlotClear    = "CLEAR";    // SLOT:CLEAR:<n> -> OK | DENY（指纹未过）

    // OTA 子命令
    public const string OtaInfo    = "INFO";
    public const string OtaVersion = "VERSION";
    public const string OtaErase   = "ERASE";
    public const string OtaHeader  = "HEADER";  // OTA:HEADER:<size>:<base64>
    public const string OtaWrite   = "WRITE";   // OTA:WRITE:<offset>:<base64>
    public const string OtaVerify  = "VERIFY";
    public const string OtaEnd     = "END";
    public const string OtaPush    = "PUSH";    // OTA:PUSH:<base64 整个 .imfw>（流式，回 PROGRESS: 百分比）

    // ---- 响应 ----
    public const string Ok      = "OK";        // 亦有 OK:count / OK:DELETED / OK:ENROLL_STARTED / OK:IDLE 等
    public const string Deny    = "DENY";
    public const string DenyNotOwner = "DENY:NOT_OWNER"; // 调用方 / 目标会话不是设备 owner
    public const string DenyGateRejected = "DENY:GATE_REJECTED"; // 设备指纹门：按错 / 被拒
    public const string DenyGateTimeout  = "DENY:GATE_TIMEOUT";  // 设备指纹门：超时 / 取消
    public const string Skip    = "SKIP";
    public const string Busy    = "BUSY";
    public const string Timeout = "TIMEOUT";
    public const string Reject  = "REJECT";

    // 错误族（ERROR:<REASON>）
    public const string Error = "ERROR";
    public const string ErrInvalidFormat   = "ERROR:INVALID_FORMAT";
    public const string ErrUnknownCommand  = "ERROR:UNKNOWN_COMMAND";
    public const string ErrNotConnected    = "ERROR:NOT_CONNECTED";
    public const string ErrInvalidSlot     = "ERROR:INVALID_SLOT";
    public const string ErrEnrollFailed    = "ERROR:ENROLL_FAILED";
    public const string ErrDeleteFailed    = "ERROR:DELETE_FAILED";
    public const string ErrOtaNotAvailable = "ERROR:OTA_NOT_AVAILABLE";
    public const string ErrHmacMismatch    = "ERROR:HMAC_MISMATCH";
    public const string ErrCallerNotTrusted = "ERROR:CALLER_NOT_TRUSTED"; // 调用方不在安装目录 / 签名不符（§3.3）

    public const char Sep = ':';
}
