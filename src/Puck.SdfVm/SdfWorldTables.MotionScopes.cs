using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
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
// What a view's passes read of a slot besides a draw, which no box bounds, moves every scope (CollectUnboundedSlots):
// a positional light's, a bounded medium's and a paint's slot. A view's pose overlay rows move that view's scopes.
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
    // The slots something no box bounds reads, and each view's pose overlay rows.
    private readonly GpuUploadRuns m_unboundedSlots = new(capacity: 64);
    private readonly List<(int First, int Count, int View)> m_overlayRows = [];
    // The skinned rows: the slots each may read, and the bound it held when last judged.
    private readonly Dictionary<int, SkinRow> m_skinRows = [];
    private readonly List<int> m_skinOrder = [];
    // The transforms the frame being staged poses its skins from.
    private IReadOnlyList<DynamicTransform> m_motionTransforms = [];

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

    // Starts a staging: lets go of the scopes nothing asked about, and opens the rest.
    private void BeginMotionStaging(SdfFrame frame) {
        m_motionStaging++;
        m_motionTransforms = frame.DynamicTransforms;
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
    // Names the slots something no box bounds reads this frame: a positional light's, a bounded medium's and a paint's,
    // and each view's pose overlay rows.
    private void CollectUnboundedSlots(SdfFrame frame) {
        m_unboundedSlots.Clear();
        m_overlayRows.Clear();
        for (var index = 0; (index < frame.Lights.Count); index++) {
            var light = frame.Lights[index];

            if (SdfLight.IsPositional(kind: light.Kind) && (light.DynamicSlot >= 0)) { m_unboundedSlots.Add(length: 1, start: light.DynamicSlot); }
        }
        for (var index = 0; (index < frame.Volumes.Count); index++) {
            if (frame.Volumes[index].DynamicSlot >= 0) { m_unboundedSlots.Add(length: 1, start: frame.Volumes[index].DynamicSlot); }
        }
        if (m_liveProgram is { } program) {
            foreach (var slot in program.PaintSlots) { m_unboundedSlots.Add(length: 1, start: slot); }
        }
        for (var view = 0; (view < frame.Views.Count); view++) {
            if (frame.Views[view].PoseOverlay is { SlotCount: > 0 } overlay) { m_overlayRows.Add(item: (overlay.OverlaySlot, overlay.SlotCount, view)); }
        }
    }

    private void Meet(SdfMotionScope scope) {
        scope.Meet(staging: m_motionStaging);
        m_motionOpen--;
        SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.ScopesMet);
    }
    // A change no box bounds: every scope shows it. A view narrows it to that view's scopes.
    private void MeetEveryScope(int? view = null) {
        if (m_motionOpen == 0) { return; }
        SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.ScopeUnbounded);
        foreach (var scope in m_motionScopes) {
            if ((scope.MetStaging != m_motionStaging) && ((view is not { } only) || (scope.View == only))) { Meet(scope: scope); }
        }
    }
    // Judges one change of a draw against every open scope: the draw as it stood before and as it stands after, each a
    // box and whether it is glass, and null for a draw no box bounds. A side that draws nothing is not live.
    private void NoteMotion(bool wasLive, in SdfMeshBox? before, bool wasGlass, bool isLive, in SdfMeshBox? after, bool isGlass) {
        if ((m_motionOpen == 0) || !(wasLive || isLive)) { return; }
        var tests = 0;

        foreach (var scope in m_motionScopes) {
            if (scope.MetStaging == m_motionStaging) { continue; }
            tests++;
            if ((wasLive && scope.Shows(box: in before, isGlass: wasGlass)) || (isLive && scope.Shows(box: in after, isGlass: isGlass))) { Meet(scope: scope); }
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
        NoteMotion(wasLive: (wasLive && !wasSkinned), before: in before, wasGlass: wasGlass, isLive: (isLive && !isSkinned), after: in after, isGlass: isGlass);
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
            NoteMotion(wasLive: wasLive, before: in before, wasGlass: wasGlass, isLive: false, after: null, isGlass: false);
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
        NoteMotion(wasLive: skin.Live, before: in skin.Box, wasGlass: skin.Glass, isLive: false, after: null, isGlass: false);

        var last = m_skinOrder[^1];

        m_skinOrder[skin.Order] = last;
        m_skinOrder.RemoveAt(index: (m_skinOrder.Count - 1));
        if (last != row) { CollectionsMarshal.GetValueRefOrNullRef(dictionary: m_skinRows, key: last).Order = skin.Order; }
    }
    // Judges each skinned row this staging staged, or whose slots a changed or reseated transform may be, by the bound
    // it held and the bound it holds under the frame's transforms.
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

            NoteMotion(wasLive: skin.Live, before: in skin.Box, wasGlass: skin.Glass, isLive: live, after: in box, isGlass: draw.Glass);
            (skin.Live, skin.Box, skin.Glass) = (live, box, draw.Glass);
        }
    }
    // Judges each changed or reseated transform slot: a slot something no box bounds reads moves every scope, a view's
    // overlay row that view's, and any other slot the draws that shade in its frame, unless this staging staged them.
    private void NoteMovedSlots() {
        NoteMovedSlots(slots: m_changedTransforms);
        NoteMovedSlots(slots: m_reseatedTransforms);
    }
    private void NoteMovedSlots(GpuUploadRuns slots) {
        for (var run = 0; (run < slots.Count); run++) {
            var end = Math.Min(val1: m_packedTransformCount, val2: (slots.Start(index: run) + slots.Length(index: run)));

            for (var slot = slots.Start(index: run); (slot < end); slot++) {
                if (m_motionOpen == 0) { return; }
                if (m_unboundedSlots.Contains(value: slot)) {
                    MeetEveryScope();
                    return;
                }
                foreach (var (first, count, view) in m_overlayRows) {
                    if ((slot >= first) && ((slot - first) < count)) { MeetEveryScope(view: view); }
                }
                for (var row = m_rowsByFrameSlot.First(key: slot); (row >= 0); row = m_rowsByFrameSlot.Next(row: row)) {
                    if (m_rowStagings[row] == m_motionStaging) { continue; }
                    var live = ReadStagedSelection(row: row, record: m_meshDrawWords.AsSpan(length: SdfMeshRecord.DrawWords, start: (row * SdfMeshRecord.DrawWords)), box: out var box, glass: out var glass);

                    NoteMotion(wasLive: live, before: in box, wasGlass: glass, isLive: false, after: null, isGlass: false);
                }
            }
        }
    }
    // Ends a staging of the draw records: the skinned rows and the moved slots, judged after every record is staged.
    // A previous table seeded whole moves every scope, since every draw's motion then reads as none.
    private void EndMotionStaging() {
        if (m_seedDynamicHistory || m_seedMeshHistory) { MeetEveryScope(); }
        // A skinned row's held bound follows its pose on every staging, open scope or none, or a later staging would
        // judge the row by a bound it left stagings ago.
        NoteSkinnedRows();
        NoteMovedSlots();
        SdfCadenceWork.ProcessWork.Add(kind: SdfCadenceWork.ScopeChanges, amount: m_motionChanges);
        SdfCadenceWork.ProcessWork.Add(kind: SdfCadenceWork.ScopeTests, amount: m_motionTests);
        (m_motionChanges, m_motionTests) = (0L, 0L);
    }
    // The upload advanced the previous tables: each scope's poses follow.
    private void UploadedMotion() {
        foreach (var scope in m_motionScopes) { scope.Uploaded(); }
    }
}
