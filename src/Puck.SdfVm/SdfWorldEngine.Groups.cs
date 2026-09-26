using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// The engine's frequency groups (SdfWorldInterfaces). Every per-view dispatch binds two sets: the ring slot's frame set
// (set 0), whose block the engine writes once a frame into its frame region, and the ring slot's views set for the view
// (set 3), whose block, the view's world values, lives in the view's own block region and whose bindings are every
// table, buffer, image and sampler the dispatches read. The baker binds one set per brick slot: that slot's request
// buffer and the brick pool, beside a block the engine writes once, and pushes its slice ordinal.
public sealed partial class SdfWorldEngine {
    private const uint FrameGroup = ((uint)ShaderInterfaceGroup.Frame);
    private const uint PassGroup = ((uint)ShaderInterfaceGroup.Pass);

    // The bindings the engine writes image by image, by their members' places in the world interface's pass group;
    // every buffer is written through WriteBuffer, which reads its member's binding, kind and stride.
    private static readonly uint GlyphAtlasBinding = WorldBinding(member: SdfWorldInterfaces.GlyphAtlas);
    private static readonly uint OutputBinding = WorldBinding(member: SdfWorldInterfaces.Output);
    private static readonly uint ScreenSamplerBinding = WorldBinding(member: SdfWorldInterfaces.ScreenSampler);
    // screenSource{i}'s binding, by screen index.
    private static readonly uint[] ScreenSourceBindings = [.. Enumerable.Range(count: MaxScreenSurfaces, start: 0).Select(selector: static screen => WorldBinding(member: SdfWorldInterfaces.ScreenSource(screen: screen)))];

    // Where each world value lies in a views set's block.
    private static readonly int ImageExtentOffset = WorldOffset(member: SdfWorldInterfaces.ImageExtent);
    private static readonly int InstanceMaskWordCountOffset = WorldOffset(member: SdfWorldInterfaces.InstanceMaskWordCount);
    private static readonly int SampleIndexOffset = WorldOffset(member: SdfWorldInterfaces.SampleIndex);
    private static readonly int ScreenMaskOffset = WorldOffset(member: SdfWorldInterfaces.ScreenMask);
    private static readonly int TileGridOffset = WorldOffset(member: SdfWorldInterfaces.TileGrid);
    private static readonly int ViewBaseOffset = WorldOffset(member: SdfWorldInterfaces.ViewBase);
    private static readonly int ViewportCountOffset = WorldOffset(member: SdfWorldInterfaces.ViewportCount);

    // The frame block, written once a frame, and the ring slot's frame set that binds it.
    private readonly GpuRegion m_frameRegion;
    private readonly nint[] m_frameSets = new nint[FrameRingSize];
    // Per view slot: the region holding the view's world block, one constant buffer per ring slot.
    private readonly GpuRegion[] m_viewBlocks;
    // The world values every view shares this frame, the view it names left zero: the cadence signature folds these
    // bytes, so it never depends on which view a set renders.
    private readonly byte[] m_worldBlock = new byte[SdfWorldInterfaces.WorldParameters.SizeBytes];
    // The baker's block, the same for every brick slot, written once at construction.
    private readonly IGpuStorageBuffer? m_brickBakeBlock;
    // The slice ordinal a bake dispatch pushes.
    private readonly byte[] m_brickBakeIndex = new byte[GpuPipelineLayoutDescription.PushIndexBytes];

    /// <summary>Gets or sets the values the next frame's frame block carries: the presented tick, its time, the pointer and
    /// the paired camera. The engine's kernels read none of them today; they reach any kernel that reads its frame group,
    /// as every graph pass's do.</summary>
    public ShaderFrameValues FrameValues { get; set; }

    // The bytes of a uniform region holding a block: whole constant-buffer views.
    private static int UniformBytes(uint blockBytes) =>
        checked(((int)(((blockBytes + (IGpuBindings.ConstantBufferAlignment - 1UL)) / IGpuBindings.ConstantBufferAlignment) * IGpuBindings.ConstantBufferAlignment)));
    private static uint WorldBinding(string member) =>
        SdfWorldInterfaces.BindingOf(
            layout: SdfWorldInterfaces.WorldLayout,
            member: member
        );
    private static int WorldOffset(string member) =>
        ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: member));

    // Binds a per-view dispatch's two sets: a ring slot's frame set and the view's views set of the same slot.
    private void BindWorldGroups(nint commandBuffer, IGpuComputePipeline pipeline, nint frameSet, nint viewsSet) {
        var recorder = m_gpu.Recorder;

        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: frameSet,
            group: FrameGroup,
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: viewsSet,
            group: PassGroup,
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
    }
    // A uniform region of one block per ring slot.
    private GpuRegion CreateBlockRegion(uint blockBytes, in GpuObjectName name) =>
        new(
            bindings: m_gpu.Bindings,
            buffers: m_gpu.BufferFactory,
            byteCount: UniformBytes(blockBytes: blockBytes),
            copyPipeline: null,
            memory: GpuResidency.RingMemory(profile: m_deviceContext.MemoryProfile),
            name: name,
            policy: GpuResidencyPolicy.Ring,
            recorder: m_gpu.Recorder,
            slotCount: FrameRingSize,
            usage: GpuBufferUsage.Uniform
        );
    // Writes a frame block into the frame region and sends it whole to a ring slot: the block is rewritten every frame,
    // so a slot's upload never depends on the frame it last held.
    private void WriteFrameBlock(int slot, ulong frame) {
        Span<byte> block = stackalloc byte[m_frameRegion.ByteCount];

        SdfWorldInterfaces.WorldParameters.WriteFrame(
            block: block,
            frame: frame,
            values: FrameValues
        );
        _ = m_frameRegion.Write(
            bytes: block,
            offset: 0
        );
        m_frameRegion.OweAll(slot: slot);
        m_frameRegion.Flush(slot: slot);
    }
    // Writes each rendered view's world block, the frame's shared values with the view's extent and its own index, into
    // the view's block region, which sends the ring slot only the words that changed.
    private void WriteViewBlocks(uint viewportCount) {
        Span<byte> block = stackalloc byte[m_viewBlocks[0].ByteCount];

        m_worldBlock.CopyTo(destination: block);

        for (var view = 0; (view < ((int)viewportCount)); view++) {
            var output = m_viewOutputs[view]!;

            SdfWorldInterfaces.WorldParameters.WriteExtent(
                block: block,
                height: output.Height,
                width: output.Width
            );
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: block[ViewBaseOffset..],
                value: ((uint)view)
            );
            _ = m_viewBlocks[view].Write(
                bytes: block,
                offset: 0
            );
            m_viewBlocks[view].Flush(slot: m_currentSlot);
        }
    }
    // Packs the world values every view shares this frame into the world block.
    private void PackWorldBlock(uint viewportCount, uint sampleIndex) {
        var block = m_worldBlock.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[ImageExtentOffset..], value: m_width);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[(ImageExtentOffset + sizeof(uint))..], value: m_height);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[TileGridOffset..], value: m_tileGridX);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[(TileGridOffset + sizeof(uint))..], value: m_tileGridY);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[ViewportCountOffset..], value: viewportCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[ScreenMaskOffset..], value: m_screenSourceMask);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[InstanceMaskWordCountOffset..], value: ((uint)m_liveInstanceMaskWordCount));
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[SampleIndexOffset..], value: sampleIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[MeshDrawsOffset..], value: m_meshDrawCount);
    }
}
