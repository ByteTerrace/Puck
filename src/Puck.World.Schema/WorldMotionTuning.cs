using System.Text.Json.Serialization;
using Puck.Maths;
using Puck.Physics.Motion;

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
    /// <param name="SprintMultiplier">The held-sprint speed multiplier, applied while
    /// <paramref name="SprintChannel"/> reads held; <c>1</c> is a no-op.</param>
    /// <param name="Response">The velocity-response table (see <see cref="MotionResponse"/>) planar velocity converges
    /// through, or <see langword="null"/> (the default) when <paramref name="Dynamics"/> shapes it instead — exactly
    /// one of the two is authored. The empty table snaps planar velocity instantly; <see cref="DeclaredResponse"/> is
    /// the null-coalesced read every caller uses.</param>
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
    public sealed record Grounded(
        float MoveSpeed,
        float TurnSpeed,
        float RiseGravity,
        float FallGravity,
        float MaxFallSpeed,
        float SprintMultiplier,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MotionResponse>? Response = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Dynamics = null,
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
    /// <param name="Buoyancy">The medium's idle vertical drift velocity (u/s, signed) below the bob band: positive
    /// drifts the body up toward its float line, negative sinks, zero holds depth. Folded into the commanded thrust
    /// target — see <see cref="FixedSwimTuning"/> — never applied as a separate acceleration.</param>
    /// <param name="MaxRiseSpeed">The terminal ascent speed (u/s) the vertical channel is clamped to.</param>
    /// <param name="MaxSinkSpeed">The terminal descent speed (u/s) the vertical channel is clamped to.</param>
    /// <param name="SurfaceSettleRate">The proportional settle gain (1/s) toward the float line, applied inside the
    /// bob band and above it (breach recovery): the medium's target velocity there is the displacement from the
    /// line times this gain, so a held ascent parks where thrust and settle balance instead of breaching.</param>
    /// <param name="FloatDepth">How far below the medium surface (world units) the body origin rests when floating — and
    /// the bob band's half-width around that rest line (one knob deliberately: the band a body settles in is the
    /// depth scale it settles at). The validator only checks this is positive and finite — it cannot see the
    /// document's contact geometry, so it does not (and cannot) check this against the local water column's depth. A
    /// <see cref="FloatDepth"/> deeper than the floor below it parks the body on the floor instead of at the float
    /// line, with <c>AtSurface</c> never true there — a geometry fact deliberately outside this validator's
    /// reach.</param>
    /// <param name="SprintMultiplier">The held-burst speed multiplier applied while <paramref name="SprintChannel"/>
    /// reads held; <c>1</c> is a no-op. Scales the whole thrust vector, vertical included.</param>
    /// <param name="Response">The velocity-response table (see <see cref="MotionResponse"/>) — read for both the
    /// planar and the vertical convergence — or <see langword="null"/> (the default) when <paramref name="Dynamics"/>
    /// shapes both instead; exactly one of the two is authored. The empty table snaps instantly (no drag, no coast);
    /// a water feel wants at least an always-row. <see cref="DeclaredResponse"/> is the null-coalesced read every
    /// caller uses.</param>
    /// <param name="Dynamics">The <c>dynamics</c> row a second-order follower shapes both the planar and the vertical
    /// convergence through instead of <paramref name="Response"/> — the same seam as
    /// <see cref="Grounded.Dynamics"/> — or <see langword="null"/> (the default) for the response table. Exactly one
    /// of the two is authored.</param>
    /// <param name="SprintChannel">The declared channel name read while held for the burst, or <see langword="null"/>
    /// (the default) for a kit with no burst — the same resolution path <see cref="Grounded.SprintChannel"/>
    /// documents.</param>
    /// <param name="MoveFrame">Which frame <c>MoveAdvance</c>/<c>MoveStrafe</c> resolve in — the same two-frame
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
        float Buoyancy,
        float MaxRiseSpeed,
        float MaxSinkSpeed,
        float SurfaceSettleRate,
        float FloatDepth,
        float SprintMultiplier,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MotionResponse>? Response = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Dynamics = null,
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
    /// <summary>The declared Turn-channel rate of whichever arm this is, in radians per second at full deflection —
    /// zero for an arm without one. The client's steer follow keys off this, arm-agnostically.</summary>
    public float DeclaredTurnSpeed => this switch {
        Grounded grounded => grounded.TurnSpeed,
        Swim swim => swim.TurnSpeed,
        _ => 0f,
    };
    /// <summary>The declared velocity-response table of whichever arm this is (see <see cref="Grounded.Response"/>/
    /// <see cref="Swim.Response"/>), null-coalesced to the empty table — a kit's authored <see langword="null"/> and a
    /// kit with no planar-shaping arm at all read identically here.</summary>
    public IReadOnlyList<MotionResponse> DeclaredResponse => ((this switch {
        Grounded grounded => grounded.Response,
        Swim swim => swim.Response,
        _ => null,
    }) ?? []);
    /// <summary>The declared <c>dynamics</c> row name of whichever arm this is (see <see cref="Grounded.Dynamics"/>/
    /// <see cref="Swim.Dynamics"/>), or <see langword="null"/> for an arm shaped by <see cref="DeclaredResponse"/>
    /// instead, or with no planar-shaping arm at all.</summary>
    public string? DeclaredDynamics => this switch {
        Grounded grounded => grounded.Dynamics,
        Swim swim => swim.Dynamics,
        _ => null,
    };
}
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
/// <summary>The document intake for the engine's compiled motion tunings — the one place an authored
/// <see cref="WorldMotionModel"/> arm becomes the fixed-point form simulation reads.</summary>
public static class WorldMotionTuningFactory {
    private static FixedMotionTuning Compile(float moveSpeed, float turnSpeed, float riseGravity, float fallGravity, float maxFallSpeed, IReadOnlyList<MotionResponse> response, float sprintMultiplier, MotionMoveFrame moveFrame, bool facingSnap, MotionScalarEnvelope? moveSpeedEnvelope, FixedMotionDynamics? dynamics) {
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
            RiseGravity: FixedQ4816.FromDouble(value: riseGravity),
            FallGravity: FixedQ4816.FromDouble(value: fallGravity),
            MaxFallSpeed: FixedQ4816.FromDouble(value: maxFallSpeed),
            Response: compiled,
            ResponseRecencyFacts: recencyFacts.ToArray(),
            ResponseRecencyWindows: recencyWindows.ToArray(),
            SprintMultiplier: FixedQ4816.FromDouble(value: sprintMultiplier),
            MoveFrame: moveFrame,
            FacingSnap: facingSnap,
            MoveSpeedEnvelope: ((moveSpeedEnvelope is { } envelope)
            ? Compile(envelope: envelope)
            : null),
            PlanarDynamics: dynamics
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
    /// <summary>Compiles an authored grounded motion row to its fixed-point form.</summary>
    /// <param name="tuning">The authored grounded arm.</param>
    /// <param name="dynamics">The compiled <c>dynamics</c>-row follower <paramref name="tuning"/> names, or
    /// <see langword="null"/> when it shapes planar velocity through <see cref="WorldMotionModel.Grounded.Response"/>
    /// instead.</param>
    /// <returns>The compiled tuning.</returns>
    public static FixedMotionTuning Compile(WorldMotionModel.Grounded tuning, FixedMotionDynamics? dynamics = null) => Compile(
        moveSpeed: tuning.MoveSpeed,
        turnSpeed: tuning.TurnSpeed,
        riseGravity: tuning.RiseGravity,
        fallGravity: tuning.FallGravity,
        maxFallSpeed: tuning.MaxFallSpeed,
        response: tuning.DeclaredResponse,
        sprintMultiplier: tuning.SprintMultiplier,
        moveFrame: tuning.MoveFrame,
        facingSnap: tuning.FacingSnap,
        moveSpeedEnvelope: tuning.MoveSpeedEnvelope,
        dynamics: dynamics
    );
    /// <summary>Compiles an authored swim motion row's shared half — speeds, response table, sprint, frame — to the
    /// same fixed-point form every model rides (the gravity fields compile to zero; the swim program's facet
    /// coherence already refused any op that would read them). The swim-specific half is
    /// <see cref="CompileSwim"/>.</summary>
    /// <param name="tuning">The authored swim arm.</param>
    /// <param name="dynamics">The compiled <c>dynamics</c>-row follower <paramref name="tuning"/> names, or
    /// <see langword="null"/> when it shapes convergence through <see cref="WorldMotionModel.Swim.Response"/>
    /// instead.</param>
    /// <returns>The compiled shared tuning.</returns>
    public static FixedMotionTuning Compile(WorldMotionModel.Swim tuning, FixedMotionDynamics? dynamics = null) => Compile(
        moveSpeed: tuning.ThrustSpeed,
        turnSpeed: tuning.TurnSpeed,
        riseGravity: 0f,
        fallGravity: 0f,
        maxFallSpeed: 0f,
        response: tuning.DeclaredResponse,
        sprintMultiplier: tuning.SprintMultiplier,
        moveFrame: tuning.MoveFrame,
        facingSnap: tuning.FacingSnap,
        moveSpeedEnvelope: tuning.ThrustSpeedEnvelope,
        dynamics: dynamics
    );
    /// <summary>Compiles an authored vehicle motion row to its fixed-point form. The held drift/boost channel names
    /// resolve to ordinals separately, through the world's channel table.</summary>
    /// <param name="tuning">The authored vehicle arm.</param>
    /// <returns>The compiled tuning.</returns>
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
        TopSpeedEnvelope: ((tuning.TopSpeedEnvelope is { } envelope)
        ? Compile(envelope: envelope)
        : null)
    );
    /// <summary>Compiles an authored swim motion row's swim-specific fields to fixed point.</summary>
    /// <param name="tuning">The authored swim arm.</param>
    /// <returns>The compiled swim-specific tuning.</returns>
    public static FixedSwimTuning CompileSwim(WorldMotionModel.Swim tuning) => new(
        VerticalThrustFraction: FixedQ4816.FromDouble(value: tuning.VerticalThrustFraction),
        Buoyancy: FixedQ4816.FromDouble(value: tuning.Buoyancy),
        MaxRiseSpeed: FixedQ4816.FromDouble(value: tuning.MaxRiseSpeed),
        MaxSinkSpeed: FixedQ4816.FromDouble(value: tuning.MaxSinkSpeed),
        SurfaceSettleRate: FixedQ4816.FromDouble(value: tuning.SurfaceSettleRate),
        FloatDepth: FixedQ4816.FromDouble(value: tuning.FloatDepth)
    );
}
