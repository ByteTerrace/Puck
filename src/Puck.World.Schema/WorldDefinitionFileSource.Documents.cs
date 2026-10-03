using Puck.Assets;

namespace Puck.World;

public static partial class WorldDefinitionFileSource {
    /// <summary>Gets the source that resolves each document name to its <c>.world.json</c> file beside the referrer and
    /// nothing else: a name whose <c>.puck</c> source stands there is refused by name, since this source cannot compile
    /// it and a document file beside a source is never read in its place.</summary>
    public static IWorldDocumentSource DirectoryDocuments { get; } = new DirectoryDocumentSource();

    /// <summary>Returns a source that resolves names as <see cref="DirectoryDocuments"/> does, reading every file
    /// through <paramref name="read"/> rather than the file system: a tree a caller holds, such as the one a revision
    /// recorded. Nothing composed over it is held, since no file proves a reader's image still stands.</summary>
    /// <param name="read">Reads a file's bytes by its full, forward-slashed path, or answers <see langword="null"/> when
    /// the tree holds no such file.</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    public static IWorldDocumentSource DirectoryDocumentsReadBy(Func<string, byte[]?> read) {
        ArgumentNullException.ThrowIfNull(argument: read);

        return new DirectoryDocumentSource(read: read);
    }

    // The module an import is named by in every refusal about it: its file, with the alias it is brought in under. Two
    // imports whose files share a name (one in each of two directories) keep their authored spelling instead, so they stay
    // two owners of what they author.
    private static string DescribeImport(HashSet<string> taken, string? alias, string resolvedName, string importName) {
        var described = ((alias is null)
            ? WorldDocumentLabel.Of(path: resolvedName)
            : $"{WorldDocumentLabel.Of(path: resolvedName)} as {alias}"
        );

        if (taken.Add(item: described)) {
            return described;
        }

        described = ((alias is null)
            ? importName
            : $"{importName} as {alias}"
        );
        _ = taken.Add(item: described);

        return described;
    }

    // The directory-backed IWorldDocumentSource every local load walks over — the one place Path.Combine/
    // Path.GetFullPath/File.Exists/File.ReadAllBytes for a basis reference live, so TryLoad's directory behavior and
    // TryResolveChainFiles' push-side walk can never drift apart.
    // With a reader, every file it reads comes from the reader rather than the file system, and nothing it composes is
    // held, since no file proves a reader's image still stands.
    private sealed class DirectoryDocumentSource(Func<string, byte[]?>? read = null) : IWorldDocumentSource {
        public bool ResolvesFiles => (read is null);

        public bool RecordInputs(string resolvedName, byte[] content) => ((read is null) && CompileInputs.ReadAllBytes(path: resolvedName).AsSpan().SequenceEqual(other: content));

        private bool Exists(string path) => ((read is null)
            ? CompileInputs.Exists(path: path)
            : (read(arg: path) is not null));

        public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
            content = null;

            if (!TryResolveDocumentBeside(
                documentPath: out resolvedName,
                name: name,
                reason: out reason,
                referrerName: referrerName,
                sourcePath: out var sourcePath
            )) {
                return false;
            }

            if (Exists(path: sourcePath)) {
                reason = $"document '{name}' (named by {WorldDocumentLabel.Of(path: referrerName)}) has a .puck source, and no composer is installed to compile it; this host resolves .world.json documents only.";

                return false;
            }

            if (!Exists(path: resolvedName)) {
                reason = $"basis document '{name}' (named by {WorldDocumentLabel.Of(path: referrerName)}) does not exist.";

                return false;
            }

            try {
                content = ((read is null)
                    ? CompileInputs.ReadAllBytes(path: resolvedName)
                    : read(arg: resolvedName));
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                reason = $"cannot read basis document '{name}': {WorldDocumentLabel.Failure(exception: exception)}";

                return false;
            }

            reason = string.Empty;

            return true;
        }
    }
}
