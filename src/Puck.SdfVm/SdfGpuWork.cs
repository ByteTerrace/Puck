using System.Diagnostics;
using System.Globalization;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>
/// One job source of a residency's background GPU work (<see cref="SdfGpuWork"/>): work that is not a frame's, taken,
/// recorded into a slot of the service's ring, submitted on the device's queue and answered with what it copied back
/// once its submission completes. The mesh bake's sampling is one (<see cref="SdfMeshBakeGpuWork"/>). Every member runs
/// on the device's recording thread, the frame thread.
/// </summary>
public interface ISdfGpuWorkClient : IDisposable {
    /// <summary>Stops admission and wakes pending submitters before the service drains fences and waits for retained
    /// readbacks. Releases no device resources; a client with no dependent submissions needs no action.</summary>
    void Stop() { }
    /// <summary>Takes the client's next job, or none: a job taken is recorded, submitted and answered, or abandoned.</summary>
    /// <param name="tables">The tables whose World set the job binds, holding the latest submitted upload.</param>
    /// <param name="job">The job, handed back to <see cref="Record"/> and then to <see cref="Complete"/> or
    /// <see cref="Abandon"/>.</param>
    /// <param name="estimatedSeconds">The GPU time the job is estimated to take, in seconds, which the service charges
    /// its budget until the job's timestamps measure it.</param>
    /// <returns><see langword="true"/> when the client hands over a job.</returns>
    bool TryTake(SdfWorldTables tables, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds);
    /// <summary>The host payload bytes staged by a taken job; zero for jobs without uploads.</summary>
    /// <param name="job">The taken job.</param>
    /// <returns>Bytes charged to the service's per-pump upload budget.</returns>
    int UploadBytes(object job) => 0;
    /// <summary>Records a taken job's commands into its slot's open command buffer, and the copy of what it answers with
    /// through <see cref="SdfGpuWorkRecording.CopyToReadback"/>.</summary>
    /// <param name="job">The job <see cref="TryTake"/> handed over.</param>
    /// <param name="recording">The slot the job records into, valid only for this call.</param>
    void Record(object job, in SdfGpuWorkRecording recording);
    /// <summary>Answers a job whose submission completed with what it copied back. The slot stays the client's until it
    /// releases the readback (<see cref="SdfGpuWorkReadback.Release"/>), which it may do on any thread once it has read
    /// it; the slot is reused only after that. A successful return transfers ownership; a throwing completion revokes
    /// the readback and abandons the job.</summary>
    /// <param name="job">The job.</param>
    /// <param name="readback">The bytes <see cref="SdfGpuWorkRecording.CopyToReadback"/> copied, released exactly
    /// once.</param>
    void Complete(object job, SdfGpuWorkReadback readback);
    /// <summary>Hands a job back unanswered: recording or completion failed, or the service is disposed before it
    /// completed. The client must not throw from this cleanup.</summary>
    /// <param name="job">The job.</param>
    void Abandon(object job);
    /// <summary>Gets the bytes of the client's own buffers for its jobs, by the memory they live in.</summary>
    GpuMemoryBytes OwnedBytes { get; }
}
/// <summary>What a completed job of the background GPU work copied back (<see cref="ISdfGpuWorkClient.Complete"/>): the
/// slot's readback, which its client reads and then releases, on any thread, giving the slot back to the service. The
/// release is the client's acknowledgement: the slot's next job is taken only after it.</summary>
public sealed class SdfGpuWorkReadback {
    private readonly object m_gate = new();
    private bool m_released;
    private IGpuReadbackBuffer? m_buffer;

    // A new token for each job, never reset or handed out again. An old holder cannot release a later job.
    internal SdfGpuWorkReadback(int slot, IGpuReadbackBuffer? buffer, int length, double? gpuSeconds) {
        Slot = slot;
        m_buffer = buffer;
        Length = length;
        GpuSeconds = gpuSeconds;
    }

    /// <summary>Gets the slot the job ran in, from zero to <see cref="SdfGpuWork.SlotCount"/>.</summary>
    public int Slot { get; }
    /// <summary>Gets the bytes the job copied back.</summary>
    public int Length { get; }
    /// <summary>The submission's GPU timestamp duration in seconds, or null on a queue without timestamps.</summary>
    public double? GpuSeconds { get; }
    internal bool Released { get { lock (m_gate) { return m_released; } } }

    /// <summary>Copies the bytes the job copied back into <paramref name="destination"/>; valid until
    /// <see cref="Release"/>.</summary>
    /// <param name="destination">The bytes to fill, at least <see cref="Length"/> long.</param>
    /// <exception cref="InvalidOperationException">The readback was released.</exception>
    public void Read(Span<byte> destination) {
        lock (m_gate) {
            if (m_released) { throw new InvalidOperationException(message: "A released readback is no longer the client's to read."); }
            if (Length > 0) { m_buffer!.Read(destination: destination[..Length]); }
        }
    }
    /// <summary>Gives the slot back to the service, once its client has read what it needs.</summary>
    /// <exception cref="InvalidOperationException">The readback was released already.</exception>
    public void Release() {
        lock (m_gate) {
            if (m_released) { throw new InvalidOperationException(message: "A background GPU job's readback is released once."); }
            Revoke();
        }
    }

    // A failed completion did not accept ownership. Serialize revocation with any read it started before throwing.
    internal void Revoke() {
        lock (m_gate) {
            m_buffer = null;
            m_released = true;
            Monitor.PulseAll(m_gate);
        }
    }
    // There is no disposable event for a late release to race. The condition and the buffer share one gate.
    internal void WaitReleased() { lock (m_gate) { while (!m_released) { Monitor.Wait(m_gate); } } }
}
/// <summary>A slot of the background GPU work a client records one job into (<see cref="ISdfGpuWorkClient.Record"/>),
/// valid only during that call: its index, which a client's own per-slot resources follow, and its open command
/// buffer.</summary>
public readonly ref struct SdfGpuWorkRecording {
    private readonly SdfGpuWork m_owner;
    private readonly int m_slot;

    internal SdfGpuWorkRecording(SdfGpuWork owner, int slot, nint commandBuffer, IGpuRecorder recorder) {
        m_owner = owner;
        m_slot = slot;
        CommandBuffer = commandBuffer;
        Recorder = recorder;
    }

    /// <summary>Gets the slot's index, from zero to <see cref="SdfGpuWork.SlotCount"/>: a slot is reused only after the job
    /// recorded into it completed and was answered, so a client's resources for the slot are free to rewrite.</summary>
    public int Slot => m_slot;
    /// <summary>Gets the slot's command buffer, open for recording.</summary>
    public nint CommandBuffer { get; }
    /// <summary>Gets the recorder the job records with.</summary>
    public IGpuRecorder Recorder { get; }

    /// <summary>Records a copy of a buffer's first bytes, which a compute shader wrote, into the slot's readback, which
    /// <see cref="ISdfGpuWorkClient.Complete"/> answers with. A job copies at most once.</summary>
    /// <param name="source">The buffer, last written by a compute shader.</param>
    /// <param name="bytes">The bytes to copy, at most the buffer's size.</param>
    /// <exception cref="InvalidOperationException">The job already copied.</exception>
    public void CopyToReadback(IGpuBuffer source, ulong bytes) => m_owner.CopyToReadback(bytes: bytes, commandBuffer: CommandBuffer, slot: m_slot, source: source);
}
/// <summary>
/// A residency's background GPU work: work that is not a frame's (the mesh bake's sampling, a probe trace), submitted
/// by one service that owns a bounded ring of slots, each a command buffer, a submission fence, a readback and a pair of
/// timestamps, and that submits a client's next job when a slot's last job completes rather than once per rendered
/// frame's pass. It is pumped on the device's recording thread (<see cref="Pump"/>), which the GPU layer requires of every
/// recording and submission; it polls its fences there and never waits on one while it runs, so it spins nowhere and
/// holds no thread, and its clients read what came back on their own workers.
/// <para>Invariant: a slot is reused only after its GPU fence signaled and its client released the job's readback
/// (<see cref="SdfGpuWorkReadback.Release"/>). The service is the slot's one owner: a client reaches a slot only through
/// the recording it is handed for one call and the readback it is handed until it releases it, so no caller writes a
/// slot it did not take.</para>
/// <para>Budget: the service takes at most <see cref="GpuShare"/> of the GPU's time, by each job's measured GPU time
/// (its timestamps, or its client's estimate on a queue without them), and keeps at most <see cref="SlotCount"/> jobs in
/// flight.</para>
/// </summary>
public sealed class SdfGpuWork : IDisposable {
    /// <summary>The jobs in flight at most.</summary>
    public const int SlotCount = 4;
    /// <summary>The share of the GPU's time background work takes at most, measured over each job's GPU time.</summary>
    public const double GpuShare = 0.25;
    /// <summary>The largest host payload one background upload job stages: one MiB.</summary>
    public const int UploadChunkBytes = 1 << 20;
    /// <summary>The most host payload bytes one pump stages.</summary>
    public const int UploadBudgetBytes = SlotCount * UploadChunkBytes;
    private static readonly TimeSpan HostBudget = TimeSpan.FromMilliseconds(2);
    // The GPU time, in seconds, an idle service may bank: an idle spell admits no more than this at once.
    private const double MostCreditSeconds = 0.02;

    private readonly IGpuDeviceContext m_device;
    private readonly GpuDeviceServices m_gpu;
    private readonly Slot[] m_slots = new Slot[SlotCount];
    private readonly List<ISdfGpuWorkClient> m_clients = [];
    private readonly IGpuTimestampPool? m_timestamps;
    private readonly string m_name;
    private int m_nextClient;
    private bool m_disposed;
    private bool m_faulted;
    private bool m_submissionFailed;

    private double m_credit = MostCreditSeconds;
    private long m_creditAt;

    // Since the first job: jobs submitted and answered, the GPU seconds they measured, the wall time some job was in
    // flight, the most in flight at once, the pumps, and the pumps the tables' World set could not be bound in.
    private long m_submitted;
    private long m_answered;
    private double m_gpuSeconds;
    private long m_busyTicks;
    private long m_busySince;
    private int m_inFlight;
    private int m_mostInFlight;
    private long m_pumps;
    private long m_unbound;
    private long m_budgetHolds;

    /// <summary>Initializes a new instance of the <see cref="SdfGpuWork"/> class: a slot's command buffer, fence and
    /// timestamp readback each, created on <paramref name="device"/>.</summary>
    /// <param name="device">The device; the service records and submits through its services on the recording
    /// thread.</param>
    /// <param name="name">The owner's name, which the slots' objects are named for.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="name"/> is
    /// <see langword="null"/>.</exception>
    public SdfGpuWork(IGpuDeviceContext device, string name) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: name);

        m_device = device;
        m_gpu = device.Services;
        m_name = name;

        using var scope = new GpuCreationScope();
        var gpu = m_gpu;

        if (gpu.TimestampFactory?.Create(count: (2u * SlotCount), name: new GpuObjectName(detail: "timestamps", owner: name, part: "gpu-work")) is { } timestamps) {
            m_timestamps = scope.Own(created: timestamps);
        }
        for (var slot = 0; (slot < SlotCount); slot++) {
            m_slots[slot] = new Slot(
                Commands: scope.Own(created: gpu.CommandPoolFactory.Create(name: new GpuObjectName(index: slot, owner: name, part: "gpu-work"))),
                Fence: scope.Own(created: gpu.QueueSubmitter.CreateSubmissionFence()),
                Times: scope.Own(created: gpu.BufferFactory.CreateReadback(name: new GpuObjectName(detail: "times", index: slot, owner: name, part: "gpu-work"), sizeBytes: (2 * sizeof(ulong))))
            );
        }
        scope.Complete();
    }

    /// <summary>Gets the jobs in flight now.</summary>
    public int InFlight => m_inFlight;

    /// <summary>Adds a job source the service takes jobs from in turn and disposes with itself.</summary>
    /// <param name="client">The source, called only on the recording thread.</param>
    public void Add(ISdfGpuWorkClient client) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        m_clients.Add(item: client);
    }

    /// <summary>Answers every job whose submission completed, then fills every free slot with the clients' next jobs while
    /// the budget allows and the tables' World set may be bound outside a frame
    /// (<see cref="SdfWorldTables.BindsBackgroundWork"/>). Runs on the device's recording thread, between the tables'
    /// upload and the next frame's preparation, and never blocks.</summary>
    /// <param name="tables">The tables whose World set the jobs bind.</param>
    /// <exception cref="ObjectDisposedException">The service is disposed.</exception>
    public void Pump(SdfWorldTables tables) {
        ArgumentNullException.ThrowIfNull(argument: tables);
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        if (m_faulted) { throw new InvalidOperationException("A failed background GPU service must be disposed before it can be replaced."); }
        try {
            m_pumps++;
            Poll();
            if (m_clients.Count == 0) { return; }
            if (!tables.BindsBackgroundWork) { m_unbound++; return; }
            Refill(tables: tables);
        } catch {
            m_faulted = true;
            throw;
        }
    }

    /// <summary>Describes the work done since the first job: jobs submitted and answered, their measured GPU time, the
    /// wall time some job was in flight and the share of it the GPU spent on them, the most in flight, the pumps, the
    /// pumps the World set could not be bound in and those the budget held.</summary>
    /// <returns>One space-led field.</returns>
    public string Describe() {
        var busy = (m_busyTicks + ((m_inFlight > 0) ? (Stopwatch.GetTimestamp() - m_busySince) : 0L)) / ((double)Stopwatch.Frequency);

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $" gpu-work=[jobs:{m_submitted},answered:{m_answered},gpu:{(m_gpuSeconds * 1000.0):0}ms,busy:{busy:0.0}s,occupancy:{((busy > 0.0) ? (m_gpuSeconds / busy) : 0.0):0.00},most-in-flight:{m_mostInFlight},pumps:{m_pumps},unbound:{m_unbound},budget-holds:{m_budgetHolds},timestamps:{(m_timestamps is not null ? "measured" : "estimated")}]");
    }

    /// <summary>Describes the buffers the work holds for <c>world.residency</c>: its slots' readbacks and its clients'
    /// own buffers, by the memory they live in, held at their largest job's size until the tables go.</summary>
    /// <returns>One line.</returns>
    public string DescribeBytes() {
        var readbacks = 0UL;

        foreach (var slot in m_slots) { readbacks += (slot.Readback?.SizeBytes ?? 0UL) + slot.Times.SizeBytes; }

        var clients = default(GpuMemoryBytes);

        foreach (var client in m_clients) { clients += client.OwnedBytes; }
        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"gpu-work slots={SlotCount} in-flight={m_inFlight} readback-bytes={readbacks} client-device-bytes={clients.DeviceLocal} client-host-bytes={clients.HostVisible}");
    }

    /// <inheritdoc/>
    /// <remarks>Waits every job in flight, a host wait on its fence, then hands it back to its client unanswered; waits,
    /// on the host's monitor, for every client still reading a readback to release it; and disposes the clients and the
    /// slots.</remarks>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        // A client can retain one completed output until a dependent copy finishes. Wake a coordinator waiting to
        // submit that copy before waiting for its retained token; device resources stay alive until the drain below.
        foreach (var client in m_clients) { client.Stop(); }
        // A submit can execute commands and then fail to arm its fence. That fence proves nothing; drain the device
        // before freeing the recorded resources, including on a backend that armed a fence but never submitted it.
        if (m_submissionFailed) { m_device.TryWaitIdle(); }
        foreach (var slot in m_slots) {
            if (slot.Job is not { } job) { continue; }
            if (!m_submissionFailed) { try { slot.Fence.Wait(); } catch (DeviceLostException) { } }
            slot.Client!.Abandon(job: job);
            slot.Job = null;
            slot.Client = null;
        }
        m_inFlight = 0;
        foreach (var slot in m_slots) { slot.Lease?.WaitReleased(); }
        foreach (var client in m_clients) { client.Dispose(); }
        m_clients.Clear();
        foreach (var slot in m_slots) {
            slot.Readback?.Dispose();
            slot.Times.Dispose();
            slot.Fence.Dispose();
            slot.Commands.Dispose();
        }
        m_timestamps?.Dispose();
    }

    // Hands each job whose fence signaled to its client: its timestamps settle its charge, and the client reads what it
    // copied back, keeping the slot until it releases the readback.
    private void Poll() {
        for (var index = 0; (index < SlotCount); index++) {
            var slot = m_slots[index];

            if ((slot.Job is not { } job) || !slot.Fence.IsSignaled) { continue; }
            slot.Fence.Wait();

            var client = slot.Client!;

            var seconds = Settle(slot: slot);
            var readback = new SdfGpuWorkReadback(slot: index, buffer: slot.Readback, length: (int)slot.ReadbackBytes, gpuSeconds: m_timestamps is null ? null : seconds);
            slot.Lease = readback;
            slot.Job = null;
            slot.Client = null;
            if (--m_inFlight == 0) { m_busyTicks += (Stopwatch.GetTimestamp() - m_busySince); }
            try {
                client.Complete(job: job, readback: readback);
                m_answered++;
            } catch {
                readback.Revoke();
                client.Abandon(job: job);
                throw;
            }
        }
    }
    // Replaces a job's estimated charge with its measured GPU time, when the queue writes timestamps.
    private double Settle(Slot slot) {
        var seconds = slot.Estimate;

        if (m_timestamps is { } timestamps) {
            Span<byte> times = stackalloc byte[(2 * sizeof(ulong))];

            slot.Times.Read(destination: times);

            var mask = ((timestamps.ValidBits >= 64) ? ulong.MaxValue : ((1UL << ((int)timestamps.ValidBits)) - 1UL));
            var ticks = ((BitConverter.ToUInt64(value: times[sizeof(ulong)..]) - BitConverter.ToUInt64(value: times)) & mask);

            seconds = ((ticks * timestamps.NanosecondsPerTick) * 1e-9);
            m_credit += (slot.Estimate - seconds);
        }
        m_gpuSeconds += seconds;
        return seconds;
    }
    // Banks the GPU time the share allows since the last pump, up to MostCreditSeconds.
    private void Accrue() {
        var now = Stopwatch.GetTimestamp();

        if (m_creditAt != 0) {
            m_credit = Math.Min(val1: MostCreditSeconds, val2: (m_credit + (((now - m_creditAt) / ((double)Stopwatch.Frequency)) * GpuShare)));
        }
        m_creditAt = now;
    }
    // Takes each free slot's next job, the clients in turn, while the budget holds credit; a job may overdraw it once.
    private void Refill(SdfWorldTables tables) {
        Accrue();
        var began = Stopwatch.GetTimestamp();
        var uploaded = 0;
        for (var index = 0; (index < SlotCount); index++) {
            if (uploaded >= UploadBudgetBytes || Stopwatch.GetElapsedTime(began) >= HostBudget) { return; }
            var slot = m_slots[index];

            if ((slot.Job is not null) || (slot.Lease is { Released: false })) { continue; }
            if (m_credit <= 0.0) { m_budgetHolds++; return; }
            if (!TryTake(tables: tables, client: out var client, estimate: out var estimate, job: out var job)) { return; }
            uploaded += client.UploadBytes(job);
            Submit(client: client, estimate: estimate, index: index, job: job, slot: slot);
        }
    }
    private bool TryTake(SdfWorldTables tables, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out ISdfGpuWorkClient? client, out double estimate, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job) {
        for (var turn = 0; (turn < m_clients.Count); turn++) {
            var candidate = m_clients[((m_nextClient + turn) % m_clients.Count)];

            if (candidate.TryTake(estimatedSeconds: out estimate, job: out job, tables: tables)) {
                m_nextClient = ((m_nextClient + turn + 1) % m_clients.Count);
                client = candidate;
                return true;
            }
        }
        client = null;
        estimate = 0.0;
        job = null;
        return false;
    }
    // Records a job into its slot between its timestamps and submits it armed with the slot's fence.
    private void Submit(ISdfGpuWorkClient client, object job, double estimate, int index, Slot slot) {
        var recorder = m_gpu.Recorder;
        var commandBuffer = slot.Commands.CommandBufferHandle;
        var first = (uint)(2 * index);

        slot.ReadbackBytes = 0;
        slot.Copied = false;
        try {
            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer);
            recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, label: $"{m_name} gpu-work");
            m_timestamps?.Reset(command: commandBuffer, count: 2, first: first);
            m_timestamps?.Write(command: commandBuffer, index: first);
            client.Record(job: job, recording: new SdfGpuWorkRecording(commandBuffer: commandBuffer, owner: this, recorder: recorder, slot: index));
            if (m_timestamps is { } timestamps) {
                timestamps.Write(command: commandBuffer, index: (first + 1));
                timestamps.Resolve(command: commandBuffer, count: 2, destination: slot.Times.BufferHandle, first: first, offset: 0);
                recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: slot.Times.BufferHandle,
                    sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                    destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
            }
            recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer);
        } catch {
            client.Abandon(job: job);
            throw;
        }
        // Retain the taken job even if submission throws after executing commands but before arming its fence.
        slot.Job = job;
        slot.Client = client;
        try { m_gpu.QueueSubmitter.Submit(commandBufferHandles: [commandBuffer], fence: slot.Fence); }
        catch { m_submissionFailed = true; throw; }
        slot.Estimate = estimate;
        m_credit -= estimate;
        m_submitted++;
        if (m_inFlight++ == 0) { m_busySince = Stopwatch.GetTimestamp(); }
        m_mostInFlight = Math.Max(val1: m_mostInFlight, val2: m_inFlight);
    }
    // Records a job's copy into its slot's readback, growing the readback, which no job in flight reads, when it is short.
    internal void CopyToReadback(int slot, nint commandBuffer, IGpuBuffer source, ulong bytes) {
        var target = m_slots[slot];

        if (target.Copied) { throw new InvalidOperationException(message: "A background GPU job copies back once."); }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: bytes, other: source.SizeBytes);
        if (bytes > int.MaxValue) { throw new ArgumentOutOfRangeException(paramName: nameof(bytes), message: "A background GPU job copies back at most 2 GiB."); }
        if ((target.Readback is not { } readback) || (readback.SizeBytes < bytes)) {
            target.Readback?.Dispose();
            target.Readback = readback = m_gpu.BufferFactory.CreateReadback(name: new GpuObjectName(detail: "readback", index: slot, owner: m_name, part: "gpu-work"),
                sizeBytes: Granule(bytes: bytes));
        }

        var recorder = m_gpu.Recorder;

        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: source.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
            destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
        recorder.CopyBuffer(commandBufferHandle: commandBuffer, destinationBufferHandle: readback.BufferHandle, sizeBytes: bytes, sourceBufferHandle: source.BufferHandle);
        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: readback.BufferHandle,
            sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
        target.ReadbackBytes = bytes;
        target.Copied = true;
    }

    /// <summary>Returns the bytes a slot's buffer of at least <paramref name="bytes"/> bytes is allocated at: whole
    /// granules of 4 MiB, so a client whose jobs grow toward a bound replaces a slot's buffers a few times, never once a
    /// job, and holds at most a granule more than its largest job.</summary>
    /// <param name="bytes">The bytes the buffer holds at least.</param>
    /// <returns>The allocation's bytes.</returns>
    internal static ulong Granule(ulong bytes) {
        const ulong Bytes = (4UL << 20);

        return (((Math.Max(val1: bytes, val2: 1UL) + (Bytes - 1UL)) / Bytes) * Bytes);
    }
    // One slot: its command buffer, fence and timestamp readback, the job in it and its client, the readback its job
    // copied into and how many bytes, and the GPU seconds the job was charged.
    private sealed class Slot(IGpuCommandPool Commands, IGpuSubmissionFence Fence, IGpuReadbackBuffer Times) {
        public IGpuCommandPool Commands { get; } = Commands;
        public IGpuSubmissionFence Fence { get; } = Fence;
        public IGpuReadbackBuffer Times { get; } = Times;
        public SdfGpuWorkReadback? Lease { get; set; }
        public IGpuReadbackBuffer? Readback { get; set; }
        public ulong ReadbackBytes { get; set; }
        public bool Copied { get; set; }
        public object? Job { get; set; }
        public ISdfGpuWorkClient? Client { get; set; }
        public double Estimate { get; set; }
    }
}
