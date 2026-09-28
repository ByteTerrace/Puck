namespace Puck.Abstractions.Gpu;

/// <summary>
/// Hands out contiguous ranges of a fixed span <c>[0, size)</c>, such as a descriptor heap's slots: first-fit in address
/// order, each range returned whole by <see cref="Free"/>, and free neighbours coalesced so a freed span is one range
/// again. The span never grows: a request no free range can hold is refused with
/// <see cref="GpuRangeExhaustedException"/>, which states the request, the largest free range and the size. Allocating
/// and freeing allocate nothing once it is constructed, since the live and free ranges are held in arrays sized for the
/// most live ranges it admits.
/// </summary>
public sealed class GpuRangeAllocator {
    // Live ranges and free runs, each sorted by start. A span of n live ranges has at most n + 1 free runs.
    private readonly uint[] m_freeLengths;
    private readonly uint[] m_freeStarts;
    private readonly uint[] m_liveLengths;
    private readonly uint[] m_liveStarts;

    private int m_freeCount;
    private int m_liveCount;

    /// <summary>Initializes a new instance of the <see cref="GpuRangeAllocator"/> class, wholly free.</summary>
    /// <param name="size">The span's size; at least one.</param>
    /// <param name="maxRanges">The most ranges live at once; at least one.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> or <paramref name="maxRanges"/> is
    /// zero.</exception>
    public GpuRangeAllocator(uint size, int maxRanges) {
        ArgumentOutOfRangeException.ThrowIfZero(value: size);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: maxRanges
        );

        m_freeLengths = new uint[(maxRanges + 1)];
        m_freeStarts = new uint[(maxRanges + 1)];
        m_liveLengths = new uint[maxRanges];
        m_liveStarts = new uint[maxRanges];
        m_freeLengths[0] = size;
        m_freeCount = 1;
        MaxRanges = maxRanges;
        Size = size;
    }

    /// <summary>Gets the free count across every free range.</summary>
    public uint FreeCount {
        get {
            var total = 0u;

            for (var index = 0; (index < m_freeCount); index++) {
                total += m_freeLengths[index];
            }

            return total;
        }
    }
    /// <summary>Gets the free ranges, which counts how fragmented the span is: one when the free space is contiguous,
    /// and zero when the span is full.</summary>
    public int FreeRanges => m_freeCount;
    /// <summary>Gets the length of the largest free range, the largest request that fits.</summary>
    public uint LargestFree {
        get {
            var largest = 0u;

            for (var index = 0; (index < m_freeCount); index++) {
                largest = Math.Max(
                    val1: largest,
                    val2: m_freeLengths[index]
                );
            }

            return largest;
        }
    }
    /// <summary>Gets the ranges live now.</summary>
    public int LiveRanges => m_liveCount;
    /// <summary>Gets the most ranges live at once.</summary>
    public int MaxRanges { get; }
    /// <summary>Gets the span's size.</summary>
    public uint Size { get; }

    /// <summary>Allocates a range of <paramref name="count"/> from the lowest free range that holds it.</summary>
    /// <param name="count">The range's length; at least one.</param>
    /// <returns>The range's start.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is zero.</exception>
    /// <exception cref="GpuRangeExhaustedException">No free range holds <paramref name="count"/>, or
    /// <see cref="MaxRanges"/> ranges are already live.</exception>
    public uint Allocate(uint count) {
        ArgumentOutOfRangeException.ThrowIfZero(value: count);

        if (m_liveCount == MaxRanges) {
            throw new GpuRangeExhaustedException(
                largestFree: LargestFree,
                message: $"A range of {count} is refused: {MaxRanges} ranges are already live, the most this span of {Size} admits.",
                request: count,
                size: Size
            );
        }

        for (var index = 0; (index < m_freeCount); index++) {
            if (m_freeLengths[index] < count) {
                continue;
            }

            var start = m_freeStarts[index];

            if (m_freeLengths[index] == count) {
                RemoveAt(
                    count: ref m_freeCount,
                    index: index,
                    lengths: m_freeLengths,
                    starts: m_freeStarts
                );
            } else {
                m_freeStarts[index] += count;
                m_freeLengths[index] -= count;
            }

            InsertAt(
                count: ref m_liveCount,
                index: LowerBound(
                    count: m_liveCount,
                    starts: m_liveStarts,
                    value: start
                ),
                length: count,
                lengths: m_liveLengths,
                start: start,
                starts: m_liveStarts
            );

            return start;
        }

        var largest = LargestFree;

        throw new GpuRangeExhaustedException(
            largestFree: largest,
            message: $"A range of {count} is refused: the largest free range holds {largest} of this span's {Size}, and the span never grows.",
            request: count,
            size: Size
        );
    }
    /// <summary>Frees the live range that starts at <paramref name="start"/>, merging it with any free neighbour.</summary>
    /// <param name="start">The start <see cref="Allocate"/> returned.</param>
    /// <returns>The freed range's length.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="start"/> lies in free space, so the range was freed
    /// already, or it starts no live range, so it was never allocated.</exception>
    public uint Free(uint start) {
        var live = LowerBound(
            count: m_liveCount,
            starts: m_liveStarts,
            value: start
        );

        if (
            (live == m_liveCount) ||
            (m_liveStarts[live] != start)
        ) {
            var free = (LowerBound(
                count: m_freeCount,
                starts: m_freeStarts,
                value: (start + 1)
            ) - 1);

            throw new InvalidOperationException(message: ((
                (free >= 0) &&
                (start < (((ulong)m_freeStarts[free]) + m_freeLengths[free]))
            )
                ? $"The range at {start} is already free; a range is freed once."
                : $"No range was allocated at {start} in this span of {Size}."));
        }

        var length = m_liveLengths[live];

        RemoveAt(
            count: ref m_liveCount,
            index: live,
            lengths: m_liveLengths,
            starts: m_liveStarts
        );

        var at = LowerBound(
            count: m_freeCount,
            starts: m_freeStarts,
            value: start
        );
        var joinsPrevious = (
            (at > 0) &&
            ((m_freeStarts[(at - 1)] + m_freeLengths[(at - 1)]) == start)
        );
        var joinsNext = (
            (at < m_freeCount) &&
            ((start + length) == m_freeStarts[at])
        );

        if (joinsPrevious && joinsNext) {
            m_freeLengths[(at - 1)] += (length + m_freeLengths[at]);
            RemoveAt(
                count: ref m_freeCount,
                index: at,
                lengths: m_freeLengths,
                starts: m_freeStarts
            );
        } else if (joinsPrevious) {
            m_freeLengths[(at - 1)] += length;
        } else if (joinsNext) {
            m_freeStarts[at] = start;
            m_freeLengths[at] += length;
        } else {
            InsertAt(
                count: ref m_freeCount,
                index: at,
                length: length,
                lengths: m_freeLengths,
                start: start,
                starts: m_freeStarts
            );
        }

        return length;
    }

    private static void InsertAt(uint[] starts, uint[] lengths, ref int count, int index, uint start, uint length) {
        Array.Copy(
            destinationArray: starts,
            destinationIndex: (index + 1),
            length: (count - index),
            sourceArray: starts,
            sourceIndex: index
        );
        Array.Copy(
            destinationArray: lengths,
            destinationIndex: (index + 1),
            length: (count - index),
            sourceArray: lengths,
            sourceIndex: index
        );
        starts[index] = start;
        lengths[index] = length;
        count++;
    }
    // The first index whose start is at or past value.
    private static int LowerBound(uint[] starts, int count, uint value) {
        var low = 0;
        var high = count;

        while (low < high) {
            var middle = ((low + high) >>> 1);

            if (starts[middle] < value) {
                low = (middle + 1);
            } else {
                high = middle;
            }
        }

        return low;
    }
    private static void RemoveAt(uint[] starts, uint[] lengths, ref int count, int index) {
        count--;
        Array.Copy(
            destinationArray: starts,
            destinationIndex: index,
            length: (count - index),
            sourceArray: starts,
            sourceIndex: (index + 1)
        );
        Array.Copy(
            destinationArray: lengths,
            destinationIndex: index,
            length: (count - index),
            sourceArray: lengths,
            sourceIndex: (index + 1)
        );
    }
}
/// <summary>A <see cref="GpuRangeAllocator"/>'s refusal of a range it cannot hand out: no free range holds the request,
/// or its most live ranges already are.</summary>
public sealed class GpuRangeExhaustedException : InvalidOperationException {
    /// <summary>Initializes a new instance of the <see cref="GpuRangeExhaustedException"/> class.</summary>
    /// <param name="message">The refusal.</param>
    /// <param name="request">The requested length.</param>
    /// <param name="largestFree">The largest free range's length when the request was refused.</param>
    /// <param name="size">The span's size.</param>
    public GpuRangeExhaustedException(string message, uint request, uint largestFree, uint size) : base(message: message) {
        LargestFree = largestFree;
        Request = request;
        Size = size;
    }

    /// <summary>Gets the largest free range's length when the request was refused.</summary>
    public uint LargestFree { get; }
    /// <summary>Gets the requested length.</summary>
    public uint Request { get; }
    /// <summary>Gets the span's size.</summary>
    public uint Size { get; }
}
