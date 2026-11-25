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
    /// <summary>Gets the delay until a clock alone can change a declined offer. A client with no clock deadline
    /// returns null. Calls on the recording thread follow the most recent take. Other changes signal
    /// <see cref="SdfGpuWork.Wake"/> or arrive in a table upload.</summary>
    TimeSpan? UntilDue => null;
    /// <summary>Takes the client's next job, or none: a job taken is recorded, submitted and answered, or abandoned.</summary>
    /// <param name="tables">The tables whose World set the job binds, holding the latest submitted upload.</param>
    /// <param name="uploadBytes">The most host payload bytes the job may stage (<see cref="UploadBytes"/>), from
    /// <see cref="SdfGpuWork.UploadChunkBytes"/> to <see cref="SdfGpuWork.MostUploadJobBytes"/>: what the service's upload
    /// budget holds now.</param>
    /// <param name="allowance">What the job may take: for background work, what is left of one frame's allowance
    /// (<see cref="SdfGpuWorkBudget.FrameAllowanceSeconds"/>) after the jobs already queued ahead of the next frame, since
    /// the queue runs every one of them whole before that frame; for a held frame's own work, which queues no frame
    /// behind it, no bound (<see cref="SdfGpuWorkAllowance.Unbounded"/>). A client sizes the job to it at its kind's
    /// measured rate (<see cref="SdfGpuWorkAllowance.UnitsWithin"/>), within its own most, and while work is queued takes
    /// no job priced past it (<see cref="SdfGpuWorkAllowance.Admits"/>), deciding before it takes anything.</param>
    /// <param name="job">The job, handed back to <see cref="Record"/> and then to <see cref="Complete"/> or
    /// <see cref="Abandon"/>.</param>
    /// <param name="estimatedSeconds">The GPU time the job is estimated to take, in seconds, which the service charges
    /// its budget until the job's timestamps measure it. When the client hands over no job because its next one is priced
    /// past what is left while work is queued, that job's price, so the service holds the room for it; otherwise
    /// zero.</param>
    /// <returns><see langword="true"/> when the client hands over a job.</returns>
    bool TryTake(SdfWorldTables tables, int uploadBytes, SdfGpuWorkAllowance allowance, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds);
    /// <summary>Takes the client's next job the frame owes (an arrival's work: the meshes a boot, a portal's world or an
    /// edit has not drawn yet, the landing of a mesh a staged draw waits for, and the first light a shown view reads from
    /// the camera sample's stand-in, <see cref="SdfMeshIrradiance.OwesFirstLight"/>), or none, as <see cref="TryTake"/>
    /// takes a job: only such work, sized and admitted against
    /// <paramref name="allowance"/>, the budget's owed allowance (<see cref="SdfGpuWorkBudget.OwedAllowance"/>), and
    /// charged nothing of the background credit. The service asks it before the client's ordinary take on each budgeted
    /// step; a client with no such work takes none. By default a client takes as <see cref="TryTake"/> does while every
    /// job it has is owed (<see cref="OwesAll"/>), and none otherwise.</summary>
    /// <param name="tables">The tables whose World set the job binds, holding the latest submitted upload.</param>
    /// <param name="uploadBytes">The most host payload bytes the job may stage, as <see cref="TryTake"/> reads it.</param>
    /// <param name="allowance">What the job may take: what is left of the owed allowance after the jobs queued ahead of
    /// the next frame.</param>
    /// <param name="job">The job, handed back to <see cref="Record"/> and then to <see cref="Complete"/> or
    /// <see cref="Abandon"/>.</param>
    /// <param name="estimatedSeconds">The GPU time the job is estimated to take, in seconds; when no job is handed over
    /// because the next is priced past what is left while work is queued, that job's price; otherwise zero.</param>
    /// <returns><see langword="true"/> when the client hands over a job.</returns>
    bool TryTakeOwed(SdfWorldTables tables, int uploadBytes, SdfGpuWorkAllowance allowance, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds) {
        if (OwesAll) { return TryTake(allowance: allowance, estimatedSeconds: out estimatedSeconds, job: out job, tables: tables, uploadBytes: uploadBytes); }
        job = null;
        estimatedSeconds = 0.0;
        return false;
    }
    /// <summary>Gets whether every job the client has now is work the frame owes a hole, so its owed take
    /// (<see cref="TryTakeOwed"/>) takes them all: the landing while a staged draw waits for its mesh, the irradiance
    /// volume's jobs while it owes the views their first light (<see cref="SdfMeshIrradiance.OwesFirstLight"/>). False by
    /// default; a client owed only some of its work answers false and takes that work in its own owed take.</summary>
    bool OwesAll => false;
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
    public IGpuStorageBuffer HostBuffer(string part, ulong bytes, GpuBufferUsage usage, GpuBufferSizing sizing = GpuBufferSizing.Granule) =>
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
    public IGpuBuffer DeviceBuffer(string part, ulong bytes, GpuBufferUsage usage, GpuBufferSizing sizing = GpuBufferSizing.Granule) =>
        m_owner.Hold(bytes: bytes, client: m_client, hostVisible: false, part: part, readback: false, sizing: sizing, slot: m_slot, usage: usage);
    /// <summary>Records a copy of a buffer's first bytes, which a compute shader wrote, into the slot's readback, which
    /// <see cref="ISdfGpuWorkClient.Complete"/> answers with. A job copies at most once.</summary>
    /// <param name="source">The buffer, last written by a compute shader.</param>
    /// <param name="bytes">The bytes to copy, at most the buffer's size.</param>
    /// <exception cref="InvalidOperationException">The job already copied.</exception>
    /// <exception cref="SdfGpuWorkDeferredException">The device has no room for the readback; the service hands the job
    /// back.</exception>
    public void CopyToReadback(IGpuBuffer source, ulong bytes) => m_owner.CopyToReadback(bytes: bytes, commandBuffer: CommandBuffer, slot: m_slot, source: source);
    /// <summary>Reserves the slot's readback for a copy of <paramref name="bytes"/> that the job records later
    /// (<see cref="CopyToReadback"/>), so a job whose readback the device has no room for is handed back before it
    /// records anything that consumes state (<see cref="CopyRegion"/>).</summary>
    /// <param name="bytes">The bytes the job will copy back.</param>
    /// <exception cref="SdfGpuWorkDeferredException">The device has no room for the readback; the service hands the job
    /// back.</exception>
    public void ReserveReadback(ulong bytes) => _ = m_owner.ReserveReadback(bytes: bytes, slot: m_slot);
    /// <summary>Records the copy of what a staged region owes into the slot's command buffer: flushes the region's
    /// <paramref name="regionSlot"/>, records a grown region's carry and the copy, and hands the region's buffer to
    /// <paramref name="readers"/>, which read it in this or any later submission. A region that owes nothing records
    /// nothing.</summary>
    /// <param name="region">The region, whose owed words the job wrote or owed before this call.</param>
    /// <param name="regionSlot">The region's slot whose staging buffer the job stages into: one that no submission in
    /// flight still reads, which a slot of this service is once its job completed.</param>
    /// <param name="readers">The shader stages that read the region's buffer.</param>
    /// <remarks>The copy takes the region's owed words: once it is recorded the job can no longer be handed back
    /// without losing them, so the slot refuses any buffer it would still have to create for the job (an
    /// <see cref="InvalidOperationException"/> from <see cref="HostBuffer"/>, <see cref="DeviceBuffer"/> or
    /// <see cref="CopyToReadback"/>). A job asks for every buffer, and reserves its readback
    /// (<see cref="ReserveReadback"/>), before it copies a region.</remarks>
    public void CopyRegion(GpuRegion region, int regionSlot, GpuStage readers) {
        ArgumentNullException.ThrowIfNull(argument: region);
        // The flush stages the owed words, growing the slot's staging buffer, and a refusal there takes nothing the region
        // owes (it stays owed), so it comes before the copy consumes the job: a staging buffer the memory account has no
        // room for hands the job back (GpuMemoryRefusedException) instead of faulting the service.
        region.Flush(slot: regionSlot);
        m_owner.Consume(slot: m_slot);

        var command = CommandBuffer;
        var copies = new GpuRegionCopyRecording(recorder: Recorder, readers: readers, begin: () => command);

        copies.Record(flushed: true, handsToReaders: true, region: region, slot: regionSlot);
        _ = copies.Finish();
        m_owner.NoteCopied(slot: m_slot, region: region, regionSlot: regionSlot);
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
/// <para>Invariant: a job's resources retire only after the device timeline passes their last use. Admission
/// requires the tables' frame queue, so its checkpoint covers every background submission too. Pending copies remain
/// explicit holds in the tables' retirement door. Ring-slot rewrites independently wait the jobs reading that slot
/// (<see cref="EndLeases"/>); submission fences also wake the host and complete readbacks.</para>
/// <para>Invariant: what a job holds on the device is reserved before it is allocated. A slot's buffers, its readback
/// among them, come from one door (<see cref="SdfGpuWorkRecording.HostBuffer"/>,
/// <see cref="SdfGpuWorkRecording.DeviceBuffer"/>, <see cref="SdfGpuWorkRecording.CopyToReadback"/>), which raises the
/// service's reservation (<see cref="SdfResidencySelector.WorkOwner"/>, workspace) by the growth before it creates the
/// buffer and lowers it when the buffer goes. A job the pool has no room for is handed back, and its client is offered
/// a slot again once one comes back or <see cref="DeferralRetry"/> has passed, while the other clients' jobs go on. A
/// client with no slot of the service in use, neither a job in flight nor a readback it still holds, reserves its job
/// as the reservation's floor, as does one with no job in flight whose job has waited <see cref="ForcedPatience"/>, so
/// every client's work always progresses. It stops at the owner (every service shares one account entry, so another
/// service's idle buffers stand against a floor until they go) and at the clients' own tables and caches, which are
/// not a job's.</para>
/// <para>Sizing: a slot's buffer grows to the largest job that used it and goes once no job in the last
/// <see cref="IdleSeconds"/> used it, so buffers sized for a cold bake's largest batch go once the bake settles rather
/// than living as long as the tables.</para>
/// <para>Budget: the service draws on its presentation's one budget (<see cref="SdfGpuWorkBudget"/>), which every
/// residency's service shares: together they take the share of the GPU's time the frame's own work leaves free
/// (<see cref="SdfGpuWorkBudget.Share"/>, at most <see cref="GpuShare"/>), by each job's measured GPU time (its
/// timestamps, or its client's estimate on a queue without them), and stage at most
/// <see cref="UploadBytesPerSecond"/> of host payload. Each service also records for at most <see cref="HostShare"/> of
/// the wall time between its steps and keeps at most <see cref="SlotCount"/> jobs in flight. Every budget is a rate of
/// wall time, never a count a pump, so work such as landing a mesh region takes as many seconds whatever the frame
/// rate. The services divide the budget by what the player sees of their residencies, not by the order they are pumped:
/// each budgeted step asks its turn first (<see cref="SdfGpuWorkBudget.Takes"/>), weighed by its residency's display
/// share (<see cref="Weigh"/>), and a step the turn holds counts as <c>turn-holds</c>.</para>
/// <para>Invariant: the budgeted jobs queued ahead of one frame together fit one frame's allowance. The service's one
/// take hands every client what its job may take (<see cref="ISdfGpuWorkClient.TryTake"/>'s <c>allowance</c>): the
/// budget's live <see cref="SdfGpuWorkBudget.AllowanceOf"/>, the frame allowance less the priced length of every charged
/// job still in flight on the presentation's queue, whichever service submitted it, since the queue runs each of them
/// whole before the frame submitted after it and the jobs ahead of a frame delay it by their sum whatever the average
/// share; and no bound for a held frame's own work. With the allowance spent, or a client's next job priced past what is
/// left, the service takes no job until one completes (<see cref="Describe"/>'s <c>deadline-holds</c>), and the waiting
/// client keeps its turn so no other client of the service takes the room it waits for. A client that cuts jobs ahead of
/// its take (the probe coordinator) reads the whole frame allowance (<see cref="FrameAllowanceSeconds"/>), so its jobs
/// wait for a queue with room for them. The turn is the service's own: another residency's service on the same budget
/// is not held for it. It stops at a client's sizing: a job whose cost its rate has not measured yet, or one unit costlier than
/// the allowance (a bake lattice is never split) taken with nothing queued, runs long, and the service counts it
/// (<c>over-allowance</c>), as it counts a job a client handed over past what was left while work was queued
/// (<c>over-deadline</c>, a client defect). Whether the frames behind the work kept their period is counted on the
/// budget (<see cref="SdfGpuWorkBudget.FramesMissedBehind"/>), from each job's span on the queue's clock.</para>
/// <para>Work the frame owes (<see cref="ISdfGpuWorkClient.TryTakeOwed"/>) is offered first on each budgeted step,
/// under the budget's owed allowance (<see cref="SdfGpuWorkBudget.OwedAllowance"/>) in place of the frame allowance and
/// outside the credit; it enters the same queue, so the ordinary jobs behind it still fit the frame allowance less
/// everything queued. Only the client's owed take hands such a job over, and the service charges it owed
/// (<see cref="SdfGpuWorkBudget.ChargeGpu"/>) at the one submit door.</para>
/// </summary>
public sealed class SdfGpuWork : IDisposable {
    internal IGpuQueueSubmitter Queue => m_gpu.QueueSubmitter;

    /// <summary>The jobs in flight at most.</summary>
    public const int SlotCount = 4;
    /// <summary>The share of the GPU's time background work takes at most, measured over each job's GPU time: the cap
    /// the budget's live share (<see cref="SdfGpuWorkBudget.Share"/>) follows the frame's free time up to.</summary>
    public const double GpuShare = 0.25;
    /// <summary>The seconds a free slot keeps a buffer no job in that span used.</summary>
    public const double IdleSeconds = 2.0;
    /// <summary>The fewest host payload bytes an upload job is allowed to stage, which the budget may overdraw once: one
    /// MiB.</summary>
    public const int UploadChunkBytes = (1 << 20);
    /// <summary>The most host payload bytes one upload job stages: eight MiB, the block of its region's staging ring the
    /// job holds until its fence signals (<see cref="GpuRegion.Retire"/>).</summary>
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

    /// <summary>The wall time after which a client whose job was handed back for want of device room is offered a slot
    /// again although none has come back since: a quarter of a second, so room another holder gave up is found.</summary>
    public static readonly TimeSpan DeferralRetry = TimeSpan.FromSeconds(value: 0.25);
    /// <summary>The wall time a client with no job in flight lets its job wait for a slot a reader of its own still
    /// holds before the job is reserved as the floor whatever the pool holds: one second.</summary>
    public static readonly TimeSpan ForcedPatience = TimeSpan.FromSeconds(value: 1.0);

    private static readonly WaitReason ServiceReason = new(Subsystem: "sdf.gpu-work", WaitsFor: "a background job's completion or its next job");
    private const string ReadbackPart = "readback";

    private readonly GpuDeviceServices m_gpu;
    private readonly SdfGpuWorkBudget m_budget;
    // The clock every wait, accrual and age below reads, the budget's own (Drive's waits on fences are real time).
    private readonly TimeProvider m_time;
    // The service's turn on the budget, which orders its budgeted takes against every other service's (Takes).
    private readonly SdfGpuWorkTurn m_turn;
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
    // The client whose job is being recorded, for which the slot door reserves what it creates.
    private ISdfGpuWorkClient? m_recording;
    private bool m_disposed;
    private bool m_faulted;
    // The clients whose job was handed back for want of room and has not been submitted since.
    private readonly List<Deferral> m_waiting = [];
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
    // Since the first job: the budgeted jobs measured past the allowance they were taken to fit, the steps the frame's
    // deadline held (the allowance spent on jobs in flight), the jobs a client handed over past what was left while work
    // was queued, and the longest measured job with what it was.
    private long m_overAllowance;
    private long m_deadlineHolds;
    private long m_overDeadline;
    // The steps the service's turn held: other services owed their share of the budget first (SdfGpuWorkBudget.Takes).
    private long m_turnHolds;
    // The jobs taken as work the frame owes, under the budget's owed allowance.
    private long m_owedJobs;
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
        m_time = (selector?.Time ?? device.Time);
        m_budget = (selector?.Work ?? new SdfGpuWorkBudget(time: m_time));
        m_turn = m_budget.Join(name: name);

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
    /// <summary>Gets the GPU time, in seconds, one budgeted job may take now: the presentation budget's live frame
    /// allowance (<see cref="SdfGpuWorkBudget.FrameAllowanceSeconds"/>), which a client that cuts its jobs ahead of their
    /// take sizes them to. Safe to read from any thread.</summary>
    public double FrameAllowanceSeconds => m_budget.FrameAllowanceSeconds;
    /// <summary>Gets the GPU time, in seconds, the work a shown view owes may queue ahead of one frame now
    /// (<see cref="SdfGpuWorkBudget.OwedFrameSeconds"/>), which a client that cuts such jobs ahead of their take sizes them
    /// to. Safe to read from any thread.</summary>
    public double OwedAllowanceSeconds => m_budget.OwedFrameSeconds;

    /// <summary>Weighs the service's draw on the presentation's budget by its residency's share of the display's pixels
    /// (<see cref="SdfGpuWorkBudget.Weigh"/>): two services that both have work divide the budget by their weights.</summary>
    /// <param name="share">The share, in [0, 1]; zero for a residency no view shows, which takes the least weight.</param>
    public void Weigh(double share) => m_budget.Weigh(share: share, turn: m_turn);

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
    /// <param name="withheld">Whether the residency withholds its frame until this work lands (its meshes or its
    /// lighting): the work is then the held frame's own, which the budgets do not meter, here or in the host's wait
    /// until the next pump. <see langword="false"/> meters it as background work.</param>
    /// <exception cref="ObjectDisposedException">The service is disposed.</exception>
    public void Pump(SdfWorldTables tables, bool withheld = false) {
        Enter(driven: withheld, tables: tables);
        if (!withheld && Quiescent(tables: tables)) {
            m_pumpsSpared++;
            ReleaseIdle();
            return;
        }
        SdfCaptureWork.ProcessWork.Count(kind: SdfCaptureWork.Pumped);
        try { _ = Step(tables: tables, within: null); } catch (Exception exception) {
            Fault(exception: exception);
            throw;
        }
    }
    /// <summary>Drives the work for a frame that is held rather than presented (one whose meshes must land or whose
    /// lighting must converge before it shows): answers and refills as <see cref="Pump"/> does, repeatedly until
    /// <see cref="DriveSlice"/> has passed, waiting on the host between steps for a job in flight to complete, a client
    /// to offer its next job or a slot to come back (<see cref="Wake"/>). No step starts after the slice, and each
    /// records only within what is left of it. Before each wait the jobs it submitted reach the queue
    /// (<see cref="IGpuSubmissionFence.HandOff"/>): a drive inside the frame's open batch would otherwise sleep on
    /// completions that cannot come until the batch's flush. The GPU, upload and recording budgets protect the frame
    /// queued behind background work; a held frame queues none, so a drive takes jobs outside them and charges them
    /// nothing, and the work proceeds at the GPU's pace rather than a job a host iteration. Runs on the device's
    /// recording thread, and returns at once when no client is added or the tables' World set may not be bound.</summary>
    /// <param name="tables">The tables whose World set the jobs bind.</param>
    /// <exception cref="ObjectDisposedException">The service is disposed.</exception>
    public void Drive(SdfWorldTables tables) {
        Enter(driven: true, tables: tables);
        try {
            var started = m_time.GetTimestamp();

            m_drives++;
            while (true) {
                var stepped = Stopwatch.GetTimestamp();

                var remaining = DriveSlice - m_time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) { return; }
                _ = Step(tables: tables, within: remaining);

                var waiting = Stopwatch.GetTimestamp();

                m_driveStepTicks += (waiting - stepped);
                remaining = DriveSlice - m_time.GetElapsedTime(started);
                if ((m_clients.Count == 0) || !tables.BindsBackgroundWork || (remaining <= TimeSpan.Zero)) { return; }

                var due = UntilDue();
                if (due is { } elapsed && elapsed <= TimeSpan.Zero) { continue; }
                var dueFirst = due is { } next && next < remaining;
                var deadline = WaitDeadline.In(budget: dueFirst ? due!.Value : remaining);
                var flying = HandOff();
                // One wait for both: a job's fence and a client's offer set the same signal, and one set while the step
                // above ran is still set here.
                var outcome = HostWaits.Wait(deadline: deadline, handle: m_service!.Signal, reason: ServiceReason);

                if (flying) { m_driveFenceTicks += (Stopwatch.GetTimestamp() - waiting); } else { m_driveIdleTicks += (Stopwatch.GetTimestamp() - waiting); }
                if (outcome != WaitOutcome.Completed && (!dueFirst || UntilDue() > TimeSpan.Zero)) { return; }
            }
        } catch (Exception exception) {
            Fault(exception: exception);
            throw;
        }
    }
    // Hands every job in flight still deferred in an open batch to the queue, so its completion can wake the wait that
    // follows; answers whether any job is in flight. The first hand-off splits the whole batch, in order.
    private bool HandOff() {
        foreach (var slot in m_slots) {
            if (slot.Job is not null) { slot.Fence.HandOff(); }
        }
        return (m_inFlight > 0);
    }
    /// <summary>Tells the service that a client has a job to take, or gave a slot back: the recording thread, asleep in
    /// the host's wait or in a <see cref="Drive"/>, steps the work at once rather than at its next frame. Callable on
    /// any thread; a client that never calls it is served at the next pump.</summary>
    public void Wake() {
        Volatile.Write(location: ref m_woken, value: 1);
        Volatile.Read(location: ref m_service)?.Wake();
    }

    // Quiescence: a service whose last step ended with nothing to take (every client declined, no deferral pending, or every
    // slot busy) has nothing to do until one of its own causes moves, so a pump or a wait's step of it does nothing else.
    // Invariant: every cause that can give a quiescent service work ends its quiescence before the next step reads it. The
    // causes and their doors: a job's completion (a slot's fence, read by Quiescent itself), a client's offer or a slot
    // given back (Wake, which every client with work outside a frame calls, and SdfGpuWorkReadback.Revoke), the tables'
    // next upload (a new frame, a new program or a newly bindable World set: SdfWorldTables.Uploads), and a held frame
    // (a pump that is driven never rests). A clock-only cause sets the next wait through UntilDue.
    private int m_woken;
    private bool m_quiescent;
    private SdfWorldTables? m_quiescentTables;
    private ulong m_quiescentUpload;
    private long m_quiescentAt;
    private TimeSpan? m_dueDelay;
    private long m_pumpsSpared;

    private TimeSpan? UntilDue() => m_dueDelay - m_time.GetElapsedTime(m_quiescentAt);

    // Whether the service has nothing to do: its last step rested, and no cause has moved since (see above).
    private bool Quiescent(SdfWorldTables tables) {
        if (!m_quiescent || (Volatile.Read(location: ref m_woken) != 0) || !ReferenceEquals(objA: tables, objB: m_quiescentTables) ||
            (tables.Uploads != m_quiescentUpload) || (UntilDue() <= TimeSpan.Zero)) {
            return false;
        }
        foreach (var slot in m_slots) {
            if ((slot.Job is not null) && !Reads(slot: slot)) { return false; }
        }
        return true;
    }
    // Marks the step that just ended as resting, or not.
    private void Rest(SdfWorldTables tables, bool resting) {
        m_quiescent = resting && !m_driven;
        m_dueDelay = null;
        if (!resting) { return; }
        m_quiescentTables = tables;
        m_quiescentUpload = tables.Uploads;
        m_quiescentAt = m_time.GetTimestamp();
        if (BusySlots() == SlotCount) { return; }
        foreach (var client in m_clients) {
            if (client.UntilDue is { } due && (m_dueDelay is not { } held || due < held)) { m_dueDelay = due; }
        }
        foreach (var waiting in m_waiting) {
            var due = DeferralRetry - m_time.GetElapsedTime(waiting.At, m_quiescentAt);

            if (m_dueDelay is not { } held || due < held) { m_dueDelay = due; }
        }
    }
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
        if (!ReferenceEquals(Queue, tables.BackgroundQueue)) {
            throw new InvalidOperationException("Background work must submit on the tables' device queue.");
        }
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
        // The thread's one wake serves every service on it; only one whose own cause moved steps.
        if (!m_driven && Quiescent(tables: tables)) { return UntilDue(); }
        try {
            var submitted = m_submitted;
            var stop = Step(tables: tables, within: remaining);

            m_services++;
            m_serviceJobs += (m_submitted - submitted);

            return stop switch {
                Stop.Budget => m_budget.UntilHolds,
                Stop.Host => TimeSpan.FromSeconds(value: (HostBudget.TotalSeconds / HostShare)),
                // The queued jobs may be another service's, whose fences wake only their own: come back once they should
                // have run.
                Stop.Deadline => TimeSpan.FromSeconds(value: m_budget.DeadlineRetrySeconds),
                // The services ahead of this one charge as their own jobs complete: come back after one frame allowance.
                Stop.Turn => TimeSpan.FromSeconds(value: Math.Max(val1: m_budget.FrameAllowanceSeconds, val2: HostBudget.TotalSeconds)),
                _ => UntilDue(),
            };
        } catch (Exception exception) {
            Fault(exception: exception);
            m_failure = ExceptionDispatchInfo.Capture(source: exception);

            return null;
        }
    }
    // One step at a pump, a drive or the host's wait. Whether the budgets meter it is decided here alone, from whether the
    // latest pump or drive named a withheld frame (m_driven): a held frame's own work is never metered, and background
    // work always is.
    private Stop Step(SdfWorldTables tables, TimeSpan? within) {
        m_pumps++;
        Volatile.Write(location: ref m_woken, value: 0);
        Poll();
        if (m_clients.Count == 0) {
            Rest(resting: true, tables: tables);
            return Stop.None;
        }

        var stop = Stop.None;

        m_refillRested = false;
        if (tables.BindsBackgroundWork) { stop = Refill(budgeted: !m_driven, tables: tables, within: within); } else { m_unbound++; }
        Rest(resting: ((stop == Stop.None) && m_refillRested), tables: tables);
        ReleaseIdle();
        return stop;
    }
    // Whether the latest refill ended with nothing it could take: every client declined with no deferral pending, or
    // every slot busy.
    private bool m_refillRested;
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
    /// stepping, the steps the World set could not be bound in, those the budget held, those the frame's deadline held,
    /// those the service's turn held (<see cref="SdfGpuWorkBudget.Takes"/>), the jobs taken as work the frame owes
    /// (<see cref="ISdfGpuWorkClient.TryTakeOwed"/>), the jobs handed back
    /// for want of device room, the waits a lease made the tables take, the budgeted jobs measured past the allowance
    /// they were taken to fit and the jobs a client handed over past what was left.</summary>
    /// <returns>One space-led field.</returns>
    public string Describe() {
        var busy = ((m_busyTicks + ((m_inFlight > 0) ? (m_time.GetTimestamp() - m_busySince) : 0L)) / ((double)m_time.TimestampFrequency));

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $" gpu-work=[jobs:{m_submitted},answered:{m_answered},gpu:{(m_gpuSeconds * 1000.0):0}ms,busy:{busy:0.0}s,occupancy:{((busy > 0.0) ? (m_gpuSeconds / busy) : 0.0):0.00},most-in-flight:{m_mostInFlight},pumps:{m_pumps},pumps-spared:{m_pumpsSpared},services:{m_services},service-jobs:{m_serviceJobs},drives:{m_drives},drive-fence:{Seconds(ticks: m_driveFenceTicks):0.0}s,drive-idle:{Seconds(ticks: m_driveIdleTicks):0.0}s,drive-step:{Seconds(ticks: m_driveStepTicks):0.0}s,unbound:{m_unbound},budget-holds:{m_budgetHolds},deadline-holds:{m_deadlineHolds},turn-holds:{m_turnHolds},owed-jobs:{m_owedJobs},deferred:{m_deferrals},lease-waits:{m_leaseWaits},over-allowance:{m_overAllowance},over-deadline:{m_overDeadline},timestamps:{((m_timestamps is not null) ? "measured" : "estimated")}] most-job=[{(m_mostJobSeconds * 1000.0):0.00}ms {m_mostJob}]");
    }

    private static double Seconds(long ticks) => (ticks / ((double)Stopwatch.Frequency));

    /// <summary>Gets the bytes the work's slots' buffers and its clients' own tables and caches hold, by the memory they
    /// live in; the slots' readbacks count as host-visible. Read on the frame thread.</summary>
    public GpuMemoryBytes OwnedBytes {
        get {
            var bytes = default(GpuMemoryBytes);

            foreach (var slot in m_slots) {
                foreach (var held in slot.Held) {
                    bytes += ((held.Readback || held.HostVisible) ? new GpuMemoryBytes(DeviceLocal: 0UL, HostVisible: held.Buffer.SizeBytes) : new GpuMemoryBytes(DeviceLocal: held.Buffer.SizeBytes, HostVisible: 0UL));
                }
            }
            foreach (var client in m_clients) { bytes += client.OwnedBytes; }
            return bytes;
        }
    }
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
    /// <summary>Stops every client's admission and wakes its pending submitters (<see cref="ISdfGpuWorkClient.Stop"/>),
    /// for work that will not be pumped again before it is disposed: its tables were parked and wait in the device's
    /// retirement. Releases nothing; <see cref="Dispose"/> stops them again and drains.</summary>
    public void StopClients() {
        foreach (var client in m_clients) { client.Stop(); }
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
            var client = slot.Client!;

            Vacate(slot: slot);
            client.Abandon(job: job);
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
        m_budget.Part(turn: m_turn);
    }

    // Hands each job whose fence signaled to its client: its timestamps settle its charge, and the client reads what it
    // copied back, keeping the slot until it releases the readback.
    private void Poll() {
        for (var index = 0; (index < SlotCount); index++) {
            var slot = m_slots[index];

            if ((slot.Job is not { } job) || !slot.Fence.IsSignaled) { continue; }
            slot.Fence.Wait();
            // The fence proves the job's region copies read what they staged: each region gives that staging back now,
            // not at its slot's next flush.
            foreach (var (region, regionSlot) in slot.Regions) { region.Retire(slot: regionSlot); }
            slot.Regions.Clear();

            var client = slot.Client!;

            var seconds = Settle(slot: slot);

            if (m_timestamps is not null) {
                if (slot.Charged && (seconds > slot.Allowance)) { m_overAllowance++; }
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
            slot.Reader = client;
            Vacate(slot: slot);
            if (--m_inFlight == 0) { m_busyTicks += (m_time.GetTimestamp() - m_busySince); }
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
        var now = m_time.GetTimestamp();
        var window = ((long)(IdleSeconds * m_time.TimestampFrequency));

        foreach (var slot in m_slots) {
            if (!slot.IsFree || (slot.Held.Count == 0)) { continue; }
            for (var at = (slot.Held.Count - 1); (at >= 0); at--) {
                var held = slot.Held[at];

                if ((now - held.WindowStart) < window) { continue; }
                if ((held.WindowBytes == 0UL) || ((held.Sizing == GpuBufferSizing.Granule) && (held.Buffer.SizeBytes > GpuFittedBuffer.AllocationSize(GpuBufferSizing.Granule, 0, held.WindowBytes)))) {
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
    // wraps to nearly the counter's range). The job's span on the queue's clock goes to the budget, which judges the
    // frames behind it, held or budgeted alike, since both occupied the queue.
    private double Settle(Slot slot) {
        var seconds = slot.Estimate;

        if (m_timestamps is { } timestamps) {
            Span<byte> times = stackalloc byte[(2 * sizeof(ulong))];

            slot.Times.Read(destination: times);

            var mask = ((timestamps.ValidBits >= 64) ? ulong.MaxValue : ((1UL << ((int)timestamps.ValidBits)) - 1UL));
            var start = BitConverter.ToUInt64(value: times);
            var ticks = (BitConverter.ToUInt64(value: times[sizeof(ulong)..]) - start) & mask;
            var measured = ((ticks * timestamps.NanosecondsPerTick) * 1e-9);
            var inFlight = m_time.GetElapsedTime(startingTimestamp: slot.SubmittedAt).TotalSeconds;

            seconds = (slot.Charged
                ? m_budget.SettleGpu(charge: slot.Charge, estimate: slot.Estimate, inFlight: inFlight, measured: measured)
                : SdfGpuWorkBudget.MeasuredWithin(clamped: out _, estimate: slot.Estimate, inFlight: inFlight, measured: measured));
            // The span's start reads the clock as the frame's timings do (ShaderPipelineRenderNode's latest span), and its
            // end follows from the held measurement, so a pair read end before start never spans the counter's range.
            var begun = ((start * timestamps.NanosecondsPerTick) * 1e-9);

            m_budget.Ran(endSeconds: (begun + seconds), startSeconds: begun);
        }
        m_gpuSeconds += seconds;
        return seconds;
    }
    // The one place a slot gives up its job, answered or abandoned: the job's charge leaves the work queued ahead of the
    // next frame (SdfGpuWorkBudget.Leave), so the budget's queued length always sums the charged jobs still in a slot.
    private void Vacate(Slot slot) {
        m_budget.Leave(charge: slot.Charge);
        slot.Charge = default;
        slot.Job = null;
        slot.Client = null;
    }
    // Banks what the presentation's budget accrued since any service last asked, and returns the seconds since this
    // service's last step (zero at the first), which its recording time is a share of.
    private double Accrue() {
        var now = m_time.GetTimestamp();
        var seconds = ((m_pumpedAt != 0) ? ((now - m_pumpedAt) / ((double)m_time.TimestampFrequency)) : 0.0);

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
        var began = m_time.GetTimestamp();

        if (within is { } left) {
            // The frame's own pump follows the wait: a job that could not be recorded before the deadline is its.
            if (left <= HostBudget) { return Stop.None; }
            hostBudget = (budgeted ? TimeSpan.FromTicks(value: Math.Min(val1: hostBudget.Ticks, val2: (left - HostBudget).Ticks)) : (left - HostBudget));
        }
        for (var index = 0; (index < SlotCount); index++) {
            var slot = m_slots[index];

            if (!slot.IsFree) { continue; }
            // Work the frame owes comes first, under its own allowance and outside the credit: a job that waits
            // for room holds the step as a deadline hold, since the ordinary allowance it would fall back to is smaller.
            if (budgeted) {
                if (m_time.GetElapsedTime(startingTimestamp: began) >= hostBudget) {
                    m_budgetHolds++;
                    return Stop.Host;
                }
                // The services divide the budget by what the player sees of their residencies, owed work included.
                if (!m_budget.Takes(turn: m_turn)) {
                    m_turnHolds++;
                    return Stop.Turn;
                }

                var owed = m_budget.OwedAllowance;

                if (!(owed.Queued && !(owed.Seconds > 0.0))) {
                    if (TryTake(allowance: owed, client: out var owedClient, estimate: out var owedEstimate, held: out var owedHeld, job: out var owedJob, owed: true, tables: tables, uploadBytes: m_budget.UploadAllowance)) {
                        if (!owed.Admits(seconds: owedEstimate)) { m_overDeadline++; }

                        var owedUpload = owedClient.UploadBytes(job: owedJob);

                        // A job handed back for want of room leaves its slot to the other clients.
                        if (!Submit(allowance: owed.Seconds, charged: true, client: owedClient, estimate: owedEstimate, index: index, job: owedJob, owed: true, slot: slot, tables: tables)) { index--; continue; }
                        m_budget.ChargeUpload(bytes: owedUpload);
                        m_owedJobs++;
                        continue;
                    }
                    if (owedHeld) {
                        m_deadlineHolds++;
                        return Stop.Deadline;
                    }
                }
            }
            if (budgeted && !m_budget.Holds) {
                // The next accrual banks the whole gap for a service that waited on the budget.
                m_budget.Want();
                m_budgetHolds++;
                return Stop.Budget;
            }
            if ((budgeted || (within is not null)) && (m_time.GetElapsedTime(startingTimestamp: began) >= hostBudget)) {
                if (budgeted) { m_budgetHolds++; }
                return Stop.Host;
            }

            var allowance = (budgeted ? m_budget.UploadAllowance : MostUploadJobBytes);
            // A held frame queues no frame behind its own work, so its jobs are cut to nothing but their clients' most.
            // Background work fits what the jobs already queued ahead of the next frame leave of its allowance.
            var fit = (budgeted ? m_budget.AllowanceOf(turn: m_turn) : SdfGpuWorkAllowance.Unbounded);

            if (fit.Queued && !(fit.Seconds > 0.0)) {
                m_deadlineHolds++;
                return Stop.Deadline;
            }
            // With work queued, a client whose next job does not fit what is left takes none and holds the room for it;
            // a completion of this service's own job wakes it, and another service's frees room by the next pump. With
            // nothing queued and the frame over its period nothing will complete to make room, so the job holds the
            // budget until the share has paid for it (SdfGpuWorkBudget.Hold).
            if (!TryTake(allowance: fit, client: out var client, estimate: out var estimate, held: out var held, job: out var job, tables: tables, uploadBytes: allowance)) {
                if (!held) {
                    // A service with nothing to take holds no other service to its pace.
                    if (budgeted) { m_budget.Idle(turn: m_turn); }
                    m_refillRested = true;
                    return Stop.None;
                }
                if (budgeted) { m_budget.Hold(seconds: estimate, turn: m_turn); }
                m_deadlineHolds++;
                return Stop.Deadline;
            }
            if (!fit.Admits(seconds: estimate)) { m_overDeadline++; }

            var uploadBytes = (budgeted ? client.UploadBytes(job: job) : 0);

            // A job handed back for want of room leaves its slot to the other clients.
            if (!Submit(allowance: fit.Seconds, charged: budgeted, client: client, estimate: estimate, index: index, job: job, owed: false, slot: slot, tables: tables)) { index--; continue; }
            if (budgeted) { m_budget.ChargeUpload(bytes: uploadBytes); }
        }
        // Every slot is busy: only a completion or a slot given back moves the service.
        m_refillRested = true;
        return Stop.None;
    }
    // Offers the clients a take in turn from the one after the last that took. False with held when a client's next job
    // waits for room while work is queued: that client keeps the turn, and the estimate is that job's price, which a hold
    // banks (SdfGpuWorkBudget.Hold). An owed take (ISdfGpuWorkClient.TryTakeOwed)
    // offers the clients in the service's order and moves no turn, since it comes before every ordinary one.
    private bool TryTake(SdfWorldTables tables, int uploadBytes, SdfGpuWorkAllowance allowance, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out ISdfGpuWorkClient? client, out double estimate, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out bool held, bool owed = false) {
        var first = (owed ? 0 : m_nextClient);

        estimate = 0.0;
        for (var turn = 0; (turn < m_clients.Count); turn++) {
            var candidate = m_clients[((first + turn) % m_clients.Count)];

            // A client whose job was handed back for want of room is offered a slot again once one has come back, whose
            // buffers its job may then take, or after a while, since room can come from another holder. The others'
            // jobs go on meanwhile.
            var waiting = WaitingOf(client: candidate);

            if ((waiting is not null) && (BusySlots() >= waiting.Busy) && (m_time.GetElapsedTime(startingTimestamp: waiting.At) < DeferralRetry)) { continue; }
            if (owed
                ? candidate.TryTakeOwed(allowance: allowance, estimatedSeconds: out estimate, job: out job, tables: tables, uploadBytes: uploadBytes)
                : candidate.TryTake(allowance: allowance, estimatedSeconds: out estimate, job: out job, tables: tables, uploadBytes: uploadBytes)) {
                if (!owed) { m_nextClient = (((first + turn) + 1) % m_clients.Count); }
                client = candidate;
                held = false;
                return true;
            }
            if (allowance.Queued && (estimate > 0.0)) {
                // A job that waits for room keeps its turn, and no client behind it takes the room it waits for, so a
                // client of whole-allowance jobs is not starved by another's jobs cut to what is left.
                if (!owed) { m_nextClient = ((first + turn) % m_clients.Count); }
                break;
            }
            if (!owed && (waiting is not null)) { _ = m_waiting.Remove(waiting); }
        }
        held = (allowance.Queued && (estimate > 0.0));
        client = null;
        if (!held) { estimate = 0.0; }
        job = null;
        return false;
    }
    // Records a job into its slot between its timestamps and submits it armed with the slot's fence, which is asked to
    // wake the host's wait when the job completes. The one door a job reaches the queue through: it stamps the job's
    // ring slot before the job binds anything. False when the device had no room for what the job holds: the job is handed
    // back to its client, and nothing reached the queue.
    private bool Submit(ISdfGpuWorkClient client, object job, double estimate, double allowance, int index, Slot slot, bool charged, bool owed, SdfWorldTables tables) {
        var recorder = m_gpu.Recorder;
        var commandBuffer = slot.Commands.CommandBufferHandle;
        var first = ((uint)(2 * index));

        slot.ReadbackBytes = 0;
        slot.Readback = null;
        slot.Copied = false;
        slot.Consumed = false;
        slot.Regions.Clear();
        slot.RingSlot = tables.CurrentSlot;
        try {
            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer);
            recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, label: $"{m_name} gpu-work");
            m_timestamps?.Reset(command: commandBuffer, count: 2, first: first);
            m_timestamps?.Write(command: commandBuffer, index: first);
            try {
                m_recording = client;
                client.Record(job: job, recording: new SdfGpuWorkRecording(client: client, commandBuffer: commandBuffer, owner: this, recorder: recorder, slot: index));
            } catch (Exception refusal) when ((refusal is SdfGpuWorkDeferredException or GpuMemoryRefusedException) && !slot.Consumed) {
                // Closed and never submitted: the slot's next job begins the command buffer again. Only a job that consumed
                // nothing is handed back; a refusal after it consumed state faults the service instead of losing that.
                recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
                recorder.EndCommandBuffer(commandBufferHandle: commandBuffer);
                client.Abandon(job: job);
                m_deferrals++;

                var now = m_time.GetTimestamp();

                if (WaitingOf(client: client) is not { } waiting) { m_waiting.Add(item: (waiting = new Deferral(Client: client, Since: now))); }
                waiting.At = now;
                waiting.Busy = BusySlots();
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
        if (WaitingOf(client: client) is { } submitted) { _ = m_waiting.Remove(item: submitted); }
        slot.SubmittedAt = m_time.GetTimestamp();
        slot.Estimate = estimate;
        slot.Allowance = allowance;
        slot.Charged = charged;
        slot.Charge = (charged ? m_budget.ChargeGpu(owed: owed, seconds: estimate, turn: m_turn) : default);
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

        var readback = ReserveReadback(bytes: Math.Max(val1: bytes, val2: sizeof(uint)), slot: slot);

        target.Consumed = true;

        // A job with nothing to copy back (a batch whose lattices owe no output words) still completes through its
        // readback, empty: the device refuses a zero-length copy, so none is recorded.
        if (bytes != 0) {
            GpuReadbackCopy.Record(
                commandBuffer: commandBuffer,
                destination: readback,
                recorder: m_gpu.Recorder,
                sizeBytes: bytes,
                source: source,
                sourceAccess: (GpuAccess.ShaderRead | GpuAccess.ShaderWrite | GpuAccess.CopyWrite),
                sourceStage: (GpuStage.ComputeShader | GpuStage.VertexShader | GpuStage.FragmentShader | GpuStage.Transfer)
            );
        }
        target.Readback = readback;
        target.ReadbackBytes = bytes;
        target.Copied = true;
    }
    // The slot's readback, held at a granule's size for a copy of the bytes: what CopyToReadback copies into, reserved
    // ahead by a job that consumes state before its copy.
    internal IGpuReadbackBuffer ReserveReadback(int slot, ulong bytes) =>
        ((IGpuReadbackBuffer)Hold(bytes: bytes, client: null, hostVisible: true, part: ReadbackPart, readback: true, sizing: GpuBufferSizing.Granule, slot: slot, usage: default));
    // Marks that the slot's job recorded something that consumes state (a region's owed words), after which handing the
    // job back would lose it.
    internal void Consume(int slot) => m_slots[slot].Consumed = true;
    // Notes that the slot's job copied a region through the region's slot, which Poll retires once the job's fence
    // signals (GpuRegion.Retire).
    internal void NoteCopied(int slot, GpuRegion region, int regionSlot) => m_slots[slot].Regions.Add(item: (region, regionSlot));
    // The one door a slot's buffer is created through: returns the buffer the slot holds for a client's part when it
    // still fits the job, and otherwise replaces it, reserving the growth before the device is asked and giving the
    // old buffer's bytes back first, so the slot never holds both. A refusal leaves the slot without the part, which
    // its next job creates.
    // Invariant: a refusal hands back a job that has consumed nothing. A slot whose job consumed state refuses to create
    // any buffer for it (an InvalidOperationException, which faults the service rather than discarding the recording):
    // only a buffer the slot already holds at the size asked comes back.
    internal IGpuBuffer Hold(int slot, ISdfGpuWorkClient? client, string part, ulong bytes, bool hostVisible, bool readback, GpuBufferUsage usage, GpuBufferSizing sizing) {
        var held = m_slots[slot].Held;
        var now = m_time.GetTimestamp();
        var at = -1;

        for (var index = 0; (index < held.Count); index++) {
            if (ReferenceEquals(objA: held[index].Client, objB: client) && (held[index].Part == part)) { at = index; break; }
        }
        var size = GpuFittedBuffer.AllocationSize(sizing, at >= 0 ? held[at].Buffer.SizeBytes : 0, bytes);
        if (at >= 0 && held[at].Buffer.SizeBytes == size) {
            held[at].WindowBytes = Math.Max(val1: held[at].WindowBytes, val2: Math.Max(val1: bytes, val2: 1UL));
            return held[at].Buffer;
        }
        if (m_slots[slot].Consumed) {
            throw new InvalidOperationException(message: $"A background GPU job asked for its {part} after it consumed state: it reserves every buffer, its readback included, before it copies a region.");
        }
        if (at >= 0) {
            Release(held: held[at]);
            held.RemoveAt(index: at);
        }

        if ((m_hold is { } hold) && !hold.TryRaise(bytes: ((long)size), forced: false, hostVisible: hostVisible)) {
            // With no room, what the other free slots still hold for jobs to come goes first. Past that a client with
            // no slot in use, neither a job in flight nor a readback it still holds, reserves its job as the floor, so
            // its work always progresses; one whose reader is slow waits for the slot, but not past its patience. Any
            // other hands the job back.
            ReleaseFree(except: slot);

            var (flying, reading) = InUseBy(client: m_recording!);
            var forced = ((flying == 0) && ((reading == 0) || ((WaitingOf(client: m_recording!) is { } waiting) && (m_time.GetElapsedTime(startingTimestamp: waiting.Since) >= ForcedPatience))));

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
    // The slots a client has in use: those its job is in flight in, and those whose readback it has not released.
    private (int Flying, int Reading) InUseBy(ISdfGpuWorkClient client) {
        var flying = 0;
        var reading = 0;

        foreach (var slot in m_slots) {
            if ((slot.Job is not null) && ReferenceEquals(objA: slot.Client, objB: client)) { flying++; }
            if ((slot.Job is null) && !slot.IsFree && ReferenceEquals(objA: slot.Reader, objB: client)) { reading++; }
        }
        return (flying, reading);
    }
    private Deferral? WaitingOf(ISdfGpuWorkClient client) {
        foreach (var waiting in m_waiting) {
            if (ReferenceEquals(objA: waiting.Client, objB: client)) { return waiting; }
        }
        return null;
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

    // What stopped a refill short of a free slot's job that waiting lifts: the budget's credit, the step's share of
    // recording time, or the frame's allowance spent on the jobs in flight.
    private enum Stop { None, Budget, Host, Deadline, Turn }

    // A client whose job was handed back for want of room: since when (its first hand-back since a job of its was
    // submitted), when the latest was, and the slots that were not free then.
    private sealed record Deferral(ISdfGpuWorkClient Client, long Since) {
        public long At { get; set; }
        public int Busy { get; set; }
    }
    // One buffer a slot holds: for a client's part, or the slot's readback (no client), and the window its size is
    // judged over: when it began and the most bytes a job needed of the buffer since.
    private sealed record HeldBuffer(ISdfGpuWorkClient? Client, string Part, IGpuBuffer Buffer, bool HostVisible, bool Readback, GpuBufferSizing Sizing) {
        public long WindowStart { get; set; }
        public ulong WindowBytes { get; set; }
    }
    // One slot: its command buffer, fence and timestamp readback, the job in it and its client, the buffers it holds,
    // the readback its job copied into and how many bytes, the GPU seconds the job was charged and when it was
    // submitted, and the tables' ring slot the job leased.
    private sealed class Slot(IGpuCommandPool Commands, IGpuSubmissionFence Fence, IGpuReadbackBuffer Times) {
        public IGpuCommandPool Commands { get; } = Commands;
        public IGpuSubmissionFence Fence { get; } = Fence;
        public IGpuReadbackBuffer Times { get; } = Times;
        public List<HeldBuffer> Held { get; } = [];
        // The regions the job in flight copied, each with the region slot it staged through.
        public List<(GpuRegion Region, int Slot)> Regions { get; } = [];

        public SdfGpuWorkReadback? Lease { get; set; }
        // The client the slot's last answered job was, which holds Lease until it releases it.
        public ISdfGpuWorkClient? Reader { get; set; }
        public IGpuReadbackBuffer? Readback { get; set; }
        public ulong ReadbackBytes { get; set; }
        // Whether no job is in flight in the slot and its last readback was released: the one condition under which
        // Refill reuses the slot and ReleaseIdle releases its buffers.
        public bool IsFree => ((Job is null) && (Lease is null or { Released: true }));
        public ISdfGpuWorkClient? Client { get; set; }
        public bool Copied { get; set; }
        // Whether the job being recorded consumed state (SdfGpuWork.Consume), so no buffer may be created for it.
        public bool Consumed { get; set; }
        public double Estimate { get; set; }
        // The GPU time the job was taken to fit (ISdfGpuWorkClient.TryTake's allowance).
        public double Allowance { get; set; }
        public object? Job { get; set; }
        public long SubmittedAt { get; set; }
        public int RingSlot { get; set; }
        // Whether the job was charged to the GPU budget, and its charge: the seconds it debited, which its measured time
        // then replaces, and the length it queued ahead of the next frame, which leaves when the slot gives the job up.
        public bool Charged { get; set; }
        public SdfGpuWorkCharge Charge { get; set; }
    }
}
