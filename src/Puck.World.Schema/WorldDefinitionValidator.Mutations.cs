namespace Puck.World;

public static partial class WorldDefinitionValidator {
    /// <summary>Validates every row a state transform names on its own terms — the same per-row and cross-row
    /// checks the whole-document walk applies to those rows today, with no rule, interaction, pattern, table,
    /// search plan, or flock affinity compiled. The containing definition has already composed the mutation; this
    /// checks only what that mutation could have changed. A row keyed over one of the named rows (a <c>keysOf</c>
    /// zone whose domain is a row this call was given) is queued alongside it: a domain row losing a key leaves such
    /// a zone holding a key outside its own domain, a shape only a walk over the zone's own cells — never the
    /// domain's — catches (see <see cref="ValidateTokenAndPhaseRow"/>'s domain-membership check).</summary>
    /// <param name="definition">The composed candidate document.</param>
    /// <param name="rowNames">The row names the mutation touched; duplicates are checked once.</param>
    /// <param name="reason">The first refusal, or empty on success.</param>
    public static bool TryValidateTouchedStateRows(WorldDefinition definition, IEnumerable<string> rowNames, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: rowNames);

        reason = string.Empty;

        var dynamicsNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var dynamics in (definition.DynamicsRaw ?? [])) {
            if (dynamics?.Name is { } dynamicsName) {
                dynamicsNames.Add(item: dynamicsName);
            }
        }

        var errors = new List<string>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var rowName in rowNames) {
            if (seen.Add(item: rowName)) {
                queue.Enqueue(item: rowName);
            }
        }

        var catalog = definition.StateCatalog;
        var rows = definition.State;
        var spacesByName = definition.Spaces.Where(predicate: static s => (s is not null)).ToDictionary(keySelector: static s => s.Name.Value, elementSelector: static s => s, comparer: StringComparer.Ordinal);
        var enumsByName = definition.Enums.Where(predicate: static e => (e is not null)).ToDictionary(keySelector: static e => e.Name.Value, elementSelector: static e => e, comparer: StringComparer.Ordinal);
        Dictionary<string, List<WorldStateRow>>? dependentsByRow = null;
        BoardDerivation? derivation = null;

        while (queue.Count > 0) {
            var rowName = queue.Dequeue();

            if (
                !catalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: rowName
            ) ||
                !catalog.TryGetDescriptor(
                descriptor: out var descriptor,
                handle: handle
            ) ||
                (((uint)descriptor.LaneOrdinal) >= ((uint)rows.Count)) ||
                (rows[descriptor.LaneOrdinal] is not { } row)
            ) {
                errors.Add(item: $"state row '{rowName}' does not exist in the composed candidate.");

                continue;
            }

            var path = $"state['{row.Name}']";

            ValidateStateRow(
                dynamicsNames: dynamicsNames,
                enums: enumsByName,
                errors: errors,
                generators: definition.Generators,
                path: path,
                row: row,
                spaces: spacesByName
            );
            ValidateTokenAndPhaseRow(
                definition: definition,
                errors: errors,
                row: row
            );
            ValidateDisclosureRow(
                definition: definition,
                errors: errors,
                row: row
            );
            ValidateVerdictRow(
                errors: errors,
                row: row
            );
            ValidateWitnessRow(
                definition: definition,
                errors: errors,
                row: row
            );

            if (
                (row.EffectiveDomain is StateDomain.CellsOf board) &&
                (row.Field is null)
            ) {
                ValidateBoardRow(
                    board: board,
                    definition: definition,
                    derivation: (derivation ??= new BoardDerivation(definition: definition)),
                    errors: errors,
                    row: row
                );
            }

            if (dependentsByRow is null) {
                // Missing row names must not trigger unrelated domain inference before a row is validated.
                // Lists retain authored order; dictionary enumeration never determines validation order.
                dependentsByRow = new Dictionary<string, List<WorldStateRow>>(comparer: StringComparer.Ordinal);
                foreach (var dependent in (definition.State ?? [])) {
                    if (
                        (dependent is null) ||
                        (dependent.EffectiveDomain is not StateDomain.KeysOf keysOf) ||
                        (keysOf.Row.Value is not { } source)
                    ) {
                        continue;
                    }
                    if (!dependentsByRow.TryGetValue(
                        key: source,
                        value: out var dependents
                    )) {
                        dependents = [];
                        dependentsByRow[key: source] = dependents;
                    }
                    dependents.Add(item: dependent);
                }
            }

            if (dependentsByRow.TryGetValue(
                key: rowName,
                value: out var matchingDependents
            )) {
                foreach (var dependent in matchingDependents) {
                    if (seen.Add(item: dependent.Name.Value)) {
                        queue.Enqueue(item: dependent.Name.Value);
                    }
                }
            }
        }

        if (errors.Count > 0) {
            reason = errors[0];

            return false;
        }

        return true;
    }
    /// <summary>Adds every row name a <see cref="StateTransform"/> reads or writes to <paramref name="names"/> —
    /// the touched-row set <see cref="TryValidateTouchedStateRows"/> needs to check the same invariants the
    /// whole-document walk would, without compiling the rest of the document.</summary>
    public static bool TryCollectTransformRowNames(StateTransform transform, ISet<string> names, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: transform);
        ArgumentNullException.ThrowIfNull(argument: names);

        reason = string.Empty;

        switch (transform) {
            case StateTransform.Observe observe:
                names.Add(item: observe.Row.Spelling);

                break;
            case StateTransform.Transfer transfer:
                names.Add(item: transfer.From.Spelling);
                names.Add(item: transfer.To.Spelling);

                // A random selection advances the draw site's own cursor when the transfer commits (see
                // StateTransform.Transfer's remarks), so the site is touched exactly like Shuffle's.
                if (transfer.Draw is { } transferDraw) {
                    names.Add(item: transferDraw.Spelling);
                }

                break;
            case StateTransform.SetRay setRay:
                names.Add(item: setRay.Row.Spelling);

                break;
            case StateTransform.Shuffle shuffle:
                names.Add(item: shuffle.Row.Spelling);
                names.Add(item: shuffle.Draw.Spelling);

                break;
            case StateTransform.Sort sort:
                names.Add(item: sort.Row.Spelling);

                foreach (var key in (sort.By ?? [])) {
                    if (key is not null) {
                        names.Add(item: key.Row.Spelling);
                    }
                }

                break;
            case StateTransform.WriteSet writeSet:
                names.Add(item: writeSet.Row.Spelling);
                names.Add(item: writeSet.Set.Spelling);

                break;
            case StateTransform.BoardCombine combine:
                names.Add(item: combine.Row.Spelling);

                if (combine.Left is { } left) {
                    names.Add(item: left.Spelling);
                }

                if (combine.Right is { } right) {
                    names.Add(item: right.Spelling);
                }

                break;
            case StateTransform.Arrange arrange:
                names.Add(item: arrange.Row.Spelling);
                names.Add(item: arrange.From.Spelling);

                break;
            case StateTransform.Push push:
                names.Add(item: push.Row.Spelling);

                break;
            case StateTransform.ClearEnclosed clear:
                names.Add(item: clear.Row.Spelling);

                break;
            case StateTransform.Mix mix:
                AddSpellingRowName(mix.Into, names);
                foreach (var term in (mix.Terms ?? [])) {
                    AddSpellingRowName(term?.From, names);
                }

                break;
            case StateTransform.Mean mean:
                AddSpellingRowName(mean.From, names);
                AddSpellingRowName(mean.Into, names);
                if (mean.Where is { } meanWhere) {
                    names.Add(item: meanWhere.Spelling);
                }

                break;
            case StateTransform.Nearest nearest:
                AddSpellingRowName(nearest.From, names);
                AddSpellingRowName(nearest.Query, names);
                names.Add(item: nearest.Into.Spelling);
                if (nearest.Where is { } nearestWhere) {
                    names.Add(item: nearestWhere.Spelling);
                }

                break;
            case StateTransform.Remember remember:
                names.Add(item: remember.Into.Spelling);
                AddSpellingRowName(remember.From, names);

                break;
            default:
                reason = $"transform kind '{transform.GetType().Name}' is not recognized";

                return false;
        }

        return true;
    }

    private static void AddSpellingRowName(string? spelling, ISet<string> names) {
        if (string.IsNullOrWhiteSpace(value: spelling)) {
            return;
        }
        spelling = spelling.Trim();
        if (spelling.StartsWith(comparisonType: StringComparison.Ordinal, value: "vector(")) {
            return;
        }
        var bracketIndex = spelling.IndexOf(value: '[');

        if ((bracketIndex > 0) && spelling.EndsWith(value: ']')) {
            var rowName = spelling[..bracketIndex].Trim();

            if (rowName.Length > 0) {
                names.Add(item: rowName);
            }
            return;
        }
        names.Add(item: spelling);
    }

}

