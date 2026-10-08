using System.Text.Json;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Affected;

/// <summary>
/// Finds the changed shipped world files whose documents compile to the same value at the base and at the head, so
/// selection can leave the canaries out for them: a canary boots the compiled world, and an edit that compiles to the
/// same world, such as a hand-authored document moved to its <c>.puck</c> source, changes nothing a canary runs.
/// <para>
/// A changed <c>.puck</c> or <c>.world.json</c> under the shipped tree names the document its stem carries. That
/// document is read at the head from the working tree and at the base from the shipped assets the base recorded
/// (exported once, <see cref="AffectedRevisionExport"/>), each through <see cref="PuckDocumentComposer"/>, which compiles a source
/// or reads a document exactly as a load does. The two are compared as values (<see cref="ValueEqual"/>). A file whose
/// source is a module library or a composition is not judged here and keeps the ordinary selection.
/// </para>
/// </summary>
public static class AffectedCompiledWorlds {
    private const string AssetsTree = "src/Puck.World/Assets";

    private static readonly string[] DocumentSuffixes = [".world.json", ".puck"];

    private static string? DocumentName(string path) {
        if (!path.StartsWith(comparisonType: StringComparison.Ordinal, value: (AffectedCommand.ShippedTree + "/"))) {
            return null;
        }

        var relative = path[(AffectedCommand.ShippedTree.Length + 1)..];

        return ((DocumentSuffixes.FirstOrDefault(predicate: suffix => relative.EndsWith(comparisonType: StringComparison.Ordinal, value: suffix)) is { } found)
            ? relative[..^found.Length]
            : null);
    }
    private static JsonDocument? Read(string root, string name) {
        var source = Path.Combine(path1: root, path2: AffectedCommand.ShippedTree, path3: (name + ".puck"));

        // A library may have a document beside it, and a composition may emit its own stem along with other worlds.
        // Comparing that one document cannot judge a change to either source.
        if (File.Exists(path: source) && (WorldSourceIndex.Declaration(path: source) is not { EmitsDocument: true, Worlds.Count: 0 })) {
            return null;
        }

        // A name resolves beside its referrer, so any file in the shipped tree's own directory refers to it.
        var referrer = Path.Combine(path1: root, path2: AffectedCommand.ShippedTree, path3: "referrer.world.json");

        if (!PuckDocumentComposer.Instance.TryRead(content: out var content, name: name, reason: out _, referrerName: referrer, resolvedName: out _) || (content is null)) {
            return null;
        }

        try {
            return JsonDocument.Parse(utf8Json: content);
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>Whether two documents are the same value: objects compare by member whatever their order, arrays by
    /// position, numbers by their exact decimal value whatever their spelling (<c>1.0</c> is <c>1</c>), and strings,
    /// booleans and null as written.</summary>
    /// <param name="left">One value.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are equal as values.</returns>
    public static bool ValueEqual(JsonElement left, JsonElement right) => JsonElement.DeepEquals(element1: left, element2: right);
    /// <summary>Returns the changed paths whose shipped document compiles to the same value at the base and the head.</summary>
    /// <param name="repositoryRoot">The repository root, the head's working tree.</param>
    /// <param name="since">The base revision.</param>
    /// <param name="changed">The changed paths, repository-relative with forward slashes, deleted ones included.</param>
    /// <returns>The paths judged unchanged in value; empty when no shipped world file changed.</returns>
    /// <exception cref="AffectedRevisionExportRefusedException">The base's shipped assets could not be exported.</exception>
    public static IReadOnlySet<string> Unchanged(string repositoryRoot, string since, IReadOnlyList<string> changed) {
        var candidates = changed.Select(selector: static path => (Path: path, Name: DocumentName(path: path)))
            .Where(predicate: static candidate => (candidate.Name is not null))
            .ToArray();
        var unchanged = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (candidates.Length == 0) {
            return unchanged;
        }

        using var export = AffectedRevisionExport.Create(paths: [AssetsTree], repository: repositoryRoot, revision: since);

        foreach (var name in candidates.Select(selector: static candidate => candidate.Name!).Distinct(comparer: StringComparer.Ordinal)) {
            using var head = Read(name: name, root: repositoryRoot);
            using var before = Read(name: name, root: export.Root);

            if ((head is not null) && (before is not null) && ValueEqual(left: before.RootElement, right: head.RootElement)) {
                unchanged.UnionWith(other: candidates.Where(predicate: candidate => (candidate.Name == name)).Select(selector: static candidate => candidate.Path));
            }
        }

        return unchanged;
    }
}
