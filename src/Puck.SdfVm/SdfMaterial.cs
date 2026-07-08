using System.Numerics;

namespace Puck.SdfVm;

/// <summary>One entry of the scene's material palette. Packed as TWO uvec4 words (see <see cref="SdfProgram"/>);
/// all-default new fields shade exactly like the albedo-only v1 material.</summary>
/// <param name="Albedo">The linear-RGB base color.</param>
/// <param name="Emissive">The self-illumination strength: <c>albedo * emissive</c> adds to the shaded color, so an
/// emissive surface glows through shadow and ambient falloff. 0 = none.</param>
/// <param name="Specular">The Blinn-Phong specular strength in [0, 1]. 0 = matte (pure lambert).</param>
/// <param name="Shininess">The Blinn-Phong exponent (highlight tightness); meaningful only when
/// <paramref name="Specular"/> is non-zero.</param>
public readonly record struct SdfMaterial(Vector3 Albedo, float Emissive = 0f, float Specular = 0f, float Shininess = 32f);
