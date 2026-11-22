using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The motion scopes: which of a staging's changes each pass can show (SdfMotionScope). A pass signs with its scope's
// revision, so the tables judge every change against every scope they hold, here and nowhere else.
//
// What a view's passes read of a draw, and where a change to each is judged:
//   - its record (pose, mesh, material, clip, carry) : every staged record passes NoteStagedRow, which judges the box
//                          the record held before the write and the box it holds after (RowSelection, JudgeSelection),
//                          the boxes every pass selects the draw by.
//   - its frame slot's row (the pose of a draw riding the slot, its material frame and its lanes) : NoteMovedSlots
//                          judges each changed slot's draws (m_rowsByFrameSlot) from the box each was last judged by
//                          (m_slotBoxes) to the box it holds under the slot's new row, unless the staging already judged
//                          the record. The kernels derive the same box from the same row (sdfSelectionBox).
//   - its members' slots (a skinned mesh) : no record changes as a member moves, so NoteSkinnedRows judges each
//                          skinned row a changed slot may pose by a host bound (SdfMeshBox.TryOfSkin, under the shared
//                          rows and each view's pose overlay), kept beside the row from one staging to the next and
//                          written into the record's selection words, so every pass selects the draw by it.
// What shading reads of a slot besides a draw (CollectReaders), each judged by the box it reaches under the pose the
// slot held before the staging wrote it and the pose it holds after (NoteMovedReaders): a positional light's range, a
// bounded medium's box and a paint's box. A cascade holds depths alone and reads none of them. A view's pose overlay
// rows move that view's scopes.
// What shading reads of the screens' emission (SdfScreenEmission), whose reduction is published after the staging: each
// screen a publication changed is judged by the box its light reaches (NoteEmission, EmissionBoxOf), which no pass that
// shades past the screen's range can show (sdfLightWindow), and moves the lighting revision of each scope that shades
// (SdfMotionScope.Lighting), not its revision: no draw or pose changed.
// A kernel that reads sdfDynamicTransforms for anything else must be named here, or a pass stands on a stale image.
// The field interpreter reads slots too, but only the bake runs it, which no pass signature covers.
//
// Scopes are judged in the order changes are staged, each until its first change of the staging, so a staging costs a
// scope nothing more once something it shows has moved. Changes are judged in batches of consecutive changes
// (FlushMotion): a scope whose cull keeps none of a batch's bound refuses the whole batch in one test. A creation's rows
// are staged together, so a patch of grass out of a view's sight costs that view's scopes a test or two.
public sealed partial class SdfWorldTables {
    /// <summary>The stagings a scope stays held without being asked about (<see cref="ObserveMotion(SdfMotionScope, in SdfMeshCull, int)"/>):
    /// about a second of frames, so a view rendered at a reduced rate keeps its scope and a parked one lets it go.</summary>
    public const int MotionUnobservedStagings = 64;

    private readonly List<SdfMotionScope> m_motionScopes = [];
    // One more for every staging of the draw records, and the held scopes no change of this staging has met yet.
    private long m_motionStaging;
    private int m_motionOpen;
    // The staging's judged changes and the scopes they were judged against, published once at its end.
    private long m_motionChanges;
    private long m_motionTests;
    // The changes noted and not yet judged (NoteMotion, FlushMotion).
    private const int MotionBatch = 64;
    private readonly PendingMotion[] m_pendingMotion = new PendingMotion[MotionBatch];
    private int m_pendingMotionCount;
    // The staging that last staged each row's record.
    private long[] m_rowStagings = [];
    // The boxed draws by the slot whose row they ride and shade in.
    private readonly GpuRowIndex<int> m_rowsByFrameSlot = new();
    // The world box each row whose record holds its box in its slot's frame (SdfMeshRecord.SlotBoxFlag) was last judged
    // by: the box under its slot's row at that staging (JudgeSelection), what the next judgement starts from.
    private SdfMeshBox?[] m_slotBoxes = [];
    // What shading reads of a slot besides a draw's, each with the box it reaches in its slot's frame: the live
    // program's paints, in slot order, and the frame's lights and media. A slot's mark says which of the two read it
    // (PaintMark, FrameMark), and a staging keeps the pose each marked slot held before it wrote the slot.
    private const byte PaintMark = 1;
    private const byte FrameMark = 2;
    private SlotReader[] m_paintReaders = [];
    private SdfProgram? m_paintProgram;
    private readonly List<SlotReader> m_frameReaders = [];
    // The same readers joined to one per slot (ReachBySlot), in slot order, which a moved slot is judged by: every reader
    // of a slot rides it rigidly, so the box holding them all in its frame moves as they do.
    private SlotReader[] m_paintReach = [];
    private readonly List<SlotReader> m_frameReach = [];
    private byte[] m_slotReaderMarks = [];
    private readonly List<(int Slot, Vector3 Position, Quaternion Orientation)> m_movedReaderSlots = [];
    // Each view's pose overlay: the rows it reads from, how many, the view, and the first slot it replaces; whether the
    // list differs from the previous staging's; and the table a skinned row's bound under one overlay reads.
    private readonly List<(int First, int Count, int View, int Stamp)> m_overlayRows = [];
    private readonly List<(int First, int Count, int View, int Stamp)> m_previousOverlayRows = [];
    private bool m_overlaysChanged;
    private readonly OverlaidTransforms m_overlaid = new();
    // The skinned rows: the slots each may read, and the bound it held when last judged.
    private readonly Dictionary<int, SkinRow> m_skinRows = [];
    private readonly List<int> m_skinOrder = [];
    // The transforms the frame being staged poses its skins from.
    private IReadOnlyList<DynamicTransform> m_motionTransforms = [];

    // One reader of a slot: its box in the slot's frame, as a turn of that frame, then a center and half extents along
    // the turned axes. A half extent that is not finite reaches everywhere.
    private readonly record struct SlotReader(int Slot, string Reader, Vector3 Center, Quaternion Turn, Vector3 Half) {
        public SdfMeshBox? BoxAt(Vector3 position, Quaternion orientation) => (float.IsFinite(f: Half.LengthSquared())
            ? SdfMeshBox.Turned(center: Center, half: Half, offset: position, turn: (orientation * Turn))
            : null);
    }
    // A change noted for judgement: what changed as it stood before and as it stands after (NoteMotion).
    private readonly record struct PendingMotion(SdfMotionCause Cause, int Row, bool WasLive, SdfMeshBox? Before, bool WasGlass, bool IsLive, SdfMeshBox? After, bool IsGlass, bool Shading);
    private struct SkinRow {
        public int Order;
        public int FirstSlot;
        public int SlotCount;
        public bool Live;
        public bool Glass;
        public SdfMeshBox? Box;
    }

    /// <summary>Gets the stagings of the draw records so far, which a consumer that caches a scope's revision for a frame
    /// keys the cache by: a revision read before a staging says nothing of what that staging changed.</summary>
    public long MotionStaging => m_motionStaging;

    /// <summary>Returns a scope's revision for the frame the tables hold, taking the cull its pass draws under now. The
    /// tables hold the scope from this call on, judging every staged change against it.</summary>
    /// <param name="scope">The scope, which only these tables may hold.</param>
    /// <param name="cull">The cull the scope's raster pass draws under: the one its latest recording drew with while
    /// the frame's camera and that recording's extent stand.</param>
    /// <param name="view">The index, in the frame's views, of the view the pass belongs to.</param>
    /// <returns>The revision (<see cref="SdfMotionScope.Revision"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    public long ObserveMotion(SdfMotionScope scope, in SdfMeshCull cull, int view) {
        ObserveMotion(scope: scope);
        if (scope.Follow(cull: in cull, view: view)) {
            SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.ScopeCulls);
        }

        return scope.Revision;
    }
    /// <summary>Keeps a scope held without changing its cull, for a consumer whose view stands whole this frame.</summary>
    /// <param name="scope">The scope, which only these tables may hold.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    public void ObserveMotion(SdfMotionScope scope) {
        ArgumentNullException.ThrowIfNull(argument: scope);
        if (!scope.Attached) {
            scope.Attached = true;
            scope.Restart(staging: m_motionStaging);
            m_motionScopes.Add(item: scope);
        }
        scope.Observed = m_motionStaging;
    }
    /// <summary>Describes the scopes the tables hold for <c>world.residency</c>: each scope's view and reader, how many
    /// stagings ago a change last moved it and how often changes have, and why its revision last moved, naming the
    /// draw, the slot or the slot's reader.</summary>
    /// <returns>The description.</returns>
    public string DescribeMotion() {
        var text = new StringBuilder();

        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"motion-scopes={m_motionScopes.Count} stagings={m_motionStaging} skinned-rows={m_skinRows.Count} slot-readers={(m_paintReaders.Length + m_frameReaders.Count)} overlays={m_overlayRows.Count}");
        foreach (var scope in m_motionScopes) {
            var detail = scope.CauseDetail;
            var named = (scope.Cause switch {
                SdfMotionCause.Draw or SdfMotionCause.Skin or SdfMotionCause.Slot when ((detail >= 0) && (detail < m_stagedCount)) => DescribeRow(row: detail),
                SdfMotionCause.Reader => $"slot{detail}({string.Join(separator: '+', values: m_paintReaders.Concat(second: m_frameReaders).Where(predicate: reader => (reader.Slot == detail)).Select(selector: static reader => reader.Reader).Distinct())})",
                SdfMotionCause.Overlay => $"slot{detail}",
                SdfMotionCause.Emission => $"screen{detail}",
                _ => string.Empty,
            });

            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $" scope.view{scope.View}.{scope.Reader}.{scope.Cull}=met{scope.Meets}/last{(m_motionStaging - scope.MetStaging)}ago/{scope.Cause}{((named.Length > 0) ? ":" : string.Empty)}{named}");
        }

        return text.ToString();
    }

    // A row for the report: its frame slot, and the box it is judged by, a skinned row's held bound or the record's.
    private string DescribeRow(int row) {
        ref readonly var draw = ref m_stagedDraws[row];
        SdfMeshBox? box = null;

        if (m_skinRows.TryGetValue(key: row, value: out var skin)) {
            box = skin.Box;
        } else {
            _ = RowSelection(row: row, box: out box, glass: out _);
        }

        return string.Create(provider: CultureInfo.InvariantCulture, handler:
            $"row{row}(slot{(draw.FrameSlot ?? -1)}{((draw.Mesh is { Members.IsEmpty: false }) ? ",skinned" : string.Empty)},{((box is { } bound) ? $"at{bound.Center.X:F1},{bound.Center.Y:F1},{bound.Center.Z:F1}~{bound.AxisHalf.Length():F1}m" : "unbounded")})");
    }
    // Starts a staging: lets go of the scopes nothing asked about, and opens the rest.
    private void BeginMotionStaging(SdfFrame frame) {
        m_motionStaging++;
        m_motionTransforms = frame.DynamicTransforms;
        m_movedReaderSlots.Clear();
        for (var index = (m_motionScopes.Count - 1); (index >= 0); index--) {
            var scope = m_motionScopes[index];

            if ((m_motionStaging - scope.Observed) > MotionUnobservedStagings) {
                scope.Attached = false;
                m_motionScopes[index] = m_motionScopes[^1];
                m_motionScopes.RemoveAt(index: (m_motionScopes.Count - 1));
            }
        }
        m_motionOpen = m_motionScopes.Count;
        SdfCadenceWork.ProcessWork.Add(kind: SdfCadenceWork.Scopes, amount: m_motionOpen);
    }
    // Names what shading reads of a slot this frame besides a draw: each positional light and bounded medium that rides
    // one, and the live program's paints that do, each with the box it reaches in the slot's frame; and each view's pose
    // overlay rows. Called before the frame's transforms are written, so the write keeps each marked slot's earlier pose.
    private void CollectReaders(SdfFrame frame) {
        foreach (var reader in m_frameReaders) { Mark(slot: reader.Slot, mark: FrameMark, set: false); }
        m_frameReaders.Clear();
        m_previousOverlayRows.Clear();
        m_previousOverlayRows.AddRange(collection: m_overlayRows);
        m_overlayRows.Clear();
        for (var index = 0; (index < frame.Lights.Count); index++) {
            var light = frame.Lights[index];

            if (SdfLight.IsPositional(kind: light.Kind) && (light.DynamicSlot >= 0)) {
                m_frameReaders.Add(item: new(Center: light.Direction, Half: new(value: SdfLightSelection.ReachOf(light: in light)), Reader: "light", Slot: light.DynamicSlot, Turn: Quaternion.Identity));
            }
        }
        for (var index = 0; (index < frame.Volumes.Count); index++) {
            var volume = frame.Volumes[index];

            if (volume.DynamicSlot >= 0) {
                var turn = Quaternion.Normalize(value: volume.Rotation);

                m_frameReaders.Add(item: new(Center: Vector3.Transform(value: volume.Position, rotation: Quaternion.Conjugate(value: turn)), Half: volume.HalfExtent, Reader: "medium", Slot: volume.DynamicSlot, Turn: turn));
            }
        }
        // In slot order, as the paints are, so a moved slot finds its readers by search (NoteMovedReaders).
        m_frameReaders.Sort(comparison: static (left, right) => left.Slot.CompareTo(value: right.Slot));
        foreach (var reader in m_frameReaders) { Mark(slot: reader.Slot, mark: FrameMark, set: true); }
        ReachBySlot(readers: CollectionsMarshal.AsSpan(list: m_frameReaders), reach: m_frameReach);
        if (!ReferenceEquals(objA: m_paintProgram, objB: m_liveProgram)) {
            foreach (var reader in m_paintReaders) { Mark(slot: reader.Slot, mark: PaintMark, set: false); }
            m_paintProgram = m_liveProgram;
            m_paintReaders = PaintReadersOf(program: m_liveProgram);
            foreach (var reader in m_paintReaders) { Mark(slot: reader.Slot, mark: PaintMark, set: true); }

            var reach = new List<SlotReader>();

            ReachBySlot(readers: m_paintReaders, reach: reach);
            m_paintReach = [.. reach];
        }
        for (var view = 0; (view < frame.Views.Count); view++) {
            if (frame.Views[view].PoseOverlay is { SlotCount: > 0 } overlay) { m_overlayRows.Add(item: (overlay.OverlaySlot, overlay.SlotCount, view, overlay.FirstSlot)); }
        }
        m_overlaysChanged = !m_overlayRows.SequenceEqual(second: m_previousOverlayRows);

        void Mark(int slot, byte mark, bool set) {
            if (slot >= m_slotReaderMarks.Length) { Array.Resize(array: ref m_slotReaderMarks, newSize: Math.Max(val1: (slot + 1), val2: (m_slotReaderMarks.Length * 2))); }
            m_slotReaderMarks[slot] = (set ? ((byte)(m_slotReaderMarks[slot] | mark)) : ((byte)(m_slotReaderMarks[slot] & ~mark)));
        }
    }
    // A program's paints that ride a slot, in slot order. A paint covers its shape's bound, in the shape's frame, grown
    // by its stroke (shade-layers.hlsli); the footprint its edge is antialiased over is within the margin every view's
    // cull widens its frustum by.
    private static SlotReader[] PaintReadersOf(SdfProgram? program) {
        if (program is null) { return []; }
        var paints = program.SlotPaints;
        var readers = new SlotReader[paints.Length];

        for (var index = 0; (index < paints.Length); index++) {
            var paint = paints[index];
            var turn = Quaternion.Normalize(value: paint.Rotation);
            var bound = paint.Shape.Bound;

            readers[index] = new(
                Center: (Vector3.Transform(value: paint.Origin, rotation: Quaternion.Conjugate(value: turn)) + (bound.IsUnbounded ? default : bound.Center)),
                Half: (bound.IsUnbounded ? new(value: float.PositiveInfinity) : (bound.HalfExtents + new Vector3(value: paint.Stroke))),
                Reader: "paint",
                Slot: paint.Slot,
                Turn: turn
            );
        }
        Array.Sort(array: readers, comparison: static (left, right) => left.Slot.CompareTo(value: right.Slot));

        return readers;
    }
    // Joins readers held in slot order to one a slot: a slot's lone reader as it is, and several as the axis box in the
    // slot's frame that holds each one's box there, reaching everywhere when any of them does. The joined box holds every
    // box it joins under any pose of the slot, so judging a moved slot by it meets every scope a reader would have met.
    private static void ReachBySlot(ReadOnlySpan<SlotReader> readers, List<SlotReader> reach) {
        reach.Clear();
        for (var start = 0; (start < readers.Length);) {
            var slot = readers[start].Slot;
            var end = (start + 1);

            while ((end < readers.Length) && (readers[end].Slot == slot)) { end++; }
            if ((end - start) == 1) {
                reach.Add(item: readers[start]);
            } else {
                var held = readers[start].BoxAt(orientation: Quaternion.Identity, position: Vector3.Zero);

                for (var index = (start + 1); ((held is not null) && (index < end)); index++) {
                    held = ((readers[index].BoxAt(orientation: Quaternion.Identity, position: Vector3.Zero) is { } box) ? SdfMeshBox.Enclosing(first: held.Value, second: in box) : null);
                }
                reach.Add(item: (held is { } joined)
                    ? new(Center: joined.Center, Half: joined.AxisHalf, Reader: readers[start].Reader, Slot: slot, Turn: Quaternion.Identity)
                    : new(Center: Vector3.Zero, Half: new(value: float.PositiveInfinity), Reader: readers[start].Reader, Slot: slot, Turn: Quaternion.Identity));
            }
            start = end;
        }
    }
    // Reads the pose a slot holds before the staging writes it, for a slot something besides a draw reads; the one
    // writer of a transform row asks before its write and keeps the answer when the write changes the row
    // (NoteReaderSlotWritten).
    private bool TryReadReaderSlot(int slot, out (Vector3 Position, Quaternion Orientation) held) {
        held = default;
        if ((slot >= m_slotReaderMarks.Length) || (m_slotReaderMarks[slot] == 0)) { return false; }
        var row = MemoryMarshal.Cast<byte, float>(span: m_dynamicTransformRegion.ContentsAt(length: (8 * sizeof(float)), offset: (((long)slot) * DynamicTransformByteLength)));

        held = (new(x: row[0], y: row[1], z: row[2]), new(x: row[4], y: row[5], z: row[6], w: row[7]));

        return true;
    }
    private void NoteReaderSlotWritten(int slot, in (Vector3 Position, Quaternion Orientation) held) =>
        m_movedReaderSlots.Add(item: (slot, held.Position, held.Orientation));

    private void Meet(SdfMotionScope scope, SdfMotionCause cause, int detail) {
        scope.Meet(cause: cause, detail: detail, staging: m_motionStaging);
        m_motionOpen--;
        SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.ScopesMet);
    }
    // A change no box bounds: every open scope shows it. A view narrows it to that view's scopes.
    private void MeetEveryScope(SdfMotionCause cause, int detail, int? view = null) {
        if (m_motionOpen == 0) { return; }
        var met = false;

        foreach (var scope in m_motionScopes) {
            if ((scope.MetStaging == m_motionStaging) || ((view is { } only) && (scope.View != only))) { continue; }
            Meet(cause: cause, detail: detail, scope: scope);
            met = true;
        }
        if (met) { SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.ScopeUnbounded); }
    }
    // Notes one change for judgement against every open scope: what changed as it stood before and as it stands after,
    // each a box and whether it is glass, and null for one no box bounds. A side that draws nothing is not live. Shading
    // names a change only shading reads, which no cascade's scope shows. Judged with its batch (FlushMotion).
    private void NoteMotion(SdfMotionCause cause, int row, bool wasLive, in SdfMeshBox? before, bool wasGlass, bool isLive, in SdfMeshBox? after, bool isGlass, bool shading = false) {
        if ((m_motionOpen == 0) || !(wasLive || isLive)) { return; }
        m_pendingMotion[m_pendingMotionCount++] = new(After: after, Before: before, Cause: cause, IsGlass: isGlass, IsLive: isLive, Row: row, Shading: shading, WasGlass: wasGlass, WasLive: wasLive);
        if (m_pendingMotionCount == MotionBatch) { FlushMotion(); }
    }
    // Judges the noted changes against every open scope: first by the box that holds every live side of the batch, which
    // refuses the batch in one test for a scope whose cull keeps none of it, then change by change until the scope meets
    // one. A side no box bounds leaves the batch unbounded.
    private void FlushMotion() {
        var count = m_pendingMotionCount;

        if (count == 0) { return; }
        m_pendingMotionCount = 0;
        var batch = m_pendingMotion.AsSpan(length: count, start: 0);
        SdfMeshBox? bound = null;
        var bounded = true;

        foreach (ref readonly var change in batch) {
            if (change.WasLive) { Hold(side: change.Before); }
            if (change.IsLive) { Hold(side: change.After); }
        }

        var tests = 0;

        foreach (var scope in m_motionScopes) {
            if (scope.MetStaging == m_motionStaging) { continue; }
            if (bounded && (bound is { } held)) {
                tests++;
                if (!scope.Shows(box: held, isGlass: true)) { continue; }
            }
            foreach (ref readonly var change in batch) {
                if (change.Shading && (scope.Reader == SdfMotionReader.Caster)) { continue; }
                tests++;
                if ((change.WasLive && scope.Shows(box: change.Before, isGlass: change.WasGlass)) || (change.IsLive && scope.Shows(box: change.After, isGlass: change.IsGlass))) {
                    Meet(cause: change.Cause, detail: change.Row, scope: scope);
                    break;
                }
            }
        }
        m_motionChanges += count;
        m_motionTests += tests;

        void Hold(SdfMeshBox? side) {
            if (side is not { } box) { bounded = false; }
            else { bound = ((bound is { } held) ? SdfMeshBox.Enclosing(first: in held, second: in box) : box); }
        }
    }

    // What a row's record says its draw was before a staging writes it, read before the write (NoteStagedRow).
    private bool ReadStagedSelection(int row, ReadOnlySpan<uint> record, out SdfMeshBox? box, out bool glass) {
        box = null;
        glass = false;

        return ((row < m_stagedCount) && RowSelection(row: row, record: record, box: out box, glass: out glass));
    }
    // The box the tables judge a staged row by as it last stood: its record's world box, or, for a draw riding its slot
    // (SdfMeshRecord.SlotBoxFlag), the box it was last judged by under its slot's row, which every staging that moves the
    // slot brings up to date (NoteMovedSlots). Every host reader of a staged row's box reads it here.
    private bool RowSelection(int row, out SdfMeshBox? box, out bool glass) =>
        RowSelection(row: row, record: m_meshDrawWords.AsSpan(length: SdfMeshRecord.DrawWords, start: (row * SdfMeshRecord.DrawWords)), box: out box, glass: out glass);
    private bool RowSelection(int row, ReadOnlySpan<uint> record, out SdfMeshBox? box, out bool glass) {
        if (!HoldsSlotBox(record: record)) { return SdfMeshRegion.ReadSelection(record: record, transforms: m_motionTransforms, box: out box, glass: out glass); }
        glass = ((record[(SdfMeshRecord.SelectionWord + 3)] & 2u) != 0u);
        box = ((row < m_slotBoxes.Length) ? m_slotBoxes[row] : null);

        return true;
    }
    // Reads a row's record as it stands after its staging, under the frame's transforms, and holds the box of a draw
    // riding its slot for the next judgement.
    private bool JudgeSelection(int row, ReadOnlySpan<uint> record, out SdfMeshBox? box, out bool glass) {
        var live = SdfMeshRegion.ReadSelection(record: record, transforms: m_motionTransforms, box: out box, glass: out glass);

        if (HoldsSlotBox(record: record)) {
            if (m_slotBoxes.Length <= row) { Array.Resize(array: ref m_slotBoxes, newSize: Math.Max(val1: (row + 1), val2: m_stagedDraws.Length)); }
            m_slotBoxes[row] = box;
        }

        return live;
    }
    private static bool HoldsSlotBox(ReadOnlySpan<uint> record) {
        var flags = record[(SdfMeshRecord.SelectionWord + 3)];

        return (((flags & SdfMeshRecord.SlotBoxFlag) != 0u) && ((flags & (SdfMeshRecord.AbsentFlag | 1u)) == 0u));
    }
    // One row's record was staged: judges the draw it held against the draw it holds, and keeps the row's place in the
    // slot index and the skinned rows. A skinned side is left to NoteSkinnedRows, which holds its bound.
    private void NoteStagedRow(int row, bool wasLive, in SdfMeshBox? before, bool wasGlass, in SdfMeshDraw draw, ReadOnlySpan<uint> record) {
        var isLive = JudgeSelection(row: row, record: record, box: out var after, glass: out var isGlass);
        var wasSkinned = ((m_skinRows.Count > 0) && m_skinRows.ContainsKey(key: row));
        var isSkinned = (draw.Mesh is { Members.IsEmpty: false });

        if (m_rowStagings.Length <= row) { Array.Resize(array: ref m_rowStagings, newSize: m_stagedDraws.Length); }
        m_rowStagings[row] = m_motionStaging;
        NoteMotion(cause: SdfMotionCause.Draw, row: row, wasLive: (wasLive && !wasSkinned), before: in before, wasGlass: wasGlass, isLive: (isLive && !isSkinned), after: in after, isGlass: isGlass);
        if (wasSkinned && !isSkinned) { RetireSkinRow(row: row); }
        if (isSkinned) {
            ref var skin = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_skinRows, key: row, exists: out var held);

            if (!held) {
                skin.Order = m_skinOrder.Count;
                m_skinOrder.Add(item: row);
            }
            (skin.FirstSlot, skin.SlotCount) = SkinSlotsOf(draw: in draw);
        }
        if (!isSkinned && (draw.Mesh is not null) && (draw.FrameSlot is { } slot) && (slot >= 0)) {
            m_rowsByFrameSlot.Set(key: slot, row: row);
        } else {
            m_rowsByFrameSlot.Remove(row: row);
        }
    }
    // A posed row's record was staged (StagePose): its mesh, frame slot and liveness stand, so its place in the slot index
    // and the skinned rows stands too, and only its box is judged; a skinned row's bound is NoteSkinnedRows'.
    private void NotePosedRow(int row, bool wasLive, in SdfMeshBox? before, bool wasGlass, in SdfMeshDraw draw, ReadOnlySpan<uint> record) {
        if (m_rowStagings.Length <= row) { Array.Resize(array: ref m_rowStagings, newSize: m_stagedDraws.Length); }
        m_rowStagings[row] = m_motionStaging;
        if (draw.Mesh is { Members.IsEmpty: false }) { return; }

        var isLive = JudgeSelection(row: row, record: record, box: out var after, glass: out var isGlass);

        NoteMotion(cause: SdfMotionCause.Draw, row: row, wasLive: wasLive, before: in before, wasGlass: wasGlass, isLive: isLive, after: in after, isGlass: isGlass);
    }
    // A row left the staged list: judges the draw it held.
    private void NoteUnnamedRow(int row) {
        var wasLive = ReadStagedSelection(row: row, record: m_meshDrawWords.AsSpan(length: SdfMeshRecord.DrawWords, start: (row * SdfMeshRecord.DrawWords)), box: out var before, glass: out var wasGlass);

        if ((m_skinRows.Count > 0) && m_skinRows.ContainsKey(key: row)) {
            RetireSkinRow(row: row);
        } else {
            NoteMotion(cause: SdfMotionCause.Draw, row: row, wasLive: wasLive, before: in before, wasGlass: wasGlass, isLive: false, after: null, isGlass: false);
        }
        m_rowsByFrameSlot.Remove(row: row);
    }
    // The slots a skinned draw may read: its root's and its members', as one range. A draw with no root slot reads
    // none the tables can name, so every slot counts.
    private static (int First, int Count) SkinSlotsOf(in SdfMeshDraw draw) {
        if ((draw.FrameSlot is not { } root) || (root < 0)) { return (0, int.MaxValue); }
        var (low, high) = (0, 0);

        foreach (ref readonly var member in draw.Mesh.Members.Span) {
            low = Math.Min(val1: low, val2: member.SlotOffset);
            high = Math.Max(val1: high, val2: member.SlotOffset);
        }

        var first = Math.Max(val1: 0L, val2: (((long)root) + low));

        return (((int)first), ((int)Math.Min(val1: int.MaxValue, val2: (((((long)root) + high) - first) + 1L))));
    }
    // A row stopped holding a skinned draw: judges the bound it held, and forgets it.
    private void RetireSkinRow(int row) {
        if (!m_skinRows.Remove(key: row, value: out var skin)) { return; }
        NoteMotion(cause: SdfMotionCause.Skin, row: row, wasLive: skin.Live, before: in skin.Box, wasGlass: skin.Glass, isLive: false, after: null, isGlass: false);

        var last = m_skinOrder[^1];

        m_skinOrder[skin.Order] = last;
        m_skinOrder.RemoveAt(index: (m_skinOrder.Count - 1));
        if (last != row) { CollectionsMarshal.GetValueRefOrNullRef(dictionary: m_skinRows, key: last).Order = skin.Order; }
    }
    // Judges each skinned row this staging staged, or whose slots a changed or reseated transform may be, by the bound
    // it held and the bound it holds under the frame's transforms, and writes the bound into its record. The held bound
    // follows the pose on every staging, open scope or none, or a later staging would judge the row by a bound it left
    // stagings ago and a pass would select the draw by one its vertices left.
    private void NoteSkinnedRows() {
        foreach (var row in m_skinOrder) {
            ref var skin = ref CollectionsMarshal.GetValueRefOrNullRef(dictionary: m_skinRows, key: row);

            if ((m_rowStagings[row] != m_motionStaging) && !m_overlaysChanged && !SkinSlotsMoved(count: skin.SlotCount, first: skin.FirstSlot)) {
                continue;
            }

            ref readonly var draw = ref m_stagedDraws[row];
            var live = !m_absentRows.Contains(item: row);
            var box = SkinBoxOf(count: skin.SlotCount, draw: in draw, first: skin.FirstSlot);

            NoteMotion(cause: SdfMotionCause.Skin, row: row, wasLive: skin.Live, before: in skin.Box, wasGlass: skin.Glass, isLive: live, after: in box, isGlass: draw.Glass);
            (skin.Live, skin.Box, skin.Glass) = (live, box, draw.Glass);
            WriteSkinSelection(box: in box, row: row);
        }
    }
    // Whether a changed or reseated slot is one a skinned row may read: one of its own, or a row of a view's pose overlay
    // that replaces one of its own.
    private bool SkinSlotsMoved(int first, int count) {
        if (Moved(start: first, length: count)) { return true; }
        foreach (var (overlay, overlayCount, _, stamp) in m_overlayRows) {
            if (Covers(stamp: stamp, overlayCount: overlayCount, first: first, count: count) && Moved(start: overlay, length: overlayCount)) { return true; }
        }

        return false;

        bool Moved(int start, int length) => (m_changedTransforms.Overlaps(length: length, start: start) || m_reseatedTransforms.Overlaps(length: length, start: start));
    }
    // Whether an overlay's replaced slots meet a skinned row's slots.
    private static bool Covers(int stamp, int overlayCount, int first, int count) =>
        ((((long)stamp) < (((long)first) + count)) && (((long)first) < (((long)stamp) + overlayCount)));
    // The bound of a skinned draw as every view's passes pose it: under the shared rows, and under each view's pose
    // overlay that replaces any of its slots (sdfViewPoseSlot), held in one box. Null when any of them has no bound.
    private SdfMeshBox? SkinBoxOf(in SdfMeshDraw draw, int first, int count) {
        if (!SdfMeshBox.TryOfSkin(box: out var box, draw: in draw, transforms: m_motionTransforms)) { return null; }
        foreach (var (overlay, overlayCount, _, stamp) in m_overlayRows) {
            if (!Covers(stamp: stamp, overlayCount: overlayCount, first: first, count: count)) { continue; }
            m_overlaid.Overlay(count: overlayCount, rows: overlay, stamp: stamp, table: m_motionTransforms);
            if (!SdfMeshBox.TryOfSkin(box: out var posed, draw: in draw, transforms: m_overlaid)) { return null; }
            box = SdfMeshBox.Enclosing(first: in box, second: in posed);
        }

        return box;
    }
    // Writes a skinned row's bound into its record's selection words (SdfMeshRegion.WriteSelectionBox), owing the region
    // only the words it changes.
    private void WriteSkinSelection(int row, in SdfMeshBox? box) {
        var start = ((row * SdfMeshRecord.DrawWords) + SdfMeshRecord.SelectionWord);
        var selection = m_meshDrawWords.AsSpan(length: SdfMeshRegion.SelectionWords, start: start);
        Span<uint> words = stackalloc uint[SdfMeshRegion.SelectionWords];

        selection.CopyTo(destination: words);
        SdfMeshRegion.WriteSelectionBox(box: in box, selection: words);
        if (words.SequenceEqual(other: selection)) { return; }
        words.CopyTo(destination: selection);
        _ = m_meshRegion.Write(bytes: MemoryMarshal.AsBytes(span: selection), offset: (((long)start) * sizeof(uint)));
    }
    // A transform table whose slots one view's pose overlay replaces with the rows it reads from.
    private sealed class OverlaidTransforms : IReadOnlyList<DynamicTransform> {
        private IReadOnlyList<DynamicTransform> m_table = [];
        private int m_stamp;
        private int m_count;
        private int m_rows;

        public void Overlay(IReadOnlyList<DynamicTransform> table, int stamp, int count, int rows) => (m_table, m_stamp, m_count, m_rows) = (table, stamp, count, rows);

        public DynamicTransform this[int index] => (((index >= m_stamp) && ((index - m_stamp) < m_count) && (((m_rows + index) - m_stamp) < m_table.Count))
            ? m_table[((m_rows + index) - m_stamp)]
            : m_table[index]);
        public int Count => m_table.Count;
        public IEnumerator<DynamicTransform> GetEnumerator() {
            for (var index = 0; (index < Count); index++) { yield return this[index]; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    // Judges the readers of each slot the staging wrote, by the box their reach (ReachBySlot) held under the pose the slot
    // held before the write and the box it holds now. A slot written for the first time held no pose, and its table is
    // seeded whole, which moves every scope.
    private void NoteMovedReaders() {
        foreach (var (slot, position, orientation) in m_movedReaderSlots) {
            if ((m_motionOpen == 0) || (slot >= m_motionTransforms.Count)) { break; }
            var now = SdfMeshGeometryBuilder.PoseOf(transform: m_motionTransforms[slot]);

            if ((m_slotReaderMarks[slot] & PaintMark) != 0) { JudgeAll(readers: m_paintReach); }
            if ((m_slotReaderMarks[slot] & FrameMark) != 0) { JudgeAll(readers: CollectionsMarshal.AsSpan(list: m_frameReach)); }

            // The slot's reach, found by search in a list held in slot order.
            void JudgeAll(ReadOnlySpan<SlotReader> readers) {
                for (var index = PartitionPoint.Of(span: readers, state: slot, isBefore: static (at, reader) => (reader.Slot < at)); ((index < readers.Length) && (readers[index].Slot == slot)); index++) {
                    SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.MotionReaders);
                    Judge(reader: in readers[index]);
                }
            }

            // A reader marks glass as it marks any surface, so it is judged as both kinds of draw.
            void Judge(in SlotReader reader) {
                var before = reader.BoxAt(orientation: orientation, position: position);
                var after = reader.BoxAt(orientation: now.Orientation, position: now.Position);

                NoteMotion(cause: SdfMotionCause.Reader, row: slot, wasLive: true, before: in before, wasGlass: true, isLive: true, after: in after, isGlass: true, shading: true);
            }
        }
    }
    // Judges each changed or reseated transform slot: a view's overlay row moves that view's scopes, and each draw riding
    // the slot (m_rowsByFrameSlot) moves with it, from the box it was last judged by to the box it holds under the slot's
    // new row, unless this staging staged it at that row already. The held box follows the slot on every staging, open
    // scope or none, or a later staging would judge the draw from a place it left stagings ago.
    private void NoteMovedSlots(GpuUploadRuns slots) {
        for (var run = 0; (run < slots.Count); run++) {
            var end = Math.Min(val1: m_packedTransformCount, val2: (slots.Start(index: run) + slots.Length(index: run)));

            for (var slot = slots.Start(index: run); (slot < end); slot++) {
                if (m_motionOpen > 0) {
                    foreach (var (first, count, view, _) in m_overlayRows) {
                        if ((slot >= first) && ((slot - first) < count)) { MeetEveryScope(cause: SdfMotionCause.Overlay, detail: slot, view: view); }
                    }
                }
                for (var row = m_rowsByFrameSlot.First(key: slot); (row >= 0); row = m_rowsByFrameSlot.Next(row: row)) {
                    if ((row >= m_stagedCount) || (m_rowStagings[row] == m_motionStaging)) { continue; }
                    var record = m_meshDrawWords.AsSpan(length: SdfMeshRecord.DrawWords, start: (row * SdfMeshRecord.DrawWords));
                    var live = RowSelection(row: row, record: record, box: out var before, glass: out var glass);

                    _ = JudgeSelection(row: row, record: record, box: out var after, glass: out _);
                    SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.MotionRows);
                    NoteMotion(cause: SdfMotionCause.Slot, row: row, wasLive: live, before: in before, wasGlass: glass, isLive: live, after: in after, isGlass: glass);
                }
            }
        }
    }
    // Ends a staging of the draw records: the skinned rows and the moved slots, judged after every record is staged.
    // A previous table seeded whole moves every scope, since every draw's motion then reads as none.
    private void EndMotionStaging() {
        FlushMotion();
        if (m_seedDynamicHistory || m_seedMeshHistory) { MeetEveryScope(cause: SdfMotionCause.Seed, detail: 0); }
        NoteSkinnedRows();
        FlushMotion();
        NoteMovedReaders();
        FlushMotion();
        NoteMovedSlots(slots: m_changedTransforms);
        FlushMotion();
        NoteMovedSlots(slots: m_reseatedTransforms);
        FlushMotion();
        SdfCadenceWork.ProcessWork.Add(kind: SdfCadenceWork.ScopeChanges, amount: m_motionChanges);
        SdfCadenceWork.ProcessWork.Add(kind: SdfCadenceWork.ScopeTests, amount: m_motionTests);
        (m_motionChanges, m_motionTests) = (0L, 0L);
    }
    // Judges a publication of the screens' emission: each scope that shades is lit by each screen the publication changed
    // (one bit a screen) whose light reaches what the scope's cull keeps. A publication lands after the frame's staging, and
    // every scope it lights moves, however often the staging met it.
    internal void NoteEmission(uint screens) {
        if ((screens == 0u) || (m_motionScopes.Count == 0)) { return; }
        for (var bits = screens; (bits != 0u); bits &= (bits - 1u)) {
            var screen = BitOperations.TrailingZeroCount(value: bits);
            var box = EmissionBoxOf(screen: screen);

            foreach (var scope in m_motionScopes) {
                if ((scope.Reader != SdfMotionReader.Caster) && scope.Shows(box: in box, isGlass: true)) {
                    scope.Light(cause: SdfMotionCause.Emission, detail: screen);
                    SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.ScopesLit);
                }
            }
        }
    }
    // The box a screen's light reaches: the half of its range's sphere in front of its face, whose center the light rides
    // (sdfLightNext's screen half), or null for a range no box bounds (an opening's).
    private SdfMeshBox? EmissionBoxOf(int screen) {
        var reach = m_screenReach[screen];

        if (!float.IsFinite(f: reach)) { return null; }
        var (origin, right, up, _) = ScreenFrameOf(screen: screen);
        var facing = Vector3.Normalize(value: Vector3.Cross(vector1: right, vector2: up));

        return new SdfMeshBox(Center: (origin + (facing * (0.5f * reach))), HalfX: (right * reach), HalfY: (up * reach), HalfZ: (facing * (0.5f * reach)));
    }
    /// <summary>Returns whether a view's pass under a motion scope may show a screen's face, so may sample its image: the
    /// face's rectangle (<see cref="SetScreenSurface"/>) is one the scope's cull keeps.</summary>
    /// <param name="scope">The scope of the pass that shades the face.</param>
    /// <param name="screen">The screen slot.</param>
    /// <returns><see langword="false"/> when no pixel the pass shades can lie on the face.</returns>
    internal bool ScreenFaceShows(SdfMotionScope scope, int screen) {
        var (origin, right, up, extent) = ScreenFrameOf(screen: screen);

        return scope.Shows(box: new SdfMeshBox(Center: origin, HalfX: (right * extent.X), HalfY: (up * extent.Y), HalfZ: Vector3.Zero), isGlass: true);
    }
    // A screen's face as its surface row holds it: its center, its unit axes and its half width and height.
    private (Vector3 Origin, Vector3 Right, Vector3 Up, Vector2 Extent) ScreenFrameOf(int screen) {
        var row = MemoryMarshal.Cast<byte, float>(span: m_screenSurfaceRegion.Contents).Slice(length: (ScreenSurfaceByteLength / sizeof(float)), start: (screen * (ScreenSurfaceByteLength / sizeof(float))));

        return (new(x: row[8], y: row[9], z: row[10]), SafeUnit(value: new(x: row[0], y: row[1], z: row[2])), SafeUnit(value: new(x: row[4], y: row[5], z: row[6])), new(x: row[3], y: row[7]));

        static Vector3 SafeUnit(Vector3 value) => ((value.LengthSquared() > 0f) ? Vector3.Normalize(value: value) : Vector3.Zero);
    }

    // The upload advanced the previous tables: each scope's poses follow.
    private void UploadedMotion() {
        foreach (var scope in m_motionScopes) { scope.Uploaded(); }
    }
}
