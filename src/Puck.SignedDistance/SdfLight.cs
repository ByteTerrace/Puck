using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The kind of one <see cref="SdfLight"/>.</summary>
public enum SdfLightKind : byte {
    /// <summary>A Lambert directional light. <see cref="SdfLight.Param"/> is the penumbra half-slope (the tangent of
    /// the light's angular radius); the one light with <see cref="SdfLight.Shadows"/> set drives the soft-shadow
    /// march, every other directional is scaled by ambient occlusion instead.</summary>
    Directional = 0,
    /// <summary>A hemisphere ambient: <see cref="SdfLight.Weight"/> is the floor, <see cref="SdfLight.Param"/> the
    /// gradient scaling the surface normal's Y. Scaled by ambient occlusion.</summary>
    Hemisphere = 1,
    /// <summary>A view-dependent silhouette brighten added after the material shade:
    /// <c>weight · color · pow(1 − saturate(dot(normal, −ray)), param)</c>.</summary>
    Rim = 2,
    /// <summary>A point light with inverse-square falloff and a soft core:
    /// <c>intensity = weight / (1 + (d / r)^2)</c>, <c>r</c> = <see cref="SdfLight.Param"/>. <see cref="SdfLight.Direction"/>
    /// carries the light's WORLD-SPACE POSITION rather than a direction; <see cref="SdfLight.DynamicSlot"/>
    /// optionally rides a dynamic transform so the position follows an anchored shape every frame instead of the
    /// authored (possibly stale) position. Lambert diffuse plus the material's GGX response from the point
    /// direction, both scaled by ambient occlusion like every non-shadow light. No shadow march in v1 — a point
    /// light never occludes and is never occluded.</summary>
    Point = 3,
    /// <summary>A bounded attenuation field with position, radius, and weight in [0, 1].</summary>
    Occluder = 4,
}
/// <summary>One light of the lit path.</summary>
/// <param name="Kind">What the light is.</param>
/// <param name="Direction">Directional: from the surface toward the light, any nonzero length (normalized on
/// upload). Point: the light's world-space POSITION instead (see <see cref="SdfLightKind.Point"/>).</param>
/// <param name="Color">The linear RGB color.</param>
/// <param name="Weight">The strength; a hemisphere's floor.</param>
/// <param name="Param">The kind's second scalar: penumbra half-slope, hemisphere gradient, rim exponent, or (point)
/// the falloff radius.</param>
/// <param name="Shadows">Directional only: whether this light drives the soft-shadow march.</param>
/// <param name="DynamicSlot">Point only: the dynamic-transform slot its position is read from every frame, or −1 for
/// the static authored position in <paramref name="Direction"/>. Ignored (packed as 0) for every other kind.</param>
public readonly record struct SdfLight(SdfLightKind Kind, Vector3 Direction, Vector3 Color, float Weight, float Param, bool Shadows, int DynamicSlot = -1);
/// <summary>One analytic studio-reflection softbox — see <c>worldStudioReflection</c> in shade/sdf-lighting.hlsli.</summary>
/// <param name="Direction">From a lit surface toward the softbox, any nonzero length (normalized on upload).</param>
/// <param name="Color">The linear RGB color.</param>
/// <param name="Weight">The strength.</param>
/// <param name="Size">The angular half-extent proxy (width, height) the falloff widens by.</param>
/// <param name="Blur">Additional falloff softening, in the same units as <paramref name="Size"/>; 0 = none.</param>
public readonly record struct SdfSoftbox(Vector3 Direction, Vector3 Color, float Weight, Vector2 Size, float Blur);
