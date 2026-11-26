using System.Diagnostics;
using System.Numerics;
using Puck.Commands;
using Puck.Input;

namespace Puck.Platform;

/// <summary>
/// The events one native window has decoded from its OS message stream but not yet handed to the host. Every window
/// backend decodes different bytes and ends up with the same queue, so the queue itself lives here once: the backend
/// enqueues while it pumps, and the host drains through <see cref="TryDequeue"/> until it reports empty.
/// </summary>
/// <remarks>
/// <para>Touch and pen contacts enter only through <see cref="EnqueueContact"/>, which keeps the one contact model
/// every backend shares: each live OS contact holds a number from 1 to <see cref="SurfaceContact.MaximumContacts"/>
/// within its device kind, the lowest free when it touched, until it lifts or is cancelled. A transition no live
/// contact explains (a move or lift of an unknown contact) is dropped, a second touch of a live contact is a move, and
/// an eleventh finger is ignored until a number frees. So the host never sees a lift without its touch, and a binding
/// on <c>touch.contact1</c> means one finger for the whole of its stroke.</para>
/// <para>Single-threaded by construction — a window pumps and drains on its own thread, so no synchronization is
/// taken. The disposal guard stays with the backend, because whether the window is still open is the backend's state,
/// not the queue's.</para>
/// </remarks>
public sealed class WindowInputQueue {
    private readonly Queue<WindowInputEvent> m_events = [];
    private readonly List<LiveContact> m_contacts = [];

    private readonly record struct LiveContact(ContactDevice Device, InputDeviceId DeviceId, ulong OsId, byte Number, Vector2 Position);

    /// <summary>Gets the number of touch and pen contacts currently down.</summary>
    public int LiveContacts => m_contacts.Count;

    /// <summary>Appends one decoded event to the tail of the queue.</summary>
    /// <param name="item">The event the backend decoded; never a <see cref="WindowInputKind.Contact"/>, which enters
    /// through <see cref="EnqueueContact"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="item"/> is a contact.</exception>
    public void Enqueue(WindowInputEvent item) {
        if (item.Kind == WindowInputKind.Contact) {
            throw new ArgumentException(message: "A contact enters through EnqueueContact, which numbers it.", paramName: nameof(item));
        }

        m_events.Enqueue(item: item);
    }
    /// <summary>Appends one OS contact transition, numbered for its contact's life, or drops it when no live contact
    /// explains it.</summary>
    /// <param name="device">The device kind; <see cref="ContactDevice.Touch"/> or <see cref="ContactDevice.Pen"/>.</param>
    /// <param name="deviceId">The digitizer's identity.</param>
    /// <param name="osId">The OS's identity for the contact, unique among its device's live contacts.</param>
    /// <param name="phase">The OS transition: <see cref="CommandPhase.Started"/> for a touch,
    /// <see cref="CommandPhase.Active"/> for a move, <see cref="CommandPhase.Completed"/> for a lift and
    /// <see cref="CommandPhase.Canceled"/> for a contact the platform took away.</param>
    /// <param name="position">The contact's client position, in pixels, or <see langword="null"/> for a transition that
    /// carries no point (Wayland's lift, Win32's capture change), which happens where the contact last was.</param>
    /// <param name="pressure">The pressure, 0 to 1, or <see cref="float.NaN"/> when the device reports none.</param>
    /// <returns><see langword="true"/> when an event was enqueued.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="device"/> is not touch or pen.</exception>
    public bool EnqueueContact(ContactDevice device, InputDeviceId deviceId, ulong osId, CommandPhase phase, Vector2? position, float pressure) {
        if (device is not (ContactDevice.Touch or ContactDevice.Pen)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(device));
        }

        var index = Find(device: device, deviceId: deviceId, osId: osId);

        if (index < 0) {
            if ((phase != CommandPhase.Started) || (position is not { } touched) || (FreeNumber(device: device) is not { } number)) {
                return false;
            }

            m_contacts.Add(item: new LiveContact(Device: device, DeviceId: deviceId, OsId: osId, Number: number, Position: touched));
            Append(contact: m_contacts[^1], phase: CommandPhase.Started, position: touched, pressure: pressure);

            return true;
        }

        var live = m_contacts[index];
        var at = (position ?? live.Position);

        if (phase is CommandPhase.Completed or CommandPhase.Canceled) {
            m_contacts.RemoveAt(index: index);
        } else {
            m_contacts[index] = (live with { Position = at });
        }

        Append(
            contact: live,
            phase: ((phase == CommandPhase.Started) ? CommandPhase.Active : phase),
            position: at,
            pressure: pressure
        );

        return true;
    }
    /// <summary>Cancels every live contact, as the platform does when it takes the touch stream (a system gesture) or
    /// the window loses focus: one <see cref="CommandPhase.Canceled"/> event each, at its last position, so
    /// none is left down.</summary>
    /// <returns>The number of contacts cancelled.</returns>
    public int CancelContacts() {
        var count = m_contacts.Count;

        foreach (var contact in m_contacts) {
            Append(contact: contact, phase: CommandPhase.Canceled, position: contact.Position, pressure: float.NaN);
        }

        m_contacts.Clear();

        return count;
    }
    /// <summary>Takes the oldest pending event.</summary>
    /// <param name="inputEvent">The oldest pending event, or <see langword="default"/> when none is pending.</param>
    /// <returns><see langword="true"/> when an event was taken; <see langword="false"/> when the queue is empty.</returns>
    public bool TryDequeue(out WindowInputEvent inputEvent) => m_events.TryDequeue(result: out inputEvent);

    private void Append(LiveContact contact, CommandPhase phase, Vector2 position, float pressure) => m_events.Enqueue(item: WindowInputEvent.ContactEvent(
        contactId: contact.Number,
        device: contact.Device,
        deviceId: contact.DeviceId,
        phase: phase,
        position: position,
        pressure: pressure,
        timestamp: Stopwatch.GetTimestamp()
    ));
    private int Find(ContactDevice device, InputDeviceId deviceId, ulong osId) {
        for (var index = 0; (index < m_contacts.Count); index++) {
            var contact = m_contacts[index];

            if ((contact.Device == device) && (contact.DeviceId == deviceId) && (contact.OsId == osId)) {
                return index;
            }
        }

        return -1;
    }
    // The lowest contact number its device kind leaves free, or none when every finger's number is taken.
    private byte? FreeNumber(ContactDevice device) {
        var taken = 0;

        foreach (var contact in m_contacts) {
            if (contact.Device == device) {
                taken |= (1 << contact.Number);
            }
        }

        for (var number = 1; (number <= SurfaceContact.MaximumContacts); number++) {
            if ((taken & (1 << number)) == 0) {
                return ((byte)number);
            }
        }

        return null;
    }
}
