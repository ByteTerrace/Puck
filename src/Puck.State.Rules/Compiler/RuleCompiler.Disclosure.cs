using Puck.Maths;

namespace Puck.State.Rules;

public static partial class RuleCompiler {
    /// <summary>Bounds the arena audience check shared by transforms and searches, including live cell and zone
    /// policies: every written row compared against every read row, each comparison as dear as the target's readers
    /// and the source's cells and zones make it.</summary>
    /// <param name="reads">The rows the check reads; a negative ordinal is skipped.</param>
    /// <param name="writes">The rows the check writes.</param>
    /// <param name="context">The compile context.</param>
    /// <returns>The work units.</returns>
    public static RuleWork DisclosureCost(IEnumerable<int> reads, IEnumerable<int> writes, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var comparisons = writes.Distinct().Aggregate(seed: 0L, func: (sum, target) => sum.AddSaturating(right: AudienceComparisons(context: context, target: target)));
        var spans = reads.Distinct().Aggregate(seed: 0L, func: (sum, source) => sum.AddSaturating(right: AudienceSpan(context: context, source: source)));

        return RuleWork.Known(units: comparisons.MultiplySaturating(right: spans));
    }
    // The comparisons one source costs against a target: one, plus every reader the target's audience can name.
    private static long AudienceComparisons(RuleCompileContext context, int target) =>
        (1L + ((long)(context.FindRowAt(rowOrdinal: target)?.Visibility?.Readers?.Count ?? 0) * StateCapacity.MaxVisibilityReaders));
    // What one source row costs a comparison: its cells, each against the zones containing it.
    private static long AudienceSpan(RuleCompileContext context, int source) {
        if (source < 0) {
            return 0L;
        }

        var zones = context.Catalog.RestrictingZones(rowOrdinal: source).Length;
        var cells = BoardCells(context: context, row: context.FindRowAt(rowOrdinal: source));
        var capacity = ((cells > 0) ? cells : context.RowCapacity(rowOrdinal: source));

        return (1L + (capacity * (1L + (3L * zones))));
    }
    /// <summary>Settles a transform's audience check for one firing: the rows its kernel reads besides its live ends
    /// (<see cref="ArenaTransform.DisclosureReads"/>) and the price of the check. A firing checks the one row each live
    /// end's index selects (<c>ArenaTransforms</c>' disclosure walk reads the bound ends), so the price is the fixed
    /// reads and writes with the dearest candidate of each live end added to both.</summary>
    /// <param name="scalar">The transform effect.</param>
    /// <param name="controls">The rows whose cells decide whether the transform runs.</param>
    /// <param name="context">The compile context.</param>
    private static void SettleDisclosure(TransformStateEffect scalar, IEnumerable<int> controls, RuleCompileContext context) {
        var accesses = new List<CellAccess>();

        scalar.FromRow?.CollectRows(into: accesses, isSet: false);

        var froms = accesses.Select(selector: static access => access.RowOrdinal).Distinct().ToArray();

        accesses.Clear();
        scalar.ToRow?.CollectRows(into: accesses, isSet: false);

        var tos = accesses.Select(selector: static access => access.RowOrdinal).Distinct().ToArray();
        var candidates = new HashSet<int>(collection: froms.Concat(tos));

        accesses.Clear();
        scalar.CollectReads(into: accesses);

        var reads = accesses.Select(selector: static access => access.RowOrdinal).Concat(controls).Where(predicate: row => !candidates.Contains(item: row)).Distinct().ToList();

        scalar.Arena.DisclosureReads = [.. reads];

        var writes = new List<int>();

        accesses.Clear();
        scalar.CollectWrites(into: accesses);
        ArenaTransforms.CollectAccesses(transform: scalar.Arena, catalog: context.Catalog, binding: ArenaTransformBinding.None, reads: reads, writes: writes);
        writes.AddRange(collection: accesses.Select(selector: static access => access.RowOrdinal).Where(predicate: row => !candidates.Contains(item: row)));

        // The check is the product of the targets' comparisons and the sources' spans, so the dearest candidate of
        // each end in each factor bounds every pairing a binding can select.
        long Dearest(int[] rows, Func<int, long> price) => ((rows.Length == 0) ? 0L : rows.Max(selector: price));

        var comparisons = writes.Distinct().Aggregate(seed: 0L, func: (sum, target) => sum.AddSaturating(right: AudienceComparisons(context: context, target: target)))
            .AddSaturating(right: Dearest(rows: froms, price: row => AudienceComparisons(context: context, target: row)))
            .AddSaturating(right: Dearest(rows: tos, price: row => AudienceComparisons(context: context, target: row)));
        var spans = reads.Distinct().Aggregate(seed: 0L, func: (sum, source) => sum.AddSaturating(right: AudienceSpan(context: context, source: source)))
            .AddSaturating(right: Dearest(rows: froms, price: row => AudienceSpan(context: context, source: row)))
            .AddSaturating(right: Dearest(rows: tos, price: row => AudienceSpan(context: context, source: row)));

        scalar.DisclosureCost = RuleWork.Known(units: comparisons.MultiplySaturating(right: spans));
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
                var reads = new List<int>(collection: scalar.Arena.DisclosureReads.Concat(controls.Select(static read => read.RowOrdinal)));
                var writes = new List<int>();
                var liveAccesses = new List<CellAccess>();

                // The audience refusal reads every row a live end can select; the per-firing check reads only the
                // bound ones.
                scalar.CollectReads(into: liveAccesses);
                reads.AddRange(collection: liveAccesses.Select(static access => access.RowOrdinal));
                ArenaTransforms.CollectAccesses(transform: scalar.Arena, catalog: context.Catalog, binding: ArenaTransformBinding.None, reads: reads, writes: writes);
                liveAccesses.Clear();
                scalar.CollectWrites(into: liveAccesses);
                writes.AddRange(collection: liveAccesses.Select(static access => access.RowOrdinal));
                RequireEnclosedAudience(reads: reads, writes: writes, context: context, ruleName: ruleName);
                SettleDisclosure(context: context, controls: scalar.Arena.DisclosureReads.Concat(controls.Select(static read => read.RowOrdinal)), scalar: scalar);
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
