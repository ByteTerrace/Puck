using System.Numerics;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// The complete live view state of one occupied seat. It is born and dies with <see cref="SeatController"/> and is
/// the sole owner of play-camera yaw/pitch, compiled live rig, and smoothing. Input adapters mutate it; movement,
/// local rendering, away rendering, and read-back all observe it.
/// </summary>
public sealed class WorldSeatViewState {

    private readonly Lock m_gate = new();
    private readonly SdfCameraBoomFollower m_boom = new();

    // How much of the world-derived alignment is taken back per update once it is trustworthy again. Small enough
    // that the carried frame is what the seat feels moment to moment, large enough that a lap's accumulated drift is
    // gone within a second of leaving the pole.
    private const float ReanchorFraction = 0.08f;
    // Within this much of the antipode the world-derived alignment carries no usable twist and is not taken at all:
    // cos(10 degrees) from straight down.
    private const float AntipodeGuard = -0.985f;

    private Quaternion m_upAlignment = Quaternion.Identity;
    private Vector3 m_alignedUp = Vector3.UnitY;

    private WorldCameraProgram? m_authoredRig;
    // The Dynamics op's coefficients are baked into the compiled ops at translate time (see
    // WorldCameraRigCompiler), never re-read per frame — so a `dynamics` row mutation with the authored program
    // instance unchanged still needs a recompile. KEEP IN SYNC: only the section's own compose path may reuse this
    // list's reference across deliveries; anything that clones it unconditionally defeats this cache check.
    private IReadOnlyList<WorldDynamicsRow>? m_authoredDynamics;
    private IWorldCameraProgramRig? m_compiledRig;
    private float m_pitch;
    private float m_yaw;

    public float Pitch { get { lock (m_gate) { return m_pitch; } } }
    public float Yaw { get { lock (m_gate) { return m_yaw; } } }

    // min/max pitch bound the TOTAL orbit pitch (the authored rig's pitch plus the live delta), so the live
    // delta is clamped against the bounds shifted by the authored pitch.
    private static float ClampLivePitch(float livePitch, float authoredPitch, WorldSeatViewControl control) => Math.Clamp(
        value: livePitch,
        min: (control.MinPitch - authoredPitch),
        max: (control.MaxPitch - authoredPitch)
    );
    // The rig's authored pitch, for the live-pitch clamp: a BOUND pitch has no single authored value, so the clamp
    // is taken about the rest angle.
    private static float AuthoredPitch(WorldViewDefaults views) => (views.SeatRig.OrbitOp?.Pitch.Literal ?? 0f);
    private static float Wrap(float radians) => (radians - (MathF.Tau * MathF.Round(x: (radians / MathF.Tau))));

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
                m_upAlignment = Quaternion.Normalize(value: (WorldSeatCameraResolver.ShortestArc(
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
                    quaternion2: WorldSeatCameraResolver.AlignUp(up: up)
                ));
            }

            return m_upAlignment;
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
            ? WorldSeatCameraResolver.BodyYaw(orientation: bodyOrientation)
            : 0f
        );

        lock (m_gate) {
            return Wrap(radians: (m_yaw + bodyYaw));
        }
    }
    public void Nudge(Vector2 input, float yawScale, float pitchScale, WorldSeatLook preference, WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: preference);
        ArgumentNullException.ThrowIfNull(argument: views);

        var control = views.SeatControl;
        var authoredPitch = AuthoredPitch(views: views);

        // Input is semantic look direction (+X right, +Y up). OrbitRig stores the eye's offset around the target,
        // so the corresponding orbit angles move in the opposite direction.
        lock (m_gate) {
            m_yaw = Wrap(radians: (m_yaw - ((input.X * yawScale) * (preference.InvertYaw
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
    /// <summary>The follow camera's step: eases the live facing yaw so it closes on
    /// <paramref name="targetYaw"/> (the body's heading — "behind the body") by the fraction
    /// <c>1 - exp(-rate · dt)</c>. Presentation-only; a World yaw reference is required by the validator. Camera
    /// program orbit offsets are deliberately excluded, so a state-bound look-behind angle never turns the body.</summary>
    /// <param name="targetYaw">The heading to close on, in radians.</param>
    /// <param name="rate">The exponential closing rate per second.</param>
    /// <param name="deltaSeconds">The step.</param>
    public void Follow(float targetYaw, float rate, float deltaSeconds) {
        var fraction = FirstOrderLag.Alpha(rate: rate, deltaSeconds: deltaSeconds);

        lock (m_gate) {
            var current = Wrap(radians: m_yaw);
            var delta = Wrap(radians: (targetYaw - current));

            m_yaw = Wrap(radians: (m_yaw + (delta * fraction)));
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
                : Wrap(radians: targetYaw)
            );
        }
    }

    public void Recenter() {
        lock (m_gate) {
            m_yaw = 0f;
            m_pitch = 0f;
            m_boom.Reseed();
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
    /// <returns>The rig.</returns>
    /// <remarks>The compiled rig is cached against the AUTHORED program instance, so a delivery that only advances
    /// the document retargets in place — the seat's live orbit is an evaluator input, never a recompile.</remarks>
    public IWorldCameraProgramRig ResolveChase(WorldViewDefaults views, Quaternion bodyOrientation, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: views);

        if (
            !ReferenceEquals(
                objA: m_authoredRig,
                objB: views.SeatRig
            ) ||
            !ReferenceEquals(
                objA: m_authoredDynamics,
                objB: definition.Dynamics
            )
        ) {
            m_authoredRig = views.SeatRig;
            m_authoredDynamics = definition.Dynamics;
            m_compiledRig = WorldCameraRigCompiler.Compile(
                definition: definition,
                interactive: true,
                program: views.SeatRig
            );
        } else {
            m_compiledRig!.Retarget(definition: definition);
        }

        float yaw;
        float pitch;

        lock (m_gate) {
            m_pitch = ClampLivePitch(
                authoredPitch: AuthoredPitch(views: views),
                control: views.SeatControl,
                livePitch: m_pitch
            );
            yaw = m_yaw;
            pitch = m_pitch;
        }

        var rig = m_compiledRig!;

        rig.Look = WorldSeatCameraResolver.Look(
            bodyOrientation: bodyOrientation,
            livePitch: pitch,
            liveYaw: yaw,
            yawReference: views.SeatControl.YawReference
        );

        return rig;
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
            } else {
                m_boom.Reseed();
            }
        }
    }
}
