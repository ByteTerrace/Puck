namespace Puck.ShaderVm.Programs;

using System.Numerics;

/// <summary>One frame of authored sky settings, after the day cycle has been interpolated.</summary>
public sealed record SkyFrameSettings {
    /// <summary>Gets the colour overhead.</summary>
    public Vector3 Zenith { get; init; } = new(x: 0.106f, y: 0.137f, z: 0.314f);
    /// <summary>Gets the colour at the horizon.</summary>
    public Vector3 Horizon { get; init; } = new(x: 0.878f, y: 0.561f, z: 0.420f);
    /// <summary>Gets the colour below the horizon.</summary>
    public Vector3 Ground { get; init; } = new(x: 0.043f, y: 0.051f, z: 0.078f);
    /// <summary>Gets the exponential fog density, per unit distance.</summary>
    public float FogDensity { get; init; } = 0.015f;
    /// <summary>Gets the unit direction toward the sun.</summary>
    public Vector3 SunDirection { get; init; } = Vector3.Normalize(value: new Vector3(x: 0.6f, y: 0.2f, z: -0.75f));
    /// <summary>Gets the sun colour.</summary>
    public Vector3 SunColor { get; init; } = new(x: 1f, y: 0.851f, z: 0.651f);
    /// <summary>Gets the additive brightness of the sun disc.</summary>
    public float SunDiscIntensity { get; init; } = 6f;
    /// <summary>Gets the angular radius of the sun disc, in radians.</summary>
    public float SunDiscRadians { get; init; } = 0.045f;
    /// <summary>Gets the star cells per octahedral axis.</summary>
    public float StarDensity { get; init; } = 64f;
    /// <summary>Gets the peak star brightness.</summary>
    public float StarBrightness { get; init; } = 0.85f;
    /// <summary>Gets the twinkle phase within its period, in the unit interval.</summary>
    public float StarPhase { get; init; }
    /// <summary>Gets the fraction of stars that twinkle.</summary>
    public float TwinkleShare { get; init; } = 0.3f;
    /// <summary>Gets how far a twinkling star dips.</summary>
    public float TwinkleDepth { get; init; } = 0.6f;
    /// <summary>Gets the seed the star field hashes.</summary>
    public uint StarSeed { get; init; } = 1337u;
    /// <summary>Gets the cloud colour.</summary>
    public Vector3 CloudColor { get; init; } = new(x: 0.788f, y: 0.749f, z: 0.769f);
    /// <summary>Gets the fraction of the layer that carries cloud.</summary>
    public float CloudCoverage { get; init; } = 0.4f;
    /// <summary>Gets the width of the cloud coverage threshold.</summary>
    public float CloudSoftness { get; init; } = 0.3f;
    /// <summary>Gets the cloud cell size, in layer units.</summary>
    public float CloudScale { get; init; } = 2.5f;
    /// <summary>Gets the integrated cloud drift offset, in cells.</summary>
    public Vector2 CloudDrift { get; init; }
    /// <summary>Gets the integrated shaping-field shear offset, in cells.</summary>
    public Vector2 CloudShear { get; init; }
    /// <summary>Gets the integrated rotation of the layer about the zenith, in radians.</summary>
    public float CloudSpin { get; init; }
    /// <summary>Gets how far the rotating frame winds the flow inward.</summary>
    public float CloudCurl { get; init; } = 0.8f;
    /// <summary>Gets the seed the cloud field hashes.</summary>
    public uint CloudSeed { get; init; } = 4242u;
}
