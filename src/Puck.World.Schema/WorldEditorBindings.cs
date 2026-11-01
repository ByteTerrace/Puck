using Puck.Commands;

namespace Puck.World;

/// <summary>The build-mode binding mechanism: the world's authored build toggle (<see cref="WorldEditorDefaults.Toggle"/>)
/// composed beneath the resting page of every group the world's binding layers declare, so a seat reaches build mode
/// from wherever it rests. The build page, its keys and the context row that selects it while a seat builds are the
/// world's own binding layers (the standard's <c>editor.puck</c>), which compose over this one, so a page that binds
/// the toggle's id or one of its sources keeps its own meaning.</summary>
public static class WorldEditorBindings {
    /// <summary>The bindable verb that flips a seat between playing and building.</summary>
    public const string ToggleCommand = "player.build";

    /// <summary>Returns the editor layer to compose beneath a world's own layers: one resting row per group those
    /// layers declare, in the order they first declare it and under the page id the last of them rests on, carrying
    /// the authored build toggle, so the toggle merges into the page a seat actually rests on and the world's first
    /// group stays the default. Returns <see langword="null"/> when the world authors no toggle or its layers declare
    /// no resting page, since a world that binds nothing has no key to build with.</summary>
    /// <param name="worldLayers">The world's layers, base first; <see langword="null"/> entries are skipped.</param>
    /// <param name="toggle">The world's authored build toggle, or <see langword="null"/> when it authors no editor
    /// section.</param>
    /// <returns>The editor layer, or <see langword="null"/> for none.</returns>
    public static BindingProfileDocument? Layer(ReadOnlySpan<BindingProfileDocument?> worldLayers, BindingPageEntryDefinition? toggle) {
        if (toggle is null) {
            return null;
        }

        var order = new List<string>();
        var resting = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var layer in worldLayers) {
            foreach (var row in (layer?.Chords ?? [])) {
                if (
                    !row.IsResting ||
                    (row.Page is not { } page)
                ) {
                    continue;
                }

                var group = row.Group.Value;

                if (!resting.ContainsKey(key: group)) {
                    order.Add(item: group);
                }

                resting[group] = page.Id;
            }
        }

        if (order.Count == 0) {
            return null;
        }

        var chords = new List<BindingChordDefinition>(capacity: order.Count);

        foreach (var group in order) {
            chords.Add(item: new BindingChordDefinition(
                Chord: [],
                Group: group,
                Page: new BindingPageDefinition(
                    Entries: [toggle],
                    Id: resting[group]
                )
            ));
        }

        return new BindingProfileDocument(
            Chords: chords,
            Contexts: [],
            Modifiers: [],
            Version: BindingProfileDocument.CurrentVersion
        );
    }
}
