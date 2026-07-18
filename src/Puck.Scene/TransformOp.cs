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
[JsonDerivedType(typeof(CellJitterOp), typeDiscriminator: "cellJitter")]
[JsonDerivedType(typeof(LogSphereOp), typeDiscriminator: "logSphere")]
[JsonDerivedType(typeof(RepeatPolarOp), typeDiscriminator: "repeatPolar")]
[JsonDerivedType(typeof(SymmetryPlaneOp), typeDiscriminator: "symmetryPlane")]
[JsonDerivedType(typeof(DomainWarpOp), typeDiscriminator: "domainWarp")]
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

    // The shared budget guard of the sinusoidal warp ops (SceneObject's Displace FIELD op / DomainWarpOp's POINT op):
    // frequency must be a finite 3-vector, amplitude finite, and their product (the warp's peak metric stretch — see
    // each op's doc) bounded, or a large product clamps the march to tiny steps (matching RequireRate's "not an
    // isometry" rationale for twist/bend). Internal (not private protected) because SceneObject.Displace — a scoped
    // FIELD op like Dilate/Onion, not a point op in the Ops chain — reuses it too; see the note on SceneObject.Displace.
    internal static void RequireWarpBudget(string path, IReadOnlyList<float> frequency, float amplitude, float maximum, ValidationErrors errors) {
        errors.RequireFinite(path: $"{path}.amplitude", name: "amplitude", value: amplitude);

        if (!JsonVector.IsValid(components: frequency, length: 3)) {
            errors.RequireVector(path: $"{path}.frequency", components: frequency, length: 3);

            return;
        }

        if (!float.IsFinite(f: amplitude)) {
            return;
        }

        var norm = MathF.Sqrt(x: (((frequency[0] * frequency[0]) + (frequency[1] * frequency[1])) + (frequency[2] * frequency[2])));
        var budget = (MathF.Abs(x: amplitude) * norm);

        if (budget > maximum) {
            errors.Add(path: path, message: $"amplitude * |frequency| ({budget}) exceeds {maximum} — the warp is not 1-Lipschitz and a large product clamps the march to tiny steps; reduce amplitude or frequency");
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

/// <summary>Hashes the cell the point falls in and jitters/spins/re-materials its content (mirrors
/// <see cref="SdfProgramBuilder.CellJitter"/>): the shapes that follow scatter across the lattice instead of
/// repeating rigidly. The caller must keep <see cref="Jitter"/>/2 plus the prototype's own radius within half the
/// smallest <see cref="Spacing"/> component, or the displaced content holes the march.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CellJitterOp : TransformOp {
    /// <summary>The per-axis cell spacing in world units, as a 3-element <c>[x, y, z]</c> array (each clamped to
    /// ≥ 0.001).</summary>
    public IReadOnlyList<float> Spacing { get; init; } = [];
    /// <summary>The peak-to-peak per-cell position displacement in world units (0 = no displacement).</summary>
    public float Jitter { get; init; }
    /// <summary>The hash seed — different seeds give independent jitter/tumble/variant fields.</summary>
    public uint Seed { get; init; }
    /// <summary>The per-cell rotation amount in [0, 1]: 0 = no rotation, 1 = up to ±π about a random axis.</summary>
    public float Tumble { get; init; }
    /// <summary>The number of hashed material rows (0 = geometric only): a hit in a cell adds a hashed
    /// 0..variants-1 to its shape's material id.</summary>
    public int MaterialVariants { get; init; }
    /// <summary>How the per-cell position offset is distributed: <c>White</c> (independent uniform — the default),
    /// <c>Blue</c> (R3 low-discrepancy — a de-clumped, more even scatter), or <c>Gaussian</c> (central-limit — offsets
    /// cluster toward the cell centre).</summary>
    public SdfNoiseFlavor Flavor { get; init; } = SdfNoiseFlavor.White;

    internal override void Apply(SdfProgramBuilder builder) {
        _ = builder.CellJitter(
            flavor: Flavor,
            jitter: Jitter,
            materialVariants: MaterialVariants,
            seed: Seed,
            spacing: JsonVector.ToVector3(components: Spacing),
            tumble: Tumble
        );
    }
    internal override void Validate(string path, ValidationErrors errors) {
        errors.RequireFinite(path: $"{path}.jitter", name: "jitter", value: Jitter);
        errors.RequireFinite(path: $"{path}.tumble", name: "tumble", value: Tumble);

        if ((Tumble < 0f) || (Tumble > 1f)) {
            errors.Add(path: $"{path}.tumble", message: $"tumble {Tumble} must be in [0, 1]");
        }

        if (MaterialVariants < 0) {
            errors.Add(path: $"{path}.materialVariants", message: "materialVariants must be non-negative (0 = geometric only)");
        }

        if (JsonVector.IsValid(components: Spacing, length: 3)) {
            var minSpacing = MathF.Max(x: 0.001f, y: MathF.Min(x: Spacing[0], y: MathF.Min(x: Spacing[1], y: Spacing[2])));

            if (float.IsFinite(f: Jitter) && ((MathF.Abs(x: Jitter) * 0.5f) >= (0.5f * minSpacing))) {
                errors.Add(path: $"{path}.jitter", message: $"jitter/2 must be < min(spacing)/2, or the displaced content crosses the cell boundary and holes the march; got jitter {Jitter} vs spacing [{Spacing[0]}, {Spacing[1]}, {Spacing[2]}]");
            }
        } else {
            errors.RequireVector(path: $"{path}.spacing", components: Spacing, length: 3);
        }
    }
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

/// <summary>Log-spherical domain warp: tiles space into infinite self-similar "Droste" shells (mirrors
/// <see cref="SdfProgramBuilder.LogSphere"/>). NOT an isometry — the march stays hole-free via a baked conservative
/// step clamp, so (unlike the bend/twist rate guard) there is no hard amplitude ceiling here; the one hard invariant
/// is the shell ratio itself, which the builder would otherwise silently re-clamp.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LogSphereOp : TransformOp {
    /// <summary>The Cartesian scale factor between consecutive shells; must be greater than 1 (a ratio at or below 1
    /// collapses the shells / divides by zero, and the builder would silently clamp it to 1.0001).</summary>
    public float ShellRatio { get; init; }
    /// <summary>Radians of Z-spin added per shell (the Droste spiral). 0 = concentric, un-spun shells.</summary>
    public float Twist { get; init; }

    internal override void Apply(SdfProgramBuilder builder) {
        _ = builder.LogSphere(shellRatio: ShellRatio, twist: Twist);
    }
    internal override void Validate(string path, ValidationErrors errors) {
        errors.RequireFinite(path: $"{path}.shellRatio", name: "shellRatio", value: ShellRatio);
        errors.RequireFinite(path: $"{path}.twist", name: "twist", value: Twist);

        if (float.IsFinite(f: ShellRatio) && (ShellRatio <= 1.0001f)) {
            errors.Add(path: $"{path}.shellRatio", message: $"shellRatio {ShellRatio} must be greater than 1.0001 (a ratio at or below it collapses the shells / divides by zero; the builder would silently clamp it)");
        }
    }
}

/// <summary>Angular domain-repeat fold: folds the plane perpendicular to <see cref="Axis"/> into <see cref="Sectors"/>
/// equal wedges, so the shapes that follow repeat rotationally around the axis (mirrors
/// <see cref="SdfProgramBuilder.RepeatPolar"/>). Both the fold and its optional mirror are isometries — 1-Lipschitz,
/// no step clamp.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepeatPolarOp : TransformOp {
    /// <summary>The number of sectors around the axis; must be at least 1 (the builder would otherwise silently
    /// clamp it — 1 is itself a valid single-full-circle no-op).</summary>
    public int Sectors { get; init; }
    /// <summary>The rotation axis — the fold acts in the plane perpendicular to it. Defaults to <see cref="SdfPolarAxis.Y"/>
    /// (the XZ ground plane).</summary>
    public SdfPolarAxis Axis { get; init; } = SdfPolarAxis.Y;
    /// <summary>When true, reflects each sector across its bisector for kaleidoscope symmetry (still an isometry).</summary>
    public bool Mirror { get; init; }
    /// <summary>The per-sector palette stride: the sector index (0..sectors-1) times this strides the material id of a
    /// later shape win, so each sector can select its own palette row. 0 (the default) keeps the fold purely geometric.
    /// The strided ids must stay inside the palette — the validator checks <c>material + stride * (sectors - 1)</c>,
    /// the same cross-cutting rule <see cref="WallpaperFoldOp"/> applies for its cell key.</summary>
    public int MaterialStride { get; init; }

    internal override void Apply(SdfProgramBuilder builder) {
        _ = builder.RepeatPolar(axis: Axis, count: Sectors, materialStride: MaterialStride, mirror: Mirror);
    }
    internal override void Validate(string path, ValidationErrors errors) {
        if (!Enum.IsDefined(value: Axis)) {
            errors.Add(path: $"{path}.axis", message: $"'{Axis}' is not a defined SdfPolarAxis");
        }

        if (Sectors < 1) {
            errors.Add(path: $"{path}.sectors", message: $"sectors {Sectors} must be at least 1 (the builder would silently clamp it)");
        }

        if (MaterialStride < 0) {
            errors.Add(path: $"{path}.materialStride", message: "materialStride must be non-negative (0 = geometric only)");
        }
    }

    // The highest sector index the fold produces — the palette-overflow cross-check in SceneObject.Validate multiplies
    // it by MaterialStride, mirroring WallpaperFoldOp.MaxCellKey.
    internal int MaxSectorIndex => Math.Max(val1: 0, val2: (Sectors - 1));
}

/// <summary>Reflection fold across an arbitrary plane (mirrors <see cref="SdfProgramBuilder.SymmetryPlane"/>) — the
/// general-normal superset of the axis-aligned <see cref="SymmetryXOp"/>/<see cref="SymmetryYOp"/>/<see cref="SymmetryZOp"/>.
/// A reflection is an isometry — 1-Lipschitz, no step clamp.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SymmetryPlaneOp : TransformOp {
    /// <summary>The plane normal (need not be unit length — normalized at build time), as a 3-element <c>[x, y, z]</c>
    /// array. Everything on the negative side is mirrored onto the positive (kept) side.</summary>
    public IReadOnlyList<float> Normal { get; init; } = [];
    /// <summary>The plane's constant term: the mirror plane is <c>dot(p, normal) + offset = 0</c>. 0 puts it through
    /// the local origin.</summary>
    public float Offset { get; init; }

    internal override void Apply(SdfProgramBuilder builder) {
        builder.SymmetryPlane(normal: JsonVector.ToVector3(components: Normal), offset: Offset);
    }
    internal override void Validate(string path, ValidationErrors errors) {
        errors.RequireFinite(path: $"{path}.offset", name: "offset", value: Offset);

        if (!JsonVector.IsValid(components: Normal, length: 3)) {
            errors.RequireVector(path: $"{path}.normal", components: Normal, length: 3);
        } else if ((Normal[0] == 0f) && (Normal[1] == 0f) && (Normal[2] == 0f)) {
            errors.Add(path: $"{path}.normal", message: "the symmetry-plane normal must be non-zero");
        }
    }
}

/// <summary>Warps the sample point by a bounded, cross-coupled sinusoidal field before the shapes evaluate — organic
/// bulging/wobble/terrain (mirrors <see cref="SdfProgramBuilder.DomainWarp"/>). A POINT op: order it before the shapes
/// it should warp, like the fold ops. NOT an isometry — <c>amplitude·‖frequency‖</c> is bounded, same rationale as
/// <see cref="SceneObject.Displace"/>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DomainWarpOp : TransformOp {
    /// <summary>Per-axis angular frequency of the warp (radians per world unit), as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Frequency { get; init; } = [];
    /// <summary>Peak point displacement (world units; 0 = an exact identity).</summary>
    public float Amplitude { get; init; }

    internal override void Apply(SdfProgramBuilder builder) {
        _ = builder.DomainWarp(amplitude: Amplitude, frequency: JsonVector.ToVector3(components: Frequency));
    }
    internal override void Validate(string path, ValidationErrors errors) {
        RequireWarpBudget(amplitude: Amplitude, errors: errors, frequency: Frequency, maximum: 3f, path: path);
    }
}
