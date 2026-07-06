using System.Text.Json.Serialization;
using Puck.SdfVm;

namespace Puck.Scene;

/// <summary>
/// One transform in an object's <c>ops</c> chain, applied to the current point before the terminal shape is emitted.
/// The set mirrors the public transform verbs on <see cref="SdfProgramBuilder"/> one-for-one; the <c>op</c> string is
/// the JSON type discriminator. Adding a transform is a new derived record (carrying its own <see cref="Apply"/> and
/// <see cref="Validate"/>) — never an edit to a central switch.
/// </summary>
[JsonDerivedType(typeof(TranslateOp), typeDiscriminator: "translate")]
[JsonDerivedType(typeof(RotateOp), typeDiscriminator: "rotate")]
[JsonDerivedType(typeof(ScaleOp), typeDiscriminator: "scale")]
[JsonDerivedType(typeof(RepeatOp), typeDiscriminator: "repeat")]
[JsonDerivedType(typeof(RepeatLimitedOp), typeDiscriminator: "repeatLimited")]
[JsonDerivedType(typeof(SymmetryXOp), typeDiscriminator: "symmetryX")]
[JsonDerivedType(typeof(SymmetryYOp), typeDiscriminator: "symmetryY")]
[JsonDerivedType(typeof(SymmetryZOp), typeDiscriminator: "symmetryZ")]
[JsonDerivedType(typeof(WallpaperFoldOp), typeDiscriminator: "wallpaperFold")]
[JsonDerivedType(typeof(TwistYOp), typeDiscriminator: "twistY")]
[JsonDerivedType(typeof(BendXOp), typeDiscriminator: "bendX")]
[JsonDerivedType(typeof(BendYOp), typeDiscriminator: "bendY")]
[JsonDerivedType(typeof(BendZOp), typeDiscriminator: "bendZ")]
[JsonDerivedType(typeof(ElongateOp), typeDiscriminator: "elongate")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
public abstract record TransformOp {
    // Applies this transform's builder verb. The point reset + every preceding op have already run.
    internal abstract void Apply(SdfProgramBuilder builder);
    // Appends any structural errors (vector arity, finiteness) for this op under the given json path.
    internal abstract void Validate(string path, ValidationErrors errors);

    // The shared rate guard of the warp ops (twist/bend): finite and within ±maximum — the warps are not isometries,
    // so an unbounded rate destabilizes the march.
    private protected static void RequireRate(string path, float rate, float maximum, ValidationErrors errors) {
        errors.RequireFinite(path: $"{path}.rate", name: "rate", value: rate);

        if (float.IsFinite(f: rate) && (MathF.Abs(x: rate) > maximum)) {
            errors.Add(path: $"{path}.rate", message: $"rate {rate} is outside the allowed range [-{maximum}, {maximum}]");
        }
    }
}

/// <summary>Translates the point by <c>[x, y, z]</c> (mirrors <see cref="SdfProgramBuilder.Translate"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TranslateOp : TransformOp {
    /// <summary>The translation offset, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Offset { get; init; } = [];

    internal override void Apply(SdfProgramBuilder builder) {
        builder.Translate(offset: JsonVector.ToVector3(components: Offset));
    }
    internal override void Validate(string path, ValidationErrors errors) {
        errors.RequireVector(path: $"{path}.offset", components: Offset, length: 3);
    }
}

/// <summary>Rotates the point by a quaternion <c>[x, y, z, w]</c> (mirrors <see cref="SdfProgramBuilder.Rotate"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RotateOp : TransformOp {
    /// <summary>The rotation quaternion, as a 4-element <c>[x, y, z, w]</c> array.</summary>
    public IReadOnlyList<float> Rotation { get; init; } = [];

    internal override void Apply(SdfProgramBuilder builder) {
        builder.Rotate(rotation: JsonVector.ToQuaternion(components: Rotation));
    }
    internal override void Validate(string path, ValidationErrors errors) {
        errors.RequireVector(path: $"{path}.rotation", components: Rotation, length: 4);
    }
}

/// <summary>Scales the point by <c>[x, y, z]</c> (mirrors <see cref="SdfProgramBuilder.Scale"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScaleOp : TransformOp {
    /// <summary>The per-axis scale, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Scale { get; init; } = [];

    internal override void Apply(SdfProgramBuilder builder) {
        builder.Scale(scale: JsonVector.ToVector3(components: Scale));
    }
    internal override void Validate(string path, ValidationErrors errors) {
        errors.RequireVector(path: $"{path}.scale", components: Scale, length: 3);
    }
}

/// <summary>Infinitely repeats space on a <c>[x, y, z]</c> lattice (mirrors <see cref="SdfProgramBuilder.Repeat"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepeatOp : TransformOp {
    /// <summary>The lattice spacing, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Spacing { get; init; } = [];

    internal override void Apply(SdfProgramBuilder builder) {
        builder.Repeat(spacing: JsonVector.ToVector3(components: Spacing));
    }
    internal override void Validate(string path, ValidationErrors errors) {
        errors.RequireVector(path: $"{path}.spacing", components: Spacing, length: 3);
    }
}

/// <summary>Repeats space on a lattice, clamped to a <c>[x, y, z]</c> cell limit (mirrors <see cref="SdfProgramBuilder.RepeatLimited"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepeatLimitedOp : TransformOp {
    /// <summary>The lattice spacing, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Spacing { get; init; } = [];
    /// <summary>The repeat-cell limit per axis, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Limit { get; init; } = [];

    internal override void Apply(SdfProgramBuilder builder) {
        builder.RepeatLimited(
            limit: JsonVector.ToVector3(components: Limit),
            spacing: JsonVector.ToVector3(components: Spacing)
        );
    }
    internal override void Validate(string path, ValidationErrors errors) {
        errors.RequireVector(path: $"{path}.spacing", components: Spacing, length: 3);
        errors.RequireVector(path: $"{path}.limit", components: Limit, length: 3);
    }
}

/// <summary>Twists space about the local Y axis by <see cref="Rate"/> radians per unit of Y (mirrors
/// <see cref="SdfProgramBuilder.TwistY"/>). Not an isometry — the rate is bounded so the march stays stable.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TwistYOp : TransformOp {
    /// <summary>Radians of rotation per unit of local Y, in [−3, 3].</summary>
    public float Rate { get; init; }

    internal override void Apply(SdfProgramBuilder builder) {
        _ = builder.TwistY(rate: Rate);
    }
    internal override void Validate(string path, ValidationErrors errors) {
        RequireRate(errors: errors, maximum: 3f, path: path, rate: Rate);
    }
}

/// <summary>Bends space about the local X axis by <see cref="Rate"/> radians per unit of X (mirrors
/// <see cref="SdfProgramBuilder.BendX"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BendXOp : TransformOp {
    /// <summary>Radians of rotation per unit of local X, in [−1.5, 1.5].</summary>
    public float Rate { get; init; }

    internal override void Apply(SdfProgramBuilder builder) {
        _ = builder.BendX(rate: Rate);
    }
    internal override void Validate(string path, ValidationErrors errors) {
        RequireRate(errors: errors, maximum: 1.5f, path: path, rate: Rate);
    }
}

/// <summary>Bends the XY plane by <see cref="Rate"/> radians per unit of Y (mirrors <see cref="SdfProgramBuilder.BendY"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BendYOp : TransformOp {
    /// <summary>Radians of rotation per unit of local Y, in [−1.5, 1.5].</summary>
    public float Rate { get; init; }

    internal override void Apply(SdfProgramBuilder builder) {
        _ = builder.BendY(rate: Rate);
    }
    internal override void Validate(string path, ValidationErrors errors) {
        RequireRate(errors: errors, maximum: 1.5f, path: path, rate: Rate);
    }
}

/// <summary>Bends the YZ plane by <see cref="Rate"/> radians per unit of Y (mirrors <see cref="SdfProgramBuilder.BendZ"/> —
/// the legacy quirk: keyed on Y, like bendY).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BendZOp : TransformOp {
    /// <summary>Radians of rotation per unit of local Y, in [−1.5, 1.5].</summary>
    public float Rate { get; init; }

    internal override void Apply(SdfProgramBuilder builder) {
        _ = builder.BendZ(rate: Rate);
    }
    internal override void Validate(string path, ValidationErrors errors) {
        RequireRate(errors: errors, maximum: 1.5f, path: path, rate: Rate);
    }
}

/// <summary>Elongates the shape that follows over ±<see cref="Extents"/> — the point clamps into that box, sweeping
/// the shape's cross-section (mirrors <see cref="SdfProgramBuilder.Elongate"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ElongateOp : TransformOp {
    /// <summary>The per-axis elongation half-extents, as a 3-element <c>[x, y, z]</c> array (each in [0, 0.8]).</summary>
    public IReadOnlyList<float> Extents { get; init; } = [];

    internal override void Apply(SdfProgramBuilder builder) {
        _ = builder.Elongate(extents: JsonVector.ToVector3(components: Extents));
    }
    internal override void Validate(string path, ValidationErrors errors) {
        if (JsonVector.IsValid(components: Extents, length: 3)) {
            if ((Extents[0] < 0f) || (Extents[1] < 0f) || (Extents[2] < 0f) || (Extents[0] > 0.8f) || (Extents[1] > 0.8f) || (Extents[2] > 0.8f)) {
                errors.Add(path: $"{path}.extents", message: $"elongation extents must each be in [0, 0.8]; got [{Extents[0]}, {Extents[1]}, {Extents[2]}]");
            }
        } else {
            errors.RequireVector(path: $"{path}.extents", components: Extents, length: 3);
        }
    }
}

/// <summary>Folds the point's in-plane coordinates onto the fundamental cell of a wallpaper symmetry group (mirrors
/// <see cref="SdfProgramBuilder.WallpaperFold"/>): the shapes that follow repeat under the group's mirrors/rotations
/// across the lattice. Content must stay clear of cell boundaries (and of the rotation seams of P2/CMM/P4*) unless a
/// mirror of the group protects that edge.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WallpaperFoldOp : TransformOp {
    /// <summary>The wallpaper group. P4/P4M/P4G and the hex groups (P3 and up) require a SQUARE cell.</summary>
    public SdfWallpaperGroup Group { get; init; }
    /// <summary>The lattice cell extents in the fold plane, as a 2-element <c>[x, y]</c> array (hex pitch = x).</summary>
    public IReadOnlyList<float> Cell { get; init; } = [];
    /// <summary>The repeat-cell limit per plane axis, as a 2-element <c>[x, y]</c> array (RepeatLimited semantics;
    /// axial indices for hex).</summary>
    public IReadOnlyList<float> Limit { get; init; } = [];
    /// <summary>The plane the fold acts on (the third axis is untouched).</summary>
    public SdfWallpaperPlane Plane { get; init; } = SdfWallpaperPlane.XZ;
    /// <summary>The parity-material stride: the cell key (checker parity / hex 3-coloring) times this strides the
    /// material id of the shapes that follow, so each lattice cell selects its own palette row. 0 = geometric only.
    /// The strided ids must stay inside the palette — the validator checks <c>material + stride * maxKey</c>.</summary>
    public int MaterialStride { get; init; }
    /// <summary>The symmetry-LOD distance threshold (0 = off): past it the lattice keeps its copy positions but the
    /// in-cell folds are skipped (upright copies, cheaper and shimmer-free at range).</summary>
    public float LodDistance { get; init; }

    internal override void Apply(SdfProgramBuilder builder) {
        _ = builder.WallpaperFold(
            cell: JsonVector.ToVector2(components: Cell),
            group: Group,
            limit: JsonVector.ToVector2(components: Limit),
            lodDistance: LodDistance,
            materialStride: MaterialStride,
            plane: Plane
        );
    }
    internal override void Validate(string path, ValidationErrors errors) {
        if (!Enum.IsDefined(value: Group)) {
            errors.Add(path: $"{path}.group", message: $"'{Group}' is not a defined SdfWallpaperGroup");
        }

        if (!Enum.IsDefined(value: Plane)) {
            errors.Add(path: $"{path}.plane", message: $"'{Plane}' is not a defined SdfWallpaperPlane");
        }

        if (JsonVector.IsValid(components: Cell, length: 2)) {
            // The fold divides by the cell extents, so a degenerate cell explodes the march — hard floor.
            if ((Cell[0] < 0.05f) || (Cell[1] < 0.05f)) {
                errors.Add(path: $"{path}.cell", message: $"cell extents must be at least 0.05 (the fold divides by them); got [{Cell[0]}, {Cell[1]}]");
            }
        } else {
            errors.RequireVector(path: $"{path}.cell", components: Cell, length: 2);
        }

        if (JsonVector.IsValid(components: Limit, length: 2)) {
            if ((Limit[0] < 0f) || (Limit[1] < 0f)) {
                errors.Add(path: $"{path}.limit", message: $"limits must be non-negative; got [{Limit[0]}, {Limit[1]}]");
            }
        } else {
            errors.RequireVector(path: $"{path}.limit", components: Limit, length: 2);
        }

        errors.RequireFinite(path: $"{path}.lodDistance", name: "lodDistance", value: LodDistance);

        if (LodDistance < 0f) {
            errors.Add(path: $"{path}.lodDistance", message: "lodDistance must be non-negative (0 = off)");
        }

        if (MaterialStride < 0) {
            errors.Add(path: $"{path}.materialStride", message: "materialStride must be non-negative (0 = geometric only)");
        }

        // Quarter-turns about the cell corners (P4*) and the equilateral hex lattice (P3 and up) are only isometries
        // on SQUARE cells; the lattice shape is not a free parameter for those groups.
        if (
            Enum.IsDefined(value: Group) &&
            (Group >= SdfWallpaperGroup.P4) &&
            JsonVector.IsValid(components: Cell, length: 2) &&
            (Cell[0] != Cell[1])
        ) {
            errors.Add(path: $"{path}.cell", message: $"the {Group} group requires a square cell (its rotations/hex lattice are only isometries there); got [{Cell[0]}, {Cell[1]}]");
        }
    }

    // The highest cell key the group's parity coloring produces (the palette-overflow check multiplies it).
    internal int MaxCellKey => ((Group >= SdfWallpaperGroup.P3) ? 2 : 1);
}

/// <summary>Mirrors the point across the YZ plane (mirrors <see cref="SdfProgramBuilder.SymmetryX"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SymmetryXOp : TransformOp {
    internal override void Apply(SdfProgramBuilder builder) {
        builder.SymmetryX();
    }
    internal override void Validate(string path, ValidationErrors errors) {
    }
}

/// <summary>Mirrors the point across the XZ plane (mirrors <see cref="SdfProgramBuilder.SymmetryY"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SymmetryYOp : TransformOp {
    internal override void Apply(SdfProgramBuilder builder) {
        builder.SymmetryY();
    }
    internal override void Validate(string path, ValidationErrors errors) {
    }
}

/// <summary>Mirrors the point across the XY plane (mirrors <see cref="SdfProgramBuilder.SymmetryZ"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SymmetryZOp : TransformOp {
    internal override void Apply(SdfProgramBuilder builder) {
        builder.SymmetryZ();
    }
    internal override void Validate(string path, ValidationErrors errors) {
    }
}
