using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Maths;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// One view's mesh pass occlusion: the draws its box cull keeps, tested on the GPU against the depth the view itself
/// drew, so a draw that walls or slabs hide is not drawn, and no authored room is needed. The pass draws in two phases
/// (the kernel is <c>passes/sdf-mesh-occlusion.comp.hlsl</c>). The first draws the candidates the previous frame found
/// visible; a depth pyramid is built from what it drew, each texel the farthest forward depth of its block, so a box
/// lying wholly beyond a texel is hidden behind every pixel the texel covers; every candidate's box is tested against it,
/// the verdict becomes the visible set, and the second phase draws the visible candidates the first did not. The two
/// phases together draw every candidate any pixel shows, whatever the previous frame saw, so the test changes no pixel
/// and nothing that comes into view is missing for a frame: a disocclusion is drawn in the frame it happens.
/// <para>Each phase draws its commands in the order the host staged the draws, as the box cull alone does, so depth ties
/// resolve alike. A draw with no host box (a skinned body, <see cref="SdfMeshBox.TryOf"/>) is a candidate the test never
/// hides: the first phase draws it every frame. Glass never occludes, since the glass pass draws it apart from the mesh target,
/// and shadow cascades keep their own cull: neither is drawn here. Each view, a nested portal view among them, tests
/// against its own camera and its own depth, so the state is one view's: the visible set, both phases' draws and the
/// pyramid live on the device and only the candidates, one buffer a frame slot, are written by the host.</para>
/// <para>The draws both phases issue are written by the GPU as counted indirect draw records
/// (<see cref="IGpuRecorder.DrawIndirectCount"/>), so their draws and vertices are counted on the GPU into the pass's
/// drawn counts (<see cref="GpuKernelCounters.DrawnWords"/>) and reach <c>gpu.draws</c> and <c>gpu.vertices</c>. KEEP IN
/// SYNC with the kernel's candidate and command layouts.</para>
/// </summary>
internal sealed class SdfMeshOcclusion : IDisposable {
    // The kernel's steps, pushed from SdfKernelInterfaces.MeshViewShift up.
    private const uint EarlyStep = 0u;
    private const uint DepthStep = 1u;
    private const uint GroupSize = 64u;
    private const uint LateStep = 2u;
    // The uint4 rows a candidate takes after the header: its draw, its vertex count and its box's center, then its three
    // half axes.
    private const int CandidateRows = 4;
    private const int CandidateWords = (CandidateRows * 4);
    private const ulong CountsBytes = (4 * sizeof(uint));
    private const int HeaderWords = 4;
    // A candidate's flag, in its fifteenth word, for a draw with no host box: the first phase draws it every frame.
    private const uint UnboxedFlag = 1u;

    private readonly IGpuBufferFactory m_buffers;
    private readonly string m_owner;
    private readonly string m_part;
    private readonly IGpuStorageBuffer?[] m_candidates;

    private readonly byte[] m_pushed = new byte[GpuPipelineLayoutDescription.PushIndexBytes];
    private uint[] m_words = [];

    private IGpuBuffer? m_visible;
    private IGpuBuffer? m_draws;
    private IGpuBuffer? m_counts;
    private IGpuBuffer? m_depth;
    private bool m_visibleCleared;
    private uint m_capacity;
    private uint m_count;
    private uint m_levels;
    private uint m_width;
    private uint m_height;
    private int m_slot;

    // Creates a view's occlusion state; every buffer is created on the first frame that needs it.
    public SdfMeshOcclusion(IGpuBufferFactory buffers, int slots, string owner, string part) {
        m_buffers = buffers;
        m_candidates = new IGpuStorageBuffer?[slots];
        m_owner = owner;
        m_part = part;
    }

    // The candidates staged for the frame.
    public uint CandidateCount => m_count;

    // Writes a frame's opaque draws the box cull keeps, in their staged order, as the frame slot's candidates, with the
    // pyramid's levels for the render extent. A draw with no box is a candidate the test never hides.
    public void Stage(int slot, IReadOnlyList<SdfMeshDraw> draws, ReadOnlySpan<SdfMeshBox?> boxes, uint count, in SdfMeshCull cull, uint width, uint height) {
        m_slot = slot;
        m_width = Math.Max(val1: width, val2: 1u);
        m_height = Math.Max(val1: height, val2: 1u);
        m_levels = LevelsOf(height: m_height, width: m_width);

        if (m_words.Length < (HeaderWords + (((int)count) * CandidateWords))) {
            m_words = new uint[(HeaderWords + (((int)count) * CandidateWords))];
        }

        var candidates = 0u;

        for (var draw = 0u; (draw < count); draw++) {
            if (draws[((int)draw)].Glass) {
                continue;
            }

            var box = boxes[((int)draw)];

            if (!cull.Keeps(box: in box)) {
                continue;
            }

            var word = (HeaderWords + (((int)candidates) * CandidateWords));
            var kept = box.GetValueOrDefault();

            m_words[word] = draw;
            m_words[(word + 1)] = ((uint)draws[((int)draw)].Mesh.Indices.Length);
            Floats(destination: m_words.AsSpan(length: 3, start: (word + 2)), value: kept.Center);
            Floats(destination: m_words.AsSpan(length: 3, start: (word + 5)), value: kept.HalfX);
            Floats(destination: m_words.AsSpan(length: 3, start: (word + 8)), value: kept.HalfY);
            Floats(destination: m_words.AsSpan(length: 3, start: (word + 11)), value: kept.HalfZ);
            m_words[(word + 14)] = (box.HasValue ? 0u : UnboxedFlag);
            m_words[(word + 15)] = 0u;
            candidates++;
        }

        m_count = candidates;
        m_words[0] = candidates;
        m_words[1] = m_levels;
        m_words[2] = 0u;
        m_words[3] = 0u;
        EnsureBuffers(draws: count);

        m_candidates[slot]!.Write(data: m_words.AsSpan(length: (HeaderWords + (((int)candidates) * CandidateWords)), start: 0));
    }
    // Writes the occlusion's members of the mesh interface into a mesh set: the frame slot's candidates, the visible set,
    // both phases' draws and counts, the pyramid and the view's mesh target, each only when the set binds another.
    public void Bind(SdfWorldTables tables, nint set, IGpuBuffer?[] bound, int first, ref nint boundTarget, nint target) {
        tables.WriteMeshBuffer(bound: ref bound[(first + 0)], buffer: m_candidates[m_slot]!, member: SdfKernelInterfaces.MeshOcclusionCandidates, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 1)], buffer: m_visible!, member: SdfKernelInterfaces.MeshOcclusionVisibleWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 2)], buffer: m_draws!, member: SdfKernelInterfaces.MeshOcclusionDrawsWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 3)], buffer: m_counts!, member: SdfKernelInterfaces.MeshOcclusionCountsWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 4)], buffer: m_depth!, member: SdfKernelInterfaces.MeshOcclusionDepthWritten, set: set);
        tables.WriteMeshTarget(bound: ref boundTarget, set: set, view: target);
    }
    // Writes the tables' dummy and filler at the occlusion's members of a mesh set whose pass does not occlude.
    public static void Unbind(SdfWorldTables tables, nint set, IGpuBuffer?[] bound, int first, ref nint boundTarget) {
        tables.WriteMeshBuffer(bound: ref bound[(first + 0)], buffer: tables.DummyBuffer, member: SdfKernelInterfaces.MeshOcclusionCandidates, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 1)], buffer: tables.DummyBuffer, member: SdfKernelInterfaces.MeshOcclusionVisibleWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 2)], buffer: tables.DummyBuffer, member: SdfKernelInterfaces.MeshOcclusionDrawsWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 3)], buffer: tables.DummyBuffer, member: SdfKernelInterfaces.MeshOcclusionCountsWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 4)], buffer: tables.DummyBuffer, member: SdfKernelInterfaces.MeshOcclusionDepthWritten, set: set);
        tables.WriteMeshTarget(bound: ref boundTarget, set: set, view: tables.SampledFiller.ImageViewHandle);
    }
    // Records the first phase's choice before the mesh pass's render pass: the counts cleared, then the candidates the
    // visible set holds written as the first phase's draws. The barrier first orders the previous frame's draws, test and
    // pyramid before this frame's writes, as the buffers are the view's across frames.
    public void RecordEarly(IGpuRecorder recorder, nint commandBuffer, IGpuComputePipeline pipeline, nint set) {
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite | GpuAccess.TransferWrite,
            destinationStageMask: GpuStage.ComputeShader | GpuStage.Transfer,
            sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite | GpuAccess.IndirectCommandRead,
            sourceStageMask: GpuStage.ComputeShader | GpuStage.DrawIndirect
        );
        recorder.ClearStorageBuffer(commandBufferHandle: commandBuffer, bufferHandle: m_counts!.BufferHandle, sizeBytes: m_counts.SizeBytes);
        Ready(buffer: m_counts, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.TransferWrite, sourceStage: GpuStage.Transfer);

        if (!m_visibleCleared) {
            recorder.ClearStorageBuffer(commandBufferHandle: commandBuffer, bufferHandle: m_visible!.BufferHandle, sizeBytes: m_visible.SizeBytes);
            Ready(buffer: m_visible, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.TransferWrite, sourceStage: GpuStage.Transfer);
            m_visibleCleared = true;
        }

        Bind(commandBuffer: commandBuffer, pipeline: pipeline, recorder: recorder, set: set);
        Dispatch(commandBuffer: commandBuffer, invocations: m_count, level: 0u, pipeline: pipeline, recorder: recorder, step: EarlyStep);
        ToIndirect(commandBuffer: commandBuffer, recorder: recorder);
    }
    // Records one phase's counted indirect draw inside the mesh pass's render pass, its graphics pipeline and set bound.
    // The pushed index is zero, so the record's identity alone names the draw (sdfMeshRasterDraw).
    public void RecordDraws(IGpuRecorder recorder, nint commandBuffer, IGpuPipeline pipeline, int phase) {
        if (m_count == 0u) {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination: m_pushed, value: 0u);
        recorder.PushConstants(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: commandBuffer,
            data: m_pushed,
            offset: 0,
            pipelineLayoutHandle: pipeline.LayoutHandle,
            stageFlags: SdfWorldTables.PipelineLayouts.MeshStages
        );
        recorder.DrawIndirectCount(
            argumentBufferHandle: m_draws!.BufferHandle,
            argumentBufferOffset: ((((ulong)phase) * m_capacity) * GpuDrawIndirectCommand.StrideBytes),
            commandBufferHandle: commandBuffer,
            countBufferHandle: m_counts!.BufferHandle,
            countBufferOffset: (((ulong)phase) * sizeof(uint)),
            maxDrawCount: m_count,
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
    }
    // Records the test between the phases: the mesh target made readable, the pyramid built level by level from it, every
    // candidate tested and the second phase's draws written, then the target handed back to the second phase's render
    // pass in its attachment layout. The caller records it only for a frame with candidates.
    public void RecordLate(IGpuRecorder recorder, nint commandBuffer, IGpuComputePipeline pipeline, nint set, nint target) {
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: target,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.RenderTarget,
            sourceAccessMask: GpuAccess.ColorAttachmentWrite,
            sourceStageMask: GpuStage.ColorAttachmentOutput
        );
        Bind(commandBuffer: commandBuffer, pipeline: pipeline, recorder: recorder, set: set);

        for (var level = 1u; (level <= m_levels); level++) {
            var width = Extent(level: level, size: m_width);
            var height = Extent(level: level, size: m_height);

            Dispatch(commandBuffer: commandBuffer, invocations: (width * height), level: level, pipeline: pipeline, recorder: recorder, step: DepthStep);
            Ready(buffer: m_depth!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
        }

        Ready(buffer: m_visible!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
        recorder.BeginBarriers(commandBufferHandle: commandBuffer);
        Ready(buffer: m_draws!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.IndirectCommandRead, sourceStage: GpuStage.DrawIndirect);
        Ready(buffer: m_counts!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.IndirectCommandRead, sourceStage: GpuStage.DrawIndirect);
        recorder.EndBarriers(commandBufferHandle: commandBuffer);
        Dispatch(commandBuffer: commandBuffer, invocations: m_count, level: 0u, pipeline: pipeline, recorder: recorder, step: LateStep);
        ToIndirect(commandBuffer: commandBuffer, recorder: recorder);
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ColorAttachmentRead | GpuAccess.ColorAttachmentWrite,
            destinationStageMask: GpuStage.ColorAttachmentOutput,
            imageHandle: target,
            newLayout: GpuImageLayout.RenderTarget,
            oldLayout: GpuImageLayout.ShaderReadOnly,
            sourceAccessMask: GpuAccess.ShaderRead,
            sourceStageMask: GpuStage.ComputeShader
        );
    }
    public void Dispose() {
        foreach (var candidates in m_candidates) {
            candidates?.Dispose();
        }

        m_visible?.Dispose();
        m_draws?.Dispose();
        m_counts?.Dispose();
        m_depth?.Dispose();
    }

    // The pyramid's levels for a render extent: halved, rounded up, until a level is 1x1.
    private static uint LevelsOf(uint width, uint height) {
        var levels = 1u;

        while ((Extent(level: levels, size: width) > 1u) || (Extent(level: levels, size: height) > 1u)) {
            levels++;
        }

        return levels;
    }
    private static uint Extent(uint size, uint level) =>
        Math.Max(val1: ((size + ((1u << ((int)level)) - 1u)) >> ((int)level)), val2: 1u);
    // The words every level of the pyramid takes together for a render extent.
    private static ulong DepthWords(uint width, uint height, uint levels) {
        var words = 0UL;

        for (var level = 1u; (level <= levels); level++) {
            words += (((ulong)Extent(level: level, size: width)) * Extent(level: level, size: height));
        }

        return words;
    }
    private static void Floats(Span<uint> destination, System.Numerics.Vector3 value) {
        destination[0] = BitConverter.SingleToUInt32Bits(value: value.X);
        destination[1] = BitConverter.SingleToUInt32Bits(value: value.Y);
        destination[2] = BitConverter.SingleToUInt32Bits(value: value.Z);
    }
    // Grows the buffers to the frame's draws and render extent. A grown visible set starts empty, so its first frame's
    // second phase draws every candidate.
    private void EnsureBuffers(uint draws) {
        var capacity = Math.Max(val1: draws, val2: 1u);
        var candidateBytes = (((ulong)(HeaderWords + (((int)capacity) * CandidateWords))) * sizeof(uint));

        if ((m_candidates[m_slot] is not { } candidates) || (candidates.SizeBytes < candidateBytes)) {
            m_candidates[m_slot]?.Dispose();
            m_candidates[m_slot] = null;
            m_candidates[m_slot] = m_buffers.CreateHostVisible(
                name: Name(detail: "occlusion candidates", index: m_slot),
                sizeBytes: (((ulong)(HeaderWords + (((int)System.Numerics.BitOperations.RoundUpToPowerOf2(value: capacity)) * CandidateWords))) * sizeof(uint)),
                usage: GpuBufferUsage.Storage
            );
        }
        if ((m_visible is null) || (m_visible.SizeBytes < (((ulong)capacity) * sizeof(uint)))) {
            m_visible?.Dispose();
            m_visible = null;
            m_visible = m_buffers.CreateDeviceLocal(
                name: Name(detail: "occlusion visible"),
                sizeBytes: (((ulong)System.Numerics.BitOperations.RoundUpToPowerOf2(value: capacity)) * sizeof(uint)),
                usage: GpuBufferUsage.Storage
            );
            m_visibleCleared = false;
        }
        if ((m_draws is null) || (m_capacity < capacity)) {
            m_draws?.Dispose();
            m_draws = null;
            m_capacity = System.Numerics.BitOperations.RoundUpToPowerOf2(value: capacity);
            m_draws = m_buffers.CreateDeviceLocal(
                name: Name(detail: "occlusion draws"),
                sizeBytes: ((2UL * m_capacity) * GpuDrawIndirectCommand.StrideBytes),
                usage: GpuBufferUsage.Storage | GpuBufferUsage.Indirect
            );
        }

        m_counts ??= m_buffers.CreateDeviceLocal(
            name: Name(detail: "occlusion counts"),
            sizeBytes: CountsBytes,
            usage: GpuBufferUsage.Storage | GpuBufferUsage.Indirect
        );

        var depthBytes = (DepthWords(height: m_height, levels: m_levels, width: m_width) * sizeof(uint));

        if ((m_depth is null) || (m_depth.SizeBytes < depthBytes)) {
            m_depth?.Dispose();
            m_depth = null;
            m_depth = m_buffers.CreateDeviceLocal(
                name: Name(detail: "occlusion depth"),
                sizeBytes: depthBytes,
                usage: GpuBufferUsage.Storage
            );
        }
    }
    private GpuObjectName Name(string detail, int index = 0) => new(detail: detail, index: index, owner: m_owner, part: m_part);
    private static void Bind(IGpuRecorder recorder, nint commandBuffer, IGpuComputePipeline pipeline, nint set) {
        recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: commandBuffer, pipelineHandle: pipeline.Handle);
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: set,
            group: ((uint)ShaderInterfaceGroup.Pass),
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
    }
    private void Dispatch(IGpuRecorder recorder, nint commandBuffer, IGpuComputePipeline pipeline, uint step, uint level, uint invocations) {
        if (invocations == 0u) {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination: m_pushed, value: (step << SdfKernelInterfaces.MeshViewShift) | level);
        recorder.PushConstants(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            data: m_pushed,
            offset: 0,
            pipelineLayoutHandle: pipeline.LayoutHandle,
            stageFlags: GpuShaderStage.Compute
        );
        recorder.Dispatch(commandBufferHandle: commandBuffer, groupCountX: invocations.CeilingDivide(divisor: GroupSize), groupCountY: 1u, groupCountZ: 1u);
    }
    // Readies a buffer a kernel wrote or read for the next kernel's reads and writes.
    private static void Ready(IGpuRecorder recorder, nint commandBuffer, IGpuBuffer buffer, GpuAccess source, GpuStage sourceStage) =>
        recorder.TransitionBuffer(
            bufferHandle: buffer.BufferHandle,
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader,
            sourceAccessMask: source,
            sourceStageMask: sourceStage
        );
    // Hands a phase's draws and counts from the kernel that wrote them to the counted indirect draw that reads them.
    private void ToIndirect(IGpuRecorder recorder, nint commandBuffer) {
        recorder.BeginBarriers(commandBufferHandle: commandBuffer);

        foreach (var buffer in ((ReadOnlySpan<IGpuBuffer>)[m_draws!, m_counts!])) {
            recorder.TransitionBuffer(
                bufferHandle: buffer.BufferHandle,
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuAccess.IndirectCommandRead,
                destinationStageMask: GpuStage.DrawIndirect,
                sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
                sourceStageMask: GpuStage.ComputeShader
            );
        }

        recorder.EndBarriers(commandBufferHandle: commandBuffer);
    }
}
