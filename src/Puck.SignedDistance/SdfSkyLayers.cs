using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

/// <summary>A sky layer's kind: which module under <c>Sdf/sky/kinds/</c> evaluates it. A kind is its parameter record
/// (an <see cref="ISdfSkyKind"/>) and its module, registered in the generated kind table the kernels switch on
/// (<c>SdfSkyKindTable</c> in <c>Puck.SdfVm.Model</c>, which writes <c>isa/sdf-sky-kinds.hlsli</c>), so adding a kind
/// touches no other kind and no pass. Its constants reach the kernels as <c>SDF_SKY_KIND_*</c>.</summary>
public enum SdfSkyLayerKind : uint {
    /// <summary>A colour gradient over elevation (<see cref="SdfSkyGradient"/>), a field kind.</summary>
    Gradient = 0,
    /// <summary>The procedural star field (<see cref="SdfSkyStars"/>), a point kind.</summary>
    Stars = 1,
    /// <summary>The procedural cloud dome (<see cref="SdfSkyClouds"/>), a field kind.</summary>
    Clouds = 2,
    /// <summary>Aurora curtains (<see cref="SdfSkyAurora"/>), a field kind.</summary>
    Aurora = 3,
    /// <summary>A periodic fractal noise field (<see cref="SdfSkyNoise"/>), a field kind.</summary>
    Noise = 4,
    /// <summary>A painted pattern over azimuth and elevation (<see cref="SdfSkyPattern"/>), a field kind.</summary>
    Pattern = 5,
    /// <summary>An image source sampled by direction (<see cref="SdfSkyPanorama"/>), a field kind.</summary>
    Panorama = 6,
    /// <summary>A disc about a light's direction, glowing or textured (<see cref="SdfSkyDisc"/>), a point kind.</summary>
    Disc = 7,
    /// <summary>A rectangular emitter at infinity (<see cref="SdfSkyPanel"/>), a point kind.</summary>
    Panel = 8,
}
/// <summary>Where a sky layer draws: everywhere, in an elevation band of the sky frame, or in a cone about a direction.
/// The mask's weight scales the layer's alpha, its edge widened by <see cref="SdfSkyLayer.MaskSoftness"/>.</summary>
public enum SdfSkyMask : uint {
    /// <summary>No mask: the layer draws in every direction.</summary>
    None = 0,
    /// <summary>An elevation band: <see cref="SdfSkyLayer.MaskBand"/>'s x and y are the band's lowest and highest
    /// height (the sky-frame direction's y, in <c>[−1, 1]</c>).</summary>
    Elevation = 1,
    /// <summary>A cone: <see cref="SdfSkyLayer.MaskBand"/>'s xyz is its unit axis in the sky frame and w the cosine of its
    /// angular radius.</summary>
    Cone = 2,
}
/// <summary>Who sees a sky layer: the camera, the lighting (the residency's environment map, which the fog in-scatters
/// and P18-9's ambient and reflection read), or both.</summary>
[Flags]
public enum SdfSkyVisibility : uint {
    /// <summary>Nobody: a layer the stack carries but no pass draws.</summary>
    None = 0,
    /// <summary>Drawn to the camera by the sky and composite passes.</summary>
    Camera = 1,
    /// <summary>Drawn into the residency's environment map.</summary>
    Lighting = 2,
    /// <summary>Drawn to the camera and into the environment map.</summary>
    Both = Camera | Lighting,
}
/// <summary>A sky quality tier: the lowest tier a layer draws at, and the sky's current tier
/// (<c>world.sky-quality</c>), below which a layer writes no entry and below <see cref="High"/> each kind takes its
/// reduced form.</summary>
public enum SdfSkyTier : uint {
    /// <summary>The floor tier: clouds take one thickness tap and three octaves, shaded flat; stars do not twinkle.</summary>
    Low = 0,
    /// <summary>The middle tier: clouds keep their lighting taps at three octaves.</summary>
    Medium = 1,
    /// <summary>The fullest sky.</summary>
    High = 2,
}
/// <summary>The shape a <see cref="SdfSkyPattern"/> paints.</summary>
public enum SdfSkyPatternShape : uint {
    /// <summary>A checkerboard of the two colours.</summary>
    Checker = 0,
    /// <summary>Bands of the second colour across the first, along elevation.</summary>
    Stripes = 1,
    /// <summary>Lines of the second colour over the first, along azimuth and elevation.</summary>
    Grid = 2,
}
/// <summary>How a <see cref="SdfSkyPanorama"/> maps a direction to its image.</summary>
public enum SdfSkyProjection : uint {
    /// <summary>Longitude across, latitude down: the image's top row is the zenith.</summary>
    Equirectangular = 0,
    /// <summary>The octahedral projection with the pole at the zenith (the environment map's).</summary>
    Octahedral = 1,
}
/// <summary>A sky layer kind's parameter record: the kind's payload in a <see cref="SdfSkyLayer"/>, whose HLSL structure
/// and decoder the kind table generates from this type. Its public fields are its layout, at most
/// <see cref="SdfSkyLayer.PayloadBytes"/> bytes.</summary>
public interface ISdfSkyKind {
    /// <summary>Gets the kind the record parameterizes.</summary>
    static abstract SdfSkyLayerKind Kind { get; }
    /// <summary>Gets the class the kind is evaluated in.</summary>
    static abstract SdfSkyLayerClass Class { get; }
    /// <summary>Gets the kind's name: its document spelling's stem, its module's file name and its constant's suffix.</summary>
    static abstract string Name { get; }
}
/// <summary>
/// One record of the sky's layer table (<c>sdfSkyLayers</c>, <see cref="SdfSky.MaxLayers"/> records) the sky, composite
/// and environment passes walk in the authored order: what every layer carries (its kind, blend, counter detail row,
/// visibility, opacity, mask, clock phase and transform) and its kind's parameters in the payload
/// (<see cref="P0"/> to <see cref="P7"/>), which each kind decodes through its generated decoder. Its public fields are
/// the record's layout, whose HLSL declaration <c>puck shaders generate</c> writes from this type.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
public record struct SdfSkyLayer {
    /// <summary>The bytes of a layer's kind payload, <see cref="P0"/> to <see cref="P7"/>.</summary>
    public const int PayloadBytes = 128;
    /// <summary>The byte offset of the payload in the record.</summary>
    public const int PayloadOffset = 64;

    /// <summary>The layer's kind.</summary>
    [FieldOffset(0)] public SdfSkyLayerKind Kind;
    /// <summary>How the layer composes over the colour beneath it.</summary>
    [FieldOffset(4)] public SdfSkyBlend Blend;
    /// <summary>Baked: the work-counter detail row the layer's evaluations count in (<see cref="SdfSkyDetails"/>).</summary>
    [FieldOffset(8)] public uint Detail;
    /// <summary>Who sees the layer.</summary>
    [FieldOffset(12)] public SdfSkyVisibility Visibility;
    /// <summary>The layer's opacity in <c>(0, 1]</c>, which scales its alpha; a layer at zero writes no entry.</summary>
    [FieldOffset(16)] public float Opacity;
    /// <summary>The layer's mask.</summary>
    [FieldOffset(20)] public SdfSkyMask Mask;
    /// <summary>The width of the mask's edge, in the units its band is measured in; zero is a hard edge.</summary>
    [FieldOffset(24)] public float MaskSoftness;
    /// <summary>The phase of the layer's clock at the frame's presented tick, in cycles in <c>[0, 1)</c>, zero without a
    /// clock.</summary>
    [FieldOffset(28)] public float Phase;
    /// <summary>The mask's band: an elevation band's lowest and highest height, or a cone's unit axis and the cosine of
    /// its radius (<see cref="SdfSkyMask"/>).</summary>
    [FieldOffset(32)] public Vector4 MaskBand;
    /// <summary>The layer's transform, a unit quaternion (x, y, z, w) taking a sky-frame direction into the layer's own
    /// frame; the identity is (0, 0, 0, 1). Normalized on pack.</summary>
    [FieldOffset(48)] public Vector4 Rotation;
    /// <summary>The kind payload's first 16 bytes.</summary>
    [FieldOffset(64)] public Vector4 P0;
    /// <summary>The kind payload's bytes 16 to 31.</summary>
    [FieldOffset(80)] public Vector4 P1;
    /// <summary>The kind payload's bytes 32 to 47.</summary>
    [FieldOffset(96)] public Vector4 P2;
    /// <summary>The kind payload's bytes 48 to 63.</summary>
    [FieldOffset(112)] public Vector4 P3;
    /// <summary>The kind payload's bytes 64 to 79.</summary>
    [FieldOffset(128)] public Vector4 P4;
    /// <summary>The kind payload's bytes 80 to 95.</summary>
    [FieldOffset(144)] public Vector4 P5;
    /// <summary>The kind payload's bytes 96 to 111.</summary>
    [FieldOffset(160)] public Vector4 P6;
    /// <summary>The kind payload's bytes 112 to 127.</summary>
    [FieldOffset(176)] public Vector4 P7;

    /// <summary>Returns the identity rotation, (0, 0, 0, 1).</summary>
    public static Vector4 IdentityRotation => new(w: 1f, x: 0f, y: 0f, z: 0f);

    /// <summary>Returns a layer's rotation from a turn about the sky frame's up and a tilt about its right axis: the
    /// quaternion that takes a sky-frame direction into the layer's frame, tilting first. Computed in double and rounded
    /// once.</summary>
    /// <param name="turn">The turn about the frame's up, in radians.</param>
    /// <param name="tilt">The tilt about the frame's right axis, in radians.</param>
    /// <returns>The unit quaternion.</returns>
    public static Vector4 RotationOf(double turn, double tilt) {
        // q = qTilt(x) · qTurn(y): a direction turns about up, then tilts about right.
        var (sy, cy) = Math.SinCos(x: (turn * 0.5d));
        var (sx, cx) = Math.SinCos(x: (tilt * 0.5d));

        return new Vector4(
            w: ((float)(cx * cy)),
            x: ((float)(sx * cy)),
            y: ((float)(cx * sy)),
            z: ((float)(sx * sy))
        );
    }
}
