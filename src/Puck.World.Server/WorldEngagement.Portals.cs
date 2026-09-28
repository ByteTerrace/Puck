using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One portal face a body can engage: a placement face whose source is a session projected as a window
/// through a portal facet naming its counterpart.</summary>
/// <param name="ScreenIndex">The face's derived screen index.</param>
/// <param name="Frame">The face's geometry, the glass a pointer ray must pass through.</param>
/// <param name="Destination">The destination the face's session observes.</param>
/// <param name="Counterpart">The destination face the portal maps the glass onto.</param>
public readonly record struct WorldPortalFace(int ScreenIndex, WorldFaceFrame Frame, string Destination, string Counterpart);
/// <summary>One engaged body's input bound through a portal face this tick: where its pointer ray met the glass, the
/// ray's direction, and its channels, in the world that produced it.</summary>
/// <param name="ScreenIndex">The portal face's screen index.</param>
/// <param name="Source">The principal whose application routed the input.</param>
/// <param name="Hit">Where the ray met the glass.</param>
/// <param name="Direction">The ray's direction.</param>
/// <param name="Intent">The body's engaged intent: its channels, in the producing world's ordinals.</param>
public readonly record struct WorldPortalForward(int ScreenIndex, Principal Source, FixedVector3 Hit, FixedVector3 Direction, PlayerIntent Intent);
public sealed partial class WorldEngagement {
    // The live definition's portal faces by screen index, for the definition they were derived from.
    private readonly Dictionary<int, WorldPortalFace> m_portalFaces = new();
    private readonly List<WorldPortalForward> m_portalForwards = [];

    private WorldDefinition? m_indexedPortals;

    /// <summary>Gets this tick's portal forwards, one per portal face at most: the first engaged body, by ascending
    /// index, whose pointer ray passes through that face's glass. Rebuilt by every <see cref="FoldTick"/>.</summary>
    public IReadOnlyList<WorldPortalForward> PortalForwards => m_portalForwards;

    // Where a ray passes through a face's glass from its outward side, in fixed point: the plane crossing ahead of the
    // ray's origin, inside the face's half extents. Null for a ray parallel to the glass, one behind it, or a miss.
    private static FixedVector3? ThroughGlass(WorldFaceFrame frame, SourceRay ray) {
        var offset = (ray.Origin - frame.Origin);
        var height = FixedVector3.Dot(
            left: offset,
            right: frame.Normal
        );
        var approach = FixedVector3.Dot(
            left: ray.Direction,
            right: frame.Normal
        );

        if (
            (height <= FixedQ4816.Zero) ||
            (approach >= FixedQ4816.Zero)
        ) {
            return null;
        }

        var hit = (ray.Origin + (ray.Direction * (-height / approach)));
        var local = (hit - frame.Origin);

        return ((
            (FixedQ4816.Abs(value: FixedVector3.Dot(left: local, right: frame.Right)) <= frame.HalfWidth) &&
            (FixedQ4816.Abs(value: FixedVector3.Dot(left: local, right: frame.Up)) <= frame.HalfHeight)
        )
            ? hit
            : null);
    }
    // Rebuilds the portal-face index when the definition moved: every derived face whose source is a session projected
    // as a window and whose placement face row carries a portal facet naming a counterpart.
    private void IndexPortals() {
        var definition = m_definition();

        if (ReferenceEquals(
            objA: definition,
            objB: m_indexedPortals
        )) {
            return;
        }

        m_indexedPortals = definition;
        m_portalFaces.Clear();

        foreach (var row in WorldFaceCatalog.For(definition: definition).Rows) {
            if (
                (row.ScreenIndex < 0) ||
                (row.Source is not WorldScreenSource.Session { Projection: WorldScreenProjection.Window } session) ||
                (WorldDefinitionRows.FindPlacement(
                id: row.PlacementId,
                placements: definition.Placements
            ) is not { } placement)
            ) {
                continue;
            }

            foreach (var face in (placement.FaceSources ?? [])) {
                if (
                    string.Equals(
                    a: face.Face,
                    b: row.FaceName,
                    comparisonType: StringComparison.Ordinal
                ) &&
                    (face.Portal is { Counterpart: { Length: > 0 } counterpart })
                ) {
                    m_portalFaces[row.ScreenIndex] = new WorldPortalFace(
                        Counterpart: counterpart,
                        Destination: session.Destination,
                        Frame: row.Frame,
                        ScreenIndex: row.ScreenIndex
                    );
                }
            }
        }
    }
    // Stages one body's engaged input through a portal face, when the face is one and no earlier body already routed
    // through it this tick, and only when its ray passes through the glass.
    private void StagePortalForward(int screenIndex, Principal principal, in PlayerIntent intent) {
        if (
            !TryPortalFace(
            face: out var face,
            screenIndex: screenIndex
        ) ||
            (intent.SourceRay is not { } ray) ||
            (ThroughGlass(
            frame: face.Frame,
            ray: ray
        ) is not { } hit)
        ) {
            return;
        }

        foreach (var staged in m_portalForwards) {
            if (staged.ScreenIndex == screenIndex) {
                return;
            }
        }

        m_portalForwards.Add(item: new WorldPortalForward(
            Direction: ray.Direction,
            Hit: hit,
            Intent: intent,
            ScreenIndex: screenIndex,
            Source: principal
        ));
    }

    /// <summary>Reads the portal face at a screen index of the live definition.</summary>
    /// <param name="screenIndex">The screen index.</param>
    /// <param name="face">The portal face, on success.</param>
    /// <returns><see langword="true"/> when the index is a portal face.</returns>
    public bool TryPortalFace(int screenIndex, out WorldPortalFace face) {
        IndexPortals();

        return m_portalFaces.TryGetValue(
            key: screenIndex,
            value: out face
        );
    }
}
