using System.Numerics;
using Puck.Maths;

namespace Puck.SdfVm;

/// <summary>The second owner, its unorm16 share and its share gradient in the rest frame. Three packed GPU words.</summary>
/// <param name="Blend">Second member byte followed by the share's sixteen bits.</param>
/// <param name="GradientXY">Two binary16 gradient components.</param>
/// <param name="GradientZ">The binary16 Z gradient, with zero padding.</param>
public readonly record struct SdfMeshInfluence(uint Blend, uint GradientXY, uint GradientZ) {
    /// <summary>Gets the second member index, or the root sentinel.</summary>
    public byte Second => ((byte)Blend);
    /// <summary>Gets the second owner's quantized share.</summary>
    public ushort Share => ((ushort)(Blend >> 8));
    /// <summary>Gets whether the vertex follows both owners.</summary>
    public bool Blended => (Share is > 0 and < ushort.MaxValue);
    /// <summary>Gets the share gradient, in reciprocal creation units.</summary>
    public Vector3 Gradient => new(x: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)GradientXY))), y: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(GradientXY >> 16)))), z: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)GradientZ))));

    /// <summary>Packs an influence, rounding each gradient component to binary16.</summary>
    public static SdfMeshInfluence Pack(byte second, ushort share, Vector3 gradient) => new(
        Blend: ((uint)second) | (((uint)share) << 8),
        GradientXY: VectorFunctions.PackHalf2x16(gradient.X, gradient.Y),
        GradientZ: BitConverter.HalfToUInt16Bits(value: ((Half)gradient.Z)));
}
