using System.Numerics;
using Puck.Maths;
using Puck.SdfVm.Views;
using Puck.SignedDistance.Queries;

namespace Puck.World.Client;

/// <summary>
/// The complete live view state of one occupied seat. It is born and dies with <see cref="SeatController"/> and is
/// the sole owner of play-camera yaw/pitch, compiled live rig, and smoothing. Input adapters mutate it; movement,
/// local rendering, away rendering, and read-back all observe it.
/// </summary>
public sealed class WorldSeatViewState {
    private readonly Lock m_gate = new();
    private readonly SdfCameraBoomFollower m_boom = new();
    private readonly WorldCameraClearance m_clearance = new();
    private readonly WorldCameraViewTransition m_viewTransition = new();
    private static readonly WorldCameraAutoFirstPerson DefaultAutomatic = new();
    private Quaternion m_upAlignment = Quaternion.Identity;
    private Vector3 m_alignedUp = Vector3.UnitY;
    private readonly WorldCameraRigCompiler.Cache m_rigCache = new();

    // Within this much of the antipode the world-derived alignment carries no usable twist and is not taken at all:
    // cos(10 degrees) from straight down.
    private const float AntipodeGuard = -0.985f;
    // How much of the world-derived alignment is taken back per update once it is trustworthy again. Small enough
    // that the carried frame is what the seat feels moment to moment, large enough that a lap's accumulated drift is
    // gone within a second of leaving the pole.
    private const float ReanchorFraction = 0.08f;

    private float m_pitch;
    private float m_yaw;
    // What the last fixed tick's view advance moved the live look by, and where it started, so a frame between ticks
    // can present the look where the body it frames is drawn (see PresentedLook).
    private float m_tickPitchStep;
    private float m_tickStartPitch;
    private float m_tickStartYaw;
    private float m_tickYawStep;
    private long m_cutRevision;
    private long m_observedBoomCut;
    private long m_observedRigRevision;

    /// <summary>Gets the camera cut revision, including a replaced authored rig and an explicit recenter.</summary>
    public long CutRevision {
        get {
            lock (m_gate) {
                if ((m_observedBoomCut != m_boom.CutRevision) || (m_observedRigRevision != m_rigCache.Revision)) {
                    m_observedBoomCut = m_boom.CutRevision;
                    m_observedRigRevision = m_rigCache.Revision;
                    m_cutRevision++;
                }
                return m_cutRevision;
            }
        }
    }
    public float Pitch { get { lock (m_gate) { return m_pitch; } } }
    public float Yaw { get { lock (m_gate) { return m_yaw; } } }

    private WorldCameraClearanceChoice m_clearanceOverride;

    /// <summary>Whether look rotates at the first-person eye and steers the upright body.</summary>
    public bool FirstPersonLook { get { lock (m_gate) { return m_viewTransition.LookFirstPerson; } } }
    /// <summary>Whether this seat requests first person.</summary>
    public bool FirstPerson { get { lock (m_gate) { return m_viewTransition.FirstPerson; } } }
    /// <summary>Sets an explicit view; null restores authored state and automatic preference.</summary>
    public void SetView(bool? first) { lock (m_gate) { m_viewTransition.SetView(first); } }
    /// <summary>Sets an explicit automatic preference; null restores the portable/author default.</summary>
    public void SetAutomaticFirstPerson(bool? enabled) { lock (m_gate) { m_viewTransition.SetAutomatic(enabled); } }
    /// <summary>Resolves the automatic preference.</summary>
    public bool AutomaticFirstPerson(WorldSeatViewControl control, WorldSeatCameraFeel feel) {
        lock (m_gate) { return m_viewTransition.Automatic(control, feel); }
    }

    /// <summary>Gets the last clearance frame's opacity for the body's stamp in this seat's own view.</summary>
    public float BodyOpacity { get { lock (m_gate) { return m_clearance.BodyOpacity; } } }

    /// <summary>Resolves the session override, portable preference, then the author's default.</summary>
    public WorldCameraClearanceChoice ClearanceChoice(WorldSeatViewControl control, WorldSeatCameraFeel feel) {
        lock (m_gate) {
            return ((m_clearanceOverride != WorldCameraClearanceChoice.Auto) ? m_clearanceOverride
                : ((feel.ClearanceMode != WorldCameraClearanceChoice.Auto) ? feel.ClearanceMode : control.ClearanceMode));
        }
    }
    /// <summary>Sets this seat's temporary clearance choice. Auto restores its document/profile preference.</summary>
    public void SetClearanceChoice(WorldCameraClearanceChoice choice) {
        lock (m_gate) {
            m_clearanceOverride = choice;
        }
    }

    // The rig's authored pitch, for the live-pitch clamp: a BOUND pitch has no single authored value, so the clamp
    // is taken about the rest angle.
    private static float AuthoredPitch(WorldViewDefaults views) => (views.SeatRig.OrbitOp?.Pitch.Value.Literal ?? 0f);
    // min/max pitch bound the TOTAL orbit pitch (the authored rig's pitch plus the live delta), so the live
    // delta is clamped against the bounds shifted by the authored pitch.
    private static float ClampLivePitch(float livePitch, float authoredPitch, WorldSeatViewControl control) => Math.Clamp(
        value: livePitch,
        min: (control.MinPitch - authoredPitch),
        max: (control.MaxPitch - authoredPitch)
    );

    /// <summary>The rotation carrying world up to the seat's own up, CARRIED across updates rather than rebuilt.
    /// Both the seat camera's boom and the movement composition ride this, so what the seat pushes and what it looks
    /// through are laid onto the surface by the same rotation.</summary>
    /// <remarks>
    /// <para>Rebuilding it from world up each update is what pins a seat at the bottom of a planetoid. The shortest
    /// arc from world up to a seat's up has no defined twist when the two are opposite, so approaching that point
    /// the twist swings further and further on smaller and smaller changes in up — the control frame spins, and the
    /// seat cannot hold a heading long enough to walk out. Transporting the held rotation by the tick's own (tiny)
    /// change in up has no such point anywhere: it is the same reason WorldBody carries its frame instead of
    /// rebuilding it.</para>
    /// <para>Transport alone would drift, because parallel transport around a closed loop on a curved surface comes
    /// back rotated. So the carried rotation is eased back toward the world-derived one wherever THAT is trustworthy,
    /// and left alone where it is not. The seat gets a frame with no singular point and no accumulating drift, and
    /// the easing is visible as the camera settling rather than as control changing under it.</para>
    /// <para>Idempotent within an update: the camera and the movement composition both ask, and the second ask sees
    /// an unchanged up and returns the same rotation rather than easing twice.</para>
    /// </remarks>
    /// <param name="up">The seat body's current unit up axis.</param>
    /// <returns>The carried alignment.</returns>
    public Quaternion CarriedUpAlignment(Vector3 up) {
        if (up.LengthSquared() <= 0f) {
            lock (m_gate) {
                return m_upAlignment;
            }
        }

        up = Vector3.Normalize(value: up);

        lock (m_gate) {
            // TRANSPORT only when the axis actually moved. An unchanged up carries the held rotation nowhere.
            if (up != m_alignedUp) {
                m_upAlignment = Quaternion.Normalize(value: (VectorFunctions.ShortestArc(
                    from: m_alignedUp,
                    to: up
                ) * m_upAlignment));
                m_alignedUp = up;
            }

            // RE-ANCHOR every time, even when the axis did not move — especially then.
            //
            // Transport accumulates: a lap of a planetoid comes back rotated by the surface's holonomy, and that
            // twist is a real rotation of the frame the seat pushes against. It is meant to be eased out against the
            // world-derived alignment. Skipping the ease whenever up is unchanged skips it exactly where it matters
            // most, because up stops changing the moment the seat is back on level ground: the twist earned out
            // exploring would freeze there permanently, silently turning "forward" for the rest of the session, and
            // the next thing to disturb up — a jump — would jerk it.
            if (up.Y > AntipodeGuard) {
                m_upAlignment = Quaternion.Normalize(value: Quaternion.Slerp(
                    amount: ReanchorFraction,
                    quaternion1: m_upAlignment,
                    quaternion2: VectorFunctions.ShortestArc(
                        from: Vector3.UnitY,
                        to: up
                    )
                ));
            }

            return m_upAlignment;
        }
    }
    /// <summary>The follow camera's step: eases the live facing yaw so it closes on
    /// <paramref name="targetYaw"/> (the body's heading — "behind the body") by the fraction
    /// <c>1 - exp(-rate · dt)</c>. Presentation-only; a World yaw reference is required by the validator. Camera
    /// program orbit offsets are deliberately excluded, so a state-bound look-behind angle never turns the body.</summary>
    /// <param name="targetYaw">The heading to close on, in radians.</param>
    /// <param name="rate">The exponential closing rate per second.</param>
    /// <param name="deltaSeconds">The step.</param>
    public void Follow(float targetYaw, float rate, float deltaSeconds) {
        var fraction = FirstOrderLag.Alpha(
            deltaSeconds: deltaSeconds,
            rate: rate
        );

        lock (m_gate) {
            var current = VectorFunctions.WrapAngle(radians: m_yaw);
            var delta = VectorFunctions.WrapAngle(radians: (targetYaw - current));

            m_yaw = VectorFunctions.WrapAngle(radians: (m_yaw + (delta * fraction)));
        }
    }
    /// <summary>Opens a fixed tick's view advance: the look as it stands now is where this tick's step starts.</summary>
    public void BeginTick() {
        lock (m_gate) {
            m_tickStartYaw = m_yaw;
            m_tickStartPitch = m_pitch;
        }
    }
    /// <summary>Closes a fixed tick's view advance: whatever the tick moved the look by (stick and gyro nudges, a
    /// follow, a held recenter) is the step a frame between this tick and the next interpolates across.</summary>
    public void CompleteTick() {
        lock (m_gate) {
            m_tickYawStep = VectorFunctions.WrapAngle(radians: (m_yaw - m_tickStartYaw));
            m_tickPitchStep = (m_pitch - m_tickStartPitch);
        }
    }
    /// <summary>
    /// <para>Returns the live look a frame presents at <paramref name="interpolationAlpha"/> of the way from the
    /// previous fixed tick to the current one.</para>
    /// <para>Stick and gyro look integrate once per fixed tick, and that tick's yaw is the facing a steering seat
    /// turns its body to. The body is drawn interpolated between its last two tick poses, so a camera on the live
    /// yaw would sit a tick ahead of the body it frames and move in tick-sized steps. Taking back the part of the
    /// last tick's step the frame has not reached puts the camera on the interpolation the body is drawn with.
    /// Only the tick's own step is interpolated: a pointer nudge between ticks moves the live look and shows at
    /// once.</para>
    /// </summary>
    /// <param name="interpolationAlpha">How far the frame sits between the last two fixed ticks, in <c>[0, 1]</c>.</param>
    /// <returns>The presented live yaw and pitch, in radians.</returns>
    public (float Yaw, float Pitch) PresentedLook(float interpolationAlpha) {
        var lag = (1f - Math.Clamp(
            max: 1f,
            min: 0f,
            value: interpolationAlpha
        ));

        lock (m_gate) {
            return (VectorFunctions.WrapAngle(radians: (m_yaw - (m_tickYawStep * lag))), (m_pitch - (m_tickPitchStep * lag)));
        }
    }

    // A cut has no tick step to interpolate across: the look lands where it was put.
    private void CutTickStep() {
        m_tickYawStep = 0f;
        m_tickPitchStep = 0f;
    }

    /// <summary>The chase boom's second-order ease: eases <paramref name="eye"/> toward <paramref name="target"/> by
    /// <paramref name="dynamics"/> while this seat frames through its own chase rig.</summary>
    /// <param name="dynamics">The chase rig's reported response.</param>
    /// <param name="enabled">Whether the boom should ease this frame — <see langword="false"/> reseeds and passes
    /// the pose through untouched.</param>
    /// <param name="deltaSeconds">The frame step.</param>
    /// <param name="eye">The resolved eye, eased in place.</param>
    /// <param name="target">The resolved target, read but never moved.</param>
    public void Follow(in SdfCameraDynamics dynamics, bool enabled, float deltaSeconds, ref Vector3 eye, ref Vector3 target) {
        lock (m_gate) {
            if (enabled) {
                m_boom.Apply(
                    deltaSeconds: deltaSeconds,
                    dynamics: in dynamics,
                    eye: ref eye,
                    target: ref target
                );
            } else if (m_boom.Seeded) {
                m_boom.Reseed();
            }
        }
    }
    /// <summary>Places the chase eye clear of geometry and of the framed body on this seat's spring arm
    /// (<see cref="WorldCameraClearance.Resolve"/>), or, when <paramref name="enabled"/> is <see langword="false"/>,
    /// drops the arm's eased state and passes the eye through.</summary>
    /// <param name="enabled">Whether the seat frames its own body through its chase rig in the world it is presented
    /// in this frame.</param>
    /// <param name="field">The static geometry the eye keeps out of, or <see langword="null"/> for none.</param>
    /// <param name="eye">The eye the eased boom resolved, in world units; placed in place.</param>
    /// <param name="target">The point the eye looks at, in world units.</param>
    /// <param name="subject">The body the eye frames and keeps out of.</param>
    /// <param name="up">The seat's up axis.</param>
    /// <param name="dynamics">The chase rig's boom dynamics, which the arm eases on.</param>
    /// <param name="deltaSeconds">The frame step, in seconds.</param>
    /// <returns>The near-plane distance, in world units, the eye's image begins at
    /// (<see cref="WorldCameraClearance.Near"/>); zero when <paramref name="enabled"/> is <see langword="false"/>.</returns>
    /// <param name="targetShift">The compiled program's close-target migration, in world axes and live scale.</param>
    /// <param name="choice">The resolved clearance preference.</param>
    /// <param name="control">The world's camera clearance parameters.</param>
    /// <param name="scale">The perceived body's live scale.</param>
    /// <param name="firstPerson">The resolved program's authored first-person destination.</param>
    /// <param name="feel">The portable automatic pull-in preference.</param>
    /// <param name="viewForward">The rig's look direction before boom smoothing.</param>
    public float Clear(bool enabled, SdfFieldEvaluator? field, ref Vector3 eye, ref Vector3 target, in WorldChaseBody subject, Vector3 up, in SdfCameraDynamics dynamics, float deltaSeconds,
        SdfCameraTargetShift targetShift = default, WorldCameraClearanceChoice choice = WorldCameraClearanceChoice.Cutaway, WorldSeatViewControl? control = null, float scale = 1f,
        SdfCameraFirstPerson? firstPerson = null, WorldSeatCameraFeel? feel = null, Vector3? viewForward = null) {
        lock (m_gate) {
            if (!enabled) {
                m_clearance.Reset();
                m_viewTransition.Reset();

                return 0f;
            }

            var forward = (viewForward ?? Vector3.Normalize(target - eye));
            var authoredLength = Vector3.Distance(eye, target);
            eye = m_clearance.Resolve(
                deltaSeconds: deltaSeconds,
                desiredEye: eye,
                dynamics: in dynamics,
                field: field,
                subject: in subject,
                target: target,
                up: up,
                targetShift: targetShift,
                choice: choice,
                characterFade: control?.CharacterFade,
                scale: scale,
                maxElevation: (control?.ClearanceMaxElevation ?? WorldCameraClearance.LiftCeiling)
            );
            if ((firstPerson is { } destination) && (control is not null)) {
                var head = subject.Point(destination.EyePoint);
                var room = WorldCameraClearance.AvailableRoom(field, head, -forward, authoredLength, scale) / scale;
                m_viewTransition.Advance(destination, (control.AutoFirstPerson ?? DefaultAutomatic),
                    m_viewTransition.Automatic(control, (feel ?? WorldSeatCameraFeel.Default)), room, deltaSeconds);
                m_clearance.ApplyView(m_viewTransition, head, forward, destination.HideRadius * scale);
                eye = m_clearance.Eye;
            } else {
                m_viewTransition.Reset();
            }
            target = m_clearance.Target;

            return m_clearance.Near;
        }
    }
    /// <summary>Returns the chase eye's latest placement (<see cref="WorldCameraClearance.Describe"/>).</summary>
    /// <returns>The read-back text.</returns>
    public string DescribeClearance() {
        lock (m_gate) {
            return $"{m_viewTransition.Describe()} {m_clearance.Describe()}";
        }
    }
    /// <summary>The total orbit pitch — the authored rig pitch plus the live delta — in radians.</summary>
    public float LogicalPitch(WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: views);

        lock (m_gate) {
            return (AuthoredPitch(views: views) + m_pitch);
        }
    }
    public float LogicalYaw(WorldViewDefaults views, Quaternion bodyOrientation) {
        // The seat's FACING — what steering writes and movement frames against: the live look yaw over the yaw
        // reference, and nothing of the camera program. The rig's orbit yaw (authored or state-bound — look behind)
        // is where the EYE sits relative to the facing; folding it in here is what once made "look behind" turn the
        // body round with the camera.
        var bodyYaw = ((views.SeatControl.YawReference == WorldSeatYawReference.Body)
            ? VectorFunctions.YawOf(attitude: bodyOrientation)
            : 0f
        );

        lock (m_gate) {
            return VectorFunctions.WrapAngle(radians: (m_yaw + bodyYaw));
        }
    }
    public void Nudge(Vector2 input, float yawScale, float pitchScale, WorldSeatCameraFeel preference, WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: preference);
        ArgumentNullException.ThrowIfNull(argument: views);

        var control = views.SeatControl;
        var authoredPitch = AuthoredPitch(views: views);

        // Input is semantic look direction (+X right, +Y up). OrbitRig stores the eye's offset around the target,
        // so the corresponding orbit angles move in the opposite direction.
        lock (m_gate) {
            m_yaw = VectorFunctions.WrapAngle(radians: (m_yaw - ((input.X * yawScale) * (preference.InvertYaw
                ? -1f
                : 1f))));
            m_pitch = ClampLivePitch(
                authoredPitch: authoredPitch,
                control: control,
                livePitch: (m_pitch - ((input.Y * pitchScale) * (preference.InvertPitch
                ? -1f
                : 1f)))
            );
        }
    }
    /// <summary>Returns the rotation of the world a mapped arrival turns its traveler by: the boundary pair's yaw delta
    /// (<see cref="WorldFrameIsometry.YawDelta"/>, the arrival yaw less the departure yaw) as a turn about world up.</summary>
    /// <param name="yawDelta">The arrival's yaw delta, fixed-point radians.</param>
    /// <returns>The turn.</returns>
    public static Quaternion ArrivalTurn(Puck.Maths.FixedQ4816 yawDelta) => Quaternion.CreateFromAxisAngle(
        angle: ((float)yawDelta),
        axis: Vector3.UnitY
    );
    /// <summary>Carries the seat's view through a mapped arrival that turns its traveler by
    /// <paramref name="turn"/>, so the seat looks through the door it walked through as a window onto the same door
    /// showed it. A world-referenced live yaw turns with the traveler; a body-referenced one already rides the
    /// traveler's turned heading. The eased chase boom turns either way.</summary>
    /// <param name="turn">The arrival's turn (<see cref="ArrivalTurn"/>).</param>
    /// <param name="yawReference">The yaw reference of the world the seat arrives in.</param>
    public void Cross(Quaternion turn, WorldSeatYawReference yawReference) {
        lock (m_gate) {
            if (yawReference == WorldSeatYawReference.World) {
                m_yaw = VectorFunctions.WrapAngle(radians: (m_yaw + VectorFunctions.YawOf(attitude: turn)));
            }

            m_boom.Rotate(rotation: turn);
            CutTickStep();
        }
    }
    public void Recenter() {
        lock (m_gate) {
            m_yaw = 0f;
            m_pitch = 0f;
            m_boom.Reseed();
            CutTickStep();
        }
    }
    /// <summary>Turns the camera round BEHIND the body: the live yaw is set so the resulting logical facing is
    /// <paramref name="targetYaw"/>. Presentation-only; the rig's own smoothing eases the turn.</summary>
    /// <param name="targetYaw">The world heading to sit behind, in radians.</param>
    /// <param name="views">The live seat view structure, whose yaw reference determines whether the body heading
    /// is already supplied by the rig's reference frame.</param>
    public void RecenterLook(float targetYaw, WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: views);

        lock (m_gate) {
            m_yaw = ((views.SeatControl.YawReference == WorldSeatYawReference.Body)
                ? 0f
                : VectorFunctions.WrapAngle(radians: targetYaw)
            );
        }
    }
    public void Reclamp(WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: views);

        var control = views.SeatControl;
        var authoredPitch = AuthoredPitch(views: views);

        lock (m_gate) {
            m_pitch = ClampLivePitch(
                authoredPitch: authoredPitch,
                control: control,
                livePitch: m_pitch
            );
        }
    }
    /// <summary>The seat's compiled chase rig for this frame, with the live look sample already folded in.</summary>
    /// <param name="views">The routed world's views section.</param>
    /// <param name="bodyOrientation">The perceived body's orientation.</param>
    /// <param name="definition">The routed world's live document.</param>
    /// <param name="mirror">The state mirror the rig's bound operands read through.</param>
    /// <param name="domains">The guard that holds the last valid value of a bound operand and reports its transitions.</param>
    /// <param name="interpolationAlpha">How far the frame sits between the last two fixed ticks: the fraction the
    /// framed body is drawn at (<see cref="PresentedLook"/>). <c>1</c>, the default, presents the latest tick's look.</param>
    /// <returns>The rig.</returns>
    /// <remarks>The compiled rig is cached against the AUTHORED program instance, so a delivery that only advances
    /// the document retargets in place — the seat's live orbit is an evaluator input, never a recompile.</remarks>
    public IWorldCameraProgramRig ResolveChase(WorldViewDefaults views, Quaternion bodyOrientation, WorldDefinition definition, WorldStateMirror mirror, WorldValueDomainGuard domains, float interpolationAlpha = 1f) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: domains);
        ArgumentNullException.ThrowIfNull(argument: views);

        var rig = m_rigCache.Resolve(
            definition: definition,
            domains: domains,
            interactive: true,
            mirror: mirror,
            program: views.SeatRig
        );

        lock (m_gate) {
            m_pitch = ClampLivePitch(
                authoredPitch: AuthoredPitch(views: views),
                control: views.SeatControl,
                livePitch: m_pitch
            );
        }

        var (yaw, pitch) = PresentedLook(interpolationAlpha: interpolationAlpha);

        rig.Look = WorldSeatCameraResolver.Look(
            bodyOrientation: bodyOrientation,
            livePitch: pitch,
            liveYaw: yaw,
            yawReference: views.SeatControl.YawReference
        );

        return rig;
    }
}
