using System.Numerics;
using Puck.SdfVm;
using Puck.World.Server;

namespace Puck.World;

/// <summary>A nearest-surface measurement with certified local mesh correspondence.</summary>
/// <param name="Surface">Closest retained triangle point.</param>
/// <param name="Normal">Triangle normal.</param>
/// <param name="Distance">Unsigned Euclidean separation in metres.</param>
/// <param name="Budget">Precision of that triangle and the queried collision witness.</param>
public sealed record WorldDiagnosticMeshSample(Vector3 Surface, Vector3 Normal, double Distance, WorldDiagnosticBudget Budget) {
    /// <summary>Whether the collision witness is on the inward side of the retained outward mesh normal.</summary>
    public bool Inside { get; init; }
    /// <summary>Whether the certified separation violates the boundary agreement contract.</summary>
    public bool IsRed => (Distance > (Budget.Total + (Inside ? Budget.ConservativeInset : 0)));
}
/// <summary>Shared correspondence gate for collider and grounded-support mesh diagnostics.</summary>
public static class WorldDiagnosticMeshQuery {
    /// <summary>Measures the nearest retained surface in a precision-derived neighbourhood. A missing local match is
    /// unknown, not a violation or a clean result. Four times the scene's precision bounds the search work and coverage;
    /// it never enlarges the returned witness's error budget. A computed conservative inset applies only on the
    /// inward side of the mesh. No collider extent, fillet or snap reach is an allowance.</summary>
    /// <param name="scene">The matching, settled retained draw scene.</param>
    /// <param name="point">Collision boundary or solver support witness.</param>
    /// <param name="budget">Collision and arithmetic precision, without a bake term.</param>
    /// <param name="sceneBakeError">Largest half-cell diagonal in the retained scene, used only for search coverage.</param>
    /// <returns>A certified measurement, or null when correspondence is unknown.</returns>
    public static WorldDiagnosticMeshSample? Measure(SdfMeshIrradianceScene scene, Vector3 point,
        WorldDiagnosticBudget budget, double sceneBakeError) {
        var radius = ((float)(4 * ((budget.Total + budget.ConservativeInset) + sceneBakeError)));

        if (!scene.TryNearestSurface(bakeHalfDiagonal: out var hitError, normal: out var normal, point: point, radius: radius, surface: out var surface)) { return null; }
        return new(surface, normal, Vector3.Distance(value1: point, value2: surface), budget with { BakeHalfDiagonal = hitError }) {
            Inside = (Vector3.Dot(vector1: (point - surface), vector2: normal) < 0),
        };
    }
}
