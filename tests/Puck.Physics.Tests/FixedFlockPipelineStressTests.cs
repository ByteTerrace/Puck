using System.Diagnostics;
using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class FixedFlockPipelineStressTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ThousandsOfDenseCreaturesHaveBoundedDeterministicAllocationFreeSteering(bool coincident, bool grounded) {
        const int count = 4096;
        const int steps = 48;
        var one = FixedQ4816.One;
        var zero = FixedQ4816.Zero;
        var radius = FixedQ4816.FromInteger(20);
        var weights = new FixedFlockWeights(one, one, one, one, one, one);
        var goal = new FixedVector3(zero, zero, one);
        var normal = grounded ? new FixedVector3(zero, one, zero) : FixedVector3.Zero;
        var dt = one / FixedQ4816.FromInteger(60);

        (ulong Hash, long Allocated, double Milliseconds) Run(bool reverse) {
            var points = new FixedSpatialPoint[count];
            var next = new FixedSpatialPoint[count];
            var velocities = new FixedVector3[count];
            var nextVelocities = new FixedVector3[count];
            var candidates = new FixedSpatialNeighbor[16];
            var neighbors = new FixedFlockNeighbor[16];
            var grid = new FixedSpatialNeighborhood(count, radius);
            for (var index = 0; index < count; index++) {
                points[index] = new(index, coincident ? FixedVector3.Zero : new(
                    FixedQ4816.FromInteger(index % 16),
                    grounded ? zero : FixedQ4816.FromInteger(index / 256),
                    FixedQ4816.FromInteger(index / 16 % 16)));
            }
            void Step(int step) {
                grid.Rebuild(points);
                for (var ordinal = 0; ordinal < count; ordinal++) {
                    var index = reverse ? count - ordinal - 1 : ordinal;
                    var point = points[index];
                    var work = grid.Query(point.Position, radius, index, 32, (ulong)(index + step * 32), candidates);
                    if (work.CandidatesExamined > 32 || work.NeighborsWritten > 16) {
                        throw new InvalidOperationException("Crowd density exceeded the explicit perception budget.");
                    }
                    for (var neighbor = 0; neighbor < work.NeighborsWritten; neighbor++) {
                        var other = candidates[neighbor].Index;
                        neighbors[neighbor] = new(other, points[other].Position - point.Position, velocities[other],
                            other % 3 == 0 ? zero : one, one);
                    }
                    var desired = FixedFlockSteering.Evaluate(index, velocities[index], goal, normal,
                        neighbors.AsSpan(0, work.NeighborsWritten), weights).Desired;
                    if (desired.LengthSquared.Value > one.Value + 8 || (grounded && desired.Y != zero)) {
                        throw new InvalidOperationException("Steering escaped its speed or support-plane bound.");
                    }
                    next[index] = new(index, point.Position + desired * dt);
                    nextVelocities[index] = desired;
                }
                (points, next) = (next, points);
                (velocities, nextVelocities) = (nextVelocities, velocities);
            }
            for (var step = 0; step < 8; step++) { Step(step); }
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var step = 8; step < steps + 8; step++) { Step(step); }
            var elapsed = Stopwatch.GetElapsedTime(start);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var hash = Fnv1aHash.Create();
            for (var index = 0; index < count; index++) {
                hash.Add(points[index].Position.X.Value); hash.Add(points[index].Position.Y.Value); hash.Add(points[index].Position.Z.Value);
                hash.Add(velocities[index].X.Value); hash.Add(velocities[index].Y.Value); hash.Add(velocities[index].Z.Value);
            }
            return (hash.Value, allocated, elapsed.TotalMilliseconds);
        }

        var forward = Run(false);
        var reverse = Run(true);
        Assert.Equal(forward.Hash, reverse.Hash);
        Assert.Equal(0, forward.Allocated);
        Assert.Equal(0, reverse.Allocated);
        TestContext.Current.TestOutputHelper!.WriteLine($"4096-creature {(grounded ? "ground" : "volume")} kernel, coincident={coincident}: " +
            $"{forward.Milliseconds / steps:F3} ms/full update; reverse={reverse.Milliseconds / steps:F3} ms; zero allocations. " +
            "Excludes body collision, world rules, navigation, and rendering; not an FPS measurement.");
    }
}
