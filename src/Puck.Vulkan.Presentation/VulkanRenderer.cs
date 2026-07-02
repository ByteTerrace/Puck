using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.Assets;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Presentation;

/// <summary>
/// A generic, window-bound frame orchestrator: it boots the Vulkan instance/surface/device for a native
/// window and owns the swapchain lifecycle (swapchain, render pass, framebuffers, command buffers and
/// synchronization), recreating them on resize. It knows nothing about <em>what</em> is drawn — callers
/// build their own pipelines against <see cref="RenderPass"/>/<see cref="Swapchain"/> (rebuilding them
/// when <see cref="PresentationResourcesRecreated"/> fires) and hand <see cref="Present"/> the draw
/// commands plus the pipelines they reference. Single-thread affine: create and drive it on the window's
/// pump thread.
/// </summary>
public sealed class VulkanRenderer(
    VulkanRendererOptions options,
    PresentationOptions presentationOptions,
    IVulkanInstanceFactory instanceFactory,
    IVulkanSurfaceFactory surfaceFactory,
    IVulkanPhysicalDeviceSelector physicalDeviceSelector,
    IVulkanLogicalDeviceFactory logicalDeviceFactory,
    IVulkanSwapchainSupportApi swapchainSupportApi,
    IVulkanSwapchainFactory swapchainFactory,
    IVulkanRenderPassFactory renderPassFactory,
    IVulkanFramebufferSetFactory framebufferSetFactory,
    IVulkanCommandResourcesFactory commandResourcesFactory,
    IVulkanFrameSynchronizationFactory frameSynchronizationFactory,
    IVulkanFramePresenter framePresenter,
    IVulkanCommandBufferRecorder commandBufferRecorder
) : IDisposable, IVulkanDeviceContext, IGpuDeviceContext {
    private VulkanCommandResources? m_commandResources;
    private VulkanLogicalDevice? m_device;
    private VulkanFramebufferSet? m_framebufferSet;
    private uint m_height;
    private VulkanInstance? m_instance;
    private bool m_needsRecreate;
    private VkPhysicalDevice m_physicalDevice;
    private VulkanRenderPass? m_renderPass;
    private VulkanSurface? m_surface;
    private VulkanSwapchain? m_swapchain;
    private VulkanFrameSynchronization? m_synchronization;
    private uint m_width;

    /// <summary>Raised after the swapchain-dependent resources are (re)created — on the first frame and
    /// after every resize. Callers rebuild any pipelines or descriptor sets bound to
    /// <see cref="RenderPass"/>/<see cref="Swapchain"/> here.</summary>
    public event Action? PresentationResourcesRecreated;

    /// <summary>The Vulkan instance, valid after <see cref="Initialize"/>.</summary>
    public VulkanInstance Instance => (m_instance ?? throw new InvalidOperationException(message: "The renderer must be initialized before its instance is used."));

    /// <summary>The logical device, valid after <see cref="Initialize"/>.</summary>
    public VulkanLogicalDevice Device => (m_device ?? throw new InvalidOperationException(message: "The renderer must be initialized before its device is used."));

    /// <summary>The logical device as the shared device-context view (alias of <see cref="Device"/>).</summary>
    public VulkanLogicalDevice LogicalDevice => Device;

    /// <summary>The selected physical device; valid after <see cref="Initialize"/>.</summary>
    public VkPhysicalDevice PhysicalDevice => m_physicalDevice;

    /// <summary>The window surface; valid after <see cref="Initialize"/>.</summary>
    public VulkanSurface Surface => (m_surface ?? throw new InvalidOperationException(message: "The renderer must be initialized before its surface is used."));

    /// <summary>The current render pass; valid after the first <see cref="BeginFrame"/> and replaced on
    /// resize (see <see cref="PresentationResourcesRecreated"/>).</summary>
    public VulkanRenderPass RenderPass => (m_renderPass ?? throw new InvalidOperationException(message: "Presentation resources are not available until the first BeginFrame."));

    /// <summary>The current swapchain; valid after the first <see cref="BeginFrame"/> and replaced on
    /// resize (see <see cref="PresentationResourcesRecreated"/>).</summary>
    public VulkanSwapchain Swapchain => (m_swapchain ?? throw new InvalidOperationException(message: "Presentation resources are not available until the first BeginFrame."));

    /// <summary>Boots the Vulkan instance, surface, and device for a native surface binding at the given
    /// initial size. Presentation resources are created lazily on the first <see cref="BeginFrame"/>.</summary>
    /// <param name="binding">The native surface binding to present into.</param>
    /// <param name="width">The initial render-target width in pixels.</param>
    /// <param name="height">The initial render-target height in pixels.</param>
    public void Initialize(NativeSurfaceBinding binding, uint width, uint height) {
        if (!binding.HasSurfacePayload) {
            throw new InvalidOperationException(message: $"The native surface binding carries no payload for display kind '{binding.DisplayKind}'.");
        }

        m_height = height;
        m_width = width;

        // A REACTIVATION (e.g. the backend switch returning to Vulkan): the instance + device deliberately survive
        // ReleasePresentation — the renderer IS the published device-context capability and the parent of every node
        // resource — so only the window surface is recreated; the swapchain chain follows lazily on the next
        // BeginFrame. The same window backs the new surface, so the original present-support selection still holds.
        if ((m_instance is not null) && (m_device is not null)) {
            m_surface?.Dispose();
            m_surface = surfaceFactory.Create(
                binding: binding,
                instanceHandle: m_instance.Handle
            );
            m_needsRecreate = true;

            return;
        }

        try {
            m_instance = instanceFactory.Create(
                applicationName: options.ApplicationName,
                displayKind: binding.DisplayKind,
                enableValidation: true
            );
            m_surface = surfaceFactory.Create(
                binding: binding,
                instanceHandle: m_instance.Handle
            );
            m_physicalDevice = physicalDeviceSelector.Select(
                instance: m_instance,
                surface: m_surface
            );
            m_device = logicalDeviceFactory.Create(
                instance: m_instance,
                physicalDevice: m_physicalDevice
            );
        } catch {
            m_device?.Dispose();
            m_device = null;
            m_surface?.Dispose();
            m_surface = null;
            m_instance?.Dispose();
            m_instance = null;
            throw;
        }
    }

    /// <summary>Prepares the next frame: (re)creates presentation resources when this is the first frame, the
    /// target size changed, or the last present reported the swapchain out of date.</summary>
    /// <param name="width">The current render-target width in pixels.</param>
    /// <param name="height">The current render-target height in pixels.</param>
    public void BeginFrame(uint width, uint height) {
        if (
            (m_device is null) ||
            (width == 0) ||
            (height == 0)
        ) {
            return;
        }

        if (
            (m_swapchain is null) ||
            m_needsRecreate ||
            (width != m_width) ||
            (height != m_height)
        ) {
            m_height = height;
            m_width = width;

            DisposePresentationResources();
            EnsurePresentationResources(
                height: height,
                width: width
            );
            m_needsRecreate = false;
        }
    }

    /// <summary>Records and presents one frame from caller-supplied draw commands and the pipelines they
    /// reference. A no-op until the first successful <see cref="BeginFrame"/>.</summary>
    public void Present(
        IReadOnlyList<VulkanDrawCommand> drawCommands,
        IReadOnlyDictionary<AssetContentHash, VulkanGraphicsPipeline> graphicsPipelines
    ) {
        ArgumentNullException.ThrowIfNull(drawCommands);
        ArgumentNullException.ThrowIfNull(graphicsPipelines);

        if (
            (m_device is null) ||
            (m_swapchain is null)
        ) {
            return;
        }

        var outcome = framePresenter.Present(
            commandResources: m_commandResources!,
            frameSynchronization: m_synchronization!,
            logicalDevice: m_device,
            recordAcquiredImage: imageIndex => commandBufferRecorder.RecordImage(
                commandResources: m_commandResources!,
                drawCommands: drawCommands,
                framebufferSet: m_framebufferSet!,
                graphicsPipelines: graphicsPipelines,
                imageIndex: (int)imageIndex,
                renderPass: m_renderPass!,
                swapchain: m_swapchain
            ),
            swapchain: m_swapchain
        );

        if (outcome.Result == VulkanFramePresentationResult.RecreatePresentationResources) {
            m_needsRecreate = true;
        } else if (outcome.Result == VulkanFramePresentationResult.ResetVulkanResources) {
            // Device/surface lost — surface it as the neutral recoverable signal for the host pump (this outcome was
            // produced but consumed nowhere before; it is now the device-loss recovery trigger).
            throw new DeviceLostException(message: "Vulkan present reported a lost device or surface.");
        }
    }
    public void WaitForGpuIdle() {
        if (m_device is null) {
            return;
        }

        m_device.WaitIdle();
    }
    /// <summary>Recreates the lost chain IN PLACE — keeping this renderer's object identity so the published
    /// device-context capability and every node that resolved it stay valid (they release + rebuild their own resources).
    /// Recreates the whole boot chain — INSTANCE, surface, physical-device selection, logical device — and (lazily, via
    /// <see cref="m_needsRecreate"/>) the presentation resources. The instance is deliberately rebuilt, NOT kept: a real
    /// adapter removal leaves the startup instance enumerating a device that no longer exists, and vkCreateDevice against
    /// that stale physical device native-crashes in some ICDs. A fresh instance created after the removal enumerates only
    /// the adapters actually present, so while the GPU is absent NO physical device is selectable and vkCreateDevice is
    /// never reached — the selection failure is surfaced as a neutral <see cref="DeviceLostException"/> for the host pump
    /// to wait on and retry (each retry rebuilds a fresh instance). Called on the pump thread during device-loss recovery.</summary>
    public void RecreateDevice(NativeSurfaceBinding binding, uint width, uint height) {
        m_height = height;
        m_width = width;

        // Tear the lost chain down completely — presentation resources, device, surface, AND the instance — so the
        // rebuild below re-enumerates adapters from scratch (TryWaitIdle inside DisposePresentationResources swallows the
        // device-lost a drain would raise).
        DisposePresentationResources();
        m_device?.Dispose();
        m_device = null;
        m_surface?.Dispose();
        m_surface = null;
        m_instance?.Dispose();
        m_instance = null;

        // Rebuild the instance, surface, physical-device selection, and logical device. While the adapter is still absent
        // the fresh instance enumerates no suitable physical device, so Select fails BEFORE vkCreateDevice is reached
        // (avoiding the ICD crash). Surface any failure here as the neutral DeviceLostException so the host pump treats it
        // as "device not back yet" and waits/retries; tear the partial chain back down so the next retry starts clean.
        try {
            m_instance = instanceFactory.Create(
                applicationName: options.ApplicationName,
                displayKind: binding.DisplayKind,
                enableValidation: true
            );
            m_surface = surfaceFactory.Create(
                binding: binding,
                instanceHandle: m_instance.Handle
            );
            m_physicalDevice = physicalDeviceSelector.Select(
                instance: m_instance,
                surface: m_surface
            );
            m_device = logicalDeviceFactory.Create(
                instance: m_instance,
                physicalDevice: m_physicalDevice
            );
        } catch (DeviceLostException) {
            throw;
        } catch (Exception exception) {
            m_device?.Dispose();
            m_device = null;
            m_surface?.Dispose();
            m_surface = null;
            m_instance?.Dispose();
            m_instance = null;

            throw new DeviceLostException(message: "The Vulkan device could not be recreated yet (the adapter is unavailable).", innerException: exception);
        }

        // The swapchain-dependent resources rebuild on the next BeginFrame, which also fires
        // PresentationResourcesRecreated so the compositor rebuilds its (now device-changed) blit resources.
        m_needsRecreate = true;
    }
    /// <summary>Releases the window-bound presentation stack — the swapchain-dependent resources plus the surface —
    /// while KEEPING the instance and logical device alive (the deactivation half of <see cref="Initialize"/>'s reuse
    /// contract). The renderer is the published device-context capability and every node resource is a child of its
    /// device, so a presenter deactivation (a backend switch away from Vulkan) must not destroy the device under
    /// them — that is a use-after-free at their eventual release. Full device teardown belongs to <see cref="Dispose"/>
    /// alone (the renderer is a container-owned singleton, disposed at host shutdown after the node tree).</summary>
    public void ReleasePresentation() {
        DisposePresentationResources();
        m_surface?.Dispose();
        m_surface = null;
    }

    // A wait-for-idle that tolerates an already-lost device: vkDeviceWaitIdle returns VK_ERROR_DEVICE_LOST on a lost
    // device (surfaced as DeviceLostException), which during teardown means "nothing left to drain" — swallow it.
    private void TryWaitIdle() {
        if (m_device is null) {
            return;
        }

        try {
            m_device.WaitIdle();
        } catch (DeviceLostException) {
            // Device already lost; there is no in-flight work to wait on.
        }
    }
    /// <summary>Forwards the closed-loop present-timing sample (from VK_KHR_present_wait) for the host pacer to phase-lock to.</summary>
    /// <param name="presentCount">When this returns <see langword="true"/>, the monotonic confirmed-present count.</param>
    /// <param name="presentTimestampTicks">When this returns <see langword="true"/>, the present's timestamp in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    /// <returns><see langword="true"/> when a usable sample exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetPresentTiming(out uint presentCount, out long presentTimestampTicks) =>
        framePresenter.TryGetPresentTiming(out presentCount, out presentTimestampTicks);

    nint IGpuDeviceContext.DeviceHandle => LogicalDevice.Handle;

    void IGpuDeviceContext.WaitIdle() => WaitForGpuIdle();

    private void EnsurePresentationResources(uint width, uint height) {
        var device = m_device!;
        var supportDetails = swapchainSupportApi.Query(
            instance: m_instance!,
            physicalDevice: m_physicalDevice,
            surface: m_surface!
        );

        if (!supportDetails.IsComplete) {
            throw new InvalidOperationException(message: "The selected Vulkan device does not support presenting to the window surface.");
        }

        // Map the neutral presentation preferences to Vulkan: the present mode to a VkPresentModeKHR, and the
        // surface format to whichever supported (format, color-space) pair matches the desired VkFormat. Both are
        // passed as preferences — the factory falls back (mailbox/immediate/FIFO; formats[0]) when unsupported.
        var preferredPresentMode = presentationOptions.PresentMode switch {
            PresentMode.Vsync => (uint?)VulkanPresentMode.Fifo,
            PresentMode.Mailbox => VulkanPresentMode.Mailbox,
            PresentMode.Immediate => VulkanPresentMode.Immediate,
            PresentMode.Adaptive => VulkanPresentMode.FifoRelaxed,
            _ => null,
        };
        var desiredVkFormat = presentationOptions.SurfaceFormat switch {
            SurfaceFormat.R8G8B8A8Unorm => (uint?)VulkanFormat.R8G8B8A8Unorm,
            SurfaceFormat.B8G8R8A8Unorm => VulkanFormat.B8G8R8A8Unorm,
            _ => null,
        };
        VulkanSurfaceFormat? preferredSurfaceFormat = null;

        if (desiredVkFormat is uint vkFormat) {
            foreach (var format in supportDetails.SurfaceFormats) {
                if (format.Format == vkFormat) {
                    preferredSurfaceFormat = format;

                    break;
                }
            }
        }

        m_swapchain = swapchainFactory.Create(
            desiredHeight: height,
            desiredWidth: width,
            logicalDevice: device,
            preferredPresentMode: preferredPresentMode,
            preferredSurfaceFormat: preferredSurfaceFormat,
            supportDetails: supportDetails,
            surface: m_surface!
        );
        m_renderPass = renderPassFactory.Create(
            logicalDevice: device,
            swapchain: m_swapchain
        );
        m_framebufferSet = framebufferSetFactory.Create(
            logicalDevice: device,
            renderPass: m_renderPass,
            swapchain: m_swapchain
        );
        m_commandResources = commandResourcesFactory.Create(
            commandBufferCount: (uint)m_framebufferSet.FramebufferHandles.Count,
            logicalDevice: device
        );
        m_synchronization = frameSynchronizationFactory.Create(
            logicalDevice: device,
            renderFinishedSemaphoreCount: m_commandResources.CommandBufferHandles.Count
        );

        PresentationResourcesRecreated?.Invoke();
    }
    private void DisposePresentationResources() {
        TryWaitIdle();

        m_synchronization?.Dispose();
        m_synchronization = null;
        m_commandResources?.Dispose();
        m_commandResources = null;
        m_framebufferSet?.Dispose();
        m_framebufferSet = null;
        m_renderPass?.Dispose();
        m_renderPass = null;
        m_swapchain?.Dispose();
        m_swapchain = null;
    }

    public void Dispose() {
        DisposePresentationResources();
        m_device?.Dispose();
        m_device = null;
        m_surface?.Dispose();
        m_surface = null;
        m_instance?.Dispose();
        m_instance = null;
    }
}
