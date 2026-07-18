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
[JsonDerivedType(typeof(VesicaObject), typeDiscriminator: "vesica")]
[JsonDerivedType(typeof(RoundedRectangleObject), typeDiscriminator: "roundedRectangle")]
[JsonDerivedType(typeof(RegularPolygonObject), typeDiscriminator: "regularPolygon")]
[JsonDerivedType(typeof(StarObject), typeDiscriminator: "star")]
[JsonDerivedType(typeof(TrapezoidObject), typeDiscriminator: "trapezoid")]
[JsonDerivedType(typeof(EllipseObject), typeDiscriminator: "ellipse")]
[JsonDerivedType(typeof(GroupObject), typeDiscriminator: "group")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "shape")]
public abstract record SceneObject {
    /// <summary>The transform chain applied to the point before the shape is evaluated, in order.</summary>
    public IReadOnlyList<TransformOp> Ops { get; init; } = [];
    /// <summary>The id (index into <c>scene.materials</c>) of this object's surface material. Ignored by
    /// <see cref="ScreenSlabObject"/>, which always uses the screen-material sentinel.</summary>
    public int Material { get; init; }
    /// <summary>How this object melds into the accumulated field — the field of EVERY object before it in
    /// <c>scene.objects</c>, not the object before it. Union and the subtraction family are local and may appear
    /// anywhere; the INTERSECTION family is not — <c>max(accumulator, candidate)</c> returns the candidate everywhere
    /// outside this object's own shape, so it annihilates every earlier object it does not overlap, the ground included.
    /// An intersecting object must therefore be FIRST. See the accumulator rule on <see cref="SdfBlendOp"/>.</summary>
    public SdfBlendOp Blend { get; init; } = SdfBlendOp.Union;
    /// <summary>The smooth-blend radius, used only when <see cref="Blend"/> is one of the smooth ops
    /// (<see cref="SdfBlendOp.SmoothUnion"/>/<see cref="SdfBlendOp.SmoothIntersection"/>/<see cref="SdfBlendOp.SmoothSubtraction"/>).</summary>
    public float Smooth { get; init; }
    /// <summary>An inflation radius applied AFTER this object melds in — a FIELD op. 0 (the default) = off.
    /// <para><see cref="Emit"/> wraps the shape and its field operations in a
    /// <c>PushField</c>/<c>PopField</c> scope, so dilation affects only this object and never the field accumulated by
    /// earlier objects. Every object may carry one, in any order.</para></summary>
    public float Dilate { get; init; }
    /// <summary>A shell half-thickness applied AFTER this object melds in (after <see cref="Dilate"/>) — a FIELD op.
    /// 0 (the default) = off.
    /// <para>Scoped to this object in the same way as <see cref="Dilate"/>, so it hollows only this object's field and
    /// never affects earlier objects.</para></summary>
    public float Onion { get; init; }
    /// <summary>A bounded sinusoidal FIELD relief — surface bumps/corrugation — applied AFTER this object melds in
    /// (after <see cref="Dilate"/>/<see cref="Onion"/>). Null (the default) = off.
    /// <para>Mirrors <see cref="SdfProgramBuilder.Displace"/>, which is itself documented as a FIELD op ("order it
    /// after the shapes it should displace") — exactly like <see cref="Dilate"/>/<see cref="Onion"/>, NOT a point op
    /// in the <see cref="Ops"/> chain (a point op there runs BEFORE this object's own shape even exists, so a
    /// same-named entry in <c>TransformOp</c> would silently corrugate whatever earlier objects already accumulated,
    /// or nothing at all on the first object — the exact unscoped-field-op failure mode <see cref="Dilate"/>'s doc
    /// describes). It therefore joins the SAME scope <see cref="Dilate"/>/<see cref="Onion"/> already open — SCOPED to
    /// this object alone, same rationale as those two.</para></summary>
    public DisplaceField? Displace { get; init; }

    // Resets the point, applies every op in order, then emits the terminal shape — the exact sequence the demo's
    // hand-authored BuildScene uses per object, reproduced from data. The optional FIELD ops follow the shape in
    // fixed dilate-then-onion-then-displace order, SCOPED to this object (see below). Virtual so GroupObject — whose
    // "shape" is a collection composed through its OWN PushField/PopField scope, not a single terminal primitive —
    // can replace this whole sequence rather than implementing EmitShape.
    internal virtual void Emit(SdfProgramBuilder builder) {
        _ = builder.ResetPoint();

        // Ops is optional; an omitted array deserializes to null under source-gen, so coalesce. A null element can
        // only occur on an unvalidated document (the validator rejects them), so skip rather than throw.
        foreach (var op in (Ops ?? [])) {
            op?.Apply(builder: builder);
        }

        // The field ops (dilate/onion/displace) are FIELD ops — in the FLAT accumulator they'd inflate/hollow/corrugate
        // the ENTIRE scene emitted so far, which is why the Dilate/Onion docs above said only the FIRST object could
        // carry one. Scope them: PushField reseeds a fresh accumulator, so the shape (emitted as a plain Union —
        // against the fresh SDF_FAR_DISTANCE seed it IS the shape) plus its dilate/onion/displace form THIS object's
        // field ALONE, then PopField melds that finished object into the scene through the object's own Blend/Smooth.
        // An object with no field op takes the flat path and emits byte-identically to before this change.
        if ((Dilate > 0f) || (Onion > 0f) || (Displace is not null)) {
            _ = builder.PushField(compose: Blend, smooth: Smooth);

            EmitShape(builder: builder, blend: SdfBlendOp.Union, smooth: 0f);

            if (Dilate > 0f) {
                _ = builder.Dilate(radius: Dilate);
            }

            if (Onion > 0f) {
                _ = builder.Onion(thickness: Onion);
            }

            if (Displace is DisplaceField displace) {
                _ = builder.Displace(amplitude: displace.Amplitude, frequency: JsonVector.ToVector3(components: displace.Frequency));
            }

            _ = builder.PopField();
        } else {
            EmitShape(builder: builder, blend: Blend, smooth: Smooth);
        }
    }
    // Virtual for the same reason as Emit — GroupObject validates a member LIST, not one shape's parameters, and
    // recurses into each member's own Validate rather than calling ValidateShape.
    internal virtual void Validate(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
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

        if (Displace is DisplaceField displace) {
            TransformOp.RequireWarpBudget(amplitude: displace.Amplitude, errors: errors, frequency: displace.Frequency, maximum: 3f, path: $"{path}.displace");
        }

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

            // The angular sibling of the wallpaper-fold check above: a RepeatPolar materialStride recolors THIS
            // object's material by the sector index, so every strided id must stay inside the palette too.
            if (
                (op is RepeatPolarOp repeatPolar) &&
                (repeatPolar.MaterialStride > 0) &&
                ReferencesMaterialPalette &&
                (materialCount > 0)
            ) {
                var highestStridedId = (Material + (repeatPolar.MaterialStride * repeatPolar.MaxSectorIndex));

                if (highestStridedId >= materialCount) {
                    errors.Add(path: $"{path}.ops[{index}].materialStride", message: $"repeatPolar strides material {Material} up to id {highestStridedId} (stride {repeatPolar.MaterialStride} × max sector index {repeatPolar.MaxSectorIndex}), past the palette of {materialCount} material(s)");
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
            errors.Add(path: $"{path}.material", message: $"material {Material} is out of range; the scene declares {materialCount} material(s) (valid ids 0..{(materialCount - 1)})");
        }
    }
    // Shared by the whole 2D-lifted family (RoundedRectangle/RegularPolygon/Star/Trapezoid/Ellipse): the lift mode
    // must be a defined SdfLift and the lift amount (the revolve radial offset or the extrude half-height) in bounds.
    private protected static void ValidateLift(string path, SdfLift lift, float liftAmount, ShapeBounds bounds, ValidationErrors errors) {
        if (!Enum.IsDefined(value: lift)) {
            errors.Add(path: $"{path}.lift", message: $"'{lift}' is not a defined SdfLift");
        }

        errors.RequireRange(path: $"{path}.liftAmount", name: "liftAmount", range: bounds.LiftAmount, value: liftAmount);
    }
    // Emits the terminal shape with the given <paramref name="blend"/>/<paramref name="smooth"/> — the object's own
    // Blend/Smooth on the flat path, or Union/0 inside a field-op scope (Emit hands the object's Blend/Smooth to the
    // closing PopField instead).
    private protected abstract void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth);
    private protected abstract void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors);
}

/// <summary>The parameters of <see cref="SceneObject.Displace"/> — a bounded sinusoidal FIELD relief (mirrors
/// <see cref="SdfProgramBuilder.Displace"/>). Not a <see cref="TransformOp"/>: see the note on
/// <see cref="SceneObject.Displace"/> for why it lives here instead of the <see cref="SceneObject.Ops"/> chain.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DisplaceField {
    /// <summary>Per-axis angular frequency of the sinusoid (radians per world unit), as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Frequency { get; init; } = [];
    /// <summary>Peak displacement added to the field (world units; 0 = an exact identity).</summary>
    public float Amplitude { get; init; }
}

/// <summary>A sphere of the given radius (mirrors <see cref="SdfProgramBuilder.Sphere"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SphereObject : SceneObject {
    /// <summary>The sphere radius.</summary>
    public float Radius { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.Sphere(blend: blend, material: Material, radius: Radius, smooth: smooth);
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

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.Box(blend: blend, halfExtents: JsonVector.ToVector3(components: HalfExtents), material: Material, round: Round, smooth: smooth);
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

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.Torus(blend: blend, majorRadius: MajorRadius, material: Material, minorRadius: MinorRadius, smooth: smooth);
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

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.Plane(blend: blend, material: Material, normal: JsonVector.ToVector3(components: Normal), offset: Offset, smooth: smooth);
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

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.RoundCone(blend: blend, height: Height, lowerRadius: LowerRadius, material: Material, smooth: smooth, upperRadius: UpperRadius);
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

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.Capsule(blend: blend, endpoint: JsonVector.ToVector3(components: Endpoint), material: Material, radius: Radius, smooth: smooth);
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
            var lengthSquared = (((Endpoint[0] * Endpoint[0]) + (Endpoint[1] * Endpoint[1])) + (Endpoint[2] * Endpoint[2]));

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

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.Cylinder(blend: blend, halfHeight: HalfHeight, material: Material, radius: Radius, smooth: smooth);
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

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.Ellipsoid(blend: blend, material: Material, radii: JsonVector.ToVector3(components: Radii), smooth: smooth);
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

/// <summary>A screen-space slab that mirrors the <see cref="SdfProgramBuilder"/> screen-slab operation. It carries no material — the
/// builder always assigns the screen-material sentinel — so any <see cref="SceneObject.Material"/> is ignored. With
/// <see cref="ScreenIndex"/> (and the explicit world-space frame that must accompany it) the slab's lit face becomes
/// a SAMPLED screen surface: the document's <c>screenSources</c> table says which provider feeds it, and an unfed
/// surface falls back to the flat/procedural screen material. The frame is EXPLICIT data by design (worldOrigin/
/// worldRight/worldUp must match the operations already applied to the slab).</summary>
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

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        if (ScreenIndex is int screenIndex) {
            _ = builder.ScreenSlab(
                blend: blend,
                halfExtents: JsonVector.ToVector3(components: HalfExtents),
                round: Round,
                screenIndex: screenIndex,
                smooth: smooth,
                worldOrigin: JsonVector.ToVector3(components: WorldOrigin!),
                worldRight: JsonVector.ToVector3(components: WorldRight!),
                worldUp: JsonVector.ToVector3(components: WorldUp!)
            );
        } else {
            _ = builder.ScreenSlab(blend: blend, halfExtents: JsonVector.ToVector3(components: HalfExtents), round: Round, smooth: smooth);
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
                errors.Add(path: $"{path}.screenIndex", message: $"a screen index must be 0..{(SdfProgramBuilder.MaxScreenSurfaces - 1)}; found {index}");
            }

            errors.RequireVector(path: $"{path}.worldOrigin", components: WorldOrigin, length: 3);
            errors.RequireVector(path: $"{path}.worldRight", components: WorldRight, length: 3);
            errors.RequireVector(path: $"{path}.worldUp", components: WorldUp, length: 3);
        } else if (hasFrameField) {
            errors.Add(path: $"{path}.screenIndex", message: "worldOrigin/worldRight/worldUp declare a sampled screen surface and require a screenIndex");
        }
    }
}

/// <summary>A vesica (lens): the intersection of two spheres of <see cref="Radius"/> whose centers are
/// 2·<see cref="HalfSeparation"/> apart, revolved into a 3D lens pointed along ±Y (mirrors
/// <see cref="SdfProgramBuilder.Vesica"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VesicaObject : SceneObject {
    /// <summary>The radius of the two intersecting spheres.</summary>
    public float Radius { get; init; }
    /// <summary>Half the distance between the two sphere centers; must be less than <see cref="Radius"/> (the tip
    /// half-height is <c>sqrt(radius² - halfSeparation²)</c>, real only below it).</summary>
    public float HalfSeparation { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.Vesica(blend: blend, halfSeparation: HalfSeparation, material: Material, radius: Radius, smooth: smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        errors.RequireRange(path: $"{path}.radius", name: "radius", range: bounds.VesicaRadius, value: Radius);
        errors.RequireRange(path: $"{path}.halfSeparation", name: "halfSeparation", range: bounds.VesicaHalfSeparation, value: HalfSeparation);

        if (float.IsFinite(f: Radius) && float.IsFinite(f: HalfSeparation) && (MathF.Abs(x: HalfSeparation) >= MathF.Abs(x: Radius))) {
            errors.Add(path: path, message: $"vesica precondition violated: |halfSeparation| ({MathF.Abs(x: HalfSeparation)}) must be < radius ({MathF.Abs(x: Radius)}), or the lens degenerates (the builder would silently clamp it)");
        }
    }
}

/// <summary>A rounded rectangle lifted to a 3D solid — <see cref="SdfLift.Extrude"/> gives a rounded slab/plaque,
/// <see cref="SdfLift.Revolve"/> a rounded disc/puck (mirrors <see cref="SdfProgramBuilder.RoundedRectangle"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoundedRectangleObject : SceneObject {
    /// <summary>Half-width of the rectangle (its local X half-extent).</summary>
    public float HalfWidth { get; init; }
    /// <summary>Half-height of the rectangle (its local Y half-extent).</summary>
    public float HalfHeight { get; init; }
    /// <summary>Corner-rounding radius; clamped by the builder to the smaller half-extent (corners round inward).</summary>
    public float CornerRadius { get; init; }
    /// <summary>Whether to revolve the profile around Y or extrude it along Z.</summary>
    public SdfLift Lift { get; init; }
    /// <summary>The revolve offset (<see cref="SdfLift.Revolve"/>) or extrude half-height (<see cref="SdfLift.Extrude"/>).</summary>
    public float LiftAmount { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.RoundedRectangle(blend: blend, cornerRadius: CornerRadius, halfHeight: HalfHeight, halfWidth: HalfWidth, lift: Lift, liftAmount: LiftAmount, material: Material, smooth: smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        ValidateLift(bounds: bounds, errors: errors, lift: Lift, liftAmount: LiftAmount, path: path);
        errors.RequireRange(path: $"{path}.halfWidth", name: "halfWidth", range: bounds.Lifted2DHalfExtent, value: HalfWidth);
        errors.RequireRange(path: $"{path}.halfHeight", name: "halfHeight", range: bounds.Lifted2DHalfExtent, value: HalfHeight);
        errors.RequireRange(path: $"{path}.cornerRadius", name: "cornerRadius", range: bounds.RoundedRectangleCornerRadius, value: CornerRadius);
    }
}

/// <summary>A regular convex <see cref="Sides"/>-gon lifted to a 3D solid — <see cref="SdfLift.Extrude"/> gives a
/// prism, <see cref="SdfLift.Revolve"/> a lathe of the polygon's profile (mirrors
/// <see cref="SdfProgramBuilder.RegularPolygon"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegularPolygonObject : SceneObject {
    /// <summary>The side count; must be at least 3 (the builder would otherwise silently clamp it).</summary>
    public int Sides { get; init; }
    /// <summary>The circumradius (centre to a vertex).</summary>
    public float Radius { get; init; }
    /// <summary>Whether to revolve the profile around Y or extrude it along Z.</summary>
    public SdfLift Lift { get; init; }
    /// <summary>The revolve offset (<see cref="SdfLift.Revolve"/>) or extrude half-height (<see cref="SdfLift.Extrude"/>).</summary>
    public float LiftAmount { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.RegularPolygon(blend: blend, lift: Lift, liftAmount: LiftAmount, material: Material, radius: Radius, sides: Sides, smooth: smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        ValidateLift(bounds: bounds, errors: errors, lift: Lift, liftAmount: LiftAmount, path: path);
        errors.RequireRange(path: $"{path}.radius", name: "radius", range: bounds.PolygonRadius, value: Radius);

        if (Sides < 3) {
            errors.Add(path: $"{path}.sides", message: $"sides {Sides} must be at least 3 (the builder would silently clamp it)");
        }
    }
}

/// <summary>An <see cref="Points"/>-pointed star lifted to a 3D solid — <see cref="SdfLift.Extrude"/> gives a star
/// prism, <see cref="SdfLift.Revolve"/> a spiked lathe (mirrors <see cref="SdfProgramBuilder.Star"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StarObject : SceneObject {
    /// <summary>The point count; must be at least 2 (the builder would otherwise silently clamp it).</summary>
    public int Points { get; init; }
    /// <summary>The outer radius (centre to a point tip).</summary>
    public float Radius { get; init; }
    /// <summary>The inner-radius control; must be in <c>[2, points]</c> (the builder would otherwise silently clamp
    /// it): 2 is a convex n-gon, larger is sharper (deeper notches between points).</summary>
    public float Sharpness { get; init; }
    /// <summary>Whether to revolve the profile around Y or extrude it along Z.</summary>
    public SdfLift Lift { get; init; }
    /// <summary>The revolve offset (<see cref="SdfLift.Revolve"/>) or extrude half-height (<see cref="SdfLift.Extrude"/>).</summary>
    public float LiftAmount { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.Star(blend: blend, lift: Lift, liftAmount: LiftAmount, material: Material, points: Points, radius: Radius, sharpness: Sharpness, smooth: smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        ValidateLift(bounds: bounds, errors: errors, lift: Lift, liftAmount: LiftAmount, path: path);
        errors.RequireRange(path: $"{path}.radius", name: "radius", range: bounds.PolygonRadius, value: Radius);

        if (Points < 2) {
            errors.Add(path: $"{path}.points", message: $"points {Points} must be at least 2 (the builder would silently clamp it)");
        }

        errors.RequireFinite(path: $"{path}.sharpness", name: "sharpness", value: Sharpness);

        if (float.IsFinite(f: Sharpness) && (Points >= 2) && ((Sharpness < 2f) || (Sharpness > Points))) {
            errors.Add(path: $"{path}.sharpness", message: $"sharpness {Sharpness} must be in [2, {Points}] (the builder would silently clamp it)");
        }
    }
}

/// <summary>An isosceles trapezoid lifted to a 3D solid — <see cref="SdfLift.Extrude"/> gives a keystone/wedge prism,
/// <see cref="SdfLift.Revolve"/> a frustum/lampshade/cup (mirrors <see cref="SdfProgramBuilder.Trapezoid"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TrapezoidObject : SceneObject {
    /// <summary>Half-width of the bottom edge (at local −Y).</summary>
    public float BottomHalfWidth { get; init; }
    /// <summary>Half-width of the top edge (at local +Y).</summary>
    public float TopHalfWidth { get; init; }
    /// <summary>Half-height of the trapezoid.</summary>
    public float HalfHeight { get; init; }
    /// <summary>Whether to revolve the profile around Y or extrude it along Z.</summary>
    public SdfLift Lift { get; init; }
    /// <summary>The revolve offset (<see cref="SdfLift.Revolve"/>) or extrude half-height (<see cref="SdfLift.Extrude"/>).</summary>
    public float LiftAmount { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.Trapezoid(blend: blend, bottomHalfWidth: BottomHalfWidth, halfHeight: HalfHeight, lift: Lift, liftAmount: LiftAmount, material: Material, smooth: smooth, topHalfWidth: TopHalfWidth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        ValidateLift(bounds: bounds, errors: errors, lift: Lift, liftAmount: LiftAmount, path: path);
        errors.RequireRange(path: $"{path}.bottomHalfWidth", name: "bottomHalfWidth", range: bounds.Lifted2DHalfExtent, value: BottomHalfWidth);
        errors.RequireRange(path: $"{path}.topHalfWidth", name: "topHalfWidth", range: bounds.Lifted2DHalfExtent, value: TopHalfWidth);
        errors.RequireRange(path: $"{path}.halfHeight", name: "halfHeight", range: bounds.Lifted2DHalfExtent, value: HalfHeight);
    }
}

/// <summary>An ellipse lifted to a 3D solid — <see cref="SdfLift.Revolve"/> at offset 0 gives an EXACT spheroid, which
/// (unlike the approximate <see cref="EllipsoidObject"/>) earns a real cull bound: prefer this shape over
/// <see cref="EllipsoidObject"/> whenever a spheroid is actually meant. <see cref="SdfLift.Extrude"/> gives an
/// elliptic-cylinder prism (mirrors <see cref="SdfProgramBuilder.Ellipse"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EllipseObject : SceneObject {
    /// <summary>The semi-axis along local X.</summary>
    public float SemiX { get; init; }
    /// <summary>The semi-axis along local Y.</summary>
    public float SemiY { get; init; }
    /// <summary>Whether to revolve the profile around Y (offset 0 ⇒ a spheroid) or extrude it along Z.</summary>
    public SdfLift Lift { get; init; }
    /// <summary>The revolve offset (<see cref="SdfLift.Revolve"/>) or extrude half-height (<see cref="SdfLift.Extrude"/>).</summary>
    public float LiftAmount { get; init; }

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) {
        _ = builder.Ellipse(blend: blend, lift: Lift, liftAmount: LiftAmount, material: Material, semiX: SemiX, semiY: SemiY, smooth: smooth);
    }
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        ValidateMaterial(errors: errors, materialCount: materialCount, path: path);
        ValidateLift(bounds: bounds, errors: errors, lift: Lift, liftAmount: LiftAmount, path: path);
        errors.RequireRange(path: $"{path}.semiX", name: "semiX", range: bounds.EllipseSemiAxis, value: SemiX);
        errors.RequireRange(path: $"{path}.semiY", name: "semiY", range: bounds.EllipseSemiAxis, value: SemiY);
    }
}

/// <summary>
/// A GROUP of objects whose accumulated field composes through ONE <see cref="SdfProgramBuilder.PushField"/>/
/// <see cref="SdfProgramBuilder.PopField"/> scope — the JSON surface for the VM's depth-1 field-scope primitive
/// Per-object field-operation scoping already places each <see cref="SceneObject.Dilate"/>/
/// <see cref="SceneObject.Onion"/>/<see cref="SceneObject.Displace"/> in its own one-object scope; this is the
/// multi-object scope. <see cref="Objects"/> blend against each other inside the group's own fresh
/// accumulator, in document order, using each MEMBER's own <see cref="SceneObject.Blend"/>/<see cref="SceneObject.Smooth"/>
/// — so a member's Intersection (or the subtraction family) composes safely against its group-mates instead of
/// annihilating the whole scene (the accumulator rule on <see cref="SdfBlendOp"/>; the creator's identical fix is
/// <c>CreatorSceneRenderer.EmitGroup</c>'s "Intersection-wipes-workbench" precedent). The FINISHED group then
/// composes into the PARENT scene via the GROUP's OWN <see cref="SceneObject.Blend"/>/<see cref="SceneObject.Smooth"/>
/// — exactly like a single field-op-scoped object already does — so scoping lands at GROUP granularity, not
/// per-member.
/// <para>Depth-1 ONLY (<see cref="SdfProgramBuilder.MaxFieldScopeDepth"/>): a member may not itself be a group, and
/// may not carry its own <see cref="SceneObject.Dilate"/>/<see cref="SceneObject.Onion"/>/<see cref="SceneObject.Displace"/>
/// — each of those wants ITS OWN PushField/PopField scope, and nesting a second one inside the group's would exceed
/// the depth-1 cap. The validator rejects both.</para>
/// <para><see cref="SceneObject.Ops"/>/<see cref="SceneObject.Material"/>/<see cref="SceneObject.Dilate"/>/
/// <see cref="SceneObject.Onion"/>/<see cref="SceneObject.Displace"/> on the GROUP itself are meaningless (the group
/// has no point-transform chain or terminal shape of its own — <see cref="Emit"/> never calls <c>ResetPoint</c>) and
/// are REJECTED by the validator rather
/// than silently ignored, so an author who reaches for them gets pointed at the member level instead.</para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupObject : SceneObject {
    /// <summary>The member objects, blended together in document order INSIDE the group's own scope (their own
    /// Blend/Smooth compose against each other, never against the parent scene directly).</summary>
    public IReadOnlyList<SceneObject> Objects { get; init; } = [];

    internal override bool ReferencesMaterialPalette =>
        (Objects ?? []).Any(predicate: static member => ((member is not null) && member.ReferencesMaterialPalette));

    internal override void Emit(SdfProgramBuilder builder) {
        _ = builder.PushField(compose: Blend, smooth: Smooth);

        foreach (var member in (Objects ?? [])) {
            member?.Emit(builder: builder);
        }

        _ = builder.PopField();
    }
    internal override void Validate(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        if (!Enum.IsDefined(value: Blend)) {
            errors.Add(path: $"{path}.blend", message: $"'{Blend}' is not a defined SdfBlendOp");
        }

        if ((Blend == SdfBlendOp.SmoothUnion) || (Blend == SdfBlendOp.SmoothIntersection) || (Blend == SdfBlendOp.SmoothSubtraction)) {
            errors.RequireRange(path: $"{path}.smooth", name: "smooth", range: bounds.Smooth, value: Smooth);
        } else {
            errors.RequireFinite(path: $"{path}.smooth", name: "smooth", value: Smooth);
        }

        if ((Ops ?? []).Count > 0) {
            errors.Add(path: $"{path}.ops", message: "a group carries no transform chain of its own (Emit never resets the point for it) — move ops onto the member objects instead");
        }

        if ((Dilate > 0f) || (Onion > 0f) || (Displace is not null)) {
            errors.Add(path: path, message: "a group's own dilate/onion/displace are ignored (the group has no terminal shape to apply them to) — move them onto a member, or wrap that member alone");
        }

        var members = (Objects ?? []);

        if (members.Count == 0) {
            errors.Add(path: $"{path}.objects", message: "a group must contain at least one object — an empty scope composes SDF_FAR_DISTANCE and would carve nothing");
        }

        for (var index = 0; (index < members.Count); index++) {
            var member = members[index];
            var memberPath = $"{path}.objects[{index}]";

            if (member is null) {
                errors.Add(path: memberPath, message: "an object entry cannot be null");

                continue;
            }

            if (member is GroupObject) {
                errors.Add(path: memberPath, message: $"a group cannot contain another group — field scopes nest only {SdfProgramBuilder.MaxFieldScopeDepth} deep and the enclosing group already opened the one allowed scope");
            } else if ((member.Dilate > 0f) || (member.Onion > 0f) || (member.Displace is not null)) {
                errors.Add(path: memberPath, message: "a group member cannot carry its own dilate/onion/displace — each requires its own PushField/PopField scope, and nesting inside the group's scope would exceed the depth-1 cap");
            }

            member.Validate(bounds: bounds, errors: errors, materialCount: materialCount, path: memberPath);
        }
    }

    private protected override void EmitShape(SdfProgramBuilder builder, SdfBlendOp blend, float smooth) =>
        throw new InvalidOperationException(message: "GroupObject overrides Emit directly (its 'shape' is a member list, not a terminal primitive) and never calls EmitShape.");
    private protected override void ValidateShape(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) =>
        throw new InvalidOperationException(message: "GroupObject overrides Validate directly and never calls ValidateShape.");
}
