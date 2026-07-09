using System.Numerics;
using Puck.Input.Devices;

namespace Puck.Demo.SdfDebug;

/// <summary>
/// The SDF-debug mode's pad orbit camera — a trimmed clone of <c>CreatorController</c>'s workpiece orbit (there is no
/// editing surface here; the whole pad drives the camera). Left stick orbits (yaw/pitch), the triggers zoom
/// exponentially, the right stick pans the orbit target, North EXITS the mode, and a CARVE chord (hold RightShoulder,
/// press South) fires a one-shot carve request — all one-shot requests the node/mode consume. The distance floor is
/// widened to ~0.5 so the camera can get right up on a small subject. Presentation only — nothing here reaches the
/// deterministic world.
/// </summary>
public sealed class SdfDebugController {
    // The orbit envelope: pitch off the poles, distance widened at the near end so we can inspect a tiny shape close up.
    private const float MinPitch = 0.05f;
    private const float MaxPitch = 1.35f;
    private const float MinDistance = 0.5f;
    private const float MaxDistance = 14f;
    private const float OrbitSpeed = 2.4f; // radians/second at full deflection
    private const float PanSpeed = 3.0f;   // world units/second at full deflection
    private const float ZoomRate = 1.2f;   // exponential zoom rate at full trigger
    // The carve chord drops its carve on the subject's camera-facing NEAR side: the orbit direction (target -> eye)
    // scaled to roughly the subject envelope radius, offset from the orbit target. Purely host-side (no field probe) —
    // the point the player is looking at, computed from the pose alone.
    private const float SubjectEnvelopeRadius = 1.2f;

    private GamepadButtons m_prevButtons;
    private bool m_exitRequested;
    private Vector3? m_carveRequest; // a pad-chord carve center awaiting consume (computed at the chord edge from the live pose)
    private float m_orbitYaw;
    private float m_orbitPitch = 0.5f;
    private float m_orbitDistance = 4f;
    private Vector3 m_orbitTarget; // the subject sits at the world origin; the target starts there and pans from it.

    /// <summary>The orbit frame for the screen director (object-intent orbit — never the head-on sprite framing).</summary>
    public (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite) CameraFrame =>
        (m_orbitTarget, m_orbitYaw, m_orbitPitch, m_orbitDistance, false);

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
            m_orbitPitch = Math.Clamp(value: p, min: MinPitch, max: MaxPitch);
        }

        if (yaw is { } y) {
            m_orbitYaw = y;
        }

        if (distance is { } d) {
            m_orbitDistance = Math.Clamp(value: d, min: MinDistance, max: MaxDistance);
        }

        if (target is { } t) {
            m_orbitTarget = t;
        }
    }

    /// <summary>Clears the edge tracking and any pending exit/carve — call when the mode toggles so a held button never
    /// fires a stale edge into the other mode. Leaves the framing (so re-entering keeps the last camera pose).</summary>
    public void Reset() {
        m_exitRequested = false;
        m_carveRequest = null;
        m_prevButtons = GamepadButtons.None;
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

    /// <summary>Advances one frame of pad input: left stick orbits, triggers zoom, right stick pans, North exits.</summary>
    /// <param name="raw">The creating slot's raw pad state this frame.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Advance(in GamepadState raw, float deltaSeconds) {
        var buttons = raw.Buttons;

        m_orbitYaw += (raw.LeftStick.X * OrbitSpeed * deltaSeconds);
        m_orbitPitch = Math.Clamp(value: (m_orbitPitch + (raw.LeftStick.Y * OrbitSpeed * deltaSeconds)), max: MaxPitch, min: MinPitch);
        m_orbitDistance = Math.Clamp(value: (m_orbitDistance * MathF.Exp(((raw.LeftTrigger - raw.RightTrigger) * ZoomRate * deltaSeconds))), max: MaxDistance, min: MinDistance);

        if (raw.RightStick != Vector2.Zero) {
            // Pan in camera-relative planar axes (right = orbit-right, up on the stick = away from the camera), so the
            // target moves the way the view suggests regardless of the orbit angle.
            var forward = new Vector2(-MathF.Sin(x: m_orbitYaw), -MathF.Cos(x: m_orbitYaw));
            var right = new Vector2(-forward.Y, forward.X);
            var planar = (((right * raw.RightStick.X) + (forward * raw.RightStick.Y)) * (PanSpeed * deltaSeconds));

            m_orbitTarget += new Vector3(planar.X, 0f, planar.Y);
        }

        if ((0 != (buttons & GamepadButtons.ButtonNorth)) && (0 == (m_prevButtons & GamepadButtons.ButtonNorth))) {
            m_exitRequested = true;
        }

        // CARVE chord: hold RightShoulder (a guard, so an orbiting stick / a lone face press never fires a destructive
        // carve) and PRESS South. Edge-tracked on South so a held chord carves exactly once; the center is snapped from
        // the pose HERE (the near-side point moves with the camera). South is otherwise unbound and North stays EXIT.
        var southEdge = ((0 != (buttons & GamepadButtons.ButtonSouth)) && (0 == (m_prevButtons & GamepadButtons.ButtonSouth)));

        if (southEdge && (0 != (buttons & GamepadButtons.RightShoulder))) {
            m_carveRequest = NearSideCarvePoint();
        }

        m_prevButtons = buttons;
    }

    // The subject's camera-facing near-side point: the orbit direction (target -> eye, the SAME basis
    // ScreenLayoutDirector builds the eye from) scaled to the subject envelope radius, offset from the orbit target. A
    // pad carve therefore bites the surface the player is looking at, computed purely from the pose — no march, no probe.
    private Vector3 NearSideCarvePoint() {
        var cosPitch = MathF.Cos(x: m_orbitPitch);
        var direction = new Vector3(
            (MathF.Sin(x: m_orbitYaw) * cosPitch),
            MathF.Sin(x: m_orbitPitch),
            (MathF.Cos(x: m_orbitYaw) * cosPitch)
        );

        return (m_orbitTarget + (direction * SubjectEnvelopeRadius));
    }
}
