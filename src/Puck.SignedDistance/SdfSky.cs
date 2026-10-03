using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

/// <summary>
/// The sky block the sky, composite, resolve and views passes read (<c>sdfSky</c>, a World-group structured buffer of
/// one record): the sun disc, the stars and their twinkle, the cloud layer, the studio reflection's horizon, and the
/// atmosphere's lanes (<see cref="SdfAtmosphere"/>, with the air lights it scatters). Its public fields are the record's layout, whose HLSL declaration <c>puck shaders generate</c> writes from
/// this type. <see cref="SdfSky"/> holds the authored values and packs this record with its host bakes
/// (<see cref="SdfSky.Pack"/>): the fields documented as baked are written there and nowhere else.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 368)]
public record struct SdfSkyBlock {
    /// <summary>The atmosphere's flags: <see cref="SdfAir.FogColorAuthored"/> when the fog in-scatters
    /// <see cref="FogColor"/> rather than the sky.</summary>
    [FieldOffset(0)] public uint AirFlags;
    /// <summary>The gradient stops the stops table holds, at least two and at most <see cref="SdfSky.MaxStops"/>.</summary>
    [FieldOffset(4)] public uint StopCount;
    /// <summary>The studio-reflection softboxes the softbox table holds, at most <see cref="SdfSky.MaxSoftboxes"/>.</summary>
    [FieldOffset(8)] public uint SoftboxCount;
    /// <summary>The light the sun disc is drawn about, or −1 when no disc is drawn.</summary>
    [FieldOffset(12)] public int DiscLight;
    /// <summary>Baked: the direction the sun disc is drawn about, its light's packed direction.</summary>
    [FieldOffset(16)] public Vector3 DiscDirection;
    /// <summary>The sun disc's peak additive brightness.</summary>
    [FieldOffset(28)] public float DiscIntensity;
    /// <summary>Baked: the <c>pow()</c> exponent that puts the disc's edge, at its authored angular radius r, at half
    /// brightness: k = ln 0.5 / ln cos r.</summary>
    [FieldOffset(32)] public float DiscExponent;
    /// <summary>Baked: the air lights the haze and the bounded media scatter, at most <see cref="SdfAir.MaxLights"/>: the
    /// lit directional lights, in table order, which are the light-casting bodies.</summary>
    [FieldOffset(36)] public uint AirLightCount;
    /// <summary>The star cell density.</summary>
    [FieldOffset(40)] public float StarDensity;
    /// <summary>The peak star brightness.</summary>
    [FieldOffset(44)] public float StarBrightness;
    /// <summary>The star hash seed.</summary>
    [FieldOffset(48)] public uint StarSeed;
    /// <summary>The twinkling share of the stars.</summary>
    [FieldOffset(52)] public float TwinkleShare;
    /// <summary>The twinkle depth.</summary>
    [FieldOffset(56)] public float TwinkleDepth;
    /// <summary>The twinkle's phase at the frame's presented tick, in cycles in <c>[0, 1)</c>: the scintillation rate
    /// integrated by the host, and zero while no star twinkles visibly, so a still sky's block repeats.</summary>
    [FieldOffset(60)] public float TwinklePhase;
    /// <summary>The cloud color.</summary>
    [FieldOffset(64)] public Vector3 CloudColor;
    /// <summary>The cloud coverage in [0, 1].</summary>
    [FieldOffset(76)] public float CloudCoverage;
    /// <summary>The cloud edge softness in (0, 1].</summary>
    [FieldOffset(80)] public float CloudSoftness;
    /// <summary>The cloud cell scale in layer units.</summary>
    [FieldOffset(84)] public float CloudScale;
    /// <summary>The cloud hash seed.</summary>
    [FieldOffset(88)] public uint CloudSeed;
    /// <summary>The Coriolis curl in radians at 45° elevation.</summary>
    [FieldOffset(92)] public float CloudCurl;
    /// <summary>The cloud drift's offset at the frame's presented tick, in layer units: the drift rate integrated by the
    /// host and reduced by the noise's lattice period.</summary>
    [FieldOffset(96)] public Vector2 CloudDriftOffset;
    /// <summary>The shaping field's offset relative to the clouds at the frame's presented tick, in layer units: the
    /// shear rate integrated by the host and reduced by the noise's lattice period.</summary>
    [FieldOffset(104)] public Vector2 CloudShearOffset;
    /// <summary>Baked: the unit direction toward the light the clouds are lit by: the shadow light's, or the pinned
    /// sun's when no light shadows.</summary>
    [FieldOffset(112)] public Vector3 CloudLightDirection;
    /// <summary>The cloud layer's rotation about the zenith at the frame's presented tick, in radians: the spin rate
    /// integrated by the host and reduced by a whole turn.</summary>
    [FieldOffset(124)] public float CloudSpinAngle;
    /// <summary>Baked: the color of the light the clouds are lit by: the shadow light's, or white when no light
    /// shadows.</summary>
    [FieldOffset(128)] public Vector3 CloudLightColor;
    /// <summary>The studio-reflection horizon's low (ground-ward) color.</summary>
    [FieldOffset(144)] public Vector3 HorizonLow;
    /// <summary>The studio-reflection horizon's high (sky-ward) color.</summary>
    [FieldOffset(160)] public Vector3 HorizonHigh;
    /// <summary>The fog's extinction per world unit at its base; zero is no fog.</summary>
    [FieldOffset(172)] public float FogExtinction;
    /// <summary>The colour the fog in-scatters when <see cref="AirFlags"/> holds <see cref="SdfAir.FogColorAuthored"/>.</summary>
    [FieldOffset(176)] public Vector3 FogColor;
    /// <summary>The fog's base height.</summary>
    [FieldOffset(188)] public float FogBase;
    /// <summary>The fog's falloff rise; zero is a level fog.</summary>
    [FieldOffset(192)] public float FogFalloff;
    /// <summary>Baked: the haze's extinction per world unit at its base, the extinction that takes the authored amount over
    /// the far distance; zero is no haze.</summary>
    [FieldOffset(196)] public float HazeExtinction;
    /// <summary>The haze's base height.</summary>
    [FieldOffset(200)] public float HazeBase;
    /// <summary>The haze's falloff rise; zero is a level haze.</summary>
    [FieldOffset(204)] public float HazeFalloff;
    /// <summary>The colour the medium in-scatters.</summary>
    [FieldOffset(208)] public Vector3 MediumColor;
    /// <summary>The medium's extinction per world unit; zero is no medium.</summary>
    [FieldOffset(220)] public float MediumExtinction;
    /// <summary>The height of the medium's surface.</summary>
    [FieldOffset(224)] public float MediumSurface;
    /// <summary>The haze's Henyey-Greenstein anisotropy.</summary>
    [FieldOffset(228)] public float HazeAnisotropy;
    /// <summary>Baked: the unit direction toward the first air light.</summary>
    [FieldOffset(240)] public Vector3 AirLight0Direction;
    /// <summary>Baked: the first air light's radiance, its colour times its weight.</summary>
    [FieldOffset(256)] public Vector3 AirLight0Radiance;
    /// <summary>Baked: the unit direction toward the second air light.</summary>
    [FieldOffset(272)] public Vector3 AirLight1Direction;
    /// <summary>Baked: the second air light's radiance.</summary>
    [FieldOffset(288)] public Vector3 AirLight1Radiance;
    /// <summary>Baked: the unit direction toward the third air light.</summary>
    [FieldOffset(304)] public Vector3 AirLight2Direction;
    /// <summary>Baked: the third air light's radiance.</summary>
    [FieldOffset(320)] public Vector3 AirLight2Radiance;
    /// <summary>Baked: the unit direction toward the fourth air light.</summary>
    [FieldOffset(336)] public Vector3 AirLight3Direction;
    /// <summary>Baked: the fourth air light's radiance.</summary>
    [FieldOffset(352)] public Vector3 AirLight3Radiance;
}
/// <summary>One stop of the sky's gradient, a record of the stops table the sky and composite passes read
/// (<c>sdfSkyStops</c>, <see cref="SdfSky.MaxStops"/> records), whose HLSL declaration is generated from this type.</summary>
/// <param name="Color">The stop's linear RGB color.</param>
/// <param name="Elevation">The stop's elevation, the direction's height in [−1, 1]; the stops ascend.</param>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public record struct SdfSkyStop(Vector3 Color, float Elevation) {
    /// <summary>The stop's linear RGB color.</summary>
    [FieldOffset(0)] public Vector3 Color = Color;
    /// <summary>The stop's elevation, the direction's height in [−1, 1]; the stops ascend.</summary>
    [FieldOffset(12)] public float Elevation = Elevation;
}
/// <summary>One analytic studio-reflection softbox (see <c>worldStudioReflection</c> in
/// <c>shade/sdf-lighting.hlsli</c>), a record of the softbox table the views pass reads (<c>sdfSoftboxes</c>,
/// <see cref="SdfSky.MaxSoftboxes"/> records), whose HLSL declaration is generated from this type.</summary>
/// <param name="Direction">The direction from a lit surface toward the softbox, any nonzero length (normalized on
/// upload).</param>
/// <param name="Color">The linear RGB color.</param>
/// <param name="Weight">The strength.</param>
/// <param name="Size">The angular half-extent proxy (width, height) the falloff widens by.</param>
/// <param name="Blur">The additional falloff softening, in the same units as <paramref name="Size"/>; 0 = none.</param>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public record struct SdfSoftbox(Vector3 Direction, Vector3 Color, float Weight, Vector2 Size, float Blur) {
    /// <summary>The direction from a lit surface toward the softbox.</summary>
    [FieldOffset(0)] public Vector3 Direction = Direction;
    /// <summary>The strength.</summary>
    [FieldOffset(12)] public float Weight = Weight;
    /// <summary>The linear RGB color.</summary>
    [FieldOffset(16)] public Vector3 Color = Color;
    /// <summary>The angular half-extent proxy (width, height) the falloff widens by.</summary>
    [FieldOffset(32)] public Vector2 Size = Size;
    /// <summary>The additional falloff softening; 0 = none.</summary>
    [FieldOffset(40)] public float Blur = Blur;
}
/// <summary>
/// A frame's sky: the sky block's authored values (<see cref="Block"/>), the gradient's stops, the studio reflection's
/// softboxes and the sun disc's angular radius, packed by <see cref="Pack"/> into the block and the two tables the
/// kernels read, with the host bakes the shader must not pay per pixel.
/// </summary>
public sealed class SdfSky {
    /// <summary>The default cloud cell scale.</summary>
    public const float DefaultCloudScale = 2f;
    /// <summary>The default cloud edge softness.</summary>
    public const float DefaultCloudSoftness = 0.25f;
    /// <summary>The narrowest cloud edge band, as a share of density: the least softness a cloud layer or a cloud
    /// volume admits. Both kernels read a band of this width beside a threshold in <c>[0, 1]</c> (a layer's
    /// <c>[t, t + s]</c>, a volume's <c>[t - s, t + s]</c>) through <c>smoothstep</c>, which is defined only while its
    /// two edges differ. An IEEE float addition is correctly rounded on both backends, and the spacing of floats in
    /// <c>[0, 1]</c> is at most <c>2^-23</c> (one ULP of 1.0), so a band of at least <c>2^-23</c> keeps its edges
    /// apart at every threshold; <c>1e-6</c> is about eight ULPs of 1.0, past what any rounding of the threshold or the
    /// edges can close, and it is a normal float no device flushes to zero.</summary>
    public const float MinCloudSoftness = 1e-6f;
    /// <summary>The default look's fog density, which a world authoring no atmosphere renders
    /// (<see cref="SdfAtmosphere.Default"/>).</summary>
    public const float DefaultFogDensity = 0.015f;
    /// <summary>The default star cell density.</summary>
    public const float DefaultStarDensity = 48f;
    /// <summary>The default sun-disc angular radius in radians.</summary>
    public const float DefaultSunDiscRadians = 0.05f;
    /// <summary>The default scintillation rate in hertz.</summary>
    public const float DefaultTwinkleRate = 1f;
    /// <summary>The most gradient stops a sky carries: the records of the stops table.</summary>
    public const int MaxStops = 4;
    /// <summary>The most studio-reflection softboxes a frame carries: the records of the softbox table.</summary>
    public const int MaxSoftboxes = 4;

    private readonly SdfSoftbox[] m_softboxes = new SdfSoftbox[MaxSoftboxes];
    private readonly SdfSkyStop[] m_stops = new SdfSkyStop[MaxStops];
    private SdfAtmosphere m_atmosphere = SdfAtmosphere.Default;

    private SdfSkyBlock m_block;

    /// <summary>Gets the zenith color of the default look's two-stop gradient, which an unauthored world renders.</summary>
    public static Vector3 DefaultZenithColor { get; } = new(
        x: 0.10f,
        y: 0.13f,
        z: 0.20f
    );
    /// <summary>Gets the ground color of the default look's two-stop gradient, which an unauthored world renders.</summary>
    public static Vector3 DefaultGroundColor { get; } = new(
        x: 0.04f,
        y: 0.05f,
        z: 0.07f
    );

    /// <summary>Initializes a new instance of the <see cref="SdfSky"/> class with the default look an unauthored world
    /// renders, as data the kernels read like any authored sky: the two-stop gradient from <see cref="DefaultGroundColor"/>
    /// below to <see cref="DefaultZenithColor"/> above, the default atmosphere's fog (<see cref="SdfAtmosphere.Default"/>),
    /// no sun disc, and every other layer at its defaults with no brightness or coverage, so it draws nothing.</summary>
    public SdfSky() {
        m_stops[0] = new SdfSkyStop(Color: DefaultGroundColor, Elevation: -1f);
        m_stops[1] = new SdfSkyStop(Color: DefaultZenithColor, Elevation: 1f);
        m_block = new SdfSkyBlock {
            CloudColor = Vector3.One,
            CloudScale = DefaultCloudScale,
            CloudSoftness = DefaultCloudSoftness,
            DiscLight = -1,
            StarDensity = DefaultStarDensity,
            StopCount = 2u,
        };
    }

    /// <summary>Gets the atmosphere's authored values, by reference, which <see cref="Pack"/> bakes into the block's
    /// atmosphere lanes.</summary>
    public ref SdfAtmosphere Atmosphere => ref m_atmosphere;
    /// <summary>Gets the block's authored values, by reference. Its baked fields are written by <see cref="Pack"/>
    /// alone, and its counts through <see cref="StopCount"/> and <see cref="SoftboxCount"/>.</summary>
    public ref SdfSkyBlock Block => ref m_block;
    /// <summary>Gets or sets the number of studio-reflection softboxes, clamped to [0, <see cref="MaxSoftboxes"/>].</summary>
    public int SoftboxCount {
        get => ((int)m_block.SoftboxCount);
        set => m_block.SoftboxCount = ((uint)Math.Clamp(
            max: MaxSoftboxes,
            min: 0,
            value: value
        ));
    }
    /// <summary>Gets every record of the softbox table, <see cref="MaxSoftboxes"/> of them.</summary>
    public ReadOnlySpan<SdfSoftbox> Softboxes => m_softboxes;
    /// <summary>Gets or sets the number of gradient stops, clamped to [0, <see cref="MaxStops"/>].</summary>
    public int StopCount {
        get => ((int)m_block.StopCount);
        set => m_block.StopCount = ((uint)Math.Clamp(
            max: MaxStops,
            min: 0,
            value: value
        ));
    }
    /// <summary>Gets every record of the stops table, <see cref="MaxStops"/> of them.</summary>
    public ReadOnlySpan<SdfSkyStop> Stops => m_stops;

    /// <summary>Gets or sets the sun disc's angular radius in radians, which <see cref="Pack"/> bakes into the block's
    /// exponent.</summary>
    public float SunDiscRadians { get; set; } = DefaultSunDiscRadians;

    /// <summary>Copies the block, every stop and softbox and the disc's radius from another sky.</summary>
    /// <param name="source">The sky to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void CopyFrom(SdfSky source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_block = source.m_block;
        m_atmosphere = source.m_atmosphere;
        source.m_stops.CopyTo(array: m_stops, index: 0);
        source.m_softboxes.CopyTo(array: m_softboxes, index: 0);
        SunDiscRadians = source.SunDiscRadians;
    }
    /// <summary>Packs the sky block and its two tables, with the host bakes: the disc's direction (its light's packed
    /// direction, <see cref="SdfLights.Pack"/>) and its exponent, the light the clouds are lit by, each softbox's
    /// direction normalized in double and rounded once (a zero one, an unauthored slot, left zero), and the atmosphere's
    /// lanes (<see cref="PackAtmosphere"/>). The twinkle's phase and the cloud drift, shear and spin arrive integrated to
    /// the frame's presented tick, so the block carries phases and offsets, never a rate.</summary>
    /// <param name="lights">The frame's lights, which the disc, the clouds and the air are lit by.</param>
    /// <param name="farDistance">The frame's far distance in world units, over which the haze takes its amount.</param>
    /// <param name="block">Receives the sky block.</param>
    /// <param name="stops">Receives the stops table, at least <see cref="MaxStops"/> records.</param>
    /// <param name="softboxes">Receives the softbox table, at least <see cref="MaxSoftboxes"/> records.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lights"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stops"/> or <paramref name="softboxes"/> is shorter than its
    /// table.</exception>
    public void Pack(SdfLights lights, float farDistance, out SdfSkyBlock block, Span<SdfSkyStop> stops, Span<SdfSoftbox> softboxes) {
        ArgumentNullException.ThrowIfNull(argument: lights);

        if (
            (stops.Length < MaxStops) ||
            (softboxes.Length < MaxSoftboxes)
        ) {
            throw new ArgumentException(message: $"The sky's tables hold {MaxStops} stops and {MaxSoftboxes} softboxes; the spans hold {stops.Length} and {softboxes.Length}.");
        }

        block = m_block;

        var cosDiscRadius = Math.Cos(d: SunDiscRadians);

        block.DiscExponent = ((float)((cosDiscRadius is > 0d and < 1d)
            ? Math.Clamp(
                value: (Math.Log(d: 0.5d) / Math.Log(d: cosDiscRadius)),
                min: 0d,
                max: 100000d
            )
            : 100000d));

        if (
            (block.DiscLight < 0) ||
            (block.DiscLight >= SdfLights.MaxLights)
        ) {
            block.DiscLight = -1;
            block.DiscDirection = Vector3.Zero;
        } else {
            var disc = lights[block.DiscLight];

            block.DiscDirection = ((disc.Kind == SdfLightKind.Directional)
                ? SdfLights.UnitDirection(direction: disc.Direction)
                : disc.Direction);
        }

        if (lights.ShadowSlots[0] >= 0) {
            var key = lights[lights.ShadowSlots[0]];

            block.CloudLightDirection = SdfLights.UnitDirection(direction: key.Direction);
            block.CloudLightColor = key.Color;
        } else {
            block.CloudLightDirection = SdfLights.DefaultSunDirection;
            block.CloudLightColor = Vector3.One;
        }

        PackAtmosphere(atmosphere: in m_atmosphere, block: ref block, farDistance: farDistance, lights: lights);
        m_stops.CopyTo(destination: stops);

        for (var index = 0; (index < MaxSoftboxes); index++) {
            var softbox = m_softboxes[index];
            double x = softbox.Direction.X, y = softbox.Direction.Y, z = softbox.Direction.Z;
            var length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

            if (length > 0d) {
                softbox.Direction = new Vector3(
                    x: ((float)(x / length)),
                    y: ((float)(y / length)),
                    z: ((float)(z / length))
                );
            }

            softboxes[index] = softbox;
        }
    }
    /// <summary>Writes an atmosphere's lanes into a sky block with their host bakes: the haze's extinction, which takes
    /// its amount along a level ray the far distance long (<c>−ln(1 − amount) / far</c>), and the air lights, the first
    /// <see cref="SdfAir.MaxLights"/> directional lights with any radiance in table order, each its unit direction and its
    /// colour times its weight. A directional light is what a light-casting body binds, so the haze and the bounded media
    /// scatter the bodies' light through it.</summary>
    /// <param name="atmosphere">The authored atmosphere.</param>
    /// <param name="lights">The frame's lights.</param>
    /// <param name="farDistance">The frame's far distance in world units.</param>
    /// <param name="block">The block whose atmosphere lanes are written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lights"/> is <see langword="null"/>.</exception>
    public static void PackAtmosphere(in SdfAtmosphere atmosphere, SdfLights lights, float farDistance, ref SdfSkyBlock block) {
        ArgumentNullException.ThrowIfNull(argument: lights);

        block.AirFlags = (atmosphere.FogColorAuthored ? SdfAir.FogColorAuthored : 0u);
        block.FogExtinction = MathF.Max(x: atmosphere.FogDensity, y: 0f);
        block.FogBase = atmosphere.FogBase;
        block.FogFalloff = MathF.Max(x: atmosphere.FogFalloff, y: 0f);
        block.FogColor = atmosphere.FogColor;
        block.HazeExtinction = (((atmosphere.HazeAmount > 0f) && (farDistance > 0f))
            ? ((float)(-Math.Log(d: (1d - Math.Min(val1: atmosphere.HazeAmount, val2: SdfAtmosphere.MaxHazeAmount))) / farDistance))
            : 0f);
        block.HazeBase = atmosphere.HazeBase;
        block.HazeFalloff = MathF.Max(x: atmosphere.HazeFalloff, y: 0f);
        block.HazeAnisotropy = Math.Clamp(max: SdfAtmosphere.MaxHazeAnisotropy, min: 0f, value: atmosphere.HazeAnisotropy);
        block.MediumExtinction = MathF.Max(x: atmosphere.MediumExtinction, y: 0f);
        block.MediumSurface = atmosphere.MediumSurface;
        block.MediumColor = atmosphere.MediumColor;
        block.AirLight0Direction = block.AirLight1Direction = block.AirLight2Direction = block.AirLight3Direction = Vector3.Zero;
        block.AirLight0Radiance = block.AirLight1Radiance = block.AirLight2Radiance = block.AirLight3Radiance = Vector3.Zero;

        var count = 0;

        for (var index = 0; ((index < lights.Count) && (count < SdfAir.MaxLights)); index++) {
            var light = lights[index];
            var radiance = (light.Color * light.Weight);

            if (
                (light.Kind != SdfLightKind.Directional) ||
                !((radiance.X > 0f) || (radiance.Y > 0f) || (radiance.Z > 0f))
            ) {
                continue;
            }

            var direction = SdfLights.UnitDirection(direction: light.Direction);

            switch (count) {
                case 0: block.AirLight0Direction = direction; block.AirLight0Radiance = radiance; break;
                case 1: block.AirLight1Direction = direction; block.AirLight1Radiance = radiance; break;
                case 2: block.AirLight2Direction = direction; block.AirLight2Radiance = radiance; break;
                default: block.AirLight3Direction = direction; block.AirLight3Radiance = radiance; break;
            }

            count++;
        }

        block.AirLightCount = ((uint)count);
    }
    /// <summary>Sets one gradient stop.</summary>
    /// <param name="index">The stop's index in the table.</param>
    /// <param name="stop">The stop.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the table.</exception>
    public void SetStop(int index, SdfSkyStop stop) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: MaxStops, value: index);

        m_stops[index] = stop;
    }
    /// <summary>Sets one studio-reflection softbox.</summary>
    /// <param name="index">The softbox's index in the table.</param>
    /// <param name="softbox">The softbox.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the table.</exception>
    public void SetSoftbox(int index, SdfSoftbox softbox) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: MaxSoftboxes, value: index);

        m_softboxes[index] = softbox;
    }
}
