using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // A run of independent ordinary cell upserts can share composition, validation and installation. Restrict
    // this to rows whose values have no cross-row interpretation: the arena admits every operand in order,
    // and final row validation then proves exactly the same constraints as validating each intermediate row.
    // Any failed prepare falls back to the original ordered acts, retaining partial success and refusal details.
    internal bool TryApplyCellBurst(IReadOnlyList<WorldPendingOp.Mutate> ops, ulong tick, ulong engineTick) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var members = new WorldMutation[ops.Count];

        for (var index = 0; (index < ops.Count); index++) {
            if (ops[index].Mutation is not WorldMutation.UpsertStateCell cell) { return false; }
            // Length is a document-validator decision. An intermediate oversized text must reach that original
            // decision even when a later member would overwrite it before combined final validation.
            if ((cell.Text is { Length: > StateCapacity.MaxTextValueLength }) ||
                (cell.CycleTokens?.Any(predicate: static token => (token.Length > StateCapacity.MaxTextValueLength)) == true)) { return false; }
            var row = WorldDefinitionRows.FindStateRow(m_definition.State, cell.Row);

            if ((row is null) || row.HostOwned || row.IsRuleWritten || row.Generated || row.GatesDrive || row.Evicts ||
                (row.Kind is not (CellKind.Int or CellKind.Fixed or CellKind.Bool or CellKind.Text)) ||
                (row.Domain is not null) || (row.ValuesFrom is not null) || (row.Enum is not null) || (row.Draw is not null) ||
                (row.Advance is not null) || (row.Cycle is not null) || (row.Dynamics is not null) || (row.Inverse is not null) ||
                (row.Phase is not null) || (row.PhaseOf is not null) || (row.Knowledge is not null) ||
                (row.Field is not null) || (row.Verdict is not null) || (row.Witness is not null)) { return false; }
            foreach (var existing in (row.Cells ?? [])) {
                if ((existing.Advance is not null) || (existing.Dynamics is not null) || (existing.Cycle is not null)) { return false; }
            }
            names.Add(item: cell.Row);
            members[index] = cell;
        }
        foreach (var row in m_definition.State) {
            if ((row.EffectiveDomain is StateDomain.KeysOf keys) && names.Contains(item: keys.Row.Value)) { return false; }
            if ((row.Inverse is { } inverse) && (names.Contains(item: inverse.Tokens) || names.Contains(item: inverse.Codes))) { return false; }
        }
        // Reconciliation of a cell-driven inhabit count has lifecycle effects between installs. A pulse that
        // admits and retires bodies cannot be collapsed to its final value.
        foreach (var placement in m_definition.Placements) {
            if ((placement.Inhabit?.Count?.Row is { } countRow) && names.Contains(item: countRow)) { return false; }
        }
        var references = WorldStateDocumentValues.ReferencedRows(definition: m_definition);

        if (names.Overlaps(other: references)) { return false; }

        var batch = new WorldMutation.Batch(members[0].Principal, members);

        if (!TryPrepareMutation(current: m_definition, denied: out _, engineTick: engineTick, mutation: batch, preMetered: true, prepared: out var prepared, reason: out _, tick: tick)) { return false; }
        InstallPrepared(prepared, ops[0].ConnectionId, ops[0].CorrelationId, record: false);
        foreach (var op in ops) {
            RecordPrepared(prepared, op.ConnectionId, op.CorrelationId, op.Mutation);
        }
        return true;
    }
}
