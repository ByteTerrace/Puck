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
/// output's place, a frame with a drawn cursor publishes the instance's own output, the bound frame-slot leases move
/// into the frame's lease list, and a steady drawn frame allocates nothing.
/// </summary>
public sealed class OverlayPackageLawTests {
    private const uint Extent = 64;
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
        slots.RetirePending();
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

        public Rig() {
            var gpu = new FakeGpuDevice(reportVersion: 0);
            var package = new OverlayPackage(
                capacity: new OverlayCapacity(
                    BindingBarMaxBanks: 0,
                    BindingBarMaxModifiers: 0,
                    BindingBarMaxSlotsPerBank: 0,
                    HudElementsPerPanel: 0,
                    HudElementsPerSeatPanel: 0,
                    HudPanels: 0,
                    HudSeatPanelsPerSeat: 0,
                    MarkerMaxChipsPerSeat: 0,
                    Seats: 1,
                    WheelMaxRings: 0,
                    WheelMaxSectorsPerRing: 0
                ),
                fragmentBytecode: new byte[] { 1 },
                frameSources: new LeasingFrameSources(retire: static () => { }),
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

            packages.Register(
                factory: package,
                package: RenderGraphPackageCatalog.Overlay
            );
            Node = new ShaderPipelineRenderNode(
                deviceContext: gpu,
                height: Extent,
                hostsOnDirectX: false,
                name: "root",
                outputLayout: GpuImageLayout.ShaderReadOnly,
                packages: packages,
                width: Extent
            );
            Node.Swap(pipeline: new CompiledShaderPipeline(
                plan: new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: Graph()).Pipeline,
                shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
            ));
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

        public ShaderPipelineRenderNode Node { get; }

        public void Dispose() => Node.Dispose();
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
    // Hands out a lease for every key, counting each retirement.
    private sealed class LeasingFrameSources(Action retire) : IOverlayFrameSources {
        public bool TryAcquire(int key, out GpuImageLease lease) {
            lease = new GpuImageLease(
                ImageViewHandle: (0x6000 + key),
                Release: _ => retire()
            );

            return true;
        }
    }
}
