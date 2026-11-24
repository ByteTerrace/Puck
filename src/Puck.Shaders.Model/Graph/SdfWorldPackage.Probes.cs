using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The shading sample spacing, classification tile edge and history output dimensions in pixels.</summary>
    public const string ViewsRate = "viewsRate";
    /// <summary>The probe lookup's records as views reads them: <see cref="ViewsProbeWords"/> words a cell of
    /// <see cref="ViewsProbeRate"/> by <see cref="ViewsProbeRate"/> render-extent pixels. Each cell holds a record of
    /// <see cref="ViewsProbeRecordWords"/> words, addressed by cell like the visibility record by pixel, then its share
    /// of the pool of extra slots, then a mix a pixel (<c>sdf-views-probes.hlsli</c> owns the layout).</summary>
    public const string ViewsProbeRecords = "sdfViewsProbeRecords";
    /// <summary>The probe lookup's records as the probe lookup pass writes them.</summary>
    public const string ViewsProbeRecordsWritten = "sdfViewsProbeRecordsRW";
    /// <summary>The edge, in render-extent pixels, of the square cell one probe lookup serves: the view gathers the
    /// irradiance volume's probes once a cell, at its representative pixel, and each pixel blends the representatives
    /// of the four cells about it whose receivers are compatible with its own. It divides
    /// <see cref="ShaderPipelineStorageCounts.TileEdge"/>, the edge of the tile the records are counted per, so a tile
    /// holds whole cells. <c>SdfIsaHlsl</c> generates it as <c>SDF_VIEWS_PROBE_RATE</c>.</summary>
    public const uint ViewsProbeRate = 4;
    /// <summary>The words of one slot: everything a shading sample reads of one gather of the irradiance volume, which
    /// the probe lookup resolves so that no shading kernel holds a gather. Its share and flags, its blended six-axis
    /// cubes in fourteen words (the light a surface receives and the radiance a reflection adds, a shared-exponent
    /// colour a face, and the sky shares), its traced lights' agreement in nine (the light bits every measured corner
    /// holds, those any holds and those corners' summed weight) and four words listing the lights its corners disagree
    /// on with each one's share. <c>SdfIsaHlsl</c> generates it as <c>SDF_VIEWS_PROBE_SLOT_WORDS</c>.</summary>
    public const uint ViewsProbeSlotWords = 28;
    /// <summary>The words of one cell's record: its representative's slot, then the pool slot of the first of the cell's
    /// extras. <c>SdfIsaHlsl</c> generates it as <c>SDF_VIEWS_PROBE_RECORD_WORDS</c>.</summary>
    public const uint ViewsProbeRecordWords = (ViewsProbeSlotWords + 1);
    /// <summary>The words a cell adds to the pool of extra slots, which follows the records in the same buffer: a count of
    /// the slots taken, which <c>SdfWorldPassRecorder</c> zeroes before each lookup, then the slots. A pixel that no
    /// representative about it serves shares one with the pixels of its cell compatible with it; a slot a cell holds an
    /// extra for every cell, and a group of cells the pool has no room for keeps none, its pixels reading their
    /// representatives'. <c>SdfIsaHlsl</c> generates it as <c>SDF_VIEWS_PROBE_POOL_WORDS</c>.</summary>
    public const uint ViewsProbePoolWords = ViewsProbeSlotWords;
    /// <summary>The words a cell's pixels' mixes take, which follow the pool: one a pixel, naming the slots the pixel
    /// blends and their shares.</summary>
    public const uint ViewsProbeMixWords = (ViewsProbeRate * ViewsProbeRate);
    /// <summary>The words the records' buffer holds for each cell: its record, its share of the pool and its pixels'
    /// mixes. <c>SdfIsaHlsl</c> generates it as <c>SDF_VIEWS_PROBE_WORDS</c>.</summary>
    public const uint ViewsProbeWords = ((ViewsProbeRecordWords + ViewsProbePoolWords) + ViewsProbeMixWords);

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
