using System.Text.Json;
using Puck.Assets;

namespace Puck.Demo.World;

/// <summary>
/// Loads and saves <see cref="WorldDocument"/>s against the <c>./worlds/</c> folder, the sibling store of
/// <see cref="Creator.CreationStore"/>/<see cref="Forge.AudioDocumentStore"/> — same discipline throughout: a
/// blank document is built through <see cref="Normalize"/> (never a hand-built record), an unrecognized
/// <c>schema</c> tag throws loudly, and names are sanitized identically. Serializes through the ONE shared
/// <see cref="DocumentJsonOptions.Shared"/> instance — <c>IncludeFields = true</c> is LOAD-BEARING here exactly as in
/// <see cref="Creator.CreationStore"/>: the document's <see cref="System.Numerics.Vector3"/> members expose fields,
/// not properties, and omitting the option silently zeroes every transform into a degenerate placement.
/// </summary>
public static class WorldDocumentStore {
    /// <summary>The folder world documents persist under (relative to the working directory, the sibling of
    /// <see cref="Creator.CreationStore.Folder"/>/<see cref="Forge.AudioDocumentStore.Folder"/>).</summary>
    public static string Folder => "worlds";

    /// <summary>Serializes a document to indented camel-case JSON.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(WorldDocument document) =>
        JsonSerializer.Serialize(options: DocumentJsonOptions.Shared, value: document);

    /// <summary>Builds a fresh blank document (no bounds, terrain, placements, lights, or walk data) through the
    /// SAME normalize path every loaded document takes — never a hand-built record.</summary>
    /// <param name="name">The new document's save/load handle (null = "world").</param>
    /// <returns>The normalized blank document.</returns>
    public static WorldDocument Blank(string? name = null) =>
        Normalize(document: new WorldDocument(
            Bounds: null,
            Lights: null,
            Name: name,
            Placements: null,
            Schema: null,
            Terrain: null,
            WalkGrid: null,
            WalkOverrides: null
        ));

    /// <summary>Saves a document under <c>./worlds/&lt;name&gt;.world.json</c> (the name is sanitized to letters,
    /// digits, dashes, and underscores) and, when <paramref name="store"/> is given, also lands the canonical bytes
    /// in the content-addressed store under <c>SetRef("worlds", name)</c>.</summary>
    /// <param name="document">The document to save.</param>
    /// <param name="name">The save handle.</param>
    /// <param name="store">The content-addressed store to also write into (null = file only).</param>
    /// <returns>The written file path, and the content hash when <paramref name="store"/> was given (null otherwise).</returns>
    public static (string Path, string? Hash) Save(WorldDocument document, string name, ContentAddressedStore? store = null) {
        ArgumentNullException.ThrowIfNull(document);

        var path = PathFor(name: name);
        var json = ToJson(document: (document with { Name = SanitizeName(name: name), Schema = WorldDocument.CurrentSchema }));

        _ = Directory.CreateDirectory(path: Folder);
        File.WriteAllText(contents: json, path: path);

        var hash = store?.Put(content: System.Text.Encoding.UTF8.GetBytes(s: json));

        if ((store is not null) && (hash is not null)) {
            store.SetRef(category: "worlds", name: SanitizeName(name: name), hash: hash);
        }

        return (path, hash);
    }

    /// <summary>Loads a world by save handle OR file path (a handle resolves under <c>./worlds/</c>). The result is
    /// normalized — never trust persisted derived values.</summary>
    /// <param name="nameOrPath">The save handle or an explicit file path.</param>
    /// <returns>The normalized document, or null when nothing readable exists at the location.</returns>
    public static WorldDocument? Load(string nameOrPath) {
        ArgumentException.ThrowIfNullOrEmpty(nameOrPath);

        var path = (File.Exists(path: nameOrPath) ? nameOrPath : PathFor(name: nameOrPath));

        if (!File.Exists(path: path)) {
            return null;
        }

        var json = File.ReadAllText(path: path);
        var document = (JsonSerializer.Deserialize<WorldDocument>(json: json, options: DocumentJsonOptions.Shared)
            ?? throw new InvalidDataException(message: $"'{path}' deserialized to null."));

        if ((document.Schema is { Length: > 0 } schema) && !string.Equals(a: schema, b: WorldDocument.CurrentSchema, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidDataException(message: $"'{path}' declares schema '{schema}', not the recognized '{WorldDocument.CurrentSchema}'.");
        }

        return Normalize(document: document);
    }

    /// <summary>Lists the save handles under <c>./worlds/</c>.</summary>
    /// <returns>The handles, sorted ordinally.</returns>
    public static IReadOnlyList<string> List() {
        if (!Directory.Exists(path: Folder)) {
            return [];
        }

        var names = new List<string>();

        foreach (var path in Directory.EnumerateFiles(path: Folder, searchPattern: "*.world.json")) {
            names.Add(item: Path.GetFileName(path: path)[..^".world.json".Length]);
        }

        names.Sort(comparer: StringComparer.Ordinal);

        return names;
    }

    /// <summary>Resolves every placement's <see cref="PlacementDocument.Source"/> against a content-addressed
    /// store, loudly reporting any that are missing — bit-for-bit doctrine forbids a partially-resolved world, so
    /// callers must check <paramref name="missing"/> is empty before proceeding with a build.</summary>
    /// <param name="document">The world document whose placements to resolve.</param>
    /// <param name="store">The content-addressed store creations are expected to live in.</param>
    /// <param name="missing">The placement ids whose source hash could not be resolved in the store.</param>
    /// <returns>Resolved creation bytes, keyed by placement id, for every placement that resolved.</returns>
    public static IReadOnlyDictionary<int, byte[]> TryResolvePlacementSources(WorldDocument document, ContentAddressedStore store, out IReadOnlyList<int> missing) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(store);

        var resolved = new Dictionary<int, byte[]>();
        var missingIds = new List<int>();

        foreach (var placement in (document.Placements ?? [])) {
            if ((placement.Source is { Length: > 0 } source) && store.TryGet(hash: source, content: out var bytes)) {
                resolved[placement.Id] = bytes;
            } else {
                missingIds.Add(item: placement.Id);
            }
        }

        missing = missingIds;

        return resolved;
    }

    private static string PathFor(string name) =>
        Path.Combine(path1: Folder, path2: $"{SanitizeName(name: name)}.world.json");

    // Mirrors CreationStore's Sanitize / AudioDocumentStore's SanitizeName exactly (non [A-Za-z0-9_-] chars -> '-';
    // empty -> a safe default).
    private static string SanitizeName(string name) {
        var builder = new System.Text.StringBuilder(capacity: name.Length);

        foreach (var character in name) {
            _ = builder.Append(value: (char.IsAsciiLetterOrDigit(c: character) || (character is '-' or '_')) ? character : '-');
        }

        return ((builder.Length > 0) ? builder.ToString() : "world");
    }

    // Normalization is the load-time half of the document doctrine: clamp/default every optional member so the
    // scene/renderer and walk-grid workstreams never see a null or malformed value they have to reason about.
    private static WorldDocument Normalize(WorldDocument document) {
        return (document with {
            Bounds = NormalizeBounds(bounds: document.Bounds),
            Cameras = NormalizeCameras(cameras: document.Cameras),
            Lights = NormalizeLights(lights: document.Lights),
            Name = (string.IsNullOrWhiteSpace(value: document.Name) ? "world" : SanitizeName(name: document.Name)),
            Placements = NormalizePlacements(placements: document.Placements),
            Schema = WorldDocument.CurrentSchema,
            Terrain = NormalizeTerrain(terrain: document.Terrain),
            WalkGrid = document.WalkGrid,
            WalkOverrides = NormalizeWalkOverrides(overrides: document.WalkOverrides),
            Wiring = NormalizeWiring(wiring: document.Wiring),
        });
    }

    // A camera eye clamps to the canonical anchor member name (world/placement), drops a non-finite pose (a degenerate
    // eye can never resolve a sound look-at), and coerces optional angles to finite. Standalone (world) eyes clear the
    // anchor id; placement-anchored eyes keep it. Duplicate ids are NOT dropped here (ids resequence on load, like
    // placements) — this stays a pure clamp, matching the rest of the doctrine.
    private static List<CameraDocument> NormalizeCameras(IReadOnlyList<CameraDocument>? cameras) {
        var normalized = new List<CameraDocument>(capacity: (cameras?.Count ?? 0));

        foreach (var camera in (cameras ?? [])) {
            if (!float.IsFinite(f: camera.Position.X) || !float.IsFinite(f: camera.Position.Y) || !float.IsFinite(f: camera.Position.Z)) {
                continue;
            }

            var anchor = NormalizeCameraAnchor(anchor: camera.Anchor);

            normalized.Add(item: camera with {
                Anchor = anchor,
                AnchorId = ((anchor == "placement") ? (camera.AnchorId ?? 0) : null),
                Focus = ((camera.Focus is { } focus && float.IsFinite(f: focus)) ? (float?)MathF.Max(focus, 0.01f) : null),
                Fov = ((camera.Fov is { } fov && float.IsFinite(f: fov)) ? (float?)Math.Clamp(value: fov, max: 170f, min: 1f) : null),
                Pitch = ((camera.Pitch is { } pitch && float.IsFinite(f: pitch)) ? (float?)Math.Clamp(value: pitch, max: 85f, min: -85f) : null),
                Yaw = ((camera.Yaw is { } yaw && float.IsFinite(f: yaw)) ? (float?)yaw : null),
            });
        }

        return normalized;
    }

    // A "placement" anchor rides a stamp; anything else (including null) reads as a standalone "world" eye. Coerced to
    // the canonical member name so a load→save round-trip is byte-stable regardless of authored casing.
    private static string NormalizeCameraAnchor(string? anchor) =>
        (string.Equals(a: anchor, b: "placement", comparisonType: StringComparison.OrdinalIgnoreCase) ? "placement" : "world");

    // The wiring table normalizes each entry to a canonical source kind and drops an entry with an out-of-range screen
    // index or (for a named source) a missing name. At most ONE entry survives per screen index — a later entry for a
    // taken index REPLACES the earlier (last-wins), matching the verb's set-semantics, so a round-trip is deterministic.
    private static List<ScreenWireDocument> NormalizeWiring(IReadOnlyList<ScreenWireDocument>? wiring) {
        var byScreen = new Dictionary<int, ScreenWireDocument>();
        var order = new List<int>();

        foreach (var entry in (wiring ?? [])) {
            if ((entry.Screen < 0) || (entry.Screen >= Puck.SdfVm.SdfProgramBuilder.MaxScreenSurfaces)) {
                continue;
            }

            var kind = NormalizeWireKind(kind: entry.Kind);

            // A named source with no name is meaningless — it drops to "none" (the surface's flat fallback) rather than
            // carrying a wire that resolves to nothing.
            if (string.Equals(a: kind, b: "named", comparisonType: StringComparison.Ordinal) && (entry.Name is not { Length: > 0 })) {
                kind = "none";
            }

            var normalized = new ScreenWireDocument(
                Index: ((kind is "brick" or "feed") ? (int?)Math.Max(val1: (entry.Index ?? 0), val2: 0) : null),
                Kind: kind,
                Name: (string.Equals(a: kind, b: "named", comparisonType: StringComparison.Ordinal) ? entry.Name : null),
                Screen: entry.Screen
            );

            if (!byScreen.ContainsKey(key: entry.Screen)) {
                order.Add(item: entry.Screen);
            }

            byScreen[entry.Screen] = normalized;
        }

        var result = new List<ScreenWireDocument>(capacity: order.Count);

        foreach (var screen in order) {
            // A "none" wire is the ABSENCE of a wire — it need not persist (an unlisted screen already reads as none),
            // so a cleared entry drops out of the saved table entirely, keeping the table minimal.
            if (!string.Equals(a: byScreen[screen].Kind, b: "none", comparisonType: StringComparison.Ordinal)) {
                result.Add(item: byScreen[screen]);
            }
        }

        return result;
    }

    private static string NormalizeWireKind(string? kind) =>
        (kind?.ToLowerInvariant() switch {
            "brick" => "brick",
            "feed" => "feed",
            "named" => "named",
            _ => "none",
        });

    private static WorldBoundsDocument? NormalizeBounds(WorldBoundsDocument? bounds) {
        if (bounds is null) {
            return null;
        }

        if (!float.IsFinite(f: bounds.MinX) || !float.IsFinite(f: bounds.MinZ) ||
            !float.IsFinite(f: bounds.MaxX) || !float.IsFinite(f: bounds.MaxZ) ||
            !float.IsFinite(f: bounds.FloorY)) {
            throw new ArgumentException(message: "World bounds must be finite.", paramName: nameof(bounds));
        }

        return (bounds with {
            MaxX = Math.Max(val1: bounds.MaxX, val2: bounds.MinX),
            MaxZ = Math.Max(val1: bounds.MaxZ, val2: bounds.MinZ),
        });
    }

    private static List<TerrainPatchDocument> NormalizeTerrain(IReadOnlyList<TerrainPatchDocument>? terrain) {
        var normalized = new List<TerrainPatchDocument>(capacity: (terrain?.Count ?? 0));

        foreach (var patch in (terrain ?? [])) {
            normalized.Add(item: patch with {
                Kind = NormalizeTerrainKind(kind: patch.Kind),
                Material = Math.Max(val1: (patch.Material ?? 0), val2: 0),
            });
        }

        return normalized;
    }

    private static string NormalizeTerrainKind(string? kind) =>
        (string.Equals(a: kind, b: "plaza", comparisonType: StringComparison.OrdinalIgnoreCase) ? "plaza" : "slab");

    private static List<PlacementDocument> NormalizePlacements(IReadOnlyList<PlacementDocument>? placements) {
        var normalized = new List<PlacementDocument>(capacity: (placements?.Count ?? 0));

        foreach (var placement in (placements ?? [])) {
            // Bit-for-bit doctrine: a placement with no (or malformed) source can never resolve to a creation, so
            // Normalize drops it rather than carrying a placement that could never build. Normalize stays pure —
            // this is a silent drop, not a throw; loud refusal belongs to TryResolvePlacementSources at load time.
            if (placement.Source is not { Length: > 0 } source || !IsWellFormedHash(hash: source)) {
                continue;
            }

            normalized.Add(item: placement with {
                Mirror = NormalizeMirror(mirror: placement.Mirror),
                Pattern = NormalizePattern(pattern: placement.Pattern),
                Repeat = NormalizeRepeat(repeat: placement.Repeat),
                Scale = ((placement.Scale is { } scale) ? (float?)Math.Max(val1: scale, val2: 0f) : null),
                Source = source,
                YawDegrees = NormalizeYaw(yawDegrees: placement.YawDegrees),
            });
        }

        return normalized;
    }

    // The mirror axis coerces to exactly "x"/"z"/null — any other value is meaningless to the fold and drops to
    // none rather than throwing (the document doctrine's clamp-don't-crash posture for enum-like strings).
    private static string? NormalizeMirror(string? mirror) {
        if (string.Equals(a: mirror, b: "x", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return "x";
        }

        return (string.Equals(a: mirror, b: "z", comparisonType: StringComparison.OrdinalIgnoreCase) ? "z" : null);
    }

    private static PlacementPatternDocument? NormalizePattern(PlacementPatternDocument? pattern) {
        if (pattern is null) {
            return null;
        }

        if (!float.IsFinite(f: pattern.CellWidth) || !float.IsFinite(f: pattern.CellHeight)) {
            throw new ArgumentException(message: "A placement pattern's cell sizes must be finite.", paramName: nameof(pattern));
        }

        if ((pattern.LimitX is { } limitX && !float.IsFinite(f: limitX)) || (pattern.LimitZ is { } limitZ && !float.IsFinite(f: limitZ))) {
            throw new ArgumentException(message: "A placement pattern's limits must be finite.", paramName: nameof(pattern));
        }

        return (pattern with {
            CellHeight = MathF.Max(pattern.CellHeight, 0.0001f),
            CellWidth = MathF.Max(pattern.CellWidth, 0.0001f),
            // Coerced to the CANONICAL member name (so a load→save round-trip is byte-stable regardless of the
            // authored casing); an unrecognized name drops to null — which reads as P1, exactly like null.
            Group = (((pattern.Group is { Length: > 0 } name) && Enum.TryParse<Puck.SdfVm.SdfWallpaperGroup>(ignoreCase: true, result: out var group, value: name)) ? group.ToString() : null),
            LimitX = ((pattern.LimitX is { } lx) ? (float?)MathF.Max(lx, 1f) : null),
            LimitZ = ((pattern.LimitZ is { } lz) ? (float?)MathF.Max(lz, 1f) : null),
            MaterialStride = ((pattern.MaterialStride is { } stride) ? (int?)Math.Max(val1: stride, val2: 0) : null),
        });
    }

    private static PlacementRepeatDocument? NormalizeRepeat(PlacementRepeatDocument? repeat) {
        if (repeat is null) {
            return null;
        }

        if (!float.IsFinite(f: repeat.SpacingX) || !float.IsFinite(f: repeat.SpacingZ)) {
            throw new ArgumentException(message: "A placement repeat's spacing must be finite.", paramName: nameof(repeat));
        }

        return (repeat with {
            CountX = Math.Max(val1: repeat.CountX, val2: 1),
            CountZ = Math.Max(val1: repeat.CountZ, val2: 1),
        });
    }

    private static float? NormalizeYaw(float? yawDegrees) {
        if (yawDegrees is not { } yaw) {
            return null;
        }

        var wrapped = (yaw % 360f);

        return ((wrapped < 0f) ? (wrapped + 360f) : wrapped);
    }

    private static List<WorldLightDocument> NormalizeLights(IReadOnlyList<WorldLightDocument>? lights) {
        var normalized = new List<WorldLightDocument>(capacity: (lights?.Count ?? 0));

        foreach (var light in (lights ?? [])) {
            normalized.Add(item: light with {
                Intensity = Math.Max(val1: (light.Intensity ?? 1f), val2: 0f),
            });
        }

        return normalized;
    }

    private static List<WalkOverrideDocument> NormalizeWalkOverrides(IReadOnlyList<WalkOverrideDocument>? overrides) {
        var normalized = new List<WalkOverrideDocument>(capacity: (overrides?.Count ?? 0));

        foreach (var overrideEntry in (overrides ?? [])) {
            normalized.Add(item: overrideEntry with {
                Kind = (string.Equals(a: overrideEntry.Kind, b: "walkable", comparisonType: StringComparison.OrdinalIgnoreCase) ? "walkable" : "blocker"),
                MaxX = Math.Max(val1: overrideEntry.MaxX, val2: overrideEntry.MinX),
                MaxZ = Math.Max(val1: overrideEntry.MaxZ, val2: overrideEntry.MinZ),
            });
        }

        return normalized;
    }

    private static bool IsWellFormedHash(string hash) =>
        (hash.StartsWith(value: "sha256/", comparisonType: StringComparison.Ordinal) && (hash.Length == ("sha256/".Length + 64)));
}
