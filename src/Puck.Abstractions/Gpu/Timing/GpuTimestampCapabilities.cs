namespace Puck.Abstractions.Gpu;

/// <summary>
/// A device's GPU timestamp capabilities, backend-neutral: the duration of one raw timestamp tick and the number of
/// meaningful bits each timestamp carries. Carries the tick→time conversion (with the valid-bits mask and a
/// wrap-around guard) so no call site has to remember either. On Vulkan the period is the device's
/// <c>timestampPeriod</c> and the bits are the graphics queue's valid bits; on Direct3D 12 the period is
/// <c>1e9 / ID3D12CommandQueue::GetTimestampFrequency</c> and the bits are a full 64.
/// </summary>
/// <param name="PeriodNanoseconds">The number of nanoseconds a raw timestamp value is incremented by per tick.</param>
/// <param name="ValidBits">The number of meaningful low-order bits in a raw timestamp value, or zero if timestamps are unsupported.</param>
public readonly record struct GpuTimestampCapabilities(double PeriodNanoseconds, uint ValidBits) {
    /// <summary>Gets a value indicating whether GPU timestamps are usable on this device.</summary>
    public bool IsSupported => ((ValidBits > 0u) && (PeriodNanoseconds > 0.0));
    /// <summary>Gets a mask selecting the meaningful low-order bits of a raw timestamp value.</summary>
    public ulong ValidBitsMask => ((ValidBits >= 64u)
        ? ulong.MaxValue
        : ((1UL << (int)ValidBits) - 1UL));

    /// <summary>Converts a start/end raw timestamp pair to elapsed milliseconds: masks both to the valid bits,
    /// applies a wrap-around guard, then scales by the tick period.</summary>
    /// <param name="startTicks">The raw timestamp written first.</param>
    /// <param name="endTicks">The raw timestamp written later.</param>
    /// <returns>The elapsed time in milliseconds, or 0 if timestamps are unsupported.</returns>
    public double TicksToMilliseconds(ulong startTicks, ulong endTicks) {
        if (!IsSupported) {
            return 0.0;
        }

        var mask = ValidBitsMask;
        var start = (startTicks & mask);
        var end = (endTicks & mask);
        // The counter can wrap within its valid bits between the two writes; recover the true delta if so.
        var delta = ((end >= start)
            ? (end - start)
            : (((mask - start) + end) + 1UL));

        return ((delta * PeriodNanoseconds) / 1_000_000.0);
    }
}
