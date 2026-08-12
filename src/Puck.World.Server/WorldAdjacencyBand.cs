using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// One invisible ownership boundary in the deterministic fixed-point domain: its stable adjacency name and the
/// compiled frame shared by observation, contact, rendering, and handoff.
/// </summary>
/// <param name="Name">The adjacency row's stable name.</param>
/// <param name="Frame">The facet's own derived frame.</param>
public readonly record struct WorldAdjacencyBand(string Name, WorldFaceFrame Frame) {
    /// <summary>Whether <paramref name="position"/> sits within this band — on this facet's source/front side (the
    /// side it faces and from which handoff admits a crossing), no farther than the derived depth from
    /// its plane, and within the face's own lateral extent. The symmetric band intentionally covers both the
    /// wall-boundary ownership deadband and the small post-integration overshoot beyond a plane-based boundary;
    /// otherwise contact and visible ground could disappear before the ownership scan commits or closes handoff.</summary>
    /// <param name="position">The point to test, in the SOURCE side's own local coordinates.</param>
    /// <param name="depth">The compiler-derived overlap depth.</param>
    public bool Contains(FixedVector3 position, FixedQ4816 depth) {
        var relative = (position - Frame.Origin);
        var normal = FixedVector3.Dot(left: relative, right: Frame.Normal);
        var horizontal = FixedVector3.Dot(left: relative, right: Frame.Right);
        var vertical = FixedVector3.Dot(left: relative, right: Frame.Up);
        return ((normal >= -depth) && (normal <= depth) &&
            (FixedQ4816.Abs(value: horizontal) <= Frame.HalfWidth) &&
            (FixedQ4816.Abs(value: vertical) <= Frame.HalfHeight));
    }
}

/// <summary>Collects every <see cref="WorldAdjacencyBand"/> a definition authors — the walk both
/// <see cref="WorldAdjacencyContactField"/> and the render composition share, so the two can never disagree about
/// which faces carry an overlap band.</summary>
public static class WorldAdjacencyBands {
    /// <summary>The reservation ceiling for direct edges plus at most one derived peer per unordered edge pair.</summary>
    public static int ProjectionCapacity(WorldDefinition definition) {
        var edges = CollectFrom(definition: definition).Count;
        return (edges + ((edges * (edges - 1)) / 2));
    }

    /// <summary>Walks <paramref name="definition"/>'s adjacency rows in document order and compiles each boundary
    /// through the one fixed-point frame derivation.</summary>
    /// <param name="definition">The definition to walk.</param>
    /// <returns>Every adjacency band the definition authors, in document order.</returns>
    public static IReadOnlyList<WorldAdjacencyBand> CollectFrom(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        List<WorldAdjacencyBand>? bands = null;
        foreach (var adjacency in (definition.Adjacencies ?? [])) {
            if (adjacency?.Boundary is not { } boundary) {
                continue;
            }
            (bands ??= []).Add(item: new WorldAdjacencyBand(Name: adjacency.Name.Value, Frame: boundary.CompileFrame()));
        }

        return ((IReadOnlyList<WorldAdjacencyBand>?)bands ?? []);
    }
}
