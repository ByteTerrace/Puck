namespace Puck.Physics.Motion;

/// <summary>Identifies one named producer-program argument from the closed vocabulary a kit's <c>producers</c> row
/// resolves to a fixed ordinal at kit-compile time — the same resolved-outside/consumed-as-ordinal seam
/// <c>Puck.Physics.Motion.FixedSpeed.HeldOrdinal</c> and <c>Puck.Physics.Motion.BodyHold.ReleaseOrdinal</c> use for a
/// channel name. <see cref="Press"/> resolves against a kit's declared composition channels, like those two; every
/// other member resolves a fixed-point scalar authored directly on the producer row.</summary>
public enum BodyProducerParameter : byte {
    Forward,
    SoftRadius,
    WeaveAmplitude,
    InwardGain,
    TurnScale,
    WeaveFrequencyBase,
    WeaveFrequencyRange,
    AltitudeGain,
    ActivityRateBase,
    ActivityRateRange,
    StrafeWave,
    TurnWave,
    UpWave,
    PitchWave,
    RollTurn,
    PressThreshold,
    AltitudeBase,
    AltitudeRange,
    StandoffRadius,
    Approach,
    Orbit,
    ReleaseRadius,
    Press,
}
/// <summary>The one table naming which <see cref="BodyProducerParameter"/> ordinals a producer op's compiled
/// behaviour reads, and the string&lt;-&gt;ordinal bridge a kit's authored <c>scalars</c>/<c>channels</c> object
/// keys resolve through. A missing or unknown authored key refuses by name at kit-compile time
/// (<c>BodyMotionProgramRefusal.ParameterMissing</c>/<c>ParameterUnknown</c>) rather than reading a runtime
/// dictionary miss.</summary>
public static class BodyProducerParameterVocabulary {
    // ProduceSteeringIntent's roam shape runs every tick regardless of sensing (see WorldBody.Step.cs), so its own
    // scalars are always required by a producer selecting the op. Its approach shape runs only on a tick this
    // program's SenseNearestInCone found a target, which is possible only when the program selects that op too —
    // s_steeringApproachScalars is required exactly then (see s_steeringApproachScalars below), never on a bare
    // roam producer that can never reach the approach shape.
    private static readonly BodyProducerParameter[] s_steeringScalars = [
        BodyProducerParameter.Forward,
        BodyProducerParameter.SoftRadius,
        BodyProducerParameter.WeaveAmplitude,
        BodyProducerParameter.InwardGain,
        BodyProducerParameter.TurnScale,
        BodyProducerParameter.WeaveFrequencyBase,
        BodyProducerParameter.WeaveFrequencyRange,
        BodyProducerParameter.AltitudeGain,
        BodyProducerParameter.ActivityRateBase,
        BodyProducerParameter.ActivityRateRange,
        BodyProducerParameter.StrafeWave,
        BodyProducerParameter.TurnWave,
        BodyProducerParameter.UpWave,
        BodyProducerParameter.PitchWave,
        BodyProducerParameter.RollTurn,
        BodyProducerParameter.PressThreshold,
        BodyProducerParameter.AltitudeBase,
        BodyProducerParameter.AltitudeRange,
    ];
    // Read only by ProduceSteeringIntent's approach shape, reachable only when the same program also selects
    // SenseNearestInCone — required then (CompiledBodyProducer.Compile, WorldDefinitionValidator.Motion.cs), never
    // on a producer that can only ever roam.
    private static readonly BodyProducerParameter[] s_steeringApproachScalars = [
        BodyProducerParameter.StandoffRadius,
        BodyProducerParameter.Approach,
        BodyProducerParameter.Orbit,
    ];
    private static readonly BodyProducerParameter[] s_faceScalars = [
        BodyProducerParameter.InwardGain,
        BodyProducerParameter.TurnScale,
    ];
    private static readonly BodyProducerParameter[] s_none = [];

    /// <summary>Gets the scalar ordinals <see cref="BodyMotionOp.ProduceSteeringIntent"/>'s roam shape reads —
    /// required of every producer selecting the op, since that shape runs every tick regardless of sensing.</summary>
    public static IReadOnlyList<BodyProducerParameter> SteeringScalars => s_steeringScalars;
    /// <summary>Gets the scalar ordinals <see cref="BodyMotionOp.ProduceSteeringIntent"/>'s approach shape reads —
    /// required only of a producer whose program also selects <see cref="BodyMotionOp.SenseNearestInCone"/>, the
    /// one way that shape becomes reachable.</summary>
    public static IReadOnlyList<BodyProducerParameter> SteeringApproachScalars => s_steeringApproachScalars;
    /// <summary>Gets the scalar ordinals <see cref="BodyMotionOp.FaceSensorTarget"/> reads.</summary>
    public static IReadOnlyList<BodyProducerParameter> FaceScalars => s_faceScalars;

    /// <summary>Returns the scalar ordinals a producer op's compiled behaviour reads unconditionally, or an empty
    /// list for an op reading none by name. <see cref="BodyMotionOp.ProduceSteeringIntent"/>'s approach-only
    /// scalars (<see cref="SteeringApproachScalars"/>) and <see cref="BodyMotionOp.SenseNearestInCone"/>'s own
    /// <see cref="BodyProducerParameter.ReleaseRadius"/> are both conditional on the program's full op set rather
    /// than any single op, and are not part of this table — see <c>CompiledBodyProducer.Compile</c>.</summary>
    public static IReadOnlyList<BodyProducerParameter> RequiredScalars(BodyMotionOp op) => op switch {
        BodyMotionOp.ProduceSteeringIntent => s_steeringScalars,
        BodyMotionOp.FaceSensorTarget => s_faceScalars,
        _ => s_none,
    };
    /// <summary>Returns the authored key a parameter resolves from — its declared name with a lowercase first
    /// character, the same casing every other document field on this wire uses.</summary>
    public static string Name(BodyProducerParameter parameter) {
        var declared = parameter.ToString();

        return $"{char.ToLowerInvariant(c: declared[0])}{declared.AsSpan(start: 1)}";
    }
    /// <summary>Resolves an authored key to its parameter ordinal.</summary>
    /// <returns><see langword="true"/> when <paramref name="name"/> names a declared parameter.</returns>
    public static bool TryParse(string name, out BodyProducerParameter parameter) {
        foreach (var candidate in Enum.GetValues<BodyProducerParameter>()) {
            if (string.Equals(
                a: Name(parameter: candidate),
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                parameter = candidate;

                return true;
            }
        }

        parameter = default;

        return false;
    }
}
