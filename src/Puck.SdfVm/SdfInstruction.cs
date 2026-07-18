using System.Numerics;

namespace Puck.SdfVm;

/// <summary>Represents one decoded SDF VM instruction before it is packed for the GPU.</summary>
/// <param name="Op">The instruction operation.</param>
/// <param name="Shape">The shape identifier for shape instructions, or operation-specific data otherwise.</param>
/// <param name="Blend">The blend identifier for shape instructions, or operation-specific data otherwise.</param>
/// <param name="Material">The material identifier or operation-specific material lane.</param>
/// <param name="Data0">The first operation-specific data vector.</param>
/// <param name="Data1">The second operation-specific data vector.</param>
public readonly record struct SdfInstruction(
    SdfOp Op,
    uint Shape,
    uint Blend,
    uint Material,
    Vector4 Data0,
    Vector4 Data1
);
