using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// The <c>sdf.world</c> package's fragment (<see cref="SdfWorldPackage.Fragment"/>), spliced into a view's graph by the
/// graph compiler, plans exactly as the engine records its dispatches by hand. The planner must order the spliced passes
/// as <see cref="SdfWorldEngine.PassLabels"/> less the upload, whose region copies touch no frame buffer and are the
/// regions' own, and plan, between passes of the frame, exactly the buffer transitions
/// <see cref="SdfFrameBufferPlan.Edges"/> derives, less the brick pool's, which the views only read and which
/// <c>sdf.bricks</c> publishes. Every scratch buffer is transient, one allocation shared by every frame slot, and at every
/// capacity the planner's size is the engine's allocation, <see cref="SdfWorldEngine.FrameBufferBytes"/>.
/// </summary>
public sealed class SdfPassPlanLawTests {
    private const string Upload = "upload";
    // The name a view's graph gives the pass running the package.
    private const string Sdf = "sdf";

    // The engine buffer each fragment buffer storage is.
    private static SdfFrameBuffer BufferOf(string storage) => storage switch {
        $"{Sdf}${SdfWorldPackage.Parts.InstanceMasks}" => SdfFrameBuffer.InstanceMasks,
        $"{Sdf}${SdfWorldPackage.Parts.Tiles}" => SdfFrameBuffer.Tiles,
        $"{Sdf}${SdfWorldPackage.Parts.Arguments}" => SdfFrameBuffer.ViewsArgs,
        $"{Sdf}${SdfWorldPackage.Parts.CullBounds}" => SdfFrameBuffer.CullBounds,
        $"{Sdf}${SdfWorldPackage.Parts.Visibility}" => SdfFrameBuffer.VisibilityRecords,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(storage), actualValue: storage, message: "Not a fragment buffer."),
    };
    // The engine's ledger label for each dispatch. Brick work and the upload are null: the brick pool is sdf.bricks', and
    // the region copies transition what they copy themselves.
    private static string? LabelOf(SdfFramePass pass) => pass switch {
        SdfFramePass.BrickUpload or SdfFramePass.BrickBake or SdfFramePass.Upload => null,
        SdfFramePass.Sky => "sky",
        SdfFramePass.Mask => "mask",
        SdfFramePass.Beam => "beam",
        SdfFramePass.CullArgs => "cull-args",
        SdfFramePass.Mesh => "mesh",
        SdfFramePass.Primary => "primary",
        SdfFramePass.Surface => "surface",
        SdfFramePass.Ambient => "ambient",
        SdfFramePass.Views => "views",
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(pass)),
    };
    // The counts a host resolves for a view of this capacity, each derived as the engine derives it.
    private static ShaderPipelineStorageCounts CountsOf(SdfFrameCapacity capacity) => new(
        Height: capacity.Height,
        Width: capacity.Width
    ) {
        BrickPoolVoxels = ((ulong)capacity.BrickPoolVoxels),
        InstanceMaskWords = ((ulong)SdfProgram.InstanceMaskStorageWordCountFor(instanceCount: capacity.Instances)),
        Instances = ((ulong)capacity.Instances),
        Tiles = capacity.Tiles,
        Viewports = capacity.Viewports,
    };
    private static SdfFrameCapacity Capacity(uint width, uint height, uint viewports, int instances) => new(
        BrickPoolVoxels: SdfWorldEngine.DefaultBrickPoolVoxelCapacity,
        Height: height,
        Instances: instances,
        Viewports: viewports,
        Width: width
    );

    // The dispatches of one rendered view, in recording order: every labelled pass.
    private static SdfFramePass[] Frame { get; } = [.. Enum.GetValues<SdfFramePass>().Where(predicate: static pass => (LabelOf(pass: pass) is not null))];
    // A view's graph: the one pass running the package, publishing its color.
    private static RenderGraphPlan Plan { get; } = new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: new RenderGraphDefinition(
        Name: "view",
        Outputs: [SdfWorldPackage.Color],
        Packages: [new RenderGraphPackagePass(
            Name: Sdf,
            Outputs: [SdfWorldPackage.Color],
            Package: RenderGraphPackageCatalog.SdfWorld
        )],
        Resources: [new ShaderPipelineResource(
            Dimensions: ShaderPipelineDimensions.Relative(),
            Format: nameof(GpuPixelFormat.R8G8B8A8Unorm),
            Name: SdfWorldPackage.Color
        )],
        Schema: RenderGraphSchemas.Graph
    ));

    private static IEnumerable<ShaderPipelinePlannedStorage> Buffers => Plan.Pipeline.Storages.Where(predicate: static storage => (storage.Declaration.Kind == ShaderPipelineResourceKind.Buffer));

    [Fact]
    public void ThePlannedOrderIsTheEnginesPassOrderLessTheUpload() {
        var labels = SdfWorldEngine.PassLabels.ToArray().Where(predicate: static label => (label != Upload)).ToArray();

        Assert.Equal(
            actual: Frame.Select(selector: static pass => LabelOf(pass: pass)!).Distinct(),
            expected: labels
        );
        Assert.Equal(
            actual: Plan.Pipeline.PassOrder,
            expected: labels.Select(selector: static label => RenderGraphPackageFragment.Spliced(
                name: label,
                pass: Sdf
            ))
        );
        Assert.All(
            action: static step => {
                Assert.Equal(expected: RenderGraphPackageCatalog.SdfWorld, actual: step.Package?.Id);
                Assert.Equal(expected: RenderGraphPackageFragment.Spliced(name: step.Planned.Package!.Part!, pass: Sdf), actual: step.Name);
            },
            collection: Plan.Steps
        );
    }
    [Fact]
    public void ThePlannedBufferBarriersBetweenPassesAreExactlyTheEnginesEdges() {
        var planned = new List<(SdfFrameBuffer Buffer, string Producer, string Consumer, GpuAccess SourceAccess, GpuAccess DestinationAccess, GpuStage SourceStage, GpuStage DestinationStage)>();
        var passes = Plan.Pipeline.Passes;

        foreach (var pass in passes) {
            foreach (var access in pass.Accesses) {
                var storage = Plan.Pipeline.Storages[access.Storage];

                if (
                    (storage.Declaration.Kind != ShaderPipelineResourceKind.Buffer) ||
                    (access.PriorKind != ShaderPipelinePriorKind.Pass) ||
                    (access.Barrier.Kind == ShaderPipelineBarrierKind.None)
                ) {
                    continue;
                }

                Assert.Equal(
                    actual: access.Barrier.Kind,
                    expected: ShaderPipelineBarrierKind.Buffer
                );
                planned.Add(item: (BufferOf(storage: storage.Name), passes[access.PriorPass].Package!.Part!, pass.Package!.Part!, access.Barrier.SourceAccess, access.Barrier.DestinationAccess, access.Barrier.SourceStage, access.Barrier.DestinationStage));
            }
        }

        var expected = SdfFrameBufferPlan.Edges(passes: Frame).Where(predicate: static edge => (edge.Buffer != SdfFrameBuffer.BrickPool)).Select(selector: static edge => (edge.Buffer, LabelOf(pass: edge.Producer)!, LabelOf(pass: edge.Consumer)!, edge.SourceAccess, edge.DestinationAccess, edge.SourceStage, edge.DestinationStage));

        Assert.Equal(
            actual: planned,
            expected: expected
        );
    }
    [InlineData(64U, 64U, 1U, 1)]
    [InlineData(100U, 37U, 2U, 33)]
    [InlineData(17U, 300U, 3U, 65)]
    [InlineData(1920U, 1080U, 4U, 1000)]
    [InlineData(2560U, 1440U, 6U, 4097)]
    [Theory]
    public void ThePlannerSizesEveryBufferAsTheEngineAllocatesIt(uint width, uint height, uint viewports, int instances) {
        var capacity = Capacity(
            height: height,
            instances: instances,
            viewports: viewports,
            width: width
        );
        var counts = CountsOf(capacity: capacity);

        Assert.Equal(
            actual: Buffers.Select(selector: static storage => BufferOf(storage: storage.Name)).Order(),
            expected: Enum.GetValues<SdfFrameBuffer>().Where(predicate: static buffer => (buffer != SdfFrameBuffer.BrickPool)).Order()
        );
        Assert.All(
            action: storage => Assert.Equal(
                actual: storage.Declaration.ResolveSizeBytes(counts: counts),
                expected: SdfWorldEngine.FrameBufferBytes(
                    buffer: BufferOf(storage: storage.Name),
                    capacity: capacity
                )
            ),
            collection: Buffers
        );
        Assert.Equal(
            actual: new ShaderPipelineResource(
                Count: RenderGraphPackageCatalog.BrickPool.Count,
                Kind: ShaderPipelineResourceKind.Buffer,
                Name: "bricks",
                StrideBytes: RenderGraphPackageCatalog.BrickPool.StrideBytes
            ).ResolveSizeBytes(counts: counts),
            expected: SdfWorldEngine.FrameBufferBytes(
                buffer: SdfFrameBuffer.BrickPool,
                capacity: capacity
            )
        );
    }
    [Fact]
    public void EveryScratchStorageIsTransientAndTheColorIsPublishedPerSlot() {
        var transient = Plan.Pipeline.Storages.Where(predicate: static storage => storage.Declaration.Transient).Select(selector: static storage => storage.Name).Order(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: transient,
            expected: new[] {
                SdfWorldPackage.Parts.Arguments,
                SdfWorldPackage.Parts.CullBounds,
                SdfWorldPackage.Parts.InstanceMasks,
                SdfWorldPackage.Parts.MeshDepth,
                SdfWorldPackage.Parts.MeshTarget,
                SdfWorldPackage.Parts.Tiles,
                SdfWorldPackage.Parts.Visibility,
            }.Select(selector: static part => RenderGraphPackageFragment.Spliced(name: part, pass: Sdf)).Order(comparer: StringComparer.Ordinal)
        );

        var color = Plan.Pipeline.Storages.Single(predicate: static storage => storage.Versions.Contains(value: SdfWorldPackage.Color));

        Assert.False(condition: color.Declaration.Transient);
        Assert.Equal(
            actual: color.Versions,
            expected: [RenderGraphPackageFragment.Spliced(name: SdfWorldPackage.Parts.SkyImage, pass: Sdf), SdfWorldPackage.Color]
        );
    }
    [Fact]
    public void EachBuffersFirstUseInTheFrameStartsFromTheFrameBefore() {
        // The engine's top-of-frame barrier orders each buffer's first use of a frame after the frame before; the planner
        // gives exactly that use a cross-frame prior, whose barrier orders the one transient allocation across frames, and
        // every later use a pass prior.
        foreach (var storage in Buffers) {
            var accesses = Plan.Pipeline.Passes.SelectMany(selector: static pass => pass.Accesses).Where(predicate: access => (access.Storage == storage.Index)).ToArray();

            Assert.Equal(
                actual: accesses[0].PriorKind,
                expected: ShaderPipelinePriorKind.CrossFrame
            );
            Assert.NotEqual(
                actual: accesses[0].Barrier.Kind,
                expected: ShaderPipelineBarrierKind.None
            );
            Assert.All(
                action: static access => Assert.Equal(
                    actual: access.PriorKind,
                    expected: ShaderPipelinePriorKind.Pass
                ),
                collection: accesses[1..]
            );
        }
    }
    [Fact]
    public void TheHitPassesAreDispatchedIndirectlyFromTheCullArguments() {
        var arguments = RenderGraphPackageFragment.Spliced(
            name: SdfWorldPackage.Parts.Arguments,
            pass: Sdf
        );

        foreach (var part in new[] { SdfWorldPackage.Parts.Primary, SdfWorldPackage.Parts.Surface, SdfWorldPackage.Parts.Ambient, SdfWorldPackage.Parts.Views }) {
            var pass = Plan.Pipeline.Passes.Single(predicate: pass => (pass.Package!.Part == part));

            Assert.Equal(
                actual: pass.Package!.Dispatch,
                expected: ShaderPipelineDispatch.Indirect(arguments: arguments)
            );
            Assert.Equal(
                actual: pass.Accesses[0].Version,
                expected: arguments
            );
            Assert.Equal(
                actual: pass.Accesses[0].Use.Stage,
                expected: GpuStage.DrawIndirect
            );
        }
    }
    [Fact]
    public void TheMeshPassDrawsItsTargetAndADepthClearedAsItsRenderPassClears() {
        var mesh = Plan.Pipeline.Passes.Single(predicate: static pass => (pass.Package!.Part == SdfWorldPackage.Parts.Mesh));
        var depth = Plan.Pipeline.Storages[mesh.Accesses[1].Storage].Declaration;

        Assert.Equal(
            actual: (depth.Kind, depth.Format, depth.ClearDepth),
            expected: (ShaderPipelineResourceKind.Depth, SdfWorldPackage.MeshDepthAttachment.Format.ToString(), ((float?)SdfWorldPackage.MeshDepthAttachment.ClearDepth))
        );
        Assert.Equal(
            actual: mesh.Accesses.Select(selector: static access => access.Use.Layout),
            expected: [GpuImageLayout.RenderTarget, GpuImageLayout.DepthAttachment]
        );
    }
}
