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
    /// <summary>The words of one cell's probe record: two cages, sixteen UNORM16 weights, a state word and the gather's
    /// blended six-axis cube in twenty-one words of half floats. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_VIEWS_PROBE_WORDS</c>.</summary>
    public const uint ViewsProbeWords = 32;

    // The cells of one tile of the extent, the basis the records are counted on.
    private const uint ViewsProbeCellsPerTile = ((ShaderPipelineStorageCounts.TileEdge / ViewsProbeRate) * (ShaderPipelineStorageCounts.TileEdge / ViewsProbeRate));

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
    }
}
