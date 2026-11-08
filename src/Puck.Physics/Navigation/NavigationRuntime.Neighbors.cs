namespace Puck.Physics.Navigation;

public sealed partial class NavigationRuntime {
    public sealed partial class Domain {
        /// <summary>One lattice step out of a node: the origin's coordinates, the step offsets, the neighbour node and
        /// the number of axes the step moves along (one for a face, two for an edge, three for a corner).</summary>
        private readonly record struct Step(int Node, int X, int Y, int Z, int Dx, int Dy, int Dz, int Axes) {
            /// <summary>Gets the fixed-point route cost of the step.</summary>
            public int Cost => StepCost(axes: Axes);
        }

        /// <summary>Enumerates a node's in-bounds neighbours in a fixed order (layer, then depth, then column) among
        /// the steps this domain's connectivity admits. It decides nothing about walkability or traversal; each
        /// caller applies its own edge predicate to the steps.</summary>
        private ref struct NeighborEnumerator {
            private readonly Domain m_domain;
            private readonly int m_maxY;
            private readonly int m_x;
            private readonly int m_y;
            private readonly int m_z;
            private int m_dx;
            private int m_dy;
            private int m_dz;

            public NeighborEnumerator(Domain domain, int node) {
                var surface = (domain.Tuning.Kind == NavigationKind.Surface);

                domain.Coordinates(
                    node: node,
                    x: out m_x,
                    y: out m_y,
                    z: out m_z
                );
                m_domain = domain;
                m_dx = -2;
                m_dy = (surface
                    ? 0
                    : -1
                );
                m_dz = -1;
                m_maxY = (surface
                    ? 0
                    : 1
                );
                Current = default;
            }

            public Step Current { get; private set; }

            public readonly NeighborEnumerator GetEnumerator() => this;
            public bool MoveNext() {
                var tuning = m_domain.Tuning;

                while (true) {
                    if (++m_dx > 1) {
                        m_dx = -1;
                        if (++m_dz > 1) {
                            m_dz = -1;
                            m_dy++;
                        }
                    }
                    if (m_dy > m_maxY) { return false; }
                    var axes = StepAxes(
                        dx: m_dx,
                        dy: m_dy,
                        dz: m_dz
                    );
                    var nx = (m_x + m_dx);
                    var ny = (m_y + m_dy);
                    var nz = (m_z + m_dz);

                    if (
                        (axes == 0) ||
                        !m_domain.AdmitsAxes(axes: axes) ||
                        (((uint)nx) >= ((uint)tuning.Width)) ||
                        (((uint)ny) >= ((uint)tuning.Layers)) ||
                        (((uint)nz) >= ((uint)tuning.Depth))
                    ) { continue; }
                    Current = new Step(
                        Axes: axes,
                        Dx: m_dx,
                        Dy: m_dy,
                        Dz: m_dz,
                        Node: m_domain.Index(
                            x: nx,
                            y: ny,
                            z: nz
                        ),
                        X: m_x,
                        Y: m_y,
                        Z: m_z
                    );
                    return true;
                }
            }
        }

        private NeighborEnumerator Neighbors(int node) => new(
            domain: this,
            node: node
        );
        private static int StepAxes(int dx, int dy, int dz) => ((((dx == 0)
            ? 0
            : 1) + ((dy == 0)
            ? 0
            : 1)) + ((dz == 0)
            ? 0
            : 1));
        private static int StepCost(int axes) => ((axes == 1)
            ? StraightCost
            : ((axes == 2)
                ? DiagonalCost
                : SpaceDiagonalCost
        ));
    }
}
