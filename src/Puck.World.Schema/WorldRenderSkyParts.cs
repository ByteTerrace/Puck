using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>How a sky layer composes over the colour beneath it. Each is affine in that colour, so a run of field layers
/// composes into one map however they blend.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldSkyBlend>))]
public enum WorldSkyBlend {
    /// <summary>The layer covers the colour beneath by its alpha: a·c + (1 − a)·d.</summary>
    [JsonStringEnumMemberName("over")]
    Over = 0,
    /// <summary>The layer adds its colour, weighted by its alpha: d + a·c.</summary>
    [JsonStringEnumMemberName("add")]
    Add = 1,
    /// <summary>The layer scales the colour beneath toward its colour by its alpha.</summary>
    [JsonStringEnumMemberName("multiply")]
    Multiply = 2,
    /// <summary>The layer lifts the colour beneath toward one by its colour and alpha: c + (1 − c)·d at full alpha.</summary>
    [JsonStringEnumMemberName("screen")]
    Screen = 3,
}
/// <summary>Who sees a sky layer.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldSkyVisibility>))]
public enum WorldSkyVisibility {
    /// <summary>The camera alone: the sky and composite passes draw it, the environment map leaves it out.</summary>
    [JsonStringEnumMemberName("camera")]
    Camera = 1,
    /// <summary>The lighting alone: the environment map (the atmosphere's in-scatter) draws it, the camera never sees it.</summary>
    [JsonStringEnumMemberName("lighting")]
    Lighting = 2,
    /// <summary>The camera and the lighting.</summary>
    [JsonStringEnumMemberName("both")]
    Both = 3,
}
/// <summary>A sky quality tier: the lowest tier a layer draws at, and the sky's current tier (<c>world.sky-quality</c>,
/// a quality preset's <c>sky</c> row), below which a layer writes no entry and below <see cref="High"/> of which each kind
/// draws its reduced form.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldSkyTier>))]
public enum WorldSkyTier {
    /// <summary>The floor tier: clouds take one thickness tap and three octaves, shaded flat; stars do not twinkle.</summary>
    [JsonStringEnumMemberName("low")]
    Low = 0,
    /// <summary>The middle tier: clouds keep their lighting at three octaves.</summary>
    [JsonStringEnumMemberName("medium")]
    Medium = 1,
    /// <summary>The fullest sky.</summary>
    [JsonStringEnumMemberName("high")]
    High = 2,
}
/// <summary>The shape a <see cref="WorldRenderSkyLayer.Pattern"/> paints.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldSkyPatternShape>))]
public enum WorldSkyPatternShape {
    /// <summary>A checkerboard of the two colours.</summary>
    [JsonStringEnumMemberName("checker")]
    Checker = 0,
    /// <summary>Bands of the second colour across the first, along elevation.</summary>
    [JsonStringEnumMemberName("stripes")]
    Stripes = 1,
    /// <summary>Lines of the second colour over the first, along azimuth and elevation.</summary>
    [JsonStringEnumMemberName("grid")]
    Grid = 2,
}
/// <summary>How a <see cref="WorldRenderSkyLayer.Panorama"/> maps a direction to its image.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldSkyProjection>))]
public enum WorldSkyProjection {
    /// <summary>Longitude across, latitude down: the image's top row is the zenith.</summary>
    [JsonStringEnumMemberName("equirect")]
    Equirect = 0,
    /// <summary>The octahedral projection with the pole at the zenith.</summary>
    [JsonStringEnumMemberName("octahedral")]
    Octahedral = 1,
}
/// <summary>The sky frame: which way is up for the sky. Every layer's elevation, mask and transform is measured from it;
/// a body's disc sits where its light shines from, in world space.</summary>
/// <param name="Up">The sky's up direction in world space, any nonzero length. Absent is world +y.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRenderSkyFrame(DocumentVector3? Up = null);
/// <summary>Where a sky layer draws: an elevation band of the sky frame, or a cone about a direction. Exactly one of
/// <paramref name="Band"/> and <paramref name="Cone"/>.</summary>
/// <param name="Band">The band's lowest and highest elevation in radians, two angles in <c>[−π/2, π/2]</c>,
/// ascending.</param>
/// <param name="Cone">The cone.</param>
/// <param name="Feather">The width of the mask's edge, an angle in radians. Absent is a hard edge.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRenderSkyMask(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<double>? Band = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderSkyCone? Cone = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Feather = null
);
/// <summary>A cone mask: the directions within <paramref name="Spread"/> of <paramref name="Toward"/>, in the sky
/// frame.</summary>
/// <param name="Toward">The cone's axis in the sky frame, any nonzero length.</param>
/// <param name="Spread">The cone's angular radius in radians, in <c>(0, π]</c>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRenderSkyCone(DocumentVector3 Toward, double Spread);
/// <summary>A sky layer's own transform about the sky frame: a turn about its up, then a tilt about its right axis. Each
/// angle may bind or key on a clock, so a layer turns over a day.</summary>
/// <param name="Turn">The turn about the sky frame's up. Absent is none.</param>
/// <param name="Tilt">The tilt about the sky frame's right axis. Absent is none.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRenderSkyTransform(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableAngle? Turn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableAngle? Tilt = null
);
/// <summary>The <c>texture</c> body shape: the image a diegetic screen shows, drawn across a body's disc, as a panorama
/// samples its screen.</summary>
/// <param name="Screen">The screen's surface index (<see cref="WorldScreen.Index"/>), a screen the world declares.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRenderSkyTexture(int Screen);
