using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Launcher.Release;
using Puck.World;
using Puck.World.Authoring;

namespace Puck.Cli.Official;

// Resolves every music/table/tune/patch row the composed root world declares into assets[]: reads the row's own
// referenced document beside the root document in the source tree (WorldDocumentPaths, the rule every load follows),
// then re-derives the row family's own canonical document hash and refuses by name on a mismatch against
// the row's declared hash — the same check WorldDefinitionValidator runs at a live load. The four row families
// (AssetRowFamilies) are the whole manifest family vocabulary.
internal static class OfficialAssetResolver {
    private static bool TryResolveOne<TDocument>(
        string family,
        string name,
        string source,
        string declaredHash,
        string? documentDirectory,
        OfficialObjectWriter writer,
        TryLoadRow<TDocument> tryLoad,
        Func<TDocument, string, string> canonicalHash,
        out OfficialAssetEntry? entry,
        out string reason
    ) where TDocument : class {
        entry = null;

        if (string.IsNullOrWhiteSpace(value: source)) {
            reason = $"{family} row '{name}': source is required.";

            return false;
        }

        if (!WorldDocumentPaths.TryResolve(
            documentDirectory: documentDirectory,
            path: source,
            reason: out var unresolved,
            resolved: out var resolvedPath
        )) {
            reason = $"{family} row '{name}': source {unresolved}.";

            return false;
        }

        if (!File.Exists(path: resolvedPath)) {
            reason = $"{family} row '{name}': source '{source}' does not exist at {resolvedPath}.";

            return false;
        }

        if (!tryLoad(
            resolvedPath,
            out var document,
            out var loadError
        )) {
            reason = $"{family} row '{name}': {loadError}";

            return false;
        }

        var recomputed = canonicalHash(
            document!,
            name
        );

        if (!string.Equals(
            a: declaredHash,
            b: recomputed,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"{family} row '{name}': declared hash '{declaredHash}' does not match the canonical sha256 '{recomputed}'.";

            return false;
        }

        var bytes = File.ReadAllBytes(path: resolvedPath);

        var (objectPath, hash, size) = writer.Put(bytes: bytes);

        entry = new OfficialAssetEntry(
            ContentType: "application/json",
            Family: family,
            Hash: hash,
            Name: name,
            Path: objectPath,
            Pin: (ContentPin.Prefix + recomputed),
            Size: size,
            Source: source
        );
        reason = string.Empty;

        return true;
    }

    public static bool TryResolve(WorldDefinition definition, OfficialObjectWriter writer, out IReadOnlyList<OfficialAssetEntry> assets, out string reason) {
        assets = [];

        var entries = new List<OfficialAssetEntry>();

        foreach (var row in (definition.Music ?? [])) {
            if (!TryResolveOne(
                canonicalHash: static (document, name) => MusicCanonicalizer.Canonicalize(
                    document: document,
                    source: name
                ).Hash,
                declaredHash: row.Hash,
                entry: out var entry,
                family: AssetRowFamilies.Music,
                name: row.Name,
                reason: out reason,
                source: row.Source,
                tryLoad: (string resolvedSource, out MusicDocument? document, out string? error) => WorldAssetRowLoader.TryLoadMusic(
                    document: out document,
                    documentDirectory: null,
                    error: out error,
                    row: (row with { Source = resolvedSource })
                ),
                documentDirectory: definition.DocumentDirectory,
                writer: writer
            )) {
                return false;
            }

            entries.Add(item: entry!);
        }

        foreach (var row in (definition.Tables ?? [])) {
            if (!TryResolveOne(
                canonicalHash: static (document, name) => TableCanonicalizer.Canonicalize(
                    document: document,
                    source: name
                ).Hash,
                declaredHash: row.Hash,
                entry: out var entry,
                family: AssetRowFamilies.Table,
                name: row.Name,
                reason: out reason,
                source: row.Source,
                tryLoad: (string resolvedSource, out TableDocument? document, out string? error) => WorldAssetRowLoader.TryLoadTable(
                    document: out document,
                    documentDirectory: null,
                    error: out error,
                    row: (row with { Source = resolvedSource })
                ),
                documentDirectory: definition.DocumentDirectory,
                writer: writer
            )) {
                return false;
            }

            entries.Add(item: entry!);
        }

        foreach (var row in definition.Tunes) {
            if (!TryResolveOne(
                canonicalHash: static (document, name) => AudioCanonicalizer.Canonicalize(
                    document: document,
                    source: name
                ).Hash,
                declaredHash: row.Hash,
                entry: out var entry,
                family: AssetRowFamilies.Tune,
                name: row.Name,
                reason: out reason,
                source: row.Source,
                tryLoad: (string resolvedSource, out AudioDocument? document, out string? error) => WorldAssetRowLoader.TryLoadTune(
                    document: out document,
                    documentDirectory: null,
                    error: out error,
                    row: (row with { Source = resolvedSource })
                ),
                documentDirectory: definition.DocumentDirectory,
                writer: writer
            )) {
                return false;
            }

            entries.Add(item: entry!);
        }

        foreach (var row in definition.Patches) {
            if (!TryResolveOne(
                canonicalHash: static (document, name) => SynthPatchCanonicalizer.Canonicalize(
                    document: document,
                    source: name
                ).Hash,
                declaredHash: row.Hash,
                entry: out var entry,
                family: AssetRowFamilies.Patch,
                name: row.Name,
                reason: out reason,
                source: row.Source,
                tryLoad: (string resolvedSource, out SynthPatchDocument? document, out string? error) => WorldAssetRowLoader.TryLoadPatch(
                    document: out document,
                    documentDirectory: null,
                    error: out error,
                    row: (row with { Source = resolvedSource })
                ),
                documentDirectory: definition.DocumentDirectory,
                writer: writer
            )) {
                return false;
            }

            entries.Add(item: entry!);
        }

        assets = entries;
        reason = string.Empty;

        return true;
    }

    private delegate bool TryLoadRow<TDocument>(string resolvedSource, out TDocument? document, out string? error) where TDocument : class;
}
