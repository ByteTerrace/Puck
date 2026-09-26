using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// The SDF engine's pass set, described as package passes of one planner definition, plans exactly as the engine
/// records it by hand. The definition is built from <see cref="SdfFrameBufferPlan.Uses"/>: a write starts a buffer's
/// version, a read-write forwards the latest version, a read binds it, and an indirect read is the pass's indirect
/// dispatch arguments; a buffer read before any pass writes it (the brick pool, which world-scoped brick work fills) is
/// external. The planner must order the passes as <see cref="SdfWorldEngine.PassLabels"/> less the upload, whose region
/// copies touch no frame buffer and are the regions' own, and plan,
/// between passes of the frame, exactly the buffer transitions <see cref="SdfFrameBufferPlan.Edges"/> derives. The one
/// image chain (sky, then views shading over it into the view's output) is the passes' published output and is not part
/// of the buffer plan.
/// Every buffer is counted over the capacities it grows with, and at every capacity the planner's size is the engine's
/// allocation, <see cref="SdfWorldEngine.FrameBufferBytes"/>.
/// </summary>
public sealed class SdfPassPlanLawTests {
    private const string Upload = "upload";

    // The engine's ledger label for each dispatch, mirrored because the engine keeps the pairing only in the order of its
    // Record methods. Brick work is null: it is world-scoped and joins the views through the external brick pool. So is the
    // upload: its region copies transition what they copy themselves.
    private static string? LabelOf(SdfFramePass pass) => pass switch {
        SdfFramePass.BrickUpload or SdfFramePass.BrickBake or SdfFramePass.Upload => null,
        SdfFramePass.Sky => "sky",
        SdfFramePass.Mask => "mask",
        SdfFramePass.Beam => "beam",
        SdfFramePass.CullArgs => "cull-args",
        SdfFramePass.Primary => "primary",
        SdfFramePass.Surface => "surface",
        SdfFramePass.Ambient => "ambient",
        SdfFramePass.Views => "views",
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(pass)),
    };
    private static ShaderPipelineCountTerm Term(ulong elements, params ShaderPipelineCountBasis[] per) => new(
        Elements: elements,
        Per: per
    );
    // Each buffer's element stride and size, counted by the capacities it grows with. The brick pool is counted as the
    // sdf.bricks package's port declares it; the indirect arguments and the dispatch box are fixed records.
    private static (uint Stride, ulong? SizeBytes, IReadOnlyList<ShaderPipelineCountTerm>? Count) StorageOf(SdfFrameBuffer buffer, SdfFrameCapacity capacity) => buffer switch {
        SdfFrameBuffer.BrickPool => (4, null, [Term(1, ShaderPipelineCountBasis.BrickPoolVoxels)]),
        SdfFrameBuffer.InstanceMasks => (4, null, [Term(1, ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles, ShaderPipelineCountBasis.InstanceMaskWords)]),
        // Four tile planes per tile, then two float3 part-bound corners in each of the primary and AO bands per instance.
        SdfFrameBuffer.Tiles => (4, null, [
            Term(4, ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles),
            Term(12, ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Instances),
        ]),
        SdfFrameBuffer.ViewsArgs => (4, ShaderPipelineDispatch.ArgumentBytes, null),
        // The dispatch box: the group origin, then the exclusive group end, four uints.
        SdfFrameBuffer.CullBounds => (4, (4 * sizeof(uint)), null),
        SdfFrameBuffer.PrimaryHits => (((uint)SdfWorldEngine.VisibilityRecordByteLength), null, [Term(1, ShaderPipelineCountBasis.Extent, ShaderPipelineCountBasis.Viewports)]),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(buffer)),
    };
    // The counts a host resolves for an engine of this capacity, each derived as the engine derives it.
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
    private static ShaderPipelineResource Buffer(SdfFrameBuffer buffer, SdfFrameCapacity capacity, string name, string? from, bool external) {
        var (stride, size, count) = StorageOf(
            buffer: buffer,
            capacity: capacity
        );

        return new ShaderPipelineResource(
            Count: count,
            From: from,
            Initialization: (external
                ? ShaderPipelineInitialization.External
                : ShaderPipelineInitialization.Undefined),
            Kind: ShaderPipelineResourceKind.Buffer,
            Name: name,
            SizeBytes: size,
            StrideBytes: stride
        );
    }
    private static ShaderPipelineResource Image(string name, string? from) => new(
        Dimensions: ShaderPipelineDimensions.Relative(),
        Format: nameof(GpuPixelFormat.R8G8B8A8Unorm),
        From: from,
        Name: name
    );

    private static SdfFrameCapacity Default { get; } = Capacity(
        height: 64,
        instances: 1,
        viewports: 1,
        width: 64
    );
    // The dispatches of one rendered view, in recording order: every labelled pass.
    private static SdfFramePass[] Frame { get; } = [.. Enum.GetValues<SdfFramePass>().Where(predicate: static pass => (LabelOf(pass: pass) is not null))];

    private static ShaderPipelinePlan Plan(SdfFrameCapacity capacity) {
        var latest = new Dictionary<SdfFrameBuffer, string>();
        var resources = new List<ShaderPipelineResource> {
            Image(
                from: null,
                name: "sky"
            ),
            Image(
                from: "sky",
                name: "color"
            ),
        };
        var passes = new List<(string Label, List<ResourceReference> Inputs, List<ResourceReference> Outputs, List<string> Arguments)>();

        foreach (var pass in Frame) {
            var label = LabelOf(pass: pass)!;

            if ((passes.Count == 0) || (passes[^1].Label != label)) {
                passes.Add(item: (label, [], [], []));
            }

            var (_, inputs, outputs, arguments) = passes[^1];

            foreach (var use in SdfFrameBufferPlan.Uses(pass: pass)) {
                var buffer = use.Buffer;
                var root = buffer.ToString();

                switch (use.Access) {
                    case SdfBufferAccess.Write:
                        Assert.False(condition: latest.ContainsKey(key: buffer), userMessage: $"{buffer} is written twice from discarded contents in one frame.");
                        resources.Add(item: Buffer(buffer: buffer, capacity: capacity, external: false, from: null, name: root));
                        latest[buffer] = root;
                        outputs.Add(item: root);
                        break;
                    case SdfBufferAccess.ReadWrite:
                        var forwarded = $"{root}.{label}";

                        resources.Add(item: Buffer(buffer: buffer, capacity: capacity, external: false, from: latest[buffer], name: forwarded));
                        latest[buffer] = forwarded;
                        outputs.Add(item: forwarded);
                        break;
                    default:
                        if (!latest.ContainsKey(key: buffer)) {
                            resources.Add(item: Buffer(buffer: buffer, capacity: capacity, external: true, from: null, name: root));
                            latest[buffer] = root;
                        }
                        if (use.Access == SdfBufferAccess.IndirectRead) {
                            arguments.Add(item: latest[buffer]);
                        } else {
                            inputs.Add(item: latest[buffer]);
                        }
                        break;
                }
            }
            if (label == "sky") {
                outputs.Add(item: "sky");
            } else if (label == "views") {
                outputs.Add(item: "color");
            }
        }

        var packages = passes.Select(selector: static pass => new ShaderPipelinePackagePass(
            Dispatch: (pass.Arguments.Count switch {
                0 => null,
                _ => ShaderPipelineDispatch.Indirect(arguments: Assert.Single(collection: pass.Arguments)),
            }),
            Members: [],
            InputAccesses: [.. pass.Inputs.Select(selector: static _ => RenderGraphPortAccess.ComputeRead)],
            Inputs: pass.Inputs,
            Name: pass.Label,
            OutputAccesses: [.. pass.Outputs.Select(selector: static _ => RenderGraphPortAccess.ComputeWrite)],
            Outputs: pass.Outputs,
            Package: RenderGraphPackageCatalog.SdfWorld
        )).ToArray();

        return new ShaderPipelineCompiler().Compile(
            definition: new RenderGraphDefinition(
                name: RenderGraphPackageCatalog.SdfWorld,
                outputs: ["color"],
                passes: [],
                resources: resources
            ),
            packages: packages
        );
    }

    [Fact]
    public void ThePlannedOrderIsTheEnginesPassOrderLessTheUpload() {
        var labels = SdfWorldEngine.PassLabels.ToArray();

        Assert.Equal(
            actual: Frame.Select(selector: static pass => LabelOf(pass: pass)!).Distinct(),
            expected: labels.Where(predicate: static label => (label != Upload))
        );
        Assert.Equal(
            actual: Plan(capacity: Default).PassOrder,
            expected: labels.Where(predicate: static label => (label != Upload))
        );
    }
    [Fact]
    public void ThePlannedBufferBarriersBetweenPassesAreExactlyTheEnginesEdges() {
        var plan = Plan(capacity: Default);
        var planned = new List<(SdfFrameBuffer Buffer, string Producer, string Consumer, GpuAccess SourceAccess, GpuAccess DestinationAccess, GpuStage SourceStage, GpuStage DestinationStage)>();

        foreach (var pass in plan.Passes) {
            foreach (var access in pass.Accesses) {
                var storage = plan.Storages[access.Storage];

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
                planned.Add(item: (Enum.Parse<SdfFrameBuffer>(value: storage.Name), plan.Passes[access.PriorPass].Name, pass.Name, access.Barrier.SourceAccess, access.Barrier.DestinationAccess, access.Barrier.SourceStage, access.Barrier.DestinationStage));
            }
        }

        var expected = SdfFrameBufferPlan.Edges(passes: Frame).Select(selector: static edge => (edge.Buffer, LabelOf(pass: edge.Producer)!, LabelOf(pass: edge.Consumer)!, edge.SourceAccess, edge.DestinationAccess, edge.SourceStage, edge.DestinationStage));

        Assert.Equal(
            actual: planned,
            expected: expected
        );
    }
    [InlineData(64U, 64U, 1U, 1)]
    [InlineData(100U, 37U, 2U, 33)]
    [InlineData(17U, 300U, 3U, 65)]
    [InlineData(1920U, 1080U, 4U, 1000)]
    [InlineData(2560U, 1440U, SdfWorldEngine.MaxViewports, 4097)]
    [Theory]
    public void ThePlannerSizesEveryBufferAsTheEngineAllocatesIt(uint width, uint height, uint viewports, int instances) {
        var capacity = Capacity(
            height: height,
            instances: instances,
            viewports: viewports,
            width: width
        );
        var counts = CountsOf(capacity: capacity);
        var buffers = Plan(capacity: capacity).Storages.Where(predicate: static storage => (storage.Declaration.Kind == ShaderPipelineResourceKind.Buffer)).ToArray();

        Assert.Equal(
            actual: buffers.Select(selector: static storage => Enum.Parse<SdfFrameBuffer>(value: storage.Name)).Order(),
            expected: Enum.GetValues<SdfFrameBuffer>().Order()
        );
        Assert.All(
            action: storage => Assert.Equal(
                actual: storage.Declaration.ResolveSizeBytes(counts: counts),
                expected: SdfWorldEngine.FrameBufferBytes(
                    buffer: Enum.Parse<SdfFrameBuffer>(value: storage.Name),
                    capacity: capacity
                )
            ),
            collection: buffers
        );
    }
    [Fact]
    public void TheExternalBrickPoolIsTheBufferSdfBricksWrites() {
        var pool = Plan(capacity: Default).Storages.Single(predicate: static storage => (storage.Name == nameof(SdfFrameBuffer.BrickPool)));

        Assert.True(condition: pool.Declaration.IsExternal);
        Assert.True(condition: RenderGraphPackageCatalog.BrickPool.Accepts(resource: pool.Declaration));
        Assert.True(condition: RenderGraphPackageCatalog.Engine.TryGet(
            id: RenderGraphPackageCatalog.SdfBricks,
            package: out var bricks
        ));
        Assert.Equal(
            actual: Assert.Single(collection: bricks.Outputs),
            expected: RenderGraphPackageCatalog.BrickPool
        );
    }
    [Fact]
    public void EachBuffersFirstUseInTheFrameStartsFromOutsideIt() {
        // The engine owes nothing for a buffer's first use in a command list: its top-of-frame barrier, or the brick
        // work's own, orders it. The planner gives exactly that use a cross-frame or host prior, and every later use a
        // pass prior.
        var plan = Plan(capacity: Default);

        foreach (var storage in plan.Storages.Where(predicate: static storage => (storage.Declaration.Kind == ShaderPipelineResourceKind.Buffer))) {
            var priors = plan.Passes.SelectMany(selector: static pass => pass.Accesses).Where(predicate: access => (access.Storage == storage.Index)).Select(selector: static access => access.PriorKind).ToArray();

            Assert.NotEqual(
                actual: priors[0],
                expected: ShaderPipelinePriorKind.Pass
            );
            Assert.All(
                action: static prior => Assert.Equal(
                    actual: prior,
                    expected: ShaderPipelinePriorKind.Pass
                ),
                collection: priors[1..]
            );
        }
    }
}
