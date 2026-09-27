using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// The <c>sdf.world</c> package's fragment (<see cref="SdfWorldPackage.Fragment"/>), spliced into a view's graph by the
/// graph compiler, is the one statement of an SDF view's dispatch set: the planner orders its passes sky, mask, beam,
/// cull arguments, mesh, primary, surface, ambient and views, and plans between them exactly the buffer transitions the
/// kernels' reads and writes need, each after the pass that last wrote or read what the next writes or reads. Every
/// scratch buffer is transient, one allocation shared by every frame slot, whose first use of a frame orders it after the
/// frame before, and at every capacity the planner sizes each buffer as the kernels index it.
/// </summary>
public sealed class SdfPassPlanLawTests {
    // The name a view's graph gives the pass running the package.
    private const string Sdf = "sdf";

    // The fragment's passes in dispatch order.
    private static readonly string[] Order = [
        SdfWorldPackage.Parts.Sky,
        SdfWorldPackage.Parts.Mask,
        SdfWorldPackage.Parts.Beam,
        SdfWorldPackage.Parts.CullArgs,
        SdfWorldPackage.Parts.Mesh,
        SdfWorldPackage.Parts.Primary,
        SdfWorldPackage.Parts.Surface,
        SdfWorldPackage.Parts.Ambient,
        SdfWorldPackage.Parts.Views,
    ];
    // Every buffer transition between two passes of a frame: the buffer, the pass it orders after and the pass it orders
    // before, and the accesses and stages on either side.
    private static readonly (string Buffer, string Producer, string Consumer, GpuAccess SourceAccess, GpuAccess DestinationAccess, GpuStage SourceStage, GpuStage DestinationStage)[] Edges = [
        (SdfWorldPackage.Parts.InstanceMasks, SdfWorldPackage.Parts.Mask, SdfWorldPackage.Parts.Beam, GpuAccess.ShaderWrite, GpuAccess.ShaderRead, GpuStage.ComputeShader, GpuStage.ComputeShader),
        (SdfWorldPackage.Parts.Tiles, SdfWorldPackage.Parts.Beam, SdfWorldPackage.Parts.CullArgs, GpuAccess.ShaderWrite, GpuAccess.ShaderRead, GpuStage.ComputeShader, GpuStage.ComputeShader),
        (SdfWorldPackage.Parts.Arguments, SdfWorldPackage.Parts.CullArgs, SdfWorldPackage.Parts.Primary, GpuAccess.ShaderWrite, GpuAccess.IndirectCommandRead, GpuStage.ComputeShader, GpuStage.DrawIndirect),
        (SdfWorldPackage.Parts.CullBounds, SdfWorldPackage.Parts.CullArgs, SdfWorldPackage.Parts.Primary, GpuAccess.ShaderWrite, GpuAccess.ShaderRead, GpuStage.ComputeShader, GpuStage.ComputeShader),
        (SdfWorldPackage.Parts.Visibility, SdfWorldPackage.Parts.Primary, SdfWorldPackage.Parts.Surface, GpuAccess.ShaderWrite, GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuStage.ComputeShader, GpuStage.ComputeShader),
        (SdfWorldPackage.Parts.Visibility, SdfWorldPackage.Parts.Surface, SdfWorldPackage.Parts.Ambient, GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuStage.ComputeShader, GpuStage.ComputeShader),
        (SdfWorldPackage.Parts.Visibility, SdfWorldPackage.Parts.Ambient, SdfWorldPackage.Parts.Views, GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuAccess.ShaderRead, GpuStage.ComputeShader, GpuStage.ComputeShader),
    ];
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
            Format: RenderGraphPackageCatalog.WorkingFormat.ToString(),
            Name: SdfWorldPackage.Color
        )],
        Schema: RenderGraphSchemas.Graph
    ));

    private static IEnumerable<ShaderPipelinePlannedStorage> Buffers => Plan.Pipeline.Storages.Where(predicate: static storage => (storage.Declaration.Kind == ShaderPipelineResourceKind.Buffer));

    // The fragment part a spliced storage's name names.
    private static string PartOf(string storage) => storage[(Sdf.Length + 1)..];
    // The bytes the kernels index a fragment buffer by at counts: per viewport and tile the masks' words and the cull
    // buffer's planes, per viewport and instance its part bounds, the three indirect group counts, the dispatch box's four
    // words, and one visibility record a pixel of every viewport.
    private static ulong BytesOf(string buffer, ShaderPipelineStorageCounts counts) => buffer switch {
        SdfWorldPackage.Parts.InstanceMasks => ((counts.Viewports * counts.Tiles) * (counts.InstanceMaskWords * sizeof(uint))),
        SdfWorldPackage.Parts.Tiles => ((counts.Viewports * ((SdfWorldPackage.TilePlaneCount * counts.Tiles) + (SdfWorldPackage.PartBoundFloatCount * counts.Instances))) * sizeof(float)),
        SdfWorldPackage.Parts.Arguments => ShaderPipelineDispatch.ArgumentBytes,
        SdfWorldPackage.Parts.CullBounds => SdfWorldPackage.CullBoundsByteLength,
        SdfWorldPackage.Parts.Visibility => (((((ulong)counts.Width) * counts.Height) * counts.Viewports) * SdfWorldPackage.VisibilityRecordByteLength),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(buffer), actualValue: buffer, message: "Not a fragment buffer."),
    };

    [Fact]
    public void ThePlannedOrderIsTheFragmentsDispatchOrder() {
        Assert.Equal(
            actual: SdfWorldPackage.Fragment.Passes.Select(selector: static pass => pass.Name),
            expected: Order
        );
        Assert.Equal(
            actual: Plan.Pipeline.PassOrder,
            expected: Order.Select(selector: static part => RenderGraphPackageFragment.Spliced(
                name: part,
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
    public void ThePlannedBufferBarriersBetweenPassesArePinned() {
        var planned = new List<(string Buffer, string Producer, string Consumer, GpuAccess SourceAccess, GpuAccess DestinationAccess, GpuStage SourceStage, GpuStage DestinationStage)>();
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
                planned.Add(item: (PartOf(storage: storage.Name), passes[access.PriorPass].Package!.Part!, pass.Package!.Part!, access.Barrier.SourceAccess, access.Barrier.DestinationAccess, access.Barrier.SourceStage, access.Barrier.DestinationStage));
            }
        }

        Assert.Equal(
            actual: planned,
            expected: Edges
        );
    }
    [InlineData(64U, 64U, 1)]
    [InlineData(100U, 37U, 33)]
    [InlineData(17U, 300U, 65)]
    [InlineData(1920U, 1080U, 1000)]
    [InlineData(2560U, 1440U, 4097)]
    [Theory]
    public void ThePlannerSizesEveryBufferAsTheKernelsIndexIt(uint width, uint height, int instances) {
        // The counts a residency states for one view of this extent.
        var counts = new ShaderPipelineStorageCounts(
            Height: height,
            Width: width
        ) {
            InstanceMaskWords = ((ulong)SdfProgram.InstanceMaskStorageWordCountFor(instanceCount: instances)),
            Instances = ((ulong)instances),
            Tiles = (((ulong)((width + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize)) * ((height + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize)),
            Viewports = 1,
        };

        Assert.Equal(
            actual: Buffers.Select(selector: static storage => PartOf(storage: storage.Name)).Order(comparer: StringComparer.Ordinal),
            expected: new[] {
                SdfWorldPackage.Parts.Arguments,
                SdfWorldPackage.Parts.CullBounds,
                SdfWorldPackage.Parts.InstanceMasks,
                SdfWorldPackage.Parts.Tiles,
                SdfWorldPackage.Parts.Visibility,
            }.Order(comparer: StringComparer.Ordinal)
        );
        Assert.All(
            action: storage => Assert.Equal(
                actual: storage.Declaration.ResolveSizeBytes(counts: counts),
                expected: BytesOf(
                    buffer: PartOf(storage: storage.Name),
                    counts: counts
                )
            ),
            collection: Buffers
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
        // The planner gives each buffer's first use of a frame a cross-frame prior, whose barrier orders the one transient
        // allocation after the frame before, and every later use a pass prior.
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
