using System.Collections.Concurrent;

namespace Puck.Assets;

/// <summary>Answers a content pin with the bytes it names, from the places a process holds content: a file a loaded
/// package registered under the pin, then a content store. Every answer is hashed and compared with its pin before it
/// is returned, so a registered file that changed on disk is forgotten and never served, and a caller needs no check
/// of its own. A path is only ever one this process registered: nothing a pin's sender wrote is opened.
/// Thread-safe.</summary>
/// <param name="store">Resolves the content store consulted after the registered files, or <see langword="null"/> for
/// none; called at each read, so a host may configure its store after this resolver is made.</param>
public sealed class ContentResolver(Func<ContentAddressedStore?>? store = null) {
    private readonly ConcurrentDictionary<ContentPin, string> m_files = new();
    private readonly FileSystemAssetSource m_reader = new();

    private long m_fromFiles;
    private long m_fromStore;
    private long m_missing;

    /// <summary>Gets how many reads a registered file answered.</summary>
    public long FromFiles => Interlocked.Read(location: ref m_fromFiles);
    /// <summary>Gets how many reads the content store answered.</summary>
    public long FromStore => Interlocked.Read(location: ref m_fromStore);
    /// <summary>Gets how many reads nothing this process holds could answer.</summary>
    public long Missing => Interlocked.Read(location: ref m_missing);

    /// <summary>Records that a file this process loaded holds a pin's bytes. The caller has already checked the file
    /// against the pin; a later read checks it again.</summary>
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
                    content = held;
                    _ = Interlocked.Increment(location: ref m_fromFiles);

                    return true;
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
            content = bytes;
            _ = Interlocked.Increment(location: ref m_fromStore);

            return true;
        }

        content = default;
        _ = Interlocked.Increment(location: ref m_missing);

        return false;
    }
}
