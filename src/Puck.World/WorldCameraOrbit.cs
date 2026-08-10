using System.Numerics;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The local seats' live camera orbit — a small thread-safe holder for each seat's chase-camera yaw/pitch offset
/// (radians), one slot per <see cref="PlayerRoster.MaxSlots"/> entry. Pointer drag and the per-frame look-stick
/// integrator both enter through <see cref="Nudge"/>, the one policy door applying the seat's inversion and pitch
/// clamp; <see cref="Client.WorldFrameSource"/> reads every slot once per frame to compose that seat's chase camera —
/// never <c>WorldClient.Orientation</c>, the simulation body orientation, so a body's facing and movement are
/// unaffected.
/// </summary>
/// <remarks>Presentation-only: nothing here rides the <c>CommandSnapshot</c> pipeline or feeds the deterministic
/// simulation, so cross-thread safety is the only contract — plain <see cref="Volatile"/> reads/writes on each slot's
/// float pair, no lock, since yaw and pitch are independent scalars with no cross-field invariant to protect, and
/// distinct slots never alias the same array element. Every scale is the CALLER's (pointer sensitivity or the
/// authored stick rate integrated against presentation time); inversion and the clamp are applied here from the
/// caller's live <see cref="WorldSeatLook"/>, never reimplemented by either input path.</remarks>
internal sealed class WorldCameraOrbit {
    private readonly float[] m_yaw = new float[PlayerRoster.MaxSlots];
    private readonly float[] m_pitch = new float[PlayerRoster.MaxSlots];

    /// <summary>Gets a seat's current orbit yaw offset in radians, wrapped to [-π, π] (0 = the body's own +Z,
    /// matching <see cref="Puck.SdfVm.Views.OrbitRig.Offset(float, float, float)"/>'s convention).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public float Yaw(int slot) => Volatile.Read(location: ref m_yaw[slot]);

    /// <summary>Gets a seat's current orbit pitch offset in radians, clamped to the pitch band the last
    /// <see cref="Nudge"/> call for that slot was given. Positive raises the camera to look down at the player,
    /// matching <see cref="Puck.SdfVm.Views.OrbitRig.Offset(float, float, float)"/>'s "positive = up" convention.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public float Pitch(int slot) => Volatile.Read(location: ref m_pitch[slot]);

    /// <summary>Nudges a seat's orbit from one input sample. Pointer drag passes pixel motion with its authored
    /// radians-per-pixel scales; the stick path passes normalized deflection with its authored radians-per-second
    /// rate already integrated against this frame's presentation delta. Inversion, yaw wrapping, and pitch clamping
    /// live only behind this door.</summary>
    /// <param name="slot">The 0-based seat slot to nudge.</param>
    /// <param name="input">The two-axis input sample before sensitivity/rate scaling.</param>
    /// <param name="yawScale">Radians of yaw per input unit.</param>
    /// <param name="pitchScale">Radians of pitch per input unit.</param>
    /// <param name="seatLook">The live authored policy supplying inversion and pitch clamps.</param>
    public void Nudge(int slot, Vector2 input, float yawScale, float pitchScale, WorldSeatLook seatLook) {
        ArgumentNullException.ThrowIfNull(argument: seatLook);

        var dYaw = ((input.X * yawScale) * (seatLook.InvertYaw ? -1f : 1f));
        var dPitch = ((input.Y * pitchScale) * (seatLook.InvertPitch ? -1f : 1f));

        ApplyRadians(slot: slot, dYaw: dYaw, dPitch: dPitch, minPitch: seatLook.MinPitch, maxPitch: seatLook.MaxPitch);
    }

    /// <summary>Reclamps a carried orbit against a newly active world's authored pitch band without moving it.</summary>
    /// <param name="slot">The 0-based seat slot to reclamp.</param>
    /// <param name="seatLook">The newly active authored seat-look structure.</param>
    public void Reclamp(int slot, WorldSeatLook seatLook) {
        ArgumentNullException.ThrowIfNull(argument: seatLook);

        ApplyRadians(slot: slot, dYaw: 0f, dPitch: 0f, minPitch: seatLook.MinPitch, maxPitch: seatLook.MaxPitch);
    }

    private void ApplyRadians(int slot, float dYaw, float dPitch, float minPitch, float maxPitch) {
        if ((uint)slot >= PlayerRoster.MaxSlots) {
            return;
        }

        var yaw = (Volatile.Read(location: ref m_yaw[slot]) + dYaw);
        // Wrap into [-π, π] so an unbroken drag session never accumulates an unbounded angle.
        yaw -= (MathF.Tau * MathF.Round(x: (yaw / MathF.Tau)));

        var pitch = Math.Clamp(value: (Volatile.Read(location: ref m_pitch[slot]) + dPitch), min: minPitch, max: maxPitch);

        Volatile.Write(location: ref m_yaw[slot], value: yaw);
        Volatile.Write(location: ref m_pitch[slot], value: pitch);
    }

    /// <summary>Resets a seat's orbit to dead center. Not wired to any input yet.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public void Recenter(int slot) {
        if ((uint)slot >= PlayerRoster.MaxSlots) {
            return;
        }

        Volatile.Write(location: ref m_yaw[slot], value: 0f);
        Volatile.Write(location: ref m_pitch[slot], value: 0f);
    }
}
