namespace Puck.World.Server;

public sealed partial class WorldDocument {
    /// <summary>Owns load + canonical hash verification for a name/source/hash reference row at the mutation
    /// composition boundary (<see cref="WorldTune"/>/<see cref="WorldPatch"/>) — the referenced twin of <see
    /// cref="TryCanonicalizeDocument{TDocument}"/>: the row carries no document to write back, so a successful
    /// verify only proves the row is admissible, never returns a payload.</summary>
    private static bool TryVerifyReferencedAsset(string? documentDirectory, string family, string name, string source, string hash, out string reason) {
        if (!WorldAssetRowLoader.TryCanonicalHash(documentDirectory: documentDirectory, error: out var error, family: family, hash: hash, name: name, pin: out var canonical, source: source)) {
            reason = $"{family} '{name}': {error}";

            return false;
        }
        if (!string.Equals(a: hash, b: canonical, comparisonType: StringComparison.Ordinal)) {
            reason = $"{family} '{name}' hash '{hash}' does not match the canonical sha256 '{canonical}' — a hash must come from the canonicalize pipeline";

            return false;
        }

        reason = string.Empty;

        return true;
    }
}
