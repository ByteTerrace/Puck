using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// The windowed host's pump runs on its own thread, and its presenter activation is where a windowed boot first brings
/// its GPU device up. A pump that throws faults its hosted service's <see cref="BackgroundService.ExecuteTask"/> and
/// stops the host with the exception in hand; an unhandled exception on a raw thread would end the process before
/// anything could report it. No GPU is involved: see <see cref="WindowedHostFixture"/>.
/// </summary>
public sealed class WindowPumpFaultLawTests {
    private static LauncherWindowHostedService Pump(IHost host) => host.Services.GetServices<IHostedService>().OfType<LauncherWindowHostedService>().Single();

    [Fact]
    public async Task AThrowingActivationFaultsTheWindowPumpWithThePresenterActivationFailure() {
        var failure = new InvalidOperationException(message: "no adapter satisfies the device requirements");
        var presenter = new WindowedHostFixture.FakePresenter(failure: failure);
        var device = new WindowedHostFixture.CountingDeviceContext();
        var host = WindowedHostFixture.Build(
            device: device,
            presenter: presenter
        );
        var pump = Pump(host: host);

        await WindowedHostFixture.RunAsync(host: host);

        Assert.Equal(
            expected: 1,
            actual: presenter.ActivateCalls
        );
        Assert.NotNull(@object: pump.ExecuteTask);
        Assert.True(condition: pump.ExecuteTask.IsFaulted);

        var activation = Assert.IsType<PresenterActivationException>(@object: pump.ExecuteTask.Exception!.InnerException);

        Assert.Same(
            expected: failure,
            actual: activation.InnerException
        );
    }
    [Fact]
    public async Task APresenterThatNeverActivatedIsNotAskedToDrainItsDevice() {
        var presenter = new WindowedHostFixture.FakePresenter(failure: new InvalidOperationException(message: "no adapter satisfies the device requirements"));
        var device = new WindowedHostFixture.CountingDeviceContext();
        var host = WindowedHostFixture.Build(
            device: device,
            presenter: presenter
        );
        var pump = Pump(host: host);

        await WindowedHostFixture.RunAsync(host: host);

        Assert.Equal(
            expected: 0,
            actual: device.WaitIdleCalls
        );
        Assert.IsType<PresenterActivationException>(@object: pump.ExecuteTask!.Exception!.InnerException);
    }
    [Fact]
    public async Task AnActivatedPresenterDrainsItsDeviceOnceWhenTheWindowCloses() {
        var presenter = new WindowedHostFixture.FakePresenter(failure: null);
        var device = new WindowedHostFixture.CountingDeviceContext();
        var host = WindowedHostFixture.Build(
            device: device,
            presenter: presenter
        );
        var pump = Pump(host: host);

        await WindowedHostFixture.RunAsync(host: host);

        Assert.Equal(
            expected: 1,
            actual: device.WaitIdleCalls
        );
        // The fixture's drain throws, and a pump that throws after activating faults with that failure unwrapped.
        Assert.True(condition: pump.ExecuteTask!.IsFaulted);
        Assert.IsType<InvalidOperationException>(@object: pump.ExecuteTask.Exception!.InnerException);
    }
}
