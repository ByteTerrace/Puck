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
    private const uint VkStructureTypeApplicationInfo = 0;
    private const uint VkStructureTypeInstanceCreateInfo = 1;
    // Request Vulkan 1.2 to make core vkGetPhysicalDeviceFeatures2, core
    // buffer-device-address, and SPIR-V 1.4+ shader modules available (needed by optional
    // ray-query pipelines). Pre-1.1 loaders, which would reject a higher requested version,
    // are effectively extinct.
    private const uint VulkanApiVersion12 = (1u << 22) | (2u << 12);

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<in VkInstanceCreateInfo, nint, out nint, VkResult> m_createInstance;
    private unsafe delegate* unmanaged[Cdecl]<nint, nint, void> m_destroyInstance;

    /// <inheritdoc/>
    public VkResult CreateInstance(VulkanInstanceCreateRequest request, out nint instanceHandle) {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApplicationName);

        instanceHandle = 0;

        using var applicationName = Utf8StringScope.Create(value: request.ApplicationName);
        using var engineName = Utf8StringScope.Create(value: "Puck");
        using var extensionNames = Utf8StringArrayScope.Create(values: request.ExtensionNames);
        using var layerNames = Utf8StringArrayScope.Create(values: request.LayerNames);

        var appInfo = new VkApplicationInfo {
            ApiVersion = VulkanApiVersion12,
            ApplicationName = applicationName.Pointer,
            EngineName = engineName.Pointer,
            StructureType = VkStructureTypeApplicationInfo,
        };

        var createInfo = new VkInstanceCreateInfo {
            ApplicationInfo = Puck.Memory.Allocator.Alloc(size: Marshal.SizeOf<VkApplicationInfo>()),
            EnabledExtensionCount = checked((uint)request.ExtensionNames.Count),
            EnabledExtensionNames = extensionNames.Pointer,
            EnabledLayerCount = checked((uint)request.LayerNames.Count),
            EnabledLayerNames = layerNames.Pointer,
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
                Puck.Memory.Allocator.Free(ptr: createInfo.ApplicationInfo);
            }
        }
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
        private readonly nint[] m_stringPointers;

        private Utf8StringArrayScope(nint pointer, nint[] stringPointers) {
            Pointer = pointer;
            m_stringPointers = stringPointers;
        }

        public nint Pointer { get; }

        public static Utf8StringArrayScope Create(IReadOnlyList<string> values) {
            ArgumentNullException.ThrowIfNull(values);

            if (0 == values.Count) {
                return new Utf8StringArrayScope(
                    pointer: 0,
                    stringPointers: []
                );
            }

            var stringPointers = new nint[values.Count];
            var arrayPointer = Puck.Memory.Allocator.Alloc(size: (IntPtr.Size * values.Count));

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
                    pointer: arrayPointer,
                    stringPointers: stringPointers
                );
            } catch {
                for (var index = 0; (index < stringPointers.Length); index++) {
                    if (0 != stringPointers[index]) {
                        Marshal.FreeCoTaskMem(ptr: stringPointers[index]);
                    }
                }

                Puck.Memory.Allocator.Free(ptr: arrayPointer);
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
                Puck.Memory.Allocator.Free(ptr: Pointer);
            }
        }
    }
}
