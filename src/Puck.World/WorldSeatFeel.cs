using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Every local seat's live CONTROL FEEL — the <see cref="WorldSeatLook"/> that seat's orbit currently responds by.
/// Per seat, never per world: an unclaimed seat feels the world's own authored <see cref="WorldPlayerDefaults.SeatLook"/>,
/// and a seat with a joined profile feels that profile's, delivered on the same
/// <see cref="WorldSeatBindings.SetProfileLayers"/> call that delivers its bindings. Two people on one couch can want
/// opposite sensitivities and neither has to lose.
/// </summary>
/// <remarks>Presentation-only, like <see cref="WorldCameraOrbit"/> beside it: nothing here rides a
/// <c>CommandSnapshot</c>. Read from the window-pump thread (<see cref="WorldCameraOrbitDrag"/>, per pointer event),
/// the render thread (<see cref="Client.WorldFrameSource"/>, per frame for <see cref="WorldSeatLook.WorldAxes"/>),
/// and the console (<c>world.view.orbit</c>), so each slot is a single <see cref="Volatile"/> reference — a record
/// swap is one atomic write and no reader can observe a half-applied policy. There is NO engine fallback anywhere in
/// this type: the world's own authored feel is the floor, and it is a required document member.</remarks>
internal sealed class WorldSeatFeel {
    private readonly WorldSeatLook?[] m_profileLooks = new WorldSeatLook?[PlayerRoster.MaxSlots];
    private WorldSeatLook m_worldLook;

    /// <summary>Initializes a new instance of the <see cref="WorldSeatFeel"/> class.</summary>
    /// <param name="worldLook">The world document's own authored feel — what a seat with no profile feels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="worldLook"/> is <see langword="null"/>.</exception>
    public WorldSeatFeel(WorldSeatLook worldLook) {
        ArgumentNullException.ThrowIfNull(argument: worldLook);

        m_worldLook = worldLook;
    }

    /// <summary>Gets a seat's live control feel: its profile's, or the world's own when that seat carries no profile
    /// yet. THE single resolution point — every null path answers here and nowhere else.</summary>
    /// <remarks>Three ways a seat can have no profile feel, all resolving to the world's own: no profile has been
    /// delivered for the seat yet; the seat's identity is replay-pinned (no document, so no feel — see
    /// <see cref="WorldIdentity.SeatLook"/>); or the slot is out of range. The first is a TIMING statement, not a
    /// locality one: a profile arriving over a link lands through the same
    /// <see cref="WorldSeatBindings.SetProfileLayers"/> door an in-process one does, so nothing here needs to change
    /// when the roster's profile-selection seam grows a socket.</remarks>
    /// <param name="slot">The 0-based seat slot.</param>
    public WorldSeatLook Look(int slot) {
        return (((uint)slot < PlayerRoster.MaxSlots) ? (Volatile.Read(location: ref m_profileLooks[slot]) ?? World) : World);
    }

    /// <summary>Gets the world document's own authored feel — the floor every unclaimed seat sits at.</summary>
    public WorldSeatLook World => Volatile.Read(location: ref m_worldLook);

    /// <summary>Sets a seat's profile-carried feel, or clears it back to the world's own.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="look">The profile's feel, or <see langword="null"/> to fall back to the world's.</param>
    public void SetProfileLook(int slot, WorldSeatLook? look) {
        if ((uint)slot < PlayerRoster.MaxSlots) {
            Volatile.Write(location: ref m_profileLooks[slot], value: look);
        }
    }

    /// <summary>Re-points the world's own feel after a definition delivery, so a live
    /// <c>world.row.set playerDefaults.seatLook</c> takes effect on the very next drag for every seat still sitting at
    /// the world's floor. A seat carrying its own profile feel is unaffected — that is the point of the split.</summary>
    /// <param name="worldLook">The delivered document's authored feel.</param>
    /// <exception cref="ArgumentNullException"><paramref name="worldLook"/> is <see langword="null"/>.</exception>
    public void SetWorldLook(WorldSeatLook worldLook) {
        ArgumentNullException.ThrowIfNull(argument: worldLook);

        Volatile.Write(location: ref m_worldLook, value: worldLook);
    }
}
