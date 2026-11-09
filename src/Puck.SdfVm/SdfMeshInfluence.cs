using System.Numerics;
using Puck.Maths;

namespace Puck.SdfVm;

/// <summary>The second owner, its unorm16 share and its share gradient in the rest frame: three packed GPU words beside a
/// skinned vertex's first member (<see cref="SdfMesh.VertexMembers"/>). A vertex whose share leaves one owner stores that
/// owner as its first member and <see cref="Rigid"/> here, so it takes the vertex stage's rigid path and its material
/// layers shade in that owner's frame; <see cref="SdfMeshSkinTuple.Of"/> is the one door that packs a tuple, and the
/// mesh refuses any other (<see cref="Canonical"/>).</summary>
public readonly record struct SdfMeshInfluence {
    /// <summary>The tuple of a vertex that follows its first member alone: no second owner, no share and no gradient.</summary>
    public static readonly SdfMeshInfluence Rigid = new(blend: SdfMesh.NoMember, gradientXY: 0u, gradientZ: 0u);

    internal SdfMeshInfluence(uint blend, uint gradientXY, uint gradientZ) {
        Blend = blend;
        GradientXY = gradientXY;
        GradientZ = gradientZ;
    }

    /// <summary>Gets the second member byte followed by the share's sixteen bits.</summary>
    public uint Blend { get; }
    /// <summary>Gets the two binary16 gradient components X and Y.</summary>
    public uint GradientXY { get; }
    /// <summary>Gets the binary16 Z gradient component, with zero padding.</summary>
    public uint GradientZ { get; }
    /// <summary>Gets the second member index, or the root sentinel.</summary>
    public byte Second => ((byte)Blend);
    /// <summary>Gets the second owner's quantized share.</summary>
    public ushort Share => ((ushort)(Blend >> 8));
    /// <summary>Gets whether the vertex follows both owners.</summary>
    public bool Blended => (Share is > 0 and < ushort.MaxValue);
    /// <summary>Gets whether the tuple is one <see cref="SdfMeshSkinTuple.Of"/> packs: blended, or <see cref="Rigid"/>.</summary>
    public bool Canonical => (Blended || (this == Rigid));
    /// <summary>Gets the share gradient, in reciprocal creation units.</summary>
    public Vector3 Gradient => new(x: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)GradientXY))), y: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(GradientXY >> 16)))), z: ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)GradientZ))));
}
/// <summary>A skinned vertex's first member and its influence (<see cref="SdfMeshInfluence"/>), in canonical form.</summary>
/// <param name="First">The member the vertex rides first, or <see cref="SdfMesh.NoMember"/> for the draw's own slot.</param>
/// <param name="Influence">The second owner, share and gradient.</param>
public readonly record struct SdfMeshSkinTuple(byte First, SdfMeshInfluence Influence) {
    /// <summary>Packs a vertex's two owners, collapsing a share that rounds to either end, or two equal owners, onto the
    /// owner that holds the whole vertex with <see cref="SdfMeshInfluence.Rigid"/>; a blended tuple rounds each gradient
    /// component to binary16.</summary>
    /// <param name="first">The first member, or <see cref="SdfMesh.NoMember"/>.</param>
    /// <param name="second">The second member, or <see cref="SdfMesh.NoMember"/>.</param>
    /// <param name="share">The second owner's share, unorm16.</param>
    /// <param name="gradient">The share's gradient, in reciprocal creation units.</param>
    /// <returns>The canonical tuple.</returns>
    public static SdfMeshSkinTuple Of(byte first, byte second, ushort share, Vector3 gradient) {
        if ((share == 0) || (first == second)) { return new(First: first, Influence: SdfMeshInfluence.Rigid); }
        if (share == ushort.MaxValue) { return new(First: second, Influence: SdfMeshInfluence.Rigid); }

        return new(First: first, Influence: new(blend: (((uint)second) | (((uint)share) << 8)), gradientXY: VectorFunctions.PackHalf2x16(gradient.X, gradient.Y), gradientZ: BitConverter.HalfToUInt16Bits(value: ((Half)gradient.Z))));
    }
}
