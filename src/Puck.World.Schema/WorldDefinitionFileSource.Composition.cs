using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;

namespace Puck.World;

public static partial class WorldDefinitionFileSource {
    /// <summary>Composes <paramref name="rootBytes"/>' basis chain over <paramref name="source"/> — the same merge,
    /// cycle-refusal, and depth-cap (<see cref="WorldDocumentBasis.MaxChainDepth"/>) logic a directory load runs,
    /// generalized onto any <see cref="IWorldDocumentSource"/>. The caller has already read the root document's own
    /// bytes (a directory caller via <c>File.ReadAllBytes</c>, a storage caller via its own blob read) — this
    /// composes everything ABOVE the root in the chain.</summary>
    /// <param name="source">The document byte source basis references resolve against.</param>
    /// <param name="rootResolvedName">The root's own canonical resolved name (see
    /// <see cref="IWorldDocumentSource.TryRead"/>'s <c>resolvedName</c> contract) — seeds cycle detection.</param>
    /// <param name="rootBytes">The root document's own already-read raw bytes.</param>
    /// <param name="composed">The composed tree (basis member stripped) when the root named a basis and every
    /// ancestor composed; <see langword="null"/> when the root names no basis, or its bytes are not a parseable JSON
    /// object at all — in either case the caller's own strict parse of its own already-decoded bytes owns the
    /// refusal wording, and must never dereference this as non-null.</param>
    /// <param name="chainBytes">The ordered raw bytes, root first then each basis ancestor in resolution order; a
    /// single-element list (just the root) whenever <paramref name="composed"/> is <see langword="null"/>.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the chain composed (or the root carries no basis).</returns>
    public static bool TryComposeChain(IWorldDocumentSource source, string rootResolvedName, byte[] rootBytes, out JsonObject? composed, out IReadOnlyList<byte[]> chainBytes, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: source);

        composed = null;

        if (!TryWalkChain(
            chain: out var chain,
            reason: out reason,
            rootBytes: rootBytes,
            rootResolvedName: rootResolvedName,
            source: source
        )) {
            chainBytes = [rootBytes];

            return false;
        }

        chainBytes = [.. chain.Select(selector: static link => link.Bytes)];

        if (chain.Count == 1) {
            return true;
        }

        var objects = new JsonObject[chain.Count];

        for (var index = 0; (index < chain.Count); index++) {
            objects[index] = chain[index].Parsed!;
        }

        var mergedFromTop = objects[^1];

        for (var index = (chain.Count - 2); (index >= 0); index--) {
            // Each link's tree was parsed for this walk alone, so the merge consumes it.
            var overlay = objects[index];

            overlay.Remove(propertyName: WorldDocumentBasis.BasisMemberName);

            if (!WorldDocumentBasis.TryMergeInto(
                consume: true,
                target: mergedFromTop,
                overlay: overlay,
                reason: out var mergeReason
            )) {
                reason = $"{chain[index].ResolvedName} over {chain[(index + 1)].ResolvedName}: {mergeReason}";
                composed = null;

                return false;
            }

        }

        composed = mergedFromTop;

        return true;
    }
    /// <summary>Composes <paramref name="rootBytes"/>' whole basis-and-imports graph over <paramref name="source"/>
    /// — the generalization of <see cref="TryComposeChain"/> that additionally resolves the root's (and every
    /// ancestor's) own <c>imports</c> list (see <see cref="WorldDocumentBasis"/>'s remarks). The caller has already
    /// read the root document's own bytes.</summary>
    /// <param name="source">The document byte source basis/import references resolve against.</param>
    /// <param name="rootResolvedName">The root's own canonical resolved name — seeds cycle detection.</param>
    /// <param name="rootBytes">The root document's own already-read raw bytes.</param>
    /// <param name="composed">The composed tree (basis/imports members stripped) when the root named either and
    /// every reference composed; <see langword="null"/> when the root names neither, or its bytes are not a
    /// parseable JSON object at all — in either case the caller's own strict parse of its own already-decoded bytes
    /// owns the refusal wording, and must never dereference this as non-null.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <param name="catalogFingerprint">Stable metadata fingerprint used to partition the composition cache.</param>
    /// <param name="catalog">The explicit machine catalog used for metadata rewriting, or <see langword="null"/> for structural composition without provider metadata rewriting.</param>
    /// <returns><see langword="true"/> when the graph composed (or the root names neither basis nor imports).</returns>
    /// <param name="origins">Optional authored scalar origins. A flat root returns its tracked tree when supplied;
    /// reused images must carry origins so every retained scalar identifies the document and value that authored it.</param>
    /// <param name="retainRoot">Whether to retain the root image after composition. Shared imports and bases are retained either way.</param>
    public static bool TryComposeChainWithImports(IWorldDocumentSource source, string rootResolvedName, byte[] rootBytes, out JsonObject? composed, out string reason, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null, WorldDocumentOrigins? origins = null, bool retainRoot = true) => TryComposeGraph(
        catalog: catalog,
        catalogFingerprint: catalogFingerprint,
        chain: out _,
        composed: out composed,
        composedJson: out _,
        origins: origins,
        parse: true,
        reason: out reason,
        retainRoot: retainRoot,
        rootBytes: rootBytes,
        rootResolvedName: rootResolvedName,
        source: source
    );
    /// <summary>Composes <paramref name="rootBytes"/>' whole basis-and-imports graph exactly as
    /// <see cref="TryComposeChainWithImports"/> does, and hands back the composed document as its compact UTF-8 JSON:
    /// the held image's own bytes when one answers, never parsed into a tree, and otherwise the composed tree written
    /// once. A loader reads the document from these bytes.</summary>
    /// <param name="source">The document byte source basis/import references resolve against.</param>
    /// <param name="rootResolvedName">The root's own canonical resolved name — seeds cycle detection.</param>
    /// <param name="rootBytes">The root document's own already-read raw bytes.</param>
    /// <param name="composedJson">The composed document's compact UTF-8 JSON when the root named a basis or imports
    /// and every reference composed; <see langword="null"/> exactly where <see cref="TryComposeChainWithImports"/>'s
    /// tree is. A held image's array is shared and is never to be written.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <param name="catalogFingerprint">Stable metadata fingerprint used to partition the composition cache.</param>
    /// <param name="catalog">The explicit machine catalog used for metadata rewriting, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the graph composed (or the root names neither basis nor imports).</returns>
    public static bool TryComposeChainBytes(IWorldDocumentSource source, string rootResolvedName, byte[] rootBytes, out byte[]? composedJson, out string reason, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) =>
        TryComposeChainBytes(catalog: catalog, catalogFingerprint: catalogFingerprint, chain: out _, composedJson: out composedJson, reason: out reason, rootBytes: rootBytes, rootResolvedName: rootResolvedName, source: source);
    // The composition above, also handing back every document it read, each once, the root first (null where
    // `composedJson` is): what the loader's content pin is taken over.
    private static bool TryComposeChainBytes(IWorldDocumentSource source, string rootResolvedName, byte[] rootBytes, out byte[]? composedJson, out IReadOnlyList<WorldComposedLink>? chain, out string reason, string catalogFingerprint, IMachineValidationCatalog? catalog) {
        var composedOk = TryComposeGraph(
            catalog: catalog,
            catalogFingerprint: catalogFingerprint,
            chain: out chain,
            composed: out var composed,
            composedJson: out var bytes,
            origins: null,
            parse: false,
            reason: out reason,
            retainRoot: true,
            rootBytes: rootBytes,
            rootResolvedName: rootResolvedName,
            source: source
        );

        composedJson = (bytes ?? ((composed is null) ? null : Compact(members: out _, tree: composed)));
        if (composedJson is null) { chain = null; }

        return composedOk;
    }
    // With `parse` false, a composition whose image holds its bytes answers them in `composedJson` and leaves `composed`
    // null; otherwise `composed` is the tree and `composedJson` is null.
    private static bool TryComposeGraph(IWorldDocumentSource source, string rootResolvedName, byte[] rootBytes, bool parse, out JsonObject? composed, out byte[]? composedJson, out IReadOnlyList<WorldComposedLink>? chain, out string reason, string catalogFingerprint, IMachineValidationCatalog? catalog, WorldDocumentOrigins? origins, bool retainRoot) {
        ArgumentNullException.ThrowIfNull(argument: source);

        composed = null;
        composedJson = null;
        chain = null;
        reason = string.Empty;

        // Whether the root composes anything is read off its root members' names; a tree is parsed only for a flat root
        // whose provenance is asked for.
        if (MembersOf(json: Puck.Confinement.FileContent.Utf8(content: rootBytes).Span) is not { } members) {
            return true;
        }

        if (!members.Any(predicate: static member => member.Name is WorldDocumentBasis.BasisMemberName or WorldDocumentBasis.ImportsMemberName)) {
            if ((origins is not null) && (JsonNode.Parse(json: DecodeJson(bytes: rootBytes)) is JsonObject root)) {
                origins.Capture(node: root, documentPath: rootResolvedName);
                composed = root;
            }
            return true;
        }

        if (!TryComposeLayersHeld(
            ancestors: [],
            bytes: rootBytes,
            catalog: catalog,
            catalogFingerprint: catalogFingerprint,
            composed: out var result,
            image: out var image,
            origins: origins,
            parseHeld: parse,
            reach: out _,
            reason: out reason,
            resolvedPath: rootResolvedName,
            retainImage: retainRoot,
            serveHeldImage: true,
            source: source,
            stack: out _,
            touched: out var touched
        )) {
            return false;
        }

        var tree = (parse || (image is null));

        composed = (tree ? result : null);
        composedJson = (tree ? null : image!.ComposedJson);
        chain = touched.Links;

        return true;
    }
}
