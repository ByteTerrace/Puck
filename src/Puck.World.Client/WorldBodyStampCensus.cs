using System.Diagnostics.CodeAnalysis;

namespace Puck.World.Client;

/// <summary>
/// The creation-stamp census: which active entities render their creation geometry through a
/// <see cref="WorldCreationInstances"/> (an inhabitant, or a crowd body wearing a creation look) instead of a catalog avatar.
/// The local scene and a session view each keep one over their own <see cref="IWorldStampSource"/>, refresh it at
/// each rebuild and hand its <see cref="Stamps"/> to their pool. A stamp bakes its body's presented live scale at that
/// rebuild, so a scene asks <see cref="TryTakeMove"/> each frame whether that scale moved since, whether by a state write
/// or by its easing, and rebuilds when it did. A body parks its catalog avatar only once the pool registered it (<see cref="WorldCreationInstances.HasBodyRegistration"/>): a stamp a full pool refused draws as its avatar.
/// </summary>
public sealed class WorldBodyStampCensus {
    // Each active body's reads of its source's state mirror (its live scale), created on first need and released when
    // the body leaves.
    private readonly WorldStateLease?[] m_reads = new WorldStateLease?[WorldBodiesLimits.CapacityCeiling];
    private readonly List<WorldCreationInstances.BodyStamp> m_stamps = new();
    // The presented live scale each stamped body's stamp baked (or the census last answered a move to), by body index,
    // and the scale row it was read from, so a move is answered once.
    private readonly float[] m_bakedScale = new float[WorldBodiesLimits.CapacityCeiling];

    private string? m_scaleRow;

    /// <summary>Gets the body-rooted stamps the latest <see cref="Refresh"/> found, for
    /// <see cref="WorldCreationInstances.Reconcile"/>.</summary>
    public IReadOnlyList<WorldCreationInstances.BodyStamp> Stamps => m_stamps;

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
            scaleRow: m_scaleRow
        );
    }

    /// <summary>Returns the creation a body renders as a stamp: an inhabitant wears its look's creation (a creation
    /// look) or its placement's own creation, and a crowd body wears its look's creation (a creation look).</summary>
    /// <param name="source">The world whose body is read.</param>
    /// <param name="definition">The source's live document.</param>
    /// <param name="index">The body's index.</param>
    /// <param name="creation">The creation the body renders as, or <see langword="null"/> when it renders as a catalog
    /// avatar.</param>
    /// <param name="scale">The creation's uniform stamp scale on the body, the placement scale times the look scale,
    /// before the body's own live scale; 1 when there is no creation.</param>
    /// <returns><see langword="true"/> when the body renders as a creation stamp.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="definition"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryResolveCreation(IWorldStampSource source, WorldDefinition definition, int index, [NotNullWhen(returnValue: true)] out WorldPrototype? creation, out float scale) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: definition);

        var look = source.Look(index: index);
        var placementScale = 1f;
        string? prototypeId = null;

        if (source.PlacementId(index: index) is { } placementId) {
            if (WorldDefinitionRows.FindPlacement(
                placements: definition.Placements,
                id: placementId
            ) is { } placement) {
                placementScale = placement.Scale;
                prototypeId = ((look.Source is WorldLookSource.Creation inhabitLook)
                    ? inhabitLook.PrototypeId.Value
                    : placement.PrototypeId
                );
            }
        } else if (look.Source is WorldLookSource.Creation crowdLook) {
            prototypeId = crowdLook.PrototypeId.Value;
        }

        creation = ((prototypeId is null)
            ? null
            : WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: prototypeId
            )
        );
        scale = ((creation is null)
            ? 1f
            : (placementScale * look.Scale)
        );

        return (creation is not null);
    }

    // The creation a body renders as a stamp, or null (it renders as a catalog avatar). The uniform scale folds the
    // placement scale, the look scale, and the body's own live scale (see LiveBodyScale) — the same bodies.scale register
    // WorldPopulation.SyncBodyScale reads server-side.
    private WorldCreationInstances.BodyStamp? ResolveStampCreation(IWorldStampSource source, WorldDefinition definition, int index) {
        var liveScale = LiveBodyScale(
            definition: definition,
            index: index,
            source: source
        );

        m_bakedScale[index] = liveScale;

        return (TryResolveCreation(
            creation: out var creation,
            definition: definition,
            index: index,
            scale: out var scale,
            source: source
        )
            ? new WorldCreationInstances.BodyStamp(
                BodyIndex: index,
                Creation: creation,
                Scale: (scale * liveScale),
                Look: source.Look(index: index)
            )
            : null
        );
    }

    /// <summary>Refreshes the census from a source's active entities. Called at each rebuild.</summary>
    /// <param name="source">The world whose entities the census reads.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void Refresh(IWorldStampSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_stamps.Clear();

        var definition = source.Definition;

        m_scaleRow = ((definition.Population.Scale is { } scale)
            ? WorldBodyStateReads.BindingRow(slot: scale)
            : null
        );

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
    /// <summary>Returns whether a stamped body's presented live scale differs from the one its stamp baked at the last
    /// <see cref="Refresh"/> or the last move this answered, answering each move once. The presented value is compared,
    /// not the tick a slot last changed at, so an eased scale settling between ticks is a move too. A check with nothing
    /// moved reads one slot per stamped body.</summary>
    /// <returns><see langword="true"/> when the scene must rebuild so its stamps bake the moved scale.</returns>
    public bool TryTakeMove() {
        var moved = false;

        foreach (var stamp in m_stamps) {
            var index = stamp.BodyIndex;

            if (m_reads[index] is not { } reads) {
                continue;
            }

            var scale = WorldGaitDrivers.LiveBodyScale(
                reads: reads,
                scaleRow: m_scaleRow
            );

            if (scale != m_bakedScale[index]) {
                m_bakedScale[index] = scale;
                moved = true;
            }
        }

        return moved;
    }
}
