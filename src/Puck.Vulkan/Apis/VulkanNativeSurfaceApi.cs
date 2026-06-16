using Puck.Platform;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanSurfaceApi"/>, marshaling to the platform-specific surface
/// creation entry points and <c>vkDestroySurfaceKHR</c> resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeSurfaceApi : IVulkanSurfaceApi {
    private const uint VkStructureTypeViSurfaceCreateInfoNn = 1000062000;
    private const uint VkStructureTypeWaylandSurfaceCreateInfoKhr = 1000006000;
    private const uint VkStructureTypeWin32SurfaceCreateInfoKhr = 1000009000;
    private const uint VkStructureTypeXcbSurfaceCreateInfoKhr = 1000005000;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getInstanceProcAddr;

    /// <inheritdoc/>
    public VkResult CreateViSurface(
        nint instanceHandle,
        ViNativeSurfaceBinding binding,
        out nint surfaceHandle
    ) {
        surfaceHandle = 0;

        if (0 == binding.Window) {
            throw new ArgumentException(
                message: "Nintendo Switch (VI) Vulkan surface creation requires a non-zero native window handle.",
                paramName: nameof(binding)
            );
        }

        var createSurface = GetPointers(instanceHandle: instanceHandle).CreateViSurfaceNn;

        if (createSurface is null) {
            return VkResult.ErrorExtensionNotPresent;
        }

        var createInfo = new VkViSurfaceCreateInfoNn {
            StructureType = VkStructureTypeViSurfaceCreateInfoNn,
            Window = binding.Window,
        };

        return createSurface(
            instanceHandle,
            in createInfo,
            0,
            out surfaceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult CreateWaylandSurface(
        nint instanceHandle,
        WaylandNativeSurfaceBinding binding,
        out nint surfaceHandle
    ) {
        surfaceHandle = 0;

        if (
            (0 == binding.Display) ||
            (0 == binding.Surface)
        ) {
            throw new ArgumentException(
                message: "Wayland Vulkan surface creation requires non-zero display and surface handles.",
                paramName: nameof(binding)
            );
        }

        var createSurface = GetPointers(instanceHandle: instanceHandle).CreateWaylandSurfaceKhr;

        if (createSurface is null) {
            return VkResult.ErrorExtensionNotPresent;
        }

        var createInfo = new VkWaylandSurfaceCreateInfoKhr {
            Display = binding.Display,
            StructureType = VkStructureTypeWaylandSurfaceCreateInfoKhr,
            Surface = binding.Surface,
        };

        return createSurface(
            instanceHandle,
            in createInfo,
            0,
            out surfaceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult CreateWin32Surface(
        nint instanceHandle,
        Win32NativeSurfaceBinding binding,
        out nint surfaceHandle
    ) {
        surfaceHandle = 0;

        if (
            (0 == binding.InstanceHandle) ||
            (0 == binding.WindowHandle)
        ) {
            throw new ArgumentException(
                message: "Win32 Vulkan surface creation requires non-zero instance and window handles.",
                paramName: nameof(binding)
            );
        }

        var createSurface = GetPointers(instanceHandle: instanceHandle).CreateWin32SurfaceKhr;

        if (createSurface is null) {
            return VkResult.ErrorExtensionNotPresent;
        }

        var createInfo = new VkWin32SurfaceCreateInfoKhr {
            InstanceHandle = binding.InstanceHandle,
            StructureType = VkStructureTypeWin32SurfaceCreateInfoKhr,
            WindowHandle = binding.WindowHandle,
        };

        return createSurface(
            instanceHandle,
            in createInfo,
            0,
            out surfaceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult CreateXcbSurface(
        nint instanceHandle,
        XcbNativeSurfaceBinding binding,
        out nint surfaceHandle
    ) {
        surfaceHandle = 0;

        if (
            (0 == binding.Connection) ||
            (0 == binding.Window)
        ) {
            throw new ArgumentException(
                message: "XCB Vulkan surface creation requires a non-zero connection and window.",
                paramName: nameof(binding)
            );
        }

        var createSurface = GetPointers(instanceHandle: instanceHandle).CreateXcbSurfaceKhr;

        if (createSurface is null) {
            return VkResult.ErrorExtensionNotPresent;
        }

        var createInfo = new VkXcbSurfaceCreateInfoKhr {
            Connection = binding.Connection,
            StructureType = VkStructureTypeXcbSurfaceCreateInfoKhr,
            Window = binding.Window,
        };

        return createSurface(
            instanceHandle,
            in createInfo,
            0,
            out surfaceHandle
        );
    }
    /// <inheritdoc/>
    public void DestroySurface(nint instanceHandle, nint surfaceHandle) {
        if (0 == surfaceHandle) {
            return;
        }

        var destroySurface = GetPointers(instanceHandle: instanceHandle).DestroySurfaceKhr;

        destroySurface(
            instanceHandle,
            surfaceHandle,
            0
        );
    }

    private unsafe struct InstancePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkViSurfaceCreateInfoNn, nint, out nint, VkResult> CreateViSurfaceNn;
        public delegate* unmanaged[Cdecl]<nint, in VkWaylandSurfaceCreateInfoKhr, nint, out nint, VkResult> CreateWaylandSurfaceKhr;
        public delegate* unmanaged[Cdecl]<nint, in VkWin32SurfaceCreateInfoKhr, nint, out nint, VkResult> CreateWin32SurfaceKhr;
        public delegate* unmanaged[Cdecl]<nint, in VkXcbSurfaceCreateInfoKhr, nint, out nint, VkResult> CreateXcbSurfaceKhr;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroySurfaceKhr;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, InstancePointers> m_pointers = new();

    private unsafe InstancePointers GetPointers(nint instanceHandle) {
        if (m_pointers.TryGetValue(
            key: instanceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetInstanceProcAddr();
        InstancePointers pNew = default;

        fixed (byte* pName = "vkCreateViSurfaceNN"u8) {
            pNew.CreateViSurfaceNn = (delegate* unmanaged[Cdecl]<nint, in VkViSurfaceCreateInfoNn, nint, out nint, VkResult>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCreateWaylandSurfaceKHR"u8) {
            pNew.CreateWaylandSurfaceKhr = (delegate* unmanaged[Cdecl]<nint, in VkWaylandSurfaceCreateInfoKhr, nint, out nint, VkResult>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCreateWin32SurfaceKHR"u8) {
            pNew.CreateWin32SurfaceKhr = (delegate* unmanaged[Cdecl]<nint, in VkWin32SurfaceCreateInfoKhr, nint, out nint, VkResult>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCreateXcbSurfaceKHR"u8) {
            pNew.CreateXcbSurfaceKhr = (delegate* unmanaged[Cdecl]<nint, in VkXcbSurfaceCreateInfoKhr, nint, out nint, VkResult>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroySurfaceKHR"u8) {
            pNew.DestroySurfaceKhr = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                instanceHandle,
                pName
            );
        }
        m_pointers[instanceHandle] = pNew;
        return pNew;
    }
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> GetInstanceProcAddr() {
        lock (m_syncRoot) {
            if (m_getInstanceProcAddr is not null) {
                return m_getInstanceProcAddr;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetInstanceProcAddr");

            m_getInstanceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getInstanceProcAddr;
        }
    }
}
