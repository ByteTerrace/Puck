using Puck.Commands;
using Puck.Input;

namespace Puck.World.Client;

/// <summary>The engine's build-mode binding layer, composed beneath every world that binds anything, so every such
/// world can be built in without authoring a binding: the build toggle on each group's resting page, the
/// <see cref="BuildGroup"/> whose page drives the editor, and the context row that selects that group while the seat
/// builds. A world's own layers compose over it, so a world that binds one of these keys keeps its own meaning, and a
/// world may extend or rebind the build page like any other. <see cref="Bar"/> is the binding bar a building seat
/// shows when its bar has no bank for the build page.</summary>
public static class WorldEditorBindings {
    /// <summary>The group a seat's bindings resolve in while it builds.</summary>
    public const string BuildGroup = "build";
    /// <summary>The build group's resting page.</summary>
    public const string BuildPage = "build";
    /// <summary>The build page's label.</summary>
    public const string BuildLabel = "Build";
    /// <summary>The build page's label on the bar of a building seat whose principal may not edit placements.</summary>
    public const string ReadOnlyLabel = "Build (read-only)";
    /// <summary>The bindable verb that flips a seat between playing and building.</summary>
    public const string ToggleCommand = "player.build";

    private const string BarLayout = "build";
    private const string GamepadTable = "gamepad";
    private const string KeyboardTable = "keyboard";

    private static readonly string[] ToggleSources = [InputSources.Keyboard.Function(number: 2), InputSources.Gamepad.Back];

    private static BindingPageEntryDefinition Toggle(string label) => new(
        Command: ToggleCommand,
        Id: "editor.toggle",
        Label: label,
        Sources: ToggleSources
    );
    private static BindingPageEntryDefinition Entry(string id, string command, string? text, string label, params string[] sources) => new(
        Command: command,
        Id: id,
        Label: label,
        Sources: sources,
        Text: text
    );
    private static string Key(char letter) => InputSources.Keyboard.Letter(letter: letter);
    // One row of plates per device family, in page order, centered on the anchor: the keyboard's above the gamepad's.
    private static WorldBindingBarAuthoring BuildBar() {
        var sources = BuildPageDefinition.Entries.SelectMany(selector: static entry => (entry.Sources ?? [])).Distinct(comparer: StringComparer.Ordinal).ToArray();
        var keyboard = sources.Where(predicate: static source => source.StartsWith(comparisonType: StringComparison.Ordinal, value: "keyboard.")).ToArray();
        var gamepad = sources.Where(predicate: static source => source.StartsWith(comparisonType: StringComparison.Ordinal, value: "gamepad.")).ToArray();

        static IReadOnlyList<WorldBindingBarSlotPlacement> Row(string[] row, float y) => [.. row.Select(selector: (source, index) => new WorldBindingBarSlotPlacement(
            Source: source,
            X: (index - (0.5f * (row.Length - 1))),
            Y: y
        ))];

        return new WorldBindingBarAuthoring(
            Banks: [new WorldBindingBarBank(Alpha: 1f, Id: BuildPage, PageId: BuildPage)],
            Layout: BarLayout,
            Layouts: new Dictionary<string, WorldBindingBarLayout>(comparer: StringComparer.Ordinal) {
                [BarLayout] = new WorldBindingBarLayout(
                    Banks: new Dictionary<string, WorldBindingBarBankPlacement>(comparer: StringComparer.Ordinal) {
                        [BuildPage] = new WorldBindingBarBankPlacement(Pieces: [new WorldBindingBarPiece(Table: KeyboardTable), new WorldBindingBarPiece(Table: GamepadTable)]),
                    },
                    Tables: new Dictionary<string, IReadOnlyList<WorldBindingBarSlotPlacement>>(comparer: StringComparer.Ordinal) {
                        [GamepadTable] = Row(row: gamepad, y: 0f),
                        [KeyboardTable] = Row(row: keyboard, y: 1f),
                    }
                ),
            },
            SlotSet: sources
        );
    }

    /// <summary>The build page: the toggle back to play, the grid and snapping, placing, and nudging and turning the
    /// seat's current placement by whole grid and angle steps.</summary>
    public static BindingPageDefinition BuildPageDefinition { get; } = new(
        Entries: [
            Toggle(label: "Play"),
            Entry(command: "world.grid", id: "editor.grid", label: "Grid", sources: [Key(letter: 'g'), InputSources.Gamepad.ButtonNorth], text: "toggle"),
            Entry(command: "world.grid", id: "editor.grid.mode", label: "Grid mode", sources: [Key(letter: 'h')], text: "next"),
            Entry(command: "world.grid", id: "editor.grid.finer", label: "Finer", sources: [InputSources.Keyboard.Minus], text: "pitch down"),
            Entry(command: "world.grid", id: "editor.grid.coarser", label: "Coarser", sources: [InputSources.Keyboard.EqualsKey], text: "pitch up"),
            Entry(command: "world.snap", id: "editor.snap", label: "Snap", sources: [Key(letter: 'n'), InputSources.Gamepad.ButtonWest], text: "toggle"),
            Entry(command: "world.snap", id: "editor.snap.reference", label: "Align", sources: [Key(letter: 'c')], text: "reference"),
            Entry(command: "world.place", id: "editor.place", label: "Place", sources: [InputSources.Keyboard.Enter, InputSources.Gamepad.ButtonSouth], text: null),
            Entry(command: "world.nudge", id: "editor.nudge.left", label: "Left", sources: [Key(letter: 'a'), InputSources.Gamepad.DpadLeft], text: "x -1"),
            Entry(command: "world.nudge", id: "editor.nudge.right", label: "Right", sources: [Key(letter: 'd'), InputSources.Gamepad.DpadRight], text: "x 1"),
            Entry(command: "world.nudge", id: "editor.nudge.back", label: "Back", sources: [Key(letter: 's'), InputSources.Gamepad.DpadDown], text: "z 1"),
            Entry(command: "world.nudge", id: "editor.nudge.forward", label: "Forward", sources: [Key(letter: 'w'), InputSources.Gamepad.DpadUp], text: "z -1"),
            Entry(command: "world.nudge", id: "editor.nudge.up", label: "Up", sources: [Key(letter: 'r')], text: "y 1"),
            Entry(command: "world.nudge", id: "editor.nudge.down", label: "Down", sources: [Key(letter: 'f')], text: "y -1"),
            Entry(command: "world.turn", id: "editor.turn.left", label: "Turn left", sources: [Key(letter: 'q'), InputSources.Gamepad.LeftShoulder], text: "-1"),
            Entry(command: "world.turn", id: "editor.turn.right", label: "Turn right", sources: [Key(letter: 'e'), InputSources.Gamepad.RightShoulder], text: "1"),
        ],
        Id: BuildPage,
        Label: BuildLabel
    );
    /// <summary>The binding bar a building seat shows when its own bar has no bank for <see cref="BuildPage"/>: the
    /// build page's controls, one row of plates for the keyboard's above one for the gamepad's.</summary>
    public static WorldBindingBarAuthoring Bar { get; } = BuildBar();

    /// <summary>Returns the editor layer to compose beneath a world's own layers: one resting row per group those
    /// layers declare, in the order they first declare it and under the page id the last of them rests on, carrying the
    /// build toggle, so the toggle merges into the page a seat actually rests on and the world's first group stays the
    /// default; then the build group and its context row. Returns <see langword="null"/> when the world's layers declare
    /// no resting page, since a world that binds nothing has no key to build with.</summary>
    /// <param name="worldLayers">The world's layers, base first; <see langword="null"/> entries are skipped.</param>
    /// <returns>The editor layer, or <see langword="null"/> for none.</returns>
    public static BindingProfileDocument? Layer(ReadOnlySpan<BindingProfileDocument?> worldLayers) {
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

        var chords = new List<BindingChordDefinition>(capacity: (order.Count + 1));

        foreach (var group in order) {
            chords.Add(item: new BindingChordDefinition(
                Chord: [],
                Group: group,
                Page: new BindingPageDefinition(
                    Entries: [Toggle(label: "Build")],
                    Id: resting[group]
                )
            ));
        }

        if (!resting.ContainsKey(key: BuildGroup)) {
            chords.Add(item: new BindingChordDefinition(
                Chord: [],
                Group: BuildGroup,
                Page: BuildPageDefinition
            ));
        }

        return new BindingProfileDocument(
            Chords: chords,
            Contexts: [new BindingContextDefinition(
                Family: WorldContextFamilies.Editor,
                Group: BuildGroup,
                State: WorldContextFamilies.EditorBuild
            )],
            Modifiers: [],
            Version: BindingProfileDocument.CurrentVersion
        );
    }
}
