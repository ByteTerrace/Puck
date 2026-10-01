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
    /// <summary>A point light with inverse-square falloff and a soft core:
    /// <c>intensity = weight / (1 + (d / r)^2)</c>, <c>r</c> = <see cref="SdfLight.Param"/>. <see cref="SdfLight.Direction"/>
    /// carries the light's WORLD-SPACE POSITION rather than a direction; <see cref="SdfLight.DynamicSlot"/>
    /// optionally rides a dynamic transform so the position follows an anchored shape every frame instead of the
    /// authored (possibly stale) position. Lambert diffuse plus the material's GGX response from the point
    /// direction, both scaled by ambient occlusion like every non-shadow light. No shadow march in v1 — a point
    /// light never occludes and is never occluded.</summary>
    Point = 3,
    /// <summary>A bounded attenuation field with position, radius, and weight in [0, 1].</summary>
    Occluder = 4,
}
/// <summary>One light of the lit path.</summary>
/// <param name="Kind">What the light is.</param>
/// <param name="Direction">Directional: from the surface toward the light, any nonzero length (normalized on
/// upload). Point: the light's world-space POSITION instead (see <see cref="SdfLightKind.Point"/>).</param>
/// <param name="Color">The linear RGB color.</param>
/// <param name="Weight">The strength; a hemisphere's floor.</param>
/// <param name="Param">The kind's second scalar: penumbra half-slope, hemisphere gradient, rim exponent, or (point)
/// the falloff radius.</param>
/// <param name="Shadows">Directional only: whether this light drives the soft-shadow march.</param>
/// <param name="DynamicSlot">Point and occluder only: the dynamic-transform slot its position is read from every frame,
/// or <see cref="SdfProgram.NoDynamicTransformSlot"/> for the static authored position in <paramref name="Direction"/>.
/// Ignored (packed as 0) for every other kind.</param>
public readonly record struct SdfLight(SdfLightKind Kind, Vector3 Direction, Vector3 Color, float Weight, float Param, bool Shadows, int DynamicSlot = SdfProgram.NoDynamicTransformSlot);
/// <summary>One analytic studio-reflection softbox — see <c>worldStudioReflection</c> in shade/sdf-lighting.hlsli.</summary>
/// <param name="Direction">From a lit surface toward the softbox, any nonzero length (normalized on upload).</param>
/// <param name="Color">The linear RGB color.</param>
/// <param name="Weight">The strength.</param>
/// <param name="Size">The angular half-extent proxy (width, height) the falloff widens by.</param>
/// <param name="Blur">Additional falloff softening, in the same units as <paramref name="Size"/>; 0 = none.</param>
public readonly record struct SdfSoftbox(Vector3 Direction, Vector3 Color, float Weight, Vector2 Size, float Blur);
/// <summary>The lit path's per-frame environment — every light, the stylization gains, and the sky — as one lane
/// table every SDF pass block carries as float4 rows (<c>SdfFrameBlock.BakeEnvironment</c>, which also performs the host
/// bakes noted per row). The kernels read the row indices generated from these constants (<c>SDF_ENV_*</c> in
/// <c>sdf-isa.hlsli</c>) and decode each row's lanes in <c>frame/sdf-lights.hlsli</c>; KEEP IN SYNC with those
/// decoders.</summary>
/// <remarks>
/// Row layout (row-relative to the environment base, four float lanes per row):
/// <list type="table">
/// <item><term>0 control</term><description>x light count, y shadow light index (−1 none), z sky enabled, w fog density</description></item>
/// <item><term>1 + 3i .. 3 + 3i, i &lt; 8</term><description>light i: (direction.xyz — a position for a point light, weight) (color.rgb, kind) (param, shadows, dynamicSlot — point or occluder only, else 0, 0)</description></item>
/// <item><term>25</term><description>curvature: cavity, rim, ink, ink band low</description></item>
/// <item><term>26</term><description>ink color.rgb, ink band high</description></item>
/// <item><term>27 sky control</term><description>x gradient stop count, y sun-disc light index (−1 none), z sun-disc angular radius in radians (uploaded as the baked <c>pow</c> exponent), w sun-disc intensity</description></item>
/// <item><term>28 .. 31</term><description>gradient stop i: color.rgb, elevation in [−1, 1] (ascending)</description></item>
/// <item><term>32 stars</term><description>density, brightness, seed, 0</description></item>
/// <item><term>33 twinkle</term><description>share, depth, the twinkle's phase in cycles at the frame's presented tick (its rate integrated by the host), 0</description></item>
/// <item><term>34 clouds A</term><description>color.rgb, coverage</description></item>
/// <item><term>35 clouds B</term><description>softness, scale, seed, 0</description></item>
/// <item><term>36 clouds C</term><description>drift offset.xy, shear offset.xy — in layer units, the drift and shear rates integrated by the host to the frame's presented tick and reduced by the noise's lattice period</description></item>
/// <item><term>37 clouds D</term><description>spin angle in radians (the spin rate integrated by the host and reduced by a whole turn), curl, 0, 0</description></item>
/// <item><term>38 softbox control</term><description>x softbox count, 0, 0, 0</description></item>
/// <item><term>39 + 3i .. 41 + 3i, i &lt; 4</term><description>softbox i: (direction.xyz, weight) (color.rgb, size.x) (size.y, blur, 0, 0)</description></item>
/// <item><term>51</term><description>studio reflection horizon low (ground-ward) color.rgb, 0</description></item>
/// <item><term>52</term><description>studio reflection horizon high (sky-ward) color.rgb, 0</description></item>
/// </list>
/// </remarks>
public sealed class SdfEnvironment {
    /// <summary>The first cloud row.</summary>
    public const int CloudsRow = 34;
    /// <summary>The control row.</summary>
    public const int ControlRow = 0;
    /// <summary>The first curvature row.</summary>
    public const int CurvatureRow = 25;
    /// <summary>The pinned ambient floor.</summary>
    public const float DefaultAmbientBase = 0.25f;
    /// <summary>The pinned ambient hemisphere gradient.</summary>
    public const float DefaultAmbientHemisphere = 0.25f;
    /// <summary>The default cloud cell scale.</summary>
    public const float DefaultCloudScale = 2f;
    /// <summary>The default cloud edge softness.</summary>
    public const float DefaultCloudSoftness = 0.25f;
    /// <summary>The default curvature magnitude at which the ink outline saturates.</summary>
    public const float DefaultCurvatureInkHigh = 16f;
    /// <summary>The default curvature magnitude at which the ink outline starts.</summary>
    public const float DefaultCurvatureInkLow = 6f;
    /// <summary>The pinned fog density.</summary>
    public const float DefaultFogDensity = 0.015f;
    /// <summary>The pinned penumbra half-slope: the retired <c>1/ShadowSharpness</c>.</summary>
    public const float DefaultPenumbraSlope = (1f / 9f);
    /// <summary>The default point-light falloff radius, in world units.</summary>
    public const float DefaultPointRadius = 1f;
    /// <summary>The default point-light weight.</summary>
    public const float DefaultPointWeight = 1f;
    /// <summary>The default rim exponent.</summary>
    public const float DefaultRimPower = 3f;
    /// <summary>The default star cell density.</summary>
    public const float DefaultStarDensity = 48f;
    /// <summary>The default sun-disc angular radius in radians.</summary>
    public const float DefaultSunDiscRadians = 0.05f;
    /// <summary>The pinned sun diffuse weight.</summary>
    public const float DefaultSunWeight = 0.85f;
    /// <summary>The default scintillation rate in hertz.</summary>
    public const float DefaultTwinkleRate = 1f;
    /// <summary>The studio-reflection horizon's high (sky-ward) color row.</summary>
    public const int HorizonHighRow = 52;
    /// <summary>The studio-reflection horizon's low (ground-ward) color row.</summary>
    public const int HorizonLowRow = 51;
    /// <summary>The float lanes the environment occupies.</summary>
    public const int LaneCount = (RowCount * 4);
    /// <summary>The first light's first row.</summary>
    public const int LightsRow = 1;
    /// <summary>The most lights a frame carries.</summary>
    public const int MaxLights = 8;
    /// <summary>The largest penumbra half-slope a directional light admits; the shadow gather's cone is three times
    /// it and must stay a valid chord.</summary>
    public const float MaxPenumbraSlope = 0.3f;
    /// <summary>The most gradient stops a sky carries.</summary>
    public const int MaxSkyStops = 4;
    /// <summary>The most studio-reflection softboxes a frame carries.</summary>
    public const int MaxSoftboxes = 4;
    /// <summary>The rows the environment occupies in a pass block's environment array.</summary>
    public const int RowCount = 53;
    /// <summary>Rows per light.</summary>
    public const int RowsPerLight = 3;
    /// <summary>Rows per softbox.</summary>
    public const int RowsPerSoftbox = 3;
    /// <summary>The sky control row.</summary>
    public const int SkyControlRow = 27;
    /// <summary>The first gradient stop row.</summary>
    public const int SkyStopsRow = 28;
    /// <summary>The softbox control row.</summary>
    public const int SoftboxControlRow = 38;
    /// <summary>The first softbox's first row.</summary>
    public const int SoftboxesRow = 39;
    /// <summary>The star row.</summary>
    public const int StarsRow = 32;
    /// <summary>The twinkle row.</summary>
    public const int TwinkleRow = 33;

    private readonly float[] m_lanes = new float[LaneCount];

    /// <summary>The pinned sun direction, as the shaders held it before the environment became per-frame data.</summary>
    public static Vector3 DefaultSunDirection { get; } = new(
        x: 0.51343602f,
        y: 0.79349202f,
        z: 0.32673201f
    );
    /// <summary>The default ink outline color.</summary>
    public static Vector3 DefaultCurvatureInkColor { get; } = new(
        x: 0.02f,
        y: 0.02f,
        z: 0.03f
    );
    /// <summary>The pinned zenith color of the two-stop sky an unauthored world renders.</summary>
    public static Vector3 DefaultSkyZenithColor { get; } = new(
        x: 0.10f,
        y: 0.13f,
        z: 0.20f
    );
    /// <summary>The pinned ground color of the two-stop sky an unauthored world renders.</summary>
    public static Vector3 DefaultSkyGroundColor { get; } = new(
        x: 0.04f,
        y: 0.05f,
        z: 0.07f
    );

    /// <summary>Creates an environment with no lights, the sky disabled (the pinned two-stop gradient seeded in its
    /// stops, so a layer drawn over an unauthored gradient has one to draw over), and every gain zero.</summary>
    public SdfEnvironment() {
        SetLane(
            lane: 1,
            row: ControlRow,
            value: -1f
        );
        SetSkyStop(
            index: 0,
            color: DefaultSkyGroundColor,
            elevation: -1f
        );
        SetSkyStop(
            index: 1,
            color: DefaultSkyZenithColor,
            elevation: 1f
        );
        SkyStopCount = 2;
        SetLane(
            lane: 3,
            row: ControlRow,
            value: DefaultFogDensity
        );
        SetLane(
            lane: 3,
            row: CurvatureRow,
            value: DefaultCurvatureInkLow
        );
        SetLane(
            row: (CurvatureRow + 1),
            lane: 0,
            value: DefaultCurvatureInkColor.X
        );
        SetLane(
            row: (CurvatureRow + 1),
            lane: 1,
            value: DefaultCurvatureInkColor.Y
        );
        SetLane(
            row: (CurvatureRow + 1),
            lane: 2,
            value: DefaultCurvatureInkColor.Z
        );
        SetLane(
            lane: 3,
            row: (CurvatureRow + 1),
            value: DefaultCurvatureInkHigh
        );
        SetLane(
            lane: 1,
            row: SkyControlRow,
            value: -1f
        );
        SetLane(
            lane: 2,
            row: SkyControlRow,
            value: DefaultSunDiscRadians
        );
        SetLane(
            lane: 0,
            row: StarsRow,
            value: DefaultStarDensity
        );
        SetLane(
            lane: 0,
            row: (CloudsRow + 1),
            value: DefaultCloudSoftness
        );
        SetLane(
            lane: 1,
            row: (CloudsRow + 1),
            value: DefaultCloudScale
        );
        SetVector(
            row: CloudsRow,
            value: Vector3.One
        );
    }

    /// <summary>Copies every lane from another environment.</summary>
    public void CopyFrom(SdfEnvironment source) {
        ArgumentNullException.ThrowIfNull(argument: source);
        source.m_lanes.CopyTo(
            array: m_lanes,
            index: 0
        );
    }
    /// <summary>Copies every lane from a lane span.</summary>
    public void CopyFrom(ReadOnlySpan<float> lanes) {
        if (lanes.Length != LaneCount) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(lanes),
                message: $"An environment carries {LaneCount} lanes, not {lanes.Length}."
            );
        }

        lanes.CopyTo(destination: m_lanes);
    }
    /// <summary>Creates the environment an unauthored world renders: the pinned sun with shadows and the pinned
    /// hemisphere ambient, no sky.</summary>
    public static SdfEnvironment Default() {
        var environment = new SdfEnvironment();

        environment.SetLight(
            index: 0,
            light: new SdfLight(
                Kind: SdfLightKind.Directional,
                Direction: DefaultSunDirection,
                Color: Vector3.One,
                Weight: DefaultSunWeight,
                Param: DefaultPenumbraSlope,
                Shadows: true
            )
        );
        environment.SetLight(
            index: 1,
            light: new SdfLight(
                Kind: SdfLightKind.Hemisphere,
                Direction: Vector3.Zero,
                Color: Vector3.One,
                Weight: DefaultAmbientBase,
                Param: DefaultAmbientHemisphere,
                Shadows: false
            )
        );
        environment.LightCount = 2;

        return environment;
    }
    /// <summary>Gets one lane.</summary>
    public float GetLane(int row, int lane) => m_lanes[((row * 4) + lane)];
    /// <summary>Gets one light.</summary>
    public SdfLight GetLight(int index) {
        var row = (LightsRow + (index * RowsPerLight));

        return new SdfLight(
            Kind: ((SdfLightKind)((byte)GetLane(
                lane: 3,
                row: (row + 1)
            ))),
            Direction: GetVector(row: row),
            Color: GetVector(row: (row + 1)),
            Weight: GetLane(
                lane: 3,
                row: row
            ),
            Param: GetLane(
                lane: 0,
                row: (row + 2)
            ),
            Shadows: (GetLane(
                lane: 1,
                row: (row + 2)
            ) > 0.5f),
            DynamicSlot: ((int)GetLane(
                lane: 2,
                row: (row + 2)
            ))
        );
    }
    /// <summary>Gets one gradient stop's color and elevation.</summary>
    public (Vector3 Color, float Elevation) GetSkyStop(int index) => (GetVector(row: (SkyStopsRow + index)), GetLane(
        lane: 3,
        row: (SkyStopsRow + index)
    ));
    /// <summary>Gets one studio-reflection softbox.</summary>
    public SdfSoftbox GetSoftbox(int index) {
        var row = (SoftboxesRow + (index * RowsPerSoftbox));

        return new SdfSoftbox(
            Direction: GetVector(row: row),
            Color: GetVector(row: (row + 1)),
            Weight: GetLane(
                lane: 3,
                row: row
            ),
            Size: new Vector2(
                x: GetLane(
                    lane: 3,
                    row: (row + 1)
                ),
                y: GetLane(
                    lane: 0,
                    row: (row + 2)
                )
            ),
            Blur: GetLane(
                lane: 1,
                row: (row + 2)
            )
        );
    }
    /// <summary>Gets a row's first three lanes.</summary>
    public Vector3 GetVector(int row) => new(
        x: m_lanes[(row * 4)],
        y: m_lanes[((row * 4) + 1)],
        z: m_lanes[((row * 4) + 2)]
    );
    /// <summary>Sets one lane.</summary>
    public void SetLane(int row, int lane, float value) => m_lanes[((row * 4) + lane)] = value;
    /// <summary>Sets one light and, when it shadows, makes it the shadow light.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the light table, or a point or
    /// occluder light's <see cref="SdfLight.DynamicSlot"/> is neither <see cref="SdfProgram.NoDynamicTransformSlot"/>
    /// nor a slot in [0, <see cref="SdfProgram.MaxDynamicTransformSlot"/>].</exception>
    public void SetLight(int index, SdfLight light) {
        if (
            (index < 0) ||
            (index >= MaxLights)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(index));
        }
        if (
            (light.Kind is SdfLightKind.Point or SdfLightKind.Occluder) &&
            (light.DynamicSlot != SdfProgram.NoDynamicTransformSlot) &&
            ((light.DynamicSlot < 0) || (light.DynamicSlot > SdfProgram.MaxDynamicTransformSlot))
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"A point or occluder light's dynamic slot must be {SdfProgram.NoDynamicTransformSlot} or in [0, {SdfProgram.MaxDynamicTransformSlot}].",
                paramName: nameof(light)
            );
        }

        var row = (LightsRow + (index * RowsPerLight));

        SetVector(
            row: row,
            value: light.Direction
        );
        SetLane(
            row: row,
            lane: 3,
            value: light.Weight
        );
        SetVector(
            row: (row + 1),
            value: light.Color
        );
        SetLane(
            row: (row + 1),
            lane: 3,
            value: ((float)((byte)light.Kind))
        );
        SetLane(
            row: (row + 2),
            lane: 0,
            value: light.Param
        );
        SetLane(
            row: (row + 2),
            lane: 1,
            value: (light.Shadows
            ? 1f
            : 0f)
        );
        // Packed only for a point or occluder light; every other kind packs 0.
        SetLane(
            row: (row + 2),
            lane: 2,
            value: ((light.Kind is SdfLightKind.Point or SdfLightKind.Occluder)
            ? light.DynamicSlot
            : 0f)
        );
        SetLane(
            lane: 3,
            row: (row + 2),
            value: 0f
        );

        if (
            light.Shadows &&
            (light.Kind == SdfLightKind.Directional)
        ) {
            SetLane(
                lane: 1,
                row: ControlRow,
                value: index
            );
        } else if (ShadowLightIndex == index) {
            SetLane(
                lane: 1,
                row: ControlRow,
                value: -1f
            );
        }
    }
    /// <summary>Sets one gradient stop.</summary>
    public void SetSkyStop(int index, Vector3 color, float elevation) {
        if (
            (index < 0) ||
            (index >= MaxSkyStops)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(index));
        }

        SetVector(
            row: (SkyStopsRow + index),
            value: color
        );
        SetLane(
            lane: 3,
            row: (SkyStopsRow + index),
            value: elevation
        );
    }
    /// <summary>Sets one studio-reflection softbox.</summary>
    public void SetSoftbox(int index, SdfSoftbox softbox) {
        if (
            (index < 0) ||
            (index >= MaxSoftboxes)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(index));
        }

        var row = (SoftboxesRow + (index * RowsPerSoftbox));

        SetVector(
            row: row,
            value: softbox.Direction
        );
        SetLane(
            row: row,
            lane: 3,
            value: softbox.Weight
        );
        SetVector(
            row: (row + 1),
            value: softbox.Color
        );
        SetLane(
            row: (row + 1),
            lane: 3,
            value: softbox.Size.X
        );
        SetLane(
            row: (row + 2),
            lane: 0,
            value: softbox.Size.Y
        );
        SetLane(
            row: (row + 2),
            lane: 1,
            value: softbox.Blur
        );
        SetLane(
            lane: 2,
            row: (row + 2),
            value: 0f
        );
        SetLane(
            lane: 3,
            row: (row + 2),
            value: 0f
        );
    }
    /// <summary>Sets a row's first three lanes.</summary>
    public void SetVector(int row, Vector3 value) {
        m_lanes[(row * 4)] = value.X; m_lanes[((row * 4) + 1)] = value.Y; m_lanes[((row * 4) + 2)] = value.Z;
    }

    /// <summary>Gets or sets the cloud color.</summary>
    public Vector3 CloudColor {
        get => GetVector(row: CloudsRow); set => SetVector(
        row: CloudsRow,
        value: value
    );
    }
    /// <summary>Gets or sets the cloud coverage in [0, 1].</summary>
    public float CloudCoverage {
        get => GetLane(
        lane: 3,
        row: CloudsRow
    ); set => SetLane(
        lane: 3,
        row: CloudsRow,
        value: value
    );
    }
    /// <summary>Gets or sets the Coriolis curl in radians at 45° elevation.</summary>
    public float CloudCurl {
        get => GetLane(
        lane: 1,
        row: (CloudsRow + 3)
    ); set => SetLane(
        lane: 1,
        row: (CloudsRow + 3),
        value: value
    );
    }
    /// <summary>Gets or sets the cloud drift's offset at the frame's presented tick, in layer units: the drift rate
    /// integrated by the host and reduced by the noise's lattice period.</summary>
    public Vector2 CloudDriftOffset {
        get => new(
            x: GetLane(
                lane: 0,
                row: (CloudsRow + 2)
            ),
            y: GetLane(
                lane: 1,
                row: (CloudsRow + 2)
            )
        );
        set {
            SetLane(
            lane: 0,
            row: (CloudsRow + 2),
            value: value.X
        ); SetLane(
            lane: 1,
            row: (CloudsRow + 2),
            value: value.Y
        );
        }
    }
    /// <summary>Gets or sets the cloud cell scale in layer units.</summary>
    public float CloudScale {
        get => GetLane(
        lane: 1,
        row: (CloudsRow + 1)
    ); set => SetLane(
        lane: 1,
        row: (CloudsRow + 1),
        value: value
    );
    }
    /// <summary>Gets or sets the cloud hash seed.</summary>
    public uint CloudSeed {
        get => ((uint)GetLane(
        lane: 2,
        row: (CloudsRow + 1)
    )); set => SetLane(
        lane: 2,
        row: (CloudsRow + 1),
        value: value
    );
    }
    /// <summary>Gets or sets the shaping field's offset relative to the clouds at the frame's presented tick, in layer
    /// units: the shear rate integrated by the host and reduced by the noise's lattice period.</summary>
    public Vector2 CloudShearOffset {
        get => new(
            x: GetLane(
                lane: 2,
                row: (CloudsRow + 2)
            ),
            y: GetLane(
                lane: 3,
                row: (CloudsRow + 2)
            )
        );
        set {
            SetLane(
            lane: 2,
            row: (CloudsRow + 2),
            value: value.X
        ); SetLane(
            lane: 3,
            row: (CloudsRow + 2),
            value: value.Y
        );
        }
    }
    /// <summary>Gets or sets the cloud edge softness in (0, 1].</summary>
    public float CloudSoftness {
        get => GetLane(
        lane: 0,
        row: (CloudsRow + 1)
    ); set => SetLane(
        lane: 0,
        row: (CloudsRow + 1),
        value: value
    );
    }
    /// <summary>Gets or sets the cloud layer's rotation about the zenith at the frame's presented tick, in radians: the
    /// spin rate integrated by the host and reduced by a whole turn.</summary>
    public float CloudSpinAngle {
        get => GetLane(
        lane: 0,
        row: (CloudsRow + 3)
    ); set => SetLane(
        lane: 0,
        row: (CloudsRow + 3),
        value: value
    );
    }
    /// <summary>Gets or sets the cavity-darkening gain.</summary>
    public float CurvatureCavity {
        get => GetLane(
        lane: 0,
        row: CurvatureRow
    ); set => SetLane(
        lane: 0,
        row: CurvatureRow,
        value: value
    );
    }
    /// <summary>Gets or sets the ink outline gain.</summary>
    public float CurvatureInk {
        get => GetLane(
        lane: 2,
        row: CurvatureRow
    ); set => SetLane(
        lane: 2,
        row: CurvatureRow,
        value: value
    );
    }
    /// <summary>Gets or sets the ink outline color.</summary>
    public Vector3 CurvatureInkColor {
        get => GetVector(row: (CurvatureRow + 1)); set => SetVector(
        row: (CurvatureRow + 1),
        value: value
    );
    }
    /// <summary>Gets or sets the curvature magnitude at which the ink outline saturates.</summary>
    public float CurvatureInkHigh {
        get => GetLane(
        lane: 3,
        row: (CurvatureRow + 1)
    ); set => SetLane(
        lane: 3,
        row: (CurvatureRow + 1),
        value: value
    );
    }
    /// <summary>Gets or sets the curvature magnitude at which the ink outline starts.</summary>
    public float CurvatureInkLow {
        get => GetLane(
        lane: 3,
        row: CurvatureRow
    ); set => SetLane(
        lane: 3,
        row: CurvatureRow,
        value: value
    );
    }
    /// <summary>Gets or sets the curvature rim gain.</summary>
    public float CurvatureRim {
        get => GetLane(
        lane: 1,
        row: CurvatureRow
    ); set => SetLane(
        lane: 1,
        row: CurvatureRow,
        value: value
    );
    }
    /// <summary>Gets or sets the exponential distance-fog density.</summary>
    public float FogDensity {
        get => GetLane(
            lane: 3,
            row: ControlRow
        );
        set => SetLane(
            lane: 3,
            row: ControlRow,
            value: value
        );
    }
    /// <summary>Gets or sets the studio-reflection horizon's high (sky-ward) color.</summary>
    public Vector3 HorizonHigh {
        get => GetVector(row: HorizonHighRow); set => SetVector(
        row: HorizonHighRow,
        value: value
    );
    }
    /// <summary>Gets or sets the studio-reflection horizon's low (ground-ward) color.</summary>
    public Vector3 HorizonLow {
        get => GetVector(row: HorizonLowRow); set => SetVector(
        row: HorizonLowRow,
        value: value
    );
    }
    /// <summary>Gets the shadow light's direction, or the pinned sun's when no light shadows.</summary>
    public Vector3 KeyLightDirection => ((ShadowLightIndex >= 0)
        ? GetLight(index: ShadowLightIndex).Direction
        : DefaultSunDirection
    );
    /// <summary>Gets the lanes, row-major, four per row.</summary>
    public ReadOnlySpan<float> Lanes => m_lanes;
    /// <summary>Gets or sets the number of lights, at most <see cref="MaxLights"/>.</summary>
    public int LightCount {
        get => ((int)GetLane(
            lane: 0,
            row: ControlRow
        ));
        set => SetLane(
            row: ControlRow,
            lane: 0,
            value: Math.Clamp(
                max: MaxLights,
                min: 0,
                value: value
            )
        );
    }
    /// <summary>Gets the index of the light that drives the soft-shadow march, or −1.</summary>
    public int ShadowLightIndex => ((int)GetLane(
        lane: 1,
        row: ControlRow
    ));
    /// <summary>Gets or sets whether the authored sky replaces the pinned two-stop gradient.</summary>
    public bool SkyEnabled {
        get => (GetLane(
            lane: 2,
            row: ControlRow
        ) > 0.5f);
        set => SetLane(
            lane: 2,
            row: ControlRow,
            value: (value
            ? 1f
            : 0f)
        );
    }
    /// <summary>Gets or sets the gradient stop count, at most <see cref="MaxSkyStops"/>.</summary>
    public int SkyStopCount {
        get => ((int)GetLane(
            lane: 0,
            row: SkyControlRow
        ));
        set => SetLane(
            row: SkyControlRow,
            lane: 0,
            value: Math.Clamp(
                max: MaxSkyStops,
                min: 0,
                value: value
            )
        );
    }
    /// <summary>Gets or sets the number of studio-reflection softboxes, at most <see cref="MaxSoftboxes"/>.</summary>
    public int SoftboxCount {
        get => ((int)GetLane(
            lane: 0,
            row: SoftboxControlRow
        ));
        set => SetLane(
            row: SoftboxControlRow,
            lane: 0,
            value: Math.Clamp(
                max: MaxSoftboxes,
                min: 0,
                value: value
            )
        );
    }
    /// <summary>Gets or sets the peak star brightness.</summary>
    public float StarBrightness {
        get => GetLane(
        lane: 1,
        row: StarsRow
    ); set => SetLane(
        lane: 1,
        row: StarsRow,
        value: value
    );
    }
    /// <summary>Gets or sets the star cell density.</summary>
    public float StarDensity {
        get => GetLane(
        lane: 0,
        row: StarsRow
    ); set => SetLane(
        lane: 0,
        row: StarsRow,
        value: value
    );
    }
    /// <summary>Gets or sets the star hash seed.</summary>
    public uint StarSeed {
        get => ((uint)GetLane(
        lane: 2,
        row: StarsRow
    )); set => SetLane(
        lane: 2,
        row: StarsRow,
        value: value
    );
    }
    /// <summary>Gets or sets the sun disc's peak additive brightness.</summary>
    public float SunDiscIntensity {
        get => GetLane(
        lane: 3,
        row: SkyControlRow
    ); set => SetLane(
        lane: 3,
        row: SkyControlRow,
        value: value
    );
    }
    /// <summary>Gets or sets the light the sun disc is drawn about, or −1 for none.</summary>
    public int SunDiscLightIndex {
        get => ((int)GetLane(
        lane: 1,
        row: SkyControlRow
    )); set => SetLane(
        lane: 1,
        row: SkyControlRow,
        value: value
    );
    }
    /// <summary>Gets or sets the sun disc's angular radius in radians.</summary>
    public float SunDiscRadians {
        get => GetLane(
        lane: 2,
        row: SkyControlRow
    ); set => SetLane(
        lane: 2,
        row: SkyControlRow,
        value: value
    );
    }
    /// <summary>Gets or sets the twinkle depth.</summary>
    public float TwinkleDepth {
        get => GetLane(
        lane: 1,
        row: TwinkleRow
    ); set => SetLane(
        lane: 1,
        row: TwinkleRow,
        value: value
    );
    }
    /// <summary>Gets or sets the twinkle's phase at the frame's presented tick, in cycles in <c>[0, 1)</c>: the
    /// scintillation rate integrated by the host, and zero while no star twinkles visibly, so a still sky's block
    /// repeats.</summary>
    public float TwinklePhase {
        get => GetLane(
        lane: 2,
        row: TwinkleRow
    ); set => SetLane(
        lane: 2,
        row: TwinkleRow,
        value: value
    );
    }
    /// <summary>Gets or sets the twinkling share of the stars.</summary>
    public float TwinkleShare {
        get => GetLane(
        lane: 0,
        row: TwinkleRow
    ); set => SetLane(
        lane: 0,
        row: TwinkleRow,
        value: value
    );
    }
}
