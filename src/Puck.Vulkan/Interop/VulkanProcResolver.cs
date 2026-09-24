using System.Text;
using Puck.Abstractions.Counting;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Resolves native Vulkan entry points from the process-wide loader. It covers the three ways an entry point
/// is looked up: raw loader exports (for example <c>vkCreateInstance</c>), instance-level procedures resolved
/// through <c>vkGetInstanceProcAddr</c>, and device-level procedures resolved through <c>vkGetDeviceProcAddr</c>.
/// Each lookup comes in a <em>required</em> form (throws when the entry point is absent) and an <em>optional</em>
/// form (returns <c>0</c> when the entry point is absent, for procedures that only exist behind an extension).
/// </summary>
/// <remarks>
/// The two lookup entry points — <c>vkGetInstanceProcAddr</c> and <c>vkGetDeviceProcAddr</c> — are loader exports
/// that never change over the life of the process, so they are resolved once and cached here. The per-handle
/// procedures they return are resolved once per instance into <see cref="VulkanInstanceCommands"/> and once per
/// device into <see cref="VulkanDeviceCommands"/>, which every API reads. Each per-handle lookup also takes the
/// resolver explicitly, so a table can be built over a resolver that stands in for the driver.
/// </remarks>
public static unsafe class VulkanProcResolver {
    /// <summary>The name a counters report heads <see cref="Work"/>'s section with.</summary>
    public const string WorkSourceName = "procedures.vulkan";

    private static readonly Lock SyncRoot = new();

    private static delegate* unmanaged[Cdecl]<nint, byte*, nint> CachedGetDeviceProcAddr;
    private static delegate* unmanaged[Cdecl]<nint, byte*, nint> CachedGetInstanceProcAddr;

    /// <summary>Gets the kind counting device-level procedures resolved through <c>vkGetDeviceProcAddr</c>, required
    /// and optional alike, found or not: a device's command table resolves every entry point it holds once.</summary>
    public static WorkKind DeviceResolutions { get; } = new(name: "vulkan.procedures.device-resolved", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting instance-level procedures resolved through <c>vkGetInstanceProcAddr</c>,
    /// required and optional alike, found or not.</summary>
    public static WorkKind InstanceResolutions { get; } = new(name: "vulkan.procedures.instance-resolved", unit: "count", workClass: WorkClass.PerBackendDeterministic);

    /// <summary>Gets the process's procedure-resolution counts, which a host registers as its
    /// <see cref="WorkSourceName"/> source. Every instance and device the process creates resolves through this class,
    /// so the counts cover all of them, and a recreated device after a loss adds its table again. Counts only go
    /// up.</summary>
    public static WorkCounterSet Work =>
        Counts.Process;
    /// <summary>Gets the loader's <c>vkGetDeviceProcAddr</c>, loading the loader on first use.</summary>
    /// <exception cref="EntryPointNotFoundException">The loader does not export it.</exception>
    public static delegate* unmanaged[Cdecl]<nint, byte*, nint> LoaderDeviceProcAddr {
        get {
            lock (SyncRoot) {
                if (CachedGetDeviceProcAddr is null) {
                    CachedGetDeviceProcAddr = ((delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr"));
                }

                return CachedGetDeviceProcAddr;
            }
        }
    }
    /// <summary>Gets the loader's <c>vkGetInstanceProcAddr</c>, loading the loader on first use.</summary>
    /// <exception cref="EntryPointNotFoundException">The loader does not export it.</exception>
    public static delegate* unmanaged[Cdecl]<nint, byte*, nint> LoaderInstanceProcAddr {
        get {
            lock (SyncRoot) {
                if (CachedGetInstanceProcAddr is null) {
                    CachedGetInstanceProcAddr = ((delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetInstanceProcAddr"));
                }

                return CachedGetInstanceProcAddr;
            }
        }
    }

    private static string Decode(ReadOnlySpan<byte> utf8) {
        return Encoding.UTF8.GetString(bytes: utf8);
    }

    /// <summary>Creates the exception reported when a required device-level procedure is absent.</summary>
    /// <param name="functionName">The UTF-8 name of the absent procedure.</param>
    /// <returns>The exception to throw.</returns>
    public static InvalidOperationException MissingDeviceProc(ReadOnlySpan<byte> functionName) {
        return new InvalidOperationException(message: $"The Vulkan device procedure '{Decode(utf8: functionName)}' is not available.");
    }
    /// <summary>Creates the exception reported when a required instance-level procedure is absent.</summary>
    /// <param name="functionName">The UTF-8 name of the absent procedure.</param>
    /// <returns>The exception to throw.</returns>
    public static InvalidOperationException MissingInstanceProc(ReadOnlySpan<byte> functionName) {
        return new InvalidOperationException(message: $"The Vulkan instance procedure '{Decode(utf8: functionName)}' is not available.");
    }
    /// <summary>Resolves a required device-level procedure through the loader's <c>vkGetDeviceProcAddr</c>.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateBuffer"u8</c>).</param>
    /// <returns>The address of the resolved procedure.</returns>
    /// <exception cref="InvalidOperationException">The device does not expose <paramref name="functionName"/>.</exception>
    public static nint ResolveDeviceProc(nint deviceHandle, ReadOnlySpan<byte> functionName) =>
        ResolveDeviceProc(
            deviceHandle: deviceHandle,
            functionName: functionName,
            getDeviceProcAddr: LoaderDeviceProcAddr
        );
    /// <summary>Resolves a required device-level procedure through a <c>vkGetDeviceProcAddr</c>-shaped resolver.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateBuffer"u8</c>).</param>
    /// <param name="getDeviceProcAddr">The resolver: <see cref="LoaderDeviceProcAddr"/>, or one standing in for the
    /// driver.</param>
    /// <returns>The address of the resolved procedure.</returns>
    /// <exception cref="InvalidOperationException">The device does not expose <paramref name="functionName"/>.</exception>
    public static nint ResolveDeviceProc(nint deviceHandle, ReadOnlySpan<byte> functionName, delegate* unmanaged[Cdecl]<nint, byte*, nint> getDeviceProcAddr) {
        var proc = ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: functionName,
            getDeviceProcAddr: getDeviceProcAddr
        );

        if (0 == proc) {
            throw MissingDeviceProc(functionName: functionName);
        }

        return proc;
    }
    /// <summary>Resolves a required export from the Vulkan loader.</summary>
    /// <param name="functionName">The name of the exported function to resolve (for example, <c>vkCreateInstance</c>).</param>
    /// <returns>The address of the exported function.</returns>
    /// <exception cref="ArgumentException"><paramref name="functionName"/> is <see langword="null"/>, empty, or white space.</exception>
    /// <exception cref="EntryPointNotFoundException">The loader does not export <paramref name="functionName"/>.</exception>
    public static nint ResolveExport(string functionName) {
        return VulkanNativeLibrary.GetExport(functionName: functionName);
    }
    /// <summary>Resolves a required instance-level procedure through the loader's <c>vkGetInstanceProcAddr</c>.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateDevice"u8</c>).</param>
    /// <returns>The address of the resolved procedure.</returns>
    /// <exception cref="InvalidOperationException">The instance does not expose <paramref name="functionName"/>.</exception>
    public static nint ResolveInstanceProc(nint instanceHandle, ReadOnlySpan<byte> functionName) =>
        ResolveInstanceProc(
            functionName: functionName,
            getInstanceProcAddr: LoaderInstanceProcAddr,
            instanceHandle: instanceHandle
        );
    /// <summary>Resolves a required instance-level procedure through a <c>vkGetInstanceProcAddr</c>-shaped resolver.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateDevice"u8</c>).</param>
    /// <param name="getInstanceProcAddr">The resolver: <see cref="LoaderInstanceProcAddr"/>, or one standing in for
    /// the driver.</param>
    /// <returns>The address of the resolved procedure.</returns>
    /// <exception cref="InvalidOperationException">The instance does not expose <paramref name="functionName"/>.</exception>
    public static nint ResolveInstanceProc(nint instanceHandle, ReadOnlySpan<byte> functionName, delegate* unmanaged[Cdecl]<nint, byte*, nint> getInstanceProcAddr) {
        var proc = ResolveOptionalInstanceProc(
            functionName: functionName,
            getInstanceProcAddr: getInstanceProcAddr,
            instanceHandle: instanceHandle
        );

        if (0 == proc) {
            throw MissingInstanceProc(functionName: functionName);
        }

        return proc;
    }
    /// <summary>Resolves an optional device-level procedure through the loader's <c>vkGetDeviceProcAddr</c>, returning
    /// <c>0</c> when it is absent.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCmdTraceRaysKHR"u8</c>).</param>
    /// <returns>The address of the resolved procedure, or <c>0</c> when the device does not expose it.</returns>
    public static nint ResolveOptionalDeviceProc(nint deviceHandle, ReadOnlySpan<byte> functionName) =>
        ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: functionName,
            getDeviceProcAddr: LoaderDeviceProcAddr
        );
    /// <summary>Resolves an optional device-level procedure through a <c>vkGetDeviceProcAddr</c>-shaped resolver,
    /// returning <c>0</c> when it is absent.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCmdTraceRaysKHR"u8</c>).</param>
    /// <param name="getDeviceProcAddr">The resolver: <see cref="LoaderDeviceProcAddr"/>, or one standing in for the
    /// driver.</param>
    /// <returns>The address of the resolved procedure, or <c>0</c> when the device does not expose it.</returns>
    public static nint ResolveOptionalDeviceProc(nint deviceHandle, ReadOnlySpan<byte> functionName, delegate* unmanaged[Cdecl]<nint, byte*, nint> getDeviceProcAddr) =>
        Resolve(
            functionName: functionName,
            getProcAddr: getDeviceProcAddr,
            handle: deviceHandle,
            kind: DeviceResolutions
        );
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
    /// <summary>Resolves an optional instance-level procedure through the loader's <c>vkGetInstanceProcAddr</c>,
    /// returning <c>0</c> when it is absent.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateDebugUtilsMessengerEXT"u8</c>).</param>
    /// <returns>The address of the resolved procedure, or <c>0</c> when the instance does not expose it.</returns>
    public static nint ResolveOptionalInstanceProc(nint instanceHandle, ReadOnlySpan<byte> functionName) =>
        ResolveOptionalInstanceProc(
            functionName: functionName,
            getInstanceProcAddr: LoaderInstanceProcAddr,
            instanceHandle: instanceHandle
        );
    /// <summary>Resolves an optional instance-level procedure through a <c>vkGetInstanceProcAddr</c>-shaped resolver,
    /// returning <c>0</c> when it is absent.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateDebugUtilsMessengerEXT"u8</c>).</param>
    /// <param name="getInstanceProcAddr">The resolver: <see cref="LoaderInstanceProcAddr"/>, or one standing in for
    /// the driver.</param>
    /// <returns>The address of the resolved procedure, or <c>0</c> when the instance does not expose it.</returns>
    public static nint ResolveOptionalInstanceProc(nint instanceHandle, ReadOnlySpan<byte> functionName, delegate* unmanaged[Cdecl]<nint, byte*, nint> getInstanceProcAddr) =>
        Resolve(
            functionName: functionName,
            getProcAddr: getInstanceProcAddr,
            handle: instanceHandle,
            kind: InstanceResolutions
        );

    // Every device- and instance-level lookup resolves here, so every resolution is counted here, once.
    private static nint Resolve(delegate* unmanaged[Cdecl]<nint, byte*, nint> getProcAddr, nint handle, ReadOnlySpan<byte> functionName, WorkKind kind) {
        Work.Count(kind: kind);

        fixed (byte* pName = functionName) {
            return getProcAddr(
                handle,
                pName
            );
        }
    }

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class Counts {
        internal static readonly WorkCounterSet Process = new(
            kinds: [DeviceResolutions, InstanceResolutions],
            name: WorkSourceName
        );
    }
}
