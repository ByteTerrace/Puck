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
        // A slot nothing was submitted to has no completion to hear.
        Assert.Equal(["record 0", "submitted 0"], factory.Events);

        factory.Events.Clear();
        factory.Skip = true;
        for (var frame = 0; (frame < 3); frame++) { _ = node.ProduceFrame(context: default); }
        // The armed slot is proven complete as soon as its fence reads signaled, before its reuse. A skipped pass is never
        // armed, and still hears each submission of a slot complete, once: an early proof and the wait at the slot's reuse
        // prove the same submission. Each slot is submitted at most once in these three frames.
        var skipped = factory.Events.ToArray();

        Assert.Equal("completed 0", skipped[0]);
        Assert.All(skipped, static entry => Assert.StartsWith("completed ", entry));
        Assert.Equal(skipped.Length, skipped.Distinct().Count());

        factory.Events.Clear();
        factory.Skip = false;
        _ = node.ProduceFrame(context: default);
        var slot = ((int)((node.FrameCounter - 1) % 3));

        Assert.Equal([$"record {slot}", $"submitted {slot}"], factory.Events.Where(static entry => !entry.StartsWith("completed ", StringComparison.Ordinal)));
    }

    // A submission whose batch never reached the queue proves nothing: its readback pass hears the slot abandoned, never
    // completed, before the slot records again, and the node's other slots are settled first.
    [Fact]
    public void AnAbandonedSlotsReadbackIsDroppedNeverCompleted() {
        var gpu = new FakePipelineGpu { Recording = true };
        var factory = new ArmingFactory();
        var packages = new RenderGraphPackageRecorders();

        foreach (var package in new[] { Writer, Shade, Composite }) { packages.Register(factory: factory, package: package); }
        using var node = new ShaderPipelineRenderNode(name: "readback-abandoned", deviceContext: gpu, pipelines: new GpuPassPipelineCache(),
            hostsOnDirectX: false, width: 32, height: 32, packages: packages);

        node.Swap(pipeline: new CompiledShaderPipeline(plan: new RenderGraphCompiler(Catalog()).Compile(definition: Graph()).Pipeline,
            shaders: new Dictionary<string, CompiledShader>()));
        node.ProduceUntilInstalled();
        factory.Events.Clear();
        gpu.AbandonNextSubmission = true;
        _ = node.ProduceFrame(context: default);

        var abandoned = factory.Events.Single(static entry => entry.StartsWith("submitted ", StringComparison.Ordinal))["submitted ".Length..];

        factory.Events.Clear();
        for (var frame = 0; (frame < 3); frame++) { _ = node.ProduceFrame(context: default); }

        var aboutAbandoned = factory.Events.Where(entry => entry.EndsWith($" {abandoned}", StringComparison.Ordinal)).ToArray();

        // A later completion of the slot comes only after the node settled every other slot, and delivers nothing the
        // abandoned submission carried (RenderGraphSlotResults below).
        Assert.Equal($"abandoned {abandoned}", aboutAbandoned[0]);
    }
    // A frame whose submission was taken for later and then never reached the queue committed its history before the
    // hand-over failed; the node rolls it back before its next frame reads that history, so the frame counter (which
    // indexes a graphics history writer's framebuffer ring) steps back with the cursors and the next frame takes its place.
    [Fact]
    public void AFrameWhoseSubmissionNeverReachedTheQueueIsRolledBackBeforeTheNextFrame() {
        var gpu = new FakePipelineGpu { Recording = true };
        var factory = new ArmingFactory();
        var packages = new RenderGraphPackageRecorders();

        foreach (var package in new[] { Writer, Shade, Composite }) { packages.Register(factory: factory, package: package); }
        using var node = new ShaderPipelineRenderNode(name: "hand-over-rollback", deviceContext: gpu, pipelines: new GpuPassPipelineCache(),
            hostsOnDirectX: false, width: 32, height: 32, packages: packages);

        node.Swap(pipeline: new CompiledShaderPipeline(plan: new RenderGraphCompiler(Catalog()).Compile(definition: Graph()).Pipeline,
            shaders: new Dictionary<string, CompiledShader>()));
        node.ProduceUntilInstalled();

        var before = node.FrameCounter;

        gpu.AbandonNextSubmission = true;
        _ = node.ProduceFrame(context: default);
        Assert.Equal(before + 1, node.FrameCounter);
        _ = node.ProduceFrame(context: default);
        Assert.Equal(before + 1, node.FrameCounter);
        _ = node.ProduceFrame(context: default);
        Assert.Equal(before + 2, node.FrameCounter);
    }
    // A package's commit (a camera a shadow layer was drawn from, a target known clear) has no checkpoint to roll back, so
    // a recorder hears Submitted only for a submission that reached the queue: never for one its batch abandoned, whose
    // commands never ran, and once for the next frame that did run.
    [Fact]
    public void ARecorderCommitsOnlyASubmissionThatReachedTheQueue() {
        var gpu = new FakePipelineGpu { Recording = true };
        var factory = new ArmingFactory();
        var packages = new RenderGraphPackageRecorders();

        foreach (var package in new[] { Writer, Shade, Composite }) { packages.Register(factory: factory, package: package); }
        using var node = new ShaderPipelineRenderNode(name: "commit-on-hand-over", deviceContext: gpu, pipelines: new GpuPassPipelineCache(),
            hostsOnDirectX: false, width: 32, height: 32, packages: packages);

        node.Swap(pipeline: new CompiledShaderPipeline(plan: new RenderGraphCompiler(Catalog()).Compile(definition: Graph()).Pipeline,
            shaders: new Dictionary<string, CompiledShader>()));
        node.ProduceUntilInstalled();
        Assert.Equal(1, factory.Commits);

        gpu.AbandonNextSubmission = true;
        _ = node.ProduceFrame(context: default);
        Assert.Equal(1, factory.Commits);
        _ = node.ProduceFrame(context: default);
        Assert.Equal(2, factory.Commits);
    }
    [Fact]
    public void AnAbandonedSlotsResultIsDroppedAndALaterCompletionDeliversNothing() {
        var completed = new List<int>();
        var dropped = new List<int>();
        var results = new RenderGraphSlotResults<int>(slots: 2, complete: completed.Add, discard: dropped.Add);

        results.Record(slot: 1, result: 7);
        results.Submitted(slot: 1);
        results.Abandoned(slot: 1);
        results.Completed(slot: 1);

        Assert.Empty(completed);
        Assert.Equal([7], dropped);
    }

    private sealed class ArmingFactory : IRenderGraphPackageFactory {
        public readonly List<string> Events = [];
        public bool Skip;
        // The commits (IRenderGraphPackageRecorder.Submitted) the readback pass has heard.
        public int Commits;

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
            public void Submitted() => owner.Commits++;
            public void Submitted(int slot) => owner.Events.Add(item: $"submitted {slot}");
            public void Completed(int slot) => owner.Events.Add(item: $"completed {slot}");
            public void Abandoned(int slot) => owner.Events.Add(item: $"abandoned {slot}");
        }
    }
}
