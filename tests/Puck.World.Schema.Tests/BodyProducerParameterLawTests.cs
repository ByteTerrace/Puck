using Xunit;

using Puck.Physics.Motion;

namespace Puck.World.Schema.Tests;

/// <summary>Pins the producer-parameter compile boundary: an authored <c>scalars</c> key resolves to a fixed
/// <see cref="BodyProducerParameter"/> ordinal once, at kit-compile time, and a missing or unknown key refuses by
/// name rather than reading a runtime dictionary miss. Also pins that the approach-only scalars
/// (<c>standoffRadius</c>/<c>approach</c>/<c>orbit</c>) are required exactly when the program also senses
/// (<see cref="BodyMotionOp.SenseNearestInCone"/>) — a bare roam program can never reach
/// <c>ProduceSteeringIntent</c>'s approach shape, so authoring them there refuses as unknown.</summary>
public sealed class BodyProducerParameterLawTests {
    // Every ordinal ProduceSteeringIntent's roam shape reads — required of every producer selecting the op,
    // regardless of sensing (the roam shape runs every tick; see WorldBody.Step.cs).
    private static Dictionary<string, float> CompleteRoamScalars() => new() {
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
    };
    // The additional ordinals ProduceSteeringIntent's approach shape reads — required only alongside
    // SenseNearestInCone, the one way that shape becomes reachable.
    private static Dictionary<string, float> CompleteApproachScalars() {
        var scalars = CompleteRoamScalars();

        scalars["standoffRadius"] = 1f;
        scalars["approach"] = 0f;
        scalars["orbit"] = 0f;

        return scalars;
    }
    private static CompiledBodyMotionProgram RoamOnlyProgram() => CompiledBodyMotionProgram.Compile(
        name: "roam",
        version: CompiledBodyMotionProgram.SupportedVersion,
        kind: BodyProgramKind.Producer,
        operations: [BodyMotionOp.ProduceSteeringIntent]
    );
    private static CompiledBodyMotionProgram SensingProgram() => CompiledBodyMotionProgram.Compile(
        name: "stalk",
        version: CompiledBodyMotionProgram.SupportedVersion,
        kind: BodyProgramKind.Producer,
        operations: [BodyMotionOp.SenseNearestInCone, BodyMotionOp.ProduceSteeringIntent]
    );
    private static CompiledBodyProducer Compile(CompiledBodyMotionProgram program, Dictionary<string, float> scalars) => CompiledBodyProducer.Compile(
        program: program,
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
    private static CompiledBodyProducer CompileRoam(Dictionary<string, float> scalars) => CompiledBodyProducer.Compile(
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
    public void TheCompleteRoamSetCompiles() {
        var producer = CompileRoam(scalars: CompleteRoamScalars());

        Assert.Equal(
            expected: 0.5f,
            actual: (double)producer.Scalar(BodyProducerParameter.Forward)
        );
    }
    [Fact]
    public void AMissingRequiredParameterRefusesByName() {
        var incomplete = CompleteRoamScalars();

        incomplete.Remove(key: "weaveAmplitude");

        var exception = Assert.Throws<BodyMotionProgramException>(testCode: () => CompileRoam(scalars: incomplete));

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
        var extra = CompleteRoamScalars();

        extra["notAnInstructionParameter"] = 1f;

        var exception = Assert.Throws<BodyMotionProgramException>(testCode: () => CompileRoam(scalars: extra));

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
    [Fact]
    public void ABareRoamProgramRefusesTheApproachOnlyScalars() {
        // Control for the derivation: a program with no SenseNearestInCone can never reach the approach shape, so
        // authoring its scalars there is refused as unknown rather than silently accepted as dead data.
        var exception = Assert.Throws<BodyMotionProgramException>(testCode: () => CompileRoam(scalars: CompleteApproachScalars()));

        Assert.Equal(
            expected: BodyMotionProgramRefusal.ParameterUnknown,
            actual: exception.Refusal
        );
        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "standoffRadius",
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void ASensingProgramCompilesWithTheApproachScalarsAuthored() {
        var producer = Compile(program: SensingProgram(), scalars: CompleteApproachScalars());

        Assert.Equal(
            expected: 1f,
            actual: (double)producer.Scalar(BodyProducerParameter.StandoffRadius)
        );
    }
    [Fact]
    public void ASensingProgramRefusesAMissingApproachOnlyScalarByName() {
        var incomplete = CompleteApproachScalars();

        incomplete.Remove(key: "standoffRadius");

        var exception = Assert.Throws<BodyMotionProgramException>(testCode: () => Compile(program: SensingProgram(), scalars: incomplete));

        Assert.Equal(
            expected: BodyMotionProgramRefusal.ParameterMissing,
            actual: exception.Refusal
        );
        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "standoffRadius",
            comparisonType: StringComparison.Ordinal
        );
    }
}
