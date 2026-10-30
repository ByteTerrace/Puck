using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    /// <summary>Gets the demand points the bake measures its groups by: the residency's, which it observes from each frame
    /// before dressing it. A bake no residency observes for has none, and keeps its plan's order.</summary>
    public SdfMeshDemand Demand { get; init; } = new();

    // The latest dressed frame's dynamic transforms, which carry a dynamic group's demand sphere into world space.
    private IReadOnlyList<DynamicTransform> m_demandTransforms = [];
    // When the latest bake that baked or loaded a group started (a Stopwatch timestamp; zero before one).
    private long m_bakeStarted;
    // The store loads the latest bake queued and finished, and when its first began and last ended.
    private int m_loadsQueued;
    private int m_loadsDone;
    private long m_loadFirst;
    private long m_loadLast;
    // How long the latest plan made on a build's thread, and the latest restart on the frame's, took.
    private double m_planMilliseconds;
    private double m_restartMilliseconds;
    // Whether the frame thread has seen every group resident since the groups or a mesh last changed.
    private bool m_allResident;

    // A group's demand sphere in world space: a dynamic group's carried out of its slot's frame by the slot's transform.
    private (Vector3 Center, float Radius) WorldSphere(Group group) {
        if (!group.Dynamic || (group.Slot >= m_demandTransforms.Count)) { return (group.DemandCenter, group.DemandRadius); }

        var transform = m_demandTransforms[group.Slot];
        var units = SdfMeshGeometryBuilder.UnitsOf(transform: transform);
        var orientation = ((transform.Orientation == default) ? Quaternion.Identity : transform.Orientation);

        return ((transform.Position + Vector3.Transform(rotation: orientation, value: (group.DemandCenter * units))), (group.DemandRadius * units));
    }

    /// <summary>Stamps, on the frame's thread after the frame's mesh region is staged, each group the bake has finished and
    /// each whose mesh's words the region holds, which <see cref="DescribeResidency"/> times from the bake's start.</summary>
    /// <param name="tables">The residency's tables, whose mesh region holds a mesh's words or not
    /// (<see cref="SdfWorldTables.HoldsMesh"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="tables"/> is <see langword="null"/>.</exception>
    public void ObserveResidency(SdfWorldTables tables) {
        ArgumentNullException.ThrowIfNull(argument: tables);

        lock (m_gate) {
            if (m_allResident) { return; }

            var now = Stopwatch.GetTimestamp();
            var all = true;

            foreach (var group in m_groups) {
                if (!group.Done) {
                    all = false;
                    continue;
                }
                if (group.LandedAt == 0) { group.LandedAt = now; }
                if (group.ResidentAt != 0) { continue; }
                if (group.Mesh is { } mesh) {
                    var held = true;

                    if (mesh.Chunks.Count == 0) {
                        held = tables.HoldsMesh(mesh: mesh);
                    } else {
                        foreach (var chunk in mesh.Chunks) { held &= tables.HoldsMesh(mesh: chunk); }
                    }
                    if (!held) {
                        all = false;
                        continue;
                    }
                }
                group.ResidentAt = now;
            }
            m_allResident = all;
        }
    }

    /// <summary>Describes the latest bake's residency: its groups, when they landed (finished) and became resident (their
    /// words in the mesh region) counted from its start, those within <paramref name="radius"/> of a demand point apart,
    /// its store loads, the time its plan and restart took, and the resident meshes' region bytes by level of
    /// detail.</summary>
    /// <param name="radius">The distance, in metres, from a demand point to a group's demand sphere within which the group
    /// counts as near.</param>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeResidency(float radius) {
        lock (m_gate) {
            var start = m_bakeStarted;
            var groups = m_groups.Count;
            int landed = 0, resident = 0, near = 0, nearLanded = 0, nearResident = 0;
            long lastLanded = 0, lastResident = 0, nearLastLanded = 0, nearLastResident = 0;
            long vertexBytes = 0;
            Span<long> levelBytes = stackalloc long[3];
            Span<long> levelTriangles = stackalloc long[3];

            foreach (var group in m_groups) {
                var (center, sphere) = WorldSphere(group: group);
                var isNear = ((group.DemandRadius >= 0f) && (Demand.Distance(center: center, radius: sphere) <= radius));

                near += (isNear ? 1 : 0);
                if (group.LandedAt != 0) {
                    landed++;
                    lastLanded = Math.Max(val1: lastLanded, val2: group.LandedAt);
                    if (isNear) {
                        nearLanded++;
                        nearLastLanded = Math.Max(val1: nearLastLanded, val2: group.LandedAt);
                    }
                }
                if (group.ResidentAt != 0) {
                    resident++;
                    lastResident = Math.Max(val1: lastResident, val2: group.ResidentAt);
                    if (isNear) {
                        nearResident++;
                        nearLastResident = Math.Max(val1: nearLastResident, val2: group.ResidentAt);
                    }
                }
                if (group.Mesh is not { } mesh) { continue; }
                foreach (var piece in ((mesh.Chunks.Count == 0) ? (IReadOnlyList<SdfMesh>)[mesh] : mesh.Chunks)) {
                    var perTriangle = ((3 + (piece.TriangleMaterials.IsEmpty ? 0 : 1)) * sizeof(uint));

                    vertexBytes += (((long)piece.Positions.Length) * SdfMeshRecord.VertexWords * sizeof(uint));
                    levelTriangles[0] += piece.TriangleCount;
                    levelBytes[0] += (((long)piece.TriangleCount) * perTriangle);
                    for (var level = 0; ((level < piece.Levels.Count) && (level < 2)); level++) {
                        levelTriangles[(level + 1)] += piece.Levels[level].TriangleCount;
                        levelBytes[(level + 1)] += (((long)piece.Levels[level].TriangleCount) * perTriangle);
                    }
                }
            }

            string Since(long stamp, bool complete) => (!complete
                ? "pending"
                : ((start == 0) ? "-" : string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Math.Max(val1: 0d, val2: Stopwatch.GetElapsedTime(startingTimestamp: start, endingTimestamp: stamp).TotalSeconds):0.000}s")));

            var line = new StringBuilder();
            var loadSpan = (((m_loadFirst != 0) && (m_loadLast != 0)) ? Stopwatch.GetElapsedTime(startingTimestamp: m_loadFirst, endingTimestamp: m_loadLast).TotalSeconds : 0d);

            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $"residency={Residency} demand-points={Demand.Count} groups={groups} landed={landed} resident={resident} all-landed={Since(stamp: lastLanded, complete: (landed == groups))} all-resident={Since(stamp: lastResident, complete: (resident == groups))}");
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" near-radius={radius:0.##}m near={near} near-landed={nearLanded} near-resident={nearResident} near-landed-at={Since(stamp: nearLastLanded, complete: (nearLanded == near))} near-resident-at={Since(stamp: nearLastResident, complete: (nearResident == near))}");
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" bake-age={((start == 0) ? 0d : Stopwatch.GetElapsedTime(startingTimestamp: start).TotalSeconds):0.000}s loads={m_loadsDone}/{m_loadsQueued} load-span={loadSpan:0.000}s plan-ms={m_planMilliseconds:0.0} restart-ms={m_restartMilliseconds:0.0}");
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" vertex-bytes={vertexBytes} level0={levelTriangles[0]}tri/{levelBytes[0]}B level1={levelTriangles[1]}tri/{levelBytes[1]}B level2={levelTriangles[2]}tri/{levelBytes[2]}B");

            return line.ToString();
        }
    }
}
