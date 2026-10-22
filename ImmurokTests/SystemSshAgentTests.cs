using System.ServiceProcess;
using ImmurokService.Ssh;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ImmurokTests;

/// <summary>
/// 接管 / 还原 Windows 内置 ssh-agent 服务的状态机。服务控制用假实现，只验证决策，不碰 SCM。
/// </summary>
public class SystemSshAgentTests
{
    private sealed class FakeService : IWindowsServiceControl
    {
        public bool Exists { get; set; } = true;
        public ServiceStartMode StartMode { get; set; } = ServiceStartMode.Manual;
        public bool IsRunning { get; set; }
        public List<string> Calls { get; } = new();
        public Exception? Throw { get; set; }

        public void SetStartMode(ServiceStartMode mode) { Calls.Add($"mode={mode}"); if (Throw is not null) throw Throw; StartMode = mode; }
        public void Stop() { Calls.Add("stop"); if (Throw is not null) throw Throw; IsRunning = false; }
        public void Start() { Calls.Add("start"); if (Throw is not null) throw Throw; IsRunning = true; }
    }

    private sealed class FakeStore : ISystemSshAgentPrevStore
    {
        public SystemSshAgentPrev? Prev { get; set; }
    }

    private static SystemSshAgent Make(FakeService svc, FakeStore store)
        => new(svc, store, NullLogger<SystemSshAgent>.Instance);

    [Fact]
    public void Takeover_stops_and_disables_running_service_and_records_previous_state()
    {
        var svc = new FakeService { StartMode = ServiceStartMode.Automatic, IsRunning = true };
        var store = new FakeStore();

        var r = Make(svc, store).Takeover();

        Assert.Equal(TakeoverResult.Taken, r);
        Assert.False(svc.IsRunning);
        Assert.Equal(ServiceStartMode.Disabled, svc.StartMode);
        Assert.Equal(new SystemSshAgentPrev("Automatic", true), store.Prev);
    }

    [Fact]
    public void Takeover_twice_keeps_the_original_record()
    {
        var svc = new FakeService { StartMode = ServiceStartMode.Automatic, IsRunning = true };
        var store = new FakeStore();
        var agent = Make(svc, store);

        agent.Takeover();
        agent.Takeover();

        Assert.Equal(new SystemSshAgentPrev("Automatic", true), store.Prev);
    }

    [Fact]
    public void Takeover_when_service_absent_returns_absent_and_records_nothing()
    {
        var svc = new FakeService { Exists = false };
        var store = new FakeStore();

        var r = Make(svc, store).Takeover();

        Assert.Equal(TakeoverResult.Absent, r);
        Assert.Null(store.Prev);
        Assert.Empty(svc.Calls);
    }

    [Fact]
    public void Takeover_when_already_disabled_and_stopped_does_not_touch_service()
    {
        var svc = new FakeService { StartMode = ServiceStartMode.Disabled, IsRunning = false };
        var store = new FakeStore();

        var r = Make(svc, store).Takeover();

        Assert.Equal(TakeoverResult.Taken, r);
        Assert.Empty(svc.Calls);
        Assert.Equal(new SystemSshAgentPrev("Disabled", false), store.Prev);
    }

    [Fact]
    public void Takeover_reports_failed_when_scm_throws()
    {
        var svc = new FakeService { StartMode = ServiceStartMode.Manual, IsRunning = true, Throw = new InvalidOperationException("scm") };
        var store = new FakeStore();

        var r = Make(svc, store).Takeover();

        Assert.Equal(TakeoverResult.Failed, r);
    }

    [Fact]
    public void Restore_puts_start_mode_back_and_restarts_if_it_was_running()
    {
        var svc = new FakeService { StartMode = ServiceStartMode.Disabled, IsRunning = false };
        var store = new FakeStore { Prev = new SystemSshAgentPrev("Automatic", true) };

        var r = Make(svc, store).Restore();

        Assert.Equal(RestoreResult.Restored, r);
        Assert.Equal(ServiceStartMode.Automatic, svc.StartMode);
        Assert.True(svc.IsRunning);
        Assert.Null(store.Prev);
    }

    [Fact]
    public void Restore_does_not_start_service_that_was_stopped_before()
    {
        var svc = new FakeService { StartMode = ServiceStartMode.Disabled, IsRunning = false };
        var store = new FakeStore { Prev = new SystemSshAgentPrev("Manual", false) };

        Make(svc, store).Restore();

        Assert.Equal(ServiceStartMode.Manual, svc.StartMode);
        Assert.False(svc.IsRunning);
        Assert.DoesNotContain("start", svc.Calls);
    }

    [Fact]
    public void Restore_without_record_does_nothing()
    {
        var svc = new FakeService { StartMode = ServiceStartMode.Disabled };
        var store = new FakeStore();

        var r = Make(svc, store).Restore();

        Assert.Equal(RestoreResult.Nothing, r);
        Assert.Empty(svc.Calls);
    }

    [Fact]
    public void Restore_when_service_vanished_clears_record()
    {
        var svc = new FakeService { Exists = false };
        var store = new FakeStore { Prev = new SystemSshAgentPrev("Manual", false) };

        var r = Make(svc, store).Restore();

        Assert.Equal(RestoreResult.Nothing, r);
        Assert.Null(store.Prev);
    }

    [Fact]
    public void Restore_keeps_record_when_scm_throws_so_it_can_retry()
    {
        var svc = new FakeService { StartMode = ServiceStartMode.Disabled, Throw = new InvalidOperationException("scm") };
        var store = new FakeStore { Prev = new SystemSshAgentPrev("Manual", false) };

        var r = Make(svc, store).Restore();

        Assert.Equal(RestoreResult.Failed, r);
        Assert.NotNull(store.Prev);
    }
}
