using Puck.Confinement;
using Puck.Abstractions;

namespace Puck.Assets;

/// <summary>
/// The repo's one persistent content-addressed store: immutable object bytes keyed by their <see cref="ContentPin"/>,
/// plus named refs pointing at objects. Doubles as the deterministic build cache — a derived artifact keyed by its
/// input's pin never needs recomputing while the input is unchanged. Unlike the in-memory
/// <see cref="ContentAddressedLruCache{TKey, TValue}"/>, this store persists bytes and always uses the full
/// 256-bit digest. The cache lets its caller choose an identity; security-sensitive callers also use full pins.
/// </summary>
/// <remarks>
/// Layout under <see cref="Root"/>: <c>objects/sha256/&lt;hex[0..2]&gt;/&lt;hex64&gt;</c> holds immutable object
/// bytes (a two-character fan-out directory keeps any one directory from accumulating too many entries);
/// <c>refs/&lt;category&gt;/&lt;name&gt;</c> holds a one-line text pointer (<c>sha256/&lt;hex64&gt;</c>).
/// <see cref="ObjectPath(string, ContentPin)"/> and <see cref="ObjectRelativePath"/> state the object layout once; a
/// reader that shares the tree without opening a store addresses objects through them. Every write is a complete-file
/// replacement through <see cref="AtomicFile"/>: the bytes are staged in a flushed temporary beside the destination and
/// renamed over it, so a reader never observes a partially-written object or ref, and a write that fails at any step
/// deletes its temporary. A temporary's name ends in <see cref="AtomicFile.TemporarySuffix"/>, which no ref may, so
/// <see cref="ListRefs"/> and retention skip a write in progress. Saving bytes already present preserves a verified
/// object; a corrupt object is replaced from the supplied bytes.
/// <para>A store opened with a <see cref="Retention"/> is a cache: a read that finds an object or a ref stamps it as
/// used, and a write keeps the objects within the bound and the refs within its entry count, least recently used out
/// (<see cref="CacheRetention"/>). A ref whose object was evicted resolves to a pin <see cref="TryGet"/> no longer finds,
/// which a cache's owner treats as a miss. A store opened without one keeps everything, as a release store must.</para>
/// </remarks>
public sealed class ContentAddressedStore {
    private readonly string m_refsDirectory;

    /// <summary>Gets the store's root directory.</summary>
    public string Root { get; }
    /// <summary>Gets the bound the store is held to as a cache, or <see langword="null"/> for a store that keeps
    /// everything.</summary>
    public CacheBound? Retention { get; }

    /// <summary>Initializes a store rooted at <paramref name="root"/>, creating its <c>objects/</c> and <c>refs/</c>
    /// subdirectories if they do not already exist.</summary>
    /// <param name="root">The store's root directory (created if missing).</param>
    /// <param name="retention">The bound that makes the store a cache, or <see langword="null"/> to keep
    /// everything.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public ContentAddressedStore(string root, CacheBound? retention = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: root);

        Retention = retention;
        Root = Path.GetFullPath(path: root);
        m_refsDirectory = Path.Combine(
            path1: Root,
            path2: "refs"
        );
        _ = Directory.CreateDirectory(path: Path.Combine(
            path1: Root,
            path2: "objects",
            path3: "sha256"
        ));
        _ = Directory.CreateDirectory(path: m_refsDirectory);
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
    // A cache stamps what a read or a write used, and a write holds the objects and the refs within the bound.
    private void Used(string path) {
        if (Retention is not null) {
            CacheRetention.Stamp(path: path);
        }
    }
    private void Retain(string written, bool isRef) {
        if (Retention is not { } bound) {
            return;
        }

        _ = CacheRetention.EnforceOnWrite(
            bound: (isRef ? CacheBound.Entries(maxEntries: bound.MaxEntries) : bound),
            directory: (isRef ? m_refsDirectory : Path.Combine(path1: Root, path2: "objects")),
            inUse: written,
            isEntry: static _ => true,
            search: SearchOption.AllDirectories
        );
    }
    // Whether the object at a pin's path exists and hashes to that pin; a corrupt object does not count.
    private static bool Holds(string objectPath, ContentPin pin) =>
        (File.Exists(path: objectPath) && (ContentPin.Compute(content: File.ReadAllBytes(path: objectPath)) == pin));

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
            var name = Path.GetFileName(path: path);

            if (!AtomicFile.IsTemporary(path: name)) { names.Add(item: name); }
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

        if (!Holds(objectPath: objectPath, pin: pin)) {
            // A missing or corrupt object is replaced, so the publication overwrites; the staged write deletes its
            // temporary on every path that does not publish it.
            using var pending = AtomicFile.StageAllBytes(bytes: content, path: objectPath);

            try {
                pending.Commit();
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && Holds(objectPath: objectPath, pin: pin)) {
                // An identical concurrent writer already published the object.
            }

            Retain(isRef: false, written: objectPath);
        } else {
            Used(path: objectPath);
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
    /// <exception cref="ArgumentException"><paramref name="category"/> or <paramref name="name"/> is <see langword="null"/>,
    /// empty or whitespace, or <paramref name="name"/> ends in <see cref="AtomicFile.TemporarySuffix"/>.</exception>
    public void SetRef(string category, string name, ContentPin hash) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: category);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        if (AtomicFile.IsTemporary(path: name)) {
            throw new ArgumentException(message: $"A ref name may not end in '{AtomicFile.TemporarySuffix}', which names a write in progress.", paramName: nameof(name));
        }

        var refPath = RefPath(
            category: category,
            name: name
        );

        AtomicFile.WriteAllText(contents: hash.ToString(), path: refPath);
        Retain(isRef: true, written: refPath);
    }
    /// <summary>Attempts to read and verify the object bytes for <paramref name="pin"/>.</summary>
    /// <param name="pin">The object's pin.</param>
    /// <param name="content">When this method returns <see langword="true"/>, the object's bytes.</param>
    /// <returns><see langword="true"/> when the object's bytes match the pin; <see langword="false"/> for a missing
    /// or corrupt object.</returns>
    public bool TryGet(ContentPin pin, out byte[] content) {
        var objectPath = ObjectPath(pin: pin);

        if (!File.Exists(path: objectPath)) {
            content = [];
            return false;
        }

        content = File.ReadAllBytes(path: objectPath);
        if (ContentPin.Compute(content: content) != pin) {
            content = [];
            return false;
        }
        Used(path: objectPath);
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

        if (!ContentPin.TryParse(
            pin: out hash,
            text: File.ReadAllText(path: refPath).Trim()
        )) {
            return false;
        }

        Used(path: refPath);
        return true;
    }
}
