using Puck.Assets;

namespace Puck.World;

/// <summary>Indexes the content a document this process loaded from a directory names by pin, so the process's one
/// resolver (<see cref="WorldProjectionContent.Resolver"/>) answers each pin from the file beside that document. A
/// document another authority delivered names the same content by the same pins and has no directory, so its content
/// is found here whenever a loaded world ships it, whichever of the two is read first.</summary>
/// <remarks>Indexing opens no file: it records where a pin's bytes are said to be, and the resolver's reader hashes
/// them against the pin at every read. Only a document with a directory is indexed, and only a document this process
/// loaded from a file has one (<see cref="WorldDefinition.DocumentDirectory"/> is never serialized), so no path a
/// delivery names is ever recorded. A family of content row that carries a pin owes a branch here.</remarks>
public static class WorldContentIndex {
    /// <summary>Records every pinned content file a loaded document names. A document with no directory, and a member
    /// the previous document already carried as the same instance, record nothing.</summary>
    /// <param name="definition">The document that became live.</param>
    /// <param name="previous">The document it replaced, or <see langword="null"/> for the first.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static void Register(WorldDefinition definition, WorldDefinition? previous = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (definition.DocumentDirectory is not { } directory) {
            return;
        }

        var moved = !string.Equals(a: directory, b: previous?.DocumentDirectory, comparisonType: StringComparison.Ordinal);

        if ((definition.Text is { Fonts: { } fonts } text) && (moved || !ReferenceEquals(objA: text, objB: previous?.Text))) {
            foreach (var font in fonts) {
                if (font is not null) {
                    RegisterFile(directory: directory, hash: font.Hash, source: font.Source);
                }
            }
        }
    }

    // A row the validator would refuse (no pin, a path that resolves nowhere) records nothing; its reader refuses it by
    // name.
    private static void RegisterFile(string directory, string source, string hash) {
        if (
            ContentPin.TryParse(pin: out var pin, text: hash) &&
            WorldDocumentPaths.TryResolve(documentDirectory: directory, path: source, reason: out _, resolved: out var resolved)
        ) {
            WorldProjectionContent.Resolver.Register(path: Path.GetFullPath(path: resolved), pin: pin);
        }
    }
}
