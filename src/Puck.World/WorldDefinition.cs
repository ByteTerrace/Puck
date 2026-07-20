using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Presentation;
using Puck.Authoring;
using Puck.Commands;
using Puck.Maths;
using Puck.Scene;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The tunable locomotion and jump feel one <see cref="WorldBody"/> integrates under — every rate, gravity, and
/// forgiveness window the grounded/free integrators read, in one authored bundle. These float values are compiled once
/// into <see cref="FixedMotionTuning"/> before simulation and never become runtime simulation state.
/// </summary>
/// <remarks><see cref="Default"/> bakes the jump-kit spatial constants through the single <see cref="DefaultActionScale"/>
/// factor, so retuning the feel to a new speed scale is that one number. Linear scaling keeps the arc proportions
/// (apex = v²/2g scales linearly, rise time v/g is unchanged); the dimensionless cut ratio and the time-valued
/// forgiveness windows are not scaled.</remarks>
/// <param name="MoveSpeed">Locomotion speed in world units per second — the profileless fallback a stand-in advances on
/// (a seated player reads its live profile's speed instead, so <c>profile.set</c> stays real-time).</param>
/// <param name="TurnSpeed">Turn speed in radians per second (the profileless fallback counterpart to <paramref name="MoveSpeed"/>).</param>
/// <param name="GroundY">The ground plane the grounded model pins the avatar's foot point to (unless a jump has lifted it).</param>
/// <param name="JumpSpeed">The upward launch velocity a jump fires with (world units/second).</param>
/// <param name="RiseGravity">The downward acceleration while rising (u/s²) — the floaty top of the arc.</param>
/// <param name="FallGravity">The downward acceleration while falling (u/s²) — the snappy descent (heavier than the rise).</param>
/// <param name="MaxFallSpeed">The terminal fall speed the descent is clamped to (u/s).</param>
/// <param name="JumpCutMultiplier">The early-release up-velocity cut (a dimensionless ratio) — a tap is a short hop.</param>
/// <param name="CoyoteTime">The grace after leaving ground where a jump still fires (seconds — a time, so unscaled).</param>
/// <param name="JumpBufferTime">The window before landing where a press is remembered and fires on touchdown (seconds — unscaled).</param>
/// <param name="Response">The velocity-response table (see <see cref="MotionResponse"/>) — SIM-AFFECTING. The empty
/// table snaps planar velocity instantly.</param>
internal readonly record struct MotionTuning(
    float MoveSpeed,
    float TurnSpeed,
    float GroundY,
    float JumpSpeed,
    float RiseGravity,
    float FallGravity,
    float MaxFallSpeed,
    float JumpCutMultiplier,
    float CoyoteTime,
    float JumpBufferTime,
    IReadOnlyList<MotionResponse> Response
) {
    private readonly IReadOnlyList<MotionResponse> m_response = (Response ?? []);

    /// <summary>The velocity-response table. The type's ONE absence-coalesce: source generation constructs this struct
    /// through its parameterless constructor and the init accessors, leaving an absent JSON property null, so the
    /// accessor — not each reader — makes the table a list.</summary>
    public IReadOnlyList<MotionResponse> Response {
        get => m_response;
        init => m_response = (value ?? []);
    }

    /// <summary>The factor <see cref="Default"/> scales its jump-kit spatial constants by (World runs at half the speed
    /// scale the constants were authored at). See the type remarks for why the ratios and windows are exempt.</summary>
    public const float DefaultActionScale = 0.5f;

    /// <summary>The built-in default tuning — the jump-kit spatial constants baked through
    /// <see cref="DefaultActionScale"/>.</summary>
    public static MotionTuning Default { get; } = new MotionTuning(
        MoveSpeed: 4f,
        TurnSpeed: 2.5f,
        GroundY: 0f,
        JumpSpeed: (11f * DefaultActionScale),
        RiseGravity: (28f * DefaultActionScale),
        FallGravity: (46f * DefaultActionScale),
        MaxFallSpeed: (40f * DefaultActionScale),
        JumpCutMultiplier: 0.45f,
        CoyoteTime: 0.09f,
        JumpBufferTime: 0.10f,
        Response: []
    );
}

/// <summary>
/// The world's MOTION defaults — the ground plane every grounded body pins its foot point to, plus the profileless
/// locomotion speeds a stand-in with no seated profile advances on. This is the whole top-level motion section: jump
/// feel, gravity, and the velocity-response table are PER-KIT (<see cref="WorldKit.Tuning"/>), which is the only place
/// a body ever reads them from, and <c>world.kit.tune</c> is the surface that moves them.
/// </summary>
/// <remarks>Unmapped members are REJECTED: a <c>world.motion.set</c> or a document carrying <c>jumpSpeed</c> here fails
/// by name rather than accepting a value nothing reads.</remarks>
/// <param name="MoveSpeed">Locomotion speed in world units per second — the profileless fallback a stand-in advances on
/// (a seated player reads its live profile's speed instead, so <c>profile.set</c> stays real-time).</param>
/// <param name="TurnSpeed">Turn speed in radians per second (the profileless fallback counterpart to <paramref name="MoveSpeed"/>).</param>
/// <param name="GroundY">The ground plane the grounded model pins the avatar's foot point to (unless a jump has lifted it).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal readonly record struct WorldMotionDefaults(
    float MoveSpeed,
    float TurnSpeed,
    float GroundY
) {
    /// <summary>The built-in motion defaults.</summary>
    public static WorldMotionDefaults Default { get; } = new WorldMotionDefaults(MoveSpeed: 4f, TurnSpeed: 2.5f, GroundY: 0f);
}

/// <summary>The one-time fixed-point compilation of the world's motion defaults. Runtime simulation reads only this
/// form.</summary>
internal readonly record struct FixedMotionDefaults(FixedQ4816 MoveSpeed, FixedQ4816 TurnSpeed, FixedQ4816 GroundY) {
    /// <summary>Compiles the authored floating-point motion defaults to their fixed-point form.</summary>
    public static FixedMotionDefaults Compile(in WorldMotionDefaults motion) => new(
        MoveSpeed: FixedQ4816.FromDouble(value: motion.MoveSpeed),
        TurnSpeed: FixedQ4816.FromDouble(value: motion.TurnSpeed),
        GroundY: FixedQ4816.FromDouble(value: motion.GroundY)
    );
}

/// <summary>One row of a kit's velocity-response table: how fast planar velocity converges on the commanded
/// target while <paramref name="Gate"/> holds. Rows evaluate in order, FIRST match wins; a body matching no row
/// snaps instantly (the built-in behavior, and the behavior of a kit with no table). The gate reuses the
/// action-lane predicate vocabulary — only body-fact kinds (<c>now</c>/<c>recently</c>/<c>all</c>) are admissible.</summary>
/// <param name="Gate">The body-fact predicate that must hold for this row to win, or <see langword="null"/> for the
/// always-row (permitted only as the final row).</param>
/// <param name="EngageRate">The convergence rate (world units/second²) while the stick is deflected — acceleration
/// toward the commanded target.</param>
/// <param name="ReleaseRate">The convergence rate while the stick is centered — deceleration toward rest (the coast).</param>
internal sealed record MotionResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionPredicate? Gate,
    float EngageRate,
    float ReleaseRate
);

/// <summary>A row's solidity facet — it participates in contact resolution using its own declared shape. Presence is
/// the whole switch; <see langword="null"/> means decoration — the row is drawn but bodies pass through it.</summary>
/// <param name="Margin">The signed skin added to the shape for contact purposes. Positive fattens the collider past the
/// drawn surface; negative lets a body sink in. Compensates the smooth-union blend.</param>
internal sealed record WorldSolid(float Margin);

/// <summary>A kit's body VOLUME — a vertical capsule from the foot point in the body's own up axis. A kit with no
/// collider is not solved against the contact field at all.</summary>
/// <param name="Radius">The capsule radius (world units).</param>
/// <param name="Height">Total height from the foot point; must be at least twice <paramref name="Radius"/> — a shorter
/// capsule is a sphere and is rejected rather than silently clamped.</param>
internal sealed record WorldCollider(float Radius, float Height);

/// <summary>Which contact field answers a world's collision queries.</summary>
internal enum WorldContactProvider : byte {
    /// <summary>Derives convex colliders from the document's own solid rows (cheap, the default).</summary>
    Analytic,

    /// <summary>Compiles the solid rows into an SDF (required for planetoids, smooth-union contact surfaces, and solid
    /// placements — landed by Arc 2).</summary>
    Field,
}

/// <summary>The contact solver's world-scale tuning. Collision is OFF under <see cref="None"/>: the world keeps its
/// flat <see cref="WorldMotionDefaults.GroundY"/> plane.</summary>
/// <param name="Enabled">Whether contact resolution runs at all.</param>
/// <param name="Provider">Which contact field answers. <c>analytic</c> derives convex colliders from the document's own
/// solid rows (cheap, the default); <c>field</c> compiles them into an SDF (Arc 2).</param>
/// <param name="ContactSkin">The signed skin the solver keeps between a body and every surface (world units).</param>
/// <param name="MaxIterations">The relaxation iteration count per tick (above 8 is a solver pathology, not a choice).</param>
/// <param name="MaxSlopeDegrees">The steepest surface a body still counts as STANDING on. A contact whose normal leans
/// further from the body's up axis than this pushes the body but never grounds it — the walkable-slope limit.</param>
/// <param name="GradientProbe">The finite-difference step the FIELD provider samples the surface normal with, in world
/// units; 0 takes the evaluator's own default. Only meaningful under <c>field</c>.</param>
internal sealed record WorldCollision(bool Enabled, WorldContactProvider Provider, float ContactSkin,
    int MaxIterations, float MaxSlopeDegrees, float GradientProbe) {
    /// <summary>The built-in default: collision OFF (a world declaring nothing keeps its flat ground plane).</summary>
    public static WorldCollision None { get; } = new(Enabled: false, Provider: WorldContactProvider.Analytic,
        ContactSkin: 0.02f, MaxIterations: 4, MaxSlopeDegrees: 60f, GradientProbe: 0f);
}

/// <summary>
/// The tuning of the simulated stand-ins' synthetic wander — the gentle drift, slow index-seeded weave, and inward
/// spring an idle population produces as analog-stick deflections. These authored float values are compiled once into
/// <see cref="FixedWanderTuning"/> before simulation.
/// </summary>
/// <remarks>The forward-drift deflection is <see cref="DriftSpeed"/> divided by the profileless move speed
/// (<see cref="WorldMotionDefaults.MoveSpeed"/>), so it crosses both sections; <see cref="WorldPopulation"/> derives it from the
/// two.</remarks>
/// <param name="DriftSpeed">The forward drift in world units per second a stand-in gently walks at.</param>
/// <param name="SoftRadius">The disc radius a stand-in is spring-steered back inside once it strays past.</param>
/// <param name="SpawnRadius">The phyllotaxis spawn disc radius (inside <paramref name="SoftRadius"/>).</param>
/// <param name="WeaveAmplitude">The peak gentle turn rate of the slow sine weave (radians/second).</param>
/// <param name="InwardGain">The proportional inward steer applied once a stand-in is outside <paramref name="SoftRadius"/>.</param>
/// <param name="GoldenAngle">The golden angle (radians) — the phyllotaxis spawn/phase spread the seeding walks.</param>
/// <param name="WeaveFrequencyBase">The base of a stand-in's slow weave frequency (Hz-ish), before the per-index hue varies it.</param>
/// <param name="WeaveFrequencyRange">The hue-varied span added onto <paramref name="WeaveFrequencyBase"/> for the weave frequency.</param>
internal readonly record struct WanderTuning(
    float DriftSpeed,
    float SoftRadius,
    float SpawnRadius,
    float WeaveAmplitude,
    float InwardGain,
    float GoldenAngle,
    float WeaveFrequencyBase,
    float WeaveFrequencyRange
) {
    /// <summary>The built-in default wander tuning.</summary>
    public static WanderTuning Default { get; } = new WanderTuning(
        DriftSpeed: 1.5f,
        SoftRadius: 45f,
        SpawnRadius: 40f,
        WeaveAmplitude: 0.5f,
        InwardGain: 1.6f,
        GoldenAngle: 2.399963f,
        WeaveFrequencyBase: 0.3f,
        WeaveFrequencyRange: 0.2f
    );
}

/// <summary>An engine-published per-body sim fact the action predicates gate on. Facts are ENGINE code.</summary>
/// <remarks>ADMISSION RULE: a new fact is privileged sim state the effects/predicates cannot derive from existing
/// facts; add one only then.</remarks>
internal enum ActionFact : byte {
    /// <summary>The body rests on the ground plane.</summary>
    Grounded,

    /// <summary>The body is off the ground plane.</summary>
    Airborne,

    /// <summary>The body's vertical velocity is positive.</summary>
    Rising,

    /// <summary>The body's vertical velocity is negative.</summary>
    Falling,
}

/// <summary>A data-composable gate over facts and per-lane action state — the closed predicate vocabulary (records
/// only; deliberately no expression language). A trigger fires only while its gate holds. The <c>$type</c> string is
/// the JSON discriminator, the same convention every polymorphic row family uses; a new predicate kind is a new
/// derived record plus its <see cref="JsonDerivedTypeAttribute"/> line.</summary>
[JsonDerivedType(typeof(ActionPredicate.Now), typeDiscriminator: "now")]
[JsonDerivedType(typeof(ActionPredicate.Recently), typeDiscriminator: "recently")]
[JsonDerivedType(typeof(ActionPredicate.CooldownElapsed), typeDiscriminator: "cooldownElapsed")]
[JsonDerivedType(typeof(ActionPredicate.UsesBelow), typeDiscriminator: "usesBelow")]
[JsonDerivedType(typeof(ActionPredicate.All), typeDiscriminator: "all")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal abstract record ActionPredicate {
    /// <summary>The fact holds this tick.</summary>
    internal sealed record Now(ActionFact Fact) : ActionPredicate;

    /// <summary>The fact held within the last <paramref name="WindowSeconds"/> — a per-instance recency clock,
    /// refreshed while the fact holds and decaying otherwise (coyote time is <c>Recently(Grounded, w)</c>).</summary>
    internal sealed record Recently(ActionFact Fact, float WindowSeconds) : ActionPredicate;

    /// <summary>The lane's cooldown clock has drained (see <see cref="ActionEffect.StartCooldown"/>).</summary>
    internal sealed record CooldownElapsed() : ActionPredicate;

    /// <summary>The lane's use counter is below <paramref name="Limit"/> — the double-jump/air-dash budget. The
    /// counter increments via <see cref="ActionEffect.ConsumeUse"/> and resets on ground contact.</summary>
    internal sealed record UsesBelow(int Limit) : ActionPredicate;

    /// <summary>Every inner predicate holds (conjunction).</summary>
    internal sealed record All(IReadOnlyList<ActionPredicate> Predicates) : ActionPredicate;
}

/// <summary>A fixed-point op a fired trigger applies to the body at the tick boundary — the closed effect
/// vocabulary.</summary>
/// <remarks>ADMISSION RULE per kind: a new effect is a new record member here PLUS its fixed-point body support, never
/// a flag on an existing one. RESERVED (named, not implemented): <c>AttitudeBurst</c> (a barrel roll — a timed
/// body-frame angular overlay) and <c>EmitWorldEvent</c> (a shoot — routes to the world-event seam when one
/// exists).</remarks>
[JsonDerivedType(typeof(ActionEffect.SetVerticalVelocity), typeDiscriminator: "setVerticalVelocity")]
[JsonDerivedType(typeof(ActionEffect.ScaleVerticalVelocity), typeDiscriminator: "scaleVerticalVelocity")]
[JsonDerivedType(typeof(ActionEffect.PlanarImpulse), typeDiscriminator: "planarImpulse")]
[JsonDerivedType(typeof(ActionEffect.StartCooldown), typeDiscriminator: "startCooldown")]
[JsonDerivedType(typeof(ActionEffect.ConsumeUse), typeDiscriminator: "consumeUse")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal abstract record ActionEffect {
    /// <summary>Writes the body's vertical-velocity channel (the jump launch / the surge). Under the grounded model
    /// gravity owns its decay; under the free model it bleeds to zero at the tuning's rise gravity (no fall phase).</summary>
    internal sealed record SetVerticalVelocity(float Velocity) : ActionEffect;

    /// <summary>Multiplies the body's vertical velocity (the jump cut; gate on <see cref="ActionFact.Rising"/>).</summary>
    internal sealed record ScaleVerticalVelocity(float Factor) : ActionEffect;

    /// <summary>A timed planar velocity overlay (the dash): <paramref name="BodyDirection"/> is rotated by the body's
    /// attitude at fire time and ridden at <paramref name="Speed"/> for <paramref name="DurationSeconds"/>, integrated
    /// through its own accumulator on top of the model's motion — integration itself is untouched.</summary>
    internal sealed record PlanarImpulse(Vector3 BodyDirection, float Speed, float DurationSeconds) : ActionEffect;

    /// <summary>Arms the lane's cooldown clock (see <see cref="ActionPredicate.CooldownElapsed"/>).</summary>
    internal sealed record StartCooldown(float Seconds) : ActionEffect;

    /// <summary>Increments the lane's use counter (see <see cref="ActionPredicate.UsesBelow"/>); ground contact
    /// resets it.</summary>
    internal sealed record ConsumeUse() : ActionEffect;
}

/// <summary>One trigger channel of a lane binding: a gate, a press latch (the buffer — a press stays pending until the
/// gate opens or the latch expires; the release channel latches nothing), and the effects a fire applies in order.</summary>
/// <param name="Gate">The predicate that must hold to fire, or <see langword="null"/> for always.</param>
/// <param name="LatchSeconds">How long a press stays pending waiting for the gate (0 = this tick only).</param>
/// <param name="Effects">The effects applied on fire, in order.</param>
internal sealed record ActionTrigger(ActionPredicate? Gate, float LatchSeconds, IReadOnlyList<ActionEffect> Effects);

/// <summary>A lane's full binding: the press trigger and the release trigger. What a channel DOES is this data — the
/// engine implements only the facts, predicates, and effects.</summary>
/// <param name="OnPress">The rising-edge trigger, or <see langword="null"/>.</param>
/// <param name="OnRelease">The falling-edge trigger (evaluated immediately, never latched), or <see langword="null"/>.</param>
internal sealed record ActionSpec(ActionTrigger? OnPress, ActionTrigger? OnRelease) {
    /// <summary>The default world's jump — the platformer composition over a kit tuning, value-for-value the shipped
    /// feel: press gated on <c>Recently(Grounded, coyote)</c> with the buffer latch, launching the tuning's impulse and
    /// consuming the single ground use; release gated on <c>Rising</c>, cutting the arc for variable height.</summary>
    /// <param name="tuning">The kit tuning supplying the jump constants.</param>
    public static ActionSpec Jump(in MotionTuning tuning) => new(
        OnPress: new ActionTrigger(
            Gate: new ActionPredicate.All(Predicates: [
                new ActionPredicate.Recently(Fact: ActionFact.Grounded, WindowSeconds: tuning.CoyoteTime),
                new ActionPredicate.UsesBelow(Limit: 1),
            ]),
            LatchSeconds: tuning.JumpBufferTime,
            Effects: [
                new ActionEffect.SetVerticalVelocity(Velocity: tuning.JumpSpeed),
                new ActionEffect.ConsumeUse(),
            ]
        ),
        OnRelease: new ActionTrigger(
            Gate: new ActionPredicate.Now(Fact: ActionFact.Rising),
            LatchSeconds: 0f,
            Effects: [new ActionEffect.ScaleVerticalVelocity(Factor: tuning.JumpCutMultiplier)]
        )
    );

    /// <summary>The default world's grounded dash — a forward planar burst at 2.5× the kit's move speed for a quarter
    /// second, grounded-and-cooled gated, then a 1.5 s cooldown.</summary>
    /// <param name="tuning">The kit tuning supplying the move speed.</param>
    public static ActionSpec Dash(in MotionTuning tuning) => new(
        OnPress: new ActionTrigger(
            Gate: new ActionPredicate.All(Predicates: [
                new ActionPredicate.Now(Fact: ActionFact.Grounded),
                new ActionPredicate.CooldownElapsed(),
            ]),
            LatchSeconds: 0.05f,
            Effects: [
                new ActionEffect.PlanarImpulse(BodyDirection: new Vector3(x: 0f, y: 0f, z: -1f), Speed: (tuning.MoveSpeed * 2.5f), DurationSeconds: 0.25f),
                new ActionEffect.StartCooldown(Seconds: 1.5f),
            ]
        ),
        OnRelease: null
    );

    /// <summary>The default world's free-kit surge — an upward velocity pop (bleeding off at the tuning's rise
    /// gravity), cooldown-gated — a second composition from the same primitives.</summary>
    /// <param name="tuning">The kit tuning supplying the impulse and bleed rate.</param>
    public static ActionSpec Surge(in MotionTuning tuning) => new(
        OnPress: new ActionTrigger(
            Gate: new ActionPredicate.CooldownElapsed(),
            LatchSeconds: 0.05f,
            Effects: [
                new ActionEffect.SetVerticalVelocity(Velocity: tuning.JumpSpeed),
                new ActionEffect.StartCooldown(Seconds: 1.5f),
            ]
        ),
        OnRelease: null
    );
}

/// <summary>A kit's wander-producer flavor — the authored constants the deterministic wander producer shapes an
/// entity's gap-filling intent with. Free-model kits read the wave/altitude channels; grounded kits read the drift,
/// strafe/turn waves, and the primary-press threshold.</summary>
/// <param name="Forward">The fixed forward deflection, ignored when <paramref name="DriftForward"/> is set.</param>
/// <param name="DriftForward">Use the wander tuning's drift-derived deflection instead of <paramref name="Forward"/>.</param>
/// <param name="StrafeWave">The activity-wave multiplier on the strafe channel.</param>
/// <param name="TurnWave">The activity-wave multiplier added onto the turn channel (the kart's corner weave).</param>
/// <param name="UpWave">The activity-wave multiplier on the up channel (free kits — the porpoise/bob).</param>
/// <param name="PitchWave">The activity-wave multiplier on the pitch rate (free kits).</param>
/// <param name="RollTurn">The negative-turn multiplier on the roll rate (free kits — the bank).</param>
/// <param name="PrimaryThreshold">When positive, the producer presses the Primary channel while the activity wave
/// exceeds it (the hopper's cadence); zero never presses.</param>
/// <param name="AltitudeBase">The preferred-altitude base a free kit holds (plus the per-index range sample).</param>
/// <param name="AltitudeRange">The per-index span added onto <paramref name="AltitudeBase"/>.</param>
internal readonly record struct WanderFlavor(
    float Forward,
    bool DriftForward,
    float StrafeWave,
    float TurnWave,
    float UpWave,
    float PitchWave,
    float RollTurn,
    float PrimaryThreshold,
    float AltitudeBase,
    float AltitudeRange
);

/// <summary>What an attending body steers toward. ADMISSION RULE: a genuinely new way of CHOOSING a target is a new
/// member here; a new way of MOVING toward one is a new <see cref="AttendFlavor"/> value, never a member here.</summary>
internal enum AttendTarget : byte {
    /// <summary>The nearest active LOCAL seat (indices 0..3) — the companion's "amble toward the player" target.</summary>
    NearestSeat,

    /// <summary>The nearest active body of ANY kind (seat, peer, or inhabitant) other than the attending body itself.</summary>
    NearestBody,
}

/// <summary>A kit's attend-producer flavor — the authored constants the deterministic attend producer shapes an
/// entity's gap-filling intent with while a target is in range. A kit without one cannot attend. SIM-AFFECTING:
/// compiled once, never re-read as float.</summary>
/// <param name="NoticeRadius">Where an in-band target is acquired (the acquire threshold).</param>
/// <param name="ReleaseRadius">Where an acquired target is dropped back to the wander flavor (must exceed
/// <paramref name="NoticeRadius"/> — the hysteresis band that stops edge flicker).</param>
/// <param name="StandoffRadius">The distance the body holds off its target at (the orbit standoff).</param>
/// <param name="Approach">The forward closure deflection while outside standoff (0..1).</param>
/// <param name="Orbit">The strafe deflection held at standoff — the lateral amble (0..1; 0 = face and hold).</param>
/// <param name="FaceTarget">Whether the body turns to face its target.</param>
/// <param name="Target">What the body steers toward.</param>
internal readonly record struct AttendFlavor(float NoticeRadius, float ReleaseRadius, float StandoffRadius,
    float Approach, float Orbit, bool FaceTarget, AttendTarget Target);

/// <summary>The one-time fixed-point compilation of an <see cref="AttendFlavor"/>. Radii compile to SQUARES so the
/// producer never takes a square root per body per tick.</summary>
internal readonly record struct FixedAttendFlavor(FixedQ4816 NoticeRadiusSquared, FixedQ4816 ReleaseRadiusSquared,
    FixedQ4816 StandoffRadius, FixedQ4816 Approach, FixedQ4816 Orbit, bool FaceTarget, AttendTarget Target) {
    /// <summary>Compiles an authored attend flavor to fixed point (the once-at-the-boundary rule), or <see langword="null"/>
    /// for a kit that declares none (it cannot attend).</summary>
    public static FixedAttendFlavor? Compile(AttendFlavor? flavor) {
        if (flavor is not { } value) {
            return null;
        }

        var notice = FixedQ4816.FromDouble(value: value.NoticeRadius);
        var release = FixedQ4816.FromDouble(value: value.ReleaseRadius);

        return new FixedAttendFlavor(
            NoticeRadiusSquared: (notice * notice),
            ReleaseRadiusSquared: (release * release),
            StandoffRadius: FixedQ4816.FromDouble(value: value.StandoffRadius),
            Approach: FixedQ4816.FromDouble(value: value.Approach),
            Orbit: FixedQ4816.FromDouble(value: value.Orbit),
            FaceTarget: value.FaceTarget,
            Target: value.Target
        );
    }
}

/// <summary>One locomotion kit — a world-definition row naming a way of moving: the integrator it runs under
/// (<see cref="Puck.World.Protocol.MotionModel"/>, an engine fact selected per row), the locomotion/jump tuning its
/// bodies compile, its wander-producer flavor, and its action-lane bindings. Every game-flavored movement noun is a
/// row of this data, never an engine enum; the census echo prints these names.</summary>
/// <param name="Name">The kit's kebab-case name (the census echo token).</param>
/// <param name="Model">The motion model the kit's bodies integrate under.</param>
/// <param name="Tuning">The locomotion/jump feel the kit's bodies compile (a seat's profile speeds still override).</param>
/// <param name="Flavor">The wander-producer flavor.</param>
/// <param name="PrimaryAction">The <see cref="Puck.World.Protocol.ActionLanes.Primary"/> binding, or <see langword="null"/> unbound.</param>
/// <param name="SecondaryAction">The <see cref="Puck.World.Protocol.ActionLanes.Secondary"/> binding, or <see langword="null"/> unbound.</param>
/// <param name="Collider">The kit's body VOLUME — a vertical capsule solved against the world contact field, or
/// <see langword="null"/> for a kit with no volume (never solved against the field). Omitted from the wire when null.</param>
/// <param name="Attend">The attend-producer flavor a body wakes on under <see cref="Puck.World.Protocol.IntentSource.Attend"/>,
/// or <see langword="null"/> for a kit that cannot attend (validation rejects an <c>Attend</c>-sourced inhabitant on a
/// kit with no flavor). Omitted from the wire when null.</param>
internal sealed record WorldKit(
    string Name,
    MotionModel Model,
    MotionTuning Tuning,
    WanderFlavor Flavor,
    ActionSpec? PrimaryAction,
    ActionSpec? SecondaryAction,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCollider? Collider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AttendFlavor? Attend = null
);

/// <summary>The flattened, fixed-point form of one predicate (a conjunction element).</summary>
internal readonly record struct CompiledPredicate(ActionFact Fact, int RecencySlot, ulong WindowTicks, int UsesLimit, CompiledPredicateKind Kind);

/// <summary>The compiled predicate dispatch tag.</summary>
internal enum CompiledPredicateKind : byte {
    Now,
    Recently,
    CooldownElapsed,
    UsesBelow,
}

/// <summary>The compiled effect dispatch tag.</summary>
internal enum CompiledEffectKind : byte {
    SetVerticalVelocity,
    ScaleVerticalVelocity,
    PlanarImpulse,
    StartCooldown,
    ConsumeUse,
}

/// <summary>The fixed-point form of one effect: <paramref name="Value"/> is the velocity/factor/speed scalar,
/// <paramref name="Direction"/> the body-frame impulse direction, <paramref name="DurationTicks"/> the
/// duration/cooldown in engine ticks.</summary>
internal readonly record struct CompiledEffect(CompiledEffectKind Kind, FixedQ4816 Value, FixedVector3 Direction, ulong DurationTicks);

/// <summary>One compiled trigger channel: the flattened conjunction gate, the press latch in engine ticks, and the
/// fixed-point effects in authored order.</summary>
internal sealed record CompiledTrigger(CompiledPredicate[] Gate, ulong LatchTicks, CompiledEffect[] Effects);

/// <summary>A lane binding compiled once before simulation: both trigger channels plus the recency-clock table (one
/// slot per <see cref="ActionPredicate.Recently"/> instance across both gates — the per-tick clock updater walks it).</summary>
internal sealed record CompiledActionSpec(CompiledTrigger? OnPress, CompiledTrigger? OnRelease, ActionFact[] RecencyFacts, ulong[] RecencyWindows) {
    /// <summary>Compiles an authored binding: predicates flatten (nested <see cref="ActionPredicate.All"/>
    /// conjunctions concatenate), seconds become engine ticks, floats become fixed point — once, at the boundary.</summary>
    /// <param name="spec">The authored binding, or <see langword="null"/> for an unbound lane.</param>
    public static CompiledActionSpec? Compile(ActionSpec? spec) {
        if (spec is null) {
            return null;
        }

        var recencyFacts = new List<ActionFact>();
        var recencyWindows = new List<ulong>();
        var onPress = CompileTrigger(trigger: spec.OnPress, recencyFacts: recencyFacts, recencyWindows: recencyWindows);
        var onRelease = CompileTrigger(trigger: spec.OnRelease, recencyFacts: recencyFacts, recencyWindows: recencyWindows);

        return new CompiledActionSpec(
            OnPress: onPress,
            OnRelease: onRelease,
            RecencyFacts: recencyFacts.ToArray(),
            RecencyWindows: recencyWindows.ToArray()
        );
    }

    private static CompiledTrigger? CompileTrigger(ActionTrigger? trigger, List<ActionFact> recencyFacts, List<ulong> recencyWindows) {
        if (trigger is null) {
            return null;
        }

        var gate = new List<CompiledPredicate>();

        FlattenPredicate(predicate: trigger.Gate, gate: gate, recencyFacts: recencyFacts, recencyWindows: recencyWindows);

        var effects = new CompiledEffect[trigger.Effects.Count];

        for (var index = 0; (index < effects.Length); index++) {
            effects[index] = CompileEffect(effect: trigger.Effects[index]);
        }

        return new CompiledTrigger(
            Gate: gate.ToArray(),
            LatchTicks: DurationTicks(seconds: trigger.LatchSeconds),
            Effects: effects
        );
    }

    // Flattens a predicate ADT into a fixed-point conjunction gate, allocating one shared recency slot per Recently
    // instance. Promoted to internal so the motion-response compiler (a non-lane caller) reuses the same slotting.
    internal static void FlattenPredicate(ActionPredicate? predicate, List<CompiledPredicate> gate, List<ActionFact> recencyFacts, List<ulong> recencyWindows) {
        switch (predicate) {
            case null:
                break;
            case ActionPredicate.All all:
                foreach (var inner in all.Predicates) {
                    FlattenPredicate(predicate: inner, gate: gate, recencyFacts: recencyFacts, recencyWindows: recencyWindows);
                }

                break;
            case ActionPredicate.Now now:
                gate.Add(item: new CompiledPredicate(Fact: now.Fact, RecencySlot: 0, WindowTicks: 0UL, UsesLimit: 0, Kind: CompiledPredicateKind.Now));

                break;
            case ActionPredicate.Recently recently:
                gate.Add(item: new CompiledPredicate(Fact: recently.Fact, RecencySlot: recencyFacts.Count, WindowTicks: 0UL, UsesLimit: 0, Kind: CompiledPredicateKind.Recently));
                recencyFacts.Add(item: recently.Fact);
                recencyWindows.Add(item: DurationTicks(seconds: recently.WindowSeconds));

                break;
            case ActionPredicate.CooldownElapsed:
                gate.Add(item: new CompiledPredicate(Fact: default, RecencySlot: 0, WindowTicks: 0UL, UsesLimit: 0, Kind: CompiledPredicateKind.CooldownElapsed));

                break;
            case ActionPredicate.UsesBelow uses:
                gate.Add(item: new CompiledPredicate(Fact: default, RecencySlot: 0, WindowTicks: 0UL, UsesLimit: uses.Limit, Kind: CompiledPredicateKind.UsesBelow));

                break;
        }
    }

    private static CompiledEffect CompileEffect(ActionEffect effect) {
        return effect switch {
            ActionEffect.SetVerticalVelocity set => new CompiledEffect(
                Kind: CompiledEffectKind.SetVerticalVelocity,
                Value: FixedQ4816.FromDouble(value: set.Velocity),
                Direction: default,
                DurationTicks: 0UL
            ),
            ActionEffect.ScaleVerticalVelocity scale => new CompiledEffect(
                Kind: CompiledEffectKind.ScaleVerticalVelocity,
                Value: FixedQ4816.FromDouble(value: scale.Factor),
                Direction: default,
                DurationTicks: 0UL
            ),
            ActionEffect.PlanarImpulse impulse => new CompiledEffect(
                Kind: CompiledEffectKind.PlanarImpulse,
                Value: FixedQ4816.FromDouble(value: impulse.Speed),
                Direction: new FixedVector3(
                    X: FixedQ4816.FromDouble(value: impulse.BodyDirection.X),
                    Y: FixedQ4816.FromDouble(value: impulse.BodyDirection.Y),
                    Z: FixedQ4816.FromDouble(value: impulse.BodyDirection.Z)
                ),
                DurationTicks: DurationTicks(seconds: impulse.DurationSeconds)
            ),
            ActionEffect.StartCooldown cooldown => new CompiledEffect(
                Kind: CompiledEffectKind.StartCooldown,
                Value: default,
                Direction: default,
                DurationTicks: DurationTicks(seconds: cooldown.Seconds)
            ),
            _ => new CompiledEffect(Kind: CompiledEffectKind.ConsumeUse, Value: default, Direction: default, DurationTicks: 0UL),
        };
    }

    // Seconds → engine ticks through the same FromDouble + round-up path the runtime tuning conversions ride.
    private static ulong DurationTicks(float seconds) {
        return WorldBody.DurationEngineTicks(duration: FixedQ4816.FromDouble(value: seconds));
    }
}

/// <summary>A <see cref="WorldKit"/>'s wander flavor compiled to fixed point once before simulation, plus its
/// compiled lane bindings — the row the producer and body construction read.</summary>
internal readonly record struct FixedWorldKit(
    MotionModel Model,
    FixedQ4816 Forward,
    bool DriftForward,
    FixedQ4816 StrafeWave,
    FixedQ4816 TurnWave,
    FixedQ4816 UpWave,
    FixedQ4816 PitchWave,
    FixedQ4816 RollTurn,
    FixedQ4816 PrimaryThreshold,
    FixedQ4816 AltitudeBase,
    FixedQ4816 AltitudeRange,
    CompiledActionSpec? Primary,
    CompiledActionSpec? Secondary,
    FixedWorldCollider? Collider,
    FixedAttendFlavor? Attend
) {
    /// <summary>Compiles a kit row's authored floats to fixed point (the once-at-the-boundary rule).</summary>
    public static FixedWorldKit Compile(WorldKit kit) => new(
        Model: kit.Model,
        Forward: FixedQ4816.FromDouble(value: kit.Flavor.Forward),
        DriftForward: kit.Flavor.DriftForward,
        StrafeWave: FixedQ4816.FromDouble(value: kit.Flavor.StrafeWave),
        TurnWave: FixedQ4816.FromDouble(value: kit.Flavor.TurnWave),
        UpWave: FixedQ4816.FromDouble(value: kit.Flavor.UpWave),
        PitchWave: FixedQ4816.FromDouble(value: kit.Flavor.PitchWave),
        RollTurn: FixedQ4816.FromDouble(value: kit.Flavor.RollTurn),
        PrimaryThreshold: FixedQ4816.FromDouble(value: kit.Flavor.PrimaryThreshold),
        AltitudeBase: FixedQ4816.FromDouble(value: kit.Flavor.AltitudeBase),
        AltitudeRange: FixedQ4816.FromDouble(value: kit.Flavor.AltitudeRange),
        Primary: CompiledActionSpec.Compile(spec: kit.PrimaryAction),
        Secondary: CompiledActionSpec.Compile(spec: kit.SecondaryAction),
        Collider: FixedWorldCollider.Compile(collider: kit.Collider),
        Attend: FixedAttendFlavor.Compile(flavor: kit.Attend)
    );
}

/// <summary>
/// One row of the world's static scene — a shape smooth-unioned into the accumulated field, addressed by its stable
/// <paramref name="Id"/> (its mutation address; the <c>UpsertSceneRow</c>/<c>RemoveSceneRow</c> whole-row key).
/// The geometry is PRESENTATION-ONLY, but the <paramref name="Solid"/> facet is SIM-AFFECTING when present: the analytic
/// contact field derives a convex collider from the row's shape, so a slice of a scene row now feeds
/// <see cref="WorldBody.Advance"/>. The id carries no meaning beyond identity. The <c>$type</c> string is the JSON
/// discriminator, matching <see cref="WorldCamera"/>'s convention; a new row kind is a new derived record plus its
/// <see cref="JsonDerivedTypeAttribute"/> line.
/// </summary>
/// <param name="Id">The row's stable string id (unique within the scene).</param>
/// <param name="Center">The shape's world-space center (its translate offset from the origin) — the position every
/// manipulation edits.</param>
/// <param name="Emission">The row's emission facet (a synth voice the shape itself makes — see
/// <see cref="WorldEmission"/>), or <see langword="null"/> for silent. Omitted from the wire when null.</param>
/// <param name="Solid">The row's solidity facet (see <see cref="WorldSolid"/>) — SIM-AFFECTING: when present the analytic
/// contact field derives a convex collider from the row's shape and bodies bump into and stand on it. <see langword="null"/>
/// means decoration — bodies pass through it. Omitted from the wire when null.</param>
[JsonDerivedType(typeof(WorldSceneRow.Boulder), typeDiscriminator: "boulder")]
[JsonDerivedType(typeof(WorldSceneRow.Slab), typeDiscriminator: "slab")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal abstract record WorldSceneRow(
    string Id,
    Vector3 Center,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldEmission? Emission,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSolid? Solid
) {
    /// <summary>A stone boulder — a sphere carrying the scene's shared <see cref="WorldScene.StoneAlbedo"/>.</summary>
    /// <param name="Id">The row's stable string id.</param>
    /// <param name="Center">The sphere's world-space center.</param>
    /// <param name="Radius">The sphere radius.</param>
    /// <param name="Smooth">The smooth-union blend radius that melds it into the field.</param>
    /// <param name="Emission">The row's emission facet, or <see langword="null"/> for silent.</param>
    /// <param name="Solid">The row's solidity facet (a sphere of <c>Radius + Margin</c>), or <see langword="null"/>.</param>
    internal sealed record Boulder(string Id, Vector3 Center, float Radius, float Smooth, WorldEmission? Emission = null, WorldSolid? Solid = null) : WorldSceneRow(Id: Id, Center: Center, Emission: Emission, Solid: Solid);

    /// <summary>A terrain slab — a rounded box patch (a plaza tile, a step, a wall segment) whose material is data on
    /// the row.</summary>
    /// <param name="Id">The row's stable string id.</param>
    /// <param name="Center">The box's world-space center.</param>
    /// <param name="HalfExtents">The box half-extents along the world axes.</param>
    /// <param name="Round">The corner-rounding radius.</param>
    /// <param name="Smooth">The smooth-union blend radius that melds it into the field.</param>
    /// <param name="Albedo">The slab's own albedo (per-row material, unlike the shared boulder stone).</param>
    /// <param name="Emission">The row's emission facet, or <see langword="null"/> for silent.</param>
    /// <param name="Solid">The row's solidity facet (a box of <c>HalfExtents + Margin</c>), or <see langword="null"/>.</param>
    internal sealed record Slab(string Id, Vector3 Center, Vector3 HalfExtents, float Round, float Smooth, Vector3 Albedo, WorldEmission? Emission = null, WorldSolid? Solid = null) : WorldSceneRow(Id: Id, Center: Center, Emission: Emission, Solid: Solid);

    /// <summary>Returns this row with a replaced <see cref="Center"/> — the shape-preserving move every drag commit and
    /// numeric move composes through.</summary>
    /// <param name="center">The new world-space center.</param>
    public WorldSceneRow WithCenter(Vector3 center) => this switch {
        Boulder boulder => (boulder with { Center = center }),
        Slab slab => (slab with { Center = center }),
        _ => this,
    };
}

/// <summary>The world's static scene — a ground plane plus the shape rows. The materials are inline albedo colors (not
/// palette indices): the frame source allocates one grass material, one per-row material (a boulder's from
/// <see cref="StoneAlbedo"/>, a slab's from its own row), and iterates the <see cref="Rows"/>. The scene is
/// PRESENTATION-ONLY except for any row carrying a <see cref="WorldSolid"/> facet, which is SIM-AFFECTING (contact).</summary>
/// <param name="GroundAlbedo">The grass ground plane's albedo.</param>
/// <param name="StoneAlbedo">The shared albedo every boulder row renders with.</param>
/// <param name="Rows">The scene's shape rows, emitted in order after the ground plane.</param>
internal sealed record WorldScene(
    Vector3 GroundAlbedo,
    Vector3 StoneAlbedo,
    IReadOnlyList<WorldSceneRow> Rows
) {
    /// <summary>The built-in default scene — the grass-and-boulders world.</summary>
    public static WorldScene Default { get; } = new WorldScene(
        GroundAlbedo: new Vector3(x: 0.33f, y: 0.52f, z: 0.24f),
        StoneAlbedo: new Vector3(x: 0.55f, y: 0.55f, z: 0.58f),
        Rows: [
            new WorldSceneRow.Boulder(Id: "boulder-1", Center: new Vector3(x: -1.2f, y: 0.72f, z: -0.3f), Radius: 0.9f, Smooth: 0.5f),
            new WorldSceneRow.Boulder(Id: "boulder-2", Center: new Vector3(x: 0.6f, y: 0.88f, z: 0.5f), Radius: 1.1f, Smooth: 0.5f),
            new WorldSceneRow.Boulder(Id: "boulder-3", Center: new Vector3(x: 1.9f, y: 0.48f, z: -0.7f), Radius: 0.6f, Smooth: 0.4f),
            new WorldSceneRow.Boulder(Id: "boulder-4", Center: new Vector3(x: -0.3f, y: 0.38f, z: 1.3f), Radius: 0.45f, Smooth: 0.35f),
            new WorldSceneRow.Boulder(Id: "boulder-5", Center: new Vector3(x: 2.4f, y: 0.62f, z: 0.7f), Radius: 0.75f, Smooth: 0.4f),
        ]
    );
}

/// <summary>
/// One creation ASSET row — a whole <c>puck.creation.v1</c> document embedded INLINE-CANONICAL in the world
/// file with its identity hash pinned beside it. The document and hash MUST come from the SAME
/// <see cref="Puck.Authoring.CanonicalDocument{TDocument}"/>: the compose boundary canonicalizes on upsert
/// and rejects a hash the pipeline did not itself compute; the validator re-verifies the pin on every candidate, so a
/// tampered world file rejects loudly. World files stay self-contained — the CAS is an authoring-time import/export
/// cache, never a load-time dependency.
/// </summary>
/// <param name="Id">The row's stable string id — its mutation address and the handle placements reference.</param>
/// <param name="Document">The canonical (validated + normalized) creation document.</param>
/// <param name="Hash">The SHA-256 hex64 of the document's canonical bytes (<see cref="Puck.Authoring.CanonicalDocument{TDocument}.Hash"/>
/// on the canonical result the compose boundary produces).</param>
internal sealed record WorldCreation(string Id, CreationDocument Document, string Hash);

/// <summary>A placement's repeat facet — a row of copies IS a repeat (the Demo placement vocabulary, adopted): the
/// stamp replays on a placement-local X/Z lattice. A row longer than <see cref="WorldAuthoringDefaults.MaxRepeatPerSegment"/>
/// on an axis auto-splits into several emitted segments so each instance bound stays tight.</summary>
/// <param name="SpacingX">The per-copy spacing along the placement's local X.</param>
/// <param name="SpacingZ">The per-copy spacing along the placement's local Z.</param>
/// <param name="CountX">The copy count along X (1 = no repeat on the axis).</param>
/// <param name="CountZ">The copy count along Z.</param>
internal sealed record WorldPlacementRepeat(float SpacingX, float SpacingZ, int CountX, int CountZ) {
    /// <summary>The total copy count across both axes.</summary>
    public int TotalCount => (Math.Max(val1: CountX, val2: 1) * Math.Max(val1: CountZ, val2: 1));
}

/// <summary>A placement's INHABIT facet — the row's binding to live population bodies. An inhabited placement is a
/// normal entry in the entity table: it holds a <see cref="Puck.World.Server.WorldBody"/>, integrates under the named
/// kit, and is addressable as <see cref="WorldAnchor.Entity"/> like any avatar. Its stamp rides the body's pose instead
/// of the row's static transform; the row's position/yaw become its SPAWN pose. Absent (null) = decoration, the
/// unchanged furniture behaviour.</summary>
/// <param name="Kit">The <see cref="WorldKit.Name"/> the bodies move under. Null resolves the creation's own
/// <see cref="Puck.Authoring.CreationBehaviorDocument.Locomotion"/> token AS a kit name — a creation declaring "swim"
/// inhabits the world's kit row named "swim". Neither resolving is a loud rejection naming every kit the world
/// declares.</param>
/// <param name="Look">The <see cref="WorldLook.Name"/> the bodies wear, or null to wear an implicit creation look on
/// this placement's own <c>CreationId</c>.</param>
/// <param name="Source">The gap-filling producer the bodies wake on (Idle/Wander/Attend; Live is legal and means
/// "nothing drives it until a principal does").</param>
/// <param name="Count">How many bodies (1..<see cref="Puck.World.Server.WorldPopulation.MaxPopulationSimulated"/>).</param>
/// <param name="Radius">The phyllotaxis scatter radius about the placement position; 0 stacks them.</param>
internal sealed record WorldPlacementInhabit(
    string? Kit,
    string? Look,
    Puck.World.Protocol.IntentSource Source,
    int Count = 1,
    float Radius = 0f
);

/// <summary>A per-instance override of one declared creation face's feed — the face twin of the emission facet's
/// per-instance override channel.</summary>
/// <param name="Face">The declared <see cref="Puck.Authoring.CreationFaceDocument.Name"/> to override.</param>
/// <param name="Source">The screen source the face shows, in the existing <see cref="WorldScreenSource"/> vocabulary.</param>
internal sealed record WorldPlacementFace(string Face, WorldScreenSource Source);

/// <summary>
/// One placement INSTANCE row — a creation asset stamped into the world by reference: transform + facets as
/// data, addressed by its stable <paramref name="Id"/>. A placement whose creation carries timeline frames is
/// ANIMATED: it replays client-side on the render clock through the reserved dynamic-transform pool (repeat/mirror
/// facets are static-stamp-only and reject on an animated row). A placement carrying an <paramref name="Inhabit"/>
/// facet is a live population body rather than furniture (see <see cref="WorldPlacementInhabit"/>); its declared
/// creation eyes derive <see cref="WorldCamera"/> feeds and its declared faces derive screens (both at the delivery
/// boundary, never written to the document).
/// </summary>
/// <param name="Id">The row's stable string id (its mutation address).</param>
/// <param name="CreationId">The referenced <see cref="WorldCreation.Id"/> (must resolve; removal of a referenced
/// creation rejects loudly).</param>
/// <param name="Position">The stamp position, world space.</param>
/// <param name="YawDegrees">The stamp yaw about +Y, degrees.</param>
/// <param name="Scale">The uniform stamp scale (clamped to the placement policy envelope by validation).</param>
/// <param name="Repeat">The repeat facet, or <see langword="null"/> for a single copy.</param>
/// <param name="Mirror">The symmetry fold axis (<c>x</c> or <c>z</c> in the placement's local frame), or
/// <see langword="null"/> for none.</param>
/// <param name="Emission">The placement's emission facet (a synth voice the stamp itself makes — see
/// <see cref="WorldEmission"/>), or <see langword="null"/> for silent. Under <paramref name="Repeat"/> the emission
/// binds to the placement ROOT only. Omitted from the wire when null.</param>
/// <param name="Solid">The placement's solidity facet (see <see cref="WorldSolid"/>) — needs the FIELD contact provider
/// (Arc 2); a solid placement under the analytic provider is a loud validator error. Omitted from the wire when null.</param>
/// <param name="Inhabit">The inhabit facet (null = decoration), binding the row to live population bodies. Omitted from
/// the wire when null.</param>
/// <param name="FaceSources">Per-instance overrides of the creation's declared faces (null = every face shows its
/// declared default). Omitted from the wire when null.</param>
internal sealed record WorldPlacement(
    string Id,
    string CreationId,
    Vector3 Position,
    float YawDegrees,
    float Scale,
    WorldPlacementRepeat? Repeat = null,
    string? Mirror = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldEmission? Emission = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSolid? Solid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementInhabit? Inhabit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldPlacementFace>? FaceSources = null
);

/// <summary>
/// The signal carried by a <see cref="WorldScreen"/>'s lit face. A source declares which provider feeds a slot; the
/// engine resolves and samples it. The <c>$type</c> string is the JSON discriminator, matching
/// <see cref="Puck.Scene.ScreenSourceProvider"/>'s convention; a new source kind is a new derived record plus its
/// <see cref="JsonDerivedTypeAttribute"/> line.
/// </summary>
[JsonDerivedType(typeof(WorldScreenSource.None), typeDiscriminator: "none")]
[JsonDerivedType(typeof(WorldScreenSource.TestPattern), typeDiscriminator: "testPattern")]
[JsonDerivedType(typeof(WorldScreenSource.Machine), typeDiscriminator: "machine")]
[JsonDerivedType(typeof(WorldScreenSource.Camera), typeDiscriminator: "camera")]
[JsonDerivedType(typeof(WorldScreenSource.View), typeDiscriminator: "view")]
[JsonDerivedType(typeof(WorldScreenSource.Capture), typeDiscriminator: "capture")]
[JsonDerivedType(typeof(WorldScreenSource.Console), typeDiscriminator: "console")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal abstract record WorldScreenSource {
    private WorldScreenSource() {
    }

    /// <summary>No provider is bound — the engine lights the slot with its procedural no-signal fallback (an animated
    /// test-card / striped no-signal look, NEVER black).</summary>
    internal sealed record None() : WorldScreenSource;

    /// <summary>The deterministic animated test pattern (<see cref="Puck.SdfVm.Views.TestPatternSource"/>), rendered
    /// from the world's sim tick (never the wall clock) into a CPU buffer and uploaded each frame.</summary>
    /// <param name="Width">The pattern framebuffer width in pixels.</param>
    /// <param name="Height">The pattern framebuffer height in pixels.</param>
    internal sealed record TestPattern(int Width, int Height) : WorldScreenSource;

    /// <summary>An arbitrary deterministic machine's unresampled framebuffer — resolved against a registered
    /// <see cref="Puck.Abstractions.Machines.IScreenMachineEngine"/> by <paramref name="Engine"/> id. The world never
    /// names a concrete machine: the engine owns its <paramref name="Options"/> vocabulary (a GamingBrick reads a
    /// dmg/cgb/agb model + a dmgspeed pin).</summary>
    /// <param name="Engine">The screen-machine engine id (e.g. <c>gaming-brick</c>).</param>
    /// <param name="ContentPath">The content file (a cartridge ROM) the machine boots, or empty when the screen is
    /// unconfigured — the binder faults the slot gracefully (no crash, no-signal card) rather than booting.</param>
    /// <param name="Options">The engine-specific options string, or <see langword="null"/> for the engine's defaults.</param>
    internal sealed record Machine(string Engine, string ContentPath, string? Options) : WorldScreenSource;

    /// <summary>The platform's default live camera feed, with an explicit preferred capture profile. The platform may
    /// negotiate a nearby extent; every screen sampling the same physical default device shares one session.</summary>
    /// <param name="Profile">The preferred capture extent and maximum upload cadence.</param>
    internal sealed record Camera(WorldFeedProfile Profile) : WorldScreenSource;

    /// <summary>A named view from the presentation view stack, such as a monitor showing another camera's output.</summary>
    /// <param name="CameraName">The registered view name this slot samples.</param>
    internal sealed record View(string CameraName) : WorldScreenSource;

    /// <summary>A live compositor capture feed — a desktop window keyed by title, or a whole monitor keyed by index. The
    /// selector is the altitude of the primitive: <paramref name="MonitorIndex"/> null is window mode; non-null is
    /// whole-monitor mode (and <paramref name="WindowTitle"/> is unused).</summary>
    /// <param name="WindowTitle">The captured window's title (window mode; ignored when <paramref name="MonitorIndex"/> is set).</param>
    /// <param name="Profile">This capture consumer's output extent and maximum refresh cadence.</param>
    /// <param name="MonitorIndex">The 0-based monitor to capture whole (0 = primary), or <see langword="null"/> for window mode.</param>
    internal sealed record Capture(string WindowTitle, WorldFeedProfile Profile, int? MonitorIndex = null) : WorldScreenSource;

    /// <summary>A screen showing the DEVELOPER CONSOLE as an object in the world — the diegetic half of the control plane
    /// the unification contract names ("the on-screen panel AND process stdin"). The frame is CPU-composed into a
    /// CRT-styled framebuffer and pushed through <c>IGpuSurfaceUpload</c>, exactly as the ported console feed does;
    /// nothing about it is a render-graph node. Complementary to — never a duplicate of — <c>WorldConsoleMirror</c>,
    /// which publishes the SAME content to the screen-space overlay. At most one <c>console</c> source may be LIVE
    /// (declared) at a time; an unselected console entry sitting in a magazine is legal.</summary>
    /// <param name="Rows">Console text rows the framebuffer composes, 1..120. Sizes the CPU buffer.</param>
    /// <param name="Columns">Console text columns, 1..400.</param>
    /// <param name="Procedural">When true the slot shows the sibling generated pattern instead of console text — carried
    /// as a MODE of this variant rather than as a seventh union case.</param>
    internal sealed record Console(int Rows = 24, int Columns = 64, bool Procedural = false) : WorldScreenSource;
}

/// <summary>An ordered set of sources one screen may show, plus the entry its selector starts on — the cycle primitive. A
/// selection is a POINTER into this list; changing it never changes how many screen slots exist, so a magazine costs no
/// render envelope. Entries are the same closed <see cref="WorldScreenSource"/> vocabulary the declared source uses, so a
/// screen may rotate a cartridge, the webcam, and a jumbotron view through one slot.</summary>
/// <param name="Entries">The ordered source list (at least one entry).</param>
/// <param name="Selected">The 0-based entry the selector STARTS on (what <c>screen.select</c> advances from), not what the
/// screen boots showing — a screen always wakes on its declared <c>Source</c> (the one-live-console ceiling depends on
/// this). Live selection drifts from this and is folded back by <c>world.save</c> (see <see cref="WorldSessionCapture"/>).</param>
/// <param name="Wrap">Whether advancing past the last entry returns to the first (the arcade cabinet's wrapping cycle);
/// when false the selector clamps at both ends.</param>
internal sealed record WorldScreenMagazine(IReadOnlyList<WorldScreenSource> Entries, int Selected = 0, bool Wrap = true);

/// <summary>A cable-linked group of screens whose machines advance as ONE interleaved unit. The binder steps the link,
/// never its members individually, so the engine's deterministic interleave — not the host's frame order — decides who
/// runs when. Every member must resolve to a machine from the SAME engine, and that engine must implement
/// <c>IMachineLinkingEngine</c>; a link whose members do not currently satisfy that is reported dormant, never silently
/// dropped.</summary>
/// <param name="Name">The link's stable kebab-case name (its mutation address).</param>
/// <param name="Screens">The engine screen indices in cable order (2 or more, no duplicates).</param>
internal sealed record WorldScreenLink(string Name, IReadOnlyList<int> Screens);

/// <summary>A live screen feed's requested output policy. It belongs to the source declaration rather than the binder,
/// so two window captures can choose different extents and cadences. Camera extents are preferences because a physical
/// device remains authoritative for its negotiated format.</summary>
/// <param name="Width">Requested output width in pixels.</param>
/// <param name="Height">Requested output height in pixels.</param>
/// <param name="RefreshRateHz">Maximum pull/upload cadence; it must divide the engine time base exactly.</param>
internal readonly record struct WorldFeedProfile(int Width, int Height, uint RefreshRateHz) {
    /// <summary>The fallback used by runtime screen verbs that do not provide an authored source profile.</summary>
    public static WorldFeedProfile Default { get; } = new(Width: 320, Height: 240, RefreshRateHz: 30U);
}

/// <summary>The route policy a <see cref="WorldScreen"/> carries: whether a player may engage the screen, the activation
/// radius, whether engaging auto-boots the selected magazine entry, and the world-event channels a gesture drives it
/// through. The optional members each default to the inert choice: no auto-boot and no gesture channel.</summary>
/// <param name="Engageable">Whether a player may engage this screen.</param>
/// <param name="EngageRadius">The world-unit radius a player must be inside to engage (meaningful only when
/// <paramref name="Engageable"/>). Validated finite and non-negative.</param>
/// <param name="AutoInsert">When set, engaging the screen first boots the selected magazine entry (the "walk over, press
/// the button, the screen lights" gesture), so the interaction is one act rather than an insert then an engage.</param>
/// <param name="EngageChannel">The world-event channel whose arrival on a body engages this screen, or
/// <see langword="null"/> (the default) for a route that does not answer gestures. THE NAME IS THE AUTHOR'S: nothing in
/// the engine special-cases a spelling. Omitted from the wire when null.</param>
/// <param name="CycleChannel">Same, for advancing the magazine selector. Omitted from the wire when null.</param>
internal readonly record struct WorldScreenRoute(bool Engageable, float EngageRadius, bool AutoInsert = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EngageChannel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CycleChannel = null) {
    /// <summary>A screen no player engages (the default for a passive display).</summary>
    public static WorldScreenRoute Passive { get; } = new WorldScreenRoute(Engageable: false, EngageRadius: 0f);
}

/// <summary>One diegetic screen in the world — a screen slab emitted by
/// <see cref="Puck.SdfVm.SdfProgramBuilder"/> whose lit face
/// samples a bound source (or the procedural fallback when unbound). The frame (<see cref="Origin"/>/<see cref="Right"/>/
/// <see cref="Up"/> + <see cref="HalfWidth"/>/<see cref="HalfHeight"/>) is the SAMPLED surface frame and must match the
/// slab's placement (it rhymes with <see cref="Puck.Scene.ScreenSlabObject"/>'s explicit world frame); the frame
/// source bakes the geometry translate from it.</summary>
/// <param name="Index">The engine screen-surface index (0..<see cref="Puck.SdfVm.SdfProgramBuilder.MaxScreenSurfaces"/>−1)
/// this slab declares — the key the source/light providers bind under.</param>
/// <param name="Origin">The front face's world-space center (the sampled surface origin); the geometry center sits one
/// <see cref="HalfDepth"/> behind it along the face normal.</param>
/// <param name="Right">The unit world axis the sampled U increases along (the slab's local +X in world space).</param>
/// <param name="Up">The unit world axis the sampled V increases against — V = 0 at the top (the slab's local +Y in
/// world space).</param>
/// <param name="HalfWidth">The face half-width (the slab's local X half-extent).</param>
/// <param name="HalfHeight">The face half-height (the slab's local Y half-extent).</param>
/// <param name="HalfDepth">The slab's local Z half-extent (its thickness behind the face).</param>
/// <param name="Round">The corner-rounding radius.</param>
/// <param name="Source">The signal the lit face carries.</param>
/// <param name="Route">The engage-route policy.</param>
/// <param name="Solid">The screen slab's solidity facet (a box collider derived from the slab's oriented frame +
/// <c>Margin</c> by <see cref="WorldColliderSet"/>), or <see langword="null"/> for a decorative screen. Omitted from the
/// wire when null.</param>
/// <param name="Magazine">The per-screen source magazine (the cycle primitive), or <see langword="null"/> for a screen
/// with no magazine — nothing to cycle. Omitted from the wire when null — the whole-row <c>UpsertScreen</c>
/// carries it for free, so no new mutation kind is needed.</param>
internal sealed record WorldScreen(
    int Index,
    Vector3 Origin,
    Vector3 Right,
    Vector3 Up,
    float HalfWidth,
    float HalfHeight,
    float HalfDepth,
    float Round,
    WorldScreenSource Source,
    WorldScreenRoute Route,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSolid? Solid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldScreenMagazine? Magazine = null
);

/// <summary>The flattened, fixed-point form of one velocity-response row: the conjunction gate (body-fact predicates
/// only), and the engage/release convergence rates the ramp integrates through the shared rate accumulator.</summary>
internal readonly record struct FixedMotionResponse(CompiledPredicate[] Gate, FixedQ4816 EngageRate, FixedQ4816 ReleaseRate);

/// <summary>The one-time fixed-point compilation of authored motion tuning. Runtime simulation reads only this form.</summary>
/// <remarks>SIM-AFFECTING extension: <see cref="Response"/> promotes a slice of the tuning that the shaping stage of
/// <see cref="WorldBody"/>'s grounded integrator reads. <see cref="ResponseRecencyFacts"/>/<see cref="ResponseRecencyWindows"/>
/// are the shared recency-clock table across every row's <see cref="ActionPredicate.Recently"/> gate (the per-tick clock
/// updater walks it), slotted by the SAME <see cref="CompiledActionSpec.FlattenPredicate"/> the lane bindings use.</remarks>
internal readonly record struct FixedMotionTuning(
    FixedQ4816 MoveSpeed,
    FixedQ4816 TurnSpeed,
    FixedQ4816 GroundY,
    FixedQ4816 JumpSpeed,
    FixedQ4816 RiseGravity,
    FixedQ4816 FallGravity,
    FixedQ4816 MaxFallSpeed,
    FixedQ4816 CoyoteTime,
    FixedQ4816 JumpBufferTime,
    FixedQ4816 JumpCutMultiplier,
    FixedMotionResponse[] Response,
    ActionFact[] ResponseRecencyFacts,
    ulong[] ResponseRecencyWindows
) {
    /// <summary>The number of recency clocks the response table's Recently gates share.</summary>
    public int RecencySlots => ResponseRecencyFacts.Length;

    /// <summary>Compiles the authored floating-point motion tuning to its fixed-point form.</summary>
    public static FixedMotionTuning Compile(in MotionTuning tuning) {
        var rows = tuning.Response;
        var response = new FixedMotionResponse[rows.Count];
        var recencyFacts = new List<ActionFact>();
        var recencyWindows = new List<ulong>();

        for (var index = 0; (index < rows.Count); index++) {
            var gate = new List<CompiledPredicate>();

            // The response table shares ONE recency-clock table across all rows (as one lane's press/release channels
            // share one), slotted by the same predicate flattener the action lanes use.
            CompiledActionSpec.FlattenPredicate(predicate: rows[index].Gate, gate: gate, recencyFacts: recencyFacts, recencyWindows: recencyWindows);

            response[index] = new FixedMotionResponse(
                Gate: gate.ToArray(),
                EngageRate: FixedQ4816.FromDouble(value: rows[index].EngageRate),
                ReleaseRate: FixedQ4816.FromDouble(value: rows[index].ReleaseRate)
            );
        }

        return new(
            MoveSpeed: FixedQ4816.FromDouble(value: tuning.MoveSpeed),
            TurnSpeed: FixedQ4816.FromDouble(value: tuning.TurnSpeed),
            GroundY: FixedQ4816.FromDouble(value: tuning.GroundY),
            JumpSpeed: FixedQ4816.FromDouble(value: tuning.JumpSpeed),
            RiseGravity: FixedQ4816.FromDouble(value: tuning.RiseGravity),
            FallGravity: FixedQ4816.FromDouble(value: tuning.FallGravity),
            MaxFallSpeed: FixedQ4816.FromDouble(value: tuning.MaxFallSpeed),
            CoyoteTime: FixedQ4816.FromDouble(value: tuning.CoyoteTime),
            JumpBufferTime: FixedQ4816.FromDouble(value: tuning.JumpBufferTime),
            JumpCutMultiplier: FixedQ4816.FromDouble(value: tuning.JumpCutMultiplier),
            Response: response,
            ResponseRecencyFacts: recencyFacts.ToArray(),
            ResponseRecencyWindows: recencyWindows.ToArray()
        );
    }
}

/// <summary>The one-time fixed-point compilation of a kit's body volume (the vertical capsule).</summary>
internal readonly record struct FixedWorldCollider(FixedQ4816 Radius, FixedQ4816 Height) {
    /// <summary>Compiles an authored collider to fixed point, or <see langword="null"/> for a volumeless kit.</summary>
    public static FixedWorldCollider? Compile(WorldCollider? collider) => ((collider is { } value)
        ? new FixedWorldCollider(Radius: FixedQ4816.FromDouble(value: value.Radius), Height: FixedQ4816.FromDouble(value: value.Height))
        : null);
}

/// <summary>The one-time fixed-point compilation of the world's contact tuning — read by the analytic contact field
/// and the grounded integrator. <see cref="GroundedThreshold"/> is the compiled <c>cos(maxSlopeDegrees)</c> a contact
/// normal's up-alignment must clear to ground a body (the same test both providers use).</summary>
internal readonly record struct FixedWorldCollision(
    bool Enabled,
    WorldContactProvider Provider,
    FixedQ4816 ContactSkin,
    int MaxIterations,
    FixedQ4816 GroundedThreshold,
    FixedQ4816 GradientProbe
) {
    /// <summary>Compiles the authored contact tuning to fixed point.</summary>
    public static FixedWorldCollision Compile(WorldCollision collision) => new(
        Enabled: collision.Enabled,
        Provider: collision.Provider,
        ContactSkin: FixedQ4816.FromDouble(value: collision.ContactSkin),
        MaxIterations: collision.MaxIterations,
        GroundedThreshold: FixedQ4816.FromDouble(value: Math.Cos(d: (collision.MaxSlopeDegrees * (Math.PI / 180.0)))),
        GradientProbe: FixedQ4816.FromDouble(value: collision.GradientProbe)
    );
}

/// <summary>The one-time fixed-point compilation of authored wander tuning. Runtime simulation reads only this form.</summary>
internal readonly record struct FixedWanderTuning(
    FixedQ4816 DriftSpeed,
    FixedQ4816 SoftRadius,
    FixedQ4816 SpawnRadius,
    FixedQ4816 WeaveAmplitude,
    FixedQ4816 InwardGain,
    FixedQ4816 GoldenAngle,
    FixedQ4816 WeaveFrequencyBase,
    FixedQ4816 WeaveFrequencyRange
) {
    /// <summary>Compiles the authored floating-point wander tuning to its fixed-point form.</summary>
    public static FixedWanderTuning Compile(in WanderTuning tuning) => new(
        DriftSpeed: FixedQ4816.FromDouble(value: tuning.DriftSpeed),
        SoftRadius: FixedQ4816.FromDouble(value: tuning.SoftRadius),
        SpawnRadius: FixedQ4816.FromDouble(value: tuning.SpawnRadius),
        WeaveAmplitude: FixedQ4816.FromDouble(value: tuning.WeaveAmplitude),
        InwardGain: FixedQ4816.FromDouble(value: tuning.InwardGain),
        GoldenAngle: FixedQ4816.FromDouble(value: tuning.GoldenAngle),
        WeaveFrequencyBase: FixedQ4816.FromDouble(value: tuning.WeaveFrequencyBase),
        WeaveFrequencyRange: FixedQ4816.FromDouble(value: tuning.WeaveFrequencyRange)
    );
}

/// <summary>One placeable camera in the world — WHERE it rides (<see cref="Anchor"/> + <see cref="Offset"/>) times HOW it
/// frames (<see cref="Rig"/>), two orthogonal axes rather than one welded kind. A <see cref="WorldScreenSource.View"/>
/// resolves the stable name and renders the resulting live view offscreen; an authored layout slot renders it into the
/// main window. Identity, placement, framing, render target — no behaviour hides in a kind.</summary>
/// <param name="Name">The camera's stable name — the handle a View screen / layout slot samples by.</param>
/// <param name="Anchor">What the camera rides (see <see cref="WorldAnchor"/>), or <see langword="null"/> to pose directly
/// in world space (an unanchored camera's <paramref name="Offset"/> IS its world eye position).</param>
/// <param name="Offset">The attachment point relative to the anchor's resolved pose (anchor-local axes), or — when
/// <paramref name="Anchor"/> is <see langword="null"/> — the eye's world position.</param>
/// <param name="Rig">How the camera frames from that pose (see <see cref="WorldRig"/>).</param>
/// <param name="RenderWidth">The offscreen render width in pixels.</param>
/// <param name="RenderHeight">The offscreen render height in pixels.</param>
internal sealed record WorldCamera(string Name, WorldAnchor? Anchor, Vector3 Offset, WorldRig Rig, uint RenderWidth, uint RenderHeight);

/// <summary>The built-in session census. Local players occupy the split-screen seats; network players are represented
/// by authoritative local stand-ins until a transport supplies their intent stream.</summary>
/// <param name="LocalPlayers">The number of active local-human seats at boot.</param>
/// <param name="NetworkPlayers">The number of active network-human stand-ins at boot.</param>
/// <param name="DefaultPeerSource">The boot intent-source template every network stand-in wakes on (<see
/// cref="IntentSource.Wander"/> in the built-in world): the durable home for the session peer-source default the
/// <c>world.population idle|wander</c> verb moves live and <c>world.save</c> folds back into session write-back.</param>
/// <param name="SpawnPolicy">How simulated peers are distributed at spawn (see <see cref="WorldSpawnPolicy"/>).
/// A THIRD timing class within this row: it is LIVE for FUTURE activations but INERT for bodies already standing
/// (a change re-clusters only peers spawned after it), narrated in the accept echo.</param>
internal readonly record struct WorldPopulationDefaults(
    int LocalPlayers,
    int NetworkPlayers,
    IntentSource DefaultPeerSource,
    WorldSpawnPolicy SpawnPolicy
) {
    private readonly WorldSpawnPolicy m_spawnPolicy = (SpawnPolicy ?? WorldSpawnPolicy.Default);

    /// <summary>How simulated peers are distributed at spawn. The absence-coalesce lives in the accessor for the same
    /// reason <see cref="MotionTuning.Response"/>'s does.</summary>
    public WorldSpawnPolicy SpawnPolicy {
        get => m_spawnPolicy;
        init => m_spawnPolicy = (value ?? WorldSpawnPolicy.Default);
    }
}
internal static class WorldApplicationDefaults {
    /// <summary>The built-in world ships with no bundled AGB cartridge — an asset-free default, never an owner-local
    /// absolute path or a copyrighted dump. Durable per-deployment cartridge/BIOS paths belong in the world data file
    /// (the "durable config lives in the data file" doctrine); the <c>puck.world.def.v1</c> loader
    /// (<see cref="WorldDefinitionLoader"/>) reads one, but the checked-in default file authors an empty content
    /// path, so the native-AGB screen boots unconfigured (a graceful fault, never a crash) until a real deployment
    /// supplies <see cref="WorldScreenSource.Machine.ContentPath"/>.</summary>
    public const string DefaultAgbCartridgePath = "";
    public const string WindowTitle = "Puck: World";
}

/// <summary>One graphics-quality preset — the bundle of render levers the <c>world.quality</c> verb writes for a named
/// tier (the individual <c>world.shadows</c>/<c>.ao</c>/<c>.render-scale</c> verbs still override afterward).</summary>
/// <param name="Shadows">The soft-shadow tier the preset selects.</param>
/// <param name="AmbientOcclusion">Whether the preset enables ambient occlusion.</param>
/// <param name="RenderScale">The render-scale tier the preset selects.</param>
internal readonly record struct WorldQualityPreset(
    ShadowTier Shadows,
    bool AmbientOcclusion,
    WorldRenderScaleTier RenderScale
);

/// <summary>The world's render-lever defaults — the boot values <see cref="WorldRenderSettings"/> wakes on and the
/// <c>world.quality</c> preset table. Session state, not identity: these are engine-wide levers (shadows, AO, render
/// scale, the crowd radius), the graphics-menu defaults a server-pulled world would carry.</summary>
/// <param name="Shadows">The boot soft-shadow tier.</param>
/// <param name="ShadowCrowdRadius">The boot soft-shadow crowd radius (world units).</param>
/// <param name="AmbientOcclusion">Whether ambient occlusion boots on.</param>
/// <param name="RenderScale">The boot render-scale tier.</param>
/// <param name="UpscaleSharpness">The boot reduced-resolution reconstruction blend (0 bilinear .. 1 Catmull-Rom).</param>
/// <param name="Low">The <c>world.quality low</c> preset.</param>
/// <param name="Medium">The <c>world.quality medium</c> preset.</param>
/// <param name="High">The <c>world.quality high</c> preset.</param>
internal sealed record WorldRenderDefaults(
    ShadowTier Shadows,
    float ShadowCrowdRadius,
    bool AmbientOcclusion,
    WorldRenderScaleTier RenderScale,
    float UpscaleSharpness,
    WorldQualityPreset Low,
    WorldQualityPreset Medium,
    WorldQualityPreset High
) {
    /// <summary>The built-in default render levers — the boot values and preset table.</summary>
    public static WorldRenderDefaults Default { get; } = new WorldRenderDefaults(
        // Exact-128 is the built-in scene, so boot in the measured fleet posture that retains ample headroom above the
        // 60-FPS floor. High/native remains a live quality preset rather than silently changing the population.
        Shadows: ShadowTier.Off,
        ShadowCrowdRadius: 15f,
        AmbientOcclusion: false,
        RenderScale: WorldRenderScaleTier.Half,
        UpscaleSharpness: 0f,
        Low: new WorldQualityPreset(Shadows: ShadowTier.Off, AmbientOcclusion: false, RenderScale: WorldRenderScaleTier.Half),
        Medium: new WorldQualityPreset(Shadows: ShadowTier.Medium, AmbientOcclusion: true, RenderScale: WorldRenderScaleTier.ThreeQuarter),
        High: new WorldQualityPreset(Shadows: ShadowTier.High, AmbientOcclusion: true, RenderScale: WorldRenderScaleTier.Native)
    );

    /// <summary>Returns the preset for a quality tier keyword (case-insensitive <c>low</c>/<c>medium</c>/<c>high</c>), or
    /// <see langword="null"/> when the token names none.</summary>
    /// <param name="name">The quality tier keyword.</param>
    /// <returns>The matching preset, or <see langword="null"/>.</returns>
    public WorldQualityPreset? Preset(string name) {
        return (name.ToUpperInvariant() switch {
            "LOW" => Low,
            "MEDIUM" => Medium,
            "HIGH" => High,
            _ => (WorldQualityPreset?)null,
        });
    }
}

/// <summary>One local seat's spawn placement — a stable string id (its mutation address) plus the spawn position (X/Z;
/// Y rides the ground plane). Order still maps slots (slot <c>n</c> spawns at <c>SpawnPoints[n]</c>); the id addresses
/// the row for mutation, order is seat identity.</summary>
/// <param name="Id">The stable spawn id (unique within the definition; <c>seat-1</c>..<c>seat-4</c> by default).</param>
/// <param name="Position">The seat's spawn position (X/Z used; Y rides the ground plane).</param>
internal readonly record struct WorldSpawnPoint(string Id, Vector3 Position);

/// <summary>The row-to-entity assignment policy — nothing about <see cref="Policy"/>/<see cref="Table"/> is kit-specific,
/// so the SAME primitive distributes the kit table (a way of MOVING) and the look table (a way of LOOKING) across the
/// population. Resolved once at construction into each entry's fixed row index (precompute; zero steady-state cost). The
/// kit assignment is SIM-AFFECTING (it selects the compiled tuning/action bindings); the look assignment is
/// PRESENTATION-ONLY (it selects the appearance row).</summary>
/// <param name="Policy">The assignment policy: <see cref="HashPolicy"/> (the R1 low-discrepancy mapping) or
/// <see cref="TablePolicy"/> (<c>row = Table[index % Table.Count]</c>).</param>
/// <param name="Table">The row-name cycle for <see cref="TablePolicy"/> (entries resolve to rows at compile); empty
/// and ignored under <see cref="HashPolicy"/>.</param>
internal sealed record WorldRowAssignment(string Policy, IReadOnlyList<string> Table) {
    private readonly IReadOnlyList<string> m_table = (Table ?? []);

    /// <summary>The row-name cycle. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="MotionTuning.Response"/>'s does.</summary>
    public IReadOnlyList<string> Table {
        get => m_table;
        init => m_table = (value ?? []);
    }

    /// <summary>The default policy token — the R1 low-discrepancy mapping (<see cref="WorldPopulation.RowFor"/>).</summary>
    public const string HashPolicy = "hash";

    /// <summary>The table policy token — <c>row = Table[index % Table.Count]</c>, a pure function of the stable
    /// population index.</summary>
    public const string TablePolicy = "table";

    /// <summary>The built-in default assignment: the hash policy with an empty table.</summary>
    public static WorldRowAssignment Hash { get; } = new WorldRowAssignment(Policy: HashPolicy, Table: []);
}

/// <summary>Where a <see cref="WorldLook"/> resolves an entity's appearance from — a pinned catalog rig or a sculpted
/// creation. The appearance peer of a way of MOVING: a new way of LOOKING is a row, never a new renderer.</summary>
[JsonDerivedType(typeof(WorldLookSource.Catalog), typeDiscriminator: "catalog")]
[JsonDerivedType(typeof(WorldLookSource.Creation), typeDiscriminator: "creation")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal abstract record WorldLookSource {
    private WorldLookSource() { }

    /// <summary>The procedural humanoid catalog (<see cref="WorldAvatarCatalog"/>) — one look source among others,
    /// no longer "the avatar system".</summary>
    /// <param name="Index">The <c>0..</c><see cref="WorldPopulation.MaxPopulation"/>-1 catalog rig to pin, or
    /// <see langword="null"/> for the index-derived pick (the pre-look behaviour every body had).</param>
    internal sealed record Catalog(int? Index) : WorldLookSource;

    /// <summary>A sculpted creation worn by the body — resolved against the world's <see cref="WorldCreation"/> rows.</summary>
    /// <param name="CreationId">The referenced <see cref="WorldCreation.Id"/> (must resolve at validation).</param>
    internal sealed record Creation(string CreationId) : WorldLookSource;
}

/// <summary>How a look animates with the body it clothes. PRESENTATION-ONLY: read by the client's stamp pool and the
/// catalog packer, never by <c>WorldBody</c>. Catalog looks read <see cref="GaitAmplitude"/>; creation looks read
/// <see cref="ReplayFrames"/> and <see cref="SecondsPerFrame"/>.</summary>
/// <param name="GaitAmplitude">The catalog rig's limb-swing scale (1 = the pre-look default; 0 stills the gait).</param>
/// <param name="ReplayFrames">Whether a creation look replays its authored timeline on the render clock.</param>
/// <param name="SecondsPerFrame">The creation timeline cadence when <see cref="ReplayFrames"/> is set.</param>
internal readonly record struct WorldLookMotion(float GaitAmplitude, bool ReplayFrames, float SecondsPerFrame) {
    /// <summary>The implicit look motion — full gait, no timeline replay — every body wore before this arc.</summary>
    public static WorldLookMotion Default { get; } = new WorldLookMotion(GaitAmplitude: 1f, ReplayFrames: false, SecondsPerFrame: 0f);
}

/// <summary>One LOOK row — the appearance peer of <see cref="WorldKit"/>'s way of MOVING. Every appearance a world
/// offers is a row of this data, never a renderer branch; <c>world.looks</c> prints these names.</summary>
/// <param name="Name">The look's stable kebab-case name (unique within the definition), assignable by the look table.</param>
/// <param name="Source">Where the appearance resolves from (a catalog rig or a creation).</param>
/// <param name="Scale">The uniform render scale. Appearance ONLY — it does not resize the body's motion tuning or its
/// collision volume.</param>
/// <param name="Motion">How the look animates with the body (see <see cref="WorldLookMotion"/>).</param>
internal sealed record WorldLook(string Name, WorldLookSource Source, float Scale, WorldLookMotion Motion) {
    /// <summary>The implicit single look every body wears when a world authors no <c>looks</c> section — the
    /// index-derived catalog pick at full gait, so an empty <c>looks</c> list is the pre-arc runtime exactly.</summary>
    public static WorldLook Implicit { get; } = new WorldLook(Name: "catalog", Source: new WorldLookSource.Catalog(Index: null), Scale: 1f, Motion: WorldLookMotion.Default);
}

/// <summary>How simulated peers are distributed at spawn. SIM-AFFECTING — compiled once to fixed point by
/// <see cref="FixedSpawnPolicy.Compile"/>; the runtime never re-reads the authored floats.</summary>
[JsonDerivedType(typeof(WorldSpawnPolicy.Phyllotaxis), typeDiscriminator: "phyllotaxis")]
[JsonDerivedType(typeof(WorldSpawnPolicy.PointCycle), typeDiscriminator: "points")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal abstract record WorldSpawnPolicy {
    private WorldSpawnPolicy() { }

    /// <summary>The pre-arc behaviour, made nameable: a golden-angle phyllotaxis disc.</summary>
    /// <param name="Radius">The disc radius; <c>0</c> defers to <c>WanderTuning.SpawnRadius</c>.</param>
    internal sealed record Phyllotaxis(float Radius) : WorldSpawnPolicy;

    /// <summary>Cycle the named spawn points, scattering each peer inside <paramref name="Jitter"/> by its index's R2
    /// low-discrepancy sample (no RNG, index-stable across rebuilds). The type is <c>PointCycle</c> (a record property
    /// cannot share its type's name), but its wire discriminator is <c>points</c> and its list member is <c>points</c>.</summary>
    /// <param name="Points">The spawn-point id cycle (each must resolve to a <see cref="WorldSpawnPoint"/>).</param>
    /// <param name="Jitter">The per-peer scatter radius around the cycled point.</param>
    internal sealed record PointCycle(IReadOnlyList<string> Points, float Jitter) : WorldSpawnPolicy;

    /// <summary>The built-in default — the phyllotaxis disc at the wander tuning's spawn radius (the pre-arc behaviour).</summary>
    public static WorldSpawnPolicy Default { get; } = new Phyllotaxis(Radius: 0f);
}

/// <summary>The one-time fixed-point compilation of a <see cref="WorldSpawnPolicy"/>. Runtime spawn seeding reads only
/// this form (the phyllotaxis radius override in fixed point, or the resolved spawn-point positions plus jitter).</summary>
internal readonly record struct FixedSpawnPolicy(FixedQ4816 PhyllotaxisRadius, FixedVector3[]? Points, FixedQ4816 Jitter) {
    /// <summary>Compiles the authored spawn policy against the definition's spawn points. A <see cref="WorldSpawnPolicy.PointCycle"/>
    /// policy resolves each named id to its position (the validator has already gated the names).</summary>
    /// <param name="policy">The authored policy.</param>
    /// <param name="spawnPoints">The definition's spawn points, for id resolution.</param>
    public static FixedSpawnPolicy Compile(WorldSpawnPolicy policy, IReadOnlyList<WorldSpawnPoint> spawnPoints) {
        switch (policy) {
            case WorldSpawnPolicy.PointCycle points: {
                var resolved = new FixedVector3[points.Points.Count];

                for (var index = 0; (index < resolved.Length); index++) {
                    var position = ResolveSpawnPoint(spawnPoints: spawnPoints, id: points.Points[index]);

                    resolved[index] = new FixedVector3(
                        X: FixedQ4816.FromDouble(value: position.X),
                        Y: FixedQ4816.FromDouble(value: position.Y),
                        Z: FixedQ4816.FromDouble(value: position.Z)
                    );
                }

                return new FixedSpawnPolicy(PhyllotaxisRadius: FixedQ4816.Zero, Points: resolved, Jitter: FixedQ4816.FromDouble(value: points.Jitter));
            }
            case WorldSpawnPolicy.Phyllotaxis phyllotaxis:
                return new FixedSpawnPolicy(PhyllotaxisRadius: FixedQ4816.FromDouble(value: phyllotaxis.Radius), Points: null, Jitter: FixedQ4816.Zero);
            default:
                return new FixedSpawnPolicy(PhyllotaxisRadius: FixedQ4816.Zero, Points: null, Jitter: FixedQ4816.Zero);
        }
    }

    private static Vector3 ResolveSpawnPoint(IReadOnlyList<WorldSpawnPoint> spawnPoints, string id) {
        foreach (var point in spawnPoints) {
            if (string.Equals(a: point.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return point.Position;
            }
        }

        return Vector3.Zero;
    }
}

/// <summary>One data-side addon descriptor the world carries — a World-local row carrying Name/ModulePath/Hash/Fuel/
/// Enabled, with no Puck.Scripting reference. Consumed when addons mount as principals through <c>IServerLink</c>.</summary>
/// <param name="Name">The addon's identifying name — unique within the definition; used by console verbs and logging.</param>
/// <param name="ModulePath">The WASM module file path (machine-local; existence/hash verification is the run path's job).</param>
/// <param name="Hash">The content-address integrity pin, or empty to skip the check.</param>
/// <param name="Fuel">The per-tick fuel budget before a deterministic halt.</param>
/// <param name="Enabled">Whether the addon starts enabled.</param>
internal sealed record WorldAddonRow(string Name, string ModulePath, string Hash, ulong Fuel, bool Enabled);

/// <summary>One per-world binding overlay — a whole <see cref="BindingProfileDocument"/> layered over the engine
/// default beneath every seat's profile bindings, so a world can contextualize the controls (a kart world remapping a
/// lane, an RTS world adding a chorded command page) as data, never a client fork. Merged in order; the composed result
/// (default ⊕ every overlay) is what the validator compiles.</summary>
/// <param name="Id">The overlay's stable id — its mutation address (unique within the definition; carries no meaning
/// beyond identity).</param>
/// <param name="Document">The overlay binding document merged into the composed mapping.</param>
internal sealed record WorldBindingOverlay(string Id, BindingProfileDocument Document);

/// <summary>
/// The world's storage host-section defaults — the per-user cloud endpoint and an explicit user-id override,
/// authored as DATA so durable configuration lives in the world file (never a <c>PUCK_*</c> env var; World has no such
/// surface). Both fields are RESERVED: nothing constructs an Azure target from <see cref="Endpoint"/>, and
/// <see cref="UserId"/> only feeds the identity resolver's explicit-override source. A <c>--storage-uri</c> /
/// <c>--user-id</c> CLI reflection overrides each at boot. <c>storage.status</c> echoes the resolved values.
/// </summary>
/// <param name="Endpoint">The per-user blob endpoint (a URI, e.g. <c>https://blob.byteterrace.com</c>), or
/// <see langword="null"/> for none. Validated as an absolute URI when present; reserved (no target is built from it this
/// arc).</param>
/// <param name="UserId">An explicit user-id override (an Entra <c>oid</c> Guid string for a dev box or agent), or
/// <see langword="null"/> to decline identity (local-only). Fed to the identity resolver's explicit-override source.</param>
internal sealed record WorldStorageDefaults(string? Endpoint, string? UserId) {
    /// <summary>The built-in default: no endpoint, no user-id (cloud unwired, identity declined — local-only).</summary>
    public static WorldStorageDefaults None { get; } = new WorldStorageDefaults(Endpoint: null, UserId: null);
}

/// <summary>
/// World-varying editor/authoring policy values, authored as data rather than compile-time constants. Two
/// consumption classes share this one row (whole-row mutable like every other section — never split into two
/// sections for a consumption nuance that consumers already handle honestly):
/// <list type="bullet">
/// <item><description><b>BOOT-CONSUMED</b> (<see cref="AuthoringHeadroomRows"/>, <see cref="AuthoringHeadroomScreens"/>,
/// <see cref="AuthoringHeadroomPlacements"/>, <see cref="MaxRepeatPerSegment"/>): read exactly ONCE, at
/// <see cref="Client.WorldFrameSource"/> construction, into the frozen render-envelope capacity floor (the probe's
/// worst-case word/instance reservation). The one honest exception: a live edit to these capacity-floor fields is
/// journaled but the running session's floor cannot retroactively grow — it applies at the next boot (the validator
/// still gates the new value against engine caps immediately, so a bad authored value never reaches a boot).</description></item>
/// <item><description><b>LIVE-CONSUMED</b> (<see cref="MinPlacementScale"/>, <see cref="MaxPlacementScale"/>,
/// <see cref="CandidateRadius"/>, <see cref="CandidateCap"/>, <see cref="WorkbenchFraction"/>,
/// <see cref="PreviewDeadlineFrames"/>): read fresh from the delivered definition at each use site (a candidate
/// gather, a layout resolve, a drag-freeze tick) — a mutation takes effect at the very next tick/frame, no restart.
/// </description></item>
/// </list>
/// </summary>
/// <param name="AuthoringHeadroomRows">BOOT-CONSUMED. The scene rows of authoring headroom the construction probe
/// reserves beyond the boot scene, captured once into <see cref="Client.WorldFrameSource"/>'s frozen field at
/// construction.</param>
/// <param name="AuthoringHeadroomScreens">BOOT-CONSUMED. The extra screen slots the probe reserves, bounded by the
/// engine's <see cref="Puck.SdfVm.SdfProgramBuilder.MaxScreenSurfaces"/> ceiling.</param>
/// <param name="AuthoringHeadroomPlacements">BOOT-CONSUMED. The placement rows of headroom the probe reserves beyond
/// the boot placements (see <see cref="Client.WorldPlacementStamper.StaticStampSegments"/>).</param>
/// <param name="MaxRepeatPerSegment">BOOT-CONSUMED. The largest per-axis repeat count one emitted placement segment
/// carries before auto-splitting — frozen at construction because it feeds the same
/// probe segment math as the headroom fields (the probe-vs-measure word-count constancy is load-bearing; see
/// <see cref="Client.WorldFrameSource"/>'s placement-reservation remarks).</param>
/// <param name="MinPlacementScale">LIVE-CONSUMED. The placement uniform-scale envelope's floor — a pure validator
/// bound, revalidated on every placement mutation.</param>
/// <param name="MaxPlacementScale">LIVE-CONSUMED. The placement uniform-scale envelope's ceiling — also the worst-case
/// scale <see cref="Client.WorldStampPool"/>'s probe bound-radius reads (bound radius is spatial-cull metadata,
/// never a word-capacity term, so re-reading it live every build cannot desync the frozen capacity floor).</param>
/// <param name="CandidateRadius">LIVE-CONSUMED. The proximity-candidate radius (world units) around a seat's editor
/// focus point — cycling never walks the whole world (the explicit candidate policy).</param>
/// <param name="CandidateCap">LIVE-CONSUMED. The candidate-count cap: at most this many nearest in-radius rows enter
/// the cycle ring.</param>
/// <param name="WorkbenchFraction">LIVE-CONSUMED. The full-height fraction a SOLE editing seat's viewport takes when
/// 2+ seats are joined (the remaining width splits as a live rail among the playing seats) — read fresh each captured
/// frame by <see cref="Client.WorldFrameSource.LayoutRegion(int, int, int, float)"/>.</param>
/// <param name="PreviewDeadlineFrames">LIVE-CONSUMED. The drag preview channel's missing-response fallback: a
/// released overlay with no definition delivery after this many produced frames drops honestly.</param>
/// <param name="DerivedFaceScreens">BOOT-CONSUMED. The derived screen slots the binder reserves at boot for creation
/// FACES (a face declared by a placement's creation, lit by a feed), registered at
/// <c>[<see cref="Client.WorldCreationFacets.DerivedFaceBase"/>, DerivedFaceBase + this)</c>. Bounded so the range
/// stays within the engine's screen-surface ceiling.</param>
internal sealed record WorldAuthoringDefaults(
    int AuthoringHeadroomRows,
    int AuthoringHeadroomScreens,
    int AuthoringHeadroomPlacements,
    int MaxRepeatPerSegment,
    float MinPlacementScale,
    float MaxPlacementScale,
    float CandidateRadius,
    int CandidateCap,
    float WorkbenchFraction,
    int PreviewDeadlineFrames,
    int DerivedFaceScreens
) {
    /// <summary>The built-in default authoring policy.</summary>
    public static WorldAuthoringDefaults Default { get; } = new WorldAuthoringDefaults(
        AuthoringHeadroomRows: 32,
        AuthoringHeadroomScreens: 4,
        AuthoringHeadroomPlacements: 8,
        MaxRepeatPerSegment: 8,
        MinPlacementScale: 0.2f,
        MaxPlacementScale: 5.0f,
        CandidateRadius: 32f,
        CandidateCap: 16,
        WorkbenchFraction: 0.70f,
        PreviewDeadlineFrames: 12,
        DerivedFaceScreens: 4
    );
}

/// <summary>Which graphics backend a world PREFERS. <see cref="Auto"/> — the default — picks the OS-appropriate backend,
/// so a shared world document is portable across an OS boundary; an explicit preference the running OS cannot satisfy
/// degrades LOUDLY (a document author preference) or hard-exits (a CLI operator assertion) rather than silently
/// mispresenting.</summary>
internal enum WorldBackendPreference : byte {
    /// <summary>Pick the OS-appropriate backend at boot — Direct3D 12 on Windows 10+, Vulkan elsewhere.</summary>
    Auto,

    /// <summary>Prefer Direct3D 12.</summary>
    DirectX,

    /// <summary>Prefer Vulkan.</summary>
    Vulkan,
}

/// <summary>
/// The world's HOST defaults — how the world asks to be PRESENTED, independent of what it contains. PRESENTATION-ONLY
/// throughout (never simulation state). Two consumption classes share this one row, named per field:
/// <list type="bullet">
/// <item><description><b>BOOT-ONLY</b> (<see cref="Backend"/>, <see cref="Width"/>, <see cref="Height"/>,
/// <see cref="SurfaceFormat"/>, <see cref="Fullscreen"/>, <see cref="PresentMode"/>, <see cref="ExitAfterSeconds"/>,
/// <see cref="RayQuery"/>, <see cref="Genlock"/>): read once at composition; a live edit is journaled and validated
/// immediately but takes effect next boot.</description></item>
/// <item><description><b>BOOT-DEFAULT WITH A LIVE LEVER</b> (<see cref="TargetHertz"/> via <c>world.target</c>,
/// <see cref="Timing"/> via <c>world.timing</c>): the value the session wakes on; <see cref="WorldSessionCapture"/>
/// folds the live values back at <c>world.save</c>.</description></item>
/// </list>
/// <see cref="Default"/> reproduces World's current boot exactly.
/// </summary>
/// <param name="Backend">The preferred graphics backend (<see cref="WorldBackendPreference.Auto"/> is OS-portable).</param>
/// <param name="Width">The window client width in pixels.</param>
/// <param name="Height">The window client height in pixels.</param>
/// <param name="SurfaceFormat">The swapchain surface format (<see cref="SurfaceFormat.Unknown"/> is rejected by the validator).</param>
/// <param name="Fullscreen">Whether the window enters borderless fullscreen when first shown.</param>
/// <param name="PresentMode">The swapchain presentation algorithm.</param>
/// <param name="TargetHertz">The boot present-pacing target in Hz; <c>0</c> selects automatic display pacing. The
/// <c>world.target</c> live lever owns "now" thereafter.</param>
/// <param name="ExitAfterSeconds">Seconds before the world auto-exits; <c>0</c> runs until the window is closed.</param>
/// <param name="RayQuery">Whether the SDF renderer may use the ray-query hardware path.</param>
/// <param name="Timing">Whether GPU per-pass timing boots armed; the <c>world.timing</c> live lever owns it thereafter.</param>
/// <param name="Genlock">The external-clock election policy, consumed at boot by the clock registry (which tolerates an
/// unknown source id): <see langword="null"/> for the launcher's automatic election, or a non-whitespace source id /
/// <c>off</c>. SHAPE-only validation (null or non-whitespace); the registry, not the validator, interprets the id.</param>
internal sealed record WorldHostDefaults(
    WorldBackendPreference Backend,
    int Width,
    int Height,
    SurfaceFormat SurfaceFormat,
    bool Fullscreen,
    PresentMode PresentMode,
    double TargetHertz,
    int ExitAfterSeconds,
    bool RayQuery,
    bool Timing,
    string? Genlock
) {
    /// <summary>The built-in host defaults — reproducing World's current hardcoded boot exactly (1280×800, auto backend,
    /// immediate present, automatic display pacing, R8G8B8A8 surface, ray-query on, timing off, no auto-exit).</summary>
    public static WorldHostDefaults Default { get; } = new WorldHostDefaults(
        Backend: WorldBackendPreference.Auto,
        Width: 1280,
        Height: 800,
        SurfaceFormat: SurfaceFormat.R8G8B8A8Unorm,
        Fullscreen: false,
        PresentMode: PresentMode.Immediate,
        TargetHertz: 0.0,
        ExitAfterSeconds: 0,
        RayQuery: true,
        Timing: false,
        Genlock: null
    );
}

/// <summary>
/// The definition of this world — the aggregate describing what the world is, distinct from the live session state that
/// plays in it. It gathers the static scene (<see cref="Scene"/>), the seat spawn points (<see cref="SpawnPoints"/>),
/// the population's wander tuning (<see cref="Wander"/>), the motion defaults (<see cref="Motion"/>), and the
/// render-lever defaults and quality presets (<see cref="Render"/>). Every consumer takes it by construction.
/// </summary>
/// <remarks>These records are serialization-friendly. <see cref="Default"/> supplies the built-in definition, and
/// loaders can construct the same shapes from external data.</remarks>
/// <param name="Motion">The ground plane and the profileless locomotion speeds (see <see cref="WorldMotionDefaults"/>). Jump feel is per-kit.</param>
/// <param name="Wander">The simulated stand-ins' synthetic wander tuning.</param>
/// <param name="Scene">The world's static scene (ground + boulders).</param>
/// <param name="SpawnPoints">Where each local seat's avatar spawns (X/Z; Y rides the ground plane), by slot.</param>
/// <param name="Render">The render-lever boot defaults and quality-preset table.</param>
/// <param name="Screens">The diegetic screens standing in the plaza — pure data the frame source emits as screen
/// slabs and the binder feeds; a screen the world never authors, only declares.</param>
/// <param name="Cameras">The placeable cameras a <see cref="WorldScreenSource.View"/> screen renders the world from
/// (the jumbotron recursion) — pure data the binder resolves View screens against at wiring.</param>
/// <param name="Population">The local/network census active from the first built-in scene frame.</param>
/// <param name="Kits">The world's locomotion kits — one row per way of moving (see <see cref="WorldKit"/>); the
/// <see cref="Assignment"/> policy distributes entities across the rows.</param>
/// <param name="DefaultSeatKit">The kit row (by name) every seat body constructs from.</param>
/// <param name="Assignment">The kit→entity assignment policy (the realized policy-as-data seam).</param>
/// <param name="Addons">The data-side addon descriptors (default empty), consumed when addons mount as
/// principals.</param>
/// <param name="BindingOverlays">The per-world binding overlays (default empty) layered over the engine default beneath
/// each seat's profile bindings.</param>
/// <param name="Storage">The storage host-section defaults — the reserved per-user cloud endpoint and explicit
/// user-id override, authored as data.</param>
/// <param name="Creations">The creation ASSET rows (default empty) — whole <c>puck.creation.v1</c> documents
/// embedded inline-canonical with their identity hashes pinned (see <see cref="WorldCreation"/>).</param>
/// <param name="Placements">The placement INSTANCE rows (default empty) — creations stamped by reference (see
/// <see cref="WorldPlacement"/>).</param>
/// <param name="Authoring">The editor/authoring policy row — headroom, placement
/// scale envelope, candidate targeting, the sole-editor layout split, and the drag-preview deadline, authored as data
/// (see <see cref="WorldAuthoringDefaults"/>) — a REQUIRED section every document carries.</param>
/// <param name="Speakers">The placeable speaker rows (default empty) — the camera family's audio sibling (see
/// <see cref="WorldSpeaker"/>): name-keyed transducers whose feeds tap shared sources.</param>
/// <param name="Tunes">The tune ASSET rows (default empty) — whole <c>puck.audio.v1</c> documents embedded
/// inline-canonical with pinned hashes (see <see cref="WorldTune"/>).</param>
/// <param name="Patches">The synth-patch ASSET rows (default empty) — whole <c>puck.synth.v1</c> documents embedded
/// inline-canonical with pinned hashes (see <see cref="WorldPatch"/>).</param>
/// <param name="Audio">The audio host-section defaults (master gain, point-attenuation coalescing, bed fade, the
/// listener policy — see <see cref="WorldAudioDefaults"/>) — a REQUIRED section every document carries.</param>
/// <param name="Collision">The contact-solver tuning (see <see cref="WorldCollision"/>) — SIM-AFFECTING.
/// <see cref="WorldCollision.None"/> is collision OFF (the flat ground plane).</param>
/// <param name="Host">The host-section defaults — how the world asks to be PRESENTED (window/backend/present/pacing/
/// timing/genlock — see <see cref="WorldHostDefaults"/>). The CLI window/backend flags override it at boot (a
/// deployment surface laid over the author's intent).</param>
/// <param name="Views">The window-composition defaults — the seat framing every seat wakes on plus the authored named
/// layouts (see <see cref="WorldViewDefaults"/>). An empty layout list falls the composer through to the built-in seat
/// ladder.</param>
/// <param name="Looks">The LOOK rows (default empty) — authored appearances the population wears, the peer of
/// <see cref="Kits"/> (see <see cref="WorldLook"/>). Empty resolves every entity to the implicit single catalog look.</param>
/// <param name="LookAssignment">The look→entity assignment policy, the same <see cref="WorldRowAssignment"/> primitive
/// <see cref="Assignment"/> uses for kits.</param>
/// <param name="Links">The cable-link rows (default empty) — groups of screens whose machines advance as one
/// interleaved unit (see <see cref="WorldScreenLink"/>).</param>
internal sealed record WorldDefinition(
    WorldMotionDefaults Motion,
    WanderTuning Wander,
    WorldScene Scene,
    IReadOnlyList<WorldSpawnPoint> SpawnPoints,
    WorldRenderDefaults Render,
    IReadOnlyList<WorldScreen> Screens,
    IReadOnlyList<WorldCamera> Cameras,
    WorldPopulationDefaults Population,
    IReadOnlyList<WorldKit> Kits,
    string DefaultSeatKit,
    WorldRowAssignment Assignment,
    IReadOnlyList<WorldAddonRow> Addons,
    IReadOnlyList<WorldBindingOverlay> BindingOverlays,
    WorldStorageDefaults Storage,
    IReadOnlyList<WorldCreation> Creations,
    IReadOnlyList<WorldPlacement> Placements,
    WorldAuthoringDefaults Authoring,
    IReadOnlyList<WorldSpeaker> Speakers,
    IReadOnlyList<WorldTune> Tunes,
    IReadOnlyList<WorldPatch> Patches,
    WorldAudioDefaults Audio,
    WorldCollision Collision,
    WorldHostDefaults Host,
    WorldViewDefaults Views,
    // An empty Looks list resolves every entity to WorldLook.Implicit (the index-derived catalog pick at full gait);
    // NO branch special-cases "the author authored none".
    IReadOnlyList<WorldLook> Looks,
    WorldRowAssignment LookAssignment,
    IReadOnlyList<WorldScreenLink> Links
) {
    /// <summary>The document schema version. A loader rejects (→ loud baked-default fallback) any other value; the
    /// canonical writer always emits it.</summary>
    public const string SchemaVersion = "puck.world.def.v1";

    /// <summary>The document schema tag — <see cref="SchemaVersion"/> for a well-formed document.</summary>
    public string Schema { get; init; } = SchemaVersion;

    /// <summary>Unknown sections preserved across a round-trip — the data-side plugin extensibility posture,
    /// matching <see cref="Puck.Scene.PuckRunDocument"/>'s JsonExtensionData round-trip convention. Null when the
    /// document carries no unknown members. A settable (not <c>init</c>) accessor is required: System.Text.Json
    /// appends to it during deserialization.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>The built-in default world.</summary>
    public static WorldDefinition Default { get; } = new WorldDefinition(
        Motion: WorldMotionDefaults.Default,
        Wander: WanderTuning.Default,
        Scene: WorldScene.Default,
        // The five built-in locomotion kits. The R1-hash
        // assignment walks these in order, so row order is census identity. Grounded kits bind the jump composition on
        // Primary and the dash on Secondary; free kits leave Primary unbound and bind the surge on Secondary.
        Kits: [
            new WorldKit(
                Name: "flyer",
                Model: MotionModel.Free,
                Tuning: MotionTuning.Default,
                Flavor: new WanderFlavor(Forward: 0.62f, DriftForward: false, StrafeWave: 0.18f, TurnWave: 0f, UpWave: 0.10f, PitchWave: 0.14f, RollTurn: 0.32f, PrimaryThreshold: 0f, AltitudeBase: 4.5f, AltitudeRange: 3.0f),
                PrimaryAction: null,
                SecondaryAction: ActionSpec.Surge(tuning: MotionTuning.Default)
            ),
            new WorldKit(
                Name: "swimmer",
                Model: MotionModel.Free,
                Tuning: MotionTuning.Default,
                Flavor: new WanderFlavor(Forward: 0.38f, DriftForward: false, StrafeWave: 0.28f, TurnWave: 0f, UpWave: 0.16f, PitchWave: 0.22f, RollTurn: 0.18f, PrimaryThreshold: 0f, AltitudeBase: 0.8f, AltitudeRange: 1.2f),
                PrimaryAction: null,
                SecondaryAction: ActionSpec.Surge(tuning: MotionTuning.Default)
            ),
            new WorldKit(
                Name: "jumper",
                Model: MotionModel.Grounded,
                Tuning: MotionTuning.Default,
                Flavor: new WanderFlavor(Forward: 0f, DriftForward: true, StrafeWave: 0f, TurnWave: 0f, UpWave: 0f, PitchWave: 0f, RollTurn: 0f, PrimaryThreshold: 0.45f, AltitudeBase: 0f, AltitudeRange: 0f),
                PrimaryAction: ActionSpec.Jump(tuning: MotionTuning.Default),
                SecondaryAction: ActionSpec.Dash(tuning: MotionTuning.Default)
            ),
            new WorldKit(
                Name: "runner",
                Model: MotionModel.Grounded,
                Tuning: MotionTuning.Default,
                Flavor: new WanderFlavor(Forward: 0f, DriftForward: true, StrafeWave: 0f, TurnWave: 0f, UpWave: 0f, PitchWave: 0f, RollTurn: 0f, PrimaryThreshold: 0f, AltitudeBase: 0f, AltitudeRange: 0f),
                PrimaryAction: ActionSpec.Jump(tuning: MotionTuning.Default),
                SecondaryAction: ActionSpec.Dash(tuning: MotionTuning.Default)
            ),
            new WorldKit(
                Name: "kart",
                Model: MotionModel.Grounded,
                Tuning: MotionTuning.Default,
                Flavor: new WanderFlavor(Forward: 0.88f, DriftForward: false, StrafeWave: 0.24f, TurnWave: 0.42f, UpWave: 0f, PitchWave: 0f, RollTurn: 0f, PrimaryThreshold: 0f, AltitudeBase: 0f, AltitudeRange: 0f),
                PrimaryAction: ActionSpec.Jump(tuning: MotionTuning.Default),
                SecondaryAction: ActionSpec.Dash(tuning: MotionTuning.Default)
            ),
        ],
        // Seats use the default grounded tuning (profile speeds override) with
        // the vertical impulse bound.
        DefaultSeatKit: "runner",
        // Staggered around the origin, all facing -Z toward the boulder cluster, so a fresh join never lands on top of
        // another avatar. Order maps slots (seat n → SpawnPoints[n]); the id is the row's mutation address.
        SpawnPoints: [
            new WorldSpawnPoint(Id: "seat-1", Position: new Vector3(x: 0f, y: 0f, z: 0f)),
            new WorldSpawnPoint(Id: "seat-2", Position: new Vector3(x: -3f, y: 0f, z: 2f)),
            new WorldSpawnPoint(Id: "seat-3", Position: new Vector3(x: 3f, y: 0f, z: 2f)),
            new WorldSpawnPoint(Id: "seat-4", Position: new Vector3(x: 0f, y: 0f, z: 4f)),
        ],
        Render: WorldRenderDefaults.Default,
        Population: new WorldPopulationDefaults(LocalPlayers: WorldPopulation.LocalSeatCount, NetworkPlayers: WorldPopulation.MaxPopulationSimulated, DefaultPeerSource: IntentSource.Wander, SpawnPolicy: WorldSpawnPolicy.Default),
        // THE PLAZA — the built-in broadcast showcase, all faces normal +Z toward a player spawned at the origin looking -Z (the
        // frame source only TRANSLATES a slab, so every screen keeps world-axis Right/Up). Two TIERS keep the sight lines
        // clean from spawn: a LOW front pair (bottoms just above the grass at y = 0.2) and a
        // HIGH broadcast wall behind and above them (bottoms at y ≈ 2.4, clear of the front tops at y = 2.2, so the front
        // never occludes the back). Screen 0 shows the fixed overhead camera and is the runtime-overwritable machine bay;
        // screen 1 captures the world's OWN window ("Puck: World"). Screen 3 is the live webcam (unbound, its no-signal
        // card, on a machine with no camera). Screen 4 is the native AGB engine, unconfigured by default (asset-free —
        // no bundled cartridge, no BIOS dump; the zeroed-BIOS "direct" fallback and an empty content path leave it in
        // the binder's graceful fault state until a real deployment supplies a cartridge). Screen 0 is pinned at
        // (-3, 1.2, -3) r2.5 — the screens proof warps to it after inserting its proof cartridge.
        Screens: [
            new WorldScreen(
                Index: 0,
                Origin: new Vector3(x: -3f, y: 1.2f, z: -3f),
                Right: Vector3.UnitX,
                Up: Vector3.UnitY,
                HalfWidth: 1.3f,
                HalfHeight: 1f,
                HalfDepth: 0.12f,
                Round: 0.08f,
                Source: new WorldScreenSource.View(CameraName: "overhead"),
                Route: new WorldScreenRoute(Engageable: true, EngageRadius: 2.5f)
            ),
            // Screen 1 — THE SELF-CAPTURE: a desktop-window capture keyed to this world's own window title.
            new WorldScreen(
                Index: 1,
                Origin: new Vector3(x: 3f, y: 1.2f, z: -3f),
                Right: Vector3.UnitX,
                Up: Vector3.UnitY,
                HalfWidth: 1.3f,
                HalfHeight: 1f,
                HalfDepth: 0.12f,
                Round: 0.08f,
                // Panel-sized and 10 Hz keeps the recursive feed visibly live without consuming the 16.67-ms budget.
                Source: new WorldScreenSource.Capture(WindowTitle: WorldApplicationDefaults.WindowTitle, Profile: new WorldFeedProfile(Width: 256, Height: 192, RefreshRateHz: 10U)),
                Route: WorldScreenRoute.Passive
            ),
            // Screen 2 — THE JUMBOTRON: a big billboard high behind the boulder cluster showing this same world through
            // the first-person camera anchored to player one's entity. A View source, so
            // the binder registers one offscreen camera render per produced frame. Passive: a jumbotron is watched, not
            // engaged. Its own face binds 0 inside its render (ViewStack's self-reference rule), so no feedback compounds.
            new WorldScreen(
                Index: 2,
                Origin: new Vector3(x: 0f, y: 3.8f, z: -7f),
                Right: Vector3.UnitX,
                Up: Vector3.UnitY,
                HalfWidth: 2.6f,
                HalfHeight: 1.4f,
                HalfDepth: 0.14f,
                Round: 0.1f,
                Source: new WorldScreenSource.View(CameraName: "first-person"),
                Route: WorldScreenRoute.Passive
            ),
            // Screen 3 — THE CAMERA: a live webcam feed on the broadcast wall's left. Faults gracefully to the procedural
            // no-signal card on a machine with no camera device (world.screens reads unbound; screen.state carries the
            // fault). Passive.
            new WorldScreen(
                Index: 3,
                Origin: new Vector3(x: -4.4f, y: 3.6f, z: -6.5f),
                Right: Vector3.UnitX,
                Up: Vector3.UnitY,
                HalfWidth: 1.6f,
                HalfHeight: 1.2f,
                HalfDepth: 0.14f,
                Round: 0.1f,
                // A diegetic panel does not benefit from full webcam resolution; the bounded profile keeps upload
                // spikes inside the exact-128 60-FPS frame budget while remaining a live 15-Hz feed.
                Source: new WorldScreenSource.Camera(Profile: new WorldFeedProfile(Width: 320, Height: 240, RefreshRateHz: 15U)),
                Route: WorldScreenRoute.Passive
            ),
            // Screen 4 — THE NATIVE AGB: the fifth screen boots the ARM7TDMI AdvancedGamingBrick, deliberately NOT the SM83
            // GamingBrick's AGB compatibility costume. Its 3:2 slab matches the native 240x160 framebuffer; the neutral
            // machine adapter owns exact tick stepping, KEYINPUT, save memory, and GPU upload. Asset-free by default: the
            // "direct" option selects the engine's legal zeroed replacement BIOS (never a copyrighted dump), and the empty
            // content path leaves the machine unconfigured — the binder faults the slot gracefully (no crash, no-signal
            // card) until a real deployment supplies a cartridge path.
            new WorldScreen(
                Index: 4,
                Origin: new Vector3(x: 4.4f, y: 3.6f, z: -6.5f),
                Right: Vector3.UnitX,
                Up: Vector3.UnitY,
                HalfWidth: 1.6f,
                HalfHeight: (1.6f / 1.5f),
                HalfDepth: 0.14f,
                Round: 0.1f,
                Source: new WorldScreenSource.Machine(
                    Engine: "advanced-gaming-brick",
                    ContentPath: WorldApplicationDefaults.DefaultAgbCartridgePath,
                    Options: "direct"
                ),
                Route: WorldScreenRoute.Passive
            ),
        ],
        // The two live world cameras: one anchored to player one's entity for the jumbotron, and one fixed high above
        // the plaza for the separate overhead monitor.
        Cameras: [
            new WorldCamera(
                Name: "first-person",
                Anchor: new WorldAnchor.Entity(Index: 0),
                Offset: WorldAvatarCatalog.EyeOffset(avatar: 0),
                Rig: new WorldRig.FirstPerson(EyeOffset: Vector3.Zero, FocusDistance: 0f, FieldOfViewRadians: (68f * (MathF.PI / 180f))),
                RenderWidth: 256,
                RenderHeight: 144
            ),
            new WorldCamera(
                Name: "overhead",
                Anchor: null,
                Offset: new Vector3(x: 0f, y: 15f, z: 7f),
                Rig: new WorldRig.LookAt(Target: new Vector3(x: 0f, y: 0.5f, z: -2.5f), FieldOfViewRadians: (55f * (MathF.PI / 180f))),
                RenderWidth: 256,
                RenderHeight: 144
            ),
        ],
        // The kit→entity assignment: the R1 low-discrepancy hash policy with an empty table.
        Assignment: WorldRowAssignment.Hash,
        // No data-side addons in the built-in world; a deployment authors them and the addon driver mounts them as principals.
        Addons: [],
        // No per-world binding overlays in the built-in world — every seat rides the engine default (plus its profile
        // bindings). A world contextualizes the controls by authoring rows here (see kart-remap.world.json).
        BindingOverlays: [],
        // Storage host-section defaults: no cloud endpoint, no explicit user-id — cloud unwired, identity declined,
        // local-only. A deployment authors these (or passes --storage-uri / --user-id) to reserve the per-user seam.
        Storage: WorldStorageDefaults.None,
        // No creation assets or placements in the built-in world — the editor stamps them live (editor.import /
        // editor.place) and world.save persists them.
        Creations: [],
        Placements: [],
        // The built-in authoring policy.
        Authoring: WorldAuthoringDefaults.Default,
        // The built-in world is silent: no speaker rows, no tune/patch assets, the stock audio defaults. A deployment
        // (or the live editor) authors sound; the audio verbs and world.save persist it.
        Speakers: [],
        Tunes: [],
        Patches: [],
        Audio: WorldAudioDefaults.Default,
        // Collision off (the flat ground plane), World's current boot for the host row, the built-in seat ladder for
        // views, and no authored looks or cable links.
        Collision: WorldCollision.None,
        Host: WorldHostDefaults.Default,
        Views: WorldViewDefaults.Default,
        Looks: [],
        LookAssignment: WorldRowAssignment.Hash,
        Links: []
    );
}
