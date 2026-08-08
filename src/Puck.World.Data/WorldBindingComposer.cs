using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The document PRE-MERGE — the pure, boundary-only function that layers N <see cref="BindingProfileDocument"/>s
/// into one before it goes through the existing <see cref="BindingProfile.Compile"/> once per seat. Chord rows merge
/// on <c>(group, ordered chord)</c>: a later layer's row for the same key OVERRIDES the earlier one — wholesale when
/// the meaning kind or page id differs (a page becoming a command, a renamed page), entry-by-source when both are the
/// SAME page (a later layer's entries for a source REPLACE the earlier layer's entries for that same source; entries
/// at new sources append — the per-world overlay's single-lane remap). Rows at new keys append; modifiers union by id
/// (a later layer overrides a same-id modifier); context rows merge on <c>(family, state)</c> in base-layer order with
/// appended keys after (see <c>MergeContexts</c>); wheels merge on their group, a later layer's wheel REPLACING the
/// earlier one's WHOLESALE (a wheel is one presentation surface — half-merging two ring sets would present a radial
/// neither layer authored; a world that re-authors a group's wheel therefore re-authors all of it, the Editor sector
/// included). Merging must happen HERE, before compilation: a compiled profile
/// resolves wholesale per <c>(slot, source)</c> and so cannot override one entry inside a shared page.
/// </summary>
public static class WorldBindingComposer {
    /// <summary>Merges the given layers in order (null layers skipped). At least one non-null layer is required (in
    /// practice the engine default is always layer 0).</summary>
    /// <param name="layers">The layers to merge, base-first.</param>
    /// <returns>The merged document (its <see cref="BindingProfileDocument.Version"/> is the first non-null layer's).</returns>
    /// <exception cref="ArgumentException">Every layer is <see langword="null"/>.</exception>
    public static BindingProfileDocument Compose(params ReadOnlySpan<BindingProfileDocument?> layers) {
        string? version = null;
        var contexts = new List<BindingContextDefinition>();
        var contextIndexByKey = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var modifiers = new List<BindingModifierDefinition>();
        var modifierIndexById = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var rows = new List<MutableRow>();
        var rowIndexByKey = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var wheels = new List<BindingWheelDefinition>();
        var wheelIndexByGroup = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var layer in layers) {
            if (layer is null) {
                continue;
            }

            version ??= layer.Version;

            // A layer from another schema version must reject LOUDLY here: a stale document deserializes with a
            // null row list, so without this check it would compose as silent nothing instead of failing.
            if (!string.Equals(a: layer.Version, b: version, comparisonType: StringComparison.Ordinal)) {
                throw new ArgumentException(message: $"Layer version \"{layer.Version}\" does not match the base layer's \"{version}\" — re-author the document.", paramName: nameof(layers));
            }

            MergeModifiers(into: modifiers, index: modifierIndexById, layer: layer);
            MergeRows(into: rows, index: rowIndexByKey, layer: layer);
            MergeContexts(into: contexts, index: contextIndexByKey, layer: layer);
            MergeWheels(into: wheels, index: wheelIndexByGroup, layer: layer);
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
            Chords: composedRows,
            Contexts: ((contexts.Count > 0) ? contexts : null),
            Wheels: ((wheels.Count > 0) ? wheels : null)
        );
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

    // Context rows merge on (family, state): a later layer's row for the same key REPLACES the earlier one's group IN
    // PLACE; rows at new keys append. The merged order is therefore the base layer's order with appended keys after —
    // across-family precedence is authored primarily by the layer that ships the vocabulary, deliberately.
    private static void MergeContexts(List<BindingContextDefinition> into, Dictionary<string, int> index, BindingProfileDocument layer) {
        foreach (var row in (layer.Contexts ?? [])) {
            if (string.IsNullOrEmpty(value: row.Family) || string.IsNullOrEmpty(value: row.State)) {
                continue;
            }

            var key = $"{row.Family}\0{row.State}";

            if (index.TryGetValue(
                key: key,
                value: out var existing
            )) {
                into[existing] = row;
            } else {
                index[key] = into.Count;
                into.Add(item: row);
            }
        }
    }

    // Wheels merge on their GROUP: a later layer's wheel for the same group REPLACES the earlier one's WHOLESALE
    // (see the class remarks — a wheel is one presentation surface, never half-merged); wheels for new groups
    // append. The merged order is the base layer's order with appended groups after, matching MergeContexts.
    private static void MergeWheels(List<BindingWheelDefinition> into, Dictionary<string, int> index, BindingProfileDocument layer) {
        foreach (var wheel in (layer.Wheels ?? [])) {
            if (string.IsNullOrEmpty(value: wheel?.Group)) {
                continue;
            }

            if (index.TryGetValue(
                key: wheel.Group,
                value: out var existing
            )) {
                into[existing] = wheel;
            } else {
                index[wheel.Group] = into.Count;
                into.Add(item: wheel);
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
        // Activator-triggered entries carry no Source (see BindingPageEntryDefinition.Activator), so they cannot
        // share m_bySource's keying — merged on their OWN identity instead: the activator's (mode, sequence), the
        // same key BindingProfile.Compile's shadow check uses (OrdinalIgnoreCase there too — a case-variant
        // sequence is the SAME activator at runtime), so a later layer's activator for the identical sequence
        // overrides the earlier one and a different sequence appends.
        private readonly List<string> m_activatorOrder = [];
        private readonly Dictionary<string, List<BindingPageEntryDefinition>> m_byActivatorKey = new(comparer: StringComparer.OrdinalIgnoreCase);
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
            m_byActivatorKey.Clear();
            m_activatorOrder.Clear();
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

            // Activator entries append after source-keyed entries, in first-seen order — a stable, deterministic
            // merge that matches how a later layer's source-keyed entries append too.
            foreach (var key in m_activatorOrder) {
                entries.AddRange(collection: m_byActivatorKey[key]);
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
            // the second entry wiping the first. Sources this layer freshly creates need no clear. Activator entries
            // (no Source) key on their own (mode, sequence) identity instead — see m_byActivatorKey's own remarks.
            var clearedThisLayer = (replace ? new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) : null);
            var clearedActivatorsThisLayer = (replace ? new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) : null);

            foreach (var entry in (entries ?? [])) {
                if (entry.Activator is { } activator) {
                    var key = $"{activator.Mode}\0{string.Join(separator: ',', values: activator.Sequence)}";

                    if (m_byActivatorKey.TryGetValue(
                        key: key,
                        value: out var activatorList
                    )) {
                        if ((clearedActivatorsThisLayer is not null) && clearedActivatorsThisLayer.Add(item: key)) {
                            activatorList.Clear();
                        }
                    } else {
                        activatorList = [];
                        m_byActivatorKey[key] = activatorList;
                        m_activatorOrder.Add(item: key);
                        _ = clearedActivatorsThisLayer?.Add(item: key);
                    }

                    activatorList.Add(item: entry);

                    continue;
                }

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
