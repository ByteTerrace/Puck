namespace Puck.Abstractions.Pacing;

/// <summary>
/// A platform capability that blocks the calling thread until a short relative duration elapses, using the highest-
/// resolution wait the platform offers (e.g. a Windows high-resolution waitable timer, ~0.5 ms granularity) instead of
/// the coarse ~15.6 ms system-tick quantization of a plain sleep. The host pacer sleeps on it for the whole remainder of
/// an inter-frame wait and never spins, so a variable-refresh present cadence is hit accurately without making
/// busy-waiting a standing frame tax.
/// </summary>
public interface IPrecisionWaiter {
    /// <summary>Blocks until <paramref name="duration"/> elapses, using the platform's high-resolution wait.</summary>
    /// <param name="duration">The relative duration to wait; non-positive returns immediately.</param>
    /// <returns><see langword="true"/> if the high-resolution wait was used; <see langword="false"/> if unsupported (the caller should fall back to a coarse sleep).</returns>
    bool TryWait(TimeSpan duration);
    /// <summary>Blocks until <paramref name="duration"/> elapses or <paramref name="wake"/> is set, whichever comes
    /// first: one operating-system wait on the platform's high-resolution timer and the event together, so a serviced
    /// wait (<c>HostWaits.Pace</c>) ends early for work without giving up the timer's precision at its deadline.</summary>
    /// <param name="duration">The relative duration to wait; non-positive returns immediately, reporting whether the
    /// event was already set.</param>
    /// <param name="wake">The event that ends the wait early. A set auto-reset event is consumed by the wait it
    /// ends.</param>
    /// <param name="woken">Whether <paramref name="wake"/> ended the wait rather than the duration.</param>
    /// <returns><see langword="true"/> if the high-resolution wait was used; <see langword="false"/> if unsupported (the
    /// caller falls back to a coarse wait on the event).</returns>
    bool TryWait(TimeSpan duration, WaitHandle wake, out bool woken);
}
