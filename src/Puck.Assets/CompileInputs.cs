using System.Collections.Concurrent;
using System.Text;

namespace Puck.Assets;

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
    /// <summary>Returns the current answer to the same filesystem query, without recording a compiler input. A file's
    /// digest or a directory's listing is read again only when its stamp no longer vouches for the answer last read
    /// (<see cref="CompileInputs.Current"/>).</summary>
    /// <returns>The current existence, content, or listing fact. A missing content file answers absent.</returns>
    /// <exception cref="IOException">The file or directory cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file or directory is refused.</exception>
    public CompileInput ReadCurrent() => CompileInputs.Current(query: this, read: out _);
    /// <summary>Returns whether the file system still answers this fact the way it answered the compile: the file is
    /// still absent, still present, or still holds the same bytes, or the directory still lists the same files.</summary>
    /// <returns><see langword="true"/> when the fact still holds; <see langword="false"/> when it moved or the file
    /// can no longer be read.</returns>
    public bool StillHolds() {
        try {
            return (this == ReadCurrent());
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
/// <para>
/// The door also remembers, for the process, the digest of every file it read and the names every directory it listed
/// held, each under the stamp (existence, length and last write) the file or directory had just before the read. A
/// later question about the same path is answered from that memory while the stamp is unchanged
/// (<see cref="Current"/>, <see cref="Digest"/>, <see cref="List"/>), so proving a held product still stands costs one
/// stat per input rather than a read and a hash. A stamp written within <see cref="RacyStampWindow"/> of the read cannot
/// prove a later write within the same timestamp tick moved it, so such an answer is never trusted and the path is read
/// again until its stamp is older. A path whose bytes are derived rather than read (a source-backed WebAssembly module,
/// <see cref="WasmGuestBuild.MayName"/>) is always read.
/// </para>
/// </summary>
public static class CompileInputs {
    /// <summary>How recent a last-write stamp may be, measured when its file or directory was read, before it proves
    /// nothing: the coarsest file-system timestamp granularity the door trusts.</summary>
    public static readonly TimeSpan RacyStampWindow = TimeSpan.FromSeconds(seconds: 2);
    /// <summary>The most files and directories whose last answer the process remembers; remembering one more past it
    /// starts the memory afresh.</summary>
    public const int MaxRememberedPaths = 65536;

    internal static readonly FileSystemAssetSource Assets = new();
    private static readonly AsyncLocal<Frame?> Recording = new();
    // The last answer read for each file ("F" + full path) or directory listing ("D" + full path), under the stamp the
    // path had when it was read.
    private static readonly ConcurrentDictionary<string, Known> Remembered = new(comparer: Puck.Abstractions.PuckPaths.Comparer);
    private static long StampReadCount;
    private static long ContentReadCount;

    /// <summary>Gets the number of file and directory stamps the door has read in this process: one per question it
    /// answered from the file system.</summary>
    public static long StampReads => Interlocked.Read(location: ref StampReadCount);
    /// <summary>Gets the number of file contents the door has read and directories it has listed in this process,
    /// which it does only for a path whose stamp does not vouch for the answer it last read.</summary>
    public static long ContentReads => Interlocked.Read(location: ref ContentReadCount);
    /// <summary>Gets the number of distinct files and directories whose last answer the door remembers: about the
    /// number of files and directories the process has read, up to <see cref="MaxRememberedPaths"/>.</summary>
    public static int RememberedPaths => Remembered.Count;

    // A file's or directory's stamp: whether it exists, its length (a file's; zero for a directory) and its last write.
    private readonly record struct Stamp(bool Exists, long Length, DateTime Written);
    // The digest last read for a path under the stamp it had then, the names a listing held, and whether that stamp
    // was too recent to vouch for anything.
    private sealed record Known(Stamp Stamp, string Hash, string[]? Names, bool Racy);

    private static void Add(CompileInput input) {
        for (var frame = Recording.Value; (frame is not null); frame = frame.Parent) {
            frame.Log.Add(input: input);
        }
    }
    private static Stamp FileStamp(string fullPath) => StampOf(entry: new FileInfo(fileName: fullPath));
    private static Stamp DirectoryStamp(string fullPath) => StampOf(entry: new DirectoryInfo(path: fullPath));
    // One stat: a file's length, or zero for a directory, and the entry's last write.
    private static Stamp StampOf(FileSystemInfo entry) {
        Interlocked.Increment(location: ref StampReadCount);

        return (entry.Exists ? new Stamp(Exists: true, Length: ((entry is FileInfo file) ? file.Length : 0L), Written: entry.LastWriteTimeUtc) : default);
    }
    // The answer remembered under `stamp`, when it was read under exactly that stamp and the stamp was old enough then.
    private static Known? Vouched(string key, Stamp stamp) => ((Remembered.TryGetValue(key: key, value: out var known) && !known.Racy && (known.Stamp == stamp))
        ? known
        : null);
    // Remembers what was read under `stamp`, which was taken before the read: a write landing between the stamp and
    // the read moves the stamp, so the next question reads again rather than trusting these bytes under a new stamp.
    private static void Remember(string key, Stamp stamp, string hash, string[]? names) {
        if (Remembered.Count >= MaxRememberedPaths) {
            Remembered.Clear();
        }

        Remembered[key] = new Known(
            Hash: hash,
            Names: names,
            Racy: (stamp.Exists && ((DateTime.UtcNow - stamp.Written) < RacyStampWindow)),
            Stamp: stamp
        );
    }
    private static string FileKey(string fullPath) => ("F" + fullPath);
    private static string ListingKey(string fullPath) => ("D" + fullPath);
    // Reads a file's bytes whole and remembers their digest under the stamp taken just before, unless the path's bytes
    // are derived rather than the file's own.
    private static byte[] ReadRemembering(string path, out string hash) {
        var fullPath = System.IO.Path.GetFullPath(path: path);
        var trusted = !WasmGuestBuild.MayName(path: fullPath);
        var stamp = (trusted ? FileStamp(fullPath: fullPath) : default);

        Interlocked.Increment(location: ref ContentReadCount);

        var content = Assets.ReadBytes(path: path);

        hash = ContentPin.Compute(content: content).Hex;
        if (trusted && stamp.Exists) {
            Remember(hash: hash, key: FileKey(fullPath: fullPath), names: null, stamp: stamp);
        }

        return content;
    }
    // The file names directly in a directory, in ordinal order, remembered under the directory's stamp; a directory's
    // last write moves when a file directly in it is added, removed or renamed.
    private static string[] Names(string directory, out string hash, out bool read) {
        var fullPath = System.IO.Path.GetFullPath(path: directory);
        var key = ListingKey(fullPath: fullPath);
        var stamp = DirectoryStamp(fullPath: fullPath);

        if (Vouched(key: key, stamp: stamp) is { Names: { } held } known) {
            hash = known.Hash;
            read = false;

            return held;
        }

        Interlocked.Increment(location: ref ContentReadCount);

        var names = (stamp.Exists
            ? Directory.EnumerateFiles(
                enumerationOptions: new EnumerationOptions {
                    AttributesToSkip = 0,
                    MatchType = MatchType.Simple,
                    RecurseSubdirectories = false,
                },
                path: directory,
                searchPattern: "*"
            ).Select(selector: static file => System.IO.Path.GetFileName(path: file)).Order(comparer: StringComparer.Ordinal).ToArray()
            : []);

        hash = ListingHash(names: names);
        read = true;
        Remember(hash: hash, key: key, names: names, stamp: stamp);

        return names;
    }
    // A listing's digest covers each file's name as the file system spells it, so a rename that changes only letter
    // case moves it as surely as a file added or removed.
    private static string ListingHash(string[] names) => ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: string.Join(separator: '\n', value: names))).Hex;

    /// <summary>Returns the current answer to <paramref name="query"/>'s filesystem question, without recording it:
    /// whether a file exists, the digest of its bytes, or the names a directory holds. A digest or a listing is read
    /// again only when the path's stamp has moved since the answer last read, or was too recent then to vouch for it;
    /// existence is answered from the stamp alone.</summary>
    /// <param name="query">The fact whose path and kind name the question; its digest is ignored.</param>
    /// <param name="read">Whether answering read a file's contents or listed a directory.</param>
    /// <returns>The current fact. A missing content file answers <see cref="CompileInputKind.Absent"/>.</returns>
    /// <exception cref="IOException">The file or directory cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file or directory is refused.</exception>
    public static CompileInput Current(CompileInput query, out bool read) {
        if (query.Kind == CompileInputKind.Listing) {
            _ = Names(directory: query.Path, hash: out var hash, read: out read);

            return new CompileInput(ContentHash: hash, Kind: CompileInputKind.Listing, Path: query.Path);
        }

        var digest = Answer(path: query.Path, content: out _, read: out read, wantDigest: (query.Kind == CompileInputKind.Content));

        return ((digest is null)
            ? new CompileInput(ContentHash: string.Empty, Kind: CompileInputKind.Absent, Path: query.Path)
            : ((query.Kind == CompileInputKind.Content)
                ? new CompileInput(ContentHash: digest, Kind: CompileInputKind.Content, Path: query.Path)
                : new CompileInput(ContentHash: string.Empty, Kind: CompileInputKind.Present, Path: query.Path)));
    }
    // A file's digest (empty when only existence was asked), or null when no file is there. `content` is the bytes when
    // answering read them.
    private static string? Answer(string path, bool wantDigest, out byte[]? content, out bool read) {
        content = null;
        read = false;

        var fullPath = System.IO.Path.GetFullPath(path: path);

        if (WasmGuestBuild.MayName(path: fullPath)) {
            if (!Assets.Exists(path: path)) {
                return null;
            }
            if (!wantDigest) {
                return string.Empty;
            }
            read = true;
            Interlocked.Increment(location: ref ContentReadCount);
            content = Assets.ReadBytes(path: path);

            return ContentPin.Compute(content: content).Hex;
        }

        var stamp = FileStamp(fullPath: fullPath);

        if (!stamp.Exists) {
            return null;
        }
        if (!wantDigest) {
            return string.Empty;
        }
        if (Vouched(key: FileKey(fullPath: fullPath), stamp: stamp) is { } known) {
            return known.Hash;
        }

        read = true;
        content = ReadRemembering(hash: out var hash, path: path);

        return hash;
    }

    /// <summary>Returns whether a file exists at <paramref name="path"/>, recording the answer.</summary>
    /// <param name="path">The path to probe.</param>
    /// <returns><see langword="true"/> when a file exists there.</returns>
    public static bool Exists(string path) {
        var exists = (Answer(content: out _, path: path, read: out _, wantDigest: false) is not null);

        Add(input: new CompileInput(
            ContentHash: string.Empty,
            Kind: (exists
                ? CompileInputKind.Present
                : CompileInputKind.Absent),
            Path: path
        ));

        return exists;
    }
    /// <summary>Returns the digest (<see cref="ContentPin.Hex"/>) of the bytes of the file at <paramref name="path"/>,
    /// recording it as what the compile read there, or recording the file's absence. The file is read only when its
    /// stamp does not vouch for the digest last read (<see cref="Current"/>).</summary>
    /// <param name="path">The file.</param>
    /// <param name="content">The file's bytes when answering read them; <see langword="null"/> when the remembered
    /// digest answered or no file is there.</param>
    /// <returns>The digest, or <see langword="null"/> when no file exists at <paramref name="path"/>.</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static string? Digest(string path, out byte[]? content) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        var digest = Answer(content: out content, path: path, read: out _, wantDigest: true);

        Add(input: ((digest is null)
            ? new CompileInput(ContentHash: string.Empty, Kind: CompileInputKind.Absent, Path: path)
            : new CompileInput(ContentHash: digest, Kind: CompileInputKind.Content, Path: path)));

        return digest;
    }
    /// <summary>Lists the files directly in <paramref name="directory"/>, hidden ones included, recording the listing
    /// (<see cref="CompileInputKind.Listing"/>), so a compile that chose among a directory's files rests on which files
    /// were there to choose from and how each name was spelled. The directory is listed again only when its stamp does
    /// not vouch for the names last listed (<see cref="Current"/>).</summary>
    /// <param name="directory">The directory to list. A directory that does not exist lists no file.</param>
    /// <returns>The files' paths, each <paramref name="directory"/> joined with the file's name, in ordinal order.</returns>
    /// <exception cref="IOException">The directory could not be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory may not be listed.</exception>
    public static IReadOnlyList<string> List(string directory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);

        var names = Names(directory: directory, hash: out var hash, read: out _);

        Add(input: new CompileInput(
            ContentHash: hash,
            Kind: CompileInputKind.Listing,
            Path: directory
        ));

        return [.. names.Select(selector: name => System.IO.Path.Join(path1: directory, path2: name))];
    }

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
        string hash;

        try {
            content = ReadRemembering(hash: out hash, path: path);
        } catch (Exception exception) when ((exception is FileNotFoundException or DirectoryNotFoundException)) {
            Add(input: new CompileInput(
                ContentHash: string.Empty,
                Kind: CompileInputKind.Absent,
                Path: path
            ));

            throw;
        }

        Add(input: new CompileInput(
            ContentHash: hash,
            Kind: CompileInputKind.Content,
            Path: path
        ));

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
    public static string ReadAllText(string path, Encoding encoding) => Puck.Confinement.FileContent.Text(content: ReadAllBytes(path: path), encoding: encoding);
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
