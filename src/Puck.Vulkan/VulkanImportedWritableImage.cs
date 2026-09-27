using System.Diagnostics.CodeAnalysis;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// A texture another device allocated in shared memory (a Direct3D 12 simultaneous-access texture), imported into a
/// Vulkan device as an image this device writes and hands on to a reader on a third device (a Direct3D 11 probe): the
/// memory is imported with the usages the writer declares (<c>VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D12_RESOURCE_BIT</c>), and
/// the shared fence the reader waits on is imported as a timeline semaphore (<see cref="VulkanSharedFence"/>), which
/// <see cref="CompleteWrite"/> signals behind every submission made to the queue before it. The texture's and the fence's
/// handles stay the caller's, who keeps both alive past this image. Owns the imported image, its memory, its view and
/// the semaphore, released on disposal; the owner drains the queue first.
/// </summary>
public sealed class VulkanImportedWritableImage : IGpuExportableImage {
    private readonly IVulkanExternalMemoryApi m_externalMemoryApi;
    private readonly VulkanSharedFence m_fence;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly VulkanLogicalDevice m_logicalDevice;
    private readonly VulkanQueueSubmitter m_queueSubmitter;

    private bool m_disposed;
    private nint m_imageHandle;
    private nint m_imageViewHandle;
    private nint m_memoryHandle;
    private ulong m_nextValue = 1UL;

    private VulkanImportedWritableImage(IVulkanExternalMemoryApi externalMemoryApi, IVulkanFramebufferSetApi framebufferSetApi, VulkanQueueSubmitter queueSubmitter, VulkanLogicalDevice logicalDevice, VulkanSharedFence fence, nint imageHandle, nint memoryHandle, nint imageViewHandle, nint sharedHandle, nint sharedFenceHandle, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) {
        m_externalMemoryApi = externalMemoryApi;
        m_fence = fence;
        m_framebufferSetApi = framebufferSetApi;
        m_imageHandle = imageHandle;
        m_imageViewHandle = imageViewHandle;
        m_logicalDevice = logicalDevice;
        m_memoryHandle = memoryHandle;
        m_queueSubmitter = queueSubmitter;
        Format = format;
        Height = height;
        SharedFenceHandle = sharedFenceHandle;
        SharedHandle = sharedHandle;
        Usage = usage;
        Width = width;
    }

    /// <inheritdoc/>
    public GpuPixelFormat Format { get; }
    /// <inheritdoc/>
    public uint Height { get; }
    /// <inheritdoc/>
    public nint ImageHandle => m_imageHandle;
    /// <inheritdoc/>
    public nint ImageViewHandle => m_imageViewHandle;
    /// <inheritdoc/>
    /// <remarks>The imported fence's handle, which the caller owns.</remarks>
    public nint SharedFenceHandle { get; }
    /// <inheritdoc/>
    /// <remarks>The imported texture's handle, which the caller owns.</remarks>
    public nint SharedHandle { get; }
    /// <inheritdoc/>
    public GpuImageUsage Usage { get; }
    /// <inheritdoc/>
    public uint Width { get; }

    /// <summary>Imports a shared texture and the shared fence that orders its writes into a device.</summary>
    /// <param name="deviceContext">The Vulkan device context whose device writes the image.</param>
    /// <param name="externalMemoryApi">The API that imports and destroys the texture's memory.</param>
    /// <param name="framebufferSetApi">The API that creates and destroys the image's view.</param>
    /// <param name="queueSubmitter">The queue submission service <see cref="CompleteWrite"/> signals through.</param>
    /// <param name="sharedHandle">The texture's shared NT handle; stays owned by the caller.</param>
    /// <param name="sharedFenceHandle">The shared fence's NT handle, which only this device signals; stays owned by the
    /// caller.</param>
    /// <param name="format">The texture's format; a color format.</param>
    /// <param name="width">The texture's width, in pixels.</param>
    /// <param name="height">The texture's height, in pixels.</param>
    /// <param name="usage">The usages the device's work puts the image to.</param>
    /// <param name="image">When this returns <see langword="true"/>, the imported image, owned by the caller.</param>
    /// <param name="refusal">When this returns <see langword="false"/>, why the device cannot import it; empty
    /// otherwise.</param>
    /// <returns>Whether the texture and the fence were imported.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    public static bool TryImport(IVulkanDeviceContext deviceContext, IVulkanExternalMemoryApi externalMemoryApi, IVulkanFramebufferSetApi framebufferSetApi, VulkanQueueSubmitter queueSubmitter, nint sharedHandle, nint sharedFenceHandle, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, [NotNullWhen(true)] out VulkanImportedWritableImage? image, out string refusal) {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(externalMemoryApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(queueSubmitter);

        image = null;

        var device = deviceContext.LogicalDevice;

        if (!VulkanSharedFence.TryImport(
            device: device.Commands,
            fence: out var fence,
            refusal: out refusal,
            sharedHandle: sharedFenceHandle
        )) {
            return false;
        }

        var vkFormat = VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format);
        var imageHandle = ((nint)0);
        var memoryHandle = ((nint)0);

        try {
            var imported = externalMemoryApi.ImportImage(request: new VulkanExternalImageImportRequest(
                Device: device.Commands,
                Format: vkFormat,
                Height: height,
                Instance: deviceContext.Instance.Commands,
                PhysicalDeviceHandle: device.PhysicalDevice.Handle,
                SharedHandle: sharedHandle,
                UsageFlags: VulkanGpuFormats.ToVkImageUsage(usage: usage),
                Width: width
            ));

            imageHandle = imported.ImageHandle;
            memoryHandle = imported.MemoryHandle;
            framebufferSetApi.CreateImageView(
                imageViewHandle: out var imageViewHandle,
                request: new VulkanImageViewCreateRequest(
                    Device: device.Commands,
                    Format: vkFormat,
                    ImageHandle: imageHandle
                )
            ).ThrowIfFailed(operation: "vkCreateImageView");

            image = new VulkanImportedWritableImage(
                externalMemoryApi: externalMemoryApi,
                fence: fence,
                format: format,
                framebufferSetApi: framebufferSetApi,
                height: height,
                imageHandle: imageHandle,
                imageViewHandle: imageViewHandle,
                logicalDevice: device,
                memoryHandle: memoryHandle,
                queueSubmitter: queueSubmitter,
                sharedFenceHandle: sharedFenceHandle,
                sharedHandle: sharedHandle,
                usage: usage,
                width: width
            );
            refusal = "";

            return true;
        } catch (Exception exception) when ((exception is not DeviceLostException)) {
            refusal = $"the shared texture did not import as a writable image: {exception.Message}";

            return false;
        } finally {
            // Whatever a refused or lost import made is released here; a completed import hands it all to the image.
            if (image is null) {
                if (0 != imageHandle) {
                    externalMemoryApi.DestroyImage(
                        device: device.Commands,
                        imageHandle: imageHandle,
                        memoryHandle: memoryHandle
                    );
                }

                fence.Dispose();
            }
        }
    }
    /// <inheritdoc/>
    /// <remarks>Queues a batch with no command buffers that sets the imported timeline semaphore to the next
    /// value.</remarks>
    public ulong CompleteWrite() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var value = m_nextValue;

        m_queueSubmitter.Signal(
            device: m_logicalDevice.Commands,
            graphicsQueue: m_logicalDevice.GraphicsQueue,
            semaphore: m_fence.SemaphoreHandle,
            value: value
        );
        m_nextValue++;

        return value;
    }
    /// <summary>Releases the view, the imported image and its memory, and the imported semaphore. Safe to call more than
    /// once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_framebufferSetApi.DestroyImageView(
            device: m_logicalDevice.Commands,
            imageViewHandle: m_imageViewHandle
        );
        m_imageViewHandle = 0;
        m_externalMemoryApi.DestroyImage(
            device: m_logicalDevice.Commands,
            imageHandle: m_imageHandle,
            memoryHandle: m_memoryHandle
        );
        m_imageHandle = 0;
        m_memoryHandle = 0;
        m_fence.Dispose();
    }
}
