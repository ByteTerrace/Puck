using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// The bake kernel's gradient stencil, for every CPU answer of a bake lattice (the offline bake, the field levels and the
/// laws' kernel stand-ins): four tetrahedral taps about a sample, a sixty-fourth of the lattice's finest cell out, whose
/// values combine into the gradient in field units per world unit. A sync pair with <c>SdfBakeTaps</c> in
/// <c>passes/sdf-mesh-bake.comp.hlsl</c>.
/// </summary>
public static class SdfMeshBakeTaps {
    /// <summary>The share of a lattice's finest cell a tap lies out from its sample, along each axis.</summary>
    public const float SpacingShare = 0.015625f;

    /// <summary>Gets the taps' directions, each a corner of the unit cube, one per tetrahedron vertex.</summary>
    public static ReadOnlySpan<Vector3> Offsets => TapOffsets;

    private static readonly Vector3[] TapOffsets = [new(x: 1f, y: -1f, z: -1f), new(x: -1f, y: -1f, z: 1f), new(x: -1f, y: 1f, z: -1f), new(x: 1f, y: 1f, z: 1f)];

    /// <summary>Returns the spacing of a lattice's taps from their sample.</summary>
    /// <param name="cell">The lattice's spacing along each axis, in metres.</param>
    /// <returns>The tap spacing, in metres.</returns>
    public static float Spacing(Vector3 cell) => (SpacingShare * MathF.Min(x: cell.X, y: MathF.Min(x: cell.Y, y: cell.Z)));

    /// <summary>Returns a field's gradient at a point from its values at the four taps a spacing out.</summary>
    /// <param name="field">The field, read once per tap.</param>
    /// <param name="point">The sample, in the field's frame, in metres.</param>
    /// <param name="spacing">The taps' spacing from the sample along each axis, in metres; positive.</param>
    /// <returns>The gradient, in field units per metre.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public static Vector3 Gradient(Func<Vector3, float> field, Vector3 point, float spacing) {
        ArgumentNullException.ThrowIfNull(argument: field);

        var gradient = Vector3.Zero;

        foreach (var tap in Offsets) {
            gradient += (tap * field(arg: (point + (tap * spacing))));
        }

        // The four taps sum to four times the spacing times the gradient.
        return (gradient * (0.25f / spacing));
    }
}
