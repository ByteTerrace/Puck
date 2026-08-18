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
    private readonly WorldSeatCameraResolver.LiveOrbitCache m_orbit = new();
    private readonly WorldSeatCameraResolver.SmoothingState m_smoothing = new();

    private WorldCameraRig? m_authoredRig;
    private ISdfCameraRig? m_compiledRig;
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
    private static float AuthoredPitch(WorldViewDefaults views) => ((views.SeatRig.Motion as WorldCameraMotion.Orbit)?.Pitch ?? 0f);
    private static float Wrap(float radians) => (radians - (MathF.Tau * MathF.Round(x: (radians / MathF.Tau))));

    /// <summary>The total orbit pitch — the authored rig pitch plus the live delta — in radians.</summary>
    public float LogicalPitch(WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: views);

        lock (m_gate) {
            return (AuthoredPitch(views: views) + m_pitch);
        }
    }
    public float LogicalYaw(WorldViewDefaults views, Quaternion bodyOrientation) {
        var authoredYaw = ((views.SeatRig.Motion as WorldCameraMotion.Orbit)?.Yaw ?? 0f);
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

        var authoredYaw = ((views.SeatRig.Motion as WorldCameraMotion.Orbit)?.Yaw ?? 0f);
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

        var authoredYaw = ((views.SeatRig.Motion as WorldCameraMotion.Orbit)?.Yaw ?? 0f);

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
            m_smoothing.Seeded = false;

            return;
        }

        m_swapRate = turnRate;
    }
    public void Recenter() {
        lock (m_gate) {
            m_yaw = 0f;
            m_pitch = 0f;
            m_smoothing.Seeded = false;
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
    public ISdfCameraRig ResolveChase(WorldViewDefaults views, Quaternion bodyOrientation) {
        if (!ReferenceEquals(
            objA: m_authoredRig,
            objB: views.SeatRig
        )) {
            m_authoredRig = views.SeatRig;
            m_compiledRig = WorldCameraRigCompiler.Compile(rig: views.SeatRig);
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

        return WorldSeatCameraResolver.ResolveChase(
            authoredRig: views.SeatRig,
            compiledChase: m_compiledRig!,
            yawReference: views.SeatControl.YawReference,
            bodyOrientation: bodyOrientation,
            liveYaw: yaw,
            livePitch: pitch,
            cache: m_orbit
        );
    }
    public void Smooth(float rate, bool enabled, float deltaSeconds, ref Vector3 eye, ref Vector3 target) {
        lock (m_gate) {
            // A swap in flight closes at ITS rate; the rig's resumes the moment the boom has landed on the turned
            // pose (within SwapLandedDistance, the same scale as an unnoticeable camera offset).
            var boomTarget = (eye - target);

            WorldSeatCameraResolver.Smooth(
                deltaSeconds: deltaSeconds,
                eye: ref eye,
                isPlainChase: enabled,
                smoothRate: (m_swapRate ?? rate),
                state: m_smoothing,
                target: ref target
            );

            if (
                (m_swapRate is not null) &&
                ((boomTarget - m_smoothing.Boom).LengthSquared() <= (SwapLandedDistance * SwapLandedDistance))
            ) {
                m_swapRate = null;
            }
        }
    }
}
