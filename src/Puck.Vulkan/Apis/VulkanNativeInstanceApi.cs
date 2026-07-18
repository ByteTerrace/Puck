using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanInstanceApi"/>, marshaling to the <c>vkCreateInstance</c>
/// and <c>vkDestroyInstance</c> entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeInstanceApi : IVulkanInstanceApi {
    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeInstanceApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeInstanceApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    private const uint DebugMessageSeverityWarning = 0x00000100; // VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT
    private const uint DebugMessageSeverityError = 0x00001000;    // VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT
    private const uint DebugMessageTypeAll = 0x00000007;          // general | validation | performance
    private const uint VkStructureTypeApplicationInfo = 0;
    private const uint VkStructureTypeDebugUtilsMessengerCreateInfoExt = 1000128004;
    private const uint VkStructureTypeInstanceCreateInfo = 1;
    // Request Vulkan 1.3 as the hard floor: SPIR-V 1.6 is core in 1.3, and every Puck kernel now
    // compiles at -fspv-target-env=vulkan1.3, so a lower instance would refuse those modules at
    // vkCreateShaderModule. 1.3 also makes core vkGetPhysicalDeviceFeatures2, buffer-device-address,
    // and subgroup-size-control available. All four supported GPUs (RTX 2070, RTX 4070, Steam Machine
    // [RDNA3], Steam Deck [RDNA2]) expose 1.3 on current drivers; pre-1.1 loaders that would reject a
    // higher requested version are effectively extinct. The device-side floor is enforced separately in
    // VulkanPhysicalDeviceSelector (a device may report a lower ApiVersion than the loader).
    private const uint VulkanApiVersion13 = (1u << 22) | (3u << 12);

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<in VkInstanceCreateInfo, nint, out nint, VkResult> m_createInstance;
    private unsafe delegate* unmanaged[Cdecl]<nint, nint, void> m_destroyInstance;
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getInstanceProcAddr;
    private unsafe delegate* unmanaged[Cdecl]<byte*, uint*, VkExtensionProperties*, VkResult> m_enumerateInstanceExtensionProperties;

    /// <inheritdoc/>
    public VkResult CreateInstance(VulkanInstanceCreateRequest request, out nint instanceHandle) {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApplicationName);

        instanceHandle = 0;

        using var applicationName = Utf8StringScope.Create(value: request.ApplicationName);
        using var engineName = Utf8StringScope.Create(value: "Puck");
        using var extensionNames = Utf8StringArrayScope.Create(allocator: m_allocator, values: request.ExtensionNames);
        using var layerNames = Utf8StringArrayScope.Create(allocator: m_allocator, values: request.LayerNames);

        var appInfo = new VkApplicationInfo {
            ApiVersion = ProbeApiVersion(),
            ApplicationName = applicationName.Pointer,
            EngineName = engineName.Pointer,
            StructureType = VkStructureTypeApplicationInfo,
        };

        // Chain a messenger create-info into pNext when validation is on, so validation messages raised DURING
        // vkCreateInstance / vkDestroyInstance — which the standalone messenger (created only after the instance
        // exists, and destroyed before it) cannot see — also reach the callback. VK_EXT_debug_utils is enabled
        // alongside validation, so the chained struct is valid exactly when EnableValidation is set. The local
        // stays in scope through the synchronous create call below, so its address is valid for the chain.
        var messengerInfo = BuildMessengerCreateInfo();
        var createInfo = new VkInstanceCreateInfo {
            ApplicationInfo = m_allocator.Alloc(size: Marshal.SizeOf<VkApplicationInfo>()),
            EnabledExtensionCount = checked((uint)request.ExtensionNames.Count),
            EnabledExtensionNames = extensionNames.Pointer,
            EnabledLayerCount = checked((uint)request.LayerNames.Count),
            EnabledLayerNames = layerNames.Pointer,
            Next = (request.EnableValidation ? (nint)(&messengerInfo) : 0),
            StructureType = VkStructureTypeInstanceCreateInfo,
        };

        try {
            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: createInfo.ApplicationInfo,
                structure: appInfo
            );
            var createInstance = GetCreateInstance();

            return createInstance(
                in createInfo,
                0,
                out instanceHandle
            );
        } finally {
            if (0 != createInfo.ApplicationInfo) {
                m_allocator.Free(ptr: createInfo.ApplicationInfo);
            }
        }
    }
    // Probe the loader's highest supported instance version (vkEnumerateInstanceVersion, Vulkan 1.1+) and request
    // it — the parity-equivalent of the Direct3D 12 path probing feature levels down to the highest the adapter
    // accepts — instead of pinning a fixed version. Floored at the 1.3 the engine's SPIR-V 1.6 kernels need; a
    // pre-1.1 loader (no such export) is effectively extinct, so fall back to 1.3 there too.
    private static uint ProbeApiVersion() {
        try {
            var enumerateInstanceVersion = (delegate* unmanaged[Cdecl]<uint*, VkResult>)VulkanNativeLibrary.GetExport(functionName: "vkEnumerateInstanceVersion");
            uint version;

            if (
                (enumerateInstanceVersion is not null) &&
                (VkResult.Success == enumerateInstanceVersion(&version)) &&
                (version >= VulkanApiVersion13)
            ) {
                return version;
            }
        } catch {
            // A pre-1.1 loader lacks the export (GetExport throws); fall through to the 1.3 floor.
        }

        return VulkanApiVersion13;
    }

    /// <inheritdoc/>
    public bool HasInstanceExtension(string extensionName) {
        ArgumentException.ThrowIfNullOrEmpty(argument: extensionName);

        var enumerate = GetEnumerateInstanceExtensionProperties();

        if (enumerate is null) {
            return false;
        }

        var count = 0U;

        // pLayerName == null enumerates the loader's core + implicit-layer instance extensions (which is where the
        // debug-utils extension is advertised), independent of any explicit validation layer.
        if (
            (VkResult.Success != enumerate(null, &count, null)) ||
            (0 == count)
        ) {
            return false;
        }

        var properties = new VkExtensionProperties[count];

        fixed (VkExtensionProperties* propertiesPointer = properties) {
            // A second enumeration can legitimately return Incomplete if the list grew between calls; the entries
            // that were written are still valid.
            var result = enumerate(null, &count, propertiesPointer);

            if (
                (VkResult.Success != result) &&
                (VkResult.Incomplete != result)
            ) {
                return false;
            }

            for (var index = 0; (index < count); index++) {
                if (string.Equals(
                    a: Marshal.PtrToStringUTF8(ptr: (nint)propertiesPointer[index].ExtensionName),
                    b: extensionName,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return true;
                }
            }
        }

        return false;
    }
    /// <inheritdoc/>
    public void DestroyInstance(nint instanceHandle) {
        if (0 == instanceHandle) {
            return;
        }

        GetDestroyInstance()(
            instanceHandle,
            0
        );
    }
    /// <inheritdoc/>
    public nint CreateDebugMessenger(nint instanceHandle) {
        if (0 == instanceHandle) {
            return 0;
        }

        var getProcAddr = GetInstanceProcAddr();
        delegate* unmanaged[Cdecl]<nint, in VkDebugUtilsMessengerCreateInfoExt, nint, out nint, VkResult> createMessenger;

        fixed (byte* functionName = "vkCreateDebugUtilsMessengerEXT"u8) {
            createMessenger = (delegate* unmanaged[Cdecl]<nint, in VkDebugUtilsMessengerCreateInfoExt, nint, out nint, VkResult>)getProcAddr(instanceHandle, functionName);
        }

        // Best-effort: the entry point is absent unless VK_EXT_debug_utils is enabled and supported.
        if (createMessenger is null) {
            return 0;
        }

        var createInfo = BuildMessengerCreateInfo();

        return ((VkResult.Success == createMessenger(instanceHandle, in createInfo, 0, out var messengerHandle))
            ? messengerHandle
            : 0);
    }

    // The messenger configuration shared by the standalone messenger (vkCreateDebugUtilsMessengerEXT) and the
    // create-info chained into vkCreateInstance's pNext: subscribe to WARNING|ERROR across all message types and
    // route each to OnDebugMessage.
    private static VkDebugUtilsMessengerCreateInfoExt BuildMessengerCreateInfo() {
        return new VkDebugUtilsMessengerCreateInfoExt {
            MessageSeverity = DebugMessageSeverityWarning | DebugMessageSeverityError,
            MessageType = DebugMessageTypeAll,
            StructureType = VkStructureTypeDebugUtilsMessengerCreateInfoExt,
            UserCallback = (nint)(delegate* unmanaged[Cdecl]<uint, uint, VkDebugUtilsMessengerCallbackDataExt*, void*, uint>)(&OnDebugMessage),
        };
    }
    /// <inheritdoc/>
    public void DestroyDebugMessenger(nint instanceHandle, nint messengerHandle) {
        if (
            (0 == instanceHandle) ||
            (0 == messengerHandle)
        ) {
            return;
        }

        var getProcAddr = GetInstanceProcAddr();
        delegate* unmanaged[Cdecl]<nint, nint, nint, void> destroyMessenger;

        fixed (byte* functionName = "vkDestroyDebugUtilsMessengerEXT"u8) {
            destroyMessenger = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getProcAddr(instanceHandle, functionName);
        }

        if (destroyMessenger is null) {
            return;
        }

        destroyMessenger(instanceHandle, messengerHandle, 0);
    }

    // The VK_EXT_debug_utils callback: it surfaces each validation message to the console (mirroring the Direct3D 12
    // info-queue drain) and returns VK_FALSE so the triggering Vulkan call still proceeds.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint OnDebugMessage(uint messageSeverity, uint messageTypes, VkDebugUtilsMessengerCallbackDataExt* callbackData, void* userData) {
        if (
            (callbackData is not null) &&
            (0 != callbackData->Message)
        ) {
            // "INFO" is a defensive fallback: the subscription is WARNING|ERROR, so it is unreachable today but
            // keeps the label total if a lower severity is ever subscribed.
            var label = ((messageSeverity >= DebugMessageSeverityError)
                ? "ERROR"
                : ((messageSeverity >= DebugMessageSeverityWarning) ? "WARNING" : "INFO"));

            Console.Error.WriteLine(value: $"[vulkan-debug] {label}: {Marshal.PtrToStringUTF8(ptr: callbackData->Message)}");
        }

        return 0;
    }
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> GetInstanceProcAddr() {
        lock (m_syncRoot) {
            if (m_getInstanceProcAddr is not null) {
                return m_getInstanceProcAddr;
            }

            m_getInstanceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetInstanceProcAddr");

            return m_getInstanceProcAddr;
        }
    }
    private unsafe delegate* unmanaged[Cdecl]<byte*, uint*, VkExtensionProperties*, VkResult> GetEnumerateInstanceExtensionProperties() {
        lock (m_syncRoot) {
            if (m_enumerateInstanceExtensionProperties is not null) {
                return m_enumerateInstanceExtensionProperties;
            }

            m_enumerateInstanceExtensionProperties = (delegate* unmanaged[Cdecl]<byte*, uint*, VkExtensionProperties*, VkResult>)VulkanNativeLibrary.GetExport(functionName: "vkEnumerateInstanceExtensionProperties");

            return m_enumerateInstanceExtensionProperties;
        }
    }
    private unsafe delegate* unmanaged[Cdecl]<in VkInstanceCreateInfo, nint, out nint, VkResult> GetCreateInstance() {
        lock (m_syncRoot) {
            if (m_createInstance is not null) {
                return m_createInstance;
            }
            m_createInstance = (delegate* unmanaged[Cdecl]<in VkInstanceCreateInfo, nint, out nint, VkResult>)VulkanNativeLibrary.GetExport(functionName: "vkCreateInstance");
            return m_createInstance;
        }
    }
    private unsafe delegate* unmanaged[Cdecl]<nint, nint, void> GetDestroyInstance() {
        lock (m_syncRoot) {
            if (m_destroyInstance is not null) {
                return m_destroyInstance;
            }
            m_destroyInstance = (delegate* unmanaged[Cdecl]<nint, nint, void>)VulkanNativeLibrary.GetExport(functionName: "vkDestroyInstance");
            return m_destroyInstance;
        }
    }

    private sealed class Utf8StringScope : IDisposable {
        private Utf8StringScope(nint pointer) {
            Pointer = pointer;
        }

        public nint Pointer { get; }

        public static Utf8StringScope Create(string value) {
            return new Utf8StringScope(pointer: Marshal.StringToCoTaskMemUTF8(s: value));
        }
        public void Dispose() {
            if (0 != Pointer) {
                Marshal.FreeCoTaskMem(ptr: Pointer);
            }
        }
    }
    private sealed class Utf8StringArrayScope : IDisposable {
        private readonly IAllocator m_allocator;
        private readonly nint[] m_stringPointers;

        private Utf8StringArrayScope(IAllocator allocator, nint pointer, nint[] stringPointers) {
            m_allocator = allocator;
            Pointer = pointer;
            m_stringPointers = stringPointers;
        }

        public nint Pointer { get; }

        public static Utf8StringArrayScope Create(IAllocator allocator, IReadOnlyList<string> values) {
            ArgumentNullException.ThrowIfNull(allocator);
            ArgumentNullException.ThrowIfNull(values);

            if (0 == values.Count) {
                return new Utf8StringArrayScope(
                    allocator: allocator,
                    pointer: 0,
                    stringPointers: []
                );
            }

            var stringPointers = new nint[values.Count];
            var arrayPointer = allocator.Alloc(size: (IntPtr.Size * values.Count));

            try {
                for (var index = 0; (index < values.Count); index++) {
                    stringPointers[index] = Marshal.StringToCoTaskMemUTF8(s: values[index]);
                    Marshal.WriteIntPtr(
                        ofs: (index * IntPtr.Size),
                        ptr: arrayPointer,
                        val: stringPointers[index]
                    );
                }

                return new Utf8StringArrayScope(
                    allocator: allocator,
                    pointer: arrayPointer,
                    stringPointers: stringPointers
                );
            } catch {
                for (var index = 0; (index < stringPointers.Length); index++) {
                    if (0 != stringPointers[index]) {
                        Marshal.FreeCoTaskMem(ptr: stringPointers[index]);
                    }
                }

                allocator.Free(ptr: arrayPointer);
                throw;
            }
        }
        public void Dispose() {
            foreach (var stringPointer in m_stringPointers) {
                if (0 != stringPointer) {
                    Marshal.FreeCoTaskMem(ptr: stringPointer);
                }
            }

            if (0 != Pointer) {
                m_allocator.Free(ptr: Pointer);
            }
        }
    }
}
