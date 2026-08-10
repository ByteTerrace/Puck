namespace Puck.World;

/// <summary>
/// The file-backed <see cref="IWorldNeighbourResolver"/> — reads a named neighbour's document straight off disk,
/// relative to a base directory resolved fresh on every call. The natural resolver for a locally-authored quilt: the
/// four shipped <c>quilt-*.world.json</c> documents name each other by a bare <see cref="WorldReference.Document"/>
/// file name (e.g. <c>"quilt-ne.world.json"</c>) and live SIDE BY SIDE in the same directory as the document doing
/// the naming, so "beside the document that names it" is the whole resolution rule — <see cref="Path.Combine(string,
/// string)"/>, never a catalog or a discovery step.
/// </summary>
/// <remarks>
/// <para><b>Read-only and parse-only</b>, mirroring <see cref="Server.WorldStorageNeighbourResolver"/>'s own contract
/// exactly: parses through <see cref="WorldJsonPayload.TryParse{T}(string, System.Text.Json.Serialization.Metadata.JsonTypeInfo{T},
/// out T, out string)"/> and <see cref="WorldDefinitionMigrations.Apply"/> only — never
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
            path = Path.GetFullPath(path: Path.Combine(path1: directory, path2: document));
        } catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{document}' does not resolve to a path this platform can express — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        if (!File.Exists(path: path)) {
            return WorldNeighbourResolution.Unavailable(reason: $"no local copy at '{path}'");
        }

        string json;

        try {
            json = File.ReadAllText(path: path);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{path}' could not be read — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        if (!WorldJsonPayload.TryParse(json: json, info: WorldJsonContext.Default.WorldDefinition, value: out var parsed, error: out var parseError)) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{path}' does not parse as {WorldDefinition.SchemaVersion} — {parseError}");
        }

        return WorldNeighbourResolution.Resolved(definition: WorldDefinitionMigrations.Apply(definition: parsed));
    }
}
