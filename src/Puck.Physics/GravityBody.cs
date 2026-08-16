using Puck.Maths;

namespace Puck.Physics;

/// <summary>A point source and target in a gravitational solve.</summary>
/// <param name="Position">The body's deterministic world-space position.</param>
/// <param name="Mass">The body's non-negative gravitational mass. A zero-mass body is a target that contributes no field.</param>
public readonly record struct GravityBody(FixedVector3 Position, FixedQ4816 Mass);
