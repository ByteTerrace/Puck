using Puck.Abstractions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

using Puck.Abstractions.Machines;

namespace Puck.World;

/// <summary>
/// The file-backed <see cref="IWorldNeighbourResolver"/> — reads a named neighbour's document straight off disk,
/// relative to a base directory resolved fresh on every call. The natural resolver for a locally-authored quilt: a
/// document names its neighbours by a <see cref="WorldReference.Document"/> locator relative to its own directory
/// (the island names <c>"shards/quilt-nw"</c>; a shard names <c>"quilt-ne"</c> beside itself and <c>"../puck"</c>
/// above it), so "relative to the document that names it" is the whole resolution rule, and the file read is that
/// name's document file (<see cref="WorldDocumentName"/>), never a catalog or a discovery step. The definition handed
/// back is read from the base directory, so every locator the neighbour authored is re-expressed against that base
/// (<see cref="ReexpressReferences"/>): the derived-corner walk compares two neighbours' locators for one third
/// document by string and resolves the winner beside the reading document, and both hold only when every locator
/// is spelled from the same place.
/// </summary>
/// <remarks>
/// <para><b>Read-only and parse-only</b>, mirroring <c>Server.WorldStorageNeighbourResolver</c>'s own contract
/// exactly: parses through <see cref="WorldJsonPayload.TryParse{T}(string, System.Text.Json.Serialization.Metadata.JsonTypeInfo{T},
/// out T, out string, bool)"/> only — never
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
    // A neighbour's resolved document is a function of the composed image it was parsed from and the directory its
    // locators were re-expressed against, so it is held against the image itself: while the image stands, every
    // resolver reading that neighbour from that directory — a boot's admission and its completion, a reload — is
    // answered without parsing it again, and a recomposed image is a different key. Nothing holds an image alive.
    private static readonly ConditionalWeakTable<byte[], ConcurrentDictionary<string, WorldDefinition>> ResolvedByImage = new();

    private readonly Func<string> m_baseDirectory;
    private readonly IMachineValidationCatalog? m_catalog;
    private readonly string m_catalogFingerprint;

    /// <summary>Initializes the resolver.</summary>
    /// <param name="baseDirectory">Resolves the directory a <see cref="WorldReference.Document"/> name is
    /// combined against, evaluated fresh on every <see cref="Resolve"/> call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="baseDirectory"/> is <see langword="null"/>.</exception>
    /// <param name="catalogFingerprint">The stable metadata fingerprint partitioning composed neighbour images.</param>
    /// <param name="catalog">The selected host machine catalog used while composing neighbour documents, or null for structural composition.</param>
    public WorldFileNeighbourResolver(Func<string> baseDirectory, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
        ArgumentNullException.ThrowIfNull(argument: baseDirectory);

        m_baseDirectory = baseDirectory;
        m_catalogFingerprint = catalogFingerprint;
        m_catalog = catalog;
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

    /// <inheritdoc/>
    public WorldNeighbourResolution Resolve(string document) {
        if (string.IsNullOrWhiteSpace(value: document)) {
            return WorldNeighbourResolution.Unavailable(reason: "the reference names no document");
        }

        WorldBootWork.Count(kind: WorldBootWork.NeighbourResolves);

        // One spelling per directory, so every resolver over the same directory shares the images held for it.
        var directory = PuckPaths.Normalize(path: m_baseDirectory());

        if (!WorldDefinitionFileSource.TryResolveDocumentIn(
            directory: directory,
            documentPath: out var path,
            name: document,
            reason: out var nameReason,
            sourcePath: out _
        )) {
            return WorldNeighbourResolution.Unavailable(reason: nameReason);
        }

        if (!File.Exists(path: path)) {
            return WorldNeighbourResolution.Unavailable(reason: $"no local copy at '{path}'");
        }

        // Asked before the composition, never after: a held image that still stands for this path is exactly what
        // the composition below is about to answer from, so this reads the outcome rather than a record of one.
        var shared = WorldDefinitionFileSource.TryGetComposedImage(
            catalogFingerprint: m_catalogFingerprint,
            composedJson: out var standing,
            resolvedPath: path
        );

        if (
            (standing is not null) &&
            ResolvedByImage.TryGetValue(
            key: standing,
            value: out var resolvedFrom
        ) &&
            resolvedFrom.TryGetValue(
            key: directory,
            value: out var held
        )
        ) {
            return WorldNeighbourResolution.Resolved(
                definition: held,
                shared: true
            );
        }

        // Composes the neighbour's basis chain (a flat file passes through untouched), so a neighbour authored as a
        // delta proves its border with the same composed document it boots as.
        if (!WorldDefinitionFileSource.TryComposeDocumentTree(
            catalog: m_catalog,
            catalogFingerprint: m_catalogFingerprint,
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
        WorldBootWork.Count(kind: WorldBootWork.Parses);

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
            definition: parsed,
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

        if (WorldDefinitionFileSource.PeekComposedImage(
            catalogFingerprint: m_catalogFingerprint,
            resolvedPath: path
        ) is { } image) {
            ResolvedByImage.GetOrCreateValue(key: image)[directory] = drawn;
        }

        return WorldNeighbourResolution.Resolved(
            definition: drawn,
            shared: shared
        );
    }
}
