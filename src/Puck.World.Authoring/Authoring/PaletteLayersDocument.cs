using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>An authored surface tint and reflectance.</summary>
public sealed record PaletteSurfaceDocument(string Color, float Roughness, float Metal) {
    /// <summary>Resolves literal colors or containing-world color bindings.</summary>
    public SdfSurface ToSurface(Func<string, Vector3> resolve) => new(
        resolve(Color),
        Roughness,
        Metal
    );
}
/// <summary>One ascending coverage threshold and the surface it reveals.</summary>
public sealed record PaletteRevealDocument(float Threshold, PaletteSurfaceDocument Surface);
/// <summary>One ascending radial ramp stop.</summary>
public sealed record PaletteRadialStopDocument(float Radius, string Color);
/// <summary>One to four radial paint stops with optional angular modulation.</summary>
public sealed record PaletteRadialPaintDocument(IReadOnlyList<PaletteRadialStopDocument> Stops, float Softness = 0f,
    float ModulationAmplitude = 0f, float ModulationFrequency = 0f, uint Seed = 0);
/// <summary>A paint plane in the winning dynamic frame, or world space when no slot owns the hit.</summary>
public sealed record PaletteInsetDocument(DocumentVector3 Origin, DocumentQuaternion Rotation, float Depth, float Ior, PaletteRadialPaintDocument Paint) {
    /// <summary>Resolves the generic engine layer.</summary>
    public SdfInset ToInset(Func<string, Vector3> resolve) => new(
        new(
            x: Origin.X,
            y: Origin.Y,
            z: Origin.Z
        ),
        new(
            Rotation.X,
            Rotation.Y,
            Rotation.Z,
            Rotation.W
        ),
        Depth,
        Ior,
        new(
            Paint.Stops.Select(selector: s => new SdfRadialStop(
                s.Radius,
                resolve(s.Color)
            )).ToArray(),
            Paint.Softness,
            Paint.ModulationAmplitude,
            Paint.ModulationFrequency,
            Paint.Seed
        )
    );
}
/// <summary>A band of paint across the surface, in the winning dynamic frame, or world space when no slot owns the hit:
/// the points within <paramref name="HalfWidth"/> of the plane through <paramref name="Origin"/> with normal
/// <paramref name="Normal"/> take <paramref name="Color"/>.</summary>
/// <param name="Origin">A point on the band's centre plane.</param>
/// <param name="Normal">The centre plane's normal; any length but zero.</param>
/// <param name="HalfWidth">The band's half-width along the normal; positive.</param>
/// <param name="Color">The band's color, #RRGGBB or a state binding.</param>
public sealed record PaletteBandDocument(DocumentVector3 Origin, DocumentVector3 Normal, float HalfWidth, string Color) {
    /// <summary>Resolves the generic engine layer.</summary>
    /// <param name="resolve">The color resolver.</param>
    /// <returns>The band.</returns>
    public SdfBand ToBand(Func<string, Vector3> resolve) => new(
        Origin: new(
            x: Origin.X,
            y: Origin.Y,
            z: Origin.Z
        ),
        Normal: new(
            x: Normal.X,
            y: Normal.Y,
            z: Normal.Z
        ),
        HalfWidth: HalfWidth,
        Color: resolve(Color)
    );
}
/// <summary>Generic weathering coverage and explicitly authored reveal/deposit surfaces.</summary>
public sealed record PaletteWeatheringDocument(float Edge = 0f, float Lines = 0f, float Settle = 0f, float Reach = 1f,
    uint Seed = 0, float Scale = 1f, float Floor = 0f, int Lane = 0,
    IReadOnlyList<PaletteRevealDocument>? Under = null, PaletteSurfaceDocument? Deposit = null) {
    /// <summary>Resolves the generic engine response.</summary>
    public SdfWeathering ToWeathering(Func<string, Vector3> resolve) => new(
        Edge,
        Lines,
        Settle,
        Reach,
        Seed,
        Scale,
        Floor,
        Lane,
        Under?.Select(selector: s => new SdfRevealStage(
            s.Threshold,
            s.Surface.ToSurface(resolve: resolve)
        )).ToArray(),
        Deposit?.ToSurface(resolve: resolve)
    );
}
