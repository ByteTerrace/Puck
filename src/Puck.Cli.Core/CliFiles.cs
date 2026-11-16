using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Confinement;

namespace Puck.Cli;

// Whole-file JSON and directory-tree operations the automation verbs share. Written JSON is indented and
// newline-terminated so a committed artifact diffs line by line.
public static class CliFiles {
    private static readonly JsonSerializerOptions Indented = new() { NewLine = "\n", WriteIndented = true };

    // Copies the files beneath source to the same relative paths beneath destination and returns how many it copied.
    // Without a selection it copies the whole tree, empty directories included; a selection (any files beneath source)
    // copies only those files and the directories that hold them. Each file keeps its last write time and, on Unix,
    // its mode; each directory created takes its source directory's mode.
    //
    // Both roots are the caller's choice and resolved as the operating system resolves them; everything below them is
    // reached through ConfinedDirectory handles, one component at a time, so neither traversal follows a link: a link
    // in the source tree or the destination tree refuses the copy there, leaving the files already copied. A replaced
    // file loses only its own name before a fresh file is created (never a write over it), so a hard link under a build
    // output keeps the bytes it shares with the NuGet cache, a source or another output.
    //
    // With intoEmpty the destination must be missing or empty and no file is replaced: a name that appears meanwhile
    // refuses the copy rather than being overwritten, so the result is exactly the source's files.
    public static int CopyDirectory(string source, string destination, IEnumerable<string>? files = null, bool intoEmpty = false) {
        using var from = (ConfinedDirectory.OpenAnchor(create: false, path: source)
            ?? throw new DirectoryNotFoundException(message: $"'{source}' does not exist."));
        using var to = ConfinedDirectory.OpenAnchor(create: true, path: destination)!;
        var copied = 0;

        if (intoEmpty && (to.Entries().Count > 0)) {
            throw new IOException(message: $"'{destination}' is not empty; a clean copy goes only into an empty or missing directory.");
        }
        if (files is null) {
            foreach (var item in from.Walk()) {
                switch (item.Entry.Kind) {
                    case ConfinedEntryKind.Directory:
                        OpenPair(from: from, relativePath: item.Path, to: to).Dispose();
                        break;
                    case ConfinedEntryKind.File:
                        // The walk lists every directory before its files, so OpenPair has already made this parent.
                        var parent = Path.GetDirectoryName(path: item.Path);
                        using (var target = (string.IsNullOrEmpty(value: parent) ? null : to.Descend(create: true, relativePath: parent))) {
                            CopyFile(from: item.Directory, name: item.Entry.Name, replace: !intoEmpty, to: (target ?? to));
                        }
                        copied++;
                        break;
                    default:
                        throw new IOException(message: $"'{Path.Combine(path1: source, path2: item.Path)}' is a link or not a regular file; a tree copy never follows one.");
                }
            }
            return copied;
        }
        var sourceRoot = Path.GetFullPath(path: source);

        foreach (var file in files) {
            var relative = Path.GetRelativePath(path: Path.GetFullPath(path: file), relativeTo: sourceRoot);

            if (Path.IsPathRooted(path: relative) || (relative == "..") || relative.StartsWith(value: $"..{Path.DirectorySeparatorChar}", comparisonType: StringComparison.Ordinal)) {
                throw new ArgumentException(message: $"'{file}' is not beneath '{source}'.", paramName: nameof(files));
            }
            var parent = Path.GetDirectoryName(path: relative);
            using var pair = (string.IsNullOrEmpty(value: parent) ? null : OpenPair(from: from, relativePath: parent, to: to));

            CopyFile(from: (pair?.From ?? from), name: Path.GetFileName(path: relative), replace: !intoEmpty, to: (pair?.To ?? to));
            copied++;
        }
        return copied;
    }
    // Opens a directory below both roots in step, one component at a time through handles, creating each destination
    // component that is missing and giving it, on Unix, its source directory's mode.
    private static DirectoryPair OpenPair(ConfinedDirectory from, ConfinedDirectory to, string relativePath) {
        var pair = new DirectoryPair();

        try {
            foreach (var segment in relativePath.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ['/', Path.DirectorySeparatorChar])) {
                var source = ((pair.From ?? from).Child(name: segment)
                    ?? throw new DirectoryNotFoundException(message: $"'{Path.Combine(path1: from.Path, path2: relativePath)}' does not exist."));
                ConfinedDirectory target;

                try {
                    target = (pair.To ?? to).Child(create: true, name: segment)!;
                } catch { source.Dispose(); throw; }
                pair.Step(from: source, to: target);
            }
            return pair;
        } catch { pair.Dispose(); throw; }
    }
    private static void CopyFile(ConfinedDirectory from, string name, ConfinedDirectory to, bool replace) {
        using var input = (from.OpenRead(name: name) ?? throw new FileNotFoundException(message: $"'{Path.Combine(path1: from.Path, path2: name)}' does not exist."));

        if (replace) { _ = to.DeleteFile(relativePath: name); }
        using var output = to.OpenFile(create: true, name: name, newFile: true)!;

        input.CopyTo(destination: output);
        output.Flush();
        File.SetLastWriteTimeUtc(fileHandle: output.SafeFileHandle, lastWriteTimeUtc: File.GetLastWriteTimeUtc(fileHandle: input.SafeFileHandle));
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(fileHandle: output.SafeFileHandle, mode: File.GetUnixFileMode(fileHandle: input.SafeFileHandle));
        }
    }
    private sealed class DirectoryPair : IDisposable {
        public ConfinedDirectory? From { get; private set; }
        public ConfinedDirectory? To { get; private set; }

        public void Step(ConfinedDirectory from, ConfinedDirectory to) {
            From?.Dispose();
            To?.Dispose();
            From = from;
            To = to;
            if (!OperatingSystem.IsWindows()) { to.UnixMode = from.UnixMode; }
        }
        public void Dispose() {
            From?.Dispose();
            To?.Dispose();
        }
    }
    public static JsonNode ReadJson(string path) =>
        (JsonNode.Parse(json: File.ReadAllText(path: path)) ?? throw new InvalidDataException(message: $"Empty JSON: {path}"));
    public static void WriteJson(string path, JsonNode value) {
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: Path.GetFullPath(path: path))!);
        File.WriteAllText(
            contents: (value.ToJsonString(options: Indented) + "\n"),
            path: path
        );
    }
}
