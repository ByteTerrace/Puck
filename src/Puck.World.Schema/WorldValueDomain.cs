using System.Collections.Frozen;
using System.Text.Json;
using Puck.Abstractions.Cameras;
using Puck.SignedDistance;
using Puck.World.Authoring;

namespace Puck.World;

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
/// starting value against its row, and the presentation maps the field's resolved value through the same row's domain
/// (<see cref="WorldValueDomain.Map"/>), so the two never disagree. A member whose every finite number is admissible declares <see cref="WorldValueDomain.Finite"/>.
/// A plain-number member whose domain a kernel depends on (<see cref="VolumeSoftness"/>) has a row too, judged where
/// its document is validated.
/// </summary>
public static class WorldValueFields {
    /// <summary>The brightest illuminance a directional light admits, in lux: twice the brightest sunlight at the Earth's
    /// surface, so a weight authored as if it were lux, or lux authored as a weight, is caught by name.</summary>
    public const float MaxIlluminance = 250000f;
    /// <summary>The brightest luminous intensity a point or spot light admits, in candela: a stadium floodlight's.</summary>
    public const float MaxIntensity = 10000000f;
    /// <summary>The largest luminous power a point or spot light admits, in lumens.</summary>
    public const float MaxPower = 100000000f;
    /// <summary>The brightest luminance a sky layer, a rim, a fog or a medium admits, in nits: the sun's own disc.</summary>
    public const float MaxLuminance = 2000000000f;

    // The photometric domains the lights and the sky share.
    private static readonly WorldValueDomain Temperature = new(Maximum: SdfPhotometry.MaximumTemperature, Minimum: SdfPhotometry.MinimumTemperature);
    private static readonly WorldValueDomain Intensity = new(Maximum: MaxIntensity, Minimum: 0f);
    private static readonly WorldValueDomain Power = new(Maximum: MaxPower, Minimum: 0f);
    private static readonly WorldValueDomain Luminance = new(Maximum: MaxLuminance, Minimum: 0f);
    private const string TemperatureWhy = "a colour temperature in kelvin, on the black-body locus between a candle's and a blue sky's";
    private const string IntensityWhy = "a luminous intensity in candela";
    private const string PowerWhy = "a luminous power in lumens";
    private const string LuminanceWhy = "a luminance in nits";

    /// <summary>Explicit diffuse light admitted to indirect transport.</summary>
    public static WorldValueField IndirectLights { get; } = new(typeof(WorldRenderIndirectSources), nameof(WorldRenderIndirectSources.Lights), WorldValueDomain.Unit);
    /// <summary>Material emission admitted to indirect transport.</summary>
    public static WorldValueField IndirectEmission { get; } = new(typeof(WorldRenderIndirectSources), nameof(WorldRenderIndirectSources.Emission), WorldValueDomain.Unit);
    /// <summary>Acquired screen emission admitted to indirect transport.</summary>
    public static WorldValueField IndirectScreens { get; } = new(typeof(WorldRenderIndirectSources), nameof(WorldRenderIndirectSources.Screens), WorldValueDomain.Unit);
    /// <summary>Certified world-exit sky radiance admitted to indirect transport.</summary>
    public static WorldValueField IndirectSky { get; } = new(typeof(WorldRenderIndirectSources), nameof(WorldRenderIndirectSources.Sky), WorldValueDomain.Unit);
    /// <summary>Reflected previous-sweep radiance admitted at each feedback hit.</summary>
    public static WorldValueField IndirectFeedback { get; } = new(typeof(WorldRenderIndirectSources), nameof(WorldRenderIndirectSources.Feedback), WorldValueDomain.Unit);
    /// <summary>Receiver-only indirect diffuse gain.</summary>
    public static WorldValueField IndirectIntensity { get; } = new(typeof(WorldRenderIndirectApply), nameof(WorldRenderIndirectApply.Intensity), WorldValueDomain.Unit);
    /// <summary>Receiver-only ambient-occlusion attenuation.</summary>
    public static WorldValueField IndirectContact { get; } = new(typeof(WorldRenderIndirectApply), nameof(WorldRenderIndirectApply.Contact), WorldValueDomain.Unit);
    /// <summary>A light's indirect diffuse gain.</summary>
    public static WorldValueField LightBounce { get; } = new(typeof(WorldRenderLight), nameof(WorldRenderLight.Bounce), WorldValueDomain.NonNegative);
    /// <summary>A directional light's illuminance, in lux, up to twice the brightest sunlight at the Earth's surface.</summary>
    public static WorldValueField DirectionalIlluminance { get; } = new(typeof(WorldRenderLight.Directional), nameof(WorldRenderLight.Directional.Illuminance), new WorldValueDomain(
        Maximum: MaxIlluminance,
        Minimum: 0f
    ), Why: "an illuminance in lux, which no sun exceeds");
    /// <summary>A directional light's colour temperature, in kelvin.</summary>
    public static WorldValueField DirectionalTemperature { get; } = new(typeof(WorldRenderLight.Directional), nameof(WorldRenderLight.Directional.Temperature), Temperature, Why: TemperatureWhy);
    /// <summary>A point light's luminous intensity, in candela.</summary>
    public static WorldValueField PointIntensity { get; } = new(typeof(WorldRenderLight.Point), nameof(WorldRenderLight.Point.Intensity), Intensity, Why: IntensityWhy);
    /// <summary>A point light's luminous power, in lumens.</summary>
    public static WorldValueField PointPower { get; } = new(typeof(WorldRenderLight.Point), nameof(WorldRenderLight.Point.Power), Power, Why: PowerWhy);
    /// <summary>A point light's colour temperature, in kelvin.</summary>
    public static WorldValueField PointTemperature { get; } = new(typeof(WorldRenderLight.Point), nameof(WorldRenderLight.Point.Temperature), Temperature, Why: TemperatureWhy);
    /// <summary>A spot light's luminous intensity along its aim, in candela.</summary>
    public static WorldValueField SpotIntensity { get; } = new(typeof(WorldRenderLight.Spot), nameof(WorldRenderLight.Spot.Intensity), Intensity, Why: IntensityWhy);
    /// <summary>A spot light's luminous power, in lumens.</summary>
    public static WorldValueField SpotPower { get; } = new(typeof(WorldRenderLight.Spot), nameof(WorldRenderLight.Spot.Power), Power, Why: PowerWhy);
    /// <summary>A spot light's colour temperature, in kelvin.</summary>
    public static WorldValueField SpotTemperature { get; } = new(typeof(WorldRenderLight.Spot), nameof(WorldRenderLight.Spot.Temperature), Temperature, Why: TemperatureWhy);
    /// <summary>A spot light's outer half angle, in radians, above zero and up to a quarter turn.</summary>
    public static WorldValueField SpotAngle { get; } = new(typeof(WorldRenderLight.Spot), nameof(WorldRenderLight.Spot.Angle), new WorldValueDomain(
        Maximum: (MathF.PI / 2f),
        Minimum: 0f,
        MinimumOpen: true
    ), Why: "a cone wider than a hemisphere is a point light");
    /// <summary>A spot light's inner half angle, in radians, up to a quarter turn.</summary>
    public static WorldValueField SpotInnerAngle { get; } = new(typeof(WorldRenderLight.Spot), nameof(WorldRenderLight.Spot.InnerAngle), new WorldValueDomain(
        Maximum: (MathF.PI / 2f),
        Minimum: 0f
    ));
    /// <summary>A sky layer's luminance, in nits.</summary>
    public static WorldValueField SkyLayerLuminance { get; } = new(typeof(WorldRenderSkyLayer), nameof(WorldRenderSkyLayer.Luminance), Luminance, Why: LuminanceWhy);
    /// <summary>The fog's luminance, in nits.</summary>
    public static WorldValueField FogLuminance { get; } = new(typeof(WorldRenderFog), nameof(WorldRenderFog.Luminance), Luminance, Why: LuminanceWhy);
    /// <summary>The medium's luminance, in nits.</summary>
    public static WorldValueField MediumLuminance { get; } = new(typeof(WorldRenderMedium), nameof(WorldRenderMedium.Luminance), Luminance, Why: LuminanceWhy);
    /// <summary>A directional light's positive angular radius, in radians, up to the widest penumbra the lights table carries.</summary>
    public static WorldValueField DirectionalAngularRadius { get; } = new(typeof(WorldRenderLight.Directional), nameof(WorldRenderLight.Directional.AngularRadius), new WorldValueDomain(
        Maximum: MathF.Atan(x: SdfLights.MaxPenumbraSlope),
        Minimum: 0f,
        MinimumOpen: true
    ));
    /// <summary>The sky's ambient gain.</summary>
    public static WorldValueField EnvironmentAmbient { get; } = new(typeof(WorldRenderEnvironment), nameof(WorldRenderEnvironment.Ambient), WorldValueDomain.NonNegative);
    /// <summary>The sky's reflection gain.</summary>
    public static WorldValueField EnvironmentReflection { get; } = new(typeof(WorldRenderEnvironment), nameof(WorldRenderEnvironment.Reflection), WorldValueDomain.NonNegative);
    /// <summary>A sky panel's intensity.</summary>
    public static WorldValueField PanelIntensity { get; } = new(typeof(WorldRenderSkyLayer.Panel), nameof(WorldRenderSkyLayer.Panel.Intensity), WorldValueDomain.NonNegative);
    /// <summary>A sky panel's blur.</summary>
    public static WorldValueField PanelBlur { get; } = new(typeof(WorldRenderSkyLayer.Panel), nameof(WorldRenderSkyLayer.Panel.Blur), WorldValueDomain.NonNegative);
    /// <summary>A rim light's luminance, in nits.</summary>
    public static WorldValueField RimLuminance { get; } = new(typeof(WorldRenderLight.Rim), nameof(WorldRenderLight.Rim.Luminance), Luminance, Why: LuminanceWhy);
    /// <summary>A rim light's power.</summary>
    public static WorldValueField RimPower { get; } = new(typeof(WorldRenderLight.Rim), nameof(WorldRenderLight.Rim.Power), WorldValueDomain.NonNegative);
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
        Minimum: SdfSky.MinCloudSoftness
    ), Why: "a narrower band leaves the cloud edge's smoothstep without two distinct edges");
    /// <summary>The clouds' scale.</summary>
    public static WorldValueField CloudScale { get; } = new(typeof(WorldRenderSkyLayer.Clouds), nameof(WorldRenderSkyLayer.Clouds.Scale), WorldValueDomain.Positive);
    /// <summary>The clouds' spin rate, in radians per second.</summary>
    public static WorldValueField CloudSpin { get; } = new(typeof(WorldRenderSkyLayer.Clouds), nameof(WorldRenderSkyLayer.Clouds.Spin), WorldValueDomain.Finite);
    /// <summary>The clouds' curl, in radians.</summary>
    public static WorldValueField CloudCurl { get; } = new(typeof(WorldRenderSkyLayer.Clouds), nameof(WorldRenderSkyLayer.Clouds.Curl), WorldValueDomain.Finite);
    /// <summary>The fog's density per world unit.</summary>
    public static WorldValueField FogDensity { get; } = new(typeof(WorldRenderFog), nameof(WorldRenderFog.Density), WorldValueDomain.NonNegative);
    /// <summary>An air kind's base height.</summary>
    public static WorldValueField AirBase { get; } = new(typeof(WorldRenderAirHeight), nameof(WorldRenderAirHeight.Base), WorldValueDomain.Finite);
    /// <summary>An air kind's falloff rise, at or above the thinnest layer a height profile admits.</summary>
    public static WorldValueField AirFalloff { get; } = new(typeof(WorldRenderAirHeight), nameof(WorldRenderAirHeight.Falloff), new WorldValueDomain(
        Maximum: float.PositiveInfinity,
        Minimum: SdfAtmosphere.MinFalloff
    ), Why: "a thinner layer is a level boundary, which the medium's surface spells");
    /// <summary>The share of the light the haze takes over the far distance.</summary>
    public static WorldValueField HazeAmount { get; } = new(typeof(WorldRenderHaze), nameof(WorldRenderHaze.Amount), new WorldValueDomain(
        Maximum: SdfAtmosphere.MaxHazeAmount,
        Minimum: 0f
    ), Why: "a haze that takes all the light has no finite extinction");
    /// <summary>The haze's Henyey-Greenstein anisotropy.</summary>
    public static WorldValueField HazeAnisotropy { get; } = new(typeof(WorldRenderHaze), nameof(WorldRenderHaze.Anisotropy), new WorldValueDomain(
        Maximum: SdfAtmosphere.MaxHazeAnisotropy,
        Minimum: 0f
    ), Why: "a sharper forward lobe collapses toward a point the composite cannot sample");
    /// <summary>The medium's surface height.</summary>
    public static WorldValueField MediumSurface { get; } = new(typeof(WorldRenderMedium), nameof(WorldRenderMedium.Surface), WorldValueDomain.Finite);
    /// <summary>The medium's extinction per world unit.</summary>
    public static WorldValueField MediumExtinction { get; } = new(typeof(WorldRenderMedium), nameof(WorldRenderMedium.Extinction), WorldValueDomain.NonNegative);
    /// <summary>A sky layer's opacity.</summary>
    public static WorldValueField SkyLayerOpacity { get; } = new(typeof(WorldRenderSkyLayer), nameof(WorldRenderSkyLayer.Opacity), WorldValueDomain.Unit);
    /// <summary>A sky layer's turn about the sky frame's up, in radians.</summary>
    public static WorldValueField SkyLayerTurn { get; } = new(typeof(WorldRenderSkyTransform), nameof(WorldRenderSkyTransform.Turn), WorldValueDomain.Finite);
    /// <summary>A sky layer's tilt about the sky frame's right axis, in radians.</summary>
    public static WorldValueField SkyLayerTilt { get; } = new(typeof(WorldRenderSkyTransform), nameof(WorldRenderSkyTransform.Tilt), WorldValueDomain.Finite);
    /// <summary>An aurora's brightness.</summary>
    public static WorldValueField AuroraIntensity { get; } = new(typeof(WorldRenderSkyLayer.Aurora), nameof(WorldRenderSkyLayer.Aurora.Intensity), WorldValueDomain.NonNegative);
    /// <summary>An aurora's base elevation, in radians, from the nadir to the zenith.</summary>
    public static WorldValueField AuroraBase { get; } = new(typeof(WorldRenderSkyLayer.Aurora), nameof(WorldRenderSkyLayer.Aurora.Base), new WorldValueDomain(
        Maximum: (MathF.PI / 2f),
        Minimum: -(MathF.PI / 2f)
    ));
    /// <summary>An aurora's height above its base, in radians, above zero and up to a half turn.</summary>
    public static WorldValueField AuroraHeight { get; } = new(typeof(WorldRenderSkyLayer.Aurora), nameof(WorldRenderSkyLayer.Aurora.Height), new WorldValueDomain(
        Maximum: MathF.PI,
        Minimum: 0f,
        MinimumOpen: true
    ), Why: "the kernel divides by a curtain's height");
    /// <summary>How far an aurora's base wavers, in radians.</summary>
    public static WorldValueField AuroraFold { get; } = new(typeof(WorldRenderSkyLayer.Aurora), nameof(WorldRenderSkyLayer.Aurora.Fold), WorldValueDomain.NonNegative);
    /// <summary>A noise layer's coverage.</summary>
    public static WorldValueField NoiseCoverage { get; } = new(typeof(WorldRenderSkyLayer.Noise), nameof(WorldRenderSkyLayer.Noise.Coverage), WorldValueDomain.Unit);
    /// <summary>A panorama's brightness.</summary>
    public static WorldValueField PanoramaIntensity { get; } = new(typeof(WorldRenderSkyLayer.Panorama), nameof(WorldRenderSkyLayer.Panorama.Intensity), WorldValueDomain.NonNegative);
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
    /// <summary>A cloud volume's softness, a literal a creation authors.</summary>
    public static WorldValueField VolumeSoftness { get; } = new(typeof(VolumeDocument), nameof(VolumeDocument.Softness), VolumeDocument.SoftnessDomain);
    /// <summary>An orbit's yaw, in radians.</summary>
    public static WorldValueField OrbitYaw { get; } = new(typeof(WorldCameraProgramOp.Orbit), nameof(WorldCameraProgramOp.Orbit.Yaw), WorldValueDomain.Finite);
    /// <summary>An orbit's pitch, in radians.</summary>
    public static WorldValueField OrbitPitch { get; } = new(typeof(WorldCameraProgramOp.Orbit), nameof(WorldCameraProgramOp.Orbit.Pitch), WorldValueDomain.Finite);
    /// <summary>A path op's fraction along its curve.</summary>
    public static WorldValueField PathFraction { get; } = new(typeof(WorldCameraProgramOp.Path), nameof(WorldCameraProgramOp.Path.Fraction), WorldValueDomain.Finite);
    /// <summary>A field-of-view op's angle, in radians, from the narrowest a camera is built with to just under a half
    /// turn.</summary>
    public static WorldValueField FieldOfView { get; } = new(typeof(WorldCameraProgramOp.FieldOfView), nameof(WorldCameraProgramOp.FieldOfView.FieldOfViewRadians), new WorldValueDomain(
        Maximum: MathF.PI,
        MaximumOpen: true,
        Minimum: CameraSnapshot.MinFieldOfViewRadians
    ), Why: "a camera cannot be built outside it");
    /// <summary>A blend op's weight: zero resolves its first program, one its second.</summary>
    public static WorldValueField BlendWeight { get; } = new(typeof(WorldCameraProgramOp.Blend), nameof(WorldCameraProgramOp.Blend.Weight), WorldValueDomain.Unit);
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
        IndirectLights, IndirectEmission, IndirectScreens, IndirectSky, IndirectFeedback, IndirectIntensity, IndirectContact,
        LightBounce,
        DirectionalIlluminance,
        DirectionalTemperature,
        DirectionalAngularRadius,
        PanelIntensity,
        PanelBlur,
        EnvironmentAmbient,
        EnvironmentReflection,
        RimLuminance,
        RimPower,
        PointIntensity,
        PointPower,
        PointTemperature,
        SpotIntensity,
        SpotPower,
        SpotTemperature,
        SpotAngle,
        SpotInnerAngle,
        SkyLayerLuminance,
        FogLuminance,
        MediumLuminance,
        OccluderRadius,
        OccluderWeight,
        CurvatureCavity,
        CurvatureRim,
        CurvatureInk,
        CurvatureInkLow,
        CurvatureInkHigh,
        StopElevation,
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
        FogDensity,
        AirBase,
        AirFalloff,
        HazeAmount,
        HazeAnisotropy,
        MediumSurface,
        MediumExtinction,
        SkyLayerOpacity,
        SkyLayerTurn,
        SkyLayerTilt,
        AuroraIntensity,
        AuroraBase,
        AuroraHeight,
        AuroraFold,
        NoiseCoverage,
        PanoramaIntensity,
        ScrimAlpha,
        BloomHaloAlpha,
        BloomRingAlpha,
        BloomNeutralHaloAlpha,
        BloomNeutralRingAlpha,
        BloomHeldInsetAlpha,
        MarkerChipAlpha,
        MarkerRingAlpha,
        VolumeSoftness,
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
