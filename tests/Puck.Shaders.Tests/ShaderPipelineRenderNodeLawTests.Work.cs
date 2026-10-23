using System.Text;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Work-counting laws for <see cref="ShaderPipelineRenderNode"/>: each pass of the feedback graph reports exactly the
/// GPU calls it records, a submission is read only once it has completed, and a reset, reload or resize withdraws the
/// sample until a newer submission completes. The expected lines are the ones the <c>pipeline-counters</c> canary
/// asserts on both backends, derived from what each pass records rather than recorded from a run.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    // The initialization submission after a reset. The accumulate pass zero-clears the one history slot it reads as the
    // previous frame's (a transition to general and a clear), moves that slot to shader-read for its read, and moves the
    // slot it writes from discarded contents to general; the other slots are written before anything reads them, so
    // their zeros would be unobservable and are not cleared. Convert moves history to shader-read and gray to general.
    // The fullscreen copy moves gray to shader-read and its target to render-target in its one barrier command buffer;
    // its render pass leaves the target shader-readable, and publication, outside every pass, moves it to the output
    // layout. Every pass binds its frame group set and its pass group set and pushes nothing, and uploads nothing: every
    // slot has held its pass block since the graph installed. Outside every pass the node sends the frame group's whole
    // region, one 256-byte constant-buffer view, which it rewrites every frame.
    internal static readonly string[] InitializationWork = [
        "work accumulate executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=3 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=2 push-constants=0 descriptor-writes=2 uploads.host-visible=0 clears=1 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0 sky.hashes=0 sky.texture-loads=0 environment.projections=0 environment.projection-texels=0 environment.skipped=0 shapes.evaluated=0 shapes.gradients=0 vertices=0 draws.indirect=0",
        "work convert executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=2 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=2 push-constants=0 descriptor-writes=2 uploads.host-visible=0 clears=0 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0 sky.hashes=0 sky.texture-loads=0 environment.projections=0 environment.projection-texels=0 environment.skipped=0 shapes.evaluated=0 shapes.gradients=0 vertices=0 draws.indirect=0",
        "work copy executed: dispatches=0 dispatches.indirect=0 draws=1 render-passes=1 command-buffers=0 barriers.image=2 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=2 push-constants=0 descriptor-writes=1 uploads.host-visible=0 clears=0 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0 sky.hashes=0 sky.texture-loads=0 environment.projections=0 environment.projection-texels=0 environment.skipped=0 shapes.evaluated=0 shapes.gradients=0 vertices=3 draws.indirect=0",
        "work outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=1 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=256 clears=0 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0 sky.hashes=0 sky.texture-loads=0 environment.projections=0 environment.projection-texels=0 environment.skipped=0 shapes.evaluated=0 shapes.gradients=0 vertices=0 draws.indirect=0",
    ];
    // The next submission: nothing is cleared, the previous history is already shader-read, and the slot written next
    // was discarded by the reset, so accumulate transitions it to general.
    internal static readonly string[] SecondWork = [
        "work accumulate executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=1 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=2 push-constants=0 descriptor-writes=2 uploads.host-visible=0 clears=0 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0 sky.hashes=0 sky.texture-loads=0 environment.projections=0 environment.projection-texels=0 environment.skipped=0 shapes.evaluated=0 shapes.gradients=0 vertices=0 draws.indirect=0",
        InitializationWork[1],
        InitializationWork[2],
        InitializationWork[3],
    ];
    // Once every slot has cycled, the slot accumulate writes was last read as shader-read, so it takes the planned
    // transition back to general.
    internal static readonly string[] SteadyWork = [
        "work accumulate executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=1 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=2 push-constants=0 descriptor-writes=2 uploads.host-visible=0 clears=0 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0 sky.hashes=0 sky.texture-loads=0 environment.projections=0 environment.projection-texels=0 environment.skipped=0 shapes.evaluated=0 shapes.gradients=0 vertices=0 draws.indirect=0",
        InitializationWork[1],
        InitializationWork[2],
        InitializationWork[3],
    ];

    // The report's lines, each without its line feed.
    private static string[] Lines(StringBuilder report) =>
        report.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n');
    // The sample's pass and outside lines: its report without the submission line.
    private static string[] WorkLines(GpuWorkSample sample) =>
        Lines(report: GpuWorkReport.AppendSample(builder: new StringBuilder(), sample: sample))[1..];
    private static GpuWorkSample Completed(ShaderPipelineRenderNode node) {
        var sample = new GpuWorkSample();

        Assert.True(condition: node.TryReadCompleted(sample: sample));

        return sample;
    }

    [Fact]
    public void APausedResetCountsEachPassOfTheInitializationSubmissionAndTheStepAfterIt() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);

        node.Paused = true;
        node.Reset();

        // The paused node renders the one initialization frame; its counts publish at the next produced frame, which
        // is held and submits nothing.
        _ = Produce(node: node);
        _ = Produce(node: node);

        var initialization = Completed(node: node);
        var again = Completed(node: node);

        Assert.Equal(
            actual: (initialization.Submission, initialization.Revision, again.Submission),
            expected: ((node.ResetSubmission + 1L), 1L, (node.ResetSubmission + 1L))
        );
        Assert.Equal(
            actual: Lines(report: GpuWorkReport.AppendSample(builder: new StringBuilder(), sample: initialization))[0],
            expected: $"work submission={(node.ResetSubmission + 1L)} revision=1"
        );
        Assert.Equal(
            actual: WorkLines(sample: initialization),
            expected: InitializationWork
        );

        node.Step();
        _ = Produce(node: node);
        _ = Produce(node: node);

        var second = Completed(node: node);

        Assert.Equal(
            actual: second.Submission,
            expected: (node.ResetSubmission + 2L)
        );
        Assert.Equal(
            actual: WorkLines(sample: second),
            expected: SecondWork
        );
    }
    [Fact]
    public void AStepRequestedBeforeTheInitializationFrameAdvancesOneSubmissionBeyondIt() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var sample = new GpuWorkSample();

        node.Paused = true;
        node.Reset();
        node.Step();

        // However many frames the host produces, the paused node renders the initialization frame and then the one step.
        Produce(
            frames: 8,
            node: node
        );
        Assert.True(condition: node.TryReadCompleted(sample: sample));
        Assert.Equal(
            actual: (Counted: (sample.Submission - node.ResetSubmission), Frames: node.FrameCounter, Pending: node.StepRequested),
            expected: (Counted: 2L, Frames: 2UL, Pending: false)
        );
        Assert.Equal(
            actual: WorkLines(sample: sample),
            expected: SecondWork
        );
    }
    [Fact]
    public void ASteadyStateSubmissionCountsTheSameWorkEveryFrameAndReadingItAllocatesNothing() {
        const int MeasuredFrames = 64;
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var sample = Completed(node: node);

        Assert.Equal(
            actual: WorkLines(sample: sample),
            expected: SteadyWork
        );

        var first = sample.Submission;
        var produced = 0;
        var read = 0;
        var allocated = AllocationWindow.Least(window: () => {
            for (var frame = 0; (frame < MeasuredFrames); frame++) {
                _ = node.ProduceFrame(context: default);
                produced++;
                read += (node.TryReadCompleted(sample: sample) ? 1 : 0);
            }
        });

        Assert.Equal(
            actual: (Read: read, Advanced: (sample.Submission - first), AllocatedBytes: allocated),
            expected: (Read: produced, Advanced: ((long)produced), AllocatedBytes: 0L)
        );
        Assert.Equal(
            actual: WorkLines(sample: sample),
            expected: SteadyWork
        );
    }
    [Fact]
    public void AResetWithdrawsTheSampleUntilANewerSubmissionCompletes() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var sample = new GpuWorkSample();
        var before = Completed(node: node).Submission;

        node.Paused = true;
        node.Reset();
        Assert.False(condition: node.TryReadCompleted(sample: sample));
        Assert.Equal(
            actual: sample.Submission,
            expected: 0L
        );

        // The initialization frame is submitted but not yet known complete.
        _ = Produce(node: node);
        Assert.False(condition: node.TryReadCompleted(sample: sample));

        _ = Produce(node: node);
        Assert.True(condition: node.TryReadCompleted(sample: sample));
        Assert.True(condition: (sample.Submission > before));
        Assert.Equal(
            actual: sample.Submission,
            expected: (node.ResetSubmission + 1L)
        );
    }
    [Fact]
    public void AnInstallOrResizeWithdrawsTheSampleAndCountsUnderANewRevision() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var sample = new GpuWorkSample();

        Assert.Equal(
            actual: (Completed(node: node).Revision, node.WorkRevision),
            expected: (1L, 1L)
        );

        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: Feedback(historyFormat: "R32G32B32A32Float")
        );
        Assert.False(condition: node.TryReadCompleted(sample: sample));
        _ = Produce(node: node);
        Assert.Equal(
            actual: Completed(node: node).Revision,
            expected: 2L
        );

        node.Resize(
            height: (Extent * 2),
            width: (Extent * 2)
        );
        _ = node.ProduceBuildStart(gpu: gpu);
        _ = Produce(node: node);
        Assert.False(condition: node.TryReadCompleted(sample: sample));
        _ = Produce(node: node);
        Assert.Equal(
            actual: (Completed(node: node).Revision, node.WorkRevision),
            expected: (3L, 3L)
        );
    }
    [Fact]
    public void TheNodeCountsTheGpuObjectsItCreates() {
        var gpu = new FakePipelineGpu();
        var cache = new GpuPassPipelineCache();
        using var node = InstalledNode(
            gpu: gpu,
            pipelines: cache
        );
        IWorkCounterSource source = node;

        Assert.Equal(
            actual: Lines(report: GpuWorkReport.AppendLifetime(
                builder: new StringBuilder(),
                source: source
            )),
            // Three slots of the history, gray and drawn images, three slots of the frame group's and each pass's constant
            // buffer, the graph's one descriptor pool, and per pass and slot a frame group set and a pass group set. The
            // passes' pipelines and modules are the pass-pipeline cache's, so the node's line counts none of them.
            expected: ["work lifetime: created.pipelines=0 created.shader-modules=0 created.images=9 created.buffers=12 created.descriptor-pools=1 created.descriptor-sets=18 created.timestamp-pools=0"]
        );
        Assert.Equal(
            actual: source.WorkKinds.ToArray(),
            expected: GpuWork.LifetimeKinds.ToArray()
        );
        // The cache counts them instead: per compute pass a pipeline and a module, and two modules and one graphics
        // pipeline for the fullscreen pass.
        Assert.Equal(
            actual: (Created(cache: cache, kind: GpuWork.PipelinesCreated), Created(cache: cache, kind: GpuWork.ShaderModulesCreated)),
            expected: (3L, 4L)
        );
    }
}
