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
    ApproachAltitudeGain,
}
/// <summary>The one table naming which <see cref="BodyProducerParameter"/> ordinals a producer op's compiled
/// behaviour reads, and the string&lt;-&gt;ordinal bridge a kit's authored <c>scalars</c>/<c>channels</c> object
/// keys resolve through. A missing or unknown authored key refuses by name at kit-compile time
/// (<c>BodyMotionProgramRefusal.ParameterMissing</c>/<c>ParameterUnknown</c>) rather than reading a runtime
/// dictionary miss.</summary>
public static class BodyProducerParameterVocabulary {
    // ProduceSteeringIntent's roam shape is authored vocabulary, not a structural consequence of selecting the op:
    // it runs only when the producer authors at least one roam-exclusive scalar (CompiledBodyProducer.Compile
    // derives the shape's activity from that presence, the same way s_steeringApproachScalars below derives its own
    // from sensing), so a sensing producer that never wants a roam fallback omits the whole set rather than
    // authoring 18 neutered values to suppress it; a non-sensing producer requires them because roam is its only
    // reachable shape. InwardGain/TurnScale cannot be presence markers because FaceSensorTarget also
    // owns them; once a roam-exclusive scalar is present, all 18 are required (WorldDefinitionValidator.Motion.cs).
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
    // on a producer that can only ever roam. ApproachAltitudeGain is the approach shape's OWN altitude gain,
    // distinct from s_steeringScalars' AltitudeGain (the roam shape's) — the two terms are independently authorable
    // rather than sharing one scalar an author cannot zero for one shape without zeroing the other's tracking too.
    private static readonly BodyProducerParameter[] s_steeringApproachScalars = [
        BodyProducerParameter.StandoffRadius,
        BodyProducerParameter.Approach,
        BodyProducerParameter.Orbit,
        BodyProducerParameter.ApproachAltitudeGain,
    ];
    private static readonly BodyProducerParameter[] s_faceScalars = [
        BodyProducerParameter.InwardGain,
        BodyProducerParameter.TurnScale,
    ];
    // s_steeringScalars minus InwardGain/TurnScale — the two FaceSensorTarget also requires unconditionally
    // (s_faceScalars). Presence of an authored InwardGain/TurnScale is explained by FaceSensorTarget alone, so it
    // must never by itself read as "this producer wants roam"; this is the set IsRoamAuthored tests instead.
    private static readonly BodyProducerParameter[] s_steeringExclusiveScalars = [
        BodyProducerParameter.Forward,
        BodyProducerParameter.SoftRadius,
        BodyProducerParameter.WeaveAmplitude,
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
    private static readonly BodyProducerParameter[] s_none = [];

    /// <summary>Gets the scalar ordinals <see cref="BodyMotionOp.ProduceSteeringIntent"/>'s roam shape reads —
    /// required by every non-sensing producer selecting the op, and by a sensing producer exactly when it authors a
    /// roam-exclusive member.</summary>
    public static IReadOnlyList<BodyProducerParameter> SteeringScalars => s_steeringScalars;
    /// <summary>Reports whether a producer's authored scalars name a roam scalar not also shared with
    /// <see cref="BodyMotionOp.FaceSensorTarget"/> — the one presence test both <c>CompiledBodyProducer.Compile</c>
    /// and the schema validator read to decide whether <see cref="SteeringScalars"/> is required and the roam shape
    /// runs, so an approach-only producer authoring InwardGain/TurnScale for FaceSensorTarget alone is never
    /// misread as wanting roam too.</summary>
    public static bool IsRoamAuthored(IReadOnlyDictionary<string, float> scalars) => s_steeringExclusiveScalars.Any(predicate: parameter => scalars.ContainsKey(key: Name(parameter: parameter)));
    /// <summary>Gets the scalar ordinals <see cref="BodyMotionOp.ProduceSteeringIntent"/>'s approach shape reads —
    /// required only of a producer whose program also selects <see cref="BodyMotionOp.SenseNearestInCone"/>, the
    /// one way that shape becomes reachable.</summary>
    public static IReadOnlyList<BodyProducerParameter> SteeringApproachScalars => s_steeringApproachScalars;
    /// <summary>Gets the scalar ordinals <see cref="BodyMotionOp.FaceSensorTarget"/> reads.</summary>
    public static IReadOnlyList<BodyProducerParameter> FaceScalars => s_faceScalars;

    /// <summary>Returns the scalar ordinals a producer op's compiled behaviour reads unconditionally, or an empty
    /// list for an op reading none by name, or one whose requirement depends on more than the op alone.
    /// <see cref="BodyMotionOp.ProduceSteeringIntent"/>'s own roam scalars (<see cref="SteeringScalars"/>, required
    /// always for a non-sensing producer and when a sensing producer authors a roam-exclusive member), its
    /// approach-only scalars
    /// (<see cref="SteeringApproachScalars"/>, required exactly when the program also senses), and
    /// <see cref="BodyMotionOp.SenseNearestInCone"/>'s own <see cref="BodyProducerParameter.ReleaseRadius"/> are all
    /// conditional rather than a fixed per-op set, and are not part of this table — see
    /// <c>CompiledBodyProducer.Compile</c>.</summary>
    public static IReadOnlyList<BodyProducerParameter> RequiredScalars(BodyMotionOp op) => op switch {
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
