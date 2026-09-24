namespace Puck.World.Server;

public sealed partial class WorldDocument {
    /// <summary>Owns load + canonical hash verification for a name/source/hash reference row at the mutation
    /// composition boundary (<see cref="WorldTune"/>/<see cref="WorldPatch"/>) — the referenced twin of <see
    /// cref="TryCanonicalizeDocument{TDocument}"/>: the row carries no document to write back, so a successful
    /// verify only proves the row is admissible, never returns a payload.</summary>
    private delegate bool AssetRowLoader<in TRow, TDocument>(string? documentDirectory, TRow row, out TDocument? document, out string? error);

    private static bool TryVerifyReferencedAsset<TRow, TDocument>(
        string? documentDirectory,
        TRow row,
        string id,
        string hash,
        string kind,
        AssetRowLoader<TRow, TDocument> tryLoad,
        Func<TDocument, string, Puck.Assets.Documents.CanonicalDocument<TDocument>> canonicalize,
        out string reason) where TDocument : class {
        if (!tryLoad(
            documentDirectory,
            row,
            out var document,
            out var loadError
        )) {
            reason = $"{kind} '{id}': {loadError}";

            return false;
        }

        Puck.Assets.Documents.CanonicalDocument<TDocument> canonical;

        try {
            canonical = canonicalize(
                arg1: document!,
                arg2: id
            );
        } catch (Exception exception) when ((exception is Puck.Assets.Documents.DocumentValidationException or InvalidOperationException)) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }

        if (!string.Equals(
            a: hash,
            b: canonical.Hash,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"{kind} '{id}' hash '{hash}' does not match the canonical sha256 '{canonical.Hash}' — a hash must come from the canonicalize pipeline";

            return false;
        }

        reason = string.Empty;

        return true;
    }
}
