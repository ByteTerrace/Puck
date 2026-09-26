namespace Puck.Commands;

/// <summary>Maps a process-local input device to the stable logical slot written into command snapshots.</summary>
public interface IInputSlotResolver {
    /// <summary>Raised before an existing device-to-slot assignment changes or is removed. The router uses this edge
    /// to cancel state carried under the old assignment before later physical releases resolve against a new slot.
    /// Implementations must raise the event on the snapshot-consumer thread.</summary>
    event Action<InputDeviceId>? DeviceSlotChanging;

    /// <summary>Raised after a logical slot stops holding an occupant. The router ends every value sustained on the
    /// slot (<see cref="InputRouter.Sustain"/>), so no host producer's value outlives the occupancy it was set for.
    /// The default implementation never raises it, for a resolver whose slots are never vacated.</summary>
    event Action<int>? SlotVacated {
        add { }
        remove { }
    }

    /// <summary>Probes the logical slot for <paramref name="device"/> without changing resolver state. A negative
    /// result drops the signal when no lane is admissible.</summary>
    int ResolveSlot(InputDeviceId device);
    /// <summary>Commits <paramref name="device"/> to a probed logical <paramref name="slot"/> after the router has
    /// accepted at least one binding on an active command map.</summary>
    /// <returns><see langword="true"/> when this call created the device-to-slot assignment; otherwise
    /// <see langword="false"/>.</returns>
    bool CommitSlot(InputDeviceId device, int slot);
    /// <summary>Records the physical kind of <paramref name="device"/> the first time any of its signals reach the
    /// router — before <see cref="ResolveSlot"/> or <see cref="CommitSlot"/> ever run for it, so a kind-aware
    /// seating policy already knows what it is resolving. The default implementation is a no-op, for a resolver
    /// that does not distinguish device kinds (e.g. a single-slot test double).</summary>
    /// <param name="device">The device the signal came from.</param>
    /// <param name="kind">The device's classified kind.</param>
    void ObserveDeviceKind(InputDeviceId device, InputDeviceKind kind) { }
}
