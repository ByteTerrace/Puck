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
    // What one source row costs a comparison: its cells, each against the zones containing it that can restrict an
    // audience, read from the declarations the arena's walk reads (StateArena.ReadAudienceEncloses). A row whose cells
    // carry no policy of their own and lie in no such zone the arena answers by its row policy alone.
    private static long AudienceSpan(RuleCompileContext context, int source) {
        if (source < 0) {
            return 0L;
        }

        var row = context.FindRowAt(rowOrdinal: source);
        var zones = 0L;

        foreach (var zone in context.Catalog.ContainingZones(rowOrdinal: source)) {
            if ((context.FindRowAt(rowOrdinal: zone) is { } declared) && StateVisibility.Restricts(policy: declared.Visibility, cellPolicies: declared.DeclaresCellVisibility)) {
                zones++;
            }
        }
        if ((zones == 0L) && (row is not { DeclaresCellVisibility: true })) {
            return 1L;
        }

        var cells = BoardCells(context: context, row: row);
        var capacity = ((cells > 0) ? cells : context.RowCapacity(rowOrdinal: source));

        return (1L + (capacity * (1L + (3L * zones))));
    }
    /// <summary>Settles a transform's audience check for one firing: the rows it reads besides the rows its live ends
    /// select (<see cref="ArenaTransform.DisclosureReads"/>, and <see cref="ArenaTransform.DisclosureMemberReads"/> for the
    /// family members only one binding reads), and the price of the check. A firing checks the one row each live end's
    /// index selects (<c>ArenaTransforms</c>' disclosure walk reads the bound ends), so a read is left out only when it is
    /// that bound row: a member a live end selects by the binding's own key (<see cref="LiveRow.SelectsOwnMember"/>), read
    /// by that same key. Every other control or fixed read stays, a member of a live end's family included, since the
    /// binding may select another member than the one it reads. The price is those reads and writes with the dearest
    /// candidate of each live end added to both, and the member reads of the dearest binding.</summary>
    /// <param name="scalar">The transform effect.</param>
    /// <param name="controls">The cells that decide whether the transform runs.</param>
    /// <param name="context">The compile context.</param>
    private static void SettleDisclosure(TransformStateEffect scalar, IReadOnlyList<CellAccess> controls, RuleCompileContext context) {
        var accesses = new List<CellAccess>();
        var bound = new HashSet<(int Row, int Member)>();

        int[] Candidates(LiveRow? end) {
            accesses.Clear();
            end?.CollectRows(into: accesses, isSet: false);
            if (end is { SelectsOwnMember: true }) {
                bound.UnionWith(other: accesses.Where(predicate: static access => access.IsMember).Select(selector: static access => (access.RowOrdinal, access.Member)));
            }
            return accesses.Select(selector: static access => access.RowOrdinal).Distinct().ToArray();
        }

        var froms = Candidates(end: scalar.FromRow);
        var tos = Candidates(end: scalar.ToRow);
        var candidates = new HashSet<int>(collection: froms.Concat(tos));
        var reads = new List<int>(collection: scalar.Arena.DisclosureReads);
        var members = new List<CellAccess>(collection: scalar.Arena.DisclosureMemberReads);

        accesses.Clear();
        scalar.CollectFixedReads(into: accesses);
        accesses.AddRange(collection: controls);
        foreach (var access in accesses) {
            if (!access.IsMember) {
                if (!reads.Contains(item: access.RowOrdinal)) { reads.Add(item: access.RowOrdinal); }
            } else if (!bound.Contains(item: (access.RowOrdinal, access.Member))) {
                var member = new CellAccess(Key: default, Member: access.Member, Reach: CellReach.Member, RowOrdinal: access.RowOrdinal);
                if (!members.Contains(item: member)) { members.Add(item: member); }
            }
        }
        _ = members.RemoveAll(match: member => reads.Contains(item: member.RowOrdinal));

        scalar.Arena.DisclosureReads = [.. reads];
        scalar.Arena.DisclosureMemberReads = [.. members];

        var fixedReads = new List<int>();
        var writes = new List<int>();

        accesses.Clear();
        scalar.CollectWrites(into: accesses);
        ArenaTransforms.CollectAccesses(transform: scalar.Arena, catalog: context.Catalog, binding: ArenaTransformBinding.None, memberReads: false, reads: fixedReads, writes: writes);
        writes.AddRange(collection: accesses.Select(selector: static access => access.RowOrdinal).Where(predicate: row => !candidates.Contains(item: row)));

        // The check is the product of the targets' comparisons and the sources' spans, so the dearest candidate of
        // each end in each factor, and the dearest binding's member reads, bound every pairing a binding can select.
        long Dearest(int[] rows, Func<int, long> price) => ((rows.Length == 0) ? 0L : rows.Max(selector: price));
        long Spans(IEnumerable<int> rows) => rows.Aggregate(seed: 0L, func: (sum, source) => sum.AddSaturating(right: AudienceSpan(context: context, source: source)));

        var comparisons = writes.Distinct().Aggregate(seed: 0L, func: (sum, target) => sum.AddSaturating(right: AudienceComparisons(context: context, target: target)))
            .AddSaturating(right: Dearest(rows: froms, price: row => AudienceComparisons(context: context, target: row)))
            .AddSaturating(right: Dearest(rows: tos, price: row => AudienceComparisons(context: context, target: row)));
        var spans = Spans(rows: fixedReads.Distinct())
            .AddSaturating(right: members.GroupBy(keySelector: static member => member.Member).Select(selector: binding => Spans(rows: binding.Select(selector: static member => member.RowOrdinal))).DefaultIfEmpty().Max())
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
                SettleDisclosure(context: context, controls: controls, scalar: scalar);
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
