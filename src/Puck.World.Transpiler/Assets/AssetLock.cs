using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Transpiler.Modules;

namespace Puck.World.Transpiler.Assets;

/// <summary>A refusal raised while loading, updating, or resolving a pinned world asset.</summary>
public sealed class AssetLockException : Exception {
    /// <summary>Initializes a refusal with a stable, user-facing explanation.</summary>
    /// <param name="message">The refusal explanation.</param>
    public AssetLockException(string message) : base(message: message) { }
}
/// <summary>An asset whose bytes match the digest pinned beside its source.</summary>
/// <param name="Path">The normalized source-relative path.</param>
/// <param name="Hash">The canonical <c>sha256/&lt;hex64&gt;</c> digest.</param>
/// <param name="Content">The exact pinned bytes.</param>
public sealed record ResolvedAsset(string Path, string Hash, ReadOnlyMemory<byte> Content);
/// <summary>An authored path together with the directory of the source or expanded module that declared it.</summary>
/// <param name="Path">The authored portable relative path.</param>
/// <param name="BasePath">The declaring source's absolute or process-relative base directory.</param>
public sealed record AssetReference(string Path, string BasePath);
/// <summary>A deterministic lock for <c>asset "path"</c> references in one world source.</summary>
/// <remarks>Compilation reads this lock but never changes it. Tooling must call <see cref="Update(string, IEnumerable{string})"/> explicitly
/// when an author intends to admit new bytes.</remarks>
public sealed class AssetLock {
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The only admitted lock schema.</summary>
    public const int SupportedFormat = 1;
    /// <summary>The suffix of the lock file beside a source.</summary>
    public const string LockSuffix = ".assets.json";
    /// <summary>The maximum number of distinct assets one source may pin.</summary>
    public const int MaximumAssetCount = 256;
    /// <summary>The maximum UTF-16 length of one normalized logical path.</summary>
    public const int MaximumPathLength = 512;
    /// <summary>The maximum bytes read from one asset.</summary>
    public const long MaximumAssetBytes = ((64L * 1024) * 1024);
    /// <summary>The maximum bytes read across one resolution.</summary>
    public const long MaximumTotalBytes = ((256L * 1024) * 1024);

    private readonly Dictionary<string, string> m_assets;

    /// <summary>Gets the pinned hashes keyed by normalized source-relative path.</summary>
    public IReadOnlyDictionary<string, string> Assets => m_assets;

    /// <summary>Initializes an empty lock.</summary>
    public AssetLock() {
        m_assets = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
    }

    private AssetLock(Dictionary<string, string> assets) {
        m_assets = assets;
    }

    /// <summary>Derives the <c>&lt;stem&gt;.assets.json</c> path beside a source.</summary>
    /// <param name="sourcePath">A <c>.puck</c> source path or an asset-lock path.</param>
    /// <returns>The absolute lock path (<see cref="WorldDocumentName.SidecarFile"/>).</returns>
    public static string DeriveLockPath(string sourcePath) => WorldDocumentName.SidecarFile(
        sourcePath: sourcePath,
        suffix: LockSuffix
    );
    /// <summary>Loads the lock beside a source.</summary>
    /// <param name="sourcePath">The source or lock path.</param>
    /// <returns>The validated lock.</returns>
    /// <exception cref="AssetLockException">The lock is absent, unreadable, or malformed.</exception>
    public static AssetLock Load(string sourcePath) {
        var lockPath = DeriveLockPath(sourcePath: sourcePath);

        try {
            return Parse(json: CompileInputs.ReadAllText(encoding: Encoding.UTF8, path: lockPath));
        } catch (AssetLockException) {
            throw;
        } catch (FileNotFoundException) {
            throw new AssetLockException(message: $"Asset lock '{lockPath}' does not exist; run the explicit asset-lock update command.");
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException)) {
            throw new AssetLockException(message: $"Asset lock '{lockPath}' could not be read: {exception.Message}");
        }
    }
    /// <summary>Parses and strictly validates a lock document.</summary>
    /// <param name="json">The lock JSON.</param>
    /// <returns>The validated lock.</returns>
    /// <exception cref="AssetLockException">The document is malformed or exceeds its bounds.</exception>
    public static AssetLock Parse(string json) {
        ArgumentNullException.ThrowIfNull(argument: json);

        try {
            using var document = JsonDocument.Parse(json: json, options: new JsonDocumentOptions {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) {
                throw new AssetLockException(message: "An asset lock root must be an object.");
            }

            JsonElement? format = null;
            JsonElement? assets = null;
            var rootNames = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var property in root.EnumerateObject()) {
                if (!rootNames.Add(item: property.Name)) {
                    throw new AssetLockException(message: $"Asset lock property '{property.Name}' is duplicated.");
                }

                if (property.NameEquals(utf8Text: "format"u8)) {
                    format = property.Value;
                } else if (property.NameEquals(utf8Text: "assets"u8)) {
                    assets = property.Value;
                } else {
                    throw new AssetLockException(message: $"Asset lock property '{property.Name}' is not recognized.");
                }
            }

            if ((format is null) || (format.Value.ValueKind != JsonValueKind.Number) ||
                !format.Value.TryGetInt32(value: out var formatNumber) || (formatNumber != SupportedFormat)) {
                throw new AssetLockException(message: $"Asset lock format must be {SupportedFormat}.");
            }
            if ((assets is null) || (assets.Value.ValueKind != JsonValueKind.Object)) {
                throw new AssetLockException(message: "Asset lock property 'assets' must be an object.");
            }

            var entries = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

            foreach (var property in assets.Value.EnumerateObject()) {
                if (entries.Count >= MaximumAssetCount) {
                    throw new AssetLockException(message: $"Asset lock exceeds its {MaximumAssetCount}-entry limit.");
                }

                var path = NormalizeLogicalPath(path: property.Name);

                if (!string.Equals(a: path, b: property.Name, comparisonType: StringComparison.Ordinal)) {
                    throw new AssetLockException(message: $"Asset lock path '{property.Name}' is not canonical; write '{path}'.");
                }
                if (!entries.TryAdd(key: path, value: ReadHash(path: path, value: property.Value))) {
                    throw new AssetLockException(message: $"Asset lock path '{path}' is duplicated.");
                }
            }

            return new AssetLock(assets: entries);
        } catch (JsonException exception) {
            throw new AssetLockException(message: $"Asset lock JSON is malformed: {exception.Message}");
        }
    }
    /// <summary>Resolves every distinct authored path and verifies its exact bytes against this lock.</summary>
    /// <param name="sourcePath">The source path that roots relative asset paths.</param>
    /// <param name="paths">The authored asset paths.</param>
    /// <returns>Resolved assets keyed ordinally by normalized logical path.</returns>
    /// <exception cref="AssetLockException">A path or lock entry is invalid, missing, stale, unsafe, or over budget.</exception>
    public IReadOnlyDictionary<string, ResolvedAsset> Resolve(string sourcePath, IEnumerable<string> paths) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);
        ArgumentNullException.ThrowIfNull(argument: paths);

        var root = Path.GetFullPath(path: (Path.GetDirectoryName(path: sourcePath) ?? "."));
        var references = paths.Select(selector: path => new AssetReference(BasePath: root, Path: path));

        return Resolve(references: references, sourcePath: sourcePath);
    }
    /// <summary>Resolves assets relative to their defining modules and keys them relative to the root source.</summary>
    /// <param name="sourcePath">The root source path whose directory defines lock keys.</param>
    /// <param name="references">Authored paths paired with their defining module directories.</param>
    /// <returns>Resolved assets keyed ordinally by root-source-relative path.</returns>
    public IReadOnlyDictionary<string, ResolvedAsset> Resolve(string sourcePath, IEnumerable<AssetReference> references) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);
        ArgumentNullException.ThrowIfNull(argument: references);

        var root = Path.GetFullPath(path: (Path.GetDirectoryName(path: sourcePath) ?? "."));
        var normalized = NormalizeReferences(references: references, root: root);
        var resolved = new SortedDictionary<string, ResolvedAsset>(comparer: StringComparer.Ordinal);
        var totalBytes = 0L;

        foreach (var reference in normalized) {
            if (!m_assets.TryGetValue(key: reference.Key, value: out var expectedHash)) {
                throw new AssetLockException(message: $"Asset '{reference.Key}' has no pinned hash; run the explicit asset-lock update command.");
            }

            var bytes = ReadAsset(fullPath: reference.FullPath, logicalPath: reference.Key);

            totalBytes = checked((totalBytes + bytes.LongLength));

            if (totalBytes > MaximumTotalBytes) {
                throw new AssetLockException(message: $"Assets exceed their {MaximumTotalBytes}-byte total limit.");
            }

            var actualHash = ContentPin.Compute(content: bytes).ToString();

            if (!string.Equals(a: expectedHash, b: actualHash, comparisonType: StringComparison.Ordinal)) {
                throw new AssetLockException(message: $"Asset '{reference.Key}' does not match its lock (expected {expectedHash}, found {actualHash}); update the lock only if the byte change is intended.");
            }

            resolved.Add(key: reference.Key, value: new ResolvedAsset(Path: reference.Key, Hash: actualHash, Content: bytes));
        }

        return resolved;
    }
    /// <summary>Creates a refreshed lock for exactly the supplied discovered paths.</summary>
    /// <param name="sourcePath">The source path that roots relative asset paths.</param>
    /// <param name="paths">The complete discovered asset path set.</param>
    /// <returns>The refreshed lock. Call <see cref="Write"/> to persist it.</returns>
    public static AssetLock Update(string sourcePath, IEnumerable<string> paths) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);
        ArgumentNullException.ThrowIfNull(argument: paths);

        var root = Path.GetFullPath(path: (Path.GetDirectoryName(path: sourcePath) ?? "."));

        return Update(
            sourcePath: sourcePath,
            references: paths.Select(selector: path => new AssetReference(BasePath: root, Path: path))
        );
    }
    /// <summary>Creates a refreshed root lock for assets declared in one or more module directories.</summary>
    /// <param name="sourcePath">The root source path whose directory defines lock keys.</param>
    /// <param name="references">The complete discovered asset reference set.</param>
    /// <returns>The refreshed lock. Call <see cref="Write"/> to persist it.</returns>
    public static AssetLock Update(string sourcePath, IEnumerable<AssetReference> references) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);
        ArgumentNullException.ThrowIfNull(argument: references);

        var root = Path.GetFullPath(path: (Path.GetDirectoryName(path: sourcePath) ?? "."));
        var entries = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var totalBytes = 0L;

        foreach (var reference in NormalizeReferences(references: references, root: root)) {
            var bytes = ReadAsset(fullPath: reference.FullPath, logicalPath: reference.Key);

            totalBytes = checked((totalBytes + bytes.LongLength));

            if (totalBytes > MaximumTotalBytes) {
                throw new AssetLockException(message: $"Assets exceed their {MaximumTotalBytes}-byte total limit.");
            }

            entries.Add(key: reference.Key, value: ContentPin.Compute(content: bytes).ToString());
        }

        return new AssetLock(assets: entries);
    }
    /// <summary>Returns the canonical lock key for one module-relative reference without reading the file.</summary>
    /// <param name="sourcePath">The root source path whose directory defines lock keys.</param>
    /// <param name="reference">The authored path and its defining module directory.</param>
    /// <returns>A portable path relative to the root source directory.</returns>
    public static string NormalizeReferencePath(string sourcePath, AssetReference reference) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);
        ArgumentNullException.ThrowIfNull(argument: reference);

        var root = Path.GetFullPath(path: (Path.GetDirectoryName(path: sourcePath) ?? "."));

        return NormalizeReferences(references: [reference], root: root)[0].Key;
    }
    /// <summary>Serializes this lock with ordinal entries, two-space indentation, and LF endings.</summary>
    public string Serialize() {
        var builder = new StringBuilder();

        builder.Append(value: "{\n  \"format\": 1,\n  \"assets\": {");

        if (m_assets.Count != 0) {
            builder.Append(value: '\n');
            var entries = m_assets.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal).ToArray();

            for (var index = 0; (index < entries.Length); index++) {
                var entry = entries[index];

                builder.Append(value: "    ");
                builder.Append(value: JsonValue.Create(value: entry.Key)!.ToJsonString());
                builder.Append(value: ": ");
                builder.Append(value: JsonValue.Create(value: entry.Value)!.ToJsonString());
                builder.Append(value: (((index + 1) == entries.Length) ? "\n" : ",\n"));
            }

            builder.Append(value: "  ");
        }

        builder.Append(value: "}\n}\n");
        return builder.ToString();
    }
    /// <summary>Writes the derived lock beside <paramref name="sourcePath"/> only when its bytes changed.</summary>
    /// <param name="sourcePath">The source or lock path.</param>
    /// <returns><see langword="true"/> when bytes were written.</returns>
    public bool Write(string sourcePath) {
        var lockPath = DeriveLockPath(sourcePath: sourcePath);
        var bytes = Utf8WithoutBom.GetBytes(s: Serialize());

        if (File.Exists(path: lockPath) && FileEquals(expected: bytes, path: lockPath)) {
            return false;
        }

        AtomicFile.WriteAllBytes(
            bytes: bytes,
            path: lockPath
        );

        return true;
    }

    private static string ReadHash(string path, JsonElement value) {
        if (value.ValueKind != JsonValueKind.String) {
            throw new AssetLockException(message: $"Asset lock hash for '{path}' must be a string.");
        }

        var hash = value.GetString()!;

        if (!ContentPin.TryParse(pin: out _, text: hash)) {
            throw new AssetLockException(message: $"Asset lock hash for '{path}' must be 'sha256/' followed by 64 lowercase hexadecimal characters.");
        }

        return hash;
    }
    private static IReadOnlyList<NormalizedReference> NormalizeReferences(string root, IEnumerable<AssetReference> references) {
        var normalized = new SortedDictionary<string, NormalizedReference>(comparer: StringComparer.Ordinal);
        var physicalPaths = new Dictionary<string, string>(comparer: PuckPaths.Comparer);

        foreach (var reference in references) {
            ArgumentNullException.ThrowIfNull(argument: reference);
            var authoredPath = NormalizeLogicalPath(path: reference.Path);
            var basePath = Path.GetFullPath(path: reference.BasePath);
            var fullPath = Path.GetFullPath(
                path: authoredPath.Replace(newChar: Path.DirectorySeparatorChar, oldChar: '/'),
                basePath: basePath
            );
            var key = Path.GetRelativePath(path: fullPath, relativeTo: root).Replace(newChar: '/', oldChar: Path.DirectorySeparatorChar);

            key = NormalizeLogicalPath(path: key);

            if (normalized.TryGetValue(key: key, value: out var existing) &&
                !string.Equals(a: existing.FullPath, b: fullPath, comparisonType: PuckPaths.Comparison)) {
                throw new AssetLockException(message: $"Asset lock key '{key}' resolves to more than one physical file.");
            }
            if (physicalPaths.TryGetValue(key: fullPath, value: out var existingKey) &&
                !string.Equals(a: existingKey, b: key, comparisonType: StringComparison.Ordinal)) {
                throw new AssetLockException(message: $"Asset paths '{existingKey}' and '{key}' resolve to the same physical file.");
            }

            normalized[key] = new NormalizedReference(FullPath: fullPath, Key: key);
            physicalPaths[fullPath] = key;
            if (normalized.Count > MaximumAssetCount) {
                throw new AssetLockException(message: $"Asset references exceed their {MaximumAssetCount}-entry limit.");
            }
        }

        return normalized.Values.ToArray();
    }
    private static string NormalizeLogicalPath(string path) {
        if (string.IsNullOrWhiteSpace(value: path)) {
            throw new AssetLockException(message: "An asset path must not be empty or whitespace.");
        }
        if (path.Length > MaximumPathLength) {
            throw new AssetLockException(message: $"Asset path exceeds its {MaximumPathLength}-character limit.");
        }
        if (Path.IsPathFullyQualified(path: path) || path.Contains(comparisonType: StringComparison.Ordinal, value: '\\') ||
            path.Contains(value: '\0') || path.Contains(comparisonType: StringComparison.Ordinal, value: ':')) {
            throw new AssetLockException(message: $"Asset path '{path}' must be a portable forward-slash relative path.");
        }

        var segments = path.Split(options: StringSplitOptions.None, separator: '/');

        if (segments.Any(predicate: static segment => ((segment.Length == 0) || (segment == ".")))) {
            throw new AssetLockException(message: $"Asset path '{path}' contains an empty or current-directory segment.");
        }

        return string.Join(separator: '/', value: segments);
    }
    private static byte[] ReadAsset(string fullPath, string logicalPath) {
        try {
            using var stream = new FileStream(
                access: FileAccess.Read,
                bufferSize: (64 * 1024),
                mode: FileMode.Open,
                options: FileOptions.SequentialScan,
                path: fullPath,
                share: FileShare.Read
            );

            if (stream.Length > MaximumAssetBytes) {
                throw new AssetLockException(message: $"Asset '{logicalPath}' exceeds its {MaximumAssetBytes}-byte limit.");
            }

            var bytes = GC.AllocateUninitializedArray<byte>(length: checked((int)stream.Length));

            stream.ReadExactly(buffer: bytes);
            if (stream.ReadByte() != -1) {
                throw new AssetLockException(message: $"Asset '{logicalPath}' changed size while it was being read.");
            }
            CompileInputs.Note(content: bytes, path: fullPath);
            return bytes;
        } catch (AssetLockException) {
            throw;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            throw new AssetLockException(message: $"Asset '{logicalPath}' could not be read: {exception.Message}");
        }
    }
    // Reads at most the expected byte count plus one trailing-byte probe. A malformed existing lock may be
    // arbitrarily large, and an explicit refresh must replace it without first allocating its size.
    private static bool FileEquals(string path, ReadOnlySpan<byte> expected) {
        try {
            using var stream = new FileStream(
                access: FileAccess.Read,
                bufferSize: 4096,
                mode: FileMode.Open,
                options: FileOptions.SequentialScan,
                path: path,
                share: FileShare.Read
            );

            if (stream.Length != expected.Length) {
                return false;
            }

            Span<byte> buffer = stackalloc byte[4096];
            var offset = 0;

            while (offset < expected.Length) {
                var count = Math.Min(val1: buffer.Length, val2: (expected.Length - offset));
                var read = stream.Read(buffer: buffer[..count]);

                if ((read == 0) || !buffer[..read].SequenceEqual(other: expected.Slice(length: read, start: offset))) {
                    return false;
                }

                offset += read;
            }

            return (stream.ReadByte() == -1);
        } catch (FileNotFoundException) {
            return false;
        }
    }

    private sealed record NormalizedReference(string Key, string FullPath);
}
