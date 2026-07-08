using Puck.Assets;
using Puck.Demo.Overworld;

namespace Puck.Demo.World;

/// <summary>
/// The deliberate-save walk-grid bake, factored out of <c>OverworldFrameSource.BakeWorldForSave</c> so the SAME
/// derivation backs both the live save (the frame source installs it as <see cref="WorldScene.PrepareForSave"/>) and
/// the headless town builder (<c>Puck.Demo.Town</c>), which must produce a committed world whose baked grid is
/// byte-identical to what a live save would write — otherwise the <c>world.verify</c> save→reload byte-compare
/// mismatches. Bakes the walk
/// grid from the document's placements (resolved from the store), its terrain patches, and the room's own console
/// stands, honoring the requested tessellation. Deterministic: bake-twice from the same (document, room, kind) is
/// byte-identical.
/// </summary>
public static class WorldWalkGridBake {
    /// <summary>Bakes the walk grid into a copy of <paramref name="document"/>.</summary>
    /// <param name="document">The world document to bake collision for.</param>
    /// <param name="room">The room whose console stands block (and whose player half-extents + floor size the walk
    /// band) — the live save passes the applied room; the town builder passes the same base room the run resolves to,
    /// so the two bakes match.</param>
    /// <param name="walkGridKind">The tessellation knob (<c>square</c> or <c>hex</c>).</param>
    /// <param name="store">The content-addressed store the placements' creations resolve against.</param>
    /// <returns>The document with its <see cref="WorldDocument.WalkGrid"/> baked.</returns>
    public static WorldDocument Bake(WorldDocument document, OverworldRoom room, string walkGridKind, ContentAddressedStore store) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(store);

        var bounds = (document.Bounds ?? new WorldBoundsDocument(FloorY: room.FloorY, MaxX: room.BoundsMax.X, MaxZ: room.BoundsMax.Y, MinX: room.BoundsMin.X, MinZ: room.BoundsMin.Y));
        var footprints = new List<WorldFootprint>();

        foreach (var placement in (document.Placements ?? [])) {
            if ((placement.Source is { Length: > 0 } source) && store.TryGet(content: out var bytes, hash: source) && (CreationDocumentBytes.Deserialize(bytes: bytes) is { } creation)) {
                footprints.AddRange(collection: WorldFootprintDerivation.ForPlacement(creation: creation, placement: placement));
            }
        }
        foreach (var patch in (document.Terrain ?? [])) {
            footprints.Add(item: WorldFootprintDerivation.ForTerrainPatch(patch: patch));
        }
        // The room's own stands block exactly as the sim's FixedConsole boxes do (full walk-band height). Room
        // planar coordinates are Vector2 XZ (X = world X, Y = world Z — the room's own convention).
        foreach (var stand in room.Consoles) {
            footprints.Add(item: new WorldFootprint(
                MaxX: (stand.Center.X + stand.HalfExtents.X),
                MaxY: (room.FloorY + (2f * room.PlayerHalfExtents.Y)),
                MaxZ: (stand.Center.Y + stand.HalfExtents.Y),
                MinX: (stand.Center.X - stand.HalfExtents.X),
                MinY: room.FloorY,
                MinZ: (stand.Center.Y - stand.HalfExtents.Y)
            ));
        }

        var overrides = (document.WalkOverrides ?? []).Select(selector: static entry => WalkOverrideInput.FromDocument(document: entry));
        var kind = (string.Equals(a: walkGridKind, b: "hex", comparisonType: StringComparison.OrdinalIgnoreCase) ? WalkGridKind.Hex : WalkGridKind.Square);
        var grid = WalkGridBaker.Bake(
            bounds: bounds,
            footprints: footprints,
            kind: kind,
            overrides: overrides,
            playerHalfExtentX: room.PlayerHalfExtents.X,
            playerHalfExtentZ: room.PlayerHalfExtents.Z,
            walkBandFloorY: bounds.FloorY,
            walkBandHeight: (2f * room.PlayerHalfExtents.Y)
        );

        return (document with { WalkGrid = grid });
    }
}
