using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

/// <summary>
/// The sky block the sky, composite and views passes read (<c>sdfSky</c>, a World-group structured buffer of one
/// record): the sky's fog, the sun disc, the stars and their twinkle, the cloud layer and the studio reflection's
/// horizon. Its public fields are the record's layout, whose HLSL declaration <c>puck shaders generate</c> writes from
/// this type. <see cref="SdfSky"/> holds the authored values and packs this record with its host bakes
/// (<see cref="SdfSky.Pack"/>): the fields documented as baked are written there and nowhere else.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 176)]
public record struct SdfSkyBlock {
    /// <summary>The exponential distance-fog density.</summary>
    [FieldOffset(0)] public float FogDensity;
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
    /// <summary>The default look's fog density.</summary>
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
    /// below to <see cref="DefaultZenithColor"/> above, the default fog, no sun disc, and every other layer at its defaults
    /// with no brightness or coverage, so it draws nothing.</summary>
    public SdfSky() {
        m_stops[0] = new SdfSkyStop(Color: DefaultGroundColor, Elevation: -1f);
        m_stops[1] = new SdfSkyStop(Color: DefaultZenithColor, Elevation: 1f);
        m_block = new SdfSkyBlock {
            CloudColor = Vector3.One,
            CloudScale = DefaultCloudScale,
            CloudSoftness = DefaultCloudSoftness,
            DiscLight = -1,
            FogDensity = DefaultFogDensity,
            StarDensity = DefaultStarDensity,
            StopCount = 2u,
        };
    }

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
        source.m_stops.CopyTo(array: m_stops, index: 0);
        source.m_softboxes.CopyTo(array: m_softboxes, index: 0);
        SunDiscRadians = source.SunDiscRadians;
    }
    /// <summary>Packs the sky block and its two tables, with the host bakes: the disc's direction (its light's packed
    /// direction, <see cref="SdfLights.Pack"/>) and its exponent, the light the clouds are lit by, and each softbox's
    /// direction normalized in double and rounded once (a zero one, an unauthored slot, left zero). The twinkle's phase
    /// and the cloud drift, shear and spin arrive integrated to the frame's presented tick, so the block carries phases
    /// and offsets, never a rate.</summary>
    /// <param name="lights">The frame's lights, which the disc and the clouds are lit by.</param>
    /// <param name="block">Receives the sky block.</param>
    /// <param name="stops">Receives the stops table, at least <see cref="MaxStops"/> records.</param>
    /// <param name="softboxes">Receives the softbox table, at least <see cref="MaxSoftboxes"/> records.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lights"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stops"/> or <paramref name="softboxes"/> is shorter than its
    /// table.</exception>
    public void Pack(SdfLights lights, out SdfSkyBlock block, Span<SdfSkyStop> stops, Span<SdfSoftbox> softboxes) {
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
