using System.Text.Json.Serialization;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>
/// A kit's locomotion tuning — the shape of the values <c>WorldKit.BodyMotionProgram</c>'s selected operations read
/// each tick. These float values are compiled once into <see cref="FixedMotionTuning"/> before simulation and never
/// become runtime simulation state.
/// </summary>
/// <remarks>Contact-solved locomotion: vertical velocity is the current <see cref="WorldHold"/> row's own arc,
/// integrated by <c>ApplyHold</c> and resolved against the world contact field; planar velocity converges toward
/// the commanded target through <see cref="Shaping"/>. Gravity, lift, and MoveUp thrust are <see cref="Holds"/>
/// facets, not this row's own — every Motion-kind kit authors at least one hold row, so a kit with no vertical law
/// of its own still authors a row of kind <c>None</c>.</remarks>
/// <param name="Speed">The kit's movement rate (see <see cref="WorldSpeed"/>).</param>
/// <param name="Turn">The kit's steering rate (see <see cref="WorldTurn"/>).</param>
/// <param name="Shaping">The ordered velocity-shaping table (see <see cref="WorldShaping"/>) the <c>ShapeVelocity</c>
/// operation reads — the first row whose <c>when</c> gate opens governs. Required (non-empty) for a kit whose
/// program selects <c>ShapeVelocity</c>; <see langword="null"/> for a kit whose program never shapes planar
/// velocity through it (a free-flight kit that owns its whole velocity channel directly).</param>
/// <param name="MoveFrame">Which frame <c>MoveAdvance</c>/<c>MoveStrafe</c> resolve in.
/// <see cref="MotionMoveFrame.Heading"/> explicitly rotates the commanded planar target by the body's own
/// integrated heading. <see cref="MotionMoveFrame.World"/> (the default) takes the two channels as
/// axes already in world frame — the seat's client composes the camera yaw into the submitted intent before it ever
/// reaches the wire, so the sim never reads a camera pose (determinism: no camera state enters simulation).</param>
/// <param name="FacingSnap">Under <see cref="MotionMoveFrame.World"/> only: whether the body's drawn ATTITUDE
/// snaps to <c>Atan2</c> of the commanded planar direction every tick that carries input (no turn-rate ramp, no
/// skid) — the body angles toward its travel, a strafe included — while its HEADING (the Turn role's integral,
/// <c>WorldBody.FixedYaw</c>, the frame a <see cref="ChannelFrame.Heading"/> pair moves in) holds, and the
/// attitude returns to the heading the tick movement stops. Only the Face roles turn the heading itself. Ignored
/// under <see cref="MotionMoveFrame.Heading"/>, where attitude is the integrated heading by construction.
/// <see langword="true"/> is the default.</param>
/// <param name="Holds">The ordered list of what may hold this body — see <see cref="WorldHold"/> — read by the
/// <c>ResolveHold</c>/<c>ApplyHold</c> operations. A Motion-kind kit authoring none refuses validation by name: the
/// hold list is the only spelling of a vertical channel, so a kit with no vertical law of its own still authors
/// one row of kind <see cref="BodyHoldKind.None"/>.</param>
public sealed record WorldMotion(
    WorldSpeed Speed,
    WorldTurn Turn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldShaping>? Shaping = null,
    MotionMoveFrame MoveFrame = MotionMoveFrame.World,
    bool FacingSnap = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldHold>? Holds = null
);
/// <summary>The world's motion defaults — the profileless locomotion speeds a stand-in with no seated profile advances on.
/// This is the whole top-level motion section: shaping and holds are per-kit
/// (<see cref="WorldKit.Motion"/>), which is the only place
/// a body ever reads them from, and <c>world.row.set kits</c> is the surface that moves them.
/// </summary>
/// <remarks>Unmapped members are rejected by name rather than accepting a value nothing reads.</remarks>
/// <param name="MoveSpeed">Locomotion speed in world units per second — the profileless fallback a stand-in advances on
/// (a seated player whose identity CLAIMS a rate reads that claim instead, live, so <c>identity.motion</c> stays
/// real-time; an identity claiming none rides the kit's own rate).</param>
/// <param name="TurnSpeed">Turn speed in radians per second (the profileless fallback counterpart to <paramref name="MoveSpeed"/>).</param>
/// <param name="MaxSmoothError">The largest server-correction position error, in world units, that presentation may
/// ease instead of snapping.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct WorldMotionDefaults(
    float MoveSpeed,
    float TurnSpeed,
    float MaxSmoothError
) {
    /// <summary>Gets the inert profileless fallback — the smallest positive speeds the validator's finite-and-
    /// positive floor admits, so an unseated stand-in with no profile advances negligibly rather than not at
    /// all.</summary>
    public static WorldMotionDefaults Default { get; } = new(
        MaxSmoothError: 0.01f,
        MoveSpeed: 0.01f,
        TurnSpeed: 0.01f
    );
}
/// <summary>An authored inclusive bound on one overridable motion scalar — the reusable shape every
/// overridable scalar clamps through (today: <see cref="WorldSpeed.Envelope"/>). Applied at the
/// seat-time profile resolve, never inside the sim: the value simulation reads is already clamped, so the guarantee
/// holds regardless of what a player's identity requests. Absent (the field default) is wide-open — today's
/// behavior exactly.</summary>
/// <param name="Min">The least admitted value (inclusive).</param>
/// <param name="Max">The greatest admitted value (inclusive) — <see cref="WorldDefinitionValidator"/> refuses
/// <paramref name="Max"/> &lt; <paramref name="Min"/> by name. Equal to <paramref name="Min"/> pins the scalar
/// outright regardless of what a profile requests.</param>
public readonly record struct MotionScalarEnvelope(float Min, float Max);
/// <summary>A kit's movement rate. Replaces the old top-level <c>moveSpeed</c>/<c>moveSpeedEnvelope</c>/
/// <c>sprintChannel</c>/<c>sprintMultiplier</c> fields with one row: the seated profile read and the envelope
/// clamp stay one law for every kit.</summary>
/// <param name="Value">Locomotion speed in world units per second — the profileless fallback a stand-in advances
/// on (a seated player reads its live profile's speed instead, so <c>identity.motion</c> stays real-time).</param>
/// <param name="Envelope">The inclusive bound a seated player's live profile speed (and the profileless
/// <paramref name="Value"/> fallback) is clamped to at seat time, or <see langword="null"/> (the default) for
/// no bound — a feel-pinned world authors this to keep a seat's speed inside its own kit's envelope regardless of
/// what the player's identity requests. <see langword="null"/> reproduces an unclamped resolve exactly;
/// <c>Min == Max</c> pins the effective speed outright; a narrower-than-wide-open range still admits a bounded
/// profile override.</param>
/// <param name="Held">The held-multiplier channel (a "boost"/"sprint"), or <see langword="null"/> for a kit with
/// no held speed multiplier. The multiplier applies AFTER <paramref name="Envelope"/> clamps the resolved value:
/// the envelope pins the base rate, the multiplier rides on top.</param>
public sealed record WorldSpeed(
    float Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MotionScalarEnvelope? Envelope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSpeedHeld? Held = null
);
/// <summary>A kit's held speed multiplier — the declared channel a body reads while held (not edge-triggered — a
/// continuous multiplier, unlike the press/release <see cref="ActionSpec"/> vocabulary) to apply
/// <paramref name="Multiplier"/>.</summary>
/// <param name="Channel">The declared composition channel name read while held.</param>
/// <param name="Multiplier">The speed multiplier while the channel reads held. Required positive.</param>
public sealed record WorldSpeedHeld(string Channel, float Multiplier);
/// <summary>A kit's steering rate. Replaces the old top-level <c>turnSpeed</c> and a <c>drive</c> row's own
/// <c>steerReferenceSpeed</c>/<c>steerFalloff</c>/<c>pitchRate</c> with one row every yaw-writing motion operation
/// reads.</summary>
/// <param name="Rate">Turn speed in radians per second at full authority (the profileless fallback counterpart to
/// <see cref="WorldSpeed.Value"/>).</param>
/// <param name="ReferenceSpeed">The longitudinal (drive) or local (grounded/free) speed, world units per second,
/// at which steering authority peaks — authority rises linearly from zero at standstill and falls linearly past it
/// toward <paramref name="Falloff"/> at the kit's resolved move speed. Omitted (the default): full authority at
/// every speed, the behavior every kit authoring no curve keeps.</param>
/// <param name="Falloff">The fraction of full steering authority remaining at the kit's resolved move speed, in
/// <c>[0, 1]</c>. Unread while <paramref name="ReferenceSpeed"/> is omitted.</param>
/// <param name="PitchRate">The pitch rate (rad/s) a drive kit's Pitch channel commands; <c>0</c> (the default)
/// locks the drive frame planar (the ground and hover variants). Positive selects the flying variant's pitched
/// facing, clamped inside the integrator so the frame can never flip past vertical.</param>
public sealed record WorldTurn(
    float Rate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ReferenceSpeed = null,
    float Falloff = 1f,
    float PitchRate = 0f
);
/// <summary>The along-the-target (whole vector) or along-the-heading (drive longitudinal) facet of one
/// <see cref="WorldShaping"/> row. An absent convergence rate means exact, immediate convergence; zero is never a
/// hidden spelling of either "instant" or "disabled". <see cref="Brake"/> and <see cref="Reverse"/> are admitted
/// only when the row also carries <see cref="WorldShaping.Across"/>.</summary>
/// <param name="Engage">The whole-vector engage rate (u/s²) while the commanded target exceeds the body's current
/// magnitude, or — paired with <see cref="WorldShaping.Across"/> — the drive's longitudinal accel rate while
/// throttle commands more speed. <see langword="null"/> means converge immediately.</param>
/// <param name="Brake">The drive's sign-reversal (brake) rate (u/s²) while back-throttle opposes forward travel.
/// <see langword="null"/> means brake immediately. Refused without a paired
/// <see cref="WorldShaping.Across"/>.</param>
/// <param name="Release">The whole-vector release rate (u/s²) while the target does not exceed the current
/// magnitude, or — paired with <see cref="WorldShaping.Across"/> — the drive's coast rate toward rest with
/// throttle centered, and the decay rate while over the commanded speed. <see langword="null"/> means converge
/// immediately.</param>
/// <param name="Reverse">The reverse speed (u/s) full back-throttle converges on from rest; absence forbids
/// reversing. Refused whenever authored without a paired <see cref="WorldShaping.Across"/>.</param>
public sealed record WorldShapingAlong(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Engage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Brake = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Release = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Reverse = null
);
/// <summary>The across-the-heading (lateral) facet of one <see cref="WorldShaping"/> row — its presence is what
/// selects the anisotropic drive decomposition over the whole-vector response law.</summary>
/// <param name="Grip">The lateral convergence rate (u/s²) toward zero slip while this row governs, or
/// <see langword="null"/> to remove slip immediately.</param>
public sealed record WorldShapingAcross(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Grip = null
);
/// <summary>One row of a kit's ordered <c>shaping</c> table: how velocity converges on the commanded intent while
/// <see cref="When"/> holds. Rows evaluate in order, first match wins; the table may carry one unconditional
/// (<see cref="When"/> omitted) row, and when present it must be last. Exactly one of <see cref="Along"/> or
/// <see cref="Dynamics"/> is authored per row; <see cref="Across"/> is legitimate only beside <see cref="Along"/>.
/// A drift/boost row is authored as an ordinary row gated on a <c>held</c> predicate: the FIRST open row governs,
/// so a drift row belongs ahead of the kit's ordinary anisotropic row.</summary>
/// <param name="When">The gate that must hold for this row to win, or <see langword="null"/> for the unconditional
/// row (permitted only as the final row). The gate reuses the action-lane predicate vocabulary, admitting
/// body-fact kinds (<c>now</c>/<c>recently</c>/<c>all</c>/<c>any</c>/<c>not</c>) and <c>held</c> (a composition
/// channel's own live read) — never a per-body action-state predicate.</param>
/// <param name="Along">The along facet — see <see cref="WorldShapingAlong"/> — or <see langword="null"/> for a
/// <see cref="Dynamics"/> row.</param>
/// <param name="Across">The across facet — see <see cref="WorldShapingAcross"/> — selecting the drive
/// decomposition, or <see langword="null"/> for a row that shapes the whole vector. Refused paired with
/// <see cref="Dynamics"/> or without <see cref="Along"/>.</param>
/// <param name="Dynamics">The <c>dynamics</c> row a second-order follower shapes velocity through instead of
/// <see cref="Along"/>, or <see langword="null"/> (the default) for the response/drive law. Exactly one of the two
/// is authored per row.</param>
/// <param name="TurnScale">The steering-authority multiplier while this row governs — the tightened drift arc's
/// spelling, and the neutral default for every ordinary row.</param>
public sealed record WorldShaping(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionPredicate? When = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldShapingAlong? Along = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldShapingAcross? Across = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Dynamics = null,
    float TurnScale = 1f
);
/// <summary>The document intake for the engine's compiled motion tunings — the one place an authored
/// <see cref="WorldMotion"/> row becomes the fixed-point form simulation reads. Channel names (a shaping row's
/// <c>held</c> gate, <see cref="WorldSpeedHeld.Channel"/>) resolve through the world's compiled channel table here,
/// the same resolved-outside/consumed-as-ordinal seam <see cref="WorldHoldFactory"/> uses.</summary>
public static class WorldMotionTuningFactory {
    /// <summary>Compiles an authored scalar envelope to its fixed-point form.</summary>
    /// <param name="envelope">The authored inclusive bound.</param>
    /// <returns>The compiled bound.</returns>
    public static FixedMotionScalarEnvelope Compile(in MotionScalarEnvelope envelope) => new(
        Min: FixedQ4816.FromDouble(value: envelope.Min),
        Max: FixedQ4816.FromDouble(value: envelope.Max)
    );
    /// <summary>Compiles the authored floating-point motion defaults to their fixed-point form.</summary>
    /// <param name="motion">The authored world motion defaults.</param>
    /// <returns>The compiled defaults.</returns>
    public static FixedMotionDefaults Compile(in WorldMotionDefaults motion) => new(
        MoveSpeed: FixedQ4816.FromDouble(value: motion.MoveSpeed),
        TurnSpeed: FixedQ4816.FromDouble(value: motion.TurnSpeed),
        MaxSmoothError: FixedQ4816.FromDouble(value: motion.MaxSmoothError)
    );
    private static FixedMotionDynamics? CompileDynamics(string? name, IReadOnlyList<WorldDynamicsRow> dynamics, int simulationRateHz) {
        if (
            (name is not { Length: > 0 }) ||
            (WorldDefinitionRows.FindDynamics(
            dynamics: dynamics,
            name: name
        ) is not { } row)
        ) {
            return null;
        }

        var compiled = SecondOrderDynamics.Create(
            dampingRatio: FixedQ4816.FromDouble(value: row.Damping),
            frequencyHz: FixedQ4816.FromDouble(value: row.Frequency),
            initialResponse: FixedQ4816.FromDouble(value: row.Response)
        );

        return new FixedMotionDynamics(Planar: compiled.Compile(
            stepTicks: (FixedTickConversion.TicksPerSecond / ((ulong)simulationRateHz)),
            ticksPerSecond: FixedTickConversion.TicksPerSecond
        ));
    }
    private static FixedBodyShaping[] CompileShaping(IReadOnlyList<WorldShaping>? shaping, WorldChannelTable channels, IReadOnlyList<WorldDynamicsRow> dynamics, int simulationRateHz, List<ActionFact> recencyFacts, List<ulong> recencyWindows) {
        if (shaping is not { Count: > 0 } rows) {
            return [];
        }

        var compiled = new FixedBodyShaping[rows.Count];

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var gate = new List<CompiledPredicate>();

            // The shaping table shares ONE recency-clock table across all rows (as one lane's press/release channels
            // share one), slotted by the same predicate flattener the action lanes use — extended here to resolve a
            // `held` predicate's channel against the world's own table.
            BodyActionSpecFactory.FlattenPredicate(
                predicate: row.When,
                gate: gate,
                recencyFacts: recencyFacts,
                recencyWindows: recencyWindows,
                channels: channels
            );

            compiled[index] = new FixedBodyShaping(
                When: gate.ToArray(),
                Along: ((row.Along is { } along)
                ? new FixedShapingAlong(
                    Engage: FixedQ4816.FromDouble(value: (along.Engage ?? 0f)),
                    Brake: FixedQ4816.FromDouble(value: (along.Brake ?? 0f)),
                    Release: FixedQ4816.FromDouble(value: (along.Release ?? 0f)),
                    Reverse: FixedQ4816.FromDouble(value: (along.Reverse ?? 0f)),
                    Instant: ((along.Engage is null ? ShapingInstant.Engage : ShapingInstant.None)
                        | (along.Brake is null ? ShapingInstant.Brake : ShapingInstant.None)
                        | (along.Release is null ? ShapingInstant.Release : ShapingInstant.None))
                )
                : null),
                Across: ((row.Across is { } across)
                ? new FixedShapingAcross(
                    Grip: FixedQ4816.FromDouble(value: (across.Grip ?? 0f)),
                    Instant: (across.Grip is null)
                )
                : null),
                Dynamics: CompileDynamics(
                    name: row.Dynamics,
                    dynamics: dynamics,
                    simulationRateHz: simulationRateHz
                ),
                TurnScale: FixedQ4816.FromDouble(value: row.TurnScale)
            );
        }

        return compiled;
    }
    /// <summary>Compiles an authored kit motion row to its fixed-point form against a world's compiled channel
    /// table and its own <c>dynamics</c>-row table.</summary>
    /// <param name="tuning">The authored motion row.</param>
    /// <param name="channels">The world's compiled channel table.</param>
    /// <param name="dynamics">The world's declared <c>dynamics</c> rows a shaping row may name.</param>
    /// <param name="simulationRateHz">The world's own simulation rate — a named dynamics row's step-width
    /// divisor.</param>
    /// <returns>The compiled tuning.</returns>
    public static FixedMotionTuning Compile(WorldMotion tuning, WorldChannelTable channels, IReadOnlyList<WorldDynamicsRow> dynamics, int simulationRateHz) {
        var recencyFacts = new List<ActionFact>();
        var recencyWindows = new List<ulong>();
        var shaping = CompileShaping(
            shaping: tuning.Shaping,
            channels: channels,
            dynamics: dynamics,
            simulationRateHz: simulationRateHz,
            recencyFacts: recencyFacts,
            recencyWindows: recencyWindows
        );
        var heldOrdinal = (((tuning.Speed.Held?.Channel is { Length: > 0 } held) && channels.TryGetOrdinal(
            name: held,
            ordinal: out var heldResolved
        ))
            ? heldResolved
            : -1
        );

        return new(
            Speed: new FixedSpeed(
                Value: FixedQ4816.FromDouble(value: tuning.Speed.Value),
                Envelope: ((tuning.Speed.Envelope is { } envelope)
                ? Compile(envelope: envelope)
                : null),
                HeldOrdinal: heldOrdinal,
                HeldMultiplier: FixedQ4816.FromDouble(value: (tuning.Speed.Held?.Multiplier ?? 1f))
            ),
            Turn: new FixedTurn(
                Rate: FixedQ4816.FromDouble(value: tuning.Turn.Rate),
                ReferenceSpeed: FixedQ4816.FromDouble(value: (tuning.Turn.ReferenceSpeed ?? 0f)),
                Falloff: FixedQ4816.FromDouble(value: tuning.Turn.Falloff),
                PitchRate: FixedQ4816.FromDouble(value: tuning.Turn.PitchRate)
            ),
            Shaping: shaping,
            ShapingRecencyFacts: recencyFacts.ToArray(),
            ShapingRecencyWindows: recencyWindows.ToArray(),
            MoveFrame: tuning.MoveFrame,
            FacingSnap: tuning.FacingSnap
        );
    }
}
