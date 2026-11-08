using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphCadenceLawTests {
    [Fact]
    public void OnlyAReadbackPassThatDrewIsArmedAndEveryOneHearsItsSlotComplete() {
        var gpu = new FakePipelineGpu { Recording = true };
        var factory = new ArmingFactory();
        var packages = new RenderGraphPackageRecorders();

        foreach (var package in new[] { Writer, Shade, Composite }) { packages.Register(factory: factory, package: package); }
        using var node = new ShaderPipelineRenderNode(name: "readback-arming", deviceContext: gpu, pipelines: new GpuPassPipelineCache(),
            hostsOnDirectX: false, width: 32, height: 32, packages: packages);

        node.Swap(pipeline: new CompiledShaderPipeline(plan: new RenderGraphCompiler(Catalog()).Compile(definition: Graph()).Pipeline,
            shaders: new Dictionary<string, CompiledShader>()));
        node.ProduceUntilInstalled();
        Assert.Equal(["completed 0", "record 0", "submitted 0"], factory.Events);

        factory.Events.Clear();
        factory.Skip = true;
        for (var frame = 0; (frame < 3); frame++) { _ = node.ProduceFrame(context: default); }
        // The armed slot is proven complete as soon as its fence reads signaled, before its reuse; a skipped pass is then
        // told each slot it would reuse has completed, and is never armed.
        Assert.Equal(4, factory.Events.Count);
        Assert.Equal("completed 0", factory.Events[0]);
        Assert.All(factory.Events, static entry => Assert.StartsWith("completed ", entry));
        var next = $"{((int.Parse(factory.Events[^1]["completed ".Length..], provider: System.Globalization.CultureInfo.InvariantCulture) + 1) % 3)}";

        factory.Events.Clear();
        factory.Skip = false;
        _ = node.ProduceFrame(context: default);
        Assert.Equal([$"completed {next}", $"record {next}", $"submitted {next}"], factory.Events);
    }

    private sealed class ArmingFactory : IRenderGraphPackageFactory {
        public readonly List<string> Events = [];
        public bool Skip;

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) =>
            ((context.Package == Shade) ? new ReadbackRecorder(owner: this) : new PlainRecorder());

        private sealed class PlainRecorder : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) => RenderGraphPackageOutcome.Drew;
        }
        private sealed class ReadbackRecorder(ArmingFactory owner) : IRenderGraphPackageRecorder, IRenderGraphPackageReadback {
            public ulong ReadbackBytes => 0;

            public void Dispose() { }
            public bool Skips(in FrameContext context) => owner.Skip;
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Events.Add(item: $"record {recording.Slot}");
                return RenderGraphPackageOutcome.Drew;
            }
            public bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback) { readback = default; return false; }
            public void Submitted(int slot) => owner.Events.Add(item: $"submitted {slot}");
            public void Completed(int slot) => owner.Events.Add(item: $"completed {slot}");
        }
    }
}
