using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>The roster's seat-vacated fact and the edge it raises. Split from <see cref="PlayerRoster"/>'s main file
/// purely for length (LEN001); every member here shares that type's single-threaded, no-lock discipline and its
/// fields.</summary>
public sealed partial class PlayerRoster {
    /// <inheritdoc/>
    /// <remarks>Raised by <see cref="VacateSeat"/>, the one seat-vacated fact every departure emits.</remarks>
    public event Action<int>? SlotVacated;

    /// <summary>The client-visible seat-vacated fact: the slot stops holding a participant, its claim and every
    /// per-claim cache die with it, the devices that were driving it are unmapped, and its authored mode-family
    /// states reset to their defaults (<see cref="WorldSeatBindings.ResetSeatModes"/>). Server-side teardown is not
    /// part of this — the caller has already decided (and performed) whatever the server half of the departure is,
    /// which is exactly why this is a fact rather than a verb.</summary>
    /// <remarks>One fact, two producers. <see cref="Leave"/> emits it after the server accepts its
    /// <see cref="SessionRequest.Leave"/> (park-with-grace, reap-on-empty and the never-leaves-slot-0 policy are all
    /// that method's own, and stay there). A same-process world transfer emits it after its departure becomes certain
    /// (<c>WorldInstanceHost.TryTransferMember</c>), whose server half is deliberately a non-parking, non-reaping
    /// detach — so it must reach the roster here rather than acquire leave's teardown, and the roster must not carry a
    /// transfer-shaped special case to notice it. Nothing here touches the simulation directly: the one value that
    /// reaches a tick is what the slot sustained on the input router, which <see cref="SlotVacated"/> ends.</remarks>
    /// <param name="slot">The slot index (0-based).</param>
    /// <returns><see langword="true"/> when a participant was removed; <see langword="false"/> for an out-of-range or
    /// already-empty slot.</returns>
    public bool VacateSeat(int slot) {
        if (
            (((uint)slot) >= MaxSlots) ||
            (m_slots[slot] is null)
        ) {
            return false;
        }

        m_slots[slot] = null;

        // Release the slot's claim (if any): a claim is a property of THIS occupancy, and a vacated slot rejoined by
        // an ordinary human must report its own Principal.Seat from PrincipalOf, never the departed claimant's.
        m_slotPrincipal[slot] = null;
        // The departed claimant's remembered target, cached handle, locked subject, and alarm ALL die with the claim —
        // see each field's own remarks. The subject was originally left out of this block (only TryClaimSlot cleared
        // it), which was unobservable solely because every re-claim happened to route through TryClaimSlot; a future
        // path re-seating a vacated slot any other way would have inherited the departed claimant's locked-in subject
        // and had the belt "confirm" the new claimant's handle against the old claimant's body.
        m_slotDrivenBody[slot] = null;
        m_slotDriveHandle[slot] = null;
        m_slotDriveSubject[slot] = null;
        m_slotDriveAlarm[slot] = null;

        // Drop any devices that were driving this slot so a reconnecting pad re-joins cleanly.
        foreach (var device in m_deviceToSlot.Where(predicate: pair => (pair.Value == slot)).Select(selector: pair => pair.Key).ToArray()) {
            DeviceSlotChanging?.Invoke(obj: device);
            _ = m_deviceToSlot.Remove(key: device);
        }

        // The departed occupant's AUTHORED mode states die with the occupancy too: a rejoiner arrives at Live and
        // must derive its group from the family defaults, never from a player.mode flip the previous occupant left
        // published on this slot.
        m_seatBindings.ResetSeatModes(slot: slot);

        m_revision++;

        // Last, once the slot is empty: the router ends every value sustained on it, a typed pointer ray included.
        SlotVacated?.Invoke(obj: slot);

        return true;
    }
}
