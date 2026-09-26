using System.Text.Json;

namespace Puck.Shaders.Tests;

/// <summary>
/// Seeding laws for <see cref="ShaderPipelineRenderNode"/>: what a pass uploads never depends on how many frames ran before.
/// Every slot holds each pass's current block after an install, the live config a replaced graph carries included, and
/// again after a reset, so an unchanged block uploads nothing whichever slot a frame lands on.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    private static readonly IReadOnlyDictionary<string, ShaderConfigField> GainConfig = new Dictionary<string, ShaderConfigField>(comparer: StringComparer.Ordinal) {
        ["gain"] = new(
            Default: JsonDocument.Parse(json: "1").RootElement,
            Type: ShaderValueType.Float
        ),
    };

    // Sets the convert pass's gain live.
    private static void SetGain(ShaderPipelineRenderNode node, float gain) {
        using var config = JsonDocument.Parse(json: $"{{\"gain\":{gain}}}");

        Assert.True(condition: node.TrySetConfig(
            config: config.RootElement,
            passName: "convert",
            reason: out var reason
        ), userMessage: reason);
    }
    // The convert pass's line of the submission a paused node renders on its next step, read once the held frame after it
    // publishes it.
    private static string StepConvertLine(ShaderPipelineRenderNode node) {
        node.Step();
        _ = Produce(node: node);
        _ = Produce(node: node);

        return Assert.Single(
            collection: WorkLines(sample: Completed(node: node)),
            predicate: static line => line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "work convert "
            )
        );
    }

    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [Theory]
    public void TheFirstFrameAfterAResetUploadsNoUnchangedPassBlockHoweverManyFramesRanSinceItsConfigChanged(int framesBeforeConfig, int framesBeforeReset) {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: Feedback(convertConfig: GainConfig)
        );

        // Which slots the changed block reaches before the reset follows from the frames on either side of the change.
        Produce(
            frames: framesBeforeConfig,
            node: node
        );
        SetGain(
            gain: 2f,
            node: node
        );
        Produce(
            frames: framesBeforeReset,
            node: node
        );
        node.Paused = true;
        node.Reset();
        _ = Produce(node: node);
        _ = Produce(node: node);

        Assert.Contains(
            actualString: Assert.Single(
                collection: WorkLines(sample: Completed(node: node)),
                predicate: static line => line.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "work convert "
                )
            ),
            expectedSubstring: " uploads.host-visible=0 "
        );
    }
    [Fact]
    public void ARebuildAfterADeviceLossCountsNoneOfItsSetupInTheNextSubmission() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);

        node.OnDeviceLost();
        Assert.True(condition: node.ProduceBuildStart(gpu: gpu).IsEmpty);
        _ = Produce(node: node);
        _ = Produce(node: node);

        // The rebuilt graph's sets and seeded blocks were written before its first submission, which sends only the frame
        // group's region outside every pass, as the first submission after an install or a reset does.
        Assert.Equal(
            actual: WorkLines(sample: Completed(node: node))[^1],
            expected: InitializationWork[^1]
        );
    }
    [Fact]
    public void AReplacedGraphsFirstFramesUploadNoneOfTheLiveConfigItCarries() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: Feedback(convertConfig: GainConfig)
        );

        SetGain(
            gain: 2f,
            node: node
        );
        Produce(
            frames: WarmFrames,
            node: node
        );
        Assert.True(condition: node.TryGetConfigSnapshot(
            bytes: out var live,
            passName: "convert"
        ));
        node.Swap(pipeline: Feedback(
            convertConfig: GainConfig,
            historyFormat: "R32G32B32A32Float"
        ));
        _ = node.ProduceBuildStart(gpu: gpu);
        node.Paused = true;
        // The paused frame installs the replacement and renders nothing.
        _ = Produce(node: node);
        Assert.Null(@object: node.LastSwapError);
        Assert.True(condition: node.TryGetConfigSnapshot(
            bytes: out var carried,
            passName: "convert"
        ));
        Assert.Equal(
            actual: carried,
            expected: live
        );

        for (var submission = 0; (submission < InFlight); submission++) {
            Assert.Contains(
                actualString: StepConvertLine(node: node),
                expectedSubstring: " uploads.host-visible=0 "
            );
        }
    }
}
