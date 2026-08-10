using System.Numerics;
using System.Text.Json;
using Puck.SdfVm;

namespace Puck.Forge.Authoring;

/// <summary>One palette entry on the wire (mirrors <see cref="SdfMaterial"/> with document-doctrine nullability).</summary>
/// <param name="Albedo">The base color.</param>
/// <param name="Emissive">The emissive strength (null = 0 — optional members are nullable and normalized at load).</param>
/// <param name="Specular">The specular strength (null = the material default).</param>
/// <param name="Shininess">The specular exponent (null = the material default).</param>
public sealed record PaletteEntryDocument(Vector3 Albedo, float? Emissive, float? Specular, float? Shininess);

/// <summary>The persisted form of a placed shape (see <see cref="SculptShape"/>).</summary>
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
    /// <summary>The largest twist rate, in radians per unit of local Y (not an isometry, so this stays moderate —
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

/// <summary>One authored entity-part identity exported by a creation look.</summary>
/// <param name="Id">The ordinal, case-sensitive identifier an entity-part anchor names.</param>
/// <param name="ShapeId">The stable <see cref="ShapeDocument.Id"/> whose dynamic pose the part publishes.</param>
public sealed record CreationPartDocument(string Id, int ShapeId);

/// <summary>
/// One engraved/embossed text run a creation carries — a string laid onto one of the creation's own surfaces (a shop
/// facade, a marquee band), stored as text-plus-placement and expanded at world-emission time into
/// <see cref="SdfShapeType.Glyph"/> shapes via the shared font atlas + <c>Puck.Text.TextLayout</c> (never persisted
/// pre-expanded, so the run stays font-independent on the wire). The run sits on its own plane (<paramref name="Position"/>
/// centre + <paramref name="Rotation"/>, in the creation's workbench space: local +X = advance, +Y = ascent, +Z = the
/// relief normal). The glyph slab straddles the host surface, so the lettering is proud (emboss / Union) or recessed
/// (engrave / Subtraction) but never coplanar — coincident zero-sets speckle (docs/sdf-wiki/text-and-glyphs.md).
/// </summary>
/// <param name="Text">The run's text (whitespace / unmapped code points advance the pen without a glyph).</param>
/// <param name="Position">The run's anchor centre on the host surface (workbench space).</param>
/// <param name="Rotation">The run plane's orientation (local +X advance, +Y ascent, +Z the relief normal).</param>
/// <param name="EmHeight">The world height of one em, in the creation's own (pre-placement) units.</param>
/// <param name="Depth">The glyph extrude half-depth — the relief the slab straddles the surface by (null = a thin default).</param>
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

    /// <summary>The number of glyph shapes this run expands to for the per-stamp shape budget — its non-whitespace
    /// character count, a conservative upper bound of the atlas's laid-out placements (which skip whitespace and
    /// unmapped code points). Whitespace-only / empty text contributes nothing. Derived (recomputed from
    /// <see cref="Text"/>), so it is kept off the wire.</summary>
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

/// <summary>One IK chain on the wire (see <see cref="SculptChain"/>'s live definition — rest geometry is
/// re-derived from the member shapes' current positions at load time, never persisted, so a loaded chain always
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

/// <summary>One timeline frame on the wire: a named full snapshot of every shape's transform. Part of the
/// hold-style animation timeline — authored frames replayed with no interpolation.</summary>
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
/// One camera eye a creation carries — a posed viewpoint anchored to one of the creation's own shapes (a
/// <see cref="ShapeDocument.Id"/>), so the eye rides that shape's live pose through IK/animation frames. This is the
/// creation-side twin of a world document's placed camera: the lantern-fish's lens dangling off its lure becomes one
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

/// <summary>One screen face a creation declares — a surface on the creation (backed by one of its shapes) that shows a
/// feed. The robot's CRT face is one of these: it shows the named host <c>emotes</c> feed by default, and is wirable to
/// any camera feed (a creation camera's named feed, another creation's feed, a world camera) purely by naming a
/// different default source. No robot-specific channel exists — a face is just a screen surface with a default wire,
/// and the wiring model does the rest.</summary>
/// <param name="Name">The face's name (a wiring handle — <c>face</c> by default).</param>
/// <param name="ShapeId">The shape whose surface is the screen (a <see cref="ShapeDocument.Id"/>; -1/null = the whole
/// creation's canonical face surface, resolved by the consumer).</param>
/// <param name="DefaultSource">The feed this face shows when nothing else is wired, as a source token a consuming world
/// resolves through a closed four-token map — <c>none</c> (no signal), <c>test</c> (the test pattern), and
/// <c>camera:&lt;name&gt;</c> / <c>feed:&lt;name&gt;</c> (a View of the named camera, resolved against the placement's
/// derived creation-eye feeds then the world's own camera rows). An unrecognized token (including a bare
/// <c>named:emotes</c>, which named a host registry no world provides) lights the no-signal card. Null = the no-signal
/// card until a world's face override wires a feed.</param>
public sealed record CreationFaceDocument(
    string Name,
    int? ShapeId,
    string? DefaultSource
);

/// <summary>One sound a creation carries — a creature/phenomenon voice as data, following <see cref="CreationFaceDocument"/>'s
/// named-wiring shape: a name (the wiring handle), an optional anchoring shape the voice emits from (null = the
/// creation's root), and the <c>puck.synth.v1</c> patch inline — creations stay portable, and the existing creation
/// hash covers the voice with no new pin machinery. A world placement of a sound-bearing creation auto-surfaces an
/// audio emitter anchored to the placement (root or the named shape); the placement row's own emission facet remains
/// the per-instance override channel.</summary>
/// <param name="Name">The sound's name (a wiring handle — <c>sound</c> by default; unique within the creation).</param>
/// <param name="ShapeId">The shape the voice emits from (a <see cref="ShapeDocument.Id"/>; null = the creation's
/// root). A sound naming a missing shape is dropped at load (the post-edit-deletion self-heal, mirroring faces).</param>
/// <param name="Patch">The voice's <c>puck.synth.v1</c> patch, inline (validated through the synth family's own
/// canonicalizer as part of creation validation).</param>
/// <param name="Level">The emitter level (null = 1 — unity).</param>
/// <param name="Radius">The audible support radius in world units (null = the consuming world's default speaker
/// radius).</param>
public sealed record CreationSoundDocument(
    string Name,
    int? ShapeId,
    SynthPatchDocument Patch,
    float? Level,
    float? Radius
) {
    /// <summary>The largest level/gain any audio-shaped document field admits — headroom above unity while every
    /// Q16 composite gain the mix path multiplies stays far inside int range. World-side gain bounds reference this
    /// same ceiling so the vocabulary cannot fork.</summary>
    public const float MaxLevel = 8f;
}

/// <summary>
/// A creation's behavior manifest — the behavioral facts a creation carries so consumers stop re-supplying them by
/// hand. A loaded fish without one walks because nothing records that it swims; this makes those facts data. Minimal
/// and normalized: a locomotion mode, the creation's declared faces (screen surfaces that show named feeds), and its
/// declared sounds (synth voices that emit from its body).
/// </summary>
/// <param name="Locomotion">How the creation moves, as a free-text token a consuming world resolves as a kit name: a
/// creation declaring <c>swim</c> inhabits the world's kit row named <c>swim</c> when a placement's inhabit facet omits
/// an explicit kit (a world declaring no such kit rejects the placement loudly, naming every kit it declares). It is not
/// a closed enum — the runtime answer to "how does it move" is the resolved <c>WorldKit.Model</c>, never this string
/// parsed per frame. Null = walk.</param>
/// <param name="Faces">The declared screen faces (null = none). A creation with a face shows a feed on its body; the
/// face's default source is pure data, wirable to any camera feed.</param>
/// <param name="Sounds">The declared sounds (null = none). Omitted from the wire when null, so a creation authored
/// without this member serializes to unchanged bytes.</param>
public sealed record CreationBehaviorDocument(
    string? Locomotion,
    IReadOnlyList<CreationFaceDocument>? Faces,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<CreationSoundDocument>? Sounds = null
);

/// <summary>
/// The <c>puck.creation.v1</c> document — an authored scene as data, the everything-as-data payoff for authoring: a
/// creation can be named, saved, reloaded, and handed to a bake/forge headlessly. Document doctrine applies
/// throughout: every optional member is declared nullable (the polymorphic parse path skips property initializers —
/// an omitted member arrives null regardless), validated only when present, and normalized at consumption (see
/// <see cref="CreationCanonicalizer"/>).
/// </summary>
/// <param name="Schema">The document version tag (<c>puck.creation.v1</c>).</param>
/// <param name="Name">The creation's handle (normalization narrows it to letters, digits, dashes, and underscores).</param>
/// <param name="Intent">The authoring intent name (null = Object).</param>
/// <param name="BakeStyle">The per-cart bake style knob (null = classic).</param>
/// <param name="Palette">The material palette (null = the default sweep).</param>
/// <param name="Shapes">The authored shapes (null = empty).</param>
/// <param name="Frames">The animation timeline frames (null = none).</param>
/// <param name="Chains">The IK rig's chains (null = none). Shapes stay flat; a chain only references shape ids.</param>
/// <param name="Cameras">The creation's anchored camera eyes (null = none). Each rides a shape and produces a named
/// feed — the lantern-fish's lure lens is one entry here.</param>
/// <param name="Behavior">The behavior manifest (null = the defaults: walks, no face). Records how the creation moves
/// and any screen faces it declares, so consumers stop re-supplying those facts by hand.</param>
/// <param name="TextRuns">The engraved/embossed text runs the creation carries (null/empty = none). Each is a string
/// laid onto a surface, expanded at emission into <see cref="SdfShapeType.Glyph"/> shapes — see
/// <see cref="TextRunDocument"/>. Omitted from the wire when null, so a creation authored without this member
/// serializes to unchanged bytes.</param>
/// <param name="Parts">The authored part identities this creation publishes when used as an entity look (null = none).
/// Each maps a stable identifier to one shape's dynamic transform.</param>
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
    IReadOnlyList<TextRunDocument>? TextRuns = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<CreationPartDocument>? Parts = null
) {
    /// <summary>The version tag every saved document carries.</summary>
    public const string CurrentSchema = "puck.creation.v1";

    /// <summary>The material palette's slot count — <see cref="ShapeDocument.Material"/>/<see cref="TextRunDocument.Material"/>
    /// clamp into <c>[0, PaletteSize)</c> at normalization.</summary>
    public const int PaletteSize = 16;

    /// <summary>Unknown sections preserved across a round-trip — the data-side plugin extensibility posture. Null
    /// when the document carries no unknown members. A settable (not <c>init</c>)
    /// accessor is required: System.Text.Json appends to it during deserialization.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>The creation's total per-stamp shape budget: its authored shapes plus every text run's expanded glyph
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
