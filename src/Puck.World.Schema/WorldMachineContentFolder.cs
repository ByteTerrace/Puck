using Puck.Abstractions.Machines;

namespace Puck.World;

/// <summary>The content a machine's authored <see cref="WorldMachine.ContentFolders"/> hold: every file directly in
/// each folder, folder by folder in authored order and by file name in ordinal order within one, each either mountable
/// or refused with the reason. A file is mountable when the engine's content provider prepares it
/// (<see cref="IMachineValidationCatalog.CanPrepare"/>), or when the engine judges it, by its header and whatever its
/// extension, an image its machines run (<see cref="IMachineValidationCatalog.RefuseImage"/>). An engine with neither
/// mounts every file. A folder that does not exist on this machine, or stops existing while the World runs, holds
/// nothing and is reported absent, never refused. Subfolders are not searched, and nothing outside the folders is
/// listed.</summary>
public static class WorldMachineContentFolder {
    /// <summary>One authored folder.</summary>
    /// <param name="Path">The folder as the document authors it.</param>
    /// <param name="Resolved">The full, forward-slashed folder it resolves to.</param>
    /// <param name="Present">Whether the folder exists on this machine.</param>
    public readonly record struct Folder(string Path, string Resolved, bool Present);
    /// <summary>One file a folder holds.</summary>
    /// <param name="Name">The file name, as <c>machine.swap</c> takes it.</param>
    /// <param name="Folder">The authored folder the file is in.</param>
    /// <param name="ContentPath">The path the machine's content operation mounts, relative to the document directory
    /// the folder is authored against, spelled with <c>/</c>: the folder, then the file name.</param>
    /// <param name="Refusal">Why the machine's engine cannot mount the file, or <see langword="null"/> when it
    /// can.</param>
    public readonly record struct Entry(string Name, string Folder, string ContentPath, string? Refusal = null);

    /// <summary>Lists the content a machine's folders offer.</summary>
    /// <param name="machine">The machine declaration.</param>
    /// <param name="documentDirectory">The directory of the document the folders are relative to, or
    /// <see langword="null"/> for a document with none.</param>
    /// <param name="catalog">The host's machine catalog, which decides what the machine's engine can mount.</param>
    /// <param name="folders">The authored folders in order, each with whether it exists; empty on refusal.</param>
    /// <param name="entries">The files the folders hold, folder by folder; empty on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the machine names folders that all resolve to paths.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="machine"/> or <paramref name="catalog"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryList(WorldMachine machine, string? documentDirectory, IMachineValidationCatalog catalog,
        out IReadOnlyList<Folder> folders, out IReadOnlyList<Entry> entries, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: machine);
        ArgumentNullException.ThrowIfNull(argument: catalog);
        folders = [];
        entries = [];

        if (machine.ContentFolders is not { Count: > 0 } authored) {
            reason = $"machine '{machine.Name}' names no contentFolders";
            return false;
        }

        var resolvedFolders = new List<Folder>(capacity: authored.Count);
        var listed = new List<Entry>();
        var provided = catalog.HasContentProvider(engineId: machine.Engine);
        var headerLength = catalog.ImageHeaderLength(engineId: machine.Engine);

        foreach (var folder in authored) {
            if (!WorldDocumentPaths.TryResolve(
                documentDirectory: documentDirectory,
                path: folder,
                reason: out var unresolved,
                resolved: out var resolved
            )) {
                reason = $"machine '{machine.Name}' contentFolders {unresolved}";
                return false;
            }

            string[] files;

            try {
                files = Directory.GetFiles(path: resolved);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                // Absent, or removed or locked between runs of this listing: the folder holds nothing now.
                files = [];
            }

            resolvedFolders.Add(item: new(
                Path: folder,
                Present: Directory.Exists(path: resolved),
                Resolved: resolved
            ));

            var inFolder = new List<Entry>(capacity: files.Length);

            foreach (var file in files) {
                var name = Path.GetFileName(path: file);

                inFolder.Add(item: new(
                    ContentPath: $"{folder}/{name}",
                    Folder: folder,
                    Name: name,
                    Refusal: ((provided && catalog.CanPrepare(contentPath: name, engineId: machine.Engine))
                        ? null
                        : ((headerLength > 0)
                            ? RefuseImage(catalog: catalog, engineId: machine.Engine, headerLength: headerLength, path: file)
                            : (provided
                                ? $"not content the '{machine.Engine}' provider prepares"
                                : null)))
                ));
            }
            inFolder.Sort(comparison: static (left, right) => string.CompareOrdinal(
                strA: left.Name,
                strB: right.Name
            ));
            listed.AddRange(collection: inFolder);
        }
        folders = resolvedFolders;
        entries = listed;
        reason = string.Empty;
        return true;
    }
    /// <summary>Finds the mountable file a console argument names: its whole file name, its folder and file name as
    /// <see cref="Entry.ContentPath"/> spells them, or the part of its file name before the first <c>.</c>, which names
    /// a mountable file before a refused one. A name that answers more than one file names none, and a name that answers
    /// a refused file is refused with its reason.</summary>
    /// <param name="entries">The files the folders hold, as <see cref="TryList"/> returns them.</param>
    /// <param name="name">The file name, folder-qualified name or stem to find.</param>
    /// <param name="entry">The named file on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when exactly one file answers the name and the engine can mount it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or <paramref name="name"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryFind(IReadOnlyList<Entry> entries, string name, out Entry entry, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: entries);
        ArgumentNullException.ThrowIfNull(argument: name);

        var whole = entries.Where(predicate: candidate =>
            string.Equals(a: candidate.Name, b: name, comparisonType: StringComparison.Ordinal) ||
            string.Equals(a: candidate.ContentPath, b: name, comparisonType: StringComparison.Ordinal)
        ).ToArray();

        if (whole.Length > 1) {
            entry = default;
            reason = $"'{name}' names {whole.Length} files ({Describe(entries: whole, qualified: true)}); give the folder and file name";
            return false;
        }

        // A stem names a mountable file first, so a cartridge's source beside it does not make the stem ambiguous; a stem
        // only refused files answer still names them, to say why.
        var stems = entries.Where(predicate: candidate => string.Equals(
            a: Stem(name: candidate.Name),
            b: name,
            comparisonType: StringComparison.Ordinal
        )).ToArray();
        var mountable = stems.Where(predicate: static candidate => (candidate.Refusal is null)).ToArray();
        var matches = ((whole.Length == 1)
            ? whole
            : ((mountable.Length > 0) ? mountable : stems)
        );

        if (matches.Length == 1) {
            entry = matches[0];

            if (entry.Refusal is { } refusal) {
                reason = $"'{entry.ContentPath}' cannot be mounted: {refusal}";
                entry = default;
                return false;
            }
            reason = string.Empty;
            return true;
        }
        entry = default;
        reason = ((matches.Length == 0)
            ? $"'{name}' names no file the folders hold ({Describe(entries: entries, qualified: false)})"
            : $"'{name}' names {matches.Length} files ({Describe(entries: matches, qualified: true)}); give the whole file name"
        );
        return false;
    }

    /// <summary>Decides whether a content path a provider operation would mount names a file directly in one of the
    /// machine's authored folders, the only content a machine mounts besides what its author boots it with. The path
    /// resolves beside the document as every content path does, rooted or relative, and its <c>..</c> segments collapse
    /// first, so a spelling that leaves a folder and comes back is judged by where it lands. The file's directory must
    /// be a folder as the document resolves it, or the folder's own link target when the folder is a symbolic link or a
    /// junction; a file that is itself a link must lead into that folder too. A subfolder is not a folder. Whether the
    /// file exists, and whether the engine can mount it, the operation decides afterwards.</summary>
    /// <param name="machine">The machine declaration whose folders admit the path.</param>
    /// <param name="documentDirectory">The directory of the document the folders and the path are relative to, or
    /// <see langword="null"/> for a document with none.</param>
    /// <param name="contentPath">The content path the operation names.</param>
    /// <param name="reason">The named refusal, or empty when the path is admitted.</param>
    /// <returns><see langword="true"/> when the path names a file directly in one of the machine's folders.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="machine"/> or <paramref name="contentPath"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryAdmit(WorldMachine machine, string? documentDirectory, string contentPath, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: machine);
        ArgumentNullException.ThrowIfNull(argument: contentPath);

        if (machine.ContentFolders is not { Count: > 0 } authored) {
            reason = $"machine '{machine.Name}' names no contentFolders, so '{contentPath}' is not content it can mount";
            return false;
        }
        if (!TryFull(documentDirectory: documentDirectory, path: contentPath, full: out var file, reason: out var unresolved)) {
            reason = $"'{contentPath}' {unresolved}";
            return false;
        }

        var directory = Path.GetDirectoryName(path: file);
        var target = LinkedFile(file: file);

        foreach (var folder in authored) {
            if (!TryFull(documentDirectory: documentDirectory, path: folder, full: out var resolved, reason: out _)) {
                continue;
            }

            var linked = LinkedDirectory(directory: resolved);

            if (
                (SamePath(left: directory, right: resolved) || SamePath(left: directory, right: linked)) &&
                ((target is null) || SamePath(left: Path.GetDirectoryName(path: target), right: linked))
            ) {
                reason = string.Empty;
                return true;
            }
        }
        reason = $"'{contentPath}' is not a file in machine '{machine.Name}' contentFolders ({string.Join(separator: ", ", values: authored)})";
        return false;
    }

    // Resolves a path beside the document and collapses its dot segments, spelled the platform's way so it compares
    // with what the file system reports.
    private static bool TryFull(string? documentDirectory, string path, out string full, out string reason) {
        full = string.Empty;

        if (!WorldDocumentPaths.TryResolve(documentDirectory: documentDirectory, path: path, reason: out reason, resolved: out var resolved)) {
            return false;
        }
        full = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: resolved));
        return true;
    }
    // The directory a folder names on disk: its final link target when it is a symbolic link or a junction, else itself.
    private static string LinkedDirectory(string directory) {
        try {
            return ((new DirectoryInfo(path: directory).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                ? Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: target.FullName))
                : directory);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return directory;
        }
    }
    // The file a link resolves to, or null when the path is no link (or names nothing yet).
    private static string? LinkedFile(string file) {
        try {
            return ((new FileInfo(fileName: file).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                ? Path.GetFullPath(path: target.FullName)
                : null);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }
    }
    private static bool SamePath(string? left, string right) => string.Equals(
        a: left,
        b: right,
        comparisonType: (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
    );
    private static string Describe(IReadOnlyList<Entry> entries, bool qualified) => ((entries.Count == 0)
        ? "none"
        : string.Join(
            separator: ", ",
            values: entries.Select(selector: entry => (qualified ? entry.ContentPath : entry.Name))
        ));
    // Reads a file's leading bytes and asks the engine why its machines do not run the image, if they do not.
    private static string? RefuseImage(IMachineValidationCatalog catalog, string engineId, int headerLength, string path) {
        try {
            using var stream = new FileStream(
                access: FileAccess.Read,
                mode: FileMode.Open,
                path: path,
                share: FileShare.Read
            );
            var header = new byte[(int)Math.Min(val1: headerLength, val2: stream.Length)];

            stream.ReadExactly(buffer: header);

            return catalog.RefuseImage(
                engineId: engineId,
                header: header,
                imageLength: stream.Length
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return $"unreadable ({exception.Message.ReplaceLineEndings(replacementText: " ")})";
        }
    }
    private static string Stem(string name) {
        var dot = name.IndexOf(value: '.');

        return ((dot < 0)
            ? name
            : name[..dot]
        );
    }
}
