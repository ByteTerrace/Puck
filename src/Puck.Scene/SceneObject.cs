using System.Text.Json.Serialization;
using Puck.SdfVm;

namespace Puck.Scene;

/// <summary>
/// One placed primitive in the scene: a transform <c>ops</c> chain followed by a terminal shape, melded into the
/// field with a blend operator. The form is shape-grouped — the <c>shape</c> string is the JSON type discriminator and
/// each derived record carries exactly that shape's named parameters — so it mirrors the public verbs of
/// <see cref="SdfProgramBuilder"/> rather than the raw packed instruction lanes. Adding a shape is a new derived
/// record (carrying its own <see cref="EmitShape"/> and <see cref="ValidateShape"/>), never an edit to a switch.
/// </summary>
[JsonDerivedType(typeof(SphereObject), typeDiscriminator: "sphere")]
[JsonDerivedType(typeof(BoxObject), typeDiscriminator: "box")]
[JsonDerivedType(typeof(TorusObject), typeDiscriminator: "torus")]
[JsonDerivedType(typeof(PlaneObject), typeDiscriminator: "plane")]
[JsonDerivedType(typeof(RoundConeObject), typeDiscriminator: "roundCone")]
[JsonDerivedType(typeof(CapsuleObject), typeDiscriminator: "capsule")]
[JsonDerivedType(typeof(CylinderObject), typeDiscriminator: "cylinder")]
[JsonDerivedType(typeof(EllipsoidObject), typeDiscriminator: "ellipsoid")]
[JsonDerivedType(typeof(ScreenSlabObject), typeDiscriminator: "screenSlab")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "shape")]
public abstract record SceneObject {
    /// <summary>The transform chain applied to the point before the shape is evaluated, in order.</summary>
    public IReadOnlyList<TransformOp> Ops { get; init; } = [];
    /// <summary>The id (index into <c>scene.materials</c>) of this object's surface material. Ignored by
    /// <see cref="ScreenSlabObject"/>, which always uses the screen-material sentinel.</summary>
    public int Material { get; init; }
    /// <summary>How this object melds into the accumulated field.</summary>
    public SdfBlendOp Blend { get; init; } = SdfBlendOp.Union;
    /// <summary>The smooth-blend radius, used only when <see cref="Blend"/> is one of the smooth ops
    /// (<see cref="SdfBlendOp.SmoothUnion"/>/<see cref="SdfBlendOp.SmoothIntersection"/>/<see cref="SdfBlendOp.SmoothSubtraction"/>).</summary>
    public float Smooth { get; init; }
    /// <summary>An inflation radius applied AFTER this object melds in — and, like the FIELD op it is, it fattens the
    /// ENTIRE field accumulated so far (every earlier object too), exactly like the source VM. Order objects
    /// accordingly. 0 (the default) = off.</summary>
    public float Dilate { get; init; }
    /// <summary>A shell half-thickness applied AFTER this object melds in (after <see cref="Dilate"/>) — a FIELD op:
    /// it hollows the ENTIRE field accumulated so far into a skin, every earlier object included. Order objects
    /// accordingly. 0 (the default) = off.</summary>
    public float Onion { get; init; }

    // Resets the point, applies every op in order, then emits the terminal shape — the exact sequence the demo's
    // hand-authored BuildScene uses per object, reproduced from data. The optional FIELD ops follow the shape in
    // fixed dilate-then-onion order (inflate first, then shell the inflated solid).
    internal void Emit(SdfProgramBuilder builder) {
        _ = builder.ResetPoint();

        // Ops is optional; an omitted array deserializes to null under source-gen, so coalesce. A null element can
        // only occur on an unvalidated document (the validator rejects them), so skip rather than throw.
        foreach (var op in (Ops ?? [])) {
            op?.Apply(builder: builder);
        }

        EmitShape(builder: builder);

        if (Dilate > 0f) {
            _ = builder.Dilate(radius: Dilate);
        }

        if (Onion > 0f) {
            _ = builder.Onion(thickness: Onion);
        }
    }
    internal void Validate(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        if (!Enum.IsDefined(value: Blend)) {
            errors.Add(path: $"{path}.blend", message: $"'{Blend}' is not a defined SdfBlendOp");
        }

        if ((Blend == SdfBlendOp.SmoothUnion) || (Blend == SdfBlendOp.SmoothIntersection) || (Blend == SdfBlendOp.SmoothSubtraction)) {
            errors.RequireRange(path: $"{path}.smooth", name: "smooth", range: bounds.Smooth, value: Smooth);
        } else {
            errors.RequireFinite(path: $"{path}.smooth", name: "smooth", value: Smooth);
        }

        errors.RequireRange(path: $"{path}.dilate", name: "dilate", range: bounds.FieldInflate, value: Dilate);
        errors.RequireRange(path: $"{path}.onion", name: "onion", range: bounds.FieldInflate, value: Onion);

        var ops = (Ops ?? []);

        for (var index = 0; (index < ops.Count); index++) {
            var op = ops[index];

            if (op is null) {
                errors.Add(path: $"{path}.ops[{index}]", message: "a transform op cannot be null");

                continue;
            }

            op.Validate(errors: errors, path: $"{path}.ops[{index}]");

            // The parity-material stride recolors THIS object's material by the fold's cell key, so every strided id
            // must stay inside the palette — a cross-cutting (op × object × palette) rule that can only live here,
            // where all three are in scope. ScreenSlab is exempt (the sentinel is never strided).
            if (
                (op is WallpaperFoldOp wallpaper) &&
                (wallpaper.MaterialStride > 0) &&
                ReferencesMaterialPalette &&
                (materialCount > 0)
            ) {
                var highestStridedId = (Material + (wallpaper.MaterialStride * wallpaper.MaxCellKey));

                if (highestStridedId >= materialCount) {
                    errors.Add(path: $"{path}.ops[{index}].materialStride", message: $"the {wallpaper.Group} fold strides material {Material} up to id {highestStridedId} (stride {wallpaper.MaterialStride} × max cell key {wallpaper.MaxCellKey}), past the palette of {materialCount} material(s)");
                }
            }
        }

        ValidateShape(bounds: bounds, errors: errors, materialCount: materialCount, path: path);
    }

    // Whether this object indexes the material palette. A ScreenSlab uses the screen-material sentinel and
    // references no palette entry, so a scene of only screen slabs needs no materials at all.
    internal virtual bool ReferencesMaterialPalette => true;

    // Validates this object's own material index against the declared material count. Shapes that carry a real
    // material call this; ScreenSlab overrides ValidateShape WITHOUT calling it (it uses the sentinel). The
    // materialCount > 0 guard avoids a nonsensical "valid ids 0..-1" message for an empty palette — that case is
    // already reported by the dedicated "at least one material is required" error.
    private protected void ValidateMaterial(string path, int materialCount, ValidationErrors errors) {
        if ((materialCount > 0) && ((Material < 0) || (Material >= materialCount))) {
            errors.Add(path: $"{path}.material", message: $"material {Material} is out of range; the scene declares {materialCount} material(s) (valid ids 0..{materialCount - 1})");
        }
    }
    private protected abstract void EmitShape(SdfProgramBuilder builder);
    private protected abstract void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors);
}

/// <summary>A sphere of the given radius (mirrors <see cref="SdfProgramBuilder.Sphere"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SphereObject : SceneObject {
    /// <summary>The sphere radius.</summary>
    public float Radius { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder) {
        _ = builder.Sphere(blend: Blend, material: Material, radius: Radius, smooth: Smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        errors.RequireRange(path: $"{path}.radius", name: "radius", range: bounds.SphereRadius, value: Radius);
    }
}

/// <summary>A rounded box with per-axis half-extents (mirrors <see cref="SdfProgramBuilder.Box"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BoxObject : SceneObject {
    /// <summary>The half-extents, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> HalfExtents { get; init; } = [];
    /// <summary>The corner rounding radius.</summary>
    public float Round { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder) {
        _ = builder.Box(blend: Blend, halfExtents: JsonVector.ToVector3(components: HalfExtents), material: Material, round: Round, smooth: Smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);

        if (JsonVector.IsValid(components: HalfExtents, length: 3)) {
            errors.RequireRange(path: $"{path}.halfExtents[0]", name: "halfExtent.x", range: bounds.BoxHalfExtent, value: HalfExtents[0]);
            errors.RequireRange(path: $"{path}.halfExtents[1]", name: "halfExtent.y", range: bounds.BoxHalfExtent, value: HalfExtents[1]);
            errors.RequireRange(path: $"{path}.halfExtents[2]", name: "halfExtent.z", range: bounds.BoxHalfExtent, value: HalfExtents[2]);
        } else {
            errors.RequireVector(path: $"{path}.halfExtents", components: HalfExtents, length: 3);
        }

        errors.RequireRange(path: $"{path}.round", name: "round", range: bounds.BoxRound, value: Round);
    }
}

/// <summary>A torus in the XZ plane (mirrors <see cref="SdfProgramBuilder.Torus"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TorusObject : SceneObject {
    /// <summary>The major radius (ring center to tube center).</summary>
    public float MajorRadius { get; init; }
    /// <summary>The minor radius (tube thickness).</summary>
    public float MinorRadius { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder) {
        _ = builder.Torus(blend: Blend, majorRadius: MajorRadius, material: Material, minorRadius: MinorRadius, smooth: Smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        errors.RequireRange(path: $"{path}.majorRadius", name: "majorRadius", range: bounds.TorusMajorRadius, value: MajorRadius);
        errors.RequireRange(path: $"{path}.minorRadius", name: "minorRadius", range: bounds.TorusMinorRadius, value: MinorRadius);
    }
}

/// <summary>An infinite half-space plane (mirrors <see cref="SdfProgramBuilder.Plane"/>). The normal is normalized
/// at build time, so it only needs to be non-zero.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlaneObject : SceneObject {
    /// <summary>The plane normal (need not be unit length), as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Normal { get; init; } = [];
    /// <summary>The signed offset of the plane from the origin along the normal.</summary>
    public float Offset { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder) {
        _ = builder.Plane(blend: Blend, material: Material, normal: JsonVector.ToVector3(components: Normal), offset: Offset, smooth: Smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        errors.RequireFinite(path: $"{path}.offset", name: "offset", value: Offset);

        if (!JsonVector.IsValid(components: Normal, length: 3)) {
            errors.RequireVector(path: $"{path}.normal", components: Normal, length: 3);
        } else if ((Normal[0] == 0f) && (Normal[1] == 0f) && (Normal[2] == 0f)) {
            errors.Add(path: $"{path}.normal", message: "the plane normal must be non-zero");
        }
    }
}

/// <summary>A capped round cone along the Y axis (mirrors <see cref="SdfProgramBuilder.RoundCone"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoundConeObject : SceneObject {
    /// <summary>The lower cap radius.</summary>
    public float LowerRadius { get; init; }
    /// <summary>The upper cap radius.</summary>
    public float UpperRadius { get; init; }
    /// <summary>The height between the cap centers.</summary>
    public float Height { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder) {
        _ = builder.RoundCone(blend: Blend, height: Height, lowerRadius: LowerRadius, material: Material, smooth: Smooth, upperRadius: UpperRadius);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        errors.RequireRange(path: $"{path}.lowerRadius", name: "lowerRadius", range: bounds.RoundConeLowerRadius, value: LowerRadius);
        errors.RequireRange(path: $"{path}.upperRadius", name: "upperRadius", range: bounds.RoundConeUpperRadius, value: UpperRadius);
        errors.RequireRange(path: $"{path}.height", name: "height", range: bounds.RoundConeHeight, value: Height);

        // The round-cone SDF is only well-formed when the cap-radius delta does not exceed the height (otherwise the
        // cone's slant passes vertical and the shader clamps the degenerate case to a sphere).
        if (float.IsFinite(f: LowerRadius) && float.IsFinite(f: UpperRadius) && float.IsFinite(f: Height) && (MathF.Abs(x: (LowerRadius - UpperRadius)) > Height)) {
            errors.Add(path: path, message: $"round-cone precondition violated: |lowerRadius - upperRadius| ({MathF.Abs(x: (LowerRadius - UpperRadius))}) must be <= height ({Height})");
        }
    }
}

/// <summary>A capsule: a sphere-swept segment from the local origin to <see cref="Endpoint"/> (mirrors
/// <see cref="SdfProgramBuilder.Capsule"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CapsuleObject : SceneObject {
    /// <summary>The segment endpoint (the segment runs from the local origin to it), as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Endpoint { get; init; } = [];
    /// <summary>The capsule radius.</summary>
    public float Radius { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder) {
        _ = builder.Capsule(blend: Blend, endpoint: JsonVector.ToVector3(components: Endpoint), material: Material, radius: Radius, smooth: Smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        errors.RequireRange(path: $"{path}.radius", name: "radius", range: bounds.CapsuleRadius, value: Radius);

        if (JsonVector.IsValid(components: Endpoint, length: 3)) {
            errors.RequireRange(path: $"{path}.endpoint[0]", name: "endpoint.x", range: bounds.CapsuleEndpoint, value: Endpoint[0]);
            errors.RequireRange(path: $"{path}.endpoint[1]", name: "endpoint.y", range: bounds.CapsuleEndpoint, value: Endpoint[1]);
            errors.RequireRange(path: $"{path}.endpoint[2]", name: "endpoint.z", range: bounds.CapsuleEndpoint, value: Endpoint[2]);

            // A near-zero endpoint degenerates the segment to a point: the shader's denominator clamp silently renders
            // a sphere, so reject the ambiguity — the author meant a sphere.
            var lengthSquared = ((Endpoint[0] * Endpoint[0]) + (Endpoint[1] * Endpoint[1]) + (Endpoint[2] * Endpoint[2]));

            if (float.IsFinite(f: lengthSquared) && (lengthSquared < (0.05f * 0.05f))) {
                errors.Add(path: $"{path}.endpoint", message: "the capsule endpoint must be at least 0.05 units from the origin (a shorter segment degenerates to a sphere — author a sphere instead)");
            }
        } else {
            errors.RequireVector(path: $"{path}.endpoint", components: Endpoint, length: 3);
        }
    }
}

/// <summary>An upright cylinder centered on the local origin (mirrors <see cref="SdfProgramBuilder.Cylinder"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CylinderObject : SceneObject {
    /// <summary>The cylinder radius.</summary>
    public float Radius { get; init; }
    /// <summary>The half-height (the cylinder spans ±halfHeight along the local Y axis).</summary>
    public float HalfHeight { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder) {
        _ = builder.Cylinder(blend: Blend, halfHeight: HalfHeight, material: Material, radius: Radius, smooth: Smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        errors.RequireRange(path: $"{path}.radius", name: "radius", range: bounds.CylinderRadius, value: Radius);
        errors.RequireRange(path: $"{path}.halfHeight", name: "halfHeight", range: bounds.CylinderHalfHeight, value: HalfHeight);
    }
}

/// <summary>An axis-aligned ellipsoid centered on the local origin (mirrors <see cref="SdfProgramBuilder.Ellipsoid"/>).
/// The shader evaluates a first-order distance approximation — accurate near the surface, degrading with high
/// eccentricity — so the validator bounds the radii to a moderate aspect ratio.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EllipsoidObject : SceneObject {
    /// <summary>The per-axis radii, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Radii { get; init; } = [];

    private protected override void EmitShape(SdfProgramBuilder builder) {
        _ = builder.Ellipsoid(blend: Blend, material: Material, radii: JsonVector.ToVector3(components: Radii), smooth: Smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);

        if (JsonVector.IsValid(components: Radii, length: 3)) {
            errors.RequireRange(path: $"{path}.radii[0]", name: "radius.x", range: bounds.EllipsoidRadius, value: Radii[0]);
            errors.RequireRange(path: $"{path}.radii[1]", name: "radius.y", range: bounds.EllipsoidRadius, value: Radii[1]);
            errors.RequireRange(path: $"{path}.radii[2]", name: "radius.z", range: bounds.EllipsoidRadius, value: Radii[2]);
        } else {
            errors.RequireVector(path: $"{path}.radii", components: Radii, length: 3);
        }
    }
}

/// <summary>A screen-space slab (mirrors <see cref="SdfProgramBuilder.ScreenSlab"/>). It carries no material — the
/// builder always assigns the screen-material sentinel — so any <see cref="SceneObject.Material"/> is ignored. With
/// <see cref="ScreenIndex"/> (and the explicit world-space frame that must accompany it) the slab's lit face becomes
/// a SAMPLED screen surface: the document's <c>screenSources</c> table says which provider feeds it, and an unfed
/// surface falls back to the flat/procedural screen material. The frame is EXPLICIT data by design (worldOrigin/
/// worldRight/worldUp must match the ops already applied to the slab) — a transform-derived convenience can layer on
/// later without changing this contract.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScreenSlabObject : SceneObject {
    /// <summary>The half-extents, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> HalfExtents { get; init; } = [];
    /// <summary>The corner rounding radius.</summary>
    public float Round { get; init; }
    /// <summary>The screen source slot (0..3) this slab's lit face samples; null (the default) keeps the plain
    /// flat/procedural screen material with no sampled surface declared.</summary>
    public int? ScreenIndex { get; init; }
    /// <summary>The front face's world-space center, as a 3-element array (required with <see cref="ScreenIndex"/>;
    /// must match the translate ops applied to the slab).</summary>
    public IReadOnlyList<float>? WorldOrigin { get; init; }
    /// <summary>The unit world-space axis the sampled U increases along — the slab's local +X in world space, as a
    /// 3-element array (required with <see cref="ScreenIndex"/>).</summary>
    public IReadOnlyList<float>? WorldRight { get; init; }
    /// <summary>The unit world-space axis the sampled V increases against — the slab's local +Y in world space, as a
    /// 3-element array (required with <see cref="ScreenIndex"/>).</summary>
    public IReadOnlyList<float>? WorldUp { get; init; }

    internal override bool ReferencesMaterialPalette => false;

    private protected override void EmitShape(SdfProgramBuilder builder) {
        if (ScreenIndex is int screenIndex) {
            _ = builder.ScreenSlab(
                blend: Blend,
                halfExtents: JsonVector.ToVector3(components: HalfExtents),
                round: Round,
                screenIndex: screenIndex,
                smooth: Smooth,
                worldOrigin: JsonVector.ToVector3(components: WorldOrigin!),
                worldRight: JsonVector.ToVector3(components: WorldRight!),
                worldUp: JsonVector.ToVector3(components: WorldUp!)
            );
        } else {
            _ = builder.ScreenSlab(blend: Blend, halfExtents: JsonVector.ToVector3(components: HalfExtents), round: Round, smooth: Smooth);
        }
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        if (JsonVector.IsValid(components: HalfExtents, length: 3)) {
            errors.RequireRange(path: $"{path}.halfExtents[0]", name: "halfExtent.x", range: bounds.ScreenSlabHalfExtent, value: HalfExtents[0]);
            errors.RequireRange(path: $"{path}.halfExtents[1]", name: "halfExtent.y", range: bounds.ScreenSlabHalfExtent, value: HalfExtents[1]);
            errors.RequireRange(path: $"{path}.halfExtents[2]", name: "halfExtent.z", range: bounds.ScreenSlabHalfExtent, value: HalfExtents[2]);
        } else {
            errors.RequireVector(path: $"{path}.halfExtents", components: HalfExtents, length: 3);
        }

        errors.RequireRange(path: $"{path}.round", name: "round", range: bounds.ScreenSlabRound, value: Round);

        var hasFrameField = ((WorldOrigin is not null) || (WorldRight is not null) || (WorldUp is not null));

        if (ScreenIndex is int index) {
            if ((index < 0) || (index >= SdfProgramBuilder.MaxScreenSurfaces)) {
                errors.Add(path: $"{path}.screenIndex", message: $"a screen index must be 0..{SdfProgramBuilder.MaxScreenSurfaces - 1}; found {index}");
            }

            errors.RequireVector(path: $"{path}.worldOrigin", components: WorldOrigin, length: 3);
            errors.RequireVector(path: $"{path}.worldRight", components: WorldRight, length: 3);
            errors.RequireVector(path: $"{path}.worldUp", components: WorldUp, length: 3);
        } else if (hasFrameField) {
            errors.Add(path: $"{path}.screenIndex", message: "worldOrigin/worldRight/worldUp declare a sampled screen surface and require a screenIndex");
        }
    }
}
