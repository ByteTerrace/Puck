using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Presentation;
using Puck.Forge.Authoring;
using Puck.Commands;
using Puck.Abstractions.Documents;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// Which locomotion model one <c>WorldBody</c> advances on, and that model's own tuning row — a kit declares both
/// <see cref="WorldKit.BodyMotionProgram"/> (which operations run each tick) and this (the shape of the tuning those
/// operations read). The <c>$type</c> string is the JSON discriminator; a new model is a new derived record, a new
/// <see cref="JsonDerivedTypeAttribute"/> line, and the facet mapping <c>WorldDefinitionValidator</c> owns for it —
/// never a hunt through <c>WorldBody</c>. These float values are compiled once into their model's own fixed-point
/// form (<see cref="FixedMotionTuning"/> for <see cref="Grounded"/>) before simulation and never become runtime
/// simulation state.
/// </summary>
[JsonDerivedType(typeof(WorldMotionModel.Grounded), typeDiscriminator: "grounded")]
[JsonDerivedType(typeof(WorldMotionModel.Vehicle), typeDiscriminator: "vehicle")]
[JsonDerivedType(typeof(WorldMotionModel.Swim), typeDiscriminator: "swim")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldMotionModel {
    private WorldMotionModel() {
    }

    /// <summary>
    /// Contact-solved locomotion: vertical velocity integrates gravity and is resolved against the world contact
    /// field, planar velocity is response-shaped toward the commanded target, and sprint/frame/facing read as
    /// described below — the ops family <c>WorldBody</c>'s grounded operations (<c>ResolveYawAttitudeAndPlanarFrame</c>,
    /// <c>ComputePlanarTargetVelocity</c>, <c>ShapePlanarVelocity</c>, <c>SnapYawToPlanarIntent</c>,
    /// <c>ApplyVerticalGravity</c>) read. The world's <c>free</c> body motion program (full 6DOF, no ground pin) also
    /// authors this arm today: its ops read only <see cref="MoveSpeed"/>/<see cref="TurnSpeed"/>/<see cref="RiseGravity"/>
    /// (as a symmetric bleed rate, via <c>ApplyVerticalDecay</c>) — a strict subset — so <see cref="Grounded"/> is a
    /// superset every existing op family can be validated against; the fields a program's ops don't touch stay
    /// authored-but-inert for that kit, same as before this seam existed.
    /// </summary>
    /// <param name="MoveSpeed">Locomotion speed in world units per second — the profileless fallback a stand-in advances on
    /// (a seated player reads its live profile's speed instead, so <c>identity.motion</c> stays real-time).</param>
    /// <param name="TurnSpeed">Turn speed in radians per second (the profileless fallback counterpart to <paramref name="MoveSpeed"/>).</param>
    /// <param name="RiseGravity">The downward acceleration while rising (u/s²) — the floaty top of the arc.</param>
    /// <param name="FallGravity">The downward acceleration while falling (u/s²) — the snappy descent (heavier than the rise).</param>
    /// <param name="MaxFallSpeed">The terminal fall speed the descent is clamped to (u/s).</param>
    /// <param name="Response">The velocity-response table (see <see cref="MotionResponse"/>) — it affects the simulation.
    /// The empty table snaps planar velocity instantly.</param>
    /// <param name="SprintMultiplier">The held-sprint speed multiplier, applied while
    /// <paramref name="SprintChannel"/> reads held; <c>1</c> is a no-op.</param>
    /// <param name="SprintChannel">The declared channel name a body reads while held (not edge-triggered — a continuous
    /// multiplier, unlike the press/release <see cref="ActionSpec"/> vocabulary) to apply <paramref name="SprintMultiplier"/>,
    /// or <see langword="null"/> (the default) for a kit with no sprint capability. Resolved to an ordinal once, alongside
    /// every other kit-channel name, by <see cref="FixedWorldKit.Compile"/> — an unresolvable name (validator-refused
    /// already) reads as "no sprint" rather than throwing.</param>
    /// <param name="MoveFrame">Which frame <c>MoveForward</c>/<c>MoveStrafe</c> resolve in.
    /// <see cref="MotionMoveFrame.Heading"/> explicitly rotates the commanded planar target by the body's own
    /// integrated heading. <see cref="MotionMoveFrame.World"/> (the default) takes the two channels as
    /// axes already in world frame — the seat's client composes the camera yaw into the submitted intent before it ever
    /// reaches the wire, so the sim never reads a camera pose (determinism: no camera state enters simulation).</param>
    /// <param name="FacingSnap">Under <see cref="MotionMoveFrame.World"/> only: whether the body's facing snaps to
    /// <c>Atan2</c> of the commanded planar direction every tick that carries input (no turn-rate ramp, no skid) rather
    /// than holding its heading. Ignored under <see cref="MotionMoveFrame.Heading"/>, where facing is the integrated
    /// heading by construction. <see langword="true"/> is the default.</param>
    /// <param name="MoveSpeedEnvelope">The inclusive bound a seated player's live profile speed (and the profileless
    /// <paramref name="MoveSpeed"/> fallback) is clamped to at seat time, or <see langword="null"/> (the default) for
    /// no bound — a feel-pinned world authors this to keep a seat's speed inside its own kit's envelope regardless of
    /// what the player's identity requests. <see langword="null"/> reproduces today's unclamped behavior exactly;
    /// <c>Min == Max</c> pins the effective speed outright; a narrower-than-wide-open range still admits a bounded
    /// profile override. See <see cref="MotionScalarEnvelope"/>.</param>
    public sealed record Grounded(
        float MoveSpeed,
        float TurnSpeed,
        float RiseGravity,
        float FallGravity,
        float MaxFallSpeed,
        IReadOnlyList<MotionResponse> Response,
        float SprintMultiplier,
        string? SprintChannel = null,
        MotionMoveFrame MoveFrame = MotionMoveFrame.World,
        bool FacingSnap = true,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MotionScalarEnvelope? MoveSpeedEnvelope = null
    ) : WorldMotionModel;

    /// <summary>
    /// Anisotropic body-frame locomotion — the racing-vehicle arm the <c>ResolveVehicleFrame</c>/
    /// <c>ShapeVehicleVelocity</c> operations read. Velocity decomposes into longitudinal/lateral (and residual)
    /// body-frame components, each converging at its own authored rate — what separates a vehicle from
    /// <see cref="Grounded"/>, whose isotropic planar shaping cannot tune grip apart from acceleration. Steering
    /// authority scales with longitudinal speed (no spinning in place, looser at top speed) and reverses sign with
    /// reversing travel. One row serves the ground, hover, and air variants: a contact-pinned variant pairs this
    /// arm with a program keeping <c>ApplyVerticalGravity</c> (grounded-style vertical ownership and ramp
    /// ballistics), a flying variant pairs it with <c>ApplyVerticalDecay</c> and a positive
    /// <paramref name="PitchRate"/> so climb emerges from the pitched facing.
    /// </summary>
    /// <param name="TopSpeed">The forward speed (u/s) full throttle converges on.</param>
    /// <param name="ReverseTopSpeed">The reverse speed (u/s) full back-throttle converges on from rest; <c>0</c>
    /// forbids reversing.</param>
    /// <param name="Accel">The longitudinal convergence rate (u/s²) while throttle commands more speed.</param>
    /// <param name="Brake">The longitudinal convergence rate (u/s²) while back-throttle opposes forward travel.</param>
    /// <param name="CoastDrag">The longitudinal convergence rate (u/s²) toward rest with throttle centered, and the
    /// decay rate while over the commanded speed (the post-boost bleed).</param>
    /// <param name="Grip">The lateral convergence rate (u/s²) toward zero slip — traction. Lower is slidier.</param>
    /// <param name="SteerRate">The yaw rate (rad/s) at full steering authority.</param>
    /// <param name="SteerReferenceSpeed">The longitudinal speed (u/s) at which steering authority peaks; authority
    /// rises linearly from zero at standstill.</param>
    /// <param name="SteerFalloff">The fraction of full steering authority remaining at <paramref name="TopSpeed"/>,
    /// in <c>[0, 1]</c>; authority falls linearly from the reference speed.</param>
    /// <param name="PitchRate">The pitch rate (rad/s) the Pitch channel commands; <c>0</c> locks the frame planar
    /// (the ground and hover variants). Positive selects the flying variant's pitched facing, clamped inside the
    /// integrator so the frame can never flip past vertical.</param>
    /// <param name="RiseGravity">The upward-motion gravity (u/s²) — the contact-pinned variant's arc top, and the
    /// flying variant's vertical-impulse bleed rate (via <c>ApplyVerticalDecay</c>).</param>
    /// <param name="FallGravity">The falling gravity (u/s²) under <c>ApplyVerticalGravity</c>.</param>
    /// <param name="MaxFallSpeed">The terminal fall speed (u/s) under <c>ApplyVerticalGravity</c>.</param>
    /// <param name="DriftGrip">The lateral convergence rate (u/s²) replacing <paramref name="Grip"/> while
    /// <paramref name="DriftChannel"/> reads held — the deliberate low-traction state. Required positive when a
    /// drift channel is declared; ignored without one.</param>
    /// <param name="DriftSteerScale">The steering-authority multiplier while drifting (the tightened drift arc).</param>
    /// <param name="BoostMultiplier">The <paramref name="TopSpeed"/> multiplier while <paramref name="BoostChannel"/>
    /// reads held; <c>1</c> is a no-op. The timed item boost is <c>planarImpulse</c>, not this.</param>
    /// <param name="DriftChannel">The declared channel name read while held to drift, or <see langword="null"/> for a kit
    /// that cannot drift. Resolved to an ordinal once by <see cref="FixedWorldKit.Compile"/>.</param>
    /// <param name="BoostChannel">The declared channel name read while held to boost, or <see langword="null"/> for a kit
    /// with no held boost. Resolved through the same held-channel seam as <see cref="Grounded.SprintChannel"/>.</param>
    /// <param name="TopSpeedEnvelope">The racing-integrity clamp: the inclusive bound the resolved base
    /// <paramref name="TopSpeed"/> is pinned to at resolve time — a live <c>world.row.set kits</c> retune (deliberately
    /// admitted even past the bound; <see cref="WorldDefinitionValidator"/> checks only the envelope's own shape,
    /// never that <paramref name="TopSpeed"/> already sits inside it — the clamp exists precisely to catch a retune
    /// the envelope disagrees with, so requiring conformance up front would refuse the case it exists for), and any
    /// future per-seat vehicle stat resolve, both pass through it, or <see langword="null"/> (the default) for no
    /// bound. <paramref name="BoostMultiplier"/> multiplies after this clamp, never before — the envelope pins the
    /// base top speed, boost rides on top, the same sprint-after-clamp precedent
    /// <see cref="Grounded.MoveSpeedEnvelope"/> established for the grounded arm. The vehicle arm's resolve
    /// deliberately never reads a seated profile's speed (a kart's speed is the kit's), so this is the only seat-time
    /// clamp the vehicle arm has — unlike the grounded arm's envelope, which bounds a fallback a separate live
    /// profile read can diverge from, so grounded's own baseline is still required to sit inside its own bound.
    /// See <see cref="MotionScalarEnvelope"/>.</param>
    public sealed record Vehicle(
        float TopSpeed,
        float ReverseTopSpeed,
        float Accel,
        float Brake,
        float CoastDrag,
        float Grip,
        float SteerRate,
        float SteerReferenceSpeed,
        float SteerFalloff,
        float PitchRate,
        float RiseGravity,
        float FallGravity,
        float MaxFallSpeed,
        float DriftGrip = 0f,
        float DriftSteerScale = 1f,
        float BoostMultiplier = 1f,
        string? DriftChannel = null,
        string? BoostChannel = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MotionScalarEnvelope? TopSpeedEnvelope = null
    ) : WorldMotionModel;

    /// <summary>
    /// Submerged locomotion in the world's standing-water medium (the <c>water</c> section, which a kit declaring
    /// this arm requires): 3D thrust in the body's yaw frame with an explicit vertical channel, planar velocity
    /// converged through the response table (thrust against water drag — the engage rate is thrust authority, the
    /// release rate is the drag coast), and a single vertical channel owned end to end by the surface stage — the
    /// medium's own drift/settle folded into the commanded thrust target before that same response-row convergence
    /// runs, so nothing else writes vertical velocity independently. The ops family:
    /// <c>ResolveYawAttitudeAndPlanarFrame</c>, <c>ComputeSwimTargetVelocity</c>, <c>ShapePlanarVelocity</c>,
    /// <c>ApplyBuoyancyAndSurface</c>, <c>IntegratePlanarAndVerticalVelocity</c>. The body's attitude stays a pure
    /// yaw rotation. Aim-directed diving is the seat's composition, and only under <see cref="MotionMoveFrame.World"/>
    /// (<see cref="MoveFrame"/>): the rendered camera's elevation splits the commanded forward direction into planar and
    /// vertical channels client-side (the same determinism seam <see cref="Grounded.MoveFrame"/> documents), never a
    /// camera pose entering the sim. The explicit MoveUp channel this splits into is orthogonal to that composition
    /// and stays live regardless of <see cref="MoveFrame"/> — a body dives on raw MoveUp input alone with no
    /// aim-composed seat at all.
    /// </summary>
    /// <param name="ThrustSpeed">Peak swim speed in world units per second — the profileless fallback (a seated
    /// player reads its live profile's move speed instead, exactly as <see cref="Grounded.MoveSpeed"/> does).</param>
    /// <param name="TurnSpeed">Turn speed in radians per second (the profileless fallback counterpart).</param>
    /// <param name="VerticalThrustFraction">The fraction of <paramref name="ThrustSpeed"/> the vertical channel
    /// commands — swimmers climb and dive slower than they cruise. <c>1</c> is fully isotropic thrust.</param>
    /// <param name="Response">The velocity-response table (see <see cref="MotionResponse"/>) — it affects the simulation, read
    /// for both the planar and the vertical convergence. The empty table snaps instantly (no drag, no coast); a
    /// water feel wants at least an always-row.</param>
    /// <param name="Buoyancy">The medium's idle vertical drift velocity (u/s, signed) below the bob band: positive
    /// drifts the body up toward its float line, negative sinks, zero holds depth. Folded into the commanded thrust
    /// target — see <see cref="FixedSwimTuning"/> — never applied as a separate acceleration.</param>
    /// <param name="MaxRiseSpeed">The terminal ascent speed (u/s) the vertical channel is clamped to.</param>
    /// <param name="MaxSinkSpeed">The terminal descent speed (u/s) the vertical channel is clamped to.</param>
    /// <param name="SurfaceSettleRate">The proportional settle gain (1/s) toward the float line, applied inside the
    /// bob band and above it (breach recovery): the medium's target velocity there is the displacement from the
    /// line times this gain, so a held ascent parks where thrust and settle balance instead of breaching.</param>
    /// <param name="FloatDepth">How far below the waterline (world units) the body origin rests when floating — and
    /// the bob band's half-width around that rest line (one knob deliberately: the band a body settles in is the
    /// depth scale it settles at). The validator only checks this is positive and finite — it cannot see the
    /// document's contact geometry, so it does not (and cannot) check this against the local water column's depth. A
    /// <see cref="FloatDepth"/> deeper than the floor below it parks the body on the floor instead of at the float
    /// line, with <c>AtSurface</c> never true there — a geometry fact deliberately outside this validator's
    /// reach.</param>
    /// <param name="SprintMultiplier">The held-burst speed multiplier applied while <paramref name="SprintChannel"/>
    /// reads held; <c>1</c> is a no-op. Scales the whole thrust vector, vertical included.</param>
    /// <param name="SprintChannel">The declared channel name read while held for the burst, or <see langword="null"/>
    /// (the default) for a kit with no burst — the same resolution path <see cref="Grounded.SprintChannel"/>
    /// documents.</param>
    /// <param name="MoveFrame">Which frame <c>MoveForward</c>/<c>MoveStrafe</c> resolve in — the same two-frame
    /// choice, and the same client-side camera composition seam, as <see cref="Grounded.MoveFrame"/>.</param>
    /// <param name="FacingSnap">Under <see cref="MotionMoveFrame.World"/> only: snap facing to the commanded planar
    /// direction each tick carrying input, as <see cref="Grounded.FacingSnap"/> documents.</param>
    /// <param name="ThrustSpeedEnvelope">The inclusive bound a seated player's live profile speed (and the
    /// profileless <paramref name="ThrustSpeed"/> fallback) is clamped to at seat time, or <see langword="null"/>
    /// (the default) for no bound — the same seam as <see cref="Grounded.MoveSpeedEnvelope"/>, for this arm's own
    /// thrust speed. <see langword="null"/> reproduces unclamped behavior exactly; <c>Min == Max</c> pins the
    /// effective thrust speed outright regardless of what a profile requests. See
    /// <see cref="MotionScalarEnvelope"/>.</param>
    public sealed record Swim(
        float ThrustSpeed,
        float TurnSpeed,
        float VerticalThrustFraction,
        IReadOnlyList<MotionResponse> Response,
        float Buoyancy,
        float MaxRiseSpeed,
        float MaxSinkSpeed,
        float SurfaceSettleRate,
        float FloatDepth,
        float SprintMultiplier,
        string? SprintChannel = null,
        MotionMoveFrame MoveFrame = MotionMoveFrame.World,
        bool FacingSnap = true,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MotionScalarEnvelope? ThrustSpeedEnvelope = null
    ) : WorldMotionModel;

    /// <summary>The declared held-multiplier channel of whichever arm this is — <see cref="Grounded.SprintChannel"/>,
    /// <see cref="Swim.SprintChannel"/>, or the vehicle arm's <see cref="Vehicle.BoostChannel"/> (the same
    /// held-multiplier seam under a different name) — or <see langword="null"/> for an arm (or a declaration) without
    /// one. The one sprint-resolution read <see cref="FixedWorldKit.Compile"/> and the seat binding surfaces share,
    /// so a new arm extends it here instead of each caller growing its own cast chain.</summary>
    public string? DeclaredSprintChannel => this switch {
        Grounded grounded => grounded.SprintChannel,
        Vehicle vehicle => vehicle.BoostChannel,
        Swim swim => swim.SprintChannel,
        _ => null,
    };

    /// <summary>The declared move frame of whichever arm this is — <see cref="MotionMoveFrame.Heading"/> for an arm
    /// without the choice. The client's camera composition keys off this, arm-agnostically.</summary>
    public MotionMoveFrame DeclaredMoveFrame => this switch {
        Grounded grounded => grounded.MoveFrame,
        Swim swim => swim.MoveFrame,
        _ => MotionMoveFrame.Heading,
    };
}

/// <summary>Which frame a grounded body's <c>MoveForward</c>/<c>MoveStrafe</c> channels resolve in — a per-kit choice
/// (<see cref="WorldMotionModel.Grounded.MoveFrame"/>), never a global switch.</summary>
[JsonConverter(typeof(StrictEnumConverter<MotionMoveFrame>))]
public enum MotionMoveFrame : byte {
    /// <summary>Body-relative: the commanded planar target rotates by the body's own integrated heading (tank
    /// controls) — the world's historical, and default, behavior.</summary>
    Heading,

    /// <summary>World-relative: the two channels are read as already-resolved world axes. The seat composes its
    /// camera yaw into the submitted intent client-side, before submission — the sim itself never sees a camera pose,
    /// preserving determinism (see <see cref="WorldMotionModel.Grounded.MoveFrame"/>'s remarks).</summary>
    World,
}

/// <summary>
/// The world's motion defaults — the profileless locomotion speeds a stand-in with no seated profile advances on.
/// This is the whole top-level motion section: gravity and the velocity-response table are per-kit
/// (<see cref="WorldKit.Motion"/>), which is the only place
/// a body ever reads them from, and <c>world.row.set kits</c> is the surface that moves them.
/// </summary>
/// <remarks>Unmapped members are rejected by name rather than accepting a value nothing reads.</remarks>
/// <param name="MoveSpeed">Locomotion speed in world units per second — the profileless fallback a stand-in advances on
/// (a seated player reads its live profile's speed instead, so <c>identity.motion</c> stays real-time).</param>
/// <param name="TurnSpeed">Turn speed in radians per second (the profileless fallback counterpart to <paramref name="MoveSpeed"/>).</param>
/// <param name="MaxSmoothError">The largest server-correction position error, in world units, that presentation may
/// ease instead of snapping.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct WorldMotionDefaults(
    float MoveSpeed,
    float TurnSpeed,
    float MaxSmoothError
);

/// <summary>The one-time fixed-point compilation of the world's motion defaults. Runtime simulation reads only this
/// form.</summary>
public readonly record struct FixedMotionDefaults(FixedQ4816 MoveSpeed, FixedQ4816 TurnSpeed, FixedQ4816 MaxSmoothError) {
    /// <summary>Compiles the authored floating-point motion defaults to their fixed-point form.</summary>
    public static FixedMotionDefaults Compile(in WorldMotionDefaults motion) => new(
        MoveSpeed: FixedQ4816.FromDouble(value: motion.MoveSpeed),
        TurnSpeed: FixedQ4816.FromDouble(value: motion.TurnSpeed),
        MaxSmoothError: FixedQ4816.FromDouble(value: motion.MaxSmoothError)
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

/// <summary>An authored inclusive bound on one <see cref="WorldMotionModel"/> scalar — the reusable shape every
/// arm's overridable scalar clamps through (today: <see cref="WorldMotionModel.Grounded.MoveSpeedEnvelope"/>; a
/// future arm's own scalar adopts the same record, never a bespoke bound). Applied at the seat-time profile
/// resolve, never inside the sim: the value simulation reads is already clamped, so the guarantee holds regardless
/// of what a player's identity requests. Absent (the field default) is wide-open — today's behavior exactly.</summary>
/// <param name="Min">The least admitted value (inclusive).</param>
/// <param name="Max">The greatest admitted value (inclusive) — <see cref="WorldDefinitionValidator"/> refuses
/// <paramref name="Max"/> &lt; <paramref name="Min"/> by name. Equal to <paramref name="Min"/> pins the scalar
/// outright regardless of what a profile requests.</param>
public readonly record struct MotionScalarEnvelope(float Min, float Max);

/// <summary>A row's solidity facet — it participates in contact resolution using its own declared shape. Presence is
/// the whole switch; <see langword="null"/> means decoration — the row is drawn but bodies pass through it.</summary>
/// <param name="Margin">The signed skin added to the shape for contact purposes. Positive fattens the collider past the
/// drawn surface; negative lets a body sink in. Compensates the smooth-union blend.</param>
public sealed record WorldSolid(float Margin);

/// <summary>A kit's closed body-volume vocabulary. A kit with no collider is not solved against the contact field.</summary>
[JsonDerivedType(typeof(WorldCollider.Sphere), typeDiscriminator: "sphere")]
[JsonDerivedType(typeof(WorldCollider.Capsule), typeDiscriminator: "capsule")]
[JsonDerivedType(typeof(WorldCollider.Box), typeDiscriminator: "box")]
[JsonDerivedType(typeof(WorldCollider.FromCreation), typeDiscriminator: "fromCreation")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldCollider {
    /// <summary>The largest number of convex volumes one body collider may compile into. This bounds the field
    /// provider's per-body sample cost, which scales linearly with the volume count.</summary>
    public const int MaxVolumes = 16;

    private WorldCollider() {
    }

    /// <summary>A sphere resting on the body root.</summary>
    /// <param name="Radius">The sphere radius.</param>
    public sealed record Sphere(float Radius) : WorldCollider;

    /// <summary>A capsule whose lower sphere rests on the body root.</summary>
    /// <param name="Endpoint">The body-local vector from the lower sphere center to the upper sphere center.</param>
    /// <param name="Radius">The capsule radius.</param>
    public sealed record Capsule(Vector3 Endpoint, float Radius) : WorldCollider;

    /// <summary>An oriented box resting on the body root before its local rotation is applied.</summary>
    /// <param name="HalfExtents">The positive half-extents.</param>
    /// <param name="Rotation">The body-local orientation.</param>
    public sealed record Box(Vector3 HalfExtents, Quaternion Rotation) : WorldCollider;

    /// <summary>The finite primitive bounds emitted by a creation, composed into one compound body collider.</summary>
    /// <param name="CreationId">The referenced <see cref="WorldCreation.Id"/>.</param>
    public sealed record FromCreation(string CreationId) : WorldCollider;
}

/// <summary>The contact solver's world-scale tuning.</summary>
/// <param name="Requirements">The contact qualities the world requires. An empty list permits analytic primitive
/// contact; any declared requirement selects the SDF field.</param>
/// <param name="ContactSkin">The signed skin the solver keeps between a body and every surface (world units).</param>
/// <param name="MaxIterations">The relaxation iteration count per tick (above 8 is a solver pathology, not a choice).</param>
/// <param name="MaxSlopeDegrees">The steepest surface a body still counts as standing on. A contact whose normal leans
/// further from the body's up axis than this pushes the body but never grounds it — the walkable-slope limit.</param>
/// <param name="GradientProbe">The finite-difference step field contact samples the surface normal with, in world
/// units; 0 takes the evaluator's own default. Meaningful only when a requirement selects field contact.</param>
public sealed record WorldCollision(IReadOnlyList<WorldContactRequirement> Requirements, float ContactSkin,
    int MaxIterations, float MaxSlopeDegrees, float GradientProbe);

/// <summary>A contact quality authored by the world, independent of the engine implementation that supplies it.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldContactRequirement>))]
public enum WorldContactRequirement : byte {
    /// <summary>Blended creation surfaces remain solid across their smooth-union seams.</summary>
    SmoothUnionContact,

    /// <summary>A body's up direction follows the contacted field gradient rather than world <c>+Y</c>.</summary>
    GradientDerivedUp,
}

/// <summary>The authored seed identities and player presentation tuning.</summary>
/// <param name="Identities">The identities used to seed an absent owned-world directory.</param>
/// <param name="NeutralColor">The placeholder color used when no profile identity is available.</param>
/// <param name="ColorSequence">The deterministic sequence used for generated profile colors.</param>
/// <param name="Saturation">The saturation used for generated colors.</param>
/// <param name="Value">The brightness used for generated colors.</param>
/// <param name="ColorSearchLimit">The number of generated colors checked before accepting the next sequence value.</param>
/// <param name="NoseFactor">The body-color multiplier used for avatar accents.</param>
/// <param name="PickerThreshold">The stick magnitude that cycles a pending profile choice.</param>
/// <param name="PickerNeutralColor">The pending-avatar desaturation target.</param>
/// <param name="PickerNeutralBlend">The pending-avatar blend amount toward <paramref name="PickerNeutralColor"/>.</param>
/// <param name="SeatLook">The control feel a seat of this document wakes with. required — there is no engine
/// fallback, so a document either states what its seats should feel like or fails validation. Read per seat from
/// whichever document owns it: the world's for an unclaimed seat, the joined identity's own for a claimed one, which
/// is how a player's feel travels with their profile (see <see cref="WorldSeatLook"/>).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlayerDefaults(
    IReadOnlyList<WorldIdentitySeed> Identities,
    string NeutralColor,
    WorldSequence ColorSequence,
    float Saturation,
    float Value,
    int ColorSearchLimit,
    float NoseFactor,
    float PickerThreshold,
    string PickerNeutralColor,
    float PickerNeutralBlend,
    WorldSeatLook SeatLook
);

/// <summary>One authored identity used to seed an owned world.</summary>
/// <param name="Id">The stable profile id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Color">The body color as <c>#RRGGBB</c>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldIdentitySeed(WorldSafeName Id, string Name, string Color);

/// <summary>The person or character identity an owned world represents.</summary>
/// <param name="Id">The stable owned-world id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Color">The body color as <c>#RRGGBB</c>.</param>
/// <param name="MoveSpeedState">The fixed state row supplying locomotion speed.</param>
/// <param name="TurnSpeedState">The fixed state row supplying turn speed.</param>
/// <param name="Controllers">Machine/device state-slot references used for controller pre-selection.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldIdentityDefinition(WorldSafeName Id, string Name, string Color, WorldCellName MoveSpeedState, WorldCellName TurnSpeedState, IReadOnlyList<WorldControllerStateSlots>? Controllers = null);

/// <summary>Two text state rows that identify one reconnect-stable controller.</summary>
/// <param name="MachineState">The row containing the machine id.</param>
/// <param name="DeviceState">The row containing the device id.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldControllerStateSlots(WorldCellName MachineState, WorldCellName DeviceState);

/// <summary>Selects the contact implementation implied by authored requirements.</summary>
public static class WorldContactSelection {
    /// <summary>Returns whether any authored requirement needs the SDF contact field.</summary>
    /// <param name="collision">The authored contact requirements and solver tuning.</param>
    /// <returns><see langword="true"/> when field contact is required; otherwise <see langword="false"/>.</returns>
    public static bool RequiresField(WorldCollision collision) => (collision.Requirements is { Count: > 0 });
}

/// <summary>An engine-published per-body sim fact the action predicates gate on. Facts are engine code.</summary>
/// <remarks>Admission rule: a new fact is privileged sim state the effects/predicates cannot derive from existing
/// facts; add one only then.</remarks>
[JsonConverter(typeof(StrictEnumConverter<ActionFact>))]
public enum ActionFact : byte {
    /// <summary>The body rests on a walkable contact surface.</summary>
    Grounded,

    /// <summary>The body is off every walkable contact surface.</summary>
    Airborne,

    /// <summary>The body's vertical velocity is positive.</summary>
    Rising,

    /// <summary>The body's vertical velocity is negative.</summary>
    Falling,

    /// <summary>A targeted effect was applied by another body on the preceding completed tick.</summary>
    AffectedBy,

    /// <summary>The body's origin is below the waterline. Written by the swim model's surface stage
    /// (<see cref="BodyMotionOp.ApplyBuoyancyAndSurface"/>); holds one tick behind that stage's evaluation, the same
    /// one-tick-behind discipline <see cref="Grounded"/> reads under.</summary>
    Submerged,

    /// <summary>The body's origin is inside the swim model's surface bob band (within its float depth of the float
    /// line). Written by the same surface stage as <see cref="Submerged"/>, on the same one-tick-behind terms.</summary>
    AtSurface,
}

/// <summary>A data-composable gate over body facts and named action state. A trigger fires only while its gate holds.
/// The <c>$type</c> string is
/// the JSON discriminator, the same convention every polymorphic row family uses; a new predicate kind is a new
/// derived record plus its <see cref="JsonDerivedTypeAttribute"/> line.</summary>
[JsonDerivedType(typeof(ActionPredicate.Now), typeDiscriminator: "now")]
[JsonDerivedType(typeof(ActionPredicate.Recently), typeDiscriminator: "recently")]
[JsonDerivedType(typeof(ActionPredicate.CompareState), typeDiscriminator: "compareState")]
[JsonDerivedType(typeof(ActionPredicate.TimerElapsed), typeDiscriminator: "timerElapsed")]
[JsonDerivedType(typeof(ActionPredicate.All), typeDiscriminator: "all")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ActionPredicate {
    /// <summary>The fact holds this tick.</summary>
    public sealed record Now(ActionFact Fact) : ActionPredicate;

    /// <summary>The fact held within the last <paramref name="WindowSeconds"/> — a per-instance recency clock,
    /// refreshed while the fact holds and decaying otherwise (coyote time is <c>Recently(Grounded, w)</c>).</summary>
    public sealed record Recently(ActionFact Fact, float WindowSeconds) : ActionPredicate;

    /// <summary>Compares a named state cell against either a fixed authored value, or — world scope only — another
    /// named state cell/reserved channel read live at the same evaluation. Both spellings are authorable; exactly one
    /// of <paramref name="Value"/> and <paramref name="ComparandState"/> may be present (refused by name when both or
    /// neither are). The comparand-row spelling is what lets a gate track a moving threshold — <c>$tick</c> compared
    /// against a schedule row the rule's own effects advance is "every N ticks"; a round row compared against a
    /// declared length row is a round boundary — composition over the same two-sided comparison, never a new
    /// mechanism.</summary>
    /// <param name="State">At body scope, a named counter slot the kit declares. At world scope (see
    /// <see cref="WorldRule"/>), a declared <c>state</c>-section row name, or one of
    /// <see cref="WorldRuleFacts"/>'s reserved channels.</param>
    /// <param name="Comparison">The comparison to apply.</param>
    /// <param name="Value">The authored constant comparand, or <see langword="null"/> when
    /// <paramref name="ComparandState"/> spells the comparand instead. Required (non-null) at body scope, where a
    /// comparand row reference is refused.</param>
    /// <param name="Key">At world scope, the cell inside <paramref name="State"/> to read —
    /// <see langword="null"/> reads the row's slot cell, which a keyed row does not have (refused by name rather
    /// than silently reading <c>cells[0]</c>). At body scope a non-null key is refused: a per-body action-state slot
    /// is not keyed, and a parsed-and-discarded field is worse than no field.</param>
    /// <param name="ComparandState">world scope only (refused at body scope, on the same terms as
    /// <paramref name="Key"/>): another declared <c>state</c>-section row name, or one of
    /// <see cref="WorldRuleFacts"/>'s reserved channels, read live and compared instead of <paramref name="Value"/>.
    /// A dotted spelling (an author reaching for <c>row.key</c> in one string) is refused by name — address the cell
    /// with <paramref name="ComparandKey"/> instead. Comparing across incompatible cell kinds (an <c>int</c> row
    /// against a <c>fixed</c> row, say) is refused by name — mixing scales silently is worse than naming the
    /// mismatch.</param>
    /// <param name="ComparandKey">The cell inside <paramref name="ComparandState"/>, on the same (row, key) terms as
    /// <paramref name="Key"/>. Refused when <paramref name="ComparandState"/> names a reserved channel or is absent.</param>
    public sealed record CompareState(
        string State,
        ActionStateComparison Comparison,
        float? Value = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandKey = null
    ) : ActionPredicate;

    /// <summary>Whether a named timer slot has drained.</summary>
    public sealed record TimerElapsed(string State) : ActionPredicate;

    /// <summary>Every inner predicate holds (conjunction).</summary>
    public sealed record All(IReadOnlyList<ActionPredicate> Predicates) : ActionPredicate;
}

/// <summary>An authored operand row lowered to a <see cref="BodyMotionOp"/> and executed by the body instruction
/// interpreter when its trigger fires.</summary>
[JsonDerivedType(typeof(ActionEffect.SetVerticalVelocity), typeDiscriminator: "setVerticalVelocity")]
[JsonDerivedType(typeof(ActionEffect.ScaleVerticalVelocity), typeDiscriminator: "scaleVerticalVelocity")]
[JsonDerivedType(typeof(ActionEffect.PlanarImpulse), typeDiscriminator: "planarImpulse")]
[JsonDerivedType(typeof(ActionEffect.SetState), typeDiscriminator: "setState")]
[JsonDerivedType(typeof(ActionEffect.AddState), typeDiscriminator: "addState")]
[JsonDerivedType(typeof(ActionEffect.CountdownState), typeDiscriminator: "countdownState")]
[JsonDerivedType(typeof(ActionEffect.StartTimer), typeDiscriminator: "startTimer")]
[JsonDerivedType(typeof(ActionEffect.Designate), typeDiscriminator: "designate")]
[JsonDerivedType(typeof(ActionEffect.Generate), typeDiscriminator: "generate")]
[JsonDerivedType(typeof(ActionEffect.UpsertHudPanel), typeDiscriminator: "upsertHudPanel")]
[JsonDerivedType(typeof(ActionEffect.RemoveHudPanel), typeDiscriminator: "removeHudPanel")]
[JsonDerivedType(typeof(ActionEffect.UpsertPlacement), typeDiscriminator: "upsertPlacement")]
[JsonDerivedType(typeof(ActionEffect.RemovePlacement), typeDiscriminator: "removePlacement")]
[JsonDerivedType(typeof(ActionEffect.Save), typeDiscriminator: "save")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ActionEffect {
    /// <summary>Writes the body's vertical-velocity channel (the jump launch / the surge). Under the grounded model
    /// gravity owns its decay; under the free model it bleeds to zero at the tuning's rise gravity (no fall phase).</summary>
    public sealed record SetVerticalVelocity(float Velocity, ActionTarget Target = ActionTarget.Self) : ActionEffect;

    /// <summary>Multiplies the body's vertical velocity (the jump cut; gate on <see cref="ActionFact.Rising"/>).</summary>
    public sealed record ScaleVerticalVelocity(float Factor, ActionTarget Target = ActionTarget.Self) : ActionEffect;

    /// <summary>A timed planar velocity overlay (the dash): <paramref name="BodyDirection"/> is rotated by the body's
    /// attitude at fire time and ridden at <paramref name="Speed"/> for <paramref name="DurationSeconds"/>, integrated
    /// through its own accumulator on top of the model's motion — integration itself is untouched.</summary>
    public sealed record PlanarImpulse(Vector3 BodyDirection, float Speed, float DurationSeconds, ActionTarget Target = ActionTarget.Self) : ActionEffect;

    /// <summary>Writes a named state cell — a kit counter slot at body scope, a <c>state</c>-section row's cell at
    /// world scope (see <see cref="WorldRule"/>).</summary>
    /// <param name="State">The counter slot (body scope) or state row name (world scope).</param>
    /// <param name="Value">The literal value to write, or <see langword="null"/> when <paramref name="FromState"/>
    /// spells a live operand to copy instead — world scope only, exactly one of the two is authored (refused by name
    /// when both or neither are present, the same duality <see cref="ActionPredicate.CompareState"/>'s own comparand
    /// carries). Required (non-null) at body scope, where a live copy source is refused.</param>
    /// <param name="Target">The addressed entity — body scope only; a non-<see cref="ActionTarget.Self"/> target is
    /// refused at world scope, where there is no entity to select.</param>
    /// <param name="Key">The cell inside <paramref name="State"/> at world scope — <see langword="null"/> writes the
    /// row's slot cell, which a keyed row does not have (refused by name). Refused at body scope.</param>
    /// <param name="FromState">world scope only (refused at body scope, on the same terms as <paramref name="Value"/>):
    /// another declared <c>state</c>-section row name, or one of <see cref="WorldRuleFacts"/>'s reserved channels,
    /// read live at fire time and copied in place of an authored <paramref name="Value"/> — the row that resets to
    /// another row's own current value (a shadow row mirroring a counter someone else advances), never only a
    /// standing literal. Resolved through the same operand walk <see cref="ActionPredicate.CompareState"/>'s own
    /// <c>ComparandState</c> uses; mixing a <c>fixed</c> row into an <c>int</c> destination (or the reverse) is
    /// refused by name rather than coerced.</param>
    /// <param name="FromKey">The cell inside <paramref name="FromState"/>, on the same (row, key) terms as
    /// <paramref name="Key"/>. Refused when <paramref name="FromState"/> names a reserved channel or is absent.</param>
    /// <param name="ValueSeconds">world scope only (refused at body scope, on the same terms as <paramref name="Value"/>
    /// and <paramref name="FromState"/> — exactly one of the three is authored): an alternative to
    /// <paramref name="Value"/> for a <c>kind=int</c> state row a companion <see cref="CountdownState"/> effect
    /// decrements once per simulation tick (a countdown/cooldown). Authored in seconds — a physical unit, not a tick count,
    /// so a world's rate can change without silently retuning every cooldown — and converted once at rule compile
    /// time to an exact whole engine-tick count via <see cref="Puck.Maths.FixedTickConversion.TryDurationEngineTicksExact"/>,
    /// never re-derived at runtime and never rounded: a duration that is not an exact whole engine-tick count is
    /// refused rather than silently rounded away (<see cref="WorldRuleRefusal.DurationNotExactEngineTicks"/>). Typed
    /// <see cref="decimal"/> rather than <see langword="float"/> because JSON deserializes a number token to
    /// <see cref="decimal"/> exactly (base-10, no binary-float intermediate), and most terminating decimals — the
    /// only ones an author can spell — have no exact binary float or fixed-point spelling either. See
    /// <see cref="WorldRuleCompiler"/>.</param>
    public sealed record SetState(
        string State,
        float? Value = null,
        ActionTarget Target = ActionTarget.Self,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ValueSeconds = null
    ) : ActionEffect;

    /// <summary>Adds to a named state cell — a kit counter slot at body scope, a <c>state</c>-section row's cell at
    /// world scope (see <see cref="WorldRule"/>).</summary>
    /// <param name="State">The counter slot (body scope) or state row name (world scope).</param>
    /// <param name="Value">The literal addend, or <see langword="null"/> when <paramref name="FromState"/> spells a
    /// live addend instead — see <see cref="SetState.Value"/>'s remarks; the same value/from duality, required
    /// (non-null) at body scope.</param>
    /// <param name="Target">The addressed entity — body scope only.</param>
    /// <param name="Key">The cell inside <paramref name="State"/> at world scope; refused at body scope.</param>
    /// <param name="FromState">world scope only — see <see cref="SetState.FromState"/>'s remarks; here the addend is
    /// read live rather than the replacement.</param>
    /// <param name="FromKey">The cell inside <paramref name="FromState"/> — see <see cref="SetState.FromKey"/>.</param>
    /// <param name="ValueSeconds">world scope only — see <see cref="SetState.ValueSeconds"/>'s remarks; here the
    /// converted tick count is the addend rather than the replacement.</param>
    public sealed record AddState(
        string State,
        float? Value = null,
        ActionTarget Target = ActionTarget.Self,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ValueSeconds = null
    ) : ActionEffect;

    /// <summary>Decrements a world-state countdown by the current simulation step's engine-tick width, saturating at
    /// zero. world scope only: the destination must be a <c>kind=int nonNegative=true</c> row. Unlike an authored
    /// <see cref="AddState"/> constant, this effect consumes the runtime step width, so changing the world's authored
    /// tick rate never retunes the duration. When the remaining duration is shorter than one step, the computed
    /// decrement is exactly the remaining value; it reaches zero without asking the explicit-write door to admit a
    /// negative candidate.</summary>
    /// <param name="State">The countdown state-row name.</param>
    /// <param name="Key">The cell inside <paramref name="State"/>; <see langword="null"/> addresses its slot.</param>
    public sealed record CountdownState(
        string State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
    ) : ActionEffect;

    /// <summary>Starts a named timer slot with an authored duration.</summary>
    public sealed record StartTimer(string State, float Seconds, ActionTarget Target = ActionTarget.Self) : ActionEffect;

    /// <summary>Submits the selected subject into a named target register.</summary>
    /// <param name="Register">The authored target-register name.</param>
    /// <param name="Target">The subject source.</param>
    public sealed record Designate(string Register, ActionTarget Target = ActionTarget.AffectingSubject) : ActionEffect;

    /// <summary>Redraws a draw site (a <c>state</c> row declaring a <see cref="WorldDraw"/>) — the one effect
    /// admissible at both scopes, and the join that makes authored randomness and world rules one arc rather than
    /// two: a kit action, a world rule, and the <c>world.generate</c> console verb all reduce to composing the same
    /// <c>WorldMutation.Generate</c> and letting it drain through the ordinary tick boundary, so journal/undo cover a
    /// draw for free wherever it was fired from. This is also how a draw's moment is authored: a
    /// <see cref="WorldDrawTiming.TickPeriod"/> site redraws on an ordinary <c>$tick</c>-scheduled rule and an
    /// <see cref="WorldDrawTiming.Event"/> site on an event-gated one, so timing costs no mutation ordinal. At body
    /// scope the firing is staged during the body's advance and enqueued for the next tick's drain (an honestly-
    /// reported one-tick latency: this is the first <see cref="ActionEffect"/> to write the document rather than
    /// per-body state, so it is the first to pay the pipeline's own round trip).</summary>
    /// <param name="Row">The draw site's row name. One name, not a (source, destination) pair: a site's source is its
    /// own facet and a site is a scalar slot, so there is nothing else to address.</param>
    public sealed record Generate(string Row) : ActionEffect;

    /// <summary>Upserts a whole HUD panel row — world scope only (refused at body scope: a per-body action has no HUD
    /// panel of its own to author). Admits <see cref="WorldMutation.UpsertHudPanel"/> into the world-rule effect set
    /// through the same seam <see cref="Generate"/> uses: the compiled effect submits the mutation stamped
    /// <see cref="WorldPrincipal.World"/>, which <c>WorldServer.TryAdmitMutation</c> admits structurally, so the
    /// panel's own validation (capacity, unknown binding) is the ordinary whole-document revalidation every
    /// <see cref="UpsertHudPanel"/> submission — console, addon, or rule — already passes through.</summary>
    /// <param name="Panel">The whole panel row, elements included.</param>
    public sealed record UpsertHudPanel(WorldHudPanel Panel) : ActionEffect;

    /// <summary>Removes a HUD panel row by id — world scope only. See <see cref="UpsertHudPanel"/>'s remarks.</summary>
    /// <param name="Id">The panel id to remove.</param>
    public sealed record RemoveHudPanel(string Id) : ActionEffect;

    /// <summary>Upserts a whole placement row — world scope only (refused at body scope: a per-body action has no
    /// placement of its own to author). Admits <see cref="WorldMutation.UpsertPlacement"/> into the world-rule effect
    /// set through the same seam <see cref="Generate"/> uses.</summary>
    /// <param name="Placement">The whole placement row.</param>
    public sealed record UpsertPlacement(WorldPlacement Placement) : ActionEffect;

    /// <summary>Removes a placement row by id — world scope only. See <see cref="UpsertPlacement"/>'s remarks.</summary>
    /// <param name="Id">The placement id to remove.</param>
    public sealed record RemovePlacement(string Id) : ActionEffect;

    /// <summary>Writes a session snapshot of the world to its own loaded file — world scope only (refused at body
    /// scope: a per-body action has no world file of its own to save). A rule gate now decides when a save happens (an
    /// every-N-ticks cadence, a boss-defeated edge), closing the one gap the mutation substrate could not: a rule
    /// could already express any cadence over <c>$tick</c> or a state fact, but had nothing to fire that composed a
    /// save — every prior save was a human typing <c>world.save</c>, so a crashed server rewound to the last manual
    /// one.</summary>
    /// <remarks>
    /// <para><b>Not a door — the one effect with no <see cref="WorldMutation"/> kind.</b> Every other admitted effect
    /// (<see cref="SetState"/>, <see cref="Generate"/>, <see cref="UpsertHudPanel"/>, <see cref="UpsertPlacement"/>, …)
    /// composes an ordinary mutation and rides <c>WorldServer.TryApplyMutation</c>: compose, whole-document validate,
    /// install, journal. <c>Save</c> does none of that — it writes no sim state, composes no candidate document, and
    /// journals nothing. It is deterministic in when it fires (an ordinary rule gate over tick/state facts, evaluated
    /// the same way on every run) and projection-only in what it does: the same settle-at-save capture
    /// <c>world.save</c> itself runs (<c>WorldSessionCapture.Capture</c>), which folds live session state into a
    /// snapshot it serializes — it never mutates the in-memory definition. The sim state after a tick carrying a fired
    /// save effect is bit-identical to a tick without one; a replay hash cannot see it, because there is nothing for a
    /// hash to see. That is why this effect needed no <c>KindMask</c> ordinal at all: it is not a mutation. It rides
    /// <c>WorldServer.FireWorldRuleEffect</c> directly instead — the one effect that does.</para>
    /// <para><b>No authored path — the world's own canonical home only.</b> A document that could point a rule's save
    /// at an arbitrary filesystem path is a hazard for no authoring benefit a fixed target does not already cover, so
    /// this effect carries no path field: it always writes to <c>WorldDefinitionSource.SourcePath</c>, the same
    /// resolution the console's own no-argument <c>world.save</c> uses (the file the world was loaded from — an
    /// explicit <c>--world</c> path or the shipped default file, both always file-backed at boot; there is no
    /// "homeless world" boot shape in this engine, so this effect has no compile-time path refusal to author).</para>
    /// <para><b>Throttle honesty — no hidden guard.</b> A <see cref="ActionTriggerMode.Level"/> rule gating this
    /// effect fires it every tick the gate holds — 240 saves/second of disk I/O at the fixed step. This effect adds no
    /// throttle beyond the ordinary <see cref="ActionTriggerMode"/> vocabulary every other effect already uses: that
    /// is the author's own footgun, the same one <see cref="WorldRule.Mode"/>'s own remarks document for a
    /// level-triggered <c>addState</c> ("wrote 503 journal entries across 500 ticks before this mode existed, which is
    /// a measurement, not a style preference") — <see cref="ActionTriggerMode.Edge"/> is what an autosave cadence
    /// wants, for the identical reason. A hidden per-effect guard would be exactly the config surface this repository
    /// does not have.</para>
    /// <para><b>Failure is narrated, never fatal.</b> A write that fails (disk full, the target's directory gone, a
    /// read-only file) is caught at the composition-root seam that performs it and printed on stderr by name; the tick
    /// that fired it continues normally, and nothing about the sim is rolled back — there was nothing to roll back.
    /// </para>
    /// </remarks>
    public sealed record Save : ActionEffect;
}

/// <summary>The entity an action effect addresses.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionTarget>))]
public enum ActionTarget : byte {
    /// <summary>The body whose trigger fired.</summary>
    Self,

    /// <summary>The target selected by the body's active producer.</summary>
    ProducerTarget,

    /// <summary>The body that applied the recipient's most recent targeted effect.</summary>
    AffectingSubject,
}

/// <summary>One engine edge/latch vocabulary, shared by every gated trigger the engine evaluates — a per-body fact
/// trigger (<see cref="ActionFactTrigger"/>) and a world rule (<see cref="WorldRule"/>) alike. It is deliberately not
/// two concepts with two spellings: "fires while the condition holds" and "fires once when the condition becomes
/// true" is the same distinction at both scopes, so it is the same enum.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionTriggerMode>))]
public enum ActionTriggerMode : byte {
    /// <summary>Fires every evaluation the condition holds — the default, and the right shape for a continuous effect
    /// (a per-tick drain, a standing impulse).</summary>
    Level,

    /// <summary>Fires once on the condition crossing from not-holding to holding, and re-arms only when it crosses
    /// back — the right shape for anything that writes a document row, since a level-triggered write fires once per
    /// tick the condition holds rather than once per crossing.</summary>
    Edge,
}

/// <summary>One trigger channel of a lane binding: a gate, a press latch (the buffer — a press stays pending until the
/// gate opens or the latch expires; the release channel latches nothing), and the effects a fire applies in order.</summary>
/// <param name="Gate">The predicate that must hold to fire, or <see langword="null"/> for always.</param>
/// <param name="LatchSeconds">How long a press stays pending waiting for the gate. <c>0</c> means this tick only —
/// the press fires if the gate is open on its own edge tick and is dropped otherwise. Legitimate only on
/// <see cref="ActionSpec.OnPress"/>: the release channel latches nothing, so a non-zero value on
/// <see cref="ActionSpec.OnRelease"/> is refused by name at validation rather than parsed and discarded.</param>
/// <param name="Effects">The effects applied on fire, in order.</param>
public sealed record ActionTrigger(ActionPredicate? Gate, float LatchSeconds, IReadOnlyList<ActionEffect> Effects);

/// <summary>A lane's full binding: the press trigger and the release trigger. What a channel does is this data — the
/// engine implements only the facts, predicates, and effects.</summary>
/// <param name="OnPress">The rising-edge trigger, or <see langword="null"/>.</param>
/// <param name="OnRelease">The falling-edge trigger (evaluated immediately, never latched), or <see langword="null"/>.</param>
/// <param name="State">Named persistent state declarations used by this action and shared by matching names across the kit.</param>
/// <param name="OnFact">Engine-fact-triggered effect lists evaluated independently of channel edges.</param>
public sealed record ActionSpec(ActionTrigger? OnPress, ActionTrigger? OnRelease, IReadOnlyList<ActionStateSlot>? State = null, IReadOnlyList<ActionFactTrigger>? OnFact = null);

/// <summary>An authored effect list fired by one engine fact pulse — gated and edged by the same
/// <see cref="ActionTriggerMode"/> vocabulary a world rule uses.</summary>
/// <param name="Fact">The fact that fires the rule.</param>
/// <param name="Effects">The effects applied in order.</param>
/// <param name="Gate">An additional predicate that must hold beside <paramref name="Fact"/>, or
/// <see langword="null"/> for none.</param>
/// <param name="Mode">Whether the trigger fires every tick the condition holds (<see cref="ActionTriggerMode.Level"/>,
/// the default) or once per crossing (<see cref="ActionTriggerMode.Edge"/>). The
/// condition is <paramref name="Fact"/> and <paramref name="Gate"/> together — an edge trigger re-arms only when
/// that conjunction stops holding.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActionFactTrigger(
    ActionFact Fact,
    IReadOnlyList<ActionEffect> Effects,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionPredicate? Gate = null,
    ActionTriggerMode Mode = ActionTriggerMode.Level
);

/// <summary>One kit's named arguments for an authored producer program.</summary>
/// <param name="Scalars">Fixed-point scalar arguments keyed by instruction-defined name.</param>
/// <param name="Channels">Authored channel arguments keyed by instruction-defined name.</param>
public sealed record BodyProgramParameters(
    IReadOnlyDictionary<string, float> Scalars,
    IReadOnlyDictionary<string, string> Channels
);

/// <summary>The population subset a sensed target source considers.</summary>
[JsonConverter(typeof(StrictEnumConverter<BodyTargetScope>))]
public enum BodyTargetScope : byte {
    /// <summary>Active local-seat bodies.</summary>
    Seats,

    /// <summary>Every active body other than the sensing body.</summary>
    Bodies,
}

/// <summary>The one target source a producer program declares.</summary>
[JsonDerivedType(typeof(BodyTargetSource.Sensed), typeDiscriminator: "sensed")]
[JsonDerivedType(typeof(BodyTargetSource.Designated), typeDiscriminator: "designated")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record BodyTargetSource {
    private BodyTargetSource() {
    }

    /// <summary>Selects the nearest member of <paramref name="Scope"/> inside a body-forward cone.</summary>
    /// <param name="Scope">The population subset considered.</param>
    /// <param name="Range">The cone's maximum world-space distance.</param>
    /// <param name="HalfAngleDegrees">The cone half-angle in degrees.</param>
    /// <param name="RequiresLineOfSight">Whether solid world geometry must leave the segment unobstructed.</param>
    public sealed record Sensed(BodyTargetScope Scope, float Range, float HalfAngleDegrees, bool RequiresLineOfSight) : BodyTargetSource;

    /// <summary>Reads the named target register owned by the body running the producer.</summary>
    /// <param name="Register">The authored <see cref="WorldTargetRegister.Name"/>.</param>
    public sealed record Designated(string Register) : BodyTargetSource;
}

/// <summary>One authored per-body target register and the envelope a designation into it must satisfy.</summary>
/// <param name="Name">The game-authored register name.</param>
/// <param name="MaximumRange">The greatest designation distance.</param>
/// <param name="MaximumHalfAngleDegrees">The widest accepted body-forward cone.</param>
/// <param name="RequiresLineOfSight">Whether solid world geometry must leave the segment unobstructed.</param>
/// <param name="RangeState">An optional durable counter slot supplying the player's requested range.</param>
/// <param name="HalfAngleState">An optional durable counter slot supplying the player's requested cone half-angle.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldTargetRegister(
    string Name,
    float MaximumRange,
    float MaximumHalfAngleDegrees,
    bool RequiresLineOfSight,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RangeState = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HalfAngleState = null
);

/// <summary>The compiled target-register name and Drive-reach ordinal tables.</summary>
public sealed class WorldTargetRegisterTable {
    private readonly Dictionary<string, int> m_indexByName;
    private readonly string[] m_names;

    private WorldTargetRegisterTable(Dictionary<string, int> indexByName, string[] names, int reachBase) {
        m_indexByName = indexByName;
        m_names = names;
        ReachBase = reachBase;
    }

    /// <summary>Gets an empty target-register table.</summary>
    public static WorldTargetRegisterTable Empty { get; } = new(indexByName: new Dictionary<string, int>(comparer: StringComparer.Ordinal), names: [], reachBase: 0);

    /// <summary>Gets the number of authored registers.</summary>
    public int Count => m_names.Length;

    /// <summary>Gets the first target-register bit in a Drive row's shared reach mask.</summary>
    public int ReachBase { get; }

    /// <summary>Resolves a register name to its compact storage index.</summary>
    public bool TryGetIndex(string name, out int index) => m_indexByName.TryGetValue(key: name, value: out index);

    /// <summary>Gets a register's authored name.</summary>
    public string Name(int index) => m_names[index];

    /// <summary>Gets the Drive-reach ordinal for a compact register index.</summary>
    public int ReachOrdinal(int index) => (ReachBase + index);

    /// <summary>Compiles target registers after the world's channel ordinal range.</summary>
    public static WorldTargetRegisterTable Compile(IReadOnlyList<WorldTargetRegister> registers, int channelCount) {
        var names = new string[registers.Count];
        var indexByName = new Dictionary<string, int>(capacity: registers.Count, comparer: StringComparer.Ordinal);

        for (var index = 0; (index < registers.Count); index++) {
            names[index] = registers[index].Name;
            indexByName.Add(key: registers[index].Name, value: index);
        }

        return new WorldTargetRegisterTable(indexByName: indexByName, names: names, reachBase: channelCount);
    }
}

/// <summary>The fixed-point target source a producer executes.</summary>
/// <param name="Source">The authored source declaration.</param>
/// <param name="Range">The sensed cone range, or zero for a designated source.</param>
/// <param name="MinimumDot">The cosine of the sensed cone half-angle, or zero for a designated source.</param>
/// <param name="RegisterIndex">The designated register index, or <c>-1</c> for a sensed source.</param>
public readonly record struct FixedBodyTargetSource(BodyTargetSource Source, FixedQ4816 Range, FixedQ4816 MinimumDot, int RegisterIndex) {
    /// <summary>Compiles one validated target declaration.</summary>
    public static FixedBodyTargetSource Compile(BodyTargetSource source, WorldTargetRegisterTable registers) => source switch {
        BodyTargetSource.Sensed sensed => new FixedBodyTargetSource(
            Source: source,
            Range: FixedQ4816.FromDouble(value: sensed.Range),
            MinimumDot: FixedQ4816.FromDouble(value: Math.Cos(sensed.HalfAngleDegrees * (Math.PI / 180.0))),
            RegisterIndex: -1
        ),
        BodyTargetSource.Designated designated => new FixedBodyTargetSource(
            Source: source,
            Range: FixedQ4816.Zero,
            MinimumDot: FixedQ4816.Zero,
            RegisterIndex: (registers.TryGetIndex(name: designated.Register, index: out var index) ? index : -1)
        ),
        _ => throw new InvalidOperationException(message: $"Unknown body target source '{source.GetType().Name}'."),
    };
}

/// <summary>The shared fixed-point body-forward cone predicate used by client proposals and authoritative senses.</summary>
public static class BodyTargetConeSense {
    /// <summary>Reports whether a candidate lies inside the supplied cone.</summary>
    public static bool Contains(in FixedVector3 origin, in FixedVector3 forward, in FixedVector3 candidate, FixedQ4816 range, FixedQ4816 minimumDot, out FixedQ4816 distanceSquared) {
        var delta = (candidate - origin);
        distanceSquared = delta.LengthSquared;

        if ((distanceSquared <= FixedQ4816.Zero) || (distanceSquared > (range * range))) {
            return false;
        }

        return (FixedVector3.Dot(left: forward.Normalize(), right: delta.Normalize()) >= minimumDot);
    }
}

/// <summary>Selects whether authored target decisions require the deterministic solid-field query provider.</summary>
public static class WorldTargetSelection {
    /// <summary>Returns whether any designation envelope, sensed source, or world-rule <c>$los:</c> operand requires
    /// line of sight — the one gate <c>Server.WorldPopulation.CompileFixedTables</c> reads to decide whether to build
    /// the solid field at all. A world rule's <c>$los:</c> channel rides the same
    /// <c>Server.WorldPopulation.HasLineOfSight</c> primitive a sensed target's own check does, and that primitive
    /// reads a field the population would otherwise never build if nothing else in the document asked for one —
    /// admitting it here is what keeps a rules-only <c>$los:</c> authoring from silently reading "always false"
    /// forever.</summary>
    public static bool RequiresLineOfSight(WorldDefinition definition) =>
        definition.TargetRegisters.Any(register => register.RequiresLineOfSight)
        || definition.BodyMotionPrograms.Any(program => program.Target is BodyTargetSource.Sensed { RequiresLineOfSight: true })
        || RulesReferenceLineOfSight(rules: definition.Rules);

    // Scanned over the AUTHORED rule rows (mirroring the two checks above), never the compiled form — this decides
    // whether to build the field the compiler's own ReadWorldFact will later read from, so it must run before (and
    // independently of) rule compilation.
    private static bool RulesReferenceLineOfSight(IReadOnlyList<WorldRule>? rules) {
        if (rules is null) {
            return false;
        }

        foreach (var rule in rules) {
            if ((rule is not null) && (PredicateReferencesLineOfSight(predicate: rule.Gate) || rule.Effects.Any(EffectReferencesLineOfSight))) {
                return true;
            }
        }

        return false;
    }

    private static bool PredicateReferencesLineOfSight(ActionPredicate? predicate) => predicate switch {
        ActionPredicate.CompareState compare => (NamesLineOfSight(name: compare.State) || NamesLineOfSight(name: compare.ComparandState)),
        ActionPredicate.All all => all.Predicates.Any(PredicateReferencesLineOfSight),
        _ => false,
    };

    private static bool EffectReferencesLineOfSight(ActionEffect effect) => effect switch {
        ActionEffect.SetState set => NamesLineOfSight(name: set.FromState),
        ActionEffect.AddState add => NamesLineOfSight(name: add.FromState),
        _ => false,
    };

    private static bool NamesLineOfSight(string? name) => ((name is not null) && name.StartsWith(value: WorldRuleFacts.LineOfSightPrefix, comparisonType: StringComparison.Ordinal));
}

/// <summary>One locomotion kit — a world-definition row naming a way of moving: the body motion program it runs under,
/// the motion model its
/// bodies compile, its producer arguments, and its action-lane bindings. Every game-flavored movement noun is a
/// row of this data, never an engine enum; the census echo prints these names.</summary>
/// <param name="Name">The kit's kebab-case name (the census echo token).</param>
/// <param name="BodyMotionProgram">The name of the body motion program the kit's bodies execute.</param>
/// <param name="Motion">The locomotion model the kit's bodies compile (a seat's profile speeds still override its
/// speed fields) — see <see cref="WorldMotionModel"/>.</param>
/// <param name="Producers">Producer parameter maps keyed by authored producer-program name.</param>
/// <param name="Actions">The kit's composition bindings, keyed by declared channel name (validated against the
/// world's channel table — a kit naming an undeclared channel is a dead name; a declared composition channel with no
/// entry here stays legal and inert per body). Compositions key off channel name, never a lane ordinal.</param>
/// <param name="Collider">The kit's body volume solved against the world contact field, or
/// <see langword="null"/> for a kit with no volume (never solved against the field). Omitted from the wire when null.</param>
/// <param name="BodyContact">Whether bodies wearing this kit overlap one another or participate in physical
/// depenetration. World geometry still uses <paramref name="Collider"/> in either mode.</param>
public sealed record WorldKit(
    string Name,
    string BodyMotionProgram,
    WorldMotionModel Motion,
    IReadOnlyDictionary<string, BodyProgramParameters> Producers,
    IReadOnlyDictionary<string, ActionSpec> Actions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCollider? Collider = null,
    WorldBodyContactMode BodyContact = WorldBodyContactMode.Overlap
);

/// <summary>Declares how a kit responds to other dynamic bodies. Interactions and targeting remain available in
/// both modes; only <see cref="Solid"/> authorizes physical depenetration.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldBodyContactMode>))]
public enum WorldBodyContactMode : byte {
    /// <summary>Bodies may overlap. This is the default; the engine never introduces crowd shoving implicitly.</summary>
    Overlap,

    /// <summary>Two bodies physically depenetrate only when both of their kits select this mode.</summary>
    Solid,
}

/// <summary>Identifies a body motion program instruction from the closed domain-operation vocabulary.</summary>
[JsonConverter(typeof(StrictEnumConverter<BodyMotionOp>))]
public enum BodyMotionOp : byte {
    SenseNearestInCone,
    ProduceWanderIntent,
    ProduceAttendIntent,
    FaceSensorTarget,
    ResolveYawAttitudeAndPlanarFrame,
    IntegrateLocalAttitude,
    ComputePlanarTargetVelocity,
    ComputeLocalTargetVelocity,
    ComputeSwimTargetVelocity,
    ShapePlanarVelocity,
    SnapYawToPlanarIntent,
    ResolveVehicleFrame,
    ShapeVehicleVelocity,
    RunActionTriggers,
    ApplyVerticalGravity,
    ApplyVerticalDecay,
    ApplyBuoyancyAndSurface,
    IntegratePlanarAndVerticalVelocity,
    IntegrateScratchVelocity,
    CommitPose,
    SetVerticalVelocity,
    ScaleVerticalVelocity,
    PlanarImpulse,
    SetState,
    AddState,
    StartTimer,
    Designate,
    Generate,
}

/// <summary>The storage kind of a named persistent action-state slot.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionStateKind>))]
public enum ActionStateKind : byte {
    Counter,
    Timer,
}

/// <summary>Declares where a named action-state slot survives.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionStateLifetime>))]
public enum ActionStateLifetime : byte {
    /// <summary>The slot belongs to one body and resets from its authored facts.</summary>
    Ephemeral,

    /// <summary>The slot belongs to a player identity and crosses sessions through the durable input/output seam.</summary>
    Durable,
}

/// <summary>The authored values a player-writable durable slot admits in this world.</summary>
[JsonDerivedType(typeof(ActionStateEnvelope.Range), typeDiscriminator: "range")]
[JsonDerivedType(typeof(ActionStateEnvelope.Set), typeDiscriminator: "set")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ActionStateEnvelope {
    private ActionStateEnvelope() {
    }

    /// <summary>An inclusive numeric interval.</summary>
    /// <param name="Minimum">The least admitted value.</param>
    /// <param name="Maximum">The greatest admitted value.</param>
    public sealed record Range(float Minimum, float Maximum) : ActionStateEnvelope;

    /// <summary>A closed numeric set. Values are authored labels encoded in the slot's deterministic numeric domain.</summary>
    /// <param name="Values">The admitted values.</param>
    public sealed record Set(IReadOnlyList<float> Values) : ActionStateEnvelope;
}

/// <summary>A fixed comparison admitted by <see cref="ActionPredicate.CompareState"/>.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionStateComparison>))]
public enum ActionStateComparison : byte {
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary>The one evaluation of an <see cref="ActionStateComparison"/> — a kit action's own state predicate and a
/// world rule's <c>compareState</c> operand ask exactly the same question of the same fixed-point pair, so the
/// vocabulary is decided in one place and neither can grow an arm the other lacks.</summary>
public static class ActionStateComparisons {
    /// <summary>Evaluates the comparison against a value/expectation pair.</summary>
    /// <param name="comparison">The comparison to evaluate.</param>
    /// <param name="value">The observed value.</param>
    /// <param name="expected">The value compared against.</param>
    /// <returns><see langword="true"/> when the comparison holds.</returns>
    public static bool Holds(this ActionStateComparison comparison, FixedQ4816 value, FixedQ4816 expected) => comparison switch {
        ActionStateComparison.Equal => (value == expected),
        ActionStateComparison.NotEqual => (value != expected),
        ActionStateComparison.Less => (value < expected),
        ActionStateComparison.LessOrEqual => (value <= expected),
        ActionStateComparison.Greater => (value > expected),
        _ => (value >= expected),
    };

    /// <summary>Evaluates the comparison when either side may be positive infinity — a fact whose magnitude exceeds
    /// every representable number (today only the <c>$parked:</c> channel's forever case). Infinity compares as
    /// strictly greater than every finite value and equal to itself, so <c>&gt; finite</c> holds, <c>&lt;= finite</c>
    /// does not, and <c>== finite</c> never does. A sentinel numeric encoding was deliberately rejected: any finite
    /// stand-in is a value an authored comparand could legitimately equal, and a comparison that cannot distinguish
    /// "forever" from one particular number is lying about one of them.</summary>
    /// <param name="comparison">The comparison to evaluate.</param>
    /// <param name="value">The observed value; ignored when <paramref name="valueIsForever"/>.</param>
    /// <param name="valueIsForever">Whether the observed side is positive infinity.</param>
    /// <param name="expected">The value compared against; ignored when <paramref name="expectedIsForever"/>.</param>
    /// <param name="expectedIsForever">Whether the expected side is positive infinity.</param>
    /// <returns><see langword="true"/> when the comparison holds.</returns>
    public static bool Holds(this ActionStateComparison comparison, FixedQ4816 value, bool valueIsForever, FixedQ4816 expected, bool expectedIsForever) {
        if (!valueIsForever && !expectedIsForever) {
            return comparison.Holds(value: value, expected: expected);
        }

        // Exactly one or both sides are infinite; the finite magnitudes no longer matter, only the ordering sign.
        var sign = ((valueIsForever, expectedIsForever)) switch {
            (true, true) => 0,
            (true, false) => 1,
            _ => -1,
        };

        return comparison switch {
            ActionStateComparison.Equal => (sign == 0),
            ActionStateComparison.NotEqual => (sign != 0),
            ActionStateComparison.Less => (sign < 0),
            ActionStateComparison.LessOrEqual => (sign <= 0),
            ActionStateComparison.Greater => (sign > 0),
            _ => (sign >= 0),
        };
    }
}

/// <summary>Declares one named persistent state slot shared by the kit's actions.</summary>
/// <param name="Name">The stable slot name predicates and effects reference.</param>
/// <param name="Kind">Whether the slot stores a counter or a remaining timer.</param>
/// <param name="Initial">The initial counter value or timer duration in seconds.</param>
/// <param name="ResetFact">An optional body fact that resets the slot to <paramref name="Initial"/> while it holds.</param>
/// <param name="Lifetime">Where the slot survives.</param>
/// <param name="PlayerWritable">Whether the identity driving the body may submit a value for the slot.</param>
/// <param name="Envelope">The visited world's admitted effective values. Required for a player-writable slot.</param>
public sealed record ActionStateSlot(
    string Name,
    ActionStateKind Kind,
    float Initial = 0f,
    ActionFact? ResetFact = null,
    ActionStateLifetime Lifetime = ActionStateLifetime.Ephemeral,
    bool PlayerWritable = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionStateEnvelope? Envelope = null
);

/// <summary>Declares the register and operation profile a compiled program uses.</summary>
[JsonConverter(typeof(StrictEnumConverter<BodyProgramKind>))]
public enum BodyProgramKind : byte {
    /// <summary>A body-motion program reading intent and writing body pose, velocity, and action state.</summary>
    Motion,

    /// <summary>A producer program reading sensors and writing channel values.</summary>
    Producer,
}

/// <summary>The register families a program kind admits.</summary>
[Flags]
public enum BodyProgramAdmission : byte {
    None = 0,
    Channels = 1,
    Pose = 2,
    Velocity = 4,
    ActionState = 8,
    Sensors = 16,
}

/// <summary>An authored fixed-phase body motion program.</summary>
/// <param name="Name">The stable name kits use to select the program.</param>
/// <param name="Version">The instruction-set version.</param>
/// <param name="Kind">The declared program profile that gates operations and registers.</param>
/// <param name="Operations">The selected domain operations; their phases are intrinsic and cannot be reordered.</param>
/// <param name="Target">The single source supplying the program's target, when it uses target-aware operations.</param>
public sealed record BodyMotionProgram(
    string Name,
    string Version,
    BodyProgramKind? Kind,
    IReadOnlyList<BodyMotionOp> Operations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BodyTargetSource? Target = null
) {
    /// <summary>The supported body-motion instruction-set version.</summary>
    public const string CurrentVersion = "puck.body-motion.v1";
}

/// <summary>Names why a body motion program was refused during construction.</summary>
public enum BodyMotionProgramRefusal : byte {
    VersionUnsupported,
    NameMissing,
    InstructionCountOutOfRange,
    OpcodeUnknown,
    OpcodeDuplicate,
    ProgramKindUnknown,
    OpcodeInadmissible,
    ParameterMissing,
    ParameterUnknown,
}

/// <summary>Reports a construction-time body motion program refusal.</summary>
public sealed class BodyMotionProgramException : ArgumentException {
    /// <summary>Initializes a body motion program refusal.</summary>
    public BodyMotionProgramException(BodyMotionProgramRefusal refusal, string programName, string detail)
        : base(message: $"Body motion program '{programName}' refused {refusal}: {detail}") {
        Refusal = refusal;
    }

    /// <summary>Gets the refusal category.</summary>
    public BodyMotionProgramRefusal Refusal { get; }
}

/// <summary>The construction-time typed form of a body motion program.</summary>
public sealed class CompiledBodyMotionProgram {
    private const int MaxOperations = 32;
    private readonly HashSet<BodyMotionOp> m_operations;

    private CompiledBodyMotionProgram(string name, BodyProgramKind kind, BodyProgramAdmission admissionMask, BodyMotionOp[][] phases, HashSet<BodyMotionOp> operations, BodyTargetSource? target) {
        Name = name;
        Kind = kind;
        AdmissionMask = admissionMask;
        Phases = phases;
        m_operations = operations;
        Target = target;
    }

    /// <summary>Gets the program name.</summary>
    public string Name { get; }
    /// <summary>Gets the declared program profile.</summary>
    public BodyProgramKind Kind { get; }
    /// <summary>Gets the register admission mask for <see cref="Kind"/>.</summary>
    public BodyProgramAdmission AdmissionMask { get; }
    /// <summary>Gets the operations grouped by their intrinsic host phase.</summary>
    public BodyMotionOp[][] Phases { get; }
    /// <summary>Gets the program's declared target source.</summary>
    public BodyTargetSource? Target { get; }
    /// <summary>Reports whether this program selects an operation.</summary>
    public bool Contains(BodyMotionOp operation) => m_operations.Contains(item: operation);
    /// <summary>Reports whether the selected instructions read <paramref name="role"/>.</summary>
    public bool RequiresRole(ChannelRole role) => role switch {
        ChannelRole.MoveForward or ChannelRole.MoveStrafe => Contains(operation: BodyMotionOp.ComputePlanarTargetVelocity)
            || Contains(operation: BodyMotionOp.SnapYawToPlanarIntent)
            || Contains(operation: BodyMotionOp.ComputeLocalTargetVelocity)
            || Contains(operation: BodyMotionOp.ComputeSwimTargetVelocity)
            || (Contains(operation: BodyMotionOp.ShapeVehicleVelocity) && (role == ChannelRole.MoveForward)),
        ChannelRole.Turn => Contains(operation: BodyMotionOp.ResolveYawAttitudeAndPlanarFrame)
            || Contains(operation: BodyMotionOp.IntegrateLocalAttitude)
            || Contains(operation: BodyMotionOp.ResolveVehicleFrame),
        ChannelRole.MoveUp => Contains(operation: BodyMotionOp.ComputeLocalTargetVelocity)
            || Contains(operation: BodyMotionOp.ComputeSwimTargetVelocity),
        // ResolveVehicleFrame reads Pitch only under a positive PitchRate, so Pitch is not REQUIRED for it — a
        // pitchless world's flying-vehicle pitch reads zero rather than refusing the kit.
        ChannelRole.Pitch or ChannelRole.Roll => Contains(operation: BodyMotionOp.IntegrateLocalAttitude),
        _ => false,
    };
    /// <summary>Reports whether this program profile admits an instruction's required registers.</summary>
    public bool Admits(BodyMotionOp operation) => ((RequiredAdmission(operation: operation) & ~AdmissionMask) == BodyProgramAdmission.None);

    /// <summary>Gets a value indicating whether this program's selected operations integrate gravity into vertical velocity
    /// (<see cref="BodyMotionOp.ApplyVerticalGravity"/> — the same op <c>WorldDefinitionValidator</c>'s
    /// <c>GravityArc</c> tuning facet maps from). This is the vertical-contact-authority signal
    /// <c>WorldBody.ResolveProgramContacts</c> gates its vertical write-back on: a program that owns this
    /// integrates its own vertical channel (e.g. <see cref="BodyMotionOp.ApplyVerticalDecay"/>'s bleed) and must
    /// not have contact resolution overwrite it — feeding a decay channel's own prior value back into itself
    /// every tick is an unbounded loop, not a correction.</summary>
    public bool OwnsVerticalContactState => Contains(operation: BodyMotionOp.ApplyVerticalGravity);

    /// <summary>Compiles and validates an authored program in one construction-time walk.</summary>
    public static CompiledBodyMotionProgram Compile(BodyMotionProgram program) {
        ArgumentNullException.ThrowIfNull(argument: program);

        if (string.IsNullOrWhiteSpace(value: program.Name)) {
            throw Refuse(BodyMotionProgramRefusal.NameMissing, program.Name, "name is required");
        }
        if (!string.Equals(a: program.Version, b: BodyMotionProgram.CurrentVersion, comparisonType: StringComparison.Ordinal)) {
            throw Refuse(BodyMotionProgramRefusal.VersionUnsupported, program.Name, $"version '{program.Version}' is not '{BodyMotionProgram.CurrentVersion}'");
        }
        if ((program.Operations is null) || (program.Operations.Count == 0) || (program.Operations.Count > MaxOperations)) {
            throw Refuse(BodyMotionProgramRefusal.InstructionCountOutOfRange, program.Name, $"operation count must be in [1, {MaxOperations}]");
        }
        if ((program.Kind is not { } kind) || !Enum.IsDefined(value: kind)) {
            throw Refuse(BodyMotionProgramRefusal.ProgramKindUnknown, program.Name, $"program kind '{program.Kind?.ToString() ?? "<missing>"}' is not declared");
        }

        var admissionMask = AdmissionFor(kind: kind);

        var seen = new HashSet<BodyMotionOp>();
        foreach (var op in program.Operations) {
            if (!Enum.IsDefined(value: op)) {
                throw Refuse(BodyMotionProgramRefusal.OpcodeUnknown, program.Name, $"opcode value {(int)op} is not declared");
            }
            if (!ProgramSelectable(operation: op) || ((RequiredAdmission(operation: op) & ~admissionMask) != BodyProgramAdmission.None)) {
                throw Refuse(BodyMotionProgramRefusal.OpcodeInadmissible, program.Name, $"opcode '{op}' is inadmissible for program kind '{kind}'");
            }
            if (!seen.Add(item: op)) {
                throw Refuse(BodyMotionProgramRefusal.OpcodeDuplicate, program.Name, $"opcode '{op}' occurs more than once");
            }

            _ = Phase(op: op);
        }
        var phaseLists = new List<BodyMotionOp>[8];
        for (var phase = 0; phase < phaseLists.Length; phase++) {
            phaseLists[phase] = [];
        }
        foreach (var op in Enum.GetValues<BodyMotionOp>()) {
            if (seen.Contains(item: op)) {
                phaseLists[Phase(op: op)].Add(item: op);
            }
        }

        var phases = new BodyMotionOp[phaseLists.Length][];
        for (var phase = 0; phase < phases.Length; phase++) {
            phases[phase] = phaseLists[phase].ToArray();
        }

        return new CompiledBodyMotionProgram(name: program.Name, kind: kind, admissionMask: admissionMask, phases: phases, operations: seen, target: program.Target);
    }

    private static BodyProgramAdmission AdmissionFor(BodyProgramKind kind) => kind switch {
        BodyProgramKind.Motion => (BodyProgramAdmission.Channels | BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity | BodyProgramAdmission.ActionState),
        BodyProgramKind.Producer => (BodyProgramAdmission.Sensors | BodyProgramAdmission.Channels | BodyProgramAdmission.ActionState),
        _ => BodyProgramAdmission.None,
    };

    private static bool ProgramSelectable(BodyMotionOp operation) => operation < BodyMotionOp.SetVerticalVelocity;

    private static BodyProgramAdmission RequiredAdmission(BodyMotionOp operation) => operation switch {
        BodyMotionOp.SenseNearestInCone => BodyProgramAdmission.Sensors,
        BodyMotionOp.ProduceWanderIntent or BodyMotionOp.ProduceAttendIntent or BodyMotionOp.FaceSensorTarget => (BodyProgramAdmission.Sensors | BodyProgramAdmission.Channels | BodyProgramAdmission.ActionState),
        BodyMotionOp.ResolveYawAttitudeAndPlanarFrame or BodyMotionOp.IntegrateLocalAttitude or BodyMotionOp.ComputePlanarTargetVelocity
            or BodyMotionOp.ComputeLocalTargetVelocity or BodyMotionOp.ComputeSwimTargetVelocity or BodyMotionOp.ShapePlanarVelocity
            or BodyMotionOp.SnapYawToPlanarIntent or BodyMotionOp.ResolveVehicleFrame or BodyMotionOp.ShapeVehicleVelocity
            => (BodyProgramAdmission.Channels | BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity),
        BodyMotionOp.RunActionTriggers => (BodyProgramAdmission.Channels | BodyProgramAdmission.Velocity | BodyProgramAdmission.ActionState),
        BodyMotionOp.ApplyVerticalGravity or BodyMotionOp.ApplyVerticalDecay or BodyMotionOp.ApplyBuoyancyAndSurface
            or BodyMotionOp.IntegratePlanarAndVerticalVelocity
            or BodyMotionOp.IntegrateScratchVelocity => (BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity),
        BodyMotionOp.CommitPose => BodyProgramAdmission.Pose,
        BodyMotionOp.SetVerticalVelocity or BodyMotionOp.ScaleVerticalVelocity or BodyMotionOp.PlanarImpulse => BodyProgramAdmission.Velocity,
        BodyMotionOp.SetState or BodyMotionOp.AddState or BodyMotionOp.StartTimer or BodyMotionOp.Designate or BodyMotionOp.Generate => BodyProgramAdmission.ActionState,
        _ => (BodyProgramAdmission.Channels | BodyProgramAdmission.Pose | BodyProgramAdmission.Velocity | BodyProgramAdmission.ActionState | BodyProgramAdmission.Sensors),
    };

    private static int Phase(BodyMotionOp op) => op switch {
        BodyMotionOp.SenseNearestInCone => 0,
        BodyMotionOp.ProduceWanderIntent => 1,
        BodyMotionOp.ProduceAttendIntent => 2,
        BodyMotionOp.FaceSensorTarget => 2,
        BodyMotionOp.ResolveYawAttitudeAndPlanarFrame or BodyMotionOp.IntegrateLocalAttitude or BodyMotionOp.ResolveVehicleFrame => 0,
        BodyMotionOp.ComputePlanarTargetVelocity or BodyMotionOp.ComputeLocalTargetVelocity or BodyMotionOp.ComputeSwimTargetVelocity => 1,
        BodyMotionOp.ShapePlanarVelocity or BodyMotionOp.SnapYawToPlanarIntent or BodyMotionOp.ShapeVehicleVelocity => 2,
        BodyMotionOp.RunActionTriggers => 3,
        BodyMotionOp.ApplyVerticalGravity or BodyMotionOp.ApplyVerticalDecay or BodyMotionOp.ApplyBuoyancyAndSurface => 4,
        BodyMotionOp.IntegratePlanarAndVerticalVelocity or BodyMotionOp.IntegrateScratchVelocity => 5,
        BodyMotionOp.CommitPose => 7,
        _ => throw Refuse(BodyMotionProgramRefusal.OpcodeUnknown, "<unnamed>", $"opcode value {(int)op} is not declared"),
    };
    private static BodyMotionProgramException Refuse(BodyMotionProgramRefusal refusal, string? name, string detail) => new(refusal: refusal, programName: (name ?? "<null>"), detail: detail);
}

/// <summary>The flattened, fixed-point form of one predicate.</summary>
public readonly record struct CompiledPredicate(ActionFact Fact, int RecencySlot, int StateSlot, FixedQ4816 Value, ActionStateComparison Comparison, CompiledPredicateKind Kind);

/// <summary>The compiled predicate dispatch tag.</summary>
public enum CompiledPredicateKind : byte {
    Now,
    Recently,
    CompareState,
    TimerElapsed,
}

/// <summary>One compiled instruction shared by program phases and action triggers.</summary>
/// <remarks><c>StateName</c> carries <see cref="BodyMotionOp.Generate"/>'s draw site — the one row a generate names,
/// since a site's source and cursor are its own — and is <see langword="null"/> for every other operation. Nothing is
/// bound at kit-compile time here: the site is a world-global <c>state</c> row, not this kit's per-body slot table, so
/// resolution happens where the mutation is composed.</remarks>
public readonly record struct CompiledBodyInstruction(BodyMotionOp Operation, FixedQ4816 Value, FixedVector3 Direction, ulong DurationTicks, int StateSlot, ActionTarget Target = ActionTarget.Self, string? StateName = null);

/// <summary>One compiled named action-state slot.</summary>
public readonly record struct CompiledActionStateSlot(
    string Name,
    ActionStateKind Kind,
    FixedQ4816 InitialValue,
    ulong InitialTicks,
    ActionFact? ResetFact,
    ActionStateLifetime Lifetime,
    bool PlayerWritable,
    CompiledActionStateEnvelope? Envelope
);

/// <summary>A slot envelope compiled into the slot's fixed counter or engine-tick domain.</summary>
/// <param name="Minimum">The inclusive range minimum, or zero for a set.</param>
/// <param name="Maximum">The inclusive range maximum, or zero for a set.</param>
/// <param name="Values">The closed set, or <see langword="null"/> for a range.</param>
public sealed record CompiledActionStateEnvelope(long Minimum, long Maximum, long[]? Values) {
    /// <summary>Returns whether a raw slot-domain value is admitted.</summary>
    public bool Contains(long value) => Values is { } values
        ? Array.IndexOf(array: values, value: value) >= 0
        : (value >= Minimum) && (value <= Maximum);

    /// <summary>Clamps a raw value to the range, or substitutes the authored initial value for a closed-set miss.</summary>
    public long Clamp(long value, long initial) => Values is null
        ? Math.Clamp(value: value, min: Minimum, max: Maximum)
        : (Contains(value: value) ? value : initial);
}

/// <summary>One compiled trigger channel: the flattened conjunction gate, the press latch in engine ticks, and the
/// fixed-point effects in authored order.</summary>
public sealed record CompiledTrigger(CompiledPredicate[] Gate, ulong LatchTicks, CompiledBodyInstruction[] Effects);

/// <summary>A lane binding compiled once before simulation: both trigger channels plus the recency-clock table (one
/// slot per <see cref="ActionPredicate.Recently"/> instance across both gates — the per-tick clock updater walks it).</summary>
public sealed record CompiledActionSpec(CompiledTrigger? OnPress, CompiledTrigger? OnRelease, CompiledFactTrigger[] OnFact, ActionFact[] RecencyFacts, ulong[] RecencyWindows) {
    /// <summary>Compiles an authored binding: predicates flatten (nested <see cref="ActionPredicate.All"/>
    /// conjunctions concatenate), seconds become engine ticks, floats become fixed point — once, at the boundary.</summary>
    /// <param name="spec">The authored binding, or <see langword="null"/> for an unbound lane.</param>
    /// <param name="stateSlots">The kit-wide named action-state lookup.</param>
    /// <param name="program">The compiled program profile admitting trigger instructions.</param>
    /// <param name="actionName">The refusing action's qualified name.</param>
    public static CompiledActionSpec? Compile(ActionSpec? spec, IReadOnlyDictionary<string, int> stateSlots, CompiledBodyMotionProgram program, string actionName) {
        if (spec is null) {
            return null;
        }

        var recencyFacts = new List<ActionFact>();
        var recencyWindows = new List<ulong>();
        var onPress = CompileTrigger(trigger: spec.OnPress, recencyFacts: recencyFacts, recencyWindows: recencyWindows, stateSlots: stateSlots, program: program, actionName: actionName);
        var onRelease = CompileTrigger(trigger: spec.OnRelease, recencyFacts: recencyFacts, recencyWindows: recencyWindows, stateSlots: stateSlots, program: program, actionName: actionName);
        // A fact trigger's own gate allocates recency slots from the SAME two lists both channel triggers use — one
        // recency clock table per lane binding, never a third parallel table for the fact channel.
        var onFact = (spec.OnFact ?? []).Select(rule => {
            var factGate = new List<CompiledPredicate>();

            FlattenPredicate(predicate: rule.Gate, gate: factGate, recencyFacts: recencyFacts, recencyWindows: recencyWindows, stateSlots: stateSlots);

            return new CompiledFactTrigger(
                Fact: rule.Fact,
                Gate: factGate.ToArray(),
                Mode: rule.Mode,
                Effects: rule.Effects.Select(effect => CompileEffect(effect: effect, stateSlots: stateSlots, program: program, actionName: actionName)).ToArray()
            );
        }).ToArray();

        return new CompiledActionSpec(
            OnPress: onPress,
            OnRelease: onRelease,
            OnFact: onFact,
            RecencyFacts: recencyFacts.ToArray(),
            RecencyWindows: recencyWindows.ToArray()
        );
    }

    private static CompiledTrigger? CompileTrigger(ActionTrigger? trigger, List<ActionFact> recencyFacts, List<ulong> recencyWindows, IReadOnlyDictionary<string, int> stateSlots, CompiledBodyMotionProgram program, string actionName) {
        if (trigger is null) {
            return null;
        }

        var gate = new List<CompiledPredicate>();

        FlattenPredicate(predicate: trigger.Gate, gate: gate, recencyFacts: recencyFacts, recencyWindows: recencyWindows, stateSlots: stateSlots);

        var effects = new CompiledBodyInstruction[trigger.Effects.Count];

        for (var index = 0; (index < effects.Length); index++) {
            effects[index] = CompileEffect(effect: trigger.Effects[index], stateSlots: stateSlots, program: program, actionName: actionName);
        }

        return new CompiledTrigger(
            Gate: gate.ToArray(),
            LatchTicks: DurationTicks(seconds: trigger.LatchSeconds),
            Effects: effects
        );
    }

    // Flattens a predicate ADT into a fixed-point conjunction gate, allocating one shared recency slot per Recently
    // instance. Promoted to internal so the motion-response compiler (a non-lane caller) reuses the same slotting.
    internal static void FlattenPredicate(ActionPredicate? predicate, List<CompiledPredicate> gate, List<ActionFact> recencyFacts, List<ulong> recencyWindows, IReadOnlyDictionary<string, int>? stateSlots = null) {
        switch (predicate) {
            case null:
                break;
            case ActionPredicate.All all:
                foreach (var inner in all.Predicates) {
                    FlattenPredicate(predicate: inner, gate: gate, recencyFacts: recencyFacts, recencyWindows: recencyWindows, stateSlots: stateSlots);
                }

                break;
            case ActionPredicate.Now now:
                gate.Add(item: new CompiledPredicate(Fact: now.Fact, RecencySlot: 0, StateSlot: -1, Value: default, Comparison: default, Kind: CompiledPredicateKind.Now));

                break;
            case ActionPredicate.Recently recently:
                gate.Add(item: new CompiledPredicate(Fact: recently.Fact, RecencySlot: recencyFacts.Count, StateSlot: -1, Value: default, Comparison: default, Kind: CompiledPredicateKind.Recently));
                recencyFacts.Add(item: recently.Fact);
                recencyWindows.Add(item: DurationTicks(seconds: recently.WindowSeconds));

                break;
            case ActionPredicate.CompareState compare:
                // A per-body action-state slot is not keyed — a `key` here would be parsed and discarded, which is
                // exactly the shape this campaign refuses. It is legitimate at WORLD scope alone (WorldRuleCompiler).
                if (compare.Key is not null) {
                    throw new InvalidOperationException(message: $"Predicate 'compareState' on action state '{compare.State}' carries a 'key' — a per-body action-state slot is not keyed; 'key' addresses a world state row's cell and is legitimate only in a world rule.");
                }
                // A comparand ROW reference addresses a world state row (or a reserved channel a world evaluates
                // per tick) — a per-body action-state slot has neither, so the second spelling is legitimate only in
                // a world rule (WorldRuleCompiler), never here.
                if ((compare.ComparandState is not null) || (compare.ComparandKey is not null)) {
                    throw new InvalidOperationException(message: $"Predicate 'compareState' on action state '{compare.State}' carries a 'comparandState'/'comparandKey' — a per-body action-state slot has no world state row to reference; a comparand row is legitimate only in a world rule.");
                }

                if (compare.Value is not { } constant) {
                    throw new InvalidOperationException(message: $"Predicate 'compareState' on action state '{compare.State}' carries no 'value' — a per-body predicate names the authored constant to compare against.");
                }

                gate.Add(item: new CompiledPredicate(Fact: default, RecencySlot: 0, StateSlot: ResolveState(name: compare.State, stateSlots: stateSlots), Value: FixedQ4816.FromDouble(value: constant), Comparison: compare.Comparison, Kind: CompiledPredicateKind.CompareState));
                break;
            case ActionPredicate.TimerElapsed elapsed:
                gate.Add(item: new CompiledPredicate(Fact: default, RecencySlot: 0, StateSlot: ResolveState(name: elapsed.State, stateSlots: stateSlots), Value: default, Comparison: default, Kind: CompiledPredicateKind.TimerElapsed));
                break;
        }
    }

    private static CompiledBodyInstruction CompileEffect(ActionEffect effect, IReadOnlyDictionary<string, int> stateSlots, CompiledBodyMotionProgram program, string actionName) {
        var instruction = effect switch {
            ActionEffect.SetVerticalVelocity set => new CompiledBodyInstruction(
                Operation: BodyMotionOp.SetVerticalVelocity,
                Value: FixedQ4816.FromDouble(value: set.Velocity),
                Direction: default,
                DurationTicks: 0UL,
                StateSlot: -1,
                Target: set.Target
            ),
            ActionEffect.ScaleVerticalVelocity scale => new CompiledBodyInstruction(
                Operation: BodyMotionOp.ScaleVerticalVelocity,
                Value: FixedQ4816.FromDouble(value: scale.Factor),
                Direction: default,
                DurationTicks: 0UL,
                StateSlot: -1,
                Target: scale.Target
            ),
            ActionEffect.PlanarImpulse impulse => new CompiledBodyInstruction(
                Operation: BodyMotionOp.PlanarImpulse,
                Value: FixedQ4816.FromDouble(value: impulse.Speed),
                Direction: new FixedVector3(
                    X: FixedQ4816.FromDouble(value: impulse.BodyDirection.X),
                    Y: FixedQ4816.FromDouble(value: impulse.BodyDirection.Y),
                    Z: FixedQ4816.FromDouble(value: impulse.BodyDirection.Z)
                ),
                DurationTicks: DurationTicks(seconds: impulse.DurationSeconds),
                StateSlot: -1,
                Target: impulse.Target
            ),
            ActionEffect.SetState set => new CompiledBodyInstruction(Operation: BodyMotionOp.SetState, Value: FixedQ4816.FromDouble(value: RequireBodyEffectValue(value: set.Value, fromState: set.FromState, fromKey: set.FromKey, valueSeconds: set.ValueSeconds, actionName: actionName, effectName: "setState", state: set.State)), Direction: default, DurationTicks: 0UL, StateSlot: ResolveState(name: set.State, stateSlots: stateSlots, key: set.Key, effect: "setState"), Target: set.Target, StateName: set.State),
            ActionEffect.AddState add => new CompiledBodyInstruction(Operation: BodyMotionOp.AddState, Value: FixedQ4816.FromDouble(value: RequireBodyEffectValue(value: add.Value, fromState: add.FromState, fromKey: add.FromKey, valueSeconds: add.ValueSeconds, actionName: actionName, effectName: "addState", state: add.State)), Direction: default, DurationTicks: 0UL, StateSlot: ResolveState(name: add.State, stateSlots: stateSlots, key: add.Key, effect: "addState"), Target: add.Target, StateName: add.State),
            ActionEffect.StartTimer timer => new CompiledBodyInstruction(
                Operation: BodyMotionOp.StartTimer,
                Value: default,
                Direction: default,
                DurationTicks: DurationTicks(seconds: timer.Seconds),
                StateSlot: ResolveState(name: timer.State, stateSlots: stateSlots),
                Target: timer.Target,
                StateName: timer.State
            ),
            ActionEffect.Designate designate => new CompiledBodyInstruction(
                Operation: BodyMotionOp.Designate,
                Value: default,
                Direction: default,
                DurationTicks: 0UL,
                StateSlot: -1,
                Target: designate.Target,
                StateName: designate.Register
            ),
            // Nothing is resolved at kit-compile time: the generator row and the destination row are world-global
            // `state` rows, not this kit's per-body slot table, so both names ride through to the mutation compose
            // boundary that owns their existence checks.
            ActionEffect.Generate generate => new CompiledBodyInstruction(
                Operation: BodyMotionOp.Generate,
                Value: default,
                Direction: default,
                DurationTicks: 0UL,
                StateSlot: -1,
                Target: ActionTarget.Self,
                StateName: generate.Row
            ),
            // countdownState/upsertHudPanel/removeHudPanel/upsertPlacement/removePlacement author WORLD state/document
            // rows — a per-body
            // action has none of its own, so these are refused BY NAME here rather than parsed and discarded
            // (legitimate only inside a WorldRule; see WorldRuleCompiler.CompileEffect).
            ActionEffect.CountdownState or ActionEffect.UpsertHudPanel or ActionEffect.RemoveHudPanel or ActionEffect.UpsertPlacement or ActionEffect.RemovePlacement =>
                throw new InvalidOperationException(message: $"Action '{actionName}' uses effect '{effect.GetType().Name}', which has no body-scope meaning — it authors a WORLD document row and is admissible only inside a world rule's own effects."),
            // save writes the WORLD's own file — a per-body action has no world file of its own to save, so this is
            // refused BY NAME here too (legitimate only inside a WorldRule; see WorldRuleCompiler.CompileEffect and
            // ActionEffect.Save's own remarks).
            ActionEffect.Save =>
                throw new InvalidOperationException(message: $"Action '{actionName}' uses effect 'Save', which has no body-scope meaning — a per-body action has no world file of its own to save, and is admissible only inside a world rule's own effects."),
            _ => throw new InvalidOperationException(message: $"Action '{actionName}' contains an unknown effect kind."),
        };

        if (!program.Admits(operation: instruction.Operation)) {
            throw new BodyMotionProgramException(refusal: BodyMotionProgramRefusal.OpcodeInadmissible, programName: program.Name, detail: $"action '{actionName}' opcode '{instruction.Operation}' is inadmissible for program kind '{program.Kind}'");
        }

        return instruction;
    }

    private static int ResolveState(string name, IReadOnlyDictionary<string, int>? stateSlots) => ((stateSlots is not null) && stateSlots.TryGetValue(key: name, value: out var slot))
        ? slot
        : throw new InvalidOperationException(message: $"Action state '{name}' was not declared.");

    // The keyed overload: a per-body action-state slot is not keyed, so an authored `key` here is refused rather than
    // discarded (it addresses a world state row's cell and is legitimate only in a world rule).
    private static int ResolveState(string name, IReadOnlyDictionary<string, int>? stateSlots, string? key, string effect) => (key is null)
        ? ResolveState(name: name, stateSlots: stateSlots)
        : throw new InvalidOperationException(message: $"Effect '{effect}' on action state '{name}' carries a 'key' — a per-body action-state slot is not keyed; 'key' addresses a world state row's cell and is legitimate only in a world rule.");

    // A per-body action-state slot has no world state row to copy from — setState/addState's live 'fromState'/
    // 'fromKey' spelling is legitimate only in a world rule (WorldRuleCompiler); a body-scope effect always writes an
    // authored constant, so 'value' is required here on the same terms compareState's own body-scope 'value' is.
    private static float RequireBodyEffectValue(float? value, string? fromState, string? fromKey, decimal? valueSeconds, string actionName, string effectName, string state) {
        if ((fromState is not null) || (fromKey is not null)) {
            throw new InvalidOperationException(message: $"Action '{actionName}' effect '{effectName}' on action state '{state}' carries a 'fromState'/'fromKey' — a per-body action-state slot has no world state row to copy from; a live copy source is legitimate only in a world rule.");
        }

        if (valueSeconds is not null) {
            throw new InvalidOperationException(message: $"Action '{actionName}' effect '{effectName}' on action state '{state}' carries a 'valueSeconds' — that spelling is WORLD SCOPE ONLY (a state row a world rule decrements once per simulation tick); a per-body effect writes an authored constant via 'value', or starts a proper timer via 'startTimer'.");
        }

        return (value ?? throw new InvalidOperationException(message: $"Action '{actionName}' effect '{effectName}' on action state '{state}' carries no 'value' — a per-body effect writes an authored constant; a live copy source is legitimate only in a world rule."));
    }

    // Seconds → engine ticks through the same FromDouble + round-up path the runtime tuning conversions ride.
    // Puck.Maths.FixedTickConversion is the single-sourced conversion Puck.World.Server's WorldBody calls too — this
    // project cannot reference WorldBody directly (Puck.World.Data must not depend on Puck.World.Server).
    private static ulong DurationTicks(float seconds) {
        return FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: seconds));
    }
}

/// <summary>One producer program and a kit's fixed-point arguments for it.</summary>
public sealed class CompiledBodyProducer {
    private readonly IReadOnlyDictionary<string, FixedQ4816> m_scalars;
    private readonly IReadOnlyDictionary<string, int> m_channels;

    private CompiledBodyProducer(CompiledBodyMotionProgram program, IReadOnlyDictionary<string, FixedQ4816> scalars, IReadOnlyDictionary<string, int> channels, FixedBodyTargetSource? target) {
        Program = program;
        m_scalars = scalars;
        m_channels = channels;
        Target = target;
    }

    /// <summary>Gets the compiled producer program.</summary>
    public CompiledBodyMotionProgram Program { get; }

    /// <summary>Gets the compiled target source, when this producer senses a target.</summary>
    public FixedBodyTargetSource? Target { get; }

    /// <summary>Reads one validated fixed-point scalar by name.</summary>
    public FixedQ4816 Scalar(string name) => m_scalars[name];

    /// <summary>Reads one validated channel ordinal by name, or <c>-1</c> when omitted.</summary>
    public int Channel(string name) => m_channels.TryGetValue(key: name, value: out var ordinal) ? ordinal : -1;

    /// <summary>Compiles a kit's producer parameters.</summary>
    public static CompiledBodyProducer Compile(CompiledBodyMotionProgram program, BodyProgramParameters parameters, WorldChannelTable channels, WorldTargetRegisterTable targets) {
        var scalars = new Dictionary<string, FixedQ4816>(capacity: parameters.Scalars.Count, comparer: StringComparer.Ordinal);
        foreach (var (name, value) in parameters.Scalars) {
            scalars.Add(key: name, value: FixedQ4816.FromDouble(value: value));
        }

        var channelOrdinals = new Dictionary<string, int>(capacity: parameters.Channels.Count, comparer: StringComparer.Ordinal);
        foreach (var (name, channel) in parameters.Channels) {
            channelOrdinals.Add(key: name, value: channels.TryGetOrdinal(name: channel, ordinal: out var ordinal) ? ordinal : -1);
        }

        return new CompiledBodyProducer(
            program: program,
            scalars: scalars,
            channels: channelOrdinals,
            target: (program.Target is { } target ? FixedBodyTargetSource.Compile(source: target, registers: targets) : null)
        );
    }
}

/// <summary>One compiled fact-triggered effect list.</summary>
/// <summary>One compiled fact trigger: the engine fact, the flattened additional gate, the edge/level mode, and the
/// effects a fire applies in order.</summary>
/// <param name="Fact">The engine fact.</param>
/// <param name="Gate">The flattened additional conjunction, empty when none is authored.</param>
/// <param name="Mode">Whether the trigger is level- or edge-fired (see <see cref="ActionTriggerMode"/>).</param>
/// <param name="Effects">The compiled effects, in authored order.</param>
public readonly record struct CompiledFactTrigger(ActionFact Fact, CompiledPredicate[] Gate, ActionTriggerMode Mode, CompiledBodyInstruction[] Effects);

/// <summary>A <see cref="WorldKit"/>'s compiled motion program, producer bindings, and action bindings.</summary>
/// <param name="BodyMotionProgram">The compiled body motion program the kit's bodies execute.</param>
/// <param name="Producers">The kit's producer bindings keyed by authored program name.</param>
/// <param name="Actions">The kit's compiled composition bindings, indexed by channel ordinal
/// (<see cref="ChannelLimits.MaxChannels"/> slots; unbound ordinals are <see langword="null"/>) — the channel-name map
/// resolved once against the world's <see cref="WorldChannelTable"/>.</param>
/// <param name="ActionThresholds">The binary crossing threshold for each ordinal in <paramref name="Actions"/>
/// (meaningful only where a binding exists).</param>
/// <param name="ActionShapes">The world's declared channel shape for every ordinal (not just where a binding
/// exists) — the held-image composition (<c>Puck.World.Server.WorldBody.NextIntent</c>) needs a composition
/// ordinal's shape whether or not this kit binds an action to it.</param>
/// <param name="Collider">The kit's compiled body volumes, or <see langword="null"/> for a volumeless kit.</param>
/// <param name="BodyContact">The authored dynamic-body contact mode.</param>
/// <param name="SprintChannelOrdinal">The ordinal <see cref="WorldMotionModel.Grounded.SprintChannel"/> (or the
/// vehicle arm's <see cref="WorldMotionModel.Vehicle.BoostChannel"/> — the same held-multiplier seam) resolved to,
/// or <c>-1</c> for a kit with no sprint capability (including a kit whose declared model carries none).</param>
/// <param name="DriftChannelOrdinal">The ordinal <see cref="WorldMotionModel.Vehicle.DriftChannel"/> resolved to,
/// or <c>-1</c> for a kit that cannot drift (every non-vehicle kit).</param>
/// <param name="RoleOrdinals">The authored ordinals resolved for engine motion roles.</param>
/// <param name="RoleMask">The compiled per-ordinal role predicate.</param>
/// <param name="ActionState">The kit's compiled named action-state register file.</param>
public readonly record struct FixedWorldKit(
    CompiledBodyMotionProgram BodyMotionProgram,
    IReadOnlyDictionary<string, CompiledBodyProducer> Producers,
    CompiledActionSpec?[] Actions,
    FixedQ4816[] ActionThresholds,
    ChannelShape[] ActionShapes,
    FixedWorldCollider? Collider,
    WorldBodyContactMode BodyContact,
    int SprintChannelOrdinal,
    int DriftChannelOrdinal,
    RoleChannelOrdinals RoleOrdinals,
    bool[] RoleMask,
    CompiledActionStateSlot[] ActionState
) {
    /// <summary>Compiles a kit row's authored floats to fixed point (the once-at-the-boundary rule), resolving its
    /// channel-name-keyed <see cref="WorldKit.Actions"/> and producer maps against the
    /// world's compiled channel table. Validation (<see cref="WorldDefinitionValidator"/>) has already rejected a dead
    /// channel name by the time this runs.</summary>
    /// <param name="kit">The authored kit row.</param>
    /// <param name="channels">The world's compiled channel table.</param>
    /// <param name="targets">The world's compiled target-register table.</param>
    /// <param name="programs">The world's compiled body motion programs keyed by stable name.</param>
    /// <param name="creations">The creation rows a <see cref="WorldCollider.FromCreation"/> may reference.</param>
    public static FixedWorldKit Compile(WorldKit kit, WorldChannelTable channels, WorldTargetRegisterTable targets, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, IReadOnlyList<WorldCreation> creations) {
        var actions = new CompiledActionSpec?[ChannelLimits.MaxChannels];
        var thresholds = new FixedQ4816[ChannelLimits.MaxChannels];
        // Every ordinal, not just bound ones — a composition channel's shape is a WORLD property, not a per-kit one,
        // and the held-image overlay composes it whether or not this kit binds an action there.
        var shapes = new ChannelShape[ChannelLimits.MaxChannels];
        var roleMask = new bool[ChannelLimits.MaxChannels];
        var program = programs[kit.BodyMotionProgram];
        var (actionState, stateSlots) = CompileActionState(actions: kit.Actions);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            shapes[ordinal] = channels.Shape(ordinal: ordinal);
            roleMask[ordinal] = channels.IsRole(ordinal: ordinal);
        }

        foreach (var (name, spec) in kit.Actions) {
            if (!channels.TryGetOrdinal(name: name, ordinal: out var ordinal)) {
                continue;
            }

            actions[ordinal] = CompiledActionSpec.Compile(spec: spec, stateSlots: stateSlots, program: program, actionName: $"{kit.Name}.{name}");
            thresholds[ordinal] = channels.Threshold(ordinal: ordinal);
        }

        // An arm without a held-multiplier channel resolves -1 here the same way a kit with the field unset does —
        // "no sprint" by construction, not a special case (DeclaredSprintChannel is the one arm-dispatch read,
        // covering Grounded's and Swim's sprint and the vehicle arm's boost, the same held-multiplier seam). The
        // vehicle arm's drift channel is its own held read, resolved the same way below.
        var sprintOrdinal = ((kit.Motion.DeclaredSprintChannel is { Length: > 0 } sprintChannel)
            && channels.TryGetOrdinal(name: sprintChannel, ordinal: out var sprintResolved)
            ? sprintResolved
            : -1);
        var driftOrdinal = (((kit.Motion as WorldMotionModel.Vehicle)?.DriftChannel is { Length: > 0 } driftChannel)
            && channels.TryGetOrdinal(name: driftChannel, ordinal: out var driftResolved)
            ? driftResolved
            : -1);
        var roleOrdinals = channels.RoleOrdinals;
        var producers = new Dictionary<string, CompiledBodyProducer>(capacity: kit.Producers.Count, comparer: StringComparer.Ordinal);

        foreach (var (name, parameters) in kit.Producers) {
            producers.Add(key: name, value: CompiledBodyProducer.Compile(program: programs[name], parameters: parameters, channels: channels, targets: targets));
        }

        RequireProgramRoles(kitName: kit.Name, program: program, ordinals: roleOrdinals);

        // The sprint/boost and drift ordinals are HELD reads, not Actions bindings — each needs its threshold in
        // ActionThresholds regardless of whether kit.Actions also binds a press/release effect there (the loop above
        // only writes a threshold where an ActionSpec exists), so WorldBody's held-channel test compares against the
        // channel's OWN declared threshold rather than the array's zero default.
        if (sprintOrdinal >= 0) {
            thresholds[sprintOrdinal] = channels.Threshold(ordinal: sprintOrdinal);
        }

        if (driftOrdinal >= 0) {
            thresholds[driftOrdinal] = channels.Threshold(ordinal: driftOrdinal);
        }

        return new FixedWorldKit(
            BodyMotionProgram: program,
            Producers: producers,
            Actions: actions,
            ActionThresholds: thresholds,
            ActionShapes: shapes,
            Collider: FixedWorldCollider.Compile(collider: kit.Collider, creations: creations),
            BodyContact: kit.BodyContact,
            SprintChannelOrdinal: sprintOrdinal,
            DriftChannelOrdinal: driftOrdinal,
            RoleOrdinals: roleOrdinals,
            RoleMask: roleMask,
            ActionState: actionState
        );
    }

    private static (CompiledActionStateSlot[] Slots, Dictionary<string, int> ByName) CompileActionState(IReadOnlyDictionary<string, ActionSpec> actions) {
        var slots = new List<CompiledActionStateSlot>();
        var byName = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var spec in actions.Values) {
            foreach (var state in (spec.State ?? [])) {
                if (byName.ContainsKey(key: state.Name)) {
                    continue;
                }

                byName[state.Name] = slots.Count;
                slots.Add(item: new CompiledActionStateSlot(
                    Name: state.Name,
                    Kind: state.Kind,
                    InitialValue: (state.Kind == ActionStateKind.Counter ? FixedQ4816.FromDouble(value: state.Initial) : FixedQ4816.Zero),
                    InitialTicks: (state.Kind == ActionStateKind.Timer ? FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: state.Initial)) : 0UL),
                    ResetFact: state.ResetFact,
                    Lifetime: state.Lifetime,
                    PlayerWritable: state.PlayerWritable,
                    Envelope: CompileEnvelope(state: state)
                ));
            }
        }

        return (Slots: slots.ToArray(), ByName: byName);
    }

    private static CompiledActionStateEnvelope? CompileEnvelope(ActionStateSlot state) {
        long Compile(float value) => state.Kind == ActionStateKind.Counter
            ? FixedQ4816.FromDouble(value: value).Value
            : checked((long)FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: value)));

        return state.Envelope switch {
            null => null,
            ActionStateEnvelope.Range range => new CompiledActionStateEnvelope(Minimum: Compile(value: range.Minimum), Maximum: Compile(value: range.Maximum), Values: null),
            ActionStateEnvelope.Set set => new CompiledActionStateEnvelope(Minimum: 0L, Maximum: 0L, Values: set.Values.Select(selector: Compile).ToArray()),
            _ => throw new InvalidOperationException(message: $"Unknown action-state envelope '{state.Envelope.GetType().Name}'."),
        };
    }

    private static void RequireProgramRoles(string kitName, CompiledBodyMotionProgram program, RoleChannelOrdinals ordinals) {
        foreach (var role in Enum.GetValues<ChannelRole>()) {
            if (program.RequiresRole(role: role) && (ordinals[role] < 0)) {
                throw new InvalidOperationException(message: $"Kit '{kitName}' body motion program '{program.Name}' requires channel role '{role}', but no declared channel claims it.");
            }
        }
    }
}

/// <summary>The declared channel ordinals resolved for the six engine motion roles. An unclaimed role is <c>-1</c>.</summary>
public readonly record struct RoleChannelOrdinals(int MoveForward, int MoveStrafe, int Turn, int MoveUp, int Pitch, int Roll) {
    /// <summary>Gets the authored ordinal claiming <paramref name="role"/>, or <c>-1</c> when unclaimed.</summary>
    public int this[ChannelRole role] => role switch {
        ChannelRole.MoveForward => MoveForward,
        ChannelRole.MoveStrafe => MoveStrafe,
        ChannelRole.Turn => Turn,
        ChannelRole.MoveUp => MoveUp,
        ChannelRole.Pitch => Pitch,
        ChannelRole.Roll => Roll,
        _ => -1,
    };

    /// <summary>Reads a resolved role from <paramref name="intent"/>.</summary>
    public FixedQ4816 Read(in PlayerIntent intent, ChannelRole role) {
        var ordinal = this[role];

        return ((ordinal >= 0) ? intent[ordinal] : FixedQ4816.Zero);
    }

    /// <summary>Builds an intent by writing values to the declared role ordinals.</summary>
    public PlayerIntent Intent(FixedQ4816 moveForward = default, FixedQ4816 moveStrafe = default, FixedQ4816 turn = default,
        FixedQ4816 moveUp = default, FixedQ4816 pitch = default, FixedQ4816 roll = default) {
        var intent = default(PlayerIntent);

        intent = Write(intent: intent, role: ChannelRole.MoveForward, value: moveForward);
        intent = Write(intent: intent, role: ChannelRole.MoveStrafe, value: moveStrafe);
        intent = Write(intent: intent, role: ChannelRole.Turn, value: turn);
        intent = Write(intent: intent, role: ChannelRole.MoveUp, value: moveUp);
        intent = Write(intent: intent, role: ChannelRole.Pitch, value: pitch);
        intent = Write(intent: intent, role: ChannelRole.Roll, value: roll);

        return intent;
    }

    /// <summary>Returns <paramref name="intent"/> with one claimed role replaced.</summary>
    public PlayerIntent Write(PlayerIntent intent, ChannelRole role, FixedQ4816 value) {
        var ordinal = this[role];

        return ((ordinal >= 0) ? intent.WithChannel(ordinal: ordinal, value: value) : intent);
    }
}

/// <summary>The world's channel table compiled once before simulation: name→ordinal resolution and per-ordinal shape
/// and threshold — the vocabulary <c>Puck.World.Server.WorldBody</c>'s edge derivation, the binding/press
/// surfaces, and the addon wire resolve declared channel names against. Validation
/// (<see cref="WorldDefinitionValidator"/>) has already run by the time this is built. Every declared channel receives
/// its document-order ordinal; role claims populate a resolved lookup and per-ordinal role mask.</summary>
public sealed class WorldChannelTable {
    /// <summary>The default binary threshold — <c>One/2</c>, the one threshold at which the flip bound
    /// <c>c ≤ min(T − 1, One − T)</c> collapses to the symmetric <c>c &lt; ½</c> (see
    /// <see cref="FixedContributionFold"/>'s remarks). A world declaring any other threshold is legal and gets the
    /// general bound, not this special case.</summary>
    public static readonly FixedQ4816 DefaultBinaryThreshold = (FixedQ4816.One / FixedQ4816.FromInteger(value: 2L));

    /// <summary>Gets the empty table — every world/kit compile call site that has not been threaded a real one yet falls
    /// back to this rather than null-checking.</summary>
    public static WorldChannelTable Empty { get; } = new WorldChannelTable(ordinalByName: new Dictionary<string, int>(comparer: StringComparer.Ordinal));

    private readonly Dictionary<string, int> m_ordinalByName;
    private readonly ChannelShape[] m_shapes = new ChannelShape[ChannelLimits.MaxChannels];
    private readonly FixedQ4816[] m_thresholds = new FixedQ4816[ChannelLimits.MaxChannels];
    private readonly bool[] m_declared = new bool[ChannelLimits.MaxChannels];
    private readonly bool[] m_roles = new bool[ChannelLimits.MaxChannels];
    private readonly int[] m_roleOrdinals = new int[Enum.GetValues<ChannelRole>().Length];
    // The reverse of m_ordinalByName — an ordinal's declared name, for a read-back that must name a channel rather
    // than just its ordinal (player.channels). Null past ChannelCount/at an undeclared ordinal.
    private readonly string?[] m_names = new string?[ChannelLimits.MaxChannels];

    private WorldChannelTable(Dictionary<string, int> ordinalByName) {
        m_ordinalByName = ordinalByName;
        Array.Fill(array: m_roleOrdinals, value: -1);
    }

    /// <summary>Gets the declared channel count.</summary>
    public int ChannelCount { get; private init; }

    /// <summary>Resolves a declared channel name to its ordinal.</summary>
    public bool TryGetOrdinal(string name, out int ordinal) => m_ordinalByName.TryGetValue(key: name, value: out ordinal);

    /// <summary>Resolves a binding channel reference to its authored ordinal. <see cref="ChannelRef"/> carries only
    /// the declared-name arm; see <c>ChannelRef.cs</c>'s remarks.</summary>
    public bool TryGetOrdinal(ChannelRef reference, out int ordinal) {
        switch (reference) {
            case ChannelRef.Name name:
                return TryGetOrdinal(name: name.Value, ordinal: out ordinal);
            default:
                ordinal = -1;

                return false;
        }
    }

    /// <summary>Determines whether a channel is declared at this ordinal.</summary>
    public bool IsDeclared(int ordinal) => ((ordinal >= 0) && (ordinal < ChannelLimits.MaxChannels) && m_declared[ordinal]);

    /// <summary>Determines whether the declared channel at <paramref name="ordinal"/> claims an engine motion role.</summary>
    public bool IsRole(int ordinal) => ((ordinal >= 0) && (ordinal < ChannelLimits.MaxChannels) && m_roles[ordinal]);

    /// <summary>Gets the resolved role ordinal set.</summary>
    public RoleChannelOrdinals RoleOrdinals => new(
        MoveForward: m_roleOrdinals[(int)ChannelRole.MoveForward],
        MoveStrafe: m_roleOrdinals[(int)ChannelRole.MoveStrafe],
        Turn: m_roleOrdinals[(int)ChannelRole.Turn],
        MoveUp: m_roleOrdinals[(int)ChannelRole.MoveUp],
        Pitch: m_roleOrdinals[(int)ChannelRole.Pitch],
        Roll: m_roleOrdinals[(int)ChannelRole.Roll]
    );

    /// <summary>Returns the declared shape at this ordinal (meaningful only when <see cref="IsDeclared"/>).</summary>
    public ChannelShape Shape(int ordinal) => m_shapes[ordinal];

    /// <summary>Returns the binary crossing threshold at this ordinal (meaningful only for a <see cref="ChannelShape.Binary"/> channel).</summary>
    public FixedQ4816 Threshold(int ordinal) => m_thresholds[ordinal];

    /// <summary>Compiles a declared channel shape to the exact range and optional terminal threshold consumed by
    /// <see cref="FixedContributionFold.Evaluate"/>: bipolar is <c>(-One, One, null)</c>, unipolar is
    /// <c>(Zero, One, null)</c>, and binary is <c>(Zero, One, threshold)</c>. Binary's continuous pool/range domain is
    /// therefore the same as unipolar; only the last threshold step snaps it to a bit.</summary>
    /// <param name="shape">The declared channel shape.</param>
    /// <param name="threshold">The channel table's compiled fixed-point threshold (read only for binary).</param>
    public static (FixedQ4816 Minimum, FixedQ4816 Maximum, FixedQ4816? Threshold) CompileFoldShape(ChannelShape shape, FixedQ4816 threshold) {
        return (shape switch {
            ChannelShape.Bipolar => (-FixedQ4816.One, FixedQ4816.One, null),
            ChannelShape.Unipolar => (FixedQ4816.Zero, FixedQ4816.One, null),
            ChannelShape.Binary => (FixedQ4816.Zero, FixedQ4816.One, threshold),
            _ => (FixedQ4816.Zero, FixedQ4816.One, null),
        });
    }

    /// <summary>Composes exactly two simultaneous held-image values. Unipolar/binary take the maximum of two
    /// already-ranged operands (an OR). Bipolar instead sums and clamps once to
    /// <c>[-One, One]</c>, making zero an additive identity that cannot overwrite a genuinely negative value.</summary>
    /// <remarks>Pairwise clamping is safe here only because both callers combine exactly two already-settled operands:
    /// the owning seat with the tick's completed contributor accumulator in
    /// <c>Server.WorldServer.FoldChannelContributions</c>, or the resolved movement tier with the live-held image in
    /// <c>WorldBody.NextIntent</c>. An unordered growing contribution set must accumulate raw instead; clamping
    /// per arrival would make a bipolar result order-dependent.</remarks>
    /// <param name="a">One side's raw Q48.16 value.</param>
    /// <param name="b">The other side's raw Q48.16 value.</param>
    /// <param name="shape">The channel's declared shape.</param>
    public static long ComposeHeld(long a, long b, ChannelShape shape) {
        if (shape != ChannelShape.Bipolar) {
            return Math.Max(val1: a, val2: b);
        }

        var sum = (a + b);

        return ((sum < -FixedQ4816.One.Value) ? -FixedQ4816.One.Value : ((sum > FixedQ4816.One.Value) ? FixedQ4816.One.Value : sum));
    }

    /// <summary>Returns the declared channel name at this ordinal, or <see langword="null"/> when <see cref="IsDeclared"/> is
    /// <see langword="false"/> for it — the reverse of name-to-ordinal resolution, for a read-back that must name a
    /// channel (<c>player.channels</c>) rather than address it.</summary>
    public string? Name(int ordinal) => m_names[ordinal];

    /// <summary>Compiles a world's declared channel table.</summary>
    /// <param name="channels">The world document's declared channel rows, already validated.</param>
    public static WorldChannelTable Compile(IReadOnlyList<WorldChannel> channels) {
        var ordinalByName = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var table = new WorldChannelTable(ordinalByName: ordinalByName) {
            ChannelCount = channels.Count,
        };
        for (var ordinal = 0; (ordinal < channels.Count); ordinal++) {
            var channel = channels[ordinal];

            ordinalByName[channel.Name] = ordinal;
            table.m_shapes[ordinal] = channel.Shape;
            table.m_declared[ordinal] = true;
            table.m_names[ordinal] = channel.Name;
            if (channel.Role is { } role) {
                table.m_roles[ordinal] = true;
                table.m_roleOrdinals[(int)role] = ordinal;
            }
            table.m_thresholds[ordinal] = ((channel.Shape == ChannelShape.Binary)
                ? ((channel.Threshold is { } threshold) ? FixedQ4816.FromDouble(value: threshold) : DefaultBinaryThreshold)
                : FixedQ4816.Zero);
        }

        return table;
    }
}

/// <summary>One row of the world's channel table — the intent vector's declared vocabulary (see
/// <see cref="Puck.World.Protocol.PlayerIntent"/>). The consumer is exactly one of <see cref="Role"/> (an engine
/// motion channel, claimable by at most one channel) or <see cref="Composition"/> (a kit composition trigger, bound —
/// or left inert — per kit via <see cref="WorldKit.Actions"/>).</summary>
/// <param name="Name">The channel's unique, non-empty name — the vocabulary key every binding, <c>player.press</c>,
/// kit <c>Actions</c> entry, and the addon wire resolve against.</param>
/// <param name="Shape">The declared value shape: bipolar <c>[-1, 1]</c>, unipolar <c>[0, 1]</c>, or binary.</param>
/// <param name="Role">The engine motion role this channel claims, or <see langword="null"/> for a composition channel.</param>
/// <param name="Composition">Whether this channel is a kit-composition trigger. Exactly one of <paramref name="Role"/>
/// or this must be set.</param>
/// <param name="Threshold">The binary crossing threshold in <c>[0, 1]</c> raw units (binary channels only); <see langword="null"/>
/// takes <see cref="WorldChannelTable.DefaultBinaryThreshold"/> (<c>One/2</c>).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldChannel(
    string Name,
    ChannelShape Shape,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChannelRole? Role = null,
    bool Composition = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Threshold = null
);

/// <summary>
/// One creation asset row — a whole <c>puck.creation.v1</c> document embedded inline, in canonical form, in the world
/// file with its identity hash pinned beside it. The document and hash must come from the same
/// <see cref="Puck.Forge.Authoring.CanonicalDocument{TDocument}"/>: the compose boundary canonicalizes on upsert
/// and rejects a hash the pipeline did not itself compute; the validator re-verifies the pin on every candidate, so a
/// tampered world file rejects loudly. World files stay self-contained — the CAS is an authoring-time import/export
/// cache, never a load-time dependency.
/// </summary>
/// <param name="Id">The row's stable string id — its mutation address and the handle placements reference.</param>
/// <param name="Document">The canonical (validated + normalized) creation document.</param>
/// <param name="Hash">The SHA-256 hex64 of the document's canonical bytes (<see cref="Puck.Forge.Authoring.CanonicalDocument{TDocument}.Hash"/>
/// on the canonical result the compose boundary produces).</param>
public sealed record WorldCreation(string Id, CreationDocument Document, string Hash);

/// <summary>A reflection plane in a placement's local frame.</summary>
/// <param name="Normal">The plane normal.</param>
/// <param name="Offset">The signed plane offset along the normalized <paramref name="Normal"/>.</param>
public sealed record WorldPlacementMirror(Vector3 Normal, float Offset);

/// <summary>A placement's inhabit facet — the row's binding to live population bodies. An inhabited placement is a
/// normal entry in the entity table: it holds a <c>Puck.World.Server.WorldBody</c>, integrates under the named
/// kit, and is addressable as <see cref="WorldAnchor.Entity"/> like any avatar. Its stamp rides the body's pose instead
/// of the row's static transform; the row's position/yaw become its spawn pose. Absent (null) = decoration, the
/// unchanged furniture behaviour.</summary>
/// <param name="Kit">The <see cref="WorldKit.Name"/> the bodies move under. Null resolves the creation's own
/// <see cref="Puck.Forge.Authoring.CreationBehaviorDocument.Locomotion"/> token AS a kit name — a creation declaring "swim"
/// inhabits the world's kit row named "swim". Neither resolving is a loud rejection naming every kit the world
/// declares.</param>
/// <param name="Look">The <see cref="WorldLook.Name"/> the bodies wear, or null to wear an implicit creation look on
/// this placement's own <c>CreationId</c>.</param>
/// <param name="Source">The live, idle, or named producer source the bodies wake on.</param>
/// <param name="Count">How many bodies, bounded by the world's authored peer capacity.</param>
/// <param name="Distribution">The region and deterministic fill sequence that place the bodies relative to the
/// placement root.</param>
public sealed record WorldPlacementInhabit(
    string? Kit,
    string? Look,
    Puck.World.Protocol.IntentSource Source,
    int Count = 1,
    WorldDistribution? Distribution = null
);

/// <summary>A per-instance override of one declared creation face's feed — the face twin of the emission facet's
/// per-instance override channel.</summary>
/// <param name="Face">The declared <see cref="Puck.Forge.Authoring.CreationFaceDocument.Name"/> to override.</param>
/// <param name="Source">The screen source the face shows, in the existing <see cref="WorldScreenSource"/> vocabulary.</param>
/// <param name="Portal">The face's portal facet (see <see cref="WorldPlacementPortal"/>) — absent (the default)
/// means this face is not a door. Optional and trailing deliberately: a face authored before this facet existed
/// round-trips unchanged, and it composes freely with <paramref name="Source"/> — the door and the screen it shows
/// are independent facts about the same face.</param>
public sealed record WorldPlacementFace(
    string Face,
    WorldScreenSource Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementPortal? Portal = null
);

/// <summary>A placement's region facet — a named volume row, not a trigger system: any placement may carry one,
/// turning its stamp into a sensing volume the world-events feed watches for body enter/exit edges (see
/// <c>Server.WorldEventFeed</c> and the <c>observe region:&lt;name&gt;</c> grant subject). The region's name is the
/// carrying placement's <see cref="WorldPlacement.Id"/> — one identity, never a second string kept in sync by hand.
/// The volume is a sphere centered on the placement's <see cref="WorldPlacement.Position"/> (the placement's own
/// <see cref="WorldPlacement.Scale"/>/<see cref="WorldPlacement.YawDegrees"/> do not affect it — a region's size is
/// its own authored radius, never derived from the creation's visual bounds). Presentation-only in itself (drawing
/// no geometry); sensing reads the same document-authored center every tick, converted to fixed-point at the same
/// boundary <see cref="WorldSolid"/> facets already cross through — unless the row also carries
/// <see cref="WorldPlacement.Attach"/>, in which case the center is the resolved live body pose instead
/// (<c>Server.WorldEventFeed.CollectRegions</c>, the same resolve <c>world.attachments</c> answers): the sensing
/// sphere follows the carrier, and an inactive carrier senses nobody rather than sensing at a stale point.</summary>
/// <param name="Radius">The sensing radius, world units. Must be finite and positive (validated).</param>
public sealed record WorldPlacementRegion(float Radius);

/// <summary>A placement's attach facet — binds the row's stamp to a live population body's transform, so the
/// resolved world pose follows that body every tick (an avatar's hat, held item, nameplate, or aura) instead of
/// sitting at the row's own authored <see cref="WorldPlacement.Position"/>/<see cref="WorldPlacement.YawDegrees"/>.
/// The offset rides the body's own local frame — rotated by the body's orientation before adding, the
/// <c>Puck.SdfVm.Views.OrientedFollowRig</c>/<c>FirstPersonRig</c> convention for a moving anchor, never the
/// world-axis <c>FollowRig</c> shape a fixed subject would use. The resolved pose is never written back into the
/// document, and it is derived twice, at two clocks, from the one authored facet:
/// <list type="bullet">
/// <item><description>the authoritative answer is fixed point — the body's fixed-point pose composed with this
/// facet's authored (float, quantized at resolution like every other placement field) offset, by
/// <c>Puck.World.Server.WorldPlacementAttachment.TryResolve</c>, on demand: <c>world.attachments</c> is its only
/// caller today, so it runs when a reader asks rather than on a schedule;</description></item>
/// <item><description>the rendered pose is presentation float — the same composition over the client's
/// interpolated body pose, packed every frame by <c>Client.WorldStampPool</c>, which is what makes an attached row
/// visibly ride its body as smoothly as the body itself. An attached row draws through that reserved stamp pool and
/// not as a static stamp (<c>Client.WorldPlacementStamper.IsStaticStamp</c>), and it charges
/// <see cref="WorldPlacementPolicy.MaxStampRegistrations"/> like an animated row does.</description></item>
/// </list>
/// Region, solid (under the analytic contact provider), and emission were once refused on the same row as this one
/// because each read the row's own static transform — all three now read the same resolved dynamic pose instead
/// (<c>Server.WorldEventFeed.CollectRegions</c>, <c>Server.WorldColliderSet.RefreshAttached</c>,
/// <c>Client.WorldStampPool.TryShapePosition</c>/<c>RootPose</c>), so a region's aura, an analytic collider's
/// hitbox, and an emission's voice all track the carrier: an equipped item's sensing sphere, hitbox, or source point
/// rides the body it is attached to. What stays refused: distribution/mirror (static-stamp-only, the same rule an
/// animated or inhabited row already enforces), inhabit (a row cannot both spawn its own driven bodies and ride
/// another's), and solid specifically under the field contact provider (it compiles every solid row's geometry once
/// into one SDF program and never rebuilds it per tick) — refused by name rather than defining a blend (see
/// <see cref="WorldDefinitionValidator"/>).</summary>
/// <param name="BodyIndex">The 0-based population entity index the placement rides — the same indexing
/// <see cref="WorldAnchor.Entity"/> and the console's <c>body:&lt;n&gt;</c> grant subject use, not the 1-based
/// <c>player.*</c> seat number (<c>body:1</c> is "player 2"). Validated within <c>0..</c>the world's authored
/// population capacity; the target need not be active at author time (see remarks — an inactive body at runtime
/// makes the row contribute nothing, it does not refuse).</param>
/// <param name="LocalOffset">The stamp's position offset in the body's own local frame, world units.</param>
/// <param name="LocalYawDegrees">The stamp's yaw offset from the body's own heading, degrees. Zero rides the
/// body's exact facing.</param>
public sealed record WorldPlacementAttach(int BodyIndex, Vector3 LocalOffset, float LocalYawDegrees = 0f);

/// <summary>
/// One placement instance row — a creation asset stamped into the world by reference: transform + facets as
/// data, addressed by its stable <paramref name="Id"/>. A placement whose creation carries timeline frames is
/// animated: it replays client-side on the render clock through the reserved dynamic-transform pool (distribution/mirror
/// facets are static-stamp-only and reject on an animated row). A placement carrying an <paramref name="Inhabit"/>
/// facet is a live population body rather than furniture (see <see cref="WorldPlacementInhabit"/>); its declared
/// creation eyes derive <see cref="WorldCamera"/> feeds and its declared faces derive screens (both at the delivery
/// boundary, never written to the document).
/// </summary>
/// <param name="Id">The row's stable string id (its mutation address).</param>
/// <param name="CreationId">The referenced <see cref="WorldCreation.Id"/> (must resolve; removal of a referenced
/// creation rejects loudly).</param>
/// <param name="Position">The stamp position, world space. Inert (still validated and stored, but read by nothing —
/// neither the resolve nor the renderer) when <paramref name="Attach"/> is set: the row's live position is the resolved
/// attachment, never this authored one.</param>
/// <param name="YawDegrees">The stamp yaw about +Y, degrees. Same attach caveat as <paramref name="Position"/>.</param>
/// <param name="Scale">The uniform stamp scale (clamped to the placement policy envelope by validation).</param>
/// <param name="Distribution">The placement distribution, or <see langword="null"/> for a single copy. Static
/// placements currently accept a lattice region with a <c>none</c> fill. Refused together with <paramref name="Attach"/>.</param>
/// <param name="Mirror">The authored local reflection plane, or <see langword="null"/> for no reflected copy. Refused
/// together with <paramref name="Attach"/>.</param>
/// <param name="Emission">The placement's emission facet (a synth voice the stamp itself makes — see
/// <see cref="WorldEmission"/>), or <see langword="null"/> for silent. Under <paramref name="Distribution"/> the emission
/// binds to the placement root only. Omitted from the wire when null. Composes with <paramref name="Attach"/>: an
/// attached row's source point rides the resolved live pose (<c>Client.WorldStampPool.TryShapePosition</c>) instead
/// of the row's static position, and an inactive carrier silences the emitter rather than leaving it at a stale point.</param>
/// <param name="Solid">The placement's solidity facet (see <see cref="WorldSolid"/>). Both contact providers compile
/// the creation's emitted shapes; analytic collision uses per-primitive colliders, including exact half-spaces for
/// planes. Omitted from the wire when null. Composes with <paramref name="Attach"/> under the analytic provider only
/// (<c>WorldColliderSet.RefreshAttached</c> recomputes an attached row's colliders every tick from the resolved live
/// pose); still refused together under the field provider, which compiles every solid row's geometry once into one
/// SDF program and never rebuilds it per tick.</param>
/// <param name="Inhabit">The inhabit facet (null = decoration), binding the row to live population bodies. Omitted from
/// the wire when null. Refused together with <paramref name="Attach"/> — a row cannot both spawn its own driven
/// bodies and ride another body's pose.</param>
/// <param name="FaceSources">Per-instance overrides of the creation's declared faces (null = every face shows its
/// declared default). Omitted from the wire when null. Orthogonal to <paramref name="Attach"/> (a content selector,
/// not a transform) — composes freely, like every other facet that now tracks the dynamic pose.</param>
/// <param name="Region">The placement's region facet (see <see cref="WorldPlacementRegion"/>) — a named volume the
/// world-events feed watches for body enter/exit, or <see langword="null"/> for none. Omitted from the wire when null.
/// Composes with <paramref name="Attach"/>: an attached row's sensing sphere centers on the resolved live pose
/// (<c>Server.WorldEventFeed.CollectRegions</c>) instead of the row's static position — see
/// <see cref="WorldPlacementRegion"/>'s own remarks.</param>
/// <param name="Attach">The placement's attach facet (see <see cref="WorldPlacementAttach"/>) — binds the row's
/// resolved world pose to a live population body, or <see langword="null"/> for a static/authored transform (the
/// default, unchanged behavior). Omitted from the wire when null.</param>
public sealed record WorldPlacement(
    string Id,
    string CreationId,
    Vector3 Position,
    float YawDegrees,
    float Scale,
    WorldDistribution? Distribution = null,
    WorldPlacementMirror? Mirror = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldEmission? Emission = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSolid? Solid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementInhabit? Inhabit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldPlacementFace>? FaceSources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementRegion? Region = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementAttach? Attach = null
);

/// <summary>Adapts placement document facets to the shared creation-stamp vocabulary.</summary>
public static class WorldPlacementStamp {
    /// <summary>Returns the placement's shared lattice declaration.</summary>
    public static CreationStampPattern? PatternFor(WorldPlacement placement) => placement.Distribution?.Region is WorldDistributionRegion.Lattice lattice
        ? new CreationStampPattern(StepA: lattice.StepA, CountA: lattice.CountA, StepB: lattice.StepB, CountB: lattice.CountB)
        : null;

    /// <summary>Returns the placement's shared reflection plane.</summary>
    public static CreationStampPlane? MirrorFor(WorldPlacement placement) => placement.Mirror is { } mirror
        ? new CreationStampPlane(Normal: mirror.Normal, Offset: mirror.Offset)
        : null;
}

/// <summary>
/// The signal carried by a <see cref="WorldScreen"/>'s lit face. A source declares which provider feeds a slot; the
/// engine resolves and samples it. The <c>$type</c> string is the JSON discriminator; a new source kind is a new
/// derived record plus its <see cref="JsonDerivedTypeAttribute"/> line.
/// </summary>
[JsonDerivedType(typeof(WorldScreenSource.None), typeDiscriminator: "none")]
[JsonDerivedType(typeof(WorldScreenSource.TestPattern), typeDiscriminator: "testPattern")]
[JsonDerivedType(typeof(WorldScreenSource.Machine), typeDiscriminator: "machine")]
[JsonDerivedType(typeof(WorldScreenSource.Camera), typeDiscriminator: "camera")]
[JsonDerivedType(typeof(WorldScreenSource.View), typeDiscriminator: "view")]
[JsonDerivedType(typeof(WorldScreenSource.Capture), typeDiscriminator: "capture")]
[JsonDerivedType(typeof(WorldScreenSource.Console), typeDiscriminator: "console")]
[JsonDerivedType(typeof(WorldScreenSource.Qr), typeDiscriminator: "qr")]
[JsonDerivedType(typeof(WorldScreenSource.Session), typeDiscriminator: "session")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldScreenSource {
    private WorldScreenSource() {
    }

    /// <summary>No provider is bound — the engine lights the slot with its procedural no-signal fallback (an animated
    /// test-card / striped no-signal look, never black).</summary>
    public sealed record None() : WorldScreenSource;

    /// <summary>The deterministic animated test pattern (<see cref="Puck.SdfVm.Views.TestPatternSource"/>), rendered
    /// from the world's sim tick (never the wall clock) into a CPU buffer and uploaded each frame.</summary>
    /// <param name="Width">The pattern framebuffer width in pixels.</param>
    /// <param name="Height">The pattern framebuffer height in pixels.</param>
    public sealed record TestPattern(int Width, int Height) : WorldScreenSource;

    /// <summary>An arbitrary deterministic machine's unresampled framebuffer — resolved against a registered
    /// <see cref="Puck.Abstractions.Machines.IScreenMachineEngine"/> by <paramref name="Engine"/> id. The world never
    /// names a concrete machine: the engine owns its <paramref name="Options"/> vocabulary (a GamingBrick reads a
    /// dmg/cgb/agb model + a dmgspeed pin).</summary>
    /// <param name="Engine">The screen-machine engine id (e.g. <c>gaming-brick</c>).</param>
    /// <param name="ContentPath">The content file (a cartridge ROM) the machine boots, or empty when the screen is
    /// unconfigured — the binder faults the slot gracefully (no crash, no-signal card) rather than booting.</param>
    /// <param name="Options">The engine-specific options string, or <see langword="null"/> for the engine's defaults.</param>
    public sealed record Machine(string Engine, string ContentPath, string? Options) : WorldScreenSource;

    /// <summary>The platform's default live camera feed, with an explicit preferred capture profile. The platform may
    /// negotiate a nearby extent; every screen sampling the same physical default device shares one session.</summary>
    /// <param name="Profile">The preferred capture extent and maximum upload cadence.</param>
    public sealed record Camera(WorldFeedProfile Profile) : WorldScreenSource;

    /// <summary>A named view from the presentation view stack, such as a monitor showing another camera's output.</summary>
    /// <param name="CameraName">The registered view name this slot samples.</param>
    public sealed record View(string CameraName) : WorldScreenSource;

    /// <summary>A live compositor capture feed — a desktop window keyed by title, or a whole monitor keyed by index. The
    /// selector is the altitude of the primitive: <paramref name="MonitorIndex"/> null is window mode; non-null is
    /// whole-monitor mode (and <paramref name="WindowTitle"/> is unused).</summary>
    /// <param name="WindowTitle">The captured window's title (window mode; ignored when <paramref name="MonitorIndex"/> is set).</param>
    /// <param name="Profile">This capture consumer's output extent and maximum refresh cadence.</param>
    /// <param name="MonitorIndex">The 0-based monitor to capture whole (0 = primary), or <see langword="null"/> for window mode.</param>
    public sealed record Capture(string WindowTitle, WorldFeedProfile Profile, int? MonitorIndex = null) : WorldScreenSource;

    /// <summary>A screen showing the developer console as an object in the world — the diegetic half of the control plane
    /// the unification contract names ("the on-screen panel and process stdin"). The frame is CPU-composed into a
    /// CRT-styled framebuffer and pushed through <c>IGpuSurfaceUpload</c>, exactly as the ported console feed does;
    /// nothing about it is a render-graph node. Complementary to — never a duplicate of — <c>WorldConsoleMirror</c>,
    /// which publishes the same content to the screen-space overlay. At most one <c>console</c> source may be live
    /// (declared) at a time; an unselected console entry sitting in a magazine is legal.</summary>
    /// <param name="Rows">Console text rows the framebuffer composes, 1..120. Sizes the CPU buffer.</param>
    /// <param name="Columns">Console text columns, 1..400.</param>
    /// <param name="Procedural">When true the slot shows the sibling generated pattern instead of console text — carried
    /// as a mode of this variant rather than as a seventh union case.</param>
    public sealed record Console(int Rows = 24, int Columns = 64, bool Procedural = false) : WorldScreenSource;

    /// <summary>An authorable QR code (ISO/IEC 18004) — the document names a payload string and the engine derives the
    /// scannable module grid (<see cref="Puck.World.Qr.QrEncoder"/>), rendered CPU-side into a static B8G8R8A8
    /// framebuffer and uploaded once, never re-derived from the tick like <see cref="TestPattern"/>. The driving case
    /// is a link one human hands another off an in-world screen. This record is the document-authored half only —
    /// nothing here mints a payload at runtime; <c>screen.source &lt;index&gt; qr</c> is the live-authoring twin, and <c>world.identify</c>
    /// is the one caller that mints its payload (the running world's own documentId and content-address pin) rather
    /// than being handed one.</summary>
    /// <param name="Payload">The encoded string, UTF-8 byte mode. Must fit within version
    /// <see cref="Puck.World.Qr.QrEncoder.MaxSupportedVersion"/> at <paramref name="EcLevel"/> — validation refuses an
    /// oversized payload by name (its byte count against the level's capacity), never truncates it.</param>
    /// <param name="EcLevel">The error-correction level: <c>L</c>, <c>M</c>, <c>Q</c>, or <c>H</c> (case-insensitive,
    /// parsed by <see cref="Puck.World.Qr.QrErrorCorrection.TryParse"/>). Defaults to <c>M</c>.</param>
    /// <param name="QuietZoneModules">The white quiet-zone border width in modules on every side. ISO/IEC 18004
    /// recommends at least 4; a smaller value authors a QR a real scanner may refuse to read (a borderless QR does not
    /// scan) — the document may still author it (validation only refuses a negative width), since a screen's physical
    /// framing sometimes supplies the margin itself.</param>
    public sealed record Qr(string Payload, string EcLevel = "M", int QuietZoneModules = 4) : WorldScreenSource;

    /// <summary>
    /// A live rendered view of another world, resolved through a <c>destinations</c> row (docs/world-model.md,
    /// "Observation and display"). The face/screen resolves the same resolver-owned identity a
    /// portal crossing at the same door would land in (<see cref="Puck.World.WorldSessionResolver"/>), attaches an
    /// observation lease to the resolved instance's server, and mirrors just enough of its delivered
    /// definition/snapshots to render its static authored geometry through <paramref name="CameraName"/> (or the
    /// destination's default projection). It never re-derives durability/scope/generation itself — those are the
    /// destination row's own facts.
    /// </summary>
    /// <remarks>
    /// <para><b>Staged boundary — no avatar/pose mirroring yet.</b> The session projection renders the destination's
    /// authored static placement geometry (terrain, structures — whatever a fixed camera would already show with
    /// nobody standing in the world); live embodied bodies in the destination are not yet mirrored into the image.</para>
    /// <para><b>Staged boundary — no sub-tick interpolation.</b> The projection re-renders whenever the destination
    /// delivers a new definition (a real content change), never on the host's own presentation alpha — it reads
    /// neither <see cref="Puck.SdfVm.ISdfFrameSource.CaptureFrame"/>'s host delta/alpha nor the destination's
    /// Tick/StepTicks for easing, so "the destination's clock, never the host's" is satisfied by construction rather
    /// than by a second interpolation implementation. Smooth interpolation of moving destination content is
    /// meaningless before the avatar mirror above lands, so it is deferred with it.</para>
    /// <para><b>Staged boundary — global scope only.</b> A <c>user</c>/<c>group</c>-scoped destination makes the
    /// resolved image viewer-dependent, and the shipped one-image-per-screen-index binding shows every viewer the
    /// same image — showing one viewer's world to everyone would be silently wrong, so a session face naming a
    /// non-global destination refuses at bind time by name rather than binding to an arbitrary viewer's resolution.
    /// Per-viewport binding is future work (docs/world-model.md, "User/group-scoped destinations make images
    /// viewer-dependent").</para>
    /// </remarks>
    /// <param name="Destination">The <see cref="Puck.World.WorldDestination.Name"/> this face/screen observes. Must
    /// resolve to a declared <c>destinations</c> row — an undeclared name refuses at boot (validated, like a portal
    /// facet's own <c>destination</c>).</param>
    /// <param name="CameraName">The destination's own placeable-camera name to render through, or
    /// <see langword="null"/> for its default projection (its first declared camera, else a fixed overview derived
    /// from its spawn points). Wire name <c>camera</c> — plain <c>Camera</c> would collide with the sibling
    /// <see cref="WorldScreenSource.Camera"/> arm's own type name inside this enclosing record. Validated only as
    /// non-empty when present at author time — the destination's own definition is not joined at boot (references
    /// assert naming intent, not reachability), so an unknown camera name is refused loudly at bind time instead,
    /// once the destination is actually resolved, falling back to the default projection rather than refusing the
    /// whole bind. Ignored under <see cref="WorldScreenProjection.Window"/> (see <paramref name="Projection"/>).</param>
    /// <param name="Projection">How the destination render projects onto this face (see <see cref="WorldScreenProjection"/>).
    /// Default <see cref="WorldScreenProjection.Camera"/> — unauthored worlds and every session facet authored before
    /// this member existed render byte-identically. Optional and trailing (the same widen-without-moving-existing-members
    /// shape <paramref name="CameraName"/> itself already follows). <see cref="WorldScreenProjection.Window"/> requires
    /// this same face's <see cref="WorldPlacementFace.Portal"/> to author <see cref="WorldPortalArrival.Mapped"/> with a
    /// <see cref="WorldPlacementPortal.Counterpart"/> — refused by name otherwise (see <see cref="WorldDefinitionValidator"/>);
    /// a top-level <c>screens</c> row or magazine entry carries no face to pair with, so <c>window</c> is refused there
    /// unconditionally.</param>
    /// <param name="Resolution">The offscreen target's <c>[width, height]</c> in pixels, or <see langword="null"/> for
    /// the engine default (<c>Puck.SdfVm.Views.WorldSessionView.DefaultWidth</c> x <c>DefaultHeight</c> — today's
    /// 160x144 panel, unchanged for an unauthored facet). Each axis is validated within
    /// <c>1..WorldDefinitionValidator.MaxSurfaceDimension</c>. Omitted from the wire when null.</param>
    public sealed record Session(
        string Destination,
        [property: JsonPropertyName("camera"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CameraName = null,
        WorldScreenProjection Projection = WorldScreenProjection.Camera,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldScreenResolution? Resolution = null
    ) : WorldScreenSource;
}

/// <summary>An ordered set of sources one screen may show, plus the entry its selector starts on — the cycle primitive. A
/// selection is a pointer into this list; changing it never changes how many screen slots exist, so a magazine costs no
/// render envelope. Entries are the same closed <see cref="WorldScreenSource"/> vocabulary the declared source uses, so a
/// screen may rotate a cartridge, the webcam, and a jumbotron view through one slot.</summary>
/// <param name="Entries">The ordered source list (at least one entry).</param>
/// <param name="Selected">The 0-based entry the selector starts on (what <c>screen.select</c> advances from), not what the
/// screen boots showing — a screen always wakes on its declared <c>Source</c> (the one-live-console ceiling depends on
/// this). Live selection drifts from this and is folded back by <c>world.save</c> (see <c>Puck.World.WorldSessionCapture</c>).</param>
/// <param name="Wrap">Whether advancing past the last entry returns to the first (the arcade cabinet's wrapping cycle);
/// when false the selector clamps at both ends.</param>
public sealed record WorldScreenMagazine(IReadOnlyList<WorldScreenSource> Entries, int Selected = 0, bool Wrap = true);

/// <summary>A cable-linked group of screens whose machines advance as one interleaved unit. The binder steps the link,
/// never its members individually, so the engine's deterministic interleave — not the host's frame order — decides who
/// runs when. Every member must resolve to a machine from the same engine, and that engine must implement
/// <c>IMachineLinkingEngine</c>; a link whose members do not currently satisfy that is reported dormant, never silently
/// dropped.</summary>
/// <param name="Name">The link's stable kebab-case name (its mutation address).</param>
/// <param name="Screens">The engine screen indices in cable order (2 or more, no duplicates).</param>
public sealed record WorldScreenLink(string Name, IReadOnlyList<int> Screens);

/// <summary>A live screen feed's requested output policy. It belongs to the source declaration rather than the binder,
/// so two window captures can choose different extents and cadences. Camera extents are preferences because a physical
/// device remains authoritative for its negotiated format.</summary>
/// <param name="Width">Requested output width in pixels.</param>
/// <param name="Height">Requested output height in pixels.</param>
/// <param name="RefreshRateHz">Maximum pull/upload cadence; it must divide the engine time base exactly.</param>
public readonly record struct WorldFeedProfile(int Width, int Height, uint RefreshRateHz) {
    /// <summary>Gets the fallback used by runtime screen verbs that do not provide an authored source profile.</summary>
    public static WorldFeedProfile Default { get; } = new(Width: 320, Height: 240, RefreshRateHz: 30U);
}

/// <summary>The neutral pad element an authored <see cref="WorldScreenTranslationRow"/> maps a channel onto — the
/// context-routes widening's replacement for <c>WorldEngagement.Translate</c>'s old hard-wired map. Named after the
/// engine-neutral <c>MachinePadState</c>'s own axis/button vocabulary; a translation row picks exactly one.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldPadElement>))]
public enum WorldPadElement : byte {
    /// <summary>The left stick's X axis.</summary>
    LeftStickX,
    /// <summary>The left stick's Y axis.</summary>
    LeftStickY,
    /// <summary>The right stick's X axis.</summary>
    RightStickX,
    /// <summary>The right stick's Y axis.</summary>
    RightStickY,
    /// <summary>The left analog trigger.</summary>
    LeftTrigger,
    /// <summary>The right analog trigger.</summary>
    RightTrigger,
    /// <summary>The bottom face button.</summary>
    South,
    /// <summary>The right face button.</summary>
    East,
    /// <summary>The left face button.</summary>
    West,
    /// <summary>The top face button.</summary>
    North,
    /// <summary>The directional pad's up direction.</summary>
    DpadUp,
    /// <summary>The directional pad's down direction.</summary>
    DpadDown,
    /// <summary>The directional pad's left direction.</summary>
    DpadLeft,
    /// <summary>The directional pad's right direction.</summary>
    DpadRight,
    /// <summary>The left shoulder (bumper) button.</summary>
    LeftShoulder,
    /// <summary>The right shoulder (bumper) button.</summary>
    RightShoulder,
    /// <summary>The start/menu/plus button.</summary>
    Start,
    /// <summary>The back/select/view/minus button.</summary>
    Back,
}

/// <summary>One authored channel→pad-element mapping row — a <see cref="WorldScreenRoute.Translation"/> entry.</summary>
/// <param name="Channel">The declared channel name this row reads.</param>
/// <param name="Element">The neutral pad element the channel's value drives.</param>
public readonly record struct WorldScreenTranslationRow(string Channel, WorldPadElement Element);

/// <summary>The route policy a <see cref="WorldScreen"/> carries: whether a player may engage the screen, the activation
/// radius, whether engaging auto-boots the selected magazine entry, the world-event channels a gesture drives it
/// through, which channel ordinals the route reaches, and how those channels translate to the target's pad image. The
/// optional members each default to the inert/baked choice: no auto-boot, no gesture channel, every channel reached,
/// and the engine's default translation (the two movement roles to the left stick — <c>MoveStrafe</c>/
/// <c>MoveForward</c>, structural ordinals, never a channel name). The default names no gameplay channel: a
/// route whose machine needs a face button (or any other element) must author that row explicitly — see
/// <c>Server.WorldEngagement.CompileTranslation</c>.</summary>
/// <param name="Engageable">Whether a player may engage this screen.</param>
/// <param name="EngageRadius">The world-unit radius a player must be inside to engage (meaningful only when
/// <paramref name="Engageable"/>). Validated finite and non-negative.</param>
/// <param name="AutoInsert">When set, engaging the screen first boots the selected magazine entry (the "walk over, press
/// the button, the screen lights" gesture), so the interaction is one act rather than an insert then an engage.</param>
/// <param name="EngageChannel">The world-event channel whose arrival on a body engages this screen, or
/// <see langword="null"/> (the default) for a route that does not answer gestures. The author chooses this name freely;
/// the engine never special-cases a spelling. Omitted from the wire when null.</param>
/// <param name="CycleChannel">Same, for advancing the magazine selector. Omitted from the wire when null.</param>
/// <param name="Channels">The declared channel names this route's mask reaches — a masked-out channel keeps flowing to
/// the routed body's own pose (relevant under the capture:false mirror policy) but never reaches this route's target.
/// <see langword="null"/> (the default) reaches every declared channel. Omitted from the wire when null.</param>
/// <param name="Translation">The authored channel→pad-element rows this route's target reads, replacing the engine's
/// default when present. <see langword="null"/> uses the engine mapping. Omitted from the wire when
/// null.</param>
public readonly record struct WorldScreenRoute(bool Engageable, float EngageRadius, bool AutoInsert = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EngageChannel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CycleChannel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Channels = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldScreenTranslationRow>? Translation = null) {
    /// <summary>Gets a screen no player engages (the default for a passive display).</summary>
    public static WorldScreenRoute Passive { get; } = new WorldScreenRoute(Engageable: false, EngageRadius: 0f);
}

/// <summary>One diegetic screen in the world — a screen slab emitted by
/// <see cref="Puck.SdfVm.SdfProgramBuilder"/> whose lit face
/// samples a bound source (or the procedural fallback when unbound). The frame (<see cref="Origin"/>/<see cref="Right"/>/
/// <see cref="Up"/> + <see cref="HalfWidth"/>/<see cref="HalfHeight"/>) is the sampled surface frame and must match the
/// slab's placement; the frame source bakes the geometry translate from it.</summary>
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
/// <c>Margin</c> by <c>Server.WorldColliderSet</c>), or <see langword="null"/> for a decorative screen. Omitted from the
/// wire when null.</param>
/// <param name="Magazine">The per-screen source magazine (the cycle primitive), or <see langword="null"/> for a screen
/// with no magazine — nothing to cycle. Omitted from the wire when null — the whole-row <c>UpsertScreen</c>
/// carries it for free, so no new mutation kind is needed.</param>
public sealed record WorldScreen(
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
public readonly record struct FixedMotionResponse(CompiledPredicate[] Gate, FixedQ4816 EngageRate, FixedQ4816 ReleaseRate);

/// <summary>The compiled fixed-point form of an authored <see cref="MotionScalarEnvelope"/> — the reusable
/// seat-time clamp bound every overridable motion-arm scalar shares. <see cref="WorldDefinitionValidator"/> has
/// already refused <see cref="Max"/> &lt; <see cref="Min"/> by the time this compiles, so <see cref="Clamp"/> never
/// faults.</summary>
public readonly record struct FixedMotionScalarEnvelope(FixedQ4816 Min, FixedQ4816 Max) {
    /// <summary>Compiles an authored scalar envelope to its fixed-point form.</summary>
    public static FixedMotionScalarEnvelope Compile(in MotionScalarEnvelope envelope) => new(
        Min: FixedQ4816.FromDouble(value: envelope.Min),
        Max: FixedQ4816.FromDouble(value: envelope.Max)
    );

    /// <summary>Restricts <paramref name="value"/> to this envelope's inclusive bound.</summary>
    public FixedQ4816 Clamp(FixedQ4816 value) => FixedQ4816.Clamp(value: value, minimum: Min, maximum: Max);
}

/// <summary>The one-time fixed-point compilation of an authored <see cref="WorldMotionModel.Grounded"/> row. Runtime
/// simulation reads only this form.</summary>
/// <remarks>A simulation-affecting extension: <see cref="Response"/> promotes a slice of the tuning that the shaping stage of
/// <c>WorldBody</c>'s grounded operations reads. <see cref="ResponseRecencyFacts"/>/<see cref="ResponseRecencyWindows"/>
/// are the shared recency-clock table across every row's <see cref="ActionPredicate.Recently"/> gate (the per-tick clock
/// updater walks it), slotted by the same <see cref="CompiledActionSpec.FlattenPredicate"/> the lane bindings use.</remarks>
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
    /// <summary>Gets the number of recency clocks the response table's Recently gates share.</summary>
    public int RecencySlots => ResponseRecencyFacts.Length;

    /// <summary>Compiles an authored grounded motion row to its fixed-point form.</summary>
    public static FixedMotionTuning Compile(WorldMotionModel.Grounded tuning) => Compile(
        moveSpeed: tuning.MoveSpeed,
        turnSpeed: tuning.TurnSpeed,
        riseGravity: tuning.RiseGravity,
        fallGravity: tuning.FallGravity,
        maxFallSpeed: tuning.MaxFallSpeed,
        response: tuning.Response,
        sprintMultiplier: tuning.SprintMultiplier,
        moveFrame: tuning.MoveFrame,
        facingSnap: tuning.FacingSnap,
        moveSpeedEnvelope: tuning.MoveSpeedEnvelope
    );

    /// <summary>Compiles an authored swim motion row's shared half — speeds, response table, sprint, frame — to the
    /// same fixed-point form every model rides (the gravity fields compile to zero; the swim program's facet
    /// coherence already refused any op that would read them). The swim-specific half is
    /// <see cref="FixedSwimTuning.Compile"/>.</summary>
    public static FixedMotionTuning Compile(WorldMotionModel.Swim tuning) => Compile(
        moveSpeed: tuning.ThrustSpeed,
        turnSpeed: tuning.TurnSpeed,
        riseGravity: 0f,
        fallGravity: 0f,
        maxFallSpeed: 0f,
        response: tuning.Response,
        sprintMultiplier: tuning.SprintMultiplier,
        moveFrame: tuning.MoveFrame,
        facingSnap: tuning.FacingSnap,
        moveSpeedEnvelope: tuning.ThrustSpeedEnvelope
    );

    private static FixedMotionTuning Compile(float moveSpeed, float turnSpeed, float riseGravity, float fallGravity, float maxFallSpeed, IReadOnlyList<MotionResponse> response, float sprintMultiplier, MotionMoveFrame moveFrame, bool facingSnap, MotionScalarEnvelope? moveSpeedEnvelope) {
        var rows = response;
        var compiled = new FixedMotionResponse[rows.Count];
        var recencyFacts = new List<ActionFact>();
        var recencyWindows = new List<ulong>();

        for (var index = 0; (index < rows.Count); index++) {
            var gate = new List<CompiledPredicate>();

            // The response table shares ONE recency-clock table across all rows (as one lane's press/release channels
            // share one), slotted by the same predicate flattener the action lanes use.
            CompiledActionSpec.FlattenPredicate(predicate: rows[index].Gate, gate: gate, recencyFacts: recencyFacts, recencyWindows: recencyWindows);

            compiled[index] = new FixedMotionResponse(
                Gate: gate.ToArray(),
                EngageRate: FixedQ4816.FromDouble(value: rows[index].EngageRate),
                ReleaseRate: FixedQ4816.FromDouble(value: rows[index].ReleaseRate)
            );
        }

        return new(
            MoveSpeed: FixedQ4816.FromDouble(value: moveSpeed),
            TurnSpeed: FixedQ4816.FromDouble(value: turnSpeed),
            RiseGravity: FixedQ4816.FromDouble(value: riseGravity),
            FallGravity: FixedQ4816.FromDouble(value: fallGravity),
            MaxFallSpeed: FixedQ4816.FromDouble(value: maxFallSpeed),
            Response: compiled,
            ResponseRecencyFacts: recencyFacts.ToArray(),
            ResponseRecencyWindows: recencyWindows.ToArray(),
            SprintMultiplier: FixedQ4816.FromDouble(value: sprintMultiplier),
            MoveFrame: moveFrame,
            FacingSnap: facingSnap,
            MoveSpeedEnvelope: ((moveSpeedEnvelope is { } envelope) ? FixedMotionScalarEnvelope.Compile(envelope: envelope) : null)
        );
    }
}

/// <summary>The one-time fixed-point compilation of an authored <see cref="WorldMotionModel.Vehicle"/> row. Runtime
/// simulation reads only this form; the held drift/boost channel names resolve to ordinals separately, through
/// <see cref="FixedWorldKit.Compile"/>'s channel table.</summary>
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
) {
    /// <summary>Compiles an authored vehicle motion row to its fixed-point form.</summary>
    public static FixedVehicleTuning Compile(WorldMotionModel.Vehicle tuning) => new(
        TopSpeed: FixedQ4816.FromDouble(value: tuning.TopSpeed),
        ReverseTopSpeed: FixedQ4816.FromDouble(value: tuning.ReverseTopSpeed),
        Accel: FixedQ4816.FromDouble(value: tuning.Accel),
        Brake: FixedQ4816.FromDouble(value: tuning.Brake),
        CoastDrag: FixedQ4816.FromDouble(value: tuning.CoastDrag),
        Grip: FixedQ4816.FromDouble(value: tuning.Grip),
        SteerRate: FixedQ4816.FromDouble(value: tuning.SteerRate),
        SteerReferenceSpeed: FixedQ4816.FromDouble(value: tuning.SteerReferenceSpeed),
        SteerFalloff: FixedQ4816.FromDouble(value: tuning.SteerFalloff),
        PitchRate: FixedQ4816.FromDouble(value: tuning.PitchRate),
        DriftGrip: FixedQ4816.FromDouble(value: tuning.DriftGrip),
        DriftSteerScale: FixedQ4816.FromDouble(value: tuning.DriftSteerScale),
        BoostMultiplier: FixedQ4816.FromDouble(value: tuning.BoostMultiplier),
        TopSpeedEnvelope: ((tuning.TopSpeedEnvelope is { } envelope) ? FixedMotionScalarEnvelope.Compile(envelope: envelope) : null)
    );
}

/// <summary>The one-time fixed-point compilation of an authored <see cref="WorldMotionModel.Swim"/> row's
/// swim-specific half. The shared half (speeds, response table, sprint, frame) compiles into the same
/// <see cref="FixedMotionTuning"/> every model rides, so the generic stages (speed resolution, the response-table
/// shape machinery) never dispatch on the model; only the swim operations read this record.</summary>
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
) {
    /// <summary>Compiles an authored swim motion row's swim-specific fields to fixed point.</summary>
    public static FixedSwimTuning Compile(WorldMotionModel.Swim tuning) => new(
        VerticalThrustFraction: FixedQ4816.FromDouble(value: tuning.VerticalThrustFraction),
        Buoyancy: FixedQ4816.FromDouble(value: tuning.Buoyancy),
        MaxRiseSpeed: FixedQ4816.FromDouble(value: tuning.MaxRiseSpeed),
        MaxSinkSpeed: FixedQ4816.FromDouble(value: tuning.MaxSinkSpeed),
        SurfaceSettleRate: FixedQ4816.FromDouble(value: tuning.SurfaceSettleRate),
        FloatDepth: FixedQ4816.FromDouble(value: tuning.FloatDepth)
    );
}

/// <summary>The fixed-point convex volume kinds accepted by both contact providers.</summary>
public enum FixedBodyColliderKind : byte {
    Sphere,
    Capsule,
    Box,
}

/// <summary>One fixed-point convex volume in a body's local frame.</summary>
/// <param name="Kind">The volume kind.</param>
/// <param name="Center">The sphere/box center or capsule lower endpoint.</param>
/// <param name="Endpoint">The capsule upper endpoint.</param>
/// <param name="HalfExtents">The box half-extents.</param>
/// <param name="Rotation">The box's local orientation.</param>
/// <param name="Radius">The sphere/capsule radius.</param>
public readonly record struct FixedBodyColliderVolume(
    FixedBodyColliderKind Kind,
    FixedVector3 Center,
    FixedVector3 Endpoint,
    FixedVector3 HalfExtents,
    FixedQuaternion Rotation,
    FixedQ4816 Radius
);

/// <summary>The one-time fixed-point compilation of a kit's compound body volume.</summary>
public readonly record struct FixedWorldCollider(FixedBodyColliderVolume[] Volumes) {
    /// <summary>Compiles authored collider floats and creation primitive copies to fixed point.</summary>
    public static FixedWorldCollider? Compile(WorldCollider? collider, IReadOnlyList<WorldCreation> creations) {
        if (collider is null) {
            return null;
        }

        var volumes = new List<FixedBodyColliderVolume>(capacity: WorldCollider.MaxVolumes);

        switch (collider) {
            case WorldCollider.Sphere sphere: {
                var radius = FixedQ4816.FromDouble(value: sphere.Radius);
                volumes.Add(item: Sphere(center: new FixedVector3(X: FixedQ4816.Zero, Y: radius, Z: FixedQ4816.Zero), radius: radius));
                break;
            }
            case WorldCollider.Capsule capsule: {
                var radius = FixedQ4816.FromDouble(value: capsule.Radius);
                var lower = new FixedVector3(X: FixedQ4816.Zero, Y: radius, Z: FixedQ4816.Zero);
                volumes.Add(item: Capsule(lower: lower, upper: (lower + FixedVector3.FromVector3(value: capsule.Endpoint)), radius: radius));
                break;
            }
            case WorldCollider.Box box: {
                var halfExtents = FixedVector3.FromVector3(value: box.HalfExtents);
                volumes.Add(item: Box(
                    center: new FixedVector3(X: FixedQ4816.Zero, Y: halfExtents.Y, Z: FixedQ4816.Zero),
                    halfExtents: halfExtents,
                    rotation: ToFixed(value: box.Rotation)
                ));
                break;
            }
            case WorldCollider.FromCreation fromCreation: {
                var creation = WorldDefinitionRows.FindCreation(creations: creations, id: fromCreation.CreationId)
                    ?? throw new InvalidOperationException(message: $"Body collider creation '{fromCreation.CreationId}' is not defined.");

                CreationStampEmitter.VisitPrimitiveCopies(
                    document: creation.Document,
                    transform: new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: 1f, ReflectionNormal: null),
                    visitor: copy => {
                        if (copy.Shape.Type == AvatarPrimitive.Plane) {
                            throw new InvalidOperationException(message: $"Body collider creation '{fromCreation.CreationId}' contains an unbounded plane.");
                        }

                        if ((copy.Shape.Type == AvatarPrimitive.Sphere) && (copy.UniformScale > 0f)) {
                            var sphere = CreationGeometry.GetLocalBounds(type: AvatarPrimitive.Sphere);
                            volumes.Add(item: Sphere(center: FixedVector3.FromVector3(value: copy.Center), radius: FixedQ4816.FromDouble(value: (sphere.HalfExtents.X * copy.UniformScale))));
                        } else {
                            volumes.Add(item: Box(center: FixedVector3.FromVector3(value: copy.Center), halfExtents: FixedVector3.FromVector3(value: copy.HalfExtents), rotation: FixedQuaternion.Identity));
                        }
                    }
                );
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(paramName: nameof(collider), actualValue: collider, message: "The body collider kind is not defined.");
        }

        return new FixedWorldCollider(Volumes: volumes.ToArray());
    }

    private static FixedBodyColliderVolume Sphere(FixedVector3 center, FixedQ4816 radius) =>
        new(Kind: FixedBodyColliderKind.Sphere, Center: center, Endpoint: FixedVector3.Zero, HalfExtents: FixedVector3.Zero, Rotation: FixedQuaternion.Identity, Radius: radius);

    private static FixedBodyColliderVolume Capsule(FixedVector3 lower, FixedVector3 upper, FixedQ4816 radius) =>
        new(Kind: FixedBodyColliderKind.Capsule, Center: lower, Endpoint: upper, HalfExtents: FixedVector3.Zero, Rotation: FixedQuaternion.Identity, Radius: radius);

    private static FixedBodyColliderVolume Box(FixedVector3 center, FixedVector3 halfExtents, FixedQuaternion rotation) =>
        new(Kind: FixedBodyColliderKind.Box, Center: center, Endpoint: FixedVector3.Zero, HalfExtents: halfExtents, Rotation: rotation, Radius: FixedQ4816.Zero);

    private static FixedQuaternion ToFixed(Quaternion value) => new FixedQuaternion(
        X: FixedQ4816.FromDouble(value: value.X),
        Y: FixedQ4816.FromDouble(value: value.Y),
        Z: FixedQ4816.FromDouble(value: value.Z),
        W: FixedQ4816.FromDouble(value: value.W)
    ).Normalize();
}

/// <summary>The one-time fixed-point compilation of the world's contact tuning — read by the analytic contact field
/// and the grounded integrator. <see cref="GroundedThreshold"/> is the compiled <c>cos(maxSlopeDegrees)</c> a contact
/// normal's up-alignment must clear to ground a body (the same test both providers use). <see cref="GradientUp"/> is
/// the compiled <see cref="WorldContactRequirement.GradientDerivedUp"/> requirement: without it the body up axis stays
/// world <c>+Y</c>, so a vertical face pushes but never grounds.</summary>
public readonly record struct FixedWorldCollision(
    FixedQ4816 ContactSkin,
    int MaxIterations,
    FixedQ4816 GroundedThreshold,
    FixedQ4816 GradientProbe,
    bool GradientUp
) {
    /// <summary>Compiles the authored contact tuning to fixed point.</summary>
    public static FixedWorldCollision Compile(WorldCollision collision) => new(
        ContactSkin: FixedQ4816.FromDouble(value: collision.ContactSkin),
        MaxIterations: collision.MaxIterations,
        GroundedThreshold: FixedQ4816.Cos(angle: FixedQ4816.FromDouble(value: (collision.MaxSlopeDegrees * (Math.PI / 180.0)))),
        GradientProbe: FixedQ4816.FromDouble(value: collision.GradientProbe),
        GradientUp: ((collision.Requirements?.Contains(value: WorldContactRequirement.GradientDerivedUp)) ?? false)
    );
}

/// <summary>One placeable camera composed from a reference frame, local motion, framing policy, lens, and render target.</summary>
/// <param name="Name">The camera's stable name — the handle a View screen / layout slot samples by.</param>
/// <param name="Anchor">What the camera rides, or <see langword="null"/> for the world reference frame.</param>
/// <param name="Rig">The independent local motion, aim, and lens axes.</param>
/// <param name="RenderWidth">The offscreen render width in pixels.</param>
/// <param name="RenderHeight">The offscreen render height in pixels.</param>
public sealed record WorldCamera(string Name, WorldAnchor? Anchor, WorldCameraRig Rig, uint RenderWidth, uint RenderHeight);

/// <summary>Whether a local seat activates automatically at boot (<see cref="Eager"/>) or waits for a claim
/// (<see cref="OnDemand"/>) — the per-seat authored policy <see cref="WorldPopulationDefaults.SeatActivation"/>
/// declares. Both doors converge on the identical <c>Server.WorldPopulation.ActivateSeat</c> call through the same
/// <c>SessionRequest.Join</c>/<c>WorldServer.ApplySession</c> session-join seam regardless of which policy admitted
/// the seat — a seat activated on demand (<c>player.join</c>, or a controller's own hot-plug first touch via
/// <c>Client.PlayerRoster.ResolveDeviceSlot</c>) is indistinguishable from one activated at boot the instant it is
/// active.</summary>
[JsonConverter(typeof(StrictEnumConverter<SeatActivationPolicy>))]
public enum SeatActivationPolicy : byte {
    /// <summary>The seat's body is minted at boot, mirroring a session join for it immediately — the only policy
    /// seat 0 (player 1) may declare, since a session always needs a first player.</summary>
    Eager,

    /// <summary>The seat stays empty at boot; its body is minted the first time something claims it.</summary>
    OnDemand,
}

/// <summary>The built-in session census. Local players occupy the split-screen seats; network players are represented
/// by authoritative local stand-ins until a transport supplies their intent stream.</summary>
/// <param name="SeatActivation">The per-seat boot-activation policy, exactly <see
/// cref="WorldPopulationLimits.LocalSeatCount"/> entries in seat order. Seat 0 (player 1) must be <see
/// cref="SeatActivationPolicy.Eager"/> (refused otherwise — the session always needs a first player); the
/// remaining seats are ordinarily authored <see cref="SeatActivationPolicy.OnDemand"/> so a friend's controller or
/// an explicit <c>player.join</c> claims one only when someone actually shows up, rather than every local seat
/// standing in the world unowned from tick 1.</param>
/// <param name="NetworkPlayers">The number of active network-human stand-ins at boot.</param>
/// <param name="DefaultPeerSource">The boot intent-source template every network stand-in wakes on (<see
/// cref="IntentSource.Producer(string)"/> in the built-in world).</param>
/// <param name="SeatSpawns">The spawn-point name selected by each local seat ordinal.</param>
/// <param name="Distribution">How simulated peers are distributed at spawn.
/// A third timing class within this row: it is live for future activations but inert for bodies already standing
/// (a change re-clusters only peers spawned after it), narrated in the accept echo.</param>
/// <param name="PeerVariation">The independently authored producer-state sequences for peer bodies.</param>
/// <param name="SeatVariation">The independently authored producer-state sequences for local-seat bodies.</param>
/// <param name="PeerColors">The stand-in color sequence, independent of producer-state variation.</param>
/// <param name="Capacity">The total authoritative body capacity, including reserved local seats.</param>
/// <param name="ReconnectGraceSeconds">How long a disconnected body stays parked — retained in the sim/collider
/// set at its last pose, still counted <c>IsHumanOccupied</c> — before the deferred teardown (body drop, and for a
/// peer, its generation's grants) actually fires. <c>0</c> disables the grace window outright: a disconnect tears
/// the body down immediately, the pre-park behavior. A positive value authored against a world whose
/// <see cref="WorldDefinition.SimulationRateHz"/> is 0 parks the body forever — there is no tick mapping for a
/// world that never advances, so the deferred teardown never fires (never, not immediately and not zero; see
/// <see cref="CompiledTickDuration"/>). Authored in seconds — a physical unit, not a tick count, so a world's rate
/// can change without silently retuning this window — and compiled once to
/// <see cref="WorldDefinition.PopulationReconnectGraceTicks"/> via <see cref="WorldSimulationTickConversion"/>.
/// Read once at construction/rebuild,
/// like the rest of this section (<c>SetPopulationDefaults</c>'s own timing class) — a live edit takes effect on the
/// next disconnect, never retroactively on an already-parked body. See <c>Server.WorldPopulation</c>'s park-with-grace
/// remarks and the <c>$parked:&lt;bodyRef&gt;</c> reserved rule channel (<see cref="WorldRuleFacts.ParkedPrefix"/>)
/// that reads a parked body's remaining count. Default 3 seconds — 720 ticks at the fixed 240 Hz simulation
/// rate.</param>
/// <param name="CapacityDraw">The census's authored-randomness facet, or <see langword="null"/> for an ordinary
/// literal <paramref name="Capacity"/>. A boot-only site (<see cref="WorldDrawSites.PopulationCapacity"/>): settled
/// into <paramref name="Capacity"/>, cleared, and narrated exactly like
/// <see cref="WorldHostDefaults.BackendDraw"/>.
/// <para><b>The census coherence rule.</b> The site's admissible domain is not the capacity ceiling alone —
/// <paramref name="NetworkPlayers"/> is validated against capacity minus the local seats, so a drawn capacity below
/// that sum is a document this same validator would refuse once resolved. The domain is narrowed statically at
/// authoring instead, so the roll can never decide whether the world boots.</para>
/// <para><b>Not an XOR.</b> Unlike <see cref="WorldHostDefaults.BackendDraw"/>, this site cannot refuse
/// both-declared: <see cref="WorldPopulationDefaults"/> is a struct, so an authored <c>capacity: 128</c> and the C#
/// default 128 are indistinguishable once parsed. When both are present the draw simply wins — a stated limitation,
/// not a silent guess.</para></param>
public readonly record struct WorldPopulationDefaults(
    IReadOnlyList<SeatActivationPolicy> SeatActivation,
    int NetworkPlayers,
    IntentSource DefaultPeerSource,
    IReadOnlyList<string> SeatSpawns,
    WorldDistribution Distribution,
    WorldPopulationVariation PeerVariation,
    WorldPopulationVariation SeatVariation,
    WorldSequence PeerColors,
    int Capacity = 128,
    float ReconnectGraceSeconds = 3.0f,
    // OPTIONAL — the authored-randomness facet over Capacity above (see the param docs).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldDraw? CapacityDraw = null
);

/// <summary>One participant-specific input-hold override. An omitted body uses the section defaults. The compiled
/// shape — <see cref="Ticks"/> is simulation ticks, the unit the runtime actually consumes. The document and the
/// <c>world.row.set inputHold</c> console verb both author this in seconds instead
/// (<see cref="WorldInputHoldParticipantAuthoring"/>); <see cref="WorldInputHoldAuthoring.Compile"/> is the one seam
/// that converts between the two, so this type itself never sees a raw tick literal from a document.</summary>
/// <param name="BodyIndex">The participant's 0-based population body index.</param>
/// <param name="Ticks">The authored hold floor, in simulation ticks.</param>
/// <param name="Equalized">Whether this participant contributes to and receives the shared maximum.</param>
public readonly record struct WorldInputHoldParticipant(int BodyIndex, int Ticks, bool Equalized);

/// <summary>The world's participant input-hold policy. Measured holds raise authored floors, the applied value is
/// capped by <see cref="CeilingTicks"/>, and a lower target must remain unchanged for <see cref="LowerAfterTicks"/>
/// before the applied hold descends one tick per simulation tick. The compiled shape — every <c>*Ticks</c> field is
/// simulation ticks, the unit <c>Server.WorldInputHoldRuntime</c> actually consumes — never what
/// <see cref="WorldDefinition.InputHold"/> itself stores (that field is the authored seconds shape,
/// <see cref="WorldInputHoldAuthoring"/>; see its own remarks). <see cref="WorldInputHoldAuthoring.Compile"/> and
/// <see cref="ToAuthoring"/> are the two conversions, both parameterized on a simulation rate rather than a pinned
/// constant, since a world's rate is authored (<see cref="WorldSimulationDefaults"/>). The separate addon-mutation ABI
/// (<c>Puck.World.Server.WorldAddonMutationDecoder</c>) still constructs this type directly with raw ticks — a live
/// runtime API, not authored document content, and out of either conversion's reach by architecture.</summary>
/// <param name="CeilingTicks">The maximum applied hold, in simulation ticks.</param>
/// <param name="LowerAfterTicks">How many simulation ticks a lower target must remain unchanged before descent.</param>
/// <param name="DefaultTicks">The authored hold floor for participants without an override.</param>
/// <param name="EqualizeByDefault">Whether participants without an override share the maximum.</param>
/// <param name="Participants">Participant-specific floor and distribution overrides, keyed by body index.</param>
public readonly record struct WorldInputHoldSettings(
    int CeilingTicks,
    int LowerAfterTicks,
    int DefaultTicks,
    bool EqualizeByDefault,
    IReadOnlyList<WorldInputHoldParticipant> Participants
) {
    /// <summary>Decompiles this compiled (ticks) settings row back to its authored (seconds) shape, at
    /// <paramref name="ratePerSecond"/> — the inverse of <see cref="WorldInputHoldAuthoring.Compile"/>. Exact whenever
    /// every tick count is a multiple of <paramref name="ratePerSecond"/> (every value a live seconds-authored
    /// <c>world.row.set inputHold</c> compiled through <see cref="WorldInputHoldAuthoring.Compile"/> is); a raw tick
    /// count from the addon-mutation ABI that is not may round-trip to the nearest second, one tick off on
    /// reconversion — see <see cref="WorldSimulationTickConversion.SecondsFromTicks"/>'s remarks.</summary>
    /// <param name="ratePerSecond">The simulation rate (Hz) this settings row runs under — a world's own
    /// <see cref="WorldDefinition.SimulationRateHz"/>.</param>
    public WorldInputHoldAuthoring ToAuthoring(uint ratePerSecond) {
        var participants = new WorldInputHoldParticipantAuthoring[Participants.Count];

        for (var index = 0; (index < participants.Length); index++) {
            var participant = Participants[index];

            participants[index] = new WorldInputHoldParticipantAuthoring(
                BodyIndex: participant.BodyIndex,
                Seconds: WorldSimulationTickConversion.SecondsFromTicks(ticks: participant.Ticks, ratePerSecond: ratePerSecond),
                Equalized: participant.Equalized
            );
        }

        return new WorldInputHoldAuthoring(
            CeilingSeconds: WorldSimulationTickConversion.SecondsFromTicks(ticks: CeilingTicks, ratePerSecond: ratePerSecond),
            LowerAfterSeconds: WorldSimulationTickConversion.SecondsFromTicks(ticks: LowerAfterTicks, ratePerSecond: ratePerSecond),
            DefaultSeconds: WorldSimulationTickConversion.SecondsFromTicks(ticks: DefaultTicks, ratePerSecond: ratePerSecond),
            EqualizeByDefault: EqualizeByDefault,
            Participants: participants
        );
    }
}
public static class WorldApplicationDefaults {
    /// <summary>The built-in world ships with no bundled AGB cartridge — an asset-free default, never an owner-local
    /// absolute path or a copyrighted dump. Durable per-deployment cartridge/BIOS paths belong in the world data file
    /// (the "durable config lives in the data file" doctrine); the <c>puck.world.def.v1</c> loader
    /// (<c>Puck.World.WorldDefinitionLoader</c>) reads one, but the checked-in default file authors an empty content
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
public readonly record struct WorldQualityPreset(
    ShadowTier Shadows,
    bool AmbientOcclusion,
    WorldRenderScaleTier RenderScale
);

/// <summary>The world's render-lever defaults — the boot values <c>Puck.World.WorldRenderSettings</c> wakes on and the
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
public sealed record WorldRenderDefaults(
    ShadowTier Shadows,
    float ShadowCrowdRadius,
    bool AmbientOcclusion,
    WorldRenderScaleTier RenderScale,
    float UpscaleSharpness,
    WorldQualityPreset Low,
    WorldQualityPreset Medium,
    WorldQualityPreset High
) {
    /// <summary>Gets the built-in default render levers — the boot values and preset table.</summary>
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

/// <summary>One named spawn pose available to seats and population policies.</summary>
/// <param name="Id">The stable spawn name, unique within the definition.</param>
/// <param name="Position">The seat's spawn position.</param>
/// <param name="YawDegrees">The spawn yaw about +Y, in degrees.</param>
public readonly record struct WorldSpawnPoint(string Id, Vector3 Position, float YawDegrees = 0f);

/// <summary>The row-to-entity assignment declaration — nothing about <see cref="Sequence"/>/<see cref="Rows"/> is kit-specific,
/// so the same primitive distributes the kit table (a way of moving) and the look table (a way of looking) across the
/// population. Resolved once at construction into each entry's fixed row index (precompute; zero steady-state cost). The
/// kit assignment affects the simulation (it selects the compiled tuning/action bindings); the look assignment is
/// presentation-only (it selects the appearance row).</summary>
/// <param name="Sequence">The sequence that selects a row.</param>
/// <param name="Rows">An authored row-name view, or empty to select from every declared row in declaration order.</param>
public sealed record WorldRowAssignment(WorldSequence Sequence, IReadOnlyList<string> Rows) {
    private readonly IReadOnlyList<string> m_rows = (Rows ?? []);

    /// <summary>Gets the authored row-name view. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="WorldMotionModel.Grounded.Response"/>'s does.</summary>
    public IReadOnlyList<string> Rows {
        get => m_rows;
        init => m_rows = (value ?? []);
    }
}

/// <summary>Where a <see cref="WorldLook"/> resolves an entity's appearance from — a pinned catalog rig or a sculpted
/// creation. The appearance peer of a way of moving: a new way of looking is a row, never a new renderer.</summary>
[JsonDerivedType(typeof(WorldLookSource.Catalog), typeDiscriminator: "catalog")]
[JsonDerivedType(typeof(WorldLookSource.Creation), typeDiscriminator: "creation")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldLookSource {
    private WorldLookSource() { }

    /// <summary>The procedural humanoid catalog (<c>WorldAvatarCatalog</c>) — one look source among others.</summary>
    /// <param name="Index">The procedural renderer catalog rig to pin, or
    /// <see langword="null"/> for the occupant-owned pick. A fresh occupant seeds that pick from its first local
    /// slot and carries it across authority transfers, so ordinary admission does not restyle it.</param>
    public sealed record Catalog(int? Index) : WorldLookSource {
        /// <summary>The procedural renderer's fixed rig count.</summary>
        public const int RigCount = 128;
    }

    /// <summary>A sculpted creation worn by the body — resolved against the world's <see cref="WorldCreation"/> rows.</summary>
    /// <param name="CreationId">The referenced <see cref="WorldCreation.Id"/> (must resolve at validation).</param>
    public sealed record Creation(string CreationId) : WorldLookSource;
}

/// <summary>How a look animates with the body it clothes. presentation-only: read by the client's stamp pool and the
/// catalog packer, never by <c>WorldBody</c>. Catalog looks read <see cref="GaitAmplitude"/>; creation looks read
/// <see cref="ReplayFrames"/> and <see cref="SecondsPerFrame"/>.</summary>
/// <param name="GaitAmplitude">The catalog rig's limb-swing scale (1 = the pre-look default; 0 stills the gait).</param>
/// <param name="ReplayFrames">Whether a creation look replays its authored timeline on the render clock.</param>
/// <param name="SecondsPerFrame">The creation timeline cadence when <see cref="ReplayFrames"/> is set.</param>
public readonly record struct WorldLookMotion(float GaitAmplitude, bool ReplayFrames, float SecondsPerFrame) {
    /// <summary>Gets the implicit look motion — full gait, no timeline replay — every body wore before this arc.</summary>
    public static WorldLookMotion Default { get; } = new WorldLookMotion(GaitAmplitude: 1f, ReplayFrames: false, SecondsPerFrame: 0f);
}

/// <summary>One look row — the appearance peer of <see cref="WorldKit"/>'s way of moving. Every appearance a world
/// offers is a row of this data, never a renderer branch; <c>world.looks</c> prints these names.</summary>
/// <param name="Name">The look's stable kebab-case name (unique within the definition), assignable by the look table.</param>
/// <param name="Source">Where the appearance resolves from (a catalog rig or a creation).</param>
/// <param name="Scale">The uniform render scale. Appearance only — it does not resize the body's motion tuning or its
/// collision volume.</param>
/// <param name="Motion">How the look animates with the body (see <see cref="WorldLookMotion"/>).</param>
public sealed record WorldLook(string Name, WorldLookSource Source, float Scale, WorldLookMotion Motion) {
    /// <summary>Gets the implicit single look every body wears when a world authors no <c>looks</c> section — the
    /// occupant-owned catalog pick at full gait.</summary>
    public static WorldLook Implicit { get; } = new WorldLook(Name: "catalog", Source: new WorldLookSource.Catalog(Index: null), Scale: 1f, Motion: WorldLookMotion.Default);
}

/// <summary>The deterministic pose compiled from one authored spawn point.</summary>
/// <param name="Position">The fixed-point world position.</param>
/// <param name="YawRadians">The fixed-point yaw in radians.</param>
public readonly record struct FixedSpawnPoint(FixedVector3 Position, FixedQ4816 YawRadians) {
    /// <summary>Compiles one authored spawn pose to deterministic numerics.</summary>
    /// <param name="point">The authored spawn point.</param>
    /// <returns>The compiled pose.</returns>
    public static FixedSpawnPoint Compile(in WorldSpawnPoint point) => new(
        Position: new FixedVector3(
            X: FixedQ4816.FromDouble(value: point.Position.X),
            Y: FixedQ4816.FromDouble(value: point.Position.Y),
            Z: FixedQ4816.FromDouble(value: point.Position.Z)
        ),
        YawRadians: FixedQ4816.FromDouble(value: (point.YawDegrees * (Math.PI / 180.0)))
    );
}

/// <summary>One data-side addon descriptor the world carries — a World-local row carrying Name/ModulePath/Hash/Fuel/
/// Enabled/Requests, with no Puck.Scripting reference. Consumed when addons mount as principals into
/// <c>Server.WorldAddonRuntime</c>.</summary>
/// <param name="Name">The addon's identifying name — unique within the definition; used by console verbs and logging.</param>
/// <param name="ModulePath">The WASM module file path (machine-local; existence/hash verification is the run path's job).</param>
/// <param name="Hash">The content-address integrity pin (<c>sha256-64/{16 hex}</c>). required — a guest whose module
/// is unpinned makes the state it touches depend on a file on disk, which is a determinism hole before it is a
/// security one.</param>
/// <param name="Fuel">The per-tick fuel budget before a deterministic halt.</param>
/// <param name="Enabled">Whether the addon starts enabled.</param>
/// <param name="Requests">The addon's manifest — what it asks for, as data (see
/// <see cref="Protocol.WorldCapabilityRequest"/>): a designation only, never authority. Deny by default holds
/// regardless of what this names, and so does the converse — this is the left half of requests ∧ grants, so a hold the
/// manifest never names materializes no handle and the guest can never reach it (see
/// <c>Server.WorldAddonRuntime</c>). Null/empty means the row asked for nothing and therefore reaches nothing.
/// Reviewed by an operator before mounting, or by the runtime's own loud mount-time line naming exactly which requested
/// pairs the settled grant table (the permissive seed plus any <see cref="WorldDefinition.Grants"/> row already
/// applied) honors for this addon's principal right now, which it withholds, and which it holds beyond the
/// manifest.</param>
/// <param name="MemoryWatches">The addon's machine-memory watch rows (the fifth event family — see
/// <see cref="WorldAddonMemoryWatch"/>): declared alongside <see cref="Requests"/>, materializing only where the
/// settled grant table also holds <c>Observe/screen:&lt;n&gt;</c> with an event budget for the watched screen (the
/// same requested ∧ granted rule every other capability here already enforces). Null/empty means no watches.</param>
public sealed record WorldAddonRow(string Name, string ModulePath, string Hash, ulong Fuel, bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldCapabilityRequest>? Requests = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAddonMemoryWatch>? MemoryWatches = null);

/// <summary>One machine-memory watch row — an addon's declaration of one byte range on one screen's machine to poll
/// for value-changed edges (the achievements-shaped primitive: works on any ROM with a known memory layout). The
/// address space is the machine's whole bus view (<see cref="Puck.Abstractions.Machines.IMachineMemoryPeek"/>
/// already covers WRAM and external/battery RAM uniformly — a single flat address, never a split
/// WRAM-vs-SRAM shape). Publishes nothing on a headless host: the peek provider is registered only when presentation
/// composes a screen's machine (see <c>Puck.World.WorldScreenBinder</c>'s registration and
/// <c>Server.WorldEventFeed</c>'s own remarks) — the retired <c>arcade.world.json</c> proof world this family was
/// built for was local play, so this is a stated, permanent scope, not a gap to close later. No shipped world
/// authors a memory-watch row today.</summary>
/// <param name="Screen">The engine screen-surface index hosting the watched machine.</param>
/// <param name="Address">The first bus address to watch.</param>
/// <param name="Length">The byte-range length, 1..8 (a watch's changed-value payload is a single zero-extended
/// <c>i64</c> lane on the wire, so a range wider than 8 bytes has nowhere to carry its value and is refused).</param>
public sealed record WorldAddonMemoryWatch(int Screen, int Address, int Length);

/// <summary>The authored layout of one on-screen binding bar. Lengths are fractions of the seat viewport's height;
/// <see cref="Scale"/> uniformly scales the slot cluster around its bottom-center anchor.</summary>
/// <param name="ButtonSize">The unscaled slot-plate size.</param>
/// <param name="CenterGap">The unscaled extra half-gap between the mirrored clusters.</param>
/// <param name="AnchorOffsetY">The anchor's lift above the viewport's bottom edge.</param>
/// <param name="GlyphOffsetRatio">The gamepad glyph's corner offset as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="GlyphSizeRatio">The gamepad glyph's size as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="Scale">The uniform cluster scale.</param>
public sealed record WorldBindingBarLayout(
    float ButtonSize,
    float CenterGap,
    float AnchorOffsetY,
    float GlyphOffsetRatio,
    float GlyphSizeRatio,
    float Scale
) {
    /// <summary>Gets the layout used when an overlay authors no binding-bar policy.</summary>
    public static WorldBindingBarLayout Default { get; } = new(
        ButtonSize: (45f / 600f),
        CenterGap: (60f / 600f),
        AnchorOffsetY: (220f / 600f),
        GlyphOffsetRatio: 0.4375f,
        GlyphSizeRatio: (24f / 45f),
        Scale: 1f
    );
}

/// <summary>The authored visibility, rest behavior, and layout of the on-screen binding bar.</summary>
/// <param name="Enabled">Whether the bar is shown when no live override hides it.</param>
/// <param name="HideAfterRestSeconds">The idle duration after which the bar hides; zero disables rest hiding.</param>
/// <param name="Layout">The bar layout; <see langword="null"/> uses <see cref="WorldBindingBarLayout.Default"/>.</param>
public sealed record WorldBindingBarAuthoring(
    bool Enabled = true,
    float HideAfterRestSeconds = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBindingBarLayout? Layout = null
) {
    /// <summary>Gets the policy that preserves the binding bar's unauthored behavior.</summary>
    public static WorldBindingBarAuthoring Default { get; } = new();

    /// <summary>Gets the resolved authored layout.</summary>
    [JsonIgnore]
    public WorldBindingBarLayout ResolvedLayout => (Layout ?? WorldBindingBarLayout.Default);
}

/// <summary>One per-world binding overlay — a whole <see cref="BindingProfileDocument"/> layered over the engine
/// default beneath every seat's profile bindings, so a world can contextualize the controls (a kart world remapping a
/// lane, an RTS world adding a chorded command page) as data, never a client fork. Merged in order; the composed result
/// (default ⊕ every overlay) is what the validator compiles.</summary>
/// <param name="Id">The overlay's stable id — its mutation address (unique within the definition; carries no meaning
/// beyond identity).</param>
/// <param name="Document">The overlay binding document merged into the composed mapping.</param>
/// <param name="BindingBar">The on-screen bar policy carried with this binding layer; <see langword="null"/> preserves
/// the always-visible reference layout.</param>
public sealed record WorldBindingOverlay(
    string Id,
    BindingProfileDocument Document,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBindingBarAuthoring? BindingBar = null
);

/// <summary>
/// The world's storage host-section defaults — the per-user cloud endpoint, an explicit user-id override, and the
/// direct-to-account discovery endpoint — authored as data so durable configuration lives in the world file (never a
/// <c>PUCK_*</c> env var; World has no such surface). An endpoint plus a resolved identity wires the owned-world sync
/// engine (<c>storage.push</c> / <c>storage.pull</c>); anything less leaves the catalog local-only. A
/// <c>--storage-uri</c> / <c>--user-id</c> / <c>--storage-discovery-uri</c> CLI reflection overrides each at boot.
/// <c>storage.status</c> echoes the resolved values.
/// </summary>
/// <param name="Endpoint">The per-user blob endpoint (a URI, e.g. <c>https://blob.byteterrace.com</c>), or
/// <see langword="null"/> for none. Validated as an absolute URI when present. Feeds
/// <c>WorldStorageSyncHandle</c>'s target construction; a URI here is edge-shaped (platform-managed containers), a
/// connection-string override (CLI-only — see the validator) is raw-shaped.</param>
/// <param name="UserId">An explicit user-id override (an Entra <c>oid</c> Guid string for a dev box or agent), or
/// <see langword="null"/> to decline identity (local-only). Fed to the identity resolver's explicit-override source.</param>
/// <param name="DiscoveryEndpoint">The direct-to-account connection container listing uses when <see cref="Endpoint"/>
/// resolves to an edge-shaped target — the platform edge cannot serve List at all (see
/// <c>AzureBlobObjectStorageTarget.DirectEndpoint</c>'s remarks), so an edge-shaped target with this
/// <see langword="null"/> refuses discovery by name instead of a request the edge cannot answer. Validated as an
/// absolute URI when present; a connection-string override (CLI-only — see the validator) is for the dev/emulator
/// shape. Ignored when <see cref="Endpoint"/> is raw-shaped (a raw target lists directly, like it reads and
/// writes).</param>
// Every member is optional and null-meaningful — see None below, which is all three absent. The explicit defaults are
// what tell the loader so, now that a parameter without one is required of the document.
public sealed record WorldStorageDefaults(string? Endpoint = null, string? UserId = null, string? DiscoveryEndpoint = null) {
    /// <summary>Gets the built-in default: no endpoint, no user-id, no discovery endpoint (cloud unwired, identity
    /// declined — local-only).</summary>
    public static WorldStorageDefaults None { get; } = new WorldStorageDefaults(Endpoint: null, UserId: null, DiscoveryEndpoint: null);
}

/// <summary>
/// World-varying editor/authoring policy values, authored as data rather than compile-time constants. Two
/// consumption classes share this one row (whole-row mutable like every other section — never split into two
/// sections for a consumption nuance that consumers already handle honestly):
/// <list type="bullet">
/// <item><description><b>Boot-consumed</b> (<see cref="AuthoringHeadroomScreens"/>,
/// <see cref="AuthoringHeadroomPlacements"/>): read exactly once, at
/// <c>Client.WorldSceneEmitter</c> construction, into the frozen render-envelope capacity floor (the probe's
/// worst-case word/instance reservation). The one honest exception: a live edit to these capacity-floor fields is
/// journaled but the running session's floor cannot retroactively grow — it applies at the next boot (the validator
/// still gates the new value against engine caps immediately, so a bad authored value never reaches a boot).</description></item>
/// <item><description><b>Live-consumed</b> (<see cref="MinPlacementScale"/>, <see cref="MaxPlacementScale"/>,
/// <see cref="CandidateRadius"/>, <see cref="CandidateCap"/>, <see cref="WorkbenchFraction"/>,
/// <see cref="PreviewDeadlineFrames"/>): read fresh from the delivered definition at each use site (a candidate
/// gather, a layout resolve, a drag-freeze tick) — a mutation takes effect at the very next tick/frame, no restart.
/// </description></item>
/// </list>
/// </summary>
/// <param name="AuthoringHeadroomScreens">Boot-consumed. The extra screen slots the probe reserves, bounded by the
/// engine's <see cref="Puck.SdfVm.SdfProgramBuilder.MaxScreenSurfaces"/> ceiling.</param>
/// <param name="AuthoringHeadroomPlacements">Boot-consumed. The placement rows of headroom the probe reserves beyond
/// the boot placements (see <c>Client.WorldPlacementStamper.StaticStampInstances</c>).</param>
/// <param name="MinPlacementScale">Live-consumed. The placement uniform-scale envelope's floor — a pure validator
/// bound, revalidated on every placement mutation.</param>
/// <param name="MaxPlacementScale">Live-consumed. The placement uniform-scale envelope's ceiling — also the worst-case
/// scale <c>Client.WorldStampPool</c>'s probe bound-radius reads (bound radius is spatial-cull metadata,
/// never a word-capacity term, so re-reading it live every build cannot desync the frozen capacity floor).</param>
/// <param name="CandidateRadius">Live-consumed. The proximity-candidate radius (world units) around a seat's editor
/// focus point — cycling never walks the whole world (the explicit candidate policy).</param>
/// <param name="CandidateCap">Live-consumed. The candidate-count cap: at most this many nearest in-radius rows enter
/// the cycle ring.</param>
/// <param name="WorkbenchFraction">Live-consumed. The full-height fraction a sole editing seat's viewport takes when
/// 2+ seats are joined (the remaining width splits as a live rail among the playing seats) — read fresh each captured
/// frame by <c>Client.WorldFrameSource.LayoutRegion(int, int, int, float)</c>.</param>
/// <param name="PreviewDeadlineFrames">Live-consumed. The drag preview channel's missing-response fallback: a
/// released overlay with no definition delivery after this many produced frames drops honestly.</param>
/// <param name="DerivedFaceScreens">Boot-consumed. The derived screen slots the binder reserves at boot for creation
/// faces (a face declared by a placement's creation, lit by a feed), registered at
/// <c>[<c>Client.WorldCreationFacets.DerivedFaceBase</c>, DerivedFaceBase + this)</c>. Bounded so the range
/// stays inside the engine screen table.</param>
public sealed record WorldAuthoringDefaults(
    int AuthoringHeadroomScreens,
    int AuthoringHeadroomPlacements,
    float MinPlacementScale,
    float MaxPlacementScale,
    float CandidateRadius,
    int CandidateCap,
    float WorkbenchFraction,
    int PreviewDeadlineFrames,
    int DerivedFaceScreens
) {
    /// <summary>Gets the built-in default authoring policy.</summary>
    public static WorldAuthoringDefaults Default { get; } = new WorldAuthoringDefaults(
        AuthoringHeadroomScreens: 4,
        AuthoringHeadroomPlacements: 8,
        MinPlacementScale: 0.2f,
        MaxPlacementScale: 5.0f,
        CandidateRadius: 32f,
        CandidateCap: 16,
        WorkbenchFraction: 0.70f,
        PreviewDeadlineFrames: 12,
        DerivedFaceScreens: 4
    );
}

/// <summary>Which graphics backend a world prefers. <see cref="Auto"/> — the default — picks the OS-appropriate backend,
/// so a shared world document is portable across an OS boundary; an explicit preference the running OS cannot satisfy
/// degrades loudly (a document author preference) or hard-exits (a CLI operator assertion) rather than silently
/// mispresenting.</summary>
public enum WorldBackendPreference : byte {
    /// <summary>Pick the OS-appropriate backend at boot — Direct3D 12 on Windows 10+, Vulkan elsewhere.</summary>
    Auto,

    /// <summary>Prefer Direct3D 12.</summary>
    DirectX,

    /// <summary>Prefer Vulkan.</summary>
    Vulkan,
}

/// <summary>
/// The world's simulation rate — how many fixed steps the authoritative server advances per second. It is simulation
/// state, unlike <see cref="WorldHostDefaults"/> (presentation-only, never simulation state): the rate is
/// simulation input (rule 4) — it is what <c>Puck.Hosting.EngineTicks.PerRate</c> turns into the exact fixed-point
/// step width every kit tuning, motion program, and physics constant is authored against, so two worlds authoring
/// different rates are two different, equally deterministic simulations, never a presentation preference.
/// </summary>
/// <param name="RateHz">The simulation rate in Hz. Zero is a legal, distinct rate: a resident, non-stepping
/// world — a static diorama the authoritative server never advances a fixed step for, though it still applies
/// ordered submissions (mutations, session requests, connects/disconnects) through the administrative drain, so a
/// rate-0 world can accept the very write that revives it. At rate 0, a simulation-tick duration authored as a
/// positive value means never — not zero and not "already expired" — since there is no tick mapping for a world
/// that never advances (see <see cref="CompiledTickDuration"/>, <see cref="WorldDefinition.PopulationReconnectGraceTicks"/>).
/// A positive rate must be a divisor of <see cref="Puck.Maths.FixedTickConversion.TicksPerSecond"/> (50400)
/// exactly, so <c>Puck.Hosting.EngineTicks.PerRate</c> always derives a whole engine-tick step width — never
/// truncated, never remainder-carried (<see cref="WorldDefinitionValidator"/> refuses a non-divisor, naming the
/// nearest valid rates; a negative rate is refused outright, at any magnitude). 45 and 90 Hz — Steam Deck OLED's
/// two refresh rates — both divide 50400 exactly (1120 and 560 engine ticks per step). Defaults to
/// <see cref="DefaultRateHz"/> (240), the fixed rate every world ran at before this section existed, so a world
/// authoring no <c>simulation</c> section boots byte-identically to before.
/// <para><b>The derived-floor seam.</b> This record is deliberately the one place a follow-on validation pass adds
/// the physics floor (from body size/speed), the interactivity floor (from input latency), the substep-derived
/// contact clamp (<c>contactHertz &lt;= RateHz * n / 8</c> at substep count <c>n</c> — it coincides with
/// <c>RateHz / 4</c> only at <c>n</c> = 2), and the representable band — none of which is built yet. The clamp's
/// <c>n</c> is a solver parameter, so its validator arrives with the solver landing that introduces it. A derived
/// floor belongs here, beside the rate it constrains, never as a second section.</para></param>
public sealed record WorldSimulationDefaults(
    int RateHz = WorldSimulationDefaults.DefaultRateHz
) {
    /// <summary>The simulation rate every world ran at before this section existed (Hz) — the fallback
    /// <see cref="WorldDefinition.SimulationRateHz"/> uses for a world authoring no <see cref="WorldDefinition.Simulation"/>
    /// section.</summary>
    public const int DefaultRateHz = 240;
}

/// <summary>
/// How the world boots its presentation shell — the closed vocabulary <see cref="WorldHostDefaults.Presentation"/> and
/// the <c>--headless</c> CLI reflection resolve to (see <c>Puck.World.WorldHostSettings.Headless</c>). Deciding this
/// before any other registration is the boot-shape split's own precondition: <see cref="None"/> composes
/// <c>AddWorldAuthoritativeCore</c> alone (no GPU device, no swapchain, no window), <see cref="Windowed"/> composes it
/// plus <c>AddWorldPresentation</c>.
/// </summary>
[JsonConverter(typeof(StrictEnumConverter<WorldHostPresentation>))]
public enum WorldHostPresentation : byte {
    /// <summary>Boot a native window, GPU device, and swapchain — World's original, still-default shape.</summary>
    Windowed,

    /// <summary>Boot the authoritative server, console, and tape only — no window, no GPU device, no swapchain, no
    /// audio device. Every presentation-only console verb (<c>world.fps</c>/<c>.gpu</c>/<c>render*</c>/<c>view*</c>/
    /// <c>.screenshot</c>, <c>screen.*</c>, audio, editor) refuses as unknown — the honest reflection of the composed
    /// set, not a special-cased denial.</summary>
    None,
}

/// <summary>
/// The world's host defaults — how the world asks to be presented, independent of what it contains. presentation-only
/// throughout (never simulation state). Two consumption classes share this one row, named per field:
/// <list type="bullet">
/// <item><description><b>boot-only</b> (<see cref="Presentation"/>, <see cref="Backend"/>, <see cref="Width"/>,
/// <see cref="Height"/>, <see cref="SurfaceFormat"/>, <see cref="Fullscreen"/>, <see cref="PresentMode"/>,
/// <see cref="ExitAfterSeconds"/>, <see cref="RayQuery"/>, <see cref="Genlock"/>): read once at composition; a live
/// edit is journaled and validated immediately but takes effect next boot.</description></item>
/// <item><description><b>Boot-default with a live lever</b> (<see cref="TargetHertz"/> via <c>world.target</c>,
/// <see cref="Timing"/> via <c>world.timing</c>): the value the session wakes on; <c>Puck.World.WorldSessionCapture</c>
/// folds the live values back at <c>world.save</c>.</description></item>
/// </list>
/// <see cref="Default"/> reproduces World's current boot exactly.
/// </summary>
/// <param name="Presentation">Which boot shape the world composes — see <see cref="WorldHostPresentation"/>. Defaults
/// to <see cref="WorldHostPresentation.Windowed"/>, so every world authored before this field existed boots
/// byte-identically; the <c>--headless</c> CLI flag reflects <see cref="WorldHostPresentation.None"/> for a single run
/// without editing the document.</param>
/// <param name="Backend">The preferred graphics backend (<see cref="WorldBackendPreference.Auto"/> is OS-portable), or
/// <see langword="null"/> when <paramref name="BackendDraw"/> draws it — omitting both reads as
/// <see cref="WorldBackendPreference.Auto"/>.</param>
/// <param name="BackendDraw">The backend choice's authored-randomness facet, or <see langword="null"/> for an ordinary
/// literal <paramref name="Backend"/>. A boot-only site (<see cref="WorldDrawSites.HostBackend"/>): the resolver draws
/// it once at composition, writes the settled preference into <paramref name="Backend"/>, clears this facet, and
/// narrates the settlement on stderr — the only surface that can say the backend was drawn at all, since a settled
/// field is indistinguishable from an authored one thereafter.
/// <para>Its natural spelling is a weighted text source over the backend tokens (<c>auto</c>/<c>directx</c>/
/// <c>vulkan</c> — a one-context Markov table with <c>bound</c> 1, the degenerate flat weighted draw), parsed through
/// <see cref="WorldHostTokens.ParseBackend"/> at settle. A token naming no backend refuses by name. Drawing the name
/// rather than an ordinal is deliberate: an ordinal draw over an enum silently re-points itself the day a member is
/// inserted, and reads at the authoring site as a number nothing explains.</para>
/// <para>Declared together with <paramref name="Backend"/> it is refused by name — this record is a class, so
/// presence is honestly observable here, unlike <see cref="WorldPopulationDefaults.CapacityDraw"/>'s struct-typed
/// site.</para></param>
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
/// <c>off</c>. Shape-only validation (null or non-whitespace); the registry, not the validator, interprets the id.</param>
/// <param name="Listen">The TCP listen endpoint (<c>host:port</c>) the authoritative host binds for remote peer
/// admission, or <see langword="null"/> to stay loopback-only (no socket ever opens). Durable configuration per the
/// unification contract — the <c>--listen</c> CLI flag reflects it for a single run without editing the document.
/// Shape-only validation (null or a non-whitespace <c>host:port</c> pair); <c>Server.WorldTcpHost</c> is what actually
/// parses and binds it.</param>
/// <param name="Authority">The TCP endpoint at which this world's authority is reached when another world resolves
/// it as a destination, or <see langword="null"/> when the authority is colocated with the resolver. Colocation
/// short-circuits the authority transport; it does not select a separate transfer path.</param>
public sealed record WorldHostDefaults(
    WorldHostPresentation Presentation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBackendPreference? Backend,
    int Width,
    int Height,
    SurfaceFormat SurfaceFormat,
    bool Fullscreen,
    PresentMode PresentMode,
    double TargetHertz,
    int ExitAfterSeconds,
    bool RayQuery,
    bool Timing,
    string? Genlock,
    string? Listen,
    string? Authority = null,
    // OPTIONAL — the authored-randomness facet over Backend above (see the param docs). XOR-BY-PRESENCE against it:
    // WorldHostDefaults is a CLASS, so a null Backend is honestly distinguishable from an authored one and declaring
    // both is refused BY NAME. (WorldPopulationDefaults.CapacityDraw's site cannot do this — see its own remarks.)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldDraw? BackendDraw = null
) {
    /// <summary>Gets the built-in host defaults — reproducing World's current hardcoded boot exactly (windowed, 1280×800,
    /// auto backend, immediate present, automatic display pacing, R8G8B8A8 surface, ray-query on, timing off, no
    /// auto-exit, no listener).</summary>
    public static WorldHostDefaults Default { get; } = new WorldHostDefaults(
        Presentation: WorldHostPresentation.Windowed,
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
        Genlock: null,
        Listen: null,
        Authority: null
    );
}

/// <summary>
/// The definition of this world — the aggregate describing what the world is, distinct from the live session state that
/// plays in it. It gathers named spawn points (<see cref="SpawnPoints"/>), motion defaults (<see cref="Motion"/>), and
/// render-lever defaults and quality presets (<see cref="Render"/>). Every consumer takes it by construction.
/// </summary>
/// <remarks>These serialization-friendly records are populated from world documents.</remarks>
/// <param name="Motion">The profileless locomotion speeds (see <see cref="WorldMotionDefaults"/>). Jump feel is per-kit.</param>
/// <param name="SpawnPoints">The named spawn poses seats and population policies reference.</param>
/// <param name="Render">The render-lever boot defaults and quality-preset table.</param>
/// <param name="Screens">The diegetic screens standing in the plaza — pure data the frame source emits as screen
/// slabs and the binder feeds; a screen the world never authors, only declares.</param>
/// <param name="Cameras">The placeable cameras a <see cref="WorldScreenSource.View"/> screen renders the world from
/// (the jumbotron recursion) — pure data the binder resolves View screens against at wiring.</param>
/// <param name="Population">The local/network census active from the first built-in scene frame.</param>
/// <param name="PlayerDefaults">The authored player-profile seed palette and picker tuning.</param>
/// <param name="Channels">The world's channel table (see <see cref="WorldChannel"/>) — the intent vector's declared
/// vocabulary every binding destination, <c>player.press</c>, kit <see cref="WorldKit.Actions"/> entry, and the addon
/// wire resolve channel names against.</param>
/// <param name="TargetRegisters">The named per-body target registers and their designation envelopes.</param>
/// <param name="BodyMotionPrograms">The versioned fixed-phase body motion programs kits select by name.</param>
/// <param name="Kits">The world's locomotion kits — one row per way of moving (see <see cref="WorldKit"/>); the
/// <see cref="Assignment"/> policy distributes entities across the rows.</param>
/// <param name="DefaultSeatKit">The kit row (by name) every seat body constructs from.</param>
/// <param name="Assignment">The kit→entity assignment policy (the realized policy-as-data seam).</param>
/// <param name="Addons">The data-side addon descriptors (default empty), consumed when addons mount as
/// principals.</param>
/// <param name="BindingOverlays">The per-world binding overlays (default empty) layered over the engine default beneath
/// each seat's profile bindings.</param>
/// <param name="Storage">The storage host-section defaults — the per-user cloud endpoint and explicit
/// user-id override, authored as data.</param>
/// <param name="Creations">The creation asset rows (default empty) — whole <c>puck.creation.v1</c> documents
/// embedded inline-canonical with their identity hashes pinned (see <see cref="WorldCreation"/>).</param>
/// <param name="Placements">The placement instance rows (default empty) — creations stamped by reference (see
/// <see cref="WorldPlacement"/>).</param>
/// <param name="Authoring">The editor/authoring policy row — headroom, placement
/// scale envelope, candidate targeting, the sole-editor layout split, and the drag-preview deadline, authored as data
/// (see <see cref="WorldAuthoringDefaults"/>) — a required section every document carries.</param>
/// <param name="Speakers">The placeable speaker rows (default empty) — the camera family's audio sibling (see
/// <see cref="WorldSpeaker"/>): name-keyed transducers whose feeds tap shared sources.</param>
/// <param name="Tunes">The tune asset rows (default empty) — whole <c>puck.audio.v1</c> documents embedded
/// inline-canonical with pinned hashes (see <see cref="WorldTune"/>).</param>
/// <param name="Patches">The synth-patch asset rows (default empty) — whole <c>puck.synth.v1</c> documents embedded
/// inline-canonical with pinned hashes (see <see cref="WorldPatch"/>).</param>
/// <param name="Audio">The audio host-section defaults (master gain, point-attenuation coalescing, bed fade, the
/// listener policy — see <see cref="WorldAudioDefaults"/>) — a required section every document carries.</param>
/// <param name="Collision">The contact-solver tuning (see <see cref="WorldCollision"/>) — it affects the simulation.</param>
/// <param name="Host">The host-section defaults — how the world asks to be presented (window/backend/present/pacing/
/// timing/genlock — see <see cref="WorldHostDefaults"/>). The CLI window/backend flags override it at boot (a
/// deployment surface laid over the author's intent).</param>
/// <param name="Views">The window-composition defaults — the seat framing every seat wakes on plus the authored named
/// layouts (see <see cref="WorldViewDefaults"/>). An empty layout list falls the composer through to the built-in seat
/// ladder.</param>
/// <param name="Looks">The look rows (default empty) — authored appearances the population wears, the peer of
/// <see cref="Kits"/> (see <see cref="WorldLook"/>). Empty resolves every entity to the implicit single catalog look.</param>
/// <param name="LookAssignment">The look→entity assignment policy, the same <see cref="WorldRowAssignment"/> primitive
/// <see cref="Assignment"/> uses for kits.</param>
/// <param name="Links">The cable-link rows (default empty) — groups of screens whose machines advance as one
/// interleaved unit (see <see cref="WorldScreenLink"/>).</param>
/// <param name="Grants">The document-authored grant rows (default empty) — capability holds a world ships with,
/// reviewable here rather than only typed at a console (see <see cref="Protocol.WorldGrant"/>). Applied at boot, in
/// order, through the same <c>Server.WorldServer.Grant</c> path <c>world.grant</c> submits through, on top of
/// the permissive seed — never in place of it. Empty (the default) is byte-identical to every world authored before
/// this section existed: nothing here changes boot behavior unless a row is actually added.</param>
/// <param name="Hud">The <c>hud</c> section — the world-scope HUD panel rows plus their defaults (see
/// <see cref="WorldHudSection"/>). presentation-only: overlay geometry and bindings, never simulation state. Defaults
/// to <see cref="WorldHudSection.Default"/> (enabled, no authored panels) — byte-identical boot behavior for every
/// world authored before this section existed.</param>
/// <param name="State">The <c>state</c> section (default empty) — genre-neutral named cells (see
/// <see cref="WorldStateRow"/>): score, rounds, inventory, flags, or a keyed table (a slot is a table with one key —
/// the primitive threat tables and the signed-carriage bearer high-water mark both want). It is simulation state: every
/// row's whole shape mutates only through
/// <see cref="Protocol.WorldMutation.UpsertStateRow"/>/<see cref="Protocol.WorldMutation.RemoveStateRow"/>, or — for
/// a per-cell write only —
/// <see cref="Protocol.WorldMutation.UpsertStateCell"/>/<see cref="Protocol.WorldMutation.RemoveStateCell"/>; the
/// same journaled/undoable/saved pipeline every other section rides. A genre world is different data here, never a
/// new message shape or an engine-interpreted name.</param>
/// <param name="InputHold">The simulation-affecting participant input-hold policy, in its authored shape — every
/// checked-in world authors the section explicitly, in seconds (<c>ceilingSeconds</c>/<c>lowerAfterSeconds</c>/
/// <c>defaultSeconds</c>, and each participant's own <c>seconds</c>). <see cref="CompiledInputHold"/> is the compiled
/// simulation-tick form (<see cref="WorldInputHoldSettings"/>) <c>Server.WorldInputHoldRuntime</c> actually consumes —
/// compiled lazily off this document's own <see cref="SimulationRateHz"/> rather than at parse, since a document's rate
/// is just another sibling section with no parse-order guarantee ahead of this one (see <see cref="SimulationRateHz"/>'s
/// remarks). Measured tick counts arrive on tick-stamped intent submissions and never read a clock here.</param>
/// <param name="Rules">The <c>rules</c> section (default null = none) — world-scoped rules, the same
/// <see cref="ActionPredicate"/>/<see cref="ActionEffect"/>/<see cref="ActionTriggerMode"/> primitive a kit's
/// per-body actions already use, widened one level up (see <see cref="WorldRule"/>). Optional, deliberately: a new
/// required section would refuse every existing document at boot for declaring nothing.</param>
/// <param name="Identity">The identity carried when this is an owned world.</param>
/// <param name="Groups">The <c>groups</c> section (default null = none) — the group+membership binding substrate:
/// the group-kind policy catalog and the group roster (see <see cref="WorldGroupsSection"/>). Optional, for the
/// same reason <see cref="Rules"/> is: a new required section would refuse every existing document at boot for
/// declaring nothing.</param>
/// <param name="Properties">The <c>properties</c> section (default null = none) — the carrier-property name
/// vocabulary (see <see cref="WorldPropertyRegistrySection"/>), validated the same way a group kind name is: unknown-
/// by-name. Optional, for the same reason <see cref="Rules"/> is.</param>
/// <param name="Interactions">The <c>interactions</c> section (default null = none) — the generalized
/// <c>property x property</c> (or <c>property x region</c>) <c>-&gt; effect</c> table (see
/// <see cref="WorldInteractionsSection"/>), which lowers to the same rule substrate <see cref="Rules"/> evaluates
/// rather than a second engine. Optional, for the same reason <see cref="Rules"/> is.</param>
/// <param name="Generation">The <c>generation</c> section (default null = <see cref="WorldGenerationDefaults.Default"/>,
/// world seed 0) — the draw seed ladder's world rung (see <see cref="WorldGeneratorEngine.ComputeSeedState"/>).
/// Optional, for the same reason <see cref="Rules"/> is.</param>
/// <param name="Generators">The <c>generators</c> section (default empty) — stochastic sources declared under a name
/// (see <see cref="WorldGeneratorRow"/>) for any number of <see cref="WorldDraw"/> sites to reference. A source is a
/// pure declaration and holds no position: the cursor and dealt decks live on each drawing site, which is what lets
/// two sites share one table and still draw independently. Optional, for the same reason <see cref="Rules"/> is.</param>
/// <param name="Water">The <c>water</c> section (default null = a dry world) — the world's standing-water medium
/// (see <see cref="WorldWaterSection"/>): one authored waterline, echoed by <c>world.status</c> and read by the swim
/// motion model's stages. Optional, for the same reason <see cref="Rules"/> is.</param>
/// <param name="References">The <c>references</c> section (default null = names nothing) — rows naming another
/// world by document path (see <see cref="WorldReference"/>), echoed by <c>world.references</c>. No boot-time
/// file-existence check; resolution is a consumer's job. Optional, for the same reason <see cref="Rules"/> is.</param>
/// <param name="Portals">The <c>portals</c> section (default null = every portal facet falls back to
/// <see cref="WorldPortalTravel.Body"/>) — the world-scope travel default a placement face's
/// <see cref="WorldPlacementPortal"/> facet resolves against when it authors none of its own (see
/// <see cref="WorldPortalsSection"/>), echoed by <c>world.portals</c>. Optional, for the same reason
/// <see cref="Rules"/> is; slotted immediately after <see cref="References"/> — the two complete the world-topology
/// cluster a portal composes from.</param>
/// <param name="Simulation">The <c>simulation</c> section (default null = <see cref="WorldSimulationDefaults.DefaultRateHz"/>,
/// 240 Hz — see <see cref="SimulationRateHz"/>) — the authoritative server's fixed step rate. Optional, for the same
/// reason <see cref="Rules"/> is: a required section would refuse every world checked in before it existed. Echoed by
/// <c>world.status</c>'s <c>rate</c> field. boot-only: no <see cref="Protocol.WorldSection"/> axis and no
/// <c>MutationKind</c>, exactly like <see cref="References"/>/<see cref="Portals"/> — nothing mutates it live.</param>
/// <param name="Destinations">The <c>destinations</c> section (default null = names nothing) — scoped-selection rows
/// layered over exactly one <see cref="References"/> row each (see <see cref="WorldDestination"/>), echoed by
/// <c>world.destinations</c>. A <see cref="WorldPlacementPortal"/> facet's <see cref="WorldPlacementPortal.Destination"/>
/// resolves against this section, not <see cref="References"/> directly. No boot-time file-existence check; resolution
/// is a consumer's job. Optional, for the same reason <see cref="Rules"/> is. boot-only like <see cref="References"/>/
/// <see cref="Portals"/> — no live mutation arm yet.</param>
/// <param name="Admission">The <c>admission</c> section (default null = admits no remote peer) — the durable
/// vocabulary of which identities/issuers this world's TCP socket admits (see <see cref="WorldAdmissionEntry"/>),
/// and what each is minted once verified. <see cref="WorldAdmissionDoor"/> is the one consumer, at the Hello
/// handshake, off the tick thread — this replaces the game socket's former blanket "admit as Control/all" wire
/// admission (docs/world-model.md's "Authenticating the game wire" row) with a verified-identity-to-principal
/// mapping. boot-only like <see cref="References"/>/<see cref="Portals"/>/<see cref="Destinations"/> — no live
/// mutation arm. A trailing addition over the section set shipped before it, never reordered among it — the same
/// trailing convention <see cref="Market"/> follows after it.</param>
/// <param name="Market">The <c>market</c> section (default null = no local auction house — every <c>market.*</c>
/// verb refuses by name) — see <see cref="WorldMarketSection"/>. Optional, for the same reason <see cref="Rules"/>
/// is. Trailing by design, after <see cref="Admission"/>, for the identical reason every optional section here
/// is.</param>
/// <param name="Adjacencies">Invisible, reciprocal authority boundaries. Optional; a world declaring none has no
/// seamless neighbours. The compiler derives overlap from both delivered documents rather than accepting an
/// authored safety margin.</param>
public sealed record WorldDefinition(
    WorldMotionDefaults Motion,
    IReadOnlyList<WorldSpawnPoint> SpawnPoints,
    WorldRenderDefaults Render,
    IReadOnlyList<WorldScreen> Screens,
    IReadOnlyList<WorldCamera> Cameras,
    WorldPopulationDefaults Population,
    WorldPlayerDefaults PlayerDefaults,
    IReadOnlyList<WorldChannel> Channels,
    IReadOnlyList<WorldTargetRegister> TargetRegisters,
    IReadOnlyList<BodyMotionProgram> BodyMotionPrograms,
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
    // An empty Looks list resolves every entity to WorldLook.Implicit (the occupant-owned catalog pick at full gait);
    // NO branch special-cases "the author authored none".
    IReadOnlyList<WorldLook> Looks,
    WorldRowAssignment LookAssignment,
    IReadOnlyList<WorldScreenLink> Links,
    IReadOnlyList<WorldGrant> Grants,
    WorldHudSection Hud,
    IReadOnlyList<WorldStateRow> State,
    WorldInputHoldAuthoring InputHold,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldRule>? Rules = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldIdentityDefinition? Identity = null,
    // OPTIONAL, exactly like Rules above: a required section would refuse every existing world at boot for
    // declaring nothing. A composer reads `current.Groups ?? WorldGroupsSection.Empty`, the identical
    // `current.Rules ?? []` fallback Rules' own composer arms use.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGroupsSection? Groups = null,
    // OPTIONAL, exactly like Groups above — same fallback shape (`current.Properties ?? WorldPropertyRegistrySection.Empty`).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPropertyRegistrySection? Properties = null,
    // OPTIONAL, exactly like Properties above (`current.Interactions ?? WorldInteractionsSection.Empty`).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldInteractionsSection? Interactions = null,
    // OPTIONAL, exactly like Interactions above (`current.Generation?.WorldSeed ?? 0UL`).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGenerationDefaults? Generation = null,
    // OPTIONAL, exactly like Generation above (`current.Generators ?? []`).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGeneratorRow>? Generators = null,
    // OPTIONAL, exactly like Generators above — a null section IS the dry world, no fallback object needed.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldWaterSection? Water = null,
    // OPTIONAL, exactly like Water above — a null section names nothing, no fallback list needed.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldReference>? References = null,
    // OPTIONAL, exactly like References above — a null section resolves every portal facet's absent travel to
    // WorldPortalTravel.Body, no fallback object needed.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPortalsSection? Portals = null,
    // OPTIONAL, exactly like Portals above — a world authoring none reads WorldSimulationDefaults.DefaultRateHz
    // (240 Hz) through SimulationRateHz below, the fixed rate every world ran at before this section existed, so
    // nothing already checked in needs an edit to keep its exact byte-for-byte boot behavior.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSimulationDefaults? Simulation = null,
    // OPTIONAL, exactly like Simulation above — a null section names no destinations. Trailing by design: added
    // over the shipped section set rather than inserted beside References/Portals, so every existing document's
    // member ORDER (irrelevant to JSON parsing, but relevant to anyone diffing a document by eye) stays untouched.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldDestination>? Destinations = null,
    // OPTIONAL, exactly like Destinations above — a null section names no admission entries, which is DENY BY
    // DEFAULT for the TCP door: no remote peer can ever verify against an absent/empty section, matching an empty
    // Puck.Carriage.TrustList's own posture. Trailing by design, for the identical reason Destinations is.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAdmissionEntry>? Admission = null,
    // OPTIONAL, exactly like Admission above — a null section IS today's no-market behavior, no fallback object
    // needed beyond `current.Market ?? WorldMarketSection.Empty`. Trailing by design, for the identical reason
    // every optional section above it is.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMarketSection? Market = null,
    // OPTIONAL topology. An adjacency is an invisible authority boundary, never a portal or screen facet.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAdjacency>? Adjacencies = null
) {
    /// <summary>The document schema version. A loader rejects any other value; the canonical writer always emits it.</summary>
    public const string SchemaVersion = "puck.world.def.v1";

    /// <summary>Gets the stable document id used when this world submits to another document.</summary>
    public string? DocumentId { get; init; }

    /// <summary>Gets the document schema tag — <see cref="SchemaVersion"/> for a well-formed document.</summary>
    public string Schema { get; init; } = SchemaVersion;

    /// <summary>Gets the unknown top-level members captured during deserialization, declared identically on every versioned
    /// document root here and validated
    /// through the shared <see cref="DocumentExtensionsPolicy"/> regime (see <see cref="WorldDefinitionValidator"/>): a
    /// reserved-prefix key ('$' schema-like keys, '_' comments) round-trips as an intentional escape hatch, but any
    /// other unrecognized key is a hard load failure — not a passive round-trip bag — because an unknown section
    /// surviving silently is how authoring drift starts. Null when the document carries no unknown members. A
    /// settable (not <c>init</c>) accessor is required: System.Text.Json appends to it during deserialization.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Gets the effective simulation rate in Hz — <see cref="Simulation"/>'s authored
    /// <see cref="WorldSimulationDefaults.RateHz"/>, or <see cref="WorldSimulationDefaults.DefaultRateHz"/> (240) when
    /// this world authors no <see cref="Simulation"/> section. The seam every simulation-tick-scoped duration on this
    /// document compiles through (see <see cref="PopulationReconnectGraceTicks"/>, <see cref="CompiledInputHold"/>):
    /// computed here, on the fully-parsed aggregate, rather than threaded as a parameter to each sub-section's own
    /// converter, because a sub-section (e.g. <see cref="WorldPopulationDefaults"/>, a struct) has no reference back to
    /// the document that carries both it and the rate, and the rate itself is just another sibling property in the same
    /// JSON object being parsed — there is no ordering guarantee that would let a nested converter see it first. A
    /// caller that already holds a <see cref="WorldDefinition"/> reads this property directly; nothing threads a raw
    /// rate parameter by hand.</summary>
    [JsonIgnore]
    public int SimulationRateHz => (Simulation?.RateHz ?? WorldSimulationDefaults.DefaultRateHz);

    /// <summary>Gets the compiled form of <see cref="WorldPopulationDefaults.ReconnectGraceSeconds"/> — a
    /// <see cref="CompiledTickDuration"/>, the unit <c>Server.WorldPopulation</c> actually consumes. Not a raw tick
    /// count: at <see cref="SimulationRateHz"/> 0 a positive authored grace has no tick mapping at all
    /// (<see cref="CompiledTickDuration.Never"/> — a disconnected body parks forever rather than tearing down
    /// immediately), which a raw <see langword="int"/> could not distinguish from an authored-disabled zero grace
    /// (<see cref="CompiledTickDuration.IsZero"/>, the immediate-teardown case, unaffected by the rate). Lives here
    /// rather than on <see cref="WorldPopulationDefaults"/> itself because compiling a duration needs
    /// <see cref="SimulationRateHz"/>, which only the whole document can supply — see
    /// <see cref="SimulationRateHz"/>'s remarks. Read once at construction/rebuild, like the rest of
    /// <see cref="Population"/> — a live edit takes effect on the next disconnect, never retroactively on an
    /// already-parked body.</summary>
    [JsonIgnore]
    public CompiledTickDuration PopulationReconnectGraceTicks => WorldSimulationTickConversion.CompiledDuration(seconds: Population.ReconnectGraceSeconds, ratePerSecond: (uint)SimulationRateHz);

    /// <summary>Gets the compiled form of <see cref="InputHold"/> — every <c>*Ticks</c> field in simulation ticks, the
    /// unit <c>Server.WorldInputHoldRuntime</c> actually consumes. <see cref="InputHold"/> itself stays the authored
    /// seconds shape (see its remarks); this compiles it once through <see cref="SimulationRateHz"/>, for the identical
    /// reason <see cref="PopulationReconnectGraceTicks"/> does.</summary>
    [JsonIgnore]
    public WorldInputHoldSettings CompiledInputHold => InputHold.Compile(ratePerSecond: (uint)SimulationRateHz);

}
