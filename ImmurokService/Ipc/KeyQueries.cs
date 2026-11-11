using System.Text;
using ImmurokService.Ble;
using ImmurokService.Platform;

namespace ImmurokService.Ipc;

/// <summary>
/// 按名字列出 / 读取设备密钥库条目。imk 命名管道（<see cref="CliServer"/>）与 WSL 会话
/// （<see cref="ImmurokService.Wsl.WslCliSession"/>）共用，两边只是回包格式不同。
/// 这里不做任何身份校验，调用方各自把关。
/// </summary>
public sealed class KeyQueries
{
    private readonly BleManager _ble;
    private readonly AppSettings _settings;

    public KeyQueries(BleManager ble, AppSettings settings)
    {
        _ble = ble;
        _settings = settings;
    }

    /// <summary>类别名 → 设备类别号；不认识返回 -1。</summary>
    public static int CatOf(string s) => s.Trim() switch
    {
        "ssh" => BleManager.CatSsh,
        "otp" => BleManager.CatOtp,
        "api" => BleManager.CatApi,
        _ => -1,
    };

    public async Task<List<string>> ListNamesAsync(byte cat)
    {
        int count = await _ble.GetKeyCountAsync(cat).ConfigureAwait(false);
        var names = new List<string>();
        for (byte i = 0; i < count; i++)
        {
            string? n = await NameAtAsync(cat, i).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(n)) names.Add(n);
        }
        return names;
    }

    /// <summary>
    /// 读一个条目。成功时 Value 是值（otp = 当前 6 位码，走指纹门；api = 明文；ssh = OpenSSH 公钥行，
    /// 私钥永不导出）；失败时 Value 是原因码（NOT_FOUND / DISABLED / DENY / READ_FAILED / EMPTY）。
    /// </summary>
    public async Task<(bool Ok, string Value)> GetAsync(byte cat, string name)
    {
        int idx = await FindIndexAsync(cat, name).ConfigureAwait(false);
        if (idx < 0) return (false, "NOT_FOUND");

        if (cat == BleManager.CatOtp)
        {
            if (!_settings.OtpEnabled) return (false, "DISABLED"); // 功能开关：OTP 已关闭
            string? code = await _ble.GetOtpCodeAsync((byte)idx).ConfigureAwait(false);
            return code is null ? (false, "DENY") : (true, code);
        }
        if (cat == BleManager.CatApi)
        {
            byte[]? entry = await _ble.ReadKeyEntryAsync(BleManager.CatApi, (byte)idx).ConfigureAwait(false);
            if (entry is null || entry.Length <= 32) return (false, "READ_FAILED");
            // api_entry: name[32] + key[128]。取 key 区，截到首个 NUL。
            int end = 32;
            while (end < entry.Length && entry[end] != 0) end++;
            string value = Encoding.UTF8.GetString(entry, 32, end - 32);
            return value.Length == 0 ? (false, "EMPTY") : (true, value);
        }
        // ssh：返回 OpenSSH 公钥行（不导出私钥）
        byte[]? pub = await _ble.SshGetPubAsync((byte)idx).ConfigureAwait(false);
        if (pub is null) return (false, "READ_FAILED");
        return (true, BleManager.SshAuthorizedKey(pub, name));
    }

    private async Task<int> FindIndexAsync(byte cat, string name)
    {
        int count = await _ble.GetKeyCountAsync(cat).ConfigureAwait(false);
        for (byte i = 0; i < count; i++)
        {
            string? n = await NameAtAsync(cat, i).ConfigureAwait(false);
            if (n == name) return i;
        }
        return -1;
    }

    private async Task<string?> NameAtAsync(byte cat, byte idx)
    {
        if (cat == BleManager.CatOtp)
            return (await _ble.ReadOtpNameServiceAsync(idx).ConfigureAwait(false))?.Name;
        return await _ble.ReadKeyNameAsync(cat, idx).ConfigureAwait(false);
    }
}
