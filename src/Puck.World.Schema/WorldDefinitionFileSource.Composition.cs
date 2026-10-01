using System.Text.Json;
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
            var overlay = ((JsonObject)objects[index].DeepClone());

            overlay.Remove(propertyName: WorldDocumentBasis.BasisMemberName);

            if (!WorldDocumentBasis.TryMerge(
                basis: mergedFromTop,
                composed: out var merged,
                overlay: overlay,
                reason: out var mergeReason
            )) {
                reason = $"{chain[index].ResolvedName} over {chain[(index + 1)].ResolvedName}: {mergeReason}";
                composed = null;

                return false;
            }

            mergedFromTop = merged!;
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
    /// <param name="chainBytes">The bytes of every file touched composing the root — the root first, then its basis
    /// chain, then each import's own touched bytes in authored order; a single-element list (just the root) whenever
    /// <paramref name="composed"/> is <see langword="null"/>.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <param name="catalogFingerprint">Stable metadata fingerprint used to partition the composition cache.</param>
    /// <param name="catalog">The explicit machine catalog used for metadata rewriting, or <see langword="null"/> for structural composition without provider metadata rewriting.</param>
    /// <returns><see langword="true"/> when the graph composed (or the root names neither basis nor imports).</returns>
    /// <param name="origins">Optional authored scalar origins. A flat root returns its tracked tree when supplied;
    /// composed images are bypassed so every retained scalar can identify the document and value that authored it.</param>
    public static bool TryComposeChainWithImports(IWorldDocumentSource source, string rootResolvedName, byte[] rootBytes, out JsonObject? composed, out IReadOnlyList<byte[]> chainBytes, out string reason, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null, WorldDocumentOrigins? origins = null) {
        ArgumentNullException.ThrowIfNull(argument: source);

        composed = null;
        chainBytes = [rootBytes];
        reason = string.Empty;

        JsonObject? root;

        try {
            root = (JsonNode.Parse(json: DecodeJson(bytes: rootBytes)) as JsonObject);
        } catch (JsonException) {
            root = null;
        }

        if (root is null) {
            return true;
        }

        if (
            !root.ContainsKey(propertyName: WorldDocumentBasis.BasisMemberName) &&
            !root.ContainsKey(propertyName: WorldDocumentBasis.ImportsMemberName)
        ) {
            if (origins is not null) {
                origins.Capture(node: root, documentPath: rootResolvedName);
                composed = root;
            }
            return true;
        }

        if (!TryComposeLayers(
            ancestors: [],
            bytes: rootBytes,
            catalog: catalog,
            catalogFingerprint: catalogFingerprint,
            composed: out var result,
            origins: origins,
            reach: out _,
            reason: out reason,
            resolvedPath: rootResolvedName,
            serveHeldImage: true,
            source: source,
            stack: out _,
            touched: out var touched,
            touchedPaths: out _
        )) {
            return false;
        }

        composed = result;
        chainBytes = touched;

        return true;
    }
}
