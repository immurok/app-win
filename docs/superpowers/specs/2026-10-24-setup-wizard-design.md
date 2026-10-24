# 设置向导 — Windows 移植设计

2026-10-24 · 对应 ImmurokClient 0.5.x（未发布）

上游设计：主仓库 `docs/setup-wizard-flow.md`（macOS 向导，v1.6.6）。本文只记录 Windows 端的落地方式和与 macOS 的差异；设计原则、示意图规范、文案规则以上游为准，改动时不要回退。

## 步骤

macOS 的 8 步在 Windows 上收成 7 步，常见用户看到 6 步：锁屏解锁走 Credential Provider，不靠键盘模拟，所以没有「辅助功能」步。

| # | 步骤 | 完成条件 | 可跳过 | 备注 |
| --- | --- | --- | --- | --- |
| 1 | 欢迎 | — | — | 只在什么都没配过时从这页开始 |
| 2 | 安装检查（按需） | 服务管道可达，且 CP 的 CLSID 已登记在 HKLM `…\Authentication\Credential Providers` | 仅当服务可达（只缺 CP） | 服务不可达给「启动服务」（UAC 提权 `sc start ImmurokService`）；CP 缺失只能重跑安装包 |
| 3 | 蓝牙连接 | `STATUS` 报告已连接 | 否 | 「添加设备」窗口简笔画 + 「打开蓝牙设置」（`ms-settings:bluetooth`）+ 等待转圈；蓝牙无线电关闭时换提示 |
| 4 | 绑定本机 | `PAIR:STATUS` 为 PAIRED，且连上时 `SLOT:STATUS` 复核本机活跃槽确实被占 | 否 | 点击前不显示设备图 |
| 5 | 解锁密码 | `PASS:STATUS` 为 CONFIGURED | 是 | `PASS:SET` 要过设备指纹门，套 `FpAuthDialog`；未绑定时按钮置灰 |
| 6 | 录入指纹 | 认证槽（0–4）至少 1 枚 | 是 | 复用 `EnrollDialog`，不占第 6 号切换槽 |
| 7 | 完成 | — | — | 五行汇总：蓝牙 / 绑定 / 密码 / 指纹 / 系统组件 |

轮询分两路（与 Linux 同理）：`STATUS` / `PAIR:STATUS` / `PASS:STATUS` 是服务本地应答，每秒一次，超时 2.5 s（服务没起来时管道 `ConnectAsync` 会空等满超时）；`SLOT:STATUS` 与 `FP:SLOTS` 是真实 BLE 往返，放慢路，只在已连接且结果未知时发起，`FP:SLOTS` 失败后 5 s 再试。断开连接或 PAIR:STATUS 变化时清掉慢路结果。向导打开时首次慢路查询最多等 2 秒。

槽位复核连续失败 3 次退回信本地（与设备页一致），旧固件（`supported=0`）直接视为有效。

与 macOS 相同的行为：侧栏可回看；当前步满足即自动前进；回看（上一步 / 往回点侧栏）暂停自动前进，直到再点下一步；启动跳到第一个未完成步，全部完成直接到完成页；「稍后再说」写完成标记并关窗；窗口 `Topmost`。

与 macOS 不同：

- **弹出条件**：`ClientSettings.SetupCompleted`（`%AppData%\immurok\client.json`）为 false，或服务不可达，或 CP 未登记。开机自启时服务可能晚于客户端就绪，判定「服务不可达」前最多重试 5 次、间隔 2 s，避免每次开机误弹。不需要向导时沿用老规则：未绑定才显示主窗口。托盘菜单「设置向导」和设置页的卡片随时可再打开。
- **安装检查是否插入在向导打开时决定一次**，之后步骤列表不变（同 Linux）。
- **主题**：首启可能只弹向导、主窗口从未加载，向导自己调一次 `ApplicationThemeManager.Apply` + `SystemThemeWatcher`。
- 页面在内容相关状态变化时才重建（渲染键），否则每秒重建会让示意图动画不停从头起、按钮焦点丢失；换步骤时重建正好让动画从头起动。

## 绑定引导框 `PairDialog`

设备页的「绑定这台电脑」和向导第 4 步打开的是同一个框、同一套结果文案（`PairDialog.Describe`）。向导走 `PairDialog.RunAsync`：先查 `SLOT:STATUS` 决定画一步还是两步，再 `PAIR:START` 流。

- 顶部示意图跟阶段走：`WaitFingerprint` 顶面 + 手指，`WaitButton` 底面 + 按钮光环，`Computing` 定格。换场景时重建。
- 等按钮时显示脚注「长按设备按钮，或等 30 秒自动取消」。Windows 原有的关闭语义保留：指纹门阶段关窗发 `CANCELGATE` 真取消，按钮门阶段客户端取消不了。

## 设备示意图 `Views/DeviceIllustration.cs`

WPF `DrawingContext` 绘制，画布 340×190，三个场景与 macOS 一一对应；蓝牙场景画的是 Windows「添加设备」窗口（窗口按钮在右上、设备条目强调色描边 + 鼠标指针），不是 macOS 的「连接」按钮。几何常量照抄 Swift / Rust 版。颜色取 `TextFillColorPrimaryBrush` 与 `SystemAccentColorPrimaryBrush`。手指遮挡用裁剪（先挖掉手指区域再画设备），不填背景色——Mica 窗口背景是透明的。文字是叠在画布上的 TextBlock。动画 33 ms 定时器，只在 Loaded 期间跑，起动放到下一个 dispatcher 轮次。

## 术语切分

App 内不再出现 Pair / Bind（9 种语言）。键名不变（`device.pair*`、`msg.pair.*`、`pair.*`、`msg.unbind.*`），只改值；新增 `wizard.*`、`illus.*`、`pair.dlg.cancelhint`、`tray.wizard`、`setup.wizard*`。系统层描述用 Windows 自己的说法（设置 › 蓝牙和其他设备 › 添加设备）。`PAIR:*` 仍是服务的 wire 命令名，不改。

## 代码位置

| 文件 | 负责 |
| --- | --- |
| `ImmurokClient/Views/SetupWizardWindow.xaml(.cs)` | 向导窗口：步骤表、状态轮询、完成判断、自动前进、页面 |
| `ImmurokClient/Views/PairDialog.xaml(.cs)` | 绑定引导框、示意图切换、`RunAsync` / `Describe` |
| `ImmurokClient/Views/DeviceIllustration.cs` | 三个场景的矢量示意图 |
| `ImmurokClient/Services/SystemComponents.cs` | CP 登记检查、提权启动服务、蓝牙开关、打开蓝牙设置 |
| `ImmurokClient/Services/ClientSettings.cs` | `SetupCompleted` |
| `ImmurokClient/App.xaml.cs` | 启动时决定弹向导 / 主窗口，托盘菜单入口 |
| `ImmurokClient/Views/SetupPage.xaml(.cs)` | 「设置向导」卡片 |

## 验证

- 开发机自包含发布后实跑截图：欢迎、蓝牙（简笔画 + 蓝灯闪烁）、绑定（未连接置灰）、完成页汇总，浅色主题。
- 三个示意图场景离屏渲染检查几何。
- **真机**（2026-10-24，ARM64 开发机）：正式安装包已签名，未签名预览放进安装目录子目录会被 `ERROR:CALLER_NOT_TRUSTED` 拒；整套替换为未签名 arm64 开发版后在真机走通绑定（用户确认）。顺带修了：绑定框在服务秒回错误时 `Loaded` 里关窗导致 WPF-UI TitleBar 零 Hwnd 崩溃（改挂 `ContentRendered`）、`ERROR:GENERIC` 附具体阶段与设备回包、`INJECT:POLL` 收到错误回复紧打转、服务「调用方不可信」日志限流。
- **未覆盖**：深色主题、安装检查页（本机服务与 CP 都正常）。
