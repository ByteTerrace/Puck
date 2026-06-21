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

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    /// <inheritdoc/>
    public IReadOnlyList<nint> GetSwapchainImages(nint deviceHandle, nint swapchainHandle) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == swapchainHandle) {
            throw new ArgumentException(
                message: "Vulkan swapchain handle must be non-zero.",
                paramName: nameof(swapchainHandle)
            );
        }

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
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

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
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

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

    private unsafe DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkCreateFramebuffer"u8) {
            pNew.CreateFramebuffer = (delegate* unmanaged[Cdecl]<nint, in VkFramebufferCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCreateImageView"u8) {
            pNew.CreateImageView = (delegate* unmanaged[Cdecl]<nint, in VkImageViewCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyFramebuffer"u8) {
            pNew.DestroyFramebuffer = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyImageView"u8) {
            pNew.DestroyImageView = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetSwapchainImagesKHR"u8) {
            pNew.GetSwapchainImagesKhr = (delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        m_pointers[deviceHandle] = pNew;
        return pNew;
    }
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (m_syncRoot) {
            if (m_getDeviceProcAddr is not null) {
                return m_getDeviceProcAddr;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");

            m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getDeviceProcAddr;
        }
    }
}
