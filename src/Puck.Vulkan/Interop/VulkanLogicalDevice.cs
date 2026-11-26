using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native logical device (<c>VkDevice</c>) through its command table, together with its graphics and present
/// queues, and destroys the device when disposed.
/// </summary>
public sealed class VulkanLogicalDevice : IGpuHeldDevice, IDisposable {
    private readonly IVulkanLogicalDeviceApi m_logicalDeviceApi;
    private readonly VkQueue m_graphicsQueue;
    private readonly VkQueue m_presentQueue;

    private bool m_disposed;
    private VulkanQueueTimeline? m_timeline;

    internal GpuQueueTimeline Timeline => (m_timeline ??= new VulkanQueueTimeline(device: this));
    // Assigned only by the factory after enabling both sparse buffer features and requesting this queue family.
    internal VkQueue? SparseQueue { get; }

    /// <summary>Gets whether this generation enabled sparse buffers and owns their binding queue.</summary>
    public bool SparseBuffersEnabled => SparseQueue.HasValue;

    // The address space this generation's sparse buffers reserve between them, which the device bounds for all of them
    // together (sparseAddressSpaceSize), never for each.
    private long m_sparseReservedBytes;
    // The sparse operations queued from any thread (QueueSparse), in order, and the list an issue swaps them into; both
    // under m_sparseGate, the second read only by the issue on the submission thread.
    private readonly Lock m_sparseGate = new();
    private List<VulkanSparseOperation> m_sparseQueued = [];
    private List<VulkanSparseOperation> m_sparseIssuing = [];
    // Whether the device is being torn down, after which an operation is settled with no value as it is queued.
    private bool m_sparseClosed;

    // One queued sparse operation: a buffer's bindings, or a checkpoint alone with none, and what hears its value.
    private readonly record struct VulkanSparseOperation(nint Buffer, VkSparseMemoryBind[]? Bindings, Action<ulong?>? Settled);
    // Whether the submission thread is issuing them now: a door read inside the issue (its checkpoint, its graphics
    // wait) issues nothing. Read and written on the submission thread alone.
    private bool m_sparseIssuingNow;

    /// <summary>Reserves address space for one sparse buffer out of what the device gives all of them together
    /// (<see cref="GpuDeviceCapabilities.SparseAddressSpaceBytes"/>). Thread-safe.</summary>
    /// <param name="bytes">The buffer's size, in bytes.</param>
    /// <returns><see langword="true"/> when the device enabled sparse buffers and has the room; the caller gives it back
    /// through <see cref="ReleaseSparseAddress"/> once the buffer is destroyed.</returns>
    internal bool TryReserveSparseAddress(ulong bytes) {
        var most = (Capabilities?.SparseAddressSpaceBytes ?? 0UL);

        if (!SparseBuffersEnabled || (bytes > most)) { return false; }
        while (true) {
            var held = Volatile.Read(location: ref m_sparseReservedBytes);

            if ((((ulong)held) + bytes) > most) { return false; }
            if (Interlocked.CompareExchange(comparand: held, location1: ref m_sparseReservedBytes, value: (held + ((long)bytes))) == held) { return true; }
        }
    }
    /// <summary>Gives back address space <see cref="TryReserveSparseAddress"/> reserved.</summary>
    /// <param name="bytes">The bytes reserved.</param>
    internal void ReleaseSparseAddress(ulong bytes) => _ = Interlocked.Add(location1: ref m_sparseReservedBytes, value: -((long)bytes));

    /// <summary>
    /// Queues a sparse operation on a buffer from any thread: a binding of its ranges, or, with no ranges, a checkpoint
    /// of the device timeline. Nothing reaches the queue here; the queue's door issues every queued operation, in the
    /// order queued, before it hands anything else to the queue (<see cref="IssueSparse"/>), so every submission the
    /// device makes after this returns follows the operation on the queue.
    /// <para>Invariant: every command buffer submitted after an operation was queued is submitted behind it, and a
    /// thread that only maps or unmaps chunks (a residency's pack, beside the frame thread) puts nothing on the queue.
    /// Enforced at the door every submission reads (<see cref="GraphicsQueue"/>, <see cref="PresentQueue"/>,
    /// <see cref="WaitIdle"/> and the batch's hand-over, <see cref="VulkanSubmissionBatch"/>), which issues before
    /// returning the queue; a submitter reads the door in the call that submits, after recording what it submits. Where
    /// it stops: a caller holding a queue handle from an earlier read and submitting work recorded since would pass it;
    /// and the door issues on whichever thread reads it, which besides the frame thread includes a pack or a recording
    /// whose retirement poll checkpoints the timeline, a read that shares the queue with the frame thread only through
    /// the frame's fork structure. Nothing checks either.</para>
    /// </summary>
    /// <param name="buffer">The sparse buffer, created on this device; ignored for a checkpoint alone.</param>
    /// <param name="bindings">Aligned, nonoverlapping ranges, null memory unbinding a range; <see langword="null"/> for a
    /// checkpoint alone. The device keeps the array until the operation is issued.</param>
    /// <param name="settled">What receives the timeline value once the operation is on the queue: a value signaled after
    /// the operation and after every submission made before it. It receives <see langword="null"/> when the operation's
    /// outcome is unknown (the issue threw, or the device was torn down first), which no timeline value certifies. Called
    /// on the thread whose door read issues the operation, or at once on a device being torn down; <see langword="null"/>
    /// for none.</param>
    /// <exception cref="InvalidOperationException">The device did not enable sparse buffers.</exception>
    /// <exception cref="ArgumentException"><paramref name="bindings"/> is empty, or names ranges of a zero
    /// <paramref name="buffer"/>.</exception>
    public void QueueSparse(nint buffer, VkSparseMemoryBind[]? bindings, Action<ulong?>? settled) {
        if (!SparseBuffersEnabled) { throw new InvalidOperationException(message: "This device did not enable sparse buffers."); }
        if (bindings is { Length: 0 }) { throw new ArgumentException(message: "A sparse binding batch must contain a range.", paramName: nameof(bindings)); }
        if ((bindings is not null) && (buffer == 0)) { throw new ArgumentException(message: "A sparse binding names its buffer.", paramName: nameof(buffer)); }

        lock (m_sparseGate) {
            if (!m_sparseClosed) {
                m_sparseQueued.Add(item: new VulkanSparseOperation(Bindings: bindings, Buffer: buffer, Settled: settled));
                return;
            }
        }
        settled?.Invoke(obj: null);
    }
    // Issues every queued sparse operation before the door it is called from returns the queue: one graphics checkpoint, the bindings in queued order, each waiting the value before it, then one graphics
    // wait on the last, so whatever the queue takes next follows them all (VulkanQueueTimeline.BindSparse). Every
    // operation then hears the last value. Then releases what the arena's sparse retirement holds behind values the
    // timeline has passed. A door read inside the issue issues nothing, nor one inside a checkpoint's signal, whose
    // value is taken already (the checkpoint's own door read, GpuQueueTimeline.BeforeCheckpoint, issued before it took
    // it); and a timeline that no longer certifies retirement (an earlier issue threw) settles every operation with no
    // value and puts nothing on the queue.
    private void IssueSparse() {
        if (m_sparseIssuingNow || !SparseBuffersEnabled || m_disposed) { return; }

        var timeline = (m_timeline ??= new VulkanQueueTimeline(device: this));

        if (timeline.IsSignaling) { return; }

        List<VulkanSparseOperation> issuing;

        lock (m_sparseGate) {
            (m_sparseQueued, m_sparseIssuing) = (m_sparseIssuing, m_sparseQueued);
            issuing = m_sparseIssuing;
        }
        m_sparseIssuingNow = true;
        try {
            if (issuing.Count > 0) { Settle(issued: issuing, value: (timeline.Certifies ? Issue(issuing: issuing, timeline: timeline) : null)); }
            if (timeline.Certifies && Commands.Arena.HoldsSparseRetirements) {
                try {
                    Commands.Arena.ReleaseSparse(completed: timeline.Completed);
                } catch (DeviceLostException) {
                    // A lost device's queue runs nothing more; the loss's teardown releases what the retirement holds.
                }
            }
        } finally {
            issuing.Clear();
            m_sparseIssuingNow = false;
        }
    }
    // Puts the operations on the queue and returns the value every one of them hears; settles them all with none and
    // rethrows when the queue refused one, since then no value certifies any.
    private static ulong Issue(List<VulkanSparseOperation> issuing, VulkanQueueTimeline timeline) {
        var batches = new List<(nint Buffer, VkSparseMemoryBind[] Bindings)>(capacity: issuing.Count);

        foreach (var operation in issuing) {
            if (operation.Bindings is { } bindings) { batches.Add(item: (operation.Buffer, bindings)); }
        }
        try {
            return ((batches.Count > 0) ? timeline.BindSparse(batches: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: batches)) : timeline.Checkpoint());
        } catch {
            Settle(issued: issuing, value: null);
            throw;
        }
    }
    private static void Settle(List<VulkanSparseOperation> issued, ulong? value) {
        foreach (var operation in issued) { operation.Settled?.Invoke(obj: value); }
    }
    // Settles every operation still queued with no value, as the device is torn down: none reached the queue, and the
    // arena's teardown releases what each was to retire.
    private void AbandonSparse() {
        List<VulkanSparseOperation> abandoned;

        lock (m_sparseGate) {
            m_sparseClosed = true;
            abandoned = m_sparseQueued;
            m_sparseQueued = [];
        }
        Settle(issued: abandoned, value: null);
    }

    /// <summary>Gets the graphics queue of the device, first handing the open batch's deferred submissions to it
    /// (<see cref="GpuSubmissionBatch.Split"/>) and issuing every queued sparse operation (<see cref="QueueSparse"/>),
    /// so a submit, signal, present or wait a caller puts on the returned queue follows every submission and sparse
    /// operation made before it. This read is the queue's one door, read on the submission thread; a queue family alone
    /// is read from <see cref="GraphicsQueueFamilyIndex"/>, which hands nothing over.</summary>
    public VkQueue GraphicsQueue {
        get {
            Batch.Split(cause: GpuQueueCause.QueueDoor);
            IssueSparse();

            return m_graphicsQueue;
        }
    }
    /// <summary>Gets the queue family index of <see cref="GraphicsQueue"/>, without handing the batch over.</summary>
    public uint GraphicsQueueFamilyIndex => m_graphicsQueue.FamilyIndex;

    /// <summary>Gets the submissions the device defers while a batch is open, which every read of
    /// <see cref="GraphicsQueue"/> or <see cref="PresentQueue"/> and the device's wait for idle hand to the queue
    /// first.</summary>
    internal VulkanSubmissionBatch Batch { get; }

    /// <summary>Gets what the device is, as its physical device reported it when the device was created; recorded
    /// for diagnostics and naming the device's pipeline-cache file, never branched on. <see langword="null"/> for a
    /// device created without one.</summary>
    public GpuDeviceIdentity? Identity { get; init; }
    /// <summary>Gets what the device can bind, as its physical device reported it when the device was created;
    /// its uniform-buffer view limit also bounds admission. <see langword="null"/> for a device created without one.</summary>
    public GpuDeviceCapabilities? Capabilities { get; init; }
    /// <summary>Gets the physical device's uniform-buffer view limit, refusing a device whose limit was never read.</summary>
    public ulong MaxConstantBufferBytes => (Capabilities?.MaxConstantBufferBytes
        ?? throw new InvalidOperationException(message: "The Vulkan device has no reported uniform-buffer view limit."));
    /// <summary>Gets the largest storage-buffer view whose length a kernel reads back correctly on the device: its
    /// reported <c>maxStorageBufferRange</c> (<see cref="GpuDeviceCapabilities.MaxStorageBufferBytes"/>), at most
    /// <see cref="StorageViewLengthBytes"/>. Refuses a device whose limit was never read.</summary>
    public ulong MaxStorageBufferBytes => ((Capabilities is { MaxStorageBufferBytes: > 0U } capabilities)
        ? Math.Min(val1: capabilities.MaxStorageBufferBytes, val2: StorageViewLengthBytes)
        : throw new InvalidOperationException(message: "The Vulkan device has no reported storage-buffer view limit."));
    /// <summary>The most bytes of a storage view whose length (<c>OpArrayLength</c>, HLSL's <c>GetDimensions</c>) every
    /// driver answers: 2^31 - 1. NVIDIA's driver reports a <c>maxStorageBufferRange</c> of 2^32 - 1 yet answers a length
    /// of zero for a view past 2 GiB, so a kernel that bounds its reads by the length (the mesh words door,
    /// <c>sdfMeshWord</c>) reads nothing at all: a 4 GiB mesh region drew no geometry on the RTX 4070 while the same
    /// region under 2 GiB drew. Dawn holds NVIDIA's buffers to 2 GiB for the same answer (dawn commit 4d0ebb4a, "Reduce
    /// max buffer size on NVIDIA to 2GB"), and NVIDIA's OpenGL reports its storage blocks at 2^31 - 1
    /// (<c>GL_MAX_SHADER_STORAGE_BLOCK_SIZE</c>).</summary>
    public const ulong StorageViewLengthBytes = int.MaxValue;
    /// <summary>Gets what the device's memory is, as its physical device reported it when the device was created;
    /// residency selection branches on it. The default profile, which reports nothing, for a device created without
    /// one.</summary>
    public GpuMemoryProfile MemoryProfile { get; init; }
    /// <summary>Gets the device's memory segments, one per heap with the pool it lies in, as its physical device
    /// reported them when the device was created; <see langword="null"/> for a device created without them.</summary>
    public GpuMemorySegments? MemorySegments { get; init; }
    /// <summary>Gets whether the device was created with <see cref="MemoryBudgetExtension"/>, so its physical device
    /// reports a budget and usage per heap.</summary>
    public bool MemoryBudgetEnabled { get; init; }

    /// <summary>The extension that reports a budget and usage per heap
    /// (<c>VkPhysicalDeviceMemoryBudgetPropertiesEXT</c>), enabled when the device lists it.</summary>
    public const string MemoryBudgetExtension = "VK_EXT_memory_budget";

    /// <summary>Gets the device's command table, which carries the native <c>VkDevice</c> handle.</summary>
    public VulkanDeviceCommands Commands { get; }
    /// <summary>Gets whether the device has been disposed — a native call through <see cref="Commands"/> after that is
    /// a use-after-free, so late teardown paths (an upload/readback outliving the renderer's device) check this first.</summary>
    public bool IsDisposed => m_disposed;
    /// <summary>Gets the physical device this logical device was created from.</summary>
    public VkPhysicalDevice PhysicalDevice { get; }
    /// <summary>Gets the present queue of the device, which may be the same queue as <see cref="GraphicsQueue"/>, first
    /// handing the open batch's deferred submissions to the graphics queue and issuing every queued sparse operation, so a
    /// present follows them.</summary>
    public VkQueue PresentQueue {
        get {
            Batch.Split(cause: GpuQueueCause.QueueDoor);
            IssueSparse();

            return m_presentQueue;
        }
    }
    /// <summary>Gets the device's persistent pipeline cache, which every compute and graphics pipeline creation on the
    /// device passes to the driver; <see langword="null"/> for a device created without one. The device owns it: it is
    /// written to disk and destroyed just before the device.</summary>
    public VulkanPipelineCache? PipelineCache { get; init; }
    /// <summary>Gets the group each of the device's descriptor sets belongs to, which the device's bindings, recorder and
    /// grouped pipelines share.</summary>
    public VulkanDescriptorSetGroups SetGroups { get; } = new();

    /// <summary>Initializes a new instance of the <see cref="VulkanLogicalDevice"/> class, taking ownership of an existing native device.</summary>
    /// <param name="device">The command table of the native device to own.</param>
    /// <param name="physicalDevice">The physical device the logical device was created from.</param>
    /// <param name="graphicsQueue">The graphics queue.</param>
    /// <param name="presentQueue">The present queue.</param>
    /// <param name="logicalDeviceApi">The API used to destroy the device and wait for it to idle.</param>
    /// <param name="sparseQueue">The requested sparse queue, only when both sparse buffer features were enabled.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="logicalDeviceApi"/> is <see langword="null"/>.</exception>
    public VulkanLogicalDevice(
        VulkanDeviceCommands device,
        VkPhysicalDevice physicalDevice,
        VkQueue graphicsQueue,
        VkQueue presentQueue,
        IVulkanLogicalDeviceApi logicalDeviceApi,
        VkQueue? sparseQueue = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: logicalDeviceApi);

        Commands = device;
        PhysicalDevice = physicalDevice;
        m_graphicsQueue = graphicsQueue;
        m_presentQueue = presentQueue;
        SparseQueue = sparseQueue;
        Batch = new VulkanSubmissionBatch(device: device, queue: graphicsQueue, issue: IssueSparse);
        m_logicalDeviceApi = logicalDeviceApi;
        device.Checkpoints.Watch(queue: graphicsQueue.Handle);
    }

    /// <summary>Destroys the owned device, then ends its entries in the device's memory counts. Safe to call more than
    /// once.</summary>
    /// <exception cref="InvalidOperationException">An allocation the device's memory counts still hold was never
    /// released by its owner; the device is destroyed first, and the message names each leaked allocation.</exception>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        AbandonSparse();
        PipelineCache?.Dispose();
        m_timeline?.Dispose();
        Commands.Arena.FreeAll();
        m_logicalDeviceApi.DestroyDevice(device: Commands);
        Commands.MarkDestroyed();
        m_disposed = true;
        Commands.Memory?.EndDevice(device: Commands.Handle);
    }
    /// <summary>Drains the device, tolerating an already-LOST device: <c>vkDeviceWaitIdle</c> returns
    /// <c>VK_ERROR_DEVICE_LOST</c> on a lost device (surfaced as <see cref="DeviceLostException"/>), which during
    /// TEARDOWN just means "there is nothing left to drain". For use in Dispose / device-loss paths only — the frame loop
    /// calls the throwing <see cref="WaitIdle"/> so a genuine loss surfaces and triggers recovery. A no-op once disposed.</summary>
    public void TryWaitIdle() {
        if (m_disposed) {
            return;
        }

        try {
            WaitIdle();
        } catch (DeviceLostException) {
            // The device is already lost; nothing in flight will ever complete, so there is nothing to drain.
        }
    }
    /// <summary>Blocks until all queues on the device have completed their outstanding work.</summary>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="VulkanException">The underlying <c>vkDeviceWaitIdle</c> call failed.</exception>
    public void WaitIdle() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        // Work deferred to an open batch, and every queued sparse operation, reach the queue before the wait drains it;
        // the batch stays open.
        Batch.Split(cause: GpuQueueCause.Drain);
        IssueSparse();

        var result = m_logicalDeviceApi.WaitIdle(device: Commands);

        result.ThrowIfFailed(device: Commands, operation: "vkDeviceWaitIdle");
    }
}
