using System.Text.Json.Nodes;

namespace Puck.World;

/// <summary>
/// The file-backed <see cref="IWorldNeighbourResolver"/> — reads a named neighbour's document straight off disk,
/// relative to a base directory resolved fresh on every call. The natural resolver for a locally-authored quilt: a
/// document names its neighbours by a <see cref="WorldReference.Document"/> locator relative to its own directory
/// (the island names <c>"shards/quilt-nw.world.json"</c>; a shard names <c>"quilt-ne.world.json"</c> beside itself
/// and <c>"../puck.world.json"</c> above it), so "relative to the document that names it" is the whole resolution
/// rule — <see cref="Path.Combine(string, string)"/>, never a catalog or a discovery step. The definition handed
/// back is read from the base directory, so every locator the neighbour authored is re-expressed against that base
/// (<see cref="ReexpressReferences"/>): the derived-corner walk compares two neighbours' locators for one third
/// document by string and resolves the winner beside the reading document, and both hold only when every locator
/// is spelled from the same place.
/// </summary>
/// <remarks>
/// <para><b>Read-only and parse-only</b>, mirroring <c>Server.WorldStorageNeighbourResolver</c>'s own contract
/// exactly: parses through <see cref="WorldJsonPayload.TryParse{T}(string, System.Text.Json.Serialization.Metadata.JsonTypeInfo{T},
/// out T, out string, bool)"/> and <see cref="WorldDefinitionMigrations.Apply"/> only — never
/// <see cref="WorldDefinitionValidator.Validate"/> — because the neighbour's own validity (which may in turn need its
/// own neighbour resolver, for a border of its own) is that world's own boot concern, not a proof this resolver
/// re-derives. A read that fails for any reason (missing file, unreadable, not valid UTF-8, does not parse) answers
/// <see cref="WorldNeighbourResolutionKind.Unavailable"/> by name rather than throwing.</para>
/// <para><b>The base directory is a callback, not a captured string</b>, so the same instance stays correct across a
/// live <c>world.load</c>/<c>world.reload</c>: <see cref="WorldDefinitionSource.SourcePath"/> is the tracked
/// document origin (see that record's own remarks — it moves the instant a rebuild's echo confirms it applied), and
/// a caller that hands this resolver <c>() =&gt; Path.GetDirectoryName(source.SourcePath)</c> gets a resolver whose
/// notion of "beside the document" tracks whichever document is currently loaded, without re-wiring on every swap.
/// A caller with no such tracked origin (the very first boot read, before a <see cref="WorldDefinitionSource"/>
/// exists) simply hands a constant callback instead.</para>
/// </remarks>
public sealed class WorldFileNeighbourResolver : IWorldNeighbourResolver {
    private readonly Func<string> m_baseDirectory;

    /// <summary>Initializes the resolver.</summary>
    /// <param name="baseDirectory">Resolves the directory a bare <see cref="WorldReference.Document"/> file name is
    /// combined against, evaluated fresh on every <see cref="Resolve"/> call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="baseDirectory"/> is <see langword="null"/>.</exception>
    public WorldFileNeighbourResolver(Func<string> baseDirectory) {
        ArgumentNullException.ThrowIfNull(argument: baseDirectory);

        m_baseDirectory = baseDirectory;
    }

    /// <inheritdoc/>
    public WorldNeighbourResolution Resolve(string document) {
        if (string.IsNullOrWhiteSpace(value: document)) {
            return WorldNeighbourResolution.Unavailable(reason: "the reference names no document");
        }

        string directory;
        string path;

        try {
            directory = m_baseDirectory();
            path = Path.GetFullPath(path: Path.Combine(
                path1: directory,
                path2: document
            ));
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{document}' does not resolve to a path this platform can express — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        if (!File.Exists(path: path)) {
            return WorldNeighbourResolution.Unavailable(reason: $"no local copy at '{path}'");
        }

        // Asked before the composition, never after: a held image that still stands for this path is exactly what
        // the composition below is about to answer from, so this reads the outcome rather than a record of one.
        var shared = WorldDefinitionFileSource.HoldsComposedDocument(resolvedPath: path);

        // Composes the neighbour's basis chain (a flat file passes through untouched), so a neighbour authored as a
        // delta proves its border with the same composed document it boots as.
        if (!WorldDefinitionFileSource.TryComposeDocumentTree(
            path: path,
            reason: out var composeReason,
            tree: out var tree
        )) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{path}' could not be read — {composeReason}");
        }

        ReexpressReferences(
            baseDirectory: directory,
            neighbourDirectory: (Path.GetDirectoryName(path: path) ?? directory),
            tree: tree!
        );

        // A reference into a first-fill draw site stays attached through this parse and is answered once the
        // site draws, the same two steps the neighbour's own boot takes (WorldDefinitionLoader), under the same
        // boot instance name, so the image proven here is the document the neighbour boots as.
        if (!WorldJsonPayload.TryParse(
            json: tree!.ToJsonString(),
            info: WorldJsonContext.Default.WorldDefinition,
            value: out var parsed,
            error: out var parseError,
            deferDrawSites: true
        )) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{path}' does not parse as {WorldDefinition.SchemaVersion} — {parseError}");
        }

        if (!WorldDrawBootResolver.TryResolve(
            definition: WorldDefinitionMigrations.Apply(definition: parsed),
            instanceIdentity: WorldDefinitionLoader.BootInstanceName,
            reason: out var drawReason,
            resolved: out var drawn
        )) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{path}' draw refused — {drawReason}");
        }

        if (!WorldStateDocumentValues.TryResolve(
            definition: drawn,
            reason: out var referenceReason
        )) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{path}' holds a state reference nothing fills — {referenceReason}");
        }

        return WorldNeighbourResolution.Resolved(
            definition: drawn,
            shared: shared
        );
    }
    // A references row's locator is relative to the document that authors it, the rule basis and imports follow;
    // the reader resolves it from its own base directory. A sibling's bare spelling re-expresses to itself. An
    // owner-form reference carries no locator and is left alone.
    private static void ReexpressReferences(JsonObject tree, string neighbourDirectory, string baseDirectory) {
        if (tree["references"] is not JsonArray rows) {
            return;
        }

        foreach (var row in rows) {
            if (
                (row is not JsonObject reference) ||
                (reference["document"] is not JsonValue locator) ||
                !locator.TryGetValue<string>(value: out var document) ||
                string.IsNullOrWhiteSpace(value: document) ||
                Path.IsPathRooted(path: document)
            ) {
                continue;
            }

            var absolute = Path.GetFullPath(path: Path.Combine(
                path1: neighbourDirectory,
                path2: document
            ));

            reference["document"] = Path.GetRelativePath(
                path: absolute,
                relativeTo: baseDirectory
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );
        }
    }
}
