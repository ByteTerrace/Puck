using System.Numerics;
using Puck.Input.Devices;

namespace Puck.Demo.SdfDebug;

/// <summary>
/// The SDF-debug mode's pad orbit camera — a trimmed clone of <c>CreatorController</c>'s workpiece orbit (there is no
/// editing surface here; the whole pad drives the camera). Left stick orbits (yaw/pitch), the triggers zoom
/// exponentially, the right stick pans the orbit target, and North EXITS the mode (a one-shot request the node
/// consumes). The distance floor is widened to ~0.5 so the camera can get right up on a small subject. Presentation
/// only — nothing here reaches the deterministic world.
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

    private GamepadButtons m_prevButtons;
    private bool m_exitRequested;
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

    /// <summary>Clears the edge tracking and any pending exit — call when the mode toggles so a held button never
    /// fires a stale edge into the other mode. Leaves the framing (so re-entering keeps the last camera pose).</summary>
    public void Reset() {
        m_exitRequested = false;
        m_prevButtons = GamepadButtons.None;
    }

    /// <summary>Returns whether the EXIT verb fired since the last consume (and clears it).</summary>
    public bool ConsumeExitRequest() {
        var requested = m_exitRequested;

        m_exitRequested = false;

        return requested;
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

        m_prevButtons = buttons;
    }
}
