namespace Puck.Abstractions;

/// <summary>
/// A platform capability that blocks the calling thread until a short relative duration elapses, using the highest-
/// resolution wait the platform offers (e.g. a Windows high-resolution waitable timer, ~0.5 ms granularity) instead of
/// the coarse ~15.6 ms system-tick quantization of a plain sleep. The host pacer uses it for the bulk of an inter-frame
/// wait and spins only the final sub-millisecond remainder, so a variable-refresh present cadence is hit accurately
/// without busy-waiting the whole interval.
/// </summary>
public interface IPrecisionWaiter {
    /// <summary>Blocks until <paramref name="dueTime"/> elapses, using the platform's high-resolution wait.</summary>
    /// <param name="dueTime">The relative duration to wait; non-positive returns immediately.</param>
    /// <returns><see langword="true"/> if the high-resolution wait was used; <see langword="false"/> if unsupported (the caller should fall back to a coarse sleep).</returns>
    bool Wait(TimeSpan dueTime);
}
