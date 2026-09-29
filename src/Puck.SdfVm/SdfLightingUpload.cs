using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The typed records written into a residency's ordinary changed-word regions. Directions and the disc
/// exponent are baked once on the host; motion is already resolved and is never integrated at this seam.</summary>
public sealed class SdfLightingUpload {
    private readonly SdfLightFrameData[] m_lightFrame = new SdfLightFrameData[1];
    private readonly SdfLightData[] m_lights = new SdfLightData[SdfLighting.MaxLights];
    private readonly SdfSkyFrameData[] m_skyFrame = new SdfSkyFrameData[1];
    private readonly SdfSkyStopData[] m_stops = new SdfSkyStopData[SdfLighting.MaxSkyStops];
    private readonly SdfSoftboxData[] m_softboxes = new SdfSoftboxData[SdfLighting.MaxSoftboxes];

    /// <summary>Gets the light table's bounds and selection record.</summary>
    public SdfLightFrameData LightFrame => m_lightFrame[0];
    /// <summary>Gets every native light slot, including unused slots.</summary>
    public ReadOnlySpan<SdfLightData> Lights => m_lights;
    /// <summary>Gets the resolved sky frame and pre-baked illumination directions.</summary>
    public SdfSkyFrameData SkyFrame => m_skyFrame[0];
    /// <summary>Gets every native gradient stop slot.</summary>
    public ReadOnlySpan<SdfSkyStopData> Stops => m_stops;
    /// <summary>Gets every native reflection panel slot.</summary>
    public ReadOnlySpan<SdfSoftboxData> Softboxes => m_softboxes;
    /// <summary>Gets the light header's native bytes for the existing region writer.</summary>
    public ReadOnlySpan<byte> LightFrameBytes => MemoryMarshal.AsBytes(span: m_lightFrame.AsSpan());
    /// <summary>Gets the light table's native bytes for the existing region writer.</summary>
    public ReadOnlySpan<byte> LightBytes => MemoryMarshal.AsBytes(span: m_lights.AsSpan());
    /// <summary>Gets the sky frame's native bytes for the existing region writer.</summary>
    public ReadOnlySpan<byte> SkyFrameBytes => MemoryMarshal.AsBytes(span: m_skyFrame.AsSpan());
    /// <summary>Gets the gradient stop table's native bytes for the existing region writer.</summary>
    public ReadOnlySpan<byte> StopBytes => MemoryMarshal.AsBytes(span: m_stops.AsSpan());
    /// <summary>Gets the reflection panel table's native bytes for the existing region writer.</summary>
    public ReadOnlySpan<byte> SoftboxBytes => MemoryMarshal.AsBytes(span: m_softboxes.AsSpan());
    /// <summary>Gets the retained native-array payload bytes, excluding managed array headers.</summary>
    public ulong CpuScratchBytes => checked((ulong)(LightFrameBytes.Length + LightBytes.Length + SkyFrameBytes.Length + StopBytes.Length + SoftboxBytes.Length));

    /// <summary>Packs resolved environment values without allocation. Every record assignment zeroes its padding.</summary>
    /// <param name="environment">The shared resolver's current environment.</param>
    public void Pack(SdfLighting environment) {
        ArgumentNullException.ThrowIfNull(argument: environment);
        m_lightFrame[0] = new SdfLightFrameData {
            Count = checked((uint)environment.LightCount), ShadowIndex = environment.ShadowLightIndex,
        };
        for (var index = 0; index < m_lights.Length; index++) {
            var light = environment.GetLight(index: index);
            m_lights[index] = new SdfLightData {
                Direction = light.Kind == SdfLightKind.Directional
                    ? Normalize(direction: light.Direction, fallback: SdfLighting.DefaultSunDirection) : light.Direction,
                Weight = light.Weight, Color = light.Color, Kind = (uint)light.Kind,
                Parameter = light.Param, Shadows = light.Shadows ? 1u : 0u, DynamicSlot = light.DynamicSlot,
            };
        }
        for (var index = 0; index < m_stops.Length; index++) {
            var stop = environment.GetSkyStop(index: index);
            m_stops[index] = new SdfSkyStopData { Color = stop.Color, Elevation = stop.Elevation };
        }
        for (var index = 0; index < m_softboxes.Length; index++) {
            var box = environment.GetSoftbox(index: index);
            m_softboxes[index] = new SdfSoftboxData {
                Direction = Normalize(direction: box.Direction, fallback: Vector3.Zero),
                Weight = box.Weight, Color = box.Color, Size = box.Size, Blur = box.Blur,
            };
        }
        var shadow = environment.ShadowLightIndex;
        var disc = environment.SunDiscLightIndex;
        var cosine = Math.Cos(d: environment.SunDiscRadians);
        var exponent = cosine is > 0d and < 1d
            ? Math.Clamp(value: Math.Log(d: 0.5d) / Math.Log(d: cosine), min: 0d, max: 100000d)
            : 100000d;
        m_skyFrame[0] = new SdfSkyFrameData {
            SkyEnabled = environment.SkyEnabled ? 1u : 0u,
            FogDensity = environment.FogDensity,
            StopCount = checked((uint)environment.SkyStopCount),
            SoftboxCount = checked((uint)environment.SoftboxCount),
            CurvatureCavity = environment.CurvatureCavity,
            CurvatureRim = environment.CurvatureRim,
            CurvatureInk = environment.CurvatureInk,
            CurvatureInkLow = environment.CurvatureInkLow,
            CurvatureInkHigh = environment.CurvatureInkHigh,
            CurvatureInkColor = environment.CurvatureInkColor,
            SunDiscDirection = disc >= 0 ? m_lights[disc].Direction : Vector3.Zero,
            SunDiscIntensity = environment.SunDiscIntensity,
            SunDiscExponent = (float)exponent,
            SunDiscEnabled = disc >= 0 ? 1u : 0u,
            CloudLightDirection = shadow >= 0 ? m_lights[shadow].Direction : SdfLighting.DefaultSunDirection,
            CloudLightColor = shadow >= 0 ? m_lights[shadow].Color : Vector3.One,
            StarDensity = environment.StarDensity,
            StarBrightness = environment.StarBrightness,
            StarSeed = environment.StarSeed,
            TwinkleShare = environment.TwinkleShare,
            TwinkleDepth = environment.TwinkleDepth,
            TwinklePhase = environment.TwinklePhase,
            CloudCoverage = environment.CloudCoverage,
            CloudSoftness = environment.CloudSoftness,
            CloudColor = environment.CloudColor,
            CloudScale = environment.CloudScale,
            CloudSeed = environment.CloudSeed,
            CloudSpinAngle = environment.CloudSpinAngle,
            CloudCurl = environment.CloudCurl,
            CloudOffset = environment.CloudOffset,
            CloudShearOffset = environment.CloudShearOffset,
            HorizonLow = environment.HorizonLow,
            HorizonHigh = environment.HorizonHigh,
        };
    }

    // Match the existing double-precision host bake, avoiding different constant-folding paths in DXIL and SPIR-V.
    private static Vector3 Normalize(Vector3 direction, Vector3 fallback) {
        double x = direction.X, y = direction.Y, z = direction.Z;
        var length = Math.Sqrt(d: ((x * x) + (y * y)) + (z * z));
        if (length <= 0d) {
            x = fallback.X; y = fallback.Y; z = fallback.Z;
            length = Math.Sqrt(d: ((x * x) + (y * y)) + (z * z));
            if (length <= 0d) { return fallback; }
        }
        return new Vector3(x: (float)(x / length), y: (float)(y / length), z: (float)(z / length));
    }
}
