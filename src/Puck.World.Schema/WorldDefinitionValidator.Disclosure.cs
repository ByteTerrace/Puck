using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateStateDisclosure(WorldDefinition definition, List<string> errors) {
        foreach (var row in definition.State) {
            if (row is null) {
                continue;
            }

            ValidateDisclosureRow(
                definition: definition,
                errors: errors,
                row: row
            );
        }
    }
    // One row's own disclosure shape: phaseOf, row/cell visibility and readersFrom, a secret draw site, each cell's
    // observation stamp, and a knowledge board's source/mask compatibility. Called once per row by the whole-
    // document walk above and by a state mutation's touched-row walk
    // (<see cref="WorldDefinitionValidator.TryValidateTouchedStateRows"/>).
    private static void ValidateDisclosureRow(WorldDefinition definition, WorldStateRow row, List<string> errors) {
        if (
            (row.PhaseOf is { } phaseOf) &&
            ((WorldDefinitionRows.FindStateRow(
            definition.State,
            phaseOf
        )?.Phase is null) || (row.Phase is not null))
        ) {
            errors.Add(item: $"state row '{row.Name}': phaseOf must name a distinct phase protocol row.");
        }

        ValidateVisibility(
            row.Visibility,
            row.Name.Value,
            errors
        );
        ValidateReadersFrom(
            definition,
            row.Visibility,
            row.Name.Value,
            errors
        );
        if (
            (row.Draw is { Secret: { } secret } draw) &&
            (secret.IsEmpty || (row.Kind != CellKind.Int) || !GeneratorEngine.TryResolveSource(
            definition.Generators,
            draw,
            out var generator,
            out _
        ) || (generator.Source != GeneratorSource.StreamDraw) || (generator.Mode != GeneratorMode.WithReplacement))
        ) {
            errors.Add(item: $"state row {row.Name}: secret requires a nonzero key and an integer streamDraw source with replacement.");
        }
        foreach (var cell in (row.Cells ?? [])) {
            if (cell is null) {
                continue;
            }

            ValidateVisibility(
                cell.Visibility,
                row.Name.Value,
                errors
            );
            ValidateReadersFrom(
                definition,
                cell.Visibility,
                row.Name.Value,
                errors
            );
            if (
                row.IsSlot &&
                (cell.Visibility is not null)
            ) {
                errors.Add(item: $"state row '{row.Name}': slot visibility belongs on the row.");
            }

            if (
                (cell.Observation is { } observation) &&
                ((row.Knowledge is null) || (observation.Tick < 0))
            ) {
                errors.Add(item: $"state row '{row.Name}': observation stamps require a knowledge board and a bounded nonnegative tick.");
            }

            if (
                (row.Knowledge is not null) &&
                (cell.Observation is null)
            ) {
                errors.Add(item: $"state row '{row.Name}': every remembered cell requires an observation stamp.");
            }
        }
        if (row.Knowledge is not { } knowledge) {
            return;
        }

        var source = WorldDefinitionRows.FindStateRow(
            definition.State,
            knowledge.Source
        );
        var mask = WorldDefinitionRows.FindStateRow(
            definition.State,
            knowledge.Mask
        );
        const string KnowledgeShape = "knowledge requires an explicit audience and either compatible source/mask boards or compatible token-keyed source/positions rows plus a boolean mask board.";

        if (
            (row.Visibility is null) ||
            (source is null) ||
            (mask?.EffectiveDomain is not StateDomain.CellsOf maskBoard) ||
            (source.Knowledge is not null) ||
            (mask.Knowledge is not null) ||
            (source.Kind != row.Kind) ||
            (mask.Kind != CellKind.Bool) ||
            (row.Name.Value == knowledge.Source) ||
            (row.Name.Value == knowledge.Mask) ||
            (row.Min != source.Min) ||
            (row.Max != source.Max) ||
            (row.Overflow != source.Overflow)
        ) {
            errors.Add(item: $"state row '{row.Name}': {KnowledgeShape}");
            return;
        }
        var valid = false;

        if (knowledge.Positions is null) {
            valid = (
                (row.EffectiveDomain is StateDomain.CellsOf rowBoard) &&
                (source.EffectiveDomain is StateDomain.CellsOf sourceBoard) &&
                (sourceBoard.Topology == rowBoard.Topology) &&
                (maskBoard.Topology == rowBoard.Topology)
            );
        } else if (knowledge.Positions is { } positionName) {
            var positions = WorldDefinitionRows.FindStateRow(definition.State, positionName);

            valid = (
                (row.EffectiveDomain is StateDomain.KeysOf rowKeys) &&
                (source.EffectiveDomain is StateDomain.KeysOf sourceKeys) &&
                (positions?.EffectiveDomain is StateDomain.KeysOf positionKeys) &&
                (positions.Knowledge is null) &&
                (positions.Kind == CellKind.Int) &&
                (row.Name.Value != positionName) &&
                (sourceKeys.Row == rowKeys.Row) &&
                (positionKeys.Row == rowKeys.Row)
            );
        }
        if (!valid) {
            errors.Add(item: $"state row '{row.Name}': {KnowledgeShape}");
        }
    }
    // The live audience row is keyed text: each cell's text is one canonical token, and a rule rewrites it. Every
    // recipient reads it whole (no row, cell or containing-zone policy can withhold a member), since who it admits
    // is otherwise a function of cells some recipient cannot see. WorldStateDisclosure.Allows admits no one through
    // an audience that is not, so this refusal names a shape the runtime would silently close.
    private static void ValidateReadersFrom(WorldDefinition definition, StateVisibility? visibility, string name, List<string> errors) {
        if (visibility?.ReadersFrom is not { } source) {
            return;
        }
        var row = WorldDefinitionRows.FindStateRow(
            definition.State,
            source
        );

        if (
            (row is null) ||
            (row.Kind != CellKind.Text) ||
            !row.IsKeyed ||
            (row.Visibility is not null) ||
            ArenaLayout.DeclaresCellVisibility(row: row) ||
            RestrictedByZone(definition: definition, row: row) ||
            (row.Name.Value == name)
        ) {
            errors.Add(item: $"state row '{name}': visibility.readersFrom must name a distinct keyed text row every recipient reads whole (no row, cell or containing-zone visibility).");
        }
    }
    // Whether a row's declarations let it withhold a member from some principal: a non-public policy of its own, a
    // cell policy its storage admits, or a containing ordered zone's. The declaration-only answer, for every recipient
    // at once, of the question WorldStateDisclosure.Observer.ReadsWhole answers for one.
    private static bool CanWithhold(WorldDefinition definition, WorldStateRow row) => (
        ((row.Visibility is { } policy) && !policy.IsPublic) ||
        ArenaLayout.DeclaresCellVisibility(row: row) ||
        RestrictedByZone(definition: definition, row: row)
    );
    // The dealt or variant row through which a deal template can withhold from some principal, or null when neither can.
    private static string? WithholdingRow(WorldDefinition definition, WorldPlacement template) => (template.Deal is not { } deal)
        ? null
        : new[] { deal.Row, deal.Variants?.Row }
            .FirstOrDefault(name => (name is not null) && (WorldDefinitionRows.FindStateRow(definition.State, name) is { } row) && CanWithhold(definition, row));
    // The deal template whose dealt child lies on placement's parent chain (placement itself included), or null. Frame
    // validation admits a distributed parent only as a template over its own dealt children, so this is the one
    // template whose hidden slots a placement's frame can follow. The walk is bounded by the row count, so a parent
    // cycle, which frame validation refuses by name, ends it.
    private static WorldPlacement? DealtAncestor(WorldPlacement placement, IReadOnlyDictionary<string, WorldPlacement> placementsById) {
        var current = placement;

        for (var steps = 0; (steps <= placementsById.Count) && (current.Parent is { } parentId) && placementsById.TryGetValue(key: parentId, value: out var parent); steps++) {
            if (WorldPlacementDeal.IsChild(parent: parent, placement: current)) { return parent; }
            current = parent;
        }
        return null;
    }
    // A deal template whose dealt or variant row can withhold is re-dealt for every reader it withholds from, so the
    // template, its children and every placement authored under them carry only what that re-deal discloses
    // (WorldPlacementDeal.UndisclosingMembers names the rest), and the template owns its children's transforms: a
    // preserved one would keep a position the authority's slot shaped, which no re-deal can separate from the deal's
    // own offset. A child is checked as well as the template, since an edit between sweeps can give it a member.
    private static void RefuseUndisclosingMembers(WorldDefinition definition, WorldPlacement template, WorldPlacement placement, string path, List<string> errors) {
        if (WithholdingRow(definition: definition, template: template) is not { } withholding) { return; }
        var isTemplate = ReferenceEquals(objA: placement, objB: template);
        var members = WorldPlacementDeal.UndisclosingMembers(placement: placement, template: isTemplate).ToArray();

        if (members.Length > 0) {
            errors.Add(item: $"{path} carries {string.Join(separator: ", ", value: members)}, but it stands under deal template '{template.Id}', which deals from '{withholding}', a row that can withhold cells: a reader the row withholds from is shown each card re-dealt, so whatever acts or shows at the authority's own slot would tell what the reader is not shown; remove {string.Join(separator: " and ", value: members)}.");
        }
        if (isTemplate && (template.Deal!.Preserve?.Transform == true)) {
            errors.Add(item: $"{path}.deal.preserve.transform is refused: deal template '{template.Id}' deals from '{withholding}', a row that can withhold cells, so each child stands where the deal stamps its re-dealt slot, never at a position the authority's slot shaped.");
        }
    }
    // Mirrors StateCatalog's ordered-zone containment: a zone keyed over the row's own key domain (or over the row).
    private static bool RestrictedByZone(WorldDefinition definition, WorldStateRow row) {
        var domain = (row.EffectiveDomain is StateDomain.KeysOf keys) ? keys.Row : row.Name;

        foreach (var zone in definition.State) {
            if ((zone?.EffectiveDomain is StateDomain.KeysOf { Ordered: true } ordered) && (ordered.Row == domain) &&
                (((zone.Visibility is { } policy) && !policy.IsPublic) || ArenaLayout.DeclaresCellVisibility(row: zone))) {
                return true;
            }
        }
        return false;
    }
    private static void ValidateVisibility(StateVisibility? visibility, string name, List<string> errors) {
        if (visibility?.Readers is not { } readers) {
            return;
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (readers.Count > StateCapacity.MaxVisibilityReaders) {
            errors.Add(item: $"state row '{name}': visibility admits at most {StateCapacity.MaxVisibilityReaders} readers.");
        }

        foreach (var reader in readers) {
            if (
                !PrincipalTokens.TryParseCanonical(
                principal: out var actor,
                token: reader
            ) ||
                (actor.Kind is PrincipalKind.World or PrincipalKind.Session) ||
                !seen.Add(item: reader)
            ) {
                errors.Add(item: $"state row '{name}': visibility readers must be distinct canonical authenticated principals.");
            }
        }
    }
}
