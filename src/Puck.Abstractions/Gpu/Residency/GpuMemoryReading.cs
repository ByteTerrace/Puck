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
/// reading is immutable; its device publishes a new one at each refresh (<see cref="GpuMemoryReadingRefresh"/>), and
/// <see cref="Serial"/> moves when a budget changes.
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
    /// <summary>Gets the count of budget changes the device has published (<see cref="GpuMemoryReadingRefresh"/>): it
    /// moves when a budget, the segments or the estimate change, not for usage; zero when nothing was read.</summary>
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

/// <summary>
/// The one rule by which a device refreshes its memory reading, for every backend: it reads when its API signals a
/// change (Direct3D 12's budget-change event), when it has not read yet, and otherwise at most once every
/// <see cref="Interval"/>, since usage moves with every allocation and no API signals it; and a published reading's
/// <see cref="GpuMemoryReading.Serial"/> moves only when a budget, the segments or the estimate differ from the last
/// reading, never for usage alone. The serial is what a reader that derives something from the budgets (the account's
/// spend limits) watches, and usage is fresh in every reading whatever the serial. Called on one thread, the one that
/// refreshes.
/// </summary>
/// <param name="serial">The serial of the last reading the device's predecessor published, so a recreated device's
/// serial keeps moving forward; zero for none.</param>
public sealed class GpuMemoryReadingRefresh(ulong serial) {
    private long m_readAt;
    private bool m_read;

    /// <summary>Gets the longest a reading goes unrefreshed without a signal, in <see cref="System.Diagnostics.Stopwatch"/> ticks: one
    /// second.</summary>
    public static long Interval { get; } = System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Gets the serial of the last reading published, or the predecessor's before the first.</summary>
    public ulong Serial { get; private set; } = serial;

    /// <summary>Returns whether a read is due.</summary>
    /// <param name="timestamp">The time now, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    /// <param name="signalled">Whether the API signalled a change since the last read, or the device changed.</param>
    /// <returns>Whether to read.</returns>
    public bool Due(long timestamp, bool signalled) => (signalled || !m_read || ((timestamp - m_readAt) >= Interval));
    /// <summary>Records a read and returns the reading to publish, its serial moved when its budgets, segments or estimate
    /// differ from <paramref name="last"/>'s.</summary>
    /// <param name="last">The reading published before.</param>
    /// <param name="segments">The device's segments.</param>
    /// <param name="readings">One reading for each of <paramref name="segments"/>, in its order.</param>
    /// <param name="estimated">Whether the figures are estimates.</param>
    /// <param name="timestamp">The time of the read, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    /// <returns>The reading.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="last"/> or <paramref name="segments"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="readings"/> does not hold one reading per segment, in order.</exception>
    public GpuMemoryReading Publish(GpuMemoryReading last, GpuMemorySegments segments, ReadOnlySpan<GpuSegmentReading> readings, bool estimated, long timestamp) {
        ArgumentNullException.ThrowIfNull(argument: last);
        var changed = ((last.State != GpuMemoryReadingState.Read) || !ReferenceEquals(objA: last.Layout, objB: segments) || (last.Estimated != estimated) || (last.Segments.Count != readings.Length));

        for (var index = 0; (!changed && (index < readings.Length)); index++) {
            changed = (last.Segments[index].Budget != readings[index].Budget);
        }

        var reading = GpuMemoryReading.Read(estimated: estimated, readings: readings, segments: segments, serial: (changed ? (Serial + 1UL) : Serial));

        Serial = reading.Serial;
        m_readAt = timestamp;
        m_read = true;

        return reading;
    }
}
