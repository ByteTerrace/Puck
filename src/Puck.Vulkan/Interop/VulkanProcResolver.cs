using System.Text;
using Puck.Abstractions.Counting;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Resolves native Vulkan entry points. An instance resolves instance-level procedures through its
/// <c>vkGetInstanceProcAddr</c> and device-level procedures through its <c>vkGetDeviceProcAddr</c>, counting every
/// resolution into its own <see cref="Work"/>; a command table takes one through its constructor, so a table can be
/// built over a resolver that stands in for the driver. Raw loader exports (for example <c>vkCreateInstance</c>) are
/// looked up without a resolver. Each lookup comes in a <em>required</em> form (throws when the entry point is absent)
/// and an <em>optional</em> form (returns <c>0</c> when the entry point is absent, for procedures that only exist
/// behind an extension).
/// </summary>
public sealed unsafe class VulkanProcResolver {
    /// <summary>The name a counters report heads <see cref="Work"/>'s section with.</summary>
    public const string WorkSourceName = "procedures.vulkan";

    private readonly Lock m_syncRoot = new();

    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getInstanceProcAddr;

    /// <summary>Gets the kind counting device-level procedures resolved through <c>vkGetDeviceProcAddr</c>, required
    /// and optional alike, found or not: a device's command table resolves every entry point it holds once.</summary>
    public static WorkKind DeviceResolutions { get; } = new(name: "vulkan.procedures.device-resolved", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting instance-level procedures resolved through <c>vkGetInstanceProcAddr</c>,
    /// required and optional alike, found or not.</summary>
    public static WorkKind InstanceResolutions { get; } = new(name: "vulkan.procedures.instance-resolved", unit: "count", workClass: WorkClass.PerBackendDeterministic);

    /// <summary>Gets this resolver's resolution counts, which a host registers once as its
    /// <see cref="WorkSourceName"/> source. Every instance and device table built over this resolver counts here, and a
    /// device recreated after a loss adds its table again. Counts only go up.</summary>
    public WorkCounterSet Work { get; } = new(
        kinds: [DeviceResolutions, InstanceResolutions],
        name: WorkSourceName
    );

    /// <summary>Initializes a new instance of the <see cref="VulkanProcResolver"/> class over the Vulkan loader, whose
    /// <c>vkGetInstanceProcAddr</c> and <c>vkGetDeviceProcAddr</c> are loaded on the first resolution, so creating one
    /// never loads the loader.</summary>
    public VulkanProcResolver() { }
    /// <summary>Initializes a new instance of the <see cref="VulkanProcResolver"/> class over the given lookups, which
    /// stand in for the driver.</summary>
    /// <param name="getInstanceProcAddr">The <c>vkGetInstanceProcAddr</c>-shaped lookup; must be non-null.</param>
    /// <param name="getDeviceProcAddr">The <c>vkGetDeviceProcAddr</c>-shaped lookup; must be non-null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="getInstanceProcAddr"/> or
    /// <paramref name="getDeviceProcAddr"/> is <see langword="null"/>.</exception>
    public VulkanProcResolver(delegate* unmanaged[Cdecl]<nint, byte*, nint> getInstanceProcAddr, delegate* unmanaged[Cdecl]<nint, byte*, nint> getDeviceProcAddr) {
        if ((getInstanceProcAddr is null) || (getDeviceProcAddr is null)) {
            throw new ArgumentNullException(paramName: ((getInstanceProcAddr is null) ? nameof(getInstanceProcAddr) : nameof(getDeviceProcAddr)));
        }

        m_getDeviceProcAddr = getDeviceProcAddr;
        m_getInstanceProcAddr = getInstanceProcAddr;
    }

    private delegate* unmanaged[Cdecl]<nint, byte*, nint> DeviceProcAddr {
        get {
            lock (m_syncRoot) {
                if (m_getDeviceProcAddr is null) {
                    m_getDeviceProcAddr = ((delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr"));
                }

                return m_getDeviceProcAddr;
            }
        }
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> InstanceProcAddr {
        get {
            lock (m_syncRoot) {
                if (m_getInstanceProcAddr is null) {
                    m_getInstanceProcAddr = ((delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetInstanceProcAddr"));
                }

                return m_getInstanceProcAddr;
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

    /// <summary>Resolves a required device-level procedure through this resolver's <c>vkGetDeviceProcAddr</c>.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateBuffer"u8</c>).</param>
    /// <returns>The address of the resolved procedure.</returns>
    /// <exception cref="InvalidOperationException">The device does not expose <paramref name="functionName"/>.</exception>
    /// <exception cref="EntryPointNotFoundException">The loader does not export <c>vkGetDeviceProcAddr</c>.</exception>
    public nint ResolveDeviceProc(nint deviceHandle, ReadOnlySpan<byte> functionName) {
        var proc = ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: functionName
        );

        if (0 == proc) {
            throw MissingDeviceProc(functionName: functionName);
        }

        return proc;
    }
    /// <summary>Resolves a required instance-level procedure through this resolver's <c>vkGetInstanceProcAddr</c>.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateDevice"u8</c>).</param>
    /// <returns>The address of the resolved procedure.</returns>
    /// <exception cref="InvalidOperationException">The instance does not expose <paramref name="functionName"/>.</exception>
    /// <exception cref="EntryPointNotFoundException">The loader does not export <c>vkGetInstanceProcAddr</c>.</exception>
    public nint ResolveInstanceProc(nint instanceHandle, ReadOnlySpan<byte> functionName) {
        var proc = ResolveOptionalInstanceProc(
            functionName: functionName,
            instanceHandle: instanceHandle
        );

        if (0 == proc) {
            throw MissingInstanceProc(functionName: functionName);
        }

        return proc;
    }
    /// <summary>Resolves an optional device-level procedure through this resolver's <c>vkGetDeviceProcAddr</c>,
    /// returning <c>0</c> when it is absent.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCmdTraceRaysKHR"u8</c>).</param>
    /// <returns>The address of the resolved procedure, or <c>0</c> when the device does not expose it.</returns>
    /// <exception cref="EntryPointNotFoundException">The loader does not export <c>vkGetDeviceProcAddr</c>.</exception>
    public nint ResolveOptionalDeviceProc(nint deviceHandle, ReadOnlySpan<byte> functionName) =>
        Resolve(
            functionName: functionName,
            getProcAddr: DeviceProcAddr,
            handle: deviceHandle,
            kind: DeviceResolutions
        );
    /// <summary>Resolves an optional instance-level procedure through this resolver's <c>vkGetInstanceProcAddr</c>,
    /// returning <c>0</c> when it is absent.</summary>
    /// <param name="instanceHandle">The native <c>VkInstance</c> handle the procedure is scoped to.</param>
    /// <param name="functionName">The UTF-8 name of the procedure (for example, <c>"vkCreateDebugUtilsMessengerEXT"u8</c>).</param>
    /// <returns>The address of the resolved procedure, or <c>0</c> when the instance does not expose it.</returns>
    /// <exception cref="EntryPointNotFoundException">The loader does not export <c>vkGetInstanceProcAddr</c>.</exception>
    public nint ResolveOptionalInstanceProc(nint instanceHandle, ReadOnlySpan<byte> functionName) =>
        Resolve(
            functionName: functionName,
            getProcAddr: InstanceProcAddr,
            handle: instanceHandle,
            kind: InstanceResolutions
        );

    // Every device- and instance-level lookup resolves here, so every resolution is counted here, once.
    private nint Resolve(delegate* unmanaged[Cdecl]<nint, byte*, nint> getProcAddr, nint handle, ReadOnlySpan<byte> functionName, WorkKind kind) {
        Work.Count(kind: kind);

        fixed (byte* pName = functionName) {
            return getProcAddr(
                handle,
                pName
            );
        }
    }
}