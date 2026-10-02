using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>
/// The numbers a presentation field admits: an interval whose either end is closed or open, or unbounded. One domain
/// serves the validator, which refuses an authored or bound starting value outside it, and the presentation, which
/// maps a bound value that moves outside it back in with <see cref="Clamp"/>.
/// </summary>
/// <param name="Minimum">The lower end, or negative infinity for none.</param>
/// <param name="Maximum">The upper end, or positive infinity for none.</param>
/// <param name="MinimumOpen">Whether <paramref name="Minimum"/> itself lies outside the domain.</param>
/// <param name="MaximumOpen">Whether <paramref name="Maximum"/> itself lies outside the domain.</param>
public readonly record struct WorldValueDomain(float Minimum, float Maximum, bool MinimumOpen = false, bool MaximumOpen = false) {
    /// <summary>Gets every finite number.</summary>
    public static WorldValueDomain Finite { get; } = new(
        Maximum: float.PositiveInfinity,
        Minimum: float.NegativeInfinity
    );
    /// <summary>Gets every finite number at or above zero.</summary>
    public static WorldValueDomain NonNegative { get; } = new(
        Maximum: float.PositiveInfinity,
        Minimum: 0f
    );
    /// <summary>Gets every finite number above zero.</summary>
    public static WorldValueDomain Positive { get; } = new(
        Maximum: float.PositiveInfinity,
        Minimum: 0f,
        MinimumOpen: true
    );
    /// <summary>Gets the closed unit interval.</summary>
    public static WorldValueDomain Unit { get; } = new(
        Maximum: 1f,
        Minimum: 0f
    );

    /// <summary>Gets whether the domain admits less than every finite number.</summary>
    public bool IsRestricted => ((Lowest > float.MinValue) || (Highest < float.MaxValue));
    /// <summary>Gets the smallest number the domain admits: its closed lower end, the next float above an open one,
    /// or the most negative finite float when it has none.</summary>
    public float Lowest => (float.IsNegativeInfinity(f: Minimum)
        ? float.MinValue
        : (MinimumOpen
            ? MathF.BitIncrement(x: Minimum)
            : Minimum));
    /// <summary>Gets the largest number the domain admits: its closed upper end, the next float below an open one,
    /// or the largest finite float when it has none.</summary>
    public float Highest => (float.IsPositiveInfinity(f: Maximum)
        ? float.MaxValue
        : (MaximumOpen
            ? MathF.BitDecrement(x: Maximum)
            : Maximum));

    /// <summary>Returns whether the domain admits a number.</summary>
    /// <param name="value">The number.</param>
    /// <returns><see langword="true"/> when it is finite and lies within both ends.</returns>
    public bool Contains(float value) => (
        float.IsFinite(f: value) &&
        (value >= Lowest) &&
        (value <= Highest)
    );
    /// <summary>Maps a number into the domain, as a pure function of the number alone: a number inside is returned
    /// as it is, a number below <see cref="Lowest"/> (or not a number) becomes <see cref="Lowest"/>, and a number above
    /// <see cref="Highest"/> becomes <see cref="Highest"/>. A closed end clamps to itself; an open end clamps to the
    /// nearest float inside it.</summary>
    /// <param name="value">The number.</param>
    /// <returns>A number the domain admits.</returns>
    public float Clamp(float value) => ((value >= Lowest)
        ? MathF.Min(
            x: value,
            y: Highest
        )
        : Lowest);
    /// <inheritdoc/>
    public override string ToString() => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"{((MinimumOpen || float.IsNegativeInfinity(f: Minimum)) ? '(' : '[')}{(float.IsNegativeInfinity(f: Minimum) ? "-inf" : Minimum.ToString(provider: CultureInfo.InvariantCulture))}, {(float.IsPositiveInfinity(f: Maximum) ? "inf" : Maximum.ToString(provider: CultureInfo.InvariantCulture))}{((MaximumOpen || float.IsPositiveInfinity(f: Maximum)) ? ')' : ']')}"
    );
}
/// <summary>One bindable presentation scalar of the document model and the one domain it declares: the
/// <see cref="BindableScalar"/> or <see cref="BindableAngle"/> member <paramref name="Member"/> of
/// <paramref name="Owner"/>.</summary>
/// <param name="Owner">The model type that declares the member.</param>
/// <param name="Member">The member's CLR name.</param>
/// <param name="Domain">The numbers the field admits.</param>
/// <param name="Why">What a value outside the domain would break, which a refusal states, or <see langword="null"/>
/// when the domain speaks for itself.</param>
public sealed record WorldValueField(Type Owner, string Member, WorldValueDomain Domain, string? Why = null) {
    /// <summary>Gets the member's document name, the last segment of every path that names the field.</summary>
    public string Name { get; } = JsonNamingPolicy.CamelCase.ConvertName(name: Member);
}
/// <summary>
/// The declaration of every bindable presentation scalar's domain, one row per <see cref="BindableScalar"/> and
/// <see cref="BindableAngle"/> member of the document model. The validator judges a field's literal, keys and bound
/// starting value against its row, and the presentation clamps the field's resolved value into the same row's domain,
/// so the two never disagree. A member whose every finite number is admissible declares <see cref="WorldValueDomain.Finite"/>.
/// </summary>
public static class WorldValueFields {
    /// <summary>A directional light's weight.</summary>
    public static WorldValueField DirectionalWeight { get; } = new(typeof(WorldRenderLight.Directional), nameof(WorldRenderLight.Directional.Weight), WorldValueDomain.NonNegative);
    /// <summary>A directional light's angular radius, in radians, up to the widest penumbra the lights table carries.</summary>
    public static WorldValueField DirectionalAngularRadius { get; } = new(typeof(WorldRenderLight.Directional), nameof(WorldRenderLight.Directional.AngularRadius), new WorldValueDomain(
        Maximum: MathF.Atan(x: SdfLights.MaxPenumbraSlope),
        Minimum: 0f
    ));
    /// <summary>A hemisphere light's base.</summary>
    public static WorldValueField HemisphereBase { get; } = new(typeof(WorldRenderLight.Hemisphere), nameof(WorldRenderLight.Hemisphere.Base), WorldValueDomain.NonNegative);
    /// <summary>A hemisphere light's gradient.</summary>
    public static WorldValueField HemisphereGradient { get; } = new(typeof(WorldRenderLight.Hemisphere), nameof(WorldRenderLight.Hemisphere.Gradient), WorldValueDomain.Finite);
    /// <summary>A rim light's weight.</summary>
    public static WorldValueField RimWeight { get; } = new(typeof(WorldRenderLight.Rim), nameof(WorldRenderLight.Rim.Weight), WorldValueDomain.NonNegative);
    /// <summary>A rim light's power.</summary>
    public static WorldValueField RimPower { get; } = new(typeof(WorldRenderLight.Rim), nameof(WorldRenderLight.Rim.Power), WorldValueDomain.NonNegative);
    /// <summary>A point light's radius.</summary>
    public static WorldValueField PointRadius { get; } = new(typeof(WorldRenderLight.Point), nameof(WorldRenderLight.Point.Radius), WorldValueDomain.Positive);
    /// <summary>A point light's weight.</summary>
    public static WorldValueField PointWeight { get; } = new(typeof(WorldRenderLight.Point), nameof(WorldRenderLight.Point.Weight), WorldValueDomain.NonNegative);
    /// <summary>An occluder's radius.</summary>
    public static WorldValueField OccluderRadius { get; } = new(typeof(WorldRenderLight.Occluder), nameof(WorldRenderLight.Occluder.Radius), WorldValueDomain.Positive);
    /// <summary>An occluder's weight.</summary>
    public static WorldValueField OccluderWeight { get; } = new(typeof(WorldRenderLight.Occluder), nameof(WorldRenderLight.Occluder.Weight), WorldValueDomain.Unit);
    /// <summary>The curvature cavity gain.</summary>
    public static WorldValueField CurvatureCavity { get; } = new(typeof(WorldRenderCurvature), nameof(WorldRenderCurvature.Cavity), WorldValueDomain.NonNegative);
    /// <summary>The curvature rim gain.</summary>
    public static WorldValueField CurvatureRim { get; } = new(typeof(WorldRenderCurvature), nameof(WorldRenderCurvature.Rim), WorldValueDomain.NonNegative);
    /// <summary>The curvature ink gain.</summary>
    public static WorldValueField CurvatureInk { get; } = new(typeof(WorldRenderCurvature), nameof(WorldRenderCurvature.Ink), WorldValueDomain.NonNegative);
    /// <summary>The ink band's lower end.</summary>
    public static WorldValueField CurvatureInkLow { get; } = new(typeof(WorldRenderCurvature), nameof(WorldRenderCurvature.InkLow), WorldValueDomain.NonNegative);
    /// <summary>The ink band's upper end.</summary>
    public static WorldValueField CurvatureInkHigh { get; } = new(typeof(WorldRenderCurvature), nameof(WorldRenderCurvature.InkHigh), WorldValueDomain.NonNegative);
    /// <summary>A gradient stop's elevation, the sine of its angle above the horizon.</summary>
    public static WorldValueField StopElevation { get; } = new(typeof(WorldRenderSkyStop), nameof(WorldRenderSkyStop.Elevation), new WorldValueDomain(
        Maximum: 1f,
        Minimum: -1f
    ));
    /// <summary>The fog's density.</summary>
    public static WorldValueField FogDensity { get; } = new(typeof(WorldRenderSkyLayer.Fog), nameof(WorldRenderSkyLayer.Fog.Density), WorldValueDomain.NonNegative);
    /// <summary>The sun disc's angular radius, in radians, above zero and up to a quarter turn.</summary>
    public static WorldValueField SunDiscRadius { get; } = new(typeof(WorldRenderSkyLayer.SunDisc), nameof(WorldRenderSkyLayer.SunDisc.Radius), new WorldValueDomain(
        Maximum: (MathF.PI / 2f),
        Minimum: 0f,
        MinimumOpen: true
    ));
    /// <summary>The sun disc's intensity.</summary>
    public static WorldValueField SunDiscIntensity { get; } = new(typeof(WorldRenderSkyLayer.SunDisc), nameof(WorldRenderSkyLayer.SunDisc.Intensity), WorldValueDomain.NonNegative);
    /// <summary>The stars' brightness.</summary>
    public static WorldValueField StarBrightness { get; } = new(typeof(WorldRenderSkyLayer.Stars), nameof(WorldRenderSkyLayer.Stars.Brightness), WorldValueDomain.NonNegative);
    /// <summary>The share of stars that twinkle.</summary>
    public static WorldValueField TwinkleShare { get; } = new(typeof(WorldRenderSkyTwinkle), nameof(WorldRenderSkyTwinkle.Share), WorldValueDomain.Unit);
    /// <summary>How deep a twinkle dims a star.</summary>
    public static WorldValueField TwinkleDepth { get; } = new(typeof(WorldRenderSkyTwinkle), nameof(WorldRenderSkyTwinkle.Depth), WorldValueDomain.Unit);
    /// <summary>The twinkle's rate, in cycles per second.</summary>
    public static WorldValueField TwinkleRate { get; } = new(typeof(WorldRenderSkyTwinkle), nameof(WorldRenderSkyTwinkle.Rate), WorldValueDomain.Positive);
    /// <summary>The clouds' coverage.</summary>
    public static WorldValueField CloudCoverage { get; } = new(typeof(WorldRenderSkyLayer.Clouds), nameof(WorldRenderSkyLayer.Clouds.Coverage), WorldValueDomain.Unit);
    /// <summary>The clouds' softness, the width of the band a cloud's edge fades across.</summary>
    public static WorldValueField CloudSoftness { get; } = new(typeof(WorldRenderSkyLayer.Clouds), nameof(WorldRenderSkyLayer.Clouds.Softness), new WorldValueDomain(
        Maximum: 1f,
        Minimum: 0f,
        MinimumOpen: true
    ));
    /// <summary>The clouds' scale.</summary>
    public static WorldValueField CloudScale { get; } = new(typeof(WorldRenderSkyLayer.Clouds), nameof(WorldRenderSkyLayer.Clouds.Scale), WorldValueDomain.Positive);
    /// <summary>The clouds' spin rate, in radians per second.</summary>
    public static WorldValueField CloudSpin { get; } = new(typeof(WorldRenderSkyLayer.Clouds), nameof(WorldRenderSkyLayer.Clouds.Spin), WorldValueDomain.Finite);
    /// <summary>The clouds' curl, in radians.</summary>
    public static WorldValueField CloudCurl { get; } = new(typeof(WorldRenderSkyLayer.Clouds), nameof(WorldRenderSkyLayer.Clouds.Curl), WorldValueDomain.Finite);
    /// <summary>A scrim's alpha, at or above the floor its contrast needs.</summary>
    public static WorldValueField ScrimAlpha { get; } = new(typeof(WorldThemeScrim), nameof(WorldThemeScrim.Alpha), new WorldValueDomain(
        Maximum: 1f,
        Minimum: WorldThemeCapacity.ScrimMinAlpha
    ), Why: "below its floor the guaranteed-AA contrast floor (over both a dark corner and a lit CRT) breaks");
    /// <summary>The accent bloom's halo alpha.</summary>
    public static WorldValueField BloomHaloAlpha { get; } = new(typeof(WorldThemeElevation), nameof(WorldThemeElevation.BloomHaloAlpha), WorldValueDomain.Unit);
    /// <summary>The accent bloom's ring alpha.</summary>
    public static WorldValueField BloomRingAlpha { get; } = new(typeof(WorldThemeElevation), nameof(WorldThemeElevation.BloomRingAlpha), WorldValueDomain.Unit);
    /// <summary>The neutral bloom's halo alpha.</summary>
    public static WorldValueField BloomNeutralHaloAlpha { get; } = new(typeof(WorldThemeElevation), nameof(WorldThemeElevation.BloomNeutralHaloAlpha), WorldValueDomain.Unit);
    /// <summary>The neutral bloom's ring alpha.</summary>
    public static WorldValueField BloomNeutralRingAlpha { get; } = new(typeof(WorldThemeElevation), nameof(WorldThemeElevation.BloomNeutralRingAlpha), WorldValueDomain.Unit);
    /// <summary>The held inset's alpha.</summary>
    public static WorldValueField BloomHeldInsetAlpha { get; } = new(typeof(WorldThemeElevation), nameof(WorldThemeElevation.BloomHeldInsetAlpha), WorldValueDomain.Unit);
    /// <summary>A marker chip's alpha.</summary>
    public static WorldValueField MarkerChipAlpha { get; } = new(typeof(WorldMarkerStyle), nameof(WorldMarkerStyle.ChipAlpha), WorldValueDomain.Unit);
    /// <summary>A marker ring's alpha.</summary>
    public static WorldValueField MarkerRingAlpha { get; } = new(typeof(WorldMarkerStyle), nameof(WorldMarkerStyle.RingAlpha), WorldValueDomain.Unit);
    /// <summary>An orbit's yaw, in radians.</summary>
    public static WorldValueField OrbitYaw { get; } = new(typeof(WorldCameraProgramOp.Orbit), nameof(WorldCameraProgramOp.Orbit.Yaw), WorldValueDomain.Finite);
    /// <summary>An orbit's pitch, in radians.</summary>
    public static WorldValueField OrbitPitch { get; } = new(typeof(WorldCameraProgramOp.Orbit), nameof(WorldCameraProgramOp.Orbit.Pitch), WorldValueDomain.Finite);
    /// <summary>A path op's fraction along its curve.</summary>
    public static WorldValueField PathFraction { get; } = new(typeof(WorldCameraProgramOp.Path), nameof(WorldCameraProgramOp.Path.Fraction), WorldValueDomain.Finite);
    /// <summary>A field-of-view op's angle, in radians.</summary>
    public static WorldValueField FieldOfView { get; } = new(typeof(WorldCameraProgramOp.FieldOfView), nameof(WorldCameraProgramOp.FieldOfView.FieldOfViewRadians), WorldValueDomain.Finite);
    /// <summary>A blend op's weight.</summary>
    public static WorldValueField BlendWeight { get; } = new(typeof(WorldCameraProgramOp.Blend), nameof(WorldCameraProgramOp.Blend.Weight), WorldValueDomain.Finite);
    /// <summary>A select op's key.</summary>
    public static WorldValueField SelectKey { get; } = new(typeof(WorldCameraProgramOp.SelectProgram), nameof(WorldCameraProgramOp.SelectProgram.Key), WorldValueDomain.Finite);

    /// <summary>Returns the field a model member declares.</summary>
    /// <param name="owner">The model type that declares the member.</param>
    /// <param name="member">The member's CLR name.</param>
    /// <returns>The field, or <see langword="null"/> when the member is no bindable presentation scalar.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> or <paramref name="member"/> is
    /// <see langword="null"/>.</exception>
    public static WorldValueField? Of(Type owner, string member) {
        ArgumentNullException.ThrowIfNull(argument: owner);
        ArgumentNullException.ThrowIfNull(argument: member);

        return (Lookup.ByMember.TryGetValue(
            key: (owner, member),
            value: out var field
        )
            ? field
            : null);
    }

    /// <summary>Gets every field, in declaration order.</summary>
    public static IReadOnlyList<WorldValueField> All { get; } = [
        DirectionalWeight,
        DirectionalAngularRadius,
        HemisphereBase,
        HemisphereGradient,
        RimWeight,
        RimPower,
        PointRadius,
        PointWeight,
        OccluderRadius,
        OccluderWeight,
        CurvatureCavity,
        CurvatureRim,
        CurvatureInk,
        CurvatureInkLow,
        CurvatureInkHigh,
        StopElevation,
        FogDensity,
        SunDiscRadius,
        SunDiscIntensity,
        StarBrightness,
        TwinkleShare,
        TwinkleDepth,
        TwinkleRate,
        CloudCoverage,
        CloudSoftness,
        CloudScale,
        CloudSpin,
        CloudCurl,
        ScrimAlpha,
        BloomHaloAlpha,
        BloomRingAlpha,
        BloomNeutralHaloAlpha,
        BloomNeutralRingAlpha,
        BloomHeldInsetAlpha,
        MarkerChipAlpha,
        MarkerRingAlpha,
        OrbitYaw,
        OrbitPitch,
        PathFraction,
        FieldOfView,
        BlendWeight,
        SelectKey,
    ];

    // Built on first lookup, once every field and All have initialized.
    private static class Lookup {
        public static readonly FrozenDictionary<(Type Owner, string Member), WorldValueField> ByMember = All.ToFrozenDictionary(keySelector: static field => (field.Owner, field.Member));
    }
}
