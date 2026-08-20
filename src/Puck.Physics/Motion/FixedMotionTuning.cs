using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.Physics.Motion;

/// <summary>Which frame a grounded body's <c>MoveAdvance</c>/<c>MoveStrafe</c> channels resolve in — a per-kit choice,
/// never a global switch.</summary>
[JsonConverter(typeof(StrictEnumConverter<MotionMoveFrame>))]
public enum MotionMoveFrame : byte {
    /// <summary>Body-relative: the commanded planar target rotates by the body's own integrated heading (tank
    /// controls).</summary>
    Heading,

    /// <summary>World-relative: the two channels are read as already-resolved world axes. The seat composes its
    /// camera yaw into the submitted intent client-side, before submission — the sim itself never sees a camera pose,
    /// preserving determinism.</summary>
    World,
}
/// <summary>The one-time fixed-point compilation of a world's motion defaults. Runtime simulation reads only this
/// form.</summary>
public readonly record struct FixedMotionDefaults(FixedQ4816 MoveSpeed, FixedQ4816 TurnSpeed, FixedQ4816 MaxSmoothError);
/// <summary>The flattened, fixed-point form of one velocity-response row: the conjunction gate (body-fact predicates
/// only), and the engage/release convergence rates the ramp integrates through the shared rate accumulator.</summary>
public readonly record struct FixedMotionResponse(CompiledPredicate[] Gate, FixedQ4816 EngageRate, FixedQ4816 ReleaseRate);
/// <summary>The compiled fixed-point form of an authored motion scalar envelope — the reusable seat-time clamp bound
/// every overridable motion-arm scalar shares. Authoring validation has already refused <see cref="Max"/> &lt;
/// <see cref="Min"/> by the time this compiles, so <see cref="Clamp"/> never faults.</summary>
public readonly record struct FixedMotionScalarEnvelope(FixedQ4816 Min, FixedQ4816 Max) {
    /// <summary>Restricts <paramref name="value"/> to this envelope's inclusive bound.</summary>
    public FixedQ4816 Clamp(FixedQ4816 value) => FixedQ4816.Clamp(
        value: value,
        minimum: Min,
        maximum: Max
    );
}
/// <summary>The one-time fixed-point compilation of an authored grounded motion row. Runtime simulation reads only
/// this form.</summary>
/// <remarks><see cref="Response"/> is simulation-affecting: it promotes the slice of the tuning the planar shaping
/// stage reads. <see cref="ResponseRecencyFacts"/>/<see cref="ResponseRecencyWindows"/> are the shared recency-clock
/// table across every row's <c>recently</c> gate, and the per-tick clock updater walks it.</remarks>
public readonly record struct FixedMotionTuning(
    FixedQ4816 MoveSpeed,
    FixedQ4816 TurnSpeed,
    FixedQ4816 RiseGravity,
    FixedQ4816 FallGravity,
    FixedQ4816 MaxFallSpeed,
    FixedMotionResponse[] Response,
    ActionFact[] ResponseRecencyFacts,
    ulong[] ResponseRecencyWindows,
    FixedQ4816 SprintMultiplier,
    MotionMoveFrame MoveFrame,
    bool FacingSnap,
    FixedMotionScalarEnvelope? MoveSpeedEnvelope
) {
    /// <summary>Gets the number of recency clocks the response table's recency gates share.</summary>
    public int RecencySlots => ResponseRecencyFacts.Length;
}
/// <summary>The one-time fixed-point compilation of an authored vehicle motion row. Runtime simulation reads only
/// this form; the held drift/boost channel names resolve to ordinals separately, through the world's channel
/// table.</summary>
public readonly record struct FixedVehicleTuning(
    FixedQ4816 TopSpeed,
    FixedQ4816 ReverseTopSpeed,
    FixedQ4816 Accel,
    FixedQ4816 Brake,
    FixedQ4816 CoastDrag,
    FixedQ4816 Grip,
    FixedQ4816 SteerRate,
    FixedQ4816 SteerReferenceSpeed,
    FixedQ4816 SteerFalloff,
    FixedQ4816 PitchRate,
    FixedQ4816 DriftGrip,
    FixedQ4816 DriftSteerScale,
    FixedQ4816 BoostMultiplier,
    FixedMotionScalarEnvelope? TopSpeedEnvelope
);
/// <summary>The one-time fixed-point compilation of an authored swim motion row's swim-specific half. The shared half
/// (speeds, response table, sprint, frame) compiles into the same <see cref="FixedMotionTuning"/> every model rides,
/// so the generic stages never dispatch on the model; only the swim operations read this record.</summary>
/// <param name="VerticalThrustFraction">The vertical channel's fraction of the thrust speed.</param>
/// <param name="Buoyancy">The medium's idle vertical drift velocity below the bob band, signed (u/s).</param>
/// <param name="MaxRiseSpeed">The terminal ascent speed (u/s).</param>
/// <param name="MaxSinkSpeed">The terminal descent speed (u/s).</param>
/// <param name="SurfaceSettleRate">The surface interface's proportional settle gain toward the float line (1/s).</param>
/// <param name="FloatDepth">The float line's depth below the waterline, and the bob band's half-width (u).</param>
public readonly record struct FixedSwimTuning(
    FixedQ4816 VerticalThrustFraction,
    FixedQ4816 Buoyancy,
    FixedQ4816 MaxRiseSpeed,
    FixedQ4816 MaxSinkSpeed,
    FixedQ4816 SurfaceSettleRate,
    FixedQ4816 FloatDepth
);
