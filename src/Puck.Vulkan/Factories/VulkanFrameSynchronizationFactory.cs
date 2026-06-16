using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanFrameSynchronizationFactory"/>: it creates the image-available semaphore,
/// the per-image render-finished semaphores, and the in-flight fence (created already signaled), returning
/// an owning <see cref="VulkanFrameSynchronization"/> and cleaning up partial progress on failure.
/// </summary>
public sealed class VulkanFrameSynchronizationFactory : IVulkanFrameSynchronizationFactory {
    private static void EnsureHandle(nint handle, string message) {
        if (handle == 0) {
            throw new InvalidOperationException(message: message);
        }
    }

    private readonly IVulkanFrameSynchronizationApi m_frameSynchronizationApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanFrameSynchronizationFactory"/> class.</summary>
    /// <param name="frameSynchronizationApi">The synchronization API used to create the semaphores and fence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frameSynchronizationApi"/> is <see langword="null"/>.</exception>
    public VulkanFrameSynchronizationFactory(IVulkanFrameSynchronizationApi frameSynchronizationApi) {
        ArgumentNullException.ThrowIfNull(frameSynchronizationApi);

        m_frameSynchronizationApi = frameSynchronizationApi;
    }

    private void Cleanup(
        nint deviceHandle,
        nint imageAvailableSemaphoreHandle,
        nint inFlightFenceHandle,
        nint[] renderFinishedSemaphoreHandles
    ) {
        foreach (var renderFinishedSemaphoreHandle in renderFinishedSemaphoreHandles) {
            if (renderFinishedSemaphoreHandle != 0) {
                m_frameSynchronizationApi.DestroySemaphore(
                    deviceHandle: deviceHandle,
                    semaphoreHandle: renderFinishedSemaphoreHandle
                );
            }
        }

        if (imageAvailableSemaphoreHandle != 0) {
            m_frameSynchronizationApi.DestroySemaphore(
                deviceHandle: deviceHandle,
                semaphoreHandle: imageAvailableSemaphoreHandle
            );
        }

        if (inFlightFenceHandle != 0) {
            m_frameSynchronizationApi.DestroyFence(
                deviceHandle: deviceHandle,
                fenceHandle: inFlightFenceHandle
            );
        }
    }

    /// <inheritdoc/>
    public VulkanFrameSynchronization Create(VulkanLogicalDevice logicalDevice, int renderFinishedSemaphoreCount) {
        ArgumentNullException.ThrowIfNull(logicalDevice);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            renderFinishedSemaphoreCount,
            1
        );

        var semaphoreRequest = new VulkanFrameSynchronizationCreateRequest(
            DeviceHandle: logicalDevice.Handle,
            StartSignaled: false
        );
        var fenceRequest = new VulkanFrameSynchronizationCreateRequest(
            DeviceHandle: logicalDevice.Handle,
            StartSignaled: true
        );
        nint imageAvailableSemaphoreHandle = 0;
        var renderFinishedSemaphoreHandles = new nint[renderFinishedSemaphoreCount];
        nint inFlightFenceHandle = 0;

        try {
            var imageAvailableResult = m_frameSynchronizationApi.CreateSemaphore(
                request: semaphoreRequest,
                semaphoreHandle: out imageAvailableSemaphoreHandle
            );

            imageAvailableResult.ThrowIfFailed(operation: "vkCreateSemaphore");
            EnsureHandle(
                handle: imageAvailableSemaphoreHandle,
                message: "vkCreateSemaphore returned success without a valid image-available semaphore handle."
            );

            // One per swapchain image: see VulkanFrameSynchronization.RenderFinishedSemaphoreHandles.
            for (var semaphoreIndex = 0; (semaphoreIndex < renderFinishedSemaphoreHandles.Length); semaphoreIndex++) {
                var renderFinishedResult = m_frameSynchronizationApi.CreateSemaphore(
                    request: semaphoreRequest,
                    semaphoreHandle: out renderFinishedSemaphoreHandles[semaphoreIndex]
                );

                renderFinishedResult.ThrowIfFailed(operation: "vkCreateSemaphore");
                EnsureHandle(
                    handle: renderFinishedSemaphoreHandles[semaphoreIndex],
                    message: "vkCreateSemaphore returned success without a valid render-finished semaphore handle."
                );
            }

            var fenceResult = m_frameSynchronizationApi.CreateFence(
                fenceHandle: out inFlightFenceHandle,
                request: fenceRequest
            );

            fenceResult.ThrowIfFailed(operation: "vkCreateFence");
            EnsureHandle(
                handle: inFlightFenceHandle,
                message: "vkCreateFence returned success without a valid in-flight fence handle."
            );

            return new VulkanFrameSynchronization(
                deviceHandle: logicalDevice.Handle,
                frameSynchronizationApi: m_frameSynchronizationApi,
                imageAvailableSemaphoreHandle: imageAvailableSemaphoreHandle,
                inFlightFenceHandle: inFlightFenceHandle,
                renderFinishedSemaphoreHandles: renderFinishedSemaphoreHandles
            );
        } catch {
            Cleanup(
                deviceHandle: logicalDevice.Handle,
                imageAvailableSemaphoreHandle: imageAvailableSemaphoreHandle,
                inFlightFenceHandle: inFlightFenceHandle,
                renderFinishedSemaphoreHandles: renderFinishedSemaphoreHandles
            );
            throw;
        }
    }
}
