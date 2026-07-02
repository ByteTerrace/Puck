using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.DirectX;
using Puck.DirectX.Interfaces;
using Puck.Hosting;
using Puck.Scene;
using Puck.SdfVm;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Demo;

/// <summary>
/// The REVERSE cross-backend present node: a Direct3D 12-hosted window showing VULKAN-produced content. It runs the
/// neutral compute SDF world (<see cref="WorldProducerNode"/>, SPIR-V kernels) on a bespoke Vulkan device LUID-matched
/// to the Direct3D 12 host adapter, writing into a <em>host-owned</em> shared storage image the Vulkan device imports
/// writable, then the Direct3D 12 host blits its own image zero-copy — no host-memory round trip. It is the mirror of
/// <see cref="CrossBackendComputeWorldNode"/> with ownership inverted: a D3D12 shared handle is the only NT handle both
/// backends open, so the HOST creates and owns the shared image (the producer imports it), and the emitted surface
/// carries NO shared handle — the host presents its own image, exactly like the same-device Direct3D 12 host path.
/// <para>
/// The synchronization is the forward path's coarse model, mirrored: the Vulkan producer drains its queue
/// (<c>FinalizeForExport</c>) before the host reads, and the host re-acquires the shared image into the
/// shader-readable state each frame (handing it back to the cross-API <c>COMMON</c> resting layout before the next
/// Vulkan write). No D3D12↔Vulkan shared timeline semaphore is needed.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class VulkanComputeWorldHostNode : IRenderNode {
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint VulkanFormat = 37; // VK_FORMAT_R8G8B8A8_UNORM

    private readonly NodeDescriptor m_descriptor = new(
        Name: "vulkan-compute-world-host",
        SurfaceId: SurfaceId.New()
    );
    private readonly VulkanComputeWorldDevice m_device;
    private readonly uint m_height;
    private readonly IGpuDeviceContext m_hostDevice;
    // Stateless Direct3D 12 host-side adapters used to record the per-frame cross-API layout transitions of the
    // host-owned shared image (resolved directly, like the forward showcase builds its bespoke services).
    private readonly IGpuComputeRecorder m_hostRecorder = new DirectXGpuComputeRecorder();
    private readonly IGpuQueueSubmitter m_hostSubmitter = new DirectXGpuQueueSubmitter();
    private readonly WorldProducerNode m_inner;
    private readonly uint m_width;
    private bool m_disposed;
    private IGpuComputeCommandPool? m_hostCommandPool;
    // The Direct3D 12 image is created in UNORDERED_ACCESS, which the neutral recorder maps from General.
    private GpuImageLayout m_lastHostLayout = GpuImageLayout.General;
    private ReverseSharedStorageImage? m_seam;

    /// <summary>Initializes a new instance of the <see cref="VulkanComputeWorldHostNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (resolves the Direct3D 12 host device and seeds the bespoke Vulkan producer).</param>
    /// <param name="frameSource">The data-driven scene/camera source to render.</param>
    /// <param name="withChild">Whether the bottom-right slot is a hosted <see cref="ChildSurfaceNode"/> instead of an SDF camera.</param>
    /// <param name="liveSources">The document's live-camera viewport slots (each becomes a <see cref="CameraChildNode"/>); null/empty for none.</param>
    /// <param name="capturePath">An optional PNG path; the inner producer reads its first rendered frame back from the bespoke Vulkan device and writes it there.</param>
    /// <param name="width">The render width in pixels (defaults to 960).</param>
    /// <param name="height">The render height in pixels (defaults to 600).</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceProvider"/> or <paramref name="frameSource"/> is <see langword="null"/>.</exception>
    public VulkanComputeWorldHostNode(IServiceProvider serviceProvider, ISdfFrameSource frameSource, bool withChild = false, IReadOnlyDictionary<int, LiveCameraSource>? liveSources = null, string? capturePath = null, uint width = 960, uint height = 600) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(frameSource);

        m_height = height;
        m_width = width;
        // The Direct3D 12 host device owns the swapchain AND the shared image; resolve it explicitly (the same device
        // the presenter blits from), so the host-owned image renders and presents on one device with a zero-copy import.
        m_hostDevice = (IGpuDeviceContext)serviceProvider.GetRequiredService<IDirectXDeviceContext>();
        m_device = new VulkanComputeWorldDevice(hostProvider: serviceProvider);
        m_inner = new WorldProducerNode(
            beamBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-beam.comp.spv")),
            cullArgsBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-cull-args.comp.spv")),
            capturePath: capturePath,
            children: WorldChildren.Build(cameraServices: serviceProvider, directX: false, gpuServices: m_device.Services, liveSources: liveSources, testChild: withChild),
            compositeBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-composite.comp.spv")),
            createStorageImage: CreateReverseSharedImage,
            frameSource: frameSource,
            height: height,
            serviceProvider: m_device.Services,
            viewsBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-views.comp.spv")),
            width: width
        );
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // Hand the host-owned shared image back to the cross-API resting state (COMMON) before the Vulkan producer
        // writes it (skipped on the very first frame, before the import exists — the image still rests in its
        // UNORDERED_ACCESS creation state, which the Vulkan write does not disturb).
        if (m_seam is not null) {
            RecordHostTransition(newLayout: GpuImageLayout.External);
        }

        // Redirect the inner node to the bespoke Vulkan device (SPIR-V kernels): it writes the import, leaves it in
        // GENERAL, and drains the Vulkan queue (FinalizeForExport) so the host reads completed pixels.
        _ = m_inner.ProduceFrame(context: context with { Host = m_device.Host });

        if (m_seam is null) {
            return default;
        }

        // Acquire the host image into the shader-readable state for the blit: the Direct3D 12 compositor samples it as
        // an SRV with no barrier of its own, so it must already rest there.
        RecordHostTransition(newLayout: GpuImageLayout.ShaderReadOnly);

        // The host owns the image — present it directly (no shared handle to import), exactly like the same-device
        // Direct3D 12 host path (scenario #2).
        return new Surface(
            ImageViewHandle: m_seam.HostImage.ImageViewHandle,
            Width: m_width,
            Height: m_height,
            Format: SurfaceFormat.R8G8B8A8Unorm
        );
    }

    // Invoked once by the inner producer (first frame) on the bespoke Vulkan device: the Direct3D 12 HOST creates the
    // exportable shared image and the Vulkan producer imports it as a WRITABLE storage image (handle type
    // D3D12_RESOURCE), exactly as the reverse-share gate does. The producer binds the Vulkan-import handles; the host
    // node blits the Direct3D 12 host image.
    private IGpuStorageImage CreateReverseSharedImage(IGpuDeviceContext vulkanDeviceContext) {
        var hostImage = new DirectXGpuSurfaceExportFactory().CreateExportableStorageImage(
            deviceContext: m_hostDevice,
            format: Format,
            height: m_height,
            width: m_width
        );
        var vkContext = (IVulkanDeviceContext)vulkanDeviceContext;
        var logicalDevice = vkContext.LogicalDevice;
        var externalMemoryApi = m_device.Services.GetRequiredService<IVulkanExternalMemoryApi>();
        var framebufferSetApi = m_device.Services.GetRequiredService<IVulkanFramebufferSetApi>();

        var imported = externalMemoryApi.ImportImage(request: new VulkanExternalImageImportRequest(
            DeviceHandle: logicalDevice.Handle,
            Format: VulkanFormat,
            Height: m_height,
            InstanceHandle: vkContext.Instance.Handle,
            PhysicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            SharedHandle: hostImage.SharedHandle,
            UsageFlags: VulkanImageUsageFlags.Storage | VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferSource,
            Width: m_width
        ));

        framebufferSetApi.CreateImageView(
            imageViewHandle: out var viewHandle,
            request: new VulkanImageViewCreateRequest(DeviceHandle: logicalDevice.Handle, Format: VulkanFormat, ImageHandle: imported.ImageHandle)
        ).ThrowIfFailed(operation: "vkCreateImageView");

        m_seam = new ReverseSharedStorageImage(
            externalMemoryApi: externalMemoryApi,
            framebufferSetApi: framebufferSetApi,
            hostImage: hostImage,
            importedImage: imported.ImageHandle,
            importedMemory: imported.MemoryHandle,
            importedView: viewHandle,
            logicalDevice: logicalDevice
        );

        return m_seam;
    }

    // Records and drains a single Direct3D 12 layout transition of the host-owned shared image on the host device's
    // queue, tracking the prior layout so the barrier's old layout is accurate frame to frame.
    private void RecordHostTransition(GpuImageLayout newLayout) {
        m_hostCommandPool ??= new DirectXGpuComputeCommandPoolFactory().Create(deviceContext: m_hostDevice);

        var commandBuffer = m_hostCommandPool.CommandBufferHandle;
        var deviceHandle = m_hostDevice.DeviceHandle;
        var acquiring = (newLayout == GpuImageLayout.ShaderReadOnly);
        var handingBack = (m_lastHostLayout == GpuImageLayout.ShaderReadOnly);

        m_hostRecorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);
        m_hostRecorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: acquiring ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None,
            destinationStageMask: acquiring ? GpuComputeStage.FragmentShader : GpuComputeStage.ComputeShader,
            deviceHandle: deviceHandle,
            imageHandle: m_seam!.HostImage.ImageHandle,
            newLayout: newLayout,
            oldLayout: m_lastHostLayout,
            sourceAccessMask: handingBack ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None,
            sourceStageMask: handingBack ? GpuComputeStage.FragmentShader : GpuComputeStage.ComputeShader
        );
        m_hostRecorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);
        m_hostSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: m_hostDevice);

        m_lastHostLayout = newLayout;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        // Drains the Vulkan device and disposes the seam (the Vulkan import + the Direct3D 12 host image).
        m_inner.Dispose();
        m_hostCommandPool?.Dispose();
        m_device.Dispose();
    }
}
