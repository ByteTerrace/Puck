using System.Runtime.CompilerServices;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Overlays;
using Puck.Shaders;
using Puck.Testing;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws of the <c>overlay</c> package (<see cref="OverlayPackage"/>) run as a graph's package pass over
/// <see cref="FakeGpuDevice"/>: a frame with nothing visible draws nothing and the instance publishes its input in the
/// output's place, a frame with a drawn cursor publishes the instance's own output, a drawn frame is handed its input
/// shader-readable and its target in render-target layout by the node's planned barriers and records none of its own,
/// the bound frame-slot leases move into the frame's lease list, a steady drawn frame allocates nothing, and the
/// compiled shader and its generated include read the interface the catalog declares for the package.
/// </summary>
public sealed partial class OverlayPackageLawTests {
    private const uint Extent = 64;
    private const uint InFlight = 3;
    private const nint WorldImage = 0x5100;

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern OverlayGlyphSdfPack CreateGlyphs(int atlasCellWidth, int atlasCellHeight, float distanceRange, uint[] packedSdf, int glyphCount);
    private static RenderGraphDefinition Graph() => new(
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
    );
    private static Surface ProduceUntilPublished(ShaderPipelineRenderNode node) {
        var surface = default(Surface);

        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => !(surface = node.ProduceFrame(context: default)).IsEmpty,
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The overlay instance never published a frame."
        );

        return surface;
    }

    [Fact]
    public void AFrameWithNothingVisiblePublishesTheInputAndADrawnFramePublishesTheOutput() {
        using var rig = new Rig();
        var hidden = ProduceUntilPublished(node: rig.Node);

        Assert.Equal(
            expected: WorldImage,
            actual: hidden.ImageHandle
        );

        rig.ShowCursor();

        var drawn = rig.Node.ProduceFrame(context: default);

        Assert.False(condition: drawn.IsEmpty);
        Assert.NotEqual(
            expected: WorldImage,
            actual: drawn.ImageHandle
        );
    }
    [Fact]
    public void ADrawnOverlayIsHandedItsPlannedLayoutsAndRecordsNoBarrierOfItsOwn() {
        using var rig = new Rig();

        _ = ProduceUntilPublished(node: rig.Node);
        rig.ShowCursor();

        for (var frame = 0; (frame < 4); frame++) {
            _ = rig.Node.ProduceFrame(context: default);
        }

        Assert.Equal(
            expected: (0, (GpuImageLayout.ShaderReadOnly, GpuImageLayout.RenderTarget), RenderGraphPackageOutcome.Drew),
            actual: (rig.Observed.PackageBarriers, rig.Observed.Layouts, rig.Observed.Outcome)
        );
    }
    [Fact]
    public void TheOverlayShaderReadsTheInterfaceItsPackageDeclares() {
        var layout = ShaderPipelineParameterLayout.ForPackage(
            config: null,
            members: RenderGraphPackageCatalog.OverlayMembers,
            package: RenderGraphPackageCatalog.Overlay
        );

        // The include is the generated declarations of the interface the catalog declares, byte for byte.
        Assert.Equal(
            actual: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: $"src/Puck.Overlays/Assets/Shaders/{ShaderFrameInterface.IncludeFileName(interfaceName: layout.Interface.Name)}")),
            expected: ShaderInterfaceHlsl.Generate(shaderInterface: layout.Interface)
        );
        // The compiled fragment shader reads every block and binding where that interface places them.
        Assert.Null(@object: layout.Layout.Mismatch(reflected: SpirvInterfaceReader.Read(module: File.ReadAllBytes(path: Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Assets",
            path3: "Shaders",
            path4: "overlay-unified.frag.spv"
        )))));
    }
    [Fact]
    public void BoundFrameSlotLeasesMoveIntoTheFramesLeaseList() {
        var retired = 0;
        var slots = new OverlayFrameSlots(sources: new LeasingFrameSources(retire: () => retired++));
        var frame = new LeaseRetireList();

        slots.BeginFrame();
        Assert.Equal(
            expected: 0,
            actual: slots.Bind(key: 7)
        );
        slots.MoveTo(destination: frame);
        Assert.Equal(
            expected: (1, 0, 0),
            actual: (frame.Count, slots.BoundCount, retired)
        );
        slots.BeginFrame();
        Assert.Equal(
            actual: retired,
            expected: 0
        );
        frame.RetireAll();
        Assert.Equal(
            actual: retired,
            expected: 1
        );
    }
    /// <summary>A lease the overlay bound for a visible <c>Frame</c> element is released exactly once when the recording
    /// that bound it fails before handing it to its frame, as a device lost mid-recording does: the recorder's disposal
    /// retires what its table still holds.</summary>
    [Fact]
    public void ALeaseARecordingBoundBeforeItFailedIsReleasedOnceWhenTheOverlayIsDisposed() {
        var released = 0;
        var hud = new HudStore();

        hud.Publish(frame: new OverlayHudFrame(Panels: new[] {
            new OverlayHudPanel(
                Band: OverlayHudBand.Over,
                Elements: new[] {
                    new OverlayHudElement(
                        Binding: null,
                        FrameSource: 7,
                        Kind: OverlayHudElementKind.Frame,
                        Rect: new OverlayHudRect(
                            Height: 32f,
                            Width: 32f,
                            X: 0f,
                            Y: 0f
                        ),
                        Role: default,
                        Text: null
                    ),
                },
                Id: "face",
                Rect: new OverlayHudRect(
                    Height: 32f,
                    Width: 32f,
                    X: 0f,
                    Y: 0f
                ),
                Style: default
            ),
        }));

        var sources = new LeasingFrameSources(retire: () => released++);
        var rig = new Rig(
            frameSources: sources,
            hud: hud
        );

        try {
            _ = ProduceUntilPublished(node: rig.Node);
            rig.Gpu.OnCall = static key => {
                if (key == "IGpuBindings.WriteSampledImage") {
                    throw new DeviceLostException(message: "The law lost the device while the overlay wrote its images.");
                }
            };

            _ = Assert.Throws<DeviceLostException>(testCode: () => rig.Node.ProduceFrame(context: default));
            rig.Gpu.OnCall = null;
        } finally {
            rig.Dispose();
        }

        // Every lease a drawn frame handed on retired with its frame, and the one the failed recording still held was
        // released with its recorder: each exactly once.
        Assert.True(condition: (sources.Acquired > 1));
        Assert.Equal(
            actual: released,
            expected: sources.Acquired
        );
    }
    [Fact]
    public void ASteadyDrawnOverlayFrameAllocatesNothing() {
        using var rig = new Rig();

        _ = ProduceUntilPublished(node: rig.Node);
        rig.ShowCursor();

        for (var warm = 0; (warm < 4); warm++) {
            _ = rig.Node.ProduceFrame(context: default);
        }

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => {
                for (var frame = 0; (frame < 16); frame++) {
                    _ = rig.Node.ProduceFrame(context: default);
                }
            }),
            expected: 0L
        );
    }

    private sealed class Rig : IDisposable {
        private readonly CursorStore m_cursor = new();

        // hud, when given, is the overlay's HUD, drawn with room for one panel of one element, and frameSources the host
        // seam its Frame elements acquire leases through.
        public Rig(GpuCreationFaults? faults = null, bool trackObjects = false, HudStore? hud = null, IOverlayFrameSources? frameSources = null) {
            var gpu = new FakeGpuDevice(
                countCalls: true,
                reportVersion: 0,
                trackObjects: trackObjects
            );

            Gpu = gpu;
            var package = new OverlayPackage(
                capacity: new OverlayCapacity(
                    BindingBarMaxBanks: 0,
                    BindingBarMaxModifiers: 0,
                    BindingBarMaxSlotsPerBank: 0,
                    HudElementsPerPanel: ((hud is null) ? 0 : 1),
                    HudElementsPerSeatPanel: 0,
                    HudPanels: ((hud is null) ? 0 : 1),
                    HudSeatPanelsPerSeat: 0,
                    MarkerMaxChipsPerSeat: 0,
                    Seats: 1,
                    WheelMaxRings: 0,
                    WheelMaxSectorsPerRing: 0
                ),
                fragmentBytecode: new byte[] { 1 },
                frameSources: (frameSources ?? new LeasingFrameSources(retire: static () => { })),
                glyphs: CreateGlyphs(
                    atlasCellHeight: 1,
                    atlasCellWidth: 1,
                    distanceRange: 1f,
                    glyphCount: 1,
                    packedSdf: [0u]
                ),
                sources: new UnifiedOverlaySources(
                    BindingBar: null,
                    Console: null,
                    Cursor: m_cursor,
                    FeedTick: null,
                    Hud: hud,
                    HudBindings: ((hud is null) ? null : new NoHudBindings()),
                    Toast: null
                ),
                theme: OverlayThemeValues.Zero with {
                    Chrome = OverlayThemeValues.Zero.Chrome with {
                        CursorAlpha = 1f,
                        CursorDotMaxHalf = 4f,
                        CursorDotRatio = 0.2f,
                    },
                },
                vertexBytecode: new byte[] { 1 }
            );
            var packages = new RenderGraphPackageRecorders();

            Observed = new ObservedPackageFactory(
                barriers: () => ((gpu.Count(key: "IGpuRecorder.TransitionImageLayout") + gpu.Count(key: "IGpuRecorder.MemoryBarrier")) + gpu.Count(key: "IGpuRecorder.TransitionBuffer")),
                inner: package
            );
            packages.Register(
                factory: Observed,
                package: RenderGraphPackageCatalog.Overlay
            );
            Node = new ShaderPipelineRenderNode(
                deviceContext: ((faults is null)
                    ? gpu
                    : new FaultingDevice(
                        faults: faults,
                        gpu: gpu
                    )),
                height: Extent,
                hostsOnDirectX: false,
                inFlightFrames: InFlight,
                name: "root",
                outputLayout: GpuImageLayout.ShaderReadOnly,
                packages: packages,
                width: Extent
            );
            Swap();
            Node.BindImage(
                image: new ShaderPipelineExternalImage(
                    Format: GpuPixelFormat.R8G8B8A8Unorm,
                    Height: Extent,
                    ImageHandle: WorldImage,
                    ImageViewHandle: (WorldImage + 1),
                    Layout: GpuImageLayout.ShaderReadOnly,
                    Width: Extent
                ),
                name: "world"
            );
        }

        public FakeGpuDevice Gpu { get; }
        public ShaderPipelineRenderNode Node { get; }
        // What the overlay's recorder was handed and recorded itself.
        public ObservedPackageFactory Observed { get; }

        public void Dispose() => Node.Dispose();
        // Swaps the overlay graph in as the node's next candidate.
        public void Swap() => Node.Swap(pipeline: new CompiledShaderPipeline(
            plan: new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: Graph()).Pipeline,
            shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
        ));
        public void ShowCursor() => m_cursor.Publish(frame: new OverlayCursorFrame(Seats: new[] {
            new OverlayCursorSeat(
                Hover: false,
                HoverLabel: "",
                Role: default,
                SizePx: 8f,
                Viewport: new NormalizedRect(
                    Height: 1f,
                    Width: 1f,
                    X: 0f,
                    Y: 0f
                ),
                X: 16f,
                Y: 16f
            ),
        }));
    }
    // A HUD binding resolver that resolves nothing, for a HUD whose elements bind no value.
    private sealed class NoHudBindings : IHudBindingResolver {
        public bool TryResolve(string binding, out float fraction, out string text) {
            fraction = 0f;
            text = string.Empty;

            return false;
        }
    }
    // Hands out a lease for every key, counting each acquisition and each retirement.
    private sealed class LeasingFrameSources(Action retire) : IOverlayFrameSources {
        public int Acquired { get; private set; }

        public bool TryAcquire(int key, out GpuImageLease lease) {
            Acquired++;
            lease = new GpuImageLease(
                ImageViewHandle: (0x6000 + key),
                Release: _ => retire()
            );

            return true;
        }
    }
}
