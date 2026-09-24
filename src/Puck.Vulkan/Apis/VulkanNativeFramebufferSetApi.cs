using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanFramebufferSetApi"/>, marshaling to the image-view,
/// framebuffer, and swapchain-image entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeFramebufferSetApi : IVulkanFramebufferSetApi {
    private const uint ComponentSwizzleIdentity = 0;
    private const uint StructureTypeFramebufferCreateInfo = 37;
    private const uint StructureTypeImageViewCreateInfo = 15;
    private const uint TwoDimensionalImageViewType = 1;

    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeFramebufferSetApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeFramebufferSetApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    /// <inheritdoc/>
    public VkResult CreateFramebuffer(VulkanFramebufferCreateRequest request, out nint framebufferHandle) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        var views = request.ImageViewHandles;

        if (
            (views is null) ||
            (views.Count == 0)
        ) {
            throw new ArgumentException(
                message: "A framebuffer binds at least one image view.",
                paramName: nameof(request)
            );
        }

        var createFramebuffer = request.Device.CreateFramebuffer;
        var attachmentsPointer = m_allocator.Alloc(size: (IntPtr.Size * views.Count));

        try {
            for (var index = 0; (index < views.Count); index++) {
                Marshal.WriteIntPtr(
                    ofs: (index * IntPtr.Size),
                    ptr: attachmentsPointer,
                    val: views[index]
                );
            }

            // One layer; one view per attachment of the render pass, in its order.
            var createInfo = new VkFramebufferCreateInfo {
                AttachmentCount = ((uint)views.Count),
                Height = request.Height,
                Layers = 1,
                PAttachments = attachmentsPointer,
                RenderPass = request.RenderPassHandle,
                SType = StructureTypeFramebufferCreateInfo,
                Width = request.Width,
            };

            return createFramebuffer(
                request.Device.Handle,
                in createInfo,
                0,
                out framebufferHandle
            );
        } finally {
            m_allocator.Free(ptr: attachmentsPointer);
        }
    }
    /// <inheritdoc/>
    public VkResult CreateImageView(VulkanImageViewCreateRequest request, out nint imageViewHandle) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        var createImageView = request.Device.CreateImageView;
        var createInfo = new VkImageViewCreateInfo {
            Components = new VkComponentMapping {
                A = ComponentSwizzleIdentity,
                B = ComponentSwizzleIdentity,
                G = ComponentSwizzleIdentity,
                R = ComponentSwizzleIdentity,
            },
            Format = request.Format,
            Image = request.ImageHandle,
            SType = StructureTypeImageViewCreateInfo,
            SubresourceRange = new VkImageSubresourceRange {
                AspectMask = request.AspectMask,
                BaseArrayLayer = 0,
                BaseMipLevel = 0,
                LayerCount = 1,
                LevelCount = 1,
            },
            ViewType = TwoDimensionalImageViewType,
        };

        return createImageView(
            request.Device.Handle,
            in createInfo,
            0,
            out imageViewHandle
        );
    }
    /// <inheritdoc/>
    public void DestroyFramebuffer(VulkanDeviceCommands device, nint framebufferHandle) =>
        device?.Destroy(
            destroy: device.DestroyFramebuffer,
            handle: framebufferHandle
        );
    /// <inheritdoc/>
    public void DestroyImageView(VulkanDeviceCommands device, nint imageViewHandle) =>
        device?.Destroy(
            destroy: device.DestroyImageView,
            handle: imageViewHandle
        );
    /// <inheritdoc/>
    public IReadOnlyList<nint> GetSwapchainImages(VulkanDeviceCommands device, nint swapchainHandle) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: swapchainHandle,
            handleDescription: "swapchain",
            paramName: nameof(swapchainHandle)
        );

        var getSwapchainImages = device.GetSwapchainImagesKhr;

        var imageCount = 0U;
        var result = getSwapchainImages(
            device.Handle,
            swapchainHandle,
            ref imageCount,
            0
        );

        result.ThrowIfFailed(operation: "vkGetSwapchainImagesKHR");

        if (0 == imageCount) {
            return [];
        }

        var imageBuffer = m_allocator.Alloc(size: (IntPtr.Size * checked((int)imageCount)));

        try {
            result = getSwapchainImages(
                device.Handle,
                swapchainHandle,
                ref imageCount,
                imageBuffer
            );
            result.ThrowIfFailed(operation: "vkGetSwapchainImagesKHR");

            var imageHandles = new nint[imageCount];

            for (var index = 0; (index < imageHandles.Length); index++) {
                imageHandles[index] = Marshal.ReadIntPtr(
                    ofs: (index * IntPtr.Size),
                    ptr: imageBuffer
                );
            }

            return imageHandles;
        } finally {
            m_allocator.Free(ptr: imageBuffer);
        }
    }
}
