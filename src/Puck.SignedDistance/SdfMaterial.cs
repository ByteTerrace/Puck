using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>One entry of the scene's material palette. Packed as TWO uvec4 words (see <see cref="SdfProgram"/>);
/// all-default new fields shade exactly like the albedo-only v1 material.</summary>
/// <param name="Albedo">The linear-RGB base color; every component must be finite and non-negative when admitted to
/// a builder or program.</param>
/// <param name="Emissive">The self-illumination strength: <c>albedo * emissive</c> adds to the shaded color, so an
/// emissive surface glows through shadow and ambient falloff. Must be finite and non-negative; 0 = none.</param>
/// <param name="Specular">The finite, non-negative Blinn-Phong specular strength. 0 = matte (pure lambert).</param>
/// <param name="Shininess">The finite, non-negative Blinn-Phong exponent (highlight tightness); meaningful only when
/// <paramref name="Specular"/> is non-zero.</param>
public readonly record struct SdfMaterial(Vector3 Albedo, float Emissive = 0f, float Specular = 0f, float Shininess = 32f);
