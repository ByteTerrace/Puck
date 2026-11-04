using System.Runtime.CompilerServices;
using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    /// <summary>Gets the kind counting point evaluations: one per
    /// <see cref="TryDistance(FixedPosition, out FixedQ4816, out int, out int)"/> walk, through either overload.</summary>
    public static WorkKind PointEvaluations { get; } = new(name: "sdf.point.evaluations", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the instructions those point evaluations visited, after culling.</summary>
    public static WorkKind PointInstructions { get; } = new(name: "sdf.point.instructions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets cheap instance-bound ranks computed before the point instruction walk, including unprimed walks.</summary>
    public static WorkKind PointPrepassBounds { get; } = new(name: "sdf.point.prepass-bounds", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets reach-scratch arrays created by the point allocation door, including zero-length requests.</summary>
    public static WorkKind PointScratchArraysCreated { get; } = new(name: "sdf.point.reach-scratch-arrays-created", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets cumulative point reach-scratch element payload created; excludes array headers and is not live or physical memory.</summary>
    public static WorkKind PointScratchPayloadCreated { get; } = new(name: "sdf.point.reach-scratch-payload-created", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets nonempty temporary ShapeReach arrays created during certificate analysis.</summary>
    public static WorkKind CertificateArraysCreated { get; } = new(name: "sdf.query.certificate-arrays-created", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets cumulative nullable ShapeReach element payload created; excludes other analysis scratch and headers, and is not live memory.</summary>
    public static WorkKind CertificatePayloadCreated { get; } = new(name: "sdf.query.certificate-payload-created", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets completed evaluator cull-table constructions, not active evaluators.</summary>
    public static WorkKind CullTableBuilds { get; } = new(name: "sdf.query.cull-table-builds", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets nonempty retained instance/member/segment/index array payloads created by those constructions.</summary>
    public static WorkKind CullArraysCreated { get; } = new(name: "sdf.query.cull-arrays-created", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets their cumulative element payload, excluding headers, list capacity, boundary arrays and rotation metadata; not live/physical memory.</summary>
    public static WorkKind CullPayloadCreated { get; } = new(name: "sdf.query.cull-payload-created", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets exact power results reused from the shared bounded memo. Cache warmth and races affect this count.</summary>
    public static WorkKind PowerMemoHits { get; } = new(name: "sdf.point.power.memo-hits", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets admitted power lookups that derived their answer with FixedQ4816.Pow.</summary>
    public static WorkKind PowerMemoMisses { get; } = new(name: "sdf.point.power.memo-misses", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets direct powers outside the memo's domain or result representation.</summary>
    public static WorkKind PowerMemoFallbacks { get; } = new(name: "sdf.point.power.fallbacks", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets zero and unity bases sent directly to FixedQ4816.Pow without a memo lookup.</summary>
    public static WorkKind PowerMemoBypasses { get; } = new(name: "sdf.point.power.bypasses", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets shared memo payload allocations, including fresh code generations.</summary>
    public static WorkKind PowerMemoAllocations { get; } = new(name: "sdf.point.power.memo-allocations", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets cumulative memo payload bytes allocated; this is work, not currently retained or physical bytes.</summary>
    public static WorkKind PowerMemoAllocatedBytes { get; } = new(name: "sdf.point.power.memo-allocated", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the process-wide point-evaluation and logical culling-construction work of every evaluator, the interval walk's peer
    /// (<see cref="SdfBoundsCache.ProcessWork"/>). A monotonic total; a reader takes a window by reading twice.</summary>
    public static WorkCounterSet PointWork { get; } = new(name: "sdf.point", kinds: [PointEvaluations, PointInstructions, PointPrepassBounds,
        PointScratchArraysCreated, PointScratchPayloadCreated, CertificateArraysCreated, CertificatePayloadCreated,
        CullTableBuilds, CullArraysCreated, CullPayloadCreated,
        PowerMemoHits, PowerMemoMisses, PowerMemoFallbacks, PowerMemoBypasses, PowerMemoAllocations, PowerMemoAllocatedBytes]);

    // Count only this named array payload, once at its creation door. Empty shared ToArray results have no new payload.
    private static void CountArrayPayload<T>(T[] rows, WorkCounterSet work, WorkKind arrays, WorkKind bytes) {
        if (rows.Length == 0) { return; }
        work.Count(kind: arrays);
        work.Add(kind: bytes, amount: (rows.LongLength * Unsafe.SizeOf<T>()));
    }

    private static FixedQ4816[] CreateReachScratch(int length, WorkCounterSet work, WorkKind arrays, WorkKind bytes) {
        var rows = new FixedQ4816[length];
        work.Count(kind: arrays);
        work.Add(kind: bytes, amount: (rows.LongLength * Unsafe.SizeOf<FixedQ4816>()));
        return rows;
    }


}
