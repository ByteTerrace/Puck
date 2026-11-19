using System.Numerics;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Authoring;
using Puck.SdfVm.Views;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The per-body animation-driver runtime a creation look's <see cref="CreationDriverDocument"/> rows drive: one
/// running phase and one eased weight per declared driver, advanced once a frame from the body's rendered pose delta
/// and its <see cref="BodyFacts"/>. Presentation-only — the state lives on the stamp pool's registration and nothing
/// derived from it re-enters the simulation.
/// </summary>
/// <remarks>An integrating driver's phase advances on its signal's delta, so an idle body holds its pose and travel
/// (or elapsed time) sets the rate; the weight eases on wall time using the driver's authored blend times
/// (default <see cref="CreationDriverDocument.WeightSeconds"/>) instead of freezing mid-stride when the gate flips.</remarks>
public static class WorldGaitDrivers {
    /// <summary>One driver evaluation's counted work and remaining activity. Initialization charges no clock;
    /// integration counts positive-time nonzero-cadence updates, while phase changes count actual stored changes.</summary>
    /// <param name="Initialized">Whether the runtime seeded its pose and cleared the driver state.</param>
    /// <param name="Evaluations">The number of declared drivers evaluated.</param>
    /// <param name="Integrations">The number of integrating drivers charged a positive-time signal.</param>
    /// <param name="PhaseChanges">The number of drivers whose stored phase changed.</param>
    /// <param name="Continues">Whether a later positive-time evaluation is still owed without an external change.</param>
    public readonly record struct Activity(bool Initialized, int Evaluations, int Integrations, int PhaseChanges, bool Continues);

    /// <summary>The travel one frame may charge to an integrating phase, world units — clamps a teleport or an
    /// authority snap so it cannot spin a limb through dozens of cycles in one frame. Shared with the procedural
    /// catalog rig's own gait so a creation limb and a catalog limb answer a jump the same way.</summary>
    public const float MaxTravelPerFrame = WorldMirroredAvatarBand.MaxGaitTravelPerFrame;
    /// <summary>The fastest body motion, metres per second, a rate signal (<c>speed</c>, the accelerations and the travel
    /// direction) reads — four times the rate <see cref="MaxTravelPerFrame"/> allows at 60 Hz. A rate is travel over
    /// the frame's seconds, so it bounds the rate and never the travel: a frame that runs long still reads the body's
    /// real speed, where a per-frame travel cap would read it low.</summary>
    public const float MaxRate = 60f;
    /// <summary>The eased rendered speed, metres per second, above which the <c>moving</c> gate token holds. The
    /// speed it tests is low-passed over <see cref="CreationDriverDocument.WeightSeconds"/>, so a body crossing the
    /// threshold does not flicker the gate frame to frame.</summary>
    public const float MovingSpeed = 0.05f;
    /// <summary>The weight below which an easing-out driver is at rest: the exponential approach never reaches zero,
    /// and a driver whose weight is merely denormal still advances its phase forever. Snapping there stops the phase
    /// as well as the pose, and a milliradian of residual swing is invisible.</summary>
    public const float RestWeight = 1e-3f;

    /// <summary>Advances one registration's driver phases and weights for a frame. Reseeds — every phase and weight to zero,
    /// no signal charged — when the entity address changes or this is the first call for the registration, so a
    /// reused body slot never inherits the previous inhabitant's stride.</summary>
    /// <param name="rig">The creation's rig, whose drivers advance in declaration order, or <see langword="null"/> for
    /// none; drivers past <paramref name="phases"/>'s length are ignored (a registration's table is sized from the
    /// rig).</param>
    /// <param name="phases">The per-driver running phase, indexed by driver row.</param>
    /// <param name="weights">The per-driver ease weight in [0, 1], same indexing.</param>
    /// <param name="deltaSeconds">The frame delta the weights ease over and the time/rate signals read.</param>
    /// <param name="facts">The body's sim facts this frame — each driver's <c>when</c> gate tests these.</param>
    /// <param name="position">The body's render position this frame.</param>
    /// <param name="orientation">The body's render attitude this frame.</param>
    /// <param name="lastPosition">The position latched by the previous call; updated every call.</param>
    /// <param name="lastOrientation">The attitude latched by the previous call; updated every call.</param>
    /// <param name="seeded">Whether a prior call latched an address; set <see langword="true"/> on return.</param>
    /// <param name="lastAddress">The address latched by the previous call; updated on a reseed.</param>
    /// <param name="easedSpeed">The low-passed rendered ground speed backing the <c>moving</c>/<c>still</c> gate tokens,
    /// metres per second; updated every call and zeroed on a reseed.</param>
    /// <param name="address">This call's entity address; uninhabited placements use index -1 with no body facts.</param>
    /// <param name="reads">The view of the rig's reads a state-cell signal and a state gate token read through, bound to
    /// the wearing body; <see langword="null"/> when none can read.</param>
    /// <param name="kinematics">The registration's eased motion and driver followers, which the direction and
    /// acceleration signals and a driver's <see cref="CreationDriverDocument.Dynamics"/> read; <see langword="null"/>
    /// reads those signals as zero and tracks every signal exactly.</param>
    /// <param name="definition">The live definition whose <c>curves</c> rows a driver's
    /// <see cref="CreationDriverDocument.CadenceBy"/> and <see cref="CreationDriverDocument.WeightBy"/> read, or
    /// <see langword="null"/>, which leaves both unscaled.</param>
    /// <returns>The work performed and whether a later positive-time advance remains eligible, independently of
    /// output pose equality.</returns>
    public static Activity Advance(CreationRig? rig, Span<float> phases, Span<float> weights, float deltaSeconds, BodyFacts facts, Vector3 position, Quaternion orientation, ref Vector3 lastPosition, ref Quaternion lastOrientation, ref bool seeded, ref WorldEntityAddress lastAddress, ref float easedSpeed, WorldEntityAddress address, WorldRigReads? reads = null, WorldGaitKinematics? kinematics = null, WorldDefinition? definition = null) {
        var drivers = rig?.Drivers;

        if (
            !seeded ||
            (lastAddress != address)
        ) {
            seeded = true;
            lastAddress = address;
            lastPosition = position;
            lastOrientation = orientation;
            easedSpeed = 0f;
            phases.Clear();
            weights.Clear();
            kinematics?.Reset();

            return new(Initialized: true, Evaluations: 0, Integrations: 0, PhaseChanges: 0, Continues: (drivers is { Count: > 0 }));
        }

        var travel = (position - lastPosition);
        var yawDelta = VectorFunctions.WrapAngle(radians: (VectorFunctions.YawOf(attitude: orientation) - VectorFunctions.YawOf(attitude: lastOrientation)));

        lastPosition = position;
        lastOrientation = orientation;

        var blend = WeightBlend(deltaSeconds: deltaSeconds);

        // The eased speed is latched whether or not the creation declares any driver, so a look gaining one mid-run
        // reads a settled speed rather than a cold zero. It is ground speed: a jump or a fall is not a step, so
        // vertical travel never opens a stride or reads as cruising.
        var groundTravel = new Vector2(
            x: travel.X,
            y: travel.Z
        ).Length();

        var previousSpeed = easedSpeed;

        easedSpeed += (((((deltaSeconds > 0f)
            ? (groundTravel / deltaSeconds)
            : 0f) - easedSpeed) * blend));

        var moving = (easedSpeed > MovingSpeed);
        var travelLength = travel.Length();
        var rateLimit = (MaxRate * deltaSeconds);

        kinematics?.Observe(
            blend: blend,
            deltaSeconds: deltaSeconds,
            travel: ((travelLength > rateLimit)
                ? (travel * (rateLimit / travelLength))
                : travel),
            yaw: VectorFunctions.YawOf(attitude: orientation)
        );

        if (drivers is not { Count: > 0 } rows) {
            return default;
        }

        var planar = MathF.Min(
            x: groundTravel,
            y: MaxTravelPerFrame
        );
        var total = MathF.Min(
            x: travel.Length(),
            y: MaxTravelPerFrame
        );
        // A zero delta reads no rate, never an infinite one.
        var perSecond = ((deltaSeconds > 0f)
            ? (1f / deltaSeconds)
            : 0f
        );
        var count = Math.Min(
            val1: rows.Count,
            val2: phases.Length
        );

        var integrations = 0;
        var phaseChanges = 0;
        var continues = ((previousSpeed != easedSpeed) || ((blend <= 0f) && (easedSpeed != 0f)) || (kinematics?.MotionContinues ?? false));

        for (var index = 0; (index < count); index++) {
            var compiled = rows[index];
            var driver = compiled.Document;
            var previousPhase = phases[index];
            var previousWeight = weights[index];
            var holds = HoldsGate(
                facts: facts,
                gate: compiled.Gate,
                moving: moving,
                reads: reads
            );
            var level = (holds
                ? ((compiled.WeightCurve is { } weightCurve)
                    ? Math.Clamp(
                        max: 1f,
                        min: 0f,
                        value: CurveOf(
                            curve: weightCurve,
                            definition: definition,
                            phase: PhaseOf(driver: compiled.WeightBy, phases: phases)
                        )
                    )
                    : 1f)
                : 0f
            );
            var seconds = ((level > weights[index])
                ? driver.BlendInSeconds
                : driver.BlendOutSeconds
            );
            var driverBlend = ((seconds is { } authoredSeconds)
                ? WeightBlend(
                    deltaSeconds: deltaSeconds,
                    seconds: authoredSeconds
                )
                : blend
            );
            var weight = (weights[index] + ((level - weights[index]) * driverBlend));

            weights[index] = (((level < RestWeight) && (weight < RestWeight))
                ? 0f
                : weight
            );

            continues |= ((weights[index] != previousWeight) || ((driverBlend <= 0f) && (weights[index] != level)));

            var cadence = (driver.Cadence.Value * ((compiled.CadenceCurve is { } cadenceCurve)
                ? CurveOf(
                    curve: cadenceCurve,
                    definition: definition,
                    phase: PhaseOf(driver: compiled.CadenceBy, phases: phases)
                )
                : 1f
            ));

            if (CreationDriverDocument.TryDriverSignalName(
                name: out _,
                signal: driver.Signal
            )) {
                phases[index] = (cadence * PhaseOf(
                    driver: compiled.Source,
                    phases: phases
                ));

                phaseChanges += ((phases[index] != previousPhase) ? 1 : 0);
                continue;
            }
            if (compiled.Signal is { } signal) {
                // A state cell presented at the frame: a cycle-trait clock, a drawn value, an advancing counter —
                // the phase is the sim's own number, read through the state mirror like every other binding.
                var stateTarget = (((reads is not null) && reads.TryNumber(
                    read: signal,
                    value: out var stateValue
                ))
                    ? (cadence * stateValue)
                    : 0f
                );

                phases[index] = (kinematics?.Follow(
                    deltaSeconds: deltaSeconds,
                    index: index,
                    period: 0f,
                    target: stateTarget
                ) ?? stateTarget);

                phaseChanges += ((phases[index] != previousPhase) ? 1 : 0);
                continues |= (kinematics?.FollowerContinues(index: index) ?? false);
                continue;
            }
            if (!CreationDriverDocument.Integrates(signal: driver.Signal)) {
                // A frame that charged no time (a held capture) has no rate to read: the phase holds what the last
                // frame with time read, where a zero rate would drop a speed-driven swing to rest.
                if (deltaSeconds <= 0f) {
                    continues |= (kinematics?.FollowerContinues(index: index) ?? false);
                    continue;
                }

                var target = (cadence * (driver.Signal switch {
                    CreationDriverDocument.SignalSpeed => MathF.Min(
                        x: (groundTravel * perSecond),
                        y: MaxRate
                    ),
                    CreationDriverDocument.SignalVerticalSpeed => (travel.Y * perSecond),
                    CreationDriverDocument.SignalTravelDirection => (kinematics?.TravelDirection ?? 0f),
                    CreationDriverDocument.SignalLateralAcceleration => (kinematics?.LateralAcceleration ?? 0f),
                    CreationDriverDocument.SignalForwardAcceleration => (kinematics?.ForwardAcceleration ?? 0f),
                    _ => (yawDelta * perSecond),
                }));

                phases[index] = (kinematics?.Follow(
                    deltaSeconds: deltaSeconds,
                    index: index,
                    period: WorldGaitKinematics.Period(
                        cadence: cadence,
                        signal: driver.Signal
                    ),
                    target: target
                ) ?? target);

                phaseChanges += ((phases[index] != previousPhase) ? 1 : 0);
                continues |= (kinematics?.FollowerContinues(index: index) ?? false);
                continue;
            }

            if (weights[index] <= 0f) {
                continue;
            }

            continues |= ((driver.Signal is CreationDriverDocument.SignalTime) && (cadence != 0f));
            if ((deltaSeconds <= 0f) || (cadence == 0f)) {
                continue;
            }

            integrations++;
            // Wrapped into [0, 2π) every frame: both waveforms are 2π-periodic in their argument, so wrapping is
            // exact for the motion and is what keeps a rotor's phase from losing float precision after an hour.
            phases[index] = VectorFunctions.WrapTurn(radians: (phases[index] + (cadence * (driver.Signal switch {
                CreationDriverDocument.SignalPlanarTravel => planar,
                CreationDriverDocument.SignalTravel => total,
                _ => deltaSeconds,
            }))));
            phaseChanges += ((phases[index] != previousPhase) ? 1 : 0);
        }

        return new(Continues: continues, Evaluations: count, Initialized: false, Integrations: integrations, PhaseChanges: phaseChanges);
    }
    /// <summary>Applies a rigid delta (<c>x → R·x + t</c>) to a shape's creation-space pose.</summary>
    /// <param name="deltaRotation">The delta's rotation.</param>
    /// <param name="deltaTranslation">The delta's translation.</param>
    /// <param name="position">The shape's position; replaced by the carried position.</param>
    /// <param name="rotation">The shape's orientation; replaced by the carried orientation.</param>
    public static void Apply(Quaternion deltaRotation, Vector3 deltaTranslation, ref Vector3 position, ref Quaternion rotation) {
        position = (Vector3.Transform(
            rotation: deltaRotation,
            value: position
        ) + deltaTranslation);
        rotation = (deltaRotation * rotation);
    }
    /// <summary>Chains a child's rigid delta under its parent's: the parent's motion carries the child's whole
    /// frame, pivots included, so <c>x → Rp·(Rc·x + tc) + tp</c>.</summary>
    /// <param name="parentRotation">The parent's composed rotation.</param>
    /// <param name="parentTranslation">The parent's composed translation.</param>
    /// <param name="rotation">The child's own delta rotation; replaced by the chained rotation.</param>
    /// <param name="translation">The child's own delta translation; replaced by the chained translation.</param>
    public static void Chain(Quaternion parentRotation, Vector3 parentTranslation, ref Quaternion rotation, ref Vector3 translation) {
        translation = (Vector3.Transform(
            rotation: parentRotation,
            value: translation
        ) + parentTranslation);
        rotation = (parentRotation * rotation);
    }
    /// <summary>Composes a shape's terms onto its creation-space rest pose: its swings in authored order, then its clips'
    /// turns, then its slides, so a slide reads as an offset along its own axis rather than one the turns have
    /// turned.</summary>
    /// <param name="terms">The shape's terms (<see cref="CreationRig.Terms"/>).</param>
    /// <param name="phases">The per-driver phases.</param>
    /// <param name="weights">The per-driver weights.</param>
    /// <param name="position">The shape's position; replaced by the animated position.</param>
    /// <param name="rotation">The shape's orientation; replaced by the animated orientation.</param>
    public static void Compose(ReadOnlySpan<CreationRigTerm> terms, ReadOnlySpan<float> phases, ReadOnlySpan<float> weights, ref Vector3 position, ref Quaternion rotation) {
        _ = ComposeDelta(
            phases: phases,
            rotation: out var deltaRotation,
            terms: terms,
            translation: out var deltaTranslation,
            weights: weights
        );
        Apply(
            deltaRotation: deltaRotation,
            deltaTranslation: deltaTranslation,
            position: ref position,
            rotation: ref rotation
        );
    }
    /// <summary>Computes the rigid delta (<c>x → R·x + t</c>, engine-frame creation space) a shape's terms produce this
    /// frame, independent of the shape's own rest pose — what a child shape rides. A term whose driver's weight is zero
    /// produces exactly nothing and is skipped.</summary>
    /// <param name="terms">The shape's terms (<see cref="CreationRig.Terms"/>), in the order they compose.</param>
    /// <param name="phases">The per-driver phases.</param>
    /// <param name="weights">The per-driver weights.</param>
    /// <param name="rotation">The delta's rotation (identity when nothing composes).</param>
    /// <param name="translation">The delta's translation (zero when nothing composes).</param>
    /// <param name="definition">The live definition a curve waveform samples, or null when none can.</param>
    /// <returns>The number of terms evaluated.</returns>
    public static int ComposeDelta(ReadOnlySpan<CreationRigTerm> terms, ReadOnlySpan<float> phases, ReadOnlySpan<float> weights, out Quaternion rotation, out Vector3 translation, WorldDefinition? definition = null) {
        rotation = Quaternion.Identity;
        translation = Vector3.Zero;

        var evaluated = 0;

        foreach (var term in terms) {
            var driver = term.DriverIndex;

            if ((((uint)driver) >= ((uint)phases.Length)) || (((uint)driver) >= ((uint)weights.Length)) || (weights[driver] == 0f)) {
                continue;
            }

            var share = weights[driver];
            Quaternion turn;

            evaluated++;
            if (term.Track is { } track) {
                // A clip's keyed pose: the share scales the interpolated turn's angle about its own axis and the slide's
                // length.
                if (term.Kind == CreationRigTermKind.Slide) {
                    translation += (track.SlideAt(phase: phases[driver]) * share);

                    continue;
                }

                turn = VectorFunctions.ScaleTurn(fraction: share, turn: track.TurnAt(phase: phases[driver]));
            } else {
                var amount = ((term.Amplitude.Value * Wave(
                    argument: (phases[driver] + (term.Phase?.Value ?? 0f)),
                    definition: definition,
                    wave: term.Wave
                )) * share);

                if (term.Kind == CreationRigTermKind.Slide) {
                    translation += (term.Axis.Value * amount);

                    continue;
                }

                turn = Quaternion.CreateFromAxisAngle(
                    angle: amount,
                    axis: term.Axis.Value
                );
            }

            var pivot = term.Pivot!.Value;

            Chain(
                parentRotation: turn,
                parentTranslation: (pivot - Vector3.Transform(
                    rotation: turn,
                    value: pivot
                )),
                rotation: ref rotation,
                translation: ref translation
            );
        }

        return evaluated;
    }
    /// <summary>Returns a <c>curves</c> row's height (Z) where its X equals <paramref name="x"/>, held at the end knots
    /// outside the row's span. The row is one the world validator admits as a driver curve, drawn left to right, so X
    /// rises along its arc and a bisection of the arc finds the one point at <paramref name="x"/>.</summary>
    /// <param name="row">The curve row.</param>
    /// <param name="x">The X to read the height at, in the row's own units.</param>
    /// <returns>The height, in the row's own units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public static float CurveHeight(WorldCurveRow row, float x) {
        ArgumentNullException.ThrowIfNull(argument: row);

        // Arc stations are Q32 raws; halving down to one Q16 step reads the height at the precision the row stores.
        const long Resolution = (1L << 16);
        var compiled = row.Compiled;
        var target = FixedQ4816.FromDouble(value: x).Value;
        var low = 0L;
        var high = compiled.TotalLengthRaw;

        if (compiled.EvaluateRaw(arcRaw: low).Position.X.Value >= target) {
            high = low;
        } else if (compiled.EvaluateRaw(arcRaw: high).Position.X.Value <= target) {
            low = high;
        }

        while ((high - low) > Resolution) {
            var middle = (low + ((high - low) / 2L));

            if (compiled.EvaluateRaw(arcRaw: middle).Position.X.Value < target) {
                low = middle;
            } else {
                high = middle;
            }
        }

        return ((float)compiled.EvaluateRaw(arcRaw: low).Position.Z);
    }

    // A driver's curve modulator: the height of the rig's curve read at an earlier driver's phase this frame; a row the
    // definition does not declare (refused at boot, missed only mid-swap) leaves the driver unscaled.
    private static float CurveOf(CreationRigRead curve, float phase, WorldDefinition? definition) => (((definition is not null) && (WorldDefinitionRows.FindCurve(
        curves: definition.Curves,
        name: curve.Reference!
    ) is { } row))
        ? CurveHeight(
            row: row,
            x: phase
        )
        : 1f
    );
    // The phase an earlier driver reached this frame, by the index the rig resolved; zero for none, which the
    // canonicalizer refuses.
    private static float PhaseOf(ReadOnlySpan<float> phases, int driver) => ((((uint)driver) < ((uint)phases.Length)) ? phases[driver] : 0f);

    /// <summary>Returns whether a compiled gate holds — every token must, and an empty gate is ungated. A state token
    /// holds while the cell's STORED value is nonzero (the truth a rule reads, never an eased sample). The world validator
    /// refuses a token naming no fact or row before a document reaches here; one that still arrives fails the
    /// conjunction, so the driver rests rather than running unconditionally.</summary>
    /// <param name="gate">The compiled gate (<see cref="CreationRigDriver.Gate"/>, <see cref="CreationRigEffector.Gate"/>).</param>
    /// <param name="facts">The body's sim facts this frame.</param>
    /// <param name="moving">Whether the body's eased speed is above <see cref="MovingSpeed"/>.</param>
    /// <param name="reads">The view of the rig's reads, bound to the reading body, or <see langword="null"/> (a state
    /// token then fails).</param>
    /// <returns><see langword="true"/> when every token holds.</returns>
    public static bool HoldsGate(IReadOnlyList<CreationRigGateToken> gate, BodyFacts facts, bool moving, WorldRigReads? reads = null) {
        ArgumentNullException.ThrowIfNull(argument: gate);

        for (var index = 0; (index < gate.Count); index++) {
            var token = gate[index];
            var holds = token.Kind switch {
                CreationRigGateTokenKind.State => ((reads is not null) && reads.TryNumber(read: token.Read!, value: out var truth) && (truth != 0f)),
                CreationRigGateTokenKind.Moving => moving,
                CreationRigGateTokenKind.Still => !moving,
                _ => (BodyFactVocabulary.TryResolve(gate: out var bit, name: token.Name) && BodyFactVocabulary.Holds(facts: facts, gate: bit)),
            };

            if (!holds) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Reads a body's live scale multiplier — the register <c>bodies.scale</c> names, which
    /// <c>WorldPopulation.SyncBodyScale</c> reads server-side, eased, through the state mirror slot the body's lease
    /// holds — for a presentation consumer that scales something other than the rendered stamp itself (a chase camera's
    /// orbit distance, say). A world naming no scale register, or a body whose slot the mirror does not carry, reads
    /// 1.</summary>
    /// <param name="reads">The body's reads of the state mirror, bound to the body.</param>
    /// <param name="scaleRow">The presentation row of the world's <c>bodies.scale</c> register
    /// (<see cref="WorldBodyStateReads.BindingRow"/>), or <see langword="null"/> when it names none.</param>
    /// <returns>The body's live scale, or 1 when unauthored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reads"/> is <see langword="null"/>.</exception>
    public static float LiveBodyScale(WorldStateLease reads, string? scaleRow) {
        ArgumentNullException.ThrowIfNull(argument: reads);

        if (scaleRow is null) {
            return 1f;
        }

        return (reads.TryNumber(
            slot: reads.Slot(
                key: StateBinding.BodyKey,
                row: scaleRow,
                source: scaleRow,
                target: false
            ),
            value: out var value
        )
            ? value
            : 1f
        );
    }
    /// <summary>Reads a driver's current phase and weight by the index its rig resolved.</summary>
    /// <param name="driver">The driver's index (<see cref="CreationRigEffector.PlantDriver"/>), or -1.</param>
    /// <param name="phases">The per-driver phases.</param>
    /// <param name="weights">The per-driver weights.</param>
    /// <param name="phase">The driver's phase, radians; zero when the index names no driver.</param>
    /// <param name="weight">The driver's eased weight in [0, 1]; zero when the index names no driver.</param>
    /// <returns><see langword="true"/> when the index names a driver.</returns>
    public static bool TryDriver(int driver, ReadOnlySpan<float> phases, ReadOnlySpan<float> weights, out float phase, out float weight) {
        if ((((uint)driver) >= ((uint)phases.Length)) || (((uint)driver) >= ((uint)weights.Length))) {
            phase = 0f;
            weight = 0f;

            return false;
        }

        phase = phases[driver];
        weight = weights[driver];

        return true;
    }
    /// <summary>Reads a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> text cell spelling a world-space <c>[x, y, z]</c>,
    /// through the state mirror slot the holder's lease holds for the reference.</summary>
    /// <param name="reads">The holder's reads of the state mirror, bound to the reading body.</param>
    /// <param name="reference">The state reference.</param>
    /// <param name="value">The parsed point; zero when the cell is absent, not text, or not three numbers.</param>
    /// <returns><see langword="true"/> when a text cell parsed as three numbers.</returns>
    /// <remarks>Parses off the cell's own string rather than deserializing, so a per-frame read allocates
    /// nothing.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reads"/> or <paramref name="reference"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryReadStateVector(WorldStateLease reads, string reference, out Vector3 value) {
        ArgumentNullException.ThrowIfNull(argument: reads);

        value = Vector3.Zero;

        if (
            !reads.TryText(
            reference: reference,
            value: out var text
        ) ||
            (text is not { Length: > 0 } spelling)
        ) {
            return false;
        }

        var cursor = spelling.AsSpan().Trim();

        if (
            (cursor.Length > 1) &&
            (cursor[0] == '[') &&
            (cursor[^1] == ']')
        ) {
            cursor = cursor[1..^1];
        }

        Span<float> components = stackalloc float[3];

        for (var index = 0; (index < 3); index++) {
            var separator = cursor.IndexOf(value: ',');
            var term = ((separator < 0)
                ? cursor
                : cursor[..separator]
            );

            if (!float.TryParse(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out components[index],
                s: term.Trim(),
                style: System.Globalization.NumberStyles.Float
            )) {
                return false;
            }

            cursor = ((separator < 0)
                ? []
                : cursor[(separator + 1)..]
            );
        }

        value = new Vector3(
            x: components[0],
            y: components[1],
            z: components[2]
        );

        return true;
    }
    /// <summary>Evaluates a facet's waveform: the built-in shapes through <see cref="CreationWave.Evaluate"/>, a
    /// <c>curve:&lt;row&gt;</c> form by sampling the world's row at the argument's fraction of a turn along its arc
    /// (Z is the value). A curve the world does not declare evaluates to zero — the validator refuses it at boot.</summary>
    /// <param name="wave">The waveform name.</param>
    /// <param name="argument">The driver phase plus the facet's phase offset, radians.</param>
    /// <param name="definition">The live definition the curve rows come from.</param>
    /// <returns>The waveform value.</returns>
    public static float Wave(string? wave, float argument, WorldDefinition? definition) {
        if (!CreationWave.TryCurveName(
            name: out var name,
            wave: wave
        )) {
            return CreationWave.Evaluate(
                argument: argument,
                wave: wave
            );
        }
        if (
            (definition is null) ||
            (WorldDefinitionRows.FindCurve(
            curves: definition.Curves,
            name: name
        ) is not { } row)
        ) {
            return 0f;
        }

        var fraction = VectorFunctions.Fract(x: (argument / MathF.Tau));
        var compiled = row.Compiled;
        var sample = compiled.Evaluate(arcLength: FixedQ4816.FromDouble(value: (((double)compiled.TotalLength) * fraction)));

        return ((float)sample.Position.Z);
    }
    /// <summary>Returns the fraction of the remaining error a weight closes in one frame — the frame-rate independent
    /// exponential approach over <see cref="CreationDriverDocument.WeightSeconds"/>.</summary>
    /// <param name="deltaSeconds">The frame delta; a non-positive delta closes nothing.</param>
    /// <param name="seconds">The non-negative exponential time constant. Zero closes the full error on a positive delta.</param>
    /// <returns>The blend factor in [0, 1].</returns>
    public static float WeightBlend(float deltaSeconds, float seconds = CreationDriverDocument.WeightSeconds) => FirstOrderLag.Alpha(
        deltaSeconds: deltaSeconds,
        rate: (1f / seconds)
    );
}
