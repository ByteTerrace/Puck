namespace Puck.Hosting;

/// <summary>
/// Maps a backend's native 32-bit millisecond timestamp (Win32 <c>GetMessageTime</c>, X11 event <c>time</c>)
/// onto the shared <see cref="InputClock"/> base. Pinned once from the first event — <c>(osReference ↔
/// engineReference)</c> — then converts later OS stamps by their delta from the pin, so an event carries the
/// time it actually occurred rather than the time it was dequeued.
/// </summary>
/// <remarks>
/// The OS counters are 32-bit and wrap (~49.7 days for a millisecond counter), so the delta is taken in
/// 32-bit space with unsigned wraparound. The result is clamped to <c>[engineReference, ceiling]</c> so a
/// stale or wrapped stamp degrades to the arrival time rather than producing a tick in the past or future.
/// </remarks>
public readonly struct OsTimeCorrelator {
    private readonly uint m_osReference;
    private readonly ulong m_engineReference;
    private readonly ulong m_osFrequency;

    private OsTimeCorrelator(uint osReference, ulong engineReference, ulong osFrequency) {
        m_engineReference = engineReference;
        m_osFrequency = osFrequency;
        m_osReference = osReference;
    }

    /// <summary>Pins the correlator to an OS stamp and the engine tick observed at the same instant.</summary>
    /// <param name="osReference">The backend's OS timestamp (32-bit units) at the pin.</param>
    /// <param name="engineReference">The <see cref="InputClock.NowTicks"/> captured at the pin.</param>
    /// <param name="osFrequency">The OS counter's units per second (1000 for a millisecond counter).</param>
    public static OsTimeCorrelator Pin(uint osReference, ulong engineReference, ulong osFrequency) {
        return new OsTimeCorrelator(
            engineReference: engineReference,
            osFrequency: osFrequency,
            osReference: osReference
        );
    }

    /// <summary>Converts an OS stamp to engine ticks, clamped to <c>[reference, ceiling]</c>.</summary>
    /// <param name="osStamp">The event's OS timestamp (same 32-bit base as the pin).</param>
    /// <param name="engineCeiling">The current <see cref="InputClock.NowTicks"/>; the result never exceeds it.</param>
    public ulong ToEngineTicks(uint osStamp, ulong engineCeiling) {
        var osDelta = (ulong)unchecked((osStamp - m_osReference));

        // floor(osDelta × PerSecond ÷ osFrequency), overflow-safe (split whole/fraction), exact.
        var whole = (osDelta / m_osFrequency);
        var fraction = (osDelta % m_osFrequency);
        var engineDelta = ((whole * EngineTicks.PerSecond) + ((fraction * EngineTicks.PerSecond) / m_osFrequency));
        var engineTick = (m_engineReference + engineDelta);

        if (engineTick < m_engineReference) {
            return m_engineReference;
        }

        return ((engineTick > engineCeiling)
            ? engineCeiling
            : engineTick);
    }
}
