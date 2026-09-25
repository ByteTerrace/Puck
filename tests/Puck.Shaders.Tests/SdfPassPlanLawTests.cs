using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.Shaders.Tests;

/// <summary>
/// The SDF engine's pass set, described as package passes of one planner definition, plans exactly as the engine
/// records it by hand. The definition is built from <see cref="SdfFrameBufferPlan.Uses"/>: a write starts a buffer's
/// version, a read-write forwards the latest version, a read binds it, and an indirect read is the pass's indirect
/// dispatch arguments; a buffer read before any pass writes it (the brick pool, which world-scoped brick work fills) is
/// external. The planner must order the passes as <see cref="SdfWorldEngine.PassLabels"/> less the composite, and plan,
/// between passes of the frame, exactly the buffer transitions <see cref="SdfFrameBufferPlan.Edges"/> derives. The one
/// image chain (sky, then views shading over it) is the passes' published output and is not part of the buffer plan.
/// </summary>
public sealed class SdfPassPlanLawTests {
    private const string Composite = "composite";

    // The engine's ledger label for each dispatch, mirrored because the engine keeps the pairing only in the order of its
    // Record methods. Brick work is null: it is world-scoped and joins the views through the external brick pool. The
    // three table uploads share the one upload pass.
    private static string? LabelOf(SdfFramePass pass) => pass switch {
        SdfFramePass.BrickUpload or SdfFramePass.BrickBake => null,
        SdfFramePass.UploadViewports or SdfFramePass.UploadDynamicTransforms or SdfFramePass.UploadInstanceGrid => "upload",
        SdfFramePass.Sky => "sky",
        SdfFramePass.Mask => "mask",
        SdfFramePass.Beam => "beam",
        SdfFramePass.CullArgs => "cull-args",
        SdfFramePass.Primary => "primary",
        SdfFramePass.Surface => "surface",
        SdfFramePass.Ambient => "ambient",
        SdfFramePass.Views => "views",
        SdfFramePass.Composite => Composite,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(pass)),
    };
    // Each buffer's element stride and size: fixed where the engine allocates a fixed table, counted by the basis its
    // capacity grows with otherwise. The masks, tile planes and hit records also scale with the viewport and tile counts,
    // which no count basis carries; the edges this law checks do not depend on any capacity.
    private static (uint Stride, ulong? SizeBytes, ShaderPipelineBufferCount? Count) StorageOf(SdfFrameBuffer buffer) => buffer switch {
        SdfFrameBuffer.Viewports => (96, ((ulong)(SdfWorldEngine.MaxViewports * 96)), null),
        SdfFrameBuffer.DynamicTransforms => (48, null, new ShaderPipelineBufferCount(Basis: ShaderPipelineCountBasis.Instances)),
        SdfFrameBuffer.InstanceGrid => (4, null, new ShaderPipelineBufferCount(Basis: ShaderPipelineCountBasis.Instances)),
        SdfFrameBuffer.BrickPool => (4, ((ulong)(SdfWorldEngine.DefaultBrickPoolVoxelCapacity * 4)), null),
        SdfFrameBuffer.InstanceMasks => (4, null, new ShaderPipelineBufferCount(Basis: ShaderPipelineCountBasis.Instances)),
        SdfFrameBuffer.Tiles => (4, null, new ShaderPipelineBufferCount(
            Basis: ShaderPipelineCountBasis.Instances,
            Elements: 12
        )),
        SdfFrameBuffer.ViewsArgs => (4, ShaderPipelineDispatch.ArgumentBytes, null),
        SdfFrameBuffer.CullBounds => (4, 8, null),
        SdfFrameBuffer.PrimaryHits => (80, null, new ShaderPipelineBufferCount(Basis: ShaderPipelineCountBasis.Extent)),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(buffer)),
    };
    private static ShaderPipelineResource Buffer(SdfFrameBuffer buffer, string name, string? from, bool external) {
        var (stride, size, count) = StorageOf(buffer: buffer);

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

    // The dispatches of one rendered view, in recording order: every labelled pass but the composite.
    private static SdfFramePass[] Frame { get; } = [.. Enum.GetValues<SdfFramePass>().Where(predicate: static pass => ((LabelOf(pass: pass) is { } label) && (label != Composite)))];

    private static ShaderPipelinePlan Plan() {
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
                        resources.Add(item: Buffer(buffer: buffer, external: false, from: null, name: root));
                        latest[buffer] = root;
                        outputs.Add(item: root);
                        break;
                    case SdfBufferAccess.ReadWrite:
                        var forwarded = $"{root}.{label}";

                        resources.Add(item: Buffer(buffer: buffer, external: false, from: latest[buffer], name: forwarded));
                        latest[buffer] = forwarded;
                        outputs.Add(item: forwarded);
                        break;
                    default:
                        if (!latest.ContainsKey(key: buffer)) {
                            resources.Add(item: Buffer(buffer: buffer, external: true, from: null, name: root));
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

        var packages = passes.Select(selector: static pass => new ShaderPipelinePass(
            Dispatch: (pass.Arguments.Count switch {
                0 => null,
                _ => ShaderPipelineDispatch.Indirect(arguments: Assert.Single(collection: pass.Arguments)),
            }),
            EntryPoint: string.Empty,
            Inputs: pass.Inputs,
            Kind: ShaderPipelinePassKind.Package,
            Name: pass.Label,
            Outputs: pass.Outputs,
            Source: RenderGraphPackageCatalog.SdfWorld
        )).ToArray();

        return new ShaderPipelineCompiler().Compile(
            definition: new ShaderPipelineDefinition(
                name: RenderGraphPackageCatalog.SdfWorld,
                outputs: ["color"],
                passes: [],
                resources: resources
            ),
            packages: packages
        );
    }

    [Fact]
    public void ThePlannedOrderIsTheEnginesPassOrderLessTheComposite() {
        var labels = SdfWorldEngine.PassLabels.ToArray();

        Assert.Equal(
            actual: Frame.Select(selector: static pass => LabelOf(pass: pass)!).Distinct(),
            expected: labels.Where(predicate: static label => (label != Composite))
        );
        Assert.Equal(
            actual: Plan().PassOrder,
            expected: labels.Where(predicate: static label => (label != Composite))
        );
    }
    [Fact]
    public void ThePlannedBufferBarriersBetweenPassesAreExactlyTheEnginesEdges() {
        var plan = Plan();
        var planned = new List<(SdfFrameBuffer Buffer, string Producer, string Consumer, GpuComputeAccess SourceAccess, GpuComputeAccess DestinationAccess, GpuComputeStage SourceStage, GpuComputeStage DestinationStage)>();

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
    [Fact]
    public void EachBuffersFirstUseInTheFrameStartsFromOutsideIt() {
        // The engine owes nothing for a buffer's first use in a command list: its top-of-frame barrier, or the brick
        // work's own, orders it. The planner gives exactly that use a cross-frame or host prior, and every later use a
        // pass prior.
        var plan = Plan();

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
