using ImmurokCommon.Git;
using Xunit;

namespace ImmurokTests;

/// <summary>
/// 把 git 指向原生 OpenSSH（core.sshCommand / gpg.ssh.program）并可还原。git 本身用假实现。
/// </summary>
public class GitSshConfigTests
{
    private sealed class FakeGit : IGitConfig
    {
        public bool Available { get; set; } = true;
        public Dictionary<string, string> Values { get; } = new();
        public List<string> Calls { get; } = new();

        public string? Get(string key) { Calls.Add($"get {key}"); return Values.TryGetValue(key, out var v) ? v : null; }
        public void Set(string key, string value) { Calls.Add($"set {key}"); Values[key] = value; }
        public void Unset(string key) { Calls.Add($"unset {key}"); Values.Remove(key); }
    }

    private sealed class FakeStore : IGitSshPrevStore
    {
        public GitSshPrev? Prev { get; set; }
    }

    private const string Ssh = "C:/Windows/System32/OpenSSH/ssh.exe";
    private const string Keygen = "C:/Windows/System32/OpenSSH/ssh-keygen.exe";

    private static GitSshConfig Make(FakeGit git, FakeStore store) => new(git, store, Ssh, Keygen);

    [Fact]
    public void Apply_points_both_keys_at_native_openssh_and_records_absence()
    {
        var git = new FakeGit();
        var store = new FakeStore();

        var r = Make(git, store).Apply();

        Assert.Equal(GitSshResult.Configured, r);
        Assert.Equal(Ssh, git.Values[GitSshConfig.SshCommandKey]);
        Assert.Equal(Keygen, git.Values[GitSshConfig.SshProgramKey]);
        Assert.Equal(new GitSshPrev(null, null), store.Prev);
    }

    [Fact]
    public void Apply_records_existing_values_so_they_can_be_restored()
    {
        var git = new FakeGit();
        git.Values[GitSshConfig.SshCommandKey] = "plink.exe";
        var store = new FakeStore();

        Make(git, store).Apply();

        Assert.Equal(new GitSshPrev("plink.exe", null), store.Prev);
    }

    [Fact]
    public void Apply_twice_keeps_the_original_record()
    {
        var git = new FakeGit();
        git.Values[GitSshConfig.SshCommandKey] = "plink.exe";
        var store = new FakeStore();
        var cfg = Make(git, store);

        cfg.Apply();
        cfg.Apply();

        Assert.Equal(new GitSshPrev("plink.exe", null), store.Prev);
    }

    [Fact]
    public void Apply_without_git_does_nothing()
    {
        var git = new FakeGit { Available = false };
        var store = new FakeStore();

        var r = Make(git, store).Apply();

        Assert.Equal(GitSshResult.GitMissing, r);
        Assert.Empty(git.Calls);
        Assert.Null(store.Prev);
    }

    [Fact]
    public void Restore_puts_previous_values_back_and_unsets_keys_that_were_absent()
    {
        var git = new FakeGit();
        git.Values[GitSshConfig.SshCommandKey] = Ssh;
        git.Values[GitSshConfig.SshProgramKey] = Keygen;
        var store = new FakeStore { Prev = new GitSshPrev("plink.exe", null) };

        var r = Make(git, store).Restore();

        Assert.Equal(GitSshResult.Restored, r);
        Assert.Equal("plink.exe", git.Values[GitSshConfig.SshCommandKey]);
        Assert.False(git.Values.ContainsKey(GitSshConfig.SshProgramKey));
        Assert.Null(store.Prev);
    }

    [Fact]
    public void Restore_without_record_does_nothing()
    {
        var git = new FakeGit();
        git.Values[GitSshConfig.SshCommandKey] = Ssh;
        var store = new FakeStore();

        var r = Make(git, store).Restore();

        Assert.Equal(GitSshResult.Nothing, r);
        Assert.Empty(git.Calls);
        Assert.Equal(Ssh, git.Values[GitSshConfig.SshCommandKey]);
    }

    [Fact]
    public void Restore_without_git_keeps_record_for_a_later_attempt()
    {
        var git = new FakeGit { Available = false };
        var store = new FakeStore { Prev = new GitSshPrev("plink.exe", null) };

        var r = Make(git, store).Restore();

        Assert.Equal(GitSshResult.GitMissing, r);
        Assert.NotNull(store.Prev);
    }

    [Fact]
    public void Apply_reports_failed_and_records_nothing_when_git_throws()
    {
        var git = new ThrowingGit();
        var store = new FakeStore();

        var r = Make(git, store).Apply();

        Assert.Equal(GitSshResult.Failed, r);
        Assert.Null(store.Prev);
    }

    private sealed class ThrowingGit : IGitConfig
    {
        public bool Available => true;
        public string? Get(string key) => throw new InvalidOperationException("git");
        public void Set(string key, string value) => throw new InvalidOperationException("git");
        public void Unset(string key) => throw new InvalidOperationException("git");
    }

    private static GitSshConfig Make(ThrowingGit git, FakeStore store) => new(git, store, Ssh, Keygen);
}
