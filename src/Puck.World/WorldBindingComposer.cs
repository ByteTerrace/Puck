using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The §2.4 document PRE-MERGE — the pure, boundary-only function that layers N <see cref="BindingProfileDocument"/>s
/// into one before it goes through the existing <see cref="BindingProfile.Compile"/> once per seat. The merge key is
/// <c>(page id + ordered chord, source)</c>: a later layer's entries for a source REPLACE the earlier layer's entries
/// for that same source within a matching page; entries at new sources append; whole pages unknown to earlier layers
/// append; modifiers union by id (a later layer overrides a same-id modifier). This is the level the compiled
/// <c>LayeredInputBindings</c> primitive cannot express — it composes wholesale per <c>(slot, source)</c> and so cannot
/// override one entry inside a shared page (the per-world overlay's single-lane remap needs exactly that).
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
        var pages = new List<MutablePage>();
        var pageIndexByKey = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var layer in layers) {
            if (layer is null) {
                continue;
            }

            version ??= layer.Version;
            MergeModifiers(into: modifiers, index: modifierIndexById, layer: layer);
            MergePages(into: pages, index: pageIndexByKey, layer: layer);
        }

        if (version is null) {
            throw new ArgumentException(message: "Compose requires at least one non-null layer.", paramName: nameof(layers));
        }

        var composedPages = new BindingPageDefinition[pages.Count];

        for (var pageIndex = 0; (pageIndex < pages.Count); pageIndex++) {
            composedPages[pageIndex] = pages[pageIndex].ToDefinition();
        }

        return new BindingProfileDocument(
            Version: version,
            Modifiers: modifiers,
            Pages: composedPages
        );
    }

    /// <summary>Flattens a composed document's no-modifier BASE page into the slot-blind <c>source → commands</c> table
    /// the console-dispatch <see cref="BindingCommandSource"/> consumes — so that consumer and the per-seat resolvers
    /// derive from the same composed documents (no second authoring grammar). The base page is the one with the empty
    /// chord; an empty table results when the document carries none.</summary>
    /// <param name="document">The composed document.</param>
    /// <returns>The base page's <c>source → commands</c> table.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> BasePageTable(BindingProfileDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var grouped = new Dictionary<string, List<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var page in (document.Pages ?? [])) {
            if ((page.Chord is { Count: > 0 }) || (page.Entries is null)) {
                continue;
            }

            foreach (var entry in page.Entries) {
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

    private static void MergePages(List<MutablePage> into, Dictionary<string, int> index, BindingProfileDocument layer) {
        foreach (var page in (layer.Pages ?? [])) {
            var key = PageKey(page: page);

            if (index.TryGetValue(
                key: key,
                value: out var existing
            )) {
                into[existing].Merge(page: page);
            } else {
                index[key] = into.Count;
                into.Add(item: MutablePage.From(page: page));
            }
        }
    }

    // The page merge key: id plus the ordered chord (a NUL separator no id/modifier id can carry), so ["left","right"]
    // and ["right","left"] are distinct pages and a same-id-and-chord page across layers merges.
    private static string PageKey(BindingPageDefinition page) {
        return $"{page.Id}\0{string.Join(separator: ',', values: (page.Chord ?? []))}";
    }

    // One page being composed: its identity (id + chord + display), plus its entries kept in first-seen SOURCE order so
    // a later layer's entries for a source replace the earlier layer's IN PLACE (a stable, deterministic merge) and a
    // new source appends.
    private sealed class MutablePage {
        private readonly List<string> m_sourceOrder = [];
        private readonly Dictionary<string, List<BindingPageEntryDefinition>> m_bySource = new(comparer: StringComparer.OrdinalIgnoreCase);
        private string m_id;
        private IReadOnlyList<string> m_chord;
        private string? m_label;
        private string? m_icon;

        private MutablePage(string id, IReadOnlyList<string> chord, string? label, string? icon) {
            m_chord = chord;
            m_icon = icon;
            m_id = id;
            m_label = label;
        }

        public static MutablePage From(BindingPageDefinition page) {
            var mutable = new MutablePage(id: page.Id, chord: (page.Chord ?? []), label: page.Label, icon: page.Icon);

            mutable.Absorb(entries: page.Entries, replace: false);

            return mutable;
        }

        // Merge a later layer's version of this page: its display metadata overrides when present, and its entries
        // replace the earlier layer's for each source it names (the single-lane remap).
        public void Merge(BindingPageDefinition page) {
            m_chord = (page.Chord ?? m_chord);
            m_icon = (page.Icon ?? m_icon);
            m_label = (page.Label ?? m_label);

            Absorb(entries: page.Entries, replace: true);
        }

        public BindingPageDefinition ToDefinition() {
            var entries = new List<BindingPageEntryDefinition>();

            foreach (var source in m_sourceOrder) {
                entries.AddRange(collection: m_bySource[source]);
            }

            return new BindingPageDefinition(
                Id: m_id,
                Chord: m_chord,
                Entries: entries,
                Label: m_label,
                Icon: m_icon
            );
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
