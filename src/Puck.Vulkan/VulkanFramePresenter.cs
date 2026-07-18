using System.Diagnostics;
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

    private const ulong PresentWaitTimeoutNanoseconds = 50_000_000UL; // 50 ms bound — a missed present can never hang the pump

    private readonly IVulkanFramePresentationApi m_framePresentationApi;
    private readonly IVulkanFrameSynchronizationApi m_frameSynchronizationApi;
    // Closed-loop present timing (VK_KHR_present_wait). All accessed only on the single pump thread that presents.
    private bool? m_presentWaitSupported;    // resolved per-device (re-resolved when m_presentWaitResolvedForDevice changes); null = not yet probed
    private nint m_presentWaitResolvedForDevice; // the device handle m_presentWaitSupported was resolved for; 0 = none yet
    private ulong m_nextPresentId = 1UL;     // monotonic per-swapchain; 0 is the "no id" sentinel
    private ulong m_priorPresentId;          // the id queued last frame; 0 = none yet
    private ulong m_priorPriorPresentId;     // the id queued TWO frames back, waited on this frame (the frame-ring mirror); 0 = none yet
    private nint m_priorSwapchainHandle;     // present ids are per-swapchain, so the counter resets when this changes
    private long m_lastPresentTimestamp;     // Stopwatch ticks of the last confirmed present; 0 = none
    private uint m_presentCount;             // monotonic confirmed-present count (the pacer's "new present" signal)

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

        // Closed-loop present timing (only when VK_KHR_present_wait is enabled): tag this present with a monotonic id;
        // a zero id leaves the present unchanged. Present ids are per-swapchain, so reset the counter when it changes.
        var deviceHandle = logicalDevice.Handle;

        // Present-wait support (and the present-API's per-device function pointers) are tied to the DEVICE, so re-resolve
        // when the device handle CHANGES — and drop the stale function-pointer cache for the prior device. No live path
        // recreates the device today (a device-lost ResetVulkanResources is produced but consumed nowhere), so this is
        // correct-by-construction hardening for a future where device-lost recovery yields a DIFFERENT device handle. A
        // self-disable below sets the flag false and, since the device handle is unchanged, it stays false for that
        // device's lifetime. RESIDUAL GAP (only matters once device-lost recovery is wired): this keys on the raw handle
        // VALUE, so a new device that happens to REUSE the prior handle value would not re-resolve; closing that needs a
        // device generation/epoch counter rather than the handle value (and a self-disable would then need to reset it).
        if (deviceHandle != m_presentWaitResolvedForDevice) {
            if (m_presentWaitResolvedForDevice != 0) {
                m_framePresentationApi.InvalidateDevice(deviceHandle: m_presentWaitResolvedForDevice);
            }

            m_presentWaitSupported = m_framePresentationApi.SupportsPresentWait(deviceHandle: deviceHandle);
            m_presentWaitResolvedForDevice = deviceHandle;
            m_priorPresentId = 0UL;
            m_priorPriorPresentId = 0UL;
            m_nextPresentId = 1UL;
        }

        if (swapchain.Handle != m_priorSwapchainHandle) {
            m_priorSwapchainHandle = swapchain.Handle;
            m_priorPresentId = 0UL;
            m_priorPriorPresentId = 0UL;
            m_nextPresentId = 1UL;
        }

        var presentId = ((m_presentWaitSupported == true) ? m_nextPresentId : 0UL);
        var presentRequest = new VulkanPresentRequest(
            DeviceHandle: deviceHandle,
            ImageIndex: imageIndex,
            PresentId: presentId,
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

        if (presentId != 0UL) {
            RecordPresentTiming(deviceHandle: deviceHandle, swapchainHandle: swapchain.Handle);
        }

        return VulkanFramePresentationOutcome.Presented(imageIndex: imageIndex);
    }

    /// <inheritdoc/>
    public bool TryGetPresentTiming(out uint presentCount, out long presentTimestampTicks) {
        presentCount = m_presentCount;
        presentTimestampTicks = m_lastPresentTimestamp;

        return (m_lastPresentTimestamp > 0L);
    }

    // Waits (bounded) for the present TWO frames back to be displayed and timestamps it, then advances the ids.
    // Two back — not one — is the presentation frame-ring's mirror: with two presents in flight, the prior present's
    // display typically lands only after the current frame's GPU work drains, so waiting on it re-serialized the pump
    // to two vblank periods per loop (the intro probe capped at 60 FPS under a 120 Hz target); the N−2 present has
    // already displayed by now in the steady state, so this wait returns ~immediately while still confirming real
    // display cadence for the pacer (one period staler — the pacer's re-anchor guard absorbs that). An unexpected
    // hard error disables further waits for the session (graceful → open-loop); a timeout/swapchain status code just
    // skips this one sample.
    private void RecordPresentTiming(nint deviceHandle, nint swapchainHandle) {
        if (m_priorPriorPresentId != 0UL) {
            var waitResult = m_framePresentationApi.WaitForPresent(
                deviceHandle: deviceHandle,
                presentId: m_priorPriorPresentId,
                swapchainHandle: swapchainHandle,
                timeoutNanoseconds: PresentWaitTimeoutNanoseconds
            );

            if (waitResult == VkResult.Success) {
                m_lastPresentTimestamp = Stopwatch.GetTimestamp();

                unchecked {
                    m_presentCount++;
                }
            } else if (
                !NeedsVulkanReset(result: waitResult) &&
                !NeedsPresentationResourceRecreate(result: waitResult) &&
                (waitResult != VkResult.Timeout)
            ) {
                // An unexpected hard error (the extension misbehaving, or a wiring bug surfacing on a present_wait-capable
                // driver): stop using present-wait for the session and surface the code so it can be debugged.
                m_presentWaitSupported = false;

                Console.Error.WriteLine(value: $"[present-timing] vkWaitForPresentKHR returned {waitResult}; disabling closed-loop present timing for this session (open-loop pacing).");
            }
        }

        m_priorPriorPresentId = m_priorPresentId;
        m_priorPresentId = m_nextPresentId;

        unchecked {
            m_nextPresentId++;

            if (m_nextPresentId == 0UL) {
                m_nextPresentId = 1UL; // never reuse the "no id" sentinel
            }
        }
    }
}
