namespace Puck.SignedDistance;

/// <summary>The maximum square shading footprint and the tile classified together, in render pixels.</summary>
/// <param name="MaxRate">Shading samples are at most this many pixels apart along each axis.</param>
/// <param name="TileSize">Edge length of the region that must qualify together.</param>
public sealed record SdfViewsRate(int MaxRate = 1, int TileSize = 8) {
    /// <summary>The engine default shades each render pixel.</summary>
    public static SdfViewsRate Full { get; } = new();

    /// <summary>Checks both lengths against the rates and tiles supported by the views kernel.</summary>
    /// <param name="path">The authored row.</param>
    /// <returns>The invalid setting or null.</returns>
    public string? Refusal(string path) => MaxRate is not (1 or 2)
        ? $"{path}.maxRate must be 1 or 2."
        : TileSize is not (2 or 4 or 8) ? $"{path}.tileSize must be 2, 4 or 8." : null;
}
