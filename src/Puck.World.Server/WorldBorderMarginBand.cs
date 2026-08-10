using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// One mapped portal facet's authored margin, in the deterministic fixed-point domain — the source-side half of a
/// shared border strip: which placement/face it sits on, its own derived frame (the SAME <see cref="WorldFaceFrame"/>
/// the portal trigger and the arrival isometry read), and the authored depth.
/// </summary>
/// <param name="PlacementId">The facet's owning placement id.</param>
/// <param name="FaceName">The facet's declared face name.</param>
/// <param name="Frame">The facet's own derived frame.</param>
/// <param name="Depth">The authored <c>marginDepth</c>, in fixed point.</param>
public readonly record struct WorldBorderMarginBand(string PlacementId, string FaceName, WorldFaceFrame Frame, FixedQ4816 Depth) {
    /// <summary>Whether <paramref name="position"/> sits within this band — on this facet's source/front side (the
    /// side it faces and from which the portal trigger admits a crossing), no farther than <see cref="Depth"/> from
    /// its plane, and within the face's own lateral extent. Reuses the SAME one-sided aperture and swept-region test
    /// the portal trigger scans a body's step against (<see cref="WorldFaceRegion.Sweep"/>), degenerated to a point
    /// test. A body past the plane has negative distance along <see cref="Frame"/>'s normal and is excluded because
    /// it is already on the crossed/back side, not standing in this world's margin.</summary>
    /// <param name="position">The point to test, in the SOURCE side's own local coordinates.</param>
    public bool Contains(FixedVector3 position) {
        var aperture = new WorldFaceAperture.Box(Frame: Frame, Depth: Depth);

        return WorldFaceRegion.Sweep(aperture: aperture, from: position, to: position).Inside;
    }
}

/// <summary>Collects every <see cref="WorldBorderMarginBand"/> a definition authors — the walk both
/// <see cref="WorldBorderMarginContactField"/> and the render composition share, so the two can never disagree about
/// which faces carry a strip.</summary>
public static class WorldBorderMarginBands {
    /// <summary>Walks <paramref name="definition"/>'s placements for every mapped portal facet authoring a
    /// (shape-valid) <c>marginDepth</c>, in document order. A face that fails the yaw-only test the arrival isometry
    /// requires (<see cref="WorldFaceFrame.IsYawOnly"/>) is skipped — the document validator already refuses a
    /// mapped facet on such a face, so reaching one here would mean an unvalidated document, and skipping rather than
    /// throwing keeps this walk a pure, total function of whatever document it is handed.</summary>
    /// <param name="definition">The definition to walk.</param>
    /// <returns>Every margin band the definition authors, in document order.</returns>
    public static IReadOnlyList<WorldBorderMarginBand> CollectFrom(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        List<WorldBorderMarginBand>? bands = null;
        var catalog = WorldFaceCatalog.For(definition: definition);

        foreach (var placement in definition.Placements) {
            if ((placement is null) || (placement.FaceSources is not { Count: > 0 } faces)) {
                continue;
            }

            foreach (var face in faces) {
                if ((face?.Portal is not { Arrival: WorldPortalArrival.Mapped, MarginDepth: { } depth }) ||
                    !float.IsFinite(f: depth) || (depth <= 0f) ||
                    !catalog.TryFind(placementId: placement.Id, faceName: face.Face, out var row) ||
                    !row.Frame.IsYawOnly) {
                    continue;
                }

                (bands ??= []).Add(item: new WorldBorderMarginBand(PlacementId: placement.Id, FaceName: face.Face, Frame: row.Frame, Depth: FixedQ4816.FromDouble(value: depth)));
            }
        }

        return ((IReadOnlyList<WorldBorderMarginBand>?)bands ?? []);
    }
}
