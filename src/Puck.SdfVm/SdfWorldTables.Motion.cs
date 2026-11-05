using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// GPU-only history: before overwriting this frame's tables, copy the rows changed in the preceding consumed frame.
// One settle copy after motion stops is owed; later still frames copy nothing, and a still upload of a frame held under
// the motion view owes it on until the hold ends (HoldsFrame). A row with no previous pose of its own
// is seeded from this frame's after the ordinary copies, so it reads as still: every row on the first upload and after
// a program geometry change or a frame owing every row, a range whose owner changed (SdfMovedTransforms.Reseat), and a mesh
// draw whose observed surface incarnation at its index changed (SdfMeshHistory.Observe). The previous mesh table holds
// only each draw's matrix. The shared region-copy kernel gathers changed rows from the mesh region in one dispatch,
// so GpuRegion's CPU-shadow contract holds and only copy requests, never a second matrix, cross the host bus.
public sealed partial class SdfWorldTables {
    private const int MeshMatrixBytes = (SdfMeshRecord.MatrixWords * sizeof(float));
    private const GpuStage TableReaders = GpuStage.ComputeShader | GpuStage.VertexShader | GpuStage.FragmentShader;

    private IGpuBuffer m_previousDynamicTransforms;
    private IGpuBuffer m_previousMeshTransforms;
    // Separate requests for the pre-upload advance and post-upload seeds, per retired upload slot.
    private readonly GpuBufferStridedCopy m_meshHistoryCopies;

    private readonly GpuUploadRuns m_changedTransforms = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_previousTransformCopies = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_reseatedTransforms = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_changedMeshMatrices = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_previousMeshCopies = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_seededMeshDraws = new(capacity: GpuRegion.MaxCopyRuns);
    // Whether the next upload seeds a whole previous table from the current one.
    private bool m_seedDynamicHistory = true;
    private bool m_seedMeshHistory = true;

    private uint m_previousMeshDrawCount;

    // One observation door supplies both matrix seed decisions and the GPU history generation.
    private readonly SdfMeshHistory m_meshHistory = new();

    // Mesh history consumes the transform journal independently of the dynamic-table and draw-journal cursors.
    // A successful stage commits this endpoint, while GPU seed runs remain pending until Upload consumes them.
    private SdfMovedTransforms? m_meshTransformsSource;
    private IReadOnlyList<DynamicTransform>? m_meshTransformsTable;
    private long m_meshTransformsSerial;
    private ulong m_meshTransformsGeometryRevision;
    private int m_meshTransformsCount = -1;
    private int m_meshCutTransformCount;
    private bool m_meshTransformCutsAll;
    private readonly GpuUploadRuns m_meshTransformReseats = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_reseatedDraws = new(capacity: GpuRegion.MaxCopyRuns);

    private void PrepareMeshTransformCuts(SdfFrame frame) {
        m_meshTransformReseats.Clear();
        m_reseatedDraws.Clear();
        m_meshCutTransformCount = frame.DynamicTransforms.Count;
        var moved = frame.MovedTransforms;
        m_meshTransformCutsAll = ((m_meshTransformsCount != m_meshCutTransformCount) ||
            (m_meshTransformsGeometryRevision != m_programGeometryRevision) ||
            !ReferenceEquals(moved, m_meshTransformsSource) ||
            ((moved is null)
                ? !ReferenceEquals(frame.DynamicTransforms, m_meshTransformsTable)
                : !moved.TryCollect(since: m_meshTransformsSerial, through: frame.MovedTransformsSerial,
                    into: null, reseated: m_meshTransformReseats)));

        if (m_meshTransformCutsAll) { SdfMeshHistory.ProcessWork.Count(SdfMeshHistory.ReseatFallbacks); }
        if (!m_meshTransformCutsAll && (m_meshTransformReseats.Count == 0)) { return; }

        // A roster walk is needed only on a cut, never for ordinary continuous movement. These indices are used only
        // with the complete producer roster; the observation door rechecks actual draws after landing filters.
        var draws = frame.MeshDraws;
        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.ReseatDrawsScanned, draws.Count);
        for (var index = 0; index < draws.Count; index++) {
            var draw = draws[index];
            if (MeshTransformWasReseated(in draw)) { m_reseatedDraws.Add(start: index, length: 1); }
        }
    }
    private void CommitMeshTransformCuts(SdfFrame frame) {
        m_meshTransformsSource = frame.MovedTransforms;
        m_meshTransformsTable = frame.DynamicTransforms;
        m_meshTransformsSerial = frame.MovedTransformsSerial;
        m_meshTransformsCount = frame.DynamicTransforms.Count;
        m_meshTransformsGeometryRevision = m_programGeometryRevision;
    }
    private bool MeshTransformWasReseated(in SdfMeshDraw draw) {
        if (!m_meshTransformCutsAll && (m_meshTransformReseats.Count == 0)) { return false; }
        // FrameSlot -1 is a sentinel, not a base from which a positive member offset can make a valid dependency.
        if ((draw.FrameSlot is not { } root) || (root < 0) || (root >= m_meshCutTransformCount)) { return false; }
        SdfMeshHistory.ProcessWork.Count(SdfMeshHistory.ReseatDependenciesChecked);
        // Every member also uses the root matrix's scale, so the root is a dependency even for fully skinned meshes.
        if (m_meshTransformCutsAll || m_meshTransformReseats.Contains(root)) { return true; }
        if (draw.Mesh.VertexMembers.IsEmpty) { return false; }

        var members = draw.Mesh.Members.Span;
        for (var index = 0; index < members.Length; index++) {
            var slot = (((long)root) + members[index].SlotOffset);
            SdfMeshHistory.ProcessWork.Count(SdfMeshHistory.ReseatDependenciesChecked);
            if ((slot < 0) || (slot >= m_meshCutTransformCount) || !m_meshTransformReseats.Contains((int)slot)) { continue; }
            // Members may contain unused entries. Only a member named by a vertex can affect this draw's history.
            SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.ReseatMemberBytesChecked, draw.Mesh.VertexMembers.Length);
            if (draw.Mesh.VertexMembers.Span.Contains((byte)index)) { return true; }
        }
        return false;
    }

    /// <summary>Gets the revision of the poses the current dynamic-transform and mesh-matrix tables hold as of the latest
    /// upload. It moves on every upload that changes a pose or seeds a previous row, and never otherwise.</summary>
    public long PoseRevision { get; private set; }
    /// <summary>Gets the revision of the poses the previous tables were advanced from as of the latest upload: the
    /// <see cref="PoseRevision"/> before that upload. A view whose latest render held these poses as current reads its
    /// motion from them; a seeded row reads as still.</summary>
    public long PreviousPoseRevision { get; private set; }
    /// <summary>Gets or sets whether the frame the tables hold is held for a capture (<see cref="SdfWorldResidency.HoldsFrame"/>),
    /// which the residency sets before each upload. While it is held and the <c>motion</c> debug view is shown, an upload
    /// that moves no pose leaves the previous tables and both pose revisions as they stand, so every render of the held
    /// frame reads the motion it was presented with rather than settling to still. A pose that moves still advances them.
    /// Any other view settles as usual: a temporal view's history accumulates over the held frame's renders, whose poses
    /// do not move between them.</summary>
    public bool HoldsFrame { get; set; }

    // Whether the latest pack owes a pose: a row it changed or seeds, or a mesh draw it moved, seeds or adds or drops.
    private bool PosesMoved => (
        m_seedDynamicHistory ||
        m_seedMeshHistory ||
        (m_changedTransforms.Count > 0) ||
        (m_reseatedTransforms.Count > 0) ||
        (m_changedMeshMatrices.Count > 0) ||
        (m_seededMeshDraws.Count > 0) ||
        (m_meshDrawCount != m_previousMeshDrawCount)
    );
    // Whether this upload keeps the motion history as it stands (HoldsFrame).
    private bool KeepsMotion => (HoldsFrame && (DebugMode == DebugViewModes.Motion) && !PosesMoved);

    private void RecordPreviousTables(nint commandBuffer, int previousSlot) {
        if (!m_seedDynamicHistory && (m_previousTransformCopies.Count > 0)) {
            CopyTransformHistory(commandBuffer: commandBuffer, copies: m_previousTransformCopies, slot: previousSlot);
        }
        if (!m_seedMeshHistory && (m_previousMeshCopies.Count > 0)) {
            CopyMeshHistory(commandBuffer: commandBuffer, slot: previousSlot, copies: m_previousMeshCopies,
                limit: Math.Min(val1: m_previousMeshDrawCount, val2: m_meshDrawCount), recording: 0);
        }
    }
    private void CompletePreviousTables(nint commandBuffer) {
        var moved = PosesMoved;

        PreviousPoseRevision = PoseRevision;
        if (moved) {
            PoseRevision++;
        }

        var seedTransforms = m_seedDynamicHistory;

        if (seedTransforms) {
            m_reseatedTransforms.Clear();
            m_reseatedTransforms.Add(length: ((int)(m_previousDynamicTransforms.SizeBytes / DynamicTransformByteLength)), start: 0);
        }
        if (m_reseatedTransforms.Count > 0) {
            CopyTransformHistory(commandBuffer: commandBuffer, copies: m_reseatedTransforms, slot: m_currentSlot);
        }
        m_reseatedTransforms.Clear();
        m_previousTransformCopies.Clear();
        if (!seedTransforms) {
            CopyRuns(from: m_changedTransforms, to: m_previousTransformCopies);
        }
        m_changedTransforms.Clear();
        m_seedDynamicHistory = false;

        var seedMesh = m_seedMeshHistory;

        if (seedMesh) {
            m_seededMeshDraws.Clear();
            m_seededMeshDraws.Add(length: ((int)m_meshDrawCount), start: 0);
        }
        CopyMeshHistory(commandBuffer: commandBuffer, copies: m_seededMeshDraws, limit: m_meshDrawCount, slot: m_currentSlot, recording: 1);
        m_seededMeshDraws.Clear();
        m_previousMeshCopies.Clear();
        if (!seedMesh) {
            CopyRuns(from: m_changedMeshMatrices, to: m_previousMeshCopies);
        }
        m_changedMeshMatrices.Clear();
        m_seedMeshHistory = false;
        m_previousMeshDrawCount = m_meshDrawCount;
    }
    private static void CopyRuns(GpuUploadRuns from, GpuUploadRuns to) {
        for (var run = 0; (run < from.Count); run++) {
            to.Add(start: from.Start(index: run), length: from.Length(index: run));
        }
    }
    private void CopyTransformHistory(nint commandBuffer, GpuUploadRuns copies, int slot) {
        var source = m_dynamicTransformRegion.Buffer(slot: slot);
        var rows = ((int)(m_previousDynamicTransforms.SizeBytes / DynamicTransformByteLength));

        BeginHistoryCopy(commandBuffer: commandBuffer, destination: m_previousDynamicTransforms, source: source);
        for (var run = 0; (run < copies.Count); run++) {
            var start = copies.Start(index: run);
            var end = Math.Min(val1: (start + copies.Length(index: run)), val2: rows);

            if (end > start) {
                var offset = (((ulong)start) * DynamicTransformByteLength);

                CopyHistoryRange(bytes: (((ulong)(end - start)) * DynamicTransformByteLength), commandBuffer: commandBuffer, destination: m_previousDynamicTransforms,
                    destinationOffset: offset, source: source, sourceOffset: offset);
            }
        }
        FinishHistoryCopy(commandBuffer: commandBuffer, destination: m_previousDynamicTransforms, source: source);
    }
    private void CopyMeshHistory(nint commandBuffer, int slot, GpuUploadRuns copies, uint limit, int recording) {
        if ((copies.Count == 0) || (limit == 0)) {
            return;
        }
        var source = m_meshRegion.Buffer(slot: slot);

        m_gpu.Recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, label: ((recording == 0) ? "mesh history advance" : "mesh history seed"));
        BeginHistoryCopy(commandBuffer: commandBuffer, destination: m_previousMeshTransforms, source: source, compute: true);
        var work = m_meshHistoryCopies.Record(commandBuffer: commandBuffer, slot: m_currentSlot, recording: recording,
            source: source, destination: m_previousMeshTransforms, rows: copies, rowLimit: limit,
            rowWords: SdfMeshRecord.MatrixWords, sourceStrideWords: SdfMeshRecord.DrawWords, destinationStrideWords: SdfMeshRecord.MatrixWords);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.HistoryRowsCopied, amount: work.Rows);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.HistoryBytesCopied, amount: ((long)work.Words * sizeof(uint)));
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.HistoryRequestBytes, amount: work.RequestBytes);
        if (work.Words > 0) { SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.HistoryDispatches); }
        FinishHistoryCopy(commandBuffer: commandBuffer, destination: m_previousMeshTransforms, source: source, compute: true);
        m_gpu.Recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
    }
    private void CopyHistoryRange(nint commandBuffer, IGpuBuffer source, IGpuBuffer destination, ulong sourceOffset, ulong destinationOffset, ulong bytes) =>
        m_gpu.Recorder.CopyBuffer(commandBufferHandle: commandBuffer, sourceBufferHandle: source.BufferHandle,
            destinationBufferHandle: destination.BufferHandle, sizeBytes: bytes, sourceOffsetBytes: sourceOffset, destinationOffsetBytes: destinationOffset);
    private void BeginHistoryCopy(nint commandBuffer, IGpuBuffer source, IGpuBuffer destination, bool compute = false) {
        // An unread routed world can copy history again without an intervening shader reader. Order earlier copies
        // as well as readers and region-upload shader writes. The buffer transitions below declare no prior access:
        // the global barrier is what orders it.
        m_gpu.Recorder.MemoryBarrier(commandBufferHandle: commandBuffer,
            destinationAccessMask: (compute ? (GpuAccess.ShaderRead | GpuAccess.ShaderWrite) : (GpuAccess.TransferRead | GpuAccess.CopyWrite)),
            destinationStageMask: (compute ? GpuStage.ComputeShader : GpuStage.Transfer),
            sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite | GpuAccess.CopyWrite, sourceStageMask: TableReaders | GpuStage.Transfer);
        m_gpu.Recorder.TransitionBuffer(bufferHandle: source.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
            destinationAccessMask: (compute ? GpuAccess.ShaderRead : GpuAccess.TransferRead), destinationStageMask: (compute ? GpuStage.ComputeShader : GpuStage.Transfer));
        m_gpu.Recorder.TransitionBuffer(bufferHandle: destination.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
            destinationAccessMask: (compute ? GpuAccess.ShaderWrite : GpuAccess.CopyWrite), destinationStageMask: (compute ? GpuStage.ComputeShader : GpuStage.Transfer));
    }
    private void FinishHistoryCopy(nint commandBuffer, IGpuBuffer source, IGpuBuffer destination, bool compute = false) {
        m_gpu.Recorder.TransitionBuffer(bufferHandle: source.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: (compute ? GpuAccess.ShaderRead : GpuAccess.TransferRead), sourceStageMask: (compute ? GpuStage.ComputeShader : GpuStage.Transfer),
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: TableReaders);
        m_gpu.Recorder.TransitionBuffer(bufferHandle: destination.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: (compute ? GpuAccess.ShaderWrite : GpuAccess.CopyWrite), sourceStageMask: (compute ? GpuStage.ComputeShader : GpuStage.Transfer),
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
    }
    // Observes and packs one record through the same door for full and incremental staging.
    private ulong StageMeshDraw(int index, in SdfMeshDraw draw, Span<uint> record, ReadOnlySpan<byte> old, ref StagingTally tally, bool incremental = false) {
        var observation = m_meshHistory.Observe(draw: in draw, index: index, reseat: MeshTransformWasReseated(in draw));
        var packedWords = SdfMeshRecord.DrawWords;
        var changedWords = 0UL;
        if (incremental) {
            // Incremental staging admits only standing arena addresses. An unchanged mesh already names its
            // placement in this retained record; only a mesh replacement needs the arena's reference lookup.
            var placement = ReferenceEquals(draw.Mesh, m_stagedDraws[index].Mesh)
                ? SdfMeshRegion.ReadPlacement(record)
                : m_meshArena.Placements[draw.Mesh];
            var work = SdfMeshRegion.WriteDrawIncremental(draw: in draw, previous: in m_stagedDraws[index],
                placement: placement, generation: observation.Generation, record: record);
            packedWords = work.PackedWords;
            changedWords = work.ChangedWords;
            if (work.NormalComputed) { tally.Normals++; }
            if (work.SelectionComputed) { tally.Selections++; }
        } else {
            SdfMeshRegion.WriteDraw(draw: in draw, placement: m_meshArena.Placements[draw.Mesh], generation: observation.Generation, record: record);
            tally.Normals++;
            tally.Selections++;
        }
        tally.Records++;
        tally.RecordBytes += (packedWords * sizeof(uint));
        if (observation.Seed) {
            m_seededMeshDraws.Add(length: 1, start: index);
            return changedWords;
        }
        var offset = (index * SdfMeshRegion.DrawBytes);
        var matrix = System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: record[..SdfMeshRecord.MatrixWords]);

        if (incremental ? (changedWords & ((1UL << SdfMeshRecord.MatrixWords) - 1)) != 0 :
            (offset > (old.Length - MeshMatrixBytes)) || !matrix.SequenceEqual(other: old.Slice(length: MeshMatrixBytes, start: offset))) {
            m_changedMeshMatrices.Add(length: 1, start: index);
            tally.Matrices++;
        }
        return changedWords;
    }
    // The staging work of one pass over draw records, counted locally and published once: an interlocked count per
    // record and kind was a measurable share of staging thousands of records a frame.
    private struct StagingTally {
        public long Normals;
        public long Selections;
        public long Records;
        public long RecordBytes;
        public long Matrices;

        public readonly void Publish() {
            var work = SdfMeshHistory.ProcessWork;

            work.Add(kind: SdfMeshHistory.NormalMatricesComputed, amount: Normals);
            work.Add(kind: SdfMeshHistory.SelectionBoxesComputed, amount: Selections);
            work.Add(kind: SdfMeshHistory.RecordsPacked, amount: Records);
            work.Add(kind: SdfMeshHistory.RecordBytesPacked, amount: RecordBytes);
            work.Add(kind: SdfMeshHistory.MatricesChanged, amount: Matrices);
            work.Add(kind: SdfMeshHistory.MatrixBytesChanged, amount: (Matrices * MeshMatrixBytes));
        }
    }
    // Packs a complete newly staged list and retires vanished cells while retaining their generation lifetimes.
    private void StageMeshMotion(Span<uint> words, IReadOnlyList<SdfMeshDraw> draws) {
        var old = m_meshRegion.ContentsAt(
            length: ((int)Math.Min(val1: m_meshRegion.ByteCount, val2: (((long)draws.Count) * SdfMeshRegion.DrawBytes))),
            offset: 0
        );

        var tally = new StagingTally();

        for (var index = 0; (index < draws.Count); index++) {
            var draw = draws[index];

            StageMeshDraw(index: index, draw: in draw, record: words.Slice(length: SdfMeshRecord.DrawWords, start: (index * SdfMeshRecord.DrawWords)), old: old, tally: ref tally);
        }
        tally.Publish();
        m_meshHistory.RetireAfter(count: draws.Count);

        var bytes = (((ulong)Math.Max(val1: 1, val2: draws.Count)) * MeshMatrixBytes);

        if (bytes <= m_previousMeshTransforms.SizeBytes) {
            return;
        }
        // Twice the need, as the mesh region grows, so draws landing over several frames replace it once; the old buffer is
        // disposed once the next upload completes, after every view that bound it.
        var replacement = m_gpu.BufferFactory.CreateDeviceLocal(name: NameOf(part: "previous-mesh-transforms"), sizeBytes: (2UL * bytes), usage: GpuBufferUsage.Storage);

        Retire(replaced: m_previousMeshTransforms);
        m_previousMeshTransforms = replacement;
        m_bindingRevision++;
        m_seedMeshHistory = true;
    }
}
