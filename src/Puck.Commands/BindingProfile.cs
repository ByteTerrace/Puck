namespace Puck.Commands;

/// <summary>
/// Validates a <see cref="BindingProfileDocument"/> and compiles it into the runtime
/// <see cref="CompiledBindingProfile"/>: one binding table and one precomputed <see cref="BindingPageView"/>
/// per page, plus the ordered-chord lookup that turns held modifiers into the active page.
/// </summary>
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

        var basePageIndex = (-1);
        var pageIds = new HashSet<string>(comparer: StringComparer.Ordinal);
        var pages = ((document.Pages is { Count: > 0 } documentPages)
            ? new CompiledBindingProfile.CompiledBindingPage[documentPages.Count]
            : throw new ArgumentException(message: "A binding profile must carry at least one page.", paramName: nameof(document)));
        var seenChords = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var pageIndex = 0; (pageIndex < documentPages.Count); pageIndex++) {
            var page = documentPages[pageIndex];

            if (string.IsNullOrEmpty(value: page.Id)) {
                throw new ArgumentException(message: "A page id must be non-empty.", paramName: nameof(document));
            }

            if (!pageIds.Add(item: page.Id)) {
                throw new ArgumentException(message: $"Duplicate page id \"{page.Id}\".", paramName: nameof(document));
            }

            var chordIds = (page.Chord ?? []);
            var chord = new int[chordIds.Count];
            var chordModifiers = new HashSet<int>();

            for (var chordIndex = 0; (chordIndex < chordIds.Count); chordIndex++) {
                if (!modifierIndexById.TryGetValue(
                    key: chordIds[chordIndex],
                    value: out var modifierIndex
                )) {
                    throw new ArgumentException(message: $"Page \"{page.Id}\" chords on undeclared modifier \"{chordIds[chordIndex]}\".", paramName: nameof(document));
                }

                if (!chordModifiers.Add(item: modifierIndex)) {
                    throw new ArgumentException(message: $"Page \"{page.Id}\" repeats modifier \"{chordIds[chordIndex]}\" in its chord.", paramName: nameof(document));
                }

                chord[chordIndex] = modifierIndex;
            }

            if (!seenChords.Add(item: string.Join(separator: ',', values: chord))) {
                throw new ArgumentException(message: $"Page \"{page.Id}\" duplicates another page's chord.", paramName: nameof(document));
            }

            if (chord.Length == 0) {
                basePageIndex = pageIndex;
            }

            pages[pageIndex] = new CompiledBindingProfile.CompiledBindingPage(
                Chord: chord,
                Table: BuildTable(page: page),
                View: BuildView(modifiers: modifiers, page: page, chord: chordModifiers)
            );
        }

        if (basePageIndex < 0) {
            throw new ArgumentException(message: "A binding profile must carry an empty-chord (no-modifier) page.", paramName: nameof(document));
        }

        return new CompiledBindingProfile(
            basePageIndex: basePageIndex,
            modifierIndexBySource: modifierIndexBySource,
            modifiers: modifiers,
            pages: pages
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
    private static BindingPageView BuildView(IReadOnlyList<BindingModifierDefinition> modifiers, BindingPageDefinition page, HashSet<int> chord) {
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
            Icon: page.Icon,
            Label: page.Label,
            Modifiers: modifierViews,
            PageId: page.Id
        );
    }
}
