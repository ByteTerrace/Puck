namespace Puck.World;

/// <summary>
/// One material bucket a capture's per-pixel census sorts against: the nearest-color match in a station's authored
/// palette, since the composed render target carries pixel colors, not a per-pixel material-id buffer, and this is
/// the mechanically honest fallback the render path actually supports (see
/// <c>Puck.World.WorldCaptureScheduler</c>'s own remarks for the exact matching rule).
/// </summary>
/// <param name="Material">The material's index — the manifest's <c>census</c> key. Author-chosen, unique within a
/// row; not read against any other section's material table.</param>
/// <param name="Color">The material's reference color, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (alpha ignored — the
/// composed frame carries none).</param>
public sealed record WorldCapturePaletteEntry(int Material, string Color);
/// <summary>
/// One tick-scheduled capture station: a stable name, the exact engine ticks it arms a composed-frame capture at,
/// and the palette its per-pixel census sorts against. A station carries no camera reference of its own — what the
/// composed frame shows at a given tick is the document's own doing (a <c>state</c> row plus <c>rules</c> driving a
/// camera program's <c>select</c> op; see <c>docs</c>/<c>views.md</c>), so two backends that step the identical
/// document capture the identical moment by construction, and this row only says WHEN to look, never AT WHAT.
/// </summary>
/// <param name="Station">The stable name — the manifest's <c>station</c> field and the frame filename's own
/// prefix (<c>&lt;station&gt;-&lt;tick&gt;.png</c>). A <see cref="WorldCellName"/>: dot-free, non-empty, free of the
/// reserved character set.</param>
/// <param name="Ticks">The exact simulation ticks (completed-tick coordinates, ascending, none repeated) this
/// station arms a capture at. Capacity: <see cref="WorldCapturesCapacity.MaxTicksPerRow"/>.</param>
/// <param name="Palette">The per-pixel census's material table — at least one entry, at most
/// <see cref="WorldCapturesCapacity.MaxPaletteEntriesPerRow"/>, unique <see cref="WorldCapturePaletteEntry.Material"/>
/// indices.</param>
public sealed record WorldCaptureRow(WorldCellName Station, IReadOnlyList<ulong> Ticks, IReadOnlyList<WorldCapturePaletteEntry> Palette);
/// <summary>
/// The <c>captures</c> document section — tick-scheduled composed-frame captures, arming the same capture path
/// <c>world.screenshot</c> uses (<c>SdfWorldRender.RequestCapture</c>/<c>SdfEngineNode.RequestCapture</c>) at exact
/// SIMULATION ticks rather than on a console request, so two backends stepping the identical document and input
/// capture the identical moment by construction. OPTIONAL, like <c>rules</c>/<c>probes</c>: a document declaring
/// none is unchanged. Boot-authored only — no mutation kind targets it and no grant subject names it, exactly like
/// <c>Simulation</c>/<c>Portals</c>; a capture schedule is topology, not live state.
/// </summary>
/// <param name="Directory">The output directory every scheduled capture in this document writes into, and where
/// <c>manifest.json</c> (the <c>puck.parity.manifest.v1</c> document) lands once at least one capture has landed —
/// relative to the process's current directory unless rooted. A <c>--capture-dir</c> boot flag overrides this for a
/// deployment run (the <c>--state-dir</c> pattern), so two backend legs of the same document can target sibling
/// directories without two document copies.</param>
/// <param name="Rows">The scheduled stations. Station names are unique; capacity
/// <see cref="WorldCapturesCapacity.MaxRows"/>.</param>
public sealed record WorldCapturesSection(string Directory, IReadOnlyList<WorldCaptureRow> Rows);
/// <summary>The <c>captures</c> section's capacity ceilings — small and fixed, since a capture schedule is authored
/// topology for a short deterministic proving run, never a live-growing table.</summary>
public static class WorldCapturesCapacity {
    /// <summary>The largest admitted station-row count.</summary>
    public const int MaxRows = 32;
    /// <summary>The largest admitted tick count per station.</summary>
    public const int MaxTicksPerRow = 16;
    /// <summary>The largest admitted palette-entry count per station.</summary>
    public const int MaxPaletteEntriesPerRow = 16;
}
