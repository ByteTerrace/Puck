using RuleWorkBudget = Puck.State.Rules.RuleWorkBudget;

namespace Puck.World;

/// <summary>The static bound on one interaction's sweep: how many carriers each side can hold, how many bound
/// evaluations the sweep can make, and what the sweep costs before its first evaluation.</summary>
/// <param name="Lefts">The most left carriers.</param>
/// <param name="Rights">The most right carriers; zero for a region interaction, which binds none.</param>
/// <param name="Evaluations">The most bound evaluations one sweep makes.</param>
/// <param name="Setup">The carrier gathering, the co-occurrence tests, and the nearest-neighbour selection.</param>
public readonly record struct WorldInteractionBound(long Lefts, long Rights, long Evaluations, RuleWork Setup) {
    // A carrier scan reads a cell's key, parses its body index, finds the body, and reads the tag.
    private const long CarrierScanWork = 4L;
    // A distance test reads two positions and compares one squared length with the squared range.
    private const long DistanceTestWork = 4L;
    // An occupancy test finds the region's table and reads one flag.
    private const long OccupancyTestWork = 2L;

    // A tag row holds at most one carrier per body; a pool can bind several logical instances to one body.
    // A name that resolves to no document row gathers nothing.
    private static long Carriers(WorldFactsCompileContext context, string row, out long scanned) {
        if (CellName.TryParse(candidate: row, name: out var poolName, reason: out _) && context.Catalog.TryGetPool(name: poolName, pool: out var pool) && (pool is not null)) {
            // The admitted carrier field selects an enum-indexed logical binding. A live snapshot and a
            // field/body read are linear in capacity; physical placement lookup is already ordinal-addressed.
            scanned = pool.Capacity;
            return pool.Capacity;
        }
        if (!context.Definition.StateCatalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: row
        )) {
            scanned = 0L;

            return 0L;
        }

        scanned = context.RowCapacity(rowOrdinal: handle.Ordinal);

        return Math.Min(
            val1: scanned,
            val2: context.Definition.Population.Capacity
        );
    }
    private static RuleWork Gather(long scanned, long carriers) => ((scanned * RuleWork.Known(units: CarrierScanWork)) + RuleWorkBudget.IntrosortWork(count: carriers));

    /// <summary>Bounds one interaction against a document.</summary>
    /// <param name="interaction">The compiled co-occurrence.</param>
    /// <param name="context">The compile context.</param>
    /// <returns>The bound.</returns>
    /// <remarks>A distance sweep tests every left against every right, whatever the neighbour limit: the limit
    /// bounds the evaluations that follow the scan, never the scan. With a limit of <c>K</c>, a right within range
    /// is inserted into the kept set at up to <c>K</c> comparisons and moves, and each left then evaluates at most
    /// <c>min(K, R)</c> pairs; without one it evaluates every right but itself.</remarks>
    public static WorldInteractionBound Of(CompiledInteraction interaction, WorldFactsCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var lefts = Carriers(
            context: context,
            row: interaction.Left,
            scanned: out var leftScanned
        );
        var setup = Gather(
            carriers: lefts,
            scanned: leftScanned
        );

        if (interaction.CoOccurrence == WorldInteractionCoOccurrence.Region) {
            return new WorldInteractionBound(
                Evaluations: ((interaction.LeftPool is not null) ? lefts : Math.Min(
                    val1: lefts,
                    val2: WorldRuleWorkBudget.RegionCapacityBound(
                        definition: context.Definition,
                        placementId: interaction.Right
                    ).Bound
                )),
                Lefts: lefts,
                Rights: 0L,
                Setup: (setup + (lefts * RuleWork.Known(units: OccupancyTestWork)))
            );
        }
        if (interaction.CoOccurrence != WorldInteractionCoOccurrence.Distance) {
            return new WorldInteractionBound(
                Evaluations: lefts,
                Lefts: lefts,
                Rights: 0L,
                Setup: setup
            );
        }

        var rights = Carriers(
            context: context,
            row: interaction.Right,
            scanned: out var rightScanned
        );
        // A body never pairs with itself. A left drawn from the same collection excludes at least its own
        // instance. A different pool can bind every right instance to another body, so physical population
        // cannot cap its logical evaluation count. Ordinary tag rows still have at most one entry per body.
        var partners = (string.Equals(
            a: interaction.Left,
            b: interaction.Right,
            comparisonType: StringComparison.Ordinal
        )
            ? Math.Max(
                val1: 0L,
                val2: (rights - 1L)
            )
            : ((interaction.RightPool is not null) ? rights : Math.Min(
                val1: rights,
                val2: Math.Max(
                    val1: 0L,
                    val2: (context.Definition.Population.Capacity - 1L)
                )
            ))
        );
        var kept = ((interaction.Neighbours > 0)
            ? Math.Min(
                val1: interaction.Neighbours,
                val2: partners
            )
            : partners
        );
        var perPair = (DistanceTestWork + ((interaction.Neighbours > 0)
            ? (2L * kept)
            : 0L
        ));

        return new WorldInteractionBound(
            Evaluations: (lefts * kept),
            Lefts: lefts,
            Rights: rights,
            Setup: ((setup + Gather(
                carriers: rights,
                scanned: rightScanned
            )) + ((lefts * rights) * RuleWork.Known(units: perPair)))
        );
    }
}
