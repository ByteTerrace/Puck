namespace Puck.State.Rules;

public static partial class RuleCompiler {
    private static RuleWork DisclosureCost(IEnumerable<int> reads, IEnumerable<int> writes, RuleCompileContext context) {
        var cost = RuleWork.Zero;
        foreach (var target in writes.Distinct()) {
            var comparisons = 1L + ((long)(context.FindRowAt(rowOrdinal: target)?.Visibility?.Readers?.Count ?? 0) * StateCapacity.MaxVisibilityReaders);
            foreach (var source in reads.Distinct()) {
                if (source < 0) { continue; }
                var zones = context.Catalog.ContainingZones(rowOrdinal: source).Count;
                var cells = BoardCells(context: context, row: context.FindRowAt(rowOrdinal: source));
                var capacity = cells > 0 ? cells : context.RowCapacity(rowOrdinal: source);
                cost += RuleWork.Known(units: comparisons) * (1L + (capacity * (1L + (3L * zones))));
            }
        }
        return cost;
    }
    /// <summary>Checks and retains state dependencies that decide whether a transform runs. Hosts with additional
    /// choice policies pass their gates, scores and membership dependencies through this same door.</summary>
    /// <param name="effects">The effects controlled by those dependencies.</param>
    /// <param name="controls">The state cells that control execution.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="ruleName">The rule name used by a refusal.</param>
    public static void RequireTransformControlAudience(IRuleEffect[] effects, IReadOnlyList<CellAccess> controls, RuleCompileContext context, string ruleName) {
        foreach (var effect in effects) {
            if (effect is TransformStateEffect { Arena: not ArenaTransform.Observe } scalar) {
                scalar.Arena.DisclosureReads = scalar.Arena.DisclosureReads.Concat(controls.Select(static read => read.RowOrdinal)).Distinct().ToArray();
                var reads = new List<int>();
                var writes = new List<int>();
                ArenaTransforms.CollectAccesses(transform: scalar.Arena, catalog: context.Catalog, binding: ArenaTransformBinding.None, reads: reads, writes: writes);
                var liveWrites = new List<CellAccess>();
                scalar.CollectWrites(into: liveWrites);
                writes.AddRange(collection: liveWrites.Select(static access => access.RowOrdinal));
                RequireEnclosedAudience(reads: reads, writes: writes, context: context, ruleName: ruleName);
                scalar.DisclosureCost = DisclosureCost(reads: reads, writes: writes, context: context);
            } else if (effect is VectorEffect and not VectorCopyEffect) {
                var vector = (VectorEffect)effect;
                vector.DisclosureControls = vector.DisclosureControls.Concat(controls.Select(static read => read.RowOrdinal)).Distinct().ToArray();
                RequireEnclosedAudience(reads: vector.DisclosureControls, writes: [vector.RowOrdinal], context: context, ruleName: ruleName);
                var reads = new List<CellAccess>();
                vector.CollectReads(into: reads);
                vector.DisclosureCost = DisclosureCost(reads: reads.Select(static read => read.RowOrdinal).Concat(vector.DisclosureControls), writes: [vector.RowOrdinal], context: context);
            }
            if (effect.Arms.Count == 0) { continue; }
            var nested = new List<CellAccess>(collection: controls);
            if (effect is IfEffect branch) {
                RuleDataflow.CollectGate(gate: branch.Condition, into: nested);
            } else {
                // A savepoint's fallback, pool loop or claim can be selected by any read its composite performs.
                effect.CollectReads(into: nested);
            }
            foreach (var arm in effect.Arms) {
                RequireTransformControlAudience(effects: arm, controls: nested, context: context, ruleName: ruleName);
            }
        }
    }
}
