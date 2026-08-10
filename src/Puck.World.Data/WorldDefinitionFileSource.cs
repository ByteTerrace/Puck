using System.Security.Cryptography;
using System.Text;

namespace Puck.World;

/// <summary>Loads and validates a world document from a file, always alongside the canonical content-address pin of
/// the exact bytes read — the one implementation both the console's <c>world.load</c>/<c>world.reload</c> handlers
/// (<c>Puck.World.WorldMutationCommandModule</c>, via <c>WorldDefinitionLoader.TryLoadFile</c>) and the replay
/// tape's offline re-drive (<c>Puck.World.WorldReplaySnapshot.Drive</c>, through <c>WorldServer.ApplyRebuild</c>)
/// share, so a live read and a re-drive's later re-read of the same path compute the hash the same way.
/// Puck.World.Server depends on Puck.World.Data already, so this is the lowest layer both can reach without a new
/// project reference.</summary>
public static class WorldDefinitionFileSource {
    /// <summary>Loads, migrates, and validates a world document from <paramref name="path"/>, returning the
    /// canonical <c>sha256-64/{hex}</c> content-address pin of the exact bytes consumed — never a re-serialization
    /// of the parsed document, so a byte the parse ignores (whitespace, member order) still moves the pin, and
    /// never a re-serialization of a migrated document either: <see cref="WorldDefinitionMigrations.Apply"/> runs
    /// on the in-memory parse only, between parsing and validating, so a pre-field save on disk still hashes to
    /// what its bytes actually are. Mirrors <c>WorldDefinitionLoader.TryLoadFile</c>'s three-class failure
    /// reporting (absent, unreadable, invalid); a broad catch is deliberate — a load boundary must never throw out
    /// of this method.</summary>
    /// <param name="path">The file to load.</param>
    /// <param name="definition">The loaded definition on success; <see langword="null"/> on failure.</param>
    /// <param name="contentHash">The canonical content-address pin of the bytes read, on success; empty on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <param name="neighbours">The injected neighbour resolver <see cref="WorldDefinitionValidator.Validate"/>
    /// reads for a cross-document border-margin check — see its own remarks. Optional here (unlike
    /// <see cref="WorldDefinitionValidator.Validate"/>'s own required parameter): this method loads arbitrary files
    /// for purposes that mostly have nothing to do with border margins (catalog scans, replay re-reads, tests), so
    /// <see langword="null"/> (the default) is the ordinary case. A caller that does have a reachable resolver at
    /// hand should pass it.</param>
    /// <returns><see langword="true"/> when the file loaded and validated.</returns>
    public static bool TryLoad(string path, out WorldDefinition? definition, out string contentHash, out string reason, IWorldNeighbourResolver? neighbours = null) =>
        TryLoadCore(path: path, definition: out definition, contentHash: out contentHash, reason: out reason, neighbours: neighbours, validateMarginClaims: true);

    /// <summary>Loads a file while validating only the facts owned by that document. Used before the composition root
    /// can supply a neighbour resolver, and by replay to obtain the bytes whose recorded content hash is compared by
    /// the caller; a live first-load boundary must use <see cref="TryLoad"/> and prove cross-document claims.</summary>
    /// <param name="path">The file to load.</param>
    /// <param name="definition">The loaded definition on success; <see langword="null"/> on failure.</param>
    /// <param name="contentHash">The canonical content-address pin of the bytes read, on success; empty on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the file loaded and its document-local facts validated.</returns>
    public static bool TryLoadLocally(string path, out WorldDefinition? definition, out string contentHash, out string reason) =>
        TryLoadCore(path: path, definition: out definition, contentHash: out contentHash, reason: out reason, neighbours: null, validateMarginClaims: false);

    private static bool TryLoadCore(string path, out WorldDefinition? definition, out string contentHash, out string reason, IWorldNeighbourResolver? neighbours, bool validateMarginClaims) {
        definition = null;
        contentHash = string.Empty;

        if (!File.Exists(path: path)) {
            reason = $"no file at {path}";

            return false;
        }

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: path);
        } catch (Exception exception) {
            reason = $"cannot read {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        string json;

        try {
            // Mirrors File.ReadAllText's own encoding detection (BOM-sniffed, UTF-8 default) so a load through this
            // path and a load through File.ReadAllText decode identically — the hash below is over the RAW bytes
            // regardless, but the parsed document must still match what the console has always loaded.
            using var reader = new StreamReader(stream: new MemoryStream(buffer: bytes), encoding: Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            json = reader.ReadToEnd();
        } catch (Exception exception) {
            reason = $"cannot decode {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        try {
            if (!WorldJsonPayload.TryParse(json: json, info: WorldJsonContext.Default.WorldDefinition, value: out var parsed, error: out var parseError)) {
                reason = $"{path} is not a valid {WorldDefinition.SchemaVersion} document: {parseError}";

                return false;
            }

            if (!string.Equals(a: parsed.Schema, b: WorldDefinition.SchemaVersion, comparisonType: StringComparison.Ordinal)) {
                reason = $"{path} is not a valid {WorldDefinition.SchemaVersion} document: schema '{parsed.Schema ?? "(absent)"}' is not {WorldDefinition.SchemaVersion}";

                return false;
            }

            parsed = WorldDefinitionMigrations.Apply(definition: parsed);

            if (validateMarginClaims) {
                WorldDefinitionValidator.Validate(definition: parsed, neighbours: neighbours);
            } else if (!WorldDefinitionValidator.TryValidateLocally(definition: parsed, reason: out var localReason)) {
                throw new InvalidOperationException(message: localReason);
            }
            definition = parsed;
            contentHash = ComputeContentHash(content: bytes);
            reason = "";

            return true;
        } catch (Exception exception) {
            reason = $"{path} is not a valid {WorldDefinition.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }

    /// <summary>Computes the canonical <c>sha256-64/{16 lowercase hex}</c> content-address pin of
    /// <paramref name="content"/> — the leading 64 bits of its SHA-256, matching
    /// <c>Puck.Assets.AssetContentHash</c>'s algorithm and <c>WorldDefinitionValidator.IsValidAddonHash</c>'s wire
    /// form exactly, so every "sha256-64/" pin in the tree reads the same bytes the same way.</summary>
    /// <param name="content">The bytes to hash.</param>
    /// <returns>The canonical content-address string.</returns>
    public static string ComputeContentHash(ReadOnlySpan<byte> content) {
        Span<byte> hash = stackalloc byte[32];

        SHA256.HashData(source: content, destination: hash);

        var value = BitConverter.ToUInt64(value: hash[..8]);

        return $"sha256-64/{value:x16}";
    }
}
