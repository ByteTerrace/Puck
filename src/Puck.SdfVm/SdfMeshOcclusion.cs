using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Maths;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// One raster pass's GPU selection and counted indirect commands. All passes cull boxes and choose levels from the
/// residency's retained mesh records. Opaque occlusion draws the previous visible set, builds a conservative depth
/// pyramid, and draws newly visible records in a second phase. Cascades select once for opaque entry/exit and glass
/// extinction; glass and a non-occluding mesh pass use one phase. Level and surface generation persist per draw within
/// one table owner, including across buffer growth;
/// changing owners clears selection history, and a capture ignores prior levels.
/// Each phase preserves staged draw order with empty commands for rejected records, so depth ties resolve alike.
/// No host draw walk or readback is involved. KEEP IN SYNC with sdf-mesh-selection.hlsli and the occlusion kernel.
/// </summary>
internal sealed class SdfMeshOcclusion : IDisposable {
    // The kernel's steps, pushed from SdfKernelInterfaces.MeshLevelShift up.
    private const uint EarlyStep = 0u;
    private const uint DepthStep = 1u;
    private const uint LateStep = 2u;
    private const uint SightStep = 3u;
    // Controls only; bounds and level metadata live in the shared mesh region. Paired with sdf-mesh-selection.hlsli:
    // one header row, three detail rows, ten cull rows.
    private const int HeaderWords = (14 * 4);
    // Early/late for camera passes; entry/solid exit/thin exit and optional extinction for cascades.
    private readonly uint m_argumentPhases;
    private const ulong CountsBytes = ((4 + SdfPortalSight.Capacity) * sizeof(uint));

    private readonly IGpuBufferFactory m_buffers;
    private readonly string m_owner;
    private readonly string m_part;
    private readonly IGpuStorageBuffer?[] m_candidates;
    private readonly List<IGpuBuffer>[] m_retired;
    private readonly byte[] m_pushed = new byte[GpuPipelineLayoutDescription.PushIndexBytes];

    private IGpuBuffer? m_visible;
    private IGpuBuffer? m_seedVisible;
    private IGpuBuffer? m_draws;
    private IGpuBuffer? m_counts;
    private IGpuBuffer? m_depth;
    private bool m_visibleCleared;
    private SdfWorldTables? m_historyOwner;
    private uint m_capacity;
    private uint m_count;
    private uint m_levels;
    private uint m_width;
    private uint m_height;
    private int m_slot;
    private readonly SdfPortalSight? m_sight;
    private readonly SightSlot[] m_sightSlots;

    private sealed class SightSlot {
        internal readonly SdfPortalSight.Query?[] Queries = new SdfPortalSight.Query?[SdfPortalSight.Capacity];
        internal IGpuReadbackBuffer? Buffer;
        internal IGpuSubmissionFence? Fence;
        internal bool Recorded;
        internal long Epoch;
    }

    // Creates a view's occlusion state; every buffer is created on the first frame that needs it.
    public SdfMeshOcclusion(IGpuBufferFactory buffers, int slots, string owner, string part, uint argumentPhases, SdfPortalSight? sight = null) {
        m_argumentPhases = argumentPhases;
        m_buffers = buffers;
        m_candidates = new IGpuStorageBuffer?[slots];
        m_retired = Enumerable.Range(count: slots, start: 0).Select(selector: static _ => new List<IGpuBuffer>()).ToArray();
        m_owner = owner;
        m_part = part;
        m_sight = sight;
        m_sightSlots = Enumerable.Range(0, slots).Select(static _ => new SightSlot()).ToArray();
        sight?.Attach(this);
    }

    // The candidates staged for the frame.
    public uint CandidateCount => m_count;

    internal void Submitted(int slot, IGpuSubmissionFence fence) {
        var pending = m_sightSlots[slot];
        if (pending.Recorded) {
            pending.Recorded = false;
            pending.Fence = fence;
        }
    }
    internal void PollSight() {
        if (m_sight is null) { return; }
        Span<byte> bytes = stackalloc byte[(int)CountsBytes];
        foreach (var slot in m_sightSlots) {
            if (slot.Fence is not { IsSignaled: true }) { continue; }
            slot.Fence = null;
            slot.Buffer!.Read(bytes);
            m_sight.Complete(slot.Epoch, slot.Queries, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes)[4..]);
        }
    }

    // Each pass uploads constant-sized controls. The residency already uploaded each changed draw once.
    public void Stage(SdfWorldTables tables, int slot, uint count, uint width, uint height, in SdfMeshCull cull, in SdfMeshDetail detail, bool held, bool glass, bool occlude, bool splitExit = false, Puck.Abstractions.Cameras.CameraSnapshot camera = default) {
        if (occlude && splitExit) { throw new ArgumentException(message: "A selection cannot combine depth occlusion and exit partitioning."); }
        // The recording slot's fence has completed before Stage. Retire replacements only on its next reuse, after all
        // preceding submissions and this slot's history copy have completed.
        foreach (var buffer in m_retired[slot]) { buffer.Dispose(); }
        m_retired[slot].Clear();
        m_slot = slot;
        m_count = count;
        m_width = Math.Max(val1: width, val2: 1u);
        m_height = Math.Max(val1: height, val2: 1u);
        m_levels = LevelsOf(height: m_height, width: m_width);
        PollSight();
        var sightSlot = m_sightSlots[slot];
        sightSlot.Recorded = false;
        sightSlot.Fence = null;
        Span<System.Numerics.Vector4> rows = stackalloc System.Numerics.Vector4[14 + (4 * SdfPortalSight.Capacity)];
        rows.Clear();
        if (occlude && m_sight is not null) { sightSlot.Epoch = m_sight.Capture(rows[14..], sightSlot.Queries, in camera); }
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<System.Numerics.Vector4, uint>(span: rows);

        words[0] = count;
        words[1] = m_levels;
        words[2] = (occlude ? 1u : 0u) | (splitExit ? 2u : 0u) | (m_argumentPhases << 2);
        words[3] = (glass ? 1u : 0u);
        detail.WriteGpu(rows: rows[1..4], held: held);
        cull.WriteGpu(rows: rows[4..]);
        EnsureBuffers(depth: occlude, draws: count);
        // Generations are table-local. Follow can preserve this pass while changing its tables, whose first cells
        // can carry the same tokens as the previous owner. Reset after growth so its seed cannot restore old history.
        if (!ReferenceEquals(objA: m_historyOwner, objB: tables)) {
            m_historyOwner = tables;
            m_seedVisible = null;
            m_visibleCleared = false;
        }
        m_candidates[slot]!.Write(data: words);
    }
    // Writes the occlusion's members of the mesh interface into a mesh set: the frame slot's candidates, the visible set,
    // both phases' draws and counts, the pyramid and the view's mesh target, each only when the set binds another.
    public void Bind(SdfWorldTables tables, nint set, IGpuBuffer?[] bound, int first, ref nint boundTarget, nint target) {
        tables.WriteMeshBuffer(bound: ref bound[(first + 0)], buffer: m_candidates[m_slot]!, member: SdfKernelInterfaces.MeshOcclusionCandidates, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 1)], buffer: m_visible!, member: SdfKernelInterfaces.MeshOcclusionVisibleWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 2)], buffer: m_draws!, member: SdfKernelInterfaces.MeshOcclusionDrawsWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 3)], buffer: m_counts!, member: SdfKernelInterfaces.MeshOcclusionCountsWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 4)], buffer: (m_depth ?? tables.DummyBuffer), member: SdfKernelInterfaces.MeshOcclusionDepthWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 5)], buffer: tables.DummyBuffer, member: SdfKernelInterfaces.MeshIndirectCommands, set: set);
        tables.WriteMeshTarget(bound: ref boundTarget, set: set, view: target);
    }
    // The raster set binds dummies at compute-only members; BindDraws supplies its read-only command alias.
    public static void Unbind(SdfWorldTables tables, nint set, IGpuBuffer?[] bound, int first, ref nint boundTarget) {
        tables.WriteMeshBuffer(bound: ref bound[(first + 0)], buffer: tables.DummyBuffer, member: SdfKernelInterfaces.MeshOcclusionCandidates, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 1)], buffer: tables.DummyBuffer, member: SdfKernelInterfaces.MeshOcclusionVisibleWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 2)], buffer: tables.DummyBuffer, member: SdfKernelInterfaces.MeshOcclusionDrawsWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 3)], buffer: tables.DummyBuffer, member: SdfKernelInterfaces.MeshOcclusionCountsWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 4)], buffer: tables.DummyBuffer, member: SdfKernelInterfaces.MeshOcclusionDepthWritten, set: set);
        tables.WriteMeshTarget(bound: ref boundTarget, set: set, view: tables.SampledFiller.ImageViewHandle);
    }
    // The raster set reads the exact command identity on Vulkan; no optional first-instance feature is needed.
    public void BindDraws(SdfWorldTables tables, nint set, IGpuBuffer?[] bound) =>
        tables.WriteMeshBuffer(bound: ref bound[5], buffer: m_draws!, member: SdfKernelInterfaces.MeshIndirectCommands, set: set);
    // Records selection before rasterization: clear counts, choose kept draws and their levels, then emit the first
    // phase (the previous visible set for opaque occlusion, every kept draw otherwise). The barrier orders prior draws, test and
    // pyramid before this frame's writes, as the buffers are the view's across frames.
    public void RecordEarly(IGpuRecorder recorder, nint commandBuffer, IGpuComputePipeline pipeline, nint set) {
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite | GpuAccess.TransferRead | GpuAccess.TransferWrite,
            destinationStageMask: GpuStage.ComputeShader | GpuStage.Transfer,
            sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite | GpuAccess.IndirectCommandRead,
            sourceStageMask: GpuStage.ComputeShader | GpuStage.VertexShader | GpuStage.DrawIndirect
        );
        recorder.ClearStorageBuffer(commandBufferHandle: commandBuffer, bufferHandle: m_counts!.BufferHandle, sizeBytes: m_counts.SizeBytes);
        Ready(buffer: m_counts, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.TransferWrite, sourceStage: GpuStage.Transfer);

        if (!m_visibleCleared) {
            var copied = (m_seedVisible is not null);

            recorder.ClearStorageBuffer(commandBufferHandle: commandBuffer, bufferHandle: m_visible!.BufferHandle, sizeBytes: m_visible.SizeBytes);
            if (m_seedVisible is { } previous) {
                // The old buffer's only use in this recording is the copy, and the global barrier above makes its
                // prior compute writes visible to transfer reads on both backends; it needs no transition of its own.
                recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: m_visible.BufferHandle,
                    sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                    destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
                recorder.CopyBuffer(commandBufferHandle: commandBuffer, sourceBufferHandle: previous.BufferHandle,
                    destinationBufferHandle: m_visible.BufferHandle, sizeBytes: previous.SizeBytes);
                m_seedVisible = null;
            }
            Ready(buffer: m_visible, commandBuffer: commandBuffer, recorder: recorder, source: (copied ? GpuAccess.CopyWrite : GpuAccess.TransferWrite), sourceStage: GpuStage.Transfer);
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
        Ready(buffer: m_draws!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.IndirectCommandRead | GpuAccess.ShaderRead, sourceStage: GpuStage.DrawIndirect | GpuStage.VertexShader);
        Ready(buffer: m_counts!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.IndirectCommandRead, sourceStage: GpuStage.DrawIndirect);
        recorder.EndBarriers(commandBufferHandle: commandBuffer);
        Dispatch(commandBuffer: commandBuffer, invocations: m_count, level: 0u, pipeline: pipeline, recorder: recorder, step: LateStep);
        if (m_sight is not null) {
            Ready(buffer: m_counts!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
            Dispatch(commandBuffer: commandBuffer, invocations: SdfPortalSight.Capacity, level: 0u, pipeline: pipeline, recorder: recorder, step: SightStep);
            var slot = m_sightSlots[m_slot];
            slot.Buffer ??= m_buffers.CreateReadback(sizeBytes: CountsBytes, name: Name("portal sight", m_slot));
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: m_counts!.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
            recorder.CopyBuffer(commandBufferHandle: commandBuffer, sourceBufferHandle: m_counts.BufferHandle,
                destinationBufferHandle: slot.Buffer.BufferHandle, sizeBytes: CountsBytes);
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: slot.Buffer.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
            Ready(buffer: m_counts, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.TransferRead, sourceStage: GpuStage.Transfer);
            slot.Recorded = true;
        }
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
        m_sight?.Detach(this);
        foreach (var slot in m_sightSlots) { slot.Buffer?.Dispose(); }
        foreach (var candidates in m_candidates) {
            candidates?.Dispose();
        }

        m_visible?.Dispose();
        m_draws?.Dispose();
        m_counts?.Dispose();
        m_depth?.Dispose();
        foreach (var slot in m_retired) { foreach (var buffer in slot) { buffer.Dispose(); } }
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
    // Grows the buffers to the frame's draws and render extent. History is copied; the new tail starts empty, so its
    // first opaque occlusion frame tests every new candidate in the second phase.
    private void EnsureBuffers(uint draws, bool depth) {
        var capacity = Math.Max(val1: draws, val2: 1u);
        var candidateBytes = ((ulong)((HeaderWords + (16 * SdfPortalSight.Capacity)) * sizeof(uint)));

        if ((m_candidates[m_slot] is not { } candidates) || (candidates.SizeBytes < candidateBytes)) {
            m_candidates[m_slot]?.Dispose();
            m_candidates[m_slot] = null;
            m_candidates[m_slot] = m_buffers.CreateHostVisible(
                name: Name(detail: "occlusion candidates", index: m_slot),
                sizeBytes: candidateBytes,
                usage: GpuBufferUsage.Storage
            );
        }
        if ((m_visible is null) || (m_visible.SizeBytes < ((((ulong)capacity) * 4) * sizeof(uint)))) {
            m_seedVisible = m_visible;
            if (m_visible is { } previous) { m_retired[m_slot].Add(item: previous); }
            m_visible = null;
            m_visible = m_buffers.CreateDeviceLocal(
                name: Name(detail: "occlusion visible"),
                sizeBytes: ((((ulong)System.Numerics.BitOperations.RoundUpToPowerOf2(value: capacity)) * 4) * sizeof(uint)),
                usage: GpuBufferUsage.Storage
            );
            m_visibleCleared = false;
        }
        if ((m_draws is null) || (m_capacity < capacity)) {
            if (m_draws is { } previous) { m_retired[m_slot].Add(item: previous); }
            m_draws = null;
            m_capacity = System.Numerics.BitOperations.RoundUpToPowerOf2(value: capacity);
            m_draws = m_buffers.CreateDeviceLocal(
                name: Name(detail: "occlusion draws"),
                sizeBytes: ((((ulong)m_argumentPhases) * m_capacity) * GpuDrawIndirectCommand.StrideBytes),
                usage: GpuBufferUsage.Storage | GpuBufferUsage.Indirect
            );
        }

        m_counts ??= m_buffers.CreateDeviceLocal(
            name: Name(detail: "occlusion counts"),
            sizeBytes: CountsBytes,
            usage: GpuBufferUsage.Storage | GpuBufferUsage.Indirect
        );

        if (!depth) { return; }
        var depthBytes = (DepthWords(height: m_height, levels: m_levels, width: m_width) * sizeof(uint));

        if ((m_depth is null) || (m_depth.SizeBytes < depthBytes)) {
            if (m_depth is { } previous) { m_retired[m_slot].Add(item: previous); }
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

        BinaryPrimitives.WriteUInt32LittleEndian(destination: m_pushed, value: (step << SdfKernelInterfaces.MeshLevelShift) | level);
        recorder.PushConstants(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            data: m_pushed,
            offset: 0,
            pipelineLayoutHandle: pipeline.LayoutHandle,
            stageFlags: GpuShaderStage.Compute
        );
        var groups = invocations.CeilingDivide(divisor: SdfKernelInterfaces.MeshSelectionGroupSize);

        recorder.Dispatch(commandBufferHandle: commandBuffer,
            groupCountX: Math.Min(val1: groups, val2: SdfKernelInterfaces.MeshSelectionGroupsPerRow),
            groupCountY: groups.CeilingDivide(divisor: SdfKernelInterfaces.MeshSelectionGroupsPerRow), groupCountZ: 1u);
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
                destinationAccessMask: GpuAccess.IndirectCommandRead | GpuAccess.ShaderRead,
                destinationStageMask: GpuStage.DrawIndirect | GpuStage.VertexShader,
                sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
                sourceStageMask: GpuStage.ComputeShader
            );
        }

        recorder.EndBarriers(commandBufferHandle: commandBuffer);
    }
}
