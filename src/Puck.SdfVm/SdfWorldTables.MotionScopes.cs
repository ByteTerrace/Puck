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
//   - its record (matrix, mesh, material, clip, carry) : every staged record passes NoteStagedRow, which judges the box
//                          the record held before the write and the box it holds after (SdfMeshRegion.ReadSelection),
//                          the boxes every pass selects the draw by.
//   - its frame slot's row (the material frame and the lanes) : NoteMovedSlots judges each changed slot's draws
//                          (m_rowsByFrameSlot) by the box they hold, unless the staging already judged the record.
//   - its members' slots (a skinned mesh) : no record changes as a member moves, so NoteSkinnedRows judges each
//                          skinned row a changed slot may pose by a host bound (SdfMeshBox.TryOfSkin), kept beside the
//                          row from one staging to the next.
// What shading reads of a slot besides a draw (CollectReaders), each judged by the box it reaches under the pose the
// slot held before the staging wrote it and the pose it holds after (NoteMovedReaders): a positional light's range, a
// bounded medium's box and a paint's box. A cascade holds depths alone and reads none of them. A view's pose overlay
// rows move that view's scopes.
// A kernel that reads sdfDynamicTransforms for anything else must be named here, or a pass stands on a stale image.
// The field interpreter reads slots too, but only the bake runs it, which no pass signature covers.
//
// Scopes are judged in the order changes are staged, each until its first change of the staging, so a staging costs a
// scope nothing more once something it shows has moved.
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
    // The staging that last staged each row's record.
    private long[] m_rowStagings = [];
    // The boxed draws by the slot whose row they shade in.
    private readonly GpuRowIndex<int> m_rowsByFrameSlot = new();
    // What shading reads of a slot besides a draw's, each with the box it reaches in its slot's frame: the live
    // program's paints, in slot order, and the frame's lights and media. A slot's mark says which of the two read it
    // (PaintMark, FrameMark), and a staging keeps the pose each marked slot held before it wrote the slot.
    private const byte PaintMark = 1;
    private const byte FrameMark = 2;
    private SlotReader[] m_paintReaders = [];
    private SdfProgram? m_paintProgram;
    private readonly List<SlotReader> m_frameReaders = [];
    private byte[] m_slotReaderMarks = [];
    private readonly List<(int Slot, Vector3 Position, Quaternion Orientation)> m_movedReaderSlots = [];
    // Each view's pose overlay rows.
    private readonly List<(int First, int Count, int View)> m_overlayRows = [];
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
            _ = SdfMeshRegion.ReadSelection(record: m_meshDrawWords.AsSpan(length: SdfMeshRecord.DrawWords, start: (row * SdfMeshRecord.DrawWords)), box: out box, glass: out _);
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
        foreach (var reader in m_frameReaders) { Mark(slot: reader.Slot, mark: FrameMark, set: true); }
        if (!ReferenceEquals(objA: m_paintProgram, objB: m_liveProgram)) {
            foreach (var reader in m_paintReaders) { Mark(slot: reader.Slot, mark: PaintMark, set: false); }
            m_paintProgram = m_liveProgram;
            m_paintReaders = PaintReadersOf(program: m_liveProgram);
            foreach (var reader in m_paintReaders) { Mark(slot: reader.Slot, mark: PaintMark, set: true); }
        }
        for (var view = 0; (view < frame.Views.Count); view++) {
            if (frame.Views[view].PoseOverlay is { SlotCount: > 0 } overlay) { m_overlayRows.Add(item: (overlay.OverlaySlot, overlay.SlotCount, view)); }
        }

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
    // Judges one change against every open scope: what changed as it stood before and as it stands after, each a box
    // and whether it is glass, and null for one no box bounds. A side that draws nothing is not live. Shading names a
    // change only shading reads, which no cascade's scope shows.
    private void NoteMotion(SdfMotionCause cause, int row, bool wasLive, in SdfMeshBox? before, bool wasGlass, bool isLive, in SdfMeshBox? after, bool isGlass, bool shading = false) {
        if ((m_motionOpen == 0) || !(wasLive || isLive)) { return; }
        var tests = 0;

        foreach (var scope in m_motionScopes) {
            if ((scope.MetStaging == m_motionStaging) || (shading && (scope.Reader == SdfMotionReader.Caster))) { continue; }
            tests++;
            if ((wasLive && scope.Shows(box: in before, isGlass: wasGlass)) || (isLive && scope.Shows(box: in after, isGlass: isGlass))) { Meet(cause: cause, detail: row, scope: scope); }
        }
        m_motionChanges++;
        m_motionTests += tests;
    }

    // What a row's record says its draw was before a staging writes it, read before the write (NoteStagedRow).
    private bool ReadStagedSelection(int row, ReadOnlySpan<uint> record, out SdfMeshBox? box, out bool glass) {
        box = null;
        glass = false;

        return ((row < m_stagedCount) && SdfMeshRegion.ReadSelection(record: record, box: out box, glass: out glass));
    }
    // One row's record was staged: judges the draw it held against the draw it holds, and keeps the row's place in the
    // slot index and the skinned rows. A skinned side is left to NoteSkinnedRows, which holds its bound.
    private void NoteStagedRow(int row, bool wasLive, in SdfMeshBox? before, bool wasGlass, in SdfMeshDraw draw, ReadOnlySpan<uint> record) {
        var isLive = SdfMeshRegion.ReadSelection(record: record, box: out var after, glass: out var isGlass);
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
    // it held and the bound it holds under the frame's transforms. The held bound follows the pose on every staging,
    // open scope or none, or a later staging would judge the row by a bound it left stagings ago.
    private void NoteSkinnedRows() {
        foreach (var row in m_skinOrder) {
            ref var skin = ref CollectionsMarshal.GetValueRefOrNullRef(dictionary: m_skinRows, key: row);

            if ((m_rowStagings[row] != m_motionStaging) &&
                !m_changedTransforms.Overlaps(length: skin.SlotCount, start: skin.FirstSlot) &&
                !m_reseatedTransforms.Overlaps(length: skin.SlotCount, start: skin.FirstSlot)) {
                continue;
            }

            ref readonly var draw = ref m_stagedDraws[row];
            var live = !m_absentRows.Contains(item: row);
            SdfMeshBox? box = (SdfMeshBox.TryOfSkin(box: out var bound, draw: in draw, transforms: m_motionTransforms) ? bound : null);

            NoteMotion(cause: SdfMotionCause.Skin, row: row, wasLive: skin.Live, before: in skin.Box, wasGlass: skin.Glass, isLive: live, after: in box, isGlass: draw.Glass);
            (skin.Live, skin.Box, skin.Glass) = (live, box, draw.Glass);
        }
    }
    // Judges each reader of a slot the staging wrote, by the box it reached under the pose the slot held before the write
    // and the box it reaches now. A slot written for the first time held no pose, and its table is seeded whole, which
    // moves every scope.
    private void NoteMovedReaders() {
        foreach (var (slot, position, orientation) in m_movedReaderSlots) {
            if ((m_motionOpen == 0) || (slot >= m_motionTransforms.Count)) { break; }
            var now = SdfMeshGeometryBuilder.PoseOf(transform: m_motionTransforms[slot]);

            if ((m_slotReaderMarks[slot] & PaintMark) != 0) {
                for (var index = PartitionPoint.Of(span: m_paintReaders.AsSpan(), state: slot, isBefore: static (at, reader) => (reader.Slot < at)); ((index < m_paintReaders.Length) && (m_paintReaders[index].Slot == slot)); index++) {
                    Judge(reader: in m_paintReaders[index]);
                }
            }
            if ((m_slotReaderMarks[slot] & FrameMark) != 0) {
                foreach (var reader in m_frameReaders) {
                    if (reader.Slot == slot) { Judge(reader: in reader); }
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
    // Judges each changed or reseated transform slot: a view's overlay row moves that view's scopes, and any slot the
    // draws that shade in its frame, unless this staging staged them.
    private void NoteMovedSlots(GpuUploadRuns slots) {
        for (var run = 0; (run < slots.Count); run++) {
            var end = Math.Min(val1: m_packedTransformCount, val2: (slots.Start(index: run) + slots.Length(index: run)));

            for (var slot = slots.Start(index: run); (slot < end); slot++) {
                if (m_motionOpen == 0) { return; }
                foreach (var (first, count, view) in m_overlayRows) {
                    if ((slot >= first) && ((slot - first) < count)) { MeetEveryScope(cause: SdfMotionCause.Overlay, detail: slot, view: view); }
                }
                for (var row = m_rowsByFrameSlot.First(key: slot); (row >= 0); row = m_rowsByFrameSlot.Next(row: row)) {
                    if (m_rowStagings[row] == m_motionStaging) { continue; }
                    var live = ReadStagedSelection(row: row, record: m_meshDrawWords.AsSpan(length: SdfMeshRecord.DrawWords, start: (row * SdfMeshRecord.DrawWords)), box: out var box, glass: out var glass);

                    NoteMotion(cause: SdfMotionCause.Slot, row: row, wasLive: live, before: in box, wasGlass: glass, isLive: false, after: null, isGlass: false);
                }
            }
        }
    }
    // Ends a staging of the draw records: the skinned rows and the moved slots, judged after every record is staged.
    // A previous table seeded whole moves every scope, since every draw's motion then reads as none.
    private void EndMotionStaging() {
        if (m_seedDynamicHistory || m_seedMeshHistory) { MeetEveryScope(cause: SdfMotionCause.Seed, detail: 0); }
        NoteSkinnedRows();
        NoteMovedReaders();
        NoteMovedSlots(slots: m_changedTransforms);
        NoteMovedSlots(slots: m_reseatedTransforms);
        SdfCadenceWork.ProcessWork.Add(kind: SdfCadenceWork.ScopeChanges, amount: m_motionChanges);
        SdfCadenceWork.ProcessWork.Add(kind: SdfCadenceWork.ScopeTests, amount: m_motionTests);
        (m_motionChanges, m_motionTests) = (0L, 0L);
    }
    // The upload advanced the previous tables: each scope's poses follow.
    private void UploadedMotion() {
        foreach (var scope in m_motionScopes) { scope.Uploaded(); }
    }
}
