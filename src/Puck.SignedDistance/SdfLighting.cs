using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The resolved lighting and sky values before native upload. Values retain their own types: integer
/// seeds, light tags and transform slots never pass through a floating-point lane.</summary>
public sealed class SdfLighting {
    /// <summary>The ambient floor of an unauthored world.</summary>
    public const float DefaultAmbientBase = 0.25f;
    /// <summary>The ambient hemisphere gradient of an unauthored world.</summary>
    public const float DefaultAmbientHemisphere = 0.25f;
    /// <summary>The default cloud cell scale.</summary>
    public const float DefaultCloudScale = 2f;
    /// <summary>The default cloud edge softness.</summary>
    public const float DefaultCloudSoftness = 0.25f;
    /// <summary>The default curvature magnitude at which the ink outline saturates.</summary>
    public const float DefaultCurvatureInkHigh = 16f;
    /// <summary>The default curvature magnitude at which the ink outline starts.</summary>
    public const float DefaultCurvatureInkLow = 6f;
    /// <summary>The fog density of an unauthored world.</summary>
    public const float DefaultFogDensity = 0.015f;
    /// <summary>The default penumbra half-slope.</summary>
    public const float DefaultPenumbraSlope = (1f / 9f);
    /// <summary>The default point-light falloff radius, in world units.</summary>
    public const float DefaultPointRadius = 1f;
    /// <summary>The default point-light strength.</summary>
    public const float DefaultPointWeight = 1f;
    /// <summary>The default rim exponent.</summary>
    public const float DefaultRimPower = 3f;
    /// <summary>The default star cell density.</summary>
    public const float DefaultStarDensity = 48f;
    /// <summary>The default sun-disc angular radius in radians.</summary>
    public const float DefaultSunDiscRadians = 0.05f;
    /// <summary>The sun diffuse weight of an unauthored world.</summary>
    public const float DefaultSunWeight = 0.85f;
    /// <summary>The default scintillation rate in hertz.</summary>
    public const float DefaultTwinkleRate = 1f;
    /// <summary>The most lights one resolved frame carries.</summary>
    public const int MaxLights = 8;
    /// <summary>The largest admitted directional-light penumbra half-slope.</summary>
    public const float MaxPenumbraSlope = 0.3f;
    /// <summary>The most stops in the current gradient.</summary>
    public const int MaxSkyStops = 4;
    /// <summary>The most studio-reflection panels in the current environment.</summary>
    public const int MaxSoftboxes = 4;

    private readonly SdfLight[] m_lights = new SdfLight[MaxLights];
    private readonly (Vector3 Color, float Elevation)[] m_stops = new (Vector3, float)[MaxSkyStops];
    private readonly SdfSoftbox[] m_softboxes = new SdfSoftbox[MaxSoftboxes];

    private int m_lightCount;
    private int m_stopCount;
    private int m_softboxCount;

    /// <summary>The sun direction of an unauthored world.</summary>
    public static Vector3 DefaultSunDirection { get; } = new(x: 0.51343602f, y: 0.79349202f, z: 0.32673201f);
    /// <summary>The default ink outline color.</summary>
    public static Vector3 DefaultCurvatureInkColor { get; } = new(x: 0.02f, y: 0.02f, z: 0.03f);
    /// <summary>The zenith color of the two-stop sky an unauthored world renders.</summary>
    public static Vector3 DefaultSkyZenithColor { get; } = new(x: 0.10f, y: 0.13f, z: 0.20f);
    /// <summary>The ground color of the two-stop sky an unauthored world renders.</summary>
    public static Vector3 DefaultSkyGroundColor { get; } = new(x: 0.04f, y: 0.05f, z: 0.07f);

    /// <summary>Creates an unlit frame with the default gradient available beneath authored layers.</summary>
    public SdfLighting() {
        SetSkyStop(0, DefaultSkyGroundColor, -1f);
        SetSkyStop(1, DefaultSkyZenithColor, 1f);
        SkyStopCount = 2;
    }

    /// <summary>Creates the lighting of an unauthored world.</summary>
    /// <returns>The default shadowed directional light and hemisphere ambient.</returns>
    public static SdfLighting Default() {
        var lighting = new SdfLighting { LightCount = 2 };

        lighting.SetLight(index: 0, light: new(SdfLightKind.Directional, DefaultSunDirection, Vector3.One,
            DefaultSunWeight, DefaultPenumbraSlope, true));
        lighting.SetLight(index: 1, light: new(SdfLightKind.Hemisphere, Vector3.Zero, Vector3.One,
            DefaultAmbientBase, DefaultAmbientHemisphere, false));
        return lighting;
    }
    /// <summary>Copies resolved values and all table slots without allocating.</summary>
    /// <param name="source">The resolved frame to copy.</param>
    public void CopyFrom(SdfLighting source) {
        ArgumentNullException.ThrowIfNull(source);
        source.m_lights.CopyTo(array: m_lights, index: 0);
        source.m_stops.CopyTo(array: m_stops, index: 0);
        source.m_softboxes.CopyTo(array: m_softboxes, index: 0);
        m_lightCount = source.m_lightCount;
        m_stopCount = source.m_stopCount;
        m_softboxCount = source.m_softboxCount;
        ShadowLightIndex = source.ShadowLightIndex;
        SkyEnabled = source.SkyEnabled;
        FogDensity = source.FogDensity;
        CurvatureCavity = source.CurvatureCavity;
        CurvatureRim = source.CurvatureRim;
        CurvatureInk = source.CurvatureInk;
        CurvatureInkLow = source.CurvatureInkLow;
        CurvatureInkHigh = source.CurvatureInkHigh;
        CurvatureInkColor = source.CurvatureInkColor;
        SunDiscLightIndex = source.SunDiscLightIndex;
        SunDiscRadians = source.SunDiscRadians;
        SunDiscIntensity = source.SunDiscIntensity;
        StarDensity = source.StarDensity;
        StarBrightness = source.StarBrightness;
        StarSeed = source.StarSeed;
        TwinkleShare = source.TwinkleShare;
        TwinkleDepth = source.TwinkleDepth;
        TwinklePhase = source.TwinklePhase;
        CloudColor = source.CloudColor;
        CloudCoverage = source.CloudCoverage;
        CloudSoftness = source.CloudSoftness;
        CloudScale = source.CloudScale;
        CloudSeed = source.CloudSeed;
        CloudOffset = source.CloudOffset;
        CloudShearOffset = source.CloudShearOffset;
        CloudSpinAngle = source.CloudSpinAngle;
        CloudCurl = source.CloudCurl;
        HorizonLow = source.HorizonLow;
        HorizonHigh = source.HorizonHigh;
        Sky = SdfSkySnapshot.Copy(source.Sky, Sky);
    }
    /// <summary>Gets one resolved light.</summary>
    /// <param name="index">The zero-based slot, below <see cref="MaxLights"/>.</param>
    /// <returns>The resolved light in that slot, whether active or unused.</returns>
    public SdfLight GetLight(int index) => m_lights[index];
    /// <summary>Gets one gradient stop.</summary>
    /// <param name="index">The zero-based slot, below <see cref="MaxSkyStops"/>.</param>
    /// <returns>The stop's color and signed elevation coordinate.</returns>
    public (Vector3 Color, float Elevation) GetSkyStop(int index) => m_stops[index];
    /// <summary>Gets one reflection panel.</summary>
    /// <param name="index">The zero-based slot, below <see cref="MaxSoftboxes"/>.</param>
    /// <returns>The resolved panel in that slot, whether active or unused.</returns>
    public SdfSoftbox GetSoftbox(int index) => m_softboxes[index];
    /// <summary>Sets one light and updates the directional shadow selection.</summary>
    /// <param name="index">The zero-based slot, below <see cref="MaxLights"/>.</param>
    /// <param name="light">The resolved light. Only positional lights retain a dynamic transform slot.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the light table.</exception>
    public void SetLight(int index, SdfLight light) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaxLights);
        m_lights[index] = ((light.Kind is SdfLightKind.Point or SdfLightKind.Occluder) ? light : light with { DynamicSlot = 0 });
        if (light.Shadows && (light.Kind == SdfLightKind.Directional)) { ShadowLightIndex = index; } else if (ShadowLightIndex == index) { ShadowLightIndex = -1; }
    }
    /// <summary>Sets one gradient stop.</summary>
    /// <param name="index">The zero-based slot, below <see cref="MaxSkyStops"/>.</param>
    /// <param name="color">The stop's resolved color.</param>
    /// <param name="elevation">The signed vertical direction coordinate in [-1, 1].</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the gradient table.</exception>
    public void SetSkyStop(int index, Vector3 color, float elevation) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaxSkyStops);
        m_stops[index] = (color, elevation);
    }
    /// <summary>Sets one reflection panel.</summary>
    /// <param name="index">The zero-based slot, below <see cref="MaxSoftboxes"/>.</param>
    /// <param name="softbox">The resolved panel.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the panel table.</exception>
    public void SetSoftbox(int index, SdfSoftbox softbox) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaxSoftboxes);
        m_softboxes[index] = softbox;
    }

    /// <summary>Gets or sets the number of active lights.</summary>
    public int LightCount { get => m_lightCount; set => m_lightCount = Math.Clamp(max: MaxLights, min: 0, value: value); }

    /// <summary>Gets the shadowed directional light, or minus one.</summary>
    public int ShadowLightIndex { get; private set; } = -1;

    /// <summary>Gets or sets whether an authored sky is enabled.</summary>
    public bool SkyEnabled { get; set; }
    /// <summary>Gets or sets the prepared ordered stack's common resolved facts. Its native kind tables are packed
    /// from the same resolver output; presentation readers must not resolve the authored sky independently.</summary>
    public SdfSkySnapshot? Sky { get; set; }
    /// <summary>Gets or sets the number of gradient stops.</summary>
    public int SkyStopCount { get => m_stopCount; set => m_stopCount = Math.Clamp(max: MaxSkyStops, min: 0, value: value); }
    /// <summary>Gets or sets the number of reflection panels.</summary>
    public int SoftboxCount { get => m_softboxCount; set => m_softboxCount = Math.Clamp(max: MaxSoftboxes, min: 0, value: value); }

    /// <summary>Gets or sets the fog density.</summary>
    public float FogDensity { get; set; } = DefaultFogDensity;

    /// <summary>Gets or sets the cavity-darkening gain.</summary>
    public float CurvatureCavity { get; set; }
    /// <summary>Gets or sets the rim-brightening gain.</summary>
    public float CurvatureRim { get; set; }
    /// <summary>Gets or sets the ink outline gain.</summary>
    public float CurvatureInk { get; set; }

    /// <summary>Gets or sets the ink band's lower threshold.</summary>
    public float CurvatureInkLow { get; set; } = DefaultCurvatureInkLow;
    /// <summary>Gets or sets the ink band's upper threshold.</summary>
    public float CurvatureInkHigh { get; set; } = DefaultCurvatureInkHigh;
    /// <summary>Gets or sets the ink outline color.</summary>
    public Vector3 CurvatureInkColor { get; set; } = DefaultCurvatureInkColor;
    /// <summary>Gets or sets the directional light represented by the disc, or minus one.</summary>
    public int SunDiscLightIndex { get; set; } = -1;
    /// <summary>Gets or sets the disc's angular radius in radians.</summary>
    public float SunDiscRadians { get; set; } = DefaultSunDiscRadians;

    /// <summary>Gets or sets the disc's intensity.</summary>
    public float SunDiscIntensity { get; set; }

    /// <summary>Gets or sets the star lattice density.</summary>
    public float StarDensity { get; set; } = DefaultStarDensity;

    /// <summary>Gets or sets the star brightness.</summary>
    public float StarBrightness { get; set; }
    /// <summary>Gets or sets all 32 authored bits of the star seed.</summary>
    public uint StarSeed { get; set; }
    /// <summary>Gets or sets the share of stars that twinkle.</summary>
    public float TwinkleShare { get; set; }
    /// <summary>Gets or sets the modulation depth of twinkling stars.</summary>
    public float TwinkleDepth { get; set; }
    /// <summary>Gets or sets the integrated twinkle phase in [0, 1).</summary>
    public float TwinklePhase { get; set; }

    /// <summary>Gets or sets the cloud color.</summary>
    public Vector3 CloudColor { get; set; } = Vector3.One;

    /// <summary>Gets or sets cloud coverage in [0, 1].</summary>
    public float CloudCoverage { get; set; }

    /// <summary>Gets or sets cloud edge softness in (0, 1].</summary>
    public float CloudSoftness { get; set; } = DefaultCloudSoftness;
    /// <summary>Gets or sets the cloud cell scale.</summary>
    public float CloudScale { get; set; } = DefaultCloudScale;

    /// <summary>Gets or sets all 32 authored bits of the cloud seed.</summary>
    public uint CloudSeed { get; set; }
    /// <summary>Gets or sets integrated drift, reduced by the cloud lattice period.</summary>
    public Vector2 CloudOffset { get; set; }
    /// <summary>Gets or sets integrated shaping-field drift, reduced by the cloud lattice period.</summary>
    public Vector2 CloudShearOffset { get; set; }
    /// <summary>Gets or sets integrated rotation, reduced by 2π.</summary>
    public float CloudSpinAngle { get; set; }
    /// <summary>Gets or sets the cloud curl angle in radians.</summary>
    public float CloudCurl { get; set; }
    /// <summary>Gets or sets the ground-facing studio-reflection horizon.</summary>
    public Vector3 HorizonLow { get; set; }
    /// <summary>Gets or sets the sky-facing studio-reflection horizon.</summary>
    public Vector3 HorizonHigh { get; set; }
}
