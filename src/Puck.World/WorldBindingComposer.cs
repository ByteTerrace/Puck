using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The §2.4 document PRE-MERGE — the pure, boundary-only function that layers N <see cref="BindingProfileDocument"/>s
/// into one before it goes through the existing <see cref="BindingProfile.Compile"/> once per seat. Chord rows merge
/// on <c>(group, ordered chord)</c>: a later layer's row for the same key OVERRIDES the earlier one — wholesale when
/// the meaning kind or page id differs (a page becoming a command, a renamed page), entry-by-source when both are the
/// SAME page (a later layer's entries for a source REPLACE the earlier layer's entries for that same source; entries
/// at new sources append — the per-world overlay's single-lane remap). Rows at new keys append; modifiers union by id
/// (a later layer overrides a same-id modifier). This is the level the compiled <c>LayeredInputBindings</c> primitive
/// cannot express — it composes wholesale per <c>(slot, source)</c> and so cannot override one entry inside a shared
/// page.
/// </summary>
internal static class WorldBindingComposer {
    /// <summary>Merges the given layers in order (null layers skipped). At least one non-null layer is required (in
    /// practice the engine default is always layer 0).</summary>
    /// <param name="layers">The layers to merge, base-first.</param>
    /// <returns>The merged document (its <see cref="BindingProfileDocument.Version"/> is the first non-null layer's).</returns>
    /// <exception cref="ArgumentException">Every layer is <see langword="null"/>.</exception>
    public static BindingProfileDocument Compose(params ReadOnlySpan<BindingProfileDocument?> layers) {
        string? version = null;
        var modifiers = new List<BindingModifierDefinition>();
        var modifierIndexById = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var rows = new List<MutableRow>();
        var rowIndexByKey = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var layer in layers) {
            if (layer is null) {
                continue;
            }

            version ??= layer.Version;
            MergeModifiers(into: modifiers, index: modifierIndexById, layer: layer);
            MergeRows(into: rows, index: rowIndexByKey, layer: layer);
        }

        if (version is null) {
            throw new ArgumentException(message: "Compose requires at least one non-null layer.", paramName: nameof(layers));
        }

        var composedRows = new BindingChordDefinition[rows.Count];

        for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
            composedRows[rowIndex] = rows[rowIndex].ToDefinition();
        }

        return new BindingProfileDocument(
            Version: version,
            Modifiers: modifiers,
            Chords: composedRows
        );
    }

    /// <summary>Flattens a composed document's DEFAULT-group resting page into the slot-blind <c>source → commands</c>
    /// table the console-dispatch <see cref="BindingCommandSource"/> consumes — so that consumer and the per-seat
    /// resolvers derive from the same composed documents (no second authoring grammar). The default group is the first
    /// row's; an empty table results when the document carries no resting page for it.</summary>
    /// <param name="document">The composed document.</param>
    /// <returns>The resting page's <c>source → commands</c> table.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> BasePageTable(BindingProfileDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var grouped = new Dictionary<string, List<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase);
        var defaultGroup = ((document.Chords is { Count: > 0 } chords) ? chords[0].Group : null);

        foreach (var row in (document.Chords ?? [])) {
            if ((row.Chord is { Count: > 0 }) ||
                (row.Page?.Entries is not { } entries) ||
                !string.Equals(a: row.Group, b: defaultGroup, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            foreach (var entry in entries) {
                if (string.IsNullOrEmpty(value: entry.Source) || string.IsNullOrEmpty(value: entry.Command)) {
                    continue;
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
        }

        var table = new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var (source, list) in grouped) {
            table[source] = list;
        }

        return table;
    }

    private static void MergeModifiers(List<BindingModifierDefinition> into, Dictionary<string, int> index, BindingProfileDocument layer) {
        foreach (var modifier in (layer.Modifiers ?? [])) {
            if (string.IsNullOrEmpty(value: modifier.Id)) {
                continue;
            }

            if (index.TryGetValue(
                key: modifier.Id,
                value: out var existing
            )) {
                into[existing] = modifier;
            } else {
                index[modifier.Id] = into.Count;
                into.Add(item: modifier);
            }
        }
    }

    private static void MergeRows(List<MutableRow> into, Dictionary<string, int> index, BindingProfileDocument layer) {
        foreach (var row in (layer.Chords ?? [])) {
            var key = RowKey(row: row);

            if (index.TryGetValue(
                key: key,
                value: out var existing
            )) {
                into[existing].Merge(row: row);
            } else {
                index[key] = into.Count;
                into.Add(item: MutableRow.From(row: row));
            }
        }
    }

    // The chord-row merge key: group plus the ordered chord (a NUL separator no group/modifier id can carry), so
    // ["lt","rt"] and ["rt","lt"] are distinct rows and a same-(group, chord) row across layers merges.
    private static string RowKey(BindingChordDefinition row) {
        return $"{row.Group}\0{string.Join(separator: ',', values: (row.Chord ?? []))}";
    }

    // One chord row being composed: its (group, chord) identity plus its current meaning. A page meaning keeps its
    // entries in first-seen SOURCE order so a later layer's entries for a source replace the earlier layer's IN PLACE
    // (a stable, deterministic merge) and a new source appends; any other meaning change replaces the row wholesale.
    private sealed class MutableRow {
        private readonly string m_group;
        private readonly IReadOnlyList<string> m_chord;
        private readonly List<string> m_sourceOrder = [];
        private readonly Dictionary<string, List<BindingPageEntryDefinition>> m_bySource = new(comparer: StringComparer.OrdinalIgnoreCase);
        private BindingCommandDefinition? m_command;
        private string? m_pageId;
        private string? m_pageLabel;
        private string? m_pageIcon;

        private MutableRow(string group, IReadOnlyList<string> chord) {
            m_chord = chord;
            m_group = group;
        }

        public static MutableRow From(BindingChordDefinition row) {
            var mutable = new MutableRow(group: row.Group, chord: (row.Chord ?? []));

            mutable.Adopt(row: row);

            return mutable;
        }

        // Merge a later layer's version of this row. The SAME page (matching id) deep-merges: display metadata
        // overrides when present, entries replace per source. Anything else — a command meaning, or a page under a
        // different id — is a wholesale override: exactly one meaning per (group, chord) must survive the merge.
        public void Merge(BindingChordDefinition row) {
            if ((row.Page is { } page) && (m_command is null) && string.Equals(a: page.Id, b: m_pageId, comparisonType: StringComparison.Ordinal)) {
                m_pageIcon = (page.Icon ?? m_pageIcon);
                m_pageLabel = (page.Label ?? m_pageLabel);

                Absorb(entries: page.Entries, replace: true);

                return;
            }

            m_bySource.Clear();
            m_sourceOrder.Clear();
            Adopt(row: row);
        }

        public BindingChordDefinition ToDefinition() {
            if (m_command is { } command) {
                return new BindingChordDefinition(
                    Group: m_group,
                    Chord: m_chord,
                    Command: command
                );
            }

            var entries = new List<BindingPageEntryDefinition>();

            foreach (var source in m_sourceOrder) {
                entries.AddRange(collection: m_bySource[source]);
            }

            return new BindingChordDefinition(
                Group: m_group,
                Chord: m_chord,
                Page: new BindingPageDefinition(
                    Id: (m_pageId ?? string.Empty),
                    Entries: entries,
                    Label: m_pageLabel,
                    Icon: m_pageIcon
                )
            );
        }

        private void Adopt(BindingChordDefinition row) {
            m_command = row.Command;
            m_pageIcon = row.Page?.Icon;
            m_pageId = row.Page?.Id;
            m_pageLabel = row.Page?.Label;

            if (row.Page is { } page) {
                Absorb(entries: page.Entries, replace: false);
            }
        }

        private void Absorb(IReadOnlyList<BindingPageEntryDefinition>? entries, bool replace) {
            // A later layer (replace) REPLACES all earlier entries for each source it names — but only on that source's
            // FIRST touch this layer, so a hold/release PAIR the layer carries for one source accumulates rather than
            // the second entry wiping the first. Sources this layer freshly creates need no clear.
            var clearedThisLayer = (replace ? new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) : null);

            foreach (var entry in (entries ?? [])) {
                if (string.IsNullOrEmpty(value: entry.Source)) {
                    continue;
                }

                if (m_bySource.TryGetValue(
                    key: entry.Source,
                    value: out var list
                )) {
                    if ((clearedThisLayer is not null) && clearedThisLayer.Add(item: entry.Source)) {
                        list.Clear();
                    }
                } else {
                    list = [];
                    m_bySource[entry.Source] = list;
                    m_sourceOrder.Add(item: entry.Source);
                    _ = clearedThisLayer?.Add(item: entry.Source);
                }

                list.Add(item: entry);
            }
        }
    }
}
