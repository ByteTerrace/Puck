namespace Puck.ShaderVm.Programs;

using System.Numerics;

/// <summary>The parameter rows a sky program reads, and the packing a host writes them with.</summary>
/// <remarks>
/// Row layout, one four-lane parameter each:
/// <code>
/// 0  zenith.rgb          fogDensity
/// 1  horizon.rgb         -
/// 2  ground.rgb          sunDiscIntensity
/// 3  sunDirection.xyz    sunDiscExponent
/// 4  sunColor.rgb        starBrightness
/// 5  starDensity         starPhase        twinkleShare  twinkleDepth
/// 6  cloudColor.rgb      cloudCoverage
/// 7  cloudSoftness       cloudScale       cloudSpin     cloudCurl
/// 8  cloudDrift.xy       cloudShear.xy
/// </code>
/// </remarks>
public static class SkyParameters {
    /// <summary>The zenith colour and the fog density.</summary>
    public const int Zenith = 0;
    /// <summary>The horizon colour.</summary>
    public const int Horizon = 1;
    /// <summary>The ground colour and the sun disc intensity.</summary>
    public const int Ground = 2;
    /// <summary>The sun direction and the host-baked sun disc exponent.</summary>
    public const int Sun = 3;
    /// <summary>The sun colour and the star brightness.</summary>
    public const int SunColor = 4;
    /// <summary>The star density, twinkle phase, twinkle share, and twinkle depth.</summary>
    public const int Stars = 5;
    /// <summary>The cloud colour and coverage.</summary>
    public const int Clouds = 6;
    /// <summary>The cloud softness, cell scale, spin angle, and curl.</summary>
    public const int CloudShape = 7;
    /// <summary>The integrated cloud drift and shear offsets, in cells.</summary>
    public const int CloudWind = 8;
    /// <summary>The number of parameter rows a sky program reads.</summary>
    public const int Count = 9;

    /// <summary>Packs one frame of sky settings into the parameter rows a sky program reads.</summary>
    /// <param name="settings">The frame settings.</param>
    /// <param name="rows">The destination, at least <see cref="Count"/> rows long.</param>
    public static void Pack(in SkyFrameSettings settings, Span<Vector4> rows) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: rows.Length, other: Count);

        rows[Zenith] = new Vector4(x: settings.Zenith.X, y: settings.Zenith.Y, z: settings.Zenith.Z, w: settings.FogDensity);
        rows[Horizon] = new Vector4(x: settings.Horizon.X, y: settings.Horizon.Y, z: settings.Horizon.Z, w: 0f);
        rows[Ground] = new Vector4(x: settings.Ground.X, y: settings.Ground.Y, z: settings.Ground.Z, w: settings.SunDiscIntensity);
        rows[Sun] = new Vector4(x: settings.SunDirection.X, y: settings.SunDirection.Y, z: settings.SunDirection.Z, w: SunDiscExponent(discRadians: settings.SunDiscRadians));
        rows[SunColor] = new Vector4(x: settings.SunColor.X, y: settings.SunColor.Y, z: settings.SunColor.Z, w: settings.StarBrightness);
        rows[Stars] = new Vector4(x: MathF.Max(x: settings.StarDensity, y: 1f), y: settings.StarPhase, z: settings.TwinkleShare, w: settings.TwinkleDepth);
        rows[Clouds] = new Vector4(x: settings.CloudColor.X, y: settings.CloudColor.Y, z: settings.CloudColor.Z, w: settings.CloudCoverage);
        rows[CloudShape] = new Vector4(x: settings.CloudSoftness, y: MathF.Max(x: settings.CloudScale, y: 1e-3f), z: settings.CloudSpin, w: settings.CloudCurl);
        rows[CloudWind] = new Vector4(x: settings.CloudDrift.X, y: settings.CloudDrift.Y, z: settings.CloudShear.X, w: settings.CloudShear.Y);
    }

    // The disc reads half brightness at its authored edge: pow(cos(discRadians), k) = 0.5.
    private static float SunDiscExponent(float discRadians) {
        var cosRadius = Math.Cos(d: discRadians);

        return ((cosRadius is > 0d and < 1d)
            ? ((float)Math.Clamp(value: (Math.Log(d: 0.5d) / Math.Log(d: cosRadius)), min: 0d, max: 100000d))
            : 100000f
        );
    }
}
