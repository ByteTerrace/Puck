using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring.Sculpting;

/// <summary>One side of a <see cref="CreationBuilder.Mirror"/> pair: a sign for the shared authoring expression and
/// the name suffix the mirrored shape carries.</summary>
/// <param name="Sign">+1 for <see cref="CreationBuilder.Left"/>, -1 for <see cref="CreationBuilder.Right"/>.</param>
/// <param name="Suffix">"L" or "R".</param>
public readonly record struct SculptSide(float Sign, string Suffix);

/// <summary>A named point in the creation's author frame — a shoulder, a hip, a wing hinge — that
/// <see cref="StateHoisting"/> matches an authored swing <c>Pivot</c> against by value.</summary>
/// <param name="Name">The joint's name (the cell key under the joints row <see cref="StateHoisting.Options.JointsRow"/>).</param>
/// <param name="Position">The joint's position, in the creation's author frame.</param>
public readonly record struct SculptJoint(string Name, Vector3 Position);

/// <summary>One shape under construction. Mutable (fields, not a record) so a caller can adjust a just-authored
/// shape the way <c>shapes[-1][...] = ...</c> does in the Python original — <see cref="CreationBuilder.Shape"/>
/// hands back the same instance it stored.</summary>
public sealed class SculptShape {
    /// <summary>The stable id.</summary>
    public int Id;
    /// <summary>The shape's own name — the handle every other authoring surface (parents, chains, frames,
    /// swings-carry-forward, effectors) addresses it by.</summary>
    public required string Name;
    /// <summary>The primitive.</summary>
    public SdfSolidPrimitive Type;
    /// <summary>The position in the creation's author frame.</summary>
    public Vector3 Position;
    /// <summary>The orientation — always a value <see cref="CreationBuilder"/>'s rotation helpers produced, so
    /// <see cref="StateHoisting"/> can find its interned name.</summary>
    public Quaternion Rotation;
    /// <summary>The per-axis scale.</summary>
    public Vector3 Scale;
    /// <summary>The palette slot.</summary>
    public int? Material;
    /// <summary>The blend op (null = Union).</summary>
    public SdfBlendOp? Blend;
    /// <summary>The smooth-blend radius (null = 0).</summary>
    public float? Smooth;
    /// <summary>The composition group (null = ungrouped).</summary>
    public int? Group;
    /// <summary>The parent shape's <see cref="Name"/> (null = the creation root). Must already be declared.</summary>
    public string? Parent;
    /// <summary>Only for Prism: top/bottom width ratio.</summary>
    public float? Taper;
    /// <summary>Only for Prism: the cross-section profile.</summary>
    public SdfPrismProfile? Profile;
    /// <summary>Only for Prism, Cylinder, Cone: the edge-rounding radius.</summary>
    public float? Rounding;
    /// <summary>The inflation radius.</summary>
    public float? Dilate;
    /// <summary>The shell thickness.</summary>
    public float? Onion;
    /// <summary>The local twist rate.</summary>
    public float? Twist;
    /// <summary>The local bend rate about Y.</summary>
    public float? Bend;
    /// <summary>Only for Box, Cylinder and (extruded) Prism: the edge-chamfer size.</summary>
    public float? Chamfer;
    /// <summary>The second-material inset face region (null = none).</summary>
    public ShapePanelDocument? Panel;
    /// <summary>The ordered domain operators (null = none) — see <see cref="ShapeDocument.Domain"/>. A shape
    /// carrying one refuses its own <see cref="Swings"/>/<see cref="Slides"/> at validation (it rides its
    /// <see cref="Parent"/> chain's rigid delta instead), so <see cref="CreationBuilder.CarryRig"/> is a no-op for
    /// it by construction.</summary>
    public IReadOnlyList<ShapeDomainOp>? Domain;
    /// <summary>The driver-fed rotations carried onto this shape — authored directly, or carried forward by name
    /// from an existing document via <see cref="CreationBuilder.CarryRig"/>.</summary>
    public IReadOnlyList<ShapeSwingDocument>? Swings;
    /// <summary>The driver-fed translations carried onto this shape — see <see cref="Swings"/>.</summary>
    public IReadOnlyList<ShapeSlideDocument>? Slides;

    /// <summary>Builds the immutable <see cref="ShapeDocument"/> this slot describes.</summary>
    public ShapeDocument ToDocument() => new(
        Bend: Bend,
        Blend: Blend,
        Chamfer: Chamfer,
        Dilate: Dilate,
        Domain: Domain,
        Group: Group,
        Id: Id,
        Joint: null,
        Lift: null,
        Material: Material,
        Name: Name,
        Onion: Onion,
        Panel: Panel,
        Parent: Parent,
        Position: new DocumentVector3(value: Position),
        Profile: Profile,
        Rotation: new DocumentQuaternion(value: Rotation),
        Rounding: Rounding,
        Scale: new DocumentVector3(value: Scale),
        Slides: Slides,
        Smooth: Smooth,
        Swings: Swings,
        Taper: Taper,
        Twist: Twist,
        Type: Type
    );
}

/// <summary>
/// A fluent, allocation-light builder over <see cref="CreationDocument"/>: palette entries by name, shapes with
/// every <see cref="ShapeDocument"/> field, mirrored (L/R) authoring, parent-chained bead sequences, quaternion
/// interning (so two shapes sharing one authored rotation share one <see cref="Quaternion"/> instance
/// <see cref="StateHoisting"/> can name once), fixed ids for shapes a world's looks/parts reference by id, and a
/// topological check (parents declared before children, ids unique) enforced as each shape is authored rather than
/// deferred to a final pass.
/// </summary>
/// <remarks>
/// Scale interning is deliberately NOT here — <see cref="StateHoisting"/> derives shared-scale names from usage
/// after every shape exists (mirroring the Python original's post-pass), since a scale's name is bookkeeping with no
/// authoring-time meaning the way a rotation's ("z180") has. Sections outside a single creation document — the
/// world's <c>render</c>, <c>rules</c>, <c>state</c>, <c>cameras</c>, <c>views</c> — are Schema-owned shapes this
/// project cannot reference; a sculpt builds those as raw <see cref="System.Text.Json.Nodes.JsonNode"/> and carries
/// them through <see cref="SculptPatch"/> instead.
/// </remarks>
public sealed class CreationBuilder {
    private readonly Dictionary<string, int> m_fixedIds = new(comparer: StringComparer.Ordinal);
    private readonly List<PaletteEntryDocument> m_palette = [];
    private readonly Dictionary<string, int> m_paletteSlotByName = new(comparer: StringComparer.Ordinal);
    private readonly List<(string Name, Quaternion Value)> m_rotations = [];
    private readonly Dictionary<Quaternion, string> m_rotationNameByRounded = new();
    private readonly List<SculptShape> m_shapes = [];
    private readonly Dictionary<string, int> m_shapeIndexByName = new(comparer: StringComparer.Ordinal);
    private int m_nextId = 100;

    /// <summary>Initializes a new builder for a creation named <paramref name="name"/>.</summary>
    public CreationBuilder(string name) {
        Name = name;
        Identity = InternRotation(
            name: "identity",
            value: Quaternion.Identity
        );
    }

    /// <summary>The mirror side carrying sign +1 and suffix "L".</summary>
    public static SculptSide Left => new(Sign: 1f, Suffix: "L");
    /// <summary>The mirror side carrying sign -1 and suffix "R".</summary>
    public static SculptSide Right => new(Sign: -1f, Suffix: "R");

    /// <summary>The declared frames, keyed by name — populate directly (a sculpt authors these as plain
    /// <see cref="FrameDocument"/> values, or through <see cref="Frame"/>).</summary>
    public List<FrameDocument> Frames { get; } = [];
    /// <summary>The declared cameras.</summary>
    public List<CreationCameraDocument> Cameras { get; } = [];
    /// <summary>The declared IK chains.</summary>
    public List<ChainDocument> Chains { get; } = [];
    /// <summary>The declared animation drivers.</summary>
    public List<CreationDriverDocument> Drivers { get; } = [];
    /// <summary>The declared effectors.</summary>
    public List<CreationEffectorDocument> Effectors { get; } = [];
    /// <summary>The declared entity-part identities.</summary>
    public List<CreationPartDocument> Parts { get; } = [];
    /// <summary>The behavior manifest (null = defaults).</summary>
    public CreationBehaviorDocument? Behavior { get; set; }
    /// <summary>The creation's name.</summary>
    public string Name { get; }
    /// <summary>The identity rotation (named "identity") — every shape's default when no rotation is authored.</summary>
    public Quaternion Identity { get; }
    /// <summary>Every distinct rotation interned so far, in first-authored order.</summary>
    public IReadOnlyList<(string Name, Quaternion Value)> NamedRotations => m_rotations;
    /// <summary>The shapes authored so far, in declaration order.</summary>
    public IReadOnlyList<SculptShape> Shapes => m_shapes;

    /// <summary>Registers a fixed id a shape named <paramref name="name"/> must carry — the world's looks/parts
    /// reference this id directly, so it must survive a re-generation unchanged.</summary>
    /// <param name="name">The shape's name.</param>
    /// <param name="id">The fixed id.</param>
    public CreationBuilder FixedId(string name, int id) {
        m_fixedIds[name] = id;

        return this;
    }
    /// <summary>Adds a palette entry, returning its slot index.</summary>
    /// <param name="name">The entry's authoring-time name (a lookup handle — not persisted).</param>
    /// <param name="entry">The entry.</param>
    public int Palette(string name, PaletteEntryDocument entry) {
        var slot = m_palette.Count;

        m_palette.Add(item: entry);
        m_paletteSlotByName[name] = slot;

        return slot;
    }
    /// <summary>Resolves a palette entry's slot by its authoring-time name.</summary>
    public int PaletteSlot(string name) => m_paletteSlotByName[name];

    /// <summary>Builds (or reuses) the quaternion for a right-handed rotation of <paramref name="degrees"/> about
    /// <paramref name="axis"/>, naming it by axis letter and magnitude (e.g. "z180", "xm90" for -90 about X) unless
    /// <paramref name="explicitName"/> is given. Two calls producing the same rotation (within 4-decimal rounding,
    /// matching the interning key every consumer compares against) return the SAME named value.</summary>
    /// <param name="axis">The rotation axis (need not be normalized).</param>
    /// <param name="degrees">The signed angle in degrees.</param>
    /// <param name="explicitName">An override name; null derives one.</param>
    public Quaternion AxisAngleDegrees(Vector3 axis, float degrees, string? explicitName = null) {
        var normalized = Vector3.Normalize(value: axis);
        var value = RoundQuaternion(value: Quaternion.CreateFromAxisAngle(
            axis: normalized,
            angle: (degrees * (MathF.PI / 180f))
        ));
        var name = (explicitName ?? DeriveAxisName(
            normalizedAxis: normalized,
            degrees: degrees
        ));

        return InternRotation(
            name: name,
            value: value
        );
    }
    /// <summary>Builds (or reuses) the quaternion product <c>a * b</c>, naming it <c>"{nameOf(a)}-{nameOf(b)}"</c>
    /// unless <paramref name="explicitName"/> is given. Both operands must already be interned (returned by
    /// <see cref="Identity"/>, <see cref="AxisAngleDegrees"/>, or a previous <see cref="Multiply"/> call).</summary>
    public Quaternion Multiply(Quaternion a, Quaternion b, string? explicitName = null) {
        if (!TryRotationName(
            name: out var nameA,
            value: a
        )) {
            throw new ArgumentException(message: "the left operand is not an interned rotation — build it through AxisAngleDegrees/Multiply first.", paramName: nameof(a));
        }

        if (!TryRotationName(
            name: out var nameB,
            value: b
        )) {
            throw new ArgumentException(message: "the right operand is not an interned rotation — build it through AxisAngleDegrees/Multiply first.", paramName: nameof(b));
        }

        var value = RoundQuaternion(value: (a * b));
        var name = (explicitName ?? $"{nameA}-{nameB}");

        return InternRotation(
            name: name,
            value: value
        );
    }
    /// <summary>Resolves an interned rotation's name.</summary>
    /// <param name="value">The rotation (rounded to 4 decimals for the lookup, matching the interning key).</param>
    /// <param name="name">The name, on success.</param>
    public bool TryRotationName(Quaternion value, out string name) => m_rotationNameByRounded.TryGetValue(
        key: RoundQuaternion(value: value),
        value: out name!
    );

    /// <summary>Runs <paramref name="both"/> once for <see cref="Left"/> then once for <see cref="Right"/> — the
    /// L/R pair-emission helper: <c>builder.Mirror(side => builder.Shape($"arm{side.Suffix}", ..., side.Sign * x, ...))</c>.</summary>
    public void Mirror(Action<SculptSide> both) {
        both(obj: Left);
        both(obj: Right);
    }

    /// <summary>A single-entry domain list folding the shape across the local YZ plane (creation-space X = <paramref
    /// name="offset"/>) — the L/R-pair-to-one-shape primitive: author the <see cref="Left"/>-side half once and let
    /// the fold produce its mirror image, in place of a <see cref="Mirror"/> pair.</summary>
    public static IReadOnlyList<ShapeDomainOp> SymmetryX(float offset = 0f) => [new ShapeDomainOp.Symmetry(Normal: new DocumentVector3(value: Vector3.UnitX), Offset: offset)];

    /// <summary>Authors one shape. The id is the registered <see cref="FixedId"/> for <paramref name="name"/> when
    /// one exists, else an auto-incrementing id starting at 100. <paramref name="rotation"/> defaults to
    /// <see cref="Identity"/> and MUST be a value this builder interned (see <see cref="AxisAngleDegrees"/>) — every
    /// shape's rotation is hoistable by construction. Refuses (throws) a duplicate name, a duplicate id, or a
    /// <paramref name="parent"/> not already declared — the topological check runs at author time, so a chain can
    /// never cycle and always resolves in one pass.</summary>
    public SculptShape Shape(
        string name,
        SdfSolidPrimitive type,
        Vector3 position,
        Vector3 scale,
        int? material,
        Quaternion? rotation = null,
        SdfBlendOp? blend = null,
        float? smooth = null,
        int? group = null,
        string? parent = null,
        float? taper = null,
        SdfPrismProfile? profile = null,
        float? rounding = null,
        float? dilate = null,
        float? onion = null,
        float? twist = null,
        float? bend = null,
        float? chamfer = null,
        ShapePanelDocument? panel = null,
        IReadOnlyList<ShapeDomainOp>? domain = null,
        int? id = null
    ) {
        if (m_shapeIndexByName.ContainsKey(key: name)) {
            throw new ArgumentException(message: $"a shape named '{name}' is already declared.", paramName: nameof(name));
        }

        if ((parent is { Length: > 0 }) && !m_shapeIndexByName.ContainsKey(key: parent)) {
            throw new ArgumentException(message: $"shape '{name}' names parent '{parent}', which is not yet declared — declare parents before children.", paramName: nameof(parent));
        }

        var resolvedRotation = (rotation ?? Identity);

        if (!TryRotationName(
            name: out _,
            value: resolvedRotation
        )) {
            throw new ArgumentException(message: $"shape '{name}''s rotation is not an interned value — build it through AxisAngleDegrees/Multiply/Identity first.", paramName: nameof(rotation));
        }

        var resolvedId = (id ?? (m_fixedIds.TryGetValue(
            key: name,
            value: out var fixedId
        )
            ? fixedId
            : m_nextId++));

        if (m_shapes.Any(predicate: existing => (existing.Id == resolvedId))) {
            throw new ArgumentException(message: $"shape '{name}' reuses id {resolvedId}, already assigned to '{m_shapes.First(s => (s.Id == resolvedId)).Name}'.", paramName: nameof(id));
        }

        var slot = new SculptShape {
            Bend = bend,
            // Written explicitly rather than left null: the canonical wire form always carries a blend op and a
            // smooth radius (the two fields every blend touches), while every other optional field below is
            // genuinely omitted when unauthored.
            Blend = (blend ?? SdfBlendOp.Union),
            Chamfer = chamfer,
            Dilate = dilate,
            Domain = domain,
            Group = group,
            Id = resolvedId,
            Material = material,
            Name = name,
            Onion = onion,
            Panel = panel,
            Parent = parent,
            // Rounded to 4 decimals at authoring time (mirroring the Python original's round(v, 4)) so two shapes
            // sharing one scale round-trip through StateHoisting's grouping key identically however they were typed.
            Position = Round4(value: position),
            Profile = profile,
            Rotation = resolvedRotation,
            Rounding = rounding,
            Scale = Round4(value: scale),
            Smooth = (smooth ?? 0f),
            Taper = taper,
            Twist = twist,
            Type = type,
        };

        m_shapeIndexByName[name] = m_shapes.Count;
        m_shapes.Add(item: slot);

        return slot;
    }
    /// <summary>Looks up a previously authored shape by name.</summary>
    public SculptShape Find(string name) => m_shapes[m_shapeIndexByName[name]];
    /// <summary>Looks up a previously authored shape by name, or null.</summary>
    public SculptShape? TryFind(string name) => (m_shapeIndexByName.TryGetValue(
        key: name,
        value: out var index
    )
        ? m_shapes[index]
        : null);

    /// <summary>Authors a chain of <paramref name="count"/> shapes, each parented to the previous (the first to
    /// <paramref name="firstParent"/>) — the bead/segment sequence primitive (a braid, a tail, a tentacle).</summary>
    /// <param name="count">How many shapes to author.</param>
    /// <param name="firstParent">The name the first shape parents to.</param>
    /// <param name="type">Every bead's primitive.</param>
    /// <param name="material">Every bead's palette slot.</param>
    /// <param name="nameOf">Maps a zero-based bead index to its name.</param>
    /// <param name="positionOf">Maps a zero-based bead index to its position.</param>
    /// <param name="scaleOf">Maps a zero-based bead index to its scale.</param>
    /// <param name="group">Every bead's composition group.</param>
    /// <param name="blend">Every bead's blend op.</param>
    /// <param name="smooth">Every bead's smooth-blend radius.</param>
    /// <returns>The chain's shape names, root to tip.</returns>
    public IReadOnlyList<string> Chain(
        int count,
        string firstParent,
        SdfSolidPrimitive type,
        int? material,
        Func<int, string> nameOf,
        Func<int, Vector3> positionOf,
        Func<int, Vector3> scaleOf,
        int? group = null,
        SdfBlendOp? blend = null,
        float? smooth = null
    ) {
        var names = new List<string>(capacity: count);
        var parent = firstParent;

        for (var index = 0; (index < count); index++) {
            var name = nameOf(arg: index);

            Shape(
                blend: blend,
                group: group,
                material: material,
                name: name,
                parent: parent,
                position: positionOf(arg: index),
                scale: scaleOf(arg: index),
                smooth: smooth,
                type: type
            );
            names.Add(item: name);
            parent = name;
        }

        return names;
    }

    /// <summary>Authors one <see cref="FrameDocument"/> from a snapshot of named shapes' CURRENT transforms, with
    /// per-shape scale overrides — the blink-frame primitive (collapse an eye's Y scale to read closed).</summary>
    /// <param name="name">The frame's name.</param>
    /// <param name="scaleOverrides">Each entry's shape (by name) and the scale it carries in this frame; a shape
    /// named here must already be declared.</param>
    public FrameDocument Frame(string name, IEnumerable<(string ShapeName, Vector3 Scale)> scaleOverrides) {
        var transforms = new List<FrameTransformDocument>();

        foreach (var (shapeName, scale) in scaleOverrides) {
            var shape = Find(name: shapeName);

            transforms.Add(item: new FrameTransformDocument(
                Id: shape.Id,
                Position: new DocumentVector3(value: shape.Position),
                Rotation: new DocumentQuaternion(value: shape.Rotation),
                Scale: new DocumentVector3(value: scale)
            ));
        }

        var frame = new FrameDocument(
            Name: name,
            Transforms: transforms
        );

        Frames.Add(item: frame);

        return frame;
    }

    /// <summary>Carries a shape's <c>swings</c>/<c>slides</c> forward BY NAME from an existing creation document —
    /// the rig-preservation primitive every re-generation needs (a re-authored shape keeps its hand-tuned motion).
    /// A no-op when <paramref name="source"/> declares no shape of this name, or the named shape carries neither
    /// facet.</summary>
    /// <param name="target">The freshly authored shape to carry rig data onto.</param>
    /// <param name="source">The existing creation to read the shape's current <c>swings</c>/<c>slides</c> from.</param>
    public static void CarryRig(SculptShape target, CreationDocument source) {
        var match = source.Shapes?.FirstOrDefault(predicate: shape => string.Equals(
            a: shape.Name?.Value,
            b: target.Name,
            comparisonType: StringComparison.Ordinal
        ));

        if (match is null) {
            return;
        }

        target.Swings = match.Swings;
        target.Slides = match.Slides;
    }

    /// <summary>Runs the topological check (every declared parent precedes its child, every id is unique) as a
    /// stand-alone assertion — redundant with the per-<see cref="Shape"/> check, kept for callers that build shapes
    /// through a path other than this builder and want the same guarantee proved once.</summary>
    /// <param name="reason">The refusal, on failure.</param>
    public bool TryValidateTopology(out string reason) {
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var ids = new HashSet<int>();

        foreach (var shape in m_shapes) {
            if (!ids.Add(item: shape.Id)) {
                reason = $"shape '{shape.Name}' reuses id {shape.Id}.";

                return false;
            }

            if ((shape.Parent is { Length: > 0 } parent) && !seen.Contains(item: parent)) {
                reason = $"shape '{shape.Name}' names parent '{parent}', not declared before it.";

                return false;
            }

            seen.Add(item: shape.Name);
        }

        reason = string.Empty;

        return true;
    }

    /// <summary>Assembles the built <see cref="CreationDocument"/>. Rotations/scales are still LITERAL at this
    /// point — run <see cref="StateHoisting.Apply"/> over the result to bind shared values to state cells.</summary>
    public CreationDocument Build() {
        if (!TryValidateTopology(reason: out var reason)) {
            throw new InvalidOperationException(message: reason);
        }

        return new CreationDocument(
            Behavior: Behavior,
            Cameras: ((Cameras.Count > 0) ? Cameras : null),
            Chains: ((Chains.Count > 0) ? Chains : null),
            Drivers: ((Drivers.Count > 0) ? Drivers : null),
            Effectors: ((Effectors.Count > 0) ? Effectors : null),
            Frames: ((Frames.Count > 0) ? Frames : null),
            Name: Name,
            Palette: ((m_palette.Count > 0) ? m_palette : null),
            Parts: ((Parts.Count > 0) ? Parts : null),
            Schema: CreationDocument.CurrentSchema,
            Shapes: [.. m_shapes.Select(selector: shape => shape.ToDocument())]
        );
    }

    private Quaternion InternRotation(string name, Quaternion value) {
        if (m_rotationNameByRounded.TryAdd(
            key: value,
            value: name
        )) {
            m_rotations.Add(item: (name, value));
        }

        return value;
    }
    private static string DeriveAxisName(Vector3 normalizedAxis, float degrees) {
        var letter = ((RoundToInt(value: normalizedAxis.X), RoundToInt(value: normalizedAxis.Y), RoundToInt(value: normalizedAxis.Z)) switch {
            (1, 0, 0) => "x",
            (0, 1, 0) => "y",
            (0, 0, 1) => "z",
            _ => "a",
        });
        var magnitude = MathF.Abs(x: degrees);
        var magnitudeText = ((magnitude == MathF.Floor(x: magnitude))
            ? ((long)magnitude).ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
            : magnitude.ToString(format: "G", provider: System.Globalization.CultureInfo.InvariantCulture)
        ).Replace(oldChar: '.', newChar: 'p');

        return $"{letter}{((degrees < 0f) ? "m" : string.Empty)}{magnitudeText}";
    }
    private static int RoundToInt(float value) => (int)MathF.Round(x: value, mode: MidpointRounding.AwayFromZero);
    private static Quaternion RoundQuaternion(Quaternion value) => new(
        w: Round4(value: value.W),
        x: Round4(value: value.X),
        y: Round4(value: value.Y),
        z: Round4(value: value.Z)
    );
    /// <summary>Rounds every component to 4 decimals — the shared authoring-precision convention every position and
    /// scale in this creation is normalized to, so two shapes sharing one authored value compare equal exactly.</summary>
    public static Vector3 Round4(Vector3 value) => new(
        x: Round4(value: value.X),
        y: Round4(value: value.Y),
        z: Round4(value: value.Z)
    );
    private static float Round4(float value) => (MathF.Round(x: value, digits: 4, mode: MidpointRounding.AwayFromZero) + 0f);
}
