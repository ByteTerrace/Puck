using Puck.Commands;

namespace Puck.World.Server;

/// <summary>Composes one submitted state operation through the arena kernels a rule firing fires through. The
/// operation is resolved to ordinals and interned keys once, applied inside a journal scope, and read back as the
/// candidate document; the scope is always rewound, so a caller's store is exactly as it was and the ordinary
/// mutation pipeline installs the candidate.</summary>
public static class WorldArenaTransforms {
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
    public static bool TryApply(WorldDefinition definition, StateTransform transform, Principal actor, ulong tick, string instance, out WorldDefinition candidate, out string reason, PhaseGuard? guard = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: transform);

        candidate = definition;
        // A row declaring a phase admits an outside operation only under that phase's own guard; the world's own
        // rules write it unguarded.
        if (
            (actor != Principal.World) &&
            transform.Subjects().Any(predicate: subject =>
            ((subject.Access == StateAccess.Write) &&
            ((WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: subject.Name
        )?.PhaseOf is { } required) && (guard?.Row != required))))
        ) {
            reason = "operation requires its declared phase guard";

            return false;
        }
        if (
            (actor != Principal.World) &&
            (transform.Subjects().Where(predicate: static subject => (subject.Access == StateAccess.Write)).Select(selector: subject => WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: subject.Name
        )).FirstOrDefault(predicate: static subject => (subject?.IsRuleWritten ?? false)) is { } verdictRow)
        ) {
            reason = WorldVerdict.RefuseWrite(row: verdictRow);

            return false;
        }
        // The arena kernels know no principals, so the one authority an operation carries is decided here, at the
        // ingress that stamped the acting principal, before the operation is resolved against any store.
        if (
            (transform is StateTransform.Observe) &&
            (actor != Principal.World)
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
            sites: WorldDrawSites.Of(catalog: arena.Catalog),
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
                    ((admission.Participant is not null) && (actor != Principal.World))
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

            candidate = definition.WithWorldState(rows: rows, pools: ((definition.StateRaw?.Pools is { Count: > 0 }) ? arena.ToPools() : null), pairPools: ((definition.StateRaw?.PairPools is { Count: > 0 }) ? arena.ToPairPools() : null));
            reason = string.Empty;

            return true;
        } finally {
            arena.Rewind(mark: mark);
        }
    }
}
