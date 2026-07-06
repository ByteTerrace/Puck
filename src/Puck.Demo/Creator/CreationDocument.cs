using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Demo.Forge;
using Puck.SdfVm;

namespace Puck.Demo.Creator;

/// <summary>One palette entry on the wire (mirrors <see cref="SdfMaterial"/> with document-doctrine nullability).</summary>
/// <param name="Albedo">The base color.</param>
/// <param name="Emissive">The emissive strength (null = 0 — optional members are nullable and normalized at load).</param>
/// <param name="Specular">The specular strength (null = the material default).</param>
/// <param name="Shininess">The specular exponent (null = the material default).</param>
public sealed record PaletteEntryDocument(Vector3 Albedo, float? Emissive, float? Specular, float? Shininess);

/// <summary>One authored shape on the wire (mirrors <see cref="CreatorShapeState"/>).</summary>
/// <param name="Id">The shape's stable id.</param>
/// <param name="Name">The optional player-given name.</param>
/// <param name="Type">The primitive.</param>
/// <param name="Position">The shape's position (workbench space).</param>
/// <param name="Rotation">The orientation.</param>
/// <param name="Scale">The per-axis scale.</param>
/// <param name="Material">The palette slot (null = 0).</param>
/// <param name="Blend">The blend op name (null = Union).</param>
/// <param name="Smooth">The smooth-blend radius (null = 0).</param>
/// <param name="Group">The composition group (null = ungrouped).</param>
/// <param name="Mirror">Whether the shape mirrors across its local X=0 plane (null = false).</param>
/// <param name="Twist">The shape's local twist rate (null = 0).</param>
/// <param name="Onion">The shape's shell thickness (null = 0, solid).</param>
public sealed record ShapeDocument(
    int Id,
    string? Name,
    AvatarPrimitive Type,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale,
    int? Material,
    SdfBlendOp? Blend,
    float? Smooth,
    int? Group,
    bool? Mirror = null,
    float? Twist = null,
    float? Onion = null
);

/// <summary>One IK chain on the wire (mirrors <see cref="CreatorChainState"/>'s DEFINITION — rest geometry is
/// re-derived from the member shapes' CURRENT positions at load time, never persisted, so a loaded chain always
/// captures fresh against whatever pose the shapes loaded at).</summary>
/// <param name="Id">The chain's stable id.</param>
/// <param name="Name">The player-given name (null = unnamed).</param>
/// <param name="Shapes">The member shape ids, root→tip order.</param>
/// <param name="Kind">"limb" or "spine" (null = "limb" when exactly 3 shapes, else "spine").</param>
/// <param name="Goal">The live goal position (null = the rest tip — re-seeded at load).</param>
/// <param name="Pole">The bend-direction hint (null = above the root — re-seeded at load).</param>
public sealed record ChainDocument(
    int Id,
    string? Name,
    IReadOnlyList<int> Shapes,
    string? Kind,
    Vector3? Goal,
    Vector3? Pole
);

/// <summary>One timeline frame on the wire: a named full snapshot of every shape's transform (the animation model —
/// consumed when the timeline lands; carried in the schema from v1 so saved files stay stable).</summary>
/// <param name="Name">The frame's name (<c>rest</c> is the live pose).</param>
/// <param name="Transforms">The per-shape transform snapshots, keyed by shape id.</param>
public sealed record FrameDocument(string Name, IReadOnlyList<FrameTransformDocument> Transforms);

/// <summary>One shape's transform inside a timeline frame.</summary>
/// <param name="Id">The shape id the snapshot belongs to.</param>
/// <param name="Position">The pose position.</param>
/// <param name="Rotation">The pose orientation.</param>
/// <param name="Scale">The pose scale.</param>
public sealed record FrameTransformDocument(int Id, Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>
/// One camera EYE a creation carries — a posed viewpoint ANCHORED to one of the creation's own shapes (a
/// <see cref="ShapeDocument.Id"/>), so the eye rides that shape's live pose through IK/animation frames. This is the
/// creation-side twin of the world document's placed camera (<c>World.CameraDocument</c>): the lantern-fish's lens
/// dangling off its lure becomes ONE entry here rather than a hardcoded engine. The offset pose (position/yaw/pitch)
/// is relative to the anchored shape's frame; the feed it produces is wired onto a screen by name through the
/// creation's behavior manifest (see <see cref="CreationBehaviorDocument"/>) or the world's wiring table.
/// </summary>
/// <param name="Id">The eye's stable id within the creation.</param>
/// <param name="ShapeId">The anchored shape id (a <see cref="ShapeDocument.Id"/>). A camera naming a missing shape is
/// dropped at load (its offset frame has no anchor).</param>
/// <param name="Position">The eye offset from the anchored shape's frame origin.</param>
/// <param name="Yaw">The eye heading offset, degrees (null = 0).</param>
/// <param name="Pitch">The eye tilt offset, degrees (null = 0).</param>
/// <param name="Fov">The vertical field of view, degrees (null = the engine default).</param>
/// <param name="Focus">The look-at target distance ahead (null = 1).</param>
/// <param name="Feed">The named feed this eye publishes (null = the eye's id as a name). A screen face wired to this
/// name shows this eye's live render — pure data, no creature-specific channel.</param>
public sealed record CreationCameraDocument(
    int Id,
    int ShapeId,
    Vector3 Position,
    float? Yaw,
    float? Pitch,
    float? Fov,
    float? Focus,
    string? Feed
);

/// <summary>One screen FACE a creation declares — a surface on the creation (backed by one of its shapes) that shows a
/// feed. The robot's CRT face is one of these: it shows the named host <c>emotes</c> feed by DEFAULT, and is wirable to
/// ANY camera feed (a creation camera's named feed, another creation's feed, a world camera) purely by naming a
/// different default source. No robot-specific channel exists — a face is just a screen surface with a default wire,
/// and the wiring model does the rest.</summary>
/// <param name="Name">The face's name (a wiring handle — <c>face</c> by default).</param>
/// <param name="ShapeId">The shape whose surface is the screen (a <see cref="ShapeDocument.Id"/>; -1/null = the whole
/// creation's canonical face surface, resolved by the consumer).</param>
/// <param name="DefaultSource">The feed this face shows when nothing else is wired — a source token in the wiring
/// grammar (<c>named:emotes</c>, <c>feed:0</c>, …; null = <c>named:emotes</c>, the emote face default).</param>
public sealed record CreationFaceDocument(
    string Name,
    int? ShapeId,
    string? DefaultSource
);

/// <summary>
/// A creation's BEHAVIOR manifest — the behavioral facts a creation carries so consumers stop re-supplying them by
/// hand. Today a loaded fish walks because nothing records that it SWIMS; this makes those facts DATA. Minimal and
/// normalized: a locomotion mode and the creation's declared faces (screen surfaces that show named feeds). Consumers
/// (the companion state, <c>companion.add</c>, the boot loader, the flagship recipes) are a FOLLOW-UP surgery round —
/// this workstream lands the schema, normalization, and round-trip only.
/// </summary>
/// <param name="Locomotion">How the creation moves — <c>walk</c> (default), <c>swim</c> (hover-bob, a swimmer), or
/// <c>hover</c> (float in place). Null = walk.</param>
/// <param name="Faces">The declared screen faces (null = none). A creation with a face shows a feed on its body; the
/// face's default source is pure data, wirable to any camera feed.</param>
public sealed record CreationBehaviorDocument(
    string? Locomotion,
    IReadOnlyList<CreationFaceDocument>? Faces
);

/// <summary>
/// The <c>puck.creation.v1</c> document — a creator scene as data, the everything-as-data payoff for authoring: a
/// creation can be named, saved, reloaded, and handed to the bake/forge headlessly. Persisted under
/// <c>./creations/&lt;name&gt;.creation.json</c>. Document doctrine applies throughout: every OPTIONAL member is
/// declared nullable (the polymorphic parse path skips property initializers — an omitted member arrives null
/// regardless), validated only when present, and normalized at consumption (<see cref="CreationStore"/>).
/// </summary>
/// <param name="Schema">The document version tag (<c>puck.creation.v1</c>).</param>
/// <param name="Name">The creation's save/load handle.</param>
/// <param name="Intent">The authoring intent name (null = Object).</param>
/// <param name="BakeStyle">The per-cart bake style knob (null = classic).</param>
/// <param name="Palette">The material palette (null = the default sweep).</param>
/// <param name="Shapes">The authored shapes (null = empty).</param>
/// <param name="Frames">The timeline frames (null = none — the animation workstream consumes these).</param>
/// <param name="Chains">The IK rig's chains (null = none). Shapes stay flat; a chain only references shape ids.</param>
/// <param name="Cameras">The creation's anchored camera eyes (null = none). Each rides a shape and produces a named
/// feed — the lantern-fish's lure lens is one entry here.</param>
/// <param name="Behavior">The behavior manifest (null = the defaults: walks, no face). Records how the creation moves
/// and any screen faces it declares, so consumers stop re-supplying those facts by hand.</param>
public sealed record CreationDocument(
    string? Schema,
    string? Name,
    CreatorIntent? Intent,
    string? BakeStyle,
    IReadOnlyList<PaletteEntryDocument>? Palette,
    IReadOnlyList<ShapeDocument>? Shapes,
    IReadOnlyList<FrameDocument>? Frames,
    IReadOnlyList<ChainDocument>? Chains = null,
    IReadOnlyList<CreationCameraDocument>? Cameras = null,
    CreationBehaviorDocument? Behavior = null
) {
    /// <summary>The version tag every saved document carries.</summary>
    public const string CurrentSchema = "puck.creation.v1";
}

/// <summary>
/// Loads and saves <see cref="CreationDocument"/>s against the <c>./creations/</c> folder, plus the legacy
/// <c>.avatar.json</c> import (an <see cref="AvatarDefinition"/> sniffed by its <c>Shapes</c> + <c>BoundRadius</c>
/// members). One shared serializer options instance — <c>IncludeFields = true</c> is LOAD-BEARING: Vector3/Quaternion
/// expose fields, not properties, and omitting it silently zeroes every transform into degenerate shapes.
/// </summary>
public static class CreationStore {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        Converters = { new JsonStringEnumConverter() },
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>The folder creations persist under (relative to the working directory, beside forged-avatars/).</summary>
    public static string Folder => "creations";

    /// <summary>Serializes a document to indented camel-case JSON.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(CreationDocument document) =>
        JsonSerializer.Serialize(options: JsonOptions, value: document);

    /// <summary>Saves a document under <c>./creations/&lt;name&gt;.creation.json</c> (the name is sanitized to
    /// letters, digits, dashes, and underscores).</summary>
    /// <param name="document">The document to save.</param>
    /// <param name="name">The save handle.</param>
    /// <returns>The written path.</returns>
    public static string Save(CreationDocument document, string name) {
        ArgumentNullException.ThrowIfNull(document);

        var sanitized = Sanitize(name: name);
        var path = PathFor(name: name);
        var json = ToJson(document: (document with { Name = sanitized, Schema = CreationDocument.CurrentSchema }));

        _ = Directory.CreateDirectory(path: Folder);
        File.WriteAllText(contents: json, path: path);

        // Everything-CAS: the canonical bytes also land in the shared content-addressed store under
        // refs/creations/<name>, so a saved creation is IMMEDIATELY stampable into the world by name
        // (world.place resolves through the store — the sculpt→stamp loop's front door).
        var store = new Puck.Assets.ContentAddressedStore(root: "store");
        var hash = store.Put(content: System.Text.Encoding.UTF8.GetBytes(s: json));

        store.SetRef(category: "creations", hash: hash, name: sanitized);

        return path;
    }

    /// <summary>Loads a creation by save handle OR file path, accepting both the native document and a legacy
    /// <c>.avatar.json</c> (imported with default style/materials). The result is normalized — never trust persisted
    /// derived values.</summary>
    /// <param name="nameOrPath">The save handle (resolved under <c>./creations/</c>) or an explicit file path.</param>
    /// <returns>The normalized document, or null when nothing readable exists at the location.</returns>
    public static CreationDocument? Load(string nameOrPath) {
        ArgumentException.ThrowIfNullOrEmpty(nameOrPath);

        var path = (File.Exists(path: nameOrPath) ? nameOrPath : PathFor(name: nameOrPath));

        if (!File.Exists(path: path)) {
            return null;
        }

        var json = File.ReadAllText(path: path);

        // The legacy avatar shape: an AvatarDefinition (Shapes + BoundRadius, no schema tag) — import it so every
        // creation authored before the document existed migrates for free.
        if (!json.Contains(value: CreationDocument.CurrentSchema, comparisonType: StringComparison.Ordinal) &&
            json.Contains(value: "BoundRadius", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return ImportAvatar(avatar: AvatarDefinition.FromJson(json: json), name: Path.GetFileNameWithoutExtension(path: path));
        }

        return Normalize(document: JsonSerializer.Deserialize<CreationDocument>(json: json, options: JsonOptions));
    }

    /// <summary>Lists the save handles under <c>./creations/</c>.</summary>
    /// <returns>The handles, sorted ordinally.</returns>
    public static IReadOnlyList<string> List() {
        if (!Directory.Exists(path: Folder)) {
            return [];
        }

        var names = new List<string>();

        foreach (var path in Directory.EnumerateFiles(path: Folder, searchPattern: "*.creation.json")) {
            names.Add(item: Path.GetFileName(path: path)[..^".creation.json".Length]);
        }

        names.Sort(comparer: StringComparer.Ordinal);

        return names;
    }

    // Normalization is the load-time half of the document doctrine: clamp/default every optional member so the
    // in-memory model never sees a null it has to reason about.
    private static CreationDocument? Normalize(CreationDocument? document) {
        if (document is null) {
            return null;
        }

        var shapes = new List<ShapeDocument>(capacity: (document.Shapes?.Count ?? 0));
        var shapeIds = new HashSet<int>();

        foreach (var shape in (document.Shapes ?? [])) {
            shapes.Add(item: shape with {
                Blend = (shape.Blend ?? SdfBlendOp.Union),
                Group = Math.Max(val1: (shape.Group ?? 0), val2: 0),
                Material = Math.Clamp(value: (shape.Material ?? 0), max: (CreatorScene.PaletteSize - 1), min: 0),
                Mirror = (shape.Mirror ?? false),
                Onion = Math.Clamp(value: (shape.Onion ?? 0f), max: CreatorScene.MaxOnion, min: 0f),
                Rotation = ((shape.Rotation == default) ? Quaternion.Identity : Quaternion.Normalize(value: shape.Rotation)),
                Scale = ((shape.Scale == default) ? Vector3.One : shape.Scale),
                Smooth = Math.Clamp(value: (shape.Smooth ?? 0f), max: CreatorScene.MaxSmooth, min: 0f),
                Twist = Math.Clamp(value: (shape.Twist ?? 0f), max: CreatorScene.MaxTwist, min: -CreatorScene.MaxTwist),
            });
            _ = shapeIds.Add(item: shape.Id);
        }

        List<ChainDocument>? chains = null;

        if (document.Chains is { Count: > 0 } sourceChains) {
            chains = new List<ChainDocument>(capacity: sourceChains.Count);

            foreach (var chain in sourceChains) {
                // A chain naming any missing shape id is dropped outright — its rest geometry can never be
                // recaptured against a shape that is not there, and a partial chain has no sound IK meaning.
                if ((chain.Shapes is not { Count: > 0 } memberIds) || !memberIds.All(predicate: shapeIds.Contains)) {
                    continue;
                }

                var kind = (chain.Kind ?? ((memberIds.Count == 3) ? CreatorChainState.KindLimb : CreatorChainState.KindSpine));

                // "limb" is a structural invariant: exactly 3 shapes (2 bones) or it demotes to "spine" — the spine
                // solver degrades gracefully to any length, so this can never leave a chain unsolvable.
                if (string.Equals(a: kind, b: CreatorChainState.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase) && (memberIds.Count != 3)) {
                    kind = CreatorChainState.KindSpine;
                }

                chains.Add(item: chain with { Kind = kind });
            }
        }

        return (document with {
            BakeStyle = (string.Equals(a: document.BakeStyle, b: "bold", comparisonType: StringComparison.OrdinalIgnoreCase) ? "bold" : "classic"),
            Behavior = NormalizeBehavior(behavior: document.Behavior, shapeIds: shapeIds),
            Cameras = NormalizeCreationCameras(cameras: document.Cameras, shapeIds: shapeIds),
            Chains = chains,
            Intent = (document.Intent ?? CreatorIntent.Object),
            Name = Sanitize(name: (document.Name ?? "creation")),
            Schema = CreationDocument.CurrentSchema,
            Shapes = shapes,
        });
    }

    // A creation camera rides one of the creation's own shapes; a camera naming a missing shape (or carrying a
    // non-finite offset) is dropped, mirroring the chain rule. Optional angles/fov coerce to finite; the feed name
    // defaults to null (the consumer falls back to the eye id).
    private static List<CreationCameraDocument>? NormalizeCreationCameras(IReadOnlyList<CreationCameraDocument>? cameras, HashSet<int> shapeIds) {
        if (cameras is not { Count: > 0 } sourceCameras) {
            return null;
        }

        var normalized = new List<CreationCameraDocument>(capacity: sourceCameras.Count);

        foreach (var camera in sourceCameras) {
            if (!shapeIds.Contains(item: camera.ShapeId) ||
                !float.IsFinite(f: camera.Position.X) || !float.IsFinite(f: camera.Position.Y) || !float.IsFinite(f: camera.Position.Z)) {
                continue;
            }

            normalized.Add(item: camera with {
                Feed = ((camera.Feed is { Length: > 0 } feed) ? feed : null),
                Focus = ((camera.Focus is { } focus && float.IsFinite(f: focus)) ? (float?)MathF.Max(focus, 0.01f) : null),
                Fov = ((camera.Fov is { } fov && float.IsFinite(f: fov)) ? (float?)Math.Clamp(value: fov, max: 170f, min: 1f) : null),
                Pitch = ((camera.Pitch is { } pitch && float.IsFinite(f: pitch)) ? (float?)Math.Clamp(value: pitch, max: 85f, min: -85f) : null),
                Yaw = ((camera.Yaw is { } yaw && float.IsFinite(f: yaw)) ? (float?)yaw : null),
            });
        }

        return ((normalized.Count > 0) ? normalized : null);
    }

    // The behavior manifest normalizes to a canonical locomotion member name and drops a face naming a missing shape.
    // A manifest that is entirely default (walk, no faces) collapses to null so a creation without behavioral facts
    // round-trips byte-identically to one that never carried the manifest at all.
    private static CreationBehaviorDocument? NormalizeBehavior(CreationBehaviorDocument? behavior, HashSet<int> shapeIds) {
        if (behavior is null) {
            return null;
        }

        var locomotion = (behavior.Locomotion?.ToLowerInvariant() switch {
            "swim" => "swim",
            "hover" => "hover",
            _ => "walk",
        });

        List<CreationFaceDocument>? faces = null;

        if (behavior.Faces is { Count: > 0 } sourceFaces) {
            faces = new List<CreationFaceDocument>(capacity: sourceFaces.Count);

            foreach (var face in sourceFaces) {
                // A face may name a specific shape surface or the creation's canonical face (null/-1). A named shape
                // that is missing drops the face (its surface is not there).
                if ((face.ShapeId is { } shapeId) && (shapeId >= 0) && !shapeIds.Contains(item: shapeId)) {
                    continue;
                }

                faces.Add(item: face with {
                    DefaultSource = ((face.DefaultSource is { Length: > 0 } source) ? source : null),
                    Name = ((face.Name is { Length: > 0 } name) ? name : "face"),
                    ShapeId = (((face.ShapeId is { } id) && (id >= 0)) ? (int?)id : null),
                });
            }
        }

        // Fully default → null (byte-stable round-trip with a manifest-less creation).
        if (string.Equals(a: locomotion, b: "walk", comparisonType: StringComparison.Ordinal) && (faces is not { Count: > 0 })) {
            return null;
        }

        return new CreationBehaviorDocument(Faces: faces, Locomotion: locomotion);
    }

    private static CreationDocument ImportAvatar(AvatarDefinition avatar, string name) {
        var shapes = new List<ShapeDocument>(capacity: avatar.Shapes.Count);

        for (var index = 0; (index < avatar.Shapes.Count); index++) {
            var shape = avatar.Shapes[index];

            shapes.Add(item: new ShapeDocument(
                Blend: SdfBlendOp.Union,
                Group: 0,
                Id: index,
                Material: (index % CreatorScene.PaletteSize),
                Name: null,
                Position: shape.Position,
                Rotation: shape.Rotation,
                Scale: shape.Scale,
                Smooth: 0f,
                Type: shape.Type
            ));
        }

        return new CreationDocument(
            BakeStyle: "classic",
            Frames: null,
            Intent: CreatorIntent.Object,
            Name: Sanitize(name: name),
            Palette: null,
            Schema: CreationDocument.CurrentSchema,
            Shapes: shapes
        );
    }

    private static string PathFor(string name) =>
        Path.Combine(path1: Folder, path2: $"{Sanitize(name: name)}.creation.json");

    private static string Sanitize(string name) {
        var builder = new System.Text.StringBuilder(capacity: name.Length);

        foreach (var character in name) {
            _ = builder.Append(value: (char.IsAsciiLetterOrDigit(c: character) || (character is '-' or '_')) ? character : '-');
        }

        return ((builder.Length > 0) ? builder.ToString() : "creation");
    }
}
