namespace Puck.Abstractions.Gpu;

/// <summary>
/// The ranges of one host table that still have to reach the GPU, as half-open <c>[start, start + length)</c> runs in
/// the table's own unit (words for a device-local copy, bytes or words for a host-visible buffer). A range that
/// touches the last run, or starts within the merge gap after it, extends that run, so a table dirtied in increasing
/// order coalesces into as few runs as its changes allow; an out-of-order range absorbs every run it overlaps or
/// touches. The run count is bounded by the construction capacity: when a new run would exceed it, the runs closest
/// together merge across the narrowest gaps until half the capacity remains, which keeps the upload correct and its run
/// list sorted and disjoint at the price of re-sending those gaps, and never joins two runs far apart (a table's head and a write at
/// its far end) while nearer runs stay apart. Allocation-free after construction.
/// </summary>
public sealed class GpuUploadRuns {
    private readonly int[] m_ends;
    // The gaps between the runs in order, and the same gaps sorted, as a coarsening reads them.
    private readonly int[] m_gaps;
    private readonly int[] m_widths;
    private readonly int m_mergeGap;
    private readonly int[] m_starts;

    private int m_count;

    /// <summary>Initializes a new instance of the <see cref="GpuUploadRuns"/> class.</summary>
    /// <param name="capacity">The most runs held at once; at least two.</param>
    /// <param name="mergeGap">The largest gap after the last run that a new range still joins it across.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below two, or
    /// <paramref name="mergeGap"/> is negative.</exception>
    public GpuUploadRuns(int capacity, int mergeGap = 0) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 2,
            value: capacity
        );
        ArgumentOutOfRangeException.ThrowIfNegative(value: mergeGap);

        m_ends = new int[capacity];
        m_gaps = new int[capacity];
        m_mergeGap = mergeGap;
        m_starts = new int[capacity];
        m_widths = new int[capacity];
    }

    /// <summary>Gets the number of runs held.</summary>
    public int Count => m_count;

    /// <summary>Gets the logical bytes in the retained integer arrays, excluding object and array headers.</summary>
    public long StorageBytes => (((long)m_ends.Length + m_gaps.Length + m_widths.Length + m_starts.Length) * sizeof(int));

    /// <summary>Records <c>[start, start + length)</c> as owed; an empty range records nothing.</summary>
    /// <param name="start">The first unit of the range.</param>
    /// <param name="length">The range's length in units.</param>
    public void Add(int start, int length) {
        if (length <= 0) {
            return;
        }

        var end = checked((start + length));

        // Increasing writes keep the common append/extend case constant-time. The last run is the rightmost one.
        if ((m_count > 0) && (start >= m_starts[(m_count - 1)])) {
            if (((long)start) <= (((long)m_ends[(m_count - 1)]) + m_mergeGap)) {
                m_ends[(m_count - 1)] = Math.Max(val1: end, val2: m_ends[(m_count - 1)]);
                return;
            }
            if (m_count < m_starts.Length) {
                m_starts[m_count] = start;
                m_ends[m_count++] = end;
                return;
            }
        }

        var first = 0;
        while ((first < m_count) && (m_ends[first] < start)) { first++; }
        var after = first;
        while ((after < m_count) && (m_starts[after] <= end)) {
            start = Math.Min(val1: start, val2: m_starts[after]);
            end = Math.Max(val1: end, val2: m_ends[after]);
            after++;
        }
        if ((first == after) && (m_count == m_starts.Length)) {
            Coarsen();
            Add(start: start, length: (end - start));
            return;
        }

        // Absorb every touched run, not only the first: otherwise a bridging write leaves overlapping GPU copies.
        Array.Copy(sourceArray: m_starts, sourceIndex: after, destinationArray: m_starts, destinationIndex: (first + 1), length: (m_count - after));
        Array.Copy(sourceArray: m_ends, sourceIndex: after, destinationArray: m_ends, destinationIndex: (first + 1), length: (m_count - after));
        m_starts[first] = start;
        m_ends[first] = end;
        m_count += (1 - (after - first));
    }
    /// <summary>Records as owed each run of words in which <paramref name="current"/> differs from
    /// <paramref name="previous"/> at the same index, and every word of <paramref name="current"/> past
    /// <paramref name="previous"/>'s end, in increasing order, offset by <paramref name="startWord"/>.</summary>
    /// <param name="previous">The words a table held.</param>
    /// <param name="current">The words it holds now.</param>
    /// <param name="startWord">The destination word corresponding to the spans' index zero.</param>
    /// <returns>The differing or newly present words before merging or coarsening owed runs.</returns>
    public int AddDifferences(ReadOnlySpan<uint> previous, ReadOnlySpan<uint> current, int startWord = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: startWord);
        _ = checked((startWord + current.Length));
        var shared = Math.Min(
            val1: previous.Length,
            val2: current.Length
        );
        var before = previous[..shared];
        var after = current[..shared];
        var index = 0;
        var changed = 0;

        while (index < shared) {
            index += after[index..].CommonPrefixLength(other: before[index..]);

            if (index >= shared) {
                break;
            }

            var start = index;

            while ((index < shared) && (after[index] != before[index])) {
                index++;
            }

            Add(
                length: (index - start),
                start: (startWord + start)
            );
            changed += (index - start);
        }

        Add(
            length: (current.Length - shared),
            start: (startWord + shared)
        );
        return (changed + (current.Length - shared));
    }
    /// <summary>Forgets every run, as a table's owed ranges reach the GPU.</summary>
    public void Clear() => m_count = 0;
    /// <summary>Gets run <paramref name="index"/>'s length in units.</summary>
    /// <param name="index">The run, below <see cref="Count"/>.</param>
    /// <returns>The run's length.</returns>
    public int Length(int index) => (m_ends[index] - m_starts[index]);
    /// <summary>Gets run <paramref name="index"/>'s first unit.</summary>
    /// <param name="index">The run, below <see cref="Count"/>.</param>
    /// <returns>The run's start.</returns>
    public int Start(int index) => m_starts[index];

    // Halves the run count by closing the smallest gaps: the runs are already ordered, and each gap no wider
    // than the one that leaves half the runs is closed, so the units re-sent are the narrowest gaps between runs and never
    // a span between runs far apart while nearer ones stay separate.
    private void Coarsen() {
        var target = (m_starts.Length / 2);
        var closing = (m_count - target);

        for (var index = 0; (index < (m_count - 1)); index++) {
            m_gaps[index] = Math.Max(
                val1: 0,
                val2: (m_starts[(index + 1)] - m_ends[index])
            );
        }

        m_gaps.AsSpan(length: (m_count - 1), start: 0).CopyTo(destination: m_widths);

        // The widest gap closed; gaps of exactly its width close only while fewer than `closing` have.
        var widest = Select(rank: (closing - 1), values: m_widths.AsSpan(length: (m_count - 1), start: 0));
        var narrower = 0;

        for (var index = 0; (index < (m_count - 1)); index++) {
            if (m_gaps[index] < widest) {
                narrower++;
            }
        }

        var equalClosing = (closing - narrower);
        var kept = 0;

        for (var index = 0; (index < m_count); index++) {
            var joins = (
                (index > 0) &&
                (
                    (m_gaps[(index - 1)] < widest) ||
                    ((m_gaps[(index - 1)] == widest) && (equalClosing-- > 0))
                )
            );

            if (joins) {
                m_ends[(kept - 1)] = Math.Max(
                    val1: m_ends[(kept - 1)],
                    val2: m_ends[index]
                );

                continue;
            }

            m_starts[kept] = m_starts[index];
            m_ends[kept] = m_ends[index];
            kept++;
        }

        m_count = kept;
    }
    // The value of a rank among values in increasing order (zero the smallest), found by partitioning them in place about
    // a middle pivot and keeping the side that holds the rank, in linear time on average.
    private static int Select(Span<int> values, int rank) {
        var low = 0;
        var high = (values.Length - 1);

        while (low < high) {
            var pivot = values[(low + ((high - low) / 2))];
            var left = low;
            var right = high;

            while (left <= right) {
                while (values[left] < pivot) { left++; }
                while (values[right] > pivot) { right--; }
                if (left <= right) {
                    (values[left], values[right]) = (values[right], values[left]);
                    left++;
                    right--;
                }
            }

            if (rank <= right) {
                high = right;
            } else if (rank >= left) {
                low = left;
            } else {
                return values[rank];
            }
        }

        return values[rank];
    }
}
