using System.Numerics;
using System.Text.Json;
using Puck.SdfVm;

namespace Puck.Authoring;

/// <summary>One palette entry on the wire (mirrors <see cref="SdfMaterial"/> with document-doctrine nullability).</summary>
/// <param name="Albedo">The base color.</param>
/// <param name="Emissive">The emissive strength (null = 0 — optional members are nullable and normalized at load).</param>
/// <param name="Specular">The specular strength (null = the material default).</param>
/// <param name="Shininess">The specular exponent (null = the material default).</param>
public sealed record PaletteEntryDocument(Vector3 Albedo, float? Emissive, float? Specular, float? Shininess);

/// <summary>One authored shape on the wire (mirrors a creator's live placed-shape state).</summary>
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
/// <param name="Bend">The shape's local bend rate about Y (null = 0).</param>
/// <param name="Dilate">The shape's inflation radius (null = 0).</param>
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
    float? Onion = null,
    float? Bend = null,
    float? Dilate = null
) {
    /// <summary>The largest smooth-blend radius a shape's clamp normalizes to.</summary>
    public const float MaxSmooth = 0.5f;
    /// <summary>The largest twist rate, in radians per unit of local Y (NOT an isometry, so this stays moderate —
    /// see <see cref="SdfProgramBuilder.TwistY"/>).</summary>
    public const float MaxTwist = 3.0f;
    /// <summary>The largest onion shell thickness a shape's clamp normalizes to.</summary>
    public const float MaxOnion = 0.2f;
    /// <summary>The largest dilate (inflation) radius — mirrors <see cref="MaxOnion"/>'s clamp.</summary>
    public const float MaxDilate = 0.2f;
    /// <summary>The largest bend rate, in radians per unit of local Y, moderated below <see cref="MaxTwist"/>'s
    /// ceiling: the bend operator's Lipschitz factor is worse than twist's (see
    /// <see cref="SdfProgramBuilder.BendZ"/>'s remarks).</summary>
    public const float MaxBend = 1.5f;
}

/// <summary>
/// One engraved/embossed TEXT RUN a creation carries — a string laid ONTO one of the creation's own surfaces (a shop
/// facade, a marquee band), stored as text-plus-placement and expanded at world-emission time into
/// <see cref="SdfShapeType.Glyph"/> shapes via the SHARED font atlas + <c>Puck.Text.TextLayout</c> (never persisted
/// pre-expanded, so the run stays font-independent on the wire). The run sits on its own plane (<paramref name="Position"/>
/// centre + <paramref name="Rotation"/>, in the creation's workbench space: local +X = advance, +Y = ascent, +Z = the
/// relief normal). The glyph slab STRADDLES the host surface, so the lettering is proud (emboss / Union) or recessed
/// (engrave / Subtraction) but NEVER coplanar — coincident zero-sets speckle (docs/sdf-wiki/text-and-glyphs.md).
/// </summary>
/// <param name="Text">The run's text (whitespace / unmapped code points advance the pen without a glyph).</param>
/// <param name="Position">The run's anchor CENTRE on the host surface (workbench space).</param>
/// <param name="Rotation">The run plane's orientation (local +X advance, +Y ascent, +Z the relief normal).</param>
/// <param name="EmHeight">The world height of one em, in the creation's own (pre-placement) units.</param>
/// <param name="Depth">The glyph extrude HALF-depth — the relief the slab straddles the surface by (null = a thin default).</param>
/// <param name="Mode"><c>engrave</c> (Subtraction — a carved recess) or <c>emboss</c> (Union — proud relief); null = emboss.</param>
/// <param name="Material">The palette slot the letters shade with (null = 0).</param>
public sealed record TextRunDocument(
    string Text,
    Vector3 Position,
    Quaternion Rotation,
    float EmHeight,
    float? Depth,
    string? Mode,
    int? Material
) {
    /// <summary>The engrave mode name (Subtraction — a carved recess).</summary>
    public const string ModeEngrave = "engrave";

    /// <summary>The emboss mode name (Union — proud relief; the default).</summary>
    public const string ModeEmboss = "emboss";

    /// <summary>The number of GLYPH shapes this run expands to for the per-stamp shape budget — its non-whitespace
    /// character count, a conservative upper bound of the atlas's laid-out placements (which skip whitespace and
    /// unmapped code points). Whitespace-only / empty text contributes nothing. Derived (recomputed from
    /// <see cref="Text"/>), so it is kept OFF the wire.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int GlyphCount {
        get {
            if (Text is not { Length: > 0 } text) {
                return 0;
            }

            var count = 0;

            foreach (var character in text) {
                if (!char.IsWhiteSpace(c: character)) {
                    count++;
                }
            }

            return count;
        }
    }
}

/// <summary>One IK chain on the wire (mirrors a creator's live chain state's DEFINITION — rest geometry is
/// re-derived from the member shapes' CURRENT positions at load time, never persisted, so a loaded chain always
/// captures fresh against whatever pose the shapes loaded at).</summary>
/// <param name="Id">The chain's stable id.</param>
/// <param name="Name">The player-given name (null = unnamed).</param>
/// <param name="Shapes">The member shape ids, root→tip order.</param>
/// <param name="Kind"><see cref="KindLimb"/> or <see cref="KindSpine"/> (null = limb when exactly 3 shapes, else spine).</param>
/// <param name="Goal">The live goal position (null = the rest tip — re-seeded at load).</param>
/// <param name="Pole">The bend-direction hint (null = above the root — re-seeded at load).</param>
public sealed record ChainDocument(
    int Id,
    string? Name,
    IReadOnlyList<int> Shapes,
    string? Kind,
    Vector3? Goal,
    Vector3? Pole
) {
    /// <summary>The "limb" kind name (exactly 3 shapes / 2 bones, two-bone IK).</summary>
    public const string KindLimb = "limb";
    /// <summary>The "spine" kind name (any length ≥ 2, single-pass drag solve).</summary>
    public const string KindSpine = "spine";
}

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
/// creation-side twin of a world document's placed camera: the lantern-fish's lens dangling off its lure becomes ONE
/// entry here rather than a hardcoded engine. The offset pose (position/yaw/pitch) is relative to the anchored
/// shape's frame; the feed it produces is wired onto a screen by name through the creation's behavior manifest (see
/// <see cref="CreationBehaviorDocument"/>) or a world's wiring table.
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
/// hand. A loaded fish without one walks because nothing records that it SWIMS; this makes those facts DATA. Minimal
/// and normalized: a locomotion mode and the creation's declared faces (screen surfaces that show named feeds).
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
/// creation can be named, saved, reloaded, and handed to a bake/forge headlessly. Document doctrine applies
/// throughout: every OPTIONAL member is declared nullable (the polymorphic parse path skips property initializers —
/// an omitted member arrives null regardless), validated only when present, and normalized at consumption (see
/// <see cref="CreationStore"/>).
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
/// <param name="TextRuns">The engraved/embossed text runs the creation carries (null/empty = none). Each is a string
/// laid onto a surface, expanded at emission into <see cref="SdfShapeType.Glyph"/> shapes — see
/// <see cref="TextRunDocument"/>. Omitted from the wire when null (a text-free creation stays byte-identical), so this
/// member did NOT churn the whole town's committed JSON when it landed.</param>
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
    CreationBehaviorDocument? Behavior = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<TextRunDocument>? TextRuns = null
) {
    /// <summary>The version tag every saved document carries.</summary>
    public const string CurrentSchema = "puck.creation.v1";

    /// <summary>The material palette's slot count — <see cref="ShapeDocument.Material"/>/<see cref="TextRunDocument.Material"/>
    /// clamp into <c>[0, PaletteSize)</c> at normalization.</summary>
    public const int PaletteSize = 16;

    /// <summary>Unknown sections preserved across a round-trip — the data-side plugin extensibility posture (the
    /// run-document precedent). Null when the document carries no unknown members. A settable (not <c>init</c>)
    /// accessor is required: System.Text.Json appends to it during deserialization.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>The creation's total per-stamp shape budget: its authored shapes PLUS every text run's expanded glyph
    /// count (a run counts as its letters — a world stamps text as real Glyph geometry, so it competes for the same
    /// per-stamp shape budget the boxes do).</summary>
    /// <returns>The total shape count a placement of this creation emits.</returns>
    public int StampShapeCount() {
        var count = (Shapes?.Count ?? 0);

        foreach (var run in (TextRuns ?? [])) {
            count += run.GlyphCount;
        }

        return count;
    }
}

/// <summary>
/// Loads and saves <see cref="CreationDocument"/>s against a creations folder. Serializes through the ONE shared
/// <see cref="DocumentJsonOptions.Shared"/> instance — <c>IncludeFields = true</c> is LOAD-BEARING: Vector3/Quaternion
/// expose fields, not properties, and omitting it silently zeroes every transform into degenerate shapes. Root paths
/// are explicit parameters — this library never bakes in a working-directory convention; a caller's own default
/// (<see cref="DefaultFolder"/>/<see cref="DefaultCasRoot"/> document what Puck.Demo passes) is the caller's choice.
/// </summary>
public static class CreationStore {
    /// <summary>The conventional creations folder name Puck.Demo passes (relative to the working directory, beside
    /// forged-avatars/) — documented, not implied: every method still takes the root explicitly.</summary>
    public const string DefaultFolder = "creations";

    /// <summary>The conventional content-addressed store root Puck.Demo passes — documented, not implied.</summary>
    public const string DefaultCasRoot = "store";

    /// <summary>Serializes a document to indented camel-case JSON.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(CreationDocument document) =>
        JsonSerializer.Serialize(options: DocumentJsonOptions.Shared, value: document);

    /// <summary>Saves a document under <c>&lt;creationsRoot&gt;/&lt;name&gt;.creation.json</c> (the name is sanitized
    /// to letters, digits, dashes, and underscores), and lands its canonical bytes in the content-addressed store so a
    /// saved creation is immediately stampable by name.</summary>
    /// <param name="document">The document to save.</param>
    /// <param name="name">The save handle.</param>
    /// <param name="creationsRoot">The creations folder (Demo's convention: <see cref="DefaultFolder"/>).</param>
    /// <param name="casRoot">The content-addressed store root (Demo's convention: <see cref="DefaultCasRoot"/>).</param>
    /// <returns>The written path.</returns>
    public static string Save(CreationDocument document, string name, string creationsRoot, string casRoot) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(creationsRoot);
        ArgumentException.ThrowIfNullOrEmpty(casRoot);

        var sanitized = Sanitize(name: name);
        var path = PathFor(name: name, creationsRoot: creationsRoot);
        var json = ToJson(document: (document with { Name = sanitized, Schema = CreationDocument.CurrentSchema }));

        _ = Directory.CreateDirectory(path: creationsRoot);
        File.WriteAllText(contents: json, path: path);

        // Everything-CAS: the canonical bytes also land in the shared content-addressed store under
        // refs/creations/<name>, so a saved creation is IMMEDIATELY stampable into the world by name
        // (world.place resolves through the store — the sculpt→stamp loop's front door).
        var store = new Puck.Assets.ContentAddressedStore(root: casRoot);
        var hash = store.Put(content: System.Text.Encoding.UTF8.GetBytes(s: json));

        store.SetRef(category: "creations", hash: hash, name: sanitized);

        return path;
    }

    /// <summary>Loads a creation by save handle or file path. The result is normalized — never trust persisted
    /// derived values.</summary>
    /// <param name="nameOrPath">The save handle (resolved under <paramref name="creationsRoot"/>) or an explicit file path.</param>
    /// <param name="creationsRoot">The creations folder a bare handle resolves against (Demo's convention: <see cref="DefaultFolder"/>).</param>
    /// <returns>The normalized document, or null when nothing readable exists at the location.</returns>
    public static CreationDocument? Load(string nameOrPath, string creationsRoot) {
        ArgumentException.ThrowIfNullOrEmpty(nameOrPath);
        ArgumentException.ThrowIfNullOrEmpty(creationsRoot);

        var path = (File.Exists(path: nameOrPath) ? nameOrPath : PathFor(name: nameOrPath, creationsRoot: creationsRoot));

        if (!File.Exists(path: path)) {
            return null;
        }

        var json = File.ReadAllText(path: path);

        return Normalize(document: JsonSerializer.Deserialize<CreationDocument>(json: json, options: DocumentJsonOptions.Shared));
    }

    /// <summary>Lists the save handles under <paramref name="creationsRoot"/>.</summary>
    /// <param name="creationsRoot">The creations folder (Demo's convention: <see cref="DefaultFolder"/>).</param>
    /// <returns>The handles, sorted ordinally.</returns>
    public static IReadOnlyList<string> List(string creationsRoot) {
        ArgumentException.ThrowIfNullOrEmpty(creationsRoot);

        if (!Directory.Exists(path: creationsRoot)) {
            return [];
        }

        var names = new List<string>();

        foreach (var path in Directory.EnumerateFiles(path: creationsRoot, searchPattern: "*.creation.json")) {
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
                Bend = Math.Clamp(value: (shape.Bend ?? 0f), max: ShapeDocument.MaxBend, min: -ShapeDocument.MaxBend),
                Blend = (shape.Blend ?? SdfBlendOp.Union),
                Dilate = Math.Clamp(value: (shape.Dilate ?? 0f), max: ShapeDocument.MaxDilate, min: 0f),
                Group = Math.Max(val1: (shape.Group ?? 0), val2: 0),
                Material = Math.Clamp(value: (shape.Material ?? 0), max: (CreationDocument.PaletteSize - 1), min: 0),
                Mirror = (shape.Mirror ?? false),
                Onion = Math.Clamp(value: (shape.Onion ?? 0f), max: ShapeDocument.MaxOnion, min: 0f),
                Rotation = ((shape.Rotation == default) ? Quaternion.Identity : Quaternion.Normalize(value: shape.Rotation)),
                Scale = ((shape.Scale == default) ? Vector3.One : shape.Scale),
                Smooth = Math.Clamp(value: (shape.Smooth ?? 0f), max: ShapeDocument.MaxSmooth, min: 0f),
                Twist = Math.Clamp(value: (shape.Twist ?? 0f), max: ShapeDocument.MaxTwist, min: -ShapeDocument.MaxTwist),
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

                var kind = (chain.Kind ?? ((memberIds.Count == 3) ? ChainDocument.KindLimb : ChainDocument.KindSpine));

                // "limb" is a structural invariant: exactly 3 shapes (2 bones) or it demotes to "spine" — the spine
                // solver degrades gracefully to any length, so this can never leave a chain unsolvable.
                if (string.Equals(a: kind, b: ChainDocument.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase) && (memberIds.Count != 3)) {
                    kind = ChainDocument.KindSpine;
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
            TextRuns = NormalizeTextRuns(textRuns: document.TextRuns),
        });
    }

    // The default extrude half-depth a text run relies on when it declares none, and the floors every run clamps to —
    // a zero-depth glyph slab has no relief (it would be coplanar with the surface), so the depth is floored positive.
    private const float DefaultTextDepth = 0.02f;
    private const float MinTextDepth = 0.001f;
    private const float MinTextEmHeight = 0.01f;

    // Text runs normalize to a canonical mode name, a clamped material slot / positive depth+em, and a normalized
    // rotation; an empty-text run drops (it carries no geometry). A fully absent list collapses to null so a text-free
    // creation round-trips byte-identically (the member is JsonIgnore-when-null too).
    private static List<TextRunDocument>? NormalizeTextRuns(IReadOnlyList<TextRunDocument>? textRuns) {
        if (textRuns is not { Count: > 0 } source) {
            return null;
        }

        var normalized = new List<TextRunDocument>(capacity: source.Count);

        foreach (var run in source) {
            if (run.Text is not { Length: > 0 } text) {
                continue;
            }

            normalized.Add(item: run with {
                Depth = MathF.Max(x: (run.Depth ?? DefaultTextDepth), y: MinTextDepth),
                EmHeight = MathF.Max(x: run.EmHeight, y: MinTextEmHeight),
                Material = Math.Clamp(value: (run.Material ?? 0), max: (CreationDocument.PaletteSize - 1), min: 0),
                Mode = (string.Equals(a: run.Mode, b: TextRunDocument.ModeEngrave, comparisonType: StringComparison.OrdinalIgnoreCase) ? TextRunDocument.ModeEngrave : TextRunDocument.ModeEmboss),
                Rotation = ((run.Rotation == default) ? Quaternion.Identity : Quaternion.Normalize(value: run.Rotation)),
                Text = text,
            });
        }

        return ((normalized.Count > 0) ? normalized : null);
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
                Focus = (((camera.Focus is { } focus) && float.IsFinite(f: focus)) ? (float?)MathF.Max(x: focus, y: 0.01f) : null),
                Fov = (((camera.Fov is { } fov) && float.IsFinite(f: fov)) ? (float?)Math.Clamp(value: fov, max: 170f, min: 1f) : null),
                Pitch = (((camera.Pitch is { } pitch) && float.IsFinite(f: pitch)) ? (float?)Math.Clamp(value: pitch, max: 85f, min: -85f) : null),
                Yaw = (((camera.Yaw is { } yaw) && float.IsFinite(f: yaw)) ? (float?)yaw : null),
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
    private static string PathFor(string name, string creationsRoot) =>
        Path.Combine(path1: creationsRoot, path2: $"{Sanitize(name: name)}.creation.json");
    private static string Sanitize(string name) {
        var builder = new System.Text.StringBuilder(capacity: name.Length);

        foreach (var character in name) {
            _ = builder.Append(value: ((char.IsAsciiLetterOrDigit(c: character) || (character is '-' or '_')) ? character : '-'));
        }

        return ((builder.Length > 0) ? builder.ToString() : "creation");
    }
}
