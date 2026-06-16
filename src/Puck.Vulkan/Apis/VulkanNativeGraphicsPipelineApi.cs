using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using static Puck.Vulkan.VulkanMarshalHelpers;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanGraphicsPipelineApi"/>, marshaling to the pipeline,
/// pipeline-layout, and descriptor-set-layout entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeGraphicsPipelineApi : IVulkanGraphicsPipelineApi {
    private const uint False = 0;
    private const uint LogicOpCopy = 3;
    private const uint ShaderStageFragmentBit = 0x00000010;
    private const uint ShaderStageVertexBit = 0x00000001;
    private const uint StructureTypeDescriptorSetLayoutCreateInfo = 32;
    private const uint StructureTypeGraphicsPipelineCreateInfo = 28;
    private const uint StructureTypePipelineColorBlendStateCreateInfo = 26;
    private const uint StructureTypePipelineDynamicStateCreateInfo = 27;
    private const uint StructureTypePipelineInputAssemblyStateCreateInfo = 20;
    private const uint StructureTypePipelineLayoutCreateInfo = 30;
    private const uint StructureTypePipelineShaderStageCreateInfo = 18;
    private const uint StructureTypePipelineVertexInputStateCreateInfo = 19;
    private const uint StructureTypePipelineViewportStateCreateInfo = 22;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    /// <inheritdoc/>
    public VkResult CreateGraphicsPipeline(
        VulkanGraphicsPipelineCreateRequest request,
        out nint descriptorSetLayoutHandle,
        out nint pipelineLayoutHandle,
        out nint pipelineHandle
    ) {
        ValidateRequest(request: request);

        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
        var createDescriptorSetLayout = pointers.CreateDescriptorSetLayout;
        var createPipelineLayout = pointers.CreatePipelineLayout;
        var createGraphicsPipelines = pointers.CreateGraphicsPipelines;

        var pipelineLayoutCreateInfo = new VkPipelineLayoutCreateInfo {
            SType = StructureTypePipelineLayoutCreateInfo,
        };

        descriptorSetLayoutHandle = 0;
        nint descriptorSetLayoutBindingPointer = 0;
        nint setLayoutsPointer = 0;
        nint pushConstantRangePointer = 0;

        var setLayoutCount = 0;
        var descriptorBindings = (request.DescriptorBindings ?? []);

        if (descriptorBindings.Count > 0) {
            setLayoutCount = 1;
            setLayoutsPointer = Puck.Memory.Allocator.Alloc(size: (IntPtr.Size * setLayoutCount));

            var bindingStride = Marshal.SizeOf<VkDescriptorSetLayoutBinding>();

            descriptorSetLayoutBindingPointer = Puck.Memory.Allocator.Alloc(size: (bindingStride * descriptorBindings.Count));

            for (var index = 0; (index < descriptorBindings.Count); index++) {
                Marshal.StructureToPtr(
                    fDeleteOld: false,
                    ptr: (descriptorSetLayoutBindingPointer + (index * bindingStride)),
                    structure: descriptorBindings[index]
                );
            }

            var descriptorSetLayoutCreateInfo = new VkDescriptorSetLayoutCreateInfo {
                BindingCount = (uint)descriptorBindings.Count,
                PBindings = descriptorSetLayoutBindingPointer,
                SType = StructureTypeDescriptorSetLayoutCreateInfo,
            };
            var descriptorSetLayoutResult = createDescriptorSetLayout(
                request.DeviceHandle,
                in descriptorSetLayoutCreateInfo,
                0,
                out descriptorSetLayoutHandle
            );

            if (!descriptorSetLayoutResult.IsSuccess()) {
                FreeIfAllocated(
                    pointer: setLayoutsPointer,
                    structureType: null
                );
                FreeIfAllocated(
                    pointer: descriptorSetLayoutBindingPointer,
                    structureType: typeof(VkDescriptorSetLayoutBinding)
                );
                pipelineLayoutHandle = 0;
                pipelineHandle = 0;
                return descriptorSetLayoutResult;
            }

            Marshal.WriteIntPtr(
                ptr: setLayoutsPointer,
                val: descriptorSetLayoutHandle
            );

            pipelineLayoutCreateInfo.SetLayoutCount = (uint)setLayoutCount;
            pipelineLayoutCreateInfo.PSetLayouts = setLayoutsPointer;
        }

        if (request.PushConstantSize > 0) {
            if (0 == request.PushConstantStageFlags) {
                throw new ArgumentException(
                    message: "Push-constant stage flags must be non-zero when a push-constant range is requested.",
                    paramName: nameof(request)
                );
            }

            pushConstantRangePointer = AllocateStruct(value: new VkPushConstantRange {
                Offset = 0,
                Size = request.PushConstantSize,
                StageFlags = request.PushConstantStageFlags,
            });
            pipelineLayoutCreateInfo.PushConstantRangeCount = 1;
            pipelineLayoutCreateInfo.PPushConstantRanges = pushConstantRangePointer;
        }

        var mainEntryPoint = IntPtr.Zero;
        var stagesPointer = IntPtr.Zero;
        var viewportPointer = IntPtr.Zero;
        var scissorPointer = IntPtr.Zero;
        var dynamicStatesPointer = IntPtr.Zero;
        var pipelineCreateInfoPointer = IntPtr.Zero;

        try {
            var layoutResult = createPipelineLayout(
                request.DeviceHandle,
                in pipelineLayoutCreateInfo,
                0,
                out pipelineLayoutHandle
            );

            if (!layoutResult.IsSuccess()) {
                if (0 != descriptorSetLayoutHandle) {
                    DestroyDescriptorSetLayout(
                        descriptorSetLayoutHandle: descriptorSetLayoutHandle,
                        deviceHandle: request.DeviceHandle
                    );
                    descriptorSetLayoutHandle = 0;
                }

                pipelineHandle = 0;
                return layoutResult;
            }

            pipelineHandle = 0;
            mainEntryPoint = Marshal.StringToCoTaskMemUTF8(s: "main");

            stagesPointer = Puck.Memory.Allocator.Alloc(size: (Marshal.SizeOf<VkPipelineShaderStageCreateInfo>() * 2));
            var vertexStage = new VkPipelineShaderStageCreateInfo {
                Module = request.VertexShaderModuleHandle,
                PName = mainEntryPoint,
                SType = StructureTypePipelineShaderStageCreateInfo,
                Stage = ShaderStageVertexBit,
            };

            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: stagesPointer,
                structure: vertexStage
            );

            var fragmentStage = new VkPipelineShaderStageCreateInfo {
                Module = request.FragmentShaderModuleHandle,
                PName = mainEntryPoint,
                SType = StructureTypePipelineShaderStageCreateInfo,
                Stage = ShaderStageFragmentBit,
            };

            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: IntPtr.Add(
                    offset: Marshal.SizeOf<VkPipelineShaderStageCreateInfo>(),
                    pointer: stagesPointer
                ),
                structure: fragmentStage
            );

            var vertexInputState = new VkPipelineVertexInputStateCreateInfo {
                SType = StructureTypePipelineVertexInputStateCreateInfo,
            };
            var vertexBindings = (request.VertexBindings ?? []);
            var vertexAttributes = (request.VertexAttributes ?? []);

            if (vertexBindings.Count > 0) {
                var stride = Marshal.SizeOf<VkVertexInputBindingDescription>();
                var bindingsPointer = Puck.Memory.Allocator.Alloc(size: (stride * vertexBindings.Count));

                for (var index = 0; (index < vertexBindings.Count); index++) {
                    Marshal.StructureToPtr(
                        fDeleteOld: false,
                        ptr: (bindingsPointer + (index * stride)),
                        structure: vertexBindings[index]
                    );
                }

                vertexInputState.VertexBindingDescriptionCount = (uint)vertexBindings.Count;
                vertexInputState.PVertexBindingDescriptions = bindingsPointer;
            }

            if (vertexAttributes.Count > 0) {
                var stride = Marshal.SizeOf<VkVertexInputAttributeDescription>();
                var attributesPointer = Puck.Memory.Allocator.Alloc(size: (stride * vertexAttributes.Count));

                for (var index = 0; (index < vertexAttributes.Count); index++) {
                    Marshal.StructureToPtr(
                        fDeleteOld: false,
                        ptr: (attributesPointer + (index * stride)),
                        structure: vertexAttributes[index]
                    );
                }

                vertexInputState.VertexAttributeDescriptionCount = (uint)vertexAttributes.Count;
                vertexInputState.PVertexAttributeDescriptions = attributesPointer;
            }

            var inputAssemblyState = new VkPipelineInputAssemblyStateCreateInfo {
                PrimitiveRestartEnable = False,
                SType = StructureTypePipelineInputAssemblyStateCreateInfo,
                Topology = request.Topology,
            };

            viewportPointer = Puck.Memory.Allocator.Alloc(size: Marshal.SizeOf<VkViewport>());
            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: viewportPointer,
                structure: new VkViewport(
                    height: request.Height,
                    maxDepth: 1f,
                    minDepth: 0f,
                    width: request.Width,
                    x: 0f,
                    y: 0f
                )
            );
            scissorPointer = Puck.Memory.Allocator.Alloc(size: Marshal.SizeOf<VkRect2D>());
            Marshal.StructureToPtr(
                fDeleteOld: false,
                ptr: scissorPointer,
                structure: new VkRect2D(
                    extent: new VkExtent2D(
                        height: request.Height,
                        width: request.Width
                    ),
                    offset: new VkOffset2D(
                        x: 0,
                        y: 0
                    )
                )
            );
            var viewportState = new VkPipelineViewportStateCreateInfo {
                PScissors = scissorPointer,
                PViewports = viewportPointer,
                SType = StructureTypePipelineViewportStateCreateInfo,
                ScissorCount = 1,
                ViewportCount = 1,
            };

            var dynamicStates = (request.DynamicStates ?? []);
            var dynamicStateCount = dynamicStates.Count;

            if (dynamicStateCount > 0) {
                dynamicStatesPointer = Puck.Memory.Allocator.Alloc(size: (sizeof(uint) * dynamicStateCount));
                for (var index = 0; (index < dynamicStateCount); index++) {
                    Marshal.WriteInt32(
                        ofs: (index * sizeof(uint)),
                        ptr: dynamicStatesPointer,
                        val: unchecked((int)dynamicStates[index])
                    );
                }
            }

            var dynamicState = new VkPipelineDynamicStateCreateInfo {
                DynamicStateCount = (uint)dynamicStateCount,
                PDynamicStates = dynamicStatesPointer,
                SType = StructureTypePipelineDynamicStateCreateInfo,
            };
            var rasterizationState = request.Rasterization;
            var multisampleState = request.Multisample;
            var colorBlendAttachments = (request.ColorBlendAttachments ?? []);
            var colorBlendStride = Marshal.SizeOf<VkPipelineColorBlendAttachmentState>();
            var colorBlendState = new VkPipelineColorBlendStateCreateInfo {
                AttachmentCount = (uint)colorBlendAttachments.Count,
                LogicOp = LogicOpCopy,
                LogicOpEnable = False,
                PAttachments = ((colorBlendAttachments.Count > 0)
                    ? Puck.Memory.Allocator.Alloc(size: (colorBlendStride * colorBlendAttachments.Count))
                    : nint.Zero),
                SType = StructureTypePipelineColorBlendStateCreateInfo,
            };

            for (var index = 0; (index < colorBlendAttachments.Count); index++) {
                Marshal.StructureToPtr(
                    fDeleteOld: false,
                    ptr: (colorBlendState.PAttachments + (index * colorBlendStride)),
                    structure: colorBlendAttachments[index]
                );
            }

            try {
                var pipelineCreateInfo = new VkGraphicsPipelineCreateInfo {
                    BasePipelineHandle = 0,
                    BasePipelineIndex = -1,
                    Layout = pipelineLayoutHandle,
                    PColorBlendState = AllocateStruct(value: colorBlendState),
                    PDynamicState = AllocateStruct(value: dynamicState),
                    PInputAssemblyState = AllocateStruct(value: inputAssemblyState),
                    PMultisampleState = AllocateStruct(value: multisampleState),
                    PRasterizationState = AllocateStruct(value: rasterizationState),
                    PStages = stagesPointer,
                    PVertexInputState = AllocateStruct(value: vertexInputState),
                    PViewportState = AllocateStruct(value: viewportState),
                    RenderPass = request.RenderPassHandle,
                    SType = StructureTypeGraphicsPipelineCreateInfo,
                    StageCount = 2,
                    Subpass = 0,
                };

                pipelineCreateInfoPointer = AllocateStruct(value: pipelineCreateInfo);
                var result = createGraphicsPipelines(
                    request.DeviceHandle,
                    0,
                    1,
                    pipelineCreateInfoPointer,
                    0,
                    out pipelineHandle
                );

                if (!result.IsSuccess()) {
                    DestroyPipelineLayout(
                        deviceHandle: request.DeviceHandle,
                        pipelineLayoutHandle: pipelineLayoutHandle
                    );
                    if (0 != descriptorSetLayoutHandle) {
                        DestroyDescriptorSetLayout(
                            descriptorSetLayoutHandle: descriptorSetLayoutHandle,
                            deviceHandle: request.DeviceHandle
                        );
                        descriptorSetLayoutHandle = 0;
                    }

                    pipelineLayoutHandle = 0;
                }

                return result;
            } finally {
                if (0 != colorBlendState.PAttachments) {
                    Puck.Memory.Allocator.Free(ptr: colorBlendState.PAttachments);
                }
            }
        } finally {
            FreeIfAllocated(
                pointer: setLayoutsPointer,
                structureType: null
            );
            FreeIfAllocated(
                pointer: descriptorSetLayoutBindingPointer,
                structureType: typeof(VkDescriptorSetLayoutBinding)
            );
            FreeIfAllocated(
                pointer: pushConstantRangePointer,
                structureType: typeof(VkPushConstantRange)
            );
            FreeIfAllocated(
                pointer: pipelineCreateInfoPointer,
                structureType: typeof(VkGraphicsPipelineCreateInfo)
            );
            Marshal.FreeCoTaskMem(ptr: mainEntryPoint);
            FreeIfAllocated(
                pointer: scissorPointer,
                structureType: typeof(VkRect2D)
            );
            FreeIfAllocated(
                pointer: viewportPointer,
                structureType: typeof(VkViewport)
            );
            FreeIfAllocated(
                pointer: stagesPointer,
                structureType: typeof(VkPipelineShaderStageCreateInfo)
            );
        }
    }
    /// <inheritdoc/>
    public void DestroyPipeline(nint deviceHandle, nint pipelineHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == pipelineHandle)
        ) {
            return;
        }

        var destroyPipeline = GetPointers(deviceHandle: deviceHandle).DestroyPipeline;

        destroyPipeline(
            deviceHandle,
            pipelineHandle,
            0
        );
    }
    /// <inheritdoc/>
    public void DestroyDescriptorSetLayout(nint deviceHandle, nint descriptorSetLayoutHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == descriptorSetLayoutHandle)
        ) {
            return;
        }

        var destroyDescriptorSetLayout = GetPointers(deviceHandle: deviceHandle).DestroyDescriptorSetLayout;

        destroyDescriptorSetLayout(
            deviceHandle,
            descriptorSetLayoutHandle,
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

        var destroyPipelineLayout = GetPointers(deviceHandle: deviceHandle).DestroyPipelineLayout;

        destroyPipelineLayout(
            deviceHandle,
            pipelineLayoutHandle,
            0
        );
    }

    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, nint, uint, nint, nint, out nint, VkResult> CreateGraphicsPipelines;
        public delegate* unmanaged[Cdecl]<nint, in VkPipelineLayoutCreateInfo, nint, out nint, VkResult> CreatePipelineLayout;
        public delegate* unmanaged[Cdecl]<nint, in VkDescriptorSetLayoutCreateInfo, nint, out nint, VkResult> CreateDescriptorSetLayout;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyPipeline;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyDescriptorSetLayout;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyPipelineLayout;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();

    private unsafe DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkCreateGraphicsPipelines"u8) {
            pNew.CreateGraphicsPipelines = (delegate* unmanaged[Cdecl]<nint, nint, uint, nint, nint, out nint, VkResult>)getAddr(
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
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (m_syncRoot) {
            if (m_getDeviceProcAddr is not null) {
                return m_getDeviceProcAddr;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");

            m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getDeviceProcAddr;
        }
    }
    private static unsafe void FreeIfAllocated(nint pointer, Type? structureType) {
        if (0 == pointer) {
            return;
        }

        if (structureType == typeof(VkGraphicsPipelineCreateInfo)) {
            var info = Marshal.PtrToStructure<VkGraphicsPipelineCreateInfo>(ptr: pointer);

            FreeIfAllocated(
                pointer: info.PDynamicState,
                structureType: typeof(VkPipelineDynamicStateCreateInfo)
            );
            FreeIfAllocated(
                pointer: info.PColorBlendState,
                structureType: typeof(VkPipelineColorBlendStateCreateInfo)
            );
            FreeIfAllocated(
                pointer: info.PMultisampleState,
                structureType: typeof(VkPipelineMultisampleStateCreateInfo)
            );
            FreeIfAllocated(
                pointer: info.PRasterizationState,
                structureType: typeof(VkPipelineRasterizationStateCreateInfo)
            );
            FreeIfAllocated(
                pointer: info.PViewportState,
                structureType: typeof(VkPipelineViewportStateCreateInfo)
            );
            FreeIfAllocated(
                pointer: info.PInputAssemblyState,
                structureType: typeof(VkPipelineInputAssemblyStateCreateInfo)
            );
            FreeIfAllocated(
                pointer: info.PVertexInputState,
                structureType: typeof(VkPipelineVertexInputStateCreateInfo)
            );
        }

        if (structureType == typeof(VkPipelineDynamicStateCreateInfo)) {
            var info = Marshal.PtrToStructure<VkPipelineDynamicStateCreateInfo>(ptr: pointer);

            FreeIfAllocated(
                pointer: info.PDynamicStates,
                structureType: null
            );
        }

        if (structureType == typeof(VkPipelineVertexInputStateCreateInfo)) {
            var info = Marshal.PtrToStructure<VkPipelineVertexInputStateCreateInfo>(ptr: pointer);

            FreeIfAllocated(
                pointer: info.PVertexBindingDescriptions,
                structureType: typeof(VkVertexInputBindingDescription)
            );
            FreeIfAllocated(
                pointer: info.PVertexAttributeDescriptions,
                structureType: typeof(VkVertexInputAttributeDescription)
            );
        }

        Puck.Memory.Allocator.Free(ptr: pointer);
    }
    private static unsafe void ValidateRequest(VulkanGraphicsPipelineCreateRequest request) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.RenderPassHandle) {
            throw new ArgumentException(
                message: "Vulkan render-pass handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.VertexShaderModuleHandle) {
            throw new ArgumentException(
                message: "Vulkan vertex shader-module handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.FragmentShaderModuleHandle) {
            throw new ArgumentException(
                message: "Vulkan fragment shader-module handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.Width) {
            throw new ArgumentOutOfRangeException(
                message: "Vulkan graphics-pipeline width must be greater than zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.Height) {
            throw new ArgumentOutOfRangeException(
                message: "Vulkan graphics-pipeline height must be greater than zero.",
                paramName: nameof(request)
            );
        }
    }
}
