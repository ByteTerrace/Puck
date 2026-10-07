using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphCadenceLawTests {
    private static ShaderPipelinePlan CombinedAccessPlan() {
        var graph = Graph();

        graph = graph with {
            Packages = graph.Packages!.Select(selector: static pass => pass.Name switch {
                "shade" => pass with { Inputs = ["a"] },
                "composite" => pass with { Inputs = ["a", "b"] },
                _ => pass,
            }).ToArray(),
        };
        return new RenderGraphCompiler(Catalog(shadeReads: true, compositeReadsPredecessor: true)).Compile(definition: graph).Pipeline;
    }

    [Fact]
    public void OneIncomingBufferAccessIncludesTheReadAndWriteAndOrdersTheLaterComposite() {
        var plan = CombinedAccessPlan();
        var before = plan.FindResource(name: "a")!;
        var after = plan.FindResource(name: "b")!;
        var shade = plan.Passes[1];
        var access = Assert.Single(collection: shade.Accesses);
        var composite = Assert.Single(collection: plan.Passes[2].Accesses, predicate: use => (use.Storage == before.Storage));

        Assert.Equal(["write", "shade", "composite"], plan.PassOrder);
        Assert.Equal(before.Storage, after.Storage);
        Assert.Equal(before.Storage, access.Storage);
        Assert.Equal(0, before.WriterPassIndex);
        Assert.Equal(1, after.WriterPassIndex);
        Assert.Equal(plan.Passes.Count, before.LastUsePassIndex);
        Assert.Equal(plan.Passes.Count, after.LastUsePassIndex);
        Assert.True(condition: (before.Stored && after.Stored));
        Assert.Equal(["a"], shade.Inputs.Select(selector: static reference => reference.Name));
        Assert.Equal(["b"], shade.Outputs.Select(selector: static reference => reference.Name));
        Assert.Equal(["a", "b"], plan.Passes[2].Inputs.Select(selector: static reference => reference.Name));
        Assert.Equal("b", access.Version);
        Assert.Equal(ShaderPipelinePriorKind.Pass, access.PriorKind);
        Assert.Equal(0, access.PriorPass);
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, access.Barrier.Kind);
        Assert.Equal(GpuAccess.ShaderWrite, access.Barrier.SourceAccess);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, access.Use.Access);
        Assert.Equal(access.Use.Access, access.Barrier.DestinationAccess);
        Assert.Equal(GpuStage.ComputeShader, access.Barrier.SourceStage);
        Assert.Equal(GpuStage.ComputeShader, access.Barrier.DestinationStage);
        Assert.Equal(1, composite.PriorPass);
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, composite.Barrier.Kind);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, composite.Barrier.SourceAccess);
        Assert.Equal(GpuAccess.ShaderRead, composite.Barrier.DestinationAccess);
        Assert.DoesNotContain(collection: plan.Passes, filter: pass => pass.Accesses.Any(predicate: use =>
            ((use.PriorKind == ShaderPipelinePriorKind.Pass) && (use.PriorPass == pass.Index))));

        // The same logical storage's current and previous instances still require separate accesses.
        var history = new RenderGraphDefinition(Name: "roles", Schema: RenderGraphSchemas.Graph, Outputs: ["out"],
            Resources: [new(Name: "history", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 16,
                    Initialization: ShaderPipelineInitialization.Zero, History: true),
                new(Name: "out", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative())],
            Packages: [new(Name: "write", Package: Writer, Outputs: ["history"]),
                new(Name: "composite", Package: Composite, Inputs: ["history", new("history", PreviousFrame: true)], Outputs: ["out"])]);
        var roles = new RenderGraphCompiler(Catalog(compositeReadsPredecessor: true)).Compile(definition: history).Pipeline;
        var reads = roles.Passes[1].Accesses.Where(predicate: use => (use.Version == "history")).ToArray();

        Assert.Equal(2, reads.Length);
        Assert.Equal([false, true], reads.Select(selector: static use => use.PreviousFrame));
        Assert.Equal(reads[0].Storage, reads[1].Storage);
    }
    [Fact]
    public void ACombinedAccessKeepsReadbackAvailableThroughBothDeclaredVersions() {
        var plan = CombinedAccessPlan();

        Assert.Single(collection: plan.Passes[1].Accesses);
        var gpu = new FakePipelineGpu { Recording = true };
        var factory = new CombinedReadbackFactory();
        var packages = new RenderGraphPackageRecorders();

        foreach (var package in new[] { Writer, Shade, Composite }) { packages.Register(factory: factory, package: package); }
        using var node = new ShaderPipelineRenderNode(name: "combined-readback", deviceContext: gpu, pipelines: new GpuPassPipelineCache(),
            hostsOnDirectX: false, width: 32, height: 32, packages: packages);

        node.Swap(pipeline: new CompiledShaderPipeline(plan: plan, shaders: new Dictionary<string, CompiledShader>()));
        node.ProduceUntilInstalled();

        Assert.Equal(actual: factory.Requests, expected: ["a", "b"]);
        Assert.Equal(2, gpu.Events.Count(predicate: entry => (entry == $"copy buffer {factory.Source} to {factory.Destination}")));
    }

    private sealed class CombinedReadbackFactory : IRenderGraphPackageFactory {
        public readonly List<string> Requests = [];

        public nint Source;
        public nint Destination;

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) =>
            new Recorder(context: context, owner: this);

        private sealed class Recorder : IRenderGraphPackageRecorder, IRenderGraphPackageReadback {
            private readonly CombinedReadbackFactory m_owner;
            private readonly IGpuReadbackBuffer? m_buffer;

            private bool m_recorded;

            public Recorder(CombinedReadbackFactory owner, RenderGraphPackageRecorderContext context) {
                m_owner = owner;
                if (context.Package == Shade) {
                    m_buffer = context.Services.BufferFactory.CreateReadback(sizeBytes: 8,
                        name: new GpuObjectName(owner: context.Instance, part: context.Pass, detail: "aliases"));
                    owner.Destination = m_buffer.BufferHandle;
                }
            }

            public ulong ReadbackBytes => (m_buffer?.SizeBytes ?? 0);

            public void Dispose() => m_buffer?.Dispose();
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                if (m_buffer is not null) {
                    m_owner.Source = recording.Outputs[0].Buffer!.BufferHandle;
                    Assert.Equal(m_owner.Source, recording.Inputs[0].Buffer!.BufferHandle);
                    m_recorded = true;
                }
                return RenderGraphPackageOutcome.Drew;
            }
            public bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback) {
                readback = default;
                if (!m_recorded || (m_buffer is null) || (index >= 2)) { return false; }
                var version = ((index == 0) ? "a" : "b");

                m_owner.Requests.Add(item: version);
                readback = new RenderGraphBufferReadback(Destination: m_buffer, DestinationOffsetBytes: ((ulong)(index * 4)), SizeBytes: 4,
                    SourceOffsetBytes: 0, Version: version);
                return true;
            }
            public void Submitted(int slot, IGpuSubmissionFence fence) => m_recorded = false;
        }
    }
}
