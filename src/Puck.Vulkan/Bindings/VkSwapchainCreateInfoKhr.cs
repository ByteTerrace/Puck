using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a swapchain to be created with <c>vkCreateSwapchainKHR</c>: the target surface, the
/// image count, format, extent and usage, the present mode and transforms, and an optional swapchain to
/// retire.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkSwapchainCreateInfoKHR (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkSwapchainCreateInfoKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkSwapchainCreateFlagBitsKHR</c> specifying parameters of the swapchain.</summary>
    public uint Flags;
    /// <summary>The surface the swapchain presents to (a <c>VkSurfaceKHR</c> handle).</summary>
    public nint Surface;
    /// <summary>The minimum number of presentable images the swapchain must contain.</summary>
    public uint MinImageCount;
    /// <summary>The format of the swapchain images, as a <c>VkFormat</c> value.</summary>
    public uint ImageFormat;
    /// <summary>The color space of the swapchain images, as a <c>VkColorSpaceKHR</c> value.</summary>
    public uint ImageColorSpace;
    /// <summary>The width and height, in pixels, of the swapchain images.</summary>
    public VkExtent2D ImageExtent;
    /// <summary>The number of views in a multiview/stereo surface; <c>1</c> for non-stereoscopic surfaces.</summary>
    public uint ImageArrayLayers;
    /// <summary>A bitmask of <c>VkImageUsageFlagBits</c> describing the intended usage of the swapchain images.</summary>
    public uint ImageUsage;
    /// <summary>The sharing mode used when the images are accessed by multiple queue families, as a <c>VkSharingMode</c> value.</summary>
    public uint ImageSharingMode;
    /// <summary>The number of entries in the <see cref="PQueueFamilyIndices"/> array. Used only when <see cref="ImageSharingMode"/> is concurrent.</summary>
    public uint QueueFamilyIndexCount;
    /// <summary>A pointer to an array of queue family indices that access the images. Used only when <see cref="ImageSharingMode"/> is concurrent.</summary>
    public nint PQueueFamilyIndices;
    /// <summary>The transform applied to images before presentation, as a <c>VkSurfaceTransformFlagBitsKHR</c> value.</summary>
    public uint PreTransform;
    /// <summary>How the alpha component is composited with other surfaces, as a <c>VkCompositeAlphaFlagBitsKHR</c> value.</summary>
    public uint CompositeAlpha;
    /// <summary>The presentation mode the swapchain uses, as a <c>VkPresentModeKHR</c> value.</summary>
    public uint PresentMode;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> allows the implementation to discard rendering to obscured regions.</summary>
    public uint Clipped;
    /// <summary>An existing swapchain being replaced (a <c>VkSwapchainKHR</c> handle), or <see langword="null"/>.</summary>
    public nint OldSwapchain;
}
