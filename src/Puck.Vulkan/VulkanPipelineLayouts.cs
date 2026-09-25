using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>The layouts of a pipeline created from a <see cref="GpuPipelineLayoutDescription"/>, as
/// <see cref="VulkanPipelineLayouts.Create(IAllocator, VulkanDeviceCommands, VulkanGroupLayouts, out VulkanGroupPipelineLayout?)"/>
/// creates them.</summary>
/// <param name="PipelineLayoutHandle">The native <c>VkPipelineLayout</c> handle.</param>
/// <param name="SetLayoutHandles">The native <c>VkDescriptorSetLayout</c> handle of each set, indexed by set
/// number.</param>
public sealed record VulkanGroupPipelineLayout(
    nint PipelineLayoutHandle,
    IReadOnlyList<nint> SetLayoutHandles
);
/// <summary>
/// The one way the compute and graphics pipeline APIs create and destroy a pipeline layout: an optional descriptor
/// set layout over the pipeline's bindings, and a pipeline layout over that set and an optional push-constant range; or,
/// for a pipeline created from a <see cref="GpuPipelineLayoutDescription"/>, one set layout per planned set and a
/// pipeline layout over all of them and the planned push range.
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
    /// <summary>Creates the pipeline layout of a pipeline created from a <see cref="GpuPipelineLayoutDescription"/>: one
    /// descriptor set layout per planned set, in set order and empty where no group sits, each binding at its number,
    /// type, count and stage flags, and a pipeline layout over every set layout and the planned push range. On a failed
    /// result nothing created here is left alive.</summary>
    /// <param name="allocator">The unmanaged allocator that marshals the bindings.</param>
    /// <param name="device">The command table of the logical device that owns the layouts.</param>
    /// <param name="groups">The planned set layouts and push range (<see cref="VulkanGroupLayouts.Plan"/>).</param>
    /// <param name="layouts">When this method returns, the created layouts, owned by the caller, or
    /// <see langword="null"/> when creation failed.</param>
    /// <returns>The first failing <see cref="VkResult"/>, or the pipeline layout's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/>, <paramref name="device"/> or
    /// <paramref name="groups"/> is <see langword="null"/>.</exception>
    public static VkResult Create(
        IAllocator allocator,
        VulkanDeviceCommands device,
        VulkanGroupLayouts groups,
        out VulkanGroupPipelineLayout? layouts
    ) {
        ArgumentNullException.ThrowIfNull(argument: allocator);
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: groups);

        var sets = new nint[groups.Sets.Count];

        layouts = null;

        for (var set = 0; (set < sets.Length); set++) {
            var planned = groups.Sets[set].Bindings;
            var bindings = new VkDescriptorSetLayoutBinding[planned.Count];

            for (var index = 0; (index < bindings.Length); index++) {
                bindings[index] = new VkDescriptorSetLayoutBinding {
                    Binding = planned[index].Binding,
                    DescriptorCount = planned[index].Count,
                    DescriptorType = planned[index].DescriptorType,
                    StageFlags = planned[index].StageFlags,
                };
            }

            var bindingsPointer = ((bindings.Length == 0)
                ? 0
                : VulkanMarshalHelpers.AllocateArray(
                    allocator: allocator,
                    values: bindings
                ));

            try {
                var setLayoutCreateInfo = new VkDescriptorSetLayoutCreateInfo {
                    BindingCount = ((uint)bindings.Length),
                    PBindings = bindingsPointer,
                    SType = StructureTypeDescriptorSetLayoutCreateInfo,
                };
                var setLayoutResult = device.CreateDescriptorSetLayout(
                    device.Handle,
                    in setLayoutCreateInfo,
                    0,
                    out sets[set]
                );

                if (!setLayoutResult.IsSuccess()) {
                    sets[set] = 0;
                    DestroySets(
                        device: device,
                        setLayoutHandles: sets
                    );

                    return setLayoutResult;
                }
            } finally {
                if (0 != bindingsPointer) {
                    allocator.Free(ptr: bindingsPointer);
                }
            }
        }

        var pushConstantRange = new VkPushConstantRange {
            Offset = 0,
            Size = groups.PushRangeBytes,
            StageFlags = groups.PushRangeStageFlags,
        };
        VkResult layoutResult;
        nint layout;

        fixed (nint* setPointer = sets) {
            var pipelineLayoutCreateInfo = new VkPipelineLayoutCreateInfo {
                PPushConstantRanges = ((groups.PushRangeBytes == 0)
                    ? 0
                    : ((nint)(&pushConstantRange))),
                PSetLayouts = ((sets.Length == 0)
                    ? 0
                    : ((nint)setPointer)),
                PushConstantRangeCount = ((groups.PushRangeBytes == 0)
                    ? 0U
                    : 1U),
                SType = StructureTypePipelineLayoutCreateInfo,
                SetLayoutCount = ((uint)sets.Length),
            };

            layoutResult = device.CreatePipelineLayout(
                device.Handle,
                in pipelineLayoutCreateInfo,
                0,
                out layout
            );
        }

        if (!layoutResult.IsSuccess()) {
            DestroySets(
                device: device,
                setLayoutHandles: sets
            );

            return layoutResult;
        }

        layouts = new VulkanGroupPipelineLayout(
            PipelineLayoutHandle: layout,
            SetLayoutHandles: sets
        );

        return layoutResult;
    }
    /// <summary>Destroys a <see cref="VulkanGroupPipelineLayout"/>'s pipeline layout and every set layout.</summary>
    /// <param name="device">The command table of the logical device that owns the layouts.</param>
    /// <param name="layouts">The layouts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="layouts"/> is <see langword="null"/>.</exception>
    public static void Destroy(VulkanDeviceCommands device, VulkanGroupPipelineLayout layouts) {
        ArgumentNullException.ThrowIfNull(argument: layouts);

        Destroy(
            descriptorSetLayoutHandle: 0,
            device: device,
            pipelineLayoutHandle: layouts.PipelineLayoutHandle
        );
        DestroySets(
            device: device,
            setLayoutHandles: layouts.SetLayoutHandles
        );
    }
    /// <summary>Destroys descriptor set layouts, such as a <see cref="VulkanGroupPipelineLayout"/>'s, skipping zero
    /// handles.</summary>
    /// <param name="device">The command table of the logical device that owns the layouts.</param>
    /// <param name="setLayoutHandles">The native <c>VkDescriptorSetLayout</c> handles.</param>
    public static void DestroySets(VulkanDeviceCommands device, IReadOnlyList<nint> setLayoutHandles) {
        ArgumentNullException.ThrowIfNull(argument: setLayoutHandles);

        foreach (var handle in setLayoutHandles) {
            device.Destroy(
                destroy: device.DestroyDescriptorSetLayout,
                handle: handle
            );
        }
    }
    /// <summary>Destroys a pipeline layout and its descriptor set layout, as
    /// <see cref="Create(IAllocator, VulkanDeviceCommands, IReadOnlyList{VkDescriptorSetLayoutBinding}, uint, uint, out nint, out nint)"/>
    /// returned them, or a pipeline layout alone, with a zero set layout.</summary>
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
