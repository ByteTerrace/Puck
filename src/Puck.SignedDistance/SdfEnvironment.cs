using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The kind of one <see cref="SdfLight"/>.</summary>
public enum SdfLightKind : byte {
    /// <summary>A Lambert directional light. <see cref="SdfLight.Param"/> is the penumbra half-slope (the tangent of
    /// the light's angular radius); the one light with <see cref="SdfLight.Shadows"/> set drives the soft-shadow
    /// march, every other directional is scaled by ambient occlusion instead.</summary>
    Directional = 0,
    /// <summary>A hemisphere ambient: <see cref="SdfLight.Weight"/> is the floor, <see cref="SdfLight.Param"/> the
    /// gradient scaling the surface normal's Y. Scaled by ambient occlusion.</summary>
    Hemisphere = 1,
    /// <summary>A view-dependent silhouette brighten added after the material shade:
    /// <c>weight · color · pow(1 − saturate(dot(normal, −ray)), param)</c>.</summary>
    Rim = 2,
}
/// <summary>One light of the lit path.</summary>
/// <param name="Kind">What the light is.</param>
/// <param name="Direction">Directional only: from the surface toward the light, any nonzero length (normalized on
/// upload).</param>
/// <param name="Color">The linear RGB color.</param>
/// <param name="Weight">The strength; a hemisphere's floor.</param>
/// <param name="Param">The kind's second scalar: penumbra half-slope, hemisphere gradient, or rim exponent.</param>
/// <param name="Shadows">Directional only: whether this light drives the soft-shadow march.</param>
public readonly record struct SdfLight(SdfLightKind Kind, Vector3 Direction, Vector3 Color, float Weight, float Param, bool Shadows);
/// <summary>One analytic studio-reflection softbox — see <c>worldStudioReflection</c> in sdf-world.hlsli.</summary>
/// <param name="Direction">From a lit surface toward the softbox, any nonzero length (normalized on upload).</param>
/// <param name="Color">The linear RGB color.</param>
/// <param name="Weight">The strength.</param>
/// <param name="Size">The angular half-extent proxy (width, height) the falloff widens by.</param>
/// <param name="Blur">Additional falloff softening, in the same units as <paramref name="Size"/>; 0 = none.</param>
public readonly record struct SdfSoftbox(Vector3 Direction, Vector3 Color, float Weight, Vector2 Size, float Blur);
/// <summary>How one environment lane interpolates between two keys of a cycle.</summary>
public enum SdfEnvironmentBlend : byte {
    /// <summary>Linear.</summary>
    Lerp = 0,
    /// <summary>The first lane of a unit-direction triple, interpolated along the arc.</summary>
    Direction = 1,
    /// <summary>Held from the earlier key (a count, a kind, a seed, a flag).</summary>
    Hold = 2,
}
/// <summary>How the frame's final color is remapped before it reaches the store — see
/// <c>sdfFilmicTonemap</c>/<c>worldTonemapMode</c> in sdf-world.hlsli.</summary>
public enum SdfTonemapMode : byte {
    /// <summary>No remap: the stylized shaded color, as every world rendered before this field existed.</summary>
    None = 0,
    /// <summary>The Narkowicz ACES-fit filmic curve, then gamma 2.2.</summary>
    Filmic = 1,
}
/// <summary>The lit path's per-frame environment — every light, the stylization gains, and the sky — as one lane
/// table the engine uploads as float4 rows of the screen-light buffer (<c>SdfWorldEngine.PackEnvironment</c>, which
/// also performs the host bakes noted per row). KEEP IN SYNC with sdf-world.hlsli's <c>SdfEnv*</c> rows and
/// accessors.</summary>
/// <remarks>
/// Row layout (row-relative to the environment base, four float lanes per row):
/// <list type="table">
/// <item><term>0 control</term><description>x light count, y shadow light index (−1 none), z sky enabled, w fog density</description></item>
/// <item><term>1 + 3i .. 3 + 3i, i &lt; 8</term><description>light i: (direction.xyz, weight) (color.rgb, kind) (param, shadows, 0, 0)</description></item>
/// <item><term>25</term><description>curvature: cavity, rim, ink, ink band low</description></item>
/// <item><term>26</term><description>ink color.rgb, ink band high</description></item>
/// <item><term>27 sky control</term><description>x gradient stop count, y sun-disc light index (−1 none), z sun-disc angular radius in radians (uploaded as the baked <c>pow</c> exponent), w sun-disc intensity</description></item>
/// <item><term>28 .. 31</term><description>gradient stop i: color.rgb, elevation in [−1, 1] (ascending)</description></item>
/// <item><term>32 stars</term><description>density, brightness, seed, 0</description></item>
/// <item><term>33 twinkle</term><description>share, depth, rate in hertz (uploaded as a period in engine ticks), 0</description></item>
/// <item><term>34 clouds A</term><description>color.rgb, coverage</description></item>
/// <item><term>35 clouds B</term><description>softness, scale, seed, 0</description></item>
/// <item><term>36 clouds C</term><description>drift.xy, shear.xy — rates in layer units per second (uploaded as offsets integrated on the tick clock)</description></item>
/// <item><term>37 clouds D</term><description>spin rate in radians per second (uploaded as the integrated angle), curl, 0, 0</description></item>
/// <item><term>38 softbox control</term><description>x softbox count, y tonemap mode (0 none, 1 filmic), 0, 0</description></item>
/// <item><term>39 + 3i .. 41 + 3i, i &lt; 4</term><description>softbox i: (direction.xyz, weight) (color.rgb, size.x) (size.y, blur, 0, 0)</description></item>
/// <item><term>51</term><description>studio reflection horizon low (ground-ward) color.rgb, 0</description></item>
/// <item><term>52</term><description>studio reflection horizon high (sky-ward) color.rgb, 0</description></item>
/// </list>
/// </remarks>
public sealed class SdfEnvironment {
    /// <summary>The most lights a frame carries.</summary>
    public const int MaxLights = 8;
    /// <summary>The most gradient stops a sky carries.</summary>
    public const int MaxSkyStops = 4;
    /// <summary>The most studio-reflection softboxes a frame carries.</summary>
    public const int MaxSoftboxes = 4;
    /// <summary>The rows the environment occupies in the screen-light buffer.</summary>
    public const int RowCount = 53;
    /// <summary>The float lanes the environment occupies.</summary>
    public const int LaneCount = (RowCount * 4);
    /// <summary>The control row.</summary>
    public const int ControlRow = 0;
    /// <summary>The first light's first row.</summary>
    public const int LightsRow = 1;
    /// <summary>Rows per light.</summary>
    public const int RowsPerLight = 3;
    /// <summary>The first curvature row.</summary>
    public const int CurvatureRow = 25;
    /// <summary>The sky control row.</summary>
    public const int SkyControlRow = 27;
    /// <summary>The first gradient stop row.</summary>
    public const int SkyStopsRow = 28;
    /// <summary>The star row.</summary>
    public const int StarsRow = 32;
    /// <summary>The twinkle row.</summary>
    public const int TwinkleRow = 33;
    /// <summary>The first cloud row.</summary>
    public const int CloudsRow = 34;
    /// <summary>The softbox control row.</summary>
    public const int SoftboxControlRow = 38;
    /// <summary>The first softbox's first row.</summary>
    public const int SoftboxesRow = 39;
    /// <summary>Rows per softbox.</summary>
    public const int RowsPerSoftbox = 3;
    /// <summary>The studio-reflection horizon's low (ground-ward) color row.</summary>
    public const int HorizonLowRow = 51;
    /// <summary>The studio-reflection horizon's high (sky-ward) color row.</summary>
    public const int HorizonHighRow = 52;
    /// <summary>The pinned sun direction, as the shaders held it before the environment became per-frame data.</summary>
    public static Vector3 DefaultSunDirection { get; } = new(x: 0.51343602f, y: 0.79349202f, z: 0.32673201f);
    /// <summary>The pinned sun diffuse weight.</summary>
    public const float DefaultSunWeight = 0.85f;
    /// <summary>The pinned penumbra half-slope: the retired <c>1/ShadowSharpness</c>.</summary>
    public const float DefaultPenumbraSlope = (1f / 9f);
    /// <summary>The largest penumbra half-slope a directional light admits; the shadow gather's cone is three times
    /// it and must stay a valid chord.</summary>
    public const float MaxPenumbraSlope = 0.3f;
    /// <summary>The pinned ambient floor.</summary>
    public const float DefaultAmbientBase = 0.25f;
    /// <summary>The pinned ambient hemisphere gradient.</summary>
    public const float DefaultAmbientHemisphere = 0.25f;
    /// <summary>The default rim exponent.</summary>
    public const float DefaultRimPower = 3f;
    /// <summary>The default curvature magnitude at which the ink outline starts.</summary>
    public const float DefaultCurvatureInkLow = 6f;
    /// <summary>The default curvature magnitude at which the ink outline saturates.</summary>
    public const float DefaultCurvatureInkHigh = 16f;
    /// <summary>The default ink outline color.</summary>
    public static Vector3 DefaultCurvatureInkColor { get; } = new(x: 0.02f, y: 0.02f, z: 0.03f);
    /// <summary>The pinned fog density.</summary>
    public const float DefaultFogDensity = 0.015f;
    /// <summary>The pinned zenith color of the two-stop sky an unauthored world renders.</summary>
    public static Vector3 DefaultSkyZenithColor { get; } = new(x: 0.10f, y: 0.13f, z: 0.20f);
    /// <summary>The pinned ground color of the two-stop sky an unauthored world renders.</summary>
    public static Vector3 DefaultSkyGroundColor { get; } = new(x: 0.04f, y: 0.05f, z: 0.07f);
    /// <summary>The default sun-disc angular radius in radians.</summary>
    public const float DefaultSunDiscRadians = 0.05f;
    /// <summary>The default star cell density.</summary>
    public const float DefaultStarDensity = 48f;
    /// <summary>The default scintillation rate in hertz.</summary>
    public const float DefaultTwinkleRate = 1f;
    /// <summary>The default cloud edge softness.</summary>
    public const float DefaultCloudSoftness = 0.25f;
    /// <summary>The default cloud cell scale.</summary>
    public const float DefaultCloudScale = 2f;

    private readonly float[] m_lanes = new float[LaneCount];

    /// <summary>Creates an environment with no lights, the sky disabled (the pinned two-stop gradient seeded in its
    /// stops, so a layer drawn over an unauthored gradient has one to draw over), and every gain zero.</summary>
    public SdfEnvironment() {
        SetLane(row: ControlRow, lane: 1, value: -1f);
        SetSkyStop(index: 0, color: DefaultSkyGroundColor, elevation: -1f);
        SetSkyStop(index: 1, color: DefaultSkyZenithColor, elevation: 1f);
        SkyStopCount = 2;
        SetLane(row: ControlRow, lane: 3, value: DefaultFogDensity);
        SetLane(row: CurvatureRow, lane: 3, value: DefaultCurvatureInkLow);
        SetLane(row: (CurvatureRow + 1), lane: 0, value: DefaultCurvatureInkColor.X);
        SetLane(row: (CurvatureRow + 1), lane: 1, value: DefaultCurvatureInkColor.Y);
        SetLane(row: (CurvatureRow + 1), lane: 2, value: DefaultCurvatureInkColor.Z);
        SetLane(row: (CurvatureRow + 1), lane: 3, value: DefaultCurvatureInkHigh);
        SetLane(row: SkyControlRow, lane: 1, value: -1f);
        SetLane(row: SkyControlRow, lane: 2, value: DefaultSunDiscRadians);
        SetLane(row: StarsRow, lane: 0, value: DefaultStarDensity);
        SetLane(row: TwinkleRow, lane: 2, value: DefaultTwinkleRate);
        SetLane(row: (CloudsRow + 1), lane: 0, value: DefaultCloudSoftness);
        SetLane(row: (CloudsRow + 1), lane: 1, value: DefaultCloudScale);
        SetVector(row: CloudsRow, value: Vector3.One);
    }
    /// <summary>Creates the environment an unauthored world renders: the pinned sun with shadows and the pinned
    /// hemisphere ambient, no sky.</summary>
    public static SdfEnvironment Default() {
        var environment = new SdfEnvironment();

        environment.SetLight(index: 0, light: new SdfLight(
            Kind: SdfLightKind.Directional,
            Direction: DefaultSunDirection,
            Color: Vector3.One,
            Weight: DefaultSunWeight,
            Param: DefaultPenumbraSlope,
            Shadows: true
        ));
        environment.SetLight(index: 1, light: new SdfLight(
            Kind: SdfLightKind.Hemisphere,
            Direction: Vector3.Zero,
            Color: Vector3.One,
            Weight: DefaultAmbientBase,
            Param: DefaultAmbientHemisphere,
            Shadows: false
        ));
        environment.LightCount = 2;

        return environment;
    }
    /// <summary>Gets the lanes, row-major, four per row.</summary>
    public ReadOnlySpan<float> Lanes => m_lanes;
    /// <summary>Copies every lane from another environment.</summary>
    public void CopyFrom(SdfEnvironment source) {
        ArgumentNullException.ThrowIfNull(argument: source);
        source.m_lanes.CopyTo(array: m_lanes, index: 0);
    }
    /// <summary>Copies every lane from a lane span.</summary>
    public void CopyFrom(ReadOnlySpan<float> lanes) {
        if (lanes.Length != LaneCount) {
            throw new ArgumentOutOfRangeException(paramName: nameof(lanes), message: $"An environment carries {LaneCount} lanes, not {lanes.Length}.");
        }

        lanes.CopyTo(destination: m_lanes);
    }
    /// <summary>Gets one lane.</summary>
    public float GetLane(int row, int lane) => m_lanes[((row * 4) + lane)];
    /// <summary>Sets one lane.</summary>
    public void SetLane(int row, int lane, float value) => m_lanes[((row * 4) + lane)] = value;
    /// <summary>Gets a row's first three lanes.</summary>
    public Vector3 GetVector(int row) => new(x: m_lanes[(row * 4)], y: m_lanes[((row * 4) + 1)], z: m_lanes[((row * 4) + 2)]);
    /// <summary>Sets a row's first three lanes.</summary>
    public void SetVector(int row, Vector3 value) {
        m_lanes[(row * 4)] = value.X; m_lanes[((row * 4) + 1)] = value.Y; m_lanes[((row * 4) + 2)] = value.Z;
    }
    /// <summary>Gets or sets the number of lights, at most <see cref="MaxLights"/>.</summary>
    public int LightCount {
        get => ((int)GetLane(row: ControlRow, lane: 0));
        set => SetLane(row: ControlRow, lane: 0, value: Math.Clamp(value: value, min: 0, max: MaxLights));
    }
    /// <summary>Gets the index of the light that drives the soft-shadow march, or −1.</summary>
    public int ShadowLightIndex => ((int)GetLane(row: ControlRow, lane: 1));
    /// <summary>Gets or sets whether the authored sky replaces the pinned two-stop gradient.</summary>
    public bool SkyEnabled {
        get => (GetLane(row: ControlRow, lane: 2) > 0.5f);
        set => SetLane(row: ControlRow, lane: 2, value: (value ? 1f : 0f));
    }
    /// <summary>Gets or sets the exponential distance-fog density.</summary>
    public float FogDensity {
        get => GetLane(row: ControlRow, lane: 3);
        set => SetLane(row: ControlRow, lane: 3, value: value);
    }
    /// <summary>Gets one light.</summary>
    public SdfLight GetLight(int index) {
        var row = (LightsRow + (index * RowsPerLight));

        return new SdfLight(
            Kind: ((SdfLightKind)((byte)GetLane(row: (row + 1), lane: 3))),
            Direction: GetVector(row: row),
            Color: GetVector(row: (row + 1)),
            Weight: GetLane(row: row, lane: 3),
            Param: GetLane(row: (row + 2), lane: 0),
            Shadows: (GetLane(row: (row + 2), lane: 1) > 0.5f)
        );
    }
    /// <summary>Sets one light and, when it shadows, makes it the shadow light.</summary>
    public void SetLight(int index, SdfLight light) {
        if ((index < 0) || (index >= MaxLights)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(index));
        }

        var row = (LightsRow + (index * RowsPerLight));

        SetVector(row: row, value: light.Direction);
        SetLane(row: row, lane: 3, value: light.Weight);
        SetVector(row: (row + 1), value: light.Color);
        SetLane(row: (row + 1), lane: 3, value: ((float)((byte)light.Kind)));
        SetLane(row: (row + 2), lane: 0, value: light.Param);
        SetLane(row: (row + 2), lane: 1, value: (light.Shadows ? 1f : 0f));
        SetLane(row: (row + 2), lane: 2, value: 0f);
        SetLane(row: (row + 2), lane: 3, value: 0f);

        if (light.Shadows && (light.Kind == SdfLightKind.Directional)) {
            SetLane(row: ControlRow, lane: 1, value: index);
        } else if (ShadowLightIndex == index) {
            SetLane(row: ControlRow, lane: 1, value: -1f);
        }
    }
    /// <summary>Gets the shadow light's direction, or the pinned sun's when no light shadows.</summary>
    public Vector3 KeyLightDirection => ((ShadowLightIndex >= 0)
        ? GetLight(index: ShadowLightIndex).Direction
        : DefaultSunDirection);
    /// <summary>Gets or sets the cavity-darkening gain.</summary>
    public float CurvatureCavity { get => GetLane(row: CurvatureRow, lane: 0); set => SetLane(row: CurvatureRow, lane: 0, value: value); }
    /// <summary>Gets or sets the curvature rim gain.</summary>
    public float CurvatureRim { get => GetLane(row: CurvatureRow, lane: 1); set => SetLane(row: CurvatureRow, lane: 1, value: value); }
    /// <summary>Gets or sets the ink outline gain.</summary>
    public float CurvatureInk { get => GetLane(row: CurvatureRow, lane: 2); set => SetLane(row: CurvatureRow, lane: 2, value: value); }
    /// <summary>Gets or sets the curvature magnitude at which the ink outline starts.</summary>
    public float CurvatureInkLow { get => GetLane(row: CurvatureRow, lane: 3); set => SetLane(row: CurvatureRow, lane: 3, value: value); }
    /// <summary>Gets or sets the curvature magnitude at which the ink outline saturates.</summary>
    public float CurvatureInkHigh { get => GetLane(row: (CurvatureRow + 1), lane: 3); set => SetLane(row: (CurvatureRow + 1), lane: 3, value: value); }
    /// <summary>Gets or sets the ink outline color.</summary>
    public Vector3 CurvatureInkColor { get => GetVector(row: (CurvatureRow + 1)); set => SetVector(row: (CurvatureRow + 1), value: value); }
    /// <summary>Gets or sets the gradient stop count, at most <see cref="MaxSkyStops"/>.</summary>
    public int SkyStopCount {
        get => ((int)GetLane(row: SkyControlRow, lane: 0));
        set => SetLane(row: SkyControlRow, lane: 0, value: Math.Clamp(value: value, min: 0, max: MaxSkyStops));
    }
    /// <summary>Gets one gradient stop's color and elevation.</summary>
    public (Vector3 Color, float Elevation) GetSkyStop(int index) => (GetVector(row: (SkyStopsRow + index)), GetLane(row: (SkyStopsRow + index), lane: 3));
    /// <summary>Sets one gradient stop.</summary>
    public void SetSkyStop(int index, Vector3 color, float elevation) {
        if ((index < 0) || (index >= MaxSkyStops)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(index));
        }

        SetVector(row: (SkyStopsRow + index), value: color);
        SetLane(row: (SkyStopsRow + index), lane: 3, value: elevation);
    }
    /// <summary>Gets or sets the light the sun disc is drawn about, or −1 for none.</summary>
    public int SunDiscLightIndex { get => ((int)GetLane(row: SkyControlRow, lane: 1)); set => SetLane(row: SkyControlRow, lane: 1, value: value); }
    /// <summary>Gets or sets the sun disc's angular radius in radians.</summary>
    public float SunDiscRadians { get => GetLane(row: SkyControlRow, lane: 2); set => SetLane(row: SkyControlRow, lane: 2, value: value); }
    /// <summary>Gets or sets the sun disc's peak additive brightness.</summary>
    public float SunDiscIntensity { get => GetLane(row: SkyControlRow, lane: 3); set => SetLane(row: SkyControlRow, lane: 3, value: value); }
    /// <summary>Gets or sets the star cell density.</summary>
    public float StarDensity { get => GetLane(row: StarsRow, lane: 0); set => SetLane(row: StarsRow, lane: 0, value: value); }
    /// <summary>Gets or sets the peak star brightness.</summary>
    public float StarBrightness { get => GetLane(row: StarsRow, lane: 1); set => SetLane(row: StarsRow, lane: 1, value: value); }
    /// <summary>Gets or sets the star hash seed.</summary>
    public uint StarSeed { get => ((uint)GetLane(row: StarsRow, lane: 2)); set => SetLane(row: StarsRow, lane: 2, value: value); }
    /// <summary>Gets or sets the twinkling share of the stars.</summary>
    public float TwinkleShare { get => GetLane(row: TwinkleRow, lane: 0); set => SetLane(row: TwinkleRow, lane: 0, value: value); }
    /// <summary>Gets or sets the twinkle depth.</summary>
    public float TwinkleDepth { get => GetLane(row: TwinkleRow, lane: 1); set => SetLane(row: TwinkleRow, lane: 1, value: value); }
    /// <summary>Gets or sets the scintillation rate in hertz.</summary>
    public float TwinkleRate { get => GetLane(row: TwinkleRow, lane: 2); set => SetLane(row: TwinkleRow, lane: 2, value: value); }
    /// <summary>Gets or sets the cloud color.</summary>
    public Vector3 CloudColor { get => GetVector(row: CloudsRow); set => SetVector(row: CloudsRow, value: value); }
    /// <summary>Gets or sets the cloud coverage in [0, 1].</summary>
    public float CloudCoverage { get => GetLane(row: CloudsRow, lane: 3); set => SetLane(row: CloudsRow, lane: 3, value: value); }
    /// <summary>Gets or sets the cloud edge softness in (0, 1].</summary>
    public float CloudSoftness { get => GetLane(row: (CloudsRow + 1), lane: 0); set => SetLane(row: (CloudsRow + 1), lane: 0, value: value); }
    /// <summary>Gets or sets the cloud cell scale in layer units.</summary>
    public float CloudScale { get => GetLane(row: (CloudsRow + 1), lane: 1); set => SetLane(row: (CloudsRow + 1), lane: 1, value: value); }
    /// <summary>Gets or sets the cloud hash seed.</summary>
    public uint CloudSeed { get => ((uint)GetLane(row: (CloudsRow + 1), lane: 2)); set => SetLane(row: (CloudsRow + 1), lane: 2, value: value); }
    /// <summary>Gets or sets the cloud drift in layer units per second.</summary>
    public Vector2 CloudDrift {
        get => new(x: GetLane(row: (CloudsRow + 2), lane: 0), y: GetLane(row: (CloudsRow + 2), lane: 1));
        set { SetLane(row: (CloudsRow + 2), lane: 0, value: value.X); SetLane(row: (CloudsRow + 2), lane: 1, value: value.Y); }
    }
    /// <summary>Gets or sets the shaping field's wind relative to the clouds, in layer units per second.</summary>
    public Vector2 CloudShear {
        get => new(x: GetLane(row: (CloudsRow + 2), lane: 2), y: GetLane(row: (CloudsRow + 2), lane: 3));
        set { SetLane(row: (CloudsRow + 2), lane: 2, value: value.X); SetLane(row: (CloudsRow + 2), lane: 3, value: value.Y); }
    }
    /// <summary>Gets or sets the cloud spin in radians per second.</summary>
    public float CloudSpin { get => GetLane(row: (CloudsRow + 3), lane: 0); set => SetLane(row: (CloudsRow + 3), lane: 0, value: value); }
    /// <summary>Gets or sets the Coriolis curl in radians at 45° elevation.</summary>
    public float CloudCurl { get => GetLane(row: (CloudsRow + 3), lane: 1); set => SetLane(row: (CloudsRow + 3), lane: 1, value: value); }
    /// <summary>Gets or sets the number of studio-reflection softboxes, at most <see cref="MaxSoftboxes"/>.</summary>
    public int SoftboxCount {
        get => ((int)GetLane(row: SoftboxControlRow, lane: 0));
        set => SetLane(row: SoftboxControlRow, lane: 0, value: Math.Clamp(value: value, min: 0, max: MaxSoftboxes));
    }
    /// <summary>Gets or sets the tonemap applied to the frame's final color.</summary>
    public SdfTonemapMode Tonemap {
        get => ((SdfTonemapMode)((byte)GetLane(row: SoftboxControlRow, lane: 1)));
        set => SetLane(row: SoftboxControlRow, lane: 1, value: ((float)((byte)value)));
    }
    /// <summary>Gets one studio-reflection softbox.</summary>
    public SdfSoftbox GetSoftbox(int index) {
        var row = (SoftboxesRow + (index * RowsPerSoftbox));

        return new SdfSoftbox(
            Direction: GetVector(row: row),
            Color: GetVector(row: (row + 1)),
            Weight: GetLane(row: row, lane: 3),
            Size: new Vector2(x: GetLane(row: (row + 1), lane: 3), y: GetLane(row: (row + 2), lane: 0)),
            Blur: GetLane(row: (row + 2), lane: 1)
        );
    }
    /// <summary>Sets one studio-reflection softbox.</summary>
    public void SetSoftbox(int index, SdfSoftbox softbox) {
        if ((index < 0) || (index >= MaxSoftboxes)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(index));
        }

        var row = (SoftboxesRow + (index * RowsPerSoftbox));

        SetVector(row: row, value: softbox.Direction);
        SetLane(row: row, lane: 3, value: softbox.Weight);
        SetVector(row: (row + 1), value: softbox.Color);
        SetLane(row: (row + 1), lane: 3, value: softbox.Size.X);
        SetLane(row: (row + 2), lane: 0, value: softbox.Size.Y);
        SetLane(row: (row + 2), lane: 1, value: softbox.Blur);
        SetLane(row: (row + 2), lane: 2, value: 0f);
        SetLane(row: (row + 2), lane: 3, value: 0f);
    }
    /// <summary>Gets or sets the studio-reflection horizon's low (ground-ward) color.</summary>
    public Vector3 HorizonLow { get => GetVector(row: HorizonLowRow); set => SetVector(row: HorizonLowRow, value: value); }
    /// <summary>Gets or sets the studio-reflection horizon's high (sky-ward) color.</summary>
    public Vector3 HorizonHigh { get => GetVector(row: HorizonHighRow); set => SetVector(row: HorizonHighRow, value: value); }
    /// <summary>Returns how a lane interpolates between two keys.</summary>
    public static SdfEnvironmentBlend BlendOf(int laneIndex) {
        var row = (laneIndex / 4);
        var lane = (laneIndex % 4);

        if (row == ControlRow) {
            return ((lane == 3) ? SdfEnvironmentBlend.Lerp : SdfEnvironmentBlend.Hold);
        }

        if ((row >= LightsRow) && (row < (LightsRow + (MaxLights * RowsPerLight)))) {
            var part = ((row - LightsRow) % RowsPerLight);

            return (part switch {
                0 => ((lane == 0) ? SdfEnvironmentBlend.Direction : ((lane == 3) ? SdfEnvironmentBlend.Lerp : SdfEnvironmentBlend.Hold)),
                1 => ((lane == 3) ? SdfEnvironmentBlend.Hold : SdfEnvironmentBlend.Lerp),
                _ => ((lane == 0) ? SdfEnvironmentBlend.Lerp : SdfEnvironmentBlend.Hold),
            });
        }

        if (row == SkyControlRow) {
            return ((lane >= 2) ? SdfEnvironmentBlend.Lerp : SdfEnvironmentBlend.Hold);
        }

        if (row == StarsRow) {
            return ((lane >= 2) ? SdfEnvironmentBlend.Hold : SdfEnvironmentBlend.Lerp);
        }

        if (row == (CloudsRow + 1)) {
            return ((lane >= 2) ? SdfEnvironmentBlend.Hold : SdfEnvironmentBlend.Lerp);
        }

        if (row == SoftboxControlRow) {
            return SdfEnvironmentBlend.Hold;
        }

        if ((row >= SoftboxesRow) && (row < (SoftboxesRow + (MaxSoftboxes * RowsPerSoftbox)))) {
            var part = ((row - SoftboxesRow) % RowsPerSoftbox);

            return (part switch {
                0 => ((lane == 0) ? SdfEnvironmentBlend.Direction : ((lane == 3) ? SdfEnvironmentBlend.Lerp : SdfEnvironmentBlend.Hold)),
                1 => SdfEnvironmentBlend.Lerp,
                _ => ((lane <= 1) ? SdfEnvironmentBlend.Lerp : SdfEnvironmentBlend.Hold),
            });
        }

        return SdfEnvironmentBlend.Lerp;
    }
    /// <summary>Writes the arc interpolation of two lane spans into a third: every lane by its
    /// <see cref="BlendOf"/> kind, the direction triples along the arc between their unit vectors.</summary>
    public static void Blend(ReadOnlySpan<float> from, ReadOnlySpan<float> to, float t, Span<float> into) {
        if ((from.Length != LaneCount) || (to.Length != LaneCount) || (into.Length != LaneCount)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(into), message: $"Every span carries {LaneCount} lanes.");
        }

        for (var index = 0; (index < LaneCount); index++) {
            switch (BlendOf(laneIndex: index)) {
                case SdfEnvironmentBlend.Lerp: {
                        into[index] = float.Lerp(value1: from[index], value2: to[index], amount: t);

                        break;
                    }
                case SdfEnvironmentBlend.Direction: {
                        var a = new Vector3(x: from[index], y: from[(index + 1)], z: from[(index + 2)]);
                        var b = new Vector3(x: to[index], y: to[(index + 1)], z: to[(index + 2)]);
                        var blended = Slerp(from: a, to: b, t: t);

                        into[index] = blended.X; into[(index + 1)] = blended.Y; into[(index + 2)] = blended.Z;
                        index += 2;

                        break;
                    }
                default: {
                        into[index] = from[index];

                        break;
                    }
            }
        }
    }
    // The arc between two directions. Antipodal directions have no unique arc; the one through +Y (then +X) is taken
    // so a sun crossing from east to west passes overhead rather than through the ground.
    private static Vector3 Slerp(Vector3 from, Vector3 to, float t) {
        var a = ((from.LengthSquared() > 1e-12f) ? Vector3.Normalize(value: from) : DefaultSunDirection);
        var b = ((to.LengthSquared() > 1e-12f) ? Vector3.Normalize(value: to) : a);
        var cosine = Math.Clamp(value: Vector3.Dot(vector1: a, vector2: b), min: -1f, max: 1f);

        if (cosine > 0.9995f) {
            var linear = Vector3.Lerp(value1: a, value2: b, amount: t);

            return ((linear.LengthSquared() > 1e-12f) ? Vector3.Normalize(value: linear) : a);
        }

        if (cosine < -0.9995f) {
            var pivot = ((MathF.Abs(x: a.Y) < 0.9f) ? Vector3.UnitY : Vector3.UnitX);
            var axis = Vector3.Normalize(value: Vector3.Cross(vector1: a, vector2: pivot));
            var rotation = Quaternion.CreateFromAxisAngle(axis: axis, angle: (MathF.PI * t));

            return Vector3.Normalize(value: Vector3.Transform(value: a, rotation: rotation));
        }

        var angle = MathF.Acos(x: cosine);
        var sine = MathF.Sin(x: angle);
        var weightA = (MathF.Sin(x: ((1f - t) * angle)) / sine);
        var weightB = (MathF.Sin(x: (t * angle)) / sine);

        return Vector3.Normalize(value: ((a * weightA) + (b * weightB)));
    }
}
