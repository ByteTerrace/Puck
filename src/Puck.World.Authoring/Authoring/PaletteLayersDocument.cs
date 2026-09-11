using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>An authored surface tint and reflectance.</summary>
public sealed record PaletteSurfaceDocument(string Color, float Roughness, float Metal) {
    /// <summary>Resolves literal colors or containing-world color bindings.</summary>
    public SdfSurface ToSurface(Func<string, Vector3> resolve) => new(resolve(Color), Roughness, Metal);
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
        new(Origin.X, Origin.Y, Origin.Z), new(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W), Depth, Ior,
        new(Paint.Stops.Select(s => new SdfRadialStop(s.Radius, resolve(s.Color))).ToArray(),
            Paint.Softness, Paint.ModulationAmplitude, Paint.ModulationFrequency, Paint.Seed));
}
/// <summary>Generic weathering coverage and explicitly authored reveal/deposit surfaces.</summary>
public sealed record PaletteWeatheringDocument(float Edge = 0f, float Lines = 0f, float Settle = 0f, float Reach = 1f,
    uint Seed = 0, float Scale = 1f, float Floor = 0f, int Lane = 0,
    IReadOnlyList<PaletteRevealDocument>? Under = null, PaletteSurfaceDocument? Deposit = null) {
    /// <summary>Resolves the generic engine response.</summary>
    public SdfWeathering ToWeathering(Func<string, Vector3> resolve) => new(Edge, Lines, Settle, Reach, Seed, Scale,
        Floor, Lane, Under?.Select(s => new SdfRevealStage(s.Threshold, s.Surface.ToSurface(resolve))).ToArray(),
        Deposit?.ToSurface(resolve));
}
