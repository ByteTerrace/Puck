using Puck.Demo.BindingBar;

namespace Puck.Demo.Overworld;

/// <summary>
/// One authoring mode, declared as data: its id (hub page suffix / console index), its hub-tile + ENTER-slot label
/// and glyph, and the host toggle that ENTERS it. The hub guarantees a clean state (it only opens from the room, no
/// authoring surface up), so a <c>Toggle*</c>-from-off is exactly an Enter.
/// </summary>
/// <param name="Id">The stable mode id: <c>world</c> | <c>creator</c> | <c>tracker</c> | (future) <c>game-edit</c>.</param>
/// <param name="Label">The hub tile + <c>ActivePageId</c> label: <c>WORLD</c> / <c>SCULPT</c> / <c>TRACKER</c>.</param>
/// <param name="Glyph">The hub tile icon, also the confirm (South) slot's icon so the target is always visible.</param>
/// <param name="Enter">The host toggle that activates the mode (from the hub's guaranteed clean state).</param>
internal readonly record struct AuthoringMode(
    string Id,
    string Label,
    BindingIconId Glyph,
    Action<ICreatorModeHost> Enter
);

/// <summary>
/// The workbench hub's authoring-mode table — the ONE place a mode is declared. Adding a mode is adding one entry to
/// <see cref="s_modes"/>; the hub picker, its diegetic label, and its activation dispatch all scale with zero other
/// edits. The two coupling-ceiling classes (<c>OverworldRenderNode</c> / <c>OverworldFrameSource</c>) never name this
/// type — they reach it only through <c>ForgeCommands</c>' primitive-typed forwarders and <c>BindingBarAdapter</c>'s
/// hub publish, exactly the escape pattern the tracker/forge registries already use.
/// </summary>
internal static class AuthoringModeRegistry {
    // Amendment order: index 0 = WORLD, so the hub opens highlighting it and one South-press reproduces the old direct
    // workbench -> world-sculpt entry (the doctrine-protected reveal ladder). SCULPT and TRACKER follow.
    private static readonly AuthoringMode[] s_modes = [
        new AuthoringMode(Id: "world", Label: "WORLD", Glyph: BindingIconId.CreatorPlace, Enter: static host => host.ToggleWorldSculptMode()),
        new AuthoringMode(Id: "creator", Label: "SCULPT", Glyph: BindingIconId.Target, Enter: static host => host.ToggleCreatorMode()),
        new AuthoringMode(Id: "tracker", Label: "TRACKER", Glyph: BindingIconId.CreatorRecord, Enter: static host => host.ToggleTrackerMode()),
        // FUTURE game-edit: exactly one more entry, no other file changes.
    ];

    /// <summary>The number of authoring modes on the hub.</summary>
    public static int Count => s_modes.Length;

    /// <summary>The mode id at <paramref name="index"/> (wrapped), e.g. <c>world</c>.</summary>
    public static string IdAt(int index) => s_modes[Wrap(index: index)].Id;

    /// <summary>The mode label at <paramref name="index"/> (wrapped), e.g. <c>WORLD</c>.</summary>
    public static string LabelAt(int index) => s_modes[Wrap(index: index)].Label;

    /// <summary>The mode glyph at <paramref name="index"/> (wrapped) — the hub tile + confirm-slot icon.</summary>
    public static BindingIconId GlyphAt(int index) => s_modes[Wrap(index: index)].Glyph;

    /// <summary>The display label for the mode whose <see cref="AuthoringMode.Id"/> is <paramref name="id"/> (the hub
    /// page id carries the id; the diegetic readout translates it back to the label so the embossed name matches the
    /// hub tile and the console echo). Falls back to the id uppercased if none matches.</summary>
    /// <param name="id">The mode id (the <c>hub-&lt;id&gt;</c> page suffix).</param>
    /// <returns>The mode's display label.</returns>
    public static string LabelForId(string id) {
        foreach (var mode in s_modes) {
            if (string.Equals(a: mode.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return mode.Label;
            }
        }

        return (id ?? string.Empty).ToUpperInvariant();
    }

    /// <summary>Enters the mode at <paramref name="index"/> (wrapped) through the host toggle. Called by
    /// <c>ForgeCommands.ActivateAuthoringMode</c>, never by the render node directly.</summary>
    /// <param name="host">The creator-mode host (the render node) whose toggles activate the surface.</param>
    /// <param name="index">The selected mode index.</param>
    public static void Enter(ICreatorModeHost host, int index) => s_modes[Wrap(index: index)].Enter(obj: host);

    // Cyclic index (the hub cursor wraps both directions), guarding an empty registry.
    private static int Wrap(int index) => ((Count == 0) ? 0 : (((index % Count) + Count) % Count));
}
