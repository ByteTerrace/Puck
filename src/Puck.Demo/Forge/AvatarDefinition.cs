using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// The primitives a player can place in creator mode — the SAME set the creator's ghost cycles through, in the same
/// order (the wire value IS the cycle index, so an avatar authored in the creator round-trips through the forge without
/// a mapping table). KEEP the order in lockstep with the creator's own primitive cycle.
/// </summary>
public enum AvatarPrimitive {
    Sphere,
    Box,
    Torus,
    Cylinder,
    Capsule,
    Ellipsoid,
    // A tapered capsule (a fat base narrowing to a rounded tip along +Y) — teeth, fangs, dorsal spikes, beaks, horns,
    // a wizard hat: the "pointy" primitive the six round/boxy ones could never make. Already a builder shape
    // (SdfShapeType.RoundCone); appended LAST so every avatar authored before it keeps its wire-value cycle index.
    RoundCone,
}

/// <summary>One placed shape in a player's avatar: which primitive it draws and its full rigid + scale transform, in
/// the avatar's own local space (recentered so the avatar stands around the origin — see
/// <see cref="AvatarDefinition.FromPlacedShapes"/>).</summary>
/// <param name="Type">The primitive this shape draws.</param>
/// <param name="Position">The shape's local position.</param>
/// <param name="Rotation">The shape's orientation.</param>
/// <param name="Scale">The shape's per-axis scale.</param>
public readonly record struct AvatarShape(AvatarPrimitive Type, Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>
/// A player's in-engine creation, lifted out of the live creator scene into a self-contained, serializable value: the
/// list of placed shapes in the avatar's own local frame, plus the derived bound the forge frames its camera against.
/// This is the seam BETWEEN the two worlds — the creator produces one, the forge (spritesheet bake → ROM) consumes one
/// — so an avatar can be built live, saved to disk, and re-forged headlessly (which is exactly how the Post battery
/// exercises the pipeline without a controller). The canonical primitive dimensions live HERE so the shape the creator
/// previews and the shape the forge bakes are byte-for-byte the same geometry.
/// </summary>
public sealed record AvatarDefinition(IReadOnlyList<AvatarShape> Shapes, float BoundRadius) {
    // The canonical primitive dimensions — the ONE source of truth shared by the creator's live ghost and the forge's
    // static bake. A primitive's worst-case reach from its local origin (used to size the avatar bound); each is the
    // matching dimension below plus a small margin.
    private const float SphereRadius = 0.38f;
    private static readonly Vector3 BoxHalfExtents = new(0.34f, 0.34f, 0.34f);
    private const float BoxRound = 0.04f;
    private const float TorusMajor = 0.30f;
    private const float TorusMinor = 0.12f;
    private const float CylinderRadius = 0.30f;
    private const float CylinderHalfHeight = 0.36f;
    private static readonly Vector3 CapsuleEndpoint = new(0f, 0.55f, 0f);
    private const float CapsuleRadius = 0.20f;
    private static readonly Vector3 EllipsoidRadii = new(0.42f, 0.28f, 0.34f);
    // The round-cone runs base→tip along +Y: a stout fang/spike at unit scale (a wide rounded base tapering to a
    // small rounded tip), sized so its radial reach sits in the same band as the other primitives.
    private const float RoundConeLowerRadius = 0.22f;
    private const float RoundConeUpperRadius = 0.05f;
    private const float RoundConeHeight = 0.52f;

    private static readonly JsonSerializerOptions JsonOptions = new() {
        Converters = { new JsonStringEnumConverter() },
        // System.Numerics.Vector3/Quaternion expose X/Y/Z/W as FIELDS, not properties; without this every vector would
        // (de)serialize as empty and collapse to zero — a zero scale/quaternion is a degenerate, unrenderable shape.
        IncludeFields = true,
        WriteIndented = true,
    };

    /// <summary>A built-in starter avatar (a little standing figure), used as the initial in-memory creation before a
    /// player has authored their own — so the avatar cabinet always has something to forge, and the Post/CLI demo has a
    /// deterministic subject. Uses only the creator's own primitive set.</summary>
    /// <returns>The recentered starter avatar.</returns>
    public static AvatarDefinition Default() =>
        FromPlacedShapes(shapes: [
            new AvatarShape(Type: AvatarPrimitive.Ellipsoid, Position: new Vector3(0f, 0.62f, 0f), Rotation: Quaternion.Identity, Scale: new Vector3(1.05f, 1.25f, 0.85f)),
            new AvatarShape(Type: AvatarPrimitive.Sphere, Position: new Vector3(0f, 1.18f, 0f), Rotation: Quaternion.Identity, Scale: new Vector3(0.72f)),
            new AvatarShape(Type: AvatarPrimitive.Capsule, Position: new Vector3(-0.2f, 0.05f, 0f), Rotation: Quaternion.Identity, Scale: new Vector3(0.5f)),
            new AvatarShape(Type: AvatarPrimitive.Capsule, Position: new Vector3(0.2f, 0.05f, 0f), Rotation: Quaternion.Identity, Scale: new Vector3(0.5f)),
        ]);

    /// <summary>Lifts the placed creator shapes (in room/world space) into an avatar in its own local frame: the shapes
    /// are recentered so the avatar's horizontal centroid sits at the origin and its lowest point rests on y = 0 (a
    /// consistent frame for the forge's orbit camera, independent of where in the room the player built it), and the
    /// bound the camera fits against is derived from the recentered extent.</summary>
    /// <param name="shapes">The placed shapes in world space (type, position, rotation, scale).</param>
    /// <returns>The recentered, self-contained avatar; empty shapes yield a single unit sphere so the forge never
    /// renders an empty frame.</returns>
    public static AvatarDefinition FromPlacedShapes(IReadOnlyList<AvatarShape> shapes) {
        ArgumentNullException.ThrowIfNull(shapes);

        if (shapes.Count == 0) {
            return new AvatarDefinition(Shapes: [new AvatarShape(Type: AvatarPrimitive.Sphere, Position: Vector3.Zero, Rotation: Quaternion.Identity, Scale: Vector3.One)], BoundRadius: SphereRadius);
        }

        // Horizontal centroid (X/Z) and the lowest reached point (Y) — the recenter offset stands the avatar on the
        // origin plane so every avatar frames the same way regardless of where it was built.
        var centroid = Vector3.Zero;
        var lowestY = float.MaxValue;

        foreach (var shape in shapes) {
            centroid += shape.Position;
            lowestY = MathF.Min(lowestY, (shape.Position.Y - LocalReach(type: shape.Type, scale: shape.Scale)));
        }

        centroid /= shapes.Count;

        var offset = new Vector3(centroid.X, lowestY, centroid.Z);
        var recentered = new List<AvatarShape>(capacity: shapes.Count);
        var bound = 0f;

        foreach (var shape in shapes) {
            var local = (shape.Position - offset);

            recentered.Add(item: shape with { Position = local });
            bound = MathF.Max(bound, (local.Length() + LocalReach(type: shape.Type, scale: shape.Scale)));
        }

        return new AvatarDefinition(Shapes: recentered, BoundRadius: MathF.Max(bound, SphereRadius));
    }

    /// <summary>Bakes the avatar into a STATIC SDF program the forge renders standalone (no dynamic-transform slots —
    /// every shape's position, orientation, and scale is folded into the instruction stream). The optional
    /// <paramref name="poseOffset"/> shifts the WHOLE avatar (the forge's procedural walk cycle nudges it per frame);
    /// each shape gets a distinct hue so a multi-part avatar keeps colour variation through the 4-colour crush.</summary>
    /// <param name="poseOffset">A world-space nudge applied to every shape (the walk-cycle bob/sway); default none.</param>
    /// <returns>The baked program (backgroundless — the forge samples the empty corner as the sprite's transparent slot).</returns>
    public SdfProgram BuildProgram(Vector3 poseOffset = default) {
        var builder = new SdfProgramBuilder();

        for (var index = 0; (index < Shapes.Count); index++) {
            var shape = Shapes[index];
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: ShapeHue(index: index)));
            // The point-transform chain matches the creator's live path (a rigid Translate+Rotate then Scale), so the
            // baked shape is exactly what the ghost previewed; poseOffset rides the translate.
            var chain = builder
                .ResetPoint()
                .Translate(offset: (shape.Position + poseOffset))
                .Rotate(rotation: shape.Rotation)
                .Scale(scale: shape.Scale);

            _ = AppendPrimitive(chain: chain, type: shape.Type, material: material);
        }

        return builder.Build();
    }

    /// <summary>Emits ONE primitive's shape instruction onto an already-transformed builder chain, using the canonical
    /// dimensions. Shared by the creator's live ghost and the forge's static bake so both draw identical geometry.
    /// The blend op and smooth radius ride the shape instruction itself (zero extra words), so a composed authoring
    /// scene emits through the same seam.</summary>
    /// <param name="chain">The builder with the point transform (translate/rotate/scale or dynamic) already applied.</param>
    /// <param name="type">The primitive to emit.</param>
    /// <param name="material">The material id for the shape.</param>
    /// <param name="blend">How the shape combines with the field before it (default plain union).</param>
    /// <param name="smooth">The blend radius for the smooth variants (0 for the hard ops).</param>
    /// <returns>The builder, for chaining.</returns>
    public static SdfProgramBuilder AppendPrimitive(SdfProgramBuilder chain, AvatarPrimitive type, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        ArgumentNullException.ThrowIfNull(chain);

        return type switch {
            AvatarPrimitive.Box => chain.Box(halfExtents: BoxHalfExtents, round: BoxRound, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.Torus => chain.Torus(majorRadius: TorusMajor, minorRadius: TorusMinor, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.Cylinder => chain.Cylinder(radius: CylinderRadius, halfHeight: CylinderHalfHeight, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.Capsule => chain.Capsule(endpoint: CapsuleEndpoint, radius: CapsuleRadius, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.Ellipsoid => chain.Ellipsoid(radii: EllipsoidRadii, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.RoundCone => chain.RoundCone(lowerRadius: RoundConeLowerRadius, upperRadius: RoundConeUpperRadius, height: RoundConeHeight, material: material, blend: blend, smooth: smooth),
            _ => chain.Sphere(radius: SphereRadius, material: material, blend: blend, smooth: smooth),
        };
    }

    /// <summary>Serializes the avatar to indented JSON (enums as names) so a creation can be saved beside its forged
    /// ROM and re-forged headlessly.</summary>
    public string ToJson() => JsonSerializer.Serialize(value: this, options: JsonOptions);

    /// <summary>Reparses an avatar from <see cref="ToJson"/> output and RE-NORMALIZES it through
    /// <see cref="FromPlacedShapes"/> (recenter + re-derive the bound). This is idempotent for an avatar that was saved
    /// already-recentered (centroid ≈ 0, feet ≈ 0 → a no-op offset), and makes an externally hand-authored avatar frame
    /// correctly too — so the persisted <see cref="BoundRadius"/> is advisory, never trusted blindly.</summary>
    /// <param name="json">The serialized avatar.</param>
    /// <returns>The parsed, normalized avatar.</returns>
    /// <exception cref="ArgumentException">The JSON does not parse into an avatar with at least one shape.</exception>
    public static AvatarDefinition FromJson(string json) {
        ArgumentException.ThrowIfNullOrEmpty(json);

        var parsed = JsonSerializer.Deserialize<AvatarDefinition>(json: json, options: JsonOptions)
            ?? throw new ArgumentException(message: "The avatar JSON was null.", paramName: nameof(json));

        if ((parsed.Shapes is null) || (parsed.Shapes.Count == 0)) {
            throw new ArgumentException(message: "The avatar JSON declared no shapes.", paramName: nameof(json));
        }

        return FromPlacedShapes(shapes: parsed.Shapes);
    }

    /// <summary>A primitive's worst-case reach from its local origin at a given scale — the largest scale component
    /// times the primitive's farthest surface point (the canonical dimension table). Shared with the bake planner's
    /// recenter/framing math so a bake camera fits exactly the geometry the forge renders.</summary>
    /// <param name="type">The primitive.</param>
    /// <param name="scale">The shape's per-axis scale.</param>
    /// <returns>The reach in local units.</returns>
    public static float Reach(AvatarPrimitive type, Vector3 scale) =>
        LocalReach(scale: scale, type: type);

    /// <summary>A primitive's canonical axis-aligned half-extents at UNIT scale (the tight per-axis counterpart of
    /// <see cref="Reach"/> — a flattened ellipsoid or a thin torus frames far tighter per axis than its radial
    /// worst case). The bake planner scales these, rotates the box, and fits its camera to the result.</summary>
    /// <param name="type">The primitive.</param>
    /// <returns>The local half-extents.</returns>
    public static Vector3 AxisExtents(AvatarPrimitive type) =>
        (type switch {
            AvatarPrimitive.Box => (BoxHalfExtents + new Vector3(BoxRound)),
            AvatarPrimitive.Torus => new Vector3((TorusMajor + TorusMinor), TorusMinor, (TorusMajor + TorusMinor)),
            AvatarPrimitive.Cylinder => new Vector3(CylinderRadius, CylinderHalfHeight, CylinderRadius),
            AvatarPrimitive.Capsule => new Vector3(CapsuleRadius, (CapsuleEndpoint.Y + CapsuleRadius), CapsuleRadius),
            AvatarPrimitive.Ellipsoid => EllipsoidRadii,
            AvatarPrimitive.RoundCone => new Vector3(RoundConeLowerRadius, (RoundConeHeight + RoundConeUpperRadius), RoundConeLowerRadius),
            _ => new Vector3(SphereRadius),
        });

    // A primitive's worst-case reach from its local origin at a given scale — the largest scale component times the
    // primitive's farthest surface point, used to size the avatar bound and the recenter baseline.
    private static float LocalReach(AvatarPrimitive type, Vector3 scale) {
        var maxScale = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
        var reach = type switch {
            AvatarPrimitive.Box => (BoxHalfExtents.Length() + BoxRound),
            AvatarPrimitive.Torus => (TorusMajor + TorusMinor),
            AvatarPrimitive.Cylinder => MathF.Sqrt((CylinderRadius * CylinderRadius) + (CylinderHalfHeight * CylinderHalfHeight)),
            AvatarPrimitive.Capsule => (CapsuleEndpoint.Length() + CapsuleRadius),
            AvatarPrimitive.Ellipsoid => MathF.Max(EllipsoidRadii.X, MathF.Max(EllipsoidRadii.Y, EllipsoidRadii.Z)),
            // Base at the local origin, tip up +Y: the farthest surface point is the rounded tip (height + tip radius).
            AvatarPrimitive.RoundCone => (RoundConeHeight + RoundConeUpperRadius),
            _ => SphereRadius,
        };

        return (reach * maxScale);
    }

    // A deterministic per-shape hue (golden-ratio sweep), lifted so a small number of parts land well-separated on the
    // 4-colour sprite palette; matches the creator's placed-shape hue feel.
    private static Vector3 ShapeHue(int index) {
        var hue = ((index * 0.61803399f) % 1f);
        var h6 = (hue * 6f);
        var x = (1f - MathF.Abs(((h6 % 2f) - 1f)));
        var (r, g, b) = ((int)h6 switch {
            0 => (1f, x, 0f),
            1 => (x, 1f, 0f),
            2 => (0f, 1f, x),
            3 => (0f, x, 1f),
            4 => (x, 0f, 1f),
            _ => (1f, 0f, x),
        });

        return new Vector3((0.30f + (0.6f * r)), (0.30f + (0.6f * g)), (0.30f + (0.6f * b)));
    }
}
