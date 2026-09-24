using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// <see cref="LauncherHostRun"/> maps how a real windowed launcher host ended onto the process exit code: a backend
/// with no usable device is exit 2 and one unsupported line, and every other pump failure is rethrown so the process
/// cannot exit 0 after a pump crashed. The failures are raised by fakes (<see cref="WindowedHostFixture"/>); no GPU is
/// involved.
/// </summary>
public sealed class LauncherHostRunLawTests {
    [Fact]
    public async Task ADeviceTheBackendCannotBringUpExitsTwoWithTheUnsupportedLine() {
        var presenter = new WindowedHostFixture.FakePresenter(failure: new GpuDeviceUnavailableException(
            backend: "vulkan",
            reason: "no Vulkan physical devices were reported for the current instance."
        ));
        var host = WindowedHostFixture.Build(
            device: new WindowedHostFixture.CountingDeviceContext(),
            presenter: presenter
        );
        using var error = new StringWriter();

        var exit = await LauncherHostRun.RunAsync(
            error: error,
            host: host,
            label: "world"
        ).WaitAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: WindowedHostFixture.HostBudget
        );

        Assert.Equal(
            actual: exit,
            expected: 2
        );
        Assert.Equal(
            expected: "[world.host: unsupported: vulkan device unavailable: no Vulkan physical devices were reported for the current instance.]",
            actual: error.ToString().TrimEnd()
        );
    }
    [Fact]
    public async Task AnActivationFailureThatIsNotAnUnavailableDeviceIsRethrownAndNotCalledUnsupported() {
        var failure = new ArgumentException(message: "the surface binding carries no payload");
        var host = WindowedHostFixture.Build(
            device: new WindowedHostFixture.CountingDeviceContext(),
            presenter: new WindowedHostFixture.FakePresenter(failure: failure)
        );
        using var error = new StringWriter();

        var thrown = await Assert.ThrowsAsync<PresenterActivationException>(testCode: () => LauncherHostRun.RunAsync(
            error: error,
            host: host,
            label: "world"
        ).WaitAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: WindowedHostFixture.HostBudget
        ));

        Assert.Same(
            expected: failure,
            actual: thrown.InnerException
        );
        Assert.Empty(collection: error.ToString());
    }
    [Fact]
    public async Task APumpThatFaultsAfterActivatingMakesTheRunFailRatherThanExitZero() {
        // The presenter activates; the fixture's device then throws when the closed window's pump drains it.
        var host = WindowedHostFixture.Build(
            device: new WindowedHostFixture.CountingDeviceContext(),
            presenter: new WindowedHostFixture.FakePresenter(failure: null)
        );
        using var error = new StringWriter();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => LauncherHostRun.RunAsync(
            error: error,
            host: host,
            label: "world"
        ).WaitAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: WindowedHostFixture.HostBudget
        ));

        Assert.Equal(
            expected: "The fixture device was asked to drain.",
            actual: thrown.Message
        );
        Assert.Empty(collection: error.ToString());
    }
}
