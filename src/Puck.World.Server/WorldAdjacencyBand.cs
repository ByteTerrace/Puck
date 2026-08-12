using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// One invisible ownership boundary in the deterministic fixed-point domain: its stable adjacency name and the
/// compiled frame shared by observation, contact, rendering, and handoff.
/// </summary>
/// <param name="Name">The adjacency row's stable name.</param>
/// <param name="Frame">The facet's own derived frame.</param>
public readonly record struct WorldAdjacencyBand(string Name, WorldFaceFrame Frame) {
    /// <summary>Whether <paramref name="position"/> sits within this band: no farther than the derived depth INSIDE
    /// the owned half-space, unbounded OUTSIDE it, and within the same lateral aperture
    /// <see cref="WorldAdjacencyRegion.Sweep(WorldFaceFrame, Puck.Maths.FixedVector3, Puck.Maths.FixedVector3, FixedQ4816)"/> hands ownership over.</summary>
    /// <remarks>
    /// <para>The aperture is the ownership rectangle, never a narrower one. Every point ownership claims must
    /// resolve contact somewhere, so the two apertures are one shape derived twice: expand the horizontal
    /// half-width by <paramref name="ownershipThreshold"/> on a yaw-only frame — otherwise two perpendicular faces
    /// leave an unowned threshold-by-threshold corner square with rendered floor and no ground — and keep the
    /// authored vertical aperture exact.</para>
    /// <para>Outward the band does not end. A body past the boundary plane has left this world's own terrain and
    /// belongs to the neighbour; the neighbour's own geometry is what decides whether there is ground there, and
    /// ownership itself has no outward limit either. A finite outward bound is a hole whenever a step overshoots it
    /// or a handoff takes longer than the margin between it and the ownership threshold.</para>
    /// </remarks>
    /// <param name="position">The point to test, in the SOURCE side's own local coordinates.</param>
    /// <param name="depth">The compiler-derived overlap depth.</param>
    /// <param name="ownershipThreshold">The non-negative ownership threshold this frame hands over at
    /// (<see cref="WorldAdjacencyPolicy.OwnershipThreshold"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ownershipThreshold"/> is negative.</exception>
    public bool Contains(FixedVector3 position, FixedQ4816 depth, FixedQ4816 ownershipThreshold) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: ownershipThreshold.Value);

        var relative = (position - Frame.Origin);
        var horizontal = FixedVector3.Dot(left: relative, right: Frame.Right);
        var vertical = FixedVector3.Dot(left: relative, right: Frame.Up);
        var ownershipHalfWidth = (Frame.HalfWidth + (Frame.IsYawOnly ? ownershipThreshold : FixedQ4816.Zero));

        return (Transits(position: position, depth: depth) &&
            (FixedQ4816.Abs(value: horizontal) <= ownershipHalfWidth) &&
            (FixedQ4816.Abs(value: vertical) <= Frame.HalfHeight));
    }

    /// <summary>Whether <paramref name="position"/> may pass THROUGH this face on the way to a farther peer: the
    /// owned-side depth bound alone, with no aperture.</summary>
    /// <remarks>An intermediate hop of a derived corner path only transports coordinates; the stage that finally
    /// reads geometry applies <see cref="Contains"/>. Gating transport on this face's own aperture would leave the
    /// junction beyond two perpendicular rectangles unreachable — which is the region the diagonal peer exists to
    /// serve, and the region a body crossing a corner diagonally is standing in. The commuting-diamond proof
    /// <c>WorldDefinitionValidator.ValidateAdjacencies</c> requires of a derived corner is what makes transport past
    /// the aperture the same point either way round.</remarks>
    /// <param name="position">The point to test, in the SOURCE side's own local coordinates.</param>
    /// <param name="depth">The compiler-derived overlap depth.</param>
    public bool Transits(FixedVector3 position, FixedQ4816 depth) =>
        (FixedVector3.Dot(left: (position - Frame.Origin), right: Frame.Normal) >= -depth);
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
