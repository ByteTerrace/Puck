namespace Puck.World.Client;

/// <summary>
/// The creation-stamp census: which active entities render their creation geometry through a
/// <see cref="WorldStampPool"/> (an inhabitant, or a crowd body wearing a creation look) instead of a catalog avatar.
/// The local scene and a session view each keep one over their own <see cref="IWorldStampSource"/>, refresh it at
/// each rebuild and hand its <see cref="Stamps"/> to their pool. A body parks its catalog avatar only once the pool
/// registered it (<see cref="WorldStampPool.HasBodyRegistration"/>): a stamp a full pool refused draws as its avatar.
/// </summary>
public sealed class WorldBodyStampCensus {
    // Each active body's reads of its source's state mirror (its live scale), created on first need and released when
    // the body leaves.
    private readonly WorldStateLease?[] m_reads = new WorldStateLease?[WorldBodiesLimits.CapacityCeiling];
    private readonly List<WorldStampPool.BodyStamp> m_stamps = new();

    /// <summary>Gets the body-rooted stamps the latest <see cref="Refresh"/> found, for
    /// <see cref="WorldStampPool.Reconcile"/>.</summary>
    public IReadOnlyList<WorldStampPool.BodyStamp> Stamps => m_stamps;

    // The body's live scale through the state mirror slot its lease holds, acquired while the body is active.
    private float LiveBodyScale(IWorldStampSource source, WorldDefinition definition, int index) {
        var reads = (m_reads[index] ??= new WorldStateLease());

        reads.Bind(
            bodyIndex: index,
            mirror: source.StateMirror
        );
        reads.Arrive(
            first: definition,
            second: null
        );

        return WorldGaitDrivers.LiveBodyScale(
            reads: reads,
            scaleRow: definition.Population.ScaleRow
        );
    }
    // The creation a body renders as a stamp, or null (it renders as a catalog avatar): an INHABITANT wears the look's
    // creation (a Creation look) or its placement's own creation; a crowd body wears its look's creation (a Creation
    // look). The uniform scale folds the placement scale, the look scale, and the body's own live scale (see
    // LiveBodyScale) — the same bodies.scaleRow cell WorldPopulation.SyncBodyScale reads server-side.
    private WorldStampPool.BodyStamp? ResolveStampCreation(IWorldStampSource source, WorldDefinition definition, int index) {
        var look = source.Look(index: index);
        var liveScale = LiveBodyScale(
            definition: definition,
            index: index,
            source: source
        );

        if (source.PlacementId(index: index) is { } placementId) {
            if (WorldDefinitionRows.FindPlacement(
                placements: definition.Placements,
                id: placementId
            ) is not { } placement) {
                return null;
            }

            var prototypeId = ((look.Source is WorldLookSource.Creation inhabitLook)
                ? inhabitLook.PrototypeId.Value
                : placement.PrototypeId
            );

            return ((WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: prototypeId
            ) is { } creation)
                ? new WorldStampPool.BodyStamp(
                    BodyIndex: index,
                    Creation: creation,
                    Scale: ((placement.Scale * look.Scale) * liveScale),
                    Look: look
                )
                : null
            );
        }

        if (look.Source is WorldLookSource.Creation crowdLook) {
            return ((WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: crowdLook.PrototypeId
            ) is { } creation)
                ? new WorldStampPool.BodyStamp(
                    BodyIndex: index,
                    Creation: creation,
                    Scale: (look.Scale * liveScale),
                    Look: look
                )
                : null
            );
        }

        return null;
    }

    /// <summary>Refreshes the census from a source's active entities. Called at each rebuild.</summary>
    /// <param name="source">The world whose entities the census reads.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void Refresh(IWorldStampSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_stamps.Clear();

        var definition = source.Definition;

        for (var index = 0; (index < WorldBodiesLimits.CapacityCeiling); index++) {
            if (!source.IsActive(index: index)) {
                // A body that left stops being read: its scale slot goes back to the mirror.
                m_reads[index]?.Release();

                continue;
            }

            if (
                (ResolveStampCreation(
                definition: definition,
                index: index,
                source: source
            ) is not { } stamp)
            ) {
                continue;
            }

            m_stamps.Add(item: stamp);
        }
    }
}
