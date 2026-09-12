using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;

namespace Puck.World.Transpiler.Composition;

/// <summary>The one <see cref="IWorldDocumentSource"/> that resolves a <c>basis</c>/<c>imports</c> reference beside
/// a directory, transparently compiling a <c>.puck</c> reference in memory before handing its bytes to
/// <see cref="WorldDefinitionFileSource.TryComposeChainWithImports"/> — a plain <c>.json</c> reference passes
/// through unchanged. Both the game boot path (<c>Puck.World.PuckWorldLoader</c>) and <c>puck compile --validate</c>
/// (<see cref="Validation.WorldSemanticValidator"/>) compose through this one implementation, so a <c>.puck</c>
/// source and the running game resolve a basis or import chain identically.</summary>
public sealed class PuckDocumentComposer : IWorldDocumentSource {
    /// <inheritdoc />
    public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
        content = null;

        try {
            var directory = (Path.GetDirectoryName(path: Path.GetFullPath(path: referrerName)) ?? ".");
            resolvedName = Path.GetFullPath(path: Path.Combine(path1: directory, path2: name));
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            resolvedName = name;
            reason = $"cannot resolve path '{name}' from {referrerName}: {ex.Message}";

            return false;
        }

        if (!File.Exists(path: resolvedName)) {
            reason = $"document {resolvedName} (named by {referrerName}) does not exist.";

            return false;
        }

        try {
            if (resolvedName.EndsWith(value: ".puck", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                var puckText = File.ReadAllText(path: resolvedName);
                var doc = PuckParser.ParseDocument(source: puckText);
                var lowered = WorldDocumentEmitter.Lower(
                    basePath: Path.GetDirectoryName(path: resolvedName),
                    document: doc
                );

                content = Encoding.UTF8.GetBytes(s: lowered.ToJsonString());
            } else {
                content = File.ReadAllBytes(path: resolvedName);
            }

            reason = string.Empty;

            return true;
        } catch (Exception ex) {
            reason = $"cannot read document {resolvedName}: {ex.Message}";

            return false;
        }
    }

    /// <summary>Composes <paramref name="rootBytes"/>' whole basis-and-imports graph, rooted beside
    /// <paramref name="rootResolvedPath"/>, transparently compiling any <c>.puck</c> basis/import reference along
    /// the way. Returns <paramref name="rootBytes"/>'s own re-parse (as <paramref name="composed"/> being
    /// <see langword="null"/>) when the root names neither — see
    /// <see cref="WorldDefinitionFileSource.TryComposeChainWithImports"/>.</summary>
    /// <param name="rootResolvedPath">The root document's own resolved path — a basis/import reference resolves
    /// relative to its directory.</param>
    /// <param name="rootBytes">The root document's already-read raw bytes (JSON, whether hand-authored or already
    /// lowered from a <c>.puck</c> source).</param>
    /// <param name="composed">The composed tree (basis/imports members stripped) on success.</param>
    /// <param name="chainBytes">The bytes of every file touched composing the root.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the graph composed (or the root names neither basis nor imports).</returns>
    public static bool TryComposeWorldDocument(
        string rootResolvedPath,
        byte[] rootBytes,
        out JsonObject? composed,
        out IReadOnlyList<byte[]> chainBytes,
        out string reason
    ) {
        var source = new PuckDocumentComposer();

        return WorldDefinitionFileSource.TryComposeChainWithImports(
            chainBytes: out chainBytes,
            composed: out composed,
            reason: out reason,
            rootBytes: rootBytes,
            rootResolvedName: rootResolvedPath,
            source: source
        );
    }
}
