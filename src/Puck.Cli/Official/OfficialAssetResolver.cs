using Puck.Assets.Documents;
using Puck.Launcher.Release;
using Puck.World;
using Puck.World.Authoring;

namespace Puck.Cli.Official;

// Resolves every music/table/tune/patch row the composed root world declares into assets[]: reads the row's own
// referenced document off the worlds project tree (never AppContext.BaseDirectory — that is the convention a BUILT
// executable's own Content-copied Assets read through, and this tool walks the SOURCE tree before any such build
// exists), then re-derives the row family's own canonical document hash and refuses by name on a mismatch against
// the row's declared hash — the same check WorldDefinitionValidator runs at a live load. WorldDefinition declares no
// row type for the "audio"/"synth"/"font" families the official manifest contract reserves (see
// OfficialAssetFamilies), so this resolver never emits an entry under one of those three.
internal static class OfficialAssetResolver {
    public static bool TryResolve(WorldDefinition definition, string worldProjectRoot, OfficialObjectWriter writer, out IReadOnlyList<OfficialAssetEntry> assets, out string reason) {
        assets = [];

        var entries = new List<OfficialAssetEntry>();

        foreach (var row in (definition.Music ?? [])) {
            if (!TryResolveOne(
                canonicalHash: static (document, name) => MusicCanonicalizer.Canonicalize(document: document, source: name).Hash,
                declaredHash: row.Hash,
                entry: out var entry,
                family: OfficialAssetFamilies.Music,
                name: row.Name,
                reason: out reason,
                source: row.Source,
                tryLoad: (string resolvedSource, out MusicDocument? document, out string? error) => WorldAssetRowLoader.TryLoadMusic(document: out document, error: out error, row: (row with { Source = resolvedSource })),
                worldProjectRoot: worldProjectRoot,
                writer: writer
            )) {
                return false;
            }

            entries.Add(item: entry!);
        }

        foreach (var row in (definition.Tables ?? [])) {
            if (!TryResolveOne(
                canonicalHash: static (document, name) => TableCanonicalizer.Canonicalize(document: document, source: name).Hash,
                declaredHash: row.Hash,
                entry: out var entry,
                family: OfficialAssetFamilies.Table,
                name: row.Name,
                reason: out reason,
                source: row.Source,
                tryLoad: (string resolvedSource, out TableDocument? document, out string? error) => WorldAssetRowLoader.TryLoadTable(document: out document, error: out error, row: (row with { Source = resolvedSource })),
                worldProjectRoot: worldProjectRoot,
                writer: writer
            )) {
                return false;
            }

            entries.Add(item: entry!);
        }

        foreach (var row in definition.Tunes) {
            if (!TryResolveOne(
                canonicalHash: static (document, name) => AudioCanonicalizer.Canonicalize(document: document, source: name).Hash,
                declaredHash: row.Hash,
                entry: out var entry,
                family: OfficialAssetFamilies.Tune,
                name: row.Name,
                reason: out reason,
                source: row.Source,
                tryLoad: (string resolvedSource, out AudioDocument? document, out string? error) => WorldAssetRowLoader.TryLoadTune(document: out document, error: out error, row: (row with { Source = resolvedSource })),
                worldProjectRoot: worldProjectRoot,
                writer: writer
            )) {
                return false;
            }

            entries.Add(item: entry!);
        }

        foreach (var row in definition.Patches) {
            if (!TryResolveOne(
                canonicalHash: static (document, name) => SynthPatchCanonicalizer.Canonicalize(document: document, source: name).Hash,
                declaredHash: row.Hash,
                entry: out var entry,
                family: OfficialAssetFamilies.Patch,
                name: row.Name,
                reason: out reason,
                source: row.Source,
                tryLoad: (string resolvedSource, out SynthPatchDocument? document, out string? error) => WorldAssetRowLoader.TryLoadPatch(document: out document, error: out error, row: (row with { Source = resolvedSource })),
                worldProjectRoot: worldProjectRoot,
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

    private static bool TryResolveOne<TDocument>(
        string family,
        string name,
        string source,
        string declaredHash,
        string worldProjectRoot,
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

        var resolvedPath = (Path.IsPathRooted(path: source)
            ? source
            : Path.Combine(path1: worldProjectRoot, path2: source)
        );

        if (!File.Exists(path: resolvedPath)) {
            reason = $"{family} row '{name}': source '{source}' does not exist under {worldProjectRoot}.";

            return false;
        }

        if (!tryLoad(resolvedPath, out var document, out var loadError)) {
            reason = $"{family} row '{name}': {loadError}";

            return false;
        }

        var recomputed = canonicalHash(document!, name);

        if (!string.Equals(a: declaredHash, b: recomputed, comparisonType: StringComparison.Ordinal)) {
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
            Pin: $"sha256/{recomputed}",
            Size: size,
            Source: source
        );
        reason = string.Empty;

        return true;
    }
}
