namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a device's GPU timestamp capabilities on the graphics queue: the duration of a timestamp tick
/// and the number of meaningful bits each timestamp carries.
/// </summary>
/// <param name="PeriodNanoseconds">The number of nanoseconds a raw timestamp value is incremented by per tick.</param>
/// <param name="GraphicsQueueValidBits">The number of meaningful high-order bits in a timestamp written by the graphics queue, or zero if timestamps are unsupported.</param>
public readonly record struct VulkanTimestampCapabilities(float PeriodNanoseconds, uint GraphicsQueueValidBits) {
    /// <summary>Gets a value indicating whether GPU timestamps are usable on the graphics queue.</summary>
    public bool IsSupported => ((GraphicsQueueValidBits > 0u) && (PeriodNanoseconds > 0.0f));
    /// <summary>Gets a mask selecting the meaningful low-order bits of a raw timestamp value, derived from <see cref="GraphicsQueueValidBits"/>.</summary>
    public ulong ValidBitsMask => ((GraphicsQueueValidBits >= 64u)
        ? ulong.MaxValue
        : ((1UL << (int)GraphicsQueueValidBits) - 1UL));
}
