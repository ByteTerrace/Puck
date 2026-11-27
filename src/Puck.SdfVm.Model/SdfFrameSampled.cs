using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The word layout of the frame's sampled table (<c>sdfFrameSampled</c>, <see cref="SdfWorldPackage.FrameSampled"/>): the
/// sampled values the render passes read at draw time, as the bake's table holds the values a bake job samples. A word per
/// handle of the frame's program (<see cref="SdfProgram.SampledFields"/>) names the word its entry starts at, or
/// <see cref="None"/> for a handle no render pass reads. A displacement's entry (<see cref="SdfProgram.Displacements"/>) is
/// <see cref="HeaderWords"/> words (<see cref="SdfDisplacement.WriteHeader"/>: its kind, then the kind's counts and float
/// words), then the frame's values and the previous frame's (<see cref="SdfDisplacement.Values"/>, a height field's one
/// float a column X fastest, a sheet's three a node row by row); <c>frame/sdf-mesh.hlsli</c> reads it through
/// <c>sdfMeshDisplaced</c>. <see cref="SdfIsaHlsl"/> writes every constant into the kernels.
/// </summary>
public static class SdfFrameSampled {
    /// <summary>The word a handle with no entry holds, and a draw record's <see cref="SdfMeshRecord.DisplacementWord"/>
    /// when the draw is not displaced.</summary>
    public const uint None = 0xFFFFFFFFu;
    /// <summary>The words of a displacement entry's header (<see cref="SdfDisplacement.HeaderWords"/>).</summary>
    public const int HeaderWords = SdfDisplacement.HeaderWords;
    /// <summary>The kind word of a height field's entry (<see cref="SdfDisplacementKind.HeightField"/>).</summary>
    public const uint HeightFieldKind = (uint)SdfDisplacementKind.HeightField;
    /// <summary>The kind word of a sheet's entry (<see cref="SdfDisplacementKind.Sheet"/>).</summary>
    public const uint SheetKind = (uint)SdfDisplacementKind.Sheet;
    /// <summary>The reference height, in cells above a height field's origin, its baked reference stands at
    /// (<see cref="SdfHeightField.ReferenceCells"/>).</summary>
    public const int ReferenceCells = SdfHeightField.ReferenceCells;
}
