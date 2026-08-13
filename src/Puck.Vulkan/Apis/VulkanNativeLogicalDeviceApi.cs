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
    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeLogicalDeviceApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeLogicalDeviceApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    private const uint VkStructureTypeDeviceCreateInfo = 3;
    private const uint VkStructureTypeDeviceQueueCreateInfo = 2;
    // Values verified against the Vulkan SDK 1.4.350 header (vulkan_core.h).
    private const uint VkStructureTypePhysicalDeviceFeatures2 = 1000059000;

    // VkPhysicalDeviceFeatures is 55 consecutive VkBool32 fields.
    private const int PhysicalDeviceFeatureCount = 55;

    // Every chained VkPhysicalDevice*Features struct shares the layout
    // { uint sType; nint pNext; VkBool32 flags[N]; ... } — sType at 0, pNext one pointer
    // in, the first feature flag two pointers in, the whole thing pointer-aligned.
    private static readonly int FeatureStructurePNextOffset = IntPtr.Size;
    private static readonly int FeatureStructureFlagOffset = (IntPtr.Size * 2);

    // A single over-sized, zeroed block per chained struct. We only enable the FIRST VkBool32 (the primary
    // feature) and require every trailing flag to read VK_FALSE; the driver reads exactly sizeof(struct) bytes
    // keyed off sType, so over-allocating is harmless but UNDER-allocating lets it read uninitialized memory
    // past the block — e.g. VkPhysicalDeviceAccelerationStructureFeaturesKHR is 5 flags / 40 bytes, past which a
    // too-small block reads the adjacent block's sType as a bogus VkBool32. 256 bytes comfortably exceeds any
    // current Vulkan feature struct (even the aggregate VkPhysicalDeviceVulkan1xFeatures).
    private const int FeatureStructureByteSize = 256;

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

    private InstancePointers GetInstancePointers(nint instanceHandle) {
        return m_instancePointers.GetOrAdd(
            key: instanceHandle,
            valueFactory: static handle => new InstancePointers {
                CreateDevice = (delegate* unmanaged[Cdecl]<nint, in VkDeviceCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveInstanceProc(instanceHandle: handle, functionName: "vkCreateDevice"u8),
            }
        );
    }
    private DevicePointers GetDevicePointers(nint deviceHandle) {
        return m_devicePointers.GetOrAdd(
            key: deviceHandle,
            valueFactory: static handle => new DevicePointers {
                DestroyDevice = (delegate* unmanaged[Cdecl]<nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDestroyDevice"u8),
                DeviceWaitIdle = (delegate* unmanaged[Cdecl]<nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDeviceWaitIdle"u8),
                GetDeviceQueue = (delegate* unmanaged[Cdecl]<nint, uint, uint, out nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkGetDeviceQueue"u8),
            }
        );
    }

    /// <inheritdoc/>
    public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out nint deviceHandle) {
        VulkanArgument.RequireHandle(
            handle: request.InstanceHandle,
            handleDescription: "instance",
            paramName: nameof(request)
        );

        var createDevice = GetInstancePointers(instanceHandle: request.InstanceHandle).CreateDevice;

        if (createDevice is null) {
            throw new InvalidOperationException(message: "vkCreateDevice is not available.");
        }

        var queueInfos = request.Queues.ToArray();
        var queueInfoSize = Marshal.SizeOf<VkDeviceQueueCreateInfo>();
        var queueInfoBuffer = m_allocator.Alloc(size: (queueInfoSize * queueInfos.Length));
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
                var queuePriority = m_allocator.Alloc(size: sizeof(float));

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
                    var block = m_allocator.Alloc(size: FeatureStructureByteSize);

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
                enabledFeaturesBuffer = m_allocator.Alloc(size: (PhysicalDeviceFeatureCount * sizeof(uint)));
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
                    m_allocator.Free(ptr: queueInfo.PQueuePriorities);
                }
            }

            m_allocator.Free(ptr: queueInfoBuffer);
            extensionBuffer.Dispose();
            foreach (var block in featureBlocks) {
                if (0 != block) {
                    m_allocator.Free(ptr: block);
                }
            }

            if (0 != enabledFeaturesBuffer) {
                m_allocator.Free(ptr: enabledFeaturesBuffer);
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
        VulkanArgument.RequireHandle(
            handle: deviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(deviceHandle)
        );

        var waitIdle = GetDevicePointers(deviceHandle: deviceHandle).DeviceWaitIdle;

        if (waitIdle is null) {
            throw new InvalidOperationException(message: "vkDeviceWaitIdle is not available.");
        }
        return waitIdle(deviceHandle);
    }
    /// <inheritdoc/>
    public nint GetDeviceQueue(nint deviceHandle, uint queueFamilyIndex, uint queueIndex) {
        VulkanArgument.RequireHandle(
            handle: deviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(deviceHandle)
        );

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

    private MarshalledStringArray MarshalStringArray(IReadOnlyList<string> values) {
        if (0 == values.Count) {
            return new MarshalledStringArray(
                Allocator: m_allocator,
                Entries: [],
                Pointer: 0
            );
        }

        var pointers = new nint[values.Count];
        var buffer = m_allocator.Alloc(size: (IntPtr.Size * values.Count));

        for (var index = 0; (index < values.Count); index++) {
            pointers[index] = MarshalUtf8(value: values[index]);
            Marshal.WriteIntPtr(
                ofs: (index * IntPtr.Size),
                ptr: buffer,
                val: pointers[index]
            );
        }

        return new MarshalledStringArray(
            Allocator: m_allocator,
            Entries: pointers,
            Pointer: buffer
        );
    }
    private nint MarshalUtf8(string value) {
        var bytes = Encoding.UTF8.GetBytes(s: (value + '\0'));
        var pointer = m_allocator.Alloc(size: bytes.Length);

        Marshal.Copy(
            destination: pointer,
            length: bytes.Length,
            source: bytes,
            startIndex: 0
        );
        return pointer;
    }

    private readonly record struct MarshalledStringArray(nint Pointer, IReadOnlyList<nint> Entries, IAllocator Allocator) : IDisposable {
        public void Dispose() {
            foreach (var entry in Entries) {
                if (0 != entry) {
                    Allocator.Free(ptr: entry);
                }
            }

            if (0 != Pointer) {
                Allocator.Free(ptr: Pointer);
            }
        }
    }
}
