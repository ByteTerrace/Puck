using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// An <see cref="IGpuExportableRenderTarget"/> for Vulkan: an offscreen color target whose backing image lives in
/// exportable, dedicated device memory. Beyond the normal <see cref="VulkanViewTarget"/> handles, it exposes an
/// opaque Win32 NT handle (<see cref="SharedHandle"/>) another Vulkan instance imports to sample the result
/// zero-copy. (Unlike a Direct3D 12 shared texture, an opaque-Vulkan handle is not importable by Direct3D 12, so
/// this is a Vulkan-to-Vulkan capability.)
/// </summary>
public sealed partial class VulkanGpuExportableRenderTarget : IGpuExportableRenderTarget, IVulkanRenderTarget {
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly VulkanCommandResources m_commandResources;
    private readonly nint m_deviceHandle;
    private readonly IVulkanExternalMemoryApi m_externalMemoryApi;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly nint m_framebufferHandle;
    private readonly VkQueue m_graphicsQueue;
    private readonly nint m_imageHandle;
    private readonly nint m_imageViewHandle;
    private readonly nint m_memoryHandle;
    private readonly VulkanQueueSubmitter m_queueSubmitter;
    private readonly VulkanRenderPass m_renderPass;
    private bool m_disposed;
    private bool m_finalized;
    private nint m_sharedHandle;

    /// <summary>Initializes a new instance of the <see cref="VulkanGpuExportableRenderTarget"/> class, creating the
    /// exportable image, its view, render pass, framebuffer, and command resources.</summary>
    /// <param name="logicalDevice">The Vulkan logical device the resources are created on.</param>
    /// <param name="instance">The Vulkan instance, used to resolve memory support.</param>
    /// <param name="format">The color format, as a <c>VkFormat</c> value.</param>
    /// <param name="width">The target width, in pixels.</param>
    /// <param name="height">The target height, in pixels.</param>
    /// <param name="externalMemoryApi">The API that creates the exportable image and retrieves its shared handle.</param>
    /// <param name="renderPassApi">The API that creates the render pass.</param>
    /// <param name="framebufferSetApi">The API that creates the image view and framebuffer.</param>
    /// <param name="commandResourcesFactory">The factory that allocates the command pool and buffers.</param>
    /// <param name="commandBufferRecordingApi">The API used to record the handoff layout transition in <see cref="FinalizeForExport"/>.</param>
    /// <param name="queueSubmitter">The submitter used to submit and drain the handoff transition in <see cref="FinalizeForExport"/>.</param>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public VulkanGpuExportableRenderTarget(
        VulkanLogicalDevice logicalDevice,
        VulkanInstance instance,
        uint format,
        uint width,
        uint height,
        IVulkanExternalMemoryApi externalMemoryApi,
        IVulkanRenderPassApi renderPassApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        VulkanQueueSubmitter queueSubmitter
    ) {
        ArgumentNullException.ThrowIfNull(logicalDevice);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(externalMemoryApi);
        ArgumentNullException.ThrowIfNull(renderPassApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);
        ArgumentNullException.ThrowIfNull(queueSubmitter);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "Exportable render target dimensions must be non-zero.");
        }

        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_deviceHandle = logicalDevice.Handle;
        m_externalMemoryApi = externalMemoryApi;
        m_framebufferSetApi = framebufferSetApi;
        m_graphicsQueue = logicalDevice.GraphicsQueue;
        m_queueSubmitter = queueSubmitter;
        Width = width;
        Height = height;

        var image = externalMemoryApi.CreateExportableImage(request: new VulkanExternalImageExportRequest(
            DeviceHandle: m_deviceHandle,
            Format: format,
            Height: height,
            InstanceHandle: instance.Handle,
            PhysicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            UsageFlags: VulkanImageUsageFlags.ColorAttachment | VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferSource,
            Width: width
        ));

        m_imageHandle = image.ImageHandle;
        m_memoryHandle = image.MemoryHandle;
        m_sharedHandle = image.SharedHandle;

        framebufferSetApi.CreateImageView(
            imageViewHandle: out m_imageViewHandle,
            request: new VulkanImageViewCreateRequest(
                DeviceHandle: m_deviceHandle,
                Format: format,
                ImageHandle: m_imageHandle
            )
        ).ThrowIfFailed(operation: "vkCreateImageView");

        renderPassApi.CreateRenderPass(
            renderPassHandle: out var renderPassHandle,
            request: VulkanRenderPassRequests.Sampled(
                colorFormat: format,
                deviceHandle: m_deviceHandle,
                preserveExistingContents: false
            )
        ).ThrowIfFailed(operation: "vkCreateRenderPass");
        m_renderPass = new VulkanRenderPass(
            deviceHandle: m_deviceHandle,
            renderPassApi: renderPassApi,
            renderPassHandle: renderPassHandle
        );

        framebufferSetApi.CreateFramebuffer(
            framebufferHandle: out m_framebufferHandle,
            request: new VulkanFramebufferCreateRequest(
                DeviceHandle: m_deviceHandle,
                Height: height,
                ImageViewHandle: m_imageViewHandle,
                RenderPassHandle: renderPassHandle,
                Width: width
            )
        ).ThrowIfFailed(operation: "vkCreateFramebuffer");

        // Two command buffers: index 0 is the compose pass the producer records into (CommandBufferHandle); index 1
        // is the dedicated finalize buffer FinalizeForExport records the handoff layout transition into.
        m_commandResources = commandResourcesFactory.Create(
            commandBufferCount: 2,
            logicalDevice: logicalDevice
        );
    }

    /// <inheritdoc/>
    public nint CommandBufferHandle => m_commandResources.CommandBufferHandles[0];
    /// <inheritdoc/>
    public nint FramebufferHandle => m_framebufferHandle;
    /// <inheritdoc/>
    public uint Height { get; }
    /// <inheritdoc/>
    public nint ImageHandle => m_imageHandle;
    /// <inheritdoc/>
    public nint ImageViewHandle => m_imageViewHandle;
    /// <inheritdoc/>
    public VulkanRenderPass RenderPass => m_renderPass;
    /// <inheritdoc/>
    public nint RenderPassHandle => m_renderPass.Handle;
    /// <inheritdoc/>
    public nint SharedHandle => m_sharedHandle;
    /// <inheritdoc/>
    public uint Width { get; }

    /// <inheritdoc/>
    public void FinalizeForExport() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        // The producer's Sampled render pass leaves the image in SHADER_READ_ONLY_OPTIMAL; transition it to GENERAL
        // — the cross-Vulkan handoff layout an importer re-transitions from — and drain the queue so the importing
        // instance samples completed writes. Idempotent, mirroring the DirectX "only if not already in the handoff
        // state" guard.
        if (m_finalized) {
            return;
        }

        var finalizeCommandBuffer = m_commandResources.CommandBufferHandles[1];

        m_commandBufferRecordingApi.BeginCommandBuffer(
            commandBufferHandle: finalizeCommandBuffer,
            deviceHandle: m_deviceHandle
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        m_commandBufferRecordingApi.TransitionImageLayout(
            baseMipLevel: 0,
            commandBufferHandle: finalizeCommandBuffer,
            destinationAccessMask: 0,
            destinationStageMask: VulkanPipelineStageFlags.BottomOfPipe,
            deviceHandle: m_deviceHandle,
            imageHandle: m_imageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.General,
            oldLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
            sourceAccessMask: VulkanAccessFlags.ColorAttachmentWrite | VulkanAccessFlags.ShaderRead,
            sourceStageMask: VulkanPipelineStageFlags.ColorAttachmentOutput | VulkanPipelineStageFlags.FragmentShader
        );
        m_commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: finalizeCommandBuffer,
            deviceHandle: m_deviceHandle
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");

        m_queueSubmitter.SubmitAndWait(
            commandBufferHandles: [finalizeCommandBuffer],
            deviceHandle: m_deviceHandle,
            graphicsQueue: m_graphicsQueue
        );
        m_finalized = true;
    }

    /// <summary>Releases the framebuffer, render pass, image view, exportable image and memory, and closes the
    /// exported shared handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_commandResources.Dispose();

        if (m_framebufferHandle != 0) {
            m_framebufferSetApi.DestroyFramebuffer(
                deviceHandle: m_deviceHandle,
                framebufferHandle: m_framebufferHandle
            );
        }

        m_renderPass.Dispose();

        if (m_imageViewHandle != 0) {
            m_framebufferSetApi.DestroyImageView(
                deviceHandle: m_deviceHandle,
                imageViewHandle: m_imageViewHandle
            );
        }

        m_externalMemoryApi.DestroyImage(
            deviceHandle: m_deviceHandle,
            imageHandle: m_imageHandle,
            memoryHandle: m_memoryHandle
        );

        if (m_sharedHandle != 0) {
            _ = CloseHandle(handle: m_sharedHandle);
            m_sharedHandle = 0;
        }
    }

    // The handle from vkGetMemoryWin32HandleKHR (OPAQUE_WIN32) is a fresh NT handle this target owns and closes.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
