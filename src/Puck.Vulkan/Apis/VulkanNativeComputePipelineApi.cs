using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanComputePipelineApi"/>, marshaling to the compute-pipeline entry
/// point resolved from the Vulkan loader, with its layouts made by <see cref="VulkanPipelineLayouts"/>. The compute
/// counterpart of <see cref="VulkanNativeGraphicsPipelineApi"/>, without the fixed-function graphics state.
/// </summary>
public unsafe sealed class VulkanNativeComputePipelineApi : IVulkanComputePipelineApi {
    private const uint ShaderStageComputeBit = 0x00000020;
    private const uint StructureTypeComputePipelineCreateInfo = 29;
    private const uint StructureTypePipelineShaderStageCreateInfo = 18;

    private readonly IAllocator m_allocator;

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
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.ShaderModuleHandle,
            handleDescription: "compute shader-module",
            paramName: nameof(request)
        );

        pipelineHandle = 0;

        // A layout the caller created stays the caller's, so a failed creation destroys only what it made here.
        var ownsLayout = (0 == request.PipelineLayoutHandle);

        if (ownsLayout) {
            var layoutResult = VulkanPipelineLayouts.Create(
                allocator: m_allocator,
                bindings: request.DescriptorBindings,
                descriptorSetLayoutHandle: out descriptorSetLayoutHandle,
                device: request.Device,
                pipelineLayoutHandle: out pipelineLayoutHandle,
                pushConstantSize: request.PushConstantSize,
                pushConstantStageFlags: request.PushConstantStageFlags
            );

            if (!layoutResult.IsSuccess()) {
                return layoutResult;
            }
        } else {
            descriptorSetLayoutHandle = 0;
            pipelineLayoutHandle = request.PipelineLayoutHandle;
        }

        // Creation feedback (core in Vulkan 1.3) says whether the pipeline cache answered; the stack structs outlive
        // the create call that reads them.
        var feedback = default(VkPipelineCreationFeedback);
        var stageFeedback = default(VkPipelineCreationFeedback);
        var feedbackInfo = new VkPipelineCreationFeedbackCreateInfo {
            PPipelineCreationFeedback = ((nint)(&feedback)),
            PPipelineStageCreationFeedbacks = ((nint)(&stageFeedback)),
            PipelineStageCreationFeedbackCount = 1,
            SType = VkPipelineCreationFeedbackCreateInfo.StructureType,
        };
        var createInfo = new VkComputePipelineCreateInfo {
            BasePipelineHandle = 0,
            BasePipelineIndex = -1,
            Layout = pipelineLayoutHandle,
            PNext = ((nint)(&feedbackInfo)),
            SType = StructureTypeComputePipelineCreateInfo,
            Stage = new VkPipelineShaderStageCreateInfo {
                Module = request.ShaderModuleHandle,
                PName = VulkanMarshalHelpers.MainEntryPoint,
                SType = StructureTypePipelineShaderStageCreateInfo,
                Stage = ShaderStageComputeBit,
            },
        };
        var result = request.Device.CreateComputePipelines(
            request.Device.Handle,
            (request.PipelineCache?.Handle ?? 0),
            1,
            ((nint)(&createInfo)),
            0,
            out pipelineHandle
        );

        if (result.IsSuccess()) {
            request.PipelineCache?.Count(feedback: in feedback);
        } else {
            if (ownsLayout) {
                VulkanPipelineLayouts.Destroy(
                    descriptorSetLayoutHandle: descriptorSetLayoutHandle,
                    device: request.Device,
                    pipelineLayoutHandle: pipelineLayoutHandle
                );
            }

            descriptorSetLayoutHandle = 0;
            pipelineLayoutHandle = 0;
            pipelineHandle = 0;
        }

        return result;
    }
    /// <inheritdoc/>
    public void DestroyPipeline(VulkanDeviceCommands device, nint pipelineHandle) =>
        device?.Destroy(
            destroy: device.DestroyPipeline,
            handle: pipelineHandle
        );
}
