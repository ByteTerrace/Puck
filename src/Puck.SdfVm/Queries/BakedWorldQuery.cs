using Puck.Maths;

namespace Puck.SdfVm.Queries;

/// <summary>
/// Pure fixed-point <see cref="IWorldQuery"/> over a baked <see cref="WorldQueryArtifact"/> — the query-namespace
/// generalization of <c>Puck.Demo.Overworld.FixedWalkGrid</c> (adds the heightfield layer and the cast/overlap
/// verbs the walk grid never needed; the blocked-bitmap semantics are the SAME "out of bounds reads as not
/// blocked" contract). Every answer carries <see cref="WorldQueryConfidence.Bounded"/> — a baked artifact is
/// resolution-quantized by construction, never sub-cell-exact. Assumes every position lies in a single
/// <see cref="WorldCoord3"/> cell (cell 0,0,0 — true for anything room/arena scale); a caller spanning multiple
/// 2^20-unit cells must normalize positions into the SAME cell before querying (this provider reads only
/// <c>.Local</c>, matching every other room-scale fixed-point consumer in the demo).
/// </summary>
public sealed class BakedWorldQuery : IWorldQuery {
    private readonly WorldQueryArtifact m_artifact;
    private readonly FixedQ4816 m_cellSize;

    /// <summary>Wraps a baked artifact.</summary>
    /// <param name="artifact">The baked artifact to query.</param>
    public BakedWorldQuery(WorldQueryArtifact artifact) {
        ArgumentNullException.ThrowIfNull(argument: artifact);

        m_artifact = artifact;
        m_cellSize = FixedQ4816.FromRawBits(value: artifact.CellSizeRaw);
    }

    /// <inheritdoc/>
    public QueryCapabilities Capabilities => new(HasBlocked: m_artifact.HasBlocked, HasHeightfield: m_artifact.HasHeightfield, HasOccupancy: false);

    /// <inheritdoc/>
    public bool TryGroundHeight(WorldCoord3 position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY) {
        groundY = FixedQ4816.Zero;

        if (!m_artifact.HasHeightfield || !TryCellIndex(x: position.Local.X, z: position.Local.Z, cellIndex: out var cellIndex)) {
            return false;
        }

        var raw = m_artifact.HeightRaw[cellIndex];

        if (raw == WorldQueryArtifact.NoHeightSentinel) {
            return false;
        }

        var candidate = FixedQ4816.FromRawBits(value: raw);
        var minY = (position.Local.Y - probeDown);
        var maxY = (position.Local.Y + probeUp);

        if ((candidate < minY) || (candidate > maxY)) {
            return false;
        }

        groundY = candidate;

        return true;
    }

    /// <inheritdoc/>
    public bool Overlap(WorldCoord3 center, FixedQ4816 radius) {
        if (!m_artifact.HasBlocked) {
            return false;
        }

        return AnyBlockedWithinRadius(x: center.Local.X, z: center.Local.Z, radius: radius);
    }

    /// <inheritdoc/>
    public bool LineOfSight(WorldCoord3 from, WorldCoord3 to) {
        if (!m_artifact.HasBlocked) {
            return true;
        }

        var delta = (to.Local - from.Local);
        var distance = new FixedVector3(X: delta.X, Y: FixedQ4816.Zero, Z: delta.Z).Length;

        if (distance <= FixedQ4816.Zero) {
            return true;
        }

        var steps = Math.Max(val1: 1, val2: ((int)((double)distance / (double)m_cellSize) + 1));
        var stepInverse = (FixedQ4816.One / FixedQ4816.FromInteger(value: steps));

        for (var step = 1; (step < steps); step++) {
            var t = (stepInverse * FixedQ4816.FromInteger(value: step));
            var x = (from.Local.X + (delta.X * t));
            var z = (from.Local.Z + (delta.Z * t));

            if (TryCellIndex(x: x, z: z, cellIndex: out var cellIndex) && IsBlockedCell(cellIndex: cellIndex)) {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public bool Raycast(WorldCoord3 origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit) =>
        March(origin: origin, dir: dir, maxDist: maxDist, radius: FixedQ4816.Zero, hit: out hit);

    /// <inheritdoc/>
    public bool SphereCast(WorldCoord3 origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit) =>
        March(origin: origin, dir: dir, maxDist: maxDist, radius: radius, hit: out hit);

    // A single stepped march shared by Raycast (radius == 0) and SphereCast (radius > 0): steps in CELL-SIZE
    // increments along the normalized direction, testing the blocked bitmap (padded by radius for the sphere case)
    // and the heightfield (a sample below its cell's authored ground counts as a hit on the ground plane). Integer
    // step COUNT, fixed-point step math throughout — deterministic, no trig.
    private bool March(WorldCoord3 origin, FixedVector3 dir, FixedQ4816 maxDist, FixedQ4816 radius, out RayHit hit) {
        hit = default;

        var direction = dir.Normalize();

        if ((direction == FixedVector3.Zero) || (maxDist <= FixedQ4816.Zero)) {
            return false;
        }

        var step = (direction * m_cellSize);
        var steps = ((int)((double)maxDist / (double)m_cellSize) + 1);
        var position = origin.Local;
        var traveled = FixedQ4816.Zero;

        for (var i = 0; (i <= steps); i++) {
            var blocked = ((radius > FixedQ4816.Zero) ? AnyBlockedWithinRadius(x: position.X, z: position.Z, radius: radius) : (TryCellIndex(x: position.X, z: position.Z, cellIndex: out var cellIndex) && IsBlockedCell(cellIndex: cellIndex)));
            var groundHit = (m_artifact.HasHeightfield && TryCellIndex(x: position.X, z: position.Z, cellIndex: out var groundCell) && (m_artifact.HeightRaw[groundCell] != WorldQueryArtifact.NoHeightSentinel) && (position.Y <= FixedQ4816.FromRawBits(value: m_artifact.HeightRaw[groundCell])));

            if (blocked || groundHit) {
                hit = new RayHit(
                    Confidence: WorldQueryConfidence.Bounded,
                    Distance: traveled,
                    Material: -1,
                    Normal: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero),
                    Point: WorldCoord3.FromLocal(local: position)
                );

                return true;
            }

            if (traveled >= maxDist) {
                break;
            }

            position += step;
            traveled += m_cellSize;
        }

        return false;
    }
    private bool AnyBlockedWithinRadius(FixedQ4816 x, FixedQ4816 z, FixedQ4816 radius) {
        if (!m_artifact.HasBlocked) {
            return false;
        }

        var cellsRadius = Math.Max(val1: 1, val2: ((int)((double)radius / (double)m_cellSize) + 1));

        if (!TryCellIndices(x: x, z: z, column: out var centerColumn, row: out var centerRow)) {
            return false;
        }

        var radiusRaw = radius.Value;
        var radiusSquared = (radiusRaw * radiusRaw);

        for (var row = Math.Max(val1: 0, val2: (centerRow - cellsRadius)); (row <= Math.Min(val1: (m_artifact.Height - 1), val2: (centerRow + cellsRadius))); row++) {
            for (var column = Math.Max(val1: 0, val2: (centerColumn - cellsRadius)); (column <= Math.Min(val1: (m_artifact.Width - 1), val2: (centerColumn + cellsRadius))); column++) {
                var cellIndex = ((row * m_artifact.Width) + column);

                if (!IsBlockedCell(cellIndex: cellIndex)) {
                    continue;
                }

                var cellCenterXRaw = (m_artifact.OriginXRaw + (((long)column * m_artifact.CellSizeRaw) + (m_artifact.CellSizeRaw / 2)));
                var cellCenterZRaw = (m_artifact.OriginZRaw + (((long)row * m_artifact.CellSizeRaw) + (m_artifact.CellSizeRaw / 2)));
                var dx = (x.Value - cellCenterXRaw);
                var dz = (z.Value - cellCenterZRaw);
                var distanceSquared = ((dx * dx) + (dz * dz));

                if (distanceSquared <= radiusSquared) {
                    return true;
                }
            }
        }

        return false;
    }
    private bool IsBlockedCell(int cellIndex) {
        var word = (cellIndex >> 6);

        return ((word >= 0) && (word < m_artifact.Blocked.Length) && ((m_artifact.Blocked[word] & (1UL << (cellIndex & 63))) != 0UL));
    }
    private bool TryCellIndex(FixedQ4816 x, FixedQ4816 z, out int cellIndex) {
        if (!TryCellIndices(x: x, z: z, column: out var column, row: out var row)) {
            cellIndex = -1;

            return false;
        }

        cellIndex = ((row * m_artifact.Width) + column);

        return true;
    }
    private bool TryCellIndices(FixedQ4816 x, FixedQ4816 z, out int column, out int row) {
        column = -1;
        row = -1;

        if ((m_artifact.Width <= 0) || (m_artifact.Height <= 0) || (m_artifact.CellSizeRaw <= 0L)) {
            return false;
        }

        var columnLong = FloorDiv(dividend: (x.Value - m_artifact.OriginXRaw), divisor: m_artifact.CellSizeRaw);
        var rowLong = FloorDiv(dividend: (z.Value - m_artifact.OriginZRaw), divisor: m_artifact.CellSizeRaw);

        if ((columnLong < 0L) || (columnLong >= m_artifact.Width) || (rowLong < 0L) || (rowLong >= m_artifact.Height)) {
            return false;
        }

        column = (int)columnLong;
        row = (int)rowLong;

        return true;
    }
    private static long FloorDiv(long dividend, long divisor) {
        var quotient = (dividend / divisor);
        var remainder = (dividend % divisor);

        return (((remainder != 0L) && ((remainder < 0L) != (divisor < 0L))) ? (quotient - 1L) : quotient);
    }
}
