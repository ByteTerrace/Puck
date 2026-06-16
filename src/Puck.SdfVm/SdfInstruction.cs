using System.Numerics;

namespace Puck.SdfVm;

public readonly record struct SdfInstruction(
    SdfOp Op,
    uint Shape,
    uint Blend,
    uint Material,
    Vector4 Data0,
    Vector4 Data1
);
