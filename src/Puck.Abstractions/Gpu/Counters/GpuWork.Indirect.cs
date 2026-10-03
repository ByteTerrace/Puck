using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

public static partial class GpuWork {
    /// <summary>The first indirect-light kind in a kernel counter row.</summary>
    public const int IndirectFirstKind = (ShadowStepsFirstKind + ShadowSlotCount);

    internal const int IndirectHitsColumn = (ShadowStepsFirstColumn + ShadowSlotCount);
    internal const int IndirectSamplesColumn = (IndirectHitsColumn + 1);
    internal const int IndirectUnresolvedColumn = (IndirectSamplesColumn + 1);

    /// <summary>Gets the stored hits lit by an indirect shade pass.</summary>
    public static WorkKind IndirectHits { get; } = new(name: "gpu.indirect.hits", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the indirect-cache lookups made by a view.</summary>
    public static WorkKind IndirectSamples { get; } = new(name: "gpu.indirect.samples", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets unresolved rays, light-view texels and shadow fallbacks, partitioned by their detail rows.</summary>
    public static WorkKind IndirectUnresolved { get; } = new(name: "gpu.indirect.unresolved", unit: "count", workClass: WorkClass.PerBackendDeterministic);
}
