using System.Numerics;

namespace Puck.SdfVm;

/// <summary>One level of the irradiance volume's table (<see cref="SdfMeshIrradiance.LevelsOf"/>): the cell size its
/// probes stand apart by along each axis and its window's probes along each axis.</summary>
/// <param name="Spacing">The distance between neighbouring probes along each axis, in metres.</param>
/// <param name="Dims">The probes along each axis of the level's window.</param>
public readonly record struct SdfMeshIrradianceLevel(Vector3 Spacing, (int X, int Y, int Z) Dims) {
    /// <summary>Gets the probes the level's window holds.</summary>
    public int Count => ((Dims.X * Dims.Y) * Dims.Z);
}
