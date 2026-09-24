using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interop;

/// <summary>
/// The command table of one Vulkan instance: every instance-level entry point Puck calls, including the
/// physical-device and surface queries, resolved once through <c>vkGetInstanceProcAddr</c> when the instance is
/// created. Core entry points are required and resolved eagerly; an extension entry point is <see langword="null"/>
/// when the instance was created without its extension, and every caller that can reach it without the extension
/// checks it first. The table lives exactly as long as its instance: <see cref="VulkanInstance"/> owns it.
/// </summary>
public sealed unsafe class VulkanInstanceCommands {
    /// <summary>The <c>vkCreateDebugUtilsMessengerEXT</c> entry point; <see langword="null"/> unless <c>VK_EXT_debug_utils</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkDebugUtilsMessengerCreateInfoExt, nint, out nint, VkResult> CreateDebugUtilsMessengerExt;
    /// <summary>The <c>vkCreateDevice</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkDeviceCreateInfo, nint, out nint, VkResult> CreateDevice;
    /// <summary>The <c>vkCreateViSurfaceNN</c> entry point; <see langword="null"/> unless <c>VK_NN_vi_surface</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkViSurfaceCreateInfoNn, nint, out nint, VkResult> CreateViSurfaceNn;
    /// <summary>The <c>vkCreateWaylandSurfaceKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_wayland_surface</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkWaylandSurfaceCreateInfoKhr, nint, out nint, VkResult> CreateWaylandSurfaceKhr;
    /// <summary>The <c>vkCreateWin32SurfaceKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_win32_surface</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkWin32SurfaceCreateInfoKhr, nint, out nint, VkResult> CreateWin32SurfaceKhr;
    /// <summary>The <c>vkCreateXcbSurfaceKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_xcb_surface</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, in VkXcbSurfaceCreateInfoKhr, nint, out nint, VkResult> CreateXcbSurfaceKhr;
    /// <summary>The <c>vkDestroyDebugUtilsMessengerEXT</c> entry point; <see langword="null"/> unless <c>VK_EXT_debug_utils</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyDebugUtilsMessengerExt;
    /// <summary>The <c>vkDestroyInstance</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void> DestroyInstance;
    /// <summary>The <c>vkDestroySurfaceKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_surface</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroySurfaceKhr;
    /// <summary>The <c>vkEnumerateDeviceExtensionProperties</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, nint, VkResult> EnumerateDeviceExtensionProperties;
    /// <summary>The <c>vkEnumeratePhysicalDevices</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, ref uint, nint, VkResult> EnumeratePhysicalDevices;
    /// <summary>The <c>vkGetPhysicalDeviceFeatures</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void> GetPhysicalDeviceFeatures;
    /// <summary>The <c>vkGetPhysicalDeviceFeatures2</c> entry point; core in Vulkan 1.1; <see langword="null"/> on an older instance.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void> GetPhysicalDeviceFeatures2;
    /// <summary>The <c>vkGetPhysicalDeviceMemoryProperties</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void> GetPhysicalDeviceMemoryProperties;
    /// <summary>The <c>vkGetPhysicalDeviceProperties</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void> GetPhysicalDeviceProperties;
    /// <summary>The <c>vkGetPhysicalDeviceProperties2</c> entry point; core in Vulkan 1.1; <see langword="null"/> on an older instance.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void> GetPhysicalDeviceProperties2;
    /// <summary>The <c>vkGetPhysicalDeviceQueueFamilyProperties</c> entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, ref uint, nint, void> GetPhysicalDeviceQueueFamilyProperties;
    /// <summary>The <c>vkGetPhysicalDeviceSurfaceCapabilitiesKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_surface</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, out VkSurfaceCapabilitiesKhr, VkResult> GetPhysicalDeviceSurfaceCapabilitiesKhr;
    /// <summary>The <c>vkGetPhysicalDeviceSurfaceFormatsKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_surface</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult> GetPhysicalDeviceSurfaceFormatsKhr;
    /// <summary>The <c>vkGetPhysicalDeviceSurfacePresentModesKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_surface</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult> GetPhysicalDeviceSurfacePresentModesKhr;
    /// <summary>The <c>vkGetPhysicalDeviceSurfaceSupportKHR</c> entry point; <see langword="null"/> unless <c>VK_KHR_surface</c> is enabled.</summary>
    public readonly delegate* unmanaged[Cdecl]<nint, uint, nint, out uint, VkResult> GetPhysicalDeviceSurfaceSupportKhr;

    /// <summary>Resolves every entry point of the instance identified by <paramref name="instanceHandle"/> through the
    /// loader.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle; must be non-zero and must outlive the table.</param>
    /// <exception cref="ArgumentException"><paramref name="instanceHandle"/> is zero.</exception>
    /// <exception cref="InvalidOperationException">The instance does not expose a required core entry point.</exception>
    public VulkanInstanceCommands(nint instanceHandle) : this(
        getInstanceProcAddr: VulkanProcResolver.LoaderInstanceProcAddr,
        instanceHandle: instanceHandle
    ) { }
    /// <summary>Resolves every entry point of the instance identified by <paramref name="instanceHandle"/> through
    /// <paramref name="getInstanceProcAddr"/>.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle; must be non-zero and must outlive the table.</param>
    /// <param name="getInstanceProcAddr">A <c>vkGetInstanceProcAddr</c>-shaped resolver:
    /// <see cref="VulkanProcResolver.LoaderInstanceProcAddr"/>, or one standing in for the driver.</param>
    /// <exception cref="ArgumentException"><paramref name="instanceHandle"/> is zero.</exception>
    /// <exception cref="InvalidOperationException">The instance does not expose a required core entry point.</exception>
    public VulkanInstanceCommands(nint instanceHandle, delegate* unmanaged[Cdecl]<nint, byte*, nint> getInstanceProcAddr) {
        VulkanArgument.RequireHandle(
            handle: instanceHandle,
            handleDescription: "instance",
            paramName: nameof(instanceHandle)
        );

        Handle = instanceHandle;
        CreateDebugUtilsMessengerExt = ((delegate* unmanaged[Cdecl]<nint, in VkDebugUtilsMessengerCreateInfoExt, nint, out nint, VkResult>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkCreateDebugUtilsMessengerEXT"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        CreateDevice = ((delegate* unmanaged[Cdecl]<nint, in VkDeviceCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveInstanceProc(
            functionName: "vkCreateDevice"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        CreateViSurfaceNn = ((delegate* unmanaged[Cdecl]<nint, in VkViSurfaceCreateInfoNn, nint, out nint, VkResult>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkCreateViSurfaceNN"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        CreateWaylandSurfaceKhr = ((delegate* unmanaged[Cdecl]<nint, in VkWaylandSurfaceCreateInfoKhr, nint, out nint, VkResult>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkCreateWaylandSurfaceKHR"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        CreateWin32SurfaceKhr = ((delegate* unmanaged[Cdecl]<nint, in VkWin32SurfaceCreateInfoKhr, nint, out nint, VkResult>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkCreateWin32SurfaceKHR"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        CreateXcbSurfaceKhr = ((delegate* unmanaged[Cdecl]<nint, in VkXcbSurfaceCreateInfoKhr, nint, out nint, VkResult>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkCreateXcbSurfaceKHR"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        DestroyDebugUtilsMessengerExt = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkDestroyDebugUtilsMessengerEXT"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        DestroyInstance = ((delegate* unmanaged[Cdecl]<nint, nint, void>)VulkanProcResolver.ResolveInstanceProc(
            functionName: "vkDestroyInstance"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        DestroySurfaceKhr = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkDestroySurfaceKHR"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        EnumerateDeviceExtensionProperties = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, VkResult>)VulkanProcResolver.ResolveInstanceProc(
            functionName: "vkEnumerateDeviceExtensionProperties"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        EnumeratePhysicalDevices = ((delegate* unmanaged[Cdecl]<nint, ref uint, nint, VkResult>)VulkanProcResolver.ResolveInstanceProc(
            functionName: "vkEnumeratePhysicalDevices"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        GetPhysicalDeviceFeatures = ((delegate* unmanaged[Cdecl]<nint, nint, void>)VulkanProcResolver.ResolveInstanceProc(
            functionName: "vkGetPhysicalDeviceFeatures"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        GetPhysicalDeviceFeatures2 = ((delegate* unmanaged[Cdecl]<nint, nint, void>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkGetPhysicalDeviceFeatures2"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        GetPhysicalDeviceMemoryProperties = ((delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void>)VulkanProcResolver.ResolveInstanceProc(
            functionName: "vkGetPhysicalDeviceMemoryProperties"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        GetPhysicalDeviceProperties = ((delegate* unmanaged[Cdecl]<nint, nint, void>)VulkanProcResolver.ResolveInstanceProc(
            functionName: "vkGetPhysicalDeviceProperties"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        GetPhysicalDeviceProperties2 = ((delegate* unmanaged[Cdecl]<nint, nint, void>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkGetPhysicalDeviceProperties2"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        GetPhysicalDeviceQueueFamilyProperties = ((delegate* unmanaged[Cdecl]<nint, ref uint, nint, void>)VulkanProcResolver.ResolveInstanceProc(
            functionName: "vkGetPhysicalDeviceQueueFamilyProperties"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        GetPhysicalDeviceSurfaceCapabilitiesKhr = ((delegate* unmanaged[Cdecl]<nint, nint, out VkSurfaceCapabilitiesKhr, VkResult>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkGetPhysicalDeviceSurfaceCapabilitiesKHR"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        GetPhysicalDeviceSurfaceFormatsKhr = ((delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkGetPhysicalDeviceSurfaceFormatsKHR"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        GetPhysicalDeviceSurfacePresentModesKhr = ((delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkGetPhysicalDeviceSurfacePresentModesKHR"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
        GetPhysicalDeviceSurfaceSupportKhr = ((delegate* unmanaged[Cdecl]<nint, uint, nint, out uint, VkResult>)VulkanProcResolver.ResolveOptionalInstanceProc(
            functionName: "vkGetPhysicalDeviceSurfaceSupportKHR"u8,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        ));
    }

    /// <summary>Gets the native <c>VkInstance</c> handle whose entry points the table holds.</summary>
    public nint Handle { get; }

    /// <summary>Destroys one object this instance owns through the object type's entry point, skipping a zero handle.
    /// This is the only zero-handle guard on instance-level destroys: <c>vkDestroySurfaceKHR</c> and
    /// <c>vkDestroyDebugUtilsMessengerEXT</c> share this shape, and their callers pass zero without checking.</summary>
    /// <param name="destroy">The entry point from this table: <see cref="DestroySurfaceKhr"/> or <see cref="DestroyDebugUtilsMessengerExt"/>.</param>
    /// <param name="handle">The native handle to destroy, or zero.</param>
    public void Destroy(delegate* unmanaged[Cdecl]<nint, nint, nint, void> destroy, nint handle) {
        if (0 != handle) {
            destroy(
                Handle,
                handle,
                0
            );
        }
    }
}
