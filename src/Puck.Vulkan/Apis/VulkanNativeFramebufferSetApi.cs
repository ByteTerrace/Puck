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
    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeFramebufferSetApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeFramebufferSetApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    private const uint AspectColorBit = 0x00000001;
    private const uint ComponentSwizzleIdentity = 0;
    private const uint StructureTypeFramebufferCreateInfo = 37;
    private const uint StructureTypeImageViewCreateInfo = 15;
    private const uint TwoDimensionalImageViewType = 1;

    /// <inheritdoc/>
    public IReadOnlyList<nint> GetSwapchainImages(nint deviceHandle, nint swapchainHandle) {
        VulkanArgument.RequireHandle(
            handle: deviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(deviceHandle)
        );

        VulkanArgument.RequireHandle(
            handle: swapchainHandle,
            handleDescription: "swapchain",
            paramName: nameof(swapchainHandle)
        );

        var getSwapchainImages = GetPointers(deviceHandle: deviceHandle).GetSwapchainImagesKhr;

        var imageCount = 0U;
        var result = getSwapchainImages(
            deviceHandle,
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
                deviceHandle,
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
    /// <inheritdoc/>
    public VkResult CreateFramebuffer(VulkanFramebufferCreateRequest request, out nint framebufferHandle) {
        VulkanArgument.RequireHandle(
            handle: request.DeviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(request)
        );

        var createFramebuffer = GetPointers(deviceHandle: request.DeviceHandle).CreateFramebuffer;
        var attachmentsPointer = m_allocator.Alloc(size: IntPtr.Size);

        try {
            Marshal.WriteIntPtr(
                ptr: attachmentsPointer,
                val: request.ImageViewHandle
            );
            // SINGLE attachment, SINGLE layer: the request carries one image view, matching the swapchain's single
            // color target. The render-pass API accepts multiple attachments, so if a multi-attachment pass (e.g. color
            // + depth, or MRT) is ever paired with this framebuffer, the request must take an array of image views and
            // AttachmentCount must equal the pass's attachment count — a mismatch creates an invalid framebuffer.
            var createInfo = new VkFramebufferCreateInfo {
                AttachmentCount = 1,
                Height = request.Height,
                Layers = 1,
                PAttachments = attachmentsPointer,
                RenderPass = request.RenderPassHandle,
                SType = StructureTypeFramebufferCreateInfo,
                Width = request.Width,
            };

            return createFramebuffer(
                request.DeviceHandle,
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
        VulkanArgument.RequireHandle(
            handle: request.DeviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(request)
        );

        var createImageView = GetPointers(deviceHandle: request.DeviceHandle).CreateImageView;
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
                AspectMask = AspectColorBit,
                BaseArrayLayer = 0,
                BaseMipLevel = 0,
                LayerCount = 1,
                LevelCount = 1,
            },
            ViewType = TwoDimensionalImageViewType,
        };

        return createImageView(
            request.DeviceHandle,
            in createInfo,
            0,
            out imageViewHandle
        );
    }
    /// <inheritdoc/>
    public void DestroyFramebuffer(nint deviceHandle, nint framebufferHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == framebufferHandle)
        ) {
            return;
        }

        var destroyFramebuffer = GetPointers(deviceHandle: deviceHandle).DestroyFramebuffer;

        destroyFramebuffer(
            deviceHandle,
            framebufferHandle,
            0
        );
    }
    /// <inheritdoc/>
    public void DestroyImageView(nint deviceHandle, nint imageViewHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == imageViewHandle)
        ) {
            return;
        }

        var destroyImageView = GetPointers(deviceHandle: deviceHandle).DestroyImageView;

        destroyImageView(
            deviceHandle,
            imageViewHandle,
            0
        );
    }

    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkFramebufferCreateInfo, nint, out nint, VkResult> CreateFramebuffer;
        public delegate* unmanaged[Cdecl]<nint, in VkImageViewCreateInfo, nint, out nint, VkResult> CreateImageView;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyFramebuffer;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyImageView;
        public delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult> GetSwapchainImagesKhr;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();

    private DevicePointers GetPointers(nint deviceHandle) {
        return m_pointers.GetOrAdd(
            key: deviceHandle,
            valueFactory: static handle => new DevicePointers {
                CreateFramebuffer = (delegate* unmanaged[Cdecl]<nint, in VkFramebufferCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCreateFramebuffer"u8),
                CreateImageView = (delegate* unmanaged[Cdecl]<nint, in VkImageViewCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCreateImageView"u8),
                DestroyFramebuffer = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDestroyFramebuffer"u8),
                DestroyImageView = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDestroyImageView"u8),
                GetSwapchainImagesKhr = (delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkGetSwapchainImagesKHR"u8),
            }
        );
    }
}
