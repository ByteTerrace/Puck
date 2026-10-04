using System.Diagnostics.CodeAnalysis;
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

    /// <summary>Returns whether a document path names a document that exists: its file, or, for a
    /// <c>.world.json</c> path, the <c>.puck</c> source of that name, which is what every door that reads
    /// a document by its file name reads when no document file stands beside it.</summary>
    /// <param name="path">The document path: a document file's, or a source file's.</param>
    /// <returns><see langword="true"/> when the file exists or its source does.</returns>
    public static bool DocumentFileExists(string path) => (File.Exists(path: path) || HasSource(documentPath: path));

    private static bool HasSource(string documentPath) => (WorldDocumentName.IsDocumentFile(path: documentPath) && File.Exists(path: WorldDocumentName.SourceFile(name: WorldDocumentName.OfDocumentFile(path: documentPath))));

    /// <summary>Reads the document a path names. A <c>.puck</c> path, and a <c>.world.json</c> path whose source of that
    /// name exists (the source wins over a document file beside it, as it does for every name), read as the document that source lowers to through
    /// <paramref name="documents"/>, so a source resolves exactly as the document it compiles to would; any other
    /// path is the file as it stands.</summary>
    /// <param name="path">The document path.</param>
    /// <param name="content">The non-null document bytes on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The one-line refusal (absent, unreadable, or a source that supplies no document), or empty on
    /// success.</param>
    /// <param name="documents">The source a <c>.puck</c> reads through, or <see langword="null"/> for
    /// <see cref="LocalDocuments"/>.</param>
    /// <param name="shown">What a refusal calls the document, or <see langword="null"/> for its path.</param>
    /// <param name="countsFileRead">Whether a plain file's read counts on the boot ledger; a source's lowering always
    /// does, since it comes through the document source every composition read does.</param>
    /// <returns><see langword="true"/> when the document was read.</returns>
    public static bool TryReadDocumentFile(string path, [NotNullWhen(returnValue: true)] out byte[]? content, out string reason, IWorldDocumentSource? documents = null, string? shown = null, bool countsFileRead = false) {
        shown ??= path;
        content = null;

        if (!DocumentFileExists(path: path)) {
            reason = $"no file at {shown}";

            return false;
        }

        if (WorldDocumentName.IsSourceFile(path: path) || HasSource(documentPath: path)) {
            // A supplied source owns how a .puck root reads — it lowers to its document, named by the path without
            // its suffix — so a pin over the result covers the document that source produces, not the file's raw bytes.
            var name = (WorldDocumentName.IsSourceFile(path: path)
                ? WorldDocumentName.OfSourceFile(path: Path.GetFullPath(path: path))
                : WorldDocumentName.OfDocumentFile(path: Path.GetFullPath(path: path))
            );

            if (!TryReadDocument(
                source: (documents ?? LocalDocuments),
                content: out var lowered,
                name: name,
                reason: out var readReason,
                referrerName: path,
                resolvedName: out _
            )) {
                reason = $"cannot read {shown}: {readReason.ReplaceLineEndings(replacementText: " ")}";

                return false;
            }

            if (lowered is null) {
                reason = $"cannot read {shown}: the document source returned no content";

                return false;
            }

            content = lowered;
            reason = string.Empty;

            return true;
        }

        // The environmental read class, filtered exactly like every sibling read here (TryResolveChainFiles,
        // DirectoryDocumentSource.TryRead): a locked, half-written, or permission-refused file, whose verdict is a
        // property of the moment rather than of the bytes. Callers classify on this wording — WorldOwnedWorlds
        // quarantines a file only for a document-shape refusal — so nothing but a real I/O refusal may reach it.
        try {
            content = File.ReadAllBytes(path: path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = $"cannot read {shown}: {WorldDocumentLabel.Failure(exception: exception)}";

            return false;
        }

        if (countsFileRead) {
            WorldBootWork.Count(kind: WorldBootWork.DocumentsRead);
        }

        reason = string.Empty;

        return true;
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
