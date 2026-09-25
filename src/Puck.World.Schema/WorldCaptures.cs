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
/// <param name="Station">The stable name — the manifest's <c>station</c> field and the first part of the capture's
/// generated name (<see cref="CaptureName"/>, <c>&lt;station&gt;~&lt;tick&gt;</c>). A <see cref="CellName"/>:
/// dot-free, non-empty, free of the reserved character set, and free of <c>~</c>.</param>
/// <param name="Ticks">The exact simulation ticks (completed-tick coordinates, ascending, none repeated) this
/// station arms a capture at. Capacity: <see cref="WorldCapturesCapacity.MaxTicksPerRow"/>.</param>
/// <param name="Palette">The per-pixel census's material table — at least one entry, at most
/// <see cref="WorldCapturesCapacity.MaxPaletteEntriesPerRow"/>, unique <see cref="WorldCapturePaletteEntry.Material"/>
/// indices.</param>
/// <param name="Instance">The render-graph instance whose output the station captures, or <see langword="null"/> (the
/// default) for the root, the frame the display shows. <see cref="WorldViewGraphs.WorldInstance"/> captures the SDF
/// world before any <c>render.extensions</c> pass or the overlay is drawn over it.</param>
public sealed record WorldCaptureRow(CellName Station, IReadOnlyList<ulong> Ticks, IReadOnlyList<WorldCapturePaletteEntry> Palette,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Instance = null) {
    /// <summary>The generated name of one capture: the station and the tick joined as a file name
    /// (<see cref="GeneratedName.JoinFile"/>), <c>lattice~860</c>. The capture's frame is this name with <c>.png</c>,
    /// and a parity comparison's evidence directory for it is this name. The tick is decimal digits and the station
    /// carries no <see cref="GeneratedName.FileJoiner"/>, so the station and the tick are the parts either side of the
    /// joiner.</summary>
    /// <param name="station">The station name.</param>
    /// <param name="tick">The tick the capture was armed for.</param>
    /// <returns>The capture's name.</returns>
    /// <exception cref="ArgumentException"><paramref name="station"/> is empty or carries
    /// <see cref="GeneratedName.FileJoiner"/>.</exception>
    public static string CaptureName(string station, ulong tick) => GeneratedName.JoinFile(
        station,
        tick.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
    );
}
/// <summary>
/// The <c>captures</c> document section — tick-scheduled composed-frame captures, arming the same capture path
/// <c>world.screenshot</c> uses (the render graph's root, or the instance a row names) at exact
/// SIMULATION ticks rather than on a console request, so two backends stepping the identical document and input
/// capture the identical moment by construction. OPTIONAL, like <c>rules</c>/<c>probes</c>: a document declaring
/// none is unchanged. Boot-authored only — no mutation kind targets it and no grant subject names it, exactly like
/// <c>Simulation</c>/<c>Portals</c>; a capture schedule is topology, not live state.
/// </summary>
/// <param name="Rows">The scheduled stations. Station names are unique ignoring case, since each names capture
/// files; capacity
/// <see cref="WorldCapturesCapacity.MaxRows"/>.</param>
/// <param name="Directory">The output directory every scheduled capture in this document writes into, and where
/// <c>manifest.json</c> (the <c>puck.parity.manifest.v1</c> document) is written whenever a capture ends, whether it
/// produced a frame or a named refusal. Absent, the default, is a <c>captures</c> directory under the run's state
/// root (<c>--state-dir</c>, else the per-user state directory), so a boot that names no directory writes nothing
/// under its working directory; an authored relative path resolves beside the document (<see cref="WorldDocumentPaths"/>). A
/// <c>--capture-dir</c> boot flag overrides either for a deployment run (the <c>--state-dir</c> pattern), so two
/// backend legs of the same document can target sibling directories without two document copies.</param>
public sealed record WorldCapturesSection(IReadOnlyList<WorldCaptureRow> Rows, string? Directory = null) {
    /// <summary>The directory, under the run's state root, captures write into when <see cref="Directory"/> is
    /// <see langword="null"/>.</summary>
    public const string DefaultDirectoryName = "captures";

    /// <summary>Returns the rooted directory this section's captures write into, before any <c>--capture-dir</c>
    /// override: <see cref="Directory"/> resolved beside the document when authored, else
    /// <see cref="DefaultDirectoryName"/> under <paramref name="stateRoot"/>.</summary>
    /// <param name="stateRoot">The run's rooted state directory.</param>
    /// <param name="documentDirectory">The document's directory (<see cref="WorldDefinition.DocumentDirectory"/>), or
    /// <see langword="null"/> for a document that has none.</param>
    /// <returns>The rooted capture directory.</returns>
    /// <exception cref="ArgumentException"><paramref name="stateRoot"/> is empty or not rooted.</exception>
    /// <exception cref="InvalidOperationException">An authored <see cref="Directory"/> is relative and the document
    /// has no directory to resolve it beside.</exception>
    public string ResolveDirectory(string stateRoot, string? documentDirectory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: stateRoot);

        if (!Path.IsPathRooted(path: stateRoot)) {
            throw new ArgumentException(
                message: $"the state root '{stateRoot}' is not rooted",
                paramName: nameof(stateRoot)
            );
        }

        return ((Directory is null)
            ? Path.Combine(
                path1: stateRoot,
                path2: DefaultDirectoryName
            )
            : WorldDocumentPaths.Resolve(
                documentDirectory: documentDirectory,
                path: Directory
            )
        );
    }
}
/// <summary>The <c>captures</c> section's capacity ceilings — small and fixed, since a capture schedule is authored
/// topology for a short deterministic proving run, never a live-growing table.</summary>
public static class WorldCapturesCapacity {
    /// <summary>The largest admitted palette-entry count per station.</summary>
    public const int MaxPaletteEntriesPerRow = 16;
    /// <summary>The largest admitted station-row count.</summary>
    public const int MaxRows = 32;
    /// <summary>The largest admitted tick count per station.</summary>
    public const int MaxTicksPerRow = 16;
}
