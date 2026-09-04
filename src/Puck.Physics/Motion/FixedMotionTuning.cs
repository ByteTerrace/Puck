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
/// <summary>The one-time fixed-point compilation of a shaping row's <c>dynamics</c> facet — the second-order
/// follower alternative to a row's own <c>Along</c> facet, bound to the world's own simulation step width.
/// <see cref="Planar"/> steps the planar/drive lanes <c>ShapeVelocity</c> reads and, for a kit authoring a medium
/// hold whose governing row also names this facet, the same compiled step also drives the one-dimensional vertical
/// lane that hold's law reads (one authored row, one compiled propagator, two lane counts) — never a second
/// compile.</summary>
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
/// <summary>Identifies the along-axis rates whose absent authored value compiles to exact convergence.</summary>
[Flags]
public enum ShapingInstant : byte {
    /// <summary>Every rate is finite.</summary>
    None = 0,

    /// <summary>The engage lane converges immediately.</summary>
    Engage = 1 << 0,

    /// <summary>The reversal lane converges immediately.</summary>
    Reversal = 1 << 1,

    /// <summary>The release lane converges immediately.</summary>
    Release = 1 << 2,
}
/// <summary>The compiled form of a <c>shaping</c> row's <c>along</c> facet: the whole-vector response law's
/// engage/release rates (read when the row carries no <see cref="FixedShapingAcross"/>), and the drive
/// decomposition's longitudinal engage/reversal/release rates and backward target speed (read when it
/// does).</summary>
/// <param name="Engage">The whole-vector engage rate (u/s²), or the drive's longitudinal forward-accelerate
/// rate.</param>
/// <param name="ReversalRate">Unread without a paired <see cref="FixedShapingAcross"/>: the drive's sign-reversal
/// rate (u/s²) while back-throttle opposes forward travel.</param>
/// <param name="Release">The whole-vector release rate (u/s²), or the drive's coast-down rate.</param>
/// <param name="BackwardSpeed">Unread without a paired <see cref="FixedShapingAcross"/>: the backward target speed
/// (u/s) full back-throttle converges on from rest.</param>
/// <param name="Instant">The explicit absence-derived immediate-convergence lanes. Rate fields are zero where
/// their corresponding flag is set and are never interpreted without that flag.</param>
public readonly record struct FixedShapingAlong(FixedQ4816 Engage, FixedQ4816 ReversalRate, FixedQ4816 Release, FixedQ4816 BackwardSpeed, ShapingInstant Instant);
/// <summary>The compiled form of a <c>shaping</c> row's <c>across</c> facet — present only on a row that runs the
/// drive decomposition.</summary>
/// <param name="Lateral">The lateral convergence rate (u/s²) toward zero slip while this row governs.</param>
/// <param name="Instant">Whether lateral and residual slip are removed immediately.</param>
public readonly record struct FixedShapingAcross(FixedQ4816 Lateral, bool Instant);
/// <summary>One compiled row of a kit's <c>shaping</c> table — the unified velocity-shaping law
/// <see cref="BodyMotionOp.ShapeVelocity"/> reads. The first row whose <see cref="When"/> gate opens governs the
/// whole tick; a row carries exactly one of <see cref="Along"/> (used alone for the whole-vector response law, or
/// paired with <see cref="Across"/> for the drive decomposition) or <see cref="Dynamics"/> (the second-order
/// follower alternative).</summary>
/// <param name="When">The flattened gate, empty for the unconditional row.</param>
/// <param name="Along">The along-the-target/along-the-heading facet, or <see langword="null"/> for a
/// <see cref="Dynamics"/> row.</param>
/// <param name="Across">The across-the-heading facet selecting the drive decomposition, or <see langword="null"/>
/// for a row that shapes the whole vector.</param>
/// <param name="Dynamics">The second-order follower this row names, or <see langword="null"/> for an
/// <see cref="Along"/> row.</param>
/// <param name="TurnScale">The steering-authority multiplier while this row governs.</param>
public readonly record struct FixedBodyShaping(
    CompiledPredicate[] When,
    FixedShapingAlong? Along,
    FixedShapingAcross? Across,
    FixedMotionDynamics? Dynamics,
    FixedQ4816 TurnScale
);
/// <summary>The one-time fixed-point compilation of a kit's <c>turn</c> row — the steering rate every yaw-writing
/// motion operation reads, and the speed-scaled authority curve <see cref="BodyMotionOp.ResolveDriveFrame"/>,
/// <see cref="BodyMotionOp.ResolveYawAttitudeAndPlanarFrame"/>, and <see cref="BodyMotionOp.IntegrateLocalAttitude"/>
/// each apply identically.</summary>
/// <param name="Rate">The turn rate (rad/s) at full authority.</param>
/// <param name="ReferenceSpeed">The longitudinal/local speed (u/s) at which authority peaks, or zero — the
/// compiled sentinel for "not authored" — for full authority at every speed.</param>
/// <param name="Falloff">The fraction of full authority remaining at the kit's resolved move speed, in
/// <c>[0, 1]</c>; unread while <see cref="ReferenceSpeed"/> is zero.</param>
/// <param name="PitchRate">The pitch rate (rad/s) the Pitch channel commands under a pitched frame; zero locks it
/// planar.</param>
public readonly record struct FixedTurn(FixedQ4816 Rate, FixedQ4816 ReferenceSpeed, FixedQ4816 Falloff, FixedQ4816 PitchRate);
/// <summary>The one-time fixed-point compilation of a kit's <c>speed</c> row — the movement rate every planar
/// motion operation reads.</summary>
/// <param name="Value">The profileless fallback speed (u/s); a seated profile's own claim overrides it before
/// <see cref="Envelope"/> clamps the resolved value.</param>
/// <param name="Envelope">The seat-time clamp bound, or <see langword="null"/> for none.</param>
/// <param name="HeldOrdinal">The resolved held-multiplier channel ordinal, or <c>-1</c> for a kit with no held
/// speed multiplier.</param>
/// <param name="HeldMultiplier">The multiplier applied to the resolved speed while the channel at
/// <see cref="HeldOrdinal"/> reads held; meaningless when <see cref="HeldOrdinal"/> is negative.</param>
public readonly record struct FixedSpeed(FixedQ4816 Value, FixedMotionScalarEnvelope? Envelope, int HeldOrdinal, FixedQ4816 HeldMultiplier);
/// <summary>The one-time fixed-point compilation of an authored grounded motion row. Runtime simulation reads only
/// this form.</summary>
/// <remarks><see cref="Shaping"/> is simulation-affecting: it selects the slice of the tuning the velocity-shaping
/// stage reads. <see cref="ShapingRecencyFacts"/>/<see cref="ShapingRecencyWindows"/> are the shared recency-clock
/// table across every row's <c>recently</c> gate, and the per-tick clock updater walks it.</remarks>
public readonly record struct FixedMotionTuning(
    FixedSpeed Speed,
    FixedTurn Turn,
    FixedBodyShaping[] Shaping,
    ActionFact[] ShapingRecencyFacts,
    ulong[] ShapingRecencyWindows,
    MotionMoveFrame MoveFrame,
    bool FacingSnap
) {
    /// <summary>Gets the number of recency clocks the shaping table's recency gates share.</summary>
    public int RecencySlots => ShapingRecencyFacts.Length;
    /// <summary>Gets a value indicating whether any shaping row names a second-order follower — whether the body's
    /// planar/vertical follower state is live and needs carrying across an up-axis transport.</summary>
    public bool HasDynamics {
        get {
            foreach (var row in Shaping) {
                if (row.Dynamics is not null) {
                    return true;
                }
            }

            return false;
        }
    }
}
