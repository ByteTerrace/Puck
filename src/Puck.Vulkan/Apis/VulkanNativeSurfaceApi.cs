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

    /// <inheritdoc/>
    public VkResult CreateViSurface(
        VulkanInstanceCommands instance,
        ViNativeSurfaceBinding binding,
        out nint surfaceHandle
    ) {
        RequireHandles(
            missing: (0 == binding.WindowHandle),
            requirement: "Nintendo Switch (VI) Vulkan surface creation requires a non-zero native window handle."
        );

        return CreateSurface(
            createInfo: new VkViSurfaceCreateInfoNn {
                StructureType = VkStructureTypeViSurfaceCreateInfoNn,
                Window = binding.WindowHandle,
            },
            createSurface: instance.CreateViSurfaceNn,
            instance: instance,
            surfaceHandle: out surfaceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult CreateWaylandSurface(
        VulkanInstanceCommands instance,
        WaylandNativeSurfaceBinding binding,
        out nint surfaceHandle
    ) {
        RequireHandles(
            missing: ((0 == binding.DisplayHandle) || (0 == binding.SurfaceHandle)),
            requirement: "Wayland Vulkan surface creation requires non-zero display and surface handles."
        );

        return CreateSurface(
            createInfo: new VkWaylandSurfaceCreateInfoKhr {
                Display = binding.DisplayHandle,
                StructureType = VkStructureTypeWaylandSurfaceCreateInfoKhr,
                Surface = binding.SurfaceHandle,
            },
            createSurface: instance.CreateWaylandSurfaceKhr,
            instance: instance,
            surfaceHandle: out surfaceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult CreateWin32Surface(
        VulkanInstanceCommands instance,
        Win32NativeSurfaceBinding binding,
        out nint surfaceHandle
    ) {
        RequireHandles(
            missing: ((0 == binding.InstanceHandle) || (0 == binding.WindowHandle)),
            requirement: "Win32 Vulkan surface creation requires non-zero instance and window handles."
        );

        return CreateSurface(
            createInfo: new VkWin32SurfaceCreateInfoKhr {
                InstanceHandle = binding.InstanceHandle,
                StructureType = VkStructureTypeWin32SurfaceCreateInfoKhr,
                WindowHandle = binding.WindowHandle,
            },
            createSurface: instance.CreateWin32SurfaceKhr,
            instance: instance,
            surfaceHandle: out surfaceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult CreateXcbSurface(
        VulkanInstanceCommands instance,
        XcbNativeSurfaceBinding binding,
        out nint surfaceHandle
    ) {
        RequireHandles(
            missing: ((0 == binding.Connection) || (0 == binding.Window)),
            requirement: "XCB Vulkan surface creation requires a non-zero connection and window."
        );

        return CreateSurface(
            createInfo: new VkXcbSurfaceCreateInfoKhr {
                Connection = binding.Connection,
                StructureType = VkStructureTypeXcbSurfaceCreateInfoKhr,
                Window = binding.Window,
            },
            createSurface: instance.CreateXcbSurfaceKhr,
            instance: instance,
            surfaceHandle: out surfaceHandle
        );
    }

    private static void RequireHandles(bool missing, string requirement) {
        if (missing) {
            throw new ArgumentException(
                message: requirement,
                paramName: "binding"
            );
        }
    }
    // Every platform surface entry point shares this shape; an instance created without the platform's extension, or
    // without VK_KHR_surface, which destroys what it creates, holds a null entry point, reported as the extension
    // being absent. The call goes through a pointer-typed view of the entry point: the runtime cannot marshal a
    // generic parameter in an unmanaged function-pointer signature (MarshalDirectiveException), and the native ABI
    // passes the create info by address either way.
    private static VkResult CreateSurface<TCreateInfo>(
        VulkanInstanceCommands instance,
        delegate* unmanaged[Cdecl]<nint, in TCreateInfo, nint, out nint, VkResult> createSurface,
        in TCreateInfo createInfo,
        out nint surfaceHandle
    ) where TCreateInfo : unmanaged {
        surfaceHandle = 0;

        if (
            (createSurface is null) ||
            (instance.DestroySurfaceKhr is null)
        ) {
            return VkResult.ErrorExtensionNotPresent;
        }

        var createSurfaceByAddress = ((delegate* unmanaged[Cdecl]<nint, void*, nint, nint*, VkResult>)createSurface);
        nint createdHandle = 0;
        VkResult result;

        fixed (TCreateInfo* createInfoAddress = &createInfo) {
            result = createSurfaceByAddress(
                instance.Handle,
                createInfoAddress,
                0,
                &createdHandle
            );
        }

        surfaceHandle = createdHandle;

        return result;
    }

    /// <inheritdoc/>
    public void DestroySurface(VulkanInstanceCommands instance, nint surfaceHandle) =>
        instance?.Destroy(
            destroy: instance.DestroySurfaceKhr,
            handle: surfaceHandle
        );
}
