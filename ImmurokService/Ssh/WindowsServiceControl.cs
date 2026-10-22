using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace ImmurokService.Ssh;

/// <summary>
/// <see cref="IWindowsServiceControl"/> 的 SCM 实现。查询 / 启停走 <see cref="ServiceController"/>，
/// 改启动类型它没有 setter，直接 P/Invoke <c>ChangeServiceConfig</c>（其余字段全 SERVICE_NO_CHANGE），
/// 不 shell 出 sc.exe。运行身份是 LocalSystem，权限够。
/// </summary>
public sealed class WindowsServiceControl : IWindowsServiceControl
{
    private readonly string _name;

    public WindowsServiceControl(string name) => _name = name;

    public bool Exists => ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, _name, StringComparison.OrdinalIgnoreCase));

    public ServiceStartMode StartMode
    {
        get { using var sc = new ServiceController(_name); return sc.StartType; }
    }

    public bool IsRunning
    {
        get { using var sc = new ServiceController(_name); return sc.Status != ServiceControllerStatus.Stopped; }
    }

    public void Stop()
    {
        using var sc = new ServiceController(_name);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.CanStop) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
    }

    public void Start()
    {
        using var sc = new ServiceController(_name);
        if (sc.Status == ServiceControllerStatus.Running) return;
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
    }

    public void SetStartMode(ServiceStartMode mode)
    {
        const uint SC_MANAGER_CONNECT = 0x0001;
        const uint SERVICE_CHANGE_CONFIG = 0x0002;
        const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            IntPtr svc = OpenService(scm, _name, SERVICE_CHANGE_CONFIG);
            if (svc == IntPtr.Zero) throw new Win32Exception();
            try
            {
                if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, (uint)mode, SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception();
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string serviceName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
