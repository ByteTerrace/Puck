using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphHistoryLawTests {
    private const string Indirect = "test.history-indirect";

    // A fragment whose first pass writes a history buffer of dispatch arguments and whose second dispatches indirectly
    // from it.
    private static CompiledShaderPipeline IndirectHistory() {
        var catalog = new RenderGraphPackageCatalog(packages: [new RenderGraphPackage(
            Fragment: new RenderGraphPackageFragment(
                InputVersions: [],
                OutputVersions: ["out"],
                Passes: [
                    new RenderGraphFragmentPass(
                        InputAccesses: [],
                        Inputs: [],
                        Name: "count",
                        OutputAccesses: [RenderGraphPortAccess.ComputeWrite],
                        Outputs: ["args"]
                    ),
                    new RenderGraphFragmentPass(
                        Dispatch: ShaderPipelineDispatch.Indirect(arguments: "args"),
                        InputAccesses: [],
                        Inputs: [],
                        Name: "draw",
                        OutputAccesses: [RenderGraphPortAccess.ComputeWrite],
                        Outputs: ["out"]
                    ),
                ],
                Resources: [
                    new ShaderPipelineResource(Name: "args", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 16, StrideBytes: 4, History: true, Initialization: ShaderPipelineInitialization.Zero),
                    new ShaderPipelineResource(Name: "out", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative()),
                ]
            ),
            Id: Indirect,
            Inputs: [],
            Members: [],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
            Summary: "Counts groups into history and dispatches indirectly from them."
        )]);
        var graph = new RenderGraphDefinition(Schema: RenderGraphSchemas.Graph, Name: "indirect-history", Outputs: ["final"],
            Resources: [new ShaderPipelineResource(Name: "final", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative())],
            Packages: [new RenderGraphPackagePass(Name: "frag", Package: Indirect, Inputs: [], Outputs: ["final"])]);

        return new CompiledShaderPipeline(plan: new RenderGraphCompiler(catalog).Compile(definition: graph).Pipeline,
            shaders: new Dictionary<string, CompiledShader>());
    }

    // An indirect dispatch reads its group counts from a history buffer through the cursor its barrier and its writer
    // use: over three frame slots, while the counting pass stands, every frame's dispatch reads the buffer the writer last
    // wrote, and every barrier into the indirect-argument state names that buffer.
    [Fact]
    public void AnIndirectDispatchReadsTheHistoryArgumentsItsStandingWriterLastWrote() {
        var gpu = new FakePipelineGpu { Recording = true };
        var model = new IndirectModel();
        var packages = new RenderGraphPackageRecorders();

        packages.Register(factory: model, package: Indirect);
        using var node = new ShaderPipelineRenderNode(deviceContext: gpu, width: 32, height: 32, hostsOnDirectX: false,
            name: "indirect", packages: packages, outputLayout: GpuImageLayout.ShaderReadOnly, pipelines: new GpuPassPipelineCache());

        node.Swap(pipeline: IndirectHistory());
        node.ProduceUntilInstalled();
        var written = Assert.Single(collection: model.Writes);

        Assert.Equal(expected: written, actual: Assert.Single(collection: model.Dispatched));
        Assert.Contains(
            collection: gpu.Barriers,
            filter: barrier => (barrier.Barrier.DestinationAccess.HasFlag(flag: GpuAccess.IndirectCommandRead) && (barrier.Handle == written))
        );
        for (var frame = 0; (frame < 6); frame++) {
            gpu.Barriers.Clear();
            model.Dispatched.Clear();
            node.ProduceFrame(context: default);
            Assert.Equal(expected: written, actual: Assert.Single(collection: model.Dispatched));
            Assert.All(
                action: barrier => Assert.Equal(actual: barrier.Handle, expected: written),
                collection: gpu.Barriers.Where(predicate: static barrier => barrier.Barrier.DestinationAccess.HasFlag(flag: GpuAccess.IndirectCommandRead))
            );
        }
        Assert.Single(collection: model.Writes);
    }

    private sealed class IndirectModel : IRenderGraphPackageFactory {
        public readonly List<nint> Writes = [];
        public readonly List<nint> Dispatched = [];

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this, counts: (context.Part == "count"));

        private sealed class Recorder(IndirectModel owner, bool counts) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public ulong? Signature(in FrameContext context) => (counts ? 1UL : null);
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                if (counts) {
                    owner.Writes.Add(item: recording.Outputs[0].Buffer!.BufferHandle);
                    recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
                } else {
                    var arguments = recording.Arguments!.BufferHandle;

                    owner.Dispatched.Add(item: arguments);
                    recording.Recorder.DispatchIndirect(commandBufferHandle: recording.CommandBuffer, argumentBufferHandle: arguments, argumentBufferOffset: 0);
                }
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
