using System.Runtime.InteropServices;
using System.Text.Json;

namespace ImmurokService.Wsl;

/// <summary>
/// 枚举正在运行的 WSL2 虚拟机 ID（Host Compute Service）。Hyper-V socket 监听必须 bind 到具体
/// 虚拟机（见 <see cref="HvSocket"/>），所以桥服务要周期性对账这份列表。
/// HCS 枚举需要管理员权限：服务是 LocalSystem，没问题；控制台调试模式以普通用户跑时拿到的是
/// 拒绝访问，返回 null（调用方记一次日志即可）。
/// 返回的 JSON 形如
/// <c>[{"Id":"..","SystemType":"VirtualMachine","Owner":"WSL","RuntimeId":"..","State":"Running"}]</c>。
/// 只认 Owner == "WSL"：Docker Desktop、普通 Hyper-V 虚拟机都不给监听，它们连不上来。
/// </summary>
internal static class WslVms
{
    /// <summary>运行中的 WSL 虚拟机 ID；HCS 调用失败返回 null。</summary>
    public static List<Guid>? Enumerate(out int hresult)
    {
        hresult = 0;
        IntPtr op = IntPtr.Zero, doc = IntPtr.Zero;
        try
        {
            op = HcsCreateOperation(IntPtr.Zero, IntPtr.Zero);
            if (op == IntPtr.Zero) { hresult = unchecked((int)0x8007000E); return null; }
            hresult = HcsEnumerateComputeSystems(null, op);
            if (hresult < 0) return null;
            hresult = HcsWaitForOperationResult(op, 5000, out doc);
            if (hresult < 0 || doc == IntPtr.Zero) return null;
            return Parse(Marshal.PtrToStringUni(doc) ?? "[]");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            hresult = unchecked((int)0x8007007E); // 没装虚拟化组件：等同于没有 WSL2
            return null;
        }
        finally
        {
            if (doc != IntPtr.Zero) LocalFree(doc);
            if (op != IntPtr.Zero) HcsCloseOperation(op);
        }
    }

    /// <summary>解析 HCS 枚举结果。单独拆出来给单元测试。</summary>
    internal static List<Guid> Parse(string json)
    {
        var ids = new List<Guid>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return ids;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!Str(e, "Owner").Equals("WSL", StringComparison.OrdinalIgnoreCase)) continue;
            string state = Str(e, "State");
            if (state.Length != 0 && !state.Equals("Running", StringComparison.OrdinalIgnoreCase)) continue;
            // hvsocket 的 VmId 是 RuntimeId；WSL 的 Id 与它相同，缺 RuntimeId 时退回 Id。
            string id = Str(e, "RuntimeId");
            if (id.Length == 0) id = Str(e, "Id");
            if (Guid.TryParse(id, out Guid g) && !ids.Contains(g)) ids.Add(g);
        }
        return ids;

        static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    [DllImport("computecore.dll")]
    private static extern IntPtr HcsCreateOperation(IntPtr context, IntPtr callback);
    [DllImport("computecore.dll", CharSet = CharSet.Unicode)]
    private static extern int HcsEnumerateComputeSystems(string? query, IntPtr operation);
    [DllImport("computecore.dll")]
    private static extern int HcsWaitForOperationResult(IntPtr operation, uint timeoutMs, out IntPtr resultDocument);
    [DllImport("computecore.dll")]
    private static extern void HcsCloseOperation(IntPtr operation);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr p);
}
