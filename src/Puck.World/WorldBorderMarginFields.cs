using Puck.World.Client;
using Puck.World.Server;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The composition root's <see cref="IWorldBorderMarginSource"/>: resolves a mapped portal facet's neighbour over
/// the same wire-shaped seam a picture-frame session screen already observes a destination through
/// (<see cref="WorldInstanceHost.TryResolveObservedDestination"/> then <see cref="WorldServer.AttachSink"/>) — data a
/// real network peer could equally have delivered, never a same-process shortcut into the neighbour's live server
/// objects. One instance-bound source is consumed by both that authority's contact resolution
/// (<see cref="WorldServer.BorderMargin"/>) and its boot-or-away render composition
/// (<see cref="Client.WorldBorderMarginSceneEmitter"/>), so a body's ground and what it sees cannot disagree.
/// </summary>
/// <remarks>One <see cref="Handle"/> per distinct (placementId, faceName) margin facet, held for the life of this
/// instance — an observation lease, exactly like a session screen's own, never released until this type is disposed.
/// Each handle lazily (re)resolves the counterpart frame and (re)compiles a <see cref="WorldSolidField"/> over the
/// mirrored definition only when the mirror's own delivery revision moves, mirroring
/// <see cref="WorldServer"/>'s own revision-gated <c>SwapSolids</c>.</remarks>
internal sealed class WorldBorderMarginFields : IWorldBorderMarginSource, IDisposable {
    private readonly WorldInstanceHost m_instances;
    private readonly string m_sourceInstanceName;
    private readonly Dictionary<(string PlacementId, string FaceName), Handle> m_handles = new();

    /// <summary>Initializes the source.</summary>
    /// <param name="instances">The process's running world instances — the one observation door a margin facet's
    /// neighbour resolves through.</param>
    /// <param name="sourceInstanceName">The concrete source authority whose authored face keys this resolver.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instances"/> is <see langword="null"/>.</exception>
    public WorldBorderMarginFields(WorldInstanceHost instances, string sourceInstanceName) {
        ArgumentNullException.ThrowIfNull(argument: instances);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourceInstanceName);

        m_instances = instances;
        m_sourceInstanceName = sourceInstanceName;
    }

    /// <inheritdoc/>
    public bool TryResolve(string placementId, string faceName, out IWorldBorderMarginNeighbour? neighbour) {
        neighbour = null;

        if (!m_instances.TryGet(name: m_sourceInstanceName, instance: out var source) || (source is null)) {
            return false;
        }

        var definition = source.Server.Definition;

        if ((WorldDefinitionRows.FindPlacement(placements: definition.Placements, id: placementId) is not { } placement) ||
            (WorldDefinitionRows.FindPlacementFace(placement: placement, face: faceName) is not { Portal: { Arrival: WorldPortalArrival.Mapped, MarginDepth: { } authoredDepth } portal })) {
            return false;
        }

        var key = (placementId, faceName);
        var depth = FixedQ4816.FromDouble(value: authoredDepth);

        if (!m_instances.TryResolveObservedDestination(source: source, destinationName: portal.Destination, target: out var target, resolved: out var resolved, reason: out _) || (target is null)) {
            return false;
        }

        var identity = new HandleIdentity(Destination: portal.Destination, InstanceName: resolved.InstanceName, GenerationId: resolved.GenerationId, Counterpart: portal.Counterpart, Depth: depth);

        if (m_handles.TryGetValue(key: key, value: out var handle) && (handle.Identity != identity)) {
            handle.Dispose();
            _ = m_handles.Remove(key: key);
            handle = null;
        }

        if (handle is null) {

            var mirror = new WorldSessionMirror(placeholder: target.Server.Definition);
            var lease = target.Server.AttachSink(sink: mirror);

            handle = new Handle(identity: identity, mirror: mirror, lease: lease, sourceDescription: $"{m_sourceInstanceName}/{placementId}/{faceName}");
            m_handles[key] = handle;
        }

        return handle.TryResolve(neighbour: out neighbour);
    }

    /// <inheritdoc/>
    public void Dispose() {
        foreach (var handle in m_handles.Values) {
            handle.Dispose();
        }

        m_handles.Clear();
    }

    // One margin facet's held observation: the mirror (kept live for the facet's lifetime), the attach lease, and the
    // counterpart frame/solid field cache — refreshed only when the mirror's own delivery revision moves.
    private readonly record struct HandleIdentity(string Destination, string InstanceName, ulong GenerationId, string? Counterpart, FixedQ4816 Depth);

    private sealed class Handle(HandleIdentity identity, WorldSessionMirror mirror, IDisposable lease, string sourceDescription) : IWorldBorderMarginNeighbour, IDisposable {
        private int m_builtRevision = -1;
        private WorldFaceFrame m_frame;
        private bool m_frameResolved;
        private WorldSolidField? m_field;
        private string m_fieldReason = string.Empty;

        public HandleIdentity Identity { get; } = identity;

        public WorldDefinition Definition => mirror.Definition;

        public int DefinitionRevision => mirror.DefinitionRevision;

        public WorldFaceFrame CounterpartFrame => m_frame;

        public bool TryResolve(out IWorldBorderMarginNeighbour? neighbour) {
            neighbour = null;

            if (m_builtRevision != mirror.DefinitionRevision) {
                if (WorldPortalCounterpart.TryResolve(definition: mirror.Definition, counterpart: Identity.Counterpart, placement: out var counterpartPlacement, face: out var counterpartFace, reason: out _) &&
                    WorldFaceCatalog.For(definition: mirror.Definition).TryFind(placementId: counterpartPlacement!.Id, faceName: counterpartFace!.Face, out var row)) {
                    m_frame = row.Frame;
                    m_frameResolved = true;

                    var selection = WorldBorderMarginGeometry.Select(definition: mirror.Definition, frame: row.Frame, marginDepth: Identity.Depth);
                    var collisionDefinition = mirror.Definition with { Placements = selection.Placements };

                    m_field = (WorldSolidField.TryBuild(definition: collisionDefinition, built: out var built, reason: out m_fieldReason) ? built : null);

                    if (selection.Truncated) {
                        Console.Error.WriteLine(value: $"[world.border: '{sourceDescription}' neighbour geometry truncated identically for collision and rendering at {WorldBorderMarginGeometry.MaximumPlacementsPerBand} solid placements]");
                    }
                } else {
                    m_frameResolved = false;
                    m_field = null;
                    m_fieldReason = "the authored counterpart frame no longer resolves";
                }
                m_builtRevision = mirror.DefinitionRevision;
            }

            if (!m_frameResolved) {
                return false;
            }

            neighbour = this;

            return true;
        }

        public bool TryGetSolidField(out WorldSolidField? field, out string reason) {
            field = m_field;
            reason = m_fieldReason;

            return (m_field is not null);
        }

        public void Dispose() => lease.Dispose();
    }
}
