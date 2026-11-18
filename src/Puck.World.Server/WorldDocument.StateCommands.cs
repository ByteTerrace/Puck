
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    private bool IsStateCommand(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertStateCell or WorldMutation.RemoveStateCell or WorldMutation.TransformState => true,
        WorldMutation.Generate generate => (WorldDefinitionRows.FindStateRow(m_definition.State, generate.Row) is { Field: null }),
        WorldMutation.UpsertStateRow write => write.Row.Draw is not { Secret: not null } and not { PrivateStream: true } &&
            WorldDefinitionRows.FindStateRow(rows: m_definition.State, name: write.Row.Name.Value) is { } before && SameComposeDeclaration(row: write.Row, previous: before),
        WorldMutation.Batch batch => batch.Mutations.All(predicate: member => IsStateCommand(mutation: member)),
        _ => false,
    };
    private bool TryStateMembers(StateReadAccess reads, WorldMutation mutation, in ArenaTime time, out string reason, out CellName? evicted) {
        reason = string.Empty; evicted = null;
        if (!Host.Grants.IsOperator(mutation.Principal)) {
            var view = Host.WriteView(mutation.Principal);
            if (!view.Admits(mutation, out var withheld)) {
                m_lastMutationFailureCode = WorldRecipientMutations.NotDisclosedCode;
                reason = view.Refusal("mutation", withheld);
                return false;
            }
        }
        switch (mutation) {
            case WorldMutation.Generate generate:
                return TryGenerateState(mutation: generate, reason: out reason, time: in time);
            case WorldMutation.TransformState transform:
                return WorldArenaTransforms.TryApply(definition: m_definition, transform: transform.Transform, actor: transform.Principal,
                    tick: time.Tick, engineTick: time.EngineTick, instance: Host.InstanceIdentity, candidate: out _, reason: out reason, guard: transform.Guard, live: Host.Arena, sampler: Host.DrawKeys);
            case WorldMutation.UpsertStateRow replace:
                var existing = WorldDefinitionRows.FindStateRow(rows: m_definition.State, name: replace.Row.Name.Value);
                if (StateRows.IsGeneratedRowName(name: replace.Row.Name.Value, rows: m_definition.State)) {
                    reason = $"state row '{replace.Row.Name}' is generated storage; mutate its owning declaration or use a typed instance effect";
                    return false;
                }
                if (existing is { Inverse: { } existingInverse }) {
                    reason = $"state row '{replace.Row.Name}' is a derived board (inverse names '{existingInverse.Tokens}'/'{existingInverse.Codes}') — write those rows instead; the engine recomputes '{replace.Row.Name}' on install";
                    return false;
                }
                if (existing is { IsRuleWritten: true }) {
                    reason = WorldVerdict.RefuseWrite(row: existing);
                    return false;
                }
                return Host.Arena.TryLoad(rows: [replace.Row], time: in time, reason: out reason);
            case WorldMutation.UpsertStateCell write:
                return TryApplyCellUpsert(arena: Host.Arena, working: m_definition, mutation: write, time: in time, reason: out reason, evictedKey: out evicted);
            case WorldMutation.RemoveStateCell remove:
                return TryApplyCellRemove(arena: Host.Arena, working: m_definition, mutation: remove, reason: out reason);
            case WorldMutation.Batch batch:
                if (!CheckStateExpectations(batch: batch, reason: out reason, time: in time)) { return false; }
                List<(WorldStateExpectation Guard, long Before)>? exact = null;
                foreach (var guard in (batch.ExpectedCells ?? [])) {
                    if (guard.Change is null) { continue; }
                    if (!TryReadChangeOperand(reads, guard, in time, out var before)) { reason = "an exact change requires a numeric cell"; return false; }
                    (exact ??= []).Add(item: (guard, before));
                }
                foreach (var member in batch.Mutations) {
                    if (!TryStateMembers(reads, evicted: out var dropped, mutation: member, reason: out reason, time: in time)) { return false; }
                    evicted ??= dropped;
                }
                foreach (var (guard, before) in (exact ?? [])) {
                    if (!TryReadChangeOperand(reads, guard, in time, out var after) || ((((Int128)after) - before) != guard.Change)) {
                        reason = $"state '{guard.Row}' constraints prevent the exact required change"; return false;
                    }
                }
                return true;
            default: reason = "the command is not a state batch"; return false;
        }
    }
    private static bool TryReadChangeOperand(StateReadAccess reads, WorldStateExpectation guard, in ArenaTime time, out long value) {
        value = default;
        if (!reads.TryResolveRow(guard.Row, out var row)) { return false; }
        StateReader.ReadCell(reads, row, guard.Key, time.Tick, time.EngineTick, out var raw, out _);
        if (raw is not { } number) { return false; }
        value = number;
        return true;
    }

    private bool CheckStateExpectations(WorldMutation.Batch batch, in ArenaTime time, out string reason) =>
        Host.WriteView(batch.Principal).CheckGuards(batch, out reason);
}
