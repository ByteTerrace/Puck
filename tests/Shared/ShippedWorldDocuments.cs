using System.Text;
using Puck.World;
using Puck.World.Transpiler;

namespace Puck.Testing;

/// <summary>A worlds directory's documents as the game resolves them (<see cref="WorldDocumentName"/>): one per
/// document name, carried by the <c>.puck</c> source that emits it where one does and by its <c>.world.json</c> document
/// otherwise, so a document file sitting beside the source of its name emitting that name is never one of them.</summary>
internal static class ShippedWorldDocuments {
    // A test host resolves local documents as the game does: a document with a .puck source is that source.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void InstallDocumentSource() => WorldDefinitionFileSource.UseLocalDocuments(source: Puck.World.Transpiler.Composition.PuckDocumentComposer.Instance);

    /// <summary>The repository-relative directory of the game's shipped worlds.</summary>
    public const string WorldDirectory = "src/Puck.World/Assets/worlds";

    /// <summary>Returns the file that carries each document under <paramref name="directory"/>, once however many
    /// names it carries, and every module library there, which carries no document name but is a source all the same
    /// (<see cref="Puck.World.Transpiler.Composition.PuckDocumentComposer.TryCarriers"/>), as full forward-slashed paths
    /// in ordinal order.</summary>
    /// <param name="directory">The full path of the directory to enumerate.</param>
    /// <param name="option">Whether subdirectories are enumerated.</param>
    /// <returns>The carrying files and the module libraries.</returns>
    /// <exception cref="InvalidDataException">Two files carry one document name.</exception>
    public static IReadOnlyList<string> Files(string directory, SearchOption option = SearchOption.AllDirectories) => (Puck.World.Transpiler.Composition.PuckDocumentComposer.TryCarriers(
        carriers: out var carriers,
        directory: directory,
        libraries: out var libraries,
        option: option,
        reason: out var reason
    )
        ? [.. carriers.Select(selector: static carrier => carrier.Path).Distinct(comparer: StringComparer.Ordinal).Concat(second: libraries).Order(comparer: StringComparer.Ordinal)]
        : throw new InvalidDataException(message: reason)
    );
    /// <summary>Returns the file that carries the document <paramref name="name"/> names beside
    /// <paramref name="referrer"/>, as the composer resolves it: the source that emits it where one does, its document
    /// file otherwise.</summary>
    /// <param name="referrer">The full path of the referring file.</param>
    /// <param name="name">The authored document name.</param>
    /// <returns>The full path of the carrying file.</returns>
    /// <exception cref="InvalidDataException">No file carries the name, or two do.</exception>
    public static string Carrier(string referrer, string name) => Path.GetFullPath(path: (Puck.World.Transpiler.Composition.PuckDocumentComposer.Instance.TryRead(
        content: out _,
        name: name,
        reason: out var reason,
        referrerName: referrer,
        resolvedName: out var resolved
    )
        ? resolved
        : throw new InvalidDataException(message: reason)
    ));
    /// <summary>Copies every file a document composes from, its basis chain and its imports as the loader resolves
    /// them (<see cref="WorldDefinitionFileSource.TryDescribeComposition"/> through the composer), into
    /// <paramref name="directory"/>, each at its place relative to the others, and returns where the document itself
    /// belongs there. A copy of the document placed at that path composes exactly as the shipped one does, so a test
    /// that moves a shipped document never keeps its own list of what the document imports.</summary>
    /// <param name="path">The full path of the document's carrying file.</param>
    /// <param name="directory">The full path of the directory to copy into.</param>
    /// <returns>The full path the document's copy belongs at, its carrying file's name kept.</returns>
    /// <exception cref="InvalidDataException">The document's composition does not resolve.</exception>
    public static string CopyComposition(string path, string directory) {
        var document = Path.GetFullPath(path: path);

        if (!WorldDefinitionFileSource.TryDescribeComposition(
            content: Read(path: document),
            documents: Puck.World.Transpiler.Composition.PuckDocumentComposer.Instance,
            layers: out var layers,
            path: document,
            reason: out var reason
        )) {
            throw new InvalidDataException(message: reason);
        }

        var files = layers.Select(selector: static layer => Path.GetFullPath(path: layer.Path)).Append(element: document).Distinct(comparer: StringComparer.Ordinal).ToArray();
        var root = Path.GetDirectoryName(path: document)!;

        // The deepest directory every file sits under, so each keeps its place relative to the others.
        while (!files.All(predicate: file => file.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: (root.TrimEnd(trimChar: Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
        ))) {
            root = (Path.GetDirectoryName(path: root) ?? throw new InvalidDataException(message: $"{document} composes from files on no common root."));
        }

        foreach (var file in files.Where(predicate: file => !string.Equals(
            a: file,
            b: document,
            comparisonType: StringComparison.Ordinal
        ))) {
            var target = Path.Combine(
                path1: directory,
                path2: Path.GetRelativePath(
                    path: file,
                    relativeTo: root
                )
            );

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.Copy(
                destFileName: target,
                overwrite: true,
                sourceFileName: file
            );
        }

        var placed = Path.Combine(
            path1: directory,
            path2: Path.GetRelativePath(
                path: document,
                relativeTo: root
            )
        );

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: placed)!);

        return placed;
    }
    /// <summary>Returns a shipped document composed where it lies, through its whole basis chain and every import as the
    /// loader resolves them (<see cref="WorldDefinitionFileSource.TryComposeDocumentTree"/> through the composer): the
    /// standalone document a test hands an in-memory composition as its host, so the test never carries its own copy
    /// of what the document imports.</summary>
    /// <param name="path">The full path of the document's carrying file.</param>
    /// <returns>The composed document's UTF-8 JSON.</returns>
    /// <exception cref="InvalidDataException">The document's composition does not resolve.</exception>
    public static byte[] Composed(string path) => (WorldDefinitionFileSource.TryComposeDocumentTree(
        content: Read(path: path),
        documents: Puck.World.Transpiler.Composition.PuckDocumentComposer.Instance,
        path: path,
        reason: out var reason,
        tree: out var tree
    )
        ? Encoding.UTF8.GetBytes(s: tree!.ToJsonString())
        : throw new InvalidDataException(message: reason)
    );
    /// <summary>Returns the document a carrying file holds, compiling a <c>.puck</c> source.</summary>
    /// <param name="path">The full path of the carrying file.</param>
    /// <returns>The document's UTF-8 JSON.</returns>
    public static byte[] Read(string path) => (WorldDocumentName.IsSourceFile(path: path)
        ? Encoding.UTF8.GetBytes(s: WorldCompiler.CompileFile(path: path).RequireJson().ToJsonString())
        : File.ReadAllBytes(path: path)
    );
}
