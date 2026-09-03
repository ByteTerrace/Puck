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
/// integrated by <c>ApplyHold</c> and resolved against the world contact field; planar velocity is
/// response-shaped toward the commanded target; sprint/frame/facing read as described below. Gravity, lift, and
/// MoveUp thrust are <see cref="Holds"/> facets, not this row's own — every Motion-kind kit authors at least one
/// hold row, so a kit with no vertical law of its own still authors a row of kind <c>None</c>.</remarks>
/// <param name="MoveSpeed">Locomotion speed in world units per second — the profileless fallback a stand-in advances on
/// (a seated player reads its live profile's speed instead, so <c>identity.motion</c> stays real-time).</param>
/// <param name="TurnSpeed">Turn speed in radians per second (the profileless fallback counterpart to <paramref name="MoveSpeed"/>).</param>
/// <param name="SprintMultiplier">The held-sprint speed multiplier, applied while
/// <paramref name="SprintChannel"/> reads held; <c>1</c> is a no-op.</param>
/// <param name="Response">The velocity-response table (see <see cref="MotionResponse"/>) planar velocity converges
/// through, or <see langword="null"/> (the default) when <paramref name="Dynamics"/> shapes it instead — exactly
/// one of the two is authored, so <see langword="null"/> means "not authored" to that rule and is never coalesced
/// here. An authored empty table is distinct: it snaps planar velocity instantly. The one place
/// <see langword="null"/> reads as the empty table is <c>WorldMotionTuningFactory.Compile</c>.</param>
/// <param name="Dynamics">The <c>dynamics</c> row a second-order follower shapes planar velocity through instead
/// of <paramref name="Response"/>, or <see langword="null"/> (the default) for the response table. Exactly one of
/// the two is authored.</param>
/// <param name="SprintChannel">The declared channel name a body reads while held (not edge-triggered — a continuous
/// multiplier, unlike the press/release <see cref="ActionSpec"/> vocabulary) to apply <paramref name="SprintMultiplier"/>,
/// or <see langword="null"/> (the default) for a kit with no sprint capability. Resolved to an ordinal once, alongside
/// every other kit-channel name, by <see cref="FixedWorldKit.Compile"/> — an unresolvable name (validator-refused
/// already) reads as "no sprint" rather than throwing.</param>
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
/// <param name="MoveSpeedEnvelope">The inclusive bound a seated player's live profile speed (and the profileless
/// <paramref name="MoveSpeed"/> fallback) is clamped to at seat time, or <see langword="null"/> (the default) for
/// no bound — a feel-pinned world authors this to keep a seat's speed inside its own kit's envelope regardless of
/// what the player's identity requests. <see langword="null"/> reproduces today's unclamped behavior exactly;
/// <c>Min == Max</c> pins the effective speed outright; a narrower-than-wide-open range still admits a bounded
/// profile override. See <see cref="MotionScalarEnvelope"/>.</param>
/// <param name="Holds">The ordered list of what may hold this body — see <see cref="WorldHold"/> — read by the
/// <c>ResolveHold</c>/<c>ApplyHold</c> operations. A Motion-kind kit authoring none refuses validation by name: the
/// hold list is the only spelling of a vertical channel, so a kit with no vertical law of its own still authors
/// one row of kind <see cref="BodyHoldKind.None"/>.</param>
/// <param name="Drive">The anisotropic drive row — see <see cref="WorldDrive"/> — read by the
/// <c>ResolveDriveFrame</c>/<c>ShapeDriveVelocity</c> operations, or <see langword="null"/> (the default) for a
/// kit whose planar velocity is the isotropic shaping's alone. A program selecting either drive operation
/// against a kit authoring no row refuses by name through the <c>Drive</c> tuning facet.</param>
public sealed record WorldMotion(
    float MoveSpeed,
    float TurnSpeed,
    float SprintMultiplier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MotionResponse>? Response = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Dynamics = null,
    string? SprintChannel = null,
    MotionMoveFrame MoveFrame = MotionMoveFrame.World,
    bool FacingSnap = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MotionScalarEnvelope? MoveSpeedEnvelope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldHold>? Holds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldDrive? Drive = null
);
/// <summary>
/// The world's motion defaults — the profileless locomotion speeds a stand-in with no seated profile advances on.
/// This is the whole top-level motion section: gravity and the velocity-response table are per-kit
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
/// <summary>One row of a kit's velocity-response table: how fast planar velocity converges on the commanded
/// target while <paramref name="Gate"/> holds. Rows evaluate in order, first match wins; a body matching no row
/// snaps instantly (the built-in behavior, and the behavior of a kit with no table). The gate reuses the
/// action-lane predicate vocabulary — only body-fact kinds (<c>now</c>/<c>recently</c>/<c>all</c>) are admissible.</summary>
/// <param name="EngageRate">The convergence rate (world units/second²) while the stick is deflected — acceleration
/// toward the commanded target.</param>
/// <param name="ReleaseRate">The convergence rate while the stick is centered — deceleration toward rest (the coast).</param>
/// <param name="Gate">The body-fact predicate that must hold for this row to win, or <see langword="null"/> for the
/// always-row (permitted only as the final row).</param>
/// <remarks><see cref="Gate"/> trails the two rates and carries an explicit <see langword="null"/> default because it
/// is genuinely optional — the always-row omits it, and the writer already omits it when null. Parameter order is what
/// expresses that to the loader: a constructor parameter with no default is required (the source-generated context
/// enforces it), so an optional member has to be able to carry one, which means trailing the required ones. Document
/// order is unaffected — JSON binds by name.</remarks>
public sealed record MotionResponse(
    float EngageRate,
    float ReleaseRate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionPredicate? Gate = null
);
/// <summary>An authored inclusive bound on one <see cref="WorldMotion"/> scalar — the reusable shape every
/// overridable scalar clamps through (today: <see cref="WorldMotion.MoveSpeedEnvelope"/>). Applied at the
/// seat-time profile resolve, never inside the sim: the value simulation reads is already clamped, so the guarantee
/// holds regardless of what a player's identity requests. Absent (the field default) is wide-open — today's
/// behavior exactly.</summary>
/// <param name="Min">The least admitted value (inclusive).</param>
/// <param name="Max">The greatest admitted value (inclusive) — <see cref="WorldDefinitionValidator"/> refuses
/// <paramref name="Max"/> &lt; <paramref name="Min"/> by name. Equal to <paramref name="Min"/> pins the scalar
/// outright regardless of what a profile requests.</param>
public readonly record struct MotionScalarEnvelope(float Min, float Max);
/// <summary>The held low-traction state of a kit's <see cref="WorldDrive"/> — a declared channel whose held read
/// swaps the drive's lateral grip and scales its steering authority. Absent, the kit cannot drift.</summary>
/// <param name="Channel">The declared composition channel name read while held.</param>
/// <param name="Grip">The lateral convergence rate (u/s²) replacing <see cref="WorldDrive.Grip"/> while the channel
/// reads held — the deliberate low-traction state. Required positive.</param>
/// <param name="SteerScale">The steering-authority multiplier while drifting (the tightened drift arc). Required
/// positive.</param>
public sealed record WorldDriveDrift(
    string Channel,
    float Grip,
    float SteerScale
);
/// <summary>
/// A kit's anisotropic drive row — what only a drive has, authored beside the one motion row rather than replacing
/// it. Velocity decomposes into longitudinal/lateral (and residual) body-frame components, each converging at its
/// own authored rate: the anisotropy a kart needs and the motion row's isotropic planar shaping cannot express,
/// since that shaping cannot tune grip apart from acceleration. Steering authority scales with longitudinal speed
/// (no spinning in place, looser at top speed) and reverses sign with reversing travel.
/// </summary>
/// <remarks>The forward speed full throttle converges on is the kit's own
/// <see cref="WorldMotion.MoveSpeed"/> (bounded by its
/// <see cref="WorldMotion.MoveSpeedEnvelope"/>, scaled by its
/// <see cref="WorldMotion.SprintMultiplier"/> while the sprint channel reads held), and the steering rate
/// at full authority is its <see cref="WorldMotion.TurnSpeed"/> — one name each, never a second spelling. The
/// vertical channel is the kit's own hold row, as for any other kit: one row serves the ground and hover variants
/// (a Surface Gravity row), the flying variant a Free row with a positive <paramref name="PitchRate"/> so climb
/// emerges from the pitched facing.</remarks>
/// <param name="Accel">The longitudinal convergence rate (u/s²) while throttle commands more speed.</param>
/// <param name="Brake">The longitudinal convergence rate (u/s²) while back-throttle opposes forward travel.</param>
/// <param name="Coast">The longitudinal convergence rate (u/s²) toward rest with throttle centered, and the decay
/// rate while over the commanded speed (the post-boost bleed).</param>
/// <param name="Grip">The lateral convergence rate (u/s²) toward zero slip — traction. Lower is slidier.</param>
/// <param name="SteerReferenceSpeed">The longitudinal speed (u/s) at which steering authority peaks; authority
/// rises linearly from zero at standstill.</param>
/// <param name="SteerFalloff">The fraction of full steering authority remaining at the kit's resolved move speed,
/// in <c>[0, 1]</c>; authority falls linearly from the reference speed.</param>
/// <param name="ReverseSpeed">The reverse speed (u/s) full back-throttle converges on from rest; <c>0</c> forbids
/// reversing.</param>
/// <param name="PitchRate">The pitch rate (rad/s) the Pitch channel commands; <c>0</c> locks the frame planar (the
/// ground and hover variants). Positive selects the flying variant's pitched facing, clamped inside the integrator
/// so the frame can never flip past vertical.</param>
/// <param name="Drift">The held low-traction state, or <see langword="null"/> (the default) for a kit that cannot
/// drift.</param>
public sealed record WorldDrive(
    float Accel,
    float Brake,
    float Coast,
    float Grip,
    float SteerReferenceSpeed,
    float SteerFalloff,
    float ReverseSpeed = 0f,
    float PitchRate = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldDriveDrift? Drift = null
);
/// <summary>The document intake for the engine's compiled motion tunings — the one place an authored
/// <see cref="WorldMotion"/> row becomes the fixed-point form simulation reads.</summary>
public static class WorldMotionTuningFactory {
    private static FixedMotionTuning Compile(float moveSpeed, float turnSpeed, IReadOnlyList<MotionResponse> response, float sprintMultiplier, MotionMoveFrame moveFrame, bool facingSnap, MotionScalarEnvelope? moveSpeedEnvelope, FixedMotionDynamics? dynamics, WorldDrive? drive) {
        var rows = response;
        var compiled = new FixedMotionResponse[rows.Count];
        var recencyFacts = new List<ActionFact>();
        var recencyWindows = new List<ulong>();

        for (var index = 0; (index < rows.Count); index++) {
            var gate = new List<CompiledPredicate>();

            // The response table shares ONE recency-clock table across all rows (as one lane's press/release channels
            // share one), slotted by the same predicate flattener the action lanes use.
            BodyActionSpecFactory.FlattenPredicate(
                predicate: rows[index].Gate,
                gate: gate,
                recencyFacts: recencyFacts,
                recencyWindows: recencyWindows
            );

            compiled[index] = new FixedMotionResponse(
                Gate: gate.ToArray(),
                EngageRate: FixedQ4816.FromDouble(value: rows[index].EngageRate),
                ReleaseRate: FixedQ4816.FromDouble(value: rows[index].ReleaseRate)
            );
        }

        return new(
            MoveSpeed: FixedQ4816.FromDouble(value: moveSpeed),
            TurnSpeed: FixedQ4816.FromDouble(value: turnSpeed),
            Response: compiled,
            ResponseRecencyFacts: recencyFacts.ToArray(),
            ResponseRecencyWindows: recencyWindows.ToArray(),
            SprintMultiplier: FixedQ4816.FromDouble(value: sprintMultiplier),
            MoveFrame: moveFrame,
            FacingSnap: facingSnap,
            MoveSpeedEnvelope: ((moveSpeedEnvelope is { } envelope)
            ? Compile(envelope: envelope)
            : null),
            PlanarDynamics: dynamics,
            Drive: ((drive is not null)
            ? Compile(drive: drive)
            : null)
        );
    }

    /// <summary>Compiles an authored drive row to its fixed-point form. The held drift channel name resolves to an
    /// ordinal separately, through the world's channel table.</summary>
    /// <param name="drive">The authored drive row.</param>
    /// <returns>The compiled row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="drive"/> is <see langword="null"/>.</exception>
    public static FixedBodyDrive Compile(WorldDrive drive) {
        ArgumentNullException.ThrowIfNull(argument: drive);

        return new(
            ReverseSpeed: FixedQ4816.FromDouble(value: drive.ReverseSpeed),
            Accel: FixedQ4816.FromDouble(value: drive.Accel),
            Brake: FixedQ4816.FromDouble(value: drive.Brake),
            Coast: FixedQ4816.FromDouble(value: drive.Coast),
            Grip: FixedQ4816.FromDouble(value: drive.Grip),
            SteerReferenceSpeed: FixedQ4816.FromDouble(value: drive.SteerReferenceSpeed),
            SteerFalloff: FixedQ4816.FromDouble(value: drive.SteerFalloff),
            PitchRate: FixedQ4816.FromDouble(value: drive.PitchRate),
            DriftGrip: FixedQ4816.FromDouble(value: (drive.Drift?.Grip ?? 0f)),
            DriftSteerScale: FixedQ4816.FromDouble(value: (drive.Drift?.SteerScale ?? 0f))
        );
    }
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
    /// <summary>Compiles an authored kit motion row to its fixed-point form.</summary>
    /// <param name="tuning">The authored motion row.</param>
    /// <param name="dynamics">The compiled <c>dynamics</c>-row follower <paramref name="tuning"/> names, or
    /// <see langword="null"/> when it shapes planar velocity through <see cref="WorldMotion.Response"/>
    /// instead.</param>
    /// <returns>The compiled tuning.</returns>
    public static FixedMotionTuning Compile(WorldMotion tuning, FixedMotionDynamics? dynamics = null) => Compile(
        moveSpeed: tuning.MoveSpeed,
        turnSpeed: tuning.TurnSpeed,
        response: (tuning.Response ?? []),
        sprintMultiplier: tuning.SprintMultiplier,
        moveFrame: tuning.MoveFrame,
        facingSnap: tuning.FacingSnap,
        moveSpeedEnvelope: tuning.MoveSpeedEnvelope,
        dynamics: dynamics,
        drive: tuning.Drive
    );

}
