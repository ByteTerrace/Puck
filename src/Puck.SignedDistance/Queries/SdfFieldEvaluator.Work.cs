using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    /// <summary>Gets the kind counting point evaluations: one per
    /// <see cref="TryDistance(FixedPosition, out FixedQ4816, out int, out int)"/> walk, through either overload.</summary>
    public static WorkKind PointEvaluations { get; } = new(name: "sdf.point.evaluations", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the instructions those point evaluations visited, after culling.</summary>
    public static WorkKind PointInstructions { get; } = new(name: "sdf.point.instructions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the process-wide point-evaluation work of every evaluator, the interval walk's peer
    /// (<see cref="SdfBoundsCache.ProcessWork"/>). A monotonic total; a reader takes a window by reading twice.</summary>
    public static WorkCounterSet PointWork { get; } = new(name: "sdf.point", kinds: [PointEvaluations, PointInstructions]);
}
