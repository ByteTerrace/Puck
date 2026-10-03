using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

/// <summary>
/// The sky block the sky, composite, views and environment passes read (<c>sdfSky</c>, a World-group structured buffer of
/// one record): the fog, the sky frame, the layer table's count and run structure, the sky's quality tier and the studio
/// reflection's horizon. Its public fields are the record's layout, whose HLSL declaration <c>puck shaders generate</c>
/// writes from this type. <see cref="SdfSky"/> holds the authored values and packs this record (<see cref="SdfSky.Pack"/>):
/// the fields documented as baked are written there and nowhere else.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 96)]
public record struct SdfSkyBlock {
    /// <summary>The exponential distance-fog density.</summary>
    [FieldOffset(0)] public float FogDensity;
    /// <summary>Baked: the layers the layer table holds, at most <see cref="SdfSky.MaxLayers"/>.</summary>
    [FieldOffset(4)] public uint LayerCount;
    /// <summary>The studio-reflection softboxes the softbox table holds, at most <see cref="SdfSky.MaxSoftboxes"/>.</summary>
    [FieldOffset(8)] public uint SoftboxCount;
    /// <summary>Baked: the sky's quality tier (<see cref="SdfSkyTier"/>), below <see cref="SdfSkyTier.High"/> of which each
    /// kind takes its reduced form.</summary>
    [FieldOffset(12)] public SdfSkyTier Quality;
    /// <summary>Baked: the sky frame's right axis in world space, the first row of the rotation taking a world direction
    /// into the sky frame.</summary>
    [FieldOffset(16)] public Vector3 FrameRight;
    /// <summary>Baked: one when the stack's lowest run is a field run, which the sky pass writes as the base image, else
    /// zero.</summary>
    [FieldOffset(28)] public uint BaseRun;
    /// <summary>Baked: the sky frame's up axis in world space.</summary>
    [FieldOffset(32)] public Vector3 FrameUp;
    /// <summary>Baked: the field runs above the stack's lowest run, at most <see cref="SdfSky.MaxUpperFieldRuns"/>.</summary>
    [FieldOffset(44)] public uint UpperRuns;
    /// <summary>Baked: the sky frame's forward axis in world space.</summary>
    [FieldOffset(48)] public Vector3 FrameForward;
    /// <summary>The studio-reflection horizon's low (ground-ward) color.</summary>
    [FieldOffset(64)] public Vector3 HorizonLow;
    /// <summary>The studio-reflection horizon's high (sky-ward) color.</summary>
    [FieldOffset(80)] public Vector3 HorizonHigh;
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
/// A frame's sky: the block's authored values (<see cref="Block"/>), the sky frame, the quality tier, an ordered stack
/// of up to <see cref="MaxLayers"/> layers of any kinds, each kind as often as authored, and the studio reflection's
/// softboxes, packed by <see cref="Pack"/> into the block and the two tables the kernels read, with the host bakes the
/// shader must not pay per pixel.
/// </summary>
public sealed class SdfSky {
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
    /// <summary>The default scintillation rate in hertz.</summary>
    public const float DefaultTwinkleRate = 1f;
    /// <summary>The most gradient stops a gradient layer carries.</summary>
    public const int MaxStops = 4;
    /// <summary>The most layers a sky carries: the records of the layer table.</summary>
    public const int MaxLayers = 8;
    /// <summary>The most infinity views (<see cref="SdfSkyLayerKind.View"/> layers, view and far alike) a world carries at once,
    /// at any nesting depth: each is a second residency whose tables take aperture bytes the smallest supported GPU's
    /// host-visible heap cannot spare.</summary>
    public const int MaxInfinityViews = 8;
    /// <summary>The most field runs above a stack's lowest run: the runs the sky pass's upper images hold.</summary>
    public const int MaxUpperFieldRuns = 2;
    /// <summary>The most studio-reflection softboxes a frame carries: the records of the softbox table.</summary>
    public const int MaxSoftboxes = 4;
    /// <summary>The default look's one layer's detail label.</summary>
    public const string DefaultGradientLabel = "gradient";

    private readonly SdfSkyLayer[] m_layers = new SdfSkyLayer[MaxLayers];
    private readonly SdfSkyLayerClass[] m_classes = new SdfSkyLayerClass[MaxLayers];
    private readonly SdfSkyTier[] m_tiers = new SdfSkyTier[MaxLayers];
    private readonly string[] m_labels = new string[MaxLayers];
    private readonly SdfSoftbox[] m_softboxes = new SdfSoftbox[MaxSoftboxes];

    private SdfSkyBlock m_block;
    private int m_layerCount;

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

    /// <summary>Gets the default look's gradient: <see cref="DefaultGroundColor"/> below to <see cref="DefaultZenithColor"/>
    /// above.</summary>
    public static SdfSkyGradient DefaultGradient {
        get {
            var gradient = new SdfSkyGradient { Count = 2u };

            gradient.SetStop(color: DefaultGroundColor, elevation: -1f, index: 0);
            gradient.SetStop(color: DefaultZenithColor, elevation: 1f, index: 1);

            return gradient;
        }
    }

    /// <summary>Initializes a new instance of the <see cref="SdfSky"/> class with the default look an unauthored world
    /// renders, as data the kernels read like any authored sky: one gradient layer, seen by the camera and the lighting,
    /// from <see cref="DefaultGroundColor"/> below to <see cref="DefaultZenithColor"/> above, and the default fog.</summary>
    public SdfSky() {
        m_block = new SdfSkyBlock { FogDensity = DefaultFogDensity };
        _ = Add(
            label: DefaultGradientLabel,
            parameters: DefaultGradient,
            visibility: SdfSkyVisibility.Both
        );
    }

    /// <summary>Gets the block's authored values, by reference: the fog density and the horizon. Its baked fields are
    /// written by <see cref="Pack"/> alone, and its softbox count through <see cref="SoftboxCount"/>.</summary>
    public ref SdfSkyBlock Block => ref m_block;
    /// <summary>Gets the layers the stack carries, in authored order.</summary>
    public int LayerCount => m_layerCount;
    /// <summary>Gets the stack's layers, in authored order, each with its authored header and parameters (its detail row
    /// and its kind's bakes are written by <see cref="Pack"/>).</summary>
    public ReadOnlySpan<SdfSkyLayer> Layers => m_layers.AsSpan(length: m_layerCount, start: 0);

    /// <summary>Gets or sets the sky's quality tier: a layer below it writes no entry, and below
    /// <see cref="SdfSkyTier.High"/> each kind takes its reduced form.</summary>
    public SdfSkyTier Quality { get; set; } = SdfSkyTier.High;
    /// <summary>Gets or sets the sky frame's up direction in world space, any nonzero length; the sky's elevation, the
    /// layers' masks and transforms are measured from it. The default is world +y.</summary>
    public Vector3 FrameUp { get; set; } = Vector3.UnitY;

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

    /// <summary>Appends a layer to the stack.</summary>
    /// <typeparam name="T">The layer's kind parameters.</typeparam>
    /// <param name="parameters">The kind's parameters.</param>
    /// <param name="label">The label its evaluations count under (<see cref="SdfSkyDetails"/>): its name, or its kind's.</param>
    /// <param name="blend">How the layer composes over the colour beneath it.</param>
    /// <param name="opacity">The layer's opacity; a layer at zero or less writes no entry.</param>
    /// <param name="visibility">Who sees the layer.</param>
    /// <param name="tier">The lowest quality tier the layer draws at.</param>
    /// <returns>The layer's index in the stack.</returns>
    /// <exception cref="ArgumentException"><paramref name="label"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The stack holds <see cref="MaxLayers"/> layers.</exception>
    public int Add<T>(in T parameters, string label, SdfSkyBlend blend = SdfSkyBlend.Over, float opacity = 1f, SdfSkyVisibility visibility = SdfSkyVisibility.Camera, SdfSkyTier tier = SdfSkyTier.Low) where T : unmanaged, ISdfSkyKind {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: label);

        if (m_layerCount >= MaxLayers) {
            throw new InvalidOperationException(message: $"A sky carries at most {MaxLayers} layers.");
        }

        if (Unsafe.SizeOf<T>() > SdfSkyLayer.PayloadBytes) {
            throw new InvalidOperationException(message: $"The {T.Name} kind's parameters take {Unsafe.SizeOf<T>()} bytes; a layer's payload holds {SdfSkyLayer.PayloadBytes}.");
        }

        var index = m_layerCount++;

        m_layers[index] = new SdfSkyLayer {
            Blend = blend,
            Kind = T.Kind,
            Opacity = opacity,
            Rotation = SdfSkyLayer.IdentityRotation,
            Visibility = visibility,
        };
        m_classes[index] = T.Class;
        m_tiers[index] = tier;
        m_labels[index] = label;
        Parameters<T>(index: index) = parameters;

        return index;
    }
    /// <summary>Returns the stack's first layer of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The layer's index, or −1 when the stack carries none.</returns>
    public int IndexOf(SdfSkyLayerKind kind) {
        for (var index = 0; (index < m_layerCount); index++) {
            if (m_layers[index].Kind == kind) {
                return index;
            }
        }

        return -1;
    }
    /// <summary>Returns the stack's first layer of a kind's parameters, by reference.</summary>
    /// <typeparam name="T">The kind's parameters.</typeparam>
    /// <returns>The parameters.</returns>
    /// <exception cref="InvalidOperationException">The stack carries no layer of the kind.</exception>
    public ref T First<T>() where T : unmanaged, ISdfSkyKind {
        var index = IndexOf(kind: T.Kind);

        if (index < 0) {
            throw new InvalidOperationException(message: $"The sky carries no {T.Name} layer.");
        }

        return ref Parameters<T>(index: index);
    }
    /// <summary>Returns a layer's class.</summary>
    /// <param name="index">The layer's index.</param>
    /// <returns>Its kind's class.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the stack.</exception>
    public SdfSkyLayerClass ClassAt(int index) => m_classes[Checked(index: index)];
    /// <summary>Returns a layer's label.</summary>
    /// <param name="index">The layer's index.</param>
    /// <returns>The label its evaluations count under.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the stack.</exception>
    public string LabelAt(int index) => m_labels[Checked(index: index)];
    /// <summary>Returns the lowest quality tier a layer draws at.</summary>
    /// <param name="index">The layer's index.</param>
    /// <returns>The tier.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the stack.</exception>
    public SdfSkyTier TierAt(int index) => m_tiers[Checked(index: index)];
    /// <summary>Returns a layer's record, by reference: its header and its kind's parameters as the payload.</summary>
    /// <param name="index">The layer's index.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the stack.</exception>
    public ref SdfSkyLayer LayerAt(int index) => ref m_layers[Checked(index: index)];
    /// <summary>Returns a layer's kind parameters, by reference.</summary>
    /// <typeparam name="T">The layer's kind parameters.</typeparam>
    /// <param name="index">The layer's index.</param>
    /// <returns>The parameters.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the stack.</exception>
    /// <exception cref="InvalidOperationException">The layer is of another kind.</exception>
    public ref T Parameters<T>(int index) where T : unmanaged, ISdfSkyKind {
        ref var layer = ref m_layers[Checked(index: index)];

        if (layer.Kind != T.Kind) {
            throw new InvalidOperationException(message: $"Layer {index} is a {layer.Kind} layer, not a {T.Kind} layer.");
        }

        return ref PayloadOf<T>(layer: ref layer);
    }
    /// <summary>Returns a record's payload read as a kind's parameters, by reference.</summary>
    /// <typeparam name="T">The kind parameters.</typeparam>
    /// <param name="layer">The record.</param>
    /// <returns>The parameters.</returns>
    public static ref T PayloadOf<T>(ref SdfSkyLayer layer) where T : unmanaged, ISdfSkyKind =>
        ref MemoryMarshal.AsRef<T>(span: MemoryMarshal.AsBytes(span: MemoryMarshal.CreateSpan(length: 1, reference: ref layer)).Slice(length: SdfSkyLayer.PayloadBytes, start: SdfSkyLayer.PayloadOffset));
    /// <summary>Removes every layer, leaving an empty stack, which draws black.</summary>
    public void ClearLayers() {
        Array.Clear(array: m_layers);
        Array.Clear(array: m_labels);
        m_layerCount = 0;
    }
    /// <summary>Copies the block, every layer, softbox, the frame and the quality tier from another sky.</summary>
    /// <param name="source">The sky to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void CopyFrom(SdfSky source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_block = source.m_block;
        source.m_layers.CopyTo(array: m_layers, index: 0);
        source.m_classes.CopyTo(array: m_classes, index: 0);
        source.m_tiers.CopyTo(array: m_tiers, index: 0);
        source.m_labels.CopyTo(array: m_labels, index: 0);
        source.m_softboxes.CopyTo(array: m_softboxes, index: 0);
        m_layerCount = source.m_layerCount;
        Quality = source.Quality;
        FrameUp = source.FrameUp;
    }
    /// <summary>Packs the sky block and its two tables, with the host bakes: the sky frame's axes, each layer's detail row
    /// and unit rotation, a cone mask's unit axis, a disc's direction (its light's packed direction,
    /// <see cref="SdfLights.Pack"/>) and exponent, the light a cloud layer is lit by, the stack's run structure, and each
    /// softbox's direction normalized in double and rounded once (a zero one, an unauthored slot, left zero). A layer
    /// below the quality tier, at zero opacity, a disc about no light, or one the camera sees that would open a field run
    /// past <see cref="MaxUpperFieldRuns"/> writes no entry (the run structure counts the layers the camera sees alone),
    /// and the table's unused records are zero. The rates arrive integrated to the frame's presented tick, so the records
    /// carry phases and offsets, never a rate.</summary>
    /// <param name="lights">The frame's lights, which discs and clouds are lit by.</param>
    /// <param name="details">The detail rows each layer's label counts in.</param>
    /// <param name="block">Receives the sky block.</param>
    /// <param name="layers">Receives the layer table, at least <see cref="MaxLayers"/> records.</param>
    /// <param name="softboxes">Receives the softbox table, at least <see cref="MaxSoftboxes"/> records.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lights"/> or <paramref name="details"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="layers"/> or <paramref name="softboxes"/> is shorter than its
    /// table.</exception>
    public void Pack(SdfLights lights, SdfSkyDetails details, out SdfSkyBlock block, Span<SdfSkyLayer> layers, Span<SdfSoftbox> softboxes) {
        ArgumentNullException.ThrowIfNull(argument: lights);
        ArgumentNullException.ThrowIfNull(argument: details);

        if (
            (layers.Length < MaxLayers) ||
            (softboxes.Length < MaxSoftboxes)
        ) {
            throw new ArgumentException(message: $"The sky's tables hold {MaxLayers} layers and {MaxSoftboxes} softboxes; the spans hold {layers.Length} and {softboxes.Length}.");
        }

        block = m_block;
        block.Quality = Quality;
        (block.FrameRight, block.FrameUp, block.FrameForward) = FrameOf(up: FrameUp);

        var count = 0;
        var runs = new SdfSkyRunCount();

        for (var index = 0; (index < m_layerCount); index++) {
            var record = m_layers[index];

            if (
                (m_tiers[index] > Quality) ||
                !(record.Opacity > 0f) ||
                !Bake(layer: ref record, lights: lights) ||
                (((record.Visibility & SdfSkyVisibility.Camera) != 0) && !runs.TryAdd(layerClass: m_classes[index]))
            ) {
                continue;
            }

            record.Detail = details.RowOf(label: m_labels[index]);
            record.Opacity = Math.Min(val1: record.Opacity, val2: 1f);
            record.Rotation = UnitQuaternion(quaternion: record.Rotation);

            if (record.Mask == SdfSkyMask.Cone) {
                var axis = UnitOr(fallback: Vector3.UnitY, vector: new Vector3(x: record.MaskBand.X, y: record.MaskBand.Y, z: record.MaskBand.Z));

                record.MaskBand = new Vector4(value: axis, w: record.MaskBand.W);
            }

            layers[count++] = record;
        }

        layers[count..].Clear();
        block.LayerCount = ((uint)count);
        block.BaseRun = (runs.BaseRun ? 1u : 0u);
        block.UpperRuns = ((uint)runs.UpperRuns);

        for (var index = 0; (index < MaxSoftboxes); index++) {
            var softbox = m_softboxes[index];

            if (softbox.Direction != Vector3.Zero) {
                softbox.Direction = UnitOr(fallback: Vector3.Zero, vector: softbox.Direction);
            }

            softboxes[index] = softbox;
        }
    }
    /// <summary>Returns the sky frame's axes for an up direction: up normalized, and right and forward completing a
    /// right-handed basis that is the identity for world +y. Computed in double and rounded once.</summary>
    /// <param name="up">The up direction, any nonzero length; zero is world +y.</param>
    /// <returns>The frame's right, up and forward axes in world space.</returns>
    public static (Vector3 Right, Vector3 Up, Vector3 Forward) FrameOf(Vector3 up) {
        double ux = up.X, uy = up.Y, uz = up.Z;
        var length = Math.Sqrt(d: (((ux * ux) + (uy * uy)) + (uz * uz)));

        if (!(length > 0d) || !double.IsFinite(d: length)) {
            (ux, uy, uz, length) = (0d, 1d, 0d, 1d);
        }

        ux /= length; uy /= length; uz /= length;

        // Right is up × the reference forward (+z, or −y when up lies along z), forward is right × up.
        var (fx, fy, fz) = ((Math.Abs(value: uz) > 0.999d) ? (0d, -1d, 0d) : (0d, 0d, 1d));
        var rx = ((uy * fz) - (uz * fy));
        var ry = ((uz * fx) - (ux * fz));
        var rz = ((ux * fy) - (uy * fx));
        var rightLength = Math.Sqrt(d: (((rx * rx) + (ry * ry)) + (rz * rz)));

        rx /= rightLength; ry /= rightLength; rz /= rightLength;

        var forwardX = ((ry * uz) - (rz * uy));
        var forwardY = ((rz * ux) - (rx * uz));
        var forwardZ = ((rx * uy) - (ry * ux));

        return (
            new Vector3(x: ((float)rx), y: ((float)ry), z: ((float)rz)),
            new Vector3(x: ((float)ux), y: ((float)uy), z: ((float)uz)),
            new Vector3(x: ((float)forwardX), y: ((float)forwardY), z: ((float)forwardZ))
        );
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

    // Writes a layer's kind bakes, and whether it writes an entry at all: a gradient's unused stops are zero, and one with
    // no stop draws nothing; a disc is drawn about its light's direction with the exponent its radius gives, and never about
    // no light; clouds are lit by the first shadow slot's light, or the pinned sun and white when no light shadows.
    private static bool Bake(ref SdfSkyLayer layer, SdfLights lights) {
        switch (layer.Kind) {
            case SdfSkyLayerKind.Disc: {
                    ref var disc = ref PayloadOf<SdfSkyDisc>(layer: ref layer);

                    if ((disc.Light < 0) || (disc.Light >= SdfLights.MaxLights)) {
                        return false;
                    }

                    var light = lights[disc.Light];
                    var cosRadius = Math.Cos(d: disc.Radius);

                    disc.Direction = ((light.Kind == SdfLightKind.Directional)
                        ? SdfLights.UnitDirection(direction: light.Direction)
                        : light.Direction);
                    disc.Exponent = ((float)((cosRadius is > 0d and < 1d)
                        ? Math.Clamp(
                            max: 100000d,
                            min: 0d,
                            value: (Math.Log(d: 0.5d) / Math.Log(d: cosRadius))
                        )
                        : 100000d));

                    return true;
                }
            case SdfSkyLayerKind.Gradient: {
                    // Stops past the ones in use draw nothing, so they pack as zero and a record differs only where its
                    // gradient does.
                    ref var gradient = ref PayloadOf<SdfSkyGradient>(layer: ref layer);

                    for (var stop = ((int)Math.Min(val1: gradient.Count, val2: MaxStops)); (stop < MaxStops); stop++) {
                        gradient.SetStop(color: Vector3.Zero, elevation: 0f, index: stop);
                    }

                    return (gradient.Count >= 1u);
                }
            case SdfSkyLayerKind.Clouds: {
                    ref var clouds = ref PayloadOf<SdfSkyClouds>(layer: ref layer);

                    if (lights.ShadowSlots[0] >= 0) {
                        var key = lights[lights.ShadowSlots[0]];

                        clouds.LightDirection = SdfLights.UnitDirection(direction: key.Direction);
                        clouds.LightColor = key.Color;
                    } else {
                        clouds.LightDirection = SdfLights.DefaultSunDirection;
                        clouds.LightColor = Vector3.One;
                    }

                    return true;
                }
            default:
                return true;
        }
    }
    private int Checked(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: m_layerCount, value: index);

        return index;
    }
    private static Vector4 UnitQuaternion(Vector4 quaternion) {
        double x = quaternion.X, y = quaternion.Y, z = quaternion.Z, w = quaternion.W;
        var length = Math.Sqrt(d: ((((x * x) + (y * y)) + (z * z)) + (w * w)));

        return (((length > 0d) && double.IsFinite(d: length))
            ? new Vector4(w: ((float)(w / length)), x: ((float)(x / length)), y: ((float)(y / length)), z: ((float)(z / length)))
            : SdfSkyLayer.IdentityRotation);
    }
    private static Vector3 UnitOr(Vector3 vector, Vector3 fallback) {
        double x = vector.X, y = vector.Y, z = vector.Z;
        var length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

        return (((length > 0d) && double.IsFinite(d: length))
            ? new Vector3(x: ((float)(x / length)), y: ((float)(y / length)), z: ((float)(z / length)))
            : fallback);
    }
}
