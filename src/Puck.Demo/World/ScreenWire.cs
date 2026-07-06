namespace Puck.Demo.World;

/// <summary>What a screen surface DISPLAYS — the source half of the wiring model, pure data. A screen shows exactly one
/// source, chosen from a small closed set of kinds; the developer wires it with a verb (<c>world.wire</c>), never a
/// heuristic. Symmetric to <see cref="CameraEye"/>: the eye produces a feed, the wire routes a feed (or any other
/// source) onto a surface.</summary>
public enum ScreenWireKind {
    /// <summary>Nothing wired — the surface falls back to its flat/procedural material (the animated test-card, NOT
    /// black). The absence of a wire, expressed explicitly so a verb can clear one.</summary>
    None,
    /// <summary>A booted BRICK viewport (today's cabinets): <see cref="ScreenWireSource.Index"/> is the console index,
    /// and the surface samples that machine's native framebuffer — the overworld's existing screen behavior, now one
    /// wire kind among several.</summary>
    Brick,
    /// <summary>A camera FEED (a <see cref="Overworld.CameraFeedEngine"/> pool slot): <see cref="ScreenWireSource.Index"/>
    /// is the feed index, and the surface samples that feed's live offscreen render of the world from its eye.</summary>
    Feed,
    /// <summary>A NAMED host feed — a CPU-published surface the host exposes by name (the robot's emote face is just a
    /// named feed; a shop's ticker, a title card, any host-drawn image is another). <see cref="ScreenWireSource.Name"/>
    /// selects it; no consumer-specific channel exists.</summary>
    Named,
}

/// <summary>
/// One wiring SOURCE descriptor — what a screen surface samples, as normalized data. A closed discriminated set (see
/// <see cref="ScreenWireKind"/>): a brick viewport by index, a camera feed by index, a named host feed by name, or
/// nothing. The host resolves a source to a concrete image-view handle at render time through its providers; the
/// descriptor itself names no handles and knows no engine types, so a wiring table round-trips as plain document data.
/// </summary>
/// <param name="Kind">Which source family this descriptor selects.</param>
/// <param name="Index">The brick console index or camera feed index (ignored for <see cref="ScreenWireKind.Named"/>/
/// <see cref="ScreenWireKind.None"/>).</param>
/// <param name="Name">The named host feed's name (only for <see cref="ScreenWireKind.Named"/>; else null).</param>
public readonly record struct ScreenWireSource(ScreenWireKind Kind, int Index, string? Name) {
    /// <summary>The explicit "nothing wired" source (the surface shows its flat/procedural fallback).</summary>
    public static ScreenWireSource None => new(Kind: ScreenWireKind.None, Index: 0, Name: null);

    /// <summary>A brick-viewport source for a console index.</summary>
    /// <param name="consoleIndex">The console index to sample.</param>
    public static ScreenWireSource Brick(int consoleIndex) => new(Kind: ScreenWireKind.Brick, Index: consoleIndex, Name: null);

    /// <summary>A camera-feed source for a feed index.</summary>
    /// <param name="feedIndex">The <see cref="Overworld.CameraFeedEngine"/> pool index to sample.</param>
    public static ScreenWireSource Feed(int feedIndex) => new(Kind: ScreenWireKind.Feed, Index: feedIndex, Name: null);

    /// <summary>A named-host-feed source.</summary>
    /// <param name="name">The host feed name.</param>
    public static ScreenWireSource Named(string name) => new(Kind: ScreenWireKind.Named, Index: 0, Name: name);

    /// <summary>A short human-readable form for narration (<c>brick:2</c>, <c>feed:0</c>, <c>named:emotes</c>,
    /// <c>none</c>).</summary>
    public override string ToString() =>
        (Kind switch {
            ScreenWireKind.Brick => $"brick:{Index}",
            ScreenWireKind.Feed => $"feed:{Index}",
            ScreenWireKind.Named => $"named:{Name}",
            _ => "none",
        });
}

/// <summary>One wiring-table entry: the source a specific screen surface index displays. The whole table is the wiring
/// model — a set of these, at most one per screen index (a later entry for a taken index replaces the earlier one).
/// Lives in the world document and is edited by <c>world.wire</c> verbs.</summary>
/// <param name="ScreenIndex">The screen-surface slot (0..<c>SdfProgramBuilder.MaxScreenSurfaces</c> - 1) this entry
/// wires.</param>
/// <param name="Source">The source that screen displays.</param>
public readonly record struct ScreenWire(int ScreenIndex, ScreenWireSource Source);
