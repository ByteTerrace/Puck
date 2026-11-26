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

    /// <summary>Orders buffer mappings after deferred graphics work and before subsequent graphics submissions.
    /// Called on the submission thread, like every graphics queue operation. Physical backing remains owned until
    /// the returned timeline value completes; a failed operation retains backing until device teardown.</summary>
    /// <param name="buffer">The sparse buffer, created on this device.</param>
    /// <param name="bindings">Aligned, nonoverlapping ranges; null memory unbinds a range.</param>
    /// <returns>The device timeline value for the binding operation.</returns>
    public ulong BindSparse(nint buffer, ReadOnlySpan<VkSparseMemoryBind> bindings) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        if (!SparseBuffersEnabled) { throw new InvalidOperationException(message: "This device did not enable sparse buffers."); }
        ArgumentOutOfRangeException.ThrowIfZero(buffer);
        if (bindings.IsEmpty) { throw new ArgumentException(message: "A sparse binding batch must contain a range.", paramName: nameof(bindings)); }
        return (m_timeline ??= new VulkanQueueTimeline(device: this)).BindSparse(buffer, bindings.ToArray());
    }

    /// <summary>Gets the graphics queue of the device, first handing the open batch's deferred submissions to it
    /// (<see cref="GpuSubmissionBatch.Split"/>), so a submit, signal, present or wait a caller puts on the returned
    /// queue follows every submission made before it. This read is the queue's one door; a queue family alone is read
    /// from <see cref="GraphicsQueueFamilyIndex"/>, which hands nothing over.</summary>
    public VkQueue GraphicsQueue {
        get {
            Batch.Split(cause: GpuQueueCause.QueueDoor);

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
    /// handing the open batch's deferred submissions to the graphics queue, so a present follows them.</summary>
    public VkQueue PresentQueue {
        get {
            Batch.Split(cause: GpuQueueCause.QueueDoor);

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
        Batch = new VulkanSubmissionBatch(device: device, queue: graphicsQueue);
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

        // Work deferred to an open batch reaches the queue before the wait drains it; the batch stays open.
        Batch.Split(cause: GpuQueueCause.Drain);

        var result = m_logicalDeviceApi.WaitIdle(device: Commands);

        result.ThrowIfFailed(device: Commands, operation: "vkDeviceWaitIdle");
    }
}
