; immurok for Windows —— Inno Setup 6 安装包定义。
;
; 由 packaging\build-installer.ps1 编译（它负责发布 .NET 产物、编 C++ CP、调 ISCC）。
;
; 这里定义「装完之后系统状态该是什么样」：
;   · Windows 服务 ImmurokService（LocalSystem、自启）
;   · Credential Provider 的 COM 注册（路径用 {app}，随实际安装目录展开）
;   · 安装目录加入系统 PATH（终端可直接调 imk）
;   · 开始菜单快捷方式 + 装完默认启动客户端（客户端自己登记 HKCU\Run 开机自启）
;   · 卸载时停服务、摘 PATH、清掉凭据管理器里的登录密码
;
; 界面语言用 Inno 自带的英文（官方发行版不含简体中文 .isl，引用会编译失败）。
; 需要中文界面可从 https://jrsoftware.org/files/istrans/ 取 ChineseSimplified.isl
; 放进 Inno 的 Languages 目录后，在 [Languages] 段增加一行即可。
;
; ⚠️ Credential Provider 进入登录链路。首次务必在带快照、可回滚的虚拟机中验证，
;    并始终保留密码登录作为后备。

#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif
#ifndef StageDir
  #define StageDir "stage"
#endif
; Arch：x64 或 arm64。由 build-installer.ps1 用 /DArch=... 传入。
#ifndef Arch
  #define Arch "x64"
#endif

; 两个架构各出一个安装包，彼此互斥安装：
;   · x64 包不允许装到 ARM64 机器 —— CP 是进程内 DLL，原生 ARM64 的 LogonUI 加载不了 x64 DLL
;   · arm64 包只装 ARM64
; 用 x64compatible 而非 x64os，是为了让 x64 包能在 ARM64 上跑起来、由我们自己给出
; 「请改用 arm64 安装包」的明确提示，而不是让 Inno 弹一句看不懂的通用报错。
#if Arch == "arm64"
  #define ArchAllowed "arm64"
#else
  #define ArchAllowed "x64compatible"
#endif

#define AppName      "immurok"
#define ServiceName  "ImmurokService"
; CP 的 CLSID。注意 Inno 里 { 是转义字符，字面量大括号要写成 {{
#define CpClsid      "{{C433E3A5-FA34-42B1-A94C-5D793D2EEA83}"

[Setup]
; AppId 决定升级与卸载时的身份，一经发布不可更改
AppId={{8D3A1F2C-7B54-4E19-9C6A-2F81D4E7B035}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=immurok
AppSupportURL=https://github.com/immurok/app-win
DefaultDirName={autopf}\immurok
DefaultGroupName=immurok
DisableProgramGroupPage=yes
; 服务注册、HKLM 注册表、系统 PATH 都需要管理员
PrivilegesRequired=admin
; 架构由 {#Arch} 决定；32 位 Windows 不支持
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
OutputDir=out
OutputBaseFilename=immurok-{#AppVersion}-{#Arch}-setup
SetupIconFile={#StageDir}\immurok.ico
UninstallDisplayIcon={app}\ImmurokClient.exe
UninstallDisplayName={#AppName} {#AppVersion} ({#Arch})
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 签名：build-installer.ps1 配了证书时传 /DSigned=1 和 /Simmurok=<签名命令>，
; Inno 用它签安装包和卸载程序（SignedUninstaller 随 SignTool 默认开启）。未传则出未签名包。
#ifdef Signed
SignTool=immurok
#endif

[Dirs]
; 数据目录只给 SYSTEM / Administrators（Inno 的 Permissions 只追加 ACE，不断开 ProgramData 的继承；
; 服务启动时 DataDirSecurity 会断开继承并对账，这里保证目录一开始就存在且带上这两条）
Name: "{commonappdata}\immurok"; Permissions: system-full admins-full
Name: "{commonappdata}\immurok\logs"; Permissions: system-full admins-full

[Files]
; 暂存目录里已是发布好的三个程序 + CP DLL + 图标，整体收进来（CP DLL 单独一条，见下）
Source: "{#StageDir}\*"; DestDir: "{app}"; Excludes: "ImmurokCredentialProvider.dll"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; Credential Provider 是 LogonUI.exe 进程内加载的 COM DLL。平时桌面上 LogonUI 不在跑，
; 覆盖没问题；但升级时若控制台正停在锁屏 / 登录界面（远程跑安装、切换用户留了一个账号在
; 登录界面、安装中途自动锁屏），DLL 正被占用，覆盖会失败，留下「新 Service + 旧 CP」
; 的半装状态（设计稿 §9.4）。PrepareToInstall 停得掉服务和客户端，但 LogonUI 不能也不该杀。
; restartreplace：占用时登记为「重启后替换」并在结束页提示重启（[Messages] 里说明了为什么）；
; uninsrestartdelete：卸载时同理，占用则重启后删除。
Source: "{#StageDir}\ImmurokCredentialProvider.dll"; DestDir: "{app}"; \
    Flags: ignoreversion restartreplace uninsrestartdelete

[Icons]
Name: "{group}\immurok"; Filename: "{app}\ImmurokClient.exe"; WorkingDir: "{app}"

[Messages]
; 只有 CP DLL 被 LogonUI 占用、登记为重启后替换时，Inno 才会走到「需要重启」的结束页。
; 默认文案只说「必须重启」，这里讲清楚重启前锁屏解锁跑的还是旧版本，让用户能做判断。
FinishedRestartLabel=Setup could not replace the lock-screen component (Credential Provider) because the Windows sign-in screen was using it. It has been scheduled to be replaced on the next restart.%n%nUntil then, fingerprint unlock at the lock screen keeps running the previous version, while the background service is already the new one.%n%nRestart now to complete the installation of [name]?
FinishedRestartMessage=Setup could not replace the lock-screen component (Credential Provider) because the Windows sign-in screen was using it. It has been scheduled to be replaced on the next restart.%n%nUntil then, fingerprint unlock at the lock screen keeps running the previous version.%n%nRestart now to complete the installation of [name]?

[Registry]
; ---- Credential Provider 的 COM 注册 ----
; InprocServer32 用 {app} 展开成真实安装路径。旧的 Register.reg 把它硬编码成
; C:\Program Files\immurok\，用户一旦改安装目录，LogonUI 就会去加载不存在的 DLL。
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{#CpClsid}"; \
    ValueType: string; ValueData: "ImmurokCredentialProvider"; Flags: uninsdeletekey

Root: HKCR; Subkey: "CLSID\{#CpClsid}"; \
    ValueType: string; ValueData: "ImmurokCredentialProvider"; Flags: uninsdeletekey

Root: HKCR; Subkey: "CLSID\{#CpClsid}\InprocServer32"; \
    ValueType: string; ValueData: "{app}\ImmurokCredentialProvider.dll"; Flags: uninsdeletekey
Root: HKCR; Subkey: "CLSID\{#CpClsid}\InprocServer32"; \
    ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"

; ---- 卸载时摘掉客户端的开机自启 ----
; 这个值是客户端首次启动时自己写的（装的时候还没有），所以这里不设值、只登记「卸载时删」。
; 注意：卸载程序以管理员身份运行，若与当前登录用户不是同一账户，删的是管理员的 HKCU。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: none; ValueName: "immurok"; Flags: uninsdeletevalue

; ---- 系统 PATH（imk 命令）----
; 摘除放在 [Code] 的 CurUninstallStepChanged 里：{olddata} 这种写法只能追加不能删除
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
    ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; \
    Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
; 用 sc.exe 注册服务，与手工安装脚本保持一致，出问题时便于对照排查
Filename: "{sys}\sc.exe"; \
    Parameters: "create {#ServiceName} binPath= ""{app}\ImmurokService.exe"" start= auto obj= LocalSystem DisplayName= ""immurok Fingerprint Authentication Service"""; \
    Flags: runhidden; StatusMsg: "Registering Windows service..."
Filename: "{sys}\sc.exe"; \
    Parameters: "description {#ServiceName} ""immurok Bluetooth fingerprint authentication: maintains the BLE connection and unlocks the lock screen."""; \
    Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; \
    Flags: runhidden; StatusMsg: "Starting service..."

; 默认勾选：客户端不常驻，密码注入（在用户会话里做 UI Automation）就完全不工作。
; 客户端首次启动会把自己登记进 HKCU\...\Run，之后每次登录自动拉起。
Filename: "{app}\ImmurokClient.exe"; Description: "Launch immurok"; \
    Flags: postinstall nowait skipifsilent

[UninstallRun]
; 顺序要紧：先停服务 → 再清凭据（此时 exe 还在）→ 最后删服务
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopSvc"
; 登录密码以 CRED_PERSIST_LOCAL_MACHINE 存在服务账户（LocalSystem）名下，只有同一身份
; 删得掉。卸载程序以管理员而非 SYSTEM 运行，所以不能直接 cmdkey，交给服务自己的入口去删。
Filename: "{app}\ImmurokService.exe"; Parameters: "--clear-credential"; Flags: runhidden; RunOnceId: "ClearCred"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DelSvc"

[Code]
const
  EnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

{ ---- .NET 8 Desktop Runtime 检测 ----
  三个程序都是框架依赖发布，缺运行时装上也起不来，所以安装前就拦下。
  不查注册表（各版本的键名布局有出入，容易误判），直接看共享框架目录下有没有 8.x：
  框架依赖应用默认的 roll-forward 只在同一主版本内滚动，装了 9 或 10 也顶不了 8。 }
function HasDotNet8Desktop(): Boolean;
var
  Root: String;
  FR: TFindRec;
begin
  Result := False;
  Root := ExpandConstant('{commonpf64}') + '\dotnet\shared\Microsoft.WindowsDesktop.App';
  if not DirExists(Root) then
    Exit;
  if FindFirst(Root + '\*', FR) then
  begin
    try
      repeat
        { 用 DirExists 判断目录，避免依赖 FILE_ATTRIBUTE_* 常量是否预定义 }
        if (Copy(FR.Name, 1, 2) = '8.') and DirExists(Root + '\' + FR.Name) then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FR);
    finally
      FindClose(FR);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;

#if Arch != "arm64"
  { ---- x64 包在 ARM64 机器上的拦截 ----
    ArchitecturesAllowed=x64compatible 按定义包含 ARM64 Windows 11（它能靠模拟跑 x64
    二进制），所以这个包在 ARM64 上是装得进去的。但 Credential Provider 是被 LogonUI.exe
    在**进程内**加载的 COM DLL，而 ARM64 上的 LogonUI 是原生 ARM64 进程，Windows 不允许
    跨架构在同一进程内加载 DLL —— x64 的 CP 永远加载不上。
    放任安装的话表现是最坏的一种：服务和客户端靠模拟跑得好好的、看起来一切正常，唯独
    锁屏解锁在登录界面静默失效。所以这里明确挡下并指向 arm64 安装包。 }
  if ProcessorArchitecture = paArm64 then
  begin
    MsgBox('This is the x64 build of immurok, and this is an Arm64 PC.' + #13#10#13#10 +
           'The Credential Provider is loaded in-process by LogonUI.exe, which is a native ' +
           'Arm64 process here — Windows cannot load an x64 DLL into it, so screen unlock ' +
           'would silently never work even though everything else ran under emulation.' + #13#10#13#10 +
           'Please install the arm64 build instead.',
           mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
#endif

  if not HasDotNet8Desktop() then
  begin
    if MsgBox('immurok requires the .NET 8 Desktop Runtime (x64), which was not found.' + #13#10#13#10 +
              'Open the download page now? Install it, then run this installer again.',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0',
                '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;

{ ---- PATH 处理 ---- }
function NeedsAddPath(Param: String): Boolean;
var
  OrigPath: String;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvKey, 'Path', OrigPath) then
  begin
    Result := True;
    Exit;
  end;
  { 前后补分号再比对，避免 C:\foo 命中 C:\foobar }
  Result := Pos(';' + Uppercase(Param) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

procedure RemoveFromPath(Dir: String);
var
  OrigPath, NewPath: String;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvKey, 'Path', OrigPath) then
    Exit;
  NewPath := ';' + OrigPath + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(NewPath));
  if P = 0 then
    Exit;
  Delete(NewPath, P, Length(Dir) + 1);
  { 去掉首尾补上的分号 }
  NewPath := Copy(NewPath, 2, Length(NewPath) - 2);
  RegWriteExpandStringValue(HKEY_LOCAL_MACHINE, EnvKey, 'Path', NewPath);
end;

{ ---- 安装前：停掉在跑的服务与客户端，否则文件被占用会要求重启 ---- }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  NeedsRestart := False;
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im ImmurokClient.exe', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im ImmurokService.exe', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  { 给服务控制管理器一点时间释放文件句柄 }
  Sleep(1500);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im ImmurokClient.exe', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if CurUninstallStep = usPostUninstall then
  begin
    RemoveFromPath(ExpandConstant('{app}'));
    { 配对数据与日志属于用户数据，询问后再删。
      登录密码不在此列 —— 它在 [UninstallRun] 里已无条件清掉。 }
    if DirExists(ExpandConstant('{commonappdata}\immurok')) then
      if MsgBox('Also delete pairing data and logs (%ProgramData%\immurok)?' + #13#10#13#10 +
                'You will need to pair the device again.',
                mbConfirmation, MB_YESNO) = IDYES then
        DelTree(ExpandConstant('{commonappdata}\immurok'), True, True, True);
  end;
end;
