using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

/// <summary>Who put work on a device queue: the subsystem whose submission a native execute carries, or whose queue
/// operation handed a batch's deferred work to the queue (<see cref="GpuQueueWork"/>).</summary>
public enum GpuQueueCategory : byte {
    /// <summary>No caller named itself. A nonzero count is an attribution gap, never a category of work.</summary>
    Unattributed = 0,
    /// <summary>A render graph's frame: its nodes' passes, region copies, readbacks and timing.</summary>
    Graph,
    /// <summary>The background service's mesh-bake sampling jobs, a background job's category
    /// (<see cref="GpuQueueWork.IsBackground"/>).</summary>
    Bake,
    /// <summary>The background service's irradiance probe jobs, a background job's category
    /// (<see cref="GpuQueueWork.IsBackground"/>).</summary>
    Probe,
    /// <summary>Host data reaching the device: a residency's table upload, a source's surface upload, a stand-in's fill.</summary>
    Upload,
    /// <summary>Images shared with another device or process: exports, imports and external waits.</summary>
    Transfer,
    /// <summary>Device data copied back to the host for a capture or an encoder.</summary>
    Readback,
    /// <summary>The presenter's composition and present.</summary>
    Present,
    /// <summary>A wait for the queue or device to go idle (a release, a resize, a reload).</summary>
    Drain,
}

/// <summary>Why a native execute happened when it did.</summary>
public enum GpuQueueCause : byte {
    /// <summary>A submission made while no batch was open, which goes straight to the queue.</summary>
    Unbatched = 0,
    /// <summary>A batch closed (<see cref="IGpuQueueSubmitter.FlushBatch"/>): the frame's own boundary.</summary>
    BatchEnd,
    /// <summary>A raw operation on the queue (an upload's own execute, a present, a timestamp heap's creation) split the
    /// open batch so it follows the deferred work.</summary>
    QueueDoor,
    /// <summary>A wait on a fence that a still-deferred submission arms split the batch.</summary>
    FenceWait,
    /// <summary>A wait on another device's fence split the batch so it holds only later work.</summary>
    ExternalWait,
    /// <summary>A submit-and-wait drained the queue.</summary>
    Drain,
    /// <summary>An empty fenced submission split the batch so its fence follows every earlier submission.</summary>
    EmptyFence,
    /// <summary>Other work was deferred behind a fenced submission that answers from a signal of its own (a background
    /// job's or an upload's, <see cref="GpuQueueWork.OwnsSignal"/>), so the batch went to the queue where that submission
    /// ends, and its fence answers from that hand-over's signal rather than the frame's last
    /// (<see cref="GpuSubmissionBatch{TEntry}.Admit"/>).</summary>
    OwnSignal,
}

/// <summary>
/// Every native execute (<c>ExecuteCommandLists</c>, <c>vkQueueSubmit</c>), signal and present the process puts on a
/// device queue, attributed by who caused it (<see cref="GpuQueueCategory"/>) and why it happened then
/// (<see cref="GpuQueueCause"/>). Each backend counts at its native call, so the matrix's executes always sum to
/// <c>executes</c> and a call nothing named counts as <see cref="GpuQueueCategory.Unattributed"/>. A caller names itself
/// with <see cref="Attribute"/> around the code that submits; a batch remembers each deferred list's category, so one
/// batched execute counts its lists by their owners and the execute by what split the batch. <c>frames</c> counts
/// presents, the interval a reading divides by; <c>waits</c> counts external waits put on a queue and
/// <c>waits-reached</c> those dropped because their fence had already reached its value. <c>jobs</c> counts the background
/// jobs' fenced submissions a batch handed to the queue (<see cref="IsBackground"/>), and <c>jobs.late</c> those among
/// them whose fence answers from a signal queued behind later work that does not answer from its own signal
/// (<see cref="OwnsSignal"/>): zero means every job deferred in a batch was answered from its own hand-over's signal,
/// ahead of the batch's owner.
/// <para>The counts are process-wide: every device of every backend, on any thread. A Vulkan fence or timeline signal
/// rides its <c>vkQueueSubmit</c>, so <c>signals</c> counts the completions a submission carries there, not separate
/// native calls.</para>
/// </summary>
public sealed class GpuQueueWork : IWorkCounterSource {
    private static readonly string[] CategoryNames = ["unattributed", "graph", "bake", "probe", "upload", "transfer", "readback", "present", "drain"];
    private static readonly string[] CauseNames = ["unbatched", "batch-end", "door", "fence-wait", "external-wait", "drain", "empty-fence", "own-signal"];
    [ThreadStatic] private static GpuQueueCategory CurrentCategory;

    private readonly WorkKind m_executes = Kind(name: "gpu.queue.executes");
    private readonly WorkKind m_lists = Kind(name: "gpu.queue.lists");
    private readonly WorkKind m_signals = Kind(name: "gpu.queue.signals");
    private readonly WorkKind m_frames = Kind(name: "gpu.queue.frames");
    private readonly WorkKind m_batches = Kind(name: "gpu.queue.batches");
    private readonly WorkKind m_waits = Kind(name: "gpu.queue.waits");
    private readonly WorkKind m_waitsReached = Kind(name: "gpu.queue.waits-reached");
    private readonly WorkKind m_jobs = Kind(name: "gpu.queue.jobs");
    private readonly WorkKind m_jobsLate = Kind(name: "gpu.queue.jobs.late");
    private readonly WorkKind[] m_listsBy;
    private readonly WorkKind[] m_signalsBy;
    private readonly WorkKind[] m_executesBy;
    private readonly WorkKind[] m_kinds;
    private readonly WorkCounterSet m_counts;

    private GpuQueueWork() {
        var categories = CategoryNames.Length;
        var causes = CauseNames.Length;

        m_listsBy = new WorkKind[categories];
        m_signalsBy = new WorkKind[categories];
        m_executesBy = new WorkKind[categories * causes];
        for (var category = 0; (category < categories); category++) {
            m_listsBy[category] = Kind(name: $"gpu.queue.lists.{CategoryNames[category]}");
            m_signalsBy[category] = Kind(name: $"gpu.queue.signals.{CategoryNames[category]}");
            for (var cause = 0; (cause < causes); cause++) {
                m_executesBy[(category * causes) + cause] = Kind(name: $"gpu.queue.executes.{CategoryNames[category]}.{CauseNames[cause]}");
            }
        }
        m_kinds = [m_frames, m_batches, m_waits, m_waitsReached, m_jobs, m_jobsLate, m_executes, m_lists, m_signals, .. m_executesBy, .. m_listsBy, .. m_signalsBy];
        m_counts = new WorkCounterSet(name: "gpu.queue", kinds: m_kinds);
    }

    /// <summary>Gets the process's queue work, across devices, backends and recreations.</summary>
    public static GpuQueueWork Process { get; } = new();
    /// <summary>Gets the category the calling thread's submissions count under now.</summary>
    public static GpuQueueCategory Current => CurrentCategory;
    /// <inheritdoc/>
    public string Name => m_counts.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => m_kinds;
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) => m_counts.TryRead(kind, out value);

    /// <summary>Gets whether <paramref name="category"/> names a background job: GPU work no pass of a frame waits on and
    /// whose completion host code waits for (a bake's samples, a probe's answers), counted in <c>jobs</c>.</summary>
    /// <param name="category">The category.</param>
    /// <returns>Whether it is <see cref="GpuQueueCategory.Bake"/> or <see cref="GpuQueueCategory.Probe"/>.</returns>
    public static bool IsBackground(GpuQueueCategory category) => (category is GpuQueueCategory.Bake or GpuQueueCategory.Probe);
    /// <summary>Gets whether a fenced submission made under <paramref name="category"/> is answered from a signal of its
    /// own, never from work of another category deferred behind it in a batch
    /// (<see cref="GpuSubmissionBatch{TEntry}.Admit"/>): host code waits on such a fence to reuse what the submission
    /// wrote or read what it copied back, so a fence answered from a later frame's work would hold the frame thread until
    /// the GPU finished that frame. A background job's (<see cref="IsBackground"/>) and an upload's
    /// (<see cref="GpuQueueCategory.Upload"/>: a residency's table ring, a source's staging) are; a frame's own passes,
    /// whose slots are waited frames in flight later, share the batch's last signal.</summary>
    /// <param name="category">The category.</param>
    /// <returns>Whether the category's fenced submissions answer from a signal of their own.</returns>
    public static bool OwnsSignal(GpuQueueCategory category) => (IsBackground(category: category) || (category == GpuQueueCategory.Upload));
    /// <summary>Counts the calling thread's queue work under <paramref name="category"/> until the returned scope is
    /// disposed, when the previous category returns: scopes nest, and the innermost names the work.</summary>
    /// <param name="category">The category.</param>
    /// <returns>The scope to dispose.</returns>
    public static Scope Attribute(GpuQueueCategory category) {
        var previous = CurrentCategory;

        CurrentCategory = category;
        return new Scope(previous: previous);
    }

    /// <summary>Counts one native execute of <paramref name="lists"/> command lists, every list owned by the calling
    /// thread's current category.</summary>
    /// <param name="cause">Why it happened now.</param>
    /// <param name="lists">The lists it carries.</param>
    public void Execute(GpuQueueCause cause, int lists) {
        CountExecute(cause: cause, category: CurrentCategory);
        m_counts.Add(m_lists, lists);
        m_counts.Add(m_listsBy[(int)CurrentCategory], lists);
    }
    /// <summary>Counts one native execute of a batch's deferred lists, each counted under the category it was deferred
    /// with; the execute itself counts under the calling thread's category, the code that split the batch.</summary>
    /// <param name="cause">What split the batch.</param>
    /// <param name="owners">The category of each list, in order.</param>
    public void Execute(GpuQueueCause cause, ReadOnlySpan<GpuQueueCategory> owners) {
        CountExecute(cause: cause, category: CurrentCategory);
        m_counts.Add(m_lists, owners.Length);
        foreach (var owner in owners) { m_counts.Count(m_listsBy[(int)owner]); }
    }
    /// <summary>Counts one completion signal under the calling thread's category.</summary>
    public void Signal() {
        m_counts.Count(m_signals);
        m_counts.Count(m_signalsBy[(int)CurrentCategory]);
    }
    /// <summary>Counts the background jobs' fenced submissions one hand-over of a batch carried.</summary>
    /// <param name="handed">The background jobs' fences the hand-over carried.</param>
    /// <param name="late">Those among them deferred ahead of work that does not answer from its own signal, whose fences
    /// therefore answer from a signal queued behind that work.</param>
    public void Jobs(int handed, int late) {
        m_counts.Add(m_jobs, handed);
        m_counts.Add(m_jobsLate, late);
    }
    /// <summary>Counts one present, the interval a reading divides by.</summary>
    public void Present() => m_counts.Count(m_frames);
    /// <summary>Counts one batch closed with deferred work.</summary>
    public void Batch() => m_counts.Count(m_batches);
    /// <summary>Counts one external wait a submitter was handed: dropped because its fence had already reached its value,
    /// or put on the queue.</summary>
    /// <param name="reached">Whether the wait's fence had already reached its value.</param>
    /// <returns><paramref name="reached"/>, so a submitter drops the wait in the same expression.</returns>
    public bool Waits(bool reached) {
        m_counts.Count(reached ? m_waitsReached : m_waits);
        return reached;
    }

    private void CountExecute(GpuQueueCause cause, GpuQueueCategory category) {
        m_counts.Count(m_executes);
        m_counts.Count(m_executesBy[((int)category * CauseNames.Length) + (int)cause]);
    }
    private static WorkKind Kind(string name) => new(name: name, unit: "count", workClass: WorkClass.Pacing);

    /// <summary>Restores the category an <see cref="Attribute"/> call replaced.</summary>
    public readonly ref struct Scope {
        private readonly GpuQueueCategory m_previous;

        internal Scope(GpuQueueCategory previous) => m_previous = previous;
        /// <summary>Restores the previous category.</summary>
        public void Dispose() => CurrentCategory = m_previous;
    }
}
