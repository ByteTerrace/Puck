using System.Numerics;

namespace Puck.World;

/// <summary>
/// The pointer consumer that turns a drag into <see cref="WorldCameraOrbit"/> nudges, according to THAT SEAT's live
/// control feel (<see cref="WorldSeatFeel"/> — the seat's own profile's
/// <c>playerDefaults.seatLook</c>, or the world's when it carries no profile): its <see cref="WorldSeatLook.Arming"/> selects which
/// button (if any) arms the drag, or disables orbiting entirely (<see cref="WorldSeatLookArming.None"/>); its
/// sensitivities, invert flags, and pitch clamp shape the resulting nudge. The cursor is free (and drives the
/// console/overlay/editor as usual) whenever the drag is not armed.
/// </summary>
/// <remarks>Reads both halves of its answer from <see cref="WorldPointer"/> — arming is just that seat's live
/// held-button state, and the drag distance is that seat's drained motion — so it tracks no held state of its own
/// and observes no raw window events. It is one of many consumers behind the single
/// <see cref="WorldPointerSink"/>, which resolves the seat the mouse rides (see its remarks) and drives this on
/// every pointer event.</remarks>
internal sealed class WorldCameraOrbitDrag : IWorldPointerConsumer {
    private readonly WorldCameraOrbit m_orbit;
    private readonly WorldPointer m_pointer;
    private readonly WorldSeatFeel m_seatFeel;

    /// <summary>Initializes a new instance of the <see cref="WorldCameraOrbitDrag"/> class.</summary>
    /// <param name="orbit">The shared orbit state this consumer nudges.</param>
    /// <param name="seatFeel">The per-seat control feel this consumer reads THIS seat's policy from.</param>
    /// <param name="pointer">The live pointer store this consumer reads arming and motion from.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldCameraOrbitDrag(WorldCameraOrbit orbit, WorldSeatFeel seatFeel, WorldPointer pointer) {
        ArgumentNullException.ThrowIfNull(argument: orbit);
        ArgumentNullException.ThrowIfNull(argument: seatFeel);
        ArgumentNullException.ThrowIfNull(argument: pointer);

        m_orbit = orbit;
        m_pointer = pointer;
        m_seatFeel = seatFeel;
    }

    /// <inheritdoc/>
    public void OnPointer(int slot) {
        var seatLook = m_seatFeel.Look(slot: slot);

        if (seatLook.Arming == WorldSeatLookArming.None) {
            // None fully disables orbiting. Drain anyway: motion accumulated while disabled must not be banked and
            // then applied in one jump the moment a live playerDefaults.seatLook edit re-arms the drag.
            _ = m_pointer.TakeMotion(slot: slot);

            return;
        }

        if ((ArmingButtonIndex(arming: seatLook.Arming) is { } armingButton) && !m_pointer.IsButtonDown(slot: slot, button: armingButton)) {
            // Armed by a button that is not down: same rule as above — the free cursor's motion is browsing, and
            // banking it would make the next press jump.
            _ = m_pointer.TakeMotion(slot: slot);

            return;
        }

        var motion = m_pointer.TakeMotion(slot: slot);

        if (motion == Vector2.Zero) {
            return;
        }

        // Dragging right swings the camera to show the player's right side; dragging down raises the camera to look
        // down at the player (WoW default) — see WorldCameraOrbit.Nudge's sign doc. The authored invert flags flip
        // either axis's sign before the nudge.
        var dYaw = ((motion.X * seatLook.YawSensitivity) * (seatLook.InvertYaw ? -1f : 1f));
        var dPitch = ((motion.Y * seatLook.PitchSensitivity) * (seatLook.InvertPitch ? -1f : 1f));

        m_orbit.Nudge(slot: slot, dYaw: dYaw, dPitch: dPitch, minPitch: seatLook.MinPitch, maxPitch: seatLook.MaxPitch);
    }

    // Maps an authored button-arming mode to the pointer button index the store keys held state by (0=left,
    // 1=right, 2=middle), or null for a mode with no arming button (Always — which orbits continuously — and,
    // already returned above, None). Shared with WorldCursorFeed's visibility rule (the cursor hides exactly while
    // this consumer would eat the motion), so the two read one mapping and can never disagree on which button arms.
    internal static int? ArmingButtonIndex(WorldSeatLookArming arming) => arming switch {
        WorldSeatLookArming.LeftButton => 0,
        WorldSeatLookArming.RightButton => 1,
        WorldSeatLookArming.MiddleButton => 2,
        _ => null,
    };
}
