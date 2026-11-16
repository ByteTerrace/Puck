namespace Puck.Abstractions.Gpu;

/// <summary>Whether a device's memory reading holds figures.</summary>
public enum GpuMemoryReadingState {
    /// <summary>No device is brought up, or the host has no real device: nothing was read.</summary>
    Absent,
    /// <summary>The device was lost and is being recreated: the reading it had no longer describes a device.</summary>
    Lost,
    /// <summary>The figures were read from the device's API.</summary>
    Read,
}

/// <summary>One segment's figures as its API last reported them, in bytes.</summary>
/// <param name="Segment">The segment.</param>
/// <param name="Budget">What the API says the process may use in the segment: <c>DXGI_QUERY_VIDEO_MEMORY_INFO.Budget</c>
/// or <c>VkPhysicalDeviceMemoryBudgetPropertiesEXT.heapBudget</c>, or the heap's size where the API reports no budget
/// (<see cref="GpuMemoryReading.Estimated"/>).</param>
/// <param name="Usage">What the API says the process uses in the segment, which includes driver and presentation
/// memory no counted allocation holds: <c>CurrentUsage</c> or <c>heapUsage</c>; <see langword="null"/> where the API
/// reports none.</param>
/// <param name="AvailableForReservation">What the process may reserve in the segment
/// (<c>AvailableForReservation</c>); <see langword="null"/> where the API has no reservation (Vulkan).</param>
/// <param name="CurrentReservation">What the process has reserved in the segment (<c>CurrentReservation</c>);
/// <see langword="null"/> where the API has no reservation (Vulkan).</param>
public readonly record struct GpuSegmentReading(GpuMemorySegment Segment, ulong Budget, ulong? Usage, ulong? AvailableForReservation, ulong? CurrentReservation);

/// <summary>
/// A device's memory budget reading: for each segment the device budgets, what the API says the process may use and
/// is using. A device that is absent, lost or being recreated reads as such (<see cref="State"/>), never as zeros. A
/// reading is immutable; its device publishes a new one when its API signals a change or its refresh finds one, and
/// <see cref="Serial"/> moves with each.
/// </summary>
public sealed class GpuMemoryReading {
    private GpuMemoryReading(GpuMemoryReadingState state, ulong serial, bool estimated, GpuMemorySegments? segments, GpuSegmentReading[] readings) {
        State = state;
        Serial = serial;
        Estimated = estimated;
        Layout = segments;
        Segments = readings;
    }

    /// <summary>Gets the reading of a host with no device brought up.</summary>
    public static GpuMemoryReading Absent { get; } = new(estimated: false, readings: [], segments: null, serial: 0UL, state: GpuMemoryReadingState.Absent);

    /// <summary>Gets whether the reading holds figures, or why it holds none.</summary>
    public GpuMemoryReadingState State { get; }
    /// <summary>Gets the count of readings the device has published, which moves each time its API signals a change or
    /// a refresh finds a budget changed; zero when nothing was read.</summary>
    public ulong Serial { get; }
    /// <summary>Gets whether the figures are estimates: Vulkan without <c>VK_EXT_memory_budget</c>, where each budget is
    /// the heap's size and no usage is reported.</summary>
    public bool Estimated { get; }
    /// <summary>Gets the device's segments and their pools, or <see langword="null"/> when nothing was read.</summary>
    public GpuMemorySegments? Layout { get; }
    /// <summary>Gets each segment's figures, in index order; empty when nothing was read.</summary>
    public IReadOnlyList<GpuSegmentReading> Segments { get; }

    /// <summary>Returns the reading of a device that was lost and is being recreated.</summary>
    /// <param name="serial">The serial of the last reading the lost device published.</param>
    /// <returns>The reading.</returns>
    public static GpuMemoryReading Lost(ulong serial) => new(estimated: false, readings: [], segments: null, serial: serial, state: GpuMemoryReadingState.Lost);
    /// <summary>Returns a reading of figures read from a device's API.</summary>
    /// <param name="segments">The device's segments.</param>
    /// <param name="readings">One reading for each of <paramref name="segments"/>, in its order.</param>
    /// <param name="estimated">Whether the figures are estimates.</param>
    /// <param name="serial">The reading's serial; positive.</param>
    /// <returns>The reading.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="readings"/> does not hold one reading per segment, in order.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="serial"/> is zero.</exception>
    public static GpuMemoryReading Read(GpuMemorySegments segments, ReadOnlySpan<GpuSegmentReading> readings, bool estimated, ulong serial) {
        ArgumentNullException.ThrowIfNull(argument: segments);
        ArgumentOutOfRangeException.ThrowIfZero(value: serial);

        if (readings.Length != segments.Segments.Count) {
            throw new ArgumentException(message: $"A reading holds one figure set per segment: {segments.Segments.Count} segments, {readings.Length} readings.", paramName: nameof(readings));
        }
        for (var index = 0; (index < readings.Length); index++) {
            if (readings[index].Segment != segments.Segments[index]) {
                throw new ArgumentException(message: $"Reading {index} names segment {readings[index].Segment.Name}, not {segments.Segments[index].Name}.", paramName: nameof(readings));
            }
        }

        return new(estimated: estimated, readings: readings.ToArray(), segments: segments, serial: serial, state: GpuMemoryReadingState.Read);
    }
}
