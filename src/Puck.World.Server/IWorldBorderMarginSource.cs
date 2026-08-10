namespace Puck.World.Server;

/// <summary>
/// The RUNTIME counterpart to <see cref="IWorldNeighbourResolver"/>: <see cref="IWorldNeighbourResolver"/> proves a
/// mapped portal facet's <c>marginDepth</c> claim once, at document-load time; this seam resolves the neighbour's
/// actual ground/solid content, every tick a body or a render composition needs it. <c>Puck.World.Server</c> (a
/// body's contact resolution) and <c>Puck.World</c>'s render composition both consume the ONE resolution an
/// implementation produces, so a border's geometry and the ground a body stands on can never disagree.
/// </summary>
/// <remarks>Injected, never resolved by reaching into a sibling instance directly (<see cref="WorldServer.BorderMargin"/>
/// is the seam a body's contact field reads it through) — the composition root's implementation is expected to reach
/// the neighbour over the SAME wire-shaped path an ordinary session screen already observes a destination through
/// (<c>Server.WorldServer.AttachSink</c>), so the answer is data that could equally have arrived over a real network
/// connection, never a same-process shortcut into the neighbour's live server objects.</remarks>
public interface IWorldBorderMarginSource {
    /// <summary>Attempts to resolve the live neighbour for a mapped portal facet carrying an authored
    /// <c>marginDepth</c>.</summary>
    /// <param name="placementId">The facet's owning placement id, on THIS (source) side.</param>
    /// <param name="faceName">The facet's declared face name.</param>
    /// <param name="neighbour">The resolved neighbour handle, when reachable.</param>
    /// <returns><see langword="true"/> when the neighbour is currently reachable and its counterpart face resolves.</returns>
    bool TryResolve(string placementId, string faceName, out IWorldBorderMarginNeighbour? neighbour);
}

/// <summary>
/// One resolved neighbour behind a mapped border: its live definition (the render composition's own source of
/// creations/placements) and the counterpart face's derived frame (the SAME <see cref="WorldFaceFrame"/> shape the
/// portal trigger and the arrival isometry read), plus a lazily-compiled <see cref="WorldSolidField"/> over that
/// SAME definition for collision. Both halves are keyed on the SAME delivered definition, so a render frame and a
/// collision query drawn from one <see cref="IWorldBorderMarginNeighbour"/> instance can never disagree about which
/// revision of the neighbour's geometry they answer against.
/// </summary>
public interface IWorldBorderMarginNeighbour {
    /// <summary>Gets the neighbour's live delivered definition.</summary>
    WorldDefinition Definition { get; }

    /// <summary>Gets the monotonic delivery counter behind <see cref="Definition"/> — the render composition's own
    /// rebuild-watch component, so a live neighbour edit is reflected exactly like a local one.</summary>
    int DefinitionRevision { get; }

    /// <summary>Gets the counterpart face's own derived frame, in the NEIGHBOUR's own local coordinate space — the
    /// SAME per-revision derivation (<see cref="WorldFaceCatalog"/>) the portal trigger, the arrival isometry, and
    /// rendering all read for this face.</summary>
    WorldFaceFrame CounterpartFrame { get; }

    /// <summary>Attempts to resolve a solid contact field compiled over <see cref="Definition"/> — the SAME
    /// derivation (<see cref="WorldSolidField.TryBuild"/>) the neighbour's own authority would compile for itself,
    /// rebuilt only when the mirrored definition's own delivery revision moves.</summary>
    /// <param name="field">The compiled field on success.</param>
    /// <param name="reason">The named compile failure (an op the warp-free evaluator cannot interpret), when this
    /// returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the field is available.</returns>
    bool TryGetSolidField(out WorldSolidField? field, out string reason);
}
