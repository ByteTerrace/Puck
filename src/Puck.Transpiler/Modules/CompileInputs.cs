using System.Text;
using Puck.Assets;

namespace Puck.Transpiler.Modules;

/// <summary>What a compile learned from one file or directory: that a file was absent, that it existed, its exact
/// bytes, or which files a directory holds.</summary>
public enum CompileInputKind {
    /// <summary>The compile probed the path and found no file there.</summary>
    Absent = 0,
    /// <summary>The compile probed the path and found a file there, without reading it.</summary>
    Present = 1,
    /// <summary>The compile read the file's bytes.</summary>
    Content = 2,
    /// <summary>The compile listed the files directly in the directory at the path, each name spelled as the file
    /// system spells it, so a file added, removed or renamed there, in any letter case, moves the fact. A directory
    /// that does not exist lists no file.</summary>
    Listing = 3,
}
/// <summary>One fact a compile read from the file system, and what must still hold for its output to stand.</summary>
/// <param name="Path">The path exactly as the compile spelled it; a relative path is resolved against the current
/// directory when the fact is checked, as it was when the compile probed it.</param>
/// <param name="Kind">What the compile learned.</param>
/// <param name="ContentHash">The <see cref="ContentPin.Hex"/> of the bytes read, for <see cref="CompileInputKind.Content"/>,
/// or of the names listed, for <see cref="CompileInputKind.Listing"/>; empty otherwise.</param>
public readonly record struct CompileInput(string Path, CompileInputKind Kind, string ContentHash) {
    /// <summary>Returns whether the file system still answers this fact the way it answered the compile: the file is
    /// still absent, still present, or still holds the same bytes, or the directory still lists the same files.</summary>
    /// <returns><see langword="true"/> when the fact still holds; <see langword="false"/> when it moved or the file
    /// can no longer be read.</returns>
    public bool StillHolds() {
        try {
            return Kind switch {
                CompileInputKind.Absent => !File.Exists(path: Path),
                CompileInputKind.Present => File.Exists(path: Path),
                CompileInputKind.Listing => string.Equals(
                    a: CompileInputs.ListingHash(files: CompileInputs.FilesIn(directory: Path)),
                    b: ContentHash,
                    comparisonType: StringComparison.Ordinal
                ),
                _ => (File.Exists(path: Path) && string.Equals(
                    a: ContentPin.OfFile(path: Path).Hex,
                    b: ContentHash,
                    comparisonType: StringComparison.Ordinal
                )),
            };
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return false;
        }
    }
}
/// <summary>The facts one compile read, in first-read order, each recorded once.</summary>
public sealed class CompileInputLog {
    private readonly List<CompileInput> m_inputs = [];
    private readonly Lock m_lock = new();
    private readonly HashSet<(string Path, CompileInputKind Kind)> m_seen = [];

    /// <summary>Gets a snapshot of the facts recorded so far.</summary>
    public IReadOnlyList<CompileInput> Inputs {
        get {
            lock (m_lock) {
                return [.. m_inputs];
            }
        }
    }

    internal void Add(CompileInput input) {
        lock (m_lock) {
            if (m_seen.Add(item: (input.Path, input.Kind))) {
                m_inputs.Add(item: input);
            }
        }
    }
}
/// <summary>
/// The one door a compile reads the file system through, so what it read can be recorded and later proved unchanged:
/// the source, every module its import walk reads, every lock and asset beside it, every path it only probes, and
/// every directory it lists.
/// Outside a <see cref="Record"/> scope a read is an ordinary file read. Inside one, each read is recorded in every
/// enclosing scope's log, so a compile that runs another compile sees the inner one's reads too.
/// </summary>
public static class CompileInputs {
    private static readonly AsyncLocal<Frame?> Recording = new();

    private static void Add(CompileInput input) {
        for (var frame = Recording.Value; (frame is not null); frame = frame.Parent) {
            frame.Log.Add(input: input);
        }
    }

    /// <summary>Returns whether a file exists at <paramref name="path"/>, recording the answer.</summary>
    /// <param name="path">The path to probe.</param>
    /// <returns><see langword="true"/> when a file exists there.</returns>
    public static bool Exists(string path) {
        var exists = File.Exists(path: path);

        Add(input: new CompileInput(
            ContentHash: string.Empty,
            Kind: (exists
                ? CompileInputKind.Present
                : CompileInputKind.Absent),
            Path: path
        ));

        return exists;
    }
    /// <summary>Lists the files directly in <paramref name="directory"/>, hidden ones included, recording the listing
    /// (<see cref="CompileInputKind.Listing"/>), so a compile that chose among a directory's files rests on which files
    /// were there to choose from and how each name was spelled.</summary>
    /// <param name="directory">The directory to list. A directory that does not exist lists no file.</param>
    /// <returns>The files' paths, rooted as <paramref name="directory"/> is, in ordinal order.</returns>
    /// <exception cref="IOException">The directory could not be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory may not be listed.</exception>
    public static IReadOnlyList<string> List(string directory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);

        var files = FilesIn(directory: directory);

        Add(input: new CompileInput(
            ContentHash: ListingHash(files: files),
            Kind: CompileInputKind.Listing,
            Path: directory
        ));

        return files;
    }

    internal static string[] FilesIn(string directory) => (Directory.Exists(path: directory)
        ? [.. Directory.EnumerateFiles(
            enumerationOptions: new EnumerationOptions {
                AttributesToSkip = 0,
                MatchType = MatchType.Simple,
                RecurseSubdirectories = false,
            },
            path: directory,
            searchPattern: "*"
        ).Order(comparer: StringComparer.Ordinal)]
        : []
    );
    // A listing's digest covers each file's name as the file system spells it, so a rename that changes only letter
    // case moves it as surely as a file added or removed.
    internal static string ListingHash(IReadOnlyList<string> files) => ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: string.Join(
        separator: '\n',
        values: files.Select(selector: static file => System.IO.Path.GetFileName(path: file)).Order(comparer: StringComparer.Ordinal)
    ))).Hex;

    /// <summary>Records <paramref name="content"/> as what the compile read from <paramref name="path"/> through a
    /// reader of its own.</summary>
    /// <param name="path">The path the bytes came from.</param>
    /// <param name="content">The bytes read, whole.</param>
    public static void Note(string path, ReadOnlySpan<byte> content) {
        if (Recording.Value is null) {
            return;
        }

        Add(input: new CompileInput(
            ContentHash: ContentPin.Compute(content: content).Hex,
            Kind: CompileInputKind.Content,
            Path: path
        ));
    }
    /// <summary>Records facts another compile read, for a compile that uses that compile's output without running it
    /// again: a compile served from a cache rests on every fact the held compile rests on.</summary>
    /// <param name="inputs">The facts the held compile read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inputs"/> is <see langword="null"/>.</exception>
    public static void Restate(IReadOnlyList<CompileInput> inputs) {
        ArgumentNullException.ThrowIfNull(argument: inputs);

        if (Recording.Value is null) {
            return;
        }

        foreach (var input in inputs) {
            Add(input: input);
        }
    }
    /// <summary>Reads the whole file at <paramref name="path"/>, recording its bytes, or its absence when there is
    /// no file to read.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on <paramref name="path"/> does not exist.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static byte[] ReadAllBytes(string path) {
        byte[] content;

        try {
            content = File.ReadAllBytes(path: path);
        } catch (Exception exception) when ((exception is FileNotFoundException or DirectoryNotFoundException)) {
            Add(input: new CompileInput(
                ContentHash: string.Empty,
                Kind: CompileInputKind.Absent,
                Path: path
            ));

            throw;
        }

        Note(
            content: content,
            path: path
        );

        return content;
    }
    /// <summary>Reads the whole file at <paramref name="path"/> as text the way <see cref="File.ReadAllText(string)"/>
    /// does — a byte-order mark selects the encoding, UTF-8 otherwise — recording its bytes.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The file's text.</returns>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on <paramref name="path"/> does not exist.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static string ReadAllText(string path) =>
        ReadAllText(
            encoding: Encoding.UTF8,
            path: path
        );
    /// <summary>Reads the whole file at <paramref name="path"/> as text in <paramref name="encoding"/> unless a
    /// byte-order mark names another, as <see cref="File.ReadAllText(string, Encoding)"/> does, recording its
    /// bytes.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="encoding">The encoding assumed when the file carries no byte-order mark.</param>
    /// <returns>The file's text.</returns>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on <paramref name="path"/> does not exist.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static string ReadAllText(string path, Encoding encoding) {
        var content = ReadAllBytes(path: path);

        using var reader = new StreamReader(
            detectEncodingFromByteOrderMarks: true,
            encoding: encoding,
            stream: new MemoryStream(buffer: content)
        );

        return reader.ReadToEnd();
    }
    /// <summary>Records every file read on the calling flow, and every flow it starts, into <paramref name="log"/>
    /// until the returned scope is disposed.</summary>
    /// <param name="log">The log to record into.</param>
    /// <returns>The scope that ends the recording.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is <see langword="null"/>.</exception>
    public static Scope Record(CompileInputLog log) {
        ArgumentNullException.ThrowIfNull(argument: log);

        var previous = Recording.Value;

        Recording.Value = new Frame(
            Log: log,
            Parent: previous
        );

        return new Scope(previous: previous);
    }

    internal sealed record Frame(CompileInputLog Log, Frame? Parent);

    /// <summary>Ends a <see cref="Record"/> scope when disposed.</summary>
    public readonly struct Scope : IDisposable {
        private readonly Frame? m_previous;

        internal Scope(Frame? previous) {
            m_previous = previous;
        }

        /// <summary>Restores the recording that was active before the scope began.</summary>
        public void Dispose() =>
            Recording.Value = m_previous;
    }
}
