using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// GPU-only history: before overwriting this frame's tables, copy the rows changed in the preceding consumed frame.
// One settle copy after motion stops is owed; later still frames copy nothing, and a still upload of a frame held under
// the motion view owes it on until the hold ends (HoldsFrame). A row with no previous pose of its own
// is seeded from this frame's after the ordinary copies, so it reads as still: every row on the first upload and after
// a program geometry change or a frame owing every row, a range whose owner changed (SdfMovedTransforms.Reseat), and a mesh
// draw whose identity at its index changed (SdfMeshDraw.Identity). The previous mesh table holds whole draw records at the
// mesh region's stride (SdfMeshRegion.DrawBytes), of which frame/sdf-reprojection.hlsli reads only the matrix, so a run of
// draws copies as one range; it is a device-side copy, so GpuRegion's CPU-shadow contract holds and no second matrix crosses
// the host bus.
public sealed partial class SdfWorldTables {
    private const int MeshMatrixBytes = (16 * sizeof(float));
    private const GpuStage TableReaders = GpuStage.ComputeShader | GpuStage.VertexShader | GpuStage.FragmentShader;

    private readonly IGpuBuffer m_previousDynamicTransforms;

    private IGpuBuffer m_previousMeshTransforms;

    private readonly GpuUploadRuns m_changedTransforms = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_previousTransformCopies = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_reseatedTransforms = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_changedMeshMatrices = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_previousMeshCopies = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_seededMeshDraws = new(capacity: GpuRegion.MaxCopyRuns);

    // Whether a previous table has been written, so its first copy waits on no earlier reader.
    private bool m_dynamicHistoryWritten;
    private bool m_meshHistoryWritten;

    // Whether the next upload seeds a whole previous table from the current one.
    private bool m_seedDynamicHistory = true;
    private bool m_seedMeshHistory = true;

    private uint m_previousMeshDrawCount;

    // The identity of each draw last staged, by index, which the next staged list is compared against.
    private object?[] m_meshIdentities = [];

    private int m_meshIdentityCount;

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
            // One range over every changed draw: a draw between them that did not change already holds the preceding
            // frame's record in the previous table, so copying it again from that frame's slot writes what it holds.
            var first = int.MaxValue;
            var end = 0;

            for (var run = 0; (run < m_previousMeshCopies.Count); run++) {
                first = Math.Min(val1: first, val2: m_previousMeshCopies.Start(index: run));
                end = Math.Max(val1: end, val2: (m_previousMeshCopies.Start(index: run) + m_previousMeshCopies.Length(index: run)));
            }

            m_previousMeshCopies.Clear();
            m_previousMeshCopies.Add(length: (end - first), start: first);
            CopyMeshHistory(commandBuffer: commandBuffer, slot: previousSlot, copies: m_previousMeshCopies,
                limit: Math.Min(val1: m_previousMeshDrawCount, val2: m_meshDrawCount));
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
        CopyMeshHistory(commandBuffer: commandBuffer, copies: m_seededMeshDraws, limit: m_meshDrawCount, slot: m_currentSlot);
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

        BeginHistoryCopy(commandBuffer: commandBuffer, destination: m_previousDynamicTransforms, initialized: m_dynamicHistoryWritten, source: source);
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
        m_dynamicHistoryWritten = true;
    }
    private void CopyMeshHistory(nint commandBuffer, int slot, GpuUploadRuns copies, uint limit) {
        if ((copies.Count == 0) || (limit == 0)) {
            return;
        }
        var source = m_meshRegion.Buffer(slot: slot);

        BeginHistoryCopy(commandBuffer: commandBuffer, destination: m_previousMeshTransforms, initialized: m_meshHistoryWritten, source: source);
        for (var run = 0; (run < copies.Count); run++) {
            var start = copies.Start(index: run);
            var end = Math.Min(val1: (start + copies.Length(index: run)), val2: ((int)limit));

            if (end > start) {
                var offset = (((ulong)start) * SdfMeshRegion.DrawBytes);

                CopyHistoryRange(bytes: (((ulong)(end - start)) * SdfMeshRegion.DrawBytes), commandBuffer: commandBuffer, destination: m_previousMeshTransforms,
                    destinationOffset: offset, source: source, sourceOffset: offset);
            }
        }
        FinishHistoryCopy(commandBuffer: commandBuffer, destination: m_previousMeshTransforms, source: source);
        m_meshHistoryWritten = true;
    }
    private void CopyHistoryRange(nint commandBuffer, IGpuBuffer source, IGpuBuffer destination, ulong sourceOffset, ulong destinationOffset, ulong bytes) =>
        m_gpu.Recorder.CopyBuffer(commandBufferHandle: commandBuffer, sourceBufferHandle: source.BufferHandle,
            destinationBufferHandle: destination.BufferHandle, sizeBytes: bytes, sourceOffsetBytes: sourceOffset, destinationOffsetBytes: destinationOffset);
    private void BeginHistoryCopy(nint commandBuffer, IGpuBuffer source, IGpuBuffer destination, bool initialized) {
        m_gpu.Recorder.TransitionBuffer(bufferHandle: source.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: TableReaders,
            destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
        m_gpu.Recorder.TransitionBuffer(bufferHandle: destination.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: (initialized ? GpuAccess.ShaderRead : GpuAccess.None), sourceStageMask: (initialized ? GpuStage.ComputeShader : GpuStage.TopOfPipe),
            destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
    }
    private void FinishHistoryCopy(nint commandBuffer, IGpuBuffer source, IGpuBuffer destination) {
        m_gpu.Recorder.TransitionBuffer(bufferHandle: source.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: TableReaders);
        m_gpu.Recorder.TransitionBuffer(bufferHandle: destination.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
    }
    // Compares a newly staged draw list with the one last staged: a draw whose identity at its index changed is seeded,
    // and one that kept its identity and moved its matrix is copied from the preceding upload's table.
    private void StageMeshMotion(ReadOnlySpan<uint> words, IReadOnlyList<SdfMeshDraw> draws) {
        // Only the draw records are compared, so a region past what one span reaches is read at its front.
        var old = m_meshRegion.ContentsAt(
            length: ((int)Math.Min(
                val1: m_meshRegion.ByteCount,
                val2: (((long)draws.Count) * SdfMeshRegion.DrawBytes)
            )),
            offset: 0
        );

        for (var draw = 0; (draw < draws.Count); draw++) {
            if (draws[draw].Identity is not { } identity) {
                throw new ArgumentException(message: $"Mesh draw {draw} names no identity.", paramName: nameof(draws));
            }
            if ((draw >= m_meshIdentityCount) || !identity.Equals(obj: m_meshIdentities[draw])) {
                m_seededMeshDraws.Add(length: 1, start: draw);
                continue;
            }

            var offset = (draw * SdfMeshRegion.DrawBytes);
            var matrix = System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: words.Slice(length: 16, start: (draw * SdfMeshRecord.DrawWords)));

            if ((offset > (old.Length - MeshMatrixBytes)) || !matrix.SequenceEqual(other: old.Slice(length: MeshMatrixBytes, start: offset))) {
                m_changedMeshMatrices.Add(length: 1, start: draw);
            }
        }
        if (m_meshIdentities.Length < draws.Count) {
            Array.Resize(array: ref m_meshIdentities, newSize: draws.Count);
        }
        for (var draw = 0; (draw < draws.Count); draw++) {
            m_meshIdentities[draw] = draws[draw].Identity;
        }
        Array.Clear(array: m_meshIdentities, index: draws.Count, length: (m_meshIdentityCount - Math.Min(val1: m_meshIdentityCount, val2: draws.Count)));
        m_meshIdentityCount = draws.Count;

        var bytes = (((ulong)Math.Max(val1: 1, val2: draws.Count)) * SdfMeshRegion.DrawBytes);

        if (bytes <= m_previousMeshTransforms.SizeBytes) {
            return;
        }
        // Twice the need, as the mesh region grows, so draws landing over several frames replace it once; the old buffer is
        // disposed once the next upload completes, after every view that bound it.
        var replacement = m_gpu.BufferFactory.CreateDeviceLocal(name: NameOf(part: "previous-mesh-transforms"), sizeBytes: (2UL * bytes), usage: GpuBufferUsage.Storage);

        Retire(replaced: m_previousMeshTransforms);
        m_previousMeshTransforms = replacement;
        m_bindingRevision++;
        m_meshHistoryWritten = false;
        m_seedMeshHistory = true;
    }
}
