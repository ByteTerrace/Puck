using Puck.Assets;
using Puck.Platform;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

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
) : IDisposable {
    private VulkanCommandResources? m_commandResources;
    private VulkanLogicalDevice? m_device;
    private bool m_disposed;
    private VulkanFramebufferSet? m_framebufferSet;
    private VulkanInstance? m_instance;
    private ulong m_lastResizeCount;
    private bool m_needsRecreate;
    private VkPhysicalDevice m_physicalDevice;
    private VulkanRenderPass? m_renderPass;
    private VulkanSurface? m_surface;
    private VulkanSwapchain? m_swapchain;
    private VulkanFrameSynchronization? m_synchronization;
    private INativeWindow? m_window;

    /// <summary>Raised after the swapchain-dependent resources are (re)created — on the first frame and
    /// after every resize. Callers rebuild any pipelines or descriptor sets bound to
    /// <see cref="RenderPass"/>/<see cref="Swapchain"/> here.</summary>
    public event Action? PresentationResourcesRecreated;

    /// <summary>The current render-target height in pixels (0 before initialization).</summary>
    public uint ViewportHeight => (m_window?.Height ?? 0u);

    /// <summary>The current render-target width in pixels (0 before initialization).</summary>
    public uint ViewportWidth => (m_window?.Width ?? 0u);

    /// <summary>The Vulkan instance, valid after <see cref="Initialize"/>.</summary>
    public VulkanInstance Instance => (m_instance ?? throw new InvalidOperationException(message: "The renderer must be initialized before its instance is used."));

    /// <summary>The logical device, valid after <see cref="Initialize"/>.</summary>
    public VulkanLogicalDevice Device => (m_device ?? throw new InvalidOperationException(message: "The renderer must be initialized before its device is used."));

    /// <summary>The current render pass; valid after the first <see cref="BeginFrame"/> and replaced on
    /// resize (see <see cref="PresentationResourcesRecreated"/>).</summary>
    public VulkanRenderPass RenderPass => (m_renderPass ?? throw new InvalidOperationException(message: "Presentation resources are not available until the first BeginFrame."));

    /// <summary>The current swapchain; valid after the first <see cref="BeginFrame"/> and replaced on
    /// resize (see <see cref="PresentationResourcesRecreated"/>).</summary>
    public VulkanSwapchain Swapchain => (m_swapchain ?? throw new InvalidOperationException(message: "Presentation resources are not available until the first BeginFrame."));

    public void Initialize(INativeWindow window) {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (window is not INativeSurfaceSourceProvider surfaceSourceProvider) {
            throw new InvalidOperationException(message: "The Vulkan renderer requires a window that can provide a native surface binding.");
        }

        m_window = window;

        var binding = surfaceSourceProvider.CreateSurfaceBinding();

        if (!binding.HasSurfacePayload) {
            throw new InvalidOperationException(message: $"The native window did not provide a surface payload for display kind '{binding.DisplayKind}'.");
        }

        m_instance = instanceFactory.Create(
            applicationName: options.ApplicationName,
            displayKind: binding.DisplayKind,
            enableValidation: false
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
    }

    /// <summary>Prepares the next frame: (re)creates presentation resources when this is the first frame,
    /// the window was resized, or the last present reported the swapchain out of date.</summary>
    public void BeginFrame() {
        if (
            m_disposed ||
            (m_device is null) ||
            (m_window is null)
        ) {
            return;
        }

        var width = m_window.Width;
        var height = m_window.Height;

        if (
            (width == 0) ||
            (height == 0)
        ) {
            return;
        }

        if (
            (m_swapchain is null) ||
            m_needsRecreate ||
            (m_window.ResizeCount != m_lastResizeCount)
        ) {
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
            m_disposed ||
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
        }
    }

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

        m_swapchain = swapchainFactory.Create(
            desiredHeight: height,
            desiredWidth: width,
            logicalDevice: device,
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
        m_lastResizeCount = m_window!.ResizeCount;

        PresentationResourcesRecreated?.Invoke();
    }
    private void DisposePresentationResources() {
        m_device?.WaitIdle();

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
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        DisposePresentationResources();
        m_device?.Dispose();
        m_surface?.Dispose();
        m_instance?.Dispose();
    }
}
