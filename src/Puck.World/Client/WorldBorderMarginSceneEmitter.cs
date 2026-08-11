using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The render half of the border-margin strip: within each mapped portal facet's authored margin, composes the
/// neighbour's own solid geometry — the same rows <see cref="WorldSolidField"/> would compile for collision, never
/// its props, bodies, or screens — through the same isometry <see cref="WorldPortalArrivalMath"/> uses for a
/// crossing traveler, so the ground a body sees continuing past the doorway is the ground
/// <see cref="WorldBorderMarginContactField"/> actually stands it on.
/// </summary>
/// <remarks>
/// <para><b>Static only.</b> Every emitted instance is a plain <see cref="WorldPlacementStamper.EmitStatic"/> stamp —
/// this emitter declares <see cref="DynamicSlotCount"/> zero and needs no dynamic-transform slot budgeting, because a
/// neighbour's ground/solid placements do not move.</para>
/// <para><b>Which placements cross.</b> A neighbour placement carrying a <c>solid</c> facet participates when either
    /// its creation contains an unbounded primitive (<c>CreationGeometry.GetLocalBounds</c>'s
    /// <c>IsUnbounded</c> — an infinite ground plane is relevant everywhere near the
/// border, regardless of how far its own authored origin sits from the door) or its own reach
    /// (<c>CreationGeometry.Reach</c>, scaled) brings it within the counterpart face's own
/// margin band. A placement with no <c>solid</c> facet (a portal frame, a decorative prop) never qualifies — the
/// same authorial signal <see cref="WorldSolidField"/> already reads for collision, reused rather than re-derived.</para>
/// <para><b>Cost.</b> Bounded by <see cref="MaxInstancesPerBand"/> instances per margin-bearing face, reserved by the
/// construction-time probe (<see cref="WorldPlacementStamper.EmitProbe"/>) exactly like the boot world's own
/// placement headroom, so it folds into the same frozen ceiling <see cref="WorldFrameSource.ProgramWordCapacity"/>/
/// <see cref="WorldFrameSource.InstanceCapacity"/> already report — no new reservation class, and no verb in this
/// tree reads those two properties back today (a gap that predates this emitter).</para>
/// </remarks>
internal sealed class WorldBorderMarginSceneEmitter : ISdfSceneEmitter {
    // The per-face worst-case reservation: generous for the shipped quilt's own solid census (ground + two walls +
    // a corner post) with headroom for a live-edited neighbour, without letting one border's content spend the whole
    // program's word budget. A capacity constant (like WorldAwaySeatQuad.Count), not a world-tunable — see this
    // emitter's own remarks and CLAUDE.md's authored-vs-constant rule: every world wants THE SAME margin-instance
    // ceiling, because it sizes the reservation this emitter itself declares, never gameplay feel.
    internal const int MaxInstancesPerBand = WorldBorderMarginGeometry.MaximumPlacementsPerBand;

    private readonly Func<WorldDefinition> m_definition;
    private readonly IWorldBorderMarginSource m_source;
    private readonly int m_reservation;
    // The last-polled reachability/revision per band, keyed by (placementId, faceName) — WriteRevision's own poll
    // compares against this to decide whether a rebuild is owed, without ever emitting from inside WriteRevision
    // itself (emission belongs to Emit alone).
    private readonly Dictionary<(string PlacementId, string FaceName), int> m_polledRevisions = new();
    private int m_neighbourRevision;

    /// <summary>Initializes the emitter over the boot definition's own margin-bearing faces.</summary>
    /// <param name="client">The snapshot-fed client view — this world's own definition (never re-read from the
    /// server directly, matching every other client-side emitter in this composition).</param>
    /// <param name="source">The injected neighbour resolver — the same wire-shaped seam
    /// <see cref="WorldBorderMarginContactField"/> reads for collision.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldBorderMarginSceneEmitter(WorldClient client, IWorldBorderMarginSource source) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: source);

        m_definition = () => client.Definition;
        m_source = source;
        m_reservation = (WorldBorderMarginBands.CollectFrom(definition: client.Definition).Count * MaxInstancesPerBand);
    }

    /// <summary>Initializes the same border renderer over a followed instance's delivered mirror.</summary>
    public WorldBorderMarginSceneEmitter(WorldSessionMirror mirror, IWorldBorderMarginSource source) {
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentNullException.ThrowIfNull(argument: source);

        m_definition = () => mirror.Definition;
        m_source = source;
        m_reservation = (WorldBorderMarginBands.CollectFrom(definition: mirror.Definition).Count * MaxInstancesPerBand);
    }

    /// <inheritdoc/>
    public int DynamicSlotCount => 0;

    /// <inheritdoc/>
    public bool OwnsMaterialScope => false;

    /// <inheritdoc/>
    public int RevisionComponentCount => 1;

    /// <inheritdoc/>
    public void WriteRevision(Span<int> destination) {
        // Poll every band's neighbour reachability/definition-revision — a live neighbour edit (or a neighbour
        // becoming reachable/unreachable) bumps this component so the host rebuilds, exactly like every other
        // watched counter in this composition.
        foreach (var band in WorldBorderMarginBands.CollectFrom(definition: m_definition())) {
            var key = (band.PlacementId, band.FaceName);
            var polled = (m_source.TryResolve(placementId: band.PlacementId, faceName: band.FaceName, neighbour: out var neighbour) ? (neighbour!.DefinitionRevision + 1) : 0);

            if (!m_polledRevisions.TryGetValue(key: key, value: out var last) || (last != polled)) {
                m_polledRevisions[key] = polled;
                m_neighbourRevision++;
            }
        }

        destination[0] = m_neighbourRevision;
    }

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (context.Probe) {
            WorldPlacementStamper.EmitProbe(builder: builder, reservedCount: m_reservation);

            return;
        }

        EmitCurrent(builder: builder);
    }

    /// <summary>Emits the currently reachable live margin geometry without the capacity-probe branch. Camera
    /// clearance uses this to evaluate the same static strip the renderer composes.</summary>
    internal void EmitCurrent(SdfProgramBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        var definition = m_definition();

        foreach (var band in WorldBorderMarginBands.CollectFrom(definition: definition)) {
            if (!m_source.TryResolve(placementId: band.PlacementId, faceName: band.FaceName, neighbour: out var neighbour) || (neighbour is null)) {
                continue;
            }

            var frame = neighbour.CounterpartFrame;
            var selection = WorldBorderMarginGeometry.Select(definition: neighbour.Definition, frame: frame, marginDepth: band.Depth);
            var transformed = selection.Placements
                .Select(selector: placement => MapIntoSource(placement: placement, neighbourFrame: frame, sourceFrame: band.Frame))
                .ToArray();

            if (transformed.Length > 0) {
                WorldPlacementStamper.EmitStatic(builder: builder, creations: neighbour.Definition.Creations, placements: transformed);
            }
        }
    }

    /// <inheritdoc/>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) {
        // No dynamic slots declared (DynamicSlotCount is 0) — nothing to pack.
    }

    // Maps a neighbour placement's authored transform into the SOURCE side's own coordinates through the EXACT SAME
    // isometry Server.WorldPortalArrivalMath uses for a crossing traveler's arrival, anchored at the two faces' own
    // frames (never a crossing's swept seam — this maps arbitrary geometry, not one traveler's own crossing point).
    // Fixed point throughout except the two float<->fixed boundary conversions (the one sanctioned rendering seam),
    // so the strip a body sees is placed by the IDENTICAL math the strip it stands on already uses.
    private static WorldPlacement MapIntoSource(WorldPlacement placement, WorldFaceFrame neighbourFrame, WorldFaceFrame sourceFrame) {
        var arrival = WorldPortalArrivalMath.ComputeArrival(
            travelerPosition: FixedVector3.FromVector3(value: placement.Position),
            travelerYawRadians: FixedQ4816.FromDouble(value: (placement.YawDegrees * (Math.PI / 180.0))),
            travelerPlanarVelocity: FixedVector3.Zero,
            travelerVerticalVelocity: FixedQ4816.Zero,
            sourcePosition: neighbourFrame.Origin,
            sourceYawRadians: neighbourFrame.PlanarYawRadians,
            destinationPosition: sourceFrame.Origin,
            destinationYawRadians: sourceFrame.PlanarYawRadians
        );

        return (placement with {
            Position = arrival.Position.ToVector3(),
            YawDegrees = (float)((double)arrival.YawRadians * (180.0 / Math.PI)),
        });
    }
}
