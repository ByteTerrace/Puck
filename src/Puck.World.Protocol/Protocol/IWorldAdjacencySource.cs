using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The runtime counterpart to <see cref="IWorldNeighbourResolver"/>: <see cref="IWorldNeighbourResolver"/> proves an
/// adjacency envelope once at document-load time. This seam reads the neighbour's published ground and entity
/// content. <c>Puck.World.Server</c> (a
/// body's contact resolution) and <c>Puck.World</c>'s render composition both consume the ONE resolution an
/// implementation produces, so a border's geometry and the ground a body stands on can never disagree.
/// </summary>
/// <remarks>Injected, never resolved by reaching into a sibling instance directly (<c>WorldServer.Adjacencies</c>
/// exposes the latest published projections) — the composition root's implementation is expected to reach
/// the neighbour over the SAME wire-shaped path an ordinary session screen already observes a destination through
/// (<c>WorldServer.AttachSink</c>), so the answer is data that could equally have arrived over a real network
/// connection, never a same-process shortcut into the neighbour's live server objects.</remarks>
public interface IWorldAdjacencySource {
    /// <summary>Reads the published neighbour for one authored adjacency.</summary>
    IWorldAdjacencyNeighbour? Find(string adjacencyName) {
        var projections = Visuals();
        for (var index = 0; index < projections.Count; index++) {
            var projection = projections[index];
            if (projection.Direct && string.Equals(projection.Name, adjacencyName, StringComparison.Ordinal)) {
                return projection.Neighbour;
            }
        }
        return null;
    }

    /// <summary>Returns the deterministic contact/render/interest projection set: every direct edge plus
    /// compiler-derived corner peers reachable through two different direct neighbours. Corner peers observe and
    /// provide geometry contact but never own a crossing; their path maps terrain and entity poses through the same
    /// reciprocal frames as both edges.</summary>
    IReadOnlyList<WorldAdjacencyProjection> Visuals();
}
/// <summary>One neighbour-to-source mapping stage.</summary>
/// <param name="Neighbour">The counterpart face's frame, in the neighbour's own coordinates.</param>
/// <param name="Source">The source face's frame, in the coordinates this stage's input is expressed in.</param>
/// <param name="OverlapDepth">The compiler-derived overlap depth for this stage.</param>
/// <param name="OwnershipThreshold">The threshold <see cref="Source"/> hands ownership over at
/// (<see cref="WorldAdjacencyPolicy.OwnershipThreshold"/>), derived from the document that authors it. Contact's
/// lateral aperture expands by it exactly as <see cref="WorldAdjacencyRegion.Sweep(WorldFaceFrame, Puck.Maths.FixedVector3, Puck.Maths.FixedVector3, FixedQ4816)"/>'s does, so no point ownership
/// claims is outside every contact band.</param>
public readonly record struct WorldAdjacencyFramePair(WorldFaceFrame Neighbour, WorldFaceFrame Source, FixedQ4816 OverlapDepth, FixedQ4816 OwnershipThreshold);
/// <summary>One direct or compiler-derived corner projection.</summary>
public sealed record WorldAdjacencyProjection(string Name, IWorldAdjacencyNeighbour Neighbour, IReadOnlyList<WorldAdjacencyFramePair> Path, FixedQ4816 OverlapDepth, bool Direct);
/// <summary>
/// One resolved adjacent authority: its delivered definition, per-tick entity image, and counterpart frame. A Server-side contact resolver additionally narrows to
/// <c>IWorldAdjacencyNeighbourContact</c> for the compiled solid field this project cannot name.
/// </summary>
public interface IWorldAdjacencyNeighbour {
    /// <summary>Gets the authority identity this link settled on: the identity stamped on the first delivered image
    /// it exposed, kept across observation replacements for the authored link's lifetime so a neighbour cannot claim
    /// another identity per image. Cross-authority
    /// settlement (<c>WorldCrossAuthoritySettlement</c>) reads it.</summary>
    string Authority { get; }
    /// <summary>Gets the neighbour's live delivered definition.</summary>
    WorldDefinition Definition { get; }
    /// <summary>Gets the monotonic delivery counter behind <see cref="Definition"/> — the render composition's own
    /// rebuild-watch component, so a live neighbour edit is reflected exactly like a local one.</summary>
    int DefinitionRevision { get; }
    /// <summary>Gets the counterpart face's own derived frame, in the NEIGHBOUR's own local coordinate space — the
    /// SAME per-revision derivation (<see cref="WorldFaceCatalog"/>) the portal trigger, the arrival isometry, and
    /// rendering all read for this face.</summary>
    WorldFaceFrame CounterpartFrame { get; }
    /// <summary>The latest delivered simulation tick.</summary>
    ulong SnapshotTick { get; }
    /// <summary>The delivered entity-set/palette revision.</summary>
    int SnapshotRevision { get; }
    /// <summary>The presentation fraction through the delivered snapshot interval, derived from that neighbour's
    /// own clock rather than the observing world's tick rate.</summary>
    float InterpolationAlpha { get; }
    /// <summary>The maximum addressable entity slots in the delivered image.</summary>
    int EntityCapacity { get; }

    /// <summary>Whether a delivered entity slot is active.</summary>
    bool IsEntityActive(int index);
    /// <summary>The durable address of a delivered entity slot.</summary>
    WorldEntityAddress EntityAddress(int index);
    /// <summary>The pose epoch of a delivered entity slot: it moves each time the entity becomes active or teleports,
    /// the two discontinuities a snapshot declares, so a presentation that follows the pose cuts there.</summary>
    int PoseEpoch(int index);
    /// <summary>Gets the authored placement identity from the same delivered entity image as its pose, or null
    /// for a body without a placement.</summary>
    string? PlacementId(int index);
    Vector3 PreviousPosition(int index);
    Quaternion PreviousOrientation(int index);
    Vector3 CurrentPosition(int index);
    Quaternion CurrentOrientation(int index);
    Vector3 BodyColor(int index);
    WorldLook Look(int index);
    byte CatalogRig(int index);
    /// <summary>The authored collider currently worn by the delivered entity, or null for a volumeless kit.</summary>
    FixedWorldCollider? Collider(int index);
    /// <summary>The authored dynamic-body contact mode worn by the delivered entity.</summary>
    WorldBodyContactMode BodyContact(int index);
}
