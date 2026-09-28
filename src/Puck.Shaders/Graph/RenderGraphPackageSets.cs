using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>What an instance hands a package pass's recorder when its graph installs: the pool its sets come from, the
/// constant buffers its two blocks live in, one per frame slot, and the images its outputs can be. The instance owns all of it; the pool's sets are
/// released with the pool.</summary>
/// <param name="DescriptorPool">The instance's descriptor pool, which holds the pass's frame group and pass group sets
/// once per frame slot.</param>
/// <param name="FrameBlocks">The constant buffer holding the frame group's block in each frame slot, which every pass of
/// the instance shares and the instance writes each frame.</param>
/// <param name="PassBlocks">The constant buffer holding the pass's pass block in each frame slot, which the instance
/// uploads after each recording (<see cref="RenderGraphPackageRecording.PassBlock"/>).</param>
/// <param name="OutputImages">Each output's image instances the pass can draw into, one list per output and empty for
/// an external or buffer output: a graphics package creates its framebuffers over them here, so a recording creates
/// nothing.</param>
/// <param name="Regions">The regions the pass's package states (<see cref="IRenderGraphPackageFactory.Regions"/>), in its
/// order. The instance owns, flushes and copies them; the recorder writes their contents and binds each slot's
/// <see cref="GpuRegion.Buffer"/>, which stays the same buffer for the region's life.</param>
public sealed record RenderGraphPackageGroups(nint DescriptorPool, IReadOnlyList<IGpuBuffer> FrameBlocks, IReadOnlyList<IGpuBuffer> PassBlocks, IReadOnlyList<IReadOnlyList<IGpuImage>> OutputImages, IReadOnlyList<GpuRegion> Regions);
/// <summary>A package pass's frame group and pass group sets, one of each per frame slot, allocated from the instance's
/// pool against its pipeline's group layouts, with each set's block already bound to its constant buffer. A recorder
/// writes its resources into the slot's pass set by member name and binds both sets by group, as a document pass's
/// node does.</summary>
public sealed class RenderGraphPackageSets {
    private readonly IGpuBindings m_bindings;
    private readonly nint[] m_frameSets;
    private readonly ShaderInterfaceGroupLayout m_passGroup;
    private readonly nint[] m_passSets;

    /// <summary>Allocates a pass's sets for every frame slot and binds each set's block to its slot's constant
    /// buffer.</summary>
    /// <param name="context">The pass, whose <see cref="RenderGraphPackageRecorderContext.Parameters"/> lays out its
    /// groups.</param>
    /// <param name="groups">The instance's pool and block buffers.</param>
    /// <param name="groupLayoutHandles">The recorder's pipeline's set layout per group
    /// (<see cref="IGpuPipeline.GroupLayoutHandles"/>).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The pool is zero, a block list does not hold one buffer per frame slot, or the
    /// pipeline binds no frame or pass group.</exception>
    public RenderGraphPackageSets(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, IReadOnlyList<nint> groupLayoutHandles) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: groups);
        ArgumentNullException.ThrowIfNull(argument: groupLayoutHandles);

        var slots = context.InFlightFrames;

        if (
            (groups.DescriptorPool == 0) ||
            (groups.FrameBlocks.Count != slots) ||
            (groups.PassBlocks.Count != slots)
        ) {
            throw new ArgumentException(
                message: $"Package pass '{context.Pass}' needs a pool and one frame block and one pass block buffer per frame slot.",
                paramName: nameof(groups)
            );
        }
        if (
            (groupLayoutHandles.Count <= ((int)ShaderInterfaceGroup.Pass)) ||
            (groupLayoutHandles[((int)ShaderInterfaceGroup.Frame)] == 0) ||
            (groupLayoutHandles[((int)ShaderInterfaceGroup.Pass)] == 0)
        ) {
            throw new ArgumentException(
                message: $"Package pass '{context.Pass}''s pipeline binds no frame or pass group; it must be created through its interface's pipeline layout.",
                paramName: nameof(groupLayoutHandles)
            );
        }

        m_bindings = context.Services.Bindings;
        m_frameSets = new nint[slots];
        m_passGroup = context.Parameters.Layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass));
        m_passSets = new nint[slots];

        for (var slot = 0; (slot < slots); slot++) {
            m_frameSets[slot] = m_bindings.AllocateSet(
                groups.DescriptorPool,
                groupLayoutHandles[((int)ShaderInterfaceGroup.Frame)],
                name: new GpuObjectName(
                    detail: "frame group",
                    index: slot,
                    owner: context.Instance,
                    part: context.Pass
                )
            );
            m_passSets[slot] = m_bindings.AllocateSet(
                groups.DescriptorPool,
                groupLayoutHandles[((int)ShaderInterfaceGroup.Pass)],
                name: new GpuObjectName(
                    detail: "pass group",
                    index: slot,
                    owner: context.Instance,
                    part: context.Pass
                )
            );
            m_bindings.WriteConstantBuffer(
                arrayElement: 0,
                binding: 0,
                bufferHandle: groups.FrameBlocks[slot].BufferHandle,
                bufferSize: groups.FrameBlocks[slot].SizeBytes,
                descriptorSetHandle: m_frameSets[slot]
            );
            m_bindings.WriteConstantBuffer(
                arrayElement: 0,
                binding: 0,
                bufferHandle: groups.PassBlocks[slot].BufferHandle,
                bufferSize: groups.PassBlocks[slot].SizeBytes,
                descriptorSetHandle: m_passSets[slot]
            );
        }
    }

    /// <summary>Returns the binding a resource member of the pass group sits at.</summary>
    /// <param name="member">The member's name.</param>
    /// <returns>The binding, which is also its Direct3D 12 register number in space 3.</returns>
    /// <exception cref="ArgumentException">The pass group declares no resource of that name.</exception>
    public uint BindingOf(string member) =>
        (m_passGroup.Resources.FirstOrDefault(predicate: resource => string.Equals(
            a: resource.Member.Name,
            b: member,
            comparisonType: StringComparison.Ordinal
        )) ?? throw new ArgumentException(
            message: $"The pass group declares no resource '{member}'.",
            paramName: nameof(member)
        )).Binding;
    /// <summary>Binds the slot's frame group set at group 0 and its pass group set at group 3.</summary>
    /// <param name="recorder">The recording's recorder.</param>
    /// <param name="commandBuffer">The recording's command buffer.</param>
    /// <param name="bindPoint">The pipeline's bind point.</param>
    /// <param name="pipelineLayout">The pipeline's layout handle.</param>
    /// <param name="slot">The frame slot.</param>
    public void Bind(IGpuRecorder recorder, nint commandBuffer, GpuBindPoint bindPoint, nint pipelineLayout, int slot) {
        ArgumentNullException.ThrowIfNull(argument: recorder);

        recorder.BindDescriptorSet(
            bindPoint: bindPoint,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_frameSets[slot],
            group: ((uint)ShaderInterfaceGroup.Frame),
            pipelineLayoutHandle: pipelineLayout
        );
        recorder.BindDescriptorSet(
            bindPoint: bindPoint,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_passSets[slot],
            group: ((uint)ShaderInterfaceGroup.Pass),
            pipelineLayoutHandle: pipelineLayout
        );
    }
    /// <summary>Returns the slot's pass group set, which the recorder writes its resources into.</summary>
    /// <param name="slot">The frame slot.</param>
    /// <returns>The set.</returns>
    public nint PassSet(int slot) => m_passSets[slot];
}
