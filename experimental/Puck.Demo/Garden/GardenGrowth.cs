using Puck.Maths;

namespace Puck.Demo.Garden;

/// <summary>One tree's growth read at a given tick: which branch order is fully revealed, and how far the FRONTIER
/// order (the one currently telescoping in) has grown toward its final size.</summary>
/// <param name="Stage">The deepest branch order currently revealed at all (0 = trunk only). Equals
/// <see cref="GardenTreeStructure.MaxDepth"/> once fully grown.</param>
/// <param name="FrontierScale">The length/radius multiplier for segments/leaves AT exactly <see cref="Stage"/> — in
/// <c>[FrontierMinScale, 1]</c>, presentation-only (cast down from a <see cref="FixedQ4816"/> computation — see
/// <see cref="GardenGrowth.Compute"/>). Segments/leaves SHALLOWER than <see cref="Stage"/> are already fully grown
/// (scale 1); DEEPER ones are not yet revealed at all.</param>
internal readonly record struct GardenGrowthState(int Stage, float FrontierScale);

/// <summary>The reveal-ladder gate: turns (ticks since planting, a tree's own max depth) into which branch order is
/// revealed and how far its frontier has telescoped in. A pure function of integers/fixed-point — the ONLY float is
/// the final cast to <see cref="GardenGrowthState.FrontierScale"/>, a presentation multiplier (see the deterministic-
/// garden design's fixed-point-for-structure/float-for-presentation split).</summary>
internal static class GardenGrowth {
    // The just-sprouted floor: a frontier segment never renders at literal zero size (a sliver), it pops in at this
    // fraction of its final size and eases up to 1 over its stage.
    private static readonly FixedQ4816 FrontierMinScale = FixedQ4816.FromDouble(value: 0.22);
    private static readonly FixedQ4816 FrontierMinScaleComplement = (FixedQ4816.One - FrontierMinScale);

    /// <summary>Computes a tree's growth state at a tick.</summary>
    /// <param name="ticksSincePlanting">How many sim ticks have elapsed since this garden was planted.</param>
    /// <param name="maxDepth">The tree's own <see cref="GardenTreeStructure.MaxDepth"/> (its final stage count − 1).</param>
    /// <returns>The revealed stage and the frontier's current scale.</returns>
    internal static GardenGrowthState Compute(ulong ticksSincePlanting, int maxDepth) {
        if (maxDepth <= 0) {
            return new GardenGrowthState(Stage: 0, FrontierScale: 1f);
        }

        var stageIndex = (int)Math.Min(val1: (ulong)maxDepth, val2: (ticksSincePlanting / GardenTreeGenerator.TicksPerStage));

        if (stageIndex >= maxDepth) {
            return new GardenGrowthState(Stage: stageIndex, FrontierScale: 1f);
        }

        var ticksIntoStage = (ticksSincePlanting - ((ulong)stageIndex * GardenTreeGenerator.TicksPerStage));
        var fraction = (FixedQ4816.FromInteger(value: (long)ticksIntoStage) / FixedQ4816.FromInteger(value: (long)GardenTreeGenerator.TicksPerStage));
        var eased = (FrontierMinScale + (fraction * FrontierMinScaleComplement));

        return new GardenGrowthState(Stage: stageIndex, FrontierScale: (float)eased);
    }
}
