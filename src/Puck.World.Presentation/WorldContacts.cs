using System.Numerics;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>One pointing contact a seat holds down: a finger, a pen tip, or the mouse while its primary button is held.
/// Presentation state: what a hand mirroring the player's touch, a cursor or a read-back draws from.</summary>
/// <param name="Device">The device kind.</param>
/// <param name="Id">The contact's number: 0 for the mouse, from 1 for a finger or pen tip.</param>
/// <param name="Position">The contact's last position, in client pixels.</param>
/// <param name="Pressure">The contact's last pressure, 0 to 1, or <see cref="float.NaN"/> when the device reports none.</param>
/// <param name="Timestamp">When the contact last changed, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
/// <param name="DeviceId">The physical device, or the default value for a synthetic contact.</param>
public readonly record struct WorldContact(ContactDevice Device, byte Id, Vector2 Position, float Pressure, long Timestamp, InputDeviceId DeviceId);
/// <summary>One contact transition waiting for its seat's view to cast it into the world.</summary>
/// <param name="Slot">The 0-based seat slot the contact belongs to.</param>
/// <param name="Device">The device kind.</param>
/// <param name="Id">The contact's number.</param>
/// <param name="Phase">The phase.</param>
/// <param name="Position">The contact's position, in client pixels.</param>
public readonly record struct WorldContactTransition(int Slot, ContactDevice Device, byte Id, ContactPhase Phase, Vector2 Position);
/// <summary>
/// Every local seat's pointing contacts: the one store the hardware (through <see cref="WorldPointerSink"/>) and the
/// console (<c>world.view.touch</c>) write, so a finger, a pen tip and the mouse's primary button reach the world through
/// one door. It keeps each seat's live contacts for presentation readers, and queues each transition for
/// <c>WorldPointerRayCapture</c>, which casts it through the seat's view into the seat's next submission.
/// </summary>
/// <remarks>
/// <para>Window-pump-thread only: the window input observers, the console's immediate verbs and the snapshot capture
/// all run there.</para>
/// <para>A move of a contact whose last queued transition is a move replaces that move's position, so a digitizer that
/// reports faster than frames are captured queues one move per contact per frame; the queue holds at most
/// <see cref="Capacity"/> transitions and drops a new one past that, which a capture every frame never reaches.</para>
/// </remarks>
public sealed class WorldContacts {
    /// <summary>The most transitions the queue holds between two captures.</summary>
    public const int Capacity = 256;

    private readonly List<WorldContact>[] m_live = CreateSeats();
    private readonly List<WorldContactTransition> m_pending = [];

    private static List<WorldContact>[] CreateSeats() {
        var seats = new List<WorldContact>[PlayerRoster.MaxSlots];

        for (var slot = 0; (slot < seats.Length); slot++) {
            seats[slot] = [];
        }

        return seats;
    }

    /// <summary>Gets the number of transitions waiting for a capture.</summary>
    public int PendingCount => m_pending.Count;

    /// <summary>Records one contact transition for a seat: updates the seat's live contacts and queues the transition.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="device">The device kind.</param>
    /// <param name="id">The contact's number.</param>
    /// <param name="phase">The phase.</param>
    /// <param name="position">The position, in client pixels.</param>
    /// <param name="pressure">The pressure, 0 to 1, or <see cref="float.NaN"/> for none.</param>
    /// <param name="timestamp">When it happened, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    /// <param name="deviceId">The physical device, or the default value for a synthetic contact.</param>
    /// <returns><see langword="false"/> when the slot is out of range, the transition contradicts the seat's live
    /// contacts (a move or lift of a contact that is not down, a touch of one already down), or the queue is full.</returns>
    public bool Record(int slot, ContactDevice device, byte id, ContactPhase phase, Vector2 position, float pressure, long timestamp, InputDeviceId deviceId) {
        if (((uint)slot) >= ((uint)m_live.Length)) {
            return false;
        }

        var live = m_live[slot];
        var index = IndexOf(live: live, device: device, id: id);

        if ((phase == ContactPhase.Down) == (index >= 0)) {
            return false;
        }

        var contact = new WorldContact(Device: device, Id: id, Position: position, Pressure: pressure, Timestamp: timestamp, DeviceId: deviceId);

        if ((phase == ContactPhase.Move) && TryCoalesceMove(slot: slot, device: device, id: id, position: position)) {
            live[index] = contact;

            return true;
        }
        // A full queue refuses the transition whole, so the live contacts never run ahead of what the capture sends.
        if (m_pending.Count == Capacity) {
            return false;
        }

        switch (phase) {
            case ContactPhase.Down:
                live.Add(item: contact);

                break;
            case ContactPhase.Move:
                live[index] = contact;

                break;
            default:
                live.RemoveAt(index: index);

                break;
        }

        m_pending.Add(item: new WorldContactTransition(Slot: slot, Device: device, Id: id, Phase: phase, Position: position));

        return true;
    }
    private static int IndexOf(List<WorldContact> live, ContactDevice device, byte id) {
        for (var index = 0; (index < live.Count); index++) {
            if ((live[index].Device == device) && (live[index].Id == id)) {
                return index;
            }
        }

        return -1;
    }
    /// <summary>Cancels every live contact of every seat, as a focus loss or a seat reassignment does: each gets one
    /// <see cref="ContactPhase.Cancel"/> transition at its last position.</summary>
    public void CancelAll() {
        for (var slot = 0; (slot < m_live.Length); slot++) {
            foreach (var contact in m_live[slot]) {
                if (m_pending.Count < Capacity) {
                    m_pending.Add(item: new WorldContactTransition(Slot: slot, Device: contact.Device, Id: contact.Id, Phase: ContactPhase.Cancel, Position: contact.Position));
                }
            }

            m_live[slot].Clear();
        }
    }
    /// <summary>Gets a seat's live contacts, in the order they touched.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The contacts; empty for a slot out of range.</returns>
    public IReadOnlyList<WorldContact> Live(int slot) => ((((uint)slot) < ((uint)m_live.Length)) ? m_live[slot] : []);
    /// <summary>Takes every queued transition, oldest first, into a caller-owned list.</summary>
    /// <param name="into">The list the transitions are appended to.</param>
    public void TakePending(List<WorldContactTransition> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.AddRange(collection: m_pending);
        m_pending.Clear();
    }

    // Replaces the position of the contact's queued move when nothing about that contact was queued after it.
    private bool TryCoalesceMove(int slot, ContactDevice device, byte id, Vector2 position) {
        for (var index = (m_pending.Count - 1); (index >= 0); index--) {
            var queued = m_pending[index];

            if ((queued.Slot != slot) || (queued.Device != device) || (queued.Id != id)) {
                continue;
            }
            if (queued.Phase != ContactPhase.Move) {
                return false;
            }

            m_pending[index] = (queued with { Position = position });

            return true;
        }

        return false;
    }
}
