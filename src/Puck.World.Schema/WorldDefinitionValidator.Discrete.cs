namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateDiscreteState(WorldDefinition definition, List<string> errors) {
        ValidateTokenAndPhaseRows(
            definition: definition,
            errors: errors
        );
        ValidateStateDisclosure(
            definition: definition,
            errors: errors
        );
        var topologies = (definition.StateRaw?.Lattices ?? []);

        if (topologies.Count > TopologyCompilation.MaxTopologies) {
            errors.Add(item: $"state.lattices exceeds {TopologyCompilation.MaxTopologies} topologies.");
        }
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var topology in topologies) {
            if (
                (topology is null) ||
                !CellName.TryParse(
                candidate: topology.Name,
                name: out _,
                reason: out _
            ) ||
                !names.Add(item: topology.Name)
            ) {
                errors.Add(item: "state.lattices requires unique valid topology names.");
                continue;
            }
            // A physical field's case type structurally has no wrap/radius/directions/elementAliases property to
            // author in the first place, so there is nothing left to check for it here.
            if (
                (topology.Kind != TopologyKind.Field) &&
                !TopologyCompilation.TryValidate(
                reason: out var reason,
                topology: topology
            )
            ) {
                errors.Add(item: $"state.lattices '{topology.Name}': {reason}.");
            }
        }
        var totalCells = 0L;
        var derivation = new BoardDerivation(definition: definition);

        foreach (var row in (definition.State ?? [])) {
            if (
                (row is null) ||
                (row.EffectiveDomain is not StateDomain.CellsOf board) ||
                (row.Field is not null)
            ) {
                continue;
            }
            if (ValidateBoardRow(
                board: board,
                definition: definition,
                derivation: derivation,
                errors: errors,
                row: row
            ) is { } compiled) {
                totalCells += compiled.CellCount;
            }
        }
        if (totalCells > TopologyCompilation.MaxTotalCells) {
            errors.Add(item: $"state board storage exceeds the {TopologyCompilation.MaxTotalCells}-cell world budget.");
        }
    }
    // One cellsOf board row's own shape (plain int/bool cells, no other storage/time trait, a value domain that
    // includes board.empty), its topology's cells, and — when it derives from tokens/codes — its derivation. Called
    // once per board row by the whole-document walk above and by a state mutation's touched-row walk
    // (<see cref="WorldDefinitionValidator.TryValidateTouchedStateRows"/>). Returns the resolved topology so the
    // whole-document walk can total its cells against the world storage budget; null when the row names no valid
    // topology (already refused by name).
    private static CompiledTopology? ValidateBoardRow(WorldDefinition definition, WorldStateRow row, StateDomain.CellsOf board, BoardDerivation derivation, List<string> errors) {
        if (
            (row.Kind is not (CellKind.Int or CellKind.Bool)) ||
            (row.Draw is not null) ||
            (row.Advance is not null) ||
            (row.Cycle is not null) ||
            (row.Dynamics is not null) ||
            row.Evicts ||
            row.GatesDrive
        ) {
            errors.Add(item: $"state row '{row.Name}': board requires plain integer or boolean cells without other storage or time traits.");
        }
        if (
            (row.ClampToEnvelope(value: board.Empty) != board.Empty) ||
            ((row.Kind == CellKind.Bool) && (board.Empty is not (0 or 1)))
        ) {
            errors.Add(item: $"state row '{row.Name}': board.empty is outside the value domain.");
        }
        var compiled = WorldTopologyCompilation.Find(
            definition: definition,
            name: board.Topology
        );

        if (compiled is null) {
            errors.Add(item: $"state row '{row.Name}': domain.topology '{board.Topology}' names no valid discrete topology.");
            return null;
        }
        if (
            (row.Capacity is { } capacity) &&
            (capacity != compiled.CellCount)
        ) {
            errors.Add(item: $"state row '{row.Name}': a board's capacity must equal its topology's {compiled.CellCount} cells or be omitted.");
        }
        foreach (var cell in (row.Cells ?? [])) {
            if (
                (cell is null) ||
                !compiled.TryCell(
                cell.Key.Value,
                out _
            ) ||
                (cell.Advance is not null) ||
                (cell.Cycle is not null) ||
                (cell.Dynamics is not null)
            ) {
                errors.Add(item: $"state row '{row.Name}': board cells require canonical topology keys and literal values.");
            }
        }
        if (row.Inverse is { } inverse) {
            ValidateDerivedBoard(
                definition: definition,
                derivation: derivation,
                errors: errors,
                inverse: inverse,
                row: row
            );
        }
        return compiled;
    }
    // A derived board's tokens/codes both resolve to keyed integer rows, codes carries exactly the tokens row's own
    // keys in the same order (so the two rows' cell lists correspond by index), and the board's own authored cells
    // (if any) are exactly what the derivation would produce today — an authored mismatch is refused rather than
    // silently overwritten, since the mutation door and the live compose step both refuse a direct write to a
    // derived board's cells and this is the door a hand-authored or foreign document passes through instead.
    private static void ValidateDerivedBoard(WorldDefinition definition, WorldStateRow row, StateInverse inverse, BoardDerivation derivation, List<string> errors) {
        var tokens = WorldDefinitionRows.FindStateRow(
            definition.State,
            inverse.Tokens.Value
        );
        var codes = WorldDefinitionRows.FindStateRow(
            definition.State,
            inverse.Codes.Value
        );

        if (
            (tokens is null) ||
            (tokens.Kind != CellKind.Int) ||
            (tokens.EffectiveDomain is StateDomain.Slot)
        ) {
            errors.Add(item: $"state row '{row.Name}': inverse.tokens '{inverse.Tokens}' names no keyed integer row.");
        }
        if (
            (codes is null) ||
            (codes.Kind != CellKind.Int)
        ) {
            errors.Add(item: $"state row '{row.Name}': inverse.codes '{inverse.Codes}' names no integer row.");
        }
        if (
            (tokens is null) ||
            (codes is null)
        ) {
            return;
        }

        var tokenCells = (tokens.Cells ?? []);
        var codeCells = (codes.Cells ?? []);
        var sameShape = (tokenCells.Count == codeCells.Count);

        for (var index = 0; (sameShape && (index < tokenCells.Count)); index++) {
            sameShape = (tokenCells[index]?.Key == codeCells[index]?.Key);
        }
        if (!sameShape) {
            errors.Add(item: $"state row '{row.Name}': inverse.codes '{inverse.Codes}' must carry the same keys, in the same order, as inverse.tokens '{inverse.Tokens}'.");

            return;
        }

        var authored = (row.Cells ?? []);

        if (authored.Count == 0) {
            return;
        }

        if (derivation.Cells(name: row.Name) is not { } derived) {
            errors.Add(item: $"state row '{row.Name}': its inverse's derivation could not be computed, so its authored cells cannot be checked — {derivation.Reason}");

            return;
        }

        if (!MatchesDerivation(
            authored: authored,
            derived: derived
        )) {
            errors.Add(item: $"state row '{row.Name}': authored cells must be empty or match its inverse's derivation — the board is never authored, only derived.");
        }
    }
    // The arena's own recompute, read back as the derivation an authored board must match: loading a section is
    // what recomputes every derived board from its tokens and codes rows, so the walk checks against the store
    // rather than against a second reading of the same rule. One arena serves a whole validation pass, and is built
    // on the first board that actually carries authored cells.
    private sealed class BoardDerivation(WorldDefinition definition) {
        private IReadOnlyList<StateRow>? m_rows;
        private bool m_loaded;
        private string m_reason = string.Empty;

        // Why the arena the derivation reads could not be built, or empty when it was. A derivation that answers no
        // cells is a validation line naming this, never a silently skipped check.
        public string Reason => m_reason;

        public IReadOnlyList<StateCell>? Cells(CellName name) {
            if (!m_loaded) {
                m_loaded = true;

                try {
                    // The participant and identity lanes come from the same function the server's own arena is laid
                    // out by, so the derivation is read off the store the server would have built.
                    if (StateArena.TryCreate(
                        arena: out var arena,
                        catalog: definition.StateCatalog,
                        options: WorldSlotLanes.Options(definition: definition),
                        reason: out var reason,
                        section: definition.StateRaw,
                        time: ArenaTime.Origin
                    )) {
                        m_rows = arena.ToRows();
                    } else {
                        m_reason = reason;
                    }
                } catch (InvalidOperationException exception) {
                    m_reason = exception.Message;
                }
            }

            return ((m_rows is null)
                ? null
                : (StateRows.FindStateRow(
                    rows: m_rows,
                    name: name.Value
                )?.Cells ?? [])
            );
        }
    }
    // Set comparison, not order-sensitive: an authored board's cell order is whatever the author or a prior save
    // wrote, while the derivation always walks cell order — the same board, spelled either way, must match.
    private static bool MatchesDerivation(IReadOnlyList<StateCell> authored, IReadOnlyList<StateCell> derived) {
        if (authored.Count != derived.Count) {
            return false;
        }

        var byKey = new Dictionary<CellName, long>(capacity: derived.Count);

        foreach (var cell in derived) {
            byKey[cell.Key] = cell.Value.Raw;
        }

        foreach (var cell in authored) {
            if (
                (cell is null) ||
                !byKey.TryGetValue(
                key: cell.Key,
                value: out var value
            ) ||
                (value != cell.Value.Raw)
            ) {
                return false;
            }
        }

        return true;
    }
    private static void ValidateTokenAndPhaseRows(WorldDefinition definition, List<string> errors) {
        foreach (var row in (definition.State ?? [])) {
            if (row is null) {
                continue;
            }
            ValidateTokenAndPhaseRow(
                definition: definition,
                errors: errors,
                row: row
            );
        }
    }
    // One row's own discrete-domain shape: the mutual exclusivity of the discrete storage traits, an inverse
    // board's own shape, a keysOf zone's domain membership, valuesFrom's topology positions, an ordered zone's
    // boolean-true membership cells, and the row's phase. Called once per row by the whole-document walk above and
    // by a state mutation's touched-row walk (<see cref="WorldDefinitionValidator.TryValidateTouchedStateRows"/>).
    private static void ValidateTokenAndPhaseRow(WorldDefinition definition, WorldStateRow row, List<string> errors) {
        // A physical-field row (CellsOf domain + a Field trait) legitimately carries a draw fill inside its own
        // paint (validated separately in WorldDefinitionValidator.State.cs); every OTHER discrete domain (KeysOf,
        // or CellsOf with no Field trait — a plain board) admits none of these continuous or draw traits.
        var domainTraits = (((row.Domain is StateDomain.CellsOf or StateDomain.KeysOf)
            ? 1
            : 0) + ((row.Phase is null)
            ? 0
            : 1));
        var isPhysicalField = ((row.Domain is StateDomain.CellsOf) && (row.Field is not null));

        if (
            (domainTraits > 1) ||
            ((domainTraits > 0) && !isPhysicalField && ((row.Field is not null) || (row.Draw is not null) || (row.Advance is not null) || (row.Dynamics is not null) || (row.Cycle is not null) || row.Evicts || row.GatesDrive))
        ) {
            errors.Add(item: $"state row '{row.Name}': discrete storage traits are mutually exclusive and cannot carry continuous or draw traits.");
        }
        if (
            (row.Inverse is not null) &&
            ((row.EffectiveDomain is not StateDomain.CellsOf) || (row.Field is not null) || (row.Kind != CellKind.Int))
        ) {
            errors.Add(item: $"state row '{row.Name}': inverse requires an integer board — a cellsOf domain over int cells with no field trait.");
        }
        var domainName = ((row.EffectiveDomain is StateDomain.KeysOf keysOf)
            ? keysOf.Row.Value
            : null
        );

        if (row.ValuesFrom is { } topologyName) {
            var topology = WorldTopologyCompilation.Find(
                definition: definition,
                name: topologyName
            );

            if (
                (domainName is null) ||
                (row.Kind != CellKind.Int) ||
                (topology is null) ||
                (row.Cells ?? []).Any(predicate: c => ((c is not null) && (((ulong)c.Value.Raw) >= ((ulong)topology.CellCount))))
            ) {
                errors.Add(item: $"state row '{row.Name}': valuesFrom requires token-keyed integer positions inside a discrete topology.");
            }
        }
        if (domainName is not null) {
            var domain = WorldDefinitionRows.FindStateRow(
                definition.State,
                domainName
            );

            if (
                (domain is null) ||
                (domain.EffectiveDomain is not StateDomain.Keys)
            ) {
                errors.Add(item: $"state row '{row.Name}': '{domainName}' names no token domain.");
            } else {
                var keys = new HashSet<CellName>(collection: (domain.Cells ?? []).Where(predicate: c => (c is not null)).Select(selector: c => c.Key));

                foreach (var cell in (row.Cells ?? [])) {
                    if (
                        (cell is not null) &&
                        !keys.Contains(item: cell.Key)
                    ) {
                        errors.Add(item: $"state row '{row.Name}': key '{cell.Key}' is outside token domain '{domainName}'.");
                    }
                }
            }
        }
        if (
            (row.EffectiveDomain is StateDomain.KeysOf { Ordered: true }) &&
            ((row.Kind != CellKind.Bool) || (row.Cells ?? []).Any(predicate: c => ((c is not null) && (c.Value.Raw != 1))))
        ) {
            errors.Add(item: $"state row '{row.Name}': an ordered keysOf (pile/zone) row contains boolean membership cells whose value is true.");
        }
        if (row.Phase is { } phase) {
            ValidatePhase(
                errors: errors,
                phase: phase,
                row: row
            );
        }
    }
    private static void ValidatePhase(WorldStateRow row, StatePhase phase, List<string> errors) {
        if (
            (row.Kind != CellKind.Int) ||
            (row.EffectiveDomain is StateDomain.Slot) ||
            (row.Cells is { Count: > 0 }) ||
            (row.Capacity is not null) ||
            (phase.Sequence < 0)
        ) {
            errors.Add(item: $"state row '{row.Name}': phase requires an integer row without cells/capacity and a nonnegative sequence.");
        }
    }
}
