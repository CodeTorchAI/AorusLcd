using AorusLcd.Core;
using AorusLcd.Gui.Services;

namespace AorusLcd.Tests;

/// <summary>
/// Verifies that a panel repair never runs while GIGABYTE Control Center's LCD service could still
/// be writing to the same bus, and never strands that service without saying so.
/// </summary>
public class PanelRecoveryCoordinatorTests
{
    private static (PanelRecoveryCoordinator Coordinator, FakePanelRecovery Hardware, FakeServiceControl Services)
        Build(ServiceState gccState)
    {
        var hardware = new FakePanelRecovery();
        var services = new FakeServiceControl { GccState = gccState };
        return (new PanelRecoveryCoordinator(hardware, services), hardware, services);
    }

    [Fact]
    public async Task Stops_The_Vendor_Service_Then_Restarts_It()
    {
        var (coordinator, hardware, services) = Build(ServiceState.Running);

        var outcome = await coordinator.RecoverAsync();

        Assert.True(outcome.Recovered);
        Assert.False(outcome.VendorServiceLeftStopped);
        Assert.Equal(1, services.StopCalls);
        Assert.Equal(1, services.StartCalls);
        Assert.Equal(1, hardware.Calls);
    }

    [Fact]
    public async Task Leaves_The_Vendor_Service_Alone_When_It_Is_Not_Running()
    {
        var (coordinator, hardware, services) = Build(ServiceState.NotInstalled);

        var outcome = await coordinator.RecoverAsync();

        Assert.True(outcome.Recovered);
        Assert.Equal(0, services.StopCalls);
        Assert.Equal(0, services.StartCalls); // nothing was stopped, so nothing is owed a restart
        Assert.Equal(1, hardware.Calls);
    }

    [Fact]
    public async Task Stops_The_Vendor_Service_While_It_Is_Still_Transitioning()
    {
        // A service on its way up or down still owns the bus, so it must not be treated as clear.
        var (coordinator, hardware, services) = Build(ServiceState.Transitioning);

        var outcome = await coordinator.RecoverAsync();

        Assert.True(outcome.Recovered);
        Assert.Equal(1, services.StopCalls);
        Assert.Equal(1, hardware.Calls);
    }

    [Fact]
    public async Task Skips_The_Repair_When_The_Vendor_Service_Will_Not_Stop()
    {
        var (coordinator, hardware, services) = Build(ServiceState.Running);
        services.StopError = new InvalidOperationException("exited with code 5");
        services.GccStateAfterFailedStop = ServiceState.Running;

        var outcome = await coordinator.RecoverAsync();

        Assert.False(outcome.Recovered);
        Assert.Equal(0, hardware.Calls); // nothing may be written to a contended bus
        Assert.False(outcome.VendorServiceLeftStopped);
        Assert.Contains("could not be stopped", outcome.ToStatusMessage());
    }

    [Fact]
    public async Task Proceeds_When_The_Stop_Reports_Failure_But_The_Service_Did_Stop()
    {
        var (coordinator, hardware, services) = Build(ServiceState.Running);
        services.StopError = new InvalidOperationException("exited with code 1062");
        services.GccStateAfterFailedStop = ServiceState.Stopped;

        var outcome = await coordinator.RecoverAsync();

        Assert.True(outcome.Recovered);
        Assert.Equal(1, hardware.Calls);
        Assert.Equal(1, services.StartCalls); // the run owns the restart even though sc.exe complained
    }

    [Fact]
    public async Task Restarts_The_Vendor_Service_Even_When_The_Repair_Throws()
    {
        var (coordinator, hardware, services) = Build(ServiceState.Running);
        hardware.Error = new InvalidOperationException("no panel answered");

        var outcome = await coordinator.RecoverAsync();

        Assert.False(outcome.Recovered);
        Assert.Equal(1, services.StartCalls);
        Assert.Contains("no panel answered", outcome.ToStatusMessage());
    }

    [Fact]
    public async Task Reports_A_Vendor_Service_It_Could_Not_Restart()
    {
        var (coordinator, _, services) = Build(ServiceState.Running);
        services.StartError = new InvalidOperationException("timed out");

        var outcome = await coordinator.RecoverAsync();

        Assert.True(outcome.Recovered);
        Assert.True(outcome.VendorServiceLeftStopped);
        Assert.Contains("still stopped", outcome.ToStatusMessage());
    }

    [Fact]
    public async Task Reports_Both_A_Failed_Repair_And_A_Stranded_Vendor_Service()
    {
        var (coordinator, hardware, services) = Build(ServiceState.Running);
        hardware.Error = new InvalidOperationException("no panel answered");
        services.StartError = new InvalidOperationException("timed out");

        var outcome = await coordinator.RecoverAsync();

        string message = outcome.ToStatusMessage();
        Assert.Contains("no panel answered", message);
        Assert.Contains("still stopped", message);
    }

    [Fact]
    public async Task Repairs_Without_Touching_The_Overlay_Or_NVRAM()
    {
        // docs/RECOVERY.md: sending no E1 and not saving is the combination that has relit a dark panel.
        var (coordinator, hardware, _) = Build(ServiceState.Running);

        await coordinator.RecoverAsync();

        Assert.Equal(OverlayAction.Leave, hardware.LastOptions!.Overlay);
        Assert.False(hardware.LastOptions.Save);
    }

    [Fact]
    public async Task Owes_A_Restart_Only_While_The_Vendor_Service_Is_Stopped()
    {
        // Shutdown reads this to avoid exiting while GCC's service is stranded.
        var hardware = new FakePanelRecovery();
        var services = new FakeServiceControl { GccState = ServiceState.Running };
        var coordinator = new PanelRecoveryCoordinator(hardware, services);
        bool owedDuringRepair = false;
        hardware.OnRecover = () => owedDuringRepair = coordinator.OwesVendorServiceRestart;

        Assert.False(coordinator.OwesVendorServiceRestart);
        await coordinator.RecoverAsync();

        Assert.True(owedDuringRepair);
        Assert.False(coordinator.OwesVendorServiceRestart);
    }

    [Fact]
    public async Task Owes_Nothing_When_It_Never_Stopped_The_Vendor_Service()
    {
        var hardware = new FakePanelRecovery();
        var services = new FakeServiceControl { GccState = ServiceState.NotInstalled };
        var coordinator = new PanelRecoveryCoordinator(hardware, services);
        bool owedDuringRepair = true;
        hardware.OnRecover = () => owedDuringRepair = coordinator.OwesVendorServiceRestart;

        await coordinator.RecoverAsync();

        Assert.False(owedDuringRepair);
    }

    private sealed class FakePanelRecovery : IPanelRecovery
    {
        public Exception? Error { get; set; }
        public int Calls { get; private set; }
        public RecoveryOptions? LastOptions { get; private set; }

        /// <summary>Runs while the repair is in flight, for observing coordinator state mid-run.</summary>
        public Action? OnRecover { get; set; }

        public Task RecoverPanelAsync(RecoveryOptions options, IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            Calls++;
            LastOptions = options;
            OnRecover?.Invoke();
            return Error is null ? Task.CompletedTask : Task.FromException(Error);
        }
    }

    private sealed class FakeServiceControl : IServiceControl
    {
        public ServiceState GccState { get; set; } = ServiceState.Running;
        public Exception? StopError { get; set; }
        public Exception? StartError { get; set; }

        /// <summary>State the service is observed in after a stop that reported failure.</summary>
        public ServiceState? GccStateAfterFailedStop { get; set; }

        public int StopCalls { get; private set; }
        public int StartCalls { get; private set; }

        public ServiceState GetState() => ServiceState.NotInstalled;

        public ServiceState GetGccServiceState() => GccState;

        public Task InstallAsync() => Task.CompletedTask;

        public Task UninstallAsync() => Task.CompletedTask;

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public Task StopGccServiceAsync()
        {
            StopCalls++;
            if (StopError is not null)
            {
                GccState = GccStateAfterFailedStop ?? GccState;
                return Task.FromException(StopError);
            }
            GccState = ServiceState.Stopped;
            return Task.CompletedTask;
        }

        public Task StartGccServiceAsync()
        {
            StartCalls++;
            if (StartError is not null)
            {
                return Task.FromException(StartError);
            }
            GccState = ServiceState.Running;
            return Task.CompletedTask;
        }
    }
}
