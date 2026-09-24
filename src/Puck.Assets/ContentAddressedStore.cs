namespace Puck.Assets;

/// <summary>
/// The repo's one persistent content-addressed store: immutable object bytes keyed by their <see cref="ContentPin"/>,
/// plus named refs pointing at objects. Doubles as the deterministic build cache — a derived artifact keyed by its
/// input's pin never needs recomputing while the input is unchanged. Distinct from <see cref="AssetContentHash"/>/
/// <see cref="ContentAddressedLruCache{TValue}"/>: those are in-memory session keys (a 64-bit truncation is plenty
/// for a process-lifetime cache), while a store accreting objects for years needs the FULL 256-bit digest to keep
/// collision risk negligible.
/// </summary>
/// <remarks>
/// Layout under <see cref="Root"/>: <c>objects/sha256/&lt;hex[0..2]&gt;/&lt;hex64&gt;</c> holds immutable object
/// bytes (a two-character fan-out directory keeps any one directory from accumulating too many entries);
/// <c>refs/&lt;category&gt;/&lt;name&gt;</c> holds a one-line text pointer (<c>sha256/&lt;hex64&gt;</c>); <c>tmp/</c>
/// is write staging. <see cref="ObjectPath(string, ContentPin)"/> and <see cref="ObjectRelativePath"/> state the object
/// layout once; a reader that shares the tree without opening a store addresses objects through them. Every write
/// lands in <c>tmp/</c> first and is promoted with <see cref="File.Move(string, string, bool)"/>, so a reader never
/// observes a partially-written object or ref. Staging outside <c>objects/</c> and <c>refs/</c>, rather than beside the
/// destination as <see cref="AtomicFile"/> does, keeps a temporary file out of <see cref="ListRefs"/>. Saving bytes
/// already present is a no-op past the hash computation: the temp file is discarded rather than replacing the
/// existing (byte-identical) object. A promotion that fails discards its temp file too, so <c>tmp/</c> never
/// accumulates an entry a failed write left behind.
/// </remarks>
public sealed class ContentAddressedStore {
    private readonly string m_refsDirectory;
    private readonly string m_tmpDirectory;

    /// <summary>Gets the store's root directory.</summary>
    public string Root { get; }

    /// <summary>Initializes a store rooted at <paramref name="root"/>, creating its <c>objects/</c>, <c>refs/</c>,
    /// and <c>tmp/</c> subdirectories if they do not already exist.</summary>
    /// <param name="root">The store's root directory (created if missing).</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public ContentAddressedStore(string root) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: root);

        Root = Path.GetFullPath(path: root);
        m_refsDirectory = Path.Combine(
            path1: Root,
            path2: "refs"
        );
        m_tmpDirectory = Path.Combine(
            path1: Root,
            path2: "tmp"
        );

        _ = Directory.CreateDirectory(path: Path.Combine(
            path1: Root,
            path2: "objects",
            path3: "sha256"
        ));
        _ = Directory.CreateDirectory(path: m_refsDirectory);
        _ = Directory.CreateDirectory(path: m_tmpDirectory);
    }

    private string ObjectPath(ContentPin pin) =>
        ObjectPath(
            pin: pin,
            root: Root
        );
    private string RefPath(string category, string name) =>
        Path.Combine(
            path1: m_refsDirectory,
            path2: category,
            path3: name
        );

    /// <summary>Returns the path of <paramref name="pin"/>'s object under a store rooted at <paramref name="root"/>:
    /// <c>&lt;root&gt;/objects/sha256/&lt;hex[0..2]&gt;/&lt;hex64&gt;</c>.</summary>
    /// <param name="root">The store's root directory.</param>
    /// <param name="pin">The object's pin.</param>
    /// <returns>The object's path; a file exists there only once the object has been stored.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public static string ObjectPath(string root, ContentPin pin) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: root);

        return Path.Combine(
            path1: root,
            path2: ObjectRelativePath(pin: pin)
        );
    }
    /// <summary>Returns the path of <paramref name="pin"/>'s object relative to a store's root, with forward slashes:
    /// <c>objects/sha256/&lt;hex[0..2]&gt;/&lt;hex64&gt;</c>. A release manifest records this form, and an HTTP mirror
    /// of a store serves an object at it.</summary>
    /// <param name="pin">The object's pin.</param>
    /// <returns>The root-relative object path.</returns>
    public static string ObjectRelativePath(ContentPin pin) {
        var hex = pin.Hex;

        return $"objects/sha256/{hex[..2]}/{hex}";
    }
    /// <summary>Determines whether an object exists for <paramref name="pin"/>.</summary>
    /// <param name="pin">The object's pin.</param>
    /// <returns><see langword="true"/> if the object exists; otherwise <see langword="false"/>.</returns>
    public bool Contains(ContentPin pin) =>
        File.Exists(path: ObjectPath(pin: pin));
    /// <summary>Lists the ref names declared under a category.</summary>
    /// <param name="category">The ref category.</param>
    /// <returns>The ref names, sorted ordinally (empty when the category has no refs).</returns>
    public IReadOnlyList<string> ListRefs(string category) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: category);

        var categoryDirectory = Path.Combine(
            path1: m_refsDirectory,
            path2: category
        );

        if (!Directory.Exists(path: categoryDirectory)) {
            return [];
        }

        var names = new List<string>();

        foreach (var path in Directory.EnumerateFiles(path: categoryDirectory)) {
            names.Add(item: Path.GetFileName(path: path));
        }

        names.Sort(comparer: StringComparer.Ordinal);

        return names;
    }
    /// <summary>Writes <paramref name="content"/> to the store, deduplicating on identical bytes.</summary>
    /// <param name="content">The object bytes to store.</param>
    /// <returns>The object's pin.</returns>
    public ContentPin Put(ReadOnlySpan<byte> content) {
        var pin = ContentPin.Compute(content: content);
        var objectPath = ObjectPath(pin: pin);

        if (!File.Exists(path: objectPath)) {
            var tmpPath = Path.Combine(
                path1: m_tmpDirectory,
                path2: $"{Guid.NewGuid():n}.tmp"
            );

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: objectPath)!);
            File.WriteAllBytes(
                bytes: content,
                path: tmpPath
            );

            try {
                File.Move(
                    destFileName: objectPath,
                    overwrite: false,
                    sourceFileName: tmpPath
                );
            } catch (IOException) when (File.Exists(path: objectPath)) {
                // Lost a race with a concurrent identical Put — the object already landed; discard our temp copy.
                File.Delete(path: tmpPath);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                // The object never landed, so the temp copy left in tmp/ is discarded rather than orphaned there.
                try {
                    File.Delete(path: tmpPath);
                } catch (Exception cleanup) when ((cleanup is IOException or UnauthorizedAccessException)) {
                    // The original failure is the one worth reporting.
                }

                throw;
            }
        }

        return pin;
    }
    /// <summary>Records a derived-cache entry: points <c>derived/&lt;kind&gt;/&lt;inputHash&gt;</c> at
    /// <paramref name="outputHash"/>, atomically.</summary>
    /// <param name="kind">The derived artifact kind (e.g. <c>bake</c>).</param>
    /// <param name="inputHash">The input's pin.</param>
    /// <param name="outputHash">The derived artifact's pin.</param>
    public void SetDerived(string kind, ContentPin inputHash, ContentPin outputHash) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: kind);

        SetRef(
            category: $"derived/{kind}",
            hash: outputHash,
            name: inputHash.Hex
        );
    }
    /// <summary>Points a named ref at an object, atomically.</summary>
    /// <param name="category">The ref category (a single path segment, e.g. <c>worlds</c>, <c>tunes</c>, <c>derived/bake</c>).</param>
    /// <param name="name">The ref name within the category.</param>
    /// <param name="hash">The target object's pin.</param>
    public void SetRef(string category, string name, ContentPin hash) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: category);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);

        var refPath = RefPath(
            category: category,
            name: name
        );
        var tmpPath = Path.Combine(
            path1: m_tmpDirectory,
            path2: $"{Guid.NewGuid():n}.tmp"
        );

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: refPath)!);
        File.WriteAllText(
            contents: hash.ToString(),
            path: tmpPath
        );

        try {
            File.Move(
                destFileName: refPath,
                overwrite: true,
                sourceFileName: tmpPath
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // The ref never landed, so the temp copy left in tmp/ is discarded rather than orphaned there.
            try {
                File.Delete(path: tmpPath);
            } catch (Exception cleanup) when ((cleanup is IOException or UnauthorizedAccessException)) {
                // The original failure is the one worth reporting.
            }

            throw;
        }
    }
    /// <summary>Attempts to read the object bytes for <paramref name="pin"/>.</summary>
    /// <param name="pin">The object's pin.</param>
    /// <param name="content">When this method returns <see langword="true"/>, the object's bytes.</param>
    /// <returns><see langword="true"/> if the object was found; otherwise <see langword="false"/>.</returns>
    public bool TryGet(ContentPin pin, out byte[] content) {
        var objectPath = ObjectPath(pin: pin);

        if (!File.Exists(path: objectPath)) {
            content = [];
            return false;
        }

        content = File.ReadAllBytes(path: objectPath);
        return true;
    }
    /// <summary>Attempts to resolve a derived-cache entry: a ref under <c>derived/&lt;kind&gt;/&lt;inputHash&gt;</c>,
    /// the store's build-cache convenience for artifacts derived from an already-hashed input.</summary>
    /// <param name="kind">The derived artifact kind (e.g. <c>bake</c>).</param>
    /// <param name="inputHash">The input's pin.</param>
    /// <param name="hash">When this method returns <see langword="true"/>, the derived artifact's pin.</param>
    /// <returns><see langword="true"/> if a cached derivation exists; otherwise <see langword="false"/>.</returns>
    public bool TryResolveDerived(string kind, ContentPin inputHash, out ContentPin hash) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: kind);

        return TryResolveRef(
            category: $"derived/{kind}",
            hash: out hash,
            name: inputHash.Hex
        );
    }
    /// <summary>Attempts to resolve a named ref to its target object's pin.</summary>
    /// <param name="category">The ref category.</param>
    /// <param name="name">The ref name within the category.</param>
    /// <param name="hash">When this method returns <see langword="true"/>, the target object's pin.</param>
    /// <returns><see langword="true"/> if the ref exists and holds a well-formed pin; otherwise <see langword="false"/>.</returns>
    public bool TryResolveRef(string category, string name, out ContentPin hash) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: category);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);

        var refPath = RefPath(
            category: category,
            name: name
        );

        if (!File.Exists(path: refPath)) {
            hash = default;
            return false;
        }

        return ContentPin.TryParse(
            pin: out hash,
            text: File.ReadAllText(path: refPath).Trim()
        );
    }
}
