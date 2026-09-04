using Xunit;

using Puck.Physics.Motion;

namespace Puck.World.Schema.Tests;

/// <summary>Pins the producer-parameter compile boundary: an authored <c>scalars</c> key resolves to a fixed
/// <see cref="BodyProducerParameter"/> ordinal once, at kit-compile time, and a missing or unknown key refuses by
/// name rather than reading a runtime dictionary miss.</summary>
public sealed class BodyProducerParameterLawTests {
    // Every ordinal ProduceSteeringIntent's compiled behaviour reads, in either of its runtime shapes — the
    // complete, valid authoring every discriminating case below removes exactly one entry from.
    private static Dictionary<string, float> CompleteSteeringScalars() => new() {
        ["forward"] = 0.5f,
        ["softRadius"] = 4f,
        ["weaveAmplitude"] = 0.3f,
        ["inwardGain"] = 1f,
        ["turnScale"] = 2f,
        ["weaveFrequencyBase"] = 0.4f,
        ["weaveFrequencyRange"] = 0.1f,
        ["altitudeGain"] = 0.5f,
        ["activityRateBase"] = 1f,
        ["activityRateRange"] = 0.2f,
        ["strafeWave"] = 0f,
        ["turnWave"] = 0f,
        ["upWave"] = 0f,
        ["pitchWave"] = 0f,
        ["rollTurn"] = 0f,
        ["pressThreshold"] = 0f,
        ["altitudeBase"] = 0f,
        ["altitudeRange"] = 0f,
        ["standoffRadius"] = 1f,
        ["approach"] = 0f,
        ["orbit"] = 0f,
    };
    private static CompiledBodyMotionProgram RoamOnlyProgram() => CompiledBodyMotionProgram.Compile(
        name: "roam",
        version: CompiledBodyMotionProgram.SupportedVersion,
        kind: BodyProgramKind.Producer,
        operations: [BodyMotionOp.ProduceSteeringIntent]
    );
    private static CompiledBodyProducer Compile(Dictionary<string, float> scalars) => CompiledBodyProducer.Compile(
        program: RoamOnlyProgram(),
        source: null,
        parameters: new BodyProgramParameters(
            Scalars: scalars,
            Channels: new Dictionary<string, string>()
        ),
        channels: WorldChannelTable.Compile(channels: []),
        targets: WorldTargetRegisterTable.Empty,
        curves: WorldCurveTable.Empty,
        navigation: WorldNavigationDomainTable.Empty,
        simulationRateHz: 240
    );

    [Fact]
    public void TheCompleteAuthoredSetCompiles() {
        var producer = Compile(scalars: CompleteSteeringScalars());

        Assert.Equal(
            expected: 0.5f,
            actual: (double)producer.Scalar(BodyProducerParameter.Forward)
        );
    }
    [Fact]
    public void AMissingRequiredParameterRefusesByName() {
        var incomplete = CompleteSteeringScalars();

        incomplete.Remove(key: "weaveAmplitude");

        var exception = Assert.Throws<BodyMotionProgramException>(testCode: () => Compile(scalars: incomplete));

        Assert.Equal(
            expected: BodyMotionProgramRefusal.ParameterMissing,
            actual: exception.Refusal
        );
        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "weaveAmplitude",
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void AnUnknownParameterRefusesByName() {
        var extra = CompleteSteeringScalars();

        extra["notAnInstructionParameter"] = 1f;

        var exception = Assert.Throws<BodyMotionProgramException>(testCode: () => Compile(scalars: extra));

        Assert.Equal(
            expected: BodyMotionProgramRefusal.ParameterUnknown,
            actual: exception.Refusal
        );
        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "notAnInstructionParameter",
            comparisonType: StringComparison.Ordinal
        );
    }
}
