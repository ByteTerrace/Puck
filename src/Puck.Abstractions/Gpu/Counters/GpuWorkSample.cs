using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The per-pass GPU work of one completed submission, as <see cref="IGpuWorkSource.TryReadCompleted"/> copies it.
/// Each configured pass has a <see cref="GpuPassState"/> and, when executed, one count per column of
/// <see cref="GpuWork.SubmissionKinds"/>; work recorded outside every pass has its own row. The labels and the counts
/// always describe the same submission: the labels are the ones that submission was recorded under.
/// <para>
/// A sample is reused across reads. It grows the first time a source has more passes than it holds and allocates
/// nothing afterwards.
/// </para>
/// </summary>
public sealed class GpuWorkSample {
    private WorkClass[] m_classes = [];
    private long[] m_counts = new long[GpuWork.SubmissionColumnCount];
    private string[] m_labels = [];
    private GpuPassState[] m_states = [];

    /// <summary>Gets the number of passes the submission was recorded under; zero when the sample holds no submission.</summary>
    public int PassCount { get; private set; }
    /// <summary>Gets the pass labels the submission was recorded under, in pass order.</summary>
    public ReadOnlySpan<string> PassLabels =>
        m_labels.AsSpan(
            length: PassCount,
            start: 0
        );
    /// <summary>Gets the revision of the pass configuration the submission was recorded under, as the node numbered
    /// it; zero when the sample holds no submission.</summary>
    public long Revision { get; private set; }
    /// <summary>Gets the submission's identity: one for a node's first counted submission, increasing by one per
    /// submission and never reused, even across a reset. Zero when the sample holds no submission.</summary>
    public long Submission { get; private set; }

    /// <summary>Reads what two runs of one pass may be held to agree on, as the node configured it: a pass whose work
    /// follows the device is <see cref="WorkClass.PerBackendDeterministic"/>, every other
    /// <see cref="WorkClass.Deterministic"/>.</summary>
    /// <param name="pass">The zero-based pass index, in <see cref="PassLabels"/> order.</param>
    /// <returns>The pass's class.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pass"/> is negative or not less than <see cref="PassCount"/>.</exception>
    public WorkClass GetPassClass(int pass) {
        ValidatePass(pass: pass);

        return ((pass < m_classes.Length)
            ? m_classes[pass]
            : WorkClass.Deterministic
        );
    }
    /// <summary>Reads what the submission did with one pass.</summary>
    /// <param name="pass">The zero-based pass index, in <see cref="PassLabels"/> order.</param>
    /// <returns>The pass's state.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pass"/> is negative or not less than <see cref="PassCount"/>.</exception>
    public GpuPassState GetPassState(int pass) {
        ValidatePass(pass: pass);

        return m_states[pass];
    }
    /// <summary>Reads one count of the work recorded outside every pass.</summary>
    /// <param name="column">The column: the index of the kind in <see cref="GpuWork.SubmissionKinds"/>.</param>
    /// <returns>The count, in the kind's unit; zero when the sample holds no submission.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="column"/> is not a column of <see cref="GpuWork.SubmissionKinds"/>.</exception>
    public long GetOutsidePassCount(int column) {
        ValidateColumn(column: column);

        return m_counts[column];
    }
    /// <summary>Reads one count of an executed pass.</summary>
    /// <param name="pass">The zero-based pass index, in <see cref="PassLabels"/> order.</param>
    /// <param name="column">The column: the index of the kind in <see cref="GpuWork.SubmissionKinds"/>.</param>
    /// <param name="value">The count, in the kind's unit; zero when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the pass executed; <see langword="false"/> when it was skipped or not
    /// reached, which has no count rather than a count of zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pass"/> is negative or not less than
    /// <see cref="PassCount"/>, or <paramref name="column"/> is not a column of <see cref="GpuWork.SubmissionKinds"/>.</exception>
    public bool TryGetPassCount(int pass, int column, out long value) {
        ValidatePass(pass: pass);
        ValidateColumn(column: column);

        if (m_states[pass] != GpuPassState.Executed) {
            value = 0L;

            return false;
        }

        value = m_counts[(((pass + 1) * GpuWork.SubmissionColumnCount) + column)];

        return true;
    }

    internal void Clear() {
        Load(
            classes: [],
            counts: [],
            labels: [],
            revision: 0L,
            states: [],
            submission: 0L
        );
    }
    // The source may be republished while this runs on another thread. Each field is read once and the lengths are
    // clamped to the arrays actually read, so a torn copy stays in bounds; the caller discards it by its version.
    internal void CopyFrom(GpuWorkSample source) {
        var classes = source.m_classes;
        var counts = source.m_counts;
        var labels = source.m_labels;
        var states = source.m_states;
        var passCount = Math.Min(
            val1: source.PassCount,
            val2: Math.Min(
                val1: Math.Min(
                    val1: labels.Length,
                    val2: states.Length
                ),
                val2: ((counts.Length / GpuWork.SubmissionColumnCount) - 1)
            )
        );

        Load(
            classes: classes,
            counts: counts.AsSpan(
                length: ((passCount + 1) * GpuWork.SubmissionColumnCount),
                start: 0
            ),
            labels: labels,
            revision: source.Revision,
            states: states.AsSpan(
                length: passCount,
                start: 0
            ),
            submission: source.Submission
        );
    }
    // counts holds the outside row, then one row per pass, or is empty for no submission. labels and classes are
    // immutable and held by reference.
    internal void Load(ReadOnlySpan<long> counts, string[] labels, WorkClass[] classes, long revision, ReadOnlySpan<GpuPassState> states, long submission) {
        var passCount = states.Length;
        var countLength = ((passCount + 1) * GpuWork.SubmissionColumnCount);

        if (m_states.Length < passCount) {
            m_states = new GpuPassState[passCount];
        }

        if (m_counts.Length < countLength) {
            m_counts = new long[countLength];
        }

        states.CopyTo(destination: m_states);

        var destination = m_counts.AsSpan(
            length: countLength,
            start: 0
        );

        if (counts.IsEmpty) {
            destination.Clear();
        } else {
            counts.CopyTo(destination: destination);
        }

        m_classes = classes;
        m_labels = labels;
        PassCount = passCount;
        Revision = revision;
        Submission = submission;
    }

    private void ValidatePass(int pass) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: ((uint)PassCount),
            value: ((uint)pass),
            paramName: nameof(pass)
        );
    private static void ValidateColumn(int column) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: ((uint)GpuWork.SubmissionColumnCount),
            value: ((uint)column),
            paramName: nameof(column)
        );
}
