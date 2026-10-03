using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

/// <summary>The kind of one <see cref="SdfLight"/>.</summary>
public enum SdfLightKind : uint {
    /// <summary>A Lambert directional light. <see cref="SdfLight.Param"/> is the penumbra half-slope (the tangent of
    /// the light's angular radius). The frame's shadow slots select which directional lights march; every
    /// directional receives its own shadow visibility.</summary>
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
    /// direction, both scaled by ambient occlusion like every non-shadow light. No shadow march: a point light never
    /// occludes and is never occluded.</summary>
    Point = 3,
    /// <summary>A bounded attenuation field with position, radius, and weight in [0, 1].</summary>
    Occluder = 4,
}
/// <summary>
/// One light of the lit path, and one record of the lights table the kernels read (<c>sdfLights</c>, a World-group
/// structured buffer of <see cref="SdfLights.MaxLights"/> records): its public fields are the record's layout, whose HLSL
/// declaration <c>puck shaders generate</c> writes from this type, so the kernels read each field by name and no lane is
/// numbered by hand. The tables pack a frame's <see cref="SdfLights"/> record for record, with a directional's direction
/// normalized (<see cref="SdfLights.Pack"/>).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public record struct SdfLight {
    /// <summary>A directional's direction from the surface toward the light, any nonzero length (normalized on
    /// upload); a point's or an occluder's world-space POSITION instead (see <see cref="SdfLightKind.Point"/>).</summary>
    [FieldOffset(0)] public Vector3 Direction;
    /// <summary>The strength; a hemisphere's floor.</summary>
    [FieldOffset(12)] public float Weight;
    /// <summary>The linear RGB color.</summary>
    [FieldOffset(16)] public Vector3 Color;
    /// <summary>What the light is.</summary>
    [FieldOffset(28)] public SdfLightKind Kind;
    /// <summary>The kind's second scalar: penumbra half-slope, hemisphere gradient, rim exponent, or a point's or an
    /// occluder's radius.</summary>
    [FieldOffset(32)] public float Param;
    /// <summary>One when the directional participates in a stable shadow slot or active handoff, zero otherwise.</summary>
    [FieldOffset(36)] public uint Shadows;
    /// <summary>A point's or an occluder's dynamic-transform slot its position is read from every frame, or
    /// <see cref="SdfProgram.NoDynamicTransformSlot"/> for the static authored position in <see cref="Direction"/>;
    /// zero for every other kind.</summary>
    [FieldOffset(40)] public int DynamicSlot;

    /// <summary>Creates a light.</summary>
    /// <param name="Kind">What the light is.</param>
    /// <param name="Direction">A directional's direction toward the light, or a point's or an occluder's
    /// position.</param>
    /// <param name="Color">The linear RGB color.</param>
    /// <param name="Weight">The strength; a hemisphere's floor.</param>
    /// <param name="Param">The kind's second scalar.</param>
    /// <param name="Shadows">A directional only: whether this light may cast a shadow; the frame's slots select its march.</param>
    /// <param name="DynamicSlot">A point or an occluder only: the dynamic-transform slot its position rides, or
    /// <see cref="SdfProgram.NoDynamicTransformSlot"/>. Packed as zero for every other kind.</param>
    public SdfLight(SdfLightKind Kind, Vector3 Direction, Vector3 Color, float Weight, float Param, bool Shadows, int DynamicSlot = SdfProgram.NoDynamicTransformSlot) {
        this.Kind = Kind;
        this.Direction = Direction;
        this.Color = Color;
        this.Weight = Weight;
        this.Param = Param;
        this.Shadows = (Shadows ? 1u : 0u);
        this.DynamicSlot = (IsPositional(kind: Kind) ? DynamicSlot : 0);
    }

    /// <summary>Gets whether the light may cast a shadow: a directional with <see cref="Shadows"/> set.</summary>
    public readonly bool CastsShadow => ((Shadows != 0u) && (Kind == SdfLightKind.Directional));

    /// <summary>Returns whether a light of a kind sits at a position its <see cref="DynamicSlot"/> may ride: a point or an
    /// occluder.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> for <see cref="SdfLightKind.Point"/> and <see cref="SdfLightKind.Occluder"/>.</returns>
    public static bool IsPositional(SdfLightKind kind) =>
        (kind is SdfLightKind.Point or SdfLightKind.Occluder);
}
