using System.Collections.Concurrent;
using Puck.Abstractions.Counting;

namespace Puck.Assets;

/// <summary>Answers a content pin with the bytes it names, from the places a process holds content: a file a loaded
/// package registered under the pin, then the process's content store, then a store an earlier fetch filled, and last
/// a fetch the caller supplies. Every answer is hashed and compared with its pin before it is returned, so a caller
/// needs no check of its own, and fetched bytes are checked as their kind before they are stored. A path is only ever
/// one this process registered: nothing a pin's sender wrote is opened. Thread-safe.</summary>
/// <param name="store">Resolves the content store consulted after the registered files, or <see langword="null"/> for
/// none; called at each read, so a host may configure its store after this resolver is made.</param>
public sealed class ContentResolver(Func<ContentAddressedStore?>? store = null) {
    /// <summary>The name a counters report heads a resolver's section with.</summary>
    public const string SourceName = "assets.content";

    private static readonly WorkKindRegistry Registry = new();

    /// <summary>Gets the kind counting reads a registered file answered. Where a pin is found depends on what the
    /// process loaded and on stores that outlive the run, as every kind here does.</summary>
    public static WorkKind FromFiles { get; } = Registry.Declare(name: "assets.content.files", unit: "reads", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting reads a content store answered.</summary>
    public static WorkKind FromStore { get; } = Registry.Declare(name: "assets.content.store", unit: "reads", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting reads a fetch answered: the bytes arrived, hashed to their pin, passed their
    /// kind's check and were stored.</summary>
    public static WorkKind Fetched { get; } = Registry.Declare(name: "assets.content.fetched", unit: "reads", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting fetched objects refused: larger than the caller admits, not the pinned bytes,
    /// or not their kind. Nothing refused is stored.</summary>
    public static WorkKind Refused { get; } = Registry.Declare(name: "assets.content.refused", unit: "objects", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting reads nothing this process holds, and no fetch, could answer.</summary>
    public static WorkKind Missing { get; } = Registry.Declare(name: "assets.content.missing", unit: "reads", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the bytes every answered read returned.</summary>
    public static WorkKind Bytes { get; } = Registry.Declare(name: "assets.content.bytes", unit: "bytes", workClass: WorkClass.Pacing);

    private readonly ConcurrentDictionary<ContentPin, string> m_files = new();
    private const int MaxRemembered = 65_536;
    // How long a pin whose fetch failed waits before it is fetched again, in milliseconds.
    private const long FetchRetryMilliseconds = 5_000;

    // The store each pin was last found in or fetched into, so a later read that names no store still finds it.
    private readonly ConcurrentDictionary<ContentPin, ContentAddressedStore> m_fetched = new();
    // When the last fetch of a pin failed, on the process's monotonic clock.
    private readonly ConcurrentDictionary<ContentPin, long> m_failed = new();
    private readonly FileSystemAssetSource m_reader = new();
    private readonly WorkCounterSet m_work = new(kinds: Registry.Kinds, name: SourceName);

    /// <summary>Gets this resolver's counts, each a monotonic total: where each read was answered from, the fetched
    /// objects refused, and the bytes answered.</summary>
    public IWorkCounterSource Work => m_work;

    /// <summary>Records the file a document this process loaded says holds a pin's bytes. Nothing is opened here: each
    /// read hashes the file against the pin, and forgets a file whose bytes are not the pin's.</summary>
    /// <param name="pin">The content pin.</param>
    /// <param name="path">The file's absolute path.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not fully qualified.</exception>
    public void Register(ContentPin pin, string path) {
        if (!Path.IsPathFullyQualified(path: path)) {
            throw new ArgumentException(message: "A registered content file names an absolute path.", paramName: nameof(path));
        }

        m_files[pin] = path;
    }
    /// <summary>Returns whether a read of a pin has somewhere to look: a registered file, the store the caller reads,
    /// or a store an earlier read found the pin in. It opens no object, so the read that follows may still find the
    /// bytes gone or changed. A pin found in <paramref name="held"/> is remembered there, so a later read that names no
    /// store finds it.</summary>
    /// <param name="pin">The content pin.</param>
    /// <param name="held">The store the caller reads in place of the process's, or <see langword="null"/> for the
    /// process's.</param>
    /// <returns><see langword="true"/> when the pin is registered or a store holds an object under it.</returns>
    public bool Contains(ContentPin pin, ContentAddressedStore? held = null) {
        if (m_files.ContainsKey(key: pin)) {
            return true;
        }
        if ((held ?? store?.Invoke()) is { } reads && reads.Contains(pin: pin)) {
            if (held is not null) { Remember(pin: pin, holder: held); }

            return true;
        }

        return (m_fetched.TryGetValue(key: pin, value: out var fetched) && fetched.Contains(pin: pin));
    }
    /// <summary>Reads the bytes a pin names, fetching them when this process holds none and the caller supplies a
    /// fetch.</summary>
    /// <param name="pin">The content pin.</param>
    /// <param name="maximumBytes">The most bytes the caller admits; larger content reads as missing, and a larger
    /// fetched object is refused.</param>
    /// <param name="content">The bytes, which hash to <paramref name="pin"/>, when this returns
    /// <see langword="true"/>.</param>
    /// <param name="held">The store read in place of the process's and the one fetched bytes are put in, which
    /// bounds what one sender's content may occupy; <see langword="null"/> uses the process's store.</param>
    /// <param name="fetch">Asks the pin's sender for its bytes, answering <see langword="null"/> when it refuses or
    /// cannot be reached; <see langword="null"/> for a read that fetches nothing.</param>
    /// <param name="admit">Checks fetched bytes as the kind of content the caller reads, before they are stored;
    /// <see langword="null"/> admits any bytes that hash to the pin.</param>
    /// <returns><see langword="true"/> when this process holds the content or fetched it.</returns>
    public bool TryRead(ContentPin pin, int maximumBytes, out ReadOnlyMemory<byte> content, ContentAddressedStore? held = null, Func<ContentPin, byte[]?>? fetch = null, Func<ReadOnlyMemory<byte>, bool>? admit = null) {
        if (m_files.TryGetValue(key: pin, value: out var path)) {
            try {
                var file = m_reader.Read(maximumBytes: maximumBytes, path: path);

                if (ContentPin.Compute(content: file.Span) == pin) {
                    return Answer(bytes: file, content: out content, kind: FromFiles);
                }
                // The file's bytes are no longer the pin's, so the file no longer stands for it.
                _ = m_files.TryRemove(key: pin, value: out _);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) {
                // A file that cannot be read now, or is larger than this caller admits, may still hold the pin for the
                // next read: it stays registered and this read looks elsewhere.
            }
        }
        var reads = (held ?? store?.Invoke());

        if (FromStoreOf(candidate: reads, content: out content, maximumBytes: maximumBytes, pin: pin)) {
            if (held is not null) { Remember(pin: pin, holder: held); }

            return true;
        }
        if (
            m_fetched.TryGetValue(key: pin, value: out var fetched) &&
            !ReferenceEquals(objA: fetched, objB: reads) &&
            FromStoreOf(candidate: fetched, content: out content, maximumBytes: maximumBytes, pin: pin)
        ) {
            return true;
        }
        if ((fetch is not null) && (reads is not null) && !FailedLately(pin: pin)) {
            if (fetch(arg: pin) is not { } arrived) {
                m_failed[pin] = Environment.TickCount64;
            } else if ((arrived.Length > maximumBytes) || (ContentPin.Compute(content: arrived) != pin) || (admit?.Invoke(arg: arrived) == false)) {
                m_failed[pin] = Environment.TickCount64;
                m_work.Count(kind: Refused);
            } else {
                _ = reads.Put(content: arrived);
                Remember(pin: pin, holder: reads);

                return Answer(bytes: arrived, content: out content, kind: Fetched);
            }
        }

        content = default;
        m_work.Count(kind: Missing);

        return false;
    }

    // Whether a fetch of the pin failed within the retry interval. A sender that refuses a pin, or a lane that is down,
    // is asked again only after the interval, so a document delivered many times a second does not ask as often.
    private bool FailedLately(ContentPin pin) {
        if (m_failed.Count >= MaxRemembered) { m_failed.Clear(); }

        return (m_failed.TryGetValue(key: pin, value: out var at) && ((Environment.TickCount64 - at) < FetchRetryMilliseconds));
    }
    // Every entry is found again by the next read that names its store, so a full table is cleared, never grown.
    private void Remember(ContentPin pin, ContentAddressedStore holder) {
        if (m_fetched.Count >= MaxRemembered) { m_fetched.Clear(); }
        m_fetched[pin] = holder;
    }
    // A store verifies an object against its pin as it reads it.
    private bool FromStoreOf(ContentAddressedStore? candidate, ContentPin pin, int maximumBytes, out ReadOnlyMemory<byte> content) {
        if ((candidate is not null) && candidate.TryGet(content: out var bytes, pin: pin) && (bytes.Length <= maximumBytes)) {
            return Answer(bytes: bytes, content: out content, kind: FromStore);
        }

        content = default;

        return false;
    }
    private bool Answer(WorkKind kind, ReadOnlyMemory<byte> bytes, out ReadOnlyMemory<byte> content) {
        content = bytes;
        m_work.Count(kind: kind);
        m_work.Add(amount: bytes.Length, kind: Bytes);

        return true;
    }
}
