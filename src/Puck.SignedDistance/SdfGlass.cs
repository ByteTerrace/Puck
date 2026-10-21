using System.Numerics;

using Puck.Maths;

namespace Puck.SignedDistance;

/// <summary>A material's glass response: the surface transmits what lies behind it, refracted and absorbed over the
/// distance the view ray travels through the shape, and reflects its surroundings by Fresnel. A shape whose material
/// carries one bakes into a mesh group of its own and draws in the glass pass after the opaque image
/// (<c>SdfWorldPackage.WithGlass</c>); the material's <see cref="SdfMaterial.Albedo"/> is the tint and its
/// <see cref="SdfMaterial.Roughness"/> the frost.</summary>
/// <param name="Depth">The thickness, in world units, over which light through the glass takes on the material's
/// albedo as its colour: finite and positive. Absorption follows Beer–Lambert over the actual distance through the
/// shape, so a part twice this thick reads twice as deep.</param>
/// <param name="Ior">The index of refraction, finite in [<see cref="MinIor"/>, <see cref="MaxIor"/>]: 1 bends nothing
/// and reflects nothing at normal incidence, 1.5 is window glass, 1.49 acrylic.</param>
public readonly record struct SdfGlass(float Depth, float Ior) {
    /// <summary>The least index of refraction: air, which bends and reflects nothing.</summary>
    public const float MinIor = 1f;
    /// <summary>The greatest index of refraction a material may declare; diamond is 2.42.</summary>
    public const float MaxIor = 4f;
    /// <summary>The least transmittance a tint channel is read at when its absorption is derived: a black tint absorbs
    /// everything within a few depths rather than at once.</summary>
    public const float MinTransmittance = 1e-3f;

    /// <summary>Gets whether the glass is admissible: a finite, positive depth and an index of refraction in
    /// [<see cref="MinIor"/>, <see cref="MaxIor"/>].</summary>
    public bool IsValid => (float.IsFinite(f: Depth) && (Depth > 0f) && float.IsFinite(f: Ior) && (Ior >= MinIor) && (Ior <= MaxIor));

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
