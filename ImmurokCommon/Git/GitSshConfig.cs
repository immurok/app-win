namespace ImmurokCommon.Git;

/// <summary>对 <c>git config --global</c> 的最小封装，便于测试时替换。Get 返回 null 表示键不存在。</summary>
public interface IGitConfig
{
    bool Available { get; }
    string? Get(string key);
    void Set(string key, string value);
    void Unset(string key);
}

/// <summary>接管前两个键的原值（null = 原本没设），用于一键关闭时还原。</summary>
public sealed record GitSshPrev(string? SshCommand, string? SshProgram);

/// <summary>接管记录的持久化（客户端 client.json）。</summary>
public interface IGitSshPrevStore
{
    GitSshPrev? Prev { get; set; }
}

public enum GitSshResult { Configured, Restored, Nothing, GitMissing, Failed }

/// <summary>
/// 让 git 改用 Windows 原生 OpenSSH：Git for Windows 自带的 MSYS ssh / ssh-keygen 不认命名管道
/// <c>\\.\pipe\openssh-ssh-agent</c>，不改这两个键 git 会静默回落到问密码。
/// <list type="bullet">
/// <item><c>core.sshCommand</c> → 原生 ssh.exe（push / pull / fetch）</item>
/// <item><c>gpg.ssh.program</c> → 原生 ssh-keygen.exe（<c>git commit -S</c> 走 <c>gpg.format=ssh</c> 时用；format 本身不动）</item>
/// </list>
/// 记录只写一次（重复开启不覆盖）；git 不在时还原保留记录以便下次重试。
/// </summary>
public sealed class GitSshConfig
{
    public const string SshCommandKey = "core.sshCommand";
    public const string SshProgramKey = "gpg.ssh.program";

    private readonly IGitConfig _git;
    private readonly IGitSshPrevStore _store;
    private readonly string _nativeSsh;
    private readonly string _nativeKeygen;

    public GitSshConfig(IGitConfig git, IGitSshPrevStore store, string nativeSsh, string nativeKeygen)
    {
        _git = git;
        _store = store;
        _nativeSsh = nativeSsh;
        _nativeKeygen = nativeKeygen;
    }

    public GitSshResult Apply()
    {
        if (!_git.Available) return GitSshResult.GitMissing;
        try
        {
            _store.Prev ??= new GitSshPrev(_git.Get(SshCommandKey), _git.Get(SshProgramKey));
            _git.Set(SshCommandKey, _nativeSsh);
            _git.Set(SshProgramKey, _nativeKeygen);
            return GitSshResult.Configured;
        }
        catch
        {
            return GitSshResult.Failed;
        }
    }

    public GitSshResult Restore()
    {
        var prev = _store.Prev;
        if (prev is null) return GitSshResult.Nothing;
        if (!_git.Available) return GitSshResult.GitMissing;
        try
        {
            Put(SshCommandKey, prev.SshCommand);
            Put(SshProgramKey, prev.SshProgram);
            _store.Prev = null;
            return GitSshResult.Restored;
        }
        catch
        {
            return GitSshResult.Failed;
        }
    }

    private void Put(string key, string? value)
    {
        if (value is null) _git.Unset(key);
        else _git.Set(key, value);
    }
}
