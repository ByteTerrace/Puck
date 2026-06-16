using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The default <see cref="IVulkanFramePresenter"/>: it waits on the in-flight fence, acquires the next
/// swapchain image, invokes the caller's recording callback, submits, and presents, mapping swapchain
/// lifecycle codes to a <see cref="VulkanFramePresentationOutcome"/>. A suboptimal swapchain is still
/// rendered and presented so the image-available semaphore's pending signal is consumed before recreation.
/// </summary>
public sealed class VulkanFramePresenter : IVulkanFramePresenter {
    private const ulong FrameAcquireTimeoutNanoseconds = 0;
    private const ulong FrameFenceWaitTimeoutNanoseconds = 0;

    private static bool IsFrameUnavailable(VkResult result) {
        return (result is VkResult.NotReady
            or VkResult.Timeout);
    }
    private static bool NeedsPresentationResourceRecreate(VkResult result) {
        return (result is VkResult.ErrorOutOfDateKhr
            or VkResult.SuboptimalKhr);
    }
    private static bool NeedsVulkanReset(VkResult result) {
        return (result is VkResult.ErrorDeviceLost
            or VkResult.ErrorSurfaceLostKhr);
    }

    private readonly IVulkanFramePresentationApi m_framePresentationApi;
    private readonly IVulkanFrameSynchronizationApi m_frameSynchronizationApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanFramePresenter"/> class.</summary>
    /// <param name="framePresentationApi">The API used to acquire, submit, and present frames.</param>
    /// <param name="frameSynchronizationApi">The API used to wait on the in-flight fence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="framePresentationApi"/> or <paramref name="frameSynchronizationApi"/> is <see langword="null"/>.</exception>
    public VulkanFramePresenter(
        IVulkanFramePresentationApi framePresentationApi,
        IVulkanFrameSynchronizationApi frameSynchronizationApi
    ) {
        ArgumentNullException.ThrowIfNull(framePresentationApi);
        ArgumentNullException.ThrowIfNull(frameSynchronizationApi);

        m_framePresentationApi = framePresentationApi;
        m_frameSynchronizationApi = frameSynchronizationApi;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The acquired image index does not map to a recorded command buffer or a render-finished semaphore.</exception>
    /// <exception cref="VulkanException">A native command failed with a result that is neither a success nor a recognized swapchain lifecycle code.</exception>
    public VulkanFramePresentationOutcome Present(
        VulkanCommandResources commandResources,
        VulkanFrameSynchronization frameSynchronization,
        VulkanLogicalDevice logicalDevice,
        Action<uint> recordAcquiredImage,
        VulkanSwapchain swapchain
    ) {
        ArgumentNullException.ThrowIfNull(commandResources);
        ArgumentNullException.ThrowIfNull(frameSynchronization);
        ArgumentNullException.ThrowIfNull(logicalDevice);
        ArgumentNullException.ThrowIfNull(recordAcquiredImage);
        ArgumentNullException.ThrowIfNull(swapchain);

        var waitResult = m_frameSynchronizationApi.WaitForFence(
            deviceHandle: logicalDevice.Handle,
            fenceHandle: frameSynchronization.InFlightFenceHandle,
            timeout: FrameFenceWaitTimeoutNanoseconds
        );

        if (IsFrameUnavailable(result: waitResult)) {
            return VulkanFramePresentationOutcome.FromResult(result: VulkanFramePresentationResult.Skipped);
        }

        if (NeedsVulkanReset(result: waitResult)) {
            return VulkanFramePresentationOutcome.FromResult(result: VulkanFramePresentationResult.ResetVulkanResources);
        }

        if (NeedsPresentationResourceRecreate(result: waitResult)) {
            return VulkanFramePresentationOutcome.FromResult(result: VulkanFramePresentationResult.RecreatePresentationResources);
        }

        waitResult.ThrowIfFailed(operation: "vkWaitForFences");

        var acquireRequest = new VulkanFrameAcquireRequest(
            DeviceHandle: logicalDevice.Handle,
            ImageAvailableSemaphoreHandle: frameSynchronization.ImageAvailableSemaphoreHandle,
            InFlightFenceHandle: 0,
            SwapchainHandle: swapchain.Handle,
            TimeoutNanoseconds: FrameAcquireTimeoutNanoseconds
        );
        var acquireResult = m_framePresentationApi.AcquireNextImage(
            imageIndex: out var imageIndex,
            request: acquireRequest
        );

        if (IsFrameUnavailable(result: acquireResult)) {
            return VulkanFramePresentationOutcome.FromResult(result: VulkanFramePresentationResult.Skipped);
        }

        if (NeedsVulkanReset(result: acquireResult)) {
            return VulkanFramePresentationOutcome.FromResult(result: VulkanFramePresentationResult.ResetVulkanResources);
        }

        // VK_ERROR_OUT_OF_DATE_KHR acquires nothing and leaves no pending semaphore
        // signal, so the recreate path may destroy the sync objects immediately.
        // VK_SUBOPTIMAL_KHR is a SUCCESS code: the image IS acquired and the
        // image-available semaphore HAS a pending signal operation that only this
        // frame's submit can consume — abandoning here would let the recreate path
        // destroy the semaphore with the signal still pending
        // (VUID-vkDestroySemaphore-semaphore-01137). Render and present the suboptimal
        // frame instead; the present reports SUBOPTIMAL again and recreation routes
        // from the present outcome, after the submit has consumed the wait.
        if (acquireResult == VkResult.ErrorOutOfDateKhr) {
            return VulkanFramePresentationOutcome.FromResult(result: VulkanFramePresentationResult.RecreatePresentationResources);
        }

        acquireResult.ThrowIfFailed(operation: "vkAcquireNextImageKHR");

        if (imageIndex >= commandResources.CommandBufferHandles.Count) {
            throw new InvalidOperationException(message: "vkAcquireNextImageKHR returned an image index that does not map to a recorded command buffer.");
        }

        if (imageIndex >= frameSynchronization.RenderFinishedSemaphoreHandles.Count) {
            throw new InvalidOperationException(message: "vkAcquireNextImageKHR returned an image index that does not map to a render-finished semaphore.");
        }

        // The lazy-record seam: the fence wait above proved every prior submission
        // retired, so only NOW — knowing which image was acquired — does the acquired
        // image's command buffer get (re)recorded, and only that one. The fence reset
        // stays below so a recording failure leaves the fence signaled for the
        // recovery paths' teardown waits.
        recordAcquiredImage(imageIndex);

        var resetResult = m_frameSynchronizationApi.ResetFence(
            deviceHandle: logicalDevice.Handle,
            fenceHandle: frameSynchronization.InFlightFenceHandle
        );

        resetResult.ThrowIfFailed(operation: "vkResetFences");

        // The render-finished semaphore is the acquired IMAGE's: vkQueuePresentKHR's wait
        // on it is not fence-observable, so it may only be reused once the presentation
        // engine hands this image back through a future acquire.
        var renderFinishedSemaphoreHandle = frameSynchronization.RenderFinishedSemaphoreHandles[(int)imageIndex];
        var submitRequest = new VulkanFrameSubmitRequest(
            CommandBufferHandle: commandResources.CommandBufferHandles[(int)imageIndex],
            DeviceHandle: logicalDevice.Handle,
            FenceHandle: frameSynchronization.InFlightFenceHandle,
            GraphicsQueueHandle: logicalDevice.GraphicsQueue.Handle,
            ImageAvailableSemaphoreHandle: frameSynchronization.ImageAvailableSemaphoreHandle,
            RenderFinishedSemaphoreHandle: renderFinishedSemaphoreHandle
        );
        var submitResult = m_framePresentationApi.Submit(request: submitRequest);

        if (NeedsVulkanReset(result: submitResult)) {
            return VulkanFramePresentationOutcome.FromResult(result: VulkanFramePresentationResult.ResetVulkanResources);
        }

        if (NeedsPresentationResourceRecreate(result: submitResult)) {
            return VulkanFramePresentationOutcome.FromResult(result: VulkanFramePresentationResult.RecreatePresentationResources);
        }

        submitResult.ThrowIfFailed(operation: "vkQueueSubmit");

        var presentRequest = new VulkanPresentRequest(
            DeviceHandle: logicalDevice.Handle,
            ImageIndex: imageIndex,
            PresentQueueHandle: logicalDevice.PresentQueue.Handle,
            RenderFinishedSemaphoreHandle: renderFinishedSemaphoreHandle,
            SwapchainHandle: swapchain.Handle
        );
        var presentResult = m_framePresentationApi.Present(request: presentRequest);

        if (NeedsVulkanReset(result: presentResult)) {
            return VulkanFramePresentationOutcome.FromResult(result: VulkanFramePresentationResult.ResetVulkanResources);
        }

        if (NeedsPresentationResourceRecreate(result: presentResult)) {
            return VulkanFramePresentationOutcome.FromResult(result: VulkanFramePresentationResult.RecreatePresentationResources);
        }

        presentResult.ThrowIfFailed(operation: "vkQueuePresentKHR");
        return VulkanFramePresentationOutcome.Presented(imageIndex: imageIndex);
    }
}
