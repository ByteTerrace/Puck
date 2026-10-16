namespace Puck.Input.Devices;

/// <summary>
/// A parser with periodic device work of its own, such as re-arming a firmware setting the controller drops or
/// restoring an indicator once the controller can show it. The hosting <see cref="GamepadDevice"/> calls
/// <see cref="ServiceAsync"/> from its single I/O loop after every read, whether or not a report arrived, so the
/// work stays serialized with the reads and writes on that device.
/// </summary>
public interface IServicedGamepadParser {
    /// <summary>Performs whatever periodic work is due.</summary>
    /// <param name="timestamp">The device clock's current <see cref="TimeProvider"/> timestamp.</param>
    /// <param name="timestampFrequency">The device clock's timestamp ticks per second.</param>
    /// <param name="cancellationToken">The token that cancels any write.</param>
    /// <returns>A task that completes when any due write has been issued.</returns>
    ValueTask ServiceAsync(long timestamp, long timestampFrequency, CancellationToken cancellationToken);
}
