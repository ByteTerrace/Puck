using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using static Puck.Vulkan.VulkanMarshalHelpers;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanComputePipelineApi"/>, marshaling to the compute-pipeline,
/// pipeline-layout, and descriptor-set-layout entry points resolved from the Vulkan loader. The compute
/// counterpart of <see cref="VulkanNativeGraphicsPipelineApi"/>, without the fixed-function graphics state.
/// </summary>
public unsafe sealed class VulkanNativeComputePipelineApi : IVulkanComputePipelineApi {
    private const uint ShaderStageComputeBit = 0x00000020;
    private const uint StructureTypeComputePipelineCreateInfo = 29;
    private const uint StructureTypeDescriptorSetLayoutCreateInfo = 32;
    private const uint StructureTypePipelineLayoutCreateInfo = 30;
    private const uint StructureTypePipelineShaderStageCreateInfo = 18;

    private readonly IAllocator m_allocator;
    private readonly Lock m_syncRoot = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeComputePipelineApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeComputePipelineApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    /// <inheritdoc/>
    public VkResult CreateComputePipeline(
        VulkanComputePipelineCreateRequest request,
        out nint descriptorSetLayoutHandle,
        out nint pipelineLayoutHandle,
        out nint pipelineHandle
    ) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(message: "Vulkan logical-device handle must be non-zero.", paramName: nameof(request));
        }

        if (0 == request.ShaderModuleHandle) {
            throw new ArgumentException(message: "Vulkan compute shader-module handle must be non-zero.", paramName: nameof(request));
        }

        var pointers = GetPointers(deviceHandle: request.DeviceHandle);

        descriptorSetLayoutHandle = 0;
        pipelineLayoutHandle = 0;
        pipelineHandle = 0;

        nint bindingsPointer = 0;
        nint setLayoutsPointer = 0;
        nint pushConstantRangePointer = 0;
        nint createInfoPointer = 0;
        var entryPoint = IntPtr.Zero;

        var pipelineLayoutCreateInfo = new VkPipelineLayoutCreateInfo {
            SType = StructureTypePipelineLayoutCreateInfo,
        };
        var bindings = (request.DescriptorBindings ?? []);

        try {
            if (bindings.Count > 0) {
                var stride = Marshal.SizeOf<VkDescriptorSetLayoutBinding>();

                bindingsPointer = m_allocator.Alloc(size: (stride * bindings.Count));

                for (var index = 0; (index < bindings.Count); index++) {
                    Marshal.StructureToPtr(
                        fDeleteOld: false,
                        ptr: (bindingsPointer + (index * stride)),
                        structure: bindings[index]
                    );
                }

                var setLayoutCreateInfo = new VkDescriptorSetLayoutCreateInfo {
                    BindingCount = (uint)bindings.Count,
                    PBindings = bindingsPointer,
                    SType = StructureTypeDescriptorSetLayoutCreateInfo,
                };
                var setLayoutResult = pointers.CreateDescriptorSetLayout(
                    request.DeviceHandle,
                    in setLayoutCreateInfo,
                    0,
                    out descriptorSetLayoutHandle
                );

                if (!setLayoutResult.IsSuccess()) {
                    return setLayoutResult;
                }

                setLayoutsPointer = m_allocator.Alloc(size: IntPtr.Size);
                Marshal.WriteIntPtr(ptr: setLayoutsPointer, val: descriptorSetLayoutHandle);
                pipelineLayoutCreateInfo.SetLayoutCount = 1;
                pipelineLayoutCreateInfo.PSetLayouts = setLayoutsPointer;
            }

            if (request.PushConstantSize > 0) {
                if (0 == request.PushConstantStageFlags) {
                    throw new ArgumentException(
                        message: "Push-constant stage flags must be non-zero when a push-constant range is requested.",
                        paramName: nameof(request)
                    );
                }

                pushConstantRangePointer = AllocateStruct(allocator: m_allocator, value: new VkPushConstantRange {
                    Offset = 0,
                    Size = request.PushConstantSize,
                    StageFlags = request.PushConstantStageFlags,
                });
                pipelineLayoutCreateInfo.PushConstantRangeCount = 1;
                pipelineLayoutCreateInfo.PPushConstantRanges = pushConstantRangePointer;
            }

            var layoutResult = pointers.CreatePipelineLayout(
                request.DeviceHandle,
                in pipelineLayoutCreateInfo,
                0,
                out pipelineLayoutHandle
            );

            if (!layoutResult.IsSuccess()) {
                if (0 != descriptorSetLayoutHandle) {
                    DestroyDescriptorSetLayout(deviceHandle: request.DeviceHandle, descriptorSetLayoutHandle: descriptorSetLayoutHandle);
                    descriptorSetLayoutHandle = 0;
                }

                pipelineLayoutHandle = 0;
                return layoutResult;
            }

            entryPoint = Marshal.StringToCoTaskMemUTF8(s: "main");

            var createInfo = new VkComputePipelineCreateInfo {
                BasePipelineHandle = 0,
                BasePipelineIndex = -1,
                Layout = pipelineLayoutHandle,
                SType = StructureTypeComputePipelineCreateInfo,
                Stage = new VkPipelineShaderStageCreateInfo {
                    Module = request.ShaderModuleHandle,
                    PName = entryPoint,
                    SType = StructureTypePipelineShaderStageCreateInfo,
                    Stage = ShaderStageComputeBit,
                },
            };

            createInfoPointer = AllocateStruct(allocator: m_allocator, value: createInfo);

            var result = pointers.CreateComputePipelines(
                request.DeviceHandle,
                0,
                1,
                createInfoPointer,
                0,
                out pipelineHandle
            );

            if (!result.IsSuccess()) {
                DestroyPipelineLayout(deviceHandle: request.DeviceHandle, pipelineLayoutHandle: pipelineLayoutHandle);

                if (0 != descriptorSetLayoutHandle) {
                    DestroyDescriptorSetLayout(deviceHandle: request.DeviceHandle, descriptorSetLayoutHandle: descriptorSetLayoutHandle);
                    descriptorSetLayoutHandle = 0;
                }

                pipelineLayoutHandle = 0;
                pipelineHandle = 0;
            }

            return result;
        } finally {
            if (0 != bindingsPointer) {
                m_allocator.Free(ptr: bindingsPointer);
            }

            if (0 != setLayoutsPointer) {
                m_allocator.Free(ptr: setLayoutsPointer);
            }

            if (0 != pushConstantRangePointer) {
                m_allocator.Free(ptr: pushConstantRangePointer);
            }

            if (0 != createInfoPointer) {
                m_allocator.Free(ptr: createInfoPointer);
            }

            if (IntPtr.Zero != entryPoint) {
                Marshal.FreeCoTaskMem(ptr: entryPoint);
            }
        }
    }
    /// <inheritdoc/>
    public void DestroyDescriptorSetLayout(nint deviceHandle, nint descriptorSetLayoutHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == descriptorSetLayoutHandle)
        ) {
            return;
        }

        GetPointers(deviceHandle: deviceHandle).DestroyDescriptorSetLayout(
            deviceHandle,
            descriptorSetLayoutHandle,
            0
        );
    }
    /// <inheritdoc/>
    public void DestroyPipeline(nint deviceHandle, nint pipelineHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == pipelineHandle)
        ) {
            return;
        }

        GetPointers(deviceHandle: deviceHandle).DestroyPipeline(
            deviceHandle,
            pipelineHandle,
            0
        );
    }
    /// <inheritdoc/>
    public void DestroyPipelineLayout(nint deviceHandle, nint pipelineLayoutHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == pipelineLayoutHandle)
        ) {
            return;
        }

        GetPointers(deviceHandle: deviceHandle).DestroyPipelineLayout(
            deviceHandle,
            pipelineLayoutHandle,
            0
        );
    }

    private struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, nint, uint, nint, nint, out nint, VkResult> CreateComputePipelines;
        public delegate* unmanaged[Cdecl]<nint, in VkPipelineLayoutCreateInfo, nint, out nint, VkResult> CreatePipelineLayout;
        public delegate* unmanaged[Cdecl]<nint, in VkDescriptorSetLayoutCreateInfo, nint, out nint, VkResult> CreateDescriptorSetLayout;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyPipeline;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyDescriptorSetLayout;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyPipelineLayout;
    }

    private DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }

        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkCreateComputePipelines"u8) {
            pNew.CreateComputePipelines = (delegate* unmanaged[Cdecl]<nint, nint, uint, nint, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCreatePipelineLayout"u8) {
            pNew.CreatePipelineLayout = (delegate* unmanaged[Cdecl]<nint, in VkPipelineLayoutCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCreateDescriptorSetLayout"u8) {
            pNew.CreateDescriptorSetLayout = (delegate* unmanaged[Cdecl]<nint, in VkDescriptorSetLayoutCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyPipeline"u8) {
            pNew.DestroyPipeline = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyDescriptorSetLayout"u8) {
            pNew.DestroyDescriptorSetLayout = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyPipelineLayout"u8) {
            pNew.DestroyPipelineLayout = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }

        m_pointers[deviceHandle] = pNew;
        return pNew;
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (m_syncRoot) {
            if (m_getDeviceProcAddr is not null) {
                return m_getDeviceProcAddr;
            }

            m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");
            return m_getDeviceProcAddr;
        }
    }
}
