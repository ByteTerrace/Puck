using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// The one way the compute and graphics pipeline APIs create and destroy a pipeline layout: an optional descriptor
/// set layout over the pipeline's bindings, and a pipeline layout over that set and an optional push-constant range.
/// </summary>
public static unsafe class VulkanPipelineLayouts {
    private const uint StructureTypeDescriptorSetLayoutCreateInfo = 32;
    private const uint StructureTypePipelineLayoutCreateInfo = 30;

    /// <summary>Creates a pipeline layout, with a descriptor set layout when <paramref name="bindings"/> is non-empty. On a
    /// failed result both handles are zero and nothing created here is left alive.</summary>
    /// <param name="allocator">The unmanaged allocator that marshals the bindings.</param>
    /// <param name="device">The command table of the logical device that owns the layouts.</param>
    /// <param name="bindings">The descriptor bindings of set zero; empty for a pipeline that binds no descriptors.</param>
    /// <param name="pushConstantSize">The size, in bytes, of the push-constant range at offset zero; zero for none.</param>
    /// <param name="pushConstantStageFlags">A bitmask of <c>VkShaderStageFlagBits</c> that read the push-constant range.</param>
    /// <param name="descriptorSetLayoutHandle">When this method returns, the native <c>VkDescriptorSetLayout</c> handle, or zero when no bindings were given or creation failed.</param>
    /// <param name="pipelineLayoutHandle">When this method returns, the native <c>VkPipelineLayout</c> handle, or zero when creation failed.</param>
    /// <returns>The first failing <see cref="VkResult"/>, or the pipeline layout's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> or <paramref name="device"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="pushConstantSize"/> is non-zero while <paramref name="pushConstantStageFlags"/> is zero.</exception>
    public static VkResult Create(
        IAllocator allocator,
        VulkanDeviceCommands device,
        IReadOnlyList<VkDescriptorSetLayoutBinding>? bindings,
        uint pushConstantSize,
        uint pushConstantStageFlags,
        out nint descriptorSetLayoutHandle,
        out nint pipelineLayoutHandle
    ) {
        ArgumentNullException.ThrowIfNull(argument: allocator);
        ArgumentNullException.ThrowIfNull(argument: device);

        if (
            (pushConstantSize > 0) &&
            (0 == pushConstantStageFlags)
        ) {
            throw new ArgumentException(
                message: "Push-constant stage flags must be non-zero when a push-constant range is requested.",
                paramName: nameof(pushConstantStageFlags)
            );
        }

        descriptorSetLayoutHandle = 0;
        pipelineLayoutHandle = 0;

        var setLayout = ((nint)0);
        var pushConstantRange = new VkPushConstantRange {
            Offset = 0,
            Size = pushConstantSize,
            StageFlags = pushConstantStageFlags,
        };
        var pipelineLayoutCreateInfo = new VkPipelineLayoutCreateInfo {
            SType = StructureTypePipelineLayoutCreateInfo,
        };

        if (bindings is { Count: > 0 }) {
            var bindingsPointer = VulkanMarshalHelpers.AllocateArray(
                allocator: allocator,
                values: bindings
            );

            try {
                var setLayoutCreateInfo = new VkDescriptorSetLayoutCreateInfo {
                    BindingCount = ((uint)bindings.Count),
                    PBindings = bindingsPointer,
                    SType = StructureTypeDescriptorSetLayoutCreateInfo,
                };
                var setLayoutResult = device.CreateDescriptorSetLayout(
                    device.Handle,
                    in setLayoutCreateInfo,
                    0,
                    out setLayout
                );

                if (!setLayoutResult.IsSuccess()) {
                    return setLayoutResult;
                }
            } finally {
                allocator.Free(ptr: bindingsPointer);
            }

            pipelineLayoutCreateInfo.SetLayoutCount = 1;
            pipelineLayoutCreateInfo.PSetLayouts = ((nint)(&setLayout));
        }

        if (pushConstantSize > 0) {
            pipelineLayoutCreateInfo.PushConstantRangeCount = 1;
            pipelineLayoutCreateInfo.PPushConstantRanges = ((nint)(&pushConstantRange));
        }

        var layoutResult = device.CreatePipelineLayout(
            device.Handle,
            in pipelineLayoutCreateInfo,
            0,
            out var layout
        );

        if (!layoutResult.IsSuccess()) {
            device.Destroy(
                destroy: device.DestroyDescriptorSetLayout,
                handle: setLayout
            );
            return layoutResult;
        }

        descriptorSetLayoutHandle = setLayout;
        pipelineLayoutHandle = layout;
        return layoutResult;
    }
    /// <summary>Destroys a pipeline layout and its descriptor set layout, as <see cref="Create"/> returned them.</summary>
    /// <param name="device">The command table of the logical device that owns the layouts.</param>
    /// <param name="descriptorSetLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle, or zero.</param>
    /// <param name="pipelineLayoutHandle">The native <c>VkPipelineLayout</c> handle, or zero.</param>
    public static void Destroy(VulkanDeviceCommands device, nint descriptorSetLayoutHandle, nint pipelineLayoutHandle) {
        device.Destroy(
            destroy: device.DestroyPipelineLayout,
            handle: pipelineLayoutHandle
        );
        device.Destroy(
            destroy: device.DestroyDescriptorSetLayout,
            handle: descriptorSetLayoutHandle
        );
    }
}
