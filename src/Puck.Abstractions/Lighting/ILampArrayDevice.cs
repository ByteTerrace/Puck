namespace Puck.Abstractions.Lighting;

/// <summary>
/// A backend-neutral handle to an opened RGB lamp array (a per-key keyboard, a mouse, a light strip). Mirrors
/// how windowing types live in <c>Puck.Abstractions</c>: the concrete implementation (the Windows HID LampArray
/// transport) lives in <c>Puck.Platform</c>, and nothing here binds to an OS. A consumer reads the lamp table
/// (<see cref="TryGetLampInfo"/>), takes control of the device (<see cref="TrySetAutonomousMode"/> with
/// <c>false</c>), pushes color updates in batches, and restores autonomous mode on <see cref="System.IDisposable.Dispose"/>.
/// </summary>
public interface ILampArrayDevice : IDisposable {
    /// <summary>Gets a stable identity for the device (its device-interface path), unique per physical port.</summary>
    string DeviceId { get; }

    /// <summary>Gets the kind of device this array lights.</summary>
    LampArrayKind Kind { get; }

    /// <summary>Gets the number of individually addressable lamps; lamp ids run <c>0</c>..(<see cref="LampCount"/> - 1).</summary>
    int LampCount { get; }

    /// <summary>Gets the device's minimum update interval in milliseconds — the fastest cadence a driver should write at.</summary>
    int MinUpdateIntervalInMilliseconds { get; }

    /// <summary>Gets the attributes of a lamp by its index.</summary>
    /// <param name="index">The lamp index, <c>0</c>..(<see cref="LampCount"/> - 1).</param>
    /// <param name="info">When this method returns <see langword="true"/>, the lamp's attributes.</param>
    /// <returns><see langword="true"/> when <paramref name="index"/> is in range; otherwise <see langword="false"/>.</returns>
    bool TryGetLampInfo(int index, out LampInfo info);

    /// <summary>
    /// Sets a batch of lamps by id. The two spans are parallel: <c>colors[i]</c> is applied to
    /// <c>lampIds[i]</c>. The implementation fragments the batch into as many device writes as the transport
    /// requires; the whole batch is applied atomically from the consumer's point of view (a single logical update).
    /// </summary>
    /// <param name="lampIds">The lamp ids to set.</param>
    /// <param name="colors">The colors to apply, one per id (same length as <paramref name="lampIds"/>).</param>
    void UpdateLamps(ReadOnlySpan<int> lampIds, ReadOnlySpan<LampColor> colors);

    /// <summary>Sets every lamp on the device to one color in a single update.</summary>
    /// <param name="color">The color to apply to all lamps.</param>
    void UpdateAllLamps(LampColor color);

    /// <summary>
    /// Sets the device's autonomous mode. Pass <see langword="false"/> to take host control (stop the device's
    /// built-in effects so host color updates take effect); pass <see langword="true"/> to hand control back to
    /// the device's firmware. A well-behaved consumer restores autonomous mode on dispose.
    /// </summary>
    /// <param name="enabled"><see langword="true"/> to hand control to the device firmware; <see langword="false"/> to take host control.</param>
    /// <returns><see langword="true"/> when the control report was written; otherwise <see langword="false"/>.</returns>
    bool TrySetAutonomousMode(bool enabled);
}
