using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Navigation;
using Puck.World.Client;

namespace Puck.World.Server;

/// <summary>
/// Path planning over one world document and nothing else: its navigation domains, baked against the static geometry
/// its solid rows compile to, with the wall behind each portal opened as the bodies' own contact field opens it. It
/// holds no body, no authority state and no lattice, so a process that was only delivered a document plans the same
/// paths from it as the authority that runs it. Solids that ride a body are not in it, and a medium domain, whose
/// cells follow a lattice's live values, admits no cell.
/// </summary>
/// <remarks>Every member runs on the thread that owns the instance; a bake runs on the thread pool
/// (<see cref="NavigationRuntime.BeginBakes"/>).</remarks>
public sealed class WorldDocumentNavigation : IWorldSeatNavigation {
    private readonly WorldColliderSet? m_colliders;
    private readonly List<NavigationRouteNode> m_cornerNodes = [];
    private readonly IContactField? m_field;
    private readonly List<NavigationRouteNode> m_routeNodes = [];
    private readonly NavigationRuntime m_runtime;

    private WorldDocumentNavigation(WorldDefinition definition, WorldColliderSet? colliders, IContactField? field, NavigationRuntime runtime) {
        Definition = definition;
        m_colliders = colliders;
        m_field = field;
        m_runtime = runtime;
    }

    /// <summary>Gets the document the navigation was compiled from.</summary>
    public WorldDefinition Definition { get; }
    /// <inheritdoc/>
    public (int Baked, int Declared) Domains => (m_runtime.BakedDomainCount, m_runtime.Count);
    /// <inheritdoc/>
    public bool Ready => m_runtime.IsBaked;

    /// <summary>Compiles a document's navigation.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="previous">The navigation compiled from an earlier revision of the document, whose colliders and
    /// bakes are taken for every row and domain that stands as it was, or <see langword="null"/> to compile all.</param>
    /// <returns>The navigation, its domains unbaked unless <paramref name="previous"/> carried their bakes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldDocumentNavigation Compile(WorldDefinition definition, WorldDocumentNavigation? previous = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        WorldColliderSet? colliders = null;
        IContactField? geometry = null;
        var areas = WorldNavigationAreas.Compile(definition: definition);

        if (!WorldContactSelection.RequiresField(collision: definition.Collision)) {
            colliders = WorldColliderSet.Build(definition: definition, previous: previous?.m_colliders);
            geometry = colliders.StaticField;
        } else if (WorldSolidField.TryBuild(built: out var solids, definition: definition, reason: out _)) {
            geometry = solids!.StaticField;
        }

        return new WorldDocumentNavigation(
            colliders: colliders,
            definition: definition,
            field: ((geometry is null) ? null : WorldPortalHoleContactField.Wrap(definition: definition, inner: geometry)),
            runtime: new NavigationRuntime(
                capacity: new NavigationCapacity(
                    GoalFieldBytes: WorldNavigationCapacity.MaxGoalFieldBytes,
                    MaxMediumSegmentSubdivisions: WorldNavigationCapacity.MaxMediumSegmentSubdivisions
                ),
                areas: areas.Set,
                domains: WorldPopulation.CompileNavigationDomains(areas: areas, definition: definition),
                fields: null,
                geometry: geometry,
                previous: previous?.m_runtime
            )
        );
    }
    /// <inheritdoc/>
    public WorldSeatPlan Plan(in WorldSeatWalker walker, in WorldSeatGoal goal, FixedQ4816 margin, List<FixedVector3> corners, List<FixedVector3> route) {
        ArgumentNullException.ThrowIfNull(argument: corners);
        ArgumentNullException.ThrowIfNull(argument: route);
        corners.Clear();
        route.Clear();
        m_cornerNodes.Clear();
        m_routeNodes.Clear();

        INavigationRouteGoal destination;

        if (goal.Place is { } place) {
            destination = new WorldNavigationPlaceGoal(Shape: place);
        } else if (TryLocate(cell: out var goalCell, domain: out var goalDomain, fromPoint: false, point: goal.Point, walker: in walker)) {
            destination = new NavigationCellGoal(Cell: goalCell, Domain: goalDomain, Position: m_runtime[goalDomain].Foot(node: goalCell));
        } else {
            return new WorldSeatPlan(Refusal: "no navigation domain holds a walkable cell at the goal", StartsOnCell: false);
        }
        if (!TryLocate(cell: out var cell, domain: out var domain, fromPoint: true, point: walker.Position, walker: in walker)) {
            return new WorldSeatPlan(Refusal: "no walkable cell lies within reach of the body", StartsOnCell: false);
        }

        var status = m_runtime.FindRoute(
            fromCell: cell,
            fromDomain: domain,
            goal: destination,
            maxExpanded: checked((int)Math.Min(val1: m_runtime.CellCount, val2: int.MaxValue)),
            maxNodes: (WorldNavigationCapacity.MaxPathNodes * WorldNavigationCapacity.MaxDomains),
            route: m_routeNodes,
            work: out _
        );

        m_runtime.Straighten(corners: m_cornerNodes, margin: margin, route: m_routeNodes);
        if ((status is not (NavigationStatus.Active or NavigationStatus.Arrived)) || (m_cornerNodes.Count == 0)) {
            return new WorldSeatPlan(Refusal: status.ToString().ToLowerInvariant(), StartsOnCell: false);
        }

        foreach (var node in m_cornerNodes) {
            corners.Add(item: m_runtime[node.Domain].Foot(node: node.Cell));
        }
        foreach (var node in m_routeNodes) {
            route.Add(item: m_runtime[node.Domain].Foot(node: node.Cell));
        }

        // The route starts at the cell under the body, or the nearest one it reaches. A body that can walk straight
        // from where it stands to the next corner, keeping the margin, steers there at once; one that cannot (standing
        // nearer a wall than a cell's center may, as a body just out of a door does) steps onto that cell first.
        var startsOnCell = true;

        if ((corners.Count > 1) && (m_cornerNodes[1].Domain == m_cornerNodes[0].Domain) &&
            m_runtime[m_cornerNodes[0].Domain].IsStraightClear(from: walker.Position, margin: margin, to: corners[1])) {
            corners.RemoveAt(index: 0);
            startsOnCell = false;
        }
        if (goal.Place is null) {
            corners.Add(item: goal.Point);
        }

        return new WorldSeatPlan(Refusal: null, StartsOnCell: startsOnCell);
    }
    /// <inheritdoc/>
    public void Prepare() => m_runtime.BeginBakes();

    // Finds a cell joined to a point by a clear sweep of the walker's volume, raised a step so a tread it climbs passes
    // under it: from the point to the cell for the body's own foot, from the cell to the point for a goal.
    private bool TryLocate(in WorldSeatWalker walker, FixedVector3 point, bool fromPoint, out int domain, out int cell) {
        var (field, orientation, step, authored) = (m_field, walker.Orientation, walker.StepHeight, walker.Volumes);

        return m_runtime.TryLocateNear(cell: out cell, domain: out domain, position: in point, reach: 2, connects: (candidate, node) => {
            if ((field is null) || (candidate.Tuning.Kind != NavigationKind.Surface)) {
                return true;
            }

            var foot = candidate.Foot(node: node);
            var stepHeight = FixedQ4816.Min(x: step, y: candidate.Tuning.MaxStepHeight);

            if (FixedQ4816.Abs(value: (point.Y - foot.Y)) > stepHeight) {
                return false;
            }

            var height = (FixedQ4816.Max(x: foot.Y, y: point.Y) + stepHeight);
            var start = (fromPoint ? point : foot) with { Y = height };
            var end = (fromPoint ? foot : point) with { Y = height };
            Span<FixedBodyColliderVolume> volumes = stackalloc FixedBodyColliderVolume[authored.Length];

            WorldPopulation.LiftRouteVolumes(destination: volumes, lift: stepHeight, source: authored.Span);
            return FixedContactProbe.IsClear(field: field, start: start, end: end, orientation: orientation, up: FixedVector3.UnitY, volumes: volumes);
        });
    }
}
