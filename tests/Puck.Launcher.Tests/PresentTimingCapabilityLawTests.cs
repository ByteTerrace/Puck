using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.Hosting;
using Xunit;

namespace Puck.Launcher.Tests;

public sealed class PresentTimingCapabilityLawTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInjectedSourceFlowsToRenderChildren(bool offscreen) {
        var services = new ServiceCollection();
        var timing = new Timing { LastPresentTiming = new(PresentCount: 4, PresentTimestampTicks: 1700), };
        services.AddSingleton<IPresentTimingFeedback>(implementationInstance: timing);
        if (offscreen) {
            services.AddLauncherOffscreenTerminal();
        } else {
            services.AddLauncherTerminal();
        }
        using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IHostContext>();
        Assert.True(condition: host.TryResolveCapability<IPresentTimingFeedback>(capability: out var resolved));
        Assert.Same(expected: timing, actual: resolved);
        Assert.False(condition: host.HoldsCapability<IPresentTimingFeedback>(capability: out _));
        timing.LastPresentTiming = new(PresentCount: 5, PresentTimestampTicks: 1900);
        Assert.Equal(expected: timing.LastPresentTiming, actual: resolved.LastPresentTiming);
    }
    [Fact]
    public void TheDefaultSourceTracksTheActivePresenterAcrossASwitch() {
        var services = new ServiceCollection();
        using var first = new Presenter { LastPresentTiming = new(PresentCount: 2, PresentTimestampTicks: 300), };
        using var second = new Presenter { LastPresentTiming = new(PresentCount: 9, PresentTimestampTicks: 700), };
        using var switcher = new BackendSwitcher(current: first, currentName: "first", other: second, otherName: "second");
        services.AddSingleton<ISurfacePresenter>(implementationInstance: switcher);
        services.AddLauncherTerminal();
        using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IHostContext>();
        Assert.True(condition: host.TryResolveCapability<IPresentTimingFeedback>(capability: out var timing));
        switcher.Activate(binding: default, width: 1, height: 1);
        Assert.Equal(expected: first.LastPresentTiming, actual: timing.LastPresentTiming);
        switcher.Switch();
        Assert.Equal(expected: second.LastPresentTiming, actual: timing.LastPresentTiming);
    }
    [Fact]
    public void PublishingTimingDoesNotTakeASecondDisposalOwnershipOfThePresenter() {
        var services = new ServiceCollection();
        var presenter = new Presenter();
        services.AddSingleton<ISurfacePresenter>(implementationFactory: _ => presenter);
        services.AddLauncherTerminal();
        using (var provider = services.BuildServiceProvider()) {
            var host = provider.GetRequiredService<IHostContext>();
            Assert.True(condition: host.TryResolveCapability<IPresentTimingFeedback>(capability: out var timing));
            _ = timing.LastPresentTiming;
        }
        Assert.Equal(expected: 1, actual: presenter.DisposeCalls);
    }
    [Fact]
    public void ResolvingHostCapabilitiesDoesNotResolveThePresenter() {
        var services = new ServiceCollection();
        var resolutions = 0;
        services.AddSingleton<ISurfacePresenter>(implementationFactory: _ => { resolutions++; return new Presenter(); });
        services.AddLauncherTerminal();
        using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IHostContext>();
        Assert.True(condition: host.TryResolveCapability<IPresentTimingFeedback>(capability: out var timing));
        Assert.Equal(expected: 0, actual: resolutions);
        _ = timing.LastPresentTiming;
        Assert.Equal(expected: 1, actual: resolutions);
    }
    [Fact]
    public void AHostWithoutAPresenterReportsUnavailable() {
        var services = new ServiceCollection();
        services.AddLauncherOffscreenTerminal();
        services.AddSingleton<ISurfacePresenter>(implementationFactory: _ => throw new InvalidOperationException(message: "Offscreen timing must not resolve a presenter."));
        using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IHostContext>();
        Assert.True(condition: host.TryResolveCapability<IPresentTimingFeedback>(capability: out var timing));
        Assert.Equal(expected: PresentTimingSample.Unavailable, actual: timing.LastPresentTiming);
    }

    private sealed class Timing : IPresentTimingFeedback {
        public PresentTimingSample LastPresentTiming { get; set; }
    }
    private sealed class Presenter : ISurfacePresenter, IPresentTimingFeedback {
        public DisplayOutput? Output => null;
        public int DisposeCalls { get; private set; }
        public PresentTimingSample LastPresentTiming { get; set; }
        public void Activate(NativeSurfaceBinding binding, uint width, uint height) { }
        public void BeginFrame(uint width, uint height) { }
        public void Deactivate() { }
        public void Dispose() => DisposeCalls++;
        public void Present(Surface surface) { }
    }
}
