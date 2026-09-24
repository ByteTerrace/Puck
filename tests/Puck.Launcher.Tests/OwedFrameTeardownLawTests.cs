using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// A host that produces frames settles what its simulation is still owed before it disposes its render root, so a
/// capture waiting on a frame is decided while the chain that would have served it is alive and never reaches the
/// disposal that refuses whatever a node still holds. Both frame-producing hosts run to their teardown at once (the
/// offscreen host on a zero exit backstop, the windowed host on a window that is already closed) over fakes that
/// record the order of the two steps. No GPU is involved.
/// </summary>
public sealed class OwedFrameTeardownLawTests {
    private const string DisposeRoot = "dispose render root";
    private const string SettleOwedFrames = "settle owed frames";

    private sealed class TeardownLog {
        public List<string> Steps { get; } = [];
    }
    private sealed class RecordingSimulation(TeardownLog log) : IFixedStepSimulation {
        public bool AwaitsFrame => false;
        public uint RatePerSecond => 30U;

        public bool HoldsClock(ulong withheldTicks) => false;
        public void SettleOwedFrames() => log.Steps.Add(item: OwedFrameTeardownLawTests.SettleOwedFrames);
        public void Step(in FixedStepContext context, in CommandSnapshot commands) { }
    }
    private sealed class RecordingRoot(TeardownLog log) : IRenderNode {
        public NodeDescriptor Descriptor { get; } = new(
            Name: "owed-frame-teardown",
            SurfaceId: SurfaceId.New()
        );

        public void Dispose() => log.Steps.Add(item: DisposeRoot);
        public Surface ProduceFrame(in FrameContext context) => default;
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }

    private static void AddRecordingSimulation(IServiceCollection services, TeardownLog log) {
        services.AddSingleton(implementationInstance: log);
        services.AddSingleton<IPrincipalResolver, ConsolePrincipal>();
        services.AddFixedStepSimulation<RecordingSimulation>(bindings: new NoBindings());
    }

    [Fact]
    public async Task TheOffscreenHostSettlesOwedFramesBeforeItDisposesTheRenderRoot() {
        var log = new TeardownLog();
        var builder = Host.CreateApplicationBuilder(settings: new HostApplicationBuilderSettings {
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(implementationInstance: new LauncherOptions {
            ExitAfter = TimeSpan.Zero,
        });
        builder.Services.AddSingleton(implementationInstance: new OffscreenRenderOptions(
            Height: 32U,
            Width: 32U
        ));
        builder.Services.AddSingleton<IRenderNode>(implementationInstance: new RecordingRoot(log: log));
        AddRecordingSimulation(
            log: log,
            services: builder.Services
        );
        builder.Services.AddLauncherOffscreenTerminal();

        using var host = builder.Build();

        await WindowedHostFixture.RunAsync(host: host);

        Assert.Equal(
            actual: log.Steps,
            expected: [SettleOwedFrames, DisposeRoot]
        );
    }
    [Fact]
    public async Task TheWindowedHostSettlesOwedFramesBeforeItDisposesTheRenderRoot() {
        var log = new TeardownLog();
        using var host = WindowedHostFixture.Build(
            configure: services => AddRecordingSimulation(
                log: log,
                services: services
            ),
            device: new WindowedHostFixture.NeverInitializedDeviceContext(),
            presenter: new WindowedHostFixture.FakePresenter(failure: null),
            root: new RecordingRoot(log: log)
        );

        await WindowedHostFixture.RunAsync(host: host);

        Assert.Equal(
            actual: log.Steps,
            expected: [SettleOwedFrames, DisposeRoot]
        );
    }
}
