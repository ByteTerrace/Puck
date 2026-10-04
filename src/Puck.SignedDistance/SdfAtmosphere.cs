using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>
/// A frame's atmosphere as authored: the air between the camera and what it sees. Each kind is off at zero and costs
/// nothing there, and <see cref="SdfSky.Pack"/> writes them into the sky block's atmosphere lanes with their host bakes.
/// <list type="bullet">
/// <item><description>The <b>fog</b>, an exponential medium of <see cref="FogDensity"/> per world unit, everywhere alike,
/// or, with a positive <see cref="FogFalloff"/>, falling by a factor of e over each <see cref="FogFalloff"/> world units
/// above <see cref="FogBase"/> (height fog). It in-scatters the sky (the environment map) or, when
/// <see cref="FogColorAuthored"/>, <see cref="FogColor"/>, and it ends at the sky: the sky is its colour at
/// infinity.</description></item>
/// <item><description>The <b>haze</b>, aerial perspective: an exponential medium that takes <see cref="HazeAmount"/> of the
/// light along a level ray the far distance long (at <see cref="HazeBase"/> when it falls off with
/// <see cref="HazeFalloff"/>), and in-scatters the sky and the light of the bodies that cast light, forward by the
/// Henyey-Greenstein <see cref="HazeAnisotropy"/>, so it glows toward a low sun. It lies before the sky as well as before
/// the surfaces.</description></item>
/// <item><description>The <b>medium</b>, water below the level <see cref="MediumSurface"/>: <see cref="MediumExtinction"/>
/// per world unit, in-scattering <see cref="MediumColor"/>. The fog and the haze fill the air above it, so a ray
/// crossing the surface passes through each in order.</description></item>
/// </list>
/// The bounded media a creation authors (<see cref="SdfVolume"/>) are not part of this record: they ride the frame's
/// volume table, and the bodies' light they scatter is the sky block's air lights this record's pack bakes.
/// </summary>
public record struct SdfAtmosphere {
    /// <summary>The default rise, in world units, over which a height fog or a falling haze thins by a factor of e.</summary>
    public const float DefaultFalloff = 10f;
    /// <summary>The default haze anisotropy, the Henyey-Greenstein g of a light aerosol haze.</summary>
    public const float DefaultHazeAnisotropy = 0.6f;
    /// <summary>The default medium extinction per world unit: clear water seen over a few units.</summary>
    public const float DefaultMediumExtinction = 0.35f;
    /// <summary>The thinnest falloff a height profile admits, in world units: a thinner layer is a level boundary, which
    /// the medium's surface spells.</summary>
    public const float MinFalloff = 0.01f;
    /// <summary>The largest haze amount: a haze that takes every bit of the light over the far distance has no finite
    /// extinction.</summary>
    public const float MaxHazeAmount = 0.99f;
    /// <summary>The largest haze anisotropy: past it the forward lobe concentrates into a point the composite cannot
    /// sample.</summary>
    public const float MaxHazeAnisotropy = 0.9f;

    /// <summary>The fog's density per world unit at its base; zero draws no fog.</summary>
    public float FogDensity;
    /// <summary>The height at which a height fog's density is <see cref="FogDensity"/>.</summary>
    public float FogBase;
    /// <summary>The rise over which a height fog thins by a factor of e; zero is a fog of the same density everywhere.</summary>
    public float FogFalloff;
    /// <summary>The colour the fog in-scatters when <see cref="FogColorAuthored"/>.</summary>
    public Vector3 FogColor;
    /// <summary>Whether the fog in-scatters <see cref="FogColor"/>; otherwise it in-scatters the sky.</summary>
    public bool FogColorAuthored;
    /// <summary>The share of the light the haze takes along a level ray the far distance long, in
    /// <c>[0, <see cref="MaxHazeAmount"/>]</c>; zero draws no haze.</summary>
    public float HazeAmount;
    /// <summary>The Henyey-Greenstein anisotropy of the haze's scattering toward the bodies, in
    /// <c>[0, <see cref="MaxHazeAnisotropy"/>]</c>.</summary>
    public float HazeAnisotropy;
    /// <summary>The height at which a falling haze takes <see cref="HazeAmount"/>.</summary>
    public float HazeBase;
    /// <summary>The rise over which a falling haze thins by a factor of e; zero is a haze alike at every height.</summary>
    public float HazeFalloff;
    /// <summary>The medium's extinction per world unit below <see cref="MediumSurface"/>; zero is no medium.</summary>
    public float MediumExtinction;
    /// <summary>The height of the medium's surface.</summary>
    public float MediumSurface;
    /// <summary>The colour the medium in-scatters.</summary>
    public Vector3 MediumColor;

    /// <summary>Gets the default medium colour, a deep blue-green water's in linear light.</summary>
    public static Vector3 DefaultMediumColor { get; } = new(x: 0.01f, y: 0.09f, z: 0.12f);
    /// <summary>Gets the default look's atmosphere, which a world authoring no <c>render.atmosphere</c> renders: the fog
    /// <see cref="SdfSky.DefaultFogDensity"/> in-scattering the sky, and nothing else.</summary>
    public static SdfAtmosphere Default => (None with { FogDensity = SdfSky.DefaultFogDensity });
    /// <summary>Gets an atmosphere with every kind off, from which an authored one is written.</summary>
    public static SdfAtmosphere None => new() { HazeAnisotropy = DefaultHazeAnisotropy };
}
/// <summary>What the atmosphere does to light along a ray from the camera's eye to one ray distance: the fraction of
/// the light from that distance it lets through, and, for each kind, the weight its in-scatter colour adds, so a colour
/// c seen there shows <c>Transmittance · c + Fog · fogColor + Haze · hazeColor + Medium · mediumColor</c>.</summary>
/// <param name="Transmittance">The fraction of the light from the ray's end that reaches the eye.</param>
/// <param name="Fog">The fog's in-scatter weight.</param>
/// <param name="Haze">The haze's in-scatter weight.</param>
/// <param name="Medium">The medium's in-scatter weight.</param>
public readonly record struct SdfAirSpan(float Transmittance, float Fog, float Haze, float Medium) {
    /// <summary>Gets the span of a ray through no atmosphere.</summary>
    public static SdfAirSpan Clear => new(Fog: 0f, Haze: 0f, Medium: 0f, Transmittance: 1f);
}
/// <summary>Each atmosphere kind's in-scatter colour in one direction.</summary>
/// <param name="Fog">The fog's: its authored colour, or the sky.</param>
/// <param name="Haze">The haze's: the sky and the bodies' light by the haze's phase.</param>
/// <param name="Medium">The medium's colour.</param>
public readonly record struct SdfAirColors(Vector3 Fog, Vector3 Haze, Vector3 Medium);
/// <summary>A ray through a packed sky block's atmosphere: the block, the camera's eye and the unit direction.</summary>
/// <param name="Block">The packed sky block, whose atmosphere lanes <see cref="SdfSky.Pack"/> wrote.</param>
/// <param name="Origin">The camera's eye.</param>
/// <param name="Direction">The ray's unit direction.</param>
public readonly record struct SdfAirRay(SdfSkyBlock Block, Vector3 Origin, Vector3 Direction) {
    /// <summary>Returns the atmosphere along the ray from the eye to a ray distance (<see cref="SdfAir.Along"/>).</summary>
    /// <param name="distance">The ray distance.</param>
    /// <param name="fog">Whether the fog is included: a surface's ray, not the sky's.</param>
    /// <returns>The span.</returns>
    public SdfAirSpan Along(float distance, bool fog) =>
        SdfAir.Along(block: Block, direction: Direction, distance: distance, fog: fog, origin: Origin);
}
/// <summary>
/// The CPU reference for the atmosphere's closed forms, which <c>shade/sdf-atmosphere.hlsli</c> evaluates in the views,
/// resolve and composite passes in the same order with the same guards. Every kind's optical depth along a ray is exact:
/// a level or exponential-height density integrates in closed form, the medium is homogeneous below its level surface,
/// and the air above it and the water below it are two segments a ray crosses in order, so the span is the exact
/// product of their transmittances. Within the air the fog's and the haze's in-scatter weights split the air's by their
/// optical depths, which is exact where their densities are proportional along the ray (both level, or with one
/// falloff and base) and otherwise apportions the shared transmittance by each kind's own depth.
/// </summary>
public static class SdfAir {
    /// <summary>The bit of the sky block's <see cref="SdfSkyBlock.AirFlags"/> set when the fog in-scatters its authored
    /// colour rather than the sky. KEEP IN SYNC with <c>SdfAirFogColorAuthored</c> in <c>shade/sdf-atmosphere.hlsli</c>.</summary>
    public const uint FogColorAuthored = 1u;
    /// <summary>The most light-casting bodies the haze and the bounded media scatter: the sky block's air lights.</summary>
    public const int MaxLights = 4;
    /// <summary>The magnitude of <c>x = directionY · length / falloff</c> below which <see cref="Share"/> takes its series:
    /// a ray parallel to a height medium's base has <c>x = 0</c>, where the closed form is zero over zero. The series'
    /// first omitted term, <c>x³/24</c>, is under 5e-11 there. KEEP IN SYNC with <c>SdfAirSeriesLimit</c>.</summary>
    public const float SeriesLimit = 1e-3f;
    /// <summary>The largest exponent a height medium's density is raised by, far below its base: <c>e^60</c> is about
    /// 1.1e26, well inside a float, and a depth that large lets no light through anyway. KEEP IN SYNC with
    /// <c>SdfAirExponentLimit</c>.</summary>
    public const float ExponentLimit = 60f;
    /// <summary>The <c>x</c> below which a descending ray's depth folds its two exponentials into one, so neither
    /// overflows alone. KEEP IN SYNC with <c>SdfAirSteepLimit</c>.</summary>
    public const float SteepLimit = -20f;

    /// <summary>Returns <c>(1 − e^−x) / x</c>, the share of a segment's length an exponential-height density integrates to
    /// relative to its value at the segment's start, one where <paramref name="x"/> is zero.</summary>
    /// <param name="x">The density's exponent change across the segment, <c>directionY · length / falloff</c>.</param>
    /// <returns>The share.</returns>
    public static float Share(float x) =>
        ((MathF.Abs(x: x) < SeriesLimit) ? (1f - (x * (0.5f - (x / 6f)))) : ((1f - MathF.Exp(x: -x)) / x));
    /// <summary>Returns the optical depth of an exponential-height medium between two ray distances: the integral of
    /// <c>extinction · e^(−(y − base) / falloff)</c> over the segment, where <c>y</c> is the ray's height, or of
    /// <c>extinction</c> alone when <paramref name="falloff"/> is zero.</summary>
    /// <param name="extinction">The density at the base, per world unit; zero or less is none.</param>
    /// <param name="baseHeight">The base height.</param>
    /// <param name="falloff">The rise over which the density falls by e; zero or less is a level medium.</param>
    /// <param name="originY">The ray's origin height.</param>
    /// <param name="directionY">The ray's unit direction's height component.</param>
    /// <param name="near">The segment's nearer ray distance.</param>
    /// <param name="far">The segment's farther ray distance.</param>
    /// <returns>The optical depth.</returns>
    public static float Depth(float extinction, float baseHeight, float falloff, float originY, float directionY, float near, float far) {
        var length = (far - near);

        if ((extinction <= 0f) || (length <= 0f)) {
            return 0f;
        }
        if (falloff <= 0f) {
            return (extinction * length);
        }

        var rise = (-((originY + (near * directionY)) - baseHeight) / falloff);
        var x = ((directionY * length) / falloff);

        return ((x < SteepLimit)
            ? (((extinction * length) * MathF.Exp(x: MathF.Min(x: (rise - x), y: ExponentLimit))) / -x)
            : (((extinction * length) * MathF.Exp(x: MathF.Min(x: rise, y: ExponentLimit))) * Share(x: x)));
    }
    /// <summary>Returns the atmosphere along a ray from the eye to a ray distance: the air above the medium's surface, its
    /// fog (when <paramref name="fog"/>) and haze, and the water below it, in the order the ray crosses them.</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="origin">The eye.</param>
    /// <param name="direction">The unit direction.</param>
    /// <param name="distance">The ray distance.</param>
    /// <param name="fog">Whether the fog is included: a surface's ray, not the sky's.</param>
    /// <returns>The span.</returns>
    public static SdfAirSpan Along(in SdfSkyBlock block, Vector3 origin, Vector3 direction, float distance, bool fog) {
        var transmittance = 1f;
        float fogWeight = 0f, hazeWeight = 0f, mediumWeight = 0f;

        if (distance <= 0f) {
            return SdfAirSpan.Clear;
        }
        if (block.MediumExtinction > 0f) {
            var below = ((origin.Y < block.MediumSurface) || ((origin.Y == block.MediumSurface) && (direction.Y < 0f)));
            var crossing = distance;

            if (direction.Y != 0f) {
                var at = ((block.MediumSurface - origin.Y) / direction.Y);

                if ((at > 0f) && (at < distance)) {
                    crossing = at;
                }
            }

            Segment(block: in block, direction: direction, far: crossing, fog: fog, fogWeight: ref fogWeight, hazeWeight: ref hazeWeight, mediumWeight: ref mediumWeight, near: 0f, origin: origin, transmittance: ref transmittance, water: below);
            Segment(block: in block, direction: direction, far: distance, fog: fog, fogWeight: ref fogWeight, hazeWeight: ref hazeWeight, mediumWeight: ref mediumWeight, near: crossing, origin: origin, transmittance: ref transmittance, water: !below);
        } else {
            Segment(block: in block, direction: direction, far: distance, fog: fog, fogWeight: ref fogWeight, hazeWeight: ref hazeWeight, mediumWeight: ref mediumWeight, near: 0f, origin: origin, transmittance: ref transmittance, water: false);
        }

        return new SdfAirSpan(Fog: fogWeight, Haze: hazeWeight, Medium: mediumWeight, Transmittance: transmittance);
    }
    /// <summary>Returns the Henyey-Greenstein phase function times 4π, one for an isotropic medium, so a body of radiance
    /// L scattered at the angle whose cosine is <paramref name="cosine"/> adds L times it.</summary>
    /// <param name="cosine">The cosine between the view ray and the direction toward the light.</param>
    /// <param name="anisotropy">The anisotropy g, in [0, 1): positive scatters forward, toward the light.</param>
    /// <returns>The phase.</returns>
    public static float Phase(float cosine, float anisotropy) {
        var g2 = (anisotropy * anisotropy);
        var denominator = MathF.Max(x: ((1f + g2) - ((2f * anisotropy) * cosine)), y: 1e-6f);

        return ((1f - g2) / (denominator * MathF.Sqrt(x: denominator)));
    }
    /// <summary>Returns the light of the block's air lights scattered toward the eye along a direction, by a phase of
    /// anisotropy <paramref name="anisotropy"/>.</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="direction">The view ray's unit direction.</param>
    /// <param name="anisotropy">The phase's anisotropy.</param>
    /// <returns>The scattered radiance.</returns>
    public static Vector3 Bodies(in SdfSkyBlock block, Vector3 direction, float anisotropy) {
        var light = Vector3.Zero;
        var count = Math.Min(val1: ((int)block.AirLightCount), val2: MaxLights);

        for (var index = 0; (index < count); index++) {
            var (toward, radiance) = LightOf(block: in block, index: index);

            light += (radiance * Phase(anisotropy: anisotropy, cosine: Vector3.Dot(vector1: direction, vector2: toward)));
        }

        return light;
    }
    /// <summary>Returns each kind's in-scatter colour in a direction.</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="direction">The view ray's unit direction.</param>
    /// <param name="sky">The sky the fog and the haze in-scatter in that direction, the environment map's.</param>
    /// <returns>The colours.</returns>
    public static SdfAirColors Colors(in SdfSkyBlock block, Vector3 direction, Vector3 sky) => new(
        Fog: (((block.AirFlags & FogColorAuthored) != 0u) ? block.FogColor : sky),
        Haze: (sky + Bodies(anisotropy: block.HazeAnisotropy, block: in block, direction: direction)),
        Medium: block.MediumColor
    );
    /// <summary>Returns one of the block's air lights.</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="index">The light, below <see cref="MaxLights"/>.</param>
    /// <returns>The unit direction toward it and its radiance.</returns>
    public static (Vector3 Direction, Vector3 Radiance) LightOf(in SdfSkyBlock block, int index) => index switch {
        0 => (block.AirLight0Direction, block.AirLight0Radiance),
        1 => (block.AirLight1Direction, block.AirLight1Radiance),
        2 => (block.AirLight2Direction, block.AirLight2Radiance),
        3 => (block.AirLight3Direction, block.AirLight3Radiance),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(index), actualValue: index, message: $"The sky block holds {MaxLights} air lights."),
    };

    // One segment of a ray, all air or all water, after everything nearer: its in-scatter seen through the transmittance
    // before it, and its own transmittance multiplied in.
    private static void Segment(in SdfSkyBlock block, Vector3 origin, Vector3 direction, float near, float far, bool fog, bool water, ref float transmittance, ref float fogWeight, ref float hazeWeight, ref float mediumWeight) {
        if (far <= near) {
            return;
        }
        if (water) {
            var through = MathF.Exp(x: -(block.MediumExtinction * (far - near)));

            mediumWeight += (transmittance * (1f - through));
            transmittance *= through;

            return;
        }

        var fogDepth = (fog ? Depth(baseHeight: block.FogBase, directionY: direction.Y, extinction: block.FogExtinction, falloff: block.FogFalloff, far: far, near: near, originY: origin.Y) : 0f);
        var hazeDepth = Depth(baseHeight: block.HazeBase, directionY: direction.Y, extinction: block.HazeExtinction, falloff: block.HazeFalloff, far: far, near: near, originY: origin.Y);
        var air = MathF.Exp(x: -(fogDepth + hazeDepth));
        var scattered = (transmittance * (1f - air));

        if (hazeDepth <= 0f) {
            fogWeight += scattered;
        } else if (fogDepth <= 0f) {
            hazeWeight += scattered;
        } else {
            var share = ((scattered * fogDepth) / (fogDepth + hazeDepth));

            fogWeight += share;
            hazeWeight += (scattered - share);
        }

        transmittance *= air;
    }
}
