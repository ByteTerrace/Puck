using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Collects one render node's GPU work per submission and per pass, and publishes a submission's counts only once
/// the GPU is known to have finished it. The node wraps its neutral GPU services with <see cref="GpuWorkCounting"/>
/// over this ledger, so every counted call lands here; the node itself only says which pass it is in.
/// <para>
/// The ledger holds one record per frame in flight plus one, all allocated up front. A record opens at the first
/// counted call or pass change after the previous submission and seals when the wrapped queue submitter submits,
/// which gives it the next submission identity: one for the first, increasing by one per submission, never reused.
/// A sealed record is published when its fence is waited or <see cref="Poll"/> finds the fence signaled, but only if
/// it is newer than the submission already published; completing a submission also retires every older one, because
/// one queue runs submissions in order. When every record is still pending, opening a record reuses the oldest
/// pending one, whose counts are dropped and never published.
/// </para>
/// <para>
/// Each record keeps the pass labels and revision it was recorded under, so a submission still in flight across a
/// <see cref="Configure"/> publishes under its own labels. <see cref="Configure"/> and <see cref="Invalidate"/>
/// withdraw the published sample: nothing is available again until a newer submission completes.
/// <see cref="Invalidate"/> also drops every record not yet published, so only a submission sealed after it can be
/// published next.
/// </para>
/// <para>
/// Every member except <see cref="TryReadCompleted"/> and <see cref="TryRead"/> runs on the node's recording thread,
/// and so does every counted call except object creation: a node may create its pipelines on a build thread while it
/// records frames, so the lifetime counts are written interlocked. <see cref="TryReadCompleted"/> and
/// <see cref="TryRead"/> may run on any thread: the published sample is double-buffered with a version, and a read that overlaps
/// a publication retries. Counting, sealing, completing, and reading allocate nothing once the records and samples
/// have grown to the configured pass count.
/// </para>
/// </summary>
public sealed class GpuWorkLedger : IGpuWorkSource, IWorkCounterSource {
    private const int Columns = GpuWork.SubmissionColumnCount;

    private readonly WorkCount[] m_lifetime = new WorkCount[GpuWork.LifetimeKindCount];

    private readonly Record[] m_records;

    private readonly GpuWorkSample[] m_snapshots = [new(), new()];
    private int m_currentPass = -1;
    private string[] m_labels = [];

    private long m_lastSealed;
    private Record? m_open;
    private long m_published;
    private long m_revision;
    private long m_version;

    /// <summary>Initializes a new instance of the <see cref="GpuWorkLedger"/> class.</summary>
    /// <param name="name">The ledger's <see cref="IWorkCounterSource.Name"/>, naming the kind of node that owns it
    /// (<c>gpu.sdf-engine</c>, <c>gpu.overlay</c>).</param>
    /// <param name="framesInFlight">The number of submissions the node keeps in flight at once; the ledger allocates
    /// one record more than this.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not of the form <see cref="WorkKind.IsName"/> accepts.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="framesInFlight"/> is less than one.</exception>
    public GpuWorkLedger(string name, int framesInFlight) {
        _ = WorkKind.RequireSourceName(
            name: name,
            paramName: nameof(name)
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: framesInFlight
        );

        Name = name;

        m_records = new Record[(framesInFlight + 1)];

        for (var index = 0; (index < m_records.Length); index++) {
            m_records[index] = new Record();
        }
    }

    /// <inheritdoc/>
    public string Name { get; }
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds =>
        GpuWork.LifetimeKinds;

    /// <summary>Sets the passes and revision the next recorded work is counted under, and withdraws the published
    /// sample. Work already counted outside every pass since the last submission is kept and moves to the new
    /// passes.</summary>
    /// <param name="revision">The node's number for this pass configuration, reported with every sample recorded under it.</param>
    /// <param name="passLabels">The pass labels, in pass order; copied.</param>
    /// <exception cref="ArgumentException">An element of <paramref name="passLabels"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The work being recorded has already entered or skipped a pass.</exception>
    public void Configure(long revision, ReadOnlySpan<string> passLabels) {
        foreach (var label in passLabels) {
            if (label is null) {
                throw new ArgumentException(
                    message: "A pass label is null.",
                    paramName: nameof(passLabels)
                );
            }
        }

        if (m_open is { HasPassActivity: true }) {
            throw new InvalidOperationException(message: "The passes cannot change while the work being recorded has entered or skipped a pass.");
        }

        m_labels = passLabels.ToArray();
        m_revision = revision;
        m_open?.Rebind(
            labels: m_labels,
            revision: m_revision
        );
        Withdraw();
    }
    /// <summary>Marks a pass as running; the work counted until <see cref="LeavePass"/> is that pass's.</summary>
    /// <param name="pass">The zero-based pass index in the configured labels.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pass"/> is not a configured pass.</exception>
    /// <exception cref="InvalidOperationException">A pass is already running, or this submission skipped <paramref name="pass"/>.</exception>
    public void EnterPass(int pass) {
        var record = OpenRecord();

        ValidatePass(
            pass: pass,
            record: record
        );

        if (m_currentPass >= 0) {
            throw new InvalidOperationException(message: $"Pass {m_currentPass} is still running; leave it before entering pass {pass}.");
        }

        if (record.States[pass] == GpuPassState.Skipped) {
            throw new InvalidOperationException(message: $"Pass {pass} was skipped in this submission.");
        }

        record.States[pass] = GpuPassState.Executed;
        record.HasPassActivity = true;
        m_currentPass = pass;
    }
    /// <summary>Withdraws the published sample and drops every submission not yet published, for a reset, a resize,
    /// or a release of the node's GPU resources. The next sample is a submission sealed after this call.</summary>
    public void Invalidate() {
        foreach (var record in m_records) {
            record.Free();
        }

        m_currentPass = -1;
        m_open = null;
        Withdraw();
    }
    /// <summary>Ends the running pass; the work counted next is outside every pass until another pass is entered.</summary>
    /// <exception cref="InvalidOperationException">No pass is running.</exception>
    public void LeavePass() {
        if (m_currentPass < 0) {
            throw new InvalidOperationException(message: "No pass is running.");
        }

        m_currentPass = -1;
    }
    /// <summary>Publishes the newest pending submission whose fence has signaled, without waiting. A node calls it
    /// once per produced frame, paused or not.</summary>
    public void Poll() {
        foreach (var record in m_records) {
            if (
                (record.State == RecordState.Sealed) &&
                (record.Fence is { } fence) &&
                (fence.ArmedSubmission == record.Submission) &&
                fence.IsSignaled
            ) {
                Complete(submission: record.Submission);
            }
        }
    }
    /// <summary>Marks a pass as not run in the work being recorded, so its sample reads skipped rather than zero.</summary>
    /// <param name="pass">The zero-based pass index in the configured labels.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pass"/> is not a configured pass.</exception>
    /// <exception cref="InvalidOperationException">This submission already executed <paramref name="pass"/>.</exception>
    public void SkipPass(int pass) {
        var record = OpenRecord();

        ValidatePass(
            pass: pass,
            record: record
        );

        if (record.States[pass] == GpuPassState.Executed) {
            throw new InvalidOperationException(message: $"Pass {pass} already executed in this submission.");
        }

        record.States[pass] = GpuPassState.Skipped;
        record.HasPassActivity = true;
    }
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) {
        var kinds = GpuWork.LifetimeKinds;

        for (var index = 0; (index < kinds.Length); index++) {
            if (ReferenceEquals(
                objA: kinds[index],
                objB: kind
            )) {
                value = m_lifetime[index].Value;

                return true;
            }
        }

        value = 0L;

        return false;
    }
    /// <inheritdoc/>
    public bool TryReadCompleted(GpuWorkSample sample) {
        ArgumentNullException.ThrowIfNull(sample);

        while (true) {
            var version = Volatile.Read(location: ref m_version);

            sample.CopyFrom(source: m_snapshots[((int)(version & 1L))]);
            Interlocked.MemoryBarrier();

            if (Volatile.Read(location: ref m_version) == version) {
                return (sample.Submission != 0L);
            }
        }
    }

    internal void Complete(long submission) {
        Record? completed = null;

        foreach (var record in m_records) {
            if ((record.State == RecordState.Sealed) && (record.Submission == submission)) {
                completed = record;
            }
        }

        if (completed is null) {
            return;
        }

        if (submission > m_published) {
            Publish(record: completed);
            m_published = submission;
        }

        foreach (var record in m_records) {
            if ((record.State == RecordState.Sealed) && (record.Submission <= submission)) {
                record.Free();
            }
        }
    }
    internal void Count(int column, long amount) {
        var record = OpenRecord();

        record.Counts[(((m_currentPass + 1) * Columns) + column)] += amount;
    }
    internal void CountCreated(int lifetimeIndex) =>
        m_lifetime[lifetimeIndex].IncrementShared();
    internal void Detach(GpuWorkCountingFence fence) {
        foreach (var record in m_records) {
            if (ReferenceEquals(
                objA: record.Fence,
                objB: fence
            )) {
                record.Fence = null;
            }
        }
    }
    internal long Seal(GpuWorkCountingFence? fence) {
        var record = OpenRecord();

        record.Fence = fence;
        record.State = RecordState.Sealed;
        record.Submission = ++m_lastSealed;
        m_currentPass = -1;
        m_open = null;

        return record.Submission;
    }

    private Record OpenRecord() {
        if (m_open is { } open) {
            return open;
        }

        Record? chosen = null;

        foreach (var record in m_records) {
            if (record.State == RecordState.Free) {
                chosen = record;

                break;
            }

            if ((chosen is null) || (record.Submission < chosen.Submission)) {
                chosen = record;
            }
        }

        chosen!.Open(
            labels: m_labels,
            revision: m_revision
        );
        m_open = chosen;

        return chosen;
    }
    private void Publish(Record record) {
        var passCount = record.Labels.Length;

        m_snapshots[((int)((m_version + 1L) & 1L))].Load(
            counts: record.Counts.AsSpan(
                length: ((passCount + 1) * Columns),
                start: 0
            ),
            labels: record.Labels,
            revision: record.Revision,
            states: record.States.AsSpan(
                length: passCount,
                start: 0
            ),
            submission: record.Submission
        );
        _ = Interlocked.Increment(location: ref m_version);
    }
    private static void ValidatePass(int pass, Record record) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: ((uint)record.Labels.Length),
            value: ((uint)pass),
            paramName: nameof(pass)
        );
    private void Withdraw() {
        m_snapshots[((int)((m_version + 1L) & 1L))].Clear();
        _ = Interlocked.Increment(location: ref m_version);
    }

    private enum RecordState : byte {
        Free = 0,
        Open = 1,
        Sealed = 2,
    }
    private sealed class Record {
        public long[] Counts = new long[Columns];

        public GpuWorkCountingFence? Fence;
        public bool HasPassActivity;

        public string[] Labels = [];

        public long Revision;
        public RecordState State;

        public GpuPassState[] States = [];

        public long Submission;

        public void Free() {
            Fence = null;
            State = RecordState.Free;
        }
        public void Open(string[] labels, long revision) {
            Rebind(
                labels: labels,
                revision: revision
            );
            Counts.AsSpan(
                length: Columns,
                start: 0
            ).Clear();
            Fence = null;
            HasPassActivity = false;
            State = RecordState.Open;
            Submission = 0L;
        }
        // Keeps the outside row, which does not depend on the passes; clears every pass row and state.
        public void Rebind(string[] labels, long revision) {
            var countLength = ((labels.Length + 1) * Columns);

            if (Counts.Length < countLength) {
                var grown = new long[countLength];

                Counts.AsSpan(
                    length: Columns,
                    start: 0
                ).CopyTo(destination: grown);
                Counts = grown;
            }

            if (States.Length < labels.Length) {
                States = new GpuPassState[labels.Length];
            }

            Counts.AsSpan(
                length: (countLength - Columns),
                start: Columns
            ).Clear();
            States.AsSpan(
                length: labels.Length,
                start: 0
            ).Clear();
            Labels = labels;
            Revision = revision;
        }
    }
}
