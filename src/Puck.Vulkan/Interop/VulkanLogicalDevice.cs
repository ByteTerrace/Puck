using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native logical device (<c>VkDevice</c>) through its command table, together with its graphics and present
/// queues, and destroys the device when disposed.
/// </summary>
public sealed class VulkanLogicalDevice : IDisposable {
    private readonly IVulkanLogicalDeviceApi m_logicalDeviceApi;

    private readonly VkQueue m_graphicsQueue;
    private readonly VkQueue m_presentQueue;

    private bool m_disposed;

    /// <summary>Gets the graphics queue of the device, first handing the open batch's deferred submissions to it
    /// (<see cref="GpuSubmissionBatch.Split"/>), so a submit, signal, present or wait a caller puts on the returned
    /// queue follows every submission made before it. This read is the queue's one door; a queue family alone is read
    /// from <see cref="GraphicsQueueFamilyIndex"/>, which hands nothing over.</summary>
    public VkQueue GraphicsQueue {
        get {
            Batch.Split();

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
    /// recorded, never branched on. <see langword="null"/> for a device created without one.</summary>
    public GpuDeviceCapabilities? Capabilities { get; init; }
    /// <summary>Gets what the device's memory is, as its physical device reported it when the device was created;
    /// residency selection branches on it. The default profile, which reports nothing, for a device created without
    /// one.</summary>
    public GpuMemoryProfile MemoryProfile { get; init; }
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
            Batch.Split();

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
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="logicalDeviceApi"/> is <see langword="null"/>.</exception>
    public VulkanLogicalDevice(
        VulkanDeviceCommands device,
        VkPhysicalDevice physicalDevice,
        VkQueue graphicsQueue,
        VkQueue presentQueue,
        IVulkanLogicalDeviceApi logicalDeviceApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: logicalDeviceApi);

        Commands = device;
        PhysicalDevice = physicalDevice;
        m_graphicsQueue = graphicsQueue;
        m_presentQueue = presentQueue;
        Batch = new VulkanSubmissionBatch(device: device, queue: graphicsQueue);
        m_logicalDeviceApi = logicalDeviceApi;
        device.Checkpoints.Watch(queue: graphicsQueue.Handle);
    }

    /// <summary>Destroys the owned device, then ends its entries in the device-local memory counts. Safe to call more
    /// than once.</summary>
    /// <exception cref="InvalidOperationException">An allocation the device's memory counts still hold was never
    /// released by its owner; the device is destroyed first, and the message names each leaked allocation.</exception>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        PipelineCache?.Dispose();
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
        Batch.Split();

        var result = m_logicalDeviceApi.WaitIdle(device: Commands);

        result.ThrowIfFailed(device: Commands, operation: "vkDeviceWaitIdle");
    }
}
