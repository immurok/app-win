using System.Text.Json;
using ImmurokCommon.Git;
using ImmurokService.Ssh;
using Xunit;

namespace ImmurokTests;

/// <summary>两条接管记录都要经 System.Text.Json 落盘（settings.json / client.json），确认 record 能来回。</summary>
public class PrevRecordJsonTests
{
    [Fact]
    public void SystemSshAgentPrev_roundtrips()
    {
        var prev = new SystemSshAgentPrev("Automatic", true);
        var back = JsonSerializer.Deserialize<SystemSshAgentPrev>(JsonSerializer.Serialize(prev));
        Assert.Equal(prev, back);
    }

    [Fact]
    public void GitSshPrev_roundtrips_with_nulls()
    {
        var prev = new GitSshPrev("plink.exe", null);
        var back = JsonSerializer.Deserialize<GitSshPrev>(JsonSerializer.Serialize(prev));
        Assert.Equal(prev, back);
    }
}
