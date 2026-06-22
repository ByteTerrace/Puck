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
    /// <summary>The smooth-blend radius, used only when <see cref="Blend"/> is <see cref="SdfBlendOp.SmoothUnion"/>.</summary>
    public float Smooth { get; init; }

    // Resets the point, applies every op in order, then emits the terminal shape — the exact sequence the demo's
    // hand-authored BuildScene uses per object, reproduced from data.
    internal void Emit(SdfProgramBuilder builder) {
        _ = builder.ResetPoint();

        // Ops is optional; an omitted array deserializes to null under source-gen, so coalesce. A null element can
        // only occur on an unvalidated document (the validator rejects them), so skip rather than throw.
        foreach (var op in (Ops ?? [])) {
            op?.Apply(builder: builder);
        }

        EmitShape(builder: builder);
    }
    internal void Validate(string path, int materialCount, ShapeBounds bounds, ValidationErrors errors) {
        if (!Enum.IsDefined(value: Blend)) {
            errors.Add(path: $"{path}.blend", message: $"'{Blend}' is not a defined SdfBlendOp");
        }

        if (Blend == SdfBlendOp.SmoothUnion) {
            errors.RequireRange(path: $"{path}.smooth", name: "smooth", range: bounds.Smooth, value: Smooth);
        } else {
            errors.RequireFinite(path: $"{path}.smooth", name: "smooth", value: Smooth);
        }

        var ops = (Ops ?? []);

        for (var index = 0; (index < ops.Count); index++) {
            var op = ops[index];

            if (op is null) {
                errors.Add(path: $"{path}.ops[{index}]", message: "a transform op cannot be null");

                continue;
            }

            op.Validate(errors: errors, path: $"{path}.ops[{index}]");
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

/// <summary>A screen-space slab (mirrors <see cref="SdfProgramBuilder.ScreenSlab"/>). It carries no material — the
/// builder always assigns the screen-material sentinel — so any <see cref="SceneObject.Material"/> is ignored.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScreenSlabObject : SceneObject {
    /// <summary>The half-extents, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> HalfExtents { get; init; } = [];
    /// <summary>The corner rounding radius.</summary>
    public float Round { get; init; }

    internal override bool ReferencesMaterialPalette => false;

    private protected override void EmitShape(SdfProgramBuilder builder) {
        _ = builder.ScreenSlab(blend: Blend, halfExtents: JsonVector.ToVector3(components: HalfExtents), round: Round, smooth: Smooth);
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
    }
}
