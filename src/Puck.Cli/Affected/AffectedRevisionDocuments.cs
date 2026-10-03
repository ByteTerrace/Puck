using System.Formats.Tar;
using Puck.Hosting;
using Puck.World;

namespace Puck.Cli.Affected;

/// <summary>
/// The document source of a revision's tree: the composer every local load resolves through
/// (<see cref="WorldDefinitionFileSource.LocalDocuments"/>) reads files, and a revision holds none, so the first read
/// exports the revision once (<c>git archive</c>) into a directory of its own and every name and resolved name crosses
/// between the repository's paths, which the tree speaks, and the export's. A world's <c>.puck</c> source recorded in a
/// revision therefore composes exactly as it does in the working tree.
/// </summary>
/// <param name="root">The repository root, the directory the tree's paths are relative to.</param>
/// <param name="revision">The revision whose tree is exported.</param>
internal sealed class AffectedRevisionDocuments(string root, string revision) : IWorldDocumentSource, IDisposable {
    private readonly Lazy<string> m_export = new(valueFactory: () => Export(revision: revision, root: root));
    private readonly string m_root = Path.GetFullPath(path: root);

    private static string Export(string revision, string root) {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-affected-{Guid.NewGuid():N}"
        );

        _ = Directory.CreateDirectory(path: directory);

        try {
            using var git = ChildProcess.StartRedirected(
                arguments: ["-C", root, "archive", "--format=tar", revision],
                fileName: "git"
            );
            var stderr = git.StandardError.ReadToEndAsync();

            TarFile.ExtractToDirectory(
                destinationDirectoryName: directory,
                overwriteFiles: true,
                source: git.StandardOutput.BaseStream
            );
            git.WaitForExit();

            if (git.ExitCode != 0) {
                throw new InvalidOperationException(message: $"git archive {revision} exited {git.ExitCode}: {stderr.GetAwaiter().GetResult().Trim()}");
            }
        } catch {
            Directory.Delete(path: directory, recursive: true);

            throw;
        }

        return directory;
    }
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
        resolvedName: Cross(from: m_root, path: resolvedName, to: m_export.Value)
    );
    /// <inheritdoc/>
    public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
        var read = WorldDefinitionFileSource.LocalDocuments.TryRead(
            content: out content,
            name: Cross(from: m_root, path: name, to: m_export.Value),
            reason: out reason,
            referrerName: Cross(from: m_root, path: referrerName, to: m_export.Value),
            resolvedName: out var exported
        );

        resolvedName = Cross(from: m_export.Value, path: exported, to: m_root);

        return read;
    }
    /// <summary>Deletes the export, when one was made.</summary>
    public void Dispose() {
        if (m_export.IsValueCreated) {
            Directory.Delete(path: m_export.Value, recursive: true);
        }
    }
}
