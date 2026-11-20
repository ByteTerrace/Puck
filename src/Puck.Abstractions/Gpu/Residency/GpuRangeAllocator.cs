using Puck.Abstractions.Memory;
namespace Puck.Abstractions.Gpu;

/// <summary>
/// Hands out contiguous ranges of a fixed span <c>[0, size)</c>: a descriptor heap's slots, a buffer's words, a block of
/// device memory's bytes. First fit in address order at the alignment each request asks for; the gap an alignment leaves
/// stays free for a later fit; each range is returned whole by <see cref="Free(ulong)"/>, which names it by its start; and free
/// neighbours coalesce, so a span every range left is one free range again (<see cref="LiveRanges"/> is zero).
/// <para>The live ranges never overlap one another or a free range: every range comes from a free run, and a free names a
/// live range's start exactly, refusing a start in free space (freed already) or inside a live range (never allocated),
/// so no caller can free part of a range or free one twice. Each live range also carries a ticket no other allocation of
/// the span ever carries (<see cref="TicketOf"/>), and a free that names one (<see cref="Free(ulong, ulong)"/>) is refused
/// once the start it names was freed and given again, so a holder of a stale range cannot free its successor. The span
/// never grows: a request no free range holds is
/// refused with <see cref="GpuRangeExhaustedException"/>, which states the request, the largest free range and the size,
/// and so is one past <see cref="MaxRanges"/> live ranges.</para>
/// <para>The live and free ranges are held in arrays sorted by start that double as the live ranges pass their earlier
/// peak, up to the most <see cref="MaxRanges"/> admits; allocating and freeing below that peak allocate nothing. Not
/// thread-safe: its owner locks.</para>
/// </summary>
public sealed class GpuRangeAllocator {
    // The arrays' first length: a span with few ranges, a memory block holding one large resource, stays small.
    private const int FirstRanges = 16;

    // Live ranges and free runs, each sorted by start. Free runs never touch, so a live range lies between any two and a
    // span of n live ranges has at most n + 1 free runs: the free arrays are always one longer than the live ones.
    private ulong[] m_freeLengths;
    private ulong[] m_freeStarts;
    private ulong[] m_liveLengths;
    private ulong[] m_liveStarts;
    // Each live range's ticket, beside its start; tickets count up from one and are never reused.
    private ulong[] m_liveTickets;

    private int m_freeCount;
    private ulong m_lastTicket;
    private int m_liveCount;

    /// <summary>Initializes a new instance of the <see cref="GpuRangeAllocator"/> class, wholly free.</summary>
    /// <param name="size">The span's size; at least one.</param>
    /// <param name="maxRanges">The most ranges live at once; at least one. Past <see cref="Array.MaxLength"/> less one,
    /// the default, it is that: the arrays' limit alone bounds the ranges.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> or <paramref name="maxRanges"/> is
    /// zero.</exception>
    public GpuRangeAllocator(ulong size, int maxRanges = int.MaxValue) {
        ArgumentOutOfRangeException.ThrowIfZero(value: size);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: maxRanges
        );

        MaxRanges = Math.Min(
            val1: maxRanges,
            val2: (Array.MaxLength - 1)
        );

        var first = Math.Min(
            val1: MaxRanges,
            val2: FirstRanges
        );

        m_freeLengths = new ulong[(first + 1)];
        m_freeStarts = new ulong[(first + 1)];
        m_liveLengths = new ulong[first];
        m_liveStarts = new ulong[first];
        m_liveTickets = new ulong[first];
        m_freeLengths[0] = size;
        m_freeCount = 1;
        Size = size;
    }

    /// <summary>Gets the free count across every free range.</summary>
    public ulong FreeCount {
        get {
            var total = 0UL;

            for (var index = 0; (index < m_freeCount); index++) {
                total += m_freeLengths[index];
            }

            return total;
        }
    }
    /// <summary>Gets the free ranges, which counts how fragmented the span is: one when the free space is contiguous,
    /// and zero when the span is full.</summary>
    public int FreeRanges => m_freeCount;
    /// <summary>Gets the length of the largest free range, the largest unaligned request that fits.</summary>
    public ulong LargestFree {
        get {
            var largest = 0UL;

            for (var index = 0; (index < m_freeCount); index++) {
                largest = Math.Max(
                    val1: largest,
                    val2: m_freeLengths[index]
                );
            }

            return largest;
        }
    }
    /// <summary>Gets the end of the highest live range, the least span that holds every live range; zero with
    /// none.</summary>
    public ulong LiveEnd => ((m_liveCount == 0) ? 0UL : (m_liveStarts[(m_liveCount - 1)] + m_liveLengths[(m_liveCount - 1)]));
    /// <summary>Gets the ranges live now.</summary>
    public int LiveRanges => m_liveCount;
    /// <summary>Gets the most ranges live at once.</summary>
    public int MaxRanges { get; }
    /// <summary>Gets the span's size.</summary>
    public ulong Size { get; }

    /// <summary>Allocates a range of <paramref name="count"/> from the lowest free range that holds it at
    /// <paramref name="alignment"/>.</summary>
    /// <param name="count">The range's length; at least one.</param>
    /// <param name="alignment">The alignment its start needs: a power of two, or zero or one for none.</param>
    /// <returns>The range's start.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is zero, or <paramref name="alignment"/>
    /// is not a power of two.</exception>
    /// <exception cref="GpuRangeExhaustedException">No free range holds <paramref name="count"/>, or
    /// <see cref="MaxRanges"/> ranges are already live.</exception>
    public ulong Allocate(ulong count, ulong alignment = 1UL) {
        if (TryAllocate(alignment: alignment, count: count, start: out var allocated)) { return allocated; }
        if (m_liveCount == MaxRanges) {
            throw new GpuRangeExhaustedException(
                largestFree: LargestFree,
                message: $"A range of {count} is refused: {MaxRanges} ranges are already live, the most this span of {Size} admits.",
                request: count,
                size: Size
            );
        }

        var largest = LargestFree;

        throw new GpuRangeExhaustedException(
            largestFree: largest,
            message: $"A range of {count}{((alignment > 1UL) ? $" at alignment {alignment}" : string.Empty)} is refused: the largest free range holds {largest} of this span's {Size}, and the span never grows.",
            request: count,
            size: Size
        );
    }
    /// <summary>Allocates a range of <paramref name="count"/> from the lowest free range that holds it at
    /// <paramref name="alignment"/>, where one does and fewer than <see cref="MaxRanges"/> ranges are live.</summary>
    /// <param name="count">The range's length; at least one.</param>
    /// <param name="start">The range's start; zero when refused.</param>
    /// <param name="alignment">The alignment its start needs: a power of two, or zero or one for none. The gap it
    /// leaves before the range stays free.</param>
    /// <returns><see langword="false"/> when no free range holds <paramref name="count"/> or
    /// <see cref="MaxRanges"/> ranges are already live.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is zero, or <paramref name="alignment"/>
    /// is not a power of two.</exception>
    public bool TryAllocate(ulong count, out ulong start, ulong alignment = 1UL) {
        ArgumentOutOfRangeException.ThrowIfZero(value: count);
        ThrowIfNotAlignment(alignment: alignment);

        start = 0UL;
        if (m_liveCount == MaxRanges) { return false; }

        var mask = ((alignment > 1UL) ? (alignment - 1UL) : 0UL);

        for (var index = 0; (index < m_freeCount); index++) {
            var runStart = m_freeStarts[index];
            var runEnd = (runStart + m_freeLengths[index]);
            var aligned = ((runStart + mask) & ~mask);

            // An alignment that wraps the address space, or passes the run, or leaves too little of it, skips the run.
            if ((aligned < runStart) || (aligned > runEnd) || ((runEnd - aligned) < count)) {
                continue;
            }

            Reserve();

            var end = (aligned + count);

            if (aligned == runStart) {
                if (end == runEnd) {
                    RemoveAt(
                        count: ref m_freeCount,
                        index: index,
                        lengths: m_freeLengths,
                        starts: m_freeStarts
                    );
                } else {
                    m_freeStarts[index] = end;
                    m_freeLengths[index] = (runEnd - end);
                }
            } else {
                // The gap before the range keeps the run's place; a tail past it is a run of its own after it.
                m_freeLengths[index] = (aligned - runStart);
                if (end < runEnd) {
                    InsertAt(
                        count: ref m_freeCount,
                        index: (index + 1),
                        length: (runEnd - end),
                        lengths: m_freeLengths,
                        start: end,
                        starts: m_freeStarts
                    );
                }
            }

            InsertAt(
                count: ref m_liveCount,
                index: LowerBound(
                    count: m_liveCount,
                    starts: m_liveStarts,
                    value: aligned
                ),
                length: count,
                lengths: m_liveLengths,
                start: aligned,
                starts: m_liveStarts,
                ticket: ++m_lastTicket,
                tickets: m_liveTickets
            );
            start = aligned;

            return true;
        }

        return false;
    }
    /// <summary>Returns the ticket of the live range that starts at <paramref name="start"/>: a number no other allocation
    /// of this span carries, before or after.</summary>
    /// <param name="start">The start <see cref="Allocate"/> or <see cref="TryAllocate"/> returned.</param>
    /// <returns>The range's ticket.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="start"/> starts no live range.</exception>
    public ulong TicketOf(ulong start) => m_liveTickets[LiveIndexOf(start: start)];
    /// <summary>Frees the live range that starts at <paramref name="start"/>, merging it with any free neighbour.</summary>
    /// <param name="start">The start <see cref="Allocate"/> or <see cref="TryAllocate"/> returned.</param>
    /// <returns>The freed range's length.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="start"/> lies in free space, so the range was freed
    /// already, or it starts no live range, so it was never allocated.</exception>
    public ulong Free(ulong start) => FreeAt(live: LiveIndexOf(start: start), start: start);
    /// <summary>Frees the live range that starts at <paramref name="start"/> when it is the allocation
    /// <paramref name="ticket"/> names, merging it with any free neighbour; a range freed and given again since carries
    /// another ticket and is left live.</summary>
    /// <param name="start">The start <see cref="Allocate"/> or <see cref="TryAllocate"/> returned.</param>
    /// <param name="ticket">The ticket <see cref="TicketOf"/> returned for that allocation.</param>
    /// <returns>The freed range's length.</returns>
    /// <exception cref="InvalidOperationException">The allocation was freed already, so <paramref name="start"/> lies in
    /// free space or starts a later allocation, or it was never made.</exception>
    public ulong Free(ulong start, ulong ticket) {
        var live = LiveIndexOf(start: start);

        if (m_liveTickets[live] != ticket) {
            throw new InvalidOperationException(message: $"The range at {start} was freed and given again (ticket {m_liveTickets[live]}, not {ticket}); a range is freed once.");
        }

        return FreeAt(live: live, start: start);
    }

    // The index of the live range that starts at start, or a refusal naming why none does.
    private int LiveIndexOf(ulong start) {
        var live = LowerBound(
            count: m_liveCount,
            starts: m_liveStarts,
            value: start
        );

        if (
            (live == m_liveCount) ||
            (m_liveStarts[live] != start)
        ) {
            // The free run that begins at or before start, if any.
            var found = m_freeStarts.AsSpan(length: m_freeCount, start: 0).BinarySearch(comparable: start);
            var free = ((found >= 0) ? found : (~found - 1));

            throw new InvalidOperationException(message: ((
                (free >= 0) &&
                ((start - m_freeStarts[free]) < m_freeLengths[free])
            )
                ? $"The range at {start} is already free; a range is freed once."
                : $"No range was allocated at {start} in this span of {Size}."));
        }

        return live;
    }
    // Frees the live range at an index, merging it with any free neighbour; its length.
    private ulong FreeAt(int live, ulong start) {
        var length = m_liveLengths[live];

        RemoveAt(
            count: ref m_liveCount,
            index: live,
            lengths: m_liveLengths,
            starts: m_liveStarts,
            tickets: m_liveTickets
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

    /// <summary>Refuses an alignment that is not a power of two; zero and one ask for none.</summary>
    /// <param name="alignment">The alignment.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="alignment"/> is not a power of two.</exception>
    public static void ThrowIfNotAlignment(ulong alignment) {
        if ((alignment > 1UL) && !ulong.IsPow2(value: alignment)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(alignment), actualValue: alignment, message: "An alignment is a power of two.");
        }
    }

    // Makes room for one more live range and the free run it may split off, doubling the arrays at their length; a
    // caller has checked that fewer than MaxRanges are live.
    private void Reserve() {
        if (m_liveCount < m_liveStarts.Length) { return; }

        var length = ArrayCapacity.Grown(m_liveStarts.Length, m_liveCount + 1, ceiling: MaxRanges);

        Array.Resize(array: ref m_liveStarts, newSize: length);
        Array.Resize(array: ref m_liveLengths, newSize: length);
        Array.Resize(array: ref m_liveTickets, newSize: length);
        Array.Resize(array: ref m_freeStarts, newSize: (length + 1));
        Array.Resize(array: ref m_freeLengths, newSize: (length + 1));
    }

    // Inserts a range at an index of sorted parallel arrays, and its ticket when the arrays carry tickets.
    private static void InsertAt(ulong[] starts, ulong[] lengths, ref int count, int index, ulong start, ulong length, ulong[]? tickets = null, ulong ticket = 0UL) {
        Shift(array: starts, count: count, index: index, value: start);
        Shift(array: lengths, count: count, index: index, value: length);
        if (tickets is not null) { Shift(array: tickets, count: count, index: index, value: ticket); }
        count++;
    }
    // The first index whose start is at or past value; the starts are sorted and distinct.
    private static int LowerBound(ulong[] starts, int count, ulong value) {
        var found = starts.AsSpan(length: count, start: 0).BinarySearch(comparable: value);

        return ((found >= 0) ? found : ~found);
    }
    // Removes the range at an index of sorted parallel arrays, and its ticket when the arrays carry tickets.
    private static void RemoveAt(ulong[] starts, ulong[] lengths, ref int count, int index, ulong[]? tickets = null) {
        count--;
        Unshift(array: starts, count: count, index: index);
        Unshift(array: lengths, count: count, index: index);
        if (tickets is not null) { Unshift(array: tickets, count: count, index: index); }
    }
    // Moves the count elements from index up by one and writes value at index.
    private static void Shift(ulong[] array, int count, int index, ulong value) {
        array.AsSpan(length: (count - index), start: index).CopyTo(destination: array.AsSpan(start: (index + 1)));
        array[index] = value;
    }
    // Moves the elements past index down by one, over it; count is the length after the removal.
    private static void Unshift(ulong[] array, int count, int index) =>
        array.AsSpan(length: (count - index), start: (index + 1)).CopyTo(destination: array.AsSpan(start: index));
}
/// <summary>A <see cref="GpuRangeAllocator"/>'s refusal of a range it cannot hand out: no free range holds the request,
/// or its most live ranges already are.</summary>
public sealed class GpuRangeExhaustedException : InvalidOperationException {
    /// <summary>Initializes a new instance of the <see cref="GpuRangeExhaustedException"/> class.</summary>
    /// <param name="message">The refusal.</param>
    /// <param name="request">The requested length.</param>
    /// <param name="largestFree">The largest free range's length when the request was refused.</param>
    /// <param name="size">The span's size.</param>
    public GpuRangeExhaustedException(string message, ulong request, ulong largestFree, ulong size) : base(message: message) {
        LargestFree = largestFree;
        Request = request;
        Size = size;
    }

    /// <summary>Gets the largest free range's length when the request was refused.</summary>
    public ulong LargestFree { get; }
    /// <summary>Gets the requested length.</summary>
    public ulong Request { get; }
    /// <summary>Gets the span's size.</summary>
    public ulong Size { get; }
}
