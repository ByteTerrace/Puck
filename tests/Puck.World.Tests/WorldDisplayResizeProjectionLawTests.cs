using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the render graph's root is shown at its own extent, so no frame it presents carries a camera
/// projection composed for another. The presenter an offscreen boot composes is the frame source of the world's
/// residency, the real <c>sdf.world</c> package renders its one view over the upload model, and that view is the root,
/// as a lone whole-display view without a tonemap is. A display resize composes the camera for the new extent on the next
/// frame while the root's graph rebuilds at it beside the installed one; until it installs, the root presents its last
/// image, and a capture of it names the extent it waits for. A render-scale change, which rebuilds the view at an
/// unchanged extent, and a layout transition's dip, which moves only the grid inside the ceiling, keep the projection on
/// the root's extent too. Every frame the root renders is judged: the aspect of the camera it rendered with against the
/// extent its installed graph rendered at.
/// </summary>
public sealed class WorldDisplayResizeProjectionLawTests : IDisposable {
    private const string Instance = "world";
    private const ulong StepTicks = 1680;
    private const string World = "tests/Puck.Counters/counters.puck";

    private readonly TemporaryDirectory m_stateDirectory = new(prefix: "puck-resize-projection-");

    public void Dispose() => m_stateDirectory.Dispose();
    [Fact]
    public void ADisplayResizeNeverPresentsAProjectionComposedForAnotherExtent() {
        using var rig = new Rig(height: 36, stateDirectory: m_stateDirectory, width: 64);

        rig.SettleAt(height: 36, width: 64);
        // Wide to square and back: each direction composes a camera whose aspect the installed graph does not render.
        rig.SettleAt(height: 36, width: 36);
        rig.SettleAt(height: 36, width: 64);
        Assert.True(condition: (rig.HeldFrames > 0), userMessage: "no frame was held while a resize built, so nothing was judged");
    }
    [Fact]
    public void ARenderScaleChangeAndATransitionDipKeepTheProjectionOnTheRootsExtent() {
        using var rig = new Rig(height: 36, stateDirectory: m_stateDirectory, transition: true, width: 64);
        var composition = rig.Services.GetRequiredService<WorldCompositionState>();
        var settings = rig.Services.GetRequiredService<WorldRenderSettings>();

        rig.SettleAt(height: 36, width: 64);
        settings.RenderScale = 0.5f;
        rig.SettleAt(height: 36, width: 64);
        composition.ActiveLayout = "eased";
        rig.Frames(count: 12);
        composition.ActiveLayout = "settled";
        rig.Frames(count: 12);
        settings.RenderScale = 1f;
        rig.SettleAt(height: 36, width: 64);
    }

    // One sdf.world instance, the root, rendering the presenter's frame over the upload model at the display extent the
    // presenter composes its cameras for.
    private sealed class Rig : IDisposable {
        private readonly FrameContext m_context;
        private readonly UploadModelGpu m_gpu = new();
        private readonly IHost m_host;
        private readonly WorldFramePresenter m_presenter;
        private readonly SdfWorldResidency m_residency;
        private readonly RenderGraphRuntime m_runtime;

        private (uint Width, uint Height) m_display;
        private long m_index;

        public Rig(TemporaryDirectory stateDirectory, uint width, uint height, bool transition = false) {
            m_host = WorldBootHarness.Compose(
                edit: (transition ? WithTransition : null),
                presentation: WorldHostPresentation.Offscreen,
                stateDirectory: stateDirectory,
                world: World
            ).Build();
            m_presenter = m_host.Services.GetRequiredService<WorldFramePresenter>();
            m_host.Services.GetRequiredService<WorldRenderSettings>().RenderScale = 1f;
            if (transition) {
                m_host.Services.GetRequiredService<WorldCompositionState>().ActiveLayout = "settled";
            }
            m_display = (width, height);
            m_presenter.ResizeDisplay(height: height, width: width);

            var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);

            m_residency = new SdfWorldResidency(
                brickPoolVoxelCapacity: 0,
                frameSource: m_presenter,
                height: height,
                kernels: SdfTestPipelines.Kernels(),
                name: Instance,
                pipelines: pipelines,
                width: width
            );

            var residency = m_residency;
            var passes = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
            var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

            packages.Register(factory: passes, package: RenderGraphPackageCatalog.SdfWorld);
            Assert.True(condition: RenderGraphInstanceSet.TryCreate(
                instances: [new RenderGraphInstance(
                    ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                    Name: Instance,
                    Passes: SdfWorldPackage.Fragment.Passes.Count,
                    Reads: [],
                    Refresh: RenderGraphRefresh.EveryFrame
                )],
                refusal: out var setRefusal,
                set: out var set
            ), userMessage: setRefusal?.Message);
            Assert.True(condition: RenderGraphRuntime.TryCreate(
                deviceContext: m_gpu,
                graphs: new RenderGraphRuntimeGraph?[1],
                hostsOnDirectX: false,
                packages: packages,
                pipelines: pipelines.Pipelines,
                refusal: out var refusal,
                root: Instance,
                runtime: out var runtime,
                set: set
            ), userMessage: refusal?.Message);
            m_runtime = runtime!;
            m_context = new FrameContext(
                AccumulatorTicks: 0UL,
                DeltaTicks: StepTicks,
                ElapsedTicks: 0UL,
                FrameDeltaTicks: StepTicks,
                Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = m_gpu }),
                StepTicks: StepTicks,
                TargetHeight: height,
                TargetWidth: width
            );
        }

        // How many frames the root held its last image while its requested extent built.
        public int HeldFrames { get; private set; }
        public IServiceProvider Services => m_host.Services;

        private ShaderPipelineRenderNode Root => m_runtime.Node(instance: m_runtime.Instances.IndexOf(name: Instance));

        public void Dispose() {
            m_runtime.Dispose();
            m_residency.Dispose();
            m_host.Dispose();
        }
        // Resizes the display and produces frames until the root has rendered at the new extent, judging every frame.
        public void SettleAt(uint width, uint height) {
            var renderedAt = false;

            m_display = (width, height);
            m_presenter.ResizeDisplay(height: height, width: width);
            TestLiveness.Until(
                reason: () => $"the root never rendered at {width}x{height}: {m_runtime.UnservedCaptureReason}",
                step: () => {
                    renderedAt |= (Produce() && (Root.Extent == (width, height)));

                    return (renderedAt && !Root.IsBuildingCandidate);
                }
            );
        }
        // Produces frames at the current display extent, judging each.
        public void Frames(int count) {
            for (var frame = 0; (frame < count); frame++) {
                _ = Produce();
            }
        }

        // Produces one frame and judges it, returning whether the root rendered it.
        private bool Produce() {
            var root = Root;
            var submitted = root.FrameCounter;
            var frame = new RenderGraphFrame(
                DisplayHeight: ((int)m_display.Height),
                DisplayHertz: 60,
                DisplayWidth: ((int)m_display.Width),
                Footprints: [],
                Index: m_index,
                Roots: [new RenderGraphRoot(Height: 1.0, Instance: Instance, Width: 1.0)],
                Tick: m_index++
            );

            _ = m_runtime.ProduceFrame(context: in m_context, frame: in frame);
            if (root.FrameCounter == submitted) {
                if (root.Extent != root.RequestedExtent) {
                    HeldFrames++;
                    Assert.Contains(
                        expectedSubstring: $"while {root.RequestedExtent.Width}x{root.RequestedExtent.Height} is requested",
                        actualString: m_runtime.UnservedCaptureReason
                    );
                }

                return false;
            }

            var camera = Assert.Single(collection: m_residency.Frame!.Views).Camera;

            var (width, height) = root.Extent;

            Assert.True(
                condition: (MathF.Abs(x: (camera.AspectRatio - (width / ((float)height)))) <= 1e-5f),
                userMessage: $"the root rendered at {width}x{height} with a camera composed for aspect {camera.AspectRatio}"
            );

            return true;
        }
        // The counters world's first slot in a settled layout and an eased one whose transition dips the render grid.
        private static WorldDefinition WithTransition(WorldDefinition definition) {
            var slot = definition.Views.Layouts[0].Slots[0];

            return (definition with {
                ViewsRaw = (definition.Views with {
                    Layouts = [
                        new WorldViewLayout(Name: "settled", Slots: [slot]),
                        new WorldViewLayout(Name: "eased", Slots: [slot], TransitionRenderScale: 0.5f, TransitionSeconds: 0.2f),
                    ],
                }),
            });
        }
    }
}
