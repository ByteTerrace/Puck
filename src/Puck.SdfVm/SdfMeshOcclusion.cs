using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Maths;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// One raster pass's GPU selection and counted indirect commands. All passes cull boxes and choose levels from the
/// residency's retained mesh records. Opaque occlusion draws the previous visible set, builds a conservative depth
/// pyramid, and draws newly visible records in a second phase. The far cascade additionally classifies against current
/// camera receivers and runs entry depth in the same two phases. Exit and glass lists depend only on receiver/box
/// classification, never on entry visibility, and draw after entry is final. Other cascades and glass use one entry phase.
/// Level and surface generation persist per draw within
/// one table owner, including across buffer growth;
/// changing owners clears selection history, and a capture ignores prior levels.
/// Each phase preserves staged draw order. A coplanar tie across phases can depend on the prior visible set.
/// No host draw walk is involved. While a reading of the selection stands (<see cref="SdfWorldTables.MarksSelections"/>),
/// one stage in <see cref="SdfWorldTables.SelectionMarkFrames"/> marks the draws its phases select, with the level each
/// draws, and reads the marks back with the portal sight verdicts once the node proves the slot complete
/// (<see cref="SdfWorldTables.ObserveSelectionMarks"/>). KEEP IN SYNC with
/// sdf-mesh-selection.hlsli and the occlusion kernel.
/// </summary>
internal sealed class SdfMeshOcclusion : IDisposable {
    // The kernel's steps, pushed from SdfKernelInterfaces.MeshLevelShift up.
    // Controls only; bounds and level metadata live in the shared mesh region. Paired with sdf-mesh-selection.hlsli:
    // one header row, three detail rows, ten cull rows, one receiver sample row.
    private const int HeaderWords = (SdfKernelInterfaces.MeshSightRow * 4);
    // Every pass reserves early/late entry; cascades also reserve exit, extinction and compact culled entry.
    private readonly uint m_argumentPhases;
    private readonly bool m_optical;
    // The counts' draw phases and portal sight verdicts; the selection marks follow from MeshMarkBase, which the
    // unsigned constant keeps past the verdicts at compile time.
    private const int SightWords = (SdfKernelInterfaces.MeshSightCountBase + SdfPortalSight.Capacity);
    private const uint MarkRoom = (uint)(SdfKernelInterfaces.MeshMarkBase - SightWords);
    private const ulong CountsBytes = (SightWords * sizeof(uint));
    // The draws one mark word holds: two bits each.
    private const uint DrawsPerMarkWord = 16u;
    // The most shares one staged draw list splits into (Stage): eight bits of the controls' fourth word.
    public const int MaxSlices = 256;

    private readonly string m_owner;
    private readonly string m_part;
    private readonly GpuFittedBuffer[] m_candidates;
    private readonly List<IGpuBuffer>[] m_retired;
    private readonly byte[] m_pushed = new byte[GpuPipelineLayoutDescription.PushIndexBytes];

    private readonly GpuFittedBuffer m_visible;
    private IGpuBuffer? m_seedVisible;
    private readonly GpuFittedBuffer m_draws;
    private readonly GpuFittedBuffer m_counts;
    private readonly GpuFittedBuffer m_depth;
    private bool m_visibleCleared;
    private SdfWorldTables? m_historyOwner;
    private uint m_capacity;
    private uint m_count;
    private uint m_levels;
    private uint m_width;
    private uint m_height;
    private bool m_cascade;
    private bool m_receivers;
    private uint m_receiverWidth;
    private uint m_receiverHeight;
    private int m_slot;
    private readonly SdfPortalSight? m_sight;
    private readonly ReadbackSlot[] m_readbackSlots;
    // Each slot's copy of the counts (sight verdicts, selection marks or both), read only once the node proves that
    // slot complete, which precedes the slot's next Stage.
    private readonly RenderGraphSlotResults<ReadbackSlot> m_readbacks;
    // The stages since the last marked one, the frame's marking, whether a late step follows the early one, and the
    // staged draws' row epoch (SdfWorldTables.MeshRowEpoch) the marks name rows of.
    private int m_stagesSinceMark;
    private bool m_marking;
    private bool m_lateFollows;
    private long m_rowEpoch;
    private uint[] m_readWords = [];

    private sealed class ReadbackSlot {
        internal readonly SdfPortalSight.Sighting?[] Sightings = new SdfPortalSight.Sighting?[SdfPortalSight.Capacity];
        internal required GpuFittedBuffer Buffer;
        internal long Epoch;
        internal bool Sight;
        internal bool Marks;
        internal ulong Bytes;
        internal uint Draws;
        internal long RowEpoch;
        internal SdfWorldTables? Tables;
    }

    // Creates a view's occlusion state; every buffer is created on the first frame that needs it.
    public SdfMeshOcclusion(IGpuBufferFactory buffers, int slots, string owner, string part, bool cascade, bool optical = false, SdfPortalSight? sight = null) {
        m_argumentPhases = cascade ? SdfKernelInterfaces.MeshShadowPhases : 2u;
        m_optical = optical;
        m_retired = Enumerable.Range(count: slots, start: 0).Select(selector: static _ => new List<IGpuBuffer>()).ToArray();
        m_owner = owner;
        m_part = part;
        m_sight = sight;
        void Retire(IGpuBuffer buffer) => m_retired[m_slot].Add(buffer);
        m_candidates = Enumerable.Range(0, slots).Select(slot => new GpuFittedBuffer(
            bytes => buffers.CreateHostVisible(bytes, GpuBufferUsage.Storage, Name("occlusion candidates", slot)))).ToArray();
        m_readbackSlots = Enumerable.Range(0, slots).Select(slot => new ReadbackSlot {
            Buffer = new(bytes => buffers.CreateReadback(bytes, Name("selection readback", slot)), retire: Retire),
        }).ToArray();
        m_visible = new(buffers, Name("occlusion visible"), sizing: GpuBufferSizing.Fit, retire: Retire);
        m_draws = new(buffers, Name("occlusion draws"), GpuBufferUsage.Storage | GpuBufferUsage.Indirect, GpuBufferSizing.Fit, Retire);
        m_counts = new(buffers, Name("occlusion counts"), GpuBufferUsage.Storage | GpuBufferUsage.Indirect, retire: Retire);
        m_depth = new(buffers, Name("occlusion depth"), retire: Retire);
        m_readbacks = new RenderGraphSlotResults<ReadbackSlot>(slots: slots, complete: ReadCounts);
        sight?.Attach(this);
    }

    // The pass part the occlusion selects for, which the selection marks are reported under.
    public string Part => m_part;
    // The candidates staged for the frame.
    public uint CandidateCount => m_count;

    internal void Submitted(int slot) => m_readbacks.Submitted(slot: slot);
    // The node proved the slot's previous submission complete: one queue completed every earlier submission too, so the
    // buffers a recording into this slot replaced are read by nothing left and are disposed, and its sight copy is read.
    internal void Completed(int slot) {
        foreach (var buffer in m_retired[slot]) {
            // A recording that never submitted left its seed uncopied; the visible set starts cleared instead.
            if (ReferenceEquals(objA: buffer, objB: m_seedVisible)) { m_seedVisible = null; }
            buffer.Dispose();
        }
        m_retired[slot].Clear();
        m_readbacks.Completed(slot: slot);
    }
    // The slot's previous submission never reached the queue: its counts copy is dropped unread, and the buffers a
    // recording into the slot replaced stay held for the slot's next completion, since nothing proved the queue passed
    // the submissions that may read them.
    internal void Abandoned(int slot) => m_readbacks.Abandoned(slot: slot);
    private void ReadCounts(ReadbackSlot slot) {
        var words = ((int)(slot.Bytes / sizeof(uint)));

        ArrayCapacity.EnsureScratch(ref m_readWords, words);

        var read = m_readWords.AsSpan(length: words, start: 0);

        ((IGpuReadbackBuffer)slot.Buffer.Buffer!).Read(System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: read));
        if (slot.Sight) { m_sight!.Complete(slot.Epoch, slot.Sightings, read.Slice(start: SdfKernelInterfaces.MeshSightCountBase, length: SdfPortalSight.Capacity)); }
        if (slot.Marks) { slot.Tables?.ObserveSelectionMarks(draws: slot.Draws, epoch: slot.RowEpoch, marks: read[SdfKernelInterfaces.MeshMarkBase..], source: this); }
    }
    // The bytes of the counts a frame marking a list of draws reads back: through the last draw's mark word.
    private static ulong MarkedBytes(uint draws) => ((((ulong)SightWords) + MarkRoom + draws.CeilingDivide(divisor: DrawsPerMarkWord)) * sizeof(uint));

    // Each pass uploads constant-sized controls. The residency already uploaded each changed draw once.
    // A cascade that draws one layer of a split refresh (skipsDepth, skipsExit) emits and counts commands for that layer
    // alone, so its counted draws and vertices are the work it draws. A pass staged with a motion filter keeps the static
    // scene's draws alone (SdfMeshRecord.DynamicFlag clear: the distant map, a far cascade's static layer) or the moving
    // draws and every glass draw alone (the far cascade over a copy of its static layer); a draw the filter leaves out keeps
    // its visibility history, so a static and a dynamic staging of one list do not erase each other's. A pass staged with a share of `slices` (a
    // distant map redraw spread over several frames) keeps only the draws whose index leaves that share's remainder, so the
    // shares of one draw list draw each of its draws exactly once. The kernel reads the count and the share from eight bits
    // each of the controls' fourth word (MaxSlices).
    public void Stage(SdfWorldTables tables, int slot, uint count, uint width, uint height, in SdfMeshCull cull, in SdfMeshDetail detail, bool held, bool glass, bool occlude, bool splitExit = false, bool skipsDepth = false, bool skipsExit = false, Puck.Abstractions.Cameras.CameraSnapshot camera = default, uint receiverWidth = 0, uint receiverHeight = 0, System.Numerics.Vector2 jitter = default, SdfMeshMotion motion = SdfMeshMotion.Any, int slice = 0, int slices = 1) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: slices, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: slices, other: MaxSlices);
        ArgumentOutOfRangeException.ThrowIfNegative(value: slice);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: slice, other: slices);
        // The node completed this slot before its recording (Completed): replacements made here retire at its next
        // completion, after this slot's history copy and every preceding submission.
        m_slot = slot;
        m_count = count;
        m_width = Math.Max(val1: width, val2: 1u);
        m_height = Math.Max(val1: height, val2: 1u);
        m_levels = LevelsOf(height: m_height, width: m_width);
        m_cascade = splitExit;
        m_receivers = splitExit && occlude;
        m_receiverWidth = receiverWidth;
        m_receiverHeight = receiverHeight;
        m_lateFollows = (occlude && !skipsDepth);
        var markDue = (++m_stagesSinceMark >= SdfWorldTables.SelectionMarkFrames);

        if (markDue) { m_stagesSinceMark = 0; }
        m_marking = (markDue && tables.MarksSelections);
        m_rowEpoch = tables.MeshRowEpoch;
        var sightSlot = m_readbackSlots[slot];
        Span<System.Numerics.Vector4> rows = stackalloc System.Numerics.Vector4[SdfKernelInterfaces.MeshSightRow + (4 * SdfPortalSight.Capacity)];
        rows.Clear();
        if (occlude && m_sight is not null) { sightSlot.Epoch = m_sight.Capture(rows[SdfKernelInterfaces.MeshSightRow..], sightSlot.Sightings, in camera); }
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<System.Numerics.Vector4, uint>(span: rows);

        words[0] = count;
        words[1] = m_levels;
        words[2] = (occlude ? 1u : 0u) | (splitExit ? 2u : 0u) | (m_argumentPhases << 2);
        words[3] = (glass ? 1u : 0u) | (skipsDepth ? 2u : 0u) | (skipsExit ? 4u : 0u) | (m_marking ? 8u : 0u) | ((motion == SdfMeshMotion.Static) ? 16u : 0u) | ((motion == SdfMeshMotion.Dynamic) ? 32u : 0u) | (m_optical ? 64u : 0u) |
            (NearChain(occlude: occlude) ? 128u : 0u) | (((uint)(slices - 1)) << 8) | (((uint)slice) << 16);
        detail.WriteGpu(rows: rows[1..4], held: held);
        cull.WriteGpu(rows: rows[4..]);
        rows[SdfKernelInterfaces.MeshReceiverRow] = new(receiverWidth, receiverHeight, jitter.X, jitter.Y);
        EnsureBuffers(depth: occlude, draws: count);
        var compact = (SdfKernelInterfaces.MeshCompactRow * 4);

        words[compact] = m_capacity;
        words[compact + 1] = CompactFirstWord;
        words[compact + 2] = GroupFirstWord;
        words[compact + 3] = GroupsOf(capacity: m_capacity);
        // Generations are table-local. Follow can preserve this pass while changing its tables, whose first cells
        // can carry the same tokens as the previous owner. Reset after growth so its seed cannot restore old history.
        if (!ReferenceEquals(objA: m_historyOwner, objB: tables)) {
            m_historyOwner?.ForgetSelectionMarks(source: this);
            m_historyOwner = tables;
            m_seedVisible = null;
            m_visibleCleared = false;
        }
        ((IGpuStorageBuffer)m_candidates[slot].Buffer!).Write(data: words);
    }
    // Writes the occlusion's members of the mesh interface into a mesh set: the frame slot's candidates, the visible set,
    // both phases' draws and counts, the pyramid and the view's mesh target, each only when the set binds another.
    public void Bind(SdfWorldTables tables, nint set, IGpuBuffer?[] bound, int first, ref nint boundTarget, nint target) {
        tables.WriteMeshBuffer(bound: ref bound[(first + 0)], buffer: m_candidates[m_slot].Buffer!, member: SdfKernelInterfaces.MeshOcclusionCandidates, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 1)], buffer: m_visible.Buffer!, member: SdfKernelInterfaces.MeshOcclusionVisibleWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 2)], buffer: m_draws.Buffer!, member: SdfKernelInterfaces.MeshOcclusionDrawsWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 3)], buffer: m_counts.Buffer!, member: SdfKernelInterfaces.MeshOcclusionCountsWritten, set: set);
        tables.WriteMeshBuffer(bound: ref bound[(first + 4)], buffer: (m_depth.Buffer ?? tables.DummyBuffer), member: SdfKernelInterfaces.MeshOcclusionDepthWritten, set: set);
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
        tables.WriteMeshBuffer(bound: ref bound[5], buffer: m_draws.Buffer!, member: SdfKernelInterfaces.MeshIndirectCommands, set: set);
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
        recorder.ClearStorageBuffer(commandBufferHandle: commandBuffer, bufferHandle: m_counts.Buffer!.BufferHandle, sizeBytes: m_counts.Buffer!.SizeBytes);
        Ready(buffer: m_counts.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.TransferWrite, sourceStage: GpuStage.Transfer);

        if (!m_visibleCleared) {
            var copied = (m_seedVisible is not null);

            recorder.ClearStorageBuffer(commandBufferHandle: commandBuffer, bufferHandle: m_visible.Buffer!.BufferHandle, sizeBytes: m_visible.Buffer!.SizeBytes);
            if (m_seedVisible is { } previous) {
                // The old buffer's only use in this recording is the copy, and the global barrier above makes its
                // prior compute writes visible to transfer reads on both backends; it needs no transition of its own.
                recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: m_visible.Buffer!.BufferHandle,
                    sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                    destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
                recorder.CopyBuffer(commandBufferHandle: commandBuffer, sourceBufferHandle: previous.BufferHandle,
                    destinationBufferHandle: m_visible.Buffer!.BufferHandle, sizeBytes: previous.SizeBytes);
                m_seedVisible = null;
            }
            Ready(buffer: m_visible.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: (copied ? GpuAccess.CopyWrite : GpuAccess.TransferWrite), sourceStage: GpuStage.Transfer);
            m_visibleCleared = true;
        }

        Bind(commandBuffer: commandBuffer, pipeline: pipeline, recorder: recorder, set: set);
        if (m_receivers) {
            // The receiver set samples current camera depth before any cascade attachment is drawn.
            recorder.ClearStorageBuffer(commandBufferHandle: commandBuffer, bufferHandle: m_depth.Buffer!.BufferHandle, sizeBytes: m_depth.Buffer!.SizeBytes);
            Ready(buffer: m_depth.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.TransferWrite, sourceStage: GpuStage.Transfer);
            Dispatch(commandBuffer: commandBuffer, invocations: m_receiverWidth * m_receiverHeight, level: 0, pipeline: pipeline, recorder: recorder, step: SdfKernelInterfaces.MeshReceiverStep);
            Ready(buffer: m_depth.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
            var width = Extent(m_width, SdfKernelInterfaces.MeshReceiverShift);
            var height = Extent(m_height, SdfKernelInterfaces.MeshReceiverShift);
            for (var level = 1u; level <= LevelsOf(width, height); level++) {
                Dispatch(commandBuffer: commandBuffer, invocations: Extent(width, level) * Extent(height, level), level: level, pipeline: pipeline, recorder: recorder, step: SdfKernelInterfaces.MeshReceiverReduceStep);
                Ready(buffer: m_depth.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
            }
        }
        Dispatch(commandBuffer: commandBuffer, invocations: m_count, level: 0u, pipeline: pipeline, recorder: recorder, step: SdfKernelInterfaces.MeshOcclusionEarlyStep);
        Compact(commandBuffer: commandBuffer, late: false, pipeline: pipeline, recorder: recorder);
        if (!m_lateFollows) { RecordReadback(commandBuffer: commandBuffer, recorder: recorder, sight: false); }
        ToIndirect(commandBuffer: commandBuffer, recorder: recorder);
    }
    internal void RecordOpticalClear(IGpuRecorder recorder, nint commandBuffer, IGpuComputePipeline pipeline, nint set, uint side) {
        Bind(commandBuffer: commandBuffer, pipeline: pipeline, recorder: recorder, set: set);
        Dispatch(commandBuffer: commandBuffer, invocations: SdfWorldPackage.ShadowOpticalBitmapWords(side) * SdfKernelInterfaces.MeshSelectionGroupSize,
            level: side, pipeline: pipeline, recorder: recorder, step: SdfKernelInterfaces.MeshShadowOpticalClearStep);
    }
    // Copies the counts after the frame's last selection step into the slot's readback, when the step computed portal
    // sight verdicts or the frame marks its selection; the node delivers the copy once it proves the slot complete.
    private void RecordReadback(IGpuRecorder recorder, nint commandBuffer, bool sight) {
        if (!sight && !m_marking) { return; }

        var slot = m_readbackSlots[m_slot];
        var bytes = (m_marking ? MarkedBytes(draws: m_count) : CountsBytes);

        slot.Buffer.Fit(Math.Max(bytes, CountsBytes));
        GpuReadbackCopy.Record(commandBuffer: commandBuffer, destination: (IGpuReadbackBuffer)slot.Buffer.Buffer!, recorder: recorder, sizeBytes: bytes,
            source: m_counts.Buffer!, sourceAccess: GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
        Ready(buffer: m_counts.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.TransferRead, sourceStage: GpuStage.Transfer);
        slot.Sight = sight;
        slot.Marks = m_marking;
        slot.Bytes = bytes;
        slot.Draws = m_count;
        slot.RowEpoch = m_rowEpoch;
        slot.Tables = m_historyOwner;
        m_readbacks.Record(slot: m_slot, result: slot);
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
            argumentBufferHandle: m_draws.Buffer!.BufferHandle,
            argumentBufferOffset: ((((ulong)(m_argumentPhases + ((uint)phase))) * m_capacity) * GpuDrawIndirectCommand.StrideBytes),
            commandBufferHandle: commandBuffer,
            countBufferHandle: m_counts.Buffer!.BufferHandle,
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
            oldLayout: m_cascade ? GpuImageLayout.DepthAttachment : GpuImageLayout.RenderTarget,
            sourceAccessMask: m_cascade ? GpuAccess.DepthAttachmentWrite : GpuAccess.ColorAttachmentWrite,
            sourceStageMask: m_cascade ? GpuStage.FragmentTests : GpuStage.ColorAttachmentOutput
        );
        Bind(commandBuffer: commandBuffer, pipeline: pipeline, recorder: recorder, set: set);

        Ready(buffer: m_depth.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
        for (var level = 1u; (level <= m_levels); level++) {
            var width = Extent(level: level, size: m_width);
            var height = Extent(level: level, size: m_height);

            Dispatch(commandBuffer: commandBuffer, invocations: (width * height), level: level, pipeline: pipeline, recorder: recorder, step: SdfKernelInterfaces.MeshOcclusionDepthStep);
            Ready(buffer: m_depth.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
        }

        Ready(buffer: m_visible.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
        recorder.BeginBarriers(commandBufferHandle: commandBuffer);
        Ready(buffer: m_draws.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.IndirectCommandRead | GpuAccess.ShaderRead, sourceStage: GpuStage.DrawIndirect | GpuStage.VertexShader);
        Ready(buffer: m_counts.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.IndirectCommandRead, sourceStage: GpuStage.DrawIndirect);
        recorder.EndBarriers(commandBufferHandle: commandBuffer);
        Dispatch(commandBuffer: commandBuffer, invocations: m_count, level: 0u, pipeline: pipeline, recorder: recorder, step: SdfKernelInterfaces.MeshOcclusionLateStep);
        Compact(commandBuffer: commandBuffer, late: true, pipeline: pipeline, recorder: recorder);
        if (m_sight is not null) {
            Ready(buffer: m_counts.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
            Dispatch(commandBuffer: commandBuffer, invocations: SdfPortalSight.Capacity, level: 0u, pipeline: pipeline, recorder: recorder, step: SdfKernelInterfaces.MeshOcclusionSightStep);
        }
        RecordReadback(commandBuffer: commandBuffer, recorder: recorder, sight: (m_sight is not null));
        ToIndirect(commandBuffer: commandBuffer, recorder: recorder);
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: m_cascade ? GpuAccess.DepthAttachmentRead | GpuAccess.DepthAttachmentWrite : GpuAccess.ColorAttachmentRead | GpuAccess.ColorAttachmentWrite,
            destinationStageMask: m_cascade ? GpuStage.FragmentTests : GpuStage.ColorAttachmentOutput,
            imageHandle: target,
            newLayout: m_cascade ? GpuImageLayout.DepthAttachment : GpuImageLayout.RenderTarget,
            oldLayout: GpuImageLayout.ShaderReadOnly,
            sourceAccessMask: GpuAccess.ShaderRead,
            sourceStageMask: GpuStage.ComputeShader
        );
    }
    public void Dispose() {
        m_sight?.Detach(this);
        m_historyOwner?.ForgetSelectionMarks(source: this);
        foreach (var slot in m_readbackSlots) { slot.Buffer.Dispose(); }
        foreach (var candidates in m_candidates) {
            candidates.Dispose();
        }

        m_visible.Dispose();
        m_draws.Dispose();
        m_counts.Dispose();
        m_depth.Dispose();
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

        m_candidates[m_slot].Fit(candidateBytes);
        var grown = (uint)GpuCapacity.Grown(m_capacity, capacity, uint.MaxValue, divisor: 1);
        var previous = m_visible.Buffer;
        if (m_visible.Fit((ulong)grown * 4 * sizeof(uint))) {
            m_seedVisible = previous;
            m_visibleCleared = false;
        }
        m_capacity = grown;
        m_draws.Fit((((ulong)GroupFirstWord) + (m_argumentPhases * GroupsOf(capacity: grown))) * sizeof(uint));
        m_counts.Fit(MarkedBytes(draws: grown));

        if (!depth) { return; }
        var depthBytes = (DepthWords(height: m_height, levels: m_levels, width: m_width) * sizeof(uint) * (NearChain(occlude: depth) ? 2UL : 1UL));
        if (m_receivers) {
            var width = Extent(m_width, SdfKernelInterfaces.MeshReceiverShift);
            var height = Extent(m_height, SdfKernelInterfaces.MeshReceiverShift);
            depthBytes += ((ulong)width * height + DepthWords(width, height, LevelsOf(width, height))) * sizeof(uint);
        }

        m_depth.Fit(depthBytes);
    }
    private GpuObjectName Name(string detail, int index = 0) => new(detail: detail, index: index, owner: m_owner, part: m_part);
    // Whether the pyramid builds its nearest chain after its farthest, which the portal sight's test over a reach of eyes
    // reads (sdfOcclusionHiddenWithin): only an occluding pass that tests portal sight.
    private bool NearChain(bool occlude) => (occlude && (m_sight is not null));
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
    // The first word of each phase's compact list in the draws buffer, past every phase's slots, and of the group counts,
    // past every compact list (sdfOcclusionCompact).
    private uint CompactFirstWord => checked(m_argumentPhases * m_capacity * GpuDrawIndirectCommand.StrideBytes / sizeof(uint));
    private uint GroupFirstWord => checked(2u * CompactFirstWord);
    private static uint GroupsOf(uint capacity) => capacity.CeilingDivide(divisor: SdfKernelInterfaces.MeshSelectionGroupSize);
    // Compacts the phases the step just recorded emitted (the early step's, or the late step's second phase) into their
    // compact lists and draw counts, in staged order: each group counts its emitted commands, then copies them past the
    // counts of the groups before it (sdfOcclusionCompact).
    private void Compact(IGpuRecorder recorder, nint commandBuffer, IGpuComputePipeline pipeline, bool late) {
        Ready(buffer: m_draws.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
        Dispatch(commandBuffer: commandBuffer, invocations: m_count, level: (late ? 1u : 0u), pipeline: pipeline, recorder: recorder, step: SdfKernelInterfaces.MeshOcclusionCountStep);
        Ready(buffer: m_draws.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
        Dispatch(commandBuffer: commandBuffer, invocations: m_count, level: (late ? 1u : 0u), pipeline: pipeline, recorder: recorder, step: SdfKernelInterfaces.MeshOcclusionCompactStep);
        Ready(buffer: m_counts.Buffer!, commandBuffer: commandBuffer, recorder: recorder, source: GpuAccess.ShaderWrite, sourceStage: GpuStage.ComputeShader);
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

        foreach (var buffer in ((ReadOnlySpan<IGpuBuffer>)[m_draws.Buffer!, m_counts.Buffer!])) {
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
/// <summary>Which draws a staging of the selection keeps by whether they can move (<see cref="SdfMeshRecord.DynamicFlag"/>).</summary>
internal enum SdfMeshMotion : byte {
    /// <summary>Every draw.</summary>
    Any,
    /// <summary>The static scene's draws alone.</summary>
    Static,
    /// <summary>The draws that can move, and every glass draw.</summary>
    Dynamic,
}
