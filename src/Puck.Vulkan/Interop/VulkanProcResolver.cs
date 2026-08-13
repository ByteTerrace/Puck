using System.Text;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Resolves native Vulkan entry points from the process-wide loader. It covers the three ways an entry point
/// is looked up: raw loader exports (for example <c>vkCreateInstance</c>), instance-level procedures resolved
/// through <c>vkGetInstanceProcAddr</c>, and device-level procedures resolved through <c>vkGetDeviceProcAddr</c>.
/// Each lookup comes in a <em>required</em> form (throws when the entry point is absent) and an <em>optional</em>
/// form (returns <c>0</c> when the entry point is absent, for procedures that only exist behind an extension).
/// </summary>
/// <remarks>
/// The two dispatch trampolines — <c>vkGetInstanceProcAddr</c> and <c>vkGetDeviceProcAddr</c> — are themselves
/// loader exports that never change over the life of the process, so they are resolved once and cached here for
/// every API to share, rather than each API caching its own copy. The per-handle procedure pointers those
/// trampolines return still differ per instance/device, so each API keeps caching its own typed pointer bundle.
/// </remarks>
public static unsafe class VulkanProcResolver {
    private static readonly Lock SyncRoot = new();
    private static delegate* unmanaged[Cdecl]<nint, byte*, nint> CachedGetDeviceProcAddr;
    private static delegate* unmanaged[Cdecl]<nint, byte*, nint> CachedGetInstanceProcAddr;

    /// <summary>Resolves a required export from the Vulkan loader.</summary>
    /// <param name="functionName">The name of the exported function to resolve (for example, <c>vkCreateInstance</c>).</param>
    /// <returns>The address of the exported function.</returns>
    /// <exception cref="ArgumentException"><paramref name="functionName"/> is <see langword="null"/>, empty, or white space.</exception>
    /// <exception cref="EntryPointNotFoundException">The loader does not export <paramref name="functionName"/>.</exception>
    public static nint ResolveExport(string functionName) {
        return VulkanNativeLibrary.GetExport(functionName: functionName);
    }
    /// <summary>Resolves an optional export from the Vulkan loader, returning <c>0</c> when it is absent.</summary>
    /// <param name="functionName">The name of the exported function to resolve (for example, <c>vkEnumerateInstanceVersion</c>).</param>
    /// <returns>The address of the exported function, or <c>0</c> when the loader does not export it.</returns>
    /// <exception cref="ArgumentException"><paramref name="functionName"/> is <see langword="null"/>, empty, or white space.</exception>
    public static nint ResolveOptionalExport(string functionName) {
        try {
            return VulkanNativeLibrary.GetExport(functionName: functionName);
        } catch (EntryPointNotFoundException) {
            // A pre-1.1 loader (or one missing an extension export) lacks the symbol; the optional contract is 0.
            return 0;
        }
    }

    /// <summary>Resolves a required instance-level procedure through <c>vkGetInstanceProcAddr</c>.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateDevice"u8</c>).</param>
    /// <returns>The address of the resolved procedure.</returns>
    /// <exception cref="InvalidOperationException">The instance does not expose <paramref name="functionName"/>.</exception>
    public static nint ResolveInstanceProc(nint instanceHandle, ReadOnlySpan<byte> functionName) {
        var proc = ResolveOptionalInstanceProc(
            functionName: functionName,
            instanceHandle: instanceHandle
        );

        if (0 == proc) {
            throw new InvalidOperationException(message: $"The Vulkan instance procedure '{Decode(utf8: functionName)}' is not available.");
        }

        return proc;
    }
    /// <summary>Resolves an optional instance-level procedure through <c>vkGetInstanceProcAddr</c>, returning <c>0</c> when it is absent.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateDebugUtilsMessengerEXT"u8</c>).</param>
    /// <returns>The address of the resolved procedure, or <c>0</c> when the instance does not expose it.</returns>
    public static nint ResolveOptionalInstanceProc(nint instanceHandle, ReadOnlySpan<byte> functionName) {
        var getInstanceProcAddr = GetInstanceProcAddr();

        fixed (byte* pName = functionName) {
            return getInstanceProcAddr(
                instanceHandle,
                pName
            );
        }
    }

    /// <summary>Resolves a required device-level procedure through <c>vkGetDeviceProcAddr</c>.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateBuffer"u8</c>).</param>
    /// <returns>The address of the resolved procedure.</returns>
    /// <exception cref="InvalidOperationException">The device does not expose <paramref name="functionName"/>.</exception>
    public static nint ResolveDeviceProc(nint deviceHandle, ReadOnlySpan<byte> functionName) {
        var proc = ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: functionName
        );

        if (0 == proc) {
            throw new InvalidOperationException(message: $"The Vulkan device procedure '{Decode(utf8: functionName)}' is not available.");
        }

        return proc;
    }
    /// <summary>Resolves an optional device-level procedure through <c>vkGetDeviceProcAddr</c>, returning <c>0</c> when it is absent.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCmdTraceRaysKHR"u8</c>).</param>
    /// <returns>The address of the resolved procedure, or <c>0</c> when the device does not expose it.</returns>
    public static nint ResolveOptionalDeviceProc(nint deviceHandle, ReadOnlySpan<byte> functionName) {
        var getDeviceProcAddr = GetDeviceProcAddr();

        fixed (byte* pName = functionName) {
            return getDeviceProcAddr(
                deviceHandle,
                pName
            );
        }
    }

    private static string Decode(ReadOnlySpan<byte> utf8) {
        return Encoding.UTF8.GetString(bytes: utf8);
    }
    private static delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (SyncRoot) {
            if (CachedGetDeviceProcAddr is null) {
                CachedGetDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");
            }

            return CachedGetDeviceProcAddr;
        }
    }
    private static delegate* unmanaged[Cdecl]<nint, byte*, nint> GetInstanceProcAddr() {
        lock (SyncRoot) {
            if (CachedGetInstanceProcAddr is null) {
                CachedGetInstanceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetInstanceProcAddr");
            }

            return CachedGetInstanceProcAddr;
        }
    }
}
