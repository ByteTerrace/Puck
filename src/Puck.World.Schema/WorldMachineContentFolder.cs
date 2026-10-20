using System.Text.Json;
using Puck.Abstractions.Machines;

namespace Puck.World;

/// <summary>The content a machine's authored <see cref="WorldMachine.ContentFolders"/> hold: every file directly in
/// each folder, folder by folder in authored order and by file name in ordinal order within one, each either mountable
/// or refused with the reason. A file is mountable when the engine's content provider prepares it
/// (<see cref="IMachineValidationCatalog.CanPrepare"/>), or when the engine judges it, by its header and whatever its
/// extension, an image its machines run (<see cref="IMachineValidationCatalog.RefuseImage"/>). An engine with neither
/// mounts every file. A folder that does not exist on this machine, or stops existing while the World runs, holds
/// nothing and is reported absent, never refused. Subfolders are not searched, and nothing outside the folders is
/// listed. A folder may hold its own settings, <see cref="SettingsFile"/>, which names the file a device preferring a
/// folder default (<see cref="WorldMachine.PreferFolderDefault"/>) boots; the settings file is never listed as
/// content.</summary>
public static class WorldMachineContentFolder {
    /// <summary>The name of the settings file a content folder may hold beside its content.</summary>
    public const string SettingsFile = "folder.json";
    /// <summary>The schema tag a <see cref="SettingsFile"/> carries.</summary>
    public const string SettingsSchema = "puck.content-folder.v1";
    // A settings file is a handful of bytes; anything larger is not one.
    private const int MaximumSettingsBytes = 4096;

    /// <summary>One authored folder.</summary>
    /// <param name="Path">The folder as the document authors it.</param>
    /// <param name="Resolved">The full, forward-slashed folder it resolves to.</param>
    /// <param name="Present">Whether the folder exists on this machine.</param>
    /// <param name="Default">The file name the folder's <see cref="SettingsFile"/> names as its default, or
    /// <see langword="null"/> when it names none or the folder holds no settings file.</param>
    /// <param name="SettingsRefusal">Why the folder's <see cref="SettingsFile"/> cannot be read, or
    /// <see langword="null"/> when it reads or is absent.</param>
    public readonly record struct Folder(string Path, string Resolved, bool Present, string? Default = null, string? SettingsRefusal = null);
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

            var settingsRefusal = ReadSettings(folder: resolved, name: out var folderDefault);

            resolvedFolders.Add(item: new(
                Default: folderDefault,
                Path: folder,
                Present: Directory.Exists(path: resolved),
                Resolved: resolved,
                SettingsRefusal: settingsRefusal
            ));

            var inFolder = new List<Entry>(capacity: files.Length);

            foreach (var file in files) {
                var name = Path.GetFileName(path: file);

                if (IsSettingsFile(name: name)) {
                    continue;
                }

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

    /// <summary>Finds the default a machine's folders name: the first folder, in authored order, whose
    /// <see cref="SettingsFile"/> names a default or cannot be read decides. The named file must be one the folder holds
    /// and the engine can mount. A refusal names the folder and its settings file, never the file the settings name, so
    /// a narrated refusal carries no content name.</summary>
    /// <param name="folders">The machine's folders, as <see cref="TryList"/> returns them.</param>
    /// <param name="entries">The files the folders hold, as <see cref="TryList"/> returns them.</param>
    /// <param name="entry">The default file on success.</param>
    /// <param name="refusal">Why the deciding folder's default cannot boot, or <see langword="null"/> on success and
    /// when no folder names a default.</param>
    /// <returns><see langword="true"/> when a folder names a default the machine can mount.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="folders"/> or <paramref name="entries"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryDefault(IReadOnlyList<Folder> folders, IReadOnlyList<Entry> entries, out Entry entry, out string? refusal) {
        ArgumentNullException.ThrowIfNull(argument: folders);
        ArgumentNullException.ThrowIfNull(argument: entries);
        entry = default;
        refusal = null;

        foreach (var folder in folders) {
            if (folder.SettingsRefusal is { } unreadable) {
                refusal = $"folder '{folder.Path}' {SettingsFile} {unreadable}";
                return false;
            }
            if (folder.Default is not { } name) {
                continue;
            }

            var found = entries.Where(predicate: candidate =>
                string.Equals(a: candidate.Folder, b: folder.Path, comparisonType: StringComparison.Ordinal) &&
                string.Equals(a: candidate.Name, b: name, comparisonType: StringComparison.Ordinal)
            ).ToArray();

            if (found.Length != 1) {
                refusal = $"folder '{folder.Path}' {SettingsFile} names a default the folder does not hold";
                return false;
            }
            if (found[0].Refusal is not null) {
                refusal = $"folder '{folder.Path}' {SettingsFile} names a default the engine cannot mount (machine.content says why)";
                return false;
            }
            entry = found[0];
            return true;
        }
        return false;
    }
    /// <summary>Writes a folder's <see cref="SettingsFile"/>, naming its default or none, creating or replacing the
    /// file.</summary>
    /// <param name="folder">The full path of the folder to write the settings into; it must exist.</param>
    /// <param name="name">The file name of the folder's default, or <see langword="null"/> to name none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="folder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a bare file name or names the settings file
    /// itself.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder denies writing.</exception>
    public static void WriteSettings(string folder, string? name) {
        ArgumentNullException.ThrowIfNull(argument: folder);

        if ((name is not null) && (DefaultNameRefusal(name: name) is { } refusal)) {
            throw new ArgumentException(message: $"default {refusal}", paramName: nameof(name));
        }

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(utf8Json: buffer, options: new() {
            // A file name is written as the owner reads it (an apostrophe stays one); the file is never served.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = true,
        })) {
            writer.WriteStartObject();
            writer.WriteString(propertyName: "schema", value: SettingsSchema);
            if (name is not null) {
                writer.WriteString(propertyName: "default", value: name);
            }
            writer.WriteEndObject();
        }
        buffer.WriteByte(value: (byte)'\n');
        File.WriteAllBytes(path: Path.Combine(path1: folder, path2: SettingsFile), bytes: buffer.ToArray());
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
    /// <param name="admitted">The admitted file spelled as <see cref="Entry.ContentPath"/> spells it, the folder as
    /// authored and then the file name, so <c>machine.content</c> names it mounted; empty on refusal.</param>
    /// <param name="reason">The named refusal, or empty when the path is admitted.</param>
    /// <returns><see langword="true"/> when the path names a file directly in one of the machine's folders.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="machine"/> or <paramref name="contentPath"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryAdmit(WorldMachine machine, string? documentDirectory, string contentPath, out string admitted, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: machine);
        ArgumentNullException.ThrowIfNull(argument: contentPath);
        admitted = string.Empty;

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
                admitted = $"{folder}/{Path.GetFileName(path: file)}";
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
    private static bool IsSettingsFile(string name) => string.Equals(a: name, b: SettingsFile, comparisonType: StringComparison.OrdinalIgnoreCase);
    // Why a settings file's default is not a bare file name in its folder, or null when it is.
    private static string? DefaultNameRefusal(string name) => (((name.Length == 0) || (name is "." or "..") ||
        (name.IndexOfAny(anyOf: ['/', '\\', ':']) >= 0) || IsSettingsFile(name: name))
        ? "must be the bare name of a file in the folder"
        : null);
    // Reads a folder's settings file: its default file name, or null when it names none or there is no file. Returns
    // why the file cannot be read, or null when it reads or is absent. Strict: the schema tag is required and any other
    // member is refused.
    private static string? ReadSettings(string folder, out string? name) {
        name = null;

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: Path.Combine(path1: folder, path2: SettingsFile));
        } catch (Exception exception) when ((exception is FileNotFoundException or DirectoryNotFoundException)) {
            return null;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return "is unreadable";
        }
        if (bytes.Length > MaximumSettingsBytes) {
            return $"is larger than {MaximumSettingsBytes} bytes";
        }

        try {
            using var document = JsonDocument.Parse(utf8Json: bytes);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) {
                return "must be a JSON object";
            }

            string? schema = null;

            foreach (var member in root.EnumerateObject()) {
                switch (member.Name) {
                    case "schema" when (member.Value.ValueKind == JsonValueKind.String):
                        schema = member.Value.GetString();
                        break;
                    case "default" when (member.Value.ValueKind == JsonValueKind.String):
                        name = member.Value.GetString();
                        break;
                    case "default" when (member.Value.ValueKind == JsonValueKind.Null):
                        break;
                    case "schema" or "default":
                        name = null;
                        return $"member '{member.Name}' must be a string";
                    default:
                        name = null;
                        return $"has unknown member '{member.Name}'";
                }
            }
            if (!string.Equals(a: schema, b: SettingsSchema, comparisonType: StringComparison.Ordinal)) {
                name = null;
                return $"must carry schema \"{SettingsSchema}\"";
            }
            if ((name is not null) && (DefaultNameRefusal(name: name) is { } refusal)) {
                name = null;
                return $"default {refusal}";
            }
            return null;
        } catch (JsonException) {
            name = null;
            return "is not valid JSON";
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
