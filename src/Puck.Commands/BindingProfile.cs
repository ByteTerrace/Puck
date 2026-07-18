namespace Puck.Commands;

/// <summary>
/// Validates a <see cref="BindingProfileDocument"/> and compiles it into the runtime
/// <see cref="CompiledBindingProfile"/>: one compiled chord row per document row (a binding table and a
/// precomputed <see cref="BindingPageView"/> for a page meaning; edge payloads for a command meaning), plus the
/// group table the per-slot resolution scopes to.
/// </summary>
/// <remarks>
/// The two uniqueness rules a document must satisfy, rejected loudly otherwise: exactly ONE meaning per
/// <c>(group, ordered chord)</c>, and exactly ONE resting (empty-chord) page per group — and the resting row must
/// be a page, since an empty chord has no completion edge to fire a command with. Page ids are unique across the
/// whole document (they address pages in editors and guided sessions). The first row's group is the DEFAULT group.
/// </remarks>
public static class BindingProfile {
    /// <summary>Validates and compiles a profile document.</summary>
    /// <param name="document">The profile document to compile.</param>
    /// <returns>The compiled profile.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="document"/> is invalid.</exception>
    public static CompiledBindingProfile Compile(BindingProfileDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Version != BindingProfileDocument.CurrentVersion) {
            throw new ArgumentException(message: $"Unsupported binding profile version \"{document.Version}\"; expected \"{BindingProfileDocument.CurrentVersion}\".", paramName: nameof(document));
        }

        var modifierIndexById = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var modifierIndexBySource = new Dictionary<string, int>(comparer: StringComparer.OrdinalIgnoreCase);
        var modifiers = (document.Modifiers ?? []);

        for (var modifierIndex = 0; (modifierIndex < modifiers.Count); modifierIndex++) {
            var modifier = modifiers[modifierIndex];

            if (string.IsNullOrEmpty(value: modifier.Id)) {
                throw new ArgumentException(message: "A modifier id must be non-empty.", paramName: nameof(document));
            }

            if (string.IsNullOrEmpty(value: modifier.Source)) {
                throw new ArgumentException(message: $"Modifier \"{modifier.Id}\" must name a source.", paramName: nameof(document));
            }

            if (modifier.ReleaseThreshold > modifier.PressThreshold) {
                throw new ArgumentException(message: $"Modifier \"{modifier.Id}\" has a release threshold above its press threshold.", paramName: nameof(document));
            }

            if (!modifierIndexById.TryAdd(
                key: modifier.Id,
                value: modifierIndex
            )) {
                throw new ArgumentException(message: $"Duplicate modifier id \"{modifier.Id}\".", paramName: nameof(document));
            }

            if (!modifierIndexBySource.TryAdd(
                key: modifier.Source,
                value: modifierIndex
            )) {
                throw new ArgumentException(message: $"Modifiers \"{modifiers[modifierIndexBySource[modifier.Source]].Id}\" and \"{modifier.Id}\" share the source \"{modifier.Source}\".", paramName: nameof(document));
            }
        }

        var documentRows = ((document.Chords is { Count: > 0 } chords)
            ? chords
            : throw new ArgumentException(message: "A binding profile must carry at least one chord row.", paramName: nameof(document)));
        // First pass: group registration, chord resolution, the uniqueness rules, and the raw row facts. Views are
        // built in a second pass so each page view can carry its whole group's command-chord hints.
        var groupIndexByName = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var groupNames = new List<string>();
        var pageIds = new HashSet<string>(comparer: StringComparer.Ordinal);
        var restingByGroup = new List<int>();
        var rowChords = new int[documentRows.Count][];
        var rowGroups = new int[documentRows.Count];
        var seenChordKeys = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var rowIndex = 0; (rowIndex < documentRows.Count); rowIndex++) {
            var row = (documentRows[rowIndex]
                ?? throw new ArgumentException(message: $"Chord row {rowIndex} is null.", paramName: nameof(document)));

            if (string.IsNullOrEmpty(value: row.Group)) {
                throw new ArgumentException(message: $"Chord row {rowIndex} must name a group.", paramName: nameof(document));
            }

            if (!groupIndexByName.TryGetValue(
                key: row.Group,
                value: out var groupIndex
            )) {
                groupIndex = groupNames.Count;
                groupIndexByName[row.Group] = groupIndex;
                groupNames.Add(item: row.Group);
                restingByGroup.Add(item: -1);
            }

            rowGroups[rowIndex] = groupIndex;

            var chordIds = (row.Chord ?? []);
            var chord = new int[chordIds.Count];
            var chordModifiers = new HashSet<int>();

            for (var chordIndex = 0; (chordIndex < chordIds.Count); chordIndex++) {
                if (!modifierIndexById.TryGetValue(
                    key: chordIds[chordIndex],
                    value: out var modifierIndex
                )) {
                    throw new ArgumentException(message: $"Chord row {rowIndex} (group \"{row.Group}\") chords on undeclared modifier \"{chordIds[chordIndex]}\".", paramName: nameof(document));
                }

                if (!chordModifiers.Add(item: modifierIndex)) {
                    throw new ArgumentException(message: $"Chord row {rowIndex} (group \"{row.Group}\") repeats modifier \"{chordIds[chordIndex]}\" in its chord.", paramName: nameof(document));
                }

                chord[chordIndex] = modifierIndex;
            }

            rowChords[rowIndex] = chord;

            // Rule 1: exactly one meaning per (group, chord).
            if (!seenChordKeys.Add(item: $"{groupIndex}\0{string.Join(separator: ',', values: chord)}")) {
                throw new ArgumentException(message: $"Group \"{row.Group}\" declares two meanings for the chord [{string.Join(separator: ", ", values: chordIds)}] — exactly one meaning per (group, chord).", paramName: nameof(document));
            }

            if ((row.Page is null) == (row.Command is null)) {
                throw new ArgumentException(message: $"Chord row {rowIndex} (group \"{row.Group}\") must carry exactly one meaning — a page or a command.", paramName: nameof(document));
            }

            if (row.Page is { } page) {
                if (string.IsNullOrEmpty(value: page.Id)) {
                    throw new ArgumentException(message: "A page id must be non-empty.", paramName: nameof(document));
                }

                if (!pageIds.Add(item: page.Id)) {
                    throw new ArgumentException(message: $"Duplicate page id \"{page.Id}\".", paramName: nameof(document));
                }

                if (chord.Length == 0) {
                    restingByGroup[groupIndex] = rowIndex;
                }
            } else {
                if (string.IsNullOrEmpty(value: row.Command!.Command)) {
                    throw new ArgumentException(message: $"Chord row {rowIndex} (group \"{row.Group}\") must name the command it fires.", paramName: nameof(document));
                }

                // Rule 2's command half: an empty chord has no completion edge — the resting row must be a page.
                if (chord.Length == 0) {
                    throw new ArgumentException(message: $"Group \"{row.Group}\" binds a command to the empty chord — the resting row must be a page.", paramName: nameof(document));
                }
            }
        }

        // Rule 2: exactly one resting page per group (uniqueness is rule 1's empty-chord case; presence is checked here).
        for (var groupIndex = 0; (groupIndex < groupNames.Count); groupIndex++) {
            if (restingByGroup[groupIndex] < 0) {
                throw new ArgumentException(message: $"Group \"{groupNames[groupIndex]}\" has no resting (empty-chord) page.", paramName: nameof(document));
            }
        }

        // Second pass: the per-group command-chord hint lists (shared by every page view of the group), then the rows.
        var commandRowsByGroup = new int[groupNames.Count][];
        var hintsByGroup = new IReadOnlyList<BindingChordCommandView>[groupNames.Count];

        for (var groupIndex = 0; (groupIndex < groupNames.Count); groupIndex++) {
            var commandRows = new List<int>();
            var hints = new List<BindingChordCommandView>();

            for (var rowIndex = 0; (rowIndex < documentRows.Count); rowIndex++) {
                if ((rowGroups[rowIndex] != groupIndex) || (documentRows[rowIndex].Command is not { } command)) {
                    continue;
                }

                commandRows.Add(item: rowIndex);
                hints.Add(item: new BindingChordCommandView(
                    Chord: [.. rowChords[rowIndex].Select(selector: index => modifiers[index].Id)],
                    Command: command.Command,
                    HoldRelease: command.HoldRelease,
                    Icon: command.Icon,
                    Label: command.Label,
                    Sources: [.. rowChords[rowIndex].Select(selector: index => modifiers[index].Source)]
                ));
            }

            commandRowsByGroup[groupIndex] = [.. commandRows];
            hintsByGroup[groupIndex] = hints;
        }

        var rows = new CompiledBindingProfile.CompiledChordRow[documentRows.Count];

        for (var rowIndex = 0; (rowIndex < documentRows.Count); rowIndex++) {
            var row = documentRows[rowIndex];
            var chord = rowChords[rowIndex];
            var groupIndex = rowGroups[rowIndex];

            if (row.Page is { } page) {
                rows[rowIndex] = new CompiledBindingProfile.CompiledChordRow(
                    Chord: chord,
                    Command: null,
                    GroupIndex: groupIndex,
                    Table: BuildTable(page: page),
                    View: BuildView(
                        chord: [.. chord],
                        group: groupNames[groupIndex],
                        hints: hintsByGroup[groupIndex],
                        modifiers: modifiers,
                        page: page
                    )
                );
            } else {
                var command = row.Command!;
                var pressValue = (command.Value ?? CommandValue.Digital(active: true));

                rows[rowIndex] = new CompiledBindingProfile.CompiledChordRow(
                    Chord: chord,
                    Command: new CompiledBindingProfile.CompiledChordCommand(
                        Command: command.Command,
                        DispatchRelease: command.HoldRelease,
                        PressValue: pressValue,
                        ReleaseValue: CommandValue.Inactive(kind: pressValue.Kind)
                    ),
                    GroupIndex: groupIndex,
                    Table: null,
                    View: null
                );
            }
        }

        return new CompiledBindingProfile(
            commandRowsByGroup: commandRowsByGroup,
            groupIndexByName: groupIndexByName,
            groups: [.. groupNames],
            modifierIndexBySource: modifierIndexBySource,
            modifiers: modifiers,
            restingRowByGroup: [.. restingByGroup],
            rows: rows
        );
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> BuildTable(BindingPageDefinition page) {
        var entries = (page.Entries ?? []);
        // Group by source into the runtime source→commands table, carrying each entry's full CommandBinding
        // expressiveness (activation edge, incidental-modifier tolerance, constant value). Built directly here rather
        // than through InputBindingDefinition, which is the reduced (edge + required-modifiers) shape.
        var grouped = new Dictionary<string, List<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase);

        for (var entryIndex = 0; (entryIndex < entries.Count); entryIndex++) {
            var entry = entries[entryIndex];

            if (string.IsNullOrEmpty(value: entry.Source) || string.IsNullOrEmpty(value: entry.Command)) {
                throw new ArgumentException(message: $"Page \"{page.Id}\" carries an entry without a source or command.", paramName: nameof(page));
            }

            if (!grouped.TryGetValue(
                key: entry.Source,
                value: out var list
            )) {
                list = [];
                grouped[entry.Source] = list;
            }

            list.Add(item: new CommandBinding(
                ActivateOn: entry.ActivateOn,
                AnyModifiers: entry.AnyModifiers,
                Command: entry.Command,
                Value: entry.Value
            ));
        }

        var table = new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var (source, list) in grouped) {
            table[source] = list;
        }

        return table;
    }
    private static BindingPageView BuildView(
        HashSet<int> chord,
        string group,
        IReadOnlyList<BindingChordCommandView> hints,
        IReadOnlyList<BindingModifierDefinition> modifiers,
        BindingPageDefinition page
    ) {
        var buttons = new BindingPageButtonView[(page.Entries?.Count ?? 0)];

        for (var entryIndex = 0; (entryIndex < buttons.Length); entryIndex++) {
            var entry = page.Entries![entryIndex];

            buttons[entryIndex] = new BindingPageButtonView(
                Command: entry.Command,
                Icon: entry.Icon,
                Label: entry.Label,
                Source: entry.Source
            );
        }

        var modifierViews = new BindingModifierView[modifiers.Count];

        for (var modifierIndex = 0; (modifierIndex < modifiers.Count); modifierIndex++) {
            var modifier = modifiers[modifierIndex];

            modifierViews[modifierIndex] = new BindingModifierView(
                Icon: modifier.Icon,
                Id: modifier.Id,
                Label: modifier.Label,
                Required: chord.Contains(item: modifierIndex),
                Source: modifier.Source
            );
        }

        return new BindingPageView(
            Buttons: buttons,
            CommandChords: hints,
            Group: group,
            Icon: page.Icon,
            Label: page.Label,
            Modifiers: modifierViews,
            PageId: page.Id
        );
    }
}
