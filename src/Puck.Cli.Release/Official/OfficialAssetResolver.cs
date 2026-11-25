using Puck.Assets;
using Puck.Launcher.Release;
using Puck.World;

namespace Puck.Cli.Official;

// Resolves every music/table/tune/patch row the composed root world declares into assets[]: reads the row's own
// referenced document beside the root document in the source tree (WorldDocumentPaths, the rule every load follows),
// then re-derives the row family's own canonical document hash (WorldAssetRowLoader, which owns the families) and
// refuses by name on a mismatch against the row's declared hash — the same check WorldDefinitionValidator runs at a
// live load. The four row families (AssetRowFamilies) are the whole manifest family vocabulary.
internal static class OfficialAssetResolver {
    private static bool TryResolveOne(string family, string name, string source, string declaredHash, string? documentDirectory, OfficialObjectWriter writer, out OfficialAssetEntry? entry, out string reason) {
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

        var bytes = File.ReadAllBytes(path: resolvedPath);

        if (!WorldAssetRowLoader.TryCanonicalHash(bytes: bytes, error: out var error, family: family, name: name, pin: out var recomputed)) {
            reason = $"{family} row '{name}': {error}";

            return false;
        }

        if (!string.Equals(
            a: declaredHash,
            b: recomputed,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"{family} row '{name}': declared hash '{declaredHash}' does not match the canonical sha256 '{recomputed}'.";

            return false;
        }

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

        foreach (var (family, name, source, declaredHash) in WorldAssetRowLoader.PinnedRows(definition: definition)) {
            if (!TryResolveOne(
                declaredHash: declaredHash,
                documentDirectory: definition.DocumentDirectory,
                entry: out var entry,
                family: family,
                name: name,
                reason: out reason,
                source: source,
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
}
