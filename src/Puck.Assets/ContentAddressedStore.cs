using System.Security.Cryptography;

namespace Puck.Assets;

/// <summary>
/// The repo's one persistent content-addressed store: immutable object bytes keyed by their full SHA-256 digest,
/// plus named refs pointing at objects. Doubles as the deterministic build cache — a derived artifact keyed by its
/// input's hash never needs recomputing while the input is unchanged. Distinct from <see cref="AssetContentHash"/>/
/// <see cref="ContentAddressedLruCache{TValue}"/>: those are in-memory session keys (a 64-bit truncation is plenty
/// for a process-lifetime cache), while a store accreting objects for years needs the FULL 256-bit digest to keep
/// collision risk negligible.
/// </summary>
/// <remarks>
/// Layout under <see cref="Root"/>: <c>objects/sha256/&lt;hex[0..2]&gt;/&lt;hex64&gt;</c> holds immutable object
/// bytes (a two-character fan-out directory keeps any one directory from accumulating too many entries);
/// <c>refs/&lt;category&gt;/&lt;name&gt;</c> holds a one-line text pointer (<c>sha256/&lt;hex64&gt;</c>); <c>tmp/</c>
/// is write staging. Every write lands in <c>tmp/</c> first and is promoted with <see cref="File.Move(string, string, bool)"/>
/// — the repo's first atomic writes (nothing else here needs the guarantee that a reader never observes a
/// partially-written object or ref). Saving bytes already present is a no-op past the hash computation: the
/// temp file is discarded rather than replacing the existing (byte-identical) object.
/// </remarks>
public sealed class ContentAddressedStore {
    private const string Sha256Prefix = "sha256/";

    private readonly string m_objectsDirectory;
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
        m_objectsDirectory = Path.Combine(path1: Root, path2: "objects", path3: "sha256");
        m_refsDirectory = Path.Combine(path1: Root, path2: "refs");
        m_tmpDirectory = Path.Combine(path1: Root, path2: "tmp");

        _ = Directory.CreateDirectory(path: m_objectsDirectory);
        _ = Directory.CreateDirectory(path: m_refsDirectory);
        _ = Directory.CreateDirectory(path: m_tmpDirectory);
    }

    /// <summary>Computes the lowercase hex64 SHA-256 digest of <paramref name="content"/> (no <c>sha256/</c> prefix).</summary>
    /// <param name="content">The bytes to hash.</param>
    /// <returns>The lowercase hex64 digest.</returns>
    public static string ComputeHash(ReadOnlySpan<byte> content) {
        Span<byte> hashBytes = stackalloc byte[32];

        SHA256.HashData(
            destination: hashBytes,
            source: content
        );
        return Convert.ToHexStringLower(bytes: hashBytes);
    }

    /// <summary>Determines whether an object exists for <paramref name="hash"/>.</summary>
    /// <param name="hash">The object's hash, as <c>sha256/&lt;hex64&gt;</c> or bare <c>&lt;hex64&gt;</c>.</param>
    /// <returns><see langword="true"/> if the object exists; otherwise <see langword="false"/>.</returns>
    public bool Contains(string hash) =>
        File.Exists(path: ObjectPath(hash: hash));

    /// <summary>Writes <paramref name="content"/> to the store, deduplicating on identical bytes.</summary>
    /// <param name="content">The object bytes to store.</param>
    /// <returns>The canonical <c>sha256/&lt;hex64&gt;</c> hash string.</returns>
    public string Put(ReadOnlySpan<byte> content) {
        var hex = ComputeHash(content: content);
        var objectPath = ObjectPath(hash: hex);

        if (!File.Exists(path: objectPath)) {
            var tmpPath = Path.Combine(path1: m_tmpDirectory, path2: $"{Guid.NewGuid():n}.tmp");

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: objectPath)!);
            File.WriteAllBytes(path: tmpPath, bytes: content.ToArray());

            try {
                File.Move(sourceFileName: tmpPath, destFileName: objectPath, overwrite: false);
            } catch (IOException) when (File.Exists(path: objectPath)) {
                // Lost a race with a concurrent identical Put — the object already landed; discard our temp copy.
                File.Delete(path: tmpPath);
            }
        }

        return $"{Sha256Prefix}{hex}";
    }

    /// <summary>Attempts to read the object bytes for <paramref name="hash"/>.</summary>
    /// <param name="hash">The object's hash, as <c>sha256/&lt;hex64&gt;</c> or bare <c>&lt;hex64&gt;</c>.</param>
    /// <param name="content">When this method returns <see langword="true"/>, the object's bytes.</param>
    /// <returns><see langword="true"/> if the object was found; otherwise <see langword="false"/>.</returns>
    public bool TryGet(string hash, out byte[] content) {
        var objectPath = ObjectPath(hash: hash);

        if (!File.Exists(path: objectPath)) {
            content = [];
            return false;
        }

        content = File.ReadAllBytes(path: objectPath);
        return true;
    }

    /// <summary>Points a named ref at an object hash, atomically.</summary>
    /// <param name="category">The ref category (a single path segment, e.g. <c>worlds</c>, <c>tunes</c>, <c>derived/bake</c>).</param>
    /// <param name="name">The ref name within the category.</param>
    /// <param name="hash">The target object's hash, as <c>sha256/&lt;hex64&gt;</c> or bare <c>&lt;hex64&gt;</c>.</param>
    public void SetRef(string category, string name, string hash) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: category);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);

        var hex = NormalizeToHex(hash: hash);
        var refPath = RefPath(category: category, name: name);
        var tmpPath = Path.Combine(path1: m_tmpDirectory, path2: $"{Guid.NewGuid():n}.tmp");

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: refPath)!);
        File.WriteAllText(path: tmpPath, contents: $"{Sha256Prefix}{hex}");
        File.Move(sourceFileName: tmpPath, destFileName: refPath, overwrite: true);
    }

    /// <summary>Attempts to resolve a named ref to its target object hash.</summary>
    /// <param name="category">The ref category.</param>
    /// <param name="name">The ref name within the category.</param>
    /// <param name="hash">When this method returns <see langword="true"/>, the canonical <c>sha256/&lt;hex64&gt;</c> hash.</param>
    /// <returns><see langword="true"/> if the ref exists and is well-formed; otherwise <see langword="false"/>.</returns>
    public bool TryResolveRef(string category, string name, out string hash) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: category);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);

        var refPath = RefPath(category: category, name: name);

        if (!File.Exists(path: refPath)) {
            hash = "";
            return false;
        }

        var text = File.ReadAllText(path: refPath).Trim();

        if (!text.StartsWith(value: Sha256Prefix, comparisonType: StringComparison.Ordinal) || (text.Length != (Sha256Prefix.Length + 64))) {
            hash = "";
            return false;
        }

        hash = text;
        return true;
    }

    /// <summary>Lists the ref names declared under a category.</summary>
    /// <param name="category">The ref category.</param>
    /// <returns>The ref names, sorted ordinally (empty when the category has no refs).</returns>
    public IReadOnlyList<string> ListRefs(string category) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: category);

        var categoryDirectory = Path.Combine(path1: m_refsDirectory, path2: category);

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

    /// <summary>Attempts to resolve a derived-cache entry: a ref under <c>derived/&lt;kind&gt;/&lt;inputHash&gt;</c>,
    /// the store's build-cache convenience for artifacts derived from an already-hashed input.</summary>
    /// <param name="kind">The derived artifact kind (e.g. <c>bake</c>).</param>
    /// <param name="inputHash">The input's hash, as <c>sha256/&lt;hex64&gt;</c> or bare <c>&lt;hex64&gt;</c>.</param>
    /// <param name="hash">When this method returns <see langword="true"/>, the derived artifact's hash.</param>
    /// <returns><see langword="true"/> if a cached derivation exists; otherwise <see langword="false"/>.</returns>
    public bool TryResolveDerived(string kind, string inputHash, out string hash) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: kind);

        return TryResolveRef(category: $"derived/{kind}", name: NormalizeToHex(hash: inputHash), hash: out hash);
    }

    /// <summary>Records a derived-cache entry: points <c>derived/&lt;kind&gt;/&lt;inputHash&gt;</c> at
    /// <paramref name="outputHash"/>, atomically.</summary>
    /// <param name="kind">The derived artifact kind (e.g. <c>bake</c>).</param>
    /// <param name="inputHash">The input's hash, as <c>sha256/&lt;hex64&gt;</c> or bare <c>&lt;hex64&gt;</c>.</param>
    /// <param name="outputHash">The derived artifact's hash, as <c>sha256/&lt;hex64&gt;</c> or bare <c>&lt;hex64&gt;</c>.</param>
    public void SetDerived(string kind, string inputHash, string outputHash) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: kind);

        SetRef(category: $"derived/{kind}", name: NormalizeToHex(hash: inputHash), hash: outputHash);
    }

    private string ObjectPath(string hash) {
        var hex = NormalizeToHex(hash: hash);

        return Path.Combine(path1: m_objectsDirectory, path2: hex[..2], path3: hex);
    }

    private string RefPath(string category, string name) =>
        Path.Combine(path1: m_refsDirectory, path2: category, path3: name);

    // Accepts either "sha256/<hex64>" or a bare "<hex64>" and returns the lowercase hex64 form.
    private static string NormalizeToHex(string hash) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: hash);

        var hex = (hash.StartsWith(value: Sha256Prefix, comparisonType: StringComparison.Ordinal) ? hash[Sha256Prefix.Length..] : hash);

        if (hex.Length != 64) {
            throw new ArgumentException(message: $"'{hash}' is not a well-formed sha256 hash (expected 64 hex characters).", paramName: nameof(hash));
        }

        return hex.ToLowerInvariant();
    }
}
