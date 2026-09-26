using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// A document pass binds groups (ShaderFrameInterface.ForPass): the frame group at set 0, whose block the node writes once
// a frame into its frame region, and its pass group at set 3, whose block (the pass's extent and config) lives in the
// pass's own region and whose bindings are its ports. Each slot's frame and pass sets take their constant buffers and
// the pass's samplers once, when they are allocated; a frame writes only the ports. A package pass binds the same two
// groups: the node creates and seeds its pass region, and its recorder allocates its sets (RenderGraphPackageSets).
public sealed partial class ShaderPipelineRenderNode {
    private const uint FrameGroup = ((uint)ShaderInterfaceGroup.Frame);
    private const uint PassGroup = ((uint)ShaderInterfaceGroup.Pass);

    // The pipeline layout of a document pass that binds groups, visible to its pass kind's stages.
    private static GpuPipelineLayoutDescription GroupLayoutOf(ShaderPipelinePlannedPass planned) =>
        planned.Parameters.Layout.PipelineLayout(
            stages: planned.Declaration!.Kind.Stages()
        );
    // The bytes of a uniform region holding a block: whole constant-buffer views.
    private static int UniformBytes(uint blockBytes) =>
        checked(((int)(((blockBytes + (IGpuBindings.ConstantBufferAlignment - 1UL)) / IGpuBindings.ConstantBufferAlignment) * IGpuBindings.ConstantBufferAlignment)));
    // Where each port of a grouped pass binds in its pass group, in document order: its inputs, then a compute pass's
    // outputs. An image input's sampler binds at Sampler; any other port has none.
    private static PortBinding[] PortBindingsOf(ShaderPipelinePlannedPass planned) {
        var declaration = planned.Declaration!;
        var group = planned.Parameters.Layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass));
        var ports = declaration.InputReferences.Concat(second: (declaration.IsGraphics
            ? []
            : declaration.OutputReferences)).ToArray();
        var bindings = new PortBinding[ports.Length];

        uint BindingOf(string name) =>
            group.Resources.Single(predicate: resource => string.Equals(
                a: resource.Member.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )).Binding;

        for (var index = 0; (index < ports.Length); index++) {
            var identifier = ShaderPipelinePassPorts.Identifier(reference: ports[index]);
            var binding = BindingOf(name: identifier);
            var sampler = group.Resources.FirstOrDefault(predicate: resource => string.Equals(
                a: resource.Member.Name,
                b: (identifier + ShaderPipelinePassPorts.SamplerSuffix),
                comparisonType: StringComparison.Ordinal
            ));

            bindings[index] = new PortBinding(
                Binding: binding,
                Sampler: (sampler?.Binding ?? PortBinding.None)
            );
        }

        return bindings;
    }
    // The graph pool's demand for one pass: a frame set and a pass set per in-flight slot. A package pass's recorder
    // allocates its sets from the same pool against its own pipeline's layouts, whose groups the pass's parameters
    // describe; which stages a group is visible to does not change what its sets hold.
    private static GpuDescriptorPoolSizes GroupPoolSizes(ShaderPipelinePlannedPass planned, uint inFlight) {
        var groups = planned.Parameters.Layout.PipelineLayout(
            stages: (planned.Declaration?.Kind.Stages() ?? GpuShaderStage.Fragment)
        ).Groups;
        var sizes = default(GpuDescriptorPoolSizes);

        for (var slot = 0u; (slot < inFlight); slot++) {
            sizes += GpuDescriptorPoolSizes.ForGroups(groups: groups);
        }

        return sizes;
    }
    // Creates a pass's pass region, one constant buffer per slot, which SeedPassRegions fills once the install has
    // preserved the live config.
    private void CreatePassRegion(RuntimePass pass) {
        pass.PassRegion = new GpuRegion(
            bindings: m_gpu.Bindings,
            buffers: m_gpu.BufferFactory,
            byteCount: UniformBytes(blockBytes: pass.ParametersLayout.SizeBytes),
            copyPipeline: null,
            memory: GpuResidency.RingMemory(profile: m_device.MemoryProfile),
            name: new GpuObjectName(
                detail: "pass block",
                owner: m_descriptor.Name,
                part: pass.Name
            ),
            policy: GpuResidencyPolicy.Ring,
            recorder: m_gpu.Recorder,
            slotCount: ((int)m_inFlight),
            usage: GpuBufferUsage.Uniform
        );
    }
    // Allocates a document pass's per-slot sets and sampler, and writes what never changes into each set: the frame and
    // pass blocks' constant buffers and the pass's samplers.
    private void AllocateGroupSets(RuntimePass pass, nint descriptorPool) {
        var bindings = m_gpu.Bindings;
        var layouts = ((pass.Kind == ShaderPipelinePassKind.Compute)
            ? pass.Compute!.GroupLayoutHandles
            : pass.Graphics!.GroupLayoutHandles);

        for (var slot = 0; (slot < m_inFlight); slot++) {
            pass.FrameSets![slot] = bindings.AllocateSet(
                descriptorPool,
                layouts[((int)FrameGroup)],
                name: new GpuObjectName(
                    detail: "frame group",
                    index: slot,
                    owner: m_descriptor.Name,
                    part: pass.Name
                )
            );
            pass.Sets![slot] = bindings.AllocateSet(
                descriptorPool,
                layouts[((int)PassGroup)],
                name: new GpuObjectName(
                    detail: "pass group",
                    index: slot,
                    owner: m_descriptor.Name,
                    part: pass.Name
                )
            );
            pass.Samplers![slot] = bindings.CreateSampler();
            bindings.WriteConstantBuffer(
                arrayElement: 0,
                binding: 0,
                bufferHandle: m_frameRegion!.Buffer(slot: slot).BufferHandle,
                bufferSize: ((ulong)m_frameRegion.ByteCount),
                descriptorSetHandle: pass.FrameSets[slot]
            );
            bindings.WriteConstantBuffer(
                arrayElement: 0,
                binding: 0,
                bufferHandle: pass.PassRegion!.Buffer(slot: slot).BufferHandle,
                bufferSize: ((ulong)pass.PassRegion.ByteCount),
                descriptorSetHandle: pass.Sets[slot]
            );

            foreach (var port in pass.PortBindings!) {
                if (port.Sampler != PortBinding.None) {
                    bindings.WriteSampler(
                        arrayElement: 0,
                        binding: port.Sampler,
                        descriptorSetHandle: pass.Sets[slot],
                        samplerHandle: pass.Samplers[slot]
                    );
                }
            }
        }
    }
    // Sends every pass's current block to every slot, so each slot's next frame uploads only what changes after this, as
    // every later one does, however many frames ran before: an install seeds the blocks once the live config is preserved
    // into them, and a reset seeds them again.
    private void SeedPassRegions() {
        foreach (var pass in m_passes) {
            if (pass.PassRegion is not { } region) {
                continue;
            }

            WritePassBlock(pass: pass);
            for (var slot = 0; (slot < m_inFlight); slot++) {
                region.Flush(slot: slot);
            }
        }
    }
    // Binds a grouped pass's frame and pass sets for this slot.
    private void BindGroupSets(RuntimePass pass, int slot, nint command, GpuBindPoint bindPoint, nint layout) {
        var recorder = m_gpu.Recorder;

        WritePassBlock(pass: pass);
        pass.PassRegion!.Flush(slot: slot);
        recorder.BindDescriptorSet(
            bindPoint: bindPoint,
            commandBufferHandle: command,
            descriptorSetHandle: pass.FrameSets![slot],
            group: FrameGroup,
            pipelineLayoutHandle: layout
        );
        recorder.BindDescriptorSet(
            bindPoint: bindPoint,
            commandBufferHandle: command,
            descriptorSetHandle: pass.Sets![slot],
            group: PassGroup,
            pipelineLayoutHandle: layout
        );
    }
    // Writes this frame's frame group block into the node's frame region and sends the whole region to the slot, or, with
    // sentinels on, every member's echo sentinel. The block is rewritten every frame, so each slot takes all of it rather
    // than what differs from the frame it last held, and a frame's upload never depends on how many frames ran before.
    private void WriteFrameGroup(int slot) {
        if (m_frameRegion is not { } region) {
            return;
        }

        var layout = m_frameLayout!;
        Span<byte> block = stackalloc byte[region.ByteCount];

        if (Sentinels) {
            ShaderInterfaceEcho.WriteSentinels(
                block: block,
                group: layout.Layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Frame))
            );
        } else {
            layout.WriteFrame(
                block: block,
                frame: m_frame,
                values: Frame
            );
        }

        _ = region.Write(
            bytes: block,
            offset: 0
        );
        region.OweAll(slot: slot);
        region.Flush(slot: slot);
    }
    // Writes a grouped pass's block into its region each time its sets bind, so a config change or the sentinels reach it
    // with no hook of their own: its bound config and its extent, or, with sentinels on, every member's echo sentinel. The
    // region owes a slot only the words that changed.
    private void WritePassBlock(RuntimePass pass) {
        var region = pass.PassRegion!;
        Span<byte> block = stackalloc byte[region.ByteCount];

        FillPassBlock(
            block: block,
            pass: pass
        );
        _ = region.Write(
            bytes: block,
            offset: 0
        );
    }
    // Fills a pass block with the pass's bound config and its extent, or, with sentinels on, every member's echo sentinel.
    private void FillPassBlock(RuntimePass pass, Span<byte> block) {
        if (Sentinels) {
            ShaderInterfaceEcho.WriteSentinels(
                block: block,
                group: pass.ParametersLayout.Layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass))
            );
        } else {
            pass.Parameters.Bytes.Span.CopyTo(destination: block);
            pass.ParametersLayout.WriteExtent(
                block: block,
                height: pass.Height,
                width: pass.Width
            );
        }
    }

    // Where one port binds in its pass group, and its sampler's binding or None.
    private readonly record struct PortBinding(uint Binding, uint Sampler) {
        public const uint None = uint.MaxValue;
    }
}
