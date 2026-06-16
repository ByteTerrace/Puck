using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns the per-frame synchronization primitives — the image-available semaphore, the in-flight fence, and
/// the per-image render-finished semaphores — and destroys them when disposed.
/// </summary>
public sealed class VulkanFrameSynchronization : IDisposable {
    private bool m_disposed;
    private readonly IVulkanFrameSynchronizationApi m_frameSynchronizationApi;
    private nint[] m_renderFinishedSemaphoreHandles;

    /// <summary>Gets the native <c>VkDevice</c> handle that owns the primitives.</summary>
    public nint DeviceHandle { get; }
    /// <summary>Gets the native <c>VkSemaphore</c> handle signaled when an acquired image becomes available, or zero once disposed.</summary>
    public nint ImageAvailableSemaphoreHandle { get; private set; }
    /// <summary>Gets the native <c>VkFence</c> handle signaled when the frame's submitted work completes, or zero once disposed.</summary>
    public nint InFlightFenceHandle { get; private set; }
    /// <summary>Gets the native <c>VkSemaphore</c> handles signaled when rendering to each swapchain image completes. Empty once disposed.</summary>
    public IReadOnlyList<nint> RenderFinishedSemaphoreHandles => m_renderFinishedSemaphoreHandles;

    /// <summary>Initializes a new instance of the <see cref="VulkanFrameSynchronization"/> class, taking ownership of the supplied synchronization primitives.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the primitives.</param>
    /// <param name="imageAvailableSemaphoreHandle">The native <c>VkSemaphore</c> handle signaled when an acquired image becomes available.</param>
    /// <param name="inFlightFenceHandle">The native <c>VkFence</c> handle signaled when the frame's work completes.</param>
    /// <param name="renderFinishedSemaphoreHandles">The native <c>VkSemaphore</c> handles, one per swapchain image, signaled when rendering completes.</param>
    /// <param name="frameSynchronizationApi">The API used to destroy the primitives and wait on the fence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="renderFinishedSemaphoreHandles"/> or <paramref name="frameSynchronizationApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="deviceHandle"/>, <paramref name="imageAvailableSemaphoreHandle"/>, or <paramref name="inFlightFenceHandle"/> is zero, or <paramref name="renderFinishedSemaphoreHandles"/> is empty or contains a zero handle.</exception>
    public VulkanFrameSynchronization(
        nint deviceHandle,
        nint imageAvailableSemaphoreHandle,
        nint inFlightFenceHandle,
        nint[] renderFinishedSemaphoreHandles,
        IVulkanFrameSynchronizationApi frameSynchronizationApi
    ) {
        ArgumentNullException.ThrowIfNull(renderFinishedSemaphoreHandles);
        ArgumentNullException.ThrowIfNull(frameSynchronizationApi);

        if (deviceHandle == 0) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (imageAvailableSemaphoreHandle == 0) {
            throw new ArgumentException(
                message: "Vulkan image-available semaphore handle must be non-zero.",
                paramName: nameof(imageAvailableSemaphoreHandle)
            );
        }

        if (inFlightFenceHandle == 0) {
            throw new ArgumentException(
                message: "Vulkan in-flight fence handle must be non-zero.",
                paramName: nameof(inFlightFenceHandle)
            );
        }

        if (
            (renderFinishedSemaphoreHandles.Length == 0) ||
            (Array.IndexOf(
                array: renderFinishedSemaphoreHandles,
                value: (nint)0
            ) >= 0)
        ) {
            throw new ArgumentException(
                message: "Vulkan render-finished semaphore handles must be non-empty and non-zero.",
                paramName: nameof(renderFinishedSemaphoreHandles)
            );
        }

        DeviceHandle = deviceHandle;
        ImageAvailableSemaphoreHandle = imageAvailableSemaphoreHandle;
        InFlightFenceHandle = inFlightFenceHandle;
        m_renderFinishedSemaphoreHandles = renderFinishedSemaphoreHandles;
        m_frameSynchronizationApi = frameSynchronizationApi;
    }

    /// <summary>Destroys the owned semaphores and fence. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        foreach (var renderFinishedSemaphoreHandle in m_renderFinishedSemaphoreHandles) {
            if (renderFinishedSemaphoreHandle != 0) {
                m_frameSynchronizationApi.DestroySemaphore(
                    deviceHandle: DeviceHandle,
                    semaphoreHandle: renderFinishedSemaphoreHandle
                );
            }
        }

        m_renderFinishedSemaphoreHandles = [];

        if (ImageAvailableSemaphoreHandle != 0) {
            m_frameSynchronizationApi.DestroySemaphore(
                deviceHandle: DeviceHandle,
                semaphoreHandle: ImageAvailableSemaphoreHandle
            );
            ImageAvailableSemaphoreHandle = 0;
        }

        if (InFlightFenceHandle != 0) {
            m_frameSynchronizationApi.DestroyFence(
                deviceHandle: DeviceHandle,
                fenceHandle: InFlightFenceHandle
            );
            InFlightFenceHandle = 0;
        }

        m_disposed = true;
    }
    /// <summary>Waits for the in-flight fence to become signaled, or until the timeout elapses.</summary>
    /// <param name="timeout">The maximum time to wait, in nanoseconds.</param>
    /// <returns><see cref="VkResult.Success"/> if the fence became signaled, <see cref="VkResult.Timeout"/> if the timeout elapsed first, or an error code.</returns>
    /// <exception cref="ObjectDisposedException">The synchronization object has been disposed.</exception>
    public VkResult WaitForInFlightFence(ulong timeout) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        return m_frameSynchronizationApi.WaitForFence(
            deviceHandle: DeviceHandle,
            fenceHandle: InFlightFenceHandle,
            timeout: timeout
        );
    }
}
