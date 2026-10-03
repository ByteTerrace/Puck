using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class IrradiancePlacementScheduleLawTests {
    [Fact]
    public void APartitionWaitsForAllAllocatedPositiveCornerBricksToBePlaced() {
        var schedule = new IrradianceSchedule([new IrradianceLevel("world", 1, 0, 0, 1)], [8], 64, 1, 40);
        var input = new IrradianceFrameInputs([Double3.Zero], [new IrradianceSphere(Double3.Zero, double.PositiveInfinity)], Double3.Zero, new Double3(7, 7, 7));
        var placed = new HashSet<IrradianceBrickKey>();
        var classified = new HashSet<IrradianceBrickKey>();

        for (var frame = 0; (frame < 16); frame++) {
            var plan = schedule.Frame(input);

            Assert.InRange(plan.Placed.Count, 0, 1);
            Assert.InRange(plan.Classified.Count, 0, 1);
            placed.UnionWith(plan.Placed);
            foreach (var key in plan.Classified) {
                for (var z = 0; (z <= 1); z++) {
                    for (var y = 0; (y <= 1); y++) {
                        for (var x = 0; (x <= 1); x++) {
                            var neighbour = key with { X = (key.X + x), Y = (key.Y + y), Z = (key.Z + z) };

                            if (schedule.Allocated.Contains(neighbour)) { Assert.Contains(neighbour, placed); }
                        }
                    }
                }
                classified.Add(key);
            }
            foreach (var trace in plan.Traces) { Assert.Contains(IrradianceLattice.BrickOf(trace.Probe), classified); }
        }
        Assert.Equal(8, classified.Count);
        Assert.True(schedule.IsComplete);
    }
}
