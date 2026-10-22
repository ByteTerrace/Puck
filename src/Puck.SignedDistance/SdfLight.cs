using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

/// <summary>The kind of one <see cref="SdfLight"/>.</summary>
public enum SdfLightKind : uint {
    /// <summary>A Lambert directional light. <see cref="SdfLight.Weight"/> is its illuminance over π
    /// (<see cref="SdfPhotometry.DirectionalWeight"/>) and <see cref="SdfLight.Param"/> the penumbra half-slope (the tangent
    /// of the light's angular radius). The frame's shadow slots select which directional lights march; every directional
    /// receives its own shadow visibility and lights through its own GGX lobe.</summary>
    Directional = 0,
    /// <summary>A view-dependent silhouette brighten added after the material shade:
    /// <c>weight · color · pow(1 − saturate(dot(normal, −ray)), param)</c>, its weight a luminance in nits.</summary>
    Rim = 1,
    /// <summary>A point or spot light: its luminous intensity over π (<see cref="SdfLight.Weight"/>,
    /// <see cref="SdfPhotometry.PositionalWeight"/>) under the windowed inverse square
    /// <c>(1 − (d / r)^4)² / d²</c>, exactly zero at the range <c>r</c> (<see cref="SdfLight.Param"/>,
    /// <see cref="SdfPhotometry.RangeOf"/>), and a spot's cone (<see cref="SdfLight.SpotAxis"/>,
    /// <see cref="SdfLight.SpotOffset"/>). <see cref="SdfLight.Direction"/> carries the light's world-space position
    /// rather than a direction; <see cref="SdfLight.DynamicSlot"/> optionally rides a dynamic transform so the position
    /// follows an anchored shape. Lambert diffuse plus the material's GGX response, both scaled by ambient occlusion. A
    /// static light (<see cref="SdfLight.Shadows"/> set, <see cref="SdfLight.IsTraced"/>) is traced into the irradiance
    /// volume, which bounces its light and occludes it by the probes' visibility of it; a moving one lights through every
    /// wall and bounces nothing.</summary>
    Point = 2,
    /// <summary>A bounded attenuation field with position, radius, and weight in [0, 1].</summary>
    Occluder = 3,
}
/// <summary>
/// One light of the lit path, and one record of the lights table the kernels read (<c>sdfLights</c>, a World-group
/// structured buffer of <see cref="SdfLights.MaxLights"/> records): its public fields are the record's layout, whose HLSL
/// declaration <c>puck shaders generate</c> writes from this type, so the kernels read each field by name and no lane is
/// numbered by hand. The tables pack a frame's <see cref="SdfLights"/> record for record, with a directional's direction
/// normalized (<see cref="SdfLights.Pack"/>). Every radiometric value is photometric and absolute: the frame shades in
/// luminance (nits) and each view's exposure maps it to the display (<see cref="SdfPhotometry"/>).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public record struct SdfLight {
    /// <summary>A directional's direction from the surface toward the light, any nonzero length (normalized on
    /// upload); a point's or an occluder's world-space POSITION instead (see <see cref="SdfLightKind.Point"/>).</summary>
    [FieldOffset(0)] public Vector3 Direction;
    /// <summary>The strength: a directional's illuminance over π, a point's or spot's luminous intensity over π, a rim's
    /// luminance, an occluder's attenuation in [0, 1].</summary>
    [FieldOffset(12)] public float Weight;
    /// <summary>The linear RGB color, of unit luminance for a photometric light.</summary>
    [FieldOffset(16)] public Vector3 Color;
    /// <summary>What the light is.</summary>
    [FieldOffset(28)] public SdfLightKind Kind;
    /// <summary>The kind's second scalar: penumbra half-slope, rim exponent, a point's range or an occluder's
    /// radius.</summary>
    [FieldOffset(32)] public float Param;
    /// <summary>One when the directional participates in a stable shadow slot or active handoff, or when the point or
    /// spot light is static, so the irradiance volume traces it and occludes it (<see cref="IsTraced"/>); zero
    /// otherwise.</summary>
    [FieldOffset(36)] public uint Shadows;
    /// <summary>A point's or an occluder's dynamic-transform slot its position is read from every frame, or
    /// <see cref="SdfProgram.NoDynamicTransformSlot"/> for the static authored position in <see cref="Direction"/>;
    /// zero for every other kind.</summary>
    [FieldOffset(40)] public int DynamicSlot;
    /// <summary>The finite nonnegative gain of this light's diffuse contribution to indirect transport. Direct
    /// shading is unchanged; rim and attenuation-only lights have no diffuse contribution to scale.</summary>
    [FieldOffset(44)] public float Bounce;
    /// <summary>A spot's aim, the unit direction it shines along, scaled by its cone's scale
    /// (<see cref="SdfPhotometry.Cone"/>): the cone's attenuation is <c>saturate(dot(−toLight, SpotAxis) + SpotOffset)²</c>.
    /// Zero for a point, which shines alike in every direction.</summary>
    [FieldOffset(48)] public Vector3 SpotAxis;
    /// <summary>A spot's cone offset (<see cref="SpotAxis"/>); one for a point.</summary>
    [FieldOffset(60)] public float SpotOffset;

    /// <summary>Creates a light.</summary>
    /// <param name="Kind">What the light is.</param>
    /// <param name="Direction">A directional's direction toward the light, or a point's or an occluder's
    /// position.</param>
    /// <param name="Color">The linear RGB color.</param>
    /// <param name="Weight">The strength.</param>
    /// <param name="Param">The kind's second scalar.</param>
    /// <param name="Shadows">A directional: whether this light may cast a shadow; the frame's slots select its march. A
    /// point or spot: whether it is static, so the irradiance volume traces and occludes it.</param>
    /// <param name="DynamicSlot">A point or an occluder only: the dynamic-transform slot its position rides, or
    /// <see cref="SdfProgram.NoDynamicTransformSlot"/>. Packed as zero for every other kind.</param>
    /// <param name="Bounce">The indirect diffuse gain, one by default.</param>
    public SdfLight(SdfLightKind Kind, Vector3 Direction, Vector3 Color, float Weight, float Param, bool Shadows, int DynamicSlot = SdfProgram.NoDynamicTransformSlot, float Bounce = 1f) {
        this.Kind = Kind;
        this.Direction = Direction;
        this.Color = Color;
        this.Weight = Weight;
        this.Param = Param;
        this.Shadows = (Shadows ? 1u : 0u);
        this.DynamicSlot = (IsPositional(kind: Kind) ? DynamicSlot : 0);
        this.Bounce = Bounce;
        SpotAxis = Vector3.Zero;
        SpotOffset = 1f;
    }

    /// <summary>Gets whether the light may cast a shadow: a directional with <see cref="Shadows"/> set.</summary>
    public readonly bool CastsShadow => ((Shadows != 0u) && (Kind == SdfLightKind.Directional));
    /// <summary>Gets whether the irradiance volume traces the light: a static point or spot light, with
    /// <see cref="Shadows"/> set, whose light the volume bounces and whose visibility each probe records, so a view
    /// occludes it by the probes about a surface.</summary>
    public readonly bool IsTraced => ((Shadows != 0u) && (Kind == SdfLightKind.Point));
    /// <summary>Gets whether the light reaches only the sphere of its range about its position: a point or a spot. Every
    /// other kind may light any surface.</summary>
    public readonly bool IsRanged => (Kind == SdfLightKind.Point);

    /// <summary>Returns whether a light of a kind sits at a position its <see cref="DynamicSlot"/> may ride: a point or an
    /// occluder.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> for <see cref="SdfLightKind.Point"/> and <see cref="SdfLightKind.Occluder"/>.</returns>
    public static bool IsPositional(SdfLightKind kind) =>
        (kind is SdfLightKind.Point or SdfLightKind.Occluder);
}
