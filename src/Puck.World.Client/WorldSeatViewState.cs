using System.Numerics;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// The complete live view state of one occupied seat. It is born and dies with <see cref="SeatController"/> and is
/// the sole owner of play-camera yaw/pitch, compiled live rig, and smoothing. Input adapters mutate it; movement,
/// local rendering, away rendering, and read-back all observe it.
/// </summary>
public sealed class WorldSeatViewState {
    // The boom-to-target distance at which a swap in flight is considered landed, in world units.
    private const float SwapLandedDistance = 0.01f;

    private readonly Lock m_gate = new();
    private readonly SdfCameraBoomSmoother m_smoothing = new();

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
    private IWorldCameraProgramRig? m_compiledRig;
    private float m_pitch;
    private float m_yaw;
    // The rate the swap in flight closes its boom at, overriding the rig's own until the boom lands; null when no
    // swap is in flight (or the world left the swap to the rig).
    private float? m_swapRate;

    public float Pitch { get { lock (m_gate) { return m_pitch; } } }
    public float Yaw { get { lock (m_gate) { return m_yaw; } } }

    // min/max pitch bound the TOTAL orbit pitch (the authored rig's pitch plus the live delta), so the live
    // delta is clamped against the bounds shifted by the authored pitch.
    private static float ClampLivePitch(float livePitch, float authoredPitch, WorldSeatViewControl control) => Math.Clamp(
        value: livePitch,
        min: (control.MinPitch - authoredPitch),
        max: (control.MaxPitch - authoredPitch)
    );
    private static float AuthoredPitch(WorldViewDefaults views) => (views.SeatRig.OrbitOp?.Pitch ?? 0f);
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
        var authoredYaw = (views.SeatRig.OrbitOp?.Yaw ?? 0f);
        var bodyYaw = ((views.SeatControl.YawReference == WorldSeatYawReference.Body)
            ? WorldSeatCameraResolver.BodyYaw(orientation: bodyOrientation)
            : 0f
        );

        lock (m_gate) {
            return Wrap(radians: ((authoredYaw + m_yaw) + bodyYaw));
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
    /// <summary>The follow camera's step: eases the live yaw so the total logical yaw closes on
    /// <paramref name="targetYaw"/> (the body's heading — "behind the body") by the fraction
    /// <c>1 - exp(-rate · dt)</c>. Presentation-only; a World yaw reference by validator rule, so the total yaw is
    /// the authored orbit yaw plus the live delta.</summary>
    /// <param name="targetYaw">The heading to close on, in radians.</param>
    /// <param name="rate">The exponential closing rate per second.</param>
    /// <param name="deltaSeconds">The step.</param>
    /// <param name="views">The seat views (for the authored orbit yaw).</param>
    public void Follow(float targetYaw, float rate, float deltaSeconds, WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: views);

        var authoredYaw = (views.SeatRig.OrbitOp?.Yaw ?? 0f);
        var fraction = (1f - MathF.Exp(x: (-rate * deltaSeconds)));

        lock (m_gate) {
            var current = Wrap(radians: (authoredYaw + m_yaw));
            var delta = Wrap(radians: (targetYaw - current));

            m_yaw = Wrap(radians: (m_yaw + (delta * fraction)));
        }
    }
    /// <summary>Turns the live yaw a half-turn — look behind the body; again to look forward. Presentation-only.</summary>
    /// <param name="rate">The rate the boom closes over this turn (<c>views.seatControl.swapRate</c>): zero re-seeds
    /// the boom at the turned pose, so the turn is a cut; a positive value eases it at that rate until it lands;
    /// <see langword="null"/> leaves the turn to the seat rig's own smoothing.</param>
    public void SwapLook(float? rate) {
        lock (m_gate) {
            m_yaw = Wrap(radians: (m_yaw + MathF.PI));
            ApplyTurnRate(rate: rate);
        }
    }
    /// <summary>Turns the camera round BEHIND the body: the live yaw is set so the total logical yaw is
    /// <paramref name="targetYaw"/>. Presentation-only.</summary>
    /// <param name="targetYaw">The heading to sit behind, in radians.</param>
    /// <param name="rate">The rate the boom closes over the turn — <see cref="SwapLook"/>'s rate, same meaning.</param>
    /// <param name="views">The seat views (for the authored orbit yaw the live delta rides).</param>
    public void RecenterLook(float targetYaw, float? rate, WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: views);

        var authoredYaw = (views.SeatRig.OrbitOp?.Yaw ?? 0f);

        lock (m_gate) {
            m_yaw = Wrap(radians: (targetYaw - authoredYaw));
            ApplyTurnRate(rate: rate);
        }
    }

    // Held under m_gate: a zero rate lands the turn instantly (the boom re-seeds at the turned pose), a positive one
    // eases it at that rate until it lands, and null leaves the turn to the rig's own smoothing.
    private void ApplyTurnRate(float? rate) {
        if (rate is not { } turnRate) {
            return;
        }
        if (turnRate <= 0f) {
            m_smoothing.Reseed();

            return;
        }

        m_swapRate = turnRate;
    }

    public void Recenter() {
        lock (m_gate) {
            m_yaw = 0f;
            m_pitch = 0f;
            m_smoothing.Reseed();
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

        if (!ReferenceEquals(
            objA: m_authoredRig,
            objB: views.SeatRig
        )) {
            m_authoredRig = views.SeatRig;
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
    public void Smooth(float rate, bool enabled, float deltaSeconds, ref Vector3 eye, ref Vector3 target) {
        lock (m_gate) {
            // A swap in flight closes at ITS rate; the rig's resumes the moment the boom has landed on the turned
            // pose (within SwapLandedDistance, the same scale as an unnoticeable camera offset).
            var boomTarget = (eye - target);

            if (enabled) {
                m_smoothing.Apply(
                    deltaSeconds: deltaSeconds,
                    eye: ref eye,
                    rate: (m_swapRate ?? rate),
                    target: ref target
                );
            } else {
                m_smoothing.Reseed();
            }

            if (
                (m_swapRate is not null) &&
                ((boomTarget - m_smoothing.Boom).LengthSquared() <= (SwapLandedDistance * SwapLandedDistance))
            ) {
                m_swapRate = null;
            }
        }
    }
}
