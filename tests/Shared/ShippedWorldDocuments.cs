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
    /// <summary>Returns the document a carrying file holds, compiling a <c>.puck</c> source.</summary>
    /// <param name="path">The full path of the carrying file.</param>
    /// <returns>The document's UTF-8 JSON.</returns>
    public static byte[] Read(string path) => (WorldDocumentName.IsSourceFile(path: path)
        ? Encoding.UTF8.GetBytes(s: WorldCompiler.CompileFile(path: path).RequireJson().ToJsonString())
        : File.ReadAllBytes(path: path)
    );
}
