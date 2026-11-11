using ImmurokCommon.Wsl;
using ImmurokService.Wsl;
using Xunit;

namespace ImmurokTests;

public class WslDistroListTests
{
    [Fact]
    public void Parses_English_Output_With_Default_Marker()
    {
        string output = "  NAME            STATE           VERSION\r\n" +
                        "* Ubuntu          Running         2\r\n" +
                        "  Debian          Stopped         2\r\n" +
                        "  legacy          Stopped         1\r\n";
        var list = WslDistroList.Parse(output);
        Assert.Equal(3, list.Count);
        Assert.Equal(new WslDistro("Ubuntu", 2, true), list[0]);
        Assert.Equal(new WslDistro("Debian", 2, false), list[1]);
        Assert.Equal(new WslDistro("legacy", 1, false), list[2]);
    }

    [Fact]
    public void Does_Not_Depend_On_Header_Text()
    {
        // 表头会本地化；数据行的状态列也会。只看首列和末列。
        string output = "  名称            状态            版本\r\n" +
                        "* Ubuntu-24.04    已停止          2\r\n";
        var list = WslDistroList.Parse(output);
        Assert.Single(list);
        Assert.Equal("Ubuntu-24.04", list[0].Name);
        Assert.Equal(2, list[0].Version);
        Assert.True(list[0].IsDefault);
    }

    [Fact]
    public void Tolerates_Nul_Bytes_Bom_And_Blank_Lines()
    {
        string output = "﻿  NAME\0  STATE  VERSION\r\n\r\n* Ubuntu  Stopped  2\0\r\n\r\n";
        var list = WslDistroList.Parse(output);
        Assert.Single(list);
        Assert.Equal("Ubuntu", list[0].Name);
    }

    [Fact]
    public void Empty_Or_Error_Output_Gives_Nothing()
    {
        Assert.Empty(WslDistroList.Parse(""));
        Assert.Empty(WslDistroList.Parse("Windows Subsystem for Linux has no installed distributions.\r\n"));
    }

    [Theory]
    [InlineData("Ubuntu", 2, true)]
    [InlineData("Ubuntu", 1, false)]                 // WSL1：没有 vsock
    [InlineData("docker-desktop", 2, false)]         // Docker Desktop 的内部发行版
    [InlineData("docker-desktop-data", 2, false)]
    public void Installable_Means_Wsl2_And_Not_Docker(string name, int version, bool expected)
        => Assert.Equal(expected, WslDistroList.IsInstallable(new WslDistro(name, version, false)));
}

public class WslInstallerTests
{
    [Fact]
    public void Install_Script_Never_Embeds_Distro_Or_Token()
    {
        // 发行版名经 $1、token 经环境变量传入：都不拼进脚本文本，不存在引号逃逸。
        string on = WslInstaller.InstallScript(sshAgent: true);
        Assert.Contains("tar xzf - -C /", on);
        Assert.Contains("--distro \"$1\"", on);
        Assert.Contains("--ssh-agent on", on);
        Assert.DoesNotContain("--token", on);
        Assert.Contains("--ssh-agent off", WslInstaller.InstallScript(sshAgent: false));
    }

    [Fact]
    public void Failure_Reason_Is_Last_Non_Empty_Line()
    {
        var r = new WslInstallResult("Ubuntu", false, 2, "bridge-version: 0.9.3\nresult: Windows service unreachable\n\n");
        Assert.Equal("result: Windows service unreachable", r.LastLine);
        Assert.Equal("exit 1", new WslInstallResult("Ubuntu", false, 1, "").LastLine);
    }
}

public class WslPreambleTests
{
    private static readonly string Token = new('a', 64);

    [Fact]
    public void Parses_Fields_In_Any_Order_And_Ignores_Unknown_Keys()
    {
        var p = WslPreamble.Parse($"IMK-WSL/1 distro=Ubuntu-24.04 future=1 uid=0 token={Token} pid=42 pids=42,1 comm=sudo age=3");
        Assert.NotNull(p);
        Assert.Equal(Token, p!.Token);
        Assert.Equal("0", p.Uid);
        Assert.Equal("Ubuntu-24.04", p.Distro);
        Assert.Equal("42", p.Pid);
        Assert.Equal("sudo", p.Comm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("STATUS")]
    [InlineData("IMK-WSL/2 token=abc")]
    [InlineData("IMK-WSL/1 uid=0 distro=Ubuntu")]   // 没有 token
    public void Rejects_Malformed(string? line) => Assert.Null(WslPreamble.Parse(line));

    [Theory]
    [InlineData("", false)]              // 旧桥：没有这个键，不能给它发提示帧
    [InlineData(" hints=1", true)]
    [InlineData(" hints=0", false)]
    [InlineData(" hints=yes", false)]
    public void Hints_Only_When_Bridge_Says_So(string suffix, bool expected)
        => Assert.Equal(expected, WslPreamble.Parse($"IMK-WSL/1 token={Token} uid=1000{suffix}")!.Hints);

    [Fact]
    public void Hint_Frame_Is_A_Well_Formed_Agent_Frame()
    {
        // [uint32 BE 长度 = 3][0xF1][code][arg]：桥按 ssh-agent 的帧格式把它从应答流里摘出来。
        Assert.Equal(new byte[] { 0, 0, 0, 3, 0xF1, 2, 1 }, WslHint.Frame(WslHint.Mismatch, 1));
        Assert.Equal(new byte[] { 0, 0, 0, 3, 0xF1, 3, 0 }, WslHint.Frame(WslHint.Signing));
    }

    [Fact]
    public void Log_Fields_Are_Sanitized()
    {
        var p = WslPreamble.Parse($"IMK-WSL/1 token={Token} distro=a/b;c comm=x{new string('y', 100)}");
        Assert.Equal("a?b?c", p!.Distro);
        Assert.Equal(64, p.Comm.Length);
    }
}

public class WslVmsTests
{
    [Fact]
    public void Only_Running_Wsl_Vms_Count()
    {
        string json = """
            [{"Id":"04087A08-9107-408A-841F-0CC350D58677","SystemType":"VirtualMachine","Owner":"WSL","RuntimeId":"04087a08-9107-408a-841f-0cc350d58677","State":"Running"},
             {"Id":"11111111-1111-1111-1111-111111111111","SystemType":"VirtualMachine","Owner":"Docker","RuntimeId":"11111111-1111-1111-1111-111111111111","State":"Running"},
             {"Id":"22222222-2222-2222-2222-222222222222","SystemType":"VirtualMachine","Owner":"WSL","RuntimeId":"22222222-2222-2222-2222-222222222222","State":"Stopped"},
             {"Id":"33333333-3333-3333-3333-333333333333","SystemType":"Container","Owner":"WSL"}]
            """;
        var ids = WslVms.Parse(json);
        Assert.Equal(2, ids.Count);
        Assert.Equal(new Guid("04087a08-9107-408a-841f-0cc350d58677"), ids[0]);
        Assert.Equal(new Guid("33333333-3333-3333-3333-333333333333"), ids[1]); // 缺 RuntimeId / State：退回 Id
    }

    [Fact]
    public void Empty_And_Non_Array_Are_Empty()
    {
        Assert.Empty(WslVms.Parse("[]"));
        Assert.Empty(WslVms.Parse("{}"));
    }
}

public class HvSocketTests
{
    [Fact]
    public void Vsock_Port_Maps_To_Service_Guid_Template()
    {
        Assert.Equal(new Guid("494d4b01-facb-11e6-bd58-64006a7986d3"), HvSocket.ServiceIdForPort(WslBridgeServer.CliPort));
        Assert.Equal(new Guid("494d4b02-facb-11e6-bd58-64006a7986d3"), HvSocket.ServiceIdForPort(WslBridgeServer.SshPort));
    }
}
