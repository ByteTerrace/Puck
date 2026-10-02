using System.Text.Json.Serialization;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>The shared sky frame, aligned from world +Y by the shortest rotation. An antipodal up axis uses
/// world +X as its deterministic half-turn axis. Layer transforms compose inside this frame.</summary>
/// <param name="Up">The finite nonzero up direction. Absent uses world +Y.</param>
public sealed record WorldSkyFrame(BindableDirection? Up = null);

public abstract partial record WorldRenderSkyLayer {
    /// <summary>The affine blend operation applied at this layer's authored position. An absent value uses the
    /// kind's ordinary composition: additive emission for stars, coverage-over for field layers.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SdfSkyBlend? Blend { get; init; }
    /// <summary>The optional directional mask, evaluated before any kind-specific work.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldSkyMask? Mask { get; init; }
    /// <summary>The layer's orientation inside the sky frame.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldSkyTransform? Transform { get; init; }
    /// <summary>The optional named clock used by the layer's motion and reported by presentation inspection.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Clock { get; init; }
    /// <summary>The layer opacity in [0, 1]. Zero skips its kind before any hash or texture sample.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BindableScalar? Opacity { get; init; }
    /// <summary>The camera and lighting consumers that may draw the layer. Structural, never keyed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SdfSkyVisibility? Visibility { get; init; }
    /// <summary>The lowest quality tier at which the layer draws. Structural, never keyed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityTier? Tier { get; init; }
}

/// <summary>A directional sky mask. Exactly one elevation band or cone is authored.</summary>
/// <param name="Elevation">The lower and upper elevation angles, in radians, as exactly two values.</param>
/// <param name="Cone">The alternative directional cone.</param>
public sealed record WorldSkyMask(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BindableAngle>? Elevation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSkyCone? Cone = null);

/// <summary>A sky cone whose radius is an angular half-width.</summary>
/// <param name="Toward">The cone's central direction in the layer's local frame, shared with its kind evaluation.</param>
/// <param name="Radius">Its angular half-width in radians, in (0, π].</param>
public sealed record WorldSkyCone(BindableDirection Toward, BindableAngle Radius);

/// <summary>The layer's local orientation, applied in yaw, pitch, roll order inside the sky frame. Its rate is
/// integrated by the shared compiled-rate resolver rather than accumulated by frames.</summary>
/// <param name="Yaw">Rotation about the frame's up axis, in radians.</param>
/// <param name="Pitch">Rotation about the yawed right axis, in radians.</param>
/// <param name="Roll">Rotation about the yawed and pitched forward axis, in radians.</param>
/// <param name="Rate">The additional yaw rate in radians per second.</param>
public sealed record WorldSkyTransform(BindableAngle? Yaw = null, BindableAngle? Pitch = null,
    BindableAngle? Roll = null, BindableScalar? Rate = null);
