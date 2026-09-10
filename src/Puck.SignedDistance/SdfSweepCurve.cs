using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>Represents one <see cref="SdfShapeType.Sweep"/> instruction's own control points and radius endpoints —
/// the host-side twin of the packed side table <see cref="SdfProgram.Words"/> carries (see
/// <see cref="SdfShapeType.Sweep"/>'s remarks), keyed by that instruction's index so
/// <see cref="Puck.SignedDistance.Queries.SdfFieldEvaluator"/> can mirror the field without decoding the packed
/// table offset.</summary>
/// <param name="InstructionIndex">The index, in the program's instruction stream, of the
/// <see cref="SdfShapeType.Sweep"/> instruction this curve belongs to.</param>
/// <param name="A">The curve's first (start) control point, in the shape's local frame.</param>
/// <param name="B">The curve's middle control point.</param>
/// <param name="C">The curve's last (end) control point.</param>
/// <param name="RadiusStart">The sweep radius at <c>t = 0</c>.</param>
/// <param name="RadiusEnd">The sweep radius at <c>t = 1</c>.</param>
/// <param name="Bulge">The mid-span radius bulge amplitude, added by <c>bulge·sin(π·t)^0.65</c>.</param>
public readonly record struct SdfSweepCurve(int InstructionIndex, Vector3 A, Vector3 B, Vector3 C, float RadiusStart, float RadiusEnd, float Bulge);
