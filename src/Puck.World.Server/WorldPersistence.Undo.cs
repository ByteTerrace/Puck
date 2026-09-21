namespace Puck.World.Server;

public sealed partial class WorldPersistence {
    // A checkpoint's undo payload is untrusted state. Validate it against a separately constructed arena before
    // adopting any document, clock, or ledger on the live host.
    private void ValidateRetainedTurns(WorldServerCheckpoint server, WorldRuleCompilation compilation) {
        var definition = compilation.Definition;
        if ((server.Undo is null) && !definition.RuleGroups.Any(group => (group.Undo is not null))) {
            return;
        }
        var time = new ArenaTime(Tick: server.LastCompletedTick, EngineTick: server.LastCompletedEngineTicks,
            TicksPerSecond: definition.SimulationRateHz, Dynamics: definition.Dynamics);

        if (!StateArena.TryCreate(catalog: definition.StateCatalog, section: definition.StateRaw,
            options: WorldSlotLanes.Options(definition: definition), time: in time, arena: out var candidate, reason: out var reason)) {
            throw new InvalidOperationException(message: $"the checkpoint's undo arena cannot be constructed: {reason}");
        }
        candidate.ConfigureUndo(compilation.Groups.Where(group => (group.Undo is not null)).Select(group => group.Undo!).ToArray());
        if (!candidate.TryRestoreKeys(names: server.ArenaKeys, reason: out reason) ||
            !candidate.ValidateUndoSnapshot((server.Undo ?? new ArenaUndoSnapshot([])), out reason)) {
            throw new InvalidOperationException(message: $"the checkpoint's retained turns do not validate: {reason}");
        }
        foreach (var group in (server.Undo?.Groups ?? [])) {
            var progress = server.RuleGroups.FirstOrDefault(predicate: item => (item.Group == group.Name));

            if ((group.Pending is not null) != progress.Running) {
                throw new InvalidOperationException(message: $"the checkpoint's pending undo turn '{group.Name}' disagrees with its rule-group progress");
            }
        }
    }
}
