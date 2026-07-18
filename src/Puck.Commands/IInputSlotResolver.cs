namespace Puck.Commands;

/// <summary>Maps a process-local input device to the stable logical slot written into command snapshots.</summary>
public interface IInputSlotResolver {
    /// <summary>Raised before an existing device-to-slot assignment changes or is removed. The router uses this edge
    /// to cancel state carried under the old assignment before later physical releases resolve against a new slot.
    /// Implementations must raise the event on the snapshot-consumer thread.</summary>
    event Action<InputDeviceId>? DeviceSlotChanging;

    /// <summary>Probes the logical slot for <paramref name="device"/> without changing resolver state. A negative
    /// result drops the signal when no lane is admissible.</summary>
    int ResolveSlot(InputDeviceId device);

    /// <summary>Commits <paramref name="device"/> to a probed logical <paramref name="slot"/> after the router has
    /// accepted at least one binding on an active command map.</summary>
    /// <returns><see langword="true"/> when this call created the device-to-slot assignment; otherwise
    /// <see langword="false"/>.</returns>
    bool CommitSlot(InputDeviceId device, int slot);
}
