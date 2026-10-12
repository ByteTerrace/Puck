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
            pipelines: new GpuPassPipelineCache(),
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
}
