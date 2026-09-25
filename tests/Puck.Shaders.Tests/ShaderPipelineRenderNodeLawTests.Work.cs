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
    // layout. Every pass pushes its 96-byte frame block.
    internal static readonly string[] InitializationWork = [
        "work accumulate executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=3 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=1 push-constants=96 descriptor-writes=2 uploads.host-visible=0 clears=1",
        "work convert executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=2 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=1 push-constants=96 descriptor-writes=2 uploads.host-visible=0 clears=0",
        "work copy executed: dispatches=0 dispatches.indirect=0 draws=1 render-passes=1 command-buffers=2 barriers.image=2 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=1 push-constants=96 descriptor-writes=1 uploads.host-visible=0 clears=0",
        "work outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=1 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=0 clears=0",
    ];
    // The next submission: nothing is cleared, the previous history is already shader-read, and the slot written next
    // was discarded by the reset, so accumulate transitions it to general.
    internal static readonly string[] SecondWork = [
        "work accumulate executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=1 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=1 push-constants=96 descriptor-writes=2 uploads.host-visible=0 clears=0",
        InitializationWork[1],
        InitializationWork[2],
        InitializationWork[3],
    ];
    // Once every slot has cycled, the slot accumulate writes was last read as shader-read, so it takes the planned
    // transition back to general.
    internal static readonly string[] SteadyWork = [
        "work accumulate executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=1 barriers.memory=0 barriers.buffer=0 binds.pipeline=1 binds.descriptor-set=1 push-constants=96 descriptor-writes=2 uploads.host-visible=0 clears=0",
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
    // A canary fixture document planned as the World plans it, with stand-in bytecode for each pass.
    private static CompiledShaderPipeline CanaryPipeline(string canary, string fileName) {
        var definition = ShaderPipelineLoader.ReadDefinition(
            name: "feedback",
            path: RepositoryPaths.Resolve(relativePath: $"tests/Puck.World.Canaries/{canary}/{fileName}")
        );
        var plan = new ShaderPipelineCompiler().Compile(definition: definition);

        return new CompiledShaderPipeline(
            plan: plan,
            shaders: plan.Passes.ToDictionary(
                elementSelector: static pass => Shader(
                    kind: pass.Declaration!.Kind,
                    name: pass.Name
                ),
                keySelector: static pass => pass.Name
            )
        );
    }
    // The pipeline-counters script on the fake GPU: each counted wait produces frames until the sample it waits for is
    // published, each inspection answers with the node's own inspection record, the one pipeline.inspect prints, and the status
    // adds the lifetime line as pipeline.status indents it.
    private static List<string> CountersScriptLines(CompiledShaderPipeline pipeline) {
        var gpu = new FakePipelineGpu();
        using var node = new ShaderPipelineRenderNode(
            deviceContext: gpu,
            height: 64,
            hostsOnDirectX: false,
            inFlightFrames: InFlight,
            name: "feedback",
            width: 64
        );
        var lines = new List<string>();
        var sample = new GpuWorkSample();
        IWorkCounterSource counters = node;

        void WaitCounted(long submissions) {
            for (var frame = 0; (frame < 8); frame++) {
                if (node.TryReadCompleted(sample: sample) && ((sample.Submission - node.ResetSubmission) >= submissions)) {
                    return;
                }

                _ = Produce(node: node);
            }

            Assert.Fail(message: $"counted {submissions} was never reached");
        }
        // A console answer reaches stdout as one framed record: its first line at column zero and every further line
        // indented, which is how the canary's exact lines read.
        void Answer(StringBuilder record) {
            var framed = new StringWriter();

            Puck.Commands.ConsoleRecord.Write(
                record: record.ToString().TrimEnd(trimChar: '\n'),
                writer: framed
            );
            lines.AddRange(collection: framed.ToString().Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: ['\r', '\n']
            ));
        }
        void Inspect() {
            var record = new StringBuilder();

            Assert.True(condition: node.TryAppendInspection(
                builder: record,
                name: "feedback"
            ));
            Answer(record: record);
        }

        node.Swap(pipeline: pipeline);
        _ = node.ProduceUntilInstalled();
        Produce(
            frames: (WarmFrames - 1),
            node: node
        );
        node.Paused = true;
        node.Reset();
        WaitCounted(submissions: 1L);
        Inspect();
        Inspect();
        // The status's one row ends the response, so its lifetime line carries the closing bracket.
        Answer(record: new StringBuilder(value: $"[pipeline.status:\n    {Lines(report: GpuWorkReport.AppendLifetime(builder: new StringBuilder(), source: counters))[0]}]"));
        node.Step();
        WaitCounted(submissions: 2L);
        Inspect();
        node.Reset();
        WaitCounted(submissions: 1L);
        Inspect();

        return lines;
    }
    // The canary leg's exact stdout line expectations, as (name, text, present).
    private static IEnumerable<(string Name, string Text, bool Present)> ExactStdoutLines(string leg) {
        using var manifest = System.Text.Json.JsonDocument.Parse(json: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "tests/Puck.World.Canaries/pipeline-counters/canary.json")));
        var expectations = new List<(string, string, bool)>();

        foreach (var expectation in manifest.RootElement.GetProperty(propertyName: leg).GetProperty(propertyName: "expect").EnumerateArray()) {
            if (
                (expectation.GetProperty(propertyName: "type").GetString() == "line") &&
                (expectation.GetProperty(propertyName: "stream").GetString() == "stdout") &&
                (expectation.GetProperty(propertyName: "match").GetString() == "exact")
            ) {
                expectations.Add(item: (
                    expectation.GetProperty(propertyName: "name").GetString()!,
                    expectation.GetProperty(propertyName: "text").GetString()!,
                    expectation.GetProperty(propertyName: "present").GetBoolean()
                ));
            }
        }

        return expectations;
    }
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
    public void ThePipelineCountersCanaryExpectsWhatTheNodeCountsForItsFixtures() {
        var positive = CountersScriptLines(pipeline: CanaryPipeline(
            canary: "pipeline-feedback",
            fileName: "feedback.graph.json"
        ));
        var fourPass = CountersScriptLines(pipeline: CanaryPipeline(
            canary: "pipeline-counters",
            fileName: "four-pass.graph.json"
        ));

        // Each leg's own exact lines hold on its own fixture, and the positive's turn red on the four-pass variant.
        Assert.All(
            action: expectation => Assert.True(
                condition: (positive.Contains(item: expectation.Text) == expectation.Present),
                userMessage: expectation.Name
            ),
            collection: ExactStdoutLines(leg: "positive")
        );
        Assert.All(
            action: expectation => Assert.True(
                condition: (fourPass.Contains(item: expectation.Text) == expectation.Present),
                userMessage: expectation.Name
            ),
            collection: ExactStdoutLines(leg: "discriminating")
        );
        Assert.Contains(
            collection: ExactStdoutLines(leg: "positive"),
            filter: expectation => (fourPass.Contains(item: expectation.Text) != expectation.Present)
        );

        // The residency line names the parameter region the layout computes, under every residency policy.
        using (var manifest = System.Text.Json.JsonDocument.Parse(json: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "tests/Puck.World.Canaries/pipeline-counters/canary.json")))) {
            var residency = manifest.RootElement.GetProperty(propertyName: "positive").GetProperty(propertyName: "expect").EnumerateArray()
                .Single(predicate: static expectation => (expectation.GetProperty(propertyName: "name").GetString() == "inspect-echoes-the-policy-for-the-parameter-region"));

            Assert.Equal(
                actual: residency.GetProperty(propertyName: "text").GetString(),
                expected: $"{Puck.Commands.ConsoleRecord.ContinuationIndent}residency: parameters={CanaryPipeline(canary: "pipeline-feedback", fileName: "feedback.graph.json").Plan.ParameterBytes} bytes policy="
            );
        }
        // The canary's per-pass lines are continuation lines of the inspect answer, indented by its framing.
        Assert.Equal(
            actual: ExactStdoutLines(leg: "positive").Where(predicate: static expectation => (expectation.Text.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: $"{Puck.Commands.ConsoleRecord.ContinuationIndent}work "
            ) && !expectation.Text.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: $"{Puck.Commands.ConsoleRecord.ContinuationIndent}work lifetime:"
            ))).Select(selector: static expectation => expectation.Text[Puck.Commands.ConsoleRecord.ContinuationIndent.Length..]).Order(comparer: StringComparer.Ordinal),
            expected: InitializationWork.Append(element: SecondWork[0]).Order(comparer: StringComparer.Ordinal)
        );
    }
    [Fact]
    public void TheNodeCountsTheGpuObjectsItCreates() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        IWorkCounterSource source = node;

        Assert.Equal(
            actual: Lines(report: GpuWorkReport.AppendLifetime(
                builder: new StringBuilder(),
                source: source
            )),
            // Per compute pass a pipeline and a module, two modules and one graphics pipeline for the fullscreen pass, three
            // slots of the history, gray and drawn images, and a descriptor pool and set per pass and slot.
            expected: ["work lifetime: created.pipelines=3 created.shader-modules=4 created.images=9 created.buffers=0 created.descriptor-pools=9 created.descriptor-sets=9"]
        );
        Assert.Equal(
            actual: source.WorkKinds.ToArray(),
            expected: GpuWork.LifetimeKinds.ToArray()
        );
    }
}
