
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    private bool IsStateCommand(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertStateCell or WorldMutation.RemoveStateCell or WorldMutation.TransformState => true,
        WorldMutation.Generate generate => WorldDefinitionRows.FindStateRow(m_definition.State, generate.Row) is { Field: null },
        WorldMutation.UpsertStateRow write => WorldDefinitionRows.FindStateRow(rows: m_definition.State, name: write.Row.Name.Value) is { } before && SameComposeDeclaration(row: write.Row, previous: before),
        WorldMutation.Batch batch => batch.Mutations.All(member => IsStateCommand(mutation: member)),
        _ => false,
    };

    private bool TryApplyStateCommand(WorldMutation mutation, ulong tick, ulong engineTick, int connectionId, long correlationId, bool preMetered) {
        m_lastMutationFailureDetail = null;
        if (!TryAdmitCompleteMutation(mutation: mutation, preMetered: preMetered, admission: out var admission)) {
            m_lastMutationFailureDetail = admission.Describe();
            Host.EchoTap?.Invoke(new WorldEditEcho(Message: m_lastMutationFailureDetail, Rejected: true, Denied: true, Kind: WorldEditEchoKind.Mutation, Mutation: mutation, ConnectionId: connectionId, CorrelationId: correlationId));
            return false;
        }
        var mark = Host.BeginStateTransaction();
        var time = ComposeTime(definition: m_definition, tick: tick, engineTick: engineTick);
        if (!TryStateMembers(mutation: mutation, time: in time, reason: out var reason, evicted: out var evicted)) {
            Host.RewindStateTransaction(mark: mark);
            Reject(mutation: mutation, reason: reason, connectionId: connectionId, correlationId: correlationId);
            return false;
        }
        if (!Host.TryCommitStateTransaction(mark: mark, principal: mutation.Principal, reason: out reason)) {
            Reject(mutation: mutation, reason: reason, connectionId: connectionId, correlationId: correlationId);
            return false;
        }
        Host.PublishArena();
        RecordCommittedMutation(mutation: mutation, tick: tick, engineTick: engineTick, connectionId: connectionId, correlationId: correlationId, evictedKey: evicted);
        return true;
    }

    private bool TryStateMembers(WorldMutation mutation, in ArenaTime time, out string reason, out CellName? evicted) {
        reason = string.Empty; evicted = null;
        switch (mutation) {
            case WorldMutation.Generate generate:
                if (!DisclosesStateRead(generate.Row, generate.Principal)) { reason = "draw site is not disclosed"; return false; }
                return TryGenerateState(generate, in time, out reason);
            case WorldMutation.TransformState transform:
                foreach (var subject in transform.Transform.Subjects()) {
                    if (transform.Principal == Puck.Commands.Principal.World || transform.Principal == Puck.Commands.Principal.Console) { continue; }
                    if (!DisclosesStateRead(subject.Name, transform.Principal)) { reason = "state operation is not disclosed"; return false; }
                }
                return WorldArenaTransforms.TryApply(definition: m_definition, transform: transform.Transform, actor: transform.Principal,
                    tick: time.Tick, engineTick: time.EngineTick, instance: Host.InstanceIdentity, candidate: out _, reason: out reason, guard: transform.Guard, live: Host.Arena);
            case WorldMutation.UpsertStateRow replace:
                Host.Arena.Catalog.TryResolve(lane: StateLane.Document, name: replace.Row.Name, handle: out var rowHandle);
                var ordinal = rowHandle.Ordinal;
                var keys = new List<CellName>();
                var cursor = 0;
                while (Host.Arena.TryNextCell(rowOrdinal: ordinal, cursor: ref cursor, key: out var key)) {
                    var name = Host.Arena.Keys[key];
                    if (!(replace.Row.Cells ?? []).Any(cell => cell.Key == name)) { keys.Add(name); }
                }
                foreach (var key in keys) {
                    if (!TryStateMembers(new WorldMutation.RemoveStateCell(Principal: replace.Principal, Row: replace.Row.Name.Value, Key: key.Value), in time, out reason, out evicted)) { return false; }
                }
                foreach (var cell in replace.Row.Cells ?? []) {
                    var value = cell.Value;
                    var write = new WorldMutation.UpsertStateCell(Principal: replace.Principal, Row: replace.Row.Name.Value, Key: cell.Key.Value,
                        Kind: WorldDocumentWriteKind.Set, Value: value.Kind is CellKind.Int or CellKind.Fixed or CellKind.Bool ? value.Raw : 0L,
                        Text: value.Kind == CellKind.Text ? value.AsText : null,
                        Vector: value.Kind == CellKind.Vector && StateVector.TryCreate(value.AsVector.Span, out var vector, out _) ? vector : null);
                    if (!TryStateMembers(write, in time, out reason, out evicted)) { return false; }
                    Host.Arena.Keys.TryResolve(name: cell.Key, key: out var stored);
                    if (Host.Arena.Layout.HasColumn(row: ordinal, column: ArenaColumn.Behavior)) {
                        Host.Arena.TryWriteBehavior(rowOrdinal: ordinal, key: stored, behavior: cell.Behavior);
                    }
                    if ((Host.Arena.Layout.HasColumn(row: ordinal, column: ArenaColumn.Visibility) && !Host.Arena.TryWriteVisibility(rowOrdinal: ordinal, key: stored, visibility: cell.Visibility)) ||
                        (Host.Arena.Layout.HasColumn(row: ordinal, column: ArenaColumn.Observation) && !Host.Arena.TryWriteObservation(rowOrdinal: ordinal, key: stored, observation: cell.Observation))) { reason = "cell metadata refused"; return false; }
                }
                return true;
            case WorldMutation.UpsertStateCell write:
                return TryApplyCellUpsert(arena: Host.Arena, working: m_definition, mutation: write, time: in time, reason: out reason, evictedKey: out evicted);
            case WorldMutation.RemoveStateCell remove:
                return TryApplyCellRemove(arena: Host.Arena, working: m_definition, mutation: remove, reason: out reason);
            case WorldMutation.Batch batch:
                if (!CheckStateExpectations(batch: batch, time: in time, reason: out reason)) { return false; }
                List<(WorldStateExpectation Guard, long Before)>? exact = null;
                foreach (var guard in batch.ExpectedCells ?? []) {
                    if (guard.Change is null) { continue; }
                    if (!WorldStateReader.TryRead(m_definition, guard.Row, guard.Key, time.Tick, time.EngineTick,
                        out _, out var before, out _, Host.Arena) || before is null) { reason = "an exact change requires a numeric cell"; return false; }
                    (exact ??= []).Add((guard, before.Value));
                }
                foreach (var member in batch.Mutations) {
                    if (!TryStateMembers(mutation: member, time: in time, reason: out reason, evicted: out var dropped)) { return false; }
                    evicted ??= dropped;
                }
                foreach (var (guard, before) in exact ?? []) {
                    if (!WorldStateReader.TryRead(m_definition, guard.Row, guard.Key, time.Tick, time.EngineTick,
                        out _, out var after, out _, Host.Arena) || after is null || (Int128)after.Value - before != guard.Change) {
                        reason = $"state '{guard.Row}' constraints prevent the exact required change"; return false;
                    }
                }
                return true;
            default: reason = "the command is not a state batch"; return false;
        }
    }

    private bool DisclosesStateRead(string name, Puck.Commands.Principal principal) {
        var arena = Host.Arena;
        if (!arena.Catalog.TryResolve(StateLane.Document, name, out var handle)) {
            if (m_definition.Sets.FirstOrDefault(set => set.Name.Value == name) is not { } declared) { return false; }
            m_setReadOrdinals.Clear();
            RuleCompiler.CollectCellSetReads(WorldFactsCompiler.Context(m_definition), declared, m_setReadOrdinals);
            foreach (var ordinal in m_setReadOrdinals) {
                if (!DisclosesStateRead(arena.Catalog.Descriptors[ordinal].Name, principal)) { return false; }
            }
            return true;
        }
        if (!WorldStateDisclosure.CanReadRow(arena, name, principal)) { return false; }
        var cursor = 0;
        while (arena.TryNextCell(handle.Ordinal, ref cursor, out var key)) {
            if (!WorldStateDisclosure.CanReadCell(m_definition, arena, name, arena.Keys[key], principal)) { return false; }
        }
        return true;
    }

    private bool CheckStateExpectations(WorldMutation.Batch batch, in ArenaTime time, out string reason) {
        reason = string.Empty;
        // Fingerprint guards are explicit preview/edit boundaries; ordinary state batches export nothing.
        if ((batch.ExpectedDefinition is not null) || (batch.ExpectedInputs is not null) || (batch.ExpectedSpatialReads is { Count: > 0 })) {
            var current = Host.MaterializeArena();
            if ((batch.ExpectedDefinition is { } expected) && expected != WorldDefinitionFingerprint.Compute(definition: current, stateRows: batch.ExpectedStateRows)) {
                reason = "the proposal is stale: its base definition changed; preview again"; return false;
            }
            if ((batch.ExpectedInputs is { } inputs) && inputs.Fingerprint != WorldDefinitionFingerprint.ComputeInputs(definition: current, placementIds: inputs.PlacementIds, stateRows: inputs.StateRows)) {
                reason = "the proposal is stale: its named inputs changed; preview again"; return false;
            }
            foreach (var spatial in batch.ExpectedSpatialReads ?? []) {
                if (spatial.Fingerprint != WorldDefinitionFingerprint.ComputeSpatial(definition: current, region: spatial.Region)) {
                    reason = "the proposal is stale: its spatial read changed; preview again"; return false;
                }
            }
        }
        foreach (var cell in batch.ExpectedCells ?? []) {
            if (!WorldStateReader.TryRead(definition: m_definition, arena: Host.Arena, rowName: cell.Row, key: cell.Key,
                tick: time.Tick, engineTick: time.EngineTick, row: out var row, rawValue: out var raw, text: out var text) ||
                (raw is not { } value) || ((cell.Kind is { } kind) && row.Kind != kind) || (cell.Text is not null && text != cell.Text) ||
                ((cell.Kind != CellKind.Text) && !cell.Comparison.Holds(value: FixedQ4816.FromRawBits(value), expected: FixedQ4816.FromRawBits(cell.Value)))) {
                reason = $"the proposal is stale: state '{cell.Row}' changed; preview again"; return false;
            }
        }
        return true;
    }
}
