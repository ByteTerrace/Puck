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
    private const uint IdentitySurfaceTransform = 0x00000001;
    private const uint TrueValue = 1;
    private const uint VkStructureTypeSwapchainCreateInfoKhr = 1000001000;

    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeSwapchainApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeSwapchainApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    // A swapchain handle exists only once this check has passed on its device, so the other VK_KHR_swapchain entry
    // points (acquire, present, image enumeration, destroy) are reached only when they are resolved and call straight
    // through the table.
    private static unsafe void RequireSwapchainCommands(VulkanDeviceCommands device) {
        if (device.AcquireNextImageKhr is null) {
            throw VulkanProcResolver.MissingDeviceProc(functionName: "vkAcquireNextImageKHR"u8);
        }

        if (device.CreateSwapchainKhr is null) {
            throw VulkanProcResolver.MissingDeviceProc(functionName: "vkCreateSwapchainKHR"u8);
        }

        if (device.DestroySwapchainKhr is null) {
            throw VulkanProcResolver.MissingDeviceProc(functionName: "vkDestroySwapchainKHR"u8);
        }

        if (device.GetSwapchainImagesKhr is null) {
            throw VulkanProcResolver.MissingDeviceProc(functionName: "vkGetSwapchainImagesKHR"u8);
        }

        if (device.QueuePresentKhr is null) {
            throw VulkanProcResolver.MissingDeviceProc(functionName: "vkQueuePresentKHR"u8);
        }
    }

    /// <inheritdoc/>
    public VkResult CreateSwapchain(VulkanSwapchainCreateRequest request, out nint swapchainHandle) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.SurfaceHandle,
            handleDescription: "surface",
            paramName: nameof(request)
        );

        RequireSwapchainCommands(device: request.Device);

        var createSwapchain = request.Device.CreateSwapchainKhr;

        var queueIndicesBuffer = VulkanMarshalHelpers.AllocateArray(
            allocator: m_allocator,
            values: request.QueueFamilyIndices
        );

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
                QueueFamilyIndexCount = ((uint)request.QueueFamilyIndices.Count),
                SType = VkStructureTypeSwapchainCreateInfoKhr,
                Surface = request.SurfaceHandle,
            };

            return createSwapchain(
                request.Device.Handle,
                in createInfo,
                0,
                out swapchainHandle
            );
        } finally {
            m_allocator.Free(ptr: queueIndicesBuffer);
        }
    }
    /// <inheritdoc/>
    public void DestroySwapchain(VulkanDeviceCommands device, nint swapchainHandle) =>
        device?.Destroy(
            destroy: device.DestroySwapchainKhr,
            handle: swapchainHandle
        );
}
