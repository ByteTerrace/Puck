using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The local seats' live camera orbit — a small thread-safe holder for each seat's chase-camera yaw/pitch offset
/// (radians), one slot per <see cref="PlayerRoster.MaxSlots"/> entry: <see cref="WorldCameraOrbitDrag"/> (the
/// window-pump thread) writes the seat the pointer rides from that seat's drained motion, armed per the authored
/// <see cref="WorldSeatLook"/> feel (that seat's own <c>playerDefaults.seatLook</c>), while <see cref="Client.WorldFrameSource"/> (the render
/// thread) reads every slot once per frame to compose that seat's chase camera — never <c>WorldClient.Orientation</c>,
/// the simulation body orientation, so a body's facing and movement are unaffected. Only the seat whose device
/// currently owns the mouse ever accumulates a nonzero offset; every other slot stays at rest.
/// </summary>
/// <remarks>Presentation-only: nothing here rides the <c>CommandSnapshot</c> pipeline or feeds the deterministic
/// simulation, so cross-thread safety is the only contract — plain <see cref="Volatile"/> reads/writes on each slot's
/// float pair, no lock, since yaw and pitch are independent scalars with no cross-field invariant to protect, and
/// distinct slots never alias the same array element. Every sensitivity, clamp, and arming choice is the CALLER's
/// (<see cref="WorldCameraOrbitDrag"/> reads the live <see cref="WorldSeatLook"/>) — this type holds only the
/// accumulated live offset per seat, no policy of its own.</remarks>
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

    /// <summary>Nudges a seat's orbit by a per-frame drag delta: yaw wraps to [-π, π], pitch clamps to
    /// <paramref name="minPitch"/>/<paramref name="maxPitch"/>.</summary>
    /// <param name="slot">The 0-based seat slot to nudge.</param>
    /// <param name="dYaw">The yaw delta in radians (positive swings the camera toward the body's right side).</param>
    /// <param name="dPitch">The pitch delta in radians (positive raises the camera to look down).</param>
    /// <param name="minPitch">The authored pitch clamp floor in radians (<see cref="WorldSeatLook.MinPitch"/>).</param>
    /// <param name="maxPitch">The authored pitch clamp ceiling in radians (<see cref="WorldSeatLook.MaxPitch"/>).</param>
    public void Nudge(int slot, float dYaw, float dPitch, float minPitch, float maxPitch) {
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
