using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    /// <summary>Gets the kind counting point evaluations: one per
    /// <see cref="TryDistance(FixedPosition, out FixedQ4816, out int, out int)"/> walk, through either overload.</summary>
    public static WorkKind PointEvaluations { get; } = new(name: "sdf.point.evaluations", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the instructions those point evaluations visited, after culling.</summary>
    public static WorkKind PointInstructions { get; } = new(name: "sdf.point.instructions", unit: "count", workClass: WorkClass.Deterministic);
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
    /// <summary>Gets the process-wide point-evaluation work of every evaluator, the interval walk's peer
    /// (<see cref="SdfBoundsCache.ProcessWork"/>). A monotonic total; a reader takes a window by reading twice.</summary>
    public static WorkCounterSet PointWork { get; } = new(name: "sdf.point", kinds: [PointEvaluations, PointInstructions,
        PowerMemoHits, PowerMemoMisses, PowerMemoFallbacks, PowerMemoBypasses, PowerMemoAllocations, PowerMemoAllocatedBytes]);
}
