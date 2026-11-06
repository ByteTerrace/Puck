using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The word layout of the frame's sampled table (<c>sdfFrameSampled</c>, <see cref="SdfWorldPackage.FrameSampled"/>): the
/// sampled values the render passes read at draw time, as the bake's table holds the values a bake job samples. A word per
/// handle of the frame's program (<see cref="SdfProgram.SampledFields"/>) names the word its entry starts at, or
/// <see cref="None"/> for a handle no render pass reads. A height field's entry (<see cref="SdfProgram.HeightFields"/>) is
/// <see cref="HeaderWords"/> words, its columns along X and Z, then its origin and cell as float bits, then the frame's
/// heights and the previous frame's, each a float a column, X fastest; <c>frame/sdf-mesh.hlsli</c> reads it through
/// <c>sdfMeshDisplaced</c>. <see cref="SdfIsaHlsl"/> writes every constant into the kernels.
/// </summary>
public static class SdfFrameSampled {
    /// <summary>The word a handle with no entry holds, and a draw record's <see cref="SdfMeshRecord.DisplacementWord"/>
    /// when the draw is not displaced.</summary>
    public const uint None = 0xFFFFFFFFu;
    /// <summary>The words of a height field entry's header: columns along X, columns along Z, the origin's three
    /// coordinates and the cell.</summary>
    public const int HeaderWords = 6;
    /// <summary>The reference height, in cells above a height field's origin, its baked reference stands at
    /// (<see cref="SdfHeightField.ReferenceCells"/>).</summary>
    public const int ReferenceCells = SdfHeightField.ReferenceCells;
}
