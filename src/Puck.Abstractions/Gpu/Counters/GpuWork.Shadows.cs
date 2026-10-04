using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

public static partial class GpuWork {
    /// <summary>The maximum shadow march slots: four stable slots and two active incoming handoffs.</summary>
    public const int ShadowSlotCount = 6;
    /// <summary>The first shadow slot's position in a kernel counter row, in counts rather than words.</summary>
    public const int ShadowStepsFirstKind = 5;

    internal const int ShadowStepsFirstColumn = 21;

    /// <summary>The position of the secondary-shadow pixel count in the kernel row.</summary>
    public const int ShadowPixelsKind = (ShadowStepsFirstKind + ShadowSlotCount);

    internal const int ShadowPixelsColumn = (ShadowStepsFirstColumn + ShadowSlotCount);

    /// <summary>Secondary lit shadow pixels classified once each into the shadow pass's decision detail rows.</summary>
    public static WorkKind ShadowPixels { get; } = new(name: "gpu.shadow.pixels", unit: "count", workClass: WorkClass.PerBackendDeterministic);

    private static readonly WorkKind[] ShadowStepKinds = [
        new(name: "gpu.shadow.slot0.steps", unit: "count", workClass: WorkClass.PerBackendDeterministic),
        new(name: "gpu.shadow.slot1.steps", unit: "count", workClass: WorkClass.PerBackendDeterministic),
        new(name: "gpu.shadow.slot2.steps", unit: "count", workClass: WorkClass.PerBackendDeterministic),
        new(name: "gpu.shadow.slot3.steps", unit: "count", workClass: WorkClass.PerBackendDeterministic),
        new(name: "gpu.shadow.slot4.steps", unit: "count", workClass: WorkClass.PerBackendDeterministic),
        new(name: "gpu.shadow.slot5.steps", unit: "count", workClass: WorkClass.PerBackendDeterministic),
    ];

    /// <summary>Gets per-slot march counts in the shadow pass's row. Stable slots occupy [0, K), active incoming
    /// handoffs [K, K + active fades); every other slot stays zero after the frame's counter clear. These counts
    /// partition the pass's <see cref="MarchSteps"/>; they are not additional field evaluations.</summary>
    public static ReadOnlySpan<WorkKind> ShadowSteps => ShadowStepKinds;
}
