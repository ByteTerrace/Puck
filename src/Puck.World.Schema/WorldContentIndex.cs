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
    /// <summary>Fetches the content a delivered document names by pin that this process does not hold, so the
    /// presentation's later reads by pin find it without touching the network. It runs where a delivery is decoded, on
    /// the stream's own thread. Content already held is not read, and content that cannot be fetched is left
    /// missing: its reader then says which pin, and the rest of the document still shows.</summary>
    /// <param name="definition">The delivered definition.</param>
    /// <param name="held">The store the sender's content is kept in, or <see langword="null"/> for the process's.</param>
    /// <param name="fetch">The sender's authorized fetch door, or <see langword="null"/> to fetch nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static void Fetch(WorldDefinition definition, ContentAddressedStore? held, Func<ContentPin, byte[]?>? fetch) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if ((fetch is null) || (definition.DocumentDirectory is not null)) {
            return;
        }

        var resolver = WorldProjectionContent.Resolver;

        foreach (var (pin, maximumBytes, admit) in Named(text: definition.Text)) {
            if (!resolver.Contains(held: held, pin: pin)) {
                _ = resolver.TryRead(admit: admit, content: out _, fetch: fetch, held: held, maximumBytes: maximumBytes, pin: pin);
            }
        }
    }
    /// <summary>Returns whether a projection names a pin among the content its recipient reads by pin, apart from its
    /// prototype bodies: the test an authority's fetch door applies to a recipient's current delivery, so a pin alone
    /// grants no access.</summary>
    /// <param name="projection">The projection as the recipient is delivered it.</param>
    /// <param name="pin">The requested pin.</param>
    /// <param name="maximumBytes">The most bytes content of the pin's family holds, when this returns
    /// <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a pinned content row of the projection carries the pin.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projection"/> is <see langword="null"/>.</exception>
    public static bool Names(WorldProjectionDocument projection, ContentPin pin, out int maximumBytes) {
        ArgumentNullException.ThrowIfNull(argument: projection);

        foreach (var (named, bound, _) in Named(text: projection.Text)) {
            if (named == pin) {
                maximumBytes = bound;

                return true;
            }
        }

        maximumBytes = 0;

        return false;
    }

    // Every pin a document reads content by, with the most bytes its family admits and the check fetched bytes must
    // pass before they are stored. A family of content row that carries a pin owes a branch here as well.
    private static IEnumerable<(ContentPin Pin, int MaximumBytes, Func<ReadOnlyMemory<byte>, bool> Admit)> Named(Puck.Text.TextFontCatalogDefinition? text) {
        foreach (var font in (text?.Fonts ?? [])) {
            if ((font is not null) && ContentPin.TryParse(pin: out var pin, text: font.Hash)) {
                yield return (pin, Puck.Text.FontAtlasSourceResolver.MaxPinnedFontBytes, static bytes => Puck.Text.OpenTypeFontFace.HasFontSignature(bytes: bytes.Span));
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
