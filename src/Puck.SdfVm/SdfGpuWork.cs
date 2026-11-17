using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Waiting;

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
    /// <param name="uploadBytes">The most host payload bytes the job may stage (<see cref="UploadBytes"/>), from
    /// <see cref="SdfGpuWork.UploadChunkBytes"/> to <see cref="SdfGpuWork.MostUploadJobBytes"/>: what the service's upload
    /// budget holds now.</param>
    /// <param name="job">The job, handed back to <see cref="Record"/> and then to <see cref="Complete"/> or
    /// <see cref="Abandon"/>.</param>
    /// <param name="estimatedSeconds">The GPU time the job is estimated to take, in seconds, which the service charges
    /// its budget until the job's timestamps measure it.</param>
    /// <returns><see langword="true"/> when the client hands over a job.</returns>
    bool TryTake(SdfWorldTables tables, int uploadBytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds);
    /// <summary>The host payload bytes staged by a taken job, at most the <c>uploadBytes</c> its take allowed; zero for
    /// jobs without uploads.</summary>
    /// <param name="job">The taken job.</param>
    /// <returns>Bytes charged to the service's upload budget.</returns>
    int UploadBytes(object job) => 0;
    /// <summary>Records a taken job's commands into its slot's open command buffer, and the copy of what it answers with
    /// through <see cref="SdfGpuWorkRecording.CopyToReadback"/>. The buffers the job reads and writes for the slot come
    /// from the recording (<see cref="SdfGpuWorkRecording.HostBuffer"/>, <see cref="SdfGpuWorkRecording.DeviceBuffer"/>),
    /// which reserves them; a job the device has no room for is handed back (<see cref="Abandon"/>) and offered
    /// again.</summary>
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
    /// <summary>Hands a job back unanswered: recording or completion failed, the device had no room for what the job
    /// holds, or the service is disposed before it completed. The client must not throw from this cleanup.</summary>
    /// <param name="job">The job.</param>
    void Abandon(object job);
    /// <summary>Describes a taken job in a few <c>key=value</c> words, for the service's slow-job and device-loss lines:
    /// what kind of work it is and how much of it, so a long or hung submission names the work it was handed.</summary>
    /// <param name="job">The taken job.</param>
    /// <returns>The description; by default the job's type name.</returns>
    string Describe(object job) => job.GetType().Name;

    /// <summary>Gets the bytes of the client's own buffers that outlive a job (its tables and caches), by the memory
    /// they live in. A slot's buffers are the service's (<see cref="SdfGpuWorkRecording.HostBuffer"/>) and are not
    /// among them.</summary>
    GpuMemoryBytes OwnedBytes { get; }
    /// <summary>Gets the category the client's jobs count under in <see cref="GpuQueueWork"/>.</summary>
    GpuQueueCategory Category { get; }
}
/// <summary>What a completed job of the background GPU work copied back (<see cref="ISdfGpuWorkClient.Complete"/>): the
/// slot's readback, which its client reads and then releases, on any thread, giving the slot back to the service. The
/// release is the client's acknowledgement: the slot's next job is taken only after it.</summary>
public sealed class SdfGpuWorkReadback {
    private readonly object m_gate = new();
    private readonly SdfGpuWork m_owner;

    private bool m_released;
    private IGpuReadbackBuffer? m_buffer;

    // A new token for each job, never reset or handed out again. An old holder cannot release a later job.
    internal SdfGpuWorkReadback(SdfGpuWork owner, int slot, IGpuReadbackBuffer? buffer, int length, double? gpuSeconds) {
        m_owner = owner;
        Slot = slot;
        m_buffer = buffer;
        Length = length;
        GpuSeconds = gpuSeconds;
    }

    /// <summary>Gets the slot the job ran in, from zero to <see cref="SdfGpuWork.SlotCount"/>.</summary>
    public int Slot { get; }
    /// <summary>Gets the bytes the job copied back.</summary>
    public int Length { get; }
    /// <summary>The submission's GPU timestamp duration in seconds, held to the wall time the job was in flight
    /// (<see cref="SdfGpuWorkBudget.MeasuredWithin"/>), or null on a queue without timestamps.</summary>
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
            Monitor.PulseAll(obj: m_gate);
        }
        m_owner.Wake();
    }
    // There is no disposable event for a late release to race. The condition and the buffer share one gate.
    internal void WaitReleased() { lock (m_gate) { while (!m_released) { Monitor.Wait(obj: m_gate); } } }
}
/// <summary>How a slot's buffer is sized for a job (<see cref="SdfGpuWorkRecording.HostBuffer"/>,
/// <see cref="SdfGpuWorkRecording.DeviceBuffer"/>).</summary>
public enum SdfGpuWorkSizing {
    /// <summary>The buffer holds at least the bytes and is allocated in whole granules (<see cref="SdfGpuWork.Granule"/>),
    /// so jobs that grow toward a bound replace it a few times.</summary>
    Granule,
    /// <summary>The buffer holds at least the bytes and is allocated at exactly the bytes asked when it is replaced.</summary>
    Fit,
    /// <summary>The buffer holds exactly the bytes, replaced whenever a job asks another size.</summary>
    Exact,
}
/// <summary>A slot of the background GPU work a client records one job into (<see cref="ISdfGpuWorkClient.Record"/>),
/// valid only during that call: its index, which a client's own per-slot resources follow, and its open command
/// buffer.</summary>
public readonly ref struct SdfGpuWorkRecording {
    private readonly SdfGpuWork m_owner;
    private readonly ISdfGpuWorkClient m_client;
    private readonly int m_slot;

    internal SdfGpuWorkRecording(SdfGpuWork owner, ISdfGpuWorkClient client, int slot, nint commandBuffer, IGpuRecorder recorder) {
        m_owner = owner;
        m_client = client;
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
    /// <summary>Gets the actual queue the service submits this recording on.</summary>
    public IGpuQueueSubmitter Queue => m_owner.Queue;

    /// <summary>Returns the slot's host-visible buffer for one part of the client's jobs, sized for this job: the one a
    /// job of the client last used in the slot when it still fits, or a new one. The service owns it: it reserves the
    /// growth before it allocates (<see cref="SdfGpuWork"/>'s hold), keeps the buffer for the slot's next jobs and
    /// gives it back once no job has used it for <see cref="SdfGpuWork.IdleSeconds"/>.</summary>
    /// <param name="part">The part's name among the client's buffers, a constant.</param>
    /// <param name="bytes">The bytes the job needs.</param>
    /// <param name="usage">How shaders use the buffer.</param>
    /// <param name="sizing">How the buffer is sized.</param>
    /// <returns>The buffer. It is another object than the part's last one whenever it was replaced, so a client that
    /// keeps what it wrote into a buffer, or the descriptors that name it, compares the object.</returns>
    /// <exception cref="SdfGpuWorkDeferredException">The device has no room for the buffer; the service hands the job
    /// back.</exception>
    public IGpuStorageBuffer HostBuffer(string part, ulong bytes, GpuBufferUsage usage, SdfGpuWorkSizing sizing = SdfGpuWorkSizing.Granule) =>
        ((IGpuStorageBuffer)m_owner.Hold(bytes: bytes, client: m_client, hostVisible: true, part: part, readback: false, sizing: sizing, slot: m_slot, usage: usage));
    /// <summary>Returns the slot's device-local buffer for one part of the client's jobs, as
    /// <see cref="HostBuffer"/> returns a host-visible one.</summary>
    /// <param name="part">The part's name among the client's buffers, a constant.</param>
    /// <param name="bytes">The bytes the job needs.</param>
    /// <param name="usage">How shaders use the buffer.</param>
    /// <param name="sizing">How the buffer is sized.</param>
    /// <returns>The buffer, another object than the part's last one whenever it was replaced.</returns>
    /// <exception cref="SdfGpuWorkDeferredException">The device has no room for the buffer; the service hands the job
    /// back.</exception>
    public IGpuBuffer DeviceBuffer(string part, ulong bytes, GpuBufferUsage usage, SdfGpuWorkSizing sizing = SdfGpuWorkSizing.Granule) =>
        m_owner.Hold(bytes: bytes, client: m_client, hostVisible: false, part: part, readback: false, sizing: sizing, slot: m_slot, usage: usage);
    /// <summary>Records a copy of a buffer's first bytes, which a compute shader wrote, into the slot's readback, which
    /// <see cref="ISdfGpuWorkClient.Complete"/> answers with. A job copies at most once.</summary>
    /// <param name="source">The buffer, last written by a compute shader.</param>
    /// <param name="bytes">The bytes to copy, at most the buffer's size.</param>
    /// <exception cref="InvalidOperationException">The job already copied.</exception>
    /// <exception cref="SdfGpuWorkDeferredException">The device has no room for the readback; the service hands the job
    /// back.</exception>
    public void CopyToReadback(IGpuBuffer source, ulong bytes) => m_owner.CopyToReadback(bytes: bytes, commandBuffer: CommandBuffer, slot: m_slot, source: source);
    /// <summary>Records the copy of what a staged region owes into the slot's command buffer: flushes the region's
    /// <paramref name="regionSlot"/>, records a grown region's carry and the copy, and hands the region's buffer to
    /// <paramref name="readers"/>, which read it in this or any later submission. A region that owes nothing records
    /// nothing.</summary>
    /// <param name="region">The region, whose owed words the job wrote or owed before this call.</param>
    /// <param name="regionSlot">The region's slot whose staging buffer the job stages into: one that no submission in
    /// flight still reads, which a slot of this service is once its job completed.</param>
    /// <param name="readers">The shader stages that read the region's buffer.</param>
    public void CopyRegion(GpuRegion region, int regionSlot, GpuStage readers) {
        var command = CommandBuffer;
        var copies = new GpuRegionCopyRecording(recorder: Recorder, readers: readers, begin: () => command);

        copies.Record(handsToReaders: true, region: region, slot: regionSlot);
        _ = copies.Finish();
    }
}
/// <summary>A background GPU job's buffer the device has no room for: the service's reservation, or the device itself,
/// refused it. The service catches it, hands the job back to its client and offers it again once a job of its own has
/// completed.</summary>
public sealed class SdfGpuWorkDeferredException : Exception {
    /// <summary>Initializes a new instance of the <see cref="SdfGpuWorkDeferredException"/> class.</summary>
    /// <param name="part">The buffer's part.</param>
    /// <param name="bytes">The bytes the pool had no room for.</param>
    /// <param name="innerException">The device's refusal, or <see langword="null"/> when the reservation refused.</param>
    public SdfGpuWorkDeferredException(string part, long bytes, Exception? innerException = null)
        : base(message: $"The device has no room for {bytes} bytes of a background GPU job's {part}.", innerException: innerException) { }
}
/// <summary>
/// A residency's background GPU work: work that is not a frame's (the mesh bake's sampling, a probe trace), submitted
/// by one service that owns a bounded ring of slots, each a command buffer, a submission fence, a readback and a pair of
/// timestamps, and that submits a client's next job when a slot's last job completes rather than once per rendered
/// frame's pass. It runs on the device's recording thread, which the GPU layer requires of every recording and
/// submission, at three kinds of point: the frame's pumps (<see cref="Pump"/>), a held frame's drive
/// (<see cref="Drive"/>), and the host's wait between frames (<see cref="HostWaits.Pace"/>), where the thread sleeps
/// on its deadline and the service's wake together and steps the work when a job's fence completes
/// (<see cref="IGpuSubmissionFence.SignalOnCompletion"/>), a client offers a job (<see cref="Wake"/>) or the budget
/// next holds credit. It polls its fences and never waits on one while it runs, so it spins nowhere and holds no
/// thread, and its clients read what came back on their own workers.
/// <para>Invariant: a slot is reused only after its GPU fence signaled and its client released the job's readback
/// (<see cref="SdfGpuWorkReadback.Release"/>). The service is the slot's one owner: a client reaches a slot only through
/// the recording it is handed for one call and the readback it is handed until it releases it, so no caller writes a
/// slot it did not take.</para>
/// <para>Invariant: a job's completion wakes the host's wait. One door submits a job (the service's own submit), and it
/// asks the slot's fence for the wake as it does, so a job cannot be in flight without one; the wake may come early or
/// twice, and a step reads the fences either way. It stops at the host: a thread that never paces (a host with no
/// pacer wait, a swap chain's own wait or a blocked present) steps the work at its frame's pumps alone.</para>
/// <para>Invariant: what a job reads is neither rewritten nor released before its fence. A job leases the tables'
/// generation as it is taken (<see cref="SdfWorldTables.BackgroundGeneration"/>, the uploads submitted, and the ring
/// slot the latest wrote), and the tables' two doors read the leases: the retirement door releases nothing retired at
/// or after the oldest lease in flight (<see cref="OldestLease"/>), and the upload waits out every lease on the ring
/// slot it rewrites (<see cref="EndLeases"/>). A lease ends when its job's fence has signaled. The lease holds on any
/// queue; on the frame's own queue the upload's wait for its predecessor already implies it, so the doors never
/// wait and the count of waits (<c>lease-waits</c>) stays zero.</para>
/// <para>Invariant: what a job holds on the device is reserved before it is allocated. A slot's buffers, its readback
/// among them, come from one door (<see cref="SdfGpuWorkRecording.HostBuffer"/>,
/// <see cref="SdfGpuWorkRecording.DeviceBuffer"/>, <see cref="SdfGpuWorkRecording.CopyToReadback"/>), which raises the
/// service's reservation (<see cref="SdfResidencySelector.WorkOwner"/>, workspace) by the growth before it creates the
/// buffer and lowers it when the buffer goes. A job the pool has no room for is handed back and offered again once a
/// slot of the service comes back or <see cref="DeferralRetry"/> has passed; a service with no job in flight and no
/// slot still held by a reader reserves its job as its floor, as does one whose job has waited
/// <see cref="ForcedPatience"/>, so the work always progresses. It stops at the owner (every service shares one
/// account entry, so another service's idle buffers stand against a floor until they go) and at the clients' own
/// tables and caches, which are not a job's.</para>
/// <para>Sizing: a slot's buffer grows to the largest job that used it and goes once no job in the last
/// <see cref="IdleSeconds"/> used it, so buffers sized for a cold bake's largest batch go once the bake settles rather
/// than living as long as the tables.</para>
/// <para>Budget: the service draws on its presentation's one budget (<see cref="SdfGpuWorkBudget"/>), which every
/// residency's service shares: together they take at most <see cref="GpuShare"/> of the GPU's time, by each job's
/// measured GPU time (its timestamps, or its client's estimate on a queue without them), and stage at most
/// <see cref="UploadBytesPerSecond"/> of host payload. Each service also records for at most <see cref="HostShare"/> of
/// the wall time between its steps and keeps at most <see cref="SlotCount"/> jobs in flight. Every budget is a rate of
/// wall time, never a count a pump, so work such as landing a mesh region takes as many seconds whatever the frame
/// rate. A client cuts its jobs to <see cref="FrameShareSeconds"/> at its kind's measured rate
/// (<see cref="SdfGpuWorkRate"/>): the queue runs a job whole before the frame submitted after it, so a job longer than
/// the share delays that frame by its length whatever the average share.</para>
/// </summary>
public sealed class SdfGpuWork : IDisposable {
    internal IGpuQueueSubmitter Queue => m_gpu.QueueSubmitter;

    /// <summary>The jobs in flight at most.</summary>
    public const int SlotCount = 4;
    /// <summary>The share of the GPU's time background work takes at most, measured over each job's GPU time.</summary>
    public const double GpuShare = 0.25;
    /// <summary>The seconds a free slot keeps a buffer no job in that span used.</summary>
    public const double IdleSeconds = 2.0;
    /// <summary>The fewest host payload bytes an upload job is allowed to stage, which the budget may overdraw once: one
    /// MiB.</summary>
    public const int UploadChunkBytes = (1 << 20);
    /// <summary>The most host payload bytes one upload job stages: eight MiB, within a staged region slot's staging floor
    /// (<see cref="GpuRegion.StagingFloorBytes"/>).</summary>
    public const int MostUploadJobBytes = (8 << 20);
    /// <summary>The host payload, in bytes a second, a presentation's background uploads stage at most: a chunk in every
    /// slot of one service each frame at the 60 Hz frame bar. It accrues by wall time between steps, so a presentation
    /// whose frames come slower (a heavy frame, or one the compositor throttles) stages more a step and lands its words
    /// in the same seconds.</summary>
    public const double UploadBytesPerSecond = ((SlotCount * UploadChunkBytes) * 60.0);
    /// <summary>The share of wall time, between steps, one step spends recording jobs at most: two milliseconds of a
    /// 60 Hz frame. A step spends at least <see cref="HostBudget"/> and at most <see cref="MostHostBudget"/>.</summary>
    public const double HostShare = 0.12;
    /// <summary>The rate, in bytes a second, an upload job's GPU time is estimated at until its timestamps measure it:
    /// the copy kernel's read of host staging memory across the bus.</summary>
    public const double UploadCopyBytesPerSecond = 1e10;
    /// <summary>The measured GPU time, in seconds, past which a completed job is written as a <c>[sdf.gpu-work] slow</c>
    /// line: a twentieth of a second, a fortieth of the two seconds after which Windows resets a device whose submission
    /// has not finished (TDR).</summary>
    public const double SlowJobSeconds = 0.05;
    /// <summary>Background work's share of one frame's GPU time at the 60 Hz frame bar, in seconds: a job measured
    /// longer delays the frame queued behind it by more than the share <see cref="GpuShare"/> grants, whatever the
    /// budget's average says.</summary>
    public const double FrameShareSeconds = (GpuShare / 60.0);

    /// <summary>The wall time one <see cref="Drive"/> takes at most: one frame at the 60 Hz frame bar, so the host's
    /// loop comes round to its console and window as often for each residency a held frame drives. On the host's pump
    /// thread the iteration's wait allowance bounds it too (<see cref="HostWaits"/>).</summary>
    public static readonly TimeSpan DriveSlice = TimeSpan.FromSeconds(value: (1.0 / 60.0));

    /// <summary>Returns the GPU time, in seconds, an upload job of <paramref name="bytes"/> host payload bytes is
    /// estimated to take (<see cref="UploadCopyBytesPerSecond"/>).</summary>
    /// <param name="bytes">The job's host payload bytes.</param>
    /// <returns>The estimate, in seconds.</returns>
    public static double UploadSecondsOf(long bytes) => (bytes / UploadCopyBytesPerSecond);

    /// <summary>The recording time one step spends at least, whatever the wall time since the last; also the time a
    /// step inside the host's wait leaves before the wait's deadline, within which it takes no job.</summary>
    public static readonly TimeSpan HostBudget = TimeSpan.FromMilliseconds(milliseconds: 2);
    /// <summary>The recording time one step spends at most, however long since the last.</summary>
    public static readonly TimeSpan MostHostBudget = TimeSpan.FromMilliseconds(milliseconds: 16);

    /// <summary>The wall time after which a job handed back for want of device room is offered again although no slot
    /// has come back since: a quarter of a second, so room another holder gave up is found.</summary>
    public static readonly TimeSpan DeferralRetry = TimeSpan.FromSeconds(value: 0.25);
    /// <summary>The wall time a service with no job in flight lets a job wait for a slot a reader still holds before it
    /// reserves the job as its floor whatever the pool holds: one second.</summary>
    public static readonly TimeSpan ForcedPatience = TimeSpan.FromSeconds(value: 1.0);

    private static readonly WaitReason ServiceReason = new(Subsystem: "sdf.gpu-work", WaitsFor: "a background job's completion or its next job");
    private const string ReadbackPart = "readback";

    private readonly GpuDeviceServices m_gpu;
    private readonly SdfGpuWorkBudget m_budget;
    private readonly SdfGpuWorkHold? m_hold;

    private readonly Slot[] m_slots = new Slot[SlotCount];
    private readonly List<ISdfGpuWorkClient> m_clients = [];

    private readonly IGpuTimestampPool? m_timestamps;
    private readonly string m_name;

    // The registration that has the recording thread step the work while it waits between frames, and whose signal a
    // job's fence, a client's offer and a released readback set. Made at the first step, on the thread that steps.
    private WaitService? m_service;
    // The tables the latest pump or drive named, which a step inside the host's wait binds, and whether that staging
    // drove the work for a held frame, whose steps in the wait then take jobs outside the budgets too.
    private SdfWorldTables? m_tables;
    private bool m_driven;
    // A failure of a step inside the host's wait, rethrown by the next pump or drive, where the host's recovery
    // catches a lost device.
    private ExceptionDispatchInfo? m_failure;

    private int m_nextClient;
    private bool m_disposed;
    private bool m_faulted;
    // While a job handed back for want of room waits: when the first was handed back since a job was last submitted
    // (zero for none), when the latest was, and the slots that were not free then.
    private long m_deferredSince;
    private long m_deferredAt;
    private int m_deferredBusy;
    private long m_pumpedAt;
    // Since the first job: jobs submitted and answered, the GPU seconds they measured, the wall time some job was in
    // flight, the most in flight at once, the steps, and the steps the tables' World set could not be bound in.
    private long m_submitted;
    private long m_answered;
    private double m_gpuSeconds;
    private long m_busyTicks;
    private long m_busySince;
    private int m_inFlight;
    private int m_mostInFlight;
    private long m_pumps;
    // The steps taken inside the host's wait, and the jobs they submitted.
    private long m_services;
    private long m_serviceJobs;
    // The drives, and the ticks they spent waiting with a job in flight, waiting for a client and stepping.
    private long m_drives;
    private long m_driveFenceTicks;
    private long m_driveIdleTicks;
    private long m_driveStepTicks;
    private long m_unbound;
    private long m_budgetHolds;
    private long m_deferrals;
    private long m_leaseWaits;
    // Since the first job: the jobs measured past FrameShareSeconds, and the longest measured job with what it was.
    private long m_overFrameShare;
    private double m_mostJobSeconds;

    private string m_mostJob = "none";

    /// <summary>Initializes a new instance of the <see cref="SdfGpuWork"/> class: a slot's command buffer, fence and
    /// timestamp readback each, created on <paramref name="device"/>.</summary>
    /// <param name="device">The device; the service records and submits through its services on the recording
    /// thread.</param>
    /// <param name="name">The owner's name, which the slots' fixed objects are named for.</param>
    /// <param name="selector">The presentation's selector, whose one budget the service draws on and whose account
    /// reserves what its jobs hold (<see cref="SdfResidencySelector.Work"/>,
    /// <see cref="SdfResidencySelector.ReserveWork"/>); <see langword="null"/> gives the service a budget of its own
    /// and reserves nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="name"/> is
    /// <see langword="null"/>.</exception>
    public SdfGpuWork(IGpuDeviceContext device, string name, SdfResidencySelector? selector = null) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: name);

        m_gpu = device.Services;
        m_name = name;
        m_budget = (selector?.Work ?? new SdfGpuWorkBudget());

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
        m_hold = selector?.ReserveWork();
        scope.Complete();
    }

    /// <summary>Gets the jobs in flight now.</summary>
    public int InFlight => m_inFlight;

    /// <summary>Returns the oldest generation a job in flight leased (<see cref="SdfWorldTables.BackgroundGeneration"/>
    /// as it was taken), or <see cref="ulong.MaxValue"/> when no lease stands: nothing the tables retired at or after
    /// it may be released. A lease ends when its job's fence has signaled, read here; a job that never reached the
    /// queue reads nothing and holds none.</summary>
    /// <returns>The generation.</returns>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    public ulong OldestLease() {
        var oldest = ulong.MaxValue;

        foreach (var slot in m_slots) {
            if ((slot.Job is not null) && (slot.Generation < oldest) && Reads(slot: slot)) { oldest = slot.Generation; }
        }
        return oldest;
    }

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
    /// upload and the next frame's preparation, and never blocks. Between this pump and the next, the host's wait steps
    /// the work the same way against the same tables.</summary>
    /// <param name="tables">The tables whose World set the jobs bind.</param>
    /// <exception cref="ObjectDisposedException">The service is disposed.</exception>
    public void Pump(SdfWorldTables tables) {
        Enter(driven: false, tables: tables);
        try { _ = Step(budgeted: true, tables: tables, within: null); } catch (Exception exception) {
            Fault(exception: exception);
            throw;
        }
    }
    /// <summary>Drives the work for a frame that is held rather than presented (one whose meshes must land or whose
    /// lighting must converge before it shows): answers and refills as <see cref="Pump"/> does, repeatedly for up to
    /// <see cref="DriveSlice"/>, waiting on the host between steps for a job in flight to complete, a client to offer its
    /// next job or a slot to come back (<see cref="Wake"/>). The GPU, upload and recording budgets protect the frame
    /// queued behind background work; a held frame queues none, so a drive takes jobs outside them and charges them
    /// nothing, and the work proceeds at the GPU's pace rather than a job a host iteration. Runs on the device's
    /// recording thread, and returns at once when no client is added or the tables' World set may not be bound.</summary>
    /// <param name="tables">The tables whose World set the jobs bind.</param>
    /// <exception cref="ObjectDisposedException">The service is disposed.</exception>
    public void Drive(SdfWorldTables tables) {
        Enter(driven: true, tables: tables);
        try {
            var deadline = WaitDeadline.In(budget: DriveSlice);

            m_drives++;
            while (true) {
                var stepped = Stopwatch.GetTimestamp();

                _ = Step(budgeted: false, tables: tables, within: null);

                var waiting = Stopwatch.GetTimestamp();

                m_driveStepTicks += (waiting - stepped);
                if ((m_clients.Count == 0) || !tables.BindsBackgroundWork) { return; }

                var flying = (m_inFlight > 0);
                // One wait for both: a job's fence and a client's offer set the same signal, and one set while the step
                // above ran is still set here.
                var outcome = HostWaits.Wait(deadline: deadline, handle: m_service!.Signal, reason: ServiceReason);

                if (flying) { m_driveFenceTicks += (Stopwatch.GetTimestamp() - waiting); } else { m_driveIdleTicks += (Stopwatch.GetTimestamp() - waiting); }
                if (outcome != WaitOutcome.Completed) { return; }
            }
        } catch (Exception exception) {
            Fault(exception: exception);
            throw;
        }
    }
    /// <summary>Tells the service that a client has a job to take, or gave a slot back: the recording thread, asleep in
    /// the host's wait or in a <see cref="Drive"/>, steps the work at once rather than at its next frame. Callable on
    /// any thread; a client that never calls it is served at the next pump.</summary>
    public void Wake() => Volatile.Read(location: ref m_service)?.Wake();
    /// <summary>Waits the fence of every job in flight that leased a ring slot, ending its lease, so the tables may
    /// rewrite the slot (<see cref="SdfWorldTables.SubmitUpload"/>). On the frame's own queue the upload has already
    /// waited a later submission, so this finds every such job complete and waits nothing.</summary>
    /// <param name="ringSlot">The ring slot about to be rewritten.</param>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    public void EndLeases(int ringSlot) {
        foreach (var slot in m_slots) {
            if ((slot.Job is null) || (slot.RingSlot != ringSlot) || !Reads(slot: slot)) { continue; }
            m_leaseWaits++;
            slot.Fence.Wait();
        }
    }

    // Whether a slot's job may still read what it leased: its fence has not signaled. An abandoned submission never
    // reached the queue, so it reads nothing; the step that polls the slot reports the abandonment.
    private static bool Reads(Slot slot) {
        try { return !slot.Fence.IsSignaled; } catch (GpuSubmissionAbandonedException) { return false; }
    }
    // Opens a pump or drive: refuses a disposed or failed service, rethrows what a step inside the host's wait caught,
    // registers the wait service on the stepping thread and remembers what such a step binds.
    private void Enter(SdfWorldTables tables, bool driven) {
        ArgumentNullException.ThrowIfNull(argument: tables);
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        if (m_failure is { } failure) {
            m_failure = null;
            failure.Throw();
        }
        if (m_faulted) { throw new InvalidOperationException(message: "A failed background GPU service must be disposed before it can be replaced."); }
        if (m_service is null) { Volatile.Write(location: ref m_service, value: HostWaits.Serve(reason: in ServiceReason, service: Service)); }
        m_tables = tables;
        m_driven = driven;
    }
    private void Fault(Exception exception) {
        m_faulted = true;
        if (exception is DeviceLostException) { ReportInFlight(); }
    }
    // The step the recording thread takes inside the host's wait between frames (HostWaits.Pace): as a pump's, against
    // the tables the latest pump named, taking no job it could not record before the wait's deadline. It returns how
    // long until the budget next holds credit when that is what held a free slot's job, so the wait comes back then.
    // A failure is kept for the next pump rather than thrown into the host's wait.
    private TimeSpan? Service(TimeSpan remaining) {
        if (m_disposed || m_faulted || (m_tables is not { } tables) || (m_clients.Count == 0)) { return null; }
        try {
            var submitted = m_submitted;
            var stop = Step(budgeted: !m_driven, tables: tables, within: remaining);

            m_services++;
            m_serviceJobs += (m_submitted - submitted);

            return stop switch {
                Stop.Budget => m_budget.UntilHolds,
                Stop.Host => TimeSpan.FromSeconds(value: (HostBudget.TotalSeconds / HostShare)),
                _ => null,
            };
        } catch (Exception exception) {
            Fault(exception: exception);
            m_failure = ExceptionDispatchInfo.Capture(source: exception);

            return null;
        }
    }
    private Stop Step(SdfWorldTables tables, bool budgeted, TimeSpan? within) {
        m_pumps++;
        Poll();
        if (m_clients.Count == 0) { return Stop.None; }

        var stop = Stop.None;

        if (tables.BindsBackgroundWork) { stop = Refill(budgeted: budgeted, tables: tables, within: within); } else { m_unbound++; }
        ReleaseIdle();
        return stop;
    }
    // A lost device names the background jobs still in flight, which DRED's trail shows only as their kernels' operations.
    private void ReportInFlight() {
        for (var index = 0; (index < SlotCount); index++) {
            if ((m_slots[index].Job is { } job) && (m_slots[index].Client is { } client)) {
                Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[sdf.gpu-work] lost in flight: {m_name} slot={index} {client.Category} {client.Describe(job: job)} estimate={(m_slots[index].Estimate * 1000.0):0.0}ms"));
            }
        }
    }

    /// <summary>Describes the work done since the first job: jobs submitted and answered, their measured GPU time, the
    /// wall time some job was in flight and the share of it the GPU spent on them, the most in flight, the steps (the
    /// pumps, a drive's and the host wait's among them), the steps taken inside the host's wait and the jobs they
    /// submitted, the drives and the wall time they spent waiting with a job in flight, waiting for a client and
    /// stepping, the steps the World set could not be bound in, those the budget held, the jobs handed back for want of
    /// device room and the waits a lease made the tables take.</summary>
    /// <returns>One space-led field.</returns>
    public string Describe() {
        var busy = ((m_busyTicks + ((m_inFlight > 0) ? (Stopwatch.GetTimestamp() - m_busySince) : 0L)) / ((double)Stopwatch.Frequency));

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $" gpu-work=[jobs:{m_submitted},answered:{m_answered},gpu:{(m_gpuSeconds * 1000.0):0}ms,busy:{busy:0.0}s,occupancy:{((busy > 0.0) ? (m_gpuSeconds / busy) : 0.0):0.00},most-in-flight:{m_mostInFlight},pumps:{m_pumps},services:{m_services},service-jobs:{m_serviceJobs},drives:{m_drives},drive-fence:{Seconds(ticks: m_driveFenceTicks):0.0}s,drive-idle:{Seconds(ticks: m_driveIdleTicks):0.0}s,drive-step:{Seconds(ticks: m_driveStepTicks):0.0}s,unbound:{m_unbound},budget-holds:{m_budgetHolds},deferred:{m_deferrals},lease-waits:{m_leaseWaits},over-frame-share:{m_overFrameShare},timestamps:{((m_timestamps is not null) ? "measured" : "estimated")}] most-job=[{(m_mostJobSeconds * 1000.0):0.00}ms {m_mostJob}]");
    }

    private static double Seconds(long ticks) => (ticks / ((double)Stopwatch.Frequency));

    /// <summary>Describes the buffers the work holds for <c>world.residency</c>: its slots' buffers, readbacks among
    /// them, by the memory they live in and as its reservation stands, and its clients' own tables and caches.</summary>
    /// <returns>One line.</returns>
    public string DescribeBytes() {
        var device = 0UL;
        var host = 0UL;
        var readbacks = 0UL;

        foreach (var slot in m_slots) {
            readbacks += slot.Times.SizeBytes;
            foreach (var held in slot.Held) {
                if (held.Readback) { readbacks += held.Buffer.SizeBytes; } else if (held.HostVisible) { host += held.Buffer.SizeBytes; } else { device += held.Buffer.SizeBytes; }
            }
        }

        var clients = default(GpuMemoryBytes);

        foreach (var client in m_clients) { clients += client.OwnedBytes; }
        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"gpu-work slots={SlotCount} in-flight={m_inFlight} readback-bytes={readbacks} slot-device-bytes={device} slot-host-bytes={host} reserved-device-bytes={(m_hold?.DeviceBytes ?? 0L)} reserved-host-bytes={(m_hold?.HostBytes ?? 0L)} client-device-bytes={clients.DeviceLocal} client-host-bytes={clients.HostVisible}");
    }
    /// <inheritdoc/>
    /// <remarks>Waits every job in flight, a host wait on its fence, then hands it back to its client unanswered; waits,
    /// on the host's monitor, for every client still reading a readback to release it; and disposes the clients, the
    /// slots and what they hold, and the reservation.</remarks>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        Volatile.Read(location: ref m_service)?.Dispose();
        // A client can retain one completed output until a dependent copy finishes. Wake a coordinator waiting to
        // submit that copy before waiting for its retained token; device resources stay alive until the drain below.
        foreach (var client in m_clients) { client.Stop(); }
        // A failed submit leaves a fence that answers for itself (GpuBatchedSubmissionFence): abandoned when nothing reached
        // the queue, proven once a refused signal's drain ran, or faulted with the drain's failure when even that could not
        // be queued. So each wait below is a proof or reports why none exists.
        foreach (var slot in m_slots) {
            if (slot.Job is not { } job) { continue; }
            try { slot.Fence.Wait(); } catch (Exception failure) when ((failure is DeviceLostException or GpuSubmissionAbandonedException)) { }
            slot.Client!.Abandon(job: job);
            slot.Job = null;
            slot.Client = null;
        }
        m_inFlight = 0;
        foreach (var slot in m_slots) { slot.Lease?.WaitReleased(); }
        foreach (var client in m_clients) { client.Dispose(); }
        m_clients.Clear();
        foreach (var slot in m_slots) {
            foreach (var held in slot.Held) { Release(held: held); }
            slot.Held.Clear();
            slot.Times.Dispose();
            slot.Fence.Dispose();
            slot.Commands.Dispose();
        }
        m_timestamps?.Dispose();
        m_hold?.Dispose();
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

            if (m_timestamps is not null) {
                if (seconds > FrameShareSeconds) { m_overFrameShare++; }
                if (seconds > m_mostJobSeconds) {
                    m_mostJobSeconds = seconds;
                    m_mostJob = string.Create(provider: CultureInfo.InvariantCulture, handler: $"{client.Category} {client.Describe(job: job)} estimate={(slot.Estimate * 1000.0):0.00}ms");
                }
                if (seconds >= SlowJobSeconds) {
                    Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[sdf.gpu-work] slow {m_name} slot={index} {client.Category} {client.Describe(job: job)} gpu={(seconds * 1000.0):0.0}ms estimate={(slot.Estimate * 1000.0):0.0}ms"));
                }
            }
            var readback = new SdfGpuWorkReadback(buffer: slot.Readback, gpuSeconds: ((m_timestamps is null) ? null : seconds), length: ((int)slot.ReadbackBytes), owner: this, slot: index);

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
    // Gives back each buffer of a free slot that no job in the last window needed at its size: one no job used, and a
    // granular one whose largest use fits fewer granules, which the next job that needs it creates at its own size. A
    // free slot is the one Refill would reuse: no job in flight and its last readback released (Slot.IsFree), so no job
    // and no client reads what goes.
    private void ReleaseIdle() {
        var now = Stopwatch.GetTimestamp();
        var window = ((long)(IdleSeconds * Stopwatch.Frequency));

        foreach (var slot in m_slots) {
            if (!slot.IsFree || (slot.Held.Count == 0)) { continue; }
            for (var at = (slot.Held.Count - 1); (at >= 0); at--) {
                var held = slot.Held[at];

                if ((now - held.WindowStart) < window) { continue; }
                if ((held.WindowBytes == 0UL) || ((held.Sizing == SdfGpuWorkSizing.Granule) && (held.Buffer.SizeBytes > Granule(bytes: held.WindowBytes)))) {
                    Release(held: held);
                    slot.Held.RemoveAt(index: at);
                    continue;
                }
                held.WindowStart = now;
                held.WindowBytes = 0UL;
            }
        }
    }
    // Replaces a job's estimated charge with its measured GPU time, when the queue writes timestamps: the difference of
    // its two timestamps, which the budget holds to the wall time the job was in flight (a pair read end before start
    // wraps to nearly the counter's range).
    private double Settle(Slot slot) {
        var seconds = slot.Estimate;

        if (m_timestamps is { } timestamps) {
            Span<byte> times = stackalloc byte[(2 * sizeof(ulong))];

            slot.Times.Read(destination: times);

            var mask = ((timestamps.ValidBits >= 64) ? ulong.MaxValue : ((1UL << ((int)timestamps.ValidBits)) - 1UL));
            var ticks = (BitConverter.ToUInt64(value: times[sizeof(ulong)..]) - BitConverter.ToUInt64(value: times)) & mask;
            var measured = ((ticks * timestamps.NanosecondsPerTick) * 1e-9);
            var inFlight = Stopwatch.GetElapsedTime(startingTimestamp: slot.SubmittedAt).TotalSeconds;

            seconds = (slot.Charged
                ? m_budget.SettleGpu(estimate: slot.Estimate, inFlight: inFlight, measured: measured)
                : SdfGpuWorkBudget.MeasuredWithin(clamped: out _, estimate: slot.Estimate, inFlight: inFlight, measured: measured));
        }
        m_gpuSeconds += seconds;
        return seconds;
    }
    // Banks what the presentation's budget accrued since any service last asked, and returns the seconds since this
    // service's last step (zero at the first), which its recording time is a share of.
    private double Accrue() {
        var now = Stopwatch.GetTimestamp();
        var seconds = ((m_pumpedAt != 0) ? ((now - m_pumpedAt) / ((double)Stopwatch.Frequency)) : 0.0);

        m_budget.Accrue();
        m_pumpedAt = now;
        return seconds;
    }
    // Takes each free slot's next job, the clients in turn, while the budgets hold credit; a job may overdraw each once.
    // The recording time and the upload bytes are rates of the wall time between steps, so the work a second does not
    // fall with the frame rate. A refill outside the budgets (a held frame's) takes every free slot's job and charges
    // none. A refill inside the host's wait stops HostBudget short of the wait's deadline. It answers what stopped it
    // short of a free slot's job, if anything the passing of time lifts.
    private Stop Refill(SdfWorldTables tables, bool budgeted, TimeSpan? within) {
        var seconds = Accrue();
        var hostBudget = TimeSpan.FromSeconds(value: Math.Clamp(value: (seconds * HostShare), min: HostBudget.TotalSeconds, max: MostHostBudget.TotalSeconds));
        var began = Stopwatch.GetTimestamp();

        if (within is { } left) {
            // The frame's own pump follows the wait: a job that could not be recorded before the deadline is its.
            if (left <= HostBudget) { return Stop.None; }
            hostBudget = (budgeted ? TimeSpan.FromTicks(value: Math.Min(val1: hostBudget.Ticks, val2: (left - HostBudget).Ticks)) : (left - HostBudget));
        }
        for (var index = 0; (index < SlotCount); index++) {
            var slot = m_slots[index];

            if (!slot.IsFree) { continue; }
            // A job handed back for want of room is offered again once a slot has come back, whose buffers it may then
            // take, or after a while, since room can come from another holder.
            if ((m_deferredSince != 0L) && (BusySlots() >= m_deferredBusy) && (Stopwatch.GetElapsedTime(startingTimestamp: m_deferredAt) < DeferralRetry)) { return Stop.None; }
            if (budgeted && !m_budget.Holds) {
                // The next accrual banks the whole gap for a service that waited on the budget.
                m_budget.Want();
                m_budgetHolds++;
                return Stop.Budget;
            }
            if ((budgeted || (within is not null)) && (Stopwatch.GetElapsedTime(startingTimestamp: began) >= hostBudget)) {
                if (budgeted) { m_budgetHolds++; }
                return Stop.Host;
            }

            var allowance = (budgeted ? m_budget.UploadAllowance : MostUploadJobBytes);

            if (!TryTake(client: out var client, estimate: out var estimate, job: out var job, tables: tables, uploadBytes: allowance)) { return Stop.None; }

            var uploadBytes = (budgeted ? client.UploadBytes(job: job) : 0);

            if (!Submit(charged: budgeted, client: client, estimate: estimate, index: index, job: job, slot: slot, tables: tables)) { return Stop.None; }
            if (budgeted) { m_budget.ChargeUpload(bytes: uploadBytes); }
        }
        return Stop.None;
    }
    private bool TryTake(SdfWorldTables tables, int uploadBytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out ISdfGpuWorkClient? client, out double estimate, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job) {
        for (var turn = 0; (turn < m_clients.Count); turn++) {
            var candidate = m_clients[((m_nextClient + turn) % m_clients.Count)];

            if (candidate.TryTake(estimatedSeconds: out estimate, job: out job, tables: tables, uploadBytes: uploadBytes)) {
                m_nextClient = (((m_nextClient + turn) + 1) % m_clients.Count);
                client = candidate;
                return true;
            }
        }
        client = null;
        estimate = 0.0;
        job = null;
        return false;
    }
    // Records a job into its slot between its timestamps and submits it armed with the slot's fence, which is asked to
    // wake the host's wait when the job completes. The one door a job reaches the queue through: it stamps the job's
    // lease before the job binds anything. False when the device had no room for what the job holds: the job is handed
    // back to its client, and nothing reached the queue.
    private bool Submit(ISdfGpuWorkClient client, object job, double estimate, int index, Slot slot, bool charged, SdfWorldTables tables) {
        var recorder = m_gpu.Recorder;
        var commandBuffer = slot.Commands.CommandBufferHandle;
        var first = ((uint)(2 * index));

        slot.ReadbackBytes = 0;
        slot.Readback = null;
        slot.Copied = false;
        slot.Generation = tables.BackgroundGeneration;
        slot.RingSlot = tables.CurrentSlot;
        try {
            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer);
            recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, label: $"{m_name} gpu-work");
            m_timestamps?.Reset(command: commandBuffer, count: 2, first: first);
            m_timestamps?.Write(command: commandBuffer, index: first);
            try {
                client.Record(job: job, recording: new SdfGpuWorkRecording(client: client, commandBuffer: commandBuffer, owner: this, recorder: recorder, slot: index));
            } catch (Exception refusal) when ((refusal is SdfGpuWorkDeferredException or GpuMemoryRefusedException)) {
                // Closed and never submitted: the slot's next job begins the command buffer again.
                recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
                recorder.EndCommandBuffer(commandBufferHandle: commandBuffer);
                client.Abandon(job: job);
                m_deferrals++;
                m_deferredAt = Stopwatch.GetTimestamp();
                m_deferredBusy = BusySlots();
                if (m_deferredSince == 0L) { m_deferredSince = m_deferredAt; }
                return false;
            }
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
        // Retain the taken job even if submission throws, so disposal waits its fence and abandons it.
        slot.Job = job;
        slot.Client = client;
        using (GpuQueueWork.Attribute(category: client.Category)) {
            m_gpu.QueueSubmitter.Submit(commandBufferHandles: [commandBuffer], fence: slot.Fence);
        }
        slot.Fence.SignalOnCompletion(signal: m_service!.Signal);
        m_deferredSince = 0L;
        slot.SubmittedAt = Stopwatch.GetTimestamp();
        slot.Estimate = estimate;
        slot.Charged = charged;
        if (charged) { m_budget.ChargeGpu(seconds: estimate); }
        m_submitted++;
        if (m_inFlight++ == 0) { m_busySince = slot.SubmittedAt; }
        m_mostInFlight = Math.Max(val1: m_mostInFlight, val2: m_inFlight);
        return true;
    }

    // Records a job's copy into its slot's readback, which the slot's door sizes, when it is short, before the copy.
    internal void CopyToReadback(int slot, nint commandBuffer, IGpuBuffer source, ulong bytes) {
        var target = m_slots[slot];

        if (target.Copied) { throw new InvalidOperationException(message: "A background GPU job copies back once."); }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: bytes, other: source.SizeBytes);
        if (bytes > int.MaxValue) { throw new ArgumentOutOfRangeException(paramName: nameof(bytes), message: "A background GPU job copies back at most 2 GiB."); }

        var readback = ((IGpuReadbackBuffer)Hold(bytes: bytes, client: null, hostVisible: true, part: ReadbackPart, readback: true, sizing: SdfGpuWorkSizing.Granule, slot: slot, usage: default));
        var recorder = m_gpu.Recorder;

        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: source.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite | GpuAccess.CopyWrite,
            sourceStageMask: GpuStage.ComputeShader | GpuStage.VertexShader | GpuStage.FragmentShader | GpuStage.Transfer,
            destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
        recorder.CopyBuffer(commandBufferHandle: commandBuffer, destinationBufferHandle: readback.BufferHandle, sizeBytes: bytes, sourceBufferHandle: source.BufferHandle);
        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: readback.BufferHandle,
            sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
        target.Readback = readback;
        target.ReadbackBytes = bytes;
        target.Copied = true;
    }
    // The one door a slot's buffer is created through: returns the buffer the slot holds for a client's part when it
    // still fits the job, and otherwise replaces it, reserving the growth before the device is asked and giving the
    // old buffer's bytes back first, so the slot never holds both. A refusal leaves the slot without the part, which
    // its next job creates.
    internal IGpuBuffer Hold(int slot, ISdfGpuWorkClient? client, string part, ulong bytes, bool hostVisible, bool readback, GpuBufferUsage usage, SdfGpuWorkSizing sizing) {
        var held = m_slots[slot].Held;
        var now = Stopwatch.GetTimestamp();
        var at = -1;

        for (var index = 0; (index < held.Count); index++) {
            if (ReferenceEquals(objA: held[index].Client, objB: client) && (held[index].Part == part)) { at = index; break; }
        }
        if ((at >= 0) && ((sizing == SdfGpuWorkSizing.Exact) ? (held[at].Buffer.SizeBytes == bytes) : (held[at].Buffer.SizeBytes >= bytes))) {
            held[at].WindowBytes = Math.Max(val1: held[at].WindowBytes, val2: Math.Max(val1: bytes, val2: 1UL));
            return held[at].Buffer;
        }
        if (at >= 0) {
            Release(held: held[at]);
            held.RemoveAt(index: at);
        }

        var size = ((sizing == SdfGpuWorkSizing.Granule) ? Granule(bytes: bytes) : Math.Max(val1: bytes, val2: 1UL));

        if ((m_hold is { } hold) && !hold.TryRaise(bytes: ((long)size), forced: false, hostVisible: hostVisible)) {
            // With no room, what the other free slots still hold for jobs to come goes first. Past that a service
            // with nothing in flight and no slot a reader still holds reserves its job as its floor, so its work always
            // progresses; one whose reader is slow waits for the slot, but not past its patience. Any other hands the
            // job back.
            ReleaseFree(except: slot);

            var forced = ((m_inFlight == 0) && ((BusySlots() == 0) || ((m_deferredSince != 0L) && (Stopwatch.GetElapsedTime(startingTimestamp: m_deferredSince) >= ForcedPatience))));

            if (!hold.TryRaise(bytes: ((long)size), forced: forced, hostVisible: hostVisible)) {
                throw new SdfGpuWorkDeferredException(bytes: ((long)size), part: part);
            }
        }

        IGpuBuffer buffer;
        var name = new GpuObjectName(index: slot, owner: SdfResidencySelector.WorkOwner, part: ((client is null) ? part : $"{client.Category.ToString().ToLowerInvariant()} {part}"));

        try {
            buffer = (readback
                ? m_gpu.BufferFactory.CreateReadback(name: name, sizeBytes: size)
                : (hostVisible
                    ? m_gpu.BufferFactory.CreateHostVisible(name: name, sizeBytes: size, usage: usage)
                    : m_gpu.BufferFactory.CreateDeviceLocal(name: name, sizeBytes: size, usage: usage)));
        } catch (Exception failure) {
            m_hold?.Lower(bytes: ((long)size), hostVisible: hostVisible);
            if (failure is GpuMemoryRefusedException) { throw new SdfGpuWorkDeferredException(bytes: ((long)size), innerException: failure, part: part); }
            throw;
        }
        held.Add(item: new HeldBuffer(Buffer: buffer, Client: client, HostVisible: hostVisible, Part: part, Readback: readback, Sizing: sizing) { WindowBytes = Math.Max(val1: bytes, val2: 1UL), WindowStart = now });
        return buffer;
    }
    // The slots that are not free: a job in flight, or a readback its client has not released.
    private int BusySlots() {
        var busy = 0;

        foreach (var slot in m_slots) { busy += (slot.IsFree ? 0 : 1); }
        return busy;
    }
    // Gives back every buffer the free slots but one hold: no job in flight and no client reads them.
    private void ReleaseFree(int except) {
        for (var index = 0; (index < SlotCount); index++) {
            var slot = m_slots[index];

            if ((index == except) || !slot.IsFree) { continue; }
            foreach (var held in slot.Held) { Release(held: held); }
            slot.Held.Clear();
        }
    }
    private void Release(HeldBuffer held) {
        var bytes = ((long)held.Buffer.SizeBytes);

        held.Buffer.Dispose();
        m_hold?.Lower(bytes: bytes, hostVisible: held.HostVisible);
    }
    /// <summary>Returns the bytes a slot's buffer of at least <paramref name="bytes"/> bytes is allocated at: whole
    /// granules of 4 MiB, so a client whose jobs grow toward a bound replaces a slot's buffers a few times, never once a
    /// job, and holds at most a granule more than its largest job.</summary>
    /// <param name="bytes">The bytes the buffer holds at least.</param>
    /// <returns>The allocation's bytes.</returns>
    public static ulong Granule(ulong bytes) {
        const ulong Bytes = (4UL << 20);

        return (((Math.Max(val1: bytes, val2: 1UL) + (Bytes - 1UL)) / Bytes) * Bytes);
    }

    // What stopped a refill short of a free slot's job that waiting lifts: the budget's credit, or the step's share of
    // recording time.
    private enum Stop { None, Budget, Host }

    // One buffer a slot holds: for a client's part, or the slot's readback (no client), and the window its size is
    // judged over: when it began and the most bytes a job needed of the buffer since.
    private sealed record HeldBuffer(ISdfGpuWorkClient? Client, string Part, IGpuBuffer Buffer, bool HostVisible, bool Readback, SdfGpuWorkSizing Sizing) {
        public long WindowStart { get; set; }
        public ulong WindowBytes { get; set; }
    }
    // One slot: its command buffer, fence and timestamp readback, the job in it and its client, the buffers it holds,
    // the readback its job copied into and how many bytes, the GPU seconds the job was charged and when it was
    // submitted, and the tables' generation and ring slot the job leased.
    private sealed class Slot(IGpuCommandPool Commands, IGpuSubmissionFence Fence, IGpuReadbackBuffer Times) {
        public IGpuCommandPool Commands { get; } = Commands;
        public IGpuSubmissionFence Fence { get; } = Fence;
        public IGpuReadbackBuffer Times { get; } = Times;
        public List<HeldBuffer> Held { get; } = [];

        public SdfGpuWorkReadback? Lease { get; set; }
        public IGpuReadbackBuffer? Readback { get; set; }
        public ulong ReadbackBytes { get; set; }
        // Whether no job is in flight in the slot and its last readback was released: the one condition under which
        // Refill reuses the slot and ReleaseIdle releases its buffers.
        public bool IsFree => ((Job is null) && (Lease is null or { Released: true }));
        public ISdfGpuWorkClient? Client { get; set; }
        public bool Copied { get; set; }
        public double Estimate { get; set; }
        public object? Job { get; set; }
        public long SubmittedAt { get; set; }
        public ulong Generation { get; set; }
        public int RingSlot { get; set; }
        // Whether the job's estimate was charged to the GPU budget, which its measured time then settles.
        public bool Charged { get; set; }
    }
}
