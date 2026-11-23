using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The shading sample spacing, classification tile edge and history output dimensions in pixels.</summary>
    public const string ViewsRate = "viewsRate";
    /// <summary>The probe lookup's records as views reads them: <see cref="ViewsProbeWords"/> words a cell of
    /// <see cref="ViewsProbeRate"/> by <see cref="ViewsProbeRate"/> render-extent pixels, the irradiance volume's gather at
    /// one representative pixel of the cell (<c>sdf-views-probes.hlsli</c> owns the packing), addressed by cell like the
    /// visibility record by pixel.</summary>
    public const string ViewsProbeRecords = "sdfViewsProbeRecords";
    /// <summary>The probe lookup's records as the probe lookup pass writes them.</summary>
    public const string ViewsProbeRecordsWritten = "sdfViewsProbeRecordsRW";
    /// <summary>The edge, in render-extent pixels, of the square cell one probe lookup serves: the view gathers the
    /// irradiance volume's probes once a cell, and views reuses that gather for each pixel of the cell whose receiver is
    /// compatible with the representative's. It divides <see cref="ShaderPipelineStorageCounts.TileEdge"/>, the edge of
    /// the tile the records are counted per, so a tile holds whole cells.
    /// <c>SdfIsaHlsl</c> generates it as <c>SDF_VIEWS_PROBE_RATE</c>.</summary>
    public const uint ViewsProbeRate = 2;
    /// <summary>The words of one cell's probe record: the representative's gather (two cages, sixteen UNORM16 weights, a
    /// state word and its blended six-axis cubes in fourteen words: the light a surface receives and the radiance a
    /// reflection adds, a shared-exponent colour a face, and the sky shares), then an extra slot of
    /// <see cref="ViewsProbeExtraWords"/> words for each other pixel of the cell, which holds that pixel's own gather where
    /// its receiver is not compatible with the representative's, then the representative's traced-light agreement
    /// (<see cref="ViewsProbeLightWords"/>). <c>SdfIsaHlsl</c> generates it as <c>SDF_VIEWS_PROBE_WORDS</c>.</summary>
    public const uint ViewsProbeWords = (ViewsProbeGatherWords + (((ViewsProbeRate * ViewsProbeRate) - 1) * ViewsProbeExtraWords) + ViewsProbeLightWords);
    /// <summary>The words of a record's traced-light agreement: the light bits every measured corner of the
    /// representative's gather holds and those any holds, a word each for every 32 lights the irradiance volume traces
    /// (four), and those corners' summed weight.</summary>
    public const uint ViewsProbeLightWords = 9;
    /// <summary>The words of a cell's record before its extra slots: the representative's gather and blended cube.</summary>
    public const uint ViewsProbeGatherWords = 25;
    /// <summary>The words of one extra slot: two cages, sixteen UNORM16 weights and the share and measured flags.</summary>
    public const uint ViewsProbeExtraWords = 11;

    /// <summary>The preceding render's probe gathers a view's probe lookup reuses: <see cref="ViewsProbeHistoryWords"/>
    /// words a cell, the gather the cell's representative read and the receiver and volume state it was taken at
    /// (<c>sdf-views-probe-history.hlsli</c> owns the packing). Every view keeps one at its own render extent, a nested
    /// view's at the share of the display it is shown at, since a kept gather validates itself against the receiver it
    /// was taken at and needs no jittered history. It is history, so it keeps two allocations: the one this render reads
    /// and the one it writes.</summary>
    public const string ViewsProbeHistory = "sdfViewsProbeHistory";
    /// <summary>The probe gathers the probe lookup writes for the next frame.</summary>
    public const string ViewsProbeHistoryWritten = "sdfViewsProbeHistoryRW";
    /// <summary>The preceding render's probe gathers the glass shading pass reuses at each cell's nearest glass face, laid
    /// out as <see cref="ViewsProbeHistory"/> and kept beside it in every fragment with the glass passes
    /// (<see cref="WithGlass"/>): a pane is lit apart from the surface behind it, so its gathers are its own.</summary>
    public const string GlassProbeHistory = "sdfGlassProbeHistory";
    /// <summary>The glass probe gathers the glass shading pass writes for the next render.</summary>
    public const string GlassProbeHistoryWritten = "sdfGlassProbeHistoryRW";
    /// <summary>The words of one cell's entry in <see cref="ViewsProbeHistory"/> and <see cref="GlassProbeHistory"/>: the
    /// receiver's point and normal, the gather's cages, weights and flags as a record holds them with the level it chose,
    /// and the volume's two serials (<c>sdf-probe-history.hlsli</c>). <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_VIEWS_PROBE_HISTORY_WORDS</c>.</summary>
    public const uint ViewsProbeHistoryWords = 17;

    // The cells of one tile of the extent, the basis the records are counted on.
    private const uint ViewsProbeCellsPerTile = ((ShaderPipelineStorageCounts.TileEdge / ViewsProbeRate) * (ShaderPipelineStorageCounts.TileEdge / ViewsProbeRate));

    // The probe lookup's history: an entry a cell, every view's cells, at the records' basis (a reduced or temporal view's
    // render extent through AtRenderExtent), two allocations, zero until the lookup first writes it (a zero entry holds no
    // gather).
    private static ShaderPipelineResource ProbeHistory => new(
        Count: [Term((ViewsProbeHistoryWords * ViewsProbeCellsPerTile), ShaderPipelineCountBasis.ExtentTiles, ShaderPipelineCountBasis.Viewports)],
        History: true,
        Initialization: ShaderPipelineInitialization.Zero,
        Kind: ShaderPipelineResourceKind.Buffer,
        Name: Parts.ProbeHistory,
        StrideBytes: sizeof(uint)
    );

    // The probe lookup pass: the visibility records and the preceding render's history in, the records and this render's
    // history out.
    private static RenderGraphFragmentPass ProbeLookup => new(
        CountsKernelWork: true,
        InputAccesses: [RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead],
        Inputs: [new ResourceReference(Name: Parts.Visibility), new ResourceReference(Name: Parts.ProbeHistory, PreviousFrame: true)],
        Name: Parts.ViewsProbes,
        OutputAccesses: [RenderGraphPortAccess.ComputeWrite, RenderGraphPortAccess.ComputeWrite],
        Outputs: [new ResourceReference(Name: Parts.Probes), new ResourceReference(Name: Parts.ProbeHistory)]
    );

    // The probe lookup's records: a record a cell, every view's cells, at the visibility record's extent basis.
    private static ShaderPipelineResource ProbeRecords => Buffer(
        count: [Term((ViewsProbeWords * ViewsProbeCellsPerTile), ShaderPipelineCountBasis.ExtentTiles, ShaderPipelineCountBasis.Viewports)],
        name: Parts.Probes,
        sizeBytes: null,
        strideBytes: sizeof(uint)
    );

    public static partial class Parts {
        /// <summary>The probe lookup pass: the irradiance volume's gather once a cell of the render extent, into the
        /// probe records views reads.</summary>
        public const string ViewsProbes = "views-probes";
        /// <summary>The probe records the probe lookup pass writes.</summary>
        public const string Probes = "viewsProbes";
        /// <summary>The probe lookup's history of gathers (<see cref="ViewsProbeHistory"/>).</summary>
        public const string ProbeHistory = "viewsProbeHistory";
    }
}
