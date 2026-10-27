namespace Puck.World.Server;

/// <summary>The arena host the world's own draw sites ride: every seed ladder folds the document's
/// <c>state.&lt;row&gt;</c> descriptor rather than the catalog's bare row name, and every write to a verdict row
/// carries the tick of the firing that made it.</summary>
public sealed class WorldArenaHost : ArenaEffectHost {
    private readonly WorldVerdictStamp? m_verdicts;

    /// <summary>Initializes the host over an arena and its draw-site descriptors.</summary>
    /// <param name="arena">The store every read and write addresses.</param>
    /// <param name="sites">The site descriptor per catalog ordinal (<see cref="WorldDrawSites.Of"/>).</param>
    /// <param name="generators">The section's declared draw sources.</param>
    /// <param name="ticksPerSecond">The simulation rate a dynamics follower is stepped at.</param>
    /// <param name="dynamics">The declared dynamics rows.</param>
    /// <param name="documentSeed">The document's own reroll lever.</param>
    /// <param name="instanceIdentity">The running instance's identity.</param>
    /// <param name="verdicts">The installed document's verdict rows, or <see langword="null"/> when it declares
    /// none.</param>
    public WorldArenaHost(StateArena arena, string[] sites, IReadOnlyList<GeneratorRow>? generators, int ticksPerSecond, IReadOnlyList<DynamicsRow>? dynamics, ulong documentSeed, string instanceIdentity, WorldVerdictStamp? verdicts = null) : base(
        arena: arena,
        documentSeed: documentSeed,
        dynamics: dynamics,
        generators: generators,
        instanceIdentity: instanceIdentity,
        sites: (sites ?? throw new ArgumentNullException(paramName: nameof(sites))),
        ticksPerSecond: ticksPerSecond
    ) {
        m_verdicts = verdicts;
    }

    /// <inheritdoc/>
    /// <remarks>This is the door that makes "a rule's firing wrote this" checkable: a write to any cell of a verdict
    /// row, or of a witness of one, stamps the verdict's <see cref="WorldVerdict.FiredTickKey"/> with the firing's
    /// tick, and a verdict that already fired <see cref="WorldVerdict.Fail"/> on an earlier tick absorbs later writes
    /// to itself and its witnesses, so its first failing tick and the values its gate saw then are what the export
    /// carries.</remarks>
    public override bool Apply(in Mutation mutation, out EffectRefusal refusal) {
        var ordinal = mutation.RowOrdinal;

        if (
            (m_verdicts is not { } verdicts) ||
            !verdicts.TryResolve(
            rowOrdinal: ordinal,
            status: out var status,
            verdictOrdinal: out var verdictOrdinal
        )
        ) {
            return base.Apply(
                mutation: in mutation,
                refusal: out refusal
            );
        }

        if (WorldVerdictStamp.IsSettled(
            arena: Arena,
            rowOrdinal: verdictOrdinal,
            status: status,
            tick: Tick
        )) {
            // Not a refusal: a level-mode rule re-fires every tick it holds, and a settled verdict answering the
            // same way is no more a hazard than a write that left a cell exactly as it was.
            refusal = EffectRefusal.None;

            return false;
        }

        var applied = base.Apply(
            mutation: in mutation,
            refusal: out refusal
        );

        if ((refusal.Code is null) && !WorldVerdictStamp.Stamp(
                arena: Arena,
                rowOrdinal: verdictOrdinal,
                tick: Tick,
                reason: out var reason
            )) {
            // The caller rewinds the firing on refusal, including the status already written above.
            refusal = EffectRefusal.Of(code: RuleEffectRefusal.MutationRejected, reason: reason);
            return false;
        }

        return applied;
    }
    /// <inheritdoc/>
    public override bool TryTransform(ArenaTransform transform, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        if (m_verdicts is not { } verdicts) {
            return base.TryTransform(binding: in binding, moved: out moved, refusal: out refusal, transform: transform);
        }

        Span<int> writes = stackalloc int[Arena.Catalog.Rows.Count];
        var count = ArenaTransforms.CollectWrites(transform: transform, catalog: Arena.Catalog, binding: in binding, writes: writes);

        foreach (var row in writes[..count]) {
            if (verdicts.TryResolve(rowOrdinal: row, status: out var status, verdictOrdinal: out var verdict) &&
                WorldVerdictStamp.IsSettled(arena: Arena, rowOrdinal: verdict, status: status, tick: Tick)) {
                // A transform is indivisible: freezing either end also freezes the other end and any draw.
                moved = false;
                refusal = EffectRefusal.None;
                return true;
            }
        }

        if (!base.TryTransform(binding: in binding, moved: out moved, refusal: out refusal, transform: transform)) {
            return false;
        }
        foreach (var row in writes[..count]) {
            if (verdicts.TryResolve(rowOrdinal: row, status: out _, verdictOrdinal: out var verdict) &&
                !WorldVerdictStamp.Stamp(arena: Arena, rowOrdinal: verdict, tick: Tick, reason: out var reason)) {
                refusal = EffectRefusal.Of(code: RuleEffectRefusal.MutationRejected, reason: reason);
                moved = false;
                return false;
            }
        }
        return true;
    }
}
