using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Composes one submitted state operation through the arena kernels a rule firing fires through. The
/// operation is resolved to ordinals and interned keys once, applied inside a journal scope, and read back as the
/// candidate document; the scope is always rewound, so a caller's store is exactly as it was and the ordinary
/// mutation pipeline installs the candidate.</summary>
public static class WorldArenaTransforms {
    /// <summary>Lists every state row whose edit capability an operation needs.</summary>
    /// <param name="transform">The operation.</param>
    /// <returns>The addressed row names.</returns>
    public static IEnumerable<string> Subjects(StateTransform transform) => transform switch {
        StateTransform.Transfer transfer => ((transfer.Draw is null)
        ? [transfer.From, transfer.To]
        : [transfer.From, transfer.To, transfer.Draw]),
        StateTransform.SetRay ray => [ray.Row],
        StateTransform.Observe observe => [observe.Row],
        StateTransform.Shuffle shuffle => [shuffle.Row, shuffle.Draw],
        StateTransform.SortZone sortZone => [sortZone.Row, .. sortZone.By.Select(selector: key => key.Row)],
        StateTransform.SortKeyed sortKeyed => [sortKeyed.Row],
        StateTransform.WriteSet writeSet => [writeSet.Row],
        StateTransform.BoardCombine combine => [combine.Row],
        StateTransform.Arrange arrange => [arrange.Row],
        StateTransform.Push push => [push.Row],
        StateTransform.ClearEnclosed enclosed => [enclosed.Row],
        StateTransform.Mix mix => SpellingSubject(spelling: mix.Into),
        StateTransform.Mean mean => SpellingSubject(spelling: mean.Into),
        StateTransform.Nearest nearest => SpellingSubject(spelling: nearest.Into),
        StateTransform.Remember remember => SpellingSubject(spelling: remember.Into),
        _ => [],
    };
    /// <summary>Composes one operation without changing the supplied definition.</summary>
    /// <param name="definition">The validated current definition.</param>
    /// <param name="transform">The operation.</param>
    /// <param name="actor">The stamped acting principal.</param>
    /// <param name="tick">The current simulation tick.</param>
    /// <param name="instance">The authoritative instance identity used by draw streams.</param>
    /// <param name="candidate">The new definition, or the original on refusal.</param>
    /// <param name="reason">The refusal reason, or empty.</param>
    /// <param name="guard">The submitted phase guard, or <see langword="null"/>. A matching guard both admits the
    /// operation and completes it: the composed candidate carries its phase row advanced by one.</param>
    /// <returns>Whether the operation composed.</returns>
    public static bool TryApply(WorldDefinition definition, StateTransform transform, WorldPrincipal actor, ulong tick, string instance, out WorldDefinition candidate, out string reason, PhaseGuard? guard = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: transform);

        candidate = definition;
        // A row declaring a phase admits an outside operation only under that phase's own guard; the world's own
        // rules write it unguarded.
        if (
            (actor != WorldPrincipal.World) &&
            Subjects(transform: transform).Any(predicate: name =>
            ((WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: name
        )?.PhaseOf is { } required) && (guard?.Row != required)))
        ) {
            reason = "operation requires its declared phase guard";

            return false;
        }
        if (
            (actor != WorldPrincipal.World) &&
            Subjects(transform: transform).Select(selector: name => WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: name
        )).FirstOrDefault(predicate: static subject => (subject?.IsRuleWritten ?? false)) is { } verdictRow
        ) {
            reason = WorldVerdict.RefuseWrite(row: verdictRow);

            return false;
        }
        // The arena kernels know no principals, so the one authority an operation carries is decided here, at the
        // ingress that stamped the acting principal, before the operation is resolved against any store.
        if (
            (transform is StateTransform.Observe) &&
            (actor != WorldPrincipal.World)
        ) {
            reason = "observe is a world-authored operation";

            return false;
        }
        if (!RuleCompiler.TryResolveTransform(
            context: WorldFactsCompiler.Context(definition: definition),
            reason: out reason,
            resolved: out var resolved,
            transform: transform
        )) {
            return false;
        }
        if (!StateArena.TryCreate(
            arena: out var arena,
            catalog: definition.StateCatalog,
            options: WorldSlotLanes.Options(definition: definition),
            reason: out reason,
            section: definition.StateRaw,
            time: ArenaTime.At(
                engineTick: tick,
                tick: tick
            )
        )
        ) {
            return false;
        }

        var host = new WorldArenaHost(
            arena: arena,
            documentSeed: (definition.Generation?.WorldSeed ?? 0UL),
            dynamics: definition.Dynamics,
            generators: definition.Generators,
            instanceIdentity: instance,
            sites: WorldServer.DrawSitesOf(catalog: arena.Catalog),
            ticksPerSecond: definition.SimulationRateHz,
            verdicts: WorldVerdictStamp.From(
                catalog: arena.Catalog,
                definition: definition
            )
        );

        host.Advance(
            engineTick: tick,
            tick: tick
        );

        var mark = arena.BeginScope();

        try {
            // The phase sequence is the arena's own per-row column, so a chained candidate's advance is already in
            // the store this admission reads. KEEP IN SYNC with the advance below: this compose is the only reader
            // of that column and the advance is its only writer, so a second one of either goes through here too
            // rather than through the row's own Phase record.
            if (guard is { } admission) {
                if (
                    !definition.StateCatalog.TryResolve(
                    handle: out var phase,
                    lane: StateLane.Document,
                    name: admission.Row
                ) ||
                    (WorldDefinitionRows.FindStateRow(
                    rows: definition.State,
                    name: admission.Row
                )?.Phase is null) ||
                    (arena.PhaseSequence(rowOrdinal: phase.Ordinal) != admission.Sequence) ||
                    ((admission.Participant is not null) && (actor != WorldPrincipal.World))
                ) {
                    reason = "phase admission refused";

                    return false;
                }
            }
            if (!host.TryTransform(
                binding: ArenaTransformBinding.None,
                moved: out _,
                refusal: out var refusal,
                transform: resolved
            )) {
                reason = refusal.Reason;

                return false;
            }

            // A matching guard both admits and completes: advancing the phase row's generation is the guard's whole
            // job now that turn order, rounds, and readiness are ordinary rows a world's rules author. KEEP IN SYNC
            // with the admission above: it is the column's only reader and this is its only writer.
            if (guard is { } completed) {
                var phase = definition.StateCatalog.TryResolve(
                    handle: out var handle,
                    lane: StateLane.Document,
                    name: completed.Row
                );

                if (
                    !phase ||
                    !arena.TryWritePhaseSequence(
                    rowOrdinal: handle.Ordinal,
                    sequence: checked((arena.PhaseSequence(rowOrdinal: handle.Ordinal) + 1L))
                )
                ) {
                    reason = $"state row '{completed.Row}' does not carry a phase sequence";

                    return false;
                }
            }

            var exported = arena.ToRows();
            var rows = new WorldStateRow[exported.Count];

            for (var index = 0; (index < exported.Count); index++) {
                rows[index] = ((WorldStateRow)exported[index]);
            }

            candidate = definition.WithWorldState(rows: rows);
            reason = string.Empty;

            return true;
        } finally {
            arena.Rewind(mark: mark);
        }
    }

    // A vector operand spells its destination as a row name or as "<row>[<key>]"; a literal vector names no row.
    private static IEnumerable<string> SpellingSubject(string? spelling) {
        if (string.IsNullOrWhiteSpace(value: spelling)) {
            return [];
        }

        var trimmed = spelling.Trim();

        if (trimmed.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "vector("
        )) {
            return [];
        }

        var bracket = trimmed.IndexOf(value: '[');

        if (
            (bracket > 0) &&
            trimmed.EndsWith(value: ']')
        ) {
            var row = trimmed[..bracket].Trim();

            return ((row.Length == 0)
                ? []
                : new[] { row }
            );
        }

        return [trimmed];
    }
}
