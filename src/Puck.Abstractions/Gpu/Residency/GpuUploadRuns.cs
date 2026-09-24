namespace Puck.Abstractions.Gpu;

/// <summary>
/// The ranges of one host table that still have to reach the GPU, as half-open <c>[start, start + length)</c> runs in
/// the table's own unit (words for a device-local copy, bytes or words for a host-visible buffer). A range that
/// touches the last run, or starts within the merge gap after it, extends that run, so a table dirtied in increasing
/// order coalesces into as few runs as its changes allow; an out-of-order range merges into any run it overlaps or
/// touches. The run count is bounded by the construction capacity: when a new run would exceed it, neighbouring runs
/// pair up into one run each (covering the gap between them), which keeps the upload correct and its run list bounded
/// at the price of re-sending those gaps. Allocation-free after construction.
/// </summary>
public sealed class GpuUploadRuns {
    private readonly int[] m_ends;
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
        m_mergeGap = mergeGap;
        m_starts = new int[capacity];
    }

    /// <summary>Gets the number of runs held.</summary>
    public int Count => m_count;

    /// <summary>Records <c>[start, start + length)</c> as owed; an empty range records nothing.</summary>
    /// <param name="start">The first unit of the range.</param>
    /// <param name="length">The range's length in units.</param>
    public void Add(int start, int length) {
        if (length <= 0) {
            return;
        }

        var end = checked((start + length));

        if (m_count > 0) {
            var last = (m_count - 1);

            if (
                (start >= m_starts[last]) &&
                (start <= (m_ends[last] + m_mergeGap))
            ) {
                m_ends[last] = Math.Max(
                    val1: end,
                    val2: m_ends[last]
                );

                return;
            }

            for (var index = 0; (index < m_count); index++) {
                if (
                    (start <= m_ends[index]) &&
                    (end >= m_starts[index])
                ) {
                    m_starts[index] = Math.Min(
                        val1: start,
                        val2: m_starts[index]
                    );
                    m_ends[index] = Math.Max(
                        val1: end,
                        val2: m_ends[index]
                    );

                    return;
                }
            }

            if (m_count == m_starts.Length) {
                Coarsen();
            }
        }

        m_starts[m_count] = start;
        m_ends[m_count] = end;
        m_count++;
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

    // Pairs runs (0, 1), (2, 3), … into one run each, halving the count; an odd last run stays as it is.
    private void Coarsen() {
        var kept = 0;

        for (var index = 0; (index < m_count); index += 2) {
            var start = m_starts[index];
            var end = m_ends[index];

            if ((index + 1) < m_count) {
                start = Math.Min(
                    val1: start,
                    val2: m_starts[(index + 1)]
                );
                end = Math.Max(
                    val1: end,
                    val2: m_ends[(index + 1)]
                );
            }

            m_starts[kept] = start;
            m_ends[kept] = end;
            kept++;
        }

        m_count = kept;
    }
}
