using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The runtime counterpart to <see cref="IWorldNeighbourResolver"/>: <see cref="IWorldNeighbourResolver"/> proves an
/// adjacency envelope once at document-load time; this seam resolves the neighbour's actual delivered ground and
/// entity content every tick a body or render composition needs it. <c>Puck.World.Server</c> (a
/// body's contact resolution) and <c>Puck.World</c>'s render composition both consume the ONE resolution an
/// implementation produces, so a border's geometry and the ground a body stands on can never disagree.
/// </summary>
/// <remarks>Injected, never resolved by reaching into a sibling instance directly (<see cref="WorldServer.Adjacencies"/>
/// is the seam a body's contact field reads it through) — the composition root's implementation is expected to reach
/// the neighbour over the SAME wire-shaped path an ordinary session screen already observes a destination through
/// (<c>Server.WorldServer.AttachSink</c>), so the answer is data that could equally have arrived over a real network
/// connection, never a same-process shortcut into the neighbour's live server objects.</remarks>
public interface IWorldAdjacencySource {
    /// <summary>Returns the durable address of one locally authoritative entity. Cross-authority interaction uses
    /// this identity—not process placement or transport arrival order—to settle a single responder.</summary>
    WorldEntityAddress LocalEntityAddress(int index);
    /// <summary>The authored dynamic-body contact mode of one locally authoritative entity.</summary>
    WorldBodyContactMode LocalBodyContact(int index);
    /// <summary>Freezes the delivered neighbour records that simulation may read during one authority tick. A
    /// transport may continue delivering newer records concurrently, but they become eligible only at the next
    /// call. Rendering observes the same most-recent frozen record.</summary>
    void BeginTick(ulong tick);
    /// <summary>Attempts to resolve one live authored adjacency.</summary>
    /// <param name="adjacencyName">The source document's stable adjacency name.</param>
    /// <param name="neighbour">The resolved neighbour handle, when reachable.</param>
    /// <returns><see langword="true"/> when the neighbour is currently reachable and its counterpart face resolves.</returns>
    bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour);
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
/// One resolved adjacent authority: its live definition, per-tick entity image, counterpart frame, and a
/// lazily-compiled <see cref="WorldSolidField"/> over that same definition. All halves share one delivered mirror,
/// so observation, interaction callers, rendering, and collision cannot disagree about which revision they read.
/// collision query drawn from one <see cref="IWorldAdjacencyNeighbour"/> instance can never disagree about which
/// revision of the neighbour's geometry they answer against.
/// </summary>
public interface IWorldAdjacencyNeighbour {
    /// <summary>The identity stamped on this delivered authority image.</summary>
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
    /// <summary>Attempts to resolve a solid contact field compiled over <see cref="Definition"/> — the SAME
    /// derivation (<see cref="WorldSolidField.TryBuild"/>) the neighbour's own authority would compile for itself,
    /// rebuilt only when the mirrored definition's own delivery revision moves.</summary>
    /// <param name="field">The compiled field on success.</param>
    /// <param name="reason">The named compile failure (an op the warp-free evaluator cannot interpret), when this
    /// returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the field is available.</returns>
    bool TryGetSolidField(out WorldSolidField? field, out string reason);
}
