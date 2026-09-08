using System.Collections.Immutable;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The document PRE-MERGE — the pure, boundary-only function that layers N <see cref="BindingProfileDocument"/>s
/// into one before it goes through the existing <see cref="BindingProfile.Compile"/> once per seat. Chord rows merge
/// on <c>(group, ordered chord)</c>: a later layer's row for the same key OVERRIDES the earlier one — wholesale when
/// the meaning kind or page id differs (a page becoming a command, a renamed page), entry-by-source when both are the
/// SAME page (a later layer's row with sources <c>[A, B]</c> REPLACES the earlier layer's entries at A and at B
/// independently; entries at new sources append — the per-world overlay's single-lane remap). An entry surviving at
/// every one of its authored sources carries them all, unsplit; one losing some of its sources to a later layer's
/// narrower row keeps only the sources still its own. Rows at new keys append; modifiers union by id (a later layer
/// overrides a same-id modifier), and a later
/// layer's modifier under a NEW id that shares a source with an earlier one ABSORBS it — a source belongs to one
/// modifier, so sharing one is declaring the same modifier under a new name: the earlier declaration is dropped and
/// every already-merged row's chord/held reference to it is rewritten to the new id (how a world renames the engine's
/// <c>tab</c> and every engine page that held on it follows); context rows merge on <c>(family, state)</c> in base-layer order with
/// appended keys after (see <c>MergeContexts</c>); wheels merge on their id, a later layer's wheel REPLACING the
/// earlier one's WHOLESALE (a wheel is one presentation surface — half-merging two ring sets would present a radial
/// neither layer authored; a world that re-authors a named wheel therefore re-authors all of it, the Editor sector
/// included). Merging must happen HERE, before compilation: a compiled profile
/// resolves wholesale per <c>(slot, source)</c> and so cannot override one entry inside a shared page.
/// </summary>
public static class WorldBindingComposer {
    // The shared key→position merge every layered row set (context/row/wheel) opens: a null/empty key skips the
    // item; a key already indexed either MERGEs into the stored value in place (when the caller supplies one — a
    // deep merge, as chord rows do) or REPLACES it wholesale; a new key appends, storing the ADOPTed value (identity
    // for a row set stored as its own wire type; a conversion for one stored as a mutable accumulator, as chord rows
    // are).
    private static void MergeByKey<TSource, TStored>(List<TStored> into, Dictionary<string, int> index, IEnumerable<TSource> items, Func<TSource, string?> key, Func<TSource, TStored> adopt, Action<TStored, TSource>? merge = null) {
        foreach (var item in items) {
            var itemKey = key(item);

            if (string.IsNullOrEmpty(value: itemKey)) {
                continue;
            }

            if (index.TryGetValue(
                key: itemKey,
                value: out var existing
            )) {
                if (merge is not null) {
                    merge(into[existing], item);
                } else {
                    into[existing] = adopt(item);
                }
            } else {
                index[itemKey] = into.Count;
                into.Add(item: adopt(item));
            }
        }
    }
    // Context rows merge on (family, state): a later layer's row for the same key REPLACES the earlier one's group IN
    // PLACE; rows at new keys append. The merged order is therefore the base layer's order with appended keys after —
    // across-family precedence is authored primarily by the layer that ships the vocabulary, deliberately.
    private static void MergeContexts(List<BindingContextDefinition> into, Dictionary<string, int> index, BindingProfileDocument layer) => MergeByKey(
        adopt: static row => row,
        index: index,
        into: into,
        items: (layer.Contexts ?? []),
        key: static row => (((row is null) || string.IsNullOrEmpty(value: row.Family) || string.IsNullOrEmpty(value: row.State))
            ? null
            : $"{row.Family}\0{row.State}"
        )
    );
    private static void MergeModifiers(List<BindingModifierDefinition> into, Dictionary<string, int> index, BindingProfileDocument layer, List<MutableRow> rows, Dictionary<string, int> rowIndex) {
        foreach (var modifier in (layer.Modifiers ?? [])) {
            if (string.IsNullOrEmpty(value: modifier.Id)) {
                continue;
            }

            if (index.TryGetValue(
                key: modifier.Id,
                value: out var existing
            )) {
                into[existing] = modifier;

                continue;
            }

            // Absorb every earlier modifier this one shares a source with (see the class remarks).
            var absorbed = false;

            for (var position = (into.Count - 1); (position >= 0); position--) {
                var earlier = into[position];

                if (!SharesSource(
                    a: earlier,
                    b: modifier
                )) {
                    continue;
                }

                into.RemoveAt(index: position);
                _ = index.Remove(key: earlier.Id);

                foreach (var row in rows) {
                    row.RenameModifier(
                        from: earlier.Id,
                        to: modifier.Id
                    );
                }

                absorbed = true;
            }

            if (absorbed) {
                // Ids inside a modifier's stored slot moved: rebuild the id → position and row-key indexes.
                index.Clear();

                for (var position = 0; (position < into.Count); position++) {
                    index[into[position].Id] = position;
                }

                rowIndex.Clear();

                for (var position = 0; (position < rows.Count); position++) {
                    rowIndex[rows[position].Key] = position;
                }
            }

            index[modifier.Id] = into.Count;
            into.Add(item: modifier);
        }
    }
    private static void MergeRows(List<MutableRow> into, Dictionary<string, int> index, BindingProfileDocument layer) => MergeByKey(
        adopt: static row => MutableRow.From(row: row),
        index: index,
        into: into,
        items: (layer.Chords ?? []),
        key: static row => RowKey(row: row),
        merge: static (existing, row) => existing.Merge(row: row)
    );
    // Wheels merge on their ID: a later layer's wheel for the same identity replaces it wholesale; a distinct id
    // appends even inside the same group, which is how one group authors several radial presentations.
    private static void MergeWheels(List<BindingWheelDefinition> into, Dictionary<string, int> index, BindingProfileDocument layer) => MergeByKey(
        adopt: static wheel => wheel,
        index: index,
        into: into,
        items: (layer.Wheels ?? []),
        key: static wheel => wheel?.Id
    );
    // The row merge key: group, the held set (sorted — order is not part of its identity), and the ordered chord (a
    // NUL/pipe no group or member id can carry), so ["lt","rt"] and ["rt","lt"] are distinct rows and a same-identity
    // row across layers merges.
    private static string RowKey(BindingChordDefinition row) => RowKey(
        chord: row.Chord,
        group: row.Group,
        held: row.Held
    );
    private static string RowKey(string group, IReadOnlyList<string>? held, IReadOnlyList<string>? chord) {
        return $"{group}\0{string.Join(
            separator: ',',
            values: (held ?? []).Order(comparer: StringComparer.Ordinal)
        )}|{string.Join(
            separator: ',',
            values: (chord ?? [])
        )}";
    }
    private static bool SharesSource(BindingModifierDefinition a, BindingModifierDefinition b) {
        foreach (var source in (a.Sources ?? [])) {
            foreach (var candidate in (b.Sources ?? [])) {
                if (string.Equals(
                    a: source,
                    b: candidate,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Merges the given layers in order (null layers skipped). No non-null layer composes to the empty
    /// document — a world authoring no bindings binds nothing.</summary>
    /// <param name="layers">The layers to merge, base-first.</param>
    /// <returns>The merged document (its <see cref="BindingProfileDocument.Version"/> is the first non-null layer's).</returns>
    public static BindingProfileDocument Compose(params ReadOnlySpan<BindingProfileDocument?> layers) {
        string? version = null;
        var contexts = new List<BindingContextDefinition>();
        var contextIndexByKey = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var modifiers = new List<BindingModifierDefinition>();
        var modifierIndexById = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var rows = new List<MutableRow>();
        var rowIndexByKey = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var wheels = new List<BindingWheelDefinition>();
        var wheelIndexById = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var layer in layers) {
            if (layer is null) {
                continue;
            }

            version ??= layer.Version;

            // A layer from another schema version must reject LOUDLY here: a stale document deserializes with a
            // null row list, so without this check it would compose as silent nothing instead of failing.
            if (!string.Equals(
                a: layer.Version,
                b: version,
                comparisonType: StringComparison.Ordinal
            )) {
                throw new ArgumentException(
                    message: $"Layer version \"{layer.Version}\" does not match the base layer's \"{version}\" — re-author the document.",
                    paramName: nameof(layers)
                );
            }

            MergeModifiers(
                index: modifierIndexById,
                into: modifiers,
                layer: layer,
                rowIndex: rowIndexByKey,
                rows: rows
            );
            MergeRows(
                index: rowIndexByKey,
                into: rows,
                layer: layer
            );
            MergeContexts(
                index: contextIndexByKey,
                into: contexts,
                layer: layer
            );
            MergeWheels(
                index: wheelIndexById,
                into: wheels,
                layer: layer
            );
        }

        var composedRows = new BindingChordDefinition[rows.Count];

        for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
            composedRows[rowIndex] = rows[rowIndex].ToDefinition();
        }

        return new BindingProfileDocument(
            Version: (version ?? BindingProfileDocument.CurrentVersion),
            Modifiers: modifiers,
            Chords: composedRows,
            Contexts: ((contexts.Count > 0)
            ? contexts
            : null),
            Wheels: ((wheels.Count > 0)
            ? wheels
            : null)
        );
    }

    // One chord row being composed: its (group, chord) identity plus its current meaning. A page meaning keeps its
    // entries in first-seen SOURCE order so a later layer's row replaces the earlier layer's entries AT EACH of its
    // own listed sources IN PLACE (a stable, deterministic merge) and a new source appends; any other meaning change
    // replaces the row wholesale. An entry with several sources is stored once per source it claims — the SAME
    // reference at each key — so ToDefinition can recover, per entry, exactly which of its authored sources still
    // point back to it.
    private sealed class MutableRow {
        private readonly string m_group;

        private IReadOnlyList<string>? m_chord;
        private BindingCommandDefinition? m_command;
        private IReadOnlyList<string>? m_held;
        private string? m_pageIcon;
        private string? m_pageId;
        private string? m_pageInherits;
        private string? m_pageLabel;

        private readonly List<string> m_sourceOrder = [];
        private readonly Dictionary<string, List<BindingPageEntryDefinition>> m_bySource = new(comparer: StringComparer.OrdinalIgnoreCase);
        // Activator-triggered entries carry no Source (see BindingPageEntryDefinition.Activator), so they cannot
        // share m_bySource's keying — merged on their OWN identity instead: the activator's (mode, sequence), the
        // same key BindingProfile.Compile's shadow check uses (OrdinalIgnoreCase there too — a case-variant
        // sequence is the SAME activator at runtime), so a later layer's activator for the identical sequence
        // overrides the earlier one and a different sequence appends.
        private readonly List<string> m_activatorOrder = [];
        private readonly Dictionary<string, List<BindingPageEntryDefinition>> m_byActivatorKey = new(comparer: StringComparer.OrdinalIgnoreCase);

        private MutableRow(string group, IReadOnlyList<string>? chord, IReadOnlyList<string>? held) {
            m_chord = chord;
            m_held = held;
            m_group = group;
        }

        private void Absorb(IReadOnlyList<BindingPageEntryDefinition>? entries, bool replace) {
            // A later layer (replace) REPLACES all earlier entries at each source it names — but only on that
            // source's FIRST touch this layer, so a hold/release PAIR the layer carries for one source accumulates
            // rather than the second entry wiping the first. Sources this layer freshly creates need no clear.
            // Activator entries (no Sources) key on their own (mode, sequence) identity instead — see
            // m_byActivatorKey's own remarks. An entry naming several sources is stored, by reference, at EACH of
            // them independently, so a later layer's narrower row can steal one of its sources while it keeps the
            // rest.
            var clearedThisLayer = (replace
                ? new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase)
                : null
            );
            var clearedActivatorsThisLayer = (replace
                ? new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase)
                : null
            );

            foreach (var entry in (entries ?? [])) {
                if (entry.Activator is { } activator) {
                    var key = $"{activator.Mode}\0{string.Join(
                        separator: ',',
                        values: activator.Sequence
                    )}";

                    if (m_byActivatorKey.TryGetValue(
                        key: key,
                        value: out var activatorList
                    )) {
                        if (
                            (clearedActivatorsThisLayer is not null) &&
                            clearedActivatorsThisLayer.Add(item: key)
                        ) {
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

                foreach (var source in (entry.Sources ?? [])) {
                    if (string.IsNullOrEmpty(value: source)) {
                        continue;
                    }

                    if (m_bySource.TryGetValue(
                        key: source,
                        value: out var list
                    )) {
                        if (
                            (clearedThisLayer is not null) &&
                            clearedThisLayer.Add(item: source)
                        ) {
                            list.Clear();
                        }
                    } else {
                        list = [];
                        m_bySource[source] = list;
                        m_sourceOrder.Add(item: source);
                        _ = clearedThisLayer?.Add(item: source);
                    }

                    list.Add(item: entry);
                }
            }
        }
        private void Adopt(BindingChordDefinition row) {
            m_command = row.Command;
            m_pageIcon = row.Page?.Icon;
            m_pageId = row.Page?.Id;
            m_pageInherits = row.Page?.Inherits;
            m_pageLabel = row.Page?.Label;

            if (row.Page is { } page) {
                Absorb(
                    entries: page.Entries,
                    replace: false
                );
            }
        }
        private static IReadOnlyList<string>? Rename(IReadOnlyList<string>? ids, string from, string to) {
            if (ids is null) {
                return null;
            }

            string[]? renamed = null;

            for (var position = 0; (position < ids.Count); position++) {
                if (!string.Equals(
                    a: ids[position],
                    b: from,
                    comparisonType: StringComparison.Ordinal
                )) {
                    continue;
                }

                renamed ??= [.. ids];
                renamed[position] = to;
            }

            if (renamed is null) {
                return ids;
            }

            if (renamed.Distinct(comparer: StringComparer.Ordinal).Count() != renamed.Length) {
                throw new ArgumentException(message: $"Modifier \"{to}\" absorbs \"{from}\", but a chord row already carries both — the row would name one modifier twice.");
            }

            return renamed;
        }

        public static MutableRow From(BindingChordDefinition row) {
            var mutable = new MutableRow(
                group: row.Group,
                chord: row.Chord,
                held: row.Held
            );

            mutable.Adopt(row: row);

            return mutable;
        }
        // Merge a later layer's version of this row. The SAME page (matching id) deep-merges: display metadata and
        // inheritance override when present, and entries replace per source. Anything else — a command meaning, or
        // a page under a different id — is a wholesale override: exactly one meaning per (group, chord) survives.
        public void Merge(BindingChordDefinition row) {
            if (
                (row.Page is { } page) &&
                (m_command is null) &&
                string.Equals(
                a: page.Id,
                b: m_pageId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                m_pageIcon = (page.Icon ?? m_pageIcon);
                m_pageInherits = (page.Inherits ?? m_pageInherits);
                m_pageLabel = (page.Label ?? m_pageLabel);

                Absorb(
                    entries: page.Entries,
                    replace: true
                );

                return;
            }

            m_bySource.Clear();
            m_sourceOrder.Clear();
            m_byActivatorKey.Clear();
            m_activatorOrder.Clear();
            Adopt(row: row);
        }
        // Rewrites this row's chord/held references from an absorbed modifier id to the absorbing one.
        public void RenameModifier(string from, string to) {
            m_chord = Rename(
                from: from,
                ids: m_chord,
                to: to
            );
            m_held = Rename(
                from: from,
                ids: m_held,
                to: to
            );
        }
        public BindingChordDefinition ToDefinition() {
            if (m_command is { } command) {
                return new BindingChordDefinition(
                    Group: m_group,
                    Chord: m_chord,
                    Command: command,
                    Held: m_held
                );
            }

            var entries = new List<BindingPageEntryDefinition>();
            // An entry may be stored at several source keys (see Absorb); emit it exactly once, at its first-seen
            // source, narrowed to whichever of its authored sources STILL point back to it — an entry untouched by
            // any later override emits with its full authored Sources, unsplit.
            var emitted = new HashSet<BindingPageEntryDefinition>(comparer: ReferenceEqualityComparer.Instance);

            foreach (var source in m_sourceOrder) {
                foreach (var entry in m_bySource[source]) {
                    if (!emitted.Add(item: entry)) {
                        continue;
                    }

                    var survivingSources = entry.Sources!.Where(predicate: candidate => (m_bySource.TryGetValue(
                        key: candidate,
                        value: out var owners
                    ) && owners.Any(predicate: owner => ReferenceEquals(
                        objA: owner,
                        objB: entry
                    )))).ToImmutableArray();

                    entries.Add(item: ((survivingSources.Length == entry.Sources!.Count)
                        ? entry
                        : (entry with { Sources = survivingSources })));
                }
            }

            // Activator entries append after source-keyed entries, in first-seen order — a stable, deterministic
            // merge that matches how a later layer's source-keyed entries append too.
            foreach (var key in m_activatorOrder) {
                entries.AddRange(collection: m_byActivatorKey[key]);
            }

            return new BindingChordDefinition(
                Group: m_group,
                Chord: m_chord,
                Held: m_held,
                Page: new BindingPageDefinition(
                    Entries: entries,
                    Icon: m_pageIcon,
                    Id: (m_pageId ?? string.Empty),
                    Inherits: m_pageInherits,
                    Label: m_pageLabel
                )
            );
        }

        public string Key => RowKey(
            chord: m_chord,
            group: m_group,
            held: m_held
        );
    }
}
