using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Fields;
using Puck.Physics.Navigation;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>One body's cached route: the domain and goal cell a path was last found for, the path itself, and
    /// the search's last outcome and expansion count.</summary>
    internal sealed class BodyNavigationState {
        public int DomainIndex = -1;
        public int ExpandedLast;
        public int GoalCell = -1;
        public int PathLength;
        public NavigationStatus Status;
        public int Waypoint;

        public int[] Path { get; private set; } = [];

        public void Clear(NavigationStatus status = NavigationStatus.None) {
            DomainIndex = -1;
            ExpandedLast = 0;
            GoalCell = -1;
            PathLength = 0;
            Waypoint = 0;
            Status = status;
        }
        public Span<int> WritablePath() {
            if (Path.Length == 0) {
                Path = new int[WorldNavigationCapacity.MaxPathNodes];
            }
            return Path;
        }
    }

    // Bridges the server's own field-lattice representation to the kernel's narrow medium-field seam, so the
    // navigation runtime depends on neither the document schema nor the lattice's representation.
    private sealed class NavigationMediumFieldAdapter : INavigationMediumField {
        private readonly (FieldLattice Lattice, int Field, string Name)[] m_fields;

        public NavigationMediumFieldAdapter(IReadOnlyList<WorldFieldRegion> regions) {
            m_fields = regions.SelectMany(selector: region => region.Lattice.Input.Fields.Select(selector: (field, index) => (region.Lattice, index, field.Name))).ToArray();
        }

        public bool IsInsideMedium(int field, in FixedVector3 position, FixedQ4816 clearance) => m_fields[field].Lattice.IsInsideMedium(m_fields[field].Field, in position, clearance);
        public bool IsSegmentInsideMedium(int field, in FixedVector3 from, in FixedVector3 to, FixedQ4816 clearance, int maximumSubdivisions) =>
            m_fields[field].Lattice.IsSegmentInsideMedium(m_fields[field].Field, in from, in to, clearance, maximumSubdivisions);
        public bool TryFieldIndex(string name, out int field) {
            for (var index = 0; (index < m_fields.Length); index++) { if (m_fields[index].Name == name) { field = index; return true; } }
            field = -1; return false;
        }
        public ulong ValueRevision(int field) => m_fields[field].Lattice.ValueRevision(field: m_fields[field].Field);
    }

    /// <summary>Draws one navigation domain's baked cells as text, a row per cell along Z (ascending) and a character per
    /// cell along X: <c>#</c> for a cell no route stands on, and for a walkable cell its ground height above the lowest
    /// walkable ground in the window, in quarter metres, as a base-36 digit. Bakes the domain.</summary>
    /// <param name="name">The domain's authored name.</param>
    /// <param name="x0">The first column drawn.</param>
    /// <param name="z0">The first row drawn.</param>
    /// <param name="width">The columns drawn, clamped to the domain.</param>
    /// <param name="depth">The rows drawn, clamped to the domain.</param>
    /// <returns>The drawing, or a refusal naming the unknown domain.</returns>
    public string DescribeNavigationMap(string name, int x0, int z0, int width, int depth) {
        for (var index = 0; (index < m_navigation.Count); index++) {
            var domain = m_navigation[index];

            if (domain.Name != name) {
                continue;
            }

            var tuning = domain.Tuning;
            var x1 = Math.Min(val1: tuning.Width, val2: (Math.Max(val1: 0, val2: x0) + width));
            var z1 = Math.Min(val1: tuning.Depth, val2: (Math.Max(val1: 0, val2: z0) + depth));
            var low = FixedQ4816.MaxValue;
            var quarter = FixedQ4816.FromDouble(value: 0.25);

            x0 = Math.Max(val1: 0, val2: x0);
            z0 = Math.Max(val1: 0, val2: z0);
            for (var z = z0; (z < z1); z++) {
                for (var x = x0; (x < x1); x++) {
                    var node = ((z * tuning.Width) + x);

                    if (domain.IsWalkable(node: node) && (domain.Foot(node: node).Y < low)) {
                        low = domain.Foot(node: node).Y;
                    }
                }
            }

            var text = new System.Text.StringBuilder();

            _ = text.Append(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"[world.navigation map: {name} columns {x0}..{(x1 - 1)} rows {z0}..{(z1 - 1)} from ({((double)domain.CellCenter(node: ((z0 * tuning.Width) + x0)).X):0.###},{((double)domain.CellCenter(node: ((z0 * tuning.Width) + x0)).Z):0.###}) every {((double)tuning.CellSize):0.###} m; ground above {((low == FixedQ4816.MaxValue) ? 0.0 : (double)low):0.###} in 0.25 m digits");
            for (var z = z0; (z < z1); z++) {
                _ = text.Append(value: '\n');
                for (var x = x0; (x < x1); x++) {
                    var node = ((z * tuning.Width) + x);

                    _ = text.Append(value: (domain.IsWalkable(node: node)
                        ? "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(((domain.Foot(node: node).Y - low) / quarter).Value >> 16) % 36]
                        : '#'));
                }
            }

            return text.Append(value: ']').ToString();
        }

        return $"[world.navigation map: no domain named '{name}']";
    }
    /// <summary>Gets a navigation domain's authored name.</summary>
    /// <param name="index">The domain's index.</param>
    public string NavigationDomainName(int index) => m_navigation[index].Name;
    /// <summary>Gets the point a body standing on a route node stands at (<see cref="NavigationRuntime.Domain.Foot"/>).</summary>
    /// <param name="node">The route node.</param>
    public FixedVector3 NavigationFoot(NavigationRouteNode node) => m_navigation[node.Domain].Foot(node: node.Cell);
    /// <summary>Finds a walkable cell for a route endpoint: the cell <see cref="NavigationRuntime.TryLocateNear"/> connects to a
    /// point, or for a place the walkable cell nearest its center whose ground lies inside its volume.</summary>
    /// <param name="point">The point, or a place's center.</param>
    /// <param name="within">The place's volume, or <see langword="null"/> for a bare point.</param>
    /// <param name="domain">The cell's domain, or -1.</param>
    /// <param name="cell">The cell, or -1.</param>
    /// <returns><see langword="true"/> when a cell was found.</returns>
    public bool TryRouteEndpoint(FixedVector3 point, FixedSpatialShape? within, out int domain, out int cell) {
        if (within is not { } shape) {
            return m_navigation.TryLocateNear(cell: out cell, domain: out domain, position: in point, reach: 2);
        }

        domain = -1;
        cell = -1;
        var best = FixedQ4816.MaxValue;

        for (var index = 0; (index < m_navigation.Count); index++) {
            var candidate = m_navigation[index];

            for (var node = 0; (node < candidate.CellCount); node++) {
                // The cell's center at the place's own height first, which needs no bake, so a domain with no cell
                // under the place never bakes for it.
                if (!shape.Contains(point: candidate.CellCenter(node: node) with { Y = shape.Center.Y }) || !candidate.IsWalkable(node: node) || !shape.Contains(point: candidate.Foot(node: node))) {
                    continue;
                }

                var distance = (candidate.Foot(node: node) - shape.Center).LengthSquared;

                if (distance < best) {
                    best = distance;
                    domain = index;
                    cell = node;
                }
            }
        }

        return (domain >= 0);
    }

    /// <summary>Finds a route between two endpoint cells across the navigation domains and checks it against the
    /// contact field the bodies collide with: a body of the default seat kit sweeps between samples along the route,
    /// 0.1 m apart, proving its volume from a step height and a contact skin over the higher end's ground up to its full
    /// height (each capsule's top lowered by its lift), must meet nothing, the domains' own step rule. A contact there
    /// is a wall, a ceiling or an obstacle taller than a step.</summary>
    /// <param name="fromDomain">The start cell's domain.</param>
    /// <param name="fromCell">The start cell.</param>
    /// <param name="goal">The cells the route may end at: one cell, or a place (<see cref="WorldNavigationPlaceGoal"/>).</param>
    /// <param name="route">Receives the route, cleared first.</param>
    /// <param name="work">The search's work.</param>
    /// <param name="samples">The samples the contact check stood a body at.</param>
    /// <param name="walls">The samples where that body met geometry, in route order.</param>
    /// <returns>The search's status.</returns>
    public NavigationStatus FindCheckedRoute(int fromDomain, int fromCell, INavigationRouteGoal goal, List<NavigationRouteNode> route, out NavigationRouteWork work, out int samples, List<FixedVector3> walls) {
        ArgumentNullException.ThrowIfNull(argument: walls);
        walls.Clear();
        samples = 0;

        var status = m_navigation.FindRoute(
            fromCell: fromCell,
            fromDomain: fromDomain,
            goal: goal,
            maxExpanded: checked((int)Math.Min(val1: m_navigation.CellCount, val2: int.MaxValue)),
            maxNodes: WorldNavigationCapacity.MaxPathNodes * WorldNavigationCapacity.MaxDomains,
            route: route,
            work: out work
        );

        if (
            (route.Count == 0) ||
            (m_contactField is not { } field) ||
            (m_kits.Length == 0) ||
            (m_kits[m_seatKit].Collider is not { } collider)
        ) {
            return status;
        }

        var spacing = FixedQ4816.FromDouble(value: 0.1);
        var skin = FixedQ4816.FromDouble(value: m_censusDefinition?.Collision.ContactSkin ?? 0.02);
        var volumes = collider.Volumes.ToArray();

        for (var index = 0; (index < route.Count); index++) {
            var from = route[(index == 0) ? 0 : (index - 1)];
            var to = route[index];
            var start = m_navigation[from.Domain].Foot(node: from.Cell);
            var end = m_navigation[to.Domain].Foot(node: to.Cell);
            // The body proves its volume from a step over its ground to its full height, as a domain's occupancy proof
            // does: it stands a step and a skin over the higher end's ground with each capsule's top lowered as far,
            // so a tread or a skirting a step could climb passes under it and its head stays where it stands.
            var lift = (FixedQ4816.Max(x: m_navigation[from.Domain].Tuning.MaxStepHeight, y: m_navigation[to.Domain].Tuning.MaxStepHeight) + skin);
            var height = (FixedQ4816.Max(x: start.Y, y: end.Y) + lift);
            var planar = new FixedVector3(X: (end.X - start.X), Y: FixedQ4816.Zero, Z: (end.Z - start.Z));
            var steps = ((index == 0) ? 0 : Math.Max(val1: 1, val2: (int)planar.Length.Value.CeilingDivide(divisor: spacing.Value)));

            LiftRouteVolumes(source: collider.Volumes, destination: volumes, lift: lift);
            var previous = start with { Y = height };

            for (var step = ((index == 0) ? 0 : 1); (step <= steps); step++) {
                var fraction = ((steps == 0) ? FixedQ4816.Zero : (FixedQ4816.FromInteger(value: step) / FixedQ4816.FromInteger(value: steps)));
                var probe = new FixedVector3(X: (start.X + (planar.X * fraction)), Y: height, Z: (start.Z + (planar.Z * fraction)));
                samples++;
                if (!FixedContactProbe.IsClear(field: field, start: previous, end: probe, orientation: FixedQuaternion.Identity, up: FixedVector3.UnitY, volumes: volumes)) {
                    walls.Add(item: probe);
                }
                previous = probe;
            }
        }

        return status;
    }

    // Lowers each capsule's top by a lift, never below its base: the volume a body raised that far over its ground
    // sweeps, so its head stays where it stands. WorldDocumentNavigation's endpoint sweep lifts by it too.
    internal static void LiftRouteVolumes(ReadOnlySpan<FixedBodyColliderVolume> source, Span<FixedBodyColliderVolume> destination, FixedQ4816 lift) {
        for (var index = 0; (index < source.Length); index++) {
            var volume = source[index];

            destination[index] = ((volume.Kind == FixedBodyColliderKind.Capsule)
                ? (volume with { Endpoint = volume.Endpoint with { Y = FixedQ4816.Max(x: volume.Center.Y, y: (volume.Endpoint.Y - lift)) } })
                : volume);
        }
    }

    // Compiles the authored navigation rows into the kernel's own plain input shape once at resolve time; the
    // kernel parses no document, so every authoring enum crosses the seam through an explicit mapping rather than
    // a numeric cast that would silently drift if either side's member order ever changed.
    internal static NavigationDomainInput[] CompileNavigationDomains(WorldDefinition definition) {
        var rows = definition.Navigation.Rows;
        var domains = new NavigationDomainInput[rows.Count];

        for (var index = 0; (index < domains.Length); index++) {
            var row = rows[index];
            var compiled = FixedWorldNavigationDomain.Compile(domain: row);
            var frame = ((row.Parent is { } parent)
                ? definition.PlacementFrames[parent]
                : default
            );
            var origin = ((row.Parent is null)
                ? row.Origin
                : (frame.Position + WorldPlacementFrameCompilation.RotateY(
                    row.Origin,
                    frame.YawDegrees
                ))
            );

            domains[index] = new NavigationDomainInput(
                Name: row.Name,
                Kind: MapNavigationKind(kind: compiled.Kind),
                Origin: FixedVector3.FromVector3(value: origin),
                CellSize: compiled.CellSize,
                Width: compiled.Width,
                Depth: compiled.Depth,
                Layers: compiled.Layers,
                Connectivity: MapNavigationConnectivity(connectivity: compiled.Connectivity),
                ProbeUp: compiled.ProbeUp,
                ProbeDown: compiled.ProbeDown,
                AgentRadius: compiled.AgentRadius,
                AgentHeight: compiled.AgentHeight,
                MaxStepHeight: compiled.MaxStepHeight,
                MaximumSlopeRise: compiled.MaximumSlopeRise,
                ArrivalDistance: compiled.ArrivalDistance,
                MaxExpandedNodes: compiled.MaxExpandedNodes,
                MaxPathNodes: compiled.MaxPathNodes,
                Medium: compiled.Medium,
                Shared: ((row.Shared is { } shared)
                ? new NavigationSharing(
                        GoalCapacity: shared.GoalCapacity,
                        ExpandedNodesPerTick: shared.ExpandedNodesPerTick
                    )
                : null),
                YawRadians: FixedQ4816.FromDegrees(degrees: frame.YawDegrees)
            );
        }
        return domains;
    }
    private static NavigationKind MapNavigationKind(WorldNavigationKind kind) => kind switch {
        WorldNavigationKind.Surface => NavigationKind.Surface,
        WorldNavigationKind.Volume => NavigationKind.Volume,
        WorldNavigationKind.Medium => NavigationKind.Medium,
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(kind),
        actualValue: kind,
        message: null
    ),
    };
    private static NavigationConnectivity MapNavigationConnectivity(WorldNavigationConnectivity connectivity) => connectivity switch {
        WorldNavigationConnectivity.Axis => NavigationConnectivity.Axis,
        WorldNavigationConnectivity.FacesAndEdges => NavigationConnectivity.FacesAndEdges,
        WorldNavigationConnectivity.Full => NavigationConnectivity.Full,
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(connectivity),
        actualValue: connectivity,
        message: null
    ),
    };
}
