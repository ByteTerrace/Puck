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
/// <summary>The one-time fixed-point compilation of a kit's <c>dynamics</c>-row planar shaping — the second-order
/// follower alternative to <see cref="FixedMotionResponse"/>'s response table, bound to the world's own simulation
/// step width. <see cref="Planar"/> steps the three-lane planar follower <c>ShapePlanarVelocity</c> reads and, for a
/// kit authoring a medium hold, the same compiled step also drives the one-dimensional vertical lane that hold's law
/// reads (one authored row, one compiled propagator, two lane counts) — never a second compile.</summary>
public readonly record struct FixedMotionDynamics(SecondOrderStep Planar);
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
    FixedMotionScalarEnvelope? MoveSpeedEnvelope,
    FixedMotionDynamics? PlanarDynamics = null,
    FixedBodyDrive? Drive = null
) {
    /// <summary>Gets the number of recency clocks the response table's recency gates share.</summary>
    public int RecencySlots => ResponseRecencyFacts.Length;
}
/// <summary>The one-time fixed-point compilation of a kit's authored <c>drive</c> row — the anisotropic
/// body-frame drive <see cref="BodyMotionOp.ResolveDriveFrame"/>/<see cref="BodyMotionOp.ShapeDriveVelocity"/>
/// read, beside the planar shaping every other operation reads. The kit's own
/// <see cref="FixedMotionTuning.MoveSpeed"/> is the forward target and <see cref="FixedMotionTuning.TurnSpeed"/> the
/// steering rate: this row carries only what a drive alone has. The held drift channel name resolves to an ordinal
/// separately, through the world's channel table.</summary>
/// <param name="ReverseSpeed">The reverse speed (u/s) full back-throttle converges on from rest; zero forbids
/// reversing.</param>
/// <param name="Accel">The longitudinal convergence rate (u/s²) while throttle commands more speed.</param>
/// <param name="Brake">The longitudinal convergence rate (u/s²) while back-throttle opposes forward travel.</param>
/// <param name="Coast">The longitudinal convergence rate (u/s²) toward rest with throttle centered, and the decay
/// rate while over the commanded speed.</param>
/// <param name="Grip">The lateral convergence rate (u/s²) toward zero slip.</param>
/// <param name="SteerReferenceSpeed">The longitudinal speed (u/s) at which steering authority peaks.</param>
/// <param name="SteerFalloff">The fraction of full steering authority remaining at the resolved move speed, in
/// <c>[0, 1]</c>.</param>
/// <param name="PitchRate">The pitch rate (rad/s) the Pitch channel commands; zero locks the frame planar.</param>
/// <param name="DriftGrip">The lateral convergence rate (u/s²) replacing <paramref name="Grip"/> while the drift
/// channel reads held; zero without a drift row.</param>
/// <param name="DriftSteerScale">The steering-authority multiplier while drifting; zero without a drift row.</param>
public readonly record struct FixedBodyDrive(
    FixedQ4816 ReverseSpeed,
    FixedQ4816 Accel,
    FixedQ4816 Brake,
    FixedQ4816 Coast,
    FixedQ4816 Grip,
    FixedQ4816 SteerReferenceSpeed,
    FixedQ4816 SteerFalloff,
    FixedQ4816 PitchRate,
    FixedQ4816 DriftGrip,
    FixedQ4816 DriftSteerScale
);
