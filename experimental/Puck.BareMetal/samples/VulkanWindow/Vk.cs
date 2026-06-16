// Puck.BareMetal VulkanWindow — Vulkan bring-up on puck.
//
// vulkan-1.dll is loaded dynamically (LoadLibrary + vkGetInstanceProcAddr), then every
// entry point is a delegate* unmanaged<> resolved through vkGetInstanceProcAddr /
// vkGetDeviceProcAddr — no import lib, no marshalling. The Vk* structs are Puck.Vulkan's
// blittable bindings (compiled as source). Dispatchable handles are nint; non-dispatchable
// handles (surface, swapchain, ...) are 64-bit (ulong).
using System;
using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;

namespace Puck.BareMetal.VulkanWindow;

internal static unsafe partial class Vk
{
    private const uint VK_STRUCTURE_TYPE_APPLICATION_INFO = 0;
    private const uint VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1;
    private const uint VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO = 2;
    private const uint VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO = 3;
    private const uint VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR = 1000009000;
    private const uint VK_QUEUE_GRAPHICS_BIT = 0x1;
    private const uint VK_API_VERSION_1_0 = 1u << 22;

    public static nint Instance;
    public static ulong Surface;
    public static nint PhysicalDevice;
    public static nint Device;
    public static nint Queue;
    public static uint QueueFamily;

    private static nint s_getInstanceProcAddr;
    private static nint s_getDeviceProcAddr;

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern nint LoadLibraryA(byte* name);

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern nint GetProcAddress(nint module, byte* name);

    private static void Ascii(byte* destination, string text)
    {
        int i = 0;
        for (; i < text.Length; i++)
            destination[i] = (byte)text[i];
        destination[i] = 0;
    }

    private static nint InstanceProc(nint instance, string name)
    {
        byte* n = stackalloc byte[64];
        Ascii(n, name);
        return ((delegate* unmanaged<nint, byte*, nint>)s_getInstanceProcAddr)(instance, n);
    }

    private static nint DeviceProc(string name)
    {
        byte* n = stackalloc byte[64];
        Ascii(n, name);
        return ((delegate* unmanaged<nint, byte*, nint>)s_getDeviceProcAddr)(Device, n);
    }

    public static bool Initialize(nint windowHandle)
    {
        // Load the Vulkan loader.
        byte* dllName = stackalloc byte[16];
        Ascii(dllName, "vulkan-1.dll");
        nint vulkan = LoadLibraryA(dllName);
        if (vulkan == 0) { Diag.Log("vulkan-1.dll not found"); return false; }

        byte* gipaName = stackalloc byte[32];
        Ascii(gipaName, "vkGetInstanceProcAddr");
        s_getInstanceProcAddr = GetProcAddress(vulkan, gipaName);
        if (s_getInstanceProcAddr == 0) { Diag.Log("vkGetInstanceProcAddr missing"); return false; }

        // Instance (with the surface extensions a window needs).
        VkApplicationInfo appInfo = default;
        appInfo.StructureType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
        appInfo.ApiVersion = VK_API_VERSION_1_0;

        byte* extSurface = stackalloc byte[32];
        Ascii(extSurface, "VK_KHR_surface");
        byte* extWin32 = stackalloc byte[32];
        Ascii(extWin32, "VK_KHR_win32_surface");
        byte** instanceExtensions = stackalloc byte*[2];
        instanceExtensions[0] = extSurface;
        instanceExtensions[1] = extWin32;

        VkInstanceCreateInfo instanceInfo = default;
        instanceInfo.StructureType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
        instanceInfo.ApplicationInfo = (nint)(&appInfo);
        instanceInfo.EnabledExtensionCount = 2;
        instanceInfo.EnabledExtensionNames = (nint)instanceExtensions;

        var vkCreateInstance = (delegate* unmanaged<VkInstanceCreateInfo*, nint, nint*, int>)InstanceProc(0, "vkCreateInstance");
        nint instance;
        int result = vkCreateInstance(&instanceInfo, 0, &instance);
        if (result != 0) { Diag.LogNum("vkCreateInstance failed", result); return false; }
        Instance = instance;
        Diag.Log("vulkan instance created");

        // Win32 surface on our window.
        var vkCreateWin32Surface = (delegate* unmanaged<nint, VkWin32SurfaceCreateInfoKhr*, nint, ulong*, int>)InstanceProc(instance, "vkCreateWin32SurfaceKHR");
        VkWin32SurfaceCreateInfoKhr surfaceInfo = default;
        surfaceInfo.StructureType = VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR;
        surfaceInfo.InstanceHandle = Win32.GetModuleHandleW(null);
        surfaceInfo.WindowHandle = windowHandle;
        ulong surface;
        result = vkCreateWin32Surface(instance, &surfaceInfo, 0, &surface);
        if (result != 0) { Diag.LogNum("vkCreateWin32SurfaceKHR failed", result); return false; }
        Surface = surface;
        Diag.Log("win32 surface created");

        // Pick a physical device with a graphics + present queue family.
        var vkEnumeratePhysicalDevices = (delegate* unmanaged<nint, uint*, nint*, int>)InstanceProc(instance, "vkEnumeratePhysicalDevices");
        var vkGetQueueFamilyProps = (delegate* unmanaged<nint, uint*, VkQueueFamilyProperties*, void>)InstanceProc(instance, "vkGetPhysicalDeviceQueueFamilyProperties");
        var vkGetSurfaceSupport = (delegate* unmanaged<nint, uint, ulong, uint*, int>)InstanceProc(instance, "vkGetPhysicalDeviceSurfaceSupportKHR");

        uint deviceCount = 0;
        vkEnumeratePhysicalDevices(instance, &deviceCount, null);
        if (deviceCount == 0) { Diag.Log("no Vulkan physical devices"); return false; }
        if (deviceCount > 16) deviceCount = 16;
        Diag.LogNum("physical devices:", deviceCount);
        nint* devices = stackalloc nint[16];
        vkEnumeratePhysicalDevices(instance, &deviceCount, devices);

        VkQueueFamilyProperties* families = stackalloc VkQueueFamilyProperties[32];
        bool found = false;
        for (uint d = 0; d < deviceCount && !found; d++)
        {
            uint familyCount = 0;
            vkGetQueueFamilyProps(devices[d], &familyCount, null);
            if (familyCount > 32) familyCount = 32;
            vkGetQueueFamilyProps(devices[d], &familyCount, families);

            for (uint q = 0; q < familyCount; q++)
            {
                bool graphics = (families[q].QueueFlags & VK_QUEUE_GRAPHICS_BIT) != 0;
                uint presentSupported = 0;
                vkGetSurfaceSupport(devices[d], q, surface, &presentSupported);
                if (graphics && presentSupported != 0)
                {
                    PhysicalDevice = devices[d];
                    QueueFamily = q;
                    found = true;
                    break;
                }
            }
        }
        if (!found) { Diag.Log("no graphics+present queue family"); return false; }
        Diag.LogNum("chosen queue family:", QueueFamily);

        // Logical device + queue (enable the swapchain extension for the next stage).
        float queuePriority = 1.0f;
        VkDeviceQueueCreateInfo queueInfo = default;
        queueInfo.SType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
        queueInfo.QueueFamilyIndex = QueueFamily;
        queueInfo.QueueCount = 1;
        queueInfo.PQueuePriorities = (nint)(&queuePriority);

        byte* extSwapchain = stackalloc byte[32];
        Ascii(extSwapchain, "VK_KHR_swapchain");
        byte** deviceExtensions = stackalloc byte*[1];
        deviceExtensions[0] = extSwapchain;

        VkDeviceCreateInfo deviceInfo = default;
        deviceInfo.SType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
        deviceInfo.QueueCreateInfoCount = 1;
        deviceInfo.PQueueCreateInfos = (nint)(&queueInfo);
        deviceInfo.EnabledExtensionCount = 1;
        deviceInfo.PpEnabledExtensionNames = (nint)deviceExtensions;

        var vkCreateDevice = (delegate* unmanaged<nint, VkDeviceCreateInfo*, nint, nint*, int>)InstanceProc(instance, "vkCreateDevice");
        nint device;
        result = vkCreateDevice(PhysicalDevice, &deviceInfo, 0, &device);
        if (result != 0) { Diag.LogNum("vkCreateDevice failed", result); return false; }
        Device = device;

        s_getDeviceProcAddr = InstanceProc(instance, "vkGetDeviceProcAddr");
        var vkGetDeviceQueue = (delegate* unmanaged<nint, uint, uint, nint*, void>)DeviceProc("vkGetDeviceQueue");
        nint queue;
        vkGetDeviceQueue(device, QueueFamily, 0, &queue);
        Queue = queue;

        Diag.Log("vulkan device + queue ready");
        return true;
    }
}
