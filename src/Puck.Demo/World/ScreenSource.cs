namespace Puck.Demo.World;

/// <summary>What a screen surface DISPLAYS — the source half of the wiring model, pure data. A screen shows exactly one
/// source, chosen from a small closed set of kinds; the developer wires it with a verb (<c>world.wire</c>), never a
/// heuristic. Symmetric to <see cref="CameraEye"/>: the eye produces a feed, the wire routes a feed (or any other
/// source) onto a surface.</summary>
public enum ScreenSourceKind {
    /// <summary>Nothing wired — the surface falls back to its flat/procedural material (the animated test-card, NOT
    /// black). The absence of a wire, expressed explicitly so a verb can clear one.</summary>
    None,
    /// <summary>A booted GUEST viewport (today's cabinets — a whole other machine's framebuffer, not this engine's own
    /// content): <see cref="ScreenSourceRef.Index"/> is the console index, and the surface samples that machine's
    /// native framebuffer — the overworld's existing screen behavior, now one wire kind among several. Named "Guest"
    /// (not "Brick") so the vocabulary reads for any hosted foreign content, not only a GamingBrick cabinet.</summary>
    Guest,
    /// <summary>A CAMERA view (a <see cref="Puck.SdfVm.Views.ViewStack"/>-registered <see cref="Puck.SdfVm.Views.SdfCameraView"/>):
    /// <see cref="ScreenSourceRef.Index"/> is the feed index, and the surface samples that view's live offscreen
    /// render of the world from its eye. Named "Camera" (not "Feed") so the wiring grammar names WHAT it samples,
    /// matching <c>world.camera</c>'s own vocabulary.</summary>
    Camera,
    /// <summary>A NAMED host feed — a CPU-published surface the host exposes by name (the robot's emote face is just a
    /// named feed; a shop's ticker, a title card, any host-drawn image is another). <see cref="ScreenSourceRef.Name"/>
    /// selects it; no consumer-specific channel exists.</summary>
    Named,
}

/// <summary>
/// One wiring SOURCE descriptor — what a screen surface samples, as normalized data. A closed discriminated set (see
/// <see cref="ScreenSourceKind"/>): a guest viewport by index, a camera feed by index, a named host feed by name, or
/// nothing. The host resolves a source to a concrete image-view handle at render time through its providers; the
/// descriptor itself names no handles and knows no engine types, so a wiring table round-trips as plain document data.
/// </summary>
/// <param name="Kind">Which source family this descriptor selects.</param>
/// <param name="Index">The guest console index or camera feed index (ignored for <see cref="ScreenSourceKind.Named"/>/
/// <see cref="ScreenSourceKind.None"/>).</param>
/// <param name="Name">The named host feed's name (only for <see cref="ScreenSourceKind.Named"/>; else null).</param>
public readonly record struct ScreenSourceRef(ScreenSourceKind Kind, int Index, string? Name) {
    /// <summary>The explicit "nothing wired" source (the surface shows its flat/procedural fallback).</summary>
    public static ScreenSourceRef None => new(Kind: ScreenSourceKind.None, Index: 0, Name: null);

    /// <summary>A guest-viewport source for a console index.</summary>
    /// <param name="consoleIndex">The console index to sample.</param>
    public static ScreenSourceRef Guest(int consoleIndex) => new(Kind: ScreenSourceKind.Guest, Index: consoleIndex, Name: null);

    /// <summary>A camera-feed source for a feed index.</summary>
    /// <param name="feedIndex">The view index (a <c>world:N</c>-named <see cref="Puck.SdfVm.Views.SdfCameraView"/>) to sample.</param>
    public static ScreenSourceRef Camera(int feedIndex) => new(Kind: ScreenSourceKind.Camera, Index: feedIndex, Name: null);

    /// <summary>A named-host-feed source.</summary>
    /// <param name="name">The host feed name.</param>
    public static ScreenSourceRef Named(string name) => new(Kind: ScreenSourceKind.Named, Index: 0, Name: name);

    /// <summary>A short human-readable form for narration (<c>guest:2</c>, <c>camera:0</c>, <c>named:emotes</c>,
    /// <c>none</c>).</summary>
    public override string ToString() =>
        (Kind switch {
            ScreenSourceKind.Guest => $"guest:{Index}",
            ScreenSourceKind.Camera => $"camera:{Index}",
            ScreenSourceKind.Named => $"named:{Name}",
            _ => "none",
        });
}

/// <summary>One wiring-table entry: the source a specific screen surface index displays. The whole table is the wiring
/// model — a set of these, at most one per screen index (a later entry for a taken index replaces the earlier one).
/// Lives in the world document and is edited by <c>world.wire</c> verbs.</summary>
/// <param name="ScreenIndex">The screen-surface slot (0..<c>SdfProgramBuilder.MaxScreenSurfaces</c> - 1) this entry
/// wires.</param>
/// <param name="Source">The source that screen displays.</param>
public readonly record struct ScreenSource(int ScreenIndex, ScreenSourceRef Source);
