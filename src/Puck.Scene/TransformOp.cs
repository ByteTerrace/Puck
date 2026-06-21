using System.Text.Json.Serialization;
using Puck.SdfVm;

namespace Puck.Scene;

/// <summary>
/// One transform in an object's <c>ops</c> chain, applied to the current point before the terminal shape is emitted.
/// The set mirrors the public transform verbs on <see cref="SdfProgramBuilder"/> one-for-one; the <c>op</c> string is
/// the JSON type discriminator. Adding a transform is a new derived record (carrying its own <see cref="Apply"/> and
/// <see cref="Validate"/>) — never an edit to a central switch.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(TranslateOp), typeDiscriminator: "translate")]
[JsonDerivedType(typeof(RotateOp), typeDiscriminator: "rotate")]
[JsonDerivedType(typeof(ScaleOp), typeDiscriminator: "scale")]
[JsonDerivedType(typeof(RepeatOp), typeDiscriminator: "repeat")]
[JsonDerivedType(typeof(RepeatLimitedOp), typeDiscriminator: "repeatLimited")]
[JsonDerivedType(typeof(SymmetryXOp), typeDiscriminator: "symmetryX")]
[JsonDerivedType(typeof(SymmetryYOp), typeDiscriminator: "symmetryY")]
[JsonDerivedType(typeof(SymmetryZOp), typeDiscriminator: "symmetryZ")]
public abstract record TransformOp {
    // Applies this transform's builder verb. The point reset + every preceding op have already run.
    internal abstract void Apply(SdfProgramBuilder builder);
    // Appends any structural errors (vector arity, finiteness) for this op under the given json path.
    internal abstract void Validate(string path, ValidationErrors errors);
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
