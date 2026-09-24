using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Modules;

namespace Puck.World.Transpiler.Embeddings;

/// <summary>Represents a single entry in an embedding lock space.</summary>
/// <param name="Text">The authored source text.</param>
/// <param name="Vector">The unpadded base64url encoded vector components.</param>
public sealed record EmbeddingLockEntry(string Text, string Vector);
/// <summary>Represents an embedding space inside an embedding lock file.</summary>
public sealed class EmbeddingLockSpace {
    /// <summary>Gets or sets the identity the space's vectors were embedded under.</summary>
    public EmbeddingIdentity Identity { get; set; }
    /// <summary>Gets the entries in this space keyed by the <see cref="Puck.Assets.ContentPin.Hex"/> of
    /// <see cref="EmbeddingText.Hash"/>.</summary>
    public Dictionary<string, EmbeddingLockEntry> Entries { get; }

    /// <summary>Initializes a new instance of <see cref="EmbeddingLockSpace"/>.</summary>
    public EmbeddingLockSpace(EmbeddingIdentity identity, Dictionary<string, EmbeddingLockEntry>? entries = null) {
        Identity = identity;
        Entries = (entries ?? new Dictionary<string, EmbeddingLockEntry>(comparer: StringComparer.Ordinal));
    }
}
/// <summary>Hermetic embedding lock file (&lt;stem&gt;.embeddings.json) mapping authored text to vectors.</summary>
public sealed class EmbeddingLock {
    /// <summary>The lock file format version.</summary>
    public const int SupportedFormat = 1;
    /// <summary>The suffix of the lock file beside a source.</summary>
    public const string LockSuffix = ".embeddings.json";

    /// <summary>Gets the format version.</summary>
    public int Format { get; } = SupportedFormat;
    /// <summary>Gets the spaces declared in this lock.</summary>
    public Dictionary<string, EmbeddingLockSpace> Spaces { get; } = new(comparer: StringComparer.Ordinal);

    /// <summary>Derives the embedding lock file path for a source document path.</summary>
    /// <param name="sourcePath">The source .puck file path.</param>
    /// <returns>The .embeddings.json path beside the source file (<see cref="WorldDocumentName.SidecarFile"/>).</returns>
    public static string DeriveLockPath(string sourcePath) => WorldDocumentName.SidecarFile(
        sourcePath: sourcePath,
        suffix: LockSuffix
    );
    /// <summary>Tries to load an embedding lock file for the given source root path.</summary>
    /// <param name="rootSourcePath">The source .puck file path or .embeddings.json path.</param>
    /// <returns>The parsed <see cref="EmbeddingLock"/>, or null if the file does not exist.</returns>
    public static EmbeddingLock? TryLoad(string? rootSourcePath) {
        if (string.IsNullOrWhiteSpace(value: rootSourcePath)) {
            return null;
        }

        var lockPath = DeriveLockPath(sourcePath: rootSourcePath);

        if (!CompileInputs.Exists(path: lockPath)) {
            return null;
        }

        try {
            var json = CompileInputs.ReadAllText(path: lockPath);

            return Parse(json: json);
        } catch {
            return null;
        }
    }
    /// <summary>Parses a JSON string into an <see cref="EmbeddingLock"/>.</summary>
    /// <param name="json">The JSON text to parse.</param>
    /// <returns>The parsed lock instance.</returns>
    public static EmbeddingLock Parse(string json) {
        ArgumentNullException.ThrowIfNull(argument: json);

        using var doc = JsonDocument.Parse(json: json);
        var root = doc.RootElement;
        var format = (root.TryGetProperty(propertyName: "format", value: out var fmtProp) ? fmtProp.GetInt32() : SupportedFormat);
        var lockFile = new EmbeddingLock();

        if (root.TryGetProperty(propertyName: "spaces", value: out var spacesProp) && (spacesProp.ValueKind == JsonValueKind.Object)) {
            foreach (var spaceProp in spacesProp.EnumerateObject()) {
                var spaceName = spaceProp.Name;
                var spaceObj = spaceProp.Value;
                var model = (spaceObj.TryGetProperty(propertyName: "model", value: out var m) ? (m.GetString() ?? "") : "");
                var revision = (spaceObj.TryGetProperty(propertyName: "revision", value: out var r) ? (r.GetString() ?? "") : "");
                var dimensions = (spaceObj.TryGetProperty(propertyName: "dimensions", value: out var d) ? d.GetInt32() : 0);

                var space = new EmbeddingLockSpace(identity: new EmbeddingIdentity(
                    Dimensions: dimensions,
                    Model: model,
                    Revision: revision
                ));

                if (spaceObj.TryGetProperty(propertyName: "entries", value: out var entriesProp) && (entriesProp.ValueKind == JsonValueKind.Object)) {
                    foreach (var entryProp in entriesProp.EnumerateObject()) {
                        var hashKey = entryProp.Name;
                        var entryObj = entryProp.Value;
                        var text = (entryObj.TryGetProperty(propertyName: "text", value: out var t) ? (t.GetString() ?? "") : "");
                        var vector = (entryObj.TryGetProperty(propertyName: "vector", value: out var v) ? (v.GetString() ?? "") : "");

                        space.Entries[hashKey] = new EmbeddingLockEntry(Text: text, Vector: vector);
                    }
                }

                lockFile.Spaces[spaceName] = space;
            }
        }

        return lockFile;
    }
    /// <summary>Looks up a vector by space name and source text.</summary>
    /// <param name="spaceName">The space name.</param>
    /// <param name="text">The source text.</param>
    /// <param name="vectorBase64Url">The base64url vector string if found.</param>
    /// <returns><see langword="true"/> if found; otherwise <see langword="false"/>.</returns>
    public bool TryGet(string spaceName, string text, [NotNullWhen(returnValue: true)] out string? vectorBase64Url) {
        ArgumentNullException.ThrowIfNull(argument: spaceName);
        ArgumentNullException.ThrowIfNull(argument: text);

        vectorBase64Url = null;

        if (!Spaces.TryGetValue(key: spaceName, value: out var space)) {
            return false;
        }

        var hash = EmbeddingText.Hash(text: text).Hex;

        if (space.Entries.TryGetValue(key: hash, value: out var entry)) {
            vectorBase64Url = entry.Vector;

            return true;
        }

        return false;
    }
    /// <summary>Finds the original text matching a vector base64url string across a given space (or the sole space if omitted).</summary>
    /// <param name="spaceName">The name of the embedding space, or null to match across the single space if only one exists.</param>
    /// <param name="vectorBase64Url">The base64url encoded vector bytes.</param>
    /// <param name="text">The single matching text, or null if zero or multiple entries match.</param>
    /// <returns><see langword="true"/> if exactly one entry holds the bytes; otherwise <see langword="false"/>.</returns>
    public bool TryFindText(string? spaceName, string vectorBase64Url, [NotNullWhen(returnValue: true)] out string? text) {
        ArgumentNullException.ThrowIfNull(argument: vectorBase64Url);

        text = null;

        EmbeddingLockSpace? space;

        if (spaceName is null) {
            if (Spaces.Count == 1) {
                space = Spaces.Values.First();
            } else {
                return false;
            }
        } else if (!Spaces.TryGetValue(key: spaceName, value: out space)) {
            return false;
        }

        string? match = null;
        var count = 0;

        foreach (var entry in space.Entries.Values) {
            if (string.Equals(a: entry.Vector, b: vectorBase64Url, comparisonType: StringComparison.Ordinal)) {
                match = entry.Text;
                count++;

                if (count > 1) {
                    return false;
                }
            }
        }

        if ((count == 1) && (match is not null)) {
            text = match;

            return true;
        }

        return false;
    }
    /// <summary>Checks whether a space is missing from the lock file or was embedded under a different identity.</summary>
    public bool IsSpaceStale(string spaceName, EmbeddingIdentity identity) => (
        !Spaces.TryGetValue(key: spaceName, value: out var space) ||
        (space.Identity != identity)
    );
    /// <summary>Adds or updates an entry in the specified space, re-stamping the space with <paramref name="identity"/>.</summary>
    public void SetEntry(string spaceName, EmbeddingIdentity identity, string text, string vectorBase64Url) {
        ArgumentNullException.ThrowIfNull(argument: spaceName);
        ArgumentNullException.ThrowIfNull(argument: identity.Model);
        ArgumentNullException.ThrowIfNull(argument: identity.Revision);
        ArgumentNullException.ThrowIfNull(argument: text);
        ArgumentNullException.ThrowIfNull(argument: vectorBase64Url);

        if (!Spaces.TryGetValue(key: spaceName, value: out var space)) {
            space = new EmbeddingLockSpace(identity: identity);
            Spaces[spaceName] = space;
        } else {
            space.Identity = identity;
        }

        space.Entries[EmbeddingText.Hash(text: text).Hex] = new EmbeddingLockEntry(Text: text, Vector: vectorBase64Url);
    }
    /// <summary>Prunes entries in a space that are not in the used texts set.</summary>
    public void PruneEntries(string spaceName, ISet<string> usedTexts) {
        if (!Spaces.TryGetValue(key: spaceName, value: out var space)) {
            return;
        }

        var toRemove = new List<string>();

        foreach (var (hash, entry) in space.Entries) {
            if (!usedTexts.Contains(item: entry.Text)) {
                toRemove.Add(item: hash);
            }
        }

        foreach (var hash in toRemove) {
            space.Entries.Remove(key: hash);
        }
    }
    /// <summary>Prunes spaces that are not in the used space names set.</summary>
    public void PruneSpaces(ISet<string> usedSpaceNames) {
        var toRemove = new List<string>();

        foreach (var name in Spaces.Keys) {
            if (!usedSpaceNames.Contains(item: name)) {
                toRemove.Add(item: name);
            }
        }

        foreach (var name in toRemove) {
            Spaces.Remove(key: name);
        }
    }
    /// <summary>Serializes the lock file deterministically with LF line endings, 2-space indentation, and ordinal order.</summary>
    public string Serialize() {
        var sb = new StringBuilder();

        sb.Append(value: "{\n  \"format\": 1,\n  \"spaces\": {");

        var orderedSpaces = Spaces.OrderBy(keySelector: s => s.Key, comparer: StringComparer.Ordinal).ToList();

        if (orderedSpaces.Count == 0) {
            sb.Append(value: "}\n}\n");

            return sb.ToString();
        }

        sb.Append(value: "\n");

        for (var s = 0; (s < orderedSpaces.Count); s++) {
            var (spaceName, space) = orderedSpaces[s];

            sb.Append(value: $"    \"{EscapeJson(text: spaceName)}\": {{\n");
            sb.Append(value: $"      \"dimensions\": {space.Identity.Dimensions},\n");
            sb.Append(value: "      \"entries\": {");

            var orderedEntries = space.Entries.OrderBy(keySelector: e => e.Key, comparer: StringComparer.Ordinal).ToList();

            if (orderedEntries.Count == 0) {
                sb.Append(value: "},\n");
            } else {
                sb.Append(value: "\n");

                for (var e = 0; (e < orderedEntries.Count); e++) {
                    var (hashKey, entry) = orderedEntries[e];

                    sb.Append(value: $"        \"{hashKey}\": {{\n");
                    sb.Append(value: $"          \"text\": \"{EscapeJson(text: entry.Text)}\",\n");
                    sb.Append(value: $"          \"vector\": \"{EscapeJson(text: entry.Vector)}\"\n");
                    sb.Append(value: "        }");

                    if (e < (orderedEntries.Count - 1)) {
                        sb.Append(value: ",");
                    }

                    sb.Append(value: "\n");
                }

                sb.Append(value: "      },\n");
            }

            sb.Append(value: $"      \"model\": \"{EscapeJson(text: space.Identity.Model)}\",\n");
            sb.Append(value: $"      \"revision\": \"{EscapeJson(text: space.Identity.Revision)}\"\n");
            sb.Append(value: "    }");

            if (s < (orderedSpaces.Count - 1)) {
                sb.Append(value: ",");
            }

            sb.Append(value: "\n");
        }

        sb.Append(value: "  }\n}\n");

        return sb.ToString();
    }
    /// <summary>Writes the lock file only when its bytes change.</summary>
    /// <param name="lockPath">Destination lock path.</param>
    /// <returns><see langword="true"/> if the file was written; <see langword="false"/> if contents were unchanged.</returns>
    public bool Write(string lockPath) {
        var serialized = Serialize();
        var newBytes = Encoding.UTF8.GetBytes(s: serialized);

        if (File.Exists(path: lockPath)) {
            try {
                var existingBytes = File.ReadAllBytes(path: lockPath);

                if (existingBytes.AsSpan().SequenceEqual(other: newBytes)) {
                    return false;
                }
            } catch {
                // If read fails, proceed with writing
            }
        }

        var dir = Path.GetDirectoryName(path: lockPath);

        if (!string.IsNullOrEmpty(value: dir) && !Directory.Exists(path: dir)) {
            Directory.CreateDirectory(path: dir);
        }

        File.WriteAllBytes(bytes: newBytes, path: lockPath);

        return true;
    }

    private static string EscapeJson(string text) {
        return JsonValue.Create(value: text)!.ToJsonString()[1..^1];
    }
}
