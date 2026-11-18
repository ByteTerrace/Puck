using Puck.Maths;

namespace Puck.Physics.Navigation;

public sealed partial class NavigationRuntime {
    // The most later nodes one corner looks along for the farthest it sees, and the failed sightings in a row after which
    // it stops looking: a long open stretch is searched whole, a winding one stops early.
    private const int MaxSightNodes = 512;
    private const int MaxSightMisses = 12;

    /// <summary>Reduces a route to the corners a walker turns at: from each kept node, the farthest later node of the
    /// same domain it can walk to in a straight line (<see cref="Domain.IsStraightClear"/>), keeping every junction hop
    /// between domains. The first and last nodes are always kept.</summary>
    /// <param name="route">The route (<see cref="FindRoute"/>).</param>
    /// <param name="margin">The clearance, in metres, a straight segment keeps from the edge of the walkable cells, on
    /// top of the agent radius the domain's cells already keep.</param>
    /// <param name="corners">Receives the kept nodes in route order, cleared first.</param>
    /// <exception cref="ArgumentNullException"><paramref name="route"/> or <paramref name="corners"/> is <see langword="null"/>.</exception>
    public void Straighten(IReadOnlyList<NavigationRouteNode> route, FixedQ4816 margin, List<NavigationRouteNode> corners) {
        ArgumentNullException.ThrowIfNull(argument: route);
        ArgumentNullException.ThrowIfNull(argument: corners);
        corners.Clear();
        if (route.Count == 0) {
            return;
        }

        var anchor = 0;

        corners.Add(item: route[0]);
        while (anchor < (route.Count - 1)) {
            var from = route[anchor];
            var best = (anchor + 1);

            if (route[best].Domain == from.Domain) {
                var domain = m_domains[from.Domain];
                var start = domain.Foot(node: from.Cell);
                var misses = 0;

                for (var next = (anchor + 1); ((next < route.Count) && ((next - anchor) <= MaxSightNodes) && (route[next].Domain == from.Domain)); next++) {
                    if (domain.IsStraightClear(from: start, margin: margin, to: domain.Foot(node: route[next].Cell))) {
                        best = next;
                        misses = 0;
                    } else if (++misses >= MaxSightMisses) {
                        break;
                    }
                }
            }

            corners.Add(item: route[best]);
            anchor = best;
        }
    }

    /// <summary>Finds the walkable cell a body standing at a position starts a route from: the cell
    /// <see cref="TryLocate"/> names there, or, where the body stands closer to an edge than a cell's center may (against
    /// a wall, in a doorway), the nearest walkable cell within <paramref name="reach"/> cells of it in a domain whose
    /// height band holds the position. Bakes each domain it asks.</summary>
    /// <param name="position">The body's foot.</param>
    /// <param name="reach">The most cells, along X or Z, the search looks away from the position's own cell.</param>
    /// <param name="domain">The cell's domain, or -1.</param>
    /// <param name="cell">The cell, or -1.</param>
    /// <returns><see langword="true"/> when a cell was found.</returns>
    public bool TryLocateNear(in FixedVector3 position, int reach, out int domain, out int cell) {
        if (TryLocate(cell: out cell, domain: out domain, position: in position)) {
            return true;
        }

        var best = Int128.MaxValue;

        for (var index = 0; (index < m_domains.Length); index++) {
            var candidate = m_domains[index];

            if (!candidate.CoversRouteHeight(position: in position)) {
                continue;
            }

            for (var dz = -reach; (dz <= reach); dz++) {
                for (var dx = -reach; (dx <= reach); dx++) {
                    var probe = new FixedVector3(
                        X: (position.X + (candidate.Tuning.CellSize * FixedQ4816.FromInteger(value: dx))),
                        Y: position.Y,
                        Z: (position.Z + (candidate.Tuning.CellSize * FixedQ4816.FromInteger(value: dz)))
                    );

                    if (!candidate.TryCell(node: out var node, position: in probe)) {
                        continue;
                    }

                    var offset = (candidate.Foot(node: node) - position);
                    var distance = ((((Int128)offset.X.Value) * offset.X.Value) + (((Int128)offset.Y.Value) * offset.Y.Value) + (((Int128)offset.Z.Value) * offset.Z.Value));

                    if (distance < best) {
                        best = distance;
                        domain = index;
                        cell = node;
                    }
                }
            }
        }

        return (domain >= 0);
    }

    public sealed partial class Domain {
        /// <summary>Returns whether a walker can go in a straight line between two points of this surface domain: every
        /// sample along the segment, half a cell apart, stands on a walkable cell, and so does every point
        /// <paramref name="margin"/> from it along X and along Z; each sample's ground lies within a step of the one
        /// before it and of its margin points' grounds; and each move from one sample's cell to the next is an edge
        /// the bake proved traversable. A volume or medium domain answers <see langword="false"/>. Bakes the
        /// domain.</summary>
        /// <param name="from">The start, a body's foot or a cell's foot; only X and Z are read.</param>
        /// <param name="to">The end.</param>
        /// <param name="margin">The clearance, in metres, kept from the edge of the walkable cells.</param>
        /// <returns><see langword="true"/> when the segment is clear.</returns>
        public bool IsStraightClear(in FixedVector3 from, in FixedVector3 to, FixedQ4816 margin) {
            if (Tuning.Kind != NavigationKind.Surface) {
                return false;
            }

            var dx = (to.X - from.X);
            var dz = (to.Z - from.Z);
            var length = FixedQ4816.Sqrt(value: ((dx * dx) + (dz * dz)));
            var spacing = (Tuning.CellSize / FixedQ4816.FromInteger(value: 2));
            var samples = Math.Max(val1: 1, val2: checked((int)(((length.Value + spacing.Value) - 1L) / spacing.Value)));
            var step = Tuning.MaxStepHeight;
            var previous = -1;
            var previousGround = FixedQ4816.Zero;

            for (var index = 0; (index <= samples); index++) {
                var fraction = (FixedQ4816.FromInteger(value: index) / FixedQ4816.FromInteger(value: samples));
                var point = new FixedVector3(X: (from.X + (dx * fraction)), Y: from.Y, Z: (from.Z + (dz * fraction)));

                if (!TryCell(node: out var cell, position: in point)) {
                    return false;
                }

                var ground = m_ground[cell];

                if (previous >= 0) {
                    if ((FixedQ4816.Abs(value: (ground - previousGround)) > step) || ((cell != previous) && !IsTraversableEdge(current: previous, next: cell))) {
                        return false;
                    }
                }
                if (!MarginClear(ground: ground, margin: margin, point: in point, step: step)) {
                    return false;
                }

                previous = cell;
                previousGround = ground;
            }

            return true;
        }

        // Whether the four points a margin from a sample along X and Z stand on walkable cells whose ground lies within
        // a step of the sample's.
        private bool MarginClear(in FixedVector3 point, FixedQ4816 margin, FixedQ4816 ground, FixedQ4816 step) {
            if (margin <= FixedQ4816.Zero) {
                return true;
            }

            ReadOnlySpan<(int X, int Z)> sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

            foreach (var (sx, sz) in sides) {
                var side = new FixedVector3(X: (point.X + (margin * FixedQ4816.FromInteger(value: sx))), Y: point.Y, Z: (point.Z + (margin * FixedQ4816.FromInteger(value: sz))));

                if (!TryCell(node: out var cell, position: in side) || (FixedQ4816.Abs(value: (m_ground[cell] - ground)) > step)) {
                    return false;
                }
            }

            return true;
        }
    }
}
