namespace Puck.World.Server;

/// <summary>The arena host the world's own draw sites ride: every seed ladder folds the document's
/// <c>state.&lt;row&gt;</c> descriptor rather than the catalog's bare row name, and every write to a verdict row
/// carries the tick of the firing that made it.</summary>
public sealed class WorldArenaHost : ArenaEffectHost {
    private readonly string[] m_sites;
    private readonly WorldVerdictStamp? m_verdicts;

    /// <summary>Initializes the host over an arena and its draw-site descriptors.</summary>
    /// <param name="arena">The store every read and write addresses.</param>
    /// <param name="sites">The site descriptor per catalog ordinal.</param>
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
        ticksPerSecond: ticksPerSecond
    ) {
        ArgumentNullException.ThrowIfNull(argument: sites);

        m_sites = sites;
        m_verdicts = verdicts;
    }

    /// <inheritdoc/>
    public override string DrawSite(int rowOrdinal) => ((((uint)rowOrdinal) < ((uint)m_sites.Length))
        ? m_sites[rowOrdinal]
        : base.DrawSite(rowOrdinal: rowOrdinal)
    );
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

        if (refusal.Code is null) {
            _ = WorldVerdictStamp.Stamp(
                arena: Arena,
                rowOrdinal: verdictOrdinal,
                tick: Tick
            );
        }

        return applied;
    }
}
