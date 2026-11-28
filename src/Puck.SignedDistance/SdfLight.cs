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
    /// <summary>A world occluder row's place in the lights table, which lights nothing: its weight is zero, so no view
    /// selects it, and the occluder it authors is a sphere of the occluders table (<see cref="SdfOccluders"/>).</summary>
    Occluder = 3,
    /// <summary>A tube (line) light, such as a fluorescent or neon tube: a straight segment whose every length element
    /// emits alike in every direction, <see cref="SdfLight.Weight"/> its luminous intensity per metre over π
    /// (<see cref="SdfPhotometry.PositionalWeight"/> of the candela per metre). <see cref="SdfLight.Direction"/> carries
    /// the segment's midpoint, <see cref="SdfLight.SpotAxis"/> the vector from the midpoint to its end (half the segment)
    /// and <see cref="SdfLight.SpotOffset"/> the tube's radius; <see cref="SdfLight.Param"/> is its range measured from the
    /// nearest point of the segment (<see cref="SdfPhotometry.LineRangeOf"/>). A surface receives the segment's exact
    /// vector irradiance (the integral of cos θ / r² along it, unclipped by the horizon: Karis 2013, "Real Shading in
    /// Unreal Engine 4"), a specular lobe from the segment's point nearest the reflected ray with the sphere and line
    /// energy normalizations, and the point light's window at its nearest distance. A long tube is a chain of segments,
    /// each one record, so the light culling keeps only the segments near what it shades. A static one is traced into
    /// the irradiance volume as a point at its midpoint carrying the segment's whole intensity
    /// (<see cref="SdfLight.IsTraced"/>).</summary>
    Line = 4,
    /// <summary>A screen's or an emitter face's area light (<see cref="SdfScreenSurface"/>): a Lambertian face of area
    /// <c>A</c> that casts <c>E = L · A · cos / (d² + A / π)</c> on a surface before it, eased to zero at its range as a
    /// point light is, its luminance <c>L</c> the face's mean in nits, which the screens' emission pass reduces on the GPU
    /// from what the face shows (<c>sdfScreenLights</c>), so the record holds no colour. A residency's tables append one
    /// after the frame's lights for every resident screen slot (<c>SdfWorldTables.Lights</c>), so screens, emitter faces
    /// and every other light are one list, culled by one mask and one tile test and walked by one walk.
    /// <see cref="SdfLight.Direction"/> carries the face's center, <see cref="SdfLight.SpotAxis"/> its unit facing,
    /// <see cref="SdfLight.Weight"/> its area over π, <see cref="SdfLight.Param"/> its range (sized from the face's peak
    /// luminance, infinite for an opening) and <see cref="SdfLight.SpotOffset"/> the screen slot its luminance is read
    /// from, a whole number (<see cref="SdfLight.Face"/>). A static one (<see cref="SdfLight.Shadows"/> set) is traced by
    /// the irradiance volume as a point at its center for its visibility alone: it bounces nothing
    /// (<see cref="SdfLight.Bounce"/> zero), and a view occludes it by the probes' answer as it occludes a traced point
    /// light.</summary>
    Face = 5,
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
    /// upload); a point's world-space POSITION instead (see <see cref="SdfLightKind.Point"/>), and a line's midpoint (see
    /// <see cref="SdfLightKind.Line"/>).</summary>
    [FieldOffset(0)] public Vector3 Direction;
    /// <summary>The strength: a directional's illuminance over π, a point's or spot's luminous intensity over π, a rim's
    /// luminance; zero for an occluder row's place.</summary>
    [FieldOffset(12)] public float Weight;
    /// <summary>The linear RGB color, of unit luminance for a photometric light.</summary>
    [FieldOffset(16)] public Vector3 Color;
    /// <summary>What the light is.</summary>
    [FieldOffset(28)] public SdfLightKind Kind;
    /// <summary>The kind's second scalar: penumbra half-slope, rim exponent, or a point's or line's range.</summary>
    [FieldOffset(32)] public float Param;
    /// <summary>One when the directional participates in a stable shadow slot or active handoff, or when the point or
    /// spot light is static, so the irradiance volume traces it and occludes it (<see cref="IsTraced"/>); zero
    /// otherwise.</summary>
    [FieldOffset(36)] public uint Shadows;
    /// <summary>A point's or a line's dynamic-transform slot its position is read from every frame, or
    /// <see cref="SdfProgram.NoDynamicTransformSlot"/> for the static authored position in <see cref="Direction"/>;
    /// zero for every other kind.</summary>
    [FieldOffset(40)] public int DynamicSlot;
    /// <summary>The finite nonnegative gain of this light's diffuse contribution to indirect transport. Direct
    /// shading is unchanged; rim and attenuation-only lights have no diffuse contribution to scale.</summary>
    [FieldOffset(44)] public float Bounce;
    /// <summary>A spot's aim, the unit direction it shines along, scaled by its cone's scale
    /// (<see cref="SdfPhotometry.Cone"/>): the cone's attenuation is <c>saturate(dot(−toLight, SpotAxis) + SpotOffset)²</c>.
    /// Zero for a point, which shines alike in every direction. A line's half segment, from its midpoint to its end
    /// (<see cref="SdfLightKind.Line"/>).</summary>
    [FieldOffset(48)] public Vector3 SpotAxis;
    /// <summary>A spot's cone offset (<see cref="SpotAxis"/>); one for a point; a line's tube radius, in world
    /// units; a face's screen slot, a whole number (<see cref="SdfLightKind.Face"/>).</summary>
    [FieldOffset(60)] public float SpotOffset;

    /// <summary>Creates a light.</summary>
    /// <param name="Kind">What the light is.</param>
    /// <param name="Direction">A directional's direction toward the light, or a point's position.</param>
    /// <param name="Color">The linear RGB color.</param>
    /// <param name="Weight">The strength.</param>
    /// <param name="Param">The kind's second scalar.</param>
    /// <param name="Shadows">A directional: whether this light may cast a shadow; the frame's slots select its march. A
    /// point or spot: whether it is static, so the irradiance volume traces and occludes it.</param>
    /// <param name="DynamicSlot">A point or a line only: the dynamic-transform slot its position rides, or
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

    /// <summary>Returns a screen's or an emitter face's area light (<see cref="SdfLightKind.Face"/>).</summary>
    /// <param name="center">The face's center.</param>
    /// <param name="facing">The face's unit normal, toward the side it lights.</param>
    /// <param name="area">The face's area, in square world units.</param>
    /// <param name="range">The distance from its center past which it lights nothing; infinite for an opening.</param>
    /// <param name="slot">The screen slot the face holds, whose reduction gives its luminance.</param>
    /// <param name="traced">Whether the face is static, so the irradiance volume traces its visibility.</param>
    /// <returns>The face's record.</returns>
    public static SdfLight Face(Vector3 center, Vector3 facing, float area, float range, int slot, bool traced) => new(
        Bounce: 0f,
        Color: Vector3.One,
        Direction: center,
        Kind: SdfLightKind.Face,
        Param: range,
        Shadows: traced,
        Weight: (area / MathF.PI)
    ) {
        SpotAxis = facing,
        SpotOffset = slot,
    };

    /// <summary>Gets whether the light may cast a shadow: a directional with <see cref="Shadows"/> set.</summary>
    public readonly bool CastsShadow => ((Shadows != 0u) && (Kind == SdfLightKind.Directional));
    /// <summary>Gets whether the irradiance volume traces the light: a static point, spot, line or face light with
    /// <see cref="Shadows"/> set, whose visibility each probe records, so a view occludes it by the probes about a surface,
    /// and whose light the volume bounces by its <see cref="Bounce"/> (none for a face).</summary>
    public readonly bool IsTraced => ((Shadows != 0u) && IsRanged);
    /// <summary>Gets whether the light reaches only the sphere of its range about its position (a point, a spot or a
    /// face), or the capsule of its range about its segment (a line). Every other kind may light any surface.</summary>
    public readonly bool IsRanged => (Kind is SdfLightKind.Point or SdfLightKind.Line or SdfLightKind.Face);
    /// <summary>Gets the disc a light's analytic occluder shadows are taken toward (<c>sdfOccludersShadow</c>): a line's
    /// half segment or radius, whichever is wider, a face's disc of its area, and no width for every other kind.</summary>
    public readonly float SourceRadius => (Kind switch {
        SdfLightKind.Line => MathF.Max(x: SpotAxis.Length(), y: SpotOffset),
        SdfLightKind.Face => MathF.Sqrt(x: MathF.Max(x: Weight, y: 0f)),
        _ => 0f,
    });
    /// <summary>Gets whether the light's position follows a dynamic transform the host does not hold: a point or a line
    /// riding its <see cref="DynamicSlot"/>.</summary>
    public readonly bool RidesSlot => (IsPositional(kind: Kind) && (DynamicSlot != SdfProgram.NoDynamicTransformSlot));
    /// <summary>Gets a line's length, twice its half segment (<see cref="SpotAxis"/>); zero for every other kind.</summary>
    public readonly float LineLength => ((Kind == SdfLightKind.Line) ? (2f * SpotAxis.Length()) : 0f);

    /// <summary>Returns whether a light of a kind sits at a position its <see cref="DynamicSlot"/> may ride: a point or
    /// a line.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> for <see cref="SdfLightKind.Point"/> and <see cref="SdfLightKind.Line"/>.</returns>
    public static bool IsPositional(SdfLightKind kind) =>
        (kind is SdfLightKind.Point or SdfLightKind.Line);
}
