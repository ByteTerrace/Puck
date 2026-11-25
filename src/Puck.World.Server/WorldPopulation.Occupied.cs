using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

// WHERE HUMANS STAND. One scan finds the bodies humans occupy (NextHumanOccupied), one image holds where they stood
// when it was last read (ReadOccupied), and one query answers how near the nearest of them is to a point
// (NearestOccupiedSquared), through the sweep grid every other range query of the population uses. The population
// reads the image before it plans a tick, for its kits' far cadences, and the host reads it when it settles a world's
// sessions, for a surface's attention; both then ask the same query, so the two cannot disagree on who is near.
public sealed partial class WorldPopulation {
    // The cell the occupied image is gridded at when no kit authors a far distance: a width for the index alone, since
    // a query visits whatever cells its reach covers.
    private static readonly FixedQ4816 OccupiedCellWidth = FixedQ4816.FromInteger(value: 32L);

    private FixedSweepGrid? m_occupiedGrid;
    private FixedSweepSphere[] m_occupiedSpheres = [];
    // Each occupied body's position as of the last read, by slot: a body moves during the tick that reads it.
    private FixedVector3[] m_occupiedCenters = [];
    private int[] m_occupiedCandidates = [];
    private int m_occupiedCount;

    /// <summary>Finds the first slot at or after <paramref name="from"/> that a human occupies
    /// (<see cref="IsHumanOccupied"/>).</summary>
    /// <param name="from">The slot to search from; a value past the capacity finds none.</param>
    /// <returns>The slot, or -1 when no human occupies a slot at or after <paramref name="from"/>.</returns>
    public int NextHumanOccupied(int from) {
        for (var index = Math.Max(val1: from, val2: 0); (index < Capacity); index++) {
            if (IsHumanOccupied(bodyIndex: index)) {
                return index;
            }
        }

        return -1;
    }
    // Whether any kit authors a far cadence, and so whether a tick reads the occupied image before it plans.
    private bool HasFarCadence() {
        foreach (var kit in m_kits) {
            if (kit.AutonomousFar is not null) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Reads where the bodies humans occupy stand now, replacing the image
    /// <see cref="NearestOccupiedSquared"/> answers from. Runs between this population's steps or before a step plans
    /// its bodies, never while a kit partition runs.</summary>
    public void ReadOccupied() {
        ThrowIfPartitioned();
        if (m_occupiedGrid is null) {
            m_occupiedGrid = new FixedSweepGrid(capacity: Capacity);
            m_occupiedSpheres = new FixedSweepSphere[Capacity];
            m_occupiedCenters = new FixedVector3[Capacity];
            m_occupiedCandidates = new int[Capacity];
        }
        m_occupiedCount = 0;
        for (var index = NextHumanOccupied(from: 0); (index >= 0); index = NextHumanOccupied(from: (index + 1))) {
            if (m_entries[index].Body is not { } body) {
                continue;
            }

            var center = body.FixedPosition;

            m_occupiedCenters[index] = center;
            m_occupiedSpheres[m_occupiedCount++] = new FixedSweepSphere(Center: center, Index: index, Radius: FixedQ4816.Zero);
        }

        var width = OccupiedCellWidth;

        foreach (var kit in m_kits) {
            if (kit.AutonomousFar is { } far) { width = FixedQ4816.Max(x: width, y: far.Beyond); }
        }
        m_occupiedGrid.Rebuild(minimumCellWidth: width, spheres: m_occupiedSpheres.AsSpan(length: m_occupiedCount, start: 0));
    }
    /// <summary>Reads the squared distance from a point to the nearest body a human occupied when the image was last
    /// read (<see cref="ReadOccupied"/>), among those within <paramref name="reach"/> of it.</summary>
    /// <param name="point">The point, in this world's coordinates.</param>
    /// <param name="reach">The distance searched, at least zero.</param>
    /// <returns>The squared distance, or <see langword="null"/> when no occupied body stood within
    /// <paramref name="reach"/> of the point or the image holds none.</returns>
    public FixedQ4816? NearestOccupiedSquared(in FixedVector3 point, FixedQ4816 reach) {
        if ((m_occupiedGrid is not { } grid) || (m_occupiedCount == 0)) {
            return null;
        }

        var count = grid.Query(box: FixedAabb.Around(center: point, radius: reach), destination: m_occupiedCandidates);
        var limit = (reach * reach);
        FixedQ4816? nearest = null;

        for (var candidate = 0; (candidate < count); candidate++) {
            var squared = (m_occupiedCenters[m_occupiedCandidates[candidate]] - point).LengthSquared;

            // The box holds its corners, which lie past the reach.
            if ((squared <= limit) && ((nearest is not { } least) || (squared < least))) {
                nearest = squared;
            }
        }

        return nearest;
    }
}
