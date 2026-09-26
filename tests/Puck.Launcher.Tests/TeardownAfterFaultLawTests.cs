using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Gpu;
using Puck.Overlays;
using Puck.Shaders;
using Puck.Testing;
using Puck.Text;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// A pump that faulted keeps its fault through teardown, and teardown releases only what was acquired. The windowed
/// case these laws stand for is a boot whose device never came up: the presenter's activation raises
/// <see cref="GpuDeviceUnavailableException"/>, and then the render root, whose GPU consumers never allocated anything,
/// is disposed against a renderer that was never initialized. No GPU is involved; see <see cref="WindowedHostFixture"/>.
/// </summary>
public sealed class TeardownAfterFaultLawTests {
    private const string Unsupported = "[world.host: unsupported: vulkan device unavailable: no Vulkan physical devices were reported for the current instance.]";

    private static GpuDeviceUnavailableException NoDevice() => new(
        backend: "vulkan",
        reason: "no Vulkan physical devices were reported for the current instance."
    );
    private static async Task<(int Exit, string Error)> RunAsync(IHost host) {
        using var error = new StringWriter();
        var exit = await LauncherHostRun.RunAsync(
            error: error,
            host: host,
            label: "world"
        ).WaitAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: WindowedHostFixture.HostBudget
        );

        return (exit, error.ToString().TrimEnd());
    }
    // The shipped overlay package drawn by a graph node over the world image, on services that must never be called: a
    // node that never produced a frame acquired nothing, so its teardown reaches none of them.
    private static ShaderPipelineRenderNode Overlay(IGpuDeviceContext device, RefusingGpuDevice unused) {
        var glyphs = OverlayGlyphSdfPack.TryCreate(new FontAtlas(
            FontAtlasKind.Mtsdf,
            "test://fixed-grid",
            32,
            8,
            4,
            2,
            default,
            [new(
                '!',
                1,
                null,
                new(
                    Bottom: 2,
                    Left: 0,
                    Right: 2,
                    Top: 0
                )
            )],
            [],
            new FontAtlasImageData(
                Enumerable.Repeat(
                    count: 32,
                    element: ((byte)127)
                ).ToArray(),
                2,
                4
            )
        ))!;

        var overlay = new OverlayPackage(
            capacity: new OverlayCapacity(
                BindingBarMaxBanks: 1,
                BindingBarMaxModifiers: 1,
                BindingBarMaxSlotsPerBank: 1,
                HudElementsPerPanel: 1,
                HudElementsPerSeatPanel: 1,
                HudPanels: 1,
                HudSeatPanelsPerSeat: 1,
                MarkerMaxChipsPerSeat: 1,
                Seats: 1,
                WheelMaxRings: 1,
                WheelMaxSectorsPerRing: 1
            ),
            fragmentBytecode: new byte[] { 1 },
            frameSources: new UnusedFrameSources(gpu: unused),
            glyphs: glyphs,
            sources: new UnifiedOverlaySources(
                BindingBar: null,
                Console: null,
                FeedTick: null,
                Toast: null
            ),
            vertexBytecode: new byte[] { 1 }
        );
        var packages = new RenderGraphPackageRecorders();

        packages.Register(
            factory: overlay,
            package: RenderGraphPackageCatalog.Overlay
        );

        var node = new ShaderPipelineRenderNode(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: device,
            height: 32U,
            hostsOnDirectX: false,
            name: "root",
            outputLayout: GpuImageLayout.ShaderReadOnly,
            packages: packages,
            width: 32U
        );

        node.Swap(pipeline: new CompiledShaderPipeline(
            plan: new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: new RenderGraphDefinition(
                Name: "root",
                Outputs: ["composed"],
                Packages: [new RenderGraphPackagePass(
                    Inputs: ["world"],
                    Name: "overlay",
                    Outputs: ["composed"],
                    Package: RenderGraphPackageCatalog.Overlay
                )],
                Resources: [
                    new ShaderPipelineResource(
                        Dimensions: ShaderPipelineDimensions.Relative(),
                        Format: "R8G8B8A8Unorm",
                        Initialization: ShaderPipelineInitialization.External,
                        Name: "world"
                    ),
                    new ShaderPipelineResource(
                        Dimensions: ShaderPipelineDimensions.Relative(),
                        Format: "R8G8B8A8Unorm",
                        Name: "composed"
                    ),
                ],
                Schema: RenderGraphSchemas.Graph
            )).Pipeline,
            shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
        ));

        return node;
    }

    [Fact]
    public async Task ADeviceThatNeverCameUpSurfacesUnmaskedThroughTheRealOverlayTeardown() {
        var unused = new RefusingGpuDevice();
        var device = new WindowedHostFixture.NeverInitializedDeviceContext(services: unused.Services);
        var presenter = new WindowedHostFixture.FakePresenter(failure: NoDevice());
        var host = WindowedHostFixture.Build(
            device: device,
            presenter: presenter,
            root: Overlay(
                device: device,
                unused: unused
            )
        );

        var (exit, error) = await RunAsync(host: host);

        Assert.Equal(
            actual: error,
            expected: Unsupported
        );
        Assert.Equal(
            actual: exit,
            expected: 2
        );
        Assert.Equal(
            expected: 0,
            actual: unused.Reaches
        );
        Assert.Equal(
            expected: 1,
            actual: presenter.DisposeCalls
        );
    }
    [Fact]
    public async Task ATeardownStepThatThrowsAfterTheFaultNeitherReplacesItNorSkipsTheSteps() {
        var root = new WindowedHostFixture.FakeRenderNode(failure: new InvalidOperationException(message: "The renderer must be initialized before its device is used."));
        var presenter = new WindowedHostFixture.FakePresenter(failure: NoDevice());
        var host = WindowedHostFixture.Build(
            device: new WindowedHostFixture.NeverInitializedDeviceContext(),
            presenter: presenter,
            root: root
        );

        var (exit, error) = await RunAsync(host: host);

        Assert.Equal(
            actual: error,
            expected: Unsupported
        );
        Assert.Equal(
            actual: exit,
            expected: 2
        );
        Assert.Equal(
            expected: 1,
            actual: root.DisposeCalls
        );
        Assert.Equal(
            expected: 1,
            actual: presenter.DisposeCalls
        );
    }
    [Fact]
    public void AnOverlayThatNeverProducedReleasesNothingAndNeverAsksForTheDevice() {
        var unused = new RefusingGpuDevice();
        var overlay = Overlay(
            device: new WindowedHostFixture.NeverInitializedDeviceContext(services: unused.Services),
            unused: unused
        );

        overlay.Dispose();
        overlay.OnDeviceLost();

        Assert.Equal(
            expected: 0,
            actual: unused.Reaches
        );
    }
    [Fact]
    public void TeardownRunsEveryStepAndKeepsTheFirstFailure() {
        var fault = new InvalidOperationException(message: "the pump's fault");
        var ran = new List<string>();

        void Fails(string step) {
            ran.Add(item: step);

            throw new InvalidOperationException(message: $"{step} teardown failure");
        }

        LauncherHostRun.RunTeardown(
            fault,
            NullLogger(),
            ("first", () => Fails(step: "first")),
            ("second", () => ran.Add(item: "second"))
        );

        Assert.Equal(
            actual: ran,
            expected: ["first", "second"]
        );

        ran.Clear();

        var thrown = Assert.Throws<InvalidOperationException>(testCode: () => LauncherHostRun.RunTeardown(
            null,
            NullLogger(),
            ("first", () => Fails(step: "first")),
            ("second", () => Fails(step: "second"))
        ));

        Assert.Equal(
            expected: "first teardown failure",
            actual: thrown.Message
        );
        Assert.Equal(
            actual: ran,
            expected: ["first", "second"]
        );
    }
    [Fact]
    public async Task AHostedServiceWhoseConstructionFindsNoDeviceExitsTwo() {
        var builder = Host.CreateApplicationBuilder(settings: new HostApplicationBuilderSettings {
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddHostedService<NoDeviceService>(implementationFactory: static _ => throw NoDevice());

        var (exit, error) = await RunAsync(host: builder.Build());

        Assert.Equal(
            actual: error,
            expected: Unsupported
        );
        Assert.Equal(
            actual: exit,
            expected: 2
        );
    }

    private static ILogger NullLogger() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private sealed class NoDeviceService : BackgroundService {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
    // The overlay's frame sources over the refusing device: acquiring one from a node that never produced a frame is a
    // failure, counted with the device's own.
    private sealed class UnusedFrameSources(RefusingGpuDevice gpu) : IOverlayFrameSources {
        public bool TryAcquire(int key, out Puck.Hosting.GpuImageLease lease) => throw gpu.Reach(member: "IOverlayFrameSources.TryAcquire");
    }
}
