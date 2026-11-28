using Xunit;
using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.State.Tests;

/// <summary>A topology compiled in an anchor's frame turns with the anchor's yaw: its authored origin and every cell
/// centre land where the anchor's own quaternion rotation (the one every placement frame uses) puts them, exactly at a
/// quarter turn, and every centre resolves back to its own cell, so a board placed at any heading plays as it does
/// unturned.</summary>
public sealed class AnchoredTopologyLawTests {
    private static readonly LatticeTopology.Grid Board = new(
        Name: "board",
        Origin: new DocumentVector3(x: -0.86f, y: 1.45f, z: -0.8f),
        CellSize: 0.15f,
        Width: 8,
        Depth: 6,
        Band: 0.12f
    );
    private static readonly FixedVector3 Anchor = new(
        X: FixedQ4816.FromDouble(value: 34.5),
        Y: FixedQ4816.FromDouble(value: 8.55),
        Z: FixedQ4816.FromDouble(value: 44.75)
    );

    [Theory]
    [InlineData(0d)]
    [InlineData(90d)]
    [InlineData(180d)]
    [InlineData(270d)]
    [InlineData(-90d)]
    [InlineData(37d)]
    public void CellsTurnWithTheAnchorAndResolveBackToThemselves(double yawDegrees) {
        var yaw = FixedQ4816.FromDegrees(degrees: yawDegrees);
        var topology = TopologyCompilation.Compile(anchorPosition: Anchor, anchorYawRadians: yaw, topology: Board);
        var rotation = FixedQuaternion.FromAxisAngle(axis: FixedVector3.UnitY, angle: yaw);
        var quarters = (((yawDegrees % 90d) == 0d) ? (((((long)(yawDegrees / 90d)) % 4L) + 4L) % 4L) : -1L);

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            var local = new FixedVector3(
                X: (FixedQ4816.FromDouble(value: -0.86f) + topology.LocalCellCentre(cell: cell).X),
                Y: FixedQ4816.FromDouble(value: 1.45f),
                Z: (FixedQ4816.FromDouble(value: -0.8f) + topology.LocalCellCentre(cell: cell).Z)
            );
            var expected = (Anchor + rotation.Rotate(vector: local));
            var centre = topology.CellCentre(cell: cell);

            Assert.InRange(actual: Math.Abs(value: (centre.X.Value - expected.X.Value)), low: 0L, high: 4L);
            Assert.Equal(expected: local.Y + Anchor.Y, actual: centre.Y);
            Assert.InRange(actual: Math.Abs(value: (centre.Z.Value - expected.Z.Value)), low: 0L, high: 4L);
            if (quarters >= 0L) {
                var turned = (quarters switch {
                    0L => local,
                    1L => new FixedVector3(X: local.Z, Y: local.Y, Z: -local.X),
                    2L => new FixedVector3(X: -local.X, Y: local.Y, Z: -local.Z),
                    _ => new FixedVector3(X: -local.Z, Y: local.Y, Z: local.X),
                });

                Assert.Equal(expected: (Anchor + turned), actual: centre);
            }
            Assert.True(condition: topology.TryCellOf(cell: out var resolved, position: centre));
            Assert.Equal(expected: cell, actual: resolved);
        }
    }
}
