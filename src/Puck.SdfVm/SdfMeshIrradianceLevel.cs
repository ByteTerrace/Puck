using System.Numerics;

namespace Puck.SdfVm;

/// <summary>One level of the irradiance volume's table (<see cref="SdfMeshIrradiance.LevelsOf"/>): the cell size its
/// probes stand apart by along each axis, its window's probes along each axis, and whether its surface maps mark sheets.</summary>
/// <param name="Spacing">The distance between neighbouring probes along each axis, in metres.</param>
/// <param name="Dims">The probes along each axis of the level's window.</param>
/// <param name="Sheets">Whether the level's trace marks the hits on sheets (<see cref="SdfMeshProbeDepth.Surfaces"/>),
/// whose taps then close a receiver at almost any share (<see cref="SdfMeshProbeCoverage"/>). A coarse level's taps are
/// metres wide, so its tap on a slab or a wall is a partition between spaces. The finest level marks none: its taps are
/// about half a metre wide and resolve a slab themselves, while a minority tap of theirs on a thin finite object (a
/// railing, a hanging sign) lies beside the receiver's segment and must not close it.</param>
public readonly record struct SdfMeshIrradianceLevel(Vector3 Spacing, (int X, int Y, int Z) Dims, bool Sheets) {
    /// <summary>Gets the probes the level's window holds.</summary>
    public int Count => ((Dims.X * Dims.Y) * Dims.Z);
}
