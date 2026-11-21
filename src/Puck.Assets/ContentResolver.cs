using System.Collections.Concurrent;
using Puck.Abstractions.Counting;

namespace Puck.Assets;

/// <summary>Answers a content pin with the bytes it names, from the places a process holds content: a file a loaded
/// package registered under the pin, then a content store. Every answer is hashed and compared with its pin before it
/// is returned, so a registered file that changed on disk is forgotten and never served, and a caller needs no check
/// of its own. A path is only ever one this process registered: nothing a pin's sender wrote is opened.
/// Thread-safe.</summary>
/// <param name="store">Resolves the content store consulted after the registered files, or <see langword="null"/> for
/// none; called at each read, so a host may configure its store after this resolver is made.</param>
public sealed class ContentResolver(Func<ContentAddressedStore?>? store = null) {
    /// <summary>The name a counters report heads a resolver's section with.</summary>
    public const string SourceName = "assets.content";

    private static readonly WorkKindRegistry Registry = new();

    /// <summary>Gets the kind counting reads a registered file answered. Where a pin is found depends on what the
    /// process loaded and on a store that outlives the run, as every kind here does.</summary>
    public static WorkKind FromFiles { get; } = Registry.Declare(name: "assets.content.files", unit: "reads", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting reads the content store answered.</summary>
    public static WorkKind FromStore { get; } = Registry.Declare(name: "assets.content.store", unit: "reads", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting reads nothing this process holds could answer.</summary>
    public static WorkKind Missing { get; } = Registry.Declare(name: "assets.content.missing", unit: "reads", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the bytes every answered read returned.</summary>
    public static WorkKind Bytes { get; } = Registry.Declare(name: "assets.content.bytes", unit: "bytes", workClass: WorkClass.Pacing);

    private readonly ConcurrentDictionary<ContentPin, string> m_files = new();
    private readonly FileSystemAssetSource m_reader = new();
    private readonly WorkCounterSet m_work = new(kinds: Registry.Kinds, name: SourceName);

    /// <summary>Gets this resolver's counts, each a monotonic total: where each read was answered from, and the bytes
    /// answered.</summary>
    public IWorkCounterSource Work => m_work;

    /// <summary>Records the file a document this process loaded says holds a pin's bytes. Nothing is opened here: each
    /// read hashes the file against the pin, and forgets a file that does not answer it.</summary>
    /// <param name="pin">The content pin.</param>
    /// <param name="path">The file's absolute path.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not fully qualified.</exception>
    public void Register(ContentPin pin, string path) {
        if (!Path.IsPathFullyQualified(path: path)) {
            throw new ArgumentException(message: "A registered content file names an absolute path.", paramName: nameof(path));
        }

        m_files[pin] = path;
    }
    /// <summary>Reads the bytes a pin names.</summary>
    /// <param name="pin">The content pin.</param>
    /// <param name="maximumBytes">The most bytes the caller admits; larger content reads as missing.</param>
    /// <param name="content">The bytes, which hash to <paramref name="pin"/>, when this returns
    /// <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when this process holds the content.</returns>
    public bool TryRead(ContentPin pin, int maximumBytes, out ReadOnlyMemory<byte> content) {
        if (m_files.TryGetValue(key: pin, value: out var path)) {
            try {
                var held = m_reader.Read(maximumBytes: maximumBytes, path: path);

                if (ContentPin.Compute(content: held.Span) == pin) {
                    return Answer(bytes: held, content: out content, kind: FromFiles);
                }
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) {
                // The file moved, cannot be read or outgrew the bound: it no longer holds the pin for this caller.
            }
            _ = m_files.TryRemove(key: pin, value: out _);
        }
        if (
            (store?.Invoke() is { } stored) &&
            stored.TryGet(content: out var bytes, pin: pin) &&
            (bytes.Length <= maximumBytes) &&
            (ContentPin.Compute(content: bytes) == pin)
        ) {
            return Answer(bytes: bytes, content: out content, kind: FromStore);
        }

        content = default;
        m_work.Count(kind: Missing);

        return false;
    }

    private bool Answer(WorkKind kind, ReadOnlyMemory<byte> bytes, out ReadOnlyMemory<byte> content) {
        content = bytes;
        m_work.Count(kind: kind);
        m_work.Add(amount: bytes.Length, kind: Bytes);

        return true;
    }
}
