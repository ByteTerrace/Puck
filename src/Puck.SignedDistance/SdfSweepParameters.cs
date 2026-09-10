using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The resolved, unscaled authoring parameters for one <see cref="SdfShapeType.Sweep"/> shape — the
/// document-independent form <see cref="SdfSolidGeometry.AppendScaledPrimitive"/> and
/// <see cref="SdfSolidGeometry.SweepReach"/> take, so neither references the authoring document types.</summary>
/// <param name="A">The curve's first control point, in creation units.</param>
/// <param name="B">The curve's middle control point.</param>
/// <param name="C">The curve's last control point.</param>
/// <param name="RadiusStart">The sweep radius at <c>t = 0</c>.</param>
/// <param name="RadiusEnd">The sweep radius at <c>t = 1</c>.</param>
/// <param name="Bulge">The mid-span radius bulge amplitude.</param>
/// <param name="Strands">The strand count, 1..<see cref="SdfProgramBuilder.MaxSweepStrands"/>.</param>
/// <param name="Twist">The strand orbit rate, in turns along the curve.</param>
/// <param name="StrandOffset">The strand orbit radius, in creation units.</param>
public readonly record struct SdfSweepParameters(Vector3 A, Vector3 B, Vector3 C, float RadiusStart, float RadiusEnd, float Bulge, int Strands, float Twist, float StrandOffset);
