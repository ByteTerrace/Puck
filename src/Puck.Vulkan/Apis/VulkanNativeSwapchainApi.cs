using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanSwapchainApi"/>, marshaling to the
/// <c>vkCreateSwapchainKHR</c> and <c>vkDestroySwapchainKHR</c> entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeSwapchainApi : IVulkanSwapchainApi {
    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeSwapchainApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeSwapchainApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    private const uint IdentitySurfaceTransform = 0x00000001;
    private const uint TrueValue = 1;
    private const uint VkStructureTypeSwapchainCreateInfoKhr = 1000001000;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    /// <inheritdoc/>
    public VkResult CreateSwapchain(VulkanSwapchainCreateRequest request, out nint swapchainHandle) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.SurfaceHandle) {
            throw new ArgumentException(
                message: "Vulkan surface handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        var createSwapchain = GetPointers(deviceHandle: request.DeviceHandle).CreateSwapchainKhr;

        var queueIndicesBuffer = MarshalQueueFamilyIndices(queueFamilyIndices: request.QueueFamilyIndices);

        try {
            var createInfo = new VkSwapchainCreateInfoKhr {
                Clipped = TrueValue,
                CompositeAlpha = request.CompositeAlpha,
                ImageArrayLayers = 1,
                ImageColorSpace = request.ImageColorSpace,
                ImageExtent = new VkExtent2D(
                    height: request.ImageExtentHeight,
                    width: request.ImageExtentWidth
                ),
                ImageFormat = request.ImageFormat,
                ImageSharingMode = request.SharingMode,
                ImageUsage = request.ImageUsage,
                MinImageCount = request.ImageCount,
                OldSwapchain = 0,
                PQueueFamilyIndices = queueIndicesBuffer,
                PreTransform = ((0 == request.PreTransform)
                    ? IdentitySurfaceTransform
                    : request.PreTransform),
                PresentMode = request.PresentMode,
                QueueFamilyIndexCount = (uint)request.QueueFamilyIndices.Count,
                SType = VkStructureTypeSwapchainCreateInfoKhr,
                Surface = request.SurfaceHandle,
            };

            return createSwapchain(
                request.DeviceHandle,
                in createInfo,
                0,
                out swapchainHandle
            );
        } finally {
            if (0 != queueIndicesBuffer) {
                m_allocator.Free(ptr: queueIndicesBuffer);
            }
        }
    }
    /// <inheritdoc/>
    public void DestroySwapchain(nint deviceHandle, nint swapchainHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == swapchainHandle)
        ) {
            return;
        }

        var destroySwapchain = GetPointers(deviceHandle: deviceHandle).DestroySwapchainKhr;

        destroySwapchain(
            deviceHandle,
            swapchainHandle,
            0
        );
    }

    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkSwapchainCreateInfoKhr, nint, out nint, VkResult> CreateSwapchainKhr;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroySwapchainKhr;
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

        fixed (byte* pName = "vkCreateSwapchainKHR"u8) {
            pNew.CreateSwapchainKhr = (delegate* unmanaged[Cdecl]<nint, in VkSwapchainCreateInfoKhr, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroySwapchainKHR"u8) {
            pNew.DestroySwapchainKhr = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
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
    private unsafe nint MarshalQueueFamilyIndices(IReadOnlyList<uint> queueFamilyIndices) {
        if (0 == queueFamilyIndices.Count) {
            return 0;
        }

        var buffer = m_allocator.Alloc(size: (sizeof(uint) * queueFamilyIndices.Count));

        for (var index = 0; (index < queueFamilyIndices.Count); index++) {
            Marshal.WriteInt32(
                ofs: (index * sizeof(uint)),
                ptr: buffer,
                val: unchecked((int)queueFamilyIndices[index])
            );
        }

        return buffer;
    }
}
