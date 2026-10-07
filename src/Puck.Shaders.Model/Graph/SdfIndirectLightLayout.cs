namespace Puck.Shaders;

/// <summary>The bounded depth-map bank and metadata layout, shared by the host and generated shader declarations.</summary>
public static class SdfIndirectLightLayout {
    /// <summary>The finest and coarsest allocated receiver regions per held or incoming light.</summary>
    public const int RegionsPerLight = 2;
    /// <summary>The square depth map's edge in texels.</summary>
    public const int Resolution = 512;
    /// <summary>The maximum maps at the largest supported shadow-slot policy.</summary>
    public const int MaxMaps = 12;
    /// <summary>The float4 rows of projection, bounds, identity and generation metadata per map.</summary>
    public const int MetadataRows = 7;
    /// <summary>The bounded full-field evaluation allowance for a light-map texel or fallback shadow ray.</summary>
    public const int MarchSteps = 128;
    /// <summary>The cone entry, gap and far searches' combined field-query cap per participating tile.</summary>
    public const int BeamSteps = 82;
    /// <summary>The bytes in one single-precision depth map.</summary>
    public const int MapBytes = ((Resolution * Resolution) * sizeof(float));
    /// <summary>The row granularity shared with the primary and depth-copy workgroups.</summary>
    public const int SliceRowEdge = 8;
    /// <summary>The map selector's low bits; zero denotes an ordinary camera.</summary>
    public const int SliceMapMask = 15;
    /// <summary>The first row group's bit offset.</summary>
    public const int SliceFirstShift = 4;
    /// <summary>The first row group's bit mask before shifting.</summary>
    public const int SliceFirstMask = 63;
    /// <summary>The row group count's bit offset.</summary>
    public const int SliceCountShift = 10;
    /// <summary>The row group count's bit mask before shifting.</summary>
    public const int SliceCountMask = 127;
    /// <summary>The first column group's bit offset.</summary>
    public const int SliceColumnFirstShift = 17;
    /// <summary>The column group count's bit offset.</summary>
    public const int SliceColumnCountShift = 23;
    /// <summary>The defined bits of a packed light slice.</summary>
    public const int SliceMask = SliceMapMask | (SliceFirstMask << SliceFirstShift) | (SliceCountMask << SliceCountShift) |
        (SliceFirstMask << SliceColumnFirstShift) | (SliceCountMask << SliceColumnCountShift);

    /// <summary>Packs one map's aligned, nonempty row interval into the shared pass word.</summary>
    /// <param name="map">The zero-based map index.</param>
    /// <param name="firstRow">The first row, aligned to the workgroup edge.</param>
    /// <param name="rowCount">The aligned number of rows.</param>
    /// <param name="firstColumn">The first aligned column.</param>
    /// <param name="columnCount">The aligned number of columns.</param>
    /// <returns>The map selector and row interval.</returns>
    public static uint PackSlice(int map, int firstRow, int rowCount, int firstColumn = 0, int columnCount = Resolution) {
        ArgumentOutOfRangeException.ThrowIfNegative(map);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(map, MaxMaps);
        ArgumentOutOfRangeException.ThrowIfNegative(firstRow);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(firstRow, Resolution);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowCount, (Resolution - firstRow));
        ArgumentOutOfRangeException.ThrowIfNegative(firstColumn);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(firstColumn, Resolution);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columnCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columnCount, (Resolution - firstColumn));
        if (((firstRow % SliceRowEdge) != 0) || ((rowCount % SliceRowEdge) != 0) ||
            ((firstColumn % SliceRowEdge) != 0) || ((columnCount % SliceRowEdge) != 0)) {
            throw new ArgumentException(message: "Light slices cover whole workgroups.");
        }
        return ((uint)((map + 1) | ((firstRow / SliceRowEdge) << SliceFirstShift) | ((rowCount / SliceRowEdge) << SliceCountShift) |
            ((firstColumn / SliceRowEdge) << SliceColumnFirstShift) | ((columnCount / SliceRowEdge) << SliceColumnCountShift)));
    }
    /// <summary>Decodes a complete light slice; ordinary cameras and malformed words authorize no map.</summary>
    /// <param name="slice">The packed pass word.</param>
    /// <param name="map">The zero-based map index, or -1.</param>
    /// <param name="firstRow">The first row, or zero.</param>
    /// <param name="rowCount">The row count, or zero.</param>
    /// <param name="firstColumn">The first column, or zero.</param>
    /// <param name="columnCount">The column count, or zero.</param>
    /// <returns>Whether every encoded field is valid.</returns>
    public static bool TryUnpackSlice(uint slice, out int map, out int firstRow, out int rowCount, out int firstColumn, out int columnCount) {
        var selector = ((int)(slice & SliceMapMask));
        var first = ((int)((slice >> SliceFirstShift) & SliceFirstMask));
        var count = ((int)((slice >> SliceCountShift) & SliceCountMask));
        var columnFirst = ((int)((slice >> SliceColumnFirstShift) & SliceFirstMask));
        var columnSize = ((int)((slice >> SliceColumnCountShift) & SliceCountMask));

        map = -1; firstRow = 0; rowCount = 0; firstColumn = 0; columnCount = 0;
        if (((slice & ~((uint)SliceMask)) != 0) || (selector == 0) || (selector > MaxMaps) ||
            (count == 0) || (count > ((Resolution / SliceRowEdge) - first)) ||
            (columnSize == 0) || (columnSize > ((Resolution / SliceRowEdge) - columnFirst))) { return false; }
        map = (selector - 1);
        firstRow = (first * SliceRowEdge);
        rowCount = (count * SliceRowEdge);
        firstColumn = (columnFirst * SliceRowEdge);
        columnCount = (columnSize * SliceRowEdge);
        return true;
    }
}
