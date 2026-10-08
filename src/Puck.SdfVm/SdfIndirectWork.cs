using Puck.Abstractions.Counting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The residency cache's deterministic host schedule counts.</summary>
public static class SdfIndirectWork {
    /// <summary>The host counter source.</summary>
    public const string SourceName = "sdf.indirect";

    /// <summary>The scheduled ray total.</summary>
    public static WorkKind Rays { get; } = Kind(name: "indirect.rays.scheduled", unit: "rays");
    /// <summary>The scheduled probe stratum total.</summary>
    public static WorkKind Probes { get; } = Kind(name: "indirect.probes.scheduled", unit: "probes");
    /// <summary>The conservative depth regions successfully submitted by the residency's light camera.</summary>
    public static WorkKind LightRegions { get; } = Kind(name: "indirect.light.regions", unit: "regions");
    /// <summary>The residency's recorded host schedule vocabulary. Receiver proof work is counted by the GPU kernels.</summary>
    public static WorkKind[] Kinds { get; } = [Rays, Probes, LightRegions,
        .. new[] { "demand", "geometry", "light", "shadow", "screen", "converge" }.Select(selector: reason => Kind(name: $"indirect.probes.scheduled.{reason}", unit: "probes")),
        .. new[] { "near", "room", "world" }.SelectMany(selector: level => new[] { "allocated", "evicted", "refused" }.Select(selector: action => Kind(name: $"indirect.bricks.{action}.{level}", unit: "bricks"))),
        Kind(name: "indirect.sweeps.completed", unit: "sweeps"), Kind(name: "indirect.sweeps.restarted", unit: "sweeps")];

    /// <summary>Bounds one shade batch's full-field fallback work by the tier's transport evaluation allowance.
    /// Every ray may miss every directional map; map availability never increases admission.</summary>
    /// <param name="layout">The cache tier and its existing work allowances.</param>
    /// <param name="directionalChannels">The stable and active incoming directional channels, counting duplicates.</param>
    /// <returns>The probe count admitted per batch, or zero for the disabled tier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The channel count exceeds the shadow layout.</exception>
    public static int ShadeProbeBudget(SdfIndirectLayout layout, int directionalChannels) {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegative(directionalChannels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(directionalChannels, (SdfShadowSlots.MaxSlots + SdfShadowSlots.MaxFadeSlots));
        if ((directionalChannels == 0) || (layout.Tier == SdfIndirectTier.Off)) { return layout.ShadeBudget; }
        var evaluationsPerProbe = checked(((layout.RaysPerProbe * directionalChannels) * SdfIndirectLightLayout.MarchSteps));

        return Math.Min(val1: layout.ShadeBudget, val2: (layout.TraceEvaluationCeiling / evaluationsPerProbe));
    }
    /// <summary>Prices the pinned source's exact directional visibility channels. Vacant and nondirectional channels
    /// perform no fallback field march; each active incoming handoff performs its own visibility query.</summary>
    /// <param name="layout">The cache tier.</param>
    /// <param name="lights">The immutable source lights.</param>
    /// <returns>The probe count admitted per batch.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static int ShadeProbeBudget(SdfIndirectLayout layout, SdfLights lights) {
        ArgumentNullException.ThrowIfNull(lights);
        return ShadeProbeBudget(directionalChannels: SdfIndirectCost.DirectionalChannels(lights: lights), layout: layout);
    }
    /// <summary>Prices the pinned frame's possible visibility work. Disabled direct light performs no shadow
    /// queries; enabled direct light retains the full directional fallback bound regardless of map availability.
    /// A probe whose shading exceeds one submission is a batch of its own, which the cache splits into ray chunks.</summary>
    /// <param name="layout">The cache tier.</param>
    /// <param name="frame">The immutable lighting source.</param>
    /// <returns>The probe count admitted per batch.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="SdfIndirectCostRefusedException">One field query or one shaded ray exceeds a submission.</exception>
    public static int ShadeProbeBudget(SdfIndirectLayout layout, SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        var count = ((((frame.IndirectSources & SdfIndirectSources.Direct) == 0) || (frame.IndirectGains.Lights == 0f))
            ? ShadeProbeBudget(directionalChannels: 0, layout: layout)
            : ShadeProbeBudget(layout: layout, lights: frame.Lights));
        var units = SdfIndirectCost.ShadeUnits(frame: frame, layout: layout);
        var whole = SdfIndirectCost.WholeItems(count: count, instructionCount: frame.Program.InstructionCount, units: units);

        if ((whole > 0) || (count == 0)) { return whole; }
        if (units.RefusalOf(instructionCount: frame.Program.InstructionCount) is { } refusal) {
            throw new SdfIndirectCostRefusedException(message: $"Indirect admission refused: {SdfWorldPackage.IndirectShade}: {refusal}.");
        }
        return 1;
    }

    private static WorkKind Kind(string name, string unit) => new(name: name, unit: unit, workClass: WorkClass.Deterministic);
}
