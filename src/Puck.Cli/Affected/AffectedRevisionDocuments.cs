using Puck.World;

namespace Puck.Cli.Affected;

/// <summary>
/// The document source of a revision's tree: the composer every local load resolves through
/// (<see cref="WorldDefinitionFileSource.LocalDocuments"/>) reads files, and a revision holds none, so the first read
/// exports the trees the documents are authored in (<see cref="AffectedRevisionExport"/>) once, and every name and
/// resolved name crosses between the repository's paths, which the tree speaks, and the export's. A world's
/// <c>.puck</c> source recorded in a revision therefore composes exactly as it does in the working tree.
/// </summary>
/// <param name="root">The repository root, the directory the tree's paths are relative to.</param>
/// <param name="revision">The revision whose tree is exported.</param>
/// <param name="documentTrees">The repository-relative trees the revision's documents and every file their compiles
/// read are authored in, which the export holds.</param>
internal sealed class AffectedRevisionDocuments(string root, string revision, IReadOnlyList<string> documentTrees) : IWorldDocumentSource, IDisposable {
    private readonly Lazy<AffectedRevisionExport> m_export = new(valueFactory: () => AffectedRevisionExport.Create(paths: documentTrees, repository: root, revision: revision));
    private readonly string m_root = Path.GetFullPath(path: root);

    private string Cross(string path, string from, string to) {
        if (!Path.IsPathRooted(path: path)) {
            return path;
        }

        var relative = Path.GetRelativePath(path: Path.GetFullPath(path: path), relativeTo: from);

        return ((relative.StartsWith(comparisonType: StringComparison.Ordinal, value: "..") || Path.IsPathRooted(path: relative))
            ? path
            : Path.Combine(path1: to, path2: relative));
    }

    /// <inheritdoc/>
    public bool StillReads(string resolvedName, byte[] content) => WorldDefinitionFileSource.LocalDocuments.StillReads(
        content: content,
        resolvedName: Cross(from: m_root, path: resolvedName, to: m_export.Value.Root)
    );
    /// <inheritdoc/>
    public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
        var read = WorldDefinitionFileSource.LocalDocuments.TryRead(
            content: out content,
            name: Cross(from: m_root, path: name, to: m_export.Value.Root),
            reason: out reason,
            referrerName: Cross(from: m_root, path: referrerName, to: m_export.Value.Root),
            resolvedName: out var exported
        );

        resolvedName = Cross(from: m_export.Value.Root, path: exported, to: m_root);

        return read;
    }
    /// <summary>Deletes the export, when one was made.</summary>
    public void Dispose() {
        if (m_export.IsValueCreated) {
            m_export.Value.Dispose();
        }
    }
}
