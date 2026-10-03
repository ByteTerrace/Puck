using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

/// <summary>The <see cref="SdfSkyLayerKind.Gradient"/> kind's parameters: two to <see cref="SdfSky.MaxStops"/> stops,
/// piecewise-linear in the layer-frame direction's height and clamped to the end stops. Opaque: its alpha is one.</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public record struct SdfSkyGradient : ISdfSkyKind {
    /// <summary>The first stop's linear RGB colour.</summary>
    [FieldOffset(0)] public Vector3 Color0;
    /// <summary>The first stop's height in <c>[−1, 1]</c>; the stops ascend.</summary>
    [FieldOffset(12)] public float Elevation0;
    /// <summary>The second stop's linear RGB colour.</summary>
    [FieldOffset(16)] public Vector3 Color1;
    /// <summary>The second stop's height.</summary>
    [FieldOffset(28)] public float Elevation1;
    /// <summary>The third stop's linear RGB colour.</summary>
    [FieldOffset(32)] public Vector3 Color2;
    /// <summary>The third stop's height.</summary>
    [FieldOffset(44)] public float Elevation2;
    /// <summary>The fourth stop's linear RGB colour.</summary>
    [FieldOffset(48)] public Vector3 Color3;
    /// <summary>The fourth stop's height.</summary>
    [FieldOffset(60)] public float Elevation3;
    /// <summary>How many stops the gradient carries, two to <see cref="SdfSky.MaxStops"/>.</summary>
    [FieldOffset(64)] public uint Count;

    /// <inheritdoc/>
    public static SdfSkyLayerKind Kind => SdfSkyLayerKind.Gradient;
    /// <inheritdoc/>
    public static SdfSkyLayerClass Class => SdfSkyLayerClass.Field;
    /// <inheritdoc/>
    public static string Name => "gradient";

    /// <summary>Returns one stop.</summary>
    /// <param name="index">The stop's index, below <see cref="SdfSky.MaxStops"/>.</param>
    /// <returns>Its colour and height.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the stops.</exception>
    public readonly (Vector3 Color, float Elevation) Stop(int index) => index switch {
        0 => (Color0, Elevation0),
        1 => (Color1, Elevation1),
        2 => (Color2, Elevation2),
        3 => (Color3, Elevation3),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(index), actualValue: index, message: "A gradient carries at most four stops."),
    };
    /// <summary>Sets one stop.</summary>
    /// <param name="index">The stop's index, below <see cref="SdfSky.MaxStops"/>.</param>
    /// <param name="color">The stop's linear RGB colour.</param>
    /// <param name="elevation">The stop's height in <c>[−1, 1]</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the stops.</exception>
    public void SetStop(int index, Vector3 color, float elevation) {
        switch (index) {
            case 0: Color0 = color; Elevation0 = elevation; break;
            case 1: Color1 = color; Elevation1 = elevation; break;
            case 2: Color2 = color; Elevation2 = elevation; break;
            case 3: Color3 = color; Elevation3 = elevation; break;
            default: throw new ArgumentOutOfRangeException(paramName: nameof(index), actualValue: index, message: "A gradient carries at most four stops.");
        }
    }
}
/// <summary>The <see cref="SdfSkyLayerKind.Stars"/> kind's parameters: a per-cell hash over the octahedral sky projection
/// picks <see cref="Sparsity"/> of the cells to carry a star, each with its own luminosity, tint and, for a
/// <see cref="TwinkleShare"/> of them, a twinkle. Drawn only above the layer frame's horizon, and only once it has
/// brightness.</summary>
[StructLayout(LayoutKind.Explicit, Size = 40)]
public record struct SdfSkyStars : ISdfSkyKind {
    /// <summary>The default star cell density.</summary>
    public const float DefaultDensity = 48f;
    /// <summary>The default share of the cells that carry a star.</summary>
    public const float DefaultSparsity = 0.08f;
    /// <summary>The default star centre's least distance from its cell walls, in cells.</summary>
    public const float DefaultInset = 0.3f;
    /// <summary>The default star angular radius as a share of one cell's angular pitch, at peak luminosity.</summary>
    public const float DefaultRadiusFraction = 0.12f;
    /// <summary>The default faintest star's luminosity as a share of the peak.</summary>
    public const float DefaultLuminosityFloor = 0.125f;

    /// <summary>The star cell density: cells per octahedral axis.</summary>
    [FieldOffset(0)] public float Density;
    /// <summary>The peak star brightness.</summary>
    [FieldOffset(4)] public float Brightness;
    /// <summary>The star hash seed.</summary>
    [FieldOffset(8)] public uint Seed;
    /// <summary>The twinkling share of the stars.</summary>
    [FieldOffset(12)] public float TwinkleShare;
    /// <summary>How far a twinkling star dips.</summary>
    [FieldOffset(16)] public float TwinkleDepth;
    /// <summary>The twinkle's phase at the frame's presented tick, in cycles in <c>[0, 1)</c>: the scintillation rate
    /// integrated by the host, and zero while no star twinkles visibly.</summary>
    [FieldOffset(20)] public float TwinklePhase;
    /// <summary>The share of the cells that carry a star.</summary>
    [FieldOffset(24)] public float Sparsity;
    /// <summary>The star angular radius as a share of one cell's angular pitch, at peak luminosity.</summary>
    [FieldOffset(28)] public float RadiusFraction;
    /// <summary>The faintest star's luminosity as a share of the peak.</summary>
    [FieldOffset(32)] public float LuminosityFloor;
    /// <summary>A star centre's least distance from its cell walls, in cells, below one half.</summary>
    [FieldOffset(36)] public float Inset;

    /// <summary>Initializes a new instance of the <see cref="SdfSkyStars"/> struct with the default field, unlit.</summary>
    public SdfSkyStars() {
        Density = DefaultDensity;
        Sparsity = DefaultSparsity;
        Inset = DefaultInset;
        RadiusFraction = DefaultRadiusFraction;
        LuminosityFloor = DefaultLuminosityFloor;
    }

    /// <inheritdoc/>
    public static SdfSkyLayerKind Kind => SdfSkyLayerKind.Stars;
    /// <inheritdoc/>
    public static SdfSkyLayerClass Class => SdfSkyLayerClass.Point;
    /// <inheritdoc/>
    public static string Name => "stars";
}
/// <summary>The <see cref="SdfSkyLayerKind.Clouds"/> kind's parameters: a heightfield of cloud on a dome, a domain-warped
/// fractal sum of the periodic lattice noise thresholded by coverage, lit against the light it names. Its constants of
/// shape (the warp, the heightfield's rise, the self-shadow, the silver lining, the extinction, the dome and the
/// horizon fade) are parameters with the defaults the kind has always drawn with.</summary>
[StructLayout(LayoutKind.Explicit, Size = 112)]
public record struct SdfSkyClouds : ISdfSkyKind {
    /// <summary>The default cloud cell scale.</summary>
    public const float DefaultScale = 2f;
    /// <summary>The default cloud edge softness.</summary>
    public const float DefaultSoftness = 0.25f;
    /// <summary>The default octaves at <see cref="SdfSkyTier.High"/>.</summary>
    public const uint DefaultOctaves = 4u;

    /// <summary>The cloud colour.</summary>
    [FieldOffset(0)] public Vector3 Color;
    /// <summary>The cloud coverage in <c>[0, 1]</c>.</summary>
    [FieldOffset(12)] public float Coverage;
    /// <summary>The cloud edge softness in <c>(0, 1]</c>.</summary>
    [FieldOffset(16)] public float Softness;
    /// <summary>The cloud cell scale in layer units.</summary>
    [FieldOffset(20)] public float Scale;
    /// <summary>The cloud hash seed.</summary>
    [FieldOffset(24)] public uint Seed;
    /// <summary>The Coriolis curl in radians at 45° elevation.</summary>
    [FieldOffset(28)] public float Curl;
    /// <summary>The drift's offset at the frame's presented tick, in layer units, reduced by the noise's lattice
    /// period.</summary>
    [FieldOffset(32)] public Vector2 DriftOffset;
    /// <summary>The shaping field's offset relative to the clouds at the frame's presented tick, in layer units.</summary>
    [FieldOffset(40)] public Vector2 ShearOffset;
    /// <summary>Baked: the unit world direction toward the light the clouds are lit by: the first shadow slot's light, or
    /// the pinned sun's when no light shadows.</summary>
    [FieldOffset(48)] public Vector3 LightDirection;
    /// <summary>The layer's rotation about the zenith at the frame's presented tick, in radians, reduced by a turn.</summary>
    [FieldOffset(60)] public float SpinAngle;
    /// <summary>Baked: the colour of the light the clouds are lit by, or white when no light shadows.</summary>
    [FieldOffset(64)] public Vector3 LightColor;
    /// <summary>How far the first fractal sum bends the second's domain, in cells.</summary>
    [FieldOffset(76)] public float Warp;
    /// <summary>The heightfield's rise per unit thickness, in cells: the lit normal's steepness.</summary>
    [FieldOffset(80)] public float Height;
    /// <summary>Beer's-law extinction per unit thickness.</summary>
    [FieldOffset(84)] public float Extinction;
    /// <summary>How dark a point goes under a taller lightward neighbour.</summary>
    [FieldOffset(88)] public float SelfShadow;
    /// <summary>The light-through-a-thin-edge highlight's strength.</summary>
    [FieldOffset(92)] public float SilverLining;
    /// <summary>The dome centre's depth below the camera, in layer units (unit height overhead).</summary>
    [FieldOffset(96)] public float DomeRadius;
    /// <summary>The height below which the layer fades to nothing.</summary>
    [FieldOffset(100)] public float HorizonFade;
    /// <summary>The lighting taps' offset from the centre, in cells.</summary>
    [FieldOffset(104)] public float NormalTap;
    /// <summary>The fractal sum's octaves at <see cref="SdfSkyTier.High"/>, one to eight.</summary>
    [FieldOffset(108)] public uint Octaves;

    /// <summary>Initializes a new instance of the <see cref="SdfSkyClouds"/> struct with the default shape, uncovered.</summary>
    public SdfSkyClouds() {
        Color = Vector3.One;
        Softness = DefaultSoftness;
        Scale = DefaultScale;
        LightColor = Vector3.One;
        Warp = 0.6f;
        Height = 0.7f;
        Extinction = 3.5f;
        SelfShadow = 0.6f;
        SilverLining = 0.5f;
        DomeRadius = 6f;
        HorizonFade = 0.05f;
        NormalTap = 0.18f;
        Octaves = DefaultOctaves;
    }

    /// <inheritdoc/>
    public static SdfSkyLayerKind Kind => SdfSkyLayerKind.Clouds;
    /// <inheritdoc/>
    public static SdfSkyLayerClass Class => SdfSkyLayerClass.Field;
    /// <inheritdoc/>
    public static string Name => "clouds";
}
/// <summary>The <see cref="SdfSkyLayerKind.Aurora"/> kind's parameters: curtains of rays rising from a wavering base,
/// hashed along azimuth, fading upward from <see cref="Color"/> at the base to <see cref="TopColor"/>, moving with the
/// layer's clock.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public record struct SdfSkyAurora : ISdfSkyKind {
    /// <summary>The curtain's colour at its base.</summary>
    [FieldOffset(0)] public Vector3 Color;
    /// <summary>The curtain's peak brightness; zero draws nothing.</summary>
    [FieldOffset(12)] public float Intensity;
    /// <summary>The curtain's colour at its top.</summary>
    [FieldOffset(16)] public Vector3 TopColor;
    /// <summary>The curtain base's mean height, in <c>[−1, 1]</c>.</summary>
    [FieldOffset(28)] public float Base;
    /// <summary>The curtain's height above its base, in height units.</summary>
    [FieldOffset(32)] public float Height;
    /// <summary>How far the base wavers, in height units.</summary>
    [FieldOffset(36)] public float Fold;
    /// <summary>The rays per turn of azimuth.</summary>
    [FieldOffset(40)] public float Rays;
    /// <summary>The aurora's hash seed.</summary>
    [FieldOffset(44)] public uint Seed;
    /// <summary>The base's waves per turn of azimuth.</summary>
    [FieldOffset(48)] public float Waves;

    /// <summary>Initializes a new instance of the <see cref="SdfSkyAurora"/> struct with a green curtain, unlit.</summary>
    public SdfSkyAurora() {
        Color = new Vector3(x: 0.24f, y: 1f, z: 0.69f);
        TopColor = new Vector3(x: 0.45f, y: 0.2f, z: 0.9f);
        Base = 0.3f;
        Height = 0.35f;
        Fold = 0.08f;
        Rays = 96f;
        Waves = 5f;
    }

    /// <inheritdoc/>
    public static SdfSkyLayerKind Kind => SdfSkyLayerKind.Aurora;
    /// <inheritdoc/>
    public static SdfSkyLayerClass Class => SdfSkyLayerClass.Field;
    /// <inheritdoc/>
    public static string Name => "aurora";
}
/// <summary>The <see cref="SdfSkyLayerKind.Noise"/> kind's parameters: a fractal sum of the periodic 3D lattice noise
/// over the layer-frame direction, coloured from <see cref="ColorLow"/> to <see cref="ColorHigh"/> and covered by a
/// threshold, its lattice sliding one period a cycle of the layer's clock.</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public record struct SdfSkyNoise : ISdfSkyKind {
    /// <summary>The colour where the noise is lowest.</summary>
    [FieldOffset(0)] public Vector3 ColorLow;
    /// <summary>The lattice cells per unit of direction.</summary>
    [FieldOffset(12)] public float Scale;
    /// <summary>The colour where the noise is highest.</summary>
    [FieldOffset(16)] public Vector3 ColorHigh;
    /// <summary>The noise's hash seed.</summary>
    [FieldOffset(28)] public uint Seed;
    /// <summary>The octaves at <see cref="SdfSkyTier.High"/>, one to eight; two below it.</summary>
    [FieldOffset(32)] public uint Octaves;
    /// <summary>Each octave's amplitude relative to the one before it.</summary>
    [FieldOffset(36)] public float Gain;
    /// <summary>The share of the sky the noise covers, in <c>[0, 1]</c>; one covers it all.</summary>
    [FieldOffset(40)] public float Coverage;
    /// <summary>The covered edge's width as a share of the noise's range.</summary>
    [FieldOffset(44)] public float Softness;

    /// <summary>Initializes a new instance of the <see cref="SdfSkyNoise"/> struct with grey noise covering the sky.</summary>
    public SdfSkyNoise() {
        ColorHigh = Vector3.One;
        Scale = 4f;
        Octaves = 4u;
        Gain = 0.5f;
        Coverage = 1f;
        Softness = 0.25f;
    }

    /// <inheritdoc/>
    public static SdfSkyLayerKind Kind => SdfSkyLayerKind.Noise;
    /// <inheritdoc/>
    public static SdfSkyLayerClass Class => SdfSkyLayerClass.Field;
    /// <inheritdoc/>
    public static string Name => "noise";
}
/// <summary>The <see cref="SdfSkyLayerKind.Pattern"/> kind's parameters: a painted pattern over the layer frame's azimuth
/// and height, its edges softened over <see cref="Softness"/> of a cell so it stays band-limited, scrolling one cell a
/// cycle of the layer's clock.</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public record struct SdfSkyPattern : ISdfSkyKind {
    /// <summary>The first colour.</summary>
    [FieldOffset(0)] public Vector3 ColorA;
    /// <summary>The shape the pattern paints.</summary>
    [FieldOffset(12)] public SdfSkyPatternShape Shape;
    /// <summary>The second colour.</summary>
    [FieldOffset(16)] public Vector3 ColorB;
    /// <summary>The cells per turn of azimuth; a cell is as tall as it is wide at the horizon.</summary>
    [FieldOffset(28)] public float Cells;
    /// <summary>A stripe's or grid line's width as a share of a cell.</summary>
    [FieldOffset(32)] public float Line;
    /// <summary>The edge's width as a share of a cell.</summary>
    [FieldOffset(36)] public float Softness;

    /// <summary>Initializes a new instance of the <see cref="SdfSkyPattern"/> struct with a black and white checker.</summary>
    public SdfSkyPattern() {
        ColorB = Vector3.One;
        Cells = 24f;
        Line = 0.1f;
        Softness = 0.05f;
    }

    /// <inheritdoc/>
    public static SdfSkyLayerKind Kind => SdfSkyLayerKind.Pattern;
    /// <inheritdoc/>
    public static SdfSkyLayerClass Class => SdfSkyLayerClass.Field;
    /// <inheritdoc/>
    public static string Name => "pattern";
}
/// <summary>The <see cref="SdfSkyLayerKind.Panorama"/> kind's parameters: the image a diegetic screen shows
/// (its source, through the screens the residency binds), sampled by the layer-frame direction. A screen that shows
/// nothing draws nothing.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public record struct SdfSkyPanorama : ISdfSkyKind {
    /// <summary>The program-declared screen surface whose image the layer samples.</summary>
    [FieldOffset(0)] public int Screen;
    /// <summary>How a direction maps to the image.</summary>
    [FieldOffset(4)] public SdfSkyProjection Projection;
    /// <summary>The image's brightness scale.</summary>
    [FieldOffset(8)] public float Intensity;

    /// <summary>Initializes a new instance of the <see cref="SdfSkyPanorama"/> struct showing nothing.</summary>
    public SdfSkyPanorama() {
        Screen = -1;
        Intensity = 1f;
    }

    /// <inheritdoc/>
    public static SdfSkyLayerKind Kind => SdfSkyLayerKind.Panorama;
    /// <inheritdoc/>
    public static SdfSkyLayerClass Class => SdfSkyLayerClass.Field;
    /// <inheritdoc/>
    public static string Name => "panorama";
}
/// <summary>The <see cref="SdfSkyLayerKind.Disc"/> kind's parameters: a disc about a light's world direction, either a
/// <c>pow()</c> glow whose edge, at the authored angular radius, is half bright, or, with a <see cref="Screen"/>, the
/// image that screen shows drawn across the disc (the <c>texture</c> body shape). The disc ignores the sky frame and its
/// layer's rotation: it sits where its light shines from.</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public record struct SdfSkyDisc : ISdfSkyKind {
    /// <summary>The default angular radius, in radians.</summary>
    public const float DefaultRadius = 0.05f;

    /// <summary>Baked: the unit world direction the disc is drawn about, its light's packed direction.</summary>
    [FieldOffset(0)] public Vector3 Direction;
    /// <summary>The disc's peak brightness; zero draws nothing.</summary>
    [FieldOffset(12)] public float Intensity;
    /// <summary>The disc's tint.</summary>
    [FieldOffset(16)] public Vector3 Color;
    /// <summary>Baked: the glow's <c>pow()</c> exponent, k = ln 0.5 / ln cos r.</summary>
    [FieldOffset(28)] public float Exponent;
    /// <summary>The disc's angular radius, in radians.</summary>
    [FieldOffset(32)] public float Radius;
    /// <summary>The light the disc is drawn about, or −1 for none, which writes no entry.</summary>
    [FieldOffset(36)] public int Light;
    /// <summary>The program-declared screen surface whose image the disc shows, or −1 for the glow.</summary>
    [FieldOffset(40)] public int Screen;

    /// <summary>Initializes a new instance of the <see cref="SdfSkyDisc"/> struct about no light.</summary>
    public SdfSkyDisc() {
        Color = Vector3.One;
        Radius = DefaultRadius;
        Light = -1;
        Screen = -1;
    }

    /// <inheritdoc/>
    public static SdfSkyLayerKind Kind => SdfSkyLayerKind.Disc;
    /// <inheritdoc/>
    public static SdfSkyLayerClass Class => SdfSkyLayerClass.Point;
    /// <inheritdoc/>
    public static string Name => "disc";
}
/// <summary>The <see cref="SdfSkyLayerKind.View"/> kind's parameters: an infinity view's image, the second
/// <c>sdf.world</c> instance (<c>sky$&lt;layer&gt;</c>) that renders another world, or only named prototypes of this one
/// (far geometry), from a fixed anchor, turned with the viewer and never translated. The instance renders exactly the
/// rectangle of the viewer's camera plane its layer's mask covers (<see cref="Rect"/>, in tangent space), so a pixel
/// samples it at the tangent its world direction has on the viewer's basis (<see cref="Right"/>, <see cref="Up"/>,
/// <see cref="Forward"/>), with no sky frame in between. The screen is the one the binder routes the instance's image
/// to (<see cref="Screen"/>); before the instance has an image, or where it renders nothing, the layer draws
/// <see cref="Fallback"/>. Either way the pixel counts a shown texel, which is what demands the instance's next frame.
/// Fitted to the residency's first view.</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public record struct SdfSkyView : ISdfSkyKind {
    /// <summary>The viewer's camera right axis the instance was fitted to, in world space.</summary>
    [FieldOffset(0)] public Vector3 Right;
    /// <summary>The image's brightness scale.</summary>
    [FieldOffset(12)] public float Intensity;
    /// <summary>The viewer's camera up axis.</summary>
    [FieldOffset(16)] public Vector3 Up;
    /// <summary>The binder-routed screen index whose image is the instance's, or −1 for none: the layer then draws
    /// <see cref="Fallback"/>.</summary>
    [FieldOffset(28)] public int Screen;
    /// <summary>The viewer's camera forward axis.</summary>
    [FieldOffset(32)] public Vector3 Forward;
    /// <summary>One when the image's alpha is its coverage (far geometry, which leaves the rest of the sky showing), zero
    /// when the image is opaque (another world's whole frame).</summary>
    [FieldOffset(44)] public uint Coverage;
    /// <summary>The rectangle the instance renders on the viewer's camera plane, <c>(minX, minY, maxX, maxY)</c> in
    /// tangent space (<c>InfinityViewFrame.Rect</c>).</summary>
    [FieldOffset(48)] public Vector4 Rect;
    /// <summary>The linear colour drawn where the instance has no image.</summary>
    [FieldOffset(64)] public Vector3 Fallback;

    /// <summary>Initializes a new instance of the <see cref="SdfSkyView"/> struct showing nothing: no screen, the camera's
    /// own axes, an empty rectangle.</summary>
    public SdfSkyView() {
        Right = Vector3.UnitX;
        Up = Vector3.UnitY;
        Forward = -Vector3.UnitZ;
        Intensity = 1f;
        Screen = -1;
    }

    /// <inheritdoc/>
    public static SdfSkyLayerKind Kind => SdfSkyLayerKind.View;
    /// <inheritdoc/>
    public static SdfSkyLayerClass Class => SdfSkyLayerClass.Point;
    /// <inheritdoc/>
    public static string Name => "view";
}
