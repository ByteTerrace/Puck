using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>Represents one decoded SDF VM instruction before it is packed for the GPU.</summary>
/// <param name="Op">The instruction operation.</param>
/// <param name="Shape">The shape identifier for shape instructions, or operation-specific data otherwise.</param>
/// <param name="Blend">The blend identifier for shape instructions, or operation-specific data otherwise.</param>
/// <param name="Material">The material identifier or operation-specific material lane.</param>
/// <param name="Data0">The first operation-specific data vector.</param>
/// <param name="Data1">The second operation-specific data vector.</param>
/// <param name="Detail">Whether a <see cref="SdfOp.ShapeBlend"/> instruction is a detail shape: it bakes into the mesh
/// like any other shape but is excluded from contact (<c>SdfFieldEvaluator</c>'s contact queries skip it). The GPU
/// encoding does not carry it. Meaningful only when <see cref="Op"/> is <see cref="SdfOp.ShapeBlend"/>; false
/// elsewhere.</param>
public readonly record struct SdfInstruction(
    SdfOp Op,
    uint Shape,
    uint Blend,
    uint Material,
    Vector4 Data0,
    Vector4 Data1,
    bool Detail = false
);
