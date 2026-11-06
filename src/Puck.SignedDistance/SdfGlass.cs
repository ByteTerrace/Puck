using System.Numerics;

using Puck.Maths;

namespace Puck.SignedDistance;

/// <summary>A material's glass response: the surface transmits what lies behind it, refracted and absorbed over the
/// distance the view ray travels through the shape, and reflects its surroundings by Fresnel. A shape whose material
/// carries one bakes into a mesh group of its own and draws in the glass pass after the opaque image
/// (<c>SdfWorldPackage.WithGlass</c>); the material's <see cref="SdfMaterial.Albedo"/> is the tint and its
/// <see cref="SdfMaterial.Roughness"/> the frost. The glass can also be a medium that scatters
/// (<paramref name="Scatter"/>, <paramref name="Scattering"/>): over a distance <c>d</c> through it, a share
/// <c>1 - exp(-Scattering * d)</c> of what it shows is the light arriving at its surface as a surface of the scatter
/// colour would show it, and the rest the light behind, so a deep body reads as its scatter colour and a shallow one
/// shows what lies behind. A contact (<paramref name="ContactReach"/>, <paramref name="ContactCoverage"/>) raises the
/// material's flow coverage (<see cref="SdfFlow.Coverage"/>) where the opaque surface behind the glass lies within the
/// reach of its surface: foam or a wet margin where water meets a step, a pier or a bank. The defaults are neither: a
/// clear, absorbing glass.</summary>
/// <param name="Depth">The thickness, in world units, over which light through the glass takes on the material's
/// albedo as its colour: finite and positive. Absorption follows Beer–Lambert over the actual distance through the
/// shape, so a part twice this thick reads twice as deep.</param>
/// <param name="Ior">The index of refraction, finite in [<see cref="MinIor"/>, <see cref="MaxIor"/>]: 1 bends nothing
/// and reflects nothing at normal incidence, 1.5 is window glass, 1.49 acrylic.</param>
/// <param name="Scatter">The medium's scatter colour, each channel in [0, 1]: the colour a deep body of it shows under
/// the light arriving at its surface.</param>
/// <param name="Scattering">The scattering coefficient, per world unit, finite and non-negative; zero scatters nothing.
/// It extinguishes the light through the glass as it adds its own, so the deeper the body the more of what it shows is
/// its own scattered light.</param>
/// <param name="ContactReach">The distance, in world units along the view ray from the glass surface to the opaque
/// surface behind it, over which the contact's coverage falls from <paramref name="ContactCoverage"/> to zero; in
/// [0, <see cref="MaxContactReach"/>], zero for no contact.</param>
/// <param name="ContactCoverage">The coverage the contact adds to the flow's where the opaque surface touches the glass
/// surface, in [0, 1]. A contact needs the material's flow to name the surface its coverage reveals
/// (<see cref="SdfMaterial.IsGlassValid"/>).</param>
/// <param name="ShadowDensity">The shadow extinction per world unit, finite and non-negative. Zero casts no shadow;
/// a light path of length d transmits exp(-ShadowDensity * d).</param>
public readonly record struct SdfGlass(float Depth, float Ior, Vector3 Scatter = default, float Scattering = 0f, float ContactReach = 0f, float ContactCoverage = 0f, float ShadowDensity = 0f) {
    /// <summary>The least index of refraction: air, which bends and reflects nothing.</summary>
    public const float MinIor = 1f;
    /// <summary>The greatest index of refraction a material may declare; diamond is 2.42.</summary>
    public const float MaxIor = 4f;
    /// <summary>The least transmittance a tint channel is read at when its absorption is derived: a black tint absorbs
    /// everything within a few depths rather than at once.</summary>
    public const float MinTransmittance = 1e-3f;
    /// <summary>The greatest contact reach, in world units: the material table packs the reach as a half float.</summary>
    public const float MaxContactReach = 1000f;

    /// <summary>Gets whether the glass is admissible: a finite, positive depth, an index of refraction in
    /// [<see cref="MinIor"/>, <see cref="MaxIor"/>], a scatter colour in [0, 1], a finite, non-negative scattering, a
    /// contact reach in [0, <see cref="MaxContactReach"/>], a contact coverage in [0, 1] and finite, non-negative shadow
    /// density.</summary>
    public bool IsValid =>
        float.IsFinite(f: Depth) && (Depth > 0f) && float.IsFinite(f: Ior) && (Ior >= MinIor) && (Ior <= MaxIor) &&
        IsTint(tint: Scatter) && float.IsFinite(f: Scattering) && (Scattering >= 0f) &&
        float.IsFinite(f: ContactReach) && (ContactReach >= 0f) && (ContactReach <= MaxContactReach) &&
        float.IsFinite(f: ContactCoverage) && (ContactCoverage >= 0f) && (ContactCoverage <= 1f) &&
        float.IsFinite(f: ShadowDensity) && (ShadowDensity >= 0f);
    /// <summary>Gets whether the glass has a contact: a positive reach and coverage.</summary>
    public bool HasContact => ((ContactReach > 0f) && (ContactCoverage > 0f));

    /// <summary>Returns the absorption per world unit of each channel for a tint: <c>-ln(tint) / Depth</c>, so that
    /// light through <see cref="Depth"/> of the glass is the tint, each channel floored at
    /// <see cref="MinTransmittance"/>. The kernels read it packed in the material table and apply
    /// <c>exp(-absorption * distance)</c>.</summary>
    /// <param name="tint">The transmitted colour at <see cref="Depth"/>, each channel in [0, 1].</param>
    /// <returns>The absorption per world unit.</returns>
    public Vector3 Absorption(Vector3 tint) {
        var clamped = Vector3.Clamp(value1: tint, min: new Vector3(value: MinTransmittance), max: Vector3.One);

        return (new Vector3(x: -MathF.Log(x: clamped.X), y: -MathF.Log(x: clamped.Y), z: -MathF.Log(x: clamped.Z)) / Depth);
    }
    /// <summary>Returns the dielectric reflectance at normal incidence the index gives, <c>((ior - 1) / (ior + 1))²</c>:
    /// 0.04 for window glass.</summary>
    /// <returns>The reflectance.</returns>
    public float NormalReflectance() {
        var ratio = ((Ior - 1f) / (Ior + 1f));

        return (ratio * ratio);
    }
    /// <summary>Returns whether a tint is admissible: finite, each channel in [0, 1].</summary>
    /// <param name="tint">The tint.</param>
    /// <returns><see langword="true"/> for an admissible tint.</returns>
    public static bool IsTint(Vector3 tint) => (VectorFunctions.IsFinite(vector: tint) && (tint.X >= 0f) && (tint.X <= 1f) && (tint.Y >= 0f) && (tint.Y <= 1f) && (tint.Z >= 0f) && (tint.Z <= 1f));
}
