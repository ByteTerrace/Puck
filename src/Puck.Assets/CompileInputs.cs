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
    /// <summary>The compile read the file only to learn what it declares (which document names a source emits), not
    /// what it lowers to. The fact is the digest of the declaration, as the declarer the fact names reads it
    /// (<see cref="CompileInputs.RegisterDeclarer"/>), so it holds while the file declares the same, whatever else in it
    /// changed: an edit to a value in a sibling a compile only resolved a name in leaves that compile standing.</summary>
    Declaration = 4,
    /// <summary>The compile read which names the files directly in the directory at the path claim, as the declarer the
    /// fact names reads them (<see cref="CompileInputs.RegisterDeclarer"/>). The fact is the declarer's name, a <c>/</c>,
    /// and the digest of the claims' canonical text, so it holds while the directory's claiming files and what each
    /// claims are the same, whatever else in them or beside them changed: one fact for the directory where a listing and
    /// a declaration per file would otherwise stand.</summary>
    Claims = 5,
}
/// <summary>One fact a compile read from the file system, and what must still hold for its output to stand.</summary>
/// <param name="Path">The path exactly as the compile spelled it; a relative path is resolved against the current
/// directory when the fact is checked, as it was when the compile probed it.</param>
/// <param name="Kind">What the compile learned.</param>
/// <param name="ContentHash">The <see cref="ContentPin.Hex"/> of the bytes read, for <see cref="CompileInputKind.Content"/>,
/// or of the names listed, for <see cref="CompileInputKind.Listing"/>; for <see cref="CompileInputKind.Declaration"/>, the
/// declarer's name, a <c>/</c>, and the <see cref="ContentPin.Hex"/> of what the file declares; for
/// <see cref="CompileInputKind.Claims"/>, the declarer's name, a <c>/</c>, and the <see cref="ContentPin.Hex"/> of what the
/// directory's files claim; empty otherwise.</param>
public readonly record struct CompileInput(string Path, CompileInputKind Kind, string ContentHash) {
    /// <summary>Gets this fact as a question: its path and kind, and for a declaration or claims the declarer that
    /// answers it, without the answer the compile read. <see cref="CompileInputs.Current"/> answers it as
    /// it answers the fact.</summary>
    public CompileInput Question => (this with {
        ContentHash = ((Kind is (CompileInputKind.Declaration or CompileInputKind.Claims))
            ? CompileInputs.DeclarerPrefix(contentHash: ContentHash)
            : string.Empty),
    });

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
                CompileInputs.CountRecorded();
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
/// held, each under the stamp (existence, length, last write and creation) the file or directory had just before the read. A
/// later question about the same path is answered from that memory while the stamp is unchanged
/// (<see cref="Current"/>, <see cref="Digest"/>, <see cref="List"/>), so proving a held product still stands costs one
/// stat per input rather than a read and a hash. A stamp written within <see cref="RacyStampWindow"/> of the read cannot
/// prove a later write within the same timestamp tick moved it, so such an answer is never trusted and the path is read
/// again until its stamp is older. A path whose bytes are derived rather than read (a source-backed WebAssembly module,
/// <see cref="WasmGuestBuild.MayName"/>) is always read.
/// </para>
/// <para>
/// A declaration (<see cref="CompileInputKind.Declaration"/>) is answered by the declarer registered under the name the
/// fact carries (<see cref="RegisterDeclarer"/>), from the file's digest: a declarer holds what each digest declares, so
/// asking about an unchanged file again reads nothing, and asking about an edited one reads it once. A declarer this
/// process has not registered answers nothing a recorded fact can equal, so the fact does not hold. A claims fact
/// (<see cref="CompileInputKind.Claims"/>) is answered by the same declarer, from the directory.
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
    // Each declarer by name: from a file's digest, and a reader of its bytes for a digest the declarer does not hold,
    // the canonical text of what the file declares; and from a directory, the canonical text of what the files
    // directly in it claim, read through this door without recording.
    private static readonly ConcurrentDictionary<string, Declarer> Declarers = new(comparer: StringComparer.Ordinal);
    private sealed record Declarer(string Name, Func<string, Func<byte[]>, string> Declare, Func<string, string> Claim);
    // A declaration fact's hash by declarer and file digest: what a file declares is a function of its bytes alone, so
    // its canonical text is built and digested once per declarer and digest, under the running code.
    private static readonly CodeDerivedCache<(string Declarer, string Digest), string> DeclaredHashes = new(capacity: MaxRememberedPaths);
    // The pass the calling flow runs in, when one is open (Pass).
    private static readonly AsyncLocal<PassMemo?> Passing = new();
    private static long StampReadCount;
    private static long ContentReadCount;
    private static long RecordedCount;

    /// <summary>Gets the number of file and directory stamps the door has read in this process: one per question it
    /// answered from the file system.</summary>
    public static long StampReads => Interlocked.Read(location: ref StampReadCount);
    /// <summary>Gets the number of facts recorded into logs in this process, each counted once per log that holds it:
    /// what held compiles carry and what proving them again reads.</summary>
    public static long RecordedFacts => Interlocked.Read(location: ref RecordedCount);

    internal static void CountRecorded() => Interlocked.Increment(location: ref RecordedCount);
    /// <summary>Gets the number of file contents the door has read and directories it has listed in this process,
    /// which it does only for a path whose stamp does not vouch for the answer it last read.</summary>
    public static long ContentReads => Interlocked.Read(location: ref ContentReadCount);
    /// <summary>Gets the number of distinct files and directories whose last answer the door remembers: about the
    /// number of files and directories the process has read, up to <see cref="MaxRememberedPaths"/>.</summary>
    public static int RememberedPaths => Remembered.Count;

    // A file's or directory's stamp: whether it exists, its length (a file's; zero for a directory), its last write and its
    // creation, which a replacement by rename moves even when it carries the old last write along.
    private readonly record struct Stamp(bool Exists, long Length, DateTime Written, DateTime Created);
    // The digest last read for a path under the stamp it had then, the names a listing held, and whether that stamp
    // was too recent to vouch for anything.
    private sealed record Known(Stamp Stamp, string Hash, string[]? Names, bool Racy);
    // What one file answered in a pass: whether it exists, and its digest once a question asked for it.
    private readonly record struct FileAnswer(bool Exists, string? Digest);
    // The answers a pass read, by full path: each file's existence and digest, each directory's names and their digest,
    // and each directory's claims.
    private sealed class PassMemo {
        public ConcurrentDictionary<string, FileAnswer> Files { get; } = new(comparer: Puck.Abstractions.PuckPaths.Comparer);
        public ConcurrentDictionary<string, (string[] Names, string Hash)> Listings { get; } = new(comparer: Puck.Abstractions.PuckPaths.Comparer);
        public ConcurrentDictionary<(string Declarer, string Directory), (string Hash, object Value)> Claims { get; } = new();
    }

    private static void Add(CompileInput input) {
        for (var frame = Recording.Value; (frame is not null); frame = frame.Parent) {
            frame.Log.Add(input: input);
        }
    }
    // One stat: a file's length, or zero for a directory, and the entry's last write and creation.
    private static Stamp StampOf(FileSystemInfo entry) {
        Interlocked.Increment(location: ref StampReadCount);

        return (entry.Exists ? new Stamp(Created: entry.CreationTimeUtc, Exists: true, Length: ((entry is FileInfo file) ? file.Length : 0L), Written: entry.LastWriteTimeUtc) : default);
    }
    // A path's full spelling, its memory keys and the stat handles its stamps are read through, built once for a fully
    // qualified path so a poll over an unchanged tree spells no path and creates no handle: the stat itself is the
    // whole cost (Refresh re-reads the one handle). A relative path resolves against the current directory, which can
    // move, so it is spelled afresh on every question.
    private sealed class PathEntry(string fullPath) {
        private readonly Lock m_gate = new();
        private FileSystemInfo? m_file;
        private FileSystemInfo? m_directory;

        public string FullPath { get; } = fullPath;
        public string FileKey { get; } = ("F" + fullPath);
        public string ListingKey { get; } = ("D" + fullPath);

        public Stamp FileStamp() => Refreshed(handle: ref m_file, directory: false);
        public Stamp DirectoryStamp() => Refreshed(handle: ref m_directory, directory: true);
        // The handle's cached attributes are written by Refresh and read by StampOf, so both run under the gate.
        private Stamp Refreshed(ref FileSystemInfo? handle, bool directory) {
            lock (m_gate) {
                var entry = (handle ??= (directory ? new DirectoryInfo(path: FullPath) : new FileInfo(fileName: FullPath)));

                entry.Refresh();
                return StampOf(entry: entry);
            }
        }
    }
    private static readonly ConcurrentDictionary<string, PathEntry> Paths = new(comparer: StringComparer.Ordinal);
    private static PathEntry EntryOf(string path) {
        if (!System.IO.Path.IsPathFullyQualified(path: path)) { return new PathEntry(fullPath: System.IO.Path.GetFullPath(path: path)); }
        if (Paths.TryGetValue(key: path, value: out var entry)) { return entry; }
        if (Paths.Count >= MaxRememberedPaths) { Paths.Clear(); }
        return Paths.GetOrAdd(key: path, valueFactory: static spelled => new PathEntry(fullPath: System.IO.Path.GetFullPath(path: spelled)));
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
    // Reads a file's bytes whole and remembers their digest under the stamp taken just before, unless the path's bytes
    // are derived rather than the file's own.
    private static byte[] ReadRemembering(string path, out string hash) {
        var entry = EntryOf(path: path);
        var fullPath = entry.FullPath;
        var trusted = !WasmGuestBuild.MayName(path: fullPath);
        var stamp = (trusted ? entry.FileStamp() : default);

        Interlocked.Increment(location: ref ContentReadCount);

        var content = Assets.ReadBytes(path: path);

        hash = ContentPin.Compute(content: content).Hex;
        if (trusted && stamp.Exists) {
            Remember(hash: hash, key: entry.FileKey, names: null, stamp: stamp);
            Passing.Value?.Files.TryAdd(key: fullPath, value: new FileAnswer(Digest: hash, Exists: true));
        }

        return content;
    }
    // The file names directly in a directory, in ordinal order, remembered under the directory's stamp; a directory's
    // last write moves when a file directly in it is added, removed or renamed.
    private static string[] Names(string directory, out string hash, out bool read) {
        var entry = EntryOf(path: directory);
        var fullPath = entry.FullPath;
        var pass = Passing.Value;

        read = false;
        if ((pass is not null) && pass.Listings.TryGetValue(key: fullPath, value: out var answered)) {
            hash = answered.Hash;

            return answered.Names;
        }

        var key = entry.ListingKey;
        var stamp = entry.DirectoryStamp();

        if (Vouched(key: key, stamp: stamp) is { Names: { } held } known) {
            hash = known.Hash;
            pass?.Listings.TryAdd(key: fullPath, value: (held, hash));

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
        pass?.Listings.TryAdd(key: fullPath, value: (names, hash));

        return names;
    }
    // A listing's digest covers each file's name as the file system spells it, so a rename that changes only letter
    // case moves it as surely as a file added or removed.
    private static string ListingHash(string[] names) => ContentPin.OfText(text: string.Join(separator: '\n', value: names)).Hex;

    /// <summary>Returns the current answer to <paramref name="query"/>'s filesystem question, without recording it:
    /// whether a file exists, the digest of its bytes or of what it declares, or the names a directory holds. A digest
    /// or a listing is read
    /// again only when the path's stamp has moved since the answer last read, or was too recent then to vouch for it;
    /// existence is answered from the stamp alone.</summary>
    /// <param name="query">The fact whose path and kind name the question, and for a declaration the declarer that
    /// answers it (<see cref="CompileInput.Question"/>); the answer it holds is ignored.</param>
    /// <param name="read">Whether answering read a file's contents or listed a directory.</param>
    /// <returns>The current fact. A missing content file answers <see cref="CompileInputKind.Absent"/>.</returns>
    /// <exception cref="IOException">The file or directory cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file or directory is refused.</exception>
    public static CompileInput Current(CompileInput query, out bool read) {
        if (query.Kind == CompileInputKind.Listing) {
            _ = Names(directory: query.Path, hash: out var hash, read: out read);

            return Answered(query: query, kind: CompileInputKind.Listing, hash: hash);
        }

        if (query.Kind is (CompileInputKind.Declaration or CompileInputKind.Claims)) {
            if (!TryDeclarerOf(contentHash: query.ContentHash, declarer: out var declarer)) {
                read = false;

                // A recorded declaration or claims fact is never empty, so an unregistered declarer's fact never holds.
                return Answered(query: query, kind: query.Kind, hash: string.Empty);
            }
            if (query.Kind == CompileInputKind.Claims) {
                var contentReads = ContentReads;
                var claimed = ClaimsHash(canonical: Unrecorded(read: static state => state.Declarer.Claim(arg: state.Path), state: (Declarer: declarer, query.Path)), declarer: declarer.Name);

                read = (ContentReads != contentReads);

                return Answered(query: query, kind: CompileInputKind.Claims, hash: claimed);
            }

            var fileDigest = Answer(path: query.Path, content: out var bytes, read: out read, wantDigest: true);

            if (fileDigest is null) {
                return Answered(query: query, kind: CompileInputKind.Absent, hash: string.Empty);
            }
            // A digest whose declaration is held needs no reader of the bytes.
            if (DeclaredHashes.TryGetValue(key: (declarer.Name, fileDigest), value: out var held)) {
                return Answered(query: query, kind: CompileInputKind.Declaration, hash: held);
            }

            return Answered(query: query, kind: CompileInputKind.Declaration, hash: DeclareAgain(bytes: bytes, declarer: declarer, digest: fileDigest, path: query.Path, read: ref read));
        }

        var digested = (query.Kind == CompileInputKind.Content);
        var digest = Answer(path: query.Path, content: out _, read: out read, wantDigest: digested);

        return ((digest is null)
            ? Answered(query: query, kind: CompileInputKind.Absent, hash: string.Empty)
            : (digested
                ? Answered(query: query, kind: query.Kind, hash: digest)
                : Answered(query: query, kind: CompileInputKind.Present, hash: string.Empty)));
    }
    // A declaration whose digest no held answer covers, read through the file's bytes. Apart from Current, whose every
    // poll would otherwise allocate this read's closure even when a held answer serves it.
    private static string DeclareAgain(Declarer declarer, string digest, string path, byte[]? bytes, ref bool read) {
        var reader = ContentReader.Take(content: bytes, path: path);

        try {
            var hash = DeclaredHash(declare: declarer.Declare, declarer: declarer.Name, digest: digest, read: reader.Read);

            read |= reader.Consumed;
            return hash;
        } finally {
            reader.Release();
        }
    }
    // The fact a question answers: the question itself when it already holds that answer, so proving an unchanged input
    // creates nothing, else a new fact for the same path.
    private static CompileInput Answered(CompileInput query, CompileInputKind kind, string hash) =>
        ((query.Kind == kind) && string.Equals(a: query.ContentHash, b: hash, comparisonType: StringComparison.Ordinal))
            ? query
            : new CompileInput(ContentHash: hash, Kind: kind, Path: query.Path);
    // The registered declarer a declaration or claims fact's hash names before its slash, found without spelling the name.
    private static bool TryDeclarerOf(string contentHash, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out Declarer? declarer) {
        var slash = contentHash.IndexOf(value: '/');

        declarer = null;
        return ((slash > 0) && Declarers.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key: contentHash.AsSpan(start: 0, length: slash), value: out declarer));
    }
    // A declaration fact's hash: its declarer's name and slash, then the digest of what the file declares, which the
    // declarer reads from the file's digest and, for a digest it does not hold, the file's bytes. Held by declarer and
    // digest unless the bytes read no longer digest to `digest` (the file moved since its digest was answered), since
    // what they declare then belongs to other bytes.
    private static string DeclaredHash(string declarer, Func<string, Func<byte[]>, string> declare, string digest, Func<byte[]> read) {
        if (DeclaredHashes.TryGetValue(key: (declarer, digest), value: out var held)) {
            return held;
        }

        byte[]? bytes = null;
        var hash = string.Concat(str0: declarer, str1: "/", str2: ContentPin.OfText(text: declare(arg1: digest, arg2: () => (bytes = read()))).Hex);

        if ((bytes is null) || (ContentPin.Compute(content: bytes).Hex == digest)) {
            DeclaredHashes.Set(key: (declarer, digest), value: hash);
        }

        return hash;
    }
    // The declarer a declaration fact's hash names, with its trailing slash; empty when it names none.
    internal static string DeclarerPrefix(string contentHash) {
        var slash = contentHash.IndexOf(value: '/');

        return ((slash < 0) ? string.Empty : contentHash[..(slash + 1)]);
    }
    // A file's digest (empty when only existence was asked), or null when no file is there. `content` is the bytes when
    // answering read them.
    private static string? Answer(string path, bool wantDigest, out byte[]? content, out bool read) {
        content = null;
        read = false;

        var entry = EntryOf(path: path);
        var fullPath = entry.FullPath;

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

        var pass = Passing.Value;

        if ((pass is not null) && pass.Files.TryGetValue(key: fullPath, value: out var answered) && (!answered.Exists || !wantDigest || (answered.Digest is not null))) {
            return (!answered.Exists ? null : (wantDigest ? answered.Digest : string.Empty));
        }

        var stamp = entry.FileStamp();

        if (!stamp.Exists) {
            pass?.Files.TryAdd(key: fullPath, value: new FileAnswer(Digest: null, Exists: false));
            return null;
        }
        if (!wantDigest) {
            pass?.Files.TryAdd(key: fullPath, value: new FileAnswer(Digest: null, Exists: true));
            return string.Empty;
        }
        if (Vouched(key: entry.FileKey, stamp: stamp) is { } known) {
            pass?.Files[fullPath] = new FileAnswer(Digest: known.Hash, Exists: true);
            return known.Hash;
        }

        read = true;
        content = ReadRemembering(hash: out var hash, path: path);
        pass?.Files[fullPath] = new FileAnswer(Digest: hash, Exists: true);

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
    /// <summary>Registers the declarer a declaration or claims fact names (<see cref="CompileInputKind.Declaration"/>,
    /// <see cref="CompileInputKind.Claims"/>), so this process can answer whether such a fact still holds. A declarer
    /// registers once, before any compile records or checks one of its facts; the first registration of a name
    /// stands.</summary>
    /// <param name="name">The declarer's name, which the facts it answers carry; it holds no <c>/</c>.</param>
    /// <param name="declare">From a file's digest, and a reader of its bytes for a digest the declarer does not hold,
    /// the canonical text of what the file declares: a function of the bytes alone.</param>
    /// <param name="claim">From a directory, the canonical text of what the files directly in it claim: the same text
    /// <see cref="Claim{T}"/>'s build answers for that directory, read through this door.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or holds a <c>/</c>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="declare"/> or <paramref name="claim"/> is
    /// <see langword="null"/>.</exception>
    public static void RegisterDeclarer(string name, Func<string, Func<byte[]>, string> declare, Func<string, string> claim) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);
        ArgumentNullException.ThrowIfNull(argument: declare);
        ArgumentNullException.ThrowIfNull(argument: claim);
        if (name.Contains(value: '/')) {
            throw new ArgumentException(message: "A declarer's name holds no '/'.", paramName: nameof(name));
        }

        _ = Declarers.TryAdd(key: name, value: new Declarer(Claim: claim, Declare: declare, Name: name));
    }
    /// <summary>Reads what the files directly in <paramref name="directory"/> claim through <paramref name="build"/>,
    /// recording one claims fact (<see cref="CompileInputKind.Claims"/>) under the registered declarer named
    /// <paramref name="declarer"/> in place of everything the build read: the build's own reads are not recorded, so a
    /// compile that resolves a name among the directory's files stands while what they claim is the same. Within a
    /// <see cref="Pass"/> the build runs once per declarer and directory, and each later read records the same fact.</summary>
    /// <typeparam name="T">What the caller learns from the claims.</typeparam>
    /// <param name="directory">The directory, spelled as the fact records it.</param>
    /// <param name="declarer">The registered declarer's name (<see cref="RegisterDeclarer"/>).</param>
    /// <param name="build">From the directory, what the caller learns and the canonical text of the claims, which
    /// must equal what the registered declarer's claim answers for the same directory.</param>
    /// <returns>What <paramref name="build"/> returned, or what it returned earlier in the same pass.</returns>
    /// <exception cref="ArgumentException"><paramref name="declarer"/> is not registered, or
    /// <paramref name="directory"/> is empty.</exception>
    public static T Claim<T>(string directory, string declarer, Func<string, (T Value, string Canonical)> build) where T : class {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        ArgumentNullException.ThrowIfNull(argument: build);
        if (!Declarers.ContainsKey(key: declarer)) {
            throw new ArgumentException(message: $"No declarer named '{declarer}' is registered.", paramName: nameof(declarer));
        }

        var pass = Passing.Value;

        if ((pass is not null) && pass.Claims.TryGetValue(key: (declarer, directory), value: out var held)) {
            Add(input: new CompileInput(ContentHash: held.Hash, Kind: CompileInputKind.Claims, Path: directory));

            return ((T)held.Value);
        }

        var (value, canonical) = Unrecorded(read: () => build(arg: directory));
        var hash = ClaimsHash(canonical: canonical, declarer: declarer);

        _ = pass?.Claims.TryAdd(key: (declarer, directory), value: (hash, value));
        Add(input: new CompileInput(ContentHash: hash, Kind: CompileInputKind.Claims, Path: directory));

        return value;
    }
    // A claims fact's hash, held by the canonical text's own instance: a declarer that answers an unchanged directory
    // hands back the text it built before, so proving the fact again digests and spells nothing.
    private static string ClaimsHash(string declarer, string canonical) {
        if (HeldClaimsHashes.TryGetValue(key: canonical, value: out var held) && string.Equals(a: held.Declarer, b: declarer, comparisonType: StringComparison.Ordinal)) {
            return held.Hash;
        }
        var hash = string.Concat(str0: declarer, str1: "/", str2: ContentPin.OfText(text: canonical).Hex);

        HeldClaimsHashes.AddOrUpdate(key: canonical, value: new HeldClaimsHash(Declarer: declarer, Hash: hash));
        return hash;
    }
    private sealed record HeldClaimsHash(string Declarer, string Hash);
    // Keyed by the canonical text's instance, so an entry lives exactly as long as the declarer holds that text.
    private static readonly Puck.Abstractions.CodeDerivedTable<string, HeldClaimsHash> HeldClaimsHashes = new();
    // Runs a read on the calling flow with no recording, so the facts it reads are not facts of the compile.
    private static T Unrecorded<T>(Func<T> read) => Unrecorded(read: static read => read(), state: read);
    private static T Unrecorded<TState, T>(TState state, Func<TState, T> read) {
        var recording = Recording.Value;

        Recording.Value = null;
        try {
            return read(arg: state);
        } finally {
            Recording.Value = recording;
        }
    }
    /// <summary>Reads what the file at <paramref name="path"/> declares through <paramref name="learn"/>, recording
    /// the declaration (<see cref="CompileInputKind.Declaration"/>) under the registered declarer named
    /// <paramref name="declarer"/> as what the compile read there, or recording the file's absence. A compile that
    /// reads a file only through here stands while the file declares the same. The file is read only when its stamp
    /// does not vouch for the digest last read and <paramref name="learn"/> or the declarer asks for its bytes; the
    /// declaration's digest is computed once per declarer and file digest.</summary>
    /// <typeparam name="T">What the caller learns from the declaration.</typeparam>
    /// <param name="path">The file.</param>
    /// <param name="declarer">The registered declarer's name (<see cref="RegisterDeclarer"/>).</param>
    /// <param name="learn">From the file's digest and a reader of its bytes, what the caller learns; the fact recorded
    /// is what the registered declarer answers for the same bytes. The reader is valid only until
    /// <paramref name="learn"/> returns.</param>
    /// <param name="digest">The digest of the file's bytes, or <see langword="null"/> when no file is there.</param>
    /// <returns>What <paramref name="learn"/> returned, or the default when no file is there.</returns>
    /// <exception cref="ArgumentException"><paramref name="declarer"/> is not registered.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static T? Declare<T>(string path, string declarer, Func<string, Func<byte[]>, T> learn, out string? digest) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);
        ArgumentNullException.ThrowIfNull(argument: learn);
        if (!Declarers.TryGetValue(key: declarer, value: out var declare)) {
            throw new ArgumentException(message: $"No declarer named '{declarer}' is registered.", paramName: nameof(declarer));
        }

        digest = Answer(content: out var content, path: path, read: out _, wantDigest: true);

        if (digest is null) {
            Add(input: new CompileInput(ContentHash: string.Empty, Kind: CompileInputKind.Absent, Path: path));

            return default;
        }

        var reader = ContentReader.Take(content: content, path: path);

        try {
            var value = learn(arg1: digest, arg2: reader.Read);

            // Only a recording keeps the fact: a source-watch poll proves the declaration again with nothing to record.
            if (Recording.Value is not null) {
                Add(input: new CompileInput(ContentHash: DeclaredHash(declare: declare.Declare, declarer: declarer, digest: digest, read: reader.Read), Kind: CompileInputKind.Declaration, Path: path));
            }

            return value;
        } finally {
            reader.Release();
        }
    }
    // One file's bytes, read at most once for a declaration and its learner. Each thread keeps one with its delegate, so a
    // declaration the stamp vouches for allocates nothing; a learner that declares again while it holds the thread's
    // reader takes a fresh one.
    private sealed class ContentReader {
        [ThreadStatic] private static ContentReader? Spare;

        private string m_path = string.Empty;
        private byte[]? m_content;

        private ContentReader() => Read = ReadContent;

        public Func<byte[]> Read { get; }
        // Whether anything asked for the bytes since Take.
        public bool Consumed { get; private set; }

        public static ContentReader Take(string path, byte[]? content) {
            var reader = (Spare ?? new ContentReader());

            Spare = null;
            reader.m_path = path;
            reader.m_content = content;
            reader.Consumed = false;
            return reader;
        }
        public void Release() {
            m_path = string.Empty;
            m_content = null;
            Spare = this;
        }
        private byte[] ReadContent() {
            Consumed = true;
            return (m_content ??= ReadRemembering(hash: out _, path: m_path));
        }
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

        // The names array is the listing's own held instance while its stamp vouches, so the paths joined from it stand
        // with it: an unchanged directory is listed without spelling a path.
        if (HeldJoins.TryGetValue(key: directory, value: out var held) && ReferenceEquals(objA: held.Names, objB: names)) { return held.Paths; }
        var paths = new string[names.Length];

        for (var index = 0; (index < names.Length); index++) { paths[index] = System.IO.Path.Join(path1: directory, path2: names[index]); }
        if (HeldJoins.Count >= MaxRememberedPaths) { HeldJoins.Clear(); }
        var joined = Array.AsReadOnly(array: paths);

        HeldJoins[directory] = (names, joined);
        return joined;
    }
    // Each listed directory's joined paths, by the directory as spelled, with the names instance they were joined from.
    private static readonly ConcurrentDictionary<string, (string[] Names, IReadOnlyList<string> Paths)> HeldJoins = new(comparer: StringComparer.Ordinal);

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

    /// <summary>Opens a pass on the calling flow, and every flow it starts, until the returned scope is disposed: within
    /// it each file's existence and digest and each directory's listing is read from the file system at most once, the
    /// first time a question asks for it, and every later question about the same path is answered as that read
    /// answered. A pass is one snapshot of what a single load or reload reads (a reload proves a held composition's
    /// inputs, then every import's, then compiles, reading the same files again and again); a file written during it
    /// is seen by the next pass. Facts are still recorded in every enclosing <see cref="Record"/> scope as they are
    /// answered. A pass opened inside another joins it.</summary>
    /// <returns>The scope that ends the pass.</returns>
    public static PassScope Pass() {
        if (Passing.Value is not null) {
            return new PassScope(opened: false);
        }

        Passing.Value = new PassMemo();

        return new PassScope(opened: true);
    }

    /// <summary>Ends a <see cref="Pass"/> when disposed.</summary>
    public readonly struct PassScope : IDisposable {
        private readonly bool m_opened;

        internal PassScope(bool opened) {
            m_opened = opened;
        }

        /// <summary>Ends the pass this scope opened, or does nothing for a scope that joined an enclosing pass.</summary>
        public void Dispose() {
            if (m_opened) {
                Passing.Value = null;
            }
        }
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
