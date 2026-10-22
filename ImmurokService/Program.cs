using ImmurokService;
using ImmurokService.Ble;
using ImmurokService.Ipc;
using ImmurokService.Security;
using ImmurokService.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// immurok Windows Service 入口。
// 以 LocalSystem 运行于 Session 0：即使用户未登录也维持 BLE 连接，锁屏时可触发 Credential Provider 解锁。
//
// 日志：作为后台服务运行时没有控制台，所有日志写入滚动日志文件（按天 + 20MB 分卷，保留 14 份）。
//   默认目录 C:\ProgramData\immurok\logs\immurok-service-YYYYMMDD.log（LocalSystem 可写、与用户会话无关）。
//   可在 appsettings.json 的 "Immurok:LogDirectory" 覆盖路径，"Serilog:MinimumLevel" 调级别。
//   控制台（F5 调试）模式下同时输出到控制台。

// 卸载清理入口（MSI 在移除文件前以 SYSTEM 身份调用）。
// 登录密码以 CRED_PERSIST_LOCAL_MACHINE 存在服务账户名下，卸载后不能留在系统里，
// 而只有与写入方相同的身份才删得掉——所以这一步放在服务自身入口，不放外部脚本。
// 不启动主机，删完即退。
if (args.Contains("--clear-credential", StringComparer.OrdinalIgnoreCase))
{
    return CredentialStore.DeleteStored() ? 0 : 1;
}

var builder = Host.CreateApplicationBuilder(args);

// 作为 Windows 服务运行（同时兼容控制台调试：直接 F5 运行即以控制台模式启动）。
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ImmurokService";
});

// ---- 解析日志目录：优先 appsettings 覆盖，否则 ProgramData\immurok\logs；失败回退到程序目录\logs ----
string logDir = builder.Configuration["Immurok:LogDirectory"] ?? string.Empty;
if (string.IsNullOrWhiteSpace(logDir))
{
    logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "immurok", "logs");
}
try { Directory.CreateDirectory(logDir); }
catch
{
    logDir = Path.Combine(AppContext.BaseDirectory, "logs");
    Directory.CreateDirectory(logDir);
}
string logPath = Path.Combine(logDir, "immurok-service-.log");

// ---- Serilog：级别从 appsettings 的 "Serilog" 节读取，Sink 在代码里固定（文件 + 控制台） ----
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.File(
        logPath,
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 20 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 14,
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(2),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}{NewLine}    {Message:lj}{NewLine}{Exception}")
    .WriteTo.Console(
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// 用 Serilog 取代默认日志提供程序（控制台/Debug/EventLog）。
builder.Logging.ClearProviders();
builder.Services.AddSerilog(Log.Logger, dispose: true);

// 核心单例。
builder.Services.AddSingleton<ImmurokSecurity>();
builder.Services.AddSingleton<PairingStore>();
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<OwnerStore>();
builder.Services.AddSingleton<DataDirSecurity>();
// 调用方分级（§3.3）。--allow-unsigned-clients 只给开发构建，安装包不传。
builder.Services.AddSingleton(new CallerPolicyOptions(
    args.Contains("--allow-unsigned-clients", StringComparer.OrdinalIgnoreCase)));
builder.Services.AddSingleton<CallerPolicy>();
builder.Services.AddSingleton<SecurityStatus>();
builder.Services.AddSingleton<FpInjectionSignal>();
builder.Services.AddSingleton<BleManager>();
builder.Services.AddSingleton<SessionMonitor>();
builder.Services.AddSingleton<ScreenUnlocker>();
builder.Services.AddSingleton<ScreenLocker>();
builder.Services.AddSingleton<AppSettings>();
builder.Services.AddSingleton<ImmurokService.Ssh.SshAgentServer>();
// 一键开关 SSH agent：接管 / 还原 Windows 内置 ssh-agent 服务（抢同一个管道名）。
builder.Services.AddSingleton<ImmurokService.Ssh.IWindowsServiceControl>(new ImmurokService.Ssh.WindowsServiceControl("ssh-agent"));
builder.Services.AddSingleton<ImmurokService.Ssh.ISystemSshAgentPrevStore>(sp => sp.GetRequiredService<AppSettings>());
builder.Services.AddSingleton<ImmurokService.Ssh.SystemSshAgent>();
builder.Services.AddSingleton<ImmurokService.Ssh.SshAgentFeature>();
builder.Services.AddSingleton<CommandHandlers>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddSingleton<CliServer>();

// 后台工作宿主：编排各组件生命周期（对应 macOS AppDelegate 的初始化职责）。
builder.Services.AddHostedService<Worker>();

try
{
    Log.Information("ImmurokService 进程启动，日志目录: {LogDir}", logDir);
    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ImmurokService 异常终止");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// 上面的 --clear-credential 分支用了 return，顶级语句的入口点因此返回 int，
// 末尾必须显式返回，否则 CS0161（并非所有代码路径都返回值）。
return 0;
