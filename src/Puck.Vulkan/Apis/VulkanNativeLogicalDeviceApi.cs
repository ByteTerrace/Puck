using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanLogicalDeviceApi"/>, marshaling to the device-creation,
/// queue-retrieval, and wait-idle entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeLogicalDeviceApi : IVulkanLogicalDeviceApi {
    private const uint VkStructureTypeDeviceCreateInfo = 3;
    private const uint VkStructureTypeDeviceQueueCreateInfo = 2;
    // Values verified against the Vulkan SDK 1.4.350 header (vulkan_core.h).
    private const uint VkStructureTypePhysicalDeviceFeatures2 = 1000059000;

    // VkPhysicalDeviceFeatures is 55 consecutive VkBool32 fields.
    private const int PhysicalDeviceFeatureCount = 55;

    // Every chained VkPhysicalDevice*Features struct shares the layout
    // { uint sType; nint pNext; VkBool32 <feature>; ... } — sType at 0, pNext one pointer
    // in, the first feature flag two pointers in, the whole thing pointer-aligned.
    private static readonly int FeatureStructurePNextOffset = IntPtr.Size;
    private static readonly int FeatureStructureFlagOffset = (IntPtr.Size * 2);
    private static readonly int FeatureStructureByteSize = (IntPtr.Size * 3);
    private readonly Lock m_syncRoot = new();
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getInstanceProcAddr;
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    private unsafe struct InstancePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkDeviceCreateInfo, nint, out nint, VkResult> CreateDevice;
    }
    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, nint, void> DestroyDevice;
        public delegate* unmanaged[Cdecl]<nint, VkResult> DeviceWaitIdle;
        public delegate* unmanaged[Cdecl]<nint, uint, uint, out nint, void> GetDeviceQueue;
    }

    private readonly ConcurrentDictionary<nint, InstancePointers> m_instancePointers = new();
    private readonly ConcurrentDictionary<nint, DevicePointers> m_devicePointers = new();

    private unsafe InstancePointers GetInstancePointers(nint instanceHandle) {
        if (m_instancePointers.TryGetValue(
            key: instanceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetInstanceProcAddr();
        InstancePointers pNew = default;

        fixed (byte* pName = "vkCreateDevice"u8) {
            pNew.CreateDevice = (delegate* unmanaged[Cdecl]<nint, in VkDeviceCreateInfo, nint, out nint, VkResult>)getAddr(
                instanceHandle,
                pName
            );
        }
        m_instancePointers[instanceHandle] = pNew;
        return pNew;
    }
    private unsafe DevicePointers GetDevicePointers(nint deviceHandle) {
        if (m_devicePointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkDestroyDevice"u8) {
            pNew.DestroyDevice = (delegate* unmanaged[Cdecl]<nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDeviceWaitIdle"u8) {
            pNew.DeviceWaitIdle = (delegate* unmanaged[Cdecl]<nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetDeviceQueue"u8) {
            pNew.GetDeviceQueue = (delegate* unmanaged[Cdecl]<nint, uint, uint, out nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        m_devicePointers[deviceHandle] = pNew;
        return pNew;
    }

    /// <inheritdoc/>
    public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out nint deviceHandle) {
        if (0 == request.InstanceHandle) {
            throw new ArgumentException(
                message: "Vulkan instance handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        var createDevice = GetInstancePointers(instanceHandle: request.InstanceHandle).CreateDevice;

        if (createDevice is null) {
            throw new InvalidOperationException(message: "vkCreateDevice is not available.");
        }

        var queueInfos = request.Queues.ToArray();
        var queueInfoSize = Marshal.SizeOf<VkDeviceQueueCreateInfo>();
        var queueInfoBuffer = Puck.Memory.Allocator.Alloc(size: (queueInfoSize * queueInfos.Length));
        var extensionBuffer = MarshalStringArray(values: request.ExtensionNames);

        var featureIndices = request.EnabledFeatureIndices;
        var featureStructureTypes = request.EnabledFeatureStructureTypes;
        var hasFeatureChain = (featureStructureTypes.Count > 0);

        // Only allocated for the no-chain path; the chain path writes base features straight
        // into the VkPhysicalDeviceFeatures2.Features block instead.
        var enabledFeaturesBuffer = nint.Zero;
        var featureBlocks = new nint[(hasFeatureChain
            ? featureStructureTypes.Count
            : 0)];

        try {
            for (var index = 0; (index < queueInfos.Length); index++) {
                var queuePriority = Puck.Memory.Allocator.Alloc(size: sizeof(float));

                Marshal.Copy(
                    destination: queuePriority,
                    length: 1,
                    source: [queueInfos[index].Priority],
                    startIndex: 0
                );

                var queueInfo = new VkDeviceQueueCreateInfo {
                    PQueuePriorities = queuePriority,
                    QueueCount = 1,
                    QueueFamilyIndex = queueInfos[index].FamilyIndex,
                    SType = VkStructureTypeDeviceQueueCreateInfo,
                };

                Marshal.StructureToPtr(
                    fDeleteOld: false,
                    ptr: IntPtr.Add(
                        offset: (index * queueInfoSize),
                        pointer: queueInfoBuffer
                    ),
                    structure: queueInfo
                );
            }

            var createInfo = new VkDeviceCreateInfo {
                EnabledExtensionCount = (uint)request.ExtensionNames.Count,
                PQueueCreateInfos = queueInfoBuffer,
                PpEnabledExtensionNames = extensionBuffer.Pointer,
                QueueCreateInfoCount = (uint)queueInfos.Length,
                SType = VkStructureTypeDeviceCreateInfo,
            };

            // VkPhysicalDeviceFeatures2 is a stack local; vkCreateDevice consumes the whole
            // chain synchronously below, so its address (and the unmanaged blocks linked off
            // it) stay valid for the call.
            var features2 = new VkPhysicalDeviceFeatures2 {
                SType = VkStructureTypePhysicalDeviceFeatures2,
            };

            if (hasFeatureChain) {
                // Build the pNext chain generically: one { sType, pNext, VkBool32 = 1 } block
                // per requested feature struct. Order is immaterial to vkCreateDevice.
                nint chainHead = 0;

                for (var index = 0; (index < featureStructureTypes.Count); index++) {
                    var block = Puck.Memory.Allocator.Alloc(size: FeatureStructureByteSize);

                    featureBlocks[index] = block;
                    new Span<byte>(
                        length: FeatureStructureByteSize,
                        pointer: (void*)block
                    ).Clear();
                    Marshal.WriteInt32(
                        ofs: 0,
                        ptr: block,
                        val: unchecked((int)featureStructureTypes[index])
                    );
                    Marshal.WriteIntPtr(
                        ofs: FeatureStructurePNextOffset,
                        ptr: block,
                        val: chainHead
                    );
                    Marshal.WriteInt32(
                        ofs: FeatureStructureFlagOffset,
                        ptr: block,
                        val: 1
                    );
                    chainHead = block;
                }

                // The spec requires pEnabledFeatures to be null when a Features2 chain is
                // used; the base feature flags live in Features2.Features instead.
                for (var index = 0; (index < featureIndices.Count); index++) {
                    features2.Features[(int)featureIndices[index]] = 1u;
                }

                features2.PNext = chainHead;
                createInfo.PNext = (nint)(&features2);
            } else if (featureIndices.Count > 0) {
                enabledFeaturesBuffer = Puck.Memory.Allocator.Alloc(size: (PhysicalDeviceFeatureCount * sizeof(uint)));
                new Span<byte>(
                    length: (PhysicalDeviceFeatureCount * sizeof(uint)),
                    pointer: (void*)enabledFeaturesBuffer
                ).Clear();
                for (var index = 0; (index < featureIndices.Count); index++) {
                    Marshal.WriteInt32(
                        ofs: ((int)featureIndices[index] * sizeof(uint)),
                        ptr: enabledFeaturesBuffer,
                        val: 1
                    );
                }

                createInfo.PEnabledFeatures = enabledFeaturesBuffer;
            }

            return createDevice(
                request.PhysicalDevice.Handle,
                in createInfo,
                0,
                out deviceHandle
            );
        } finally {
            for (var index = 0; (index < queueInfos.Length); index++) {
                var queueInfo = Marshal.PtrToStructure<VkDeviceQueueCreateInfo>(ptr: IntPtr.Add(
                    offset: (index * queueInfoSize),
                    pointer: queueInfoBuffer
                ));

                if (0 != queueInfo.PQueuePriorities) {
                    Puck.Memory.Allocator.Free(ptr: queueInfo.PQueuePriorities);
                }
            }

            Puck.Memory.Allocator.Free(ptr: queueInfoBuffer);
            extensionBuffer.Dispose();
            foreach (var block in featureBlocks) {
                if (0 != block) {
                    Puck.Memory.Allocator.Free(ptr: block);
                }
            }

            if (0 != enabledFeaturesBuffer) {
                Puck.Memory.Allocator.Free(ptr: enabledFeaturesBuffer);
            }
        }
    }
    /// <inheritdoc/>
    public void DestroyDevice(nint deviceHandle) {
        if (0 == deviceHandle) {
            return;
        }

        var destroyDevice = GetDevicePointers(deviceHandle: deviceHandle).DestroyDevice;

        if (destroyDevice is not null) {
            destroyDevice(
                deviceHandle,
                0
            );
        }
    }
    /// <inheritdoc/>
    public VkResult WaitIdle(nint deviceHandle) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        var waitIdle = GetDevicePointers(deviceHandle: deviceHandle).DeviceWaitIdle;

        if (waitIdle is null) {
            throw new InvalidOperationException(message: "vkDeviceWaitIdle is not available.");
        }
        return waitIdle(deviceHandle);
    }
    /// <inheritdoc/>
    public nint GetDeviceQueue(nint deviceHandle, uint queueFamilyIndex, uint queueIndex) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        var getDeviceQueue = GetDevicePointers(deviceHandle: deviceHandle).GetDeviceQueue;

        if (getDeviceQueue is null) {
            throw new InvalidOperationException(message: "vkGetDeviceQueue is not available.");
        }
        getDeviceQueue(
            deviceHandle,
            queueFamilyIndex,
            queueIndex,
            out var queueHandle
        );
        return queueHandle;
    }

    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        return GetProcAddr(
            cached: ref m_getDeviceProcAddr,
            exportName: "vkGetDeviceProcAddr"
        );
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetInstanceProcAddr() {
        return GetProcAddr(
            cached: ref m_getInstanceProcAddr,
            exportName: "vkGetInstanceProcAddr"
        );
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetProcAddr(ref delegate* unmanaged[Cdecl]<nint, byte*, nint> cached, string exportName) {
        lock (m_syncRoot) {
            if (cached is not null) {
                return cached;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: exportName);

            cached = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return cached;
        }
    }
    private static MarshalledStringArray MarshalStringArray(IReadOnlyList<string> values) {
        if (0 == values.Count) {
            return new MarshalledStringArray(
                Entries: [],
                Pointer: 0
            );
        }

        var pointers = new nint[values.Count];
        var buffer = Puck.Memory.Allocator.Alloc(size: (IntPtr.Size * values.Count));

        for (var index = 0; (index < values.Count); index++) {
            pointers[index] = MarshalUtf8(value: values[index]);
            Marshal.WriteIntPtr(
                ofs: (index * IntPtr.Size),
                ptr: buffer,
                val: pointers[index]
            );
        }

        return new MarshalledStringArray(
            Entries: pointers,
            Pointer: buffer
        );
    }
    private static nint MarshalUtf8(string value) {
        var bytes = Encoding.UTF8.GetBytes(s: (value + '\0'));
        var pointer = Puck.Memory.Allocator.Alloc(size: bytes.Length);

        Marshal.Copy(
            destination: pointer,
            length: bytes.Length,
            source: bytes,
            startIndex: 0
        );
        return pointer;
    }

    private readonly record struct MarshalledStringArray(nint Pointer, IReadOnlyList<nint> Entries) : IDisposable {
        public void Dispose() {
            foreach (var entry in Entries) {
                if (0 != entry) {
                    Puck.Memory.Allocator.Free(ptr: entry);
                }
            }

            if (0 != Pointer) {
                Puck.Memory.Allocator.Free(ptr: Pointer);
            }
        }
    }
}
