namespace Puck.Input.Devices;

/// <summary>
/// A parser whose controller streams state continuously once initialized, so a stream that falls silent means
/// the link is gone even when the transport reports no removal (a Bluetooth pad that powered off or walked out of
/// range). The hosting <see cref="GamepadDevice"/> faults after <see cref="StreamSilenceTimeout"/> without a parsed
/// report, which releases the pad's held input and lets the manager reopen it.
/// </summary>
public interface IStreamingParser {
    /// <summary>Gets the longest silence of the parsed stream that the live controller can produce.</summary>
    TimeSpan StreamSilenceTimeout { get; }
}
