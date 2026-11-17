using System.Numerics;
using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>The observed incarnation of one staged surface: its nonzero generation and whether its previous
/// matrix must start at the current matrix. The same generation validates GPU visibility and detail history.</summary>
/// <param name="Generation">The observed cell generation, never zero and never reused by that cell.</param>
/// <param name="Seed">Whether this observation starts a new surface incarnation at its index.</param>
public readonly record struct SdfMeshObservation(uint Generation, bool Seed);
/// <summary>One staged draw table's history owner. Indices are transient addresses; changing an occupant, mesh or
/// surface frame starts a new nonzero generation. Retirement releases keys and meshes but preserves generations,
/// so a later grow/reuse cannot adopt the GPU history still stored for a former occupant.</summary>
public sealed class SdfMeshHistory {
    // Declaring a kind is what lists it in ProcessWork: the static properties below initialize in textual order after
    // this field, and ProcessWork reads the closed list last.
    private static readonly WorkKindRegistry Registry = new();

    /// <summary>Counts staging observations; unchanged incremental records need none.</summary>
    public static WorkKind Observed { get; } = Registry.Declare(name: "sdf.instances.observed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts draw rows scanned only when transform cuts or unknown provenance may invalidate mesh history.</summary>
    public static WorkKind ReseatDrawsScanned { get; } = Registry.Declare(name: "sdf.instances.reseat.draws-scanned", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts root/member slot dependency tests while resolving transform cuts.</summary>
    public static WorkKind ReseatDependenciesChecked { get; } = Registry.Declare(name: "sdf.instances.reseat.dependencies-checked", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts vertex-member bytes submitted to membership searches for a potentially cut skin member.</summary>
    public static WorkKind ReseatMemberBytesChecked { get; } = Registry.Declare(name: "sdf.instances.reseat.member-bytes-checked", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Counts transform windows with unknown/full provenance, conservatively cutting dependent draws.</summary>
    public static WorkKind ReseatFallbacks { get; } = Registry.Declare(name: "sdf.instances.reseat.fallbacks", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts staged observations forced to a new generation by a root or used member transform cut.</summary>
    public static WorkKind Reseated { get; } = Registry.Declare(name: "sdf.instances.reseated", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts observations starting a new surface incarnation.</summary>
    public static WorkKind Seeded { get; } = Registry.Declare(name: "sdf.instances.seeded", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts live cells retired by absence or a vanished suffix.</summary>
    public static WorkKind Retired { get; } = Registry.Declare(name: "sdf.instances.retired", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts continuous observations whose object-to-world matrix changed.</summary>
    public static WorkKind MatricesChanged { get; } = Registry.Declare(name: "sdf.instances.matrices.changed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts matrix bytes changed by continuous motion, excluding newly seeded history.</summary>
    public static WorkKind MatrixBytesChanged { get; } = Registry.Declare(name: "sdf.instances.matrix-bytes.changed", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Counts mesh matrix rows copied on the GPU, including bounded run coarsening and seeds.</summary>
    public static WorkKind HistoryRowsCopied { get; } = Registry.Declare(name: "sdf.instances.history.rows-copied", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts mesh-history payload bytes read and written on the GPU, once per copied byte, excluding requests.</summary>
    public static WorkKind HistoryBytesCopied { get; } = Registry.Declare(name: "sdf.instances.history.bytes-copied", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Counts copy-request bytes written by the host; no matrix payload is uploaded for history.</summary>
    public static WorkKind HistoryRequestBytes { get; } = Registry.Declare(name: "sdf.instances.history.request-bytes", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Counts batched mesh-history copy dispatches.</summary>
    public static WorkKind HistoryDispatches { get; } = Registry.Declare(name: "sdf.instances.history.dispatches", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts records packed by full or incremental staging, independently of GPU transfer coalescing.</summary>
    public static WorkKind RecordsPacked { get; } = Registry.Declare(name: "sdf.instances.records.packed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts bytes assigned by full or dependency-aware record packing, not device transfer bytes.</summary>
    public static WorkKind RecordBytesPacked { get; } = Registry.Declare(name: "sdf.instances.record-bytes.packed", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Counts normal-matrix derivations, skipped when the exact upper 3 by 3 matrix stands unchanged.</summary>
    public static WorkKind NormalMatricesComputed { get; } = Registry.Declare(name: "sdf.instances.normal-matrices.computed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts mesh selection-box derivations, skipped when their geometry, matrix and carry stand unchanged.</summary>
    public static WorkKind SelectionBoxesComputed { get; } = Registry.Declare(name: "sdf.instances.selection-boxes.computed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts incremental draw comparisons, including records whose packed bytes stand unchanged.</summary>
    public static WorkKind RecordsCompared { get; } = Registry.Declare(name: "sdf.instances.records.compared", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts incremental draws whose pose, host metadata or history cut required packing.</summary>
    public static WorkKind RecordsChanged { get; } = Registry.Declare(name: "sdf.instances.records.changed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts incremental packed words that differ, before upload-run merging and coarsening.</summary>
    public static WorkKind PackedWordsChanged { get; } = Registry.Declare(name: "sdf.instances.packed-words.changed", unit: "words", workClass: WorkClass.Pacing);
    /// <summary>Counts changes to the geometry/copy correspondence used to dress a frame.</summary>
    public static WorkKind DressTopologyBuilds { get; } = Registry.Declare(name: "sdf.instances.dress.topology-builds", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts retained dressed records rewritten by pose or metadata changes.</summary>
    public static WorkKind DressedRecordsUpdated { get; } = Registry.Declare(name: "sdf.instances.dress.records-updated", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts retained bindings available to a dress call's pose update, including unchanged bindings.</summary>
    public static WorkKind DressPoseCandidates { get; } = Registry.Declare(name: "sdf.instances.dress.pose-candidates", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts retained bindings actually visited to update their draw pose.</summary>
    public static WorkKind DressBindingsVisited { get; } = Registry.Declare(name: "sdf.instances.dress.bindings-visited", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts object-to-world matrix constructions while dressing, before reuse by surfaces or chunks.</summary>
    public static WorkKind DressMatricesComputed { get; } = Registry.Declare(name: "sdf.instances.dress.matrices-computed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts draw rows walked to refresh the staging owner's mesh claims.</summary>
    public static WorkKind StagingClaimRecords { get; } = Registry.Declare(name: "sdf.instances.staging.claim-records", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts draw rows submitted to the mesh placement planner.</summary>
    public static WorkKind StagingPlanRecords { get; } = Registry.Declare(name: "sdf.instances.staging.plan-records", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts changed-run rows checked for mesh replacement before reusing the placement plan.</summary>
    public static WorkKind StagingMeshChecks { get; } = Registry.Declare(name: "sdf.instances.staging.mesh-checks", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts the rows a dress changed the owner of, whole: every row of a bake group whose mesh, piece count or
    /// copies changed, of the host's list when its count changed, and each host row or instance mesh that names another
    /// mesh. It is what a table whose owners keep their rows would write for the frame.</summary>
    public static WorkKind RowsOwnedChanged { get; } = Registry.Declare(name: "sdf.instances.rows.owned.changed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts live rows yielded by the table's whole-table enumeration.</summary>
    public static WorkKind RowsWalkedWhole { get; } = Registry.Declare(name: "sdf.instances.rows.walked-whole", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts the passes that resolve hits from a target recorded under another revision of the draw records
    /// than the tables now hold (<see cref="SdfWorldTables.MeshRevision"/>): a surface pass reading a mesh target, a
    /// views or resolve pass reading visibility records, a glass shading pass reading a glass target. Zero on a correct
    /// run; the hit door bounds such a read, but cannot tell an occupant that is in bounds from the one the target
    /// named.</summary>
    public static WorkKind TargetsStale { get; } = Registry.Declare(name: "sdf.instances.targets.stale", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the most rows one staging submitted to the mesh placement planner since the peaks were last
    /// cleared.</summary>
    public static WorkKind PeakRowsPlanned { get; } = Registry.DeclarePeak(name: "sdf.instances.peak.rows.planned", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the most rows one staging's draw list held since the peaks were last cleared.</summary>
    public static WorkKind PeakRowsExtent { get; } = Registry.DeclarePeak(name: "sdf.instances.peak.rows.extent", unit: "count", workClass: WorkClass.Pacing);

    private static readonly WorkKind[] RowsPackedKinds = DeclareCauses(peak: false, prefix: "sdf.instances.rows.packed");
    private static readonly WorkKind[] FramesKinds = DeclareCauses(peak: false, prefix: "sdf.instances.frames");
    private static readonly WorkKind[] FramesOverChunkKinds = DeclareCauses(peak: false, prefix: "sdf.instances.frames.over-chunk");
    private static readonly WorkKind[] PeakRowsPackedKinds = DeclareCauses(peak: true, prefix: "sdf.instances.peak.rows.packed");
    private static readonly WorkKind[] TopologyBuildKinds = DeclareTopologyReasons();

    /// <summary>Gets process-lifetime observations from every staged table. These counts never enter simulation state.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.instances", kinds: Registry.Kinds);

    /// <summary>The records one staging packs past which it counts as over one chunk: one background upload chunk of
    /// records (<see cref="SdfGpuWork.UploadChunkBytes"/> over <see cref="SdfMeshRegion.DrawBytes"/>), more record bytes
    /// on the frame thread and in the frame's own upload than the background upload allots one slot in one frame.</summary>
    public const int ChunkRecords = (SdfGpuWork.UploadChunkBytes / SdfMeshRegion.DrawBytes);

    /// <summary>Returns the kind counting the records stagings packed under a cause, on either staging path. The seven
    /// causes' totals sum to <see cref="RecordsPacked"/>.</summary>
    /// <param name="cause">The cause.</param>
    /// <returns>The <c>sdf.instances.rows.packed.&lt;cause&gt;</c> kind.</returns>
    public static WorkKind RowsPackedOf(SdfMeshStagingCause cause) => RowsPackedKinds[((int)cause)];
    /// <summary>Returns the kind counting the stagings that packed a record under a cause.</summary>
    /// <param name="cause">The cause.</param>
    /// <returns>The <c>sdf.instances.frames.&lt;cause&gt;</c> kind.</returns>
    public static WorkKind FramesOf(SdfMeshStagingCause cause) => FramesKinds[((int)cause)];
    /// <summary>Returns the kind counting the stagings whose records under a cause number more than
    /// <see cref="ChunkRecords"/>.</summary>
    /// <param name="cause">The cause.</param>
    /// <returns>The <c>sdf.instances.frames.over-chunk.&lt;cause&gt;</c> kind.</returns>
    public static WorkKind FramesOverChunkOf(SdfMeshStagingCause cause) => FramesOverChunkKinds[((int)cause)];
    /// <summary>Returns the peak kind holding the most records one staging packed under a cause since the peaks were
    /// last cleared.</summary>
    /// <param name="cause">The cause.</param>
    /// <returns>The <c>sdf.instances.peak.rows.packed.&lt;cause&gt;</c> kind.</returns>
    public static WorkKind PeakRowsPackedOf(SdfMeshStagingCause cause) => PeakRowsPackedKinds[((int)cause)];
    /// <summary>Returns the kind counting the dresser's topology rebuilds for which a reason was the first that fired.
    /// The six reasons' totals sum to <see cref="DressTopologyBuilds"/>.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The <c>sdf.instances.dress.topology-builds.&lt;reason&gt;</c> kind.</returns>
    public static WorkKind TopologyBuildsOf(SdfMeshTopologyReason reason) => TopologyBuildKinds[((int)reason)];

    /// <summary>Publishes the records one staging packed under each cause: each cause's total, its staging count, its
    /// over-chunk count and its peak. <paramref name="records"/> is indexed by <see cref="SdfMeshStagingCause"/>, and its
    /// sum is what the same staging adds to <see cref="RecordsPacked"/>.</summary>
    /// <param name="records">The staging's packed records by cause.</param>
    internal static void PublishCauses(ReadOnlySpan<long> records) {
        for (var cause = 0; (cause < records.Length); cause++) {
            var packed = records[cause];

            if (packed == 0L) { continue; }
            ProcessWork.Add(kind: RowsPackedKinds[cause], amount: packed);
            ProcessWork.Count(kind: FramesKinds[cause]);
            if (packed > ChunkRecords) { ProcessWork.Count(kind: FramesOverChunkKinds[cause]); }
            ProcessWork.Raise(kind: PeakRowsPackedKinds[cause], value: packed);
        }
    }

    private static WorkKind[] DeclareCauses(string prefix, bool peak) {
        var kinds = new WorkKind[SdfMeshStagingCauses.Count];

        for (var cause = 0; (cause < kinds.Length); cause++) {
            var name = $"{prefix}.{SdfMeshStagingCauses.NameOf(cause: ((SdfMeshStagingCause)cause))}";

            kinds[cause] = (peak
                ? Registry.DeclarePeak(name: name, unit: "count", workClass: WorkClass.Pacing)
                : Registry.Declare(name: name, unit: "count", workClass: WorkClass.Pacing));
        }

        return kinds;
    }
    private static WorkKind[] DeclareTopologyReasons() {
        string[] names = ["retry", "free-share", "locked", "forced"];
        var kinds = new WorkKind[names.Length];

        for (var reason = 0; (reason < kinds.Length); reason++) {
            kinds[reason] = Registry.Declare(name: $"sdf.instances.dress.topology-builds.{names[reason]}", unit: "count", workClass: WorkClass.Pacing);
        }

        return kinds;
    }

    private struct Cell {
        public ulong Occupancy;
        public object? Table;
        public object? Key;
        public SdfMesh? Mesh;
        public uint Generation;
        public int? FrameSlot;
        public bool Dynamic;
        public bool Glass;
        public bool UsesRootMaterialFrame;
        public Vector4 Clip;
        public Quaternion CarryRotation;
        public Vector3 CarryTranslation;
        public float CarryScale;
    }

    private Cell[] m_cells = [];
    private int m_activeCount;

    /// <summary>Gets the prefix that may contain live observed cells. Capacity and retired generation storage do not shrink.</summary>
    public int ActiveCount => m_activeCount;

    /// <summary>Retires one row without reusing its generation lifetime. Its next observation seeds even if the
    /// returning draw has the same identity.</summary>
    /// <param name="index">The nonnegative row.</param>
    public void Retire(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if ((index >= m_cells.Length) || (m_cells[index].Key is null)) { return; }
        m_cells[index].Key = null;
        m_cells[index].Mesh = null;
        m_cells[index].Table = null;
        ProcessWork.Count(kind: Retired);
    }
    /// <summary>Observes a surface at its staged index. Ordinary matrix motion retains its generation; changes to
    /// stable motion key, mesh, slot interpretation, clipping/carry or semantic flags, and explicit transform cuts,
    /// seed both CPU and GPU history.</summary>
    /// <param name="index">The staged draw index.</param>
    /// <param name="draw">The surface, with a stable motion key or a stable picking identity.</param>
    /// <param name="reseat">Whether a root or used skin-member transform starts a new history interpretation, even
    /// when the surface's packed pose and metadata are unchanged.</param>
    /// <param name="occupancy">The row allocation's incarnation, zero for an undressed host list.</param>
    /// <param name="table">The producer table owning the occupancy sequence.</param>
    /// <returns>The generation to write into that exact record and its matrix seed decision.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the draw representation.</exception>
    /// <exception cref="ArgumentException">The draw names no identity.</exception>
    /// <exception cref="InvalidOperationException">This cell exhausted its uint generation lifetime.</exception>
    public SdfMeshObservation Observe(int index, in SdfMeshDraw draw, bool reseat = false, ulong occupancy = 0, object? table = null) {
        if ((index < 0) || (index >= SdfMeshRegion.MaxDraws)) { throw new ArgumentOutOfRangeException(paramName: nameof(index)); }
        var key = ((draw.MotionIdentity ?? draw.Identity) ?? throw new ArgumentException(message: "A mesh draw needs a stable surface identity.", paramName: nameof(draw)));

        if (index >= m_cells.Length) { Array.Resize(array: ref m_cells, newSize: Math.Max(val1: (index + 1), val2: Math.Max(val1: 4, val2: (2 * m_cells.Length)))); }
        ref var cell = ref m_cells[index];
        var seed = (reseat || !ReferenceEquals(objA: cell.Table, objB: table) || (cell.Occupancy != occupancy) || !Equals(objA: cell.Key, objB: key) || !ReferenceEquals(objA: cell.Mesh, objB: draw.Mesh) ||
            (cell.FrameSlot != draw.FrameSlot) || (cell.UsesRootMaterialFrame != draw.UsesRootMaterialFrame) || (cell.Dynamic != draw.IsDynamic) || (cell.Glass != draw.Glass) ||
            (cell.Clip != draw.ClipPlane) || (cell.CarryRotation != draw.CarryRotation) ||
            (cell.CarryTranslation != draw.CarryTranslation) || (cell.CarryScale != draw.CarryScale));

        if (seed) {
            if (cell.Generation == uint.MaxValue) { throw new InvalidOperationException(message: "A staged mesh cell exhausted its generation lifetime; its GPU history cannot safely reuse a token."); }
            cell.Generation++;
            cell.Occupancy = occupancy;
            cell.Table = table;
            cell.Key = key;
            cell.Mesh = draw.Mesh;
            cell.FrameSlot = draw.FrameSlot;
            cell.UsesRootMaterialFrame = draw.UsesRootMaterialFrame;
            cell.Dynamic = draw.IsDynamic;
            cell.Glass = draw.Glass;
            cell.Clip = draw.ClipPlane;
            cell.CarryRotation = draw.CarryRotation;
            cell.CarryTranslation = draw.CarryTranslation;
            cell.CarryScale = draw.CarryScale;
            ProcessWork.Count(kind: Seeded);
            if (reseat) { ProcessWork.Count(kind: Reseated); }
        }
        m_activeCount = Math.Max(val1: m_activeCount, val2: (index + 1));
        ProcessWork.Count(kind: Observed);
        return new(Generation: cell.Generation, Seed: seed);
    }
    /// <summary>Retires a vanished suffix without reusing its generations. Passing zero retires an empty draw list.</summary>
    /// <param name="count">The staged table's current extent, including absent rows with no observed cell.</param>
    public void RetireAfter(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        count = Math.Min(val1: count, val2: m_activeCount);
        var retired = 0;

        for (var index = count; (index < m_activeCount); index++) {
            ref var cell = ref m_cells[index];

            if (cell.Key is null) { continue; }
            cell.Key = null;
            cell.Mesh = null;
            cell.Table = null;
            retired++;
        }
        m_activeCount = count;
        ProcessWork.Add(kind: Retired, amount: retired);
    }
}
