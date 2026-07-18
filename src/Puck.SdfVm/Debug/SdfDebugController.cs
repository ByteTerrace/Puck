using System.Numerics;
using Puck.SdfVm.Views;

namespace Puck.SdfVm.Debug;

/// <summary>
/// Controls the SDF debug mode's orbit camera. The neutral <see cref="SdfOrbitInput"/> keeps this type independent
/// of any controller family or input project. The left stick orbits
/// (yaw/pitch), the triggers zoom exponentially, the right stick pans the orbit target, the exit button EXITS the
/// mode, and a CARVE chord (hold the carve guard, press the carve button) fires a one-shot carve request — all
/// one-shot requests the node/mode consume. The distance floor is widened to ~0.5 so the camera can get right up on
/// a small subject. Presentation only — nothing here reaches the deterministic world. Device-agnostic by
/// construction: a host adapts its own controller/keyboard state into <see cref="SdfOrbitInput"/> before calling
/// <see cref="Advance"/> — this keeps <c>Puck.SdfVm</c> free of a <c>Puck.Input</c> project reference.
/// The internal <see cref="OrbitRig"/> owns the pose and camera math; this class owns input and edge handling.
/// </summary>
public sealed class SdfDebugController {
    // The orbit envelope: pitch off the poles, distance widened at the near end so we can inspect a tiny shape close up.
    private const float MinPitch = 0.05f;
    private const float MaxDistance = 14f;
    private const float MaxPitch = 1.35f;
    private const float MinDistance = 0.5f;
    private const float OrbitSpeed = 2.4f; // radians/second at full deflection
    private const float PanSpeed = 3.0f;   // world units/second at full deflection
    private const float ZoomRate = 1.2f;   // exponential zoom rate at full trigger
    // The carve chord drops its carve on the subject's camera-facing NEAR side: the orbit direction (target -> eye)
    // scaled to roughly the subject envelope radius, offset from the orbit target. Purely host-side (no field probe) —
    // the point the player is looking at, computed from the pose alone.
    private const float SubjectEnvelopeRadius = 1.2f;

    // Edge tracking for the exit/carve buttons — two bools rather than a device button-flags snapshot, since
    // SdfOrbitInput carries no device vocabulary to snapshot (see class doc).
    private bool m_prevExitButton;
    private bool m_prevCarveButton;
    private bool m_exitRequested;
    private Vector3? m_carveRequest; // a pad-chord carve center awaiting consume (computed at the chord edge from the live pose)
    // The orbit pose itself: the subject sits at the world origin, so the rig's target starts there and pans from it.
    private readonly OrbitRig m_rig = new() { Distance = 4f, Pitch = 0.5f };

    /// <summary>The orbit frame for the screen director (object-intent orbit — never the head-on sprite framing).</summary>
    public (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite) CameraFrame =>
        (m_rig.Target, m_rig.Yaw, m_rig.Pitch, m_rig.Distance, false);

    /// <summary>Poses the orbit camera directly (the deterministic, scriptable lever the pad drives interactively) —
    /// each argument is optional and clamped to the same orbit envelope the pad respects, so a scripted repro can pin a
    /// grazing low pitch / a framing target without a controller in the loop. A neutral pad leaves the pose put on the
    /// next <see cref="Advance"/>, so the set sticks.</summary>
    /// <param name="pitch">The orbit pitch (radians, clamped [0.05, 1.35]); null keeps the current pitch.</param>
    /// <param name="yaw">The orbit yaw (radians); null keeps the current yaw.</param>
    /// <param name="distance">The orbit distance (world units, clamped [0.5, 14]); null keeps the current distance.</param>
    /// <param name="target">The orbit target (world units); null keeps the current target.</param>
    public void SetPose(float? pitch, float? yaw, float? distance, Vector3? target) {
        if (pitch is { } p) {
            m_rig.Pitch = Math.Clamp(value: p, min: MinPitch, max: MaxPitch);
        }

        if (yaw is { } y) {
            m_rig.Yaw = y;
        }

        if (distance is { } d) {
            m_rig.Distance = Math.Clamp(value: d, min: MinDistance, max: MaxDistance);
        }

        if (target is { } t) {
            m_rig.Target = t;
        }
    }

    /// <summary>Clears the edge tracking and any pending exit/carve — call when the mode toggles so a held button never
    /// fires a stale edge into the other mode. Leaves the framing (so re-entering keeps the last camera pose).</summary>
    public void Reset() {
        m_exitRequested = false;
        m_carveRequest = null;
        m_prevExitButton = false;
        m_prevCarveButton = false;
    }

    /// <summary>Returns whether the EXIT verb fired since the last consume (and clears it).</summary>
    public bool ConsumeExitRequest() {
        var requested = m_exitRequested;

        m_exitRequested = false;

        return requested;
    }

    /// <summary>Returns the pending pad-chord carve center (world units) if the carve chord fired since the last consume,
    /// else null — and clears it. The center was computed at the chord edge from the live orbit pose (deterministic:
    /// pure yaw/pitch/target, no field evaluation), so a pad carve appends the same data a scripted <c>sdf.carve</c> does.</summary>
    public Vector3? ConsumeCarveRequest() {
        var request = m_carveRequest;

        m_carveRequest = null;

        return request;
    }

    /// <summary>Advances one frame of orbit input: left stick orbits, triggers zoom, right stick pans, the exit
    /// button exits.</summary>
    /// <param name="raw">The neutral orbit input this frame.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Advance(in SdfOrbitInput raw, float deltaSeconds) {
        m_rig.Yaw += ((raw.LeftStick.X * OrbitSpeed) * deltaSeconds);
        m_rig.Pitch = Math.Clamp(value: (m_rig.Pitch + ((raw.LeftStick.Y * OrbitSpeed) * deltaSeconds)), max: MaxPitch, min: MinPitch);
        m_rig.Distance = Math.Clamp(value: (m_rig.Distance * MathF.Exp(x: (((raw.LeftTrigger - raw.RightTrigger) * ZoomRate) * deltaSeconds))), max: MaxDistance, min: MinDistance);

        if (raw.RightStick != Vector2.Zero) {
            // Pan in camera-relative planar axes (right = orbit-right, up on the stick = away from the camera), so the
            // target moves the way the view suggests regardless of the orbit angle.
            var forward = new Vector2(x: -MathF.Sin(x: m_rig.Yaw), y: -MathF.Cos(x: m_rig.Yaw));
            var right = new Vector2(x: -forward.Y, y: forward.X);
            var planar = (((right * raw.RightStick.X) + (forward * raw.RightStick.Y)) * (PanSpeed * deltaSeconds));

            m_rig.Target += new Vector3(x: planar.X, y: 0f, z: planar.Y);
        }

        if (raw.ExitButton && !m_prevExitButton) {
            m_exitRequested = true;
        }

        // CARVE chord: hold the carve guard (so an orbiting stick / a lone carve-button press never fires a
        // destructive carve) and PRESS the carve button. Edge-tracked on the carve button so a held chord carves
        // exactly once; the center is snapped from the pose HERE (the near-side point moves with the camera).
        var carveEdge = (raw.CarveButton && !m_prevCarveButton);

        if (carveEdge && raw.CarveGuardButton) {
            m_carveRequest = NearSideCarvePoint();
        }

        m_prevExitButton = raw.ExitButton;
        m_prevCarveButton = raw.CarveButton;
    }

    // The subject's camera-facing near-side point: the orbit direction (target -> eye, the SAME basis
    // ScreenLayoutDirector builds the eye from — now the shared OrbitRig.Offset every object-intent camera in the
    // codebase reduces to) scaled to the subject envelope radius, offset from the orbit target. A pad carve therefore
    // bites the surface the player is looking at, computed purely from the pose — no march, no probe.
    private Vector3 NearSideCarvePoint() =>
        (m_rig.Target + OrbitRig.Offset(yaw: m_rig.Yaw, pitch: m_rig.Pitch, distance: SubjectEnvelopeRadius));
}
