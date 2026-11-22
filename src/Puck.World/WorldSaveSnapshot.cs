using System.Globalization;
using Puck.Launcher;
using Puck.World.Machines;
using Puck.World.Transpiler;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Composes the snapshot the <c>world.save</c> verb and the <c>save</c> rule effect write: the authority half
/// (<see cref="WorldSessionCapture.Capture"/>) folds the session state the server owns, and then
/// <see cref="WorldSessionLevers.Fold"/> folds the presentation levers. Both keep every section the session left alone
/// as its author wrote it, so the snapshot of an untouched world is its authored document.</summary>
internal static class WorldSaveSnapshot {
    /// <summary>Writes a save snapshot, then compacts the journal through <paramref name="compact"/> once the write
    /// succeeded.</summary>
    internal static bool TryWrite(WorldDefinition snapshot, string target, WorldDefinitionSource source, Action compact,
        WorldMachineCatalog catalog, string catalogFingerprint, out string result) {
        if (target.EndsWith(".puck", StringComparison.OrdinalIgnoreCase)) {
            if (!WorldSourceSave.TrySave(definition: snapshot, path: target, catalog: catalog, catalogFingerprint: catalogFingerprint,
                bytesWritten: out var bytes, written: out var sources, reason: out result)) {
                return false;
            }
            var changes = sources.Count == 0 ? "unchanged" : $"{bytes} bytes in {string.Join(", ", sources.Select(WorldDocumentLabel.Of))}";
            result = $"{target} ({changes}, source preserved)";
        } else {
            var bytes = WorldDefinitionSerialization.SavePreservingBasis(definition: snapshot, path: target, catalog: catalog,
                catalogFingerprint: catalogFingerprint, basisPath: out var basisPath, imports: out var imports, note: out var note);
            var derivation = basisPath is not null || imports.Count > 0
                ? $", basis: {basisPath ?? "none"}, imports: {imports.Count.ToString(CultureInfo.InvariantCulture)}" : note.Length > 0 ? $", {note}" : string.Empty;
            result = $"{target} ({bytes} bytes{derivation})";
        }
        if (source.CompositionOrigin is { } composition && Puck.Abstractions.PuckPaths.Comparer.Equals(Path.GetFullPath(target), composition.StagedPath)) {
            Puck.Confinement.AtomicFile.WriteAllBytesDurably(composition.SavedDocument, File.ReadAllBytes(target));
            result = Puck.Abstractions.PuckPaths.Normalize(composition.SavedDocument);
        }
        compact();
        return true;
    }

    /// <summary>Composes the save snapshot of <paramref name="server"/>'s live definition.</summary>
    /// <param name="server">The authoritative server whose definition, population, and machines fold.</param>
    /// <param name="tick">The server's completed tick, the instant cycling and easing state settles at.</param>
    /// <param name="render">The live render levers.</param>
    /// <param name="pacing">The live present-rate control.</param>
    /// <param name="audio">The audio director owning the master-volume lever.</param>
    /// <param name="bindingBar">The live per-seat binding-bar visibility overrides.</param>
    /// <param name="editor">The live per-seat editor state, whose primary seat folds into <c>editor</c>.</param>
    /// <returns>The snapshot definition to serialize.</returns>
    public static WorldDefinition Compose(WorldServer server, ulong tick, WorldRenderSettings render, PresentPacingControl pacing, IWorldAudioLever audio, WorldBindingBarVisibility bindingBar, WorldEditorSeats editor) =>
        server.ExecuteAuthorityOperation(() => Fold(
            audio: audio,
            bindingBar: bindingBar,
            editor: editor,
            authority: WorldSessionCapture.Capture(
                definition: server.Definition,
                engineTick: server.CompletedEngineTicks,
                machines: server.Machines,
                population: server.Population,
                tick: tick
            ),
            pacing: pacing,
            render: render
        ));
    /// <summary>Folds the presentation levers into the authority half of a save snapshot.</summary>
    /// <param name="authority">The authority half (<see cref="WorldSessionCapture.Capture"/>).</param>
    /// <param name="render">The live render levers.</param>
    /// <param name="pacing">The live present-rate control.</param>
    /// <param name="audio">The audio director owning the master-volume lever.</param>
    /// <param name="bindingBar">The live per-seat binding-bar visibility overrides.</param>
    /// <param name="editor">The live per-seat editor state, whose primary seat folds into <c>editor</c>.</param>
    /// <returns>The snapshot definition to serialize.</returns>
    public static WorldDefinition Fold(WorldDefinition authority, WorldRenderSettings render, PresentPacingControl pacing, IWorldAudioLever audio, WorldBindingBarVisibility bindingBar, WorldEditorSeats editor) =>
        WorldSessionLevers.Fold(
            audio: audio,
            bindingBar: bindingBar,
            editor: editor,
            definition: authority,
            pacing: pacing,
            settings: render
        );
}
