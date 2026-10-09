using Puck.SignedDistance;

namespace Puck.SdfVm;

// Readiness and captures never wait on a solve that cannot finish. A solve cannot finish when the scene keeps
// withdrawing its admitted transport while a wait holds (a held capture pins the simulation, so a second invalidation
// after the wait began is a scene that changes on its own), or when its remaining work, at the prices the device
// measured, needs more produced frames than FinishFrameBound. Before every kind with work left has been measured the
// estimate is unknown and the solve is still presumed finishable; the transport and lighting it measures arrive within
// its first frames of each kind.
public sealed partial class SdfIndirectCache {
    /// <summary>The produced frames a wait may still expect a solve to need before it is refused as unable to finish.</summary>
    public const long FinishFrameBound = 4096;

    /// <summary>Gets the geometry invalidations that withdrew admitted transport, a reset included.</summary>
    public long Invalidations { get; private set; }
    /// <summary>Gets the produced frames the current transport and finite lighting solve still need at the measured
    /// prices, or null while a kind with work left has no measurement. Each frame submits at most its device slice
    /// (<see cref="FrameCost"/>) and one plan's item budgets of transport, and one shade batch.</summary>
    public long? RemainingFrames {
        get {
            var remaining = m_schedule.Remaining;
            var instructions = InstructionCount;
            var place = UnitsOf(kind: PlaceKind);
            var classify = UnitsOf(kind: ClassifyKind);
            var trace = UnitsOf(kind: TraceKind);

            if (((remaining.Placements > 0) && (place.MeasuredFieldCost is null)) || ((remaining.Partitions > 0) && (classify.MeasuredFieldCost is null)) ||
                ((remaining.Strata > 0) && (trace.MeasuredFieldCost is null))) { return null; }
            var transportCost = checked((((remaining.Placements * place.ItemCost(instructionCount: instructions))
                + (remaining.Partitions * classify.ItemCost(instructionCount: instructions))) + (remaining.Strata * trace.ItemCost(instructionCount: instructions))));
            var transport = Math.Max(val1: Frames(cost: transportCost), val2: (Batches(count: Math.Max(val1: remaining.Placements, val2: remaining.Partitions), budget: Layout.ClassifyBudget)
                + Batches(count: remaining.Strata, budget: Layout.TraceBudget)));
            var probes = ((m_solve is { } solve) ? solve.RemainingProbes
                : (((long)(Layout.BounceLimit + 1)) * (m_slots.Count * ((long)SdfIndirectLayout.ProbesPerBrick))));

            if (probes == 0) { return transport; }
            // Unmeasured lighting leaves the total unknown, unless the transport alone already exceeds the bound.
            if ((Lighting?.CapturedFrame is not { } source) || (MeasuredFieldCost(kind: SdfIndirectLayout.CostShade) is not { } shade)) {
                return ((transport > FinishFrameBound) ? transport : null);
            }
            var units = ShadeUnitsOf(frame: source);
            var lighting = Math.Max(val1: Frames(cost: checked((probes * units.ItemCost(instructionCount: source.Program.InstructionCount)))),
                val2: Batches(count: probes, budget: Math.Max(val1: 1, val2: SdfIndirectWork.ShadeProbeBudget(frame: source, layout: Layout, measuredFieldCost: shade))));

            return checked((transport + lighting));
        }
    }

    /// <summary>Names why a wait armed when <see cref="Invalidations"/> read <paramref name="since"/> cannot expect this
    /// solve to finish, or returns null.</summary>
    /// <param name="since">The invalidation count when the wait began.</param>
    /// <returns>The refusal, or null.</returns>
    public string? CannotFinishReason(long since) {
        var invalidated = (Invalidations - since);

        if (invalidated > 1) {
            return System.FormattableString.Invariant(formattable: $"the scene withdrew its admitted transport {invalidated} times since the wait began, so its solve never stands still long enough to finish");
        }
        return (((RemainingFrames is { } frames) && (frames > FinishFrameBound))
            ? System.FormattableString.Invariant(formattable: $"its solve still needs about {frames} produced frames at the measured prices, beyond the {FinishFrameBound}-frame bound")
            : null);
    }

    // Each produced frame admits its device slice of transport and lighting.
    private long Frames(long cost) => (((cost + FrameCost) - 1) / FrameCost);
    private static long Batches(long count, int budget) => ((budget <= 0) ? 0 : (((count + budget) - 1) / budget));
    private void CountInvalidation() => Invalidations++;
}
