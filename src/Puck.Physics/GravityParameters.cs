using Puck.Maths;

namespace Puck.Physics;

/// <summary>The physical constants shared by every interaction in one gravitational solve.</summary>
/// <param name="GravitationalConstant">The non-negative proportionality constant applied to every source mass.</param>
/// <param name="SofteningLength">A positive Plummer softening length. Its representable square must be non-zero.</param>
public readonly record struct GravityParameters(
    FixedQ4816 GravitationalConstant,
    FixedQ4816 SofteningLength
);
