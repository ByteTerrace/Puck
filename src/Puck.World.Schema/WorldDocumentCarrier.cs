namespace Puck.World;

/// <summary>A document name and the one file that carries it in a directory
/// (<see cref="WorldDocumentName.TryCarriers(IEnumerable{WorldDocumentCarrier}, out IReadOnlyList{WorldDocumentCarrier}, out string)"/>):
/// the <c>.puck</c> source that emits it where one does, its <c>.world.json</c> document otherwise. A composition
/// source carries each world it declares, so several carriers may share its file.</summary>
/// <param name="Name">The document name, relative to the enumerated directory with forward slashes
/// (<c>"games/klondike"</c>).</param>
/// <param name="Path">The carrying file's path, with forward slashes, rooted as the enumerated directory was.</param>
public readonly record struct WorldDocumentCarrier(string Name, string Path) {
    /// <summary>Gets whether the carrying file is a <c>.puck</c> source, which a reader compiles, rather than a
    /// document it reads as it stands.</summary>
    public bool IsSource => WorldDocumentName.IsSourceFile(path: Path);
}
