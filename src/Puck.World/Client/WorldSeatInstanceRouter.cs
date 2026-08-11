namespace Puck.World.Client;

/// <summary>
/// The presentation-side follow-target table: per local roster slot, which running <see cref="WorldInstanceHost"/>
/// instance (and which of that instance's own local seats) the local client currently presents that seat from —
/// render, input re-route, HUD, and audio all resolve through this one table, shaped exactly like
/// <see cref="WorldPerceptionAnchor"/>: a small fixed array, one owning host, presentation state only, never read by
/// simulation code. Every slot seeds at the boot instance under its own slot number (the
/// 1:1 seat-to-body convention every seat seam already follows). <see cref="WorldInstanceHost"/> is its sole writer:
/// a committed transfer publishes the landed member's new location, and a committed roster departure resets the
/// vacated slot to its boot default. Both are conclusions of the same authoritative body transition, never a
/// presentation-side guess.
/// </summary>
/// <remarks>Sized for four seats today (<see cref="WorldSeatBindings.SeatCount"/>), and shaped so a later wave that
/// widens local seating never has to touch this table's own contract — only the loop bound moves. A seat is never
/// "unrouted": absent any transfer, its location is simply the boot instance at its own slot, exactly the
/// presentation state that existed before this table did.
///
/// <see cref="Publish"/> writes from <see cref="WorldInstanceHost.ApplyTransfer"/>'s commit loop (the fixed-step
/// thread); <see cref="Location"/> is read from the window-pump thread and from intent/session routing. The instance
/// name and its instance-local slot are one routing identity, so each array entry is an immutable reference published
/// and read through <see cref="Volatile"/> as a single value.
/// A reader therefore observes either the complete old route or the complete new one, never a cross-instance torn
/// pair that could submit intent or a leave request to the wrong body.</remarks>
internal sealed class WorldSeatInstanceRouter {
    private readonly SeatLocation[] m_locations;

    /// <summary>Initializes the router with every seat presenting from the boot instance, at its own slot number —
    /// the pre-first-publish default, matching every seat seam's own boot-time 1:1 seat-to-body convention.</summary>
    public WorldSeatInstanceRouter() {
        m_locations = new SeatLocation[WorldSeatBindings.SeatCount];

        for (var slot = 0; (slot < m_locations.Length); slot++) {
            m_locations[slot] = new SeatLocation(InstanceName: WorldInstanceHost.BootInstanceName, InstanceSlot: slot);
        }
    }

    /// <summary>The instance (and instance-local seat) local seat <paramref name="slot"/> currently presents
    /// from.</summary>
    /// <param name="slot">The 0-based local roster slot.</param>
    /// <returns>The seat's current presenting location — the boot instance at <paramref name="slot"/> for a slot
    /// out of range, matching this table's own boot-seeded default.</returns>
    public SeatLocation Location(int slot) {
        if ((uint)slot >= (uint)m_locations.Length) {
            return new SeatLocation(InstanceName: WorldInstanceHost.BootInstanceName, InstanceSlot: slot);
        }

        return Volatile.Read(location: ref m_locations[slot]);
    }

    /// <summary>Raised by <see cref="Publish"/> the instant a seat's presenting instance actually changes — a
    /// crossing in or out, never a same-instance seat-index correction. Lets presentation state keyed by "which
    /// world currently frames this seat" react at the transition itself rather than waiting for its own next
    /// unrelated tick; <see cref="WorldSeatViewInput"/> reclamps a carried live orbit pitch here. Presentation-only,
    /// like the rest of this table — never subscribed to by simulation code.</summary>
    public event Action<int>? LocationChanged;

    /// <summary>Publishes seat <paramref name="slot"/>'s new presenting location. Called only from
    /// <see cref="WorldInstanceHost.ApplyTransfer"/>'s commit loop, the transfer substrate's one point where a
    /// followed seat's landed body (and the instance it landed in) are both already known — unconditional across
    /// boot&lt;-&gt;anywhere and anywhere&lt;-&gt;anywhere, the same commit that generalizes
    /// <see cref="Server.WorldServer"/>'s own roster bookkeeping past the boot-only special case.</summary>
    /// <param name="slot">The 0-based local roster slot.</param>
    /// <param name="instanceName">The instance the seat now presents from.</param>
    /// <param name="instanceSlot">The 0-based local seat slot within that instance the seat now presents
    /// from.</param>
    internal void Publish(int slot, string instanceName, int instanceSlot) {
        if ((uint)slot >= (uint)m_locations.Length) {
            return;
        }

        var previous = Volatile.Read(location: ref m_locations[slot]);
        var crossed = !string.Equals(a: previous.InstanceName, b: instanceName, comparisonType: StringComparison.Ordinal);

        Volatile.Write(location: ref m_locations[slot], value: new SeatLocation(InstanceName: instanceName, InstanceSlot: instanceSlot));

        if (crossed) {
            LocationChanged?.Invoke(obj: slot);
        }
    }
}

/// <summary>One seat's presenting location — see <see cref="WorldSeatInstanceRouter"/>.</summary>
/// <param name="InstanceName">The console-facing instance name (<see cref="WorldInstanceHost.BootInstanceName"/>
/// included).</param>
/// <param name="InstanceSlot">The 0-based local seat slot within that instance.</param>
internal sealed record SeatLocation(string InstanceName, int InstanceSlot);
