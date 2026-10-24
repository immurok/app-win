# Windows：IPC 加固设计（管道抢占、实例抢占、调用方鉴权、落盘密钥）

日期：2026-09-03
状态：设计稿，待 Windows 端实现（需 Windows VM，CP 改动务必在带快照的 VM 里调）
**动手前先读 §9** —— Linux 端同类工作已落地，那边八个问题设计稿一个都没预见到，类型高度可迁移。
2026-09-06 修订（按 `app-win` 代码逐条核对后回写）：§1.3 关于 `KEY:OTP` 的描述原先不准确，改为 §1.5 的固件 cooldown 问题（三端共有）；§3.1 补「Service 建管道、CP 来连」的反向方案与抢占后的确定行为；§3.2 补 `CreateNewInstance` 为什么能去掉；§3.3 的表按 §9.1 重写并明确为纵深防御；新增 §3.5（owner SID，已是现成 bug）、§3.6（固件侧待办：授权时声明预算，**本轮不做**）、§3.7（日志）；§10 实施次序细化。
来源：macOS 端「PAM 信道 nonce+HMAC」已合并（imPress-v1 主仓库 `docs/superpowers/specs/2026-09-03-pam-channel-mac-design.md`）。Windows 架构不同（Service 跑 SYSTEM 自己做 BLE，没有「特权组件信任用户态回复」这一层），**同一类问题在 Windows 上以四种形式出现**，本文逐个给出修法。

## 1. 现状与问题

### 1.1 CP 管道可被抢占，登录密码明文可被截获（最严重）

- 方向是 **Service → CP**：`ImmurokService/System/ScreenUnlocker.cs:23-56` 用 `WaitNamedPipe` + `CreateFile(GENERIC_WRITE)` 连 `\\.\pipe\ImmurokCredentialProvider`，一次 `WriteFile` 写入 UTF-16LE `username\0password\0` 明文（`ScreenUnlocker.cs:42`）。
- CP 端 `CPipeListener.cpp:118-145` 创建管道时带 DACL `D:(A;;GA;;;SY)(A;;GA;;;BA)`、`PIPE_ACCESS_INBOUND`、1 个实例。但管道名是全局命名空间、先到先得：CP 只在 `CPUS_LOGON` / `CPUS_UNLOCK_WORKSTATION` 时才创建（`CSampleProvider.cpp:95-152`），用户登录后到锁屏前这段时间没有任何人持有这个名字。普通用户进程在这期间 `CreateNamedPipe` 同名管道，DACL 想写多宽写多宽；等用户锁屏、触摸指纹，Service 连上的就是攻击者的实例，`WriteFile` 把 Windows 登录密码交出去。
- Service 侧对管道服务端零校验：没有 `GetNamedPipeServerProcessId`，没有挑战。
- 附带：`ImmurokCredentialProvider/README.md:28` 仍写「NULL DACL」，落后于代码。

### 1.2 Service 管道的实例可被抢占，客户端命令可被中间人

- `\\.\pipe\immurok` 的 `PipeSecurity` 给 **Authenticated Users** 授了 `ReadWrite | CreateNewInstance`（`ImmurokService/Ipc/PipeServer.cs:123-125`）；`\\.\pipe\immurok-cli` 同样（`ImmurokService/Ipc/CliServer.cs:82-87`）。
- `CreateNewInstance` 意味着任意用户进程能创建同名管道的额外实例。客户端 `NamedPipeClientStream` 连接时拿到哪个实例由系统分配，攻击者实例会收到 WPF 客户端发的完整命令，包括 `PASS:SET:<b64user>:<b64pass>`（`CommandHandlers.cs:631-647`）里的明文密码。

### 1.3 Service 不鉴别调用方，任何进程都能改安全状态、读秘密

- `PipeServer.HandleConnectionAsync`（`PipeServer.cs:61-113`）和 `CommandHandlers.HandleAsync`（`CommandHandlers.cs:40-74`）不调用 `GetNamedPipeClientProcessId`，无 impersonation，无签名校验。
- 任意本机进程可执行：`PASS:SET` / `PASS:CLEAR`（改/清解锁密码）、`KEY:ADDOTP/ADDAPI/SSHGEN/DELETE/UPDATE`、`FEATURE:SET`（关掉保护开关、启停 SSH agent，`CommandHandlers.cs:80-114`）、`PAIR:START/RESET`、`SLOT:CLEAR*`。这些在 macOS 上要么走指纹门（`imk get`）要么只有 App 自己能发。
- `KEY:OTP:<idx>` / `KEY:DELETE` / imk 管道的 `GET:otp|api:<name>` **并非**无门：`BleManager.GetOtpCodeAsync`（`BleManager.cs:1243-1259`）收到 `WAIT_FP` 会走 `RunFpGateAsync`，门在固件侧（`firmware/APP/hidkbd.c:6506`）。真正的问题是固件的 cooldown 可被无限续期，见 §1.5——那比「无门」更糟，且三端共有。（初稿此处写成「直接返回明文」，已订正。）
- `PASS:SET` 的 user 字段为空时回落到 `Environment.UserName`（`CommandHandlers.cs:643`）——Service 跑在 SYSTEM 下，取到的是 `"SYSTEM"`，不是交互用户。见 §3.5。
- `\\.\pipe\immurok-cli` 的 `APPROVE:<command>` 只看功能开关（`CliServer.cs:125-137`）。

### 1.4 落盘密钥机器级可解

- `pairing.dat`（BLE `shared_key`）用 `DataProtectionScope.LocalMachine`（`ImmurokService/Security/PairingStore.cs:30-32, 51-62`），同机任意进程可解密；`%ProgramData%\immurok` 目录未设 ACL（`PairingStore.cs:24-25`、`AppSettings.cs:30-32`），默认 Users 可读。
- `settings.json` 明文无 ACL。
- 解锁密码在 Credential Manager 里以 SYSTEM 身份写入（`CredentialStore.cs:20-43`），这一项本身是对的。
- `\\.\pipe\openssh-ssh-agent` 没有显式 `PipeSecurity`（`SshAgentServer.cs:83-85`），用的是默认描述符。

### 1.5 固件指纹门的 cooldown 可被无限续期（三端共有）

`firmware/APP/hidkbd.c:5363` 的 `fp_gate_needed()`：同类别通过一次门之后 10s 内免门（`FP_GATE_COOLDOWN_MS`），**且每次免门通过都把时间戳刷新到当下**（`s_fp_gate_last[cat] = now;  // rolling within category`）。`KEY_OTP_GET`（`hidkbd.c:6505`）、API secret 读取、`KEY_SIGN` 都显式接受 `FP_CAT_AUTH` 的 cooldown——注释写明是为了让 `imk run --agent` 覆盖整个 agent 会话不重复要指纹。

合起来：任何能向 Service 发命令的本机进程，只要在用户一次真实触摸之后以 <10s 的间隔轮询 `KEY:OTP` / `GET:api:*` / SSH 签名，就能**无限续期**，把全部 OTP、API secret 抽干、任意签名，直到 BLE 断连（`hidkbd.c:5177` 断连时清零）才停。这不是 Windows 独有：三端 daemon 只要同用户进程能发命令，结果一样。固件侧的根治记在 §3.6（待办，本轮不做）；本轮 Windows 侧用 §3.3 的命令分级 + Service 自己计数来缓解。

## 2. 目标与信任边界

目标：普通用户进程不能截获登录密码、不能冒充 Service 或 CP、不能在没有指纹的情况下改安全状态或读秘密、不能解密落盘密钥。

信任边界：`ImmurokService`（LocalSystem）+ LogonUI 里的 CP + `%ProgramData%\immurok`（ACL 收紧后）。攻击者模型：交互用户身份下的任意进程，含标准用户和未提权的管理员进程；不含已提权的管理员和 SYSTEM。

## 3. 修法

按优先级排列，1 和 2 是安全补丁，3 是 macOS 同类工作的 Windows 版，4 是纵深。

### 3.1 CP 管道：Service 写凭据前校验对端进程

状态：第 1-5 条**已实现**（2026-09-06，`System/ScreenUnlocker.cs` `VerifyPeer`，复用 `Ipc/PipeCaller.IdentifyProcess`；编译通过，**未在 VM 上跑过**）。实现细节：进程句柄持有到 `WriteFile` 完成，防 pid 复用；三条判据任一取不到信息即按失败；结果存 `ScreenUnlocker.LastPeerCheck` / `LastPeerCheckDetail`（pid / 映像路径 / 会话，不含凭据）供 §4 的 `SECURITY:STATUS` 读取；失败走 ILogger（Serilog 文件日志）——Service 目前没有接 Windows 事件日志 sink，接不接另议。CP 侧 `FIRST_PIPE_INSTANCE` 是 §10 第 3 步，未动。

在 `ScreenUnlocker.Unlock()` 连上管道之后、`WriteFile` 之前：

1. `GetNamedPipeServerProcessId(hPipe, out pid)`。
2. `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, pid)` → `OpenProcessToken(TOKEN_QUERY)` → `GetTokenInformation(TokenUser)`，SID 必须是 `S-1-5-18`（LocalSystem）。普通用户进程不可能以 SYSTEM 运行，这一条就足够。
3. 再查 `QueryFullProcessImageName` 必须是 `%SystemRoot%\System32\LogonUI.exe`（作为第二道，不作为唯一依据）。
4. `GetNamedPipeServerSessionId` 必须等于当前要解锁的会话（`WTSGetActiveConsoleSessionId`）。
5. 任一不满足：不写、记事件日志（`EventLog` Warning，含 pid / 映像路径 / 会话），并在 Service 里置一个 `cpPipeSquatted` 状态供客户端展示。

同时 CP 侧 `CreateNamedPipeW` 加 `FILE_FLAG_FIRST_PIPE_INSTANCE`（`CPipeListener.cpp:137-144`）：名字已被占时 CP 会得到 `ERROR_ACCESS_DENIED`，此时 CP 记日志并退化为不显示指纹磁贴，而不是静默失败。

上面 1-5 是**止血**：抢占仍然会发生，只是密码不再泄露。它有一个初稿没写清的后果：抢占者的实例与 CP 的实例并存时，`CreateFile` 连到哪个由系统分配，Service 可能反复连到攻击者那个，表现为指纹解锁**彻底不可用**而不是偶发失败。所以校验失败后的行为要定死：

- 不重试其他实例（重试也是随机的，只会拖长锁屏等待）；置 `cpPipeSquatted`，本次解锁放弃，用户回退密码。
- 下一次锁屏事件到来时重新尝试一次并刷新状态，不在同一次锁屏里循环。

**根治方向：Service 建管道、CP 来连。** 初稿否掉了反向连接，理由是「要处理 Service 未就绪、重连、超时，改动大且 CP 崩溃会锁死登录」。回头看这个否定下得太快——反向是唯一能**消灭**抢占窗口而不是「事后检测」的做法：

- Service 是 `start= auto` 的 LocalSystem 服务，开机时早于任何交互登录。它在启动时用 `FILE_FLAG_FIRST_PIPE_INSTANCE` 建一条专用管道 `\\.\pipe\immurok-cp`，DACL 只给 SYSTEM（`D:(A;;GA;;;SY)`），`CreateNewInstance` 同样只给 SYSTEM。名字从开机起被 SYSTEM 占住，普通用户进程连创建同名管道都会 `ERROR_ACCESS_DENIED`。
- CP 在 `CPUS_LOGON` / `CPUS_UNLOCK_WORKSTATION` 时作为客户端连接，连上后 `GetNamedPipeServerProcessId` + SYSTEM SID 校验（与上面第 2 条对称），然后阻塞读一帧凭据；Service 在指纹通过时向已连接的 CP 实例（正常只有一个，按会话 id 挑）写凭据。
- 「CP 崩溃锁死登录」这个理由其实是反的：CP 当客户端时失败只是连不上→不显示磁贴，比它当服务端（`CreateNamedPipe` 失败还得自己退化）风险更低。Service 未就绪同样只是不显示磁贴。
- 代价：CP 多一个连接线程、Service 维护连接集合、一次协议改版（旧 CP + 新 Service 必须还能走旧管道，见 §5）。

建议：先做止血（1-5，只动 Service，§10 第 1 步），反向管道作为第二阶段，在同一个 VM 快照上验完锁屏 / 开机登录 / RDP 三条路径后替换。

### 3.2 Service 管道：去掉实例抢占，加 FirstPipeInstance

状态：**已实现并在开发机验收**（2026-09-06，`Ipc/PipeAcl.cs` 共用 ACL；`PipeServer` / `CliServer` / `SshAgentServer` 三条管道）。实测：`\\.\pipe\immurok` 的 DACL 从 `(A;;0x12019f;;;AU)` 变为 `(A;;0x12019b;;;AU)`（0x4 = CreateNewInstance 已去掉）；普通用户建额外实例得 `UnauthorizedAccessException`；`AUTH` 占着连接 30s 期间并发 `STATUS` 正常应答——直接证明原注释「没有 CreateNewInstance 并发建实例会炸」在服务身份下不成立。与稿子的两处差异：(1) 名字被占时**不退出进程**，每 5s 重试、每次记 error、置 `Contended`——BLE 与锁屏解锁不依赖这条管道，退出等于把指纹解锁一起交给抢占者；(2) `PipeAcl` 顺带给本进程用户 FullControl，控制台调试模式（非 SYSTEM）下后续实例才建得出来。顺手修了 `PipeServer.DisposeAsync` 不可重入导致每次停服务都报「异常终止」的老 bug。

- `PipeServer.cs:118-129` 与 `CliServer.cs:82-87`：Authenticated Users 只授 `ReadWrite`，**去掉 `CreateNewInstance`**；`CreateNewInstance` 只给 `LocalSystemSid`。
- **为什么能去掉**——`PipeServer.cs:119-121` 有一条注释说这个权限是必须的，否则并发创建下一个实例会抛 `UnauthorizedAccessException`（`PAIR:START` 占着实例时触发）。照本节改、第一次跑 `PAIR:START` 就炸、然后被当成回归改回去，是可以预见的。这条注释是误诊：创建后续实例的访问检查对的是**创建者**的 token，Service 是 LocalSystem（`install.ps1:37`、`packaging/immurok.iss:107`），DACL 里 SYSTEM 有 `FullControl`（值 2032031，含 `CreateNewInstance` = 4）。当时撞到的异常几乎可以肯定是在控制台调试模式下以普通用户身份跑 Service 时出现的。改的时候把那条注释一并替换成本段理由，并在 VM 上以真正的服务身份跑一次 `PAIR:START` + 并发 `STATUS` 作为验收。
- 第一个实例用 `PipeOptions.FirstPipeInstance`（.NET 6+ 有；若目标框架没有，用 `NamedPipeServerStreamAcl.Create` + P/Invoke `CreateNamedPipe` 带 `FILE_FLAG_FIRST_PIPE_INSTANCE`），创建失败即名字被占，Service 记事件日志并每 5 秒重试，不要静默降级。**这个 flag 只能给第一个实例**：accept 循环每次迭代都新建实例，第二次起必须不带，否则每次都失败。
- `SshAgentServer.cs:83-85` 补同样的显式 `PipeSecurity`。

### 3.3 Service 命令鉴权：按调用方 + 指纹门（2026-09-06 按 §9.1 重写）

状态：**已实现并在开发机验收**（2026-09-06，`Ipc/CallerPolicy.cs` + `CommandHandlers.AuthorizeAsync`，PipeServer 在分派前统一调用，CliServer 对 `APPROVE` / `CANCEL` 同样校验）。实测：普通用户的 powershell.exe（不在安装目录）发 `AUTH` / `FEATURE:SET` / `FP:ENROLL` / `PASS:SET` / `PAIR:RESET` / imk `APPROVE` 全部 `ERROR:CALLER_NOT_TRUSTED`，`PASS:STATUS` / `KEY:LIST` 放行；安装目录里的 imk.exe `imk run --agent` 照常。与下文的差异：

- **Service 未签名时**（开发构建），`CallerPolicy` 退化为只看「映像路径在 Service 自己的安装目录下」（`Mode = path-only`，日志有警告）；Service 自身带签名时启用 `WinVerifyTrust` + 发布者指纹比对（`signed`）。2026-10 起正式安装包经 Azure Artifact Signing 签名，已在签名构建上实测生效：放在安装目录子目录里的未签名客户端被拒 `ERROR:CALLER_NOT_TRUSTED`（2026-10-24，试设置向导时撞上）。安装目录取 `AppContext.BaseDirectory` 而不是写死 `%ProgramFiles%`，§9.7 的 x86/x64 路径问题随之消失。
- **只对「持久写入」要主机侧门**（2026-09-06 晚用户决定收窄）：`PASS:SET`（写凭据管理器）、`OTA:PUSH`（刷固件）先发 `AUTH_REQUEST`（固件对它永远要新触摸）。`FEATURE:SET:*:1` 和 `SSHAGENT:ON` **不要门**——开关本身不放行任何秘密：解锁推密码、SSH 签名、读 OTP 各自仍要触摸，攻击者替用户打开开关什么也拿不到，加门只是每次开开关多摸一次，还引出过客户端串行锁冻界面一分钟的事故（见下）。关掉功能、清密码、解绑设备（`PASS:CLEAR`、`FEATURE:SET:*:0`、`SSHAGENT:OFF`、`PAIR:RESET`）同样不要门——收缩攻击面，设备丢了正需要能做这些——只受 owner 校验（§3.5）。这一条与下表不同，以本段为准。
- 固件已设门的命令（`FP:ENROLL`（已有指纹时）/`FP:DELETE`、`KEY:DELETE/ADD*/SSHGEN/UPDATE`、`SLOT:CLEAR`、`PAIR:START` 的配对流程）不再叠一道主机门，否则每个动作要摸两次；残余风险是固件 cooldown 10s 内的重放（§3.6 待办）。
- 客户端：所有要过门的操作一律套现成的 `FpAuthDialog`（30s 倒计时 + 取消，取消发 `CANCELGATE`），`PASS:SET` 已接上，管道超时 35s。踩过的坑：`PipeClient` 原有一把 `SemaphoreSlim(1,1)` 把同实例所有请求串行化，一条等门的请求会把页面查询、按钮点击、连 `CANCELGATE` 都堵在后面 30s（实测两次门超时 → 设置页停在「查询中」、「清除」一分钟后才生效；等待窗的「取消」实际到不了服务端）。已去掉这把锁，服务端管道多实例、并发短连接没问题。

Windows 同用户的两个进程在 ACL 上无法区分，所以分两层。先说定性：**第一层是纵深防御，不是边界**（§9.1：同用户、同完整性级别的进程可以 `OpenProcess` + 注入合法客户端，从里面发命令）。真正的边界只有设备上的那次触摸，所以**凡是重放一百次会让用户处境变糟的命令，都必须自己带指纹门**，`CallerKind` 只决定「要不要理你」。

**第一层：调用方进程校验**（`PipeServer.HandleConnectionAsync` 里，每个连接一次）：
- 链条顺序固定，避免 TOCTOU：`GetNamedPipeClientProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` 拿到 `HANDLE` 并**持有到连接结束**（pid 复用就此规避）→ `QueryFullProcessImageName(handle)` → 路径必须在 `%ProgramFiles%\immurok\`（或 `%ProgramFiles(x86)%`，按构建目标，§9.7）下——安装目录只有管理员可写，这一条挡的是「用户在自己可写的目录放一个签名合法的旧版客户端」→ 对**该路径**做 `WinVerifyTrust`，发布者证书指纹与 Service 自身一致。
- `WinVerifyTrust` 验的是磁盘文件，进程内存可能早已被注入——这就是为什么它只是减速带。未签名的开发构建用 Service 启动参数 `--allow-unsigned-clients` 放行，安装包不传，`SECURITY:STATUS` 报 `caller_check: off`。
- 校验结果作为 `CallerKind`（`TrustedClient` / `Other`）传给 `CommandHandlers`；代码注释里写「纵深防御」，不要写「信任边界」。

**第二层：按命令分级**（`IpcProtocol.cs:10-99` 的命令族 + `CliServer.cs` 的 imk 命令）。「门」= 设备指纹门（`AUTH` 同款 `RunFpGateAsync`），由固件执行，Service 不能替它作答；门失败 / 超时分别回 `DENY:GATE_REJECTED` / `DENY:GATE_TIMEOUT`，不退化：

| 命令 | `TrustedClient` | `Other` | 为什么 |
|---|---|---|---|
| `STATUS`、`INFO`、`SECURITY:STATUS`、`FEATURE:GET`、`PASS:STATUS`、`PAIR:STATUS`、`SLOT:STATUS`、`FP:LIST/SLOTS/STATUS`、`KEY:SSHPUB`、`OTA:INFO/VERSION` | 允许 | 允许 | 只读，不泄密 |
| `KEY:LIST`、imk 管道 `LIST:*` | 允许 + **owner 校验** | 允许 + **owner 校验** | 只读，但 2026-09-06 多用户实测后按用户要求也只给 owner：值拿不到，密钥名也没必要给另一个账号看 |
| `KEY:OTP:<idx>`、imk 管道 `GET:otp\|api:<name>`、SSH agent 签名 | 允许 + **门** + **owner 校验** | 允许 + **门** + **owner 校验** | 本来就给 `imk` / `ssh` 用，不看映像路径；固件封顶落地前由 Service 计数（§3.6 过渡方案）。**2026-09-06 多用户实测后补 owner 校验**：原来「靠门不靠身份」——切换用户后另一账号的进程还活着，而 GateBudget 是服务级的，owner 为别的事触摸一次，对方 60s 内不用触摸就能读走；实测 super2 会话 `imk get` 触摸后确实读出了 key。现在非 owner 回 `DENY:NOT_OWNER`，设备不亮灯，与 SSH agent 管道 ACL、AUTH / APPROVE 对齐 |
| `AUTH:*`、`CANCELGATE`、`INJECT:POLL`、imk 管道 `CANCEL` | 允许 | 拒绝 | 只有客户端 / imk 会发 |
| `FP:ENROLL` | 允许 + **门** | 拒绝 | **最严重**：偷录一枚指纹 = 长期后门 |
| `FP:DELETE`、`KEY:DELETE`、`KEY:UPDATE`、`KEY:SSHGEN`、`KEY:ADDOTP/ADDAPI` | 允许 + **门** | 拒绝 | 破坏、静默换钥、植入 |
| `PASS:SET`、`PASS:CLEAR` | 允许 + **门** + owner 校验（§3.5） | 拒绝 | 改 / 清登录密码 |
| `FEATURE:SET`、`SSHAGENT:ON\|OFF` | 允许 + **门** | 拒绝 | 关保护开关、改 agent 行为 |
| `PAIR:START`、`PAIR:RESET`、`SLOT:CLEAROWN/CLEAR` | 允许 + **门**（`PAIR:START` 的门就是配对流程本身的触摸 / 按键） | 拒绝 | 换设备、清槽 |
| `OTA:ERASE/HEADER/WRITE/VERIFY/END/PUSH` | 允许 + **门**（整个会话一次）；固件签名链校验另议（§7） | 拒绝 | 换固件 |
| imk 管道 `APPROVE:<cmd>` | `imk.exe` 属于 `TrustedClient`；`APPROVE` 本身就是门 | 拒绝 | — |

与初版表的差异：`FP:ENROLL`、`KEY:DELETE/UPDATE/SSHGEN/ADD*`、`SSHAGENT:*`、`PAIR:START`、`OTA:*` 从「只看 `CallerKind`」改为「必须带门」——固件对其中一部分（`KEY:DELETE` 等）已经会回 `WAIT_FP`，Service 侧要做的是**不把 cooldown 内的免门当成已授权**（§3.6 固件修复落地前尤其如此），并对固件不设门的命令（`FP:ENROLL`、`PASS:*`、`FEATURE:SET`、`SSHAGENT:*`）在 Service 侧显式先过一次 `AUTH` 门。

`Other` 的拒绝一律回 `ERROR:CALLER_NOT_TRUSTED`，不断连（§9.9）。

### 3.4 落盘密钥

状态：**已实现并在开发机验收**（2026-09-06，`Security/PairingStore.cs`、`Security/DataDirSecurity.cs`、Inno `[Dirs]`）。踩到一个稿子没预见的坑：**`CryptUnprotectData` 不看传入的 scope 参数**，作用域记在 blob 里，LocalMachine 的旧文件用 `CurrentUser` 参数照样解开——「先试 CurrentUser、失败再试 LocalMachine」的迁移判定形同虚设，第一次部署后设备连上了但什么都没迁移。改成文件自带格式头（`IMKDPU1` + blob = 新格式；无头 = 旧 LocalMachine 文件 → 迁移）才对。实测：日志「DPAPI 作用域已从 LocalMachine 迁移到 CurrentUser（旧文件保留为 .machine）」，设备随后正常重连；目录 DACL 只剩 SYSTEM / Administrators，普通用户列目录、读 `pairing.dat` / `settings.json`、建文件全部被拒。与稿子的差异：`logs` 子目录给的是 **owner 读**而不是 Users 读——多用户机器上日志会公开每一次认证事件与 agent 命令文本，Linux 端 0640 是同一个理由；owner 变更（配对 / PASS:SET）时 `OwnerStore.Changed` 触发重设。迁移只在 SYSTEM 身份下做，控制台调试模式不动文件（否则用开发者的用户密钥重写会把真正的服务锁在外面）。

- `pairing.dat` / `verified.dat`：DPAPI 改 `DataProtectionScope.CurrentUser`（Service 以 SYSTEM 运行，即 SYSTEM 的用户密钥），迁移：启动时若用 `CurrentUser` 解不开则尝试 `LocalMachine` 解开后重写（先验证再删，§9.5）。**收益要写准**：SYSTEM 的用户主密钥在 `%WINDIR%\System32\Microsoft\Protect\S-1-5-18`，管理员可读，所以这一条只对普通用户进程有效，对已提权管理员为零——他们本来就在攻击者模型之外，但别让横幅文案或代码注释暗示它防管理员。
- `%ProgramData%\immurok` 目录：安装（Inno `[Dirs]` 段 `Permissions: system-full admins-full`）和 Service 启动时都设 ACL：SYSTEM Full、Administrators Full、去掉 Users；日志子目录可单独给 Users 读。
- `settings.json` 随目录 ACL 收紧即可。
- `\\.\pipe\openssh-ssh-agent`（`SshAgentServer.cs:83-85`）：**已实现**（DACL 实测 `(A;;0x1f019f;;;SY)(A;;0x12019b;;;<owner SID>)`）。改之前实测：普通用户 token 跑 `ssh-add -l` 是 **Permission denied**——默认描述符就是 SYSTEM + Administrators，SSH agent 对标准用户本来是坏的，这次顺带修好（改后 `ssh-add -l` 列出 `id_ecdsa`）。原稿：补显式 `PipeSecurity`，**给谁**要写明——`ReadWrite` 只给 owner SID（§3.5），`CreateNewInstance` 只给 SYSTEM；对标 Linux 的活动会话校验。动手前先在 VM 上测清现状：SYSTEM 建的无 ACL 管道理论上继承 token 默认 DACL（SYSTEM + Administrators），普通用户的 `ssh` 应该连不上，但功能现在是能用的，说明实际 DACL 与推断不符——先用 `accesschk` / PowerShell `Get-Acl` 读出真实 DACL 再改，否则会把功能改坏。

### 3.5 谁是设备的主人：owner SID（现成的 bug，不是理论风险）

状态：**已实现**（2026-09-06，`Security/OwnerStore.cs`、`Ipc/PipeCaller.cs`；三个 C# 工程编译通过，**未在 VM 上跑过**——验收项见 §10 第 0 步）。实现与下文的差异：owner 文件在目录 ACL 收紧前自带 SYSTEM/Administrators-only 的 DACL、先删后建，读取时校验文件属主（防止有人抢先放一个文件把自己写成 owner）；`PAIR:START` / `PAIR:RESET` 也做 owner 校验，`PAIR:RESET` 清 owner；日志里不再出现用户名。

§9.6 把它当多用户场景的隐患，但代码里已经能看到它咬人：`CommandHandlers.cs:643` 的 `PASS:SET` 在 user 字段为空时回落到 `Environment.UserName`，Service 跑在 SYSTEM 下，取到的是 `"SYSTEM"`；`CredentialStore` 全局只有一条 `immurok/login`。

- 配对成功时（以及 `PASS:SET` 时）记录 owner 的 SID 到 `%ProgramData%\immurok\owner`（对标 Linux 的 `/var/lib/immurok/owner`），来源是当前连接的调用方 token（`GetNamedPipeClientProcessId` → `OpenProcessToken` → `TokenUser`），不是 `Environment.UserName`。
- `ScreenUnlocker.Unlock()` 写凭据前校验：目标会话（§3.1 第 4 条）的属主 SID == owner。`SessionMonitor` 已经在用 `WTSQuerySessionInformation`，加 `WTSUserName` + `LookupAccountName` 即可。
- `AUTH:*` 与 `APPROVE:` 请求带上发起方会话 id；会话属主 != owner 一律 `DENY:NOT_OWNER`。
- 没有 owner 记录时告警放行（升级过渡），配对 / `PASS:SET` 一次就补上；不锁死单用户机器。

### 3.6 固件侧待办：授权时声明预算（本轮不做固件）

§1.5 的根治在固件，但**本轮不动固件**（2026-09-06 决定）。记下正确的做法，别做成「全局加一个 60s 封顶」那种半吊子：

- 固件写死一个上限不够可控——`imk run --agent` 的一次会话和 `imk get` 的一次读取需要的预算完全不同。应该由**主机在首次授权时声明预算**：`AUTH_REQUEST` 带 payload `[ttl_s:2B][max_uses:1B]`（都为 0 = 沿用固件默认），固件把这次触摸换成一张「ttl 内最多用 max_uses 次」的票；rolling 刷新只在票内有效，票用完或到期就重新要门。
- 固件对主机声明的值再加硬上限（比如 ttl ≤ 300s、uses ≤ 50），主机声明不能突破。
- 未带 payload 的旧 `AUTH_REQUEST` 保持现状，三端可以分别升级。
- 需要改 `docs/protocol.md`、固件、三端 daemon；固件 bump 版本，daemon 按固件版本决定是否带 payload。

**本轮 Windows 侧的过渡方案**（不依赖固件，**已实现**：`Ble/GateBudget.cs` + `BleManager.EnsureSecretBudgetAsync`，挂在 `GetOtpCodeAsync` / `ReadKeyEntryAsync` / `SshSignAsync` 前；一次真实指纹门通过 = 一张票，60s / 20 次，断连作废；编译通过、未在 VM 上跑过）：`AUTH_REQUEST` 本身不看 cooldown、永远要新触摸（`hidkbd.c:5908-5915`），所以 Service 可以自己记「上次真实触摸」的时间和已放行的读秘密次数，超过预算就先发一条 `AUTH_REQUEST` 逼一次新触摸再转发。Service 是 SYSTEM、在攻击者模型之外，这个计数在 Windows 上站得住；macOS 的 App 在会话内、站不住——所以固件待办不能因为有了过渡方案就一直拖。

### 3.7 日志与遥测不能变成新的泄露口

- 事件日志 Application 通道普通用户可读。squat 检测记 pid / 映像路径 / 会话 id 没问题；**绝不记**凭据 payload、用户名、命令参数（`PASS:SET` 的 b64 字段、`KEY:ADD*` 的 secret）。现有 `_log.LogInformation("登录密码已保存（用户 {User}）")` 要去掉用户名。
- `SECURITY:STATUS` 是只读命令、任何人可查，返回值里只放枚举状态，不放路径。

## 4. UI 建议（与 macOS 横幅一致的语义）

状态：**已实现**（2026-09-06）。Service：`Ipc/SecurityStatus.cs` 汇总各管道的 `Contended`，`CommandHandlers.HandleSecurityStatus` 读 `ScreenUnlocker.LastPeerCheck` / `CallerPolicy.Mode` / `DataDirSecurity.IsLoose()` / `PairingStore.KeyScope` / `OwnerStore.IsSet`，应答形如 `OK:cp_pipe=unknown;service_pipe=ok;caller_check=path-only;data_acl=ok;pairing_scope=user;owner=set`（开发机实测）。客户端：`Views/SecurityBanner` 放在「功能」页顶部、配对横幅下方（用户要求：它是功能子项，不放在整个 app 之上），随功能页刷新；全绿可收起为右上角绿盾牌（`ClientSettings.SecurityBannerCollapsed`），有异常时橙色不可关闭、「查看详情」跳状态页；`StatusPage` 追加六项。`caller_check=path-only`（未签名构建）按 ok 显示并附注「仅按安装路径」；`pairing_scope=none`（未配对）按 ok；`cp_pipe=unknown` 按 ok 并附注「尚未触发过解锁」。老版本服务不认 `SECURITY:STATUS` 时横幅不显示。文案加了 en / zh-Hans / zh-Hant，其余语言回退英文。

macOS 做法：权限页顶部一条横幅，未启用/失配时橙色且不可关闭，启用后绿色可收起为右上角盾牌。Windows 上加固不需要用户「启用」（升级后 Service 自带），横幅表达的是**健康检查结果**：

- 位置：`MainWindow` 内容区顶部（所有页面共用）或 `StatusPage` 顶部；建议前者，因为这是安全状态不是某个功能。
- 数据源：新增 `SECURITY:STATUS` 命令，Service 返回：
  - `cp_pipe`: `ok` / `squatted`（3.1 的校验最近一次结果）/ `unknown`（还没触发过解锁）
  - `service_pipe`: `ok` / `contended`（3.2 的 FirstPipeInstance 创建失败）
  - `caller_check`: `on` / `off`（`--allow-unsigned-clients` 生效时为 off）
  - `data_acl`: `ok` / `loose`（启动时检查 `%ProgramData%\immurok` 的 ACL 里有没有 Users）
  - `pairing_scope`: `user` / `machine`
  - `owner`: `set` / `unset`（§3.5）
- 展示：
  - 全部 ok：绿色横幅「IPC 加固已生效」，右侧「×」，收起后左上/右上一个绿盾牌，点击展开。收起状态存客户端设置。
  - 任一异常：橙色横幅，不可关闭，文案指向具体项，例如「检测到有进程占用了凭据提供程序管道，指纹解锁已暂停」「数据目录权限过宽，请重新运行安装程序」，按钮「查看详情」跳 `StatusPage`。
- `StatusPage` checklist（`StatusPage.xaml.cs:63-74`）逐项追加与上面一一对应，✓/✕ 各带一句说明。
- `FeaturesPage` 不加开关：加固不是可选功能。

文案（英/中）：

```
Strong IPC protection is active            IPC 加固已生效
Credential pipe hijacked — unlock paused   凭据管道被占用，指纹解锁已暂停
Service pipe contended                     服务管道被其他进程占用
Data directory permissions too loose       数据目录权限过宽
Client verification disabled (dev build)   客户端校验已关闭（开发版）
```

## 5. 兼容与升级

- 旧客户端 + 新 Service：客户端未签名或不在安装目录 → 被判 `Other`，只剩只读命令；升级后恢复。安装包同时更新两者，正常不会遇到。
- 新 CP + 旧 Service：CP 加了 `FIRST_PIPE_INSTANCE` 不影响旧 Service 连接。
- 反向管道（§3.1 第二阶段）上线后：新 Service 同时保留旧方向（连 `\\.\pipe\ImmurokCredentialProvider` 并做对端校验）一个版本周期——§9.4 的「重启后替换」意味着「新 Service + 旧 CP」会真实存在一个重启周期。
- 旧 CP + 新 Service：Service 的 3.1 校验对旧 CP 同样通过（它就在 LogonUI 里以 SYSTEM 跑）。
- DPAPI 作用域迁移一次性完成，失败保留旧文件不删。

## 6. 测试

- 单元测试项目目前没有（仓库无 `*Tests*.csproj`），建议新建 `ImmurokService.Tests`（xUnit）至少覆盖：命令分级表（`CallerKind` × 命令 → 允许/拒绝/需指纹门）、`SECURITY:STATUS` 序列化、ACL 检查函数对「有 Users」目录返回 `loose`。
- 攻击复现脚本落成仓库文件 `tools/test-ipc-isolation.ps1`（对标 Linux 的 `scripts/test-isolation.sh`），普通用户身份运行，**脚本开头检测到自己是管理员（`WindowsPrincipal.IsInRole(Administrator)`）就退出**（§9.8）。每条都必须失败：
  1. 在锁屏前创建 `\\.\pipe\ImmurokCredentialProvider`（`New-Object System.IO.Pipes.NamedPipeServerStream`），锁屏、触摸；期望：Service 事件日志出现拒绝记录，脚本一个字节都收不到，横幅变橙。
  2. 创建 `\\.\pipe\immurok` 额外实例；期望：`UnauthorizedAccessException`。
  3. 用 `System.IO.Pipes` 直接发 `PASS:SET:...`、`KEY:OTP:0`、`FEATURE:SET:unlock:0`；期望：分别 `ERROR:CALLER_NOT_TRUSTED`、要求指纹门、`ERROR:CALLER_NOT_TRUSTED`。
  4. 以普通用户读 `%ProgramData%\immurok\pairing.dat`；期望：访问被拒。
  5. 用 `System.IO.Pipes` 直接发 `FP:ENROLL:0`；期望：`ERROR:CALLER_NOT_TRUSTED`（验的是授权分级，不是 ACL，§9.8）。
  6. 在一次真实触摸后以 5s 间隔连发 20 次 `KEY:OTP:0`；期望：超过 Service 的预算后要求重新触摸（§3.6 过渡方案生效）。
- 真机回归：锁屏指纹解锁、`imk run --agent`、OTP 读取（有指纹门）、密码设置流程、卸载 `--clear-credential`。

## 7. 不在范围

- UAC / `consent.exe` / `CPUS_CREDUI` 指纹提权：CP 目前故意不在该场景监听（`CSampleProvider.cpp:76-93`），另立议题。
- 固件侧签名（三端「选项 2」）。
- 把解锁密码改成不落盘（例如 LSA secret + 每次触摸时才解）。

## 8. 残余风险

- 已提权的管理员进程可以做任何事，含替换安装目录里的客户端。
- 无显示屏令牌的固有极限：用户真实触摸时，攻击者可以抢先发起自己的指纹门请求（同 macOS 残余风险）。
- CP 管道被占用时指纹解锁不可用（DoS），但密码不再泄露；横幅会提示。反向管道（§3.1 第二阶段）落地后这条消失。
- §1.5 的无限续期：Windows 本轮靠 Service 计数缓解（§3.6 过渡方案），固件侧根治（授权时声明预算）是待办；落地前 macOS 仍然是漏的。

---

## 9. 评审：Linux 特权分离实做之后的回头看（2026-09-04）

Linux 端的同类工作已经落地并在真机跑通（`app-linux-rs/docs/superpowers/specs/2026-09-03-pam-channel-hardening-design.md`，
逐条踩坑见同目录 plans）。那边**八个问题里设计稿一个都没预见到**，全部是真机部署时才暴露的，
类型高度可迁移。以下按「Linux 上踩到的 → Windows 上对应什么」组织。

评审基于本仓库内的 C# / C++ 源码；**没有 Windows 机器，未实测**，凡是需要在 VM 上验证的都标了。

### 9.1 `CallerKind::TrustedClient` 是减速带，不是边界

§3.3 第一层用「映像路径在 `%ProgramFiles%\immurok\` 下 + Authenticode 签名」判定可信客户端。
安装目录只有管理员可写，所以**替换 exe** 这条路确实被堵住了。但同一个用户、同一完整性级别的
进程之间，Windows 不阻止 `OpenProcess(PROCESS_ALL_ACCESS)` + 注入 —— 攻击者不用改任何文件，
直接把代码注进已经在跑的合法客户端，从里面发命令，`GetNamedPipeClientProcessId` 看到的就是
一个签名齐全、路径正确的进程。

这正是 Linux 那边的核心结论在 Windows 的镜像：**会话内的组件不能作为信任边界**。所以：

- 把「调用方校验」明确写成**纵深防御**，不要在文档或代码注释里让它读起来像边界。
- **所有会改变安全状态或产生持久后果的命令，都必须自己带指纹门，不能只靠 `CallerKind`。**
  §3.3 的表里这几条现在只有 `TrustedClient` 而没有门，建议补上：
  - `FP:ENROLL` —— **最严重的一条**：注入后偷偷录一枚指纹，就是一个长期后门，之后所有指纹门
    都对攻击者敞开。
  - `KEY:DELETE` / `KEY:UPDATE` / `KEY:SSHGEN` —— 破坏与静默换钥。
  - `SSHAGENT:*` —— 关掉 agent 或改其行为。
  - `PAIR:START` —— 重新配对等于换设备。
  - `OTA:*` —— 至少要门，理想情况还要确认固件签名链。

  剩下的只读命令保持现状即可。判据可以简单点：**一条命令如果重放一百次会让用户处境变糟，就
  要门。**

### 9.2 UI 只能用来拒绝，不能用来放行

Linux 上我们让 daemon 直接 spawn GTK 对话框，特权分离后它连不上显示服务器、瞬间非零退出，
而代码把「对话框退出」一律当成「用户点了取消」—— 结果每一次 agent 授权都被拒。方向是安全的，
但功能全废。修法是把 UI 拆成一个会话内的代理，并写死三条约束：没有订阅者 = 没有 UI，认证照常；
退出码区分「我们关的」和「用户关的」；UI 通道只能用来拒绝。

Windows 的 WPF 客户端是同一类角色。落到 §4 的横幅与后续的授权提示上：

- Service 必须能区分「客户端没在跑」和「用户点了取消」，前者不能变成拒绝，后者必须是拒绝。
- 反过来更要紧：客户端说「用户同意了」**永远不能**成为放行依据 —— 同用户的攻击者能伪造它。
  放行只能来自设备上的那次触摸。
- 横幅的绿色状态同理：它是给人看的健康提示，不是安全判定的输入。

### 9.3 起不来要响亮，不要静默降级

Linux 上我们发现 socket 绑定失败只 `warn` 后 return，而 `main` 的 select 任一分支返回就整体
以 **exit 0** 结束 —— `Restart=on-failure` 永远不触发，指纹功能静默死亡（仍会回退密码，但没人
知道为什么）。

Windows 对应两处：

- `PipeServer.cs:52-57` 的 accept 循环 catch-all：任何异常都是「记一条 error + 睡 1 秒」。如果
  `FILE_FLAG_FIRST_PIPE_INSTANCE` 因为名字被占而持续失败，服务会以每秒一行的速度安静地转下去，
  SCM 那边一切正常。建议：连续 N 次创建失败就把服务标记为 Faulted（让 SCM 的恢复动作接手），
  并且 `SECURITY:STATUS` 立刻报 `service_pipe: contended`。
- 服务重启时旧实例可能还没释放，新实例会短暂创建失败。这段时间 `SECURITY:STATUS` 必须报
  `contended` 而不是 `ok`，否则横幅会在每次重启后短暂说谎。

### 9.4 升级与半装状态：换掉认证组件的那一刻，认证就是断的

Linux 上这一条把我们坑得最实在：新 PAM 模块一装上、系统 daemon 还没起来的那段时间，指纹 sudo
必然失效；而安装脚本里散着十几条 `sudo`，无 tty 时 sudo 的时间戳按 ppid 记、缓存不生效，于是
安装跑到一半掉进密码提示、机器停在半装状态。修法是把所有特权步骤收敛成**一次**授权。

Windows 的安装是 Inno 一次提权，没有这个具体问题，但同一类风险在别处：

- **CP DLL 正被 LogonUI 加载时不能原地替换。** Linux 上我们踩过等价的坑：原地覆盖
  `pam_immurok.so` 会打烂正在跑的 sudo 的代码页（`dlclose` 时 SIGSEGV），必须临时文件 +
  原子 rename。Windows 上要走「重启后替换」（`RestartReplace` / `MoveFileEx` +
  `MOVEFILE_DELAY_UNTIL_REBOOT`），并且**确认安装包不会留下「新 Service + 旧 CP」这种组合**
  —— §5 讨论了协议兼容，但没讨论文件替换失败时的状态。
  **2026-09-06 已做**：`immurok.iss` 把 CP DLL 拆成单独一条 `[Files]`，加 `restartreplace uninsrestartdelete`；
  占用时登记重启后替换，结束页用自定义 `FinishedRestartLabel` 说明「重启前锁屏解锁跑的还是旧版本」。
  ISCC 编译通过，**锁屏状态下升级这条路径还没在 VM 上跑过**（0.4.1 快照 → 锁屏 → 远程跑 0.5.0 安装包）。
- 安装/升级过程中锁屏会怎样？至少要保证 CP 在 Service 不可用时退化为「不显示指纹磁贴」，
  而不是让 LogonUI 卡住。这条建议写进验收单。

### 9.5 迁移要先验证再删

§3.4 的 DPAPI 作用域迁移写了「失败保留旧文件不删」，方向对，再紧一格：**用新作用域重新加密
之后，先把它解回来验证一次，通过了再替换原文件**；旧文件留成 `pairing.dat.machine` 这类带后缀
的名字（Linux 那边我们留的是 `.migrated`，回滚时用户能自己搬回去）。

另外一个 §3.4 没提的失败模式：服务账户如果被改过（有人把 LocalSystem 改成别的账号），
`CurrentUser` 作用域的密文就永久解不开了。这时候应该识别出来并提示「需要重新配对」，而不是
启动时崩溃或反复重试。

### 9.6 谁是设备的主人

Linux 上把 socket 从「每用户一个」改成「全机一个」之后，我们才发现 `AUTH` 请求里的用户名
**从来没参与过鉴权**——同机第二个账号跑 sudo，机主的一次触摸就把 root 给了他。补的办法是记一个
owner uid，并校验请求目标 == owner。

Windows 的 Service 本来就是全机一个、会话有多个，`CredentialStore` 里也只有一条
`immurok/login`（一份用户名+密码）。所以同一个问题在这里表现为：

- 第二个用户锁屏后触摸传感器，Service 会拿着 owner 的凭据去解**他**的会话。大概率被 LogonUI
  拒绝（凭据对不上会话属主），但那是靠下游兜底，不是设计。
- 建议显式记录 owner 的 SID，并在写凭据前校验「目标会话的属主 == owner」；`SessionMonitor`
  拿到的会话信息足够做这件事。指纹门请求同理，应该带上发起会话，避免跨会话劫持。

### 9.7 环境假设要在干净 VM 上验，不能只在开发机上验

Linux 上我们写了 `SupplementaryGroups=bluetooth`，在 Debian 系一切正常，到 Arch 上直接让单元起
不来 —— Arch 根本没有这个组，而 systemd 遇到不存在的组是硬失败。这种「只在别人的机器上炸」的
假设，Windows 侧至少这几条要在干净 VM 上过一遍：

- `%ProgramFiles%` 还是 `%ProgramFiles(x86)%`（取决于构建目标），§3.3 的路径判定别写死一个。
- `LogonUI.exe` 的路径与「总是以 SYSTEM 运行」在 Windows 11 的各版本上是否都成立。
- `WTSGetActiveConsoleSessionId` 在 RDP / 多会话下拿到的**不是**「用户所在的会话」。
- 用户名格式：域账号（`DOMAIN\user`）、Microsoft 账号、以及启用 PIN 之后密码解锁是否仍被接受
  —— `username\0password\0` 这个载荷对这三种情况的行为要各测一次。
- Home 版缺少某些组策略/API。

### 9.8 验收脚本必须以攻击者身份跑，并拒绝提权运行

§6 的 PowerShell 复现步骤要加一条**反向**的自检：脚本发现自己跑在管理员权限下就直接退出。
否则「被拒绝」可能只是因为你恰好是管理员，测出来的绿色是假的。（Linux 侧的
`scripts/test-isolation.sh` 就是这么做的：`id -u` 是 0 就拒绝执行。）

同样建议把「直接连管道发一条特权命令，期望拿到明确的错误码」作为独立断言 —— 它验的是授权分级，
而不只是 ACL；这两者在我们这边确实是分开坏掉过的。

### 9.9 拒绝要有话说

Linux 上最初的实现是「鉴权不过就关连接」，CLI 那头显示的是 `Connection reset by peer`，
用户完全不知道发生了什么。改成回一个明确的 `DENY:NOT_AUTHORIZED` 之后才可诊断。
Windows 侧 §3.3 已经写了 `ERROR:CALLER_NOT_TRUSTED`，保持这个风格，并确保**指纹门超时/拒绝**
也有各自可区分的错误码。

### 9.10 一个次序建议

Linux 那边的经验是：**先做安装/迁移脚本并在真机上跑一次，再补 UI 与周边**。按顺序把设计稿从头
实现到尾，等到最后才部署，会让上面这类「权限模型换了、旧假设静默失效」的问题一次性全部涌出来，
而且是在你最不想调试的时候。Windows 这边对应的动作是：先把 §3.1 + §3.2（两个管道）做完、在带
快照的 VM 上跑一轮锁屏解锁与攻击复现，再回头做 §3.3 的命令分级与 §4 的横幅。

## 10. 实施次序（2026-09-06 细化）

§9.10 的「先做两个管道、上 VM 跑一轮」保持不变，再拆细。前三步不需要 VM，可以在开发机上编译通过后再进 VM 回归：

0. §3.5 owner SID + `PASS:SET` 的 `Environment.UserName` 修复 + §3.7 去掉日志里的用户名——纯 Service 侧。**2026-09-06 开发机验收通过**（owner 从既有凭据迁移；owner 文件普通用户不可读写；owner 发 `AUTH` 进门）。「第二个账号被拒」一项开发机只有一个账户，未验。原验收项：配对后 `%ProgramData%\immurok\owner` 出现且普通用户不可写；第二个本地账号发 `PASS:SET` / `AUTH` / `APPROVE` 得 `DENY:NOT_OWNER`；第二个账号锁屏后触摸不推送凭据（日志「活动会话的属主不是 owner」）；owner 自己的锁屏解锁、`imk run --agent` 不受影响；旧安装升级后没有 owner 文件时一切照常并有告警日志。
1. §3.1 第 1-5 条对端校验——纯 Service 侧，不动 CP。**2026-09-06 开发机验收通过**（`tools/test-ipc-isolation.ps1 -Squat`：普通用户 powershell 抢占后锁屏触摸，脚本收到 0 字节，日志「服务端不是 SYSTEM」；脚本退出后再触摸正常解锁）。RDP 一项未验。原验收项：正常锁屏触摸解锁照常，日志出现「已向 CP 推送解锁凭据」；普通用户在锁屏前用 `New-Object System.IO.Pipes.NamedPipeServerStream('ImmurokCredentialProvider')` 抢占后锁屏触摸——脚本一个字节都收不到，日志「CP 管道对端校验失败」含 pid / image / session；RDP 会话下锁屏触摸的行为（§9.7，控制台会话 id 与 RDP 会话不同，预期是拒绝并记日志，不能崩）。
2. §3.6 过渡方案：Service 记真实触摸时间 + 读秘密计数，超预算先发 `AUTH_REQUEST`。固件侧「授权时声明预算」留作待办，本轮不做。**2026-09-06 开发机验收通过**（`imk get imk://api/test` ×3：第一次 30s 未触摸→`READ_FAILED`，第二次触摸后通过，第三次 2s 免触摸）。20 次 / 65s 上限与断连一项未验。原验收项：`imk get imk://otp/x` 连续 3 次——第一次要触摸、后两次不要；一次触摸后以 5s 间隔连发 25 次 `KEY:OTP:0`，第 21 次设备重新亮灯要触摸、不摸则回 `DENY`；触摸后等 65s 再读要重新触摸；`imk run --agent` 会话内的 `imk get` 与 `git push`（ssh 签名）不受影响；断连重连后第一次读要触摸。
3. CP 侧 `FIRST_PIPE_INSTANCE`（或直接做反向管道）——动 CP，**必须**在带快照的 VM 里。
4. §3.2 两个 Service 管道去 `CreateNewInstance` + `FirstPipeInstance`，验收含 `PAIR:START` 并发。**2026-09-06 开发机验收通过**（用 `AUTH` 占连接代替 `PAIR:START` 做并发；DACL、额外实例被拒、`ssh-add -l` 三项见 §3.2 / §3.4）。第二次部署已验：停服务日志干净（无「异常终止」）、`ImmurokCommon.dll` 换齐、客户端自动拉起、`ssh-add -l` 仍正常。未验：名字被占时的 5s 重试与 `Contended` 状态。
5. §3.4 目录 ACL + DPAPI 迁移（先验证再删）+ SSH agent 管道 ACL（先测现状）。**2026-09-06 开发机验收通过**（见 §3.4；`tools/test-ipc-isolation.ps1 -DataDir` 全绿）。未验：服务账户被改后的「需要重新配对」路径、Inno `[Dirs]` 段（要跑一次安装包）。
6. §3.3 命令分级（按重写后的表）。**2026-09-06 开发机验收通过**（`tools/test-ipc-isolation.ps1 -Commands` 8/8；imk 可信路径正常）。未验：签名构建下的 `WinVerifyTrust` 分支；客户端 UI 里打开功能 / 设置密码时的触摸提示与 35s 超时（需手工点一次）。
7. §4 横幅与 `SECURITY:STATUS`。**2026-09-06 已实现并部署**：`SECURITY:STATUS` 普通用户可查、返回六项枚举；客户端横幅与状态页需手工看一眼（绿色 → 收起成盾牌 → 点盾牌展开；用 `-Squat` 抢占后锁屏触摸再回来应变橙色并指向「凭据管道被占用」）。
8. `tools/test-ipc-isolation.ps1` 全绿，真机回归（§6）。
