using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The word layout of the frame's sampled table (<c>sdfFrameSampled</c>, <see cref="SdfWorldPackage.FrameSampled"/>): the
/// sampled values the render passes read at draw time, as the bake's table holds the values a bake job samples. It opens
/// with <see cref="TableHeaderWords"/> words: the frame's form band count, the word its bands start at and the motes
/// they generate together (<see cref="TableBandCountWord"/>, <see cref="TableBandBaseWord"/>,
/// <see cref="TableMoteTotalWord"/>). A word per handle of the frame's program (<see cref="SdfProgram.SampledFields"/>),
/// from <see cref="TableHeaderWords"/> on, names the word its entry starts at, or <see cref="None"/> for a handle no
/// render pass reads. A displacement's entry (<see cref="SdfProgram.Displacements"/>) is
/// <see cref="HeaderWords"/> words (<see cref="SdfDisplacement.WriteHeader"/>: its kind, then the kind's counts and float
/// words), then the frame's values and the previous frame's (<see cref="SdfDisplacement.Values"/>, a height field's one
/// float a column X fastest, a sheet's three a node row by row); <c>frame/sdf-mesh.hlsli</c> reads it through
/// <c>sdfMeshDisplaced</c>. Each form band (<c>SdfFormBand</c>) is <see cref="BandWords"/> words, in the order of their first
/// slots, which <c>frame/sdf-form-band.hlsli</c> reads: the run, the side and seed, the sweep, the edge, the motes and the
/// draw rows the motes take their surface points from (<see cref="BandRowsWord"/>, at most <see cref="BandRowCapacity"/>).
/// <see cref="SdfIsaHlsl"/> writes every constant into the kernels.
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
    /// <summary>The words the table opens with, before its handle words.</summary>
    public const int TableHeaderWords = 4;
    /// <summary>The header word holding the frame's form band count.</summary>
    public const int TableBandCountWord = 0;
    /// <summary>The header word holding the word the first form band starts at.</summary>
    public const int TableBandBaseWord = 1;
    /// <summary>The header word holding the motes every form band generates together.</summary>
    public const int TableMoteTotalWord = 2;
    /// <summary>The most form bands one frame's table holds.</summary>
    public const int MaxBands = 512;
    /// <summary>The most motes one frame's form bands generate together.</summary>
    public const int MaxMotes = (1 << 18);
    /// <summary>The words of one form band.</summary>
    public const int BandWords = 40;
    /// <summary>The most draw rows a form band's motes take surface points from.</summary>
    public const int BandRowCapacity = 8;
    /// <summary>A form band's first slot.</summary>
    public const int BandFirstSlotWord = 0;
    /// <summary>A form band's slot count.</summary>
    public const int BandSlotCountWord = 1;
    /// <summary>A form band's flags (<see cref="BandSweptFlag"/>).</summary>
    public const int BandFlagsWord = 2;
    /// <summary>A form band's seed.</summary>
    public const int BandSeedWord = 3;
    /// <summary>A form band's origin, three floats, then its start along its axis.</summary>
    public const int BandOriginWord = 4;
    /// <summary>A form band's unit axis, three floats, then its end along it.</summary>
    public const int BandAxisWord = 8;
    /// <summary>A form band's edge light, three floats of nits, then the edge's width.</summary>
    public const int BandEdgeWord = 12;
    /// <summary>A form band's roughness, then its noise frequency, its elapsed seconds and its sweep's seconds.</summary>
    public const int BandSweepWord = 16;
    /// <summary>A form band's mote light, three floats of nits, then a mote's diameter.</summary>
    public const int BandMoteLightWord = 20;
    /// <summary>A form band's mote count, then its first mote among the frame's, a mote's life in seconds and its drift
    /// speed.</summary>
    public const int BandMoteWord = 24;
    /// <summary>A form band's mote rise, then its swirl, then its draw row count.</summary>
    public const int BandMotionWord = 28;
    /// <summary>A form band's first draw row; <see cref="BandRowCapacity"/> words.</summary>
    public const int BandRowsWord = 32;
    /// <summary>The flag of a form band whose run is drawn where its front has passed (a materializing run).</summary>
    public const uint BandSweptFlag = 1u;
    /// <summary>The flag of a form band whose run's slots move apart (a form shed or assembled piece by piece): a draw
    /// blending several of its slots, a skinned seam, would stretch between them, so it is not drawn.</summary>
    public const uint BandPartedFlag = 2u;
}
