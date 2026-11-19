using System.Runtime.InteropServices;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private readonly record struct RowLedger(SdfMeshDraw Draw, ulong Occupancy, bool Absent, uint Generation);

    private RowLedger[] m_rowLedger = [];
    private int m_ledgerExtent;
    private bool m_ledgerEnabled;
    private object? m_ledgerRoster;
    private long m_skipDrawThrough;

    private readonly HashSet<int> m_namedRowCuts = [];

    /// <summary>Gets the first row comparison failure since these tables were created, including the row's identity.</summary>
    public string? RowCheckFailure { get; private set; }

    /// <summary>Forces admission through the arena's one placing function, then restages its rows absent until landed.</summary>
    /// <param name="bytes">The admission cap in bytes; null restores the device limit.</param>
    public void ForceMeshLimit(ulong? bytes) { m_meshArena.ForceLimit(bytes: bytes); m_meshSource = null; m_meshStaged = false; }
    /// <summary>Forces the next staging to relocate the arena through its ordinary packing path.</summary>
    public void ForceMeshRelocation() { m_meshArena.ForceRelocation(); m_meshSource = null; m_meshStaged = false; }
    /// <summary>Leaves the table consumer's cursor uncommitted longer than its bounded journal history.</summary>
    public void ForceSkipTables() => m_skipDrawThrough = checked(((m_drawCursor.Serial + SdfMovedTransforms.History) + 2));

    private void CheckMeshRows(SdfFrame frame) {
        if (!SdfRowChecks.Enabled) {
            if (m_ledgerEnabled) { m_rowLedger = []; m_ledgerExtent = 0; m_ledgerEnabled = false; }
            return;
        }
        var draws = frame.DrawRows;
        var extent = draws.Count;

        if (!m_ledgerEnabled) { m_rowLedger = []; m_ledgerExtent = 0; m_ledgerEnabled = true; }
        if (m_rowLedger.Length < extent) { Array.Resize(array: ref m_rowLedger, newSize: extent); }
        var words = MemoryMarshal.Cast<byte, uint>(span: m_meshRegion.ContentsAt(length: (extent * SdfMeshRegion.DrawBytes), offset: 0));
        Span<uint> expected = stackalloc uint[SdfMeshRecord.DrawWords];

        for (var row = 0; (row < extent); row++) {
            var draw = draws[row];
            var absent = ((draw.Mesh is null) || !MeshStands(mesh: draw.Mesh));
            var record = words.Slice(length: SdfMeshRecord.DrawWords, start: (row * SdfMeshRecord.DrawWords));
            var generation = record[(SdfMeshRecord.SelectionWord + 11)];

            if (absent) { SdfMeshRegion.WriteAbsent(record: expected); } else {
                // The packer admits only live tokens; a corrupt zero is still diagnosed by the ledger below.
                SdfMeshRegion.WriteDraw(draw, m_meshArena.Placements[draw.Mesh!], Math.Max(val1: 1u, val2: generation), expected);
                expected[(SdfMeshRecord.SelectionWord + 11)] = generation;
            }
            SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.Packed);
            if (!record.SequenceEqual(other: expected)) { RowMismatch(SdfRowChecks.PackMismatch, row, draw.Identity, "pack"); }

            var last = m_rowLedger[row];
            var known = (row < m_ledgerExtent);
            // Deliberately independent of Observe's identity predicate and of its cells. Ordinary matrix, material,
            // bounds and displacement changes are not incarnation changes.
            var changed = (known && (last.Absent || !ReferenceEquals(objA: m_ledgerRoster, objB: draws.Rows) || (last.Occupancy != draws.Occupancy(row: row)) ||
                !Equals(objA: (last.Draw.MotionIdentity ?? last.Draw.Identity), objB: (draw.MotionIdentity ?? draw.Identity)) ||
                !ReferenceEquals(objA: last.Draw.Mesh, objB: draw.Mesh) || (last.Draw.FrameSlot != draw.FrameSlot) ||
                (last.Draw.IsDynamic != draw.IsDynamic) || (last.Draw.Glass != draw.Glass) ||
                (last.Draw.UsesRootMaterialFrame != draw.UsesRootMaterialFrame) || (last.Draw.ClipPlane != draw.ClipPlane) ||
                (last.Draw.CarryRotation != draw.CarryRotation) || (last.Draw.CarryTranslation != draw.CarryTranslation) ||
                (last.Draw.CarryScale != draw.CarryScale) || m_namedRowCuts.Contains(item: row)));

            SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.Ledger);
            if (absent ? (generation != 0) : ((generation == 0) || (known && ((generation != last.Generation) != changed)))) {
                RowMismatch(SdfRowChecks.LedgerMismatch, row, draw.Identity, "generation ledger");
            }
            if (!absent && (known ? (generation != last.Generation) : m_seededMeshDraws.Contains(value: row))) {
                SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.Seeds);
                if (!m_seedMeshHistory && !m_seededMeshDraws.Contains(value: row)) { RowMismatch(SdfRowChecks.SeedMismatch, row, draw.Identity, "seed"); }
            }
            m_rowLedger[row] = new(Absent: absent, Draw: draw, Occupancy: draws.Occupancy(row: row), Generation: generation);
        }
        // A removed suffix is absent to a subsequent grow, even when the returning identity is unchanged.
        for (var row = extent; (row < m_ledgerExtent); row++) { m_rowLedger[row] = new(Absent: true, Draw: default, Generation: 0, Occupancy: 0); }
        m_ledgerExtent = extent;
        m_ledgerRoster = draws.Rows;
    }
    private void RowMismatch(WorkKind kind, int row, object? owner, string comparison) {
        SdfRowChecks.ProcessWork.Count(kind: kind);
        RowCheckFailure ??= $"{comparison}: row {row}, identity {owner}";
        Console.Error.WriteLine(value: $"[sdf.rows.check] {RowCheckFailure}");
    }

    // The service owns the readback slot until completion. A replaced region's buffer retires only after a later
    // upload fence, which is ordered after this job on the same queue.
    private sealed class MeshRowsReadback(SdfWorldTables tables) : ISdfGpuWorkClient {
        private sealed class Job { public byte[]? Expected; public object?[]? Owners; }

        private bool m_pending;
        private long m_checkedRevision = -1;

        public GpuQueueCategory Category => GpuQueueCategory.Upload;
        public GpuMemoryBytes OwnedBytes => default;

        public bool TryTake(SdfWorldTables current, int uploadBytes, double allowanceSeconds,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? job, out double estimatedSeconds) {
            estimatedSeconds = 0;
            job = null;
            if (!SdfRowChecks.Enabled || m_pending || (current.m_meshDrawCount == 0) || current.m_meshRegion.OwesCopy ||
                current.m_meshRegion.OwesCarry || (current.m_landing.Count > 0) || (m_checkedRevision == current.m_meshRevision)) { return false; }
            m_pending = true;
            m_checkedRevision = current.m_meshRevision;
            job = new Job();
            return true;
        }
        public void Record(object job, in SdfGpuWorkRecording recording) {
            var check = ((Job)job);

            if (tables.m_meshRegion.OwesCopy || (tables.m_landing.Count > 0)) { return; }
            var bytes = (((int)tables.m_meshDrawCount) * SdfMeshRegion.DrawBytes);

            check.Expected = tables.m_meshRegion.ContentsAt(length: bytes, offset: 0).ToArray();
            check.Owners = new object?[tables.m_stagedCount];
            for (var row = 0; (row < check.Owners.Length); row++) { check.Owners[row] = tables.m_stagedDraws[row].Identity; }
            recording.CopyToReadback(tables.m_meshRegion.Buffer(slot: tables.m_currentSlot), ((ulong)bytes));
            recording.Recorder.TransitionBuffer(commandBufferHandle: recording.CommandBuffer,
                bufferHandle: tables.m_meshRegion.Buffer(slot: tables.m_currentSlot).BufferHandle,
                sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: RegionReaders);
        }
        public void Complete(object job, SdfGpuWorkReadback readback) {
            try {
                if (((Job)job).Expected is not { } expected) { return; }
                var actual = new byte[expected.Length];

                readback.Read(destination: actual);
                for (var row = 0; (row < (expected.Length / SdfMeshRegion.DrawBytes)); row++) {
                    SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.Device);
                    var start = (row * SdfMeshRegion.DrawBytes);

                    if (!actual.AsSpan(length: SdfMeshRegion.DrawBytes, start: start).SequenceEqual(other: expected.AsSpan(length: SdfMeshRegion.DrawBytes, start: start))) {
                        tables.RowMismatch(SdfRowChecks.DeviceMismatch, row, ((Job)job).Owners![row], "device");
                    }
                }
            } finally { m_pending = false; readback.Release(); }
        }
        public void Abandon(object job) { m_pending = false; m_checkedRevision = -1; }
        public void Dispose() { }
    }
}
