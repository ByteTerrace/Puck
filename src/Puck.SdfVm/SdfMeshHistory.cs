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
    /// <summary>Counts staging observations; unchanged incremental records need none.</summary>
    public static WorkKind Observed { get; } = new(name: "sdf.instances.observed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts observations starting a new surface incarnation.</summary>
    public static WorkKind Seeded { get; } = new(name: "sdf.instances.seeded", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts live cells retired when their staged list shrinks.</summary>
    public static WorkKind Retired { get; } = new(name: "sdf.instances.retired", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts continuous observations whose object-to-world matrix changed.</summary>
    public static WorkKind MatricesChanged { get; } = new(name: "sdf.instances.matrices.changed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts matrix bytes changed by continuous motion, excluding newly seeded history.</summary>
    public static WorkKind MatrixBytesChanged { get; } = new(name: "sdf.instances.matrix-bytes.changed", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Counts mesh matrix rows copied on the GPU, including bounded run coarsening and seeds.</summary>
    public static WorkKind HistoryRowsCopied { get; } = new(name: "sdf.instances.history.rows-copied", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts mesh-history payload bytes read and written on the GPU, once per copied byte, excluding requests.</summary>
    public static WorkKind HistoryBytesCopied { get; } = new(name: "sdf.instances.history.bytes-copied", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Counts copy-request bytes written by the host; no matrix payload is uploaded for history.</summary>
    public static WorkKind HistoryRequestBytes { get; } = new(name: "sdf.instances.history.request-bytes", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Counts batched mesh-history copy dispatches.</summary>
    public static WorkKind HistoryDispatches { get; } = new(name: "sdf.instances.history.dispatches", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts records packed by full or incremental staging, independently of GPU transfer coalescing.</summary>
    public static WorkKind RecordsPacked { get; } = new(name: "sdf.instances.records.packed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts bytes assigned by full or dependency-aware record packing, not device transfer bytes.</summary>
    public static WorkKind RecordBytesPacked { get; } = new(name: "sdf.instances.record-bytes.packed", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Counts normal-matrix derivations, skipped when the exact upper 3 by 3 matrix stands unchanged.</summary>
    public static WorkKind NormalMatricesComputed { get; } = new(name: "sdf.instances.normal-matrices.computed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts mesh selection-box derivations, skipped when their geometry, matrix and carry stand unchanged.</summary>
    public static WorkKind SelectionBoxesComputed { get; } = new(name: "sdf.instances.selection-boxes.computed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts incremental draw comparisons, including records whose packed bytes stand unchanged.</summary>
    public static WorkKind RecordsCompared { get; } = new(name: "sdf.instances.records.compared", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts incremental draws whose pose or host metadata changed and required packing.</summary>
    public static WorkKind RecordsChanged { get; } = new(name: "sdf.instances.records.changed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts incremental packed words that differ, before upload-run merging and coarsening.</summary>
    public static WorkKind PackedWordsChanged { get; } = new(name: "sdf.instances.packed-words.changed", unit: "words", workClass: WorkClass.Pacing);
    /// <summary>Counts changes to the geometry/copy correspondence used to dress a frame.</summary>
    public static WorkKind DressTopologyBuilds { get; } = new(name: "sdf.instances.dress.topology-builds", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts retained dressed records rewritten by pose or metadata changes.</summary>
    public static WorkKind DressedRecordsUpdated { get; } = new(name: "sdf.instances.dress.records-updated", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets process-lifetime observations from every staged table. These counts never enter simulation state.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.instances", kinds: [Observed, Seeded, Retired, MatricesChanged, MatrixBytesChanged, HistoryRowsCopied, HistoryBytesCopied, HistoryRequestBytes, HistoryDispatches, RecordsPacked, RecordBytesPacked, NormalMatricesComputed, SelectionBoxesComputed, RecordsCompared, RecordsChanged, PackedWordsChanged, DressTopologyBuilds, DressedRecordsUpdated]);

    private struct Cell {
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

    /// <summary>Observes a surface at its staged index. Ordinary matrix motion retains its generation; changes to
    /// stable motion key, mesh, slot interpretation, clipping/carry or semantic flags seed both CPU and GPU history.</summary>
    /// <param name="index">The staged draw index.</param>
    /// <param name="draw">The surface, with a stable motion key or a stable picking identity.</param>
    /// <returns>The generation to write into that exact record and its matrix seed decision.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the draw representation.</exception>
    /// <exception cref="ArgumentException">The draw names no identity.</exception>
    /// <exception cref="InvalidOperationException">This cell exhausted its uint generation lifetime.</exception>
    public SdfMeshObservation Observe(int index, in SdfMeshDraw draw) {
        if ((index < 0) || (index >= SdfMeshRegion.MaxDraws)) { throw new ArgumentOutOfRangeException(paramName: nameof(index)); }
        var key = ((draw.MotionIdentity ?? draw.Identity) ?? throw new ArgumentException(message: "A mesh draw needs a stable surface identity.", paramName: nameof(draw)));

        if (index >= m_cells.Length) { Array.Resize(array: ref m_cells, newSize: Math.Max(val1: (index + 1), val2: Math.Max(val1: 4, val2: (2 * m_cells.Length)))); }
        ref var cell = ref m_cells[index];
        var seed = (!Equals(objA: cell.Key, objB: key) || !ReferenceEquals(objA: cell.Mesh, objB: draw.Mesh) ||
            (cell.FrameSlot != draw.FrameSlot) || (cell.UsesRootMaterialFrame != draw.UsesRootMaterialFrame) || (cell.Dynamic != draw.IsDynamic) || (cell.Glass != draw.Glass) ||
            (cell.Clip != draw.ClipPlane) || (cell.CarryRotation != draw.CarryRotation) ||
            (cell.CarryTranslation != draw.CarryTranslation) || (cell.CarryScale != draw.CarryScale));

        if (seed) {
            if (cell.Generation == uint.MaxValue) { throw new InvalidOperationException(message: "A staged mesh cell exhausted its generation lifetime; its GPU history cannot safely reuse a token."); }
            cell.Generation++;
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
        }
        m_activeCount = Math.Max(val1: m_activeCount, val2: (index + 1));
        ProcessWork.Count(kind: Observed);
        return new(Generation: cell.Generation, Seed: seed);
    }
    /// <summary>Retires a vanished suffix without reusing its generations. Passing zero retires an empty draw list.</summary>
    /// <param name="count">The staged list's current length, at most the observed prefix.</param>
    public void RetireAfter(int count) {
        if ((count < 0) || (count > m_activeCount)) { throw new ArgumentOutOfRangeException(paramName: nameof(count)); }
        var retired = 0;

        for (var index = count; (index < m_activeCount); index++) {
            ref var cell = ref m_cells[index];

            if (cell.Key is null) { continue; }
            cell.Key = null;
            cell.Mesh = null;
            retired++;
        }
        m_activeCount = count;
        ProcessWork.Add(kind: Retired, amount: retired);
    }
}
