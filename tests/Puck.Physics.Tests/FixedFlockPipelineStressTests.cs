using System.Diagnostics;
using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class FixedFlockPipelineStressTests {
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void ThousandsOfDenseCreaturesHaveBoundedDeterministicAllocationFreeSteering(bool coincident, bool grounded) {
        const int Count = 4096;
        // Each update covers all 4096 bodies and the reverse-order replay; eight updates exercise evolving neighborhoods.
        const int Steps = 8;
        var one = FixedQ4816.One;
        var zero = FixedQ4816.Zero;
        var radius = FixedQ4816.FromInteger(value: 20);
        var weights = new FixedFlockWeights(
            Alignment: one,
            Cohesion: one,
            Goal: one,
            Inertia: one,
            Separation: one,
            SeparationRadius: one
        );
        var goal = new FixedVector3(
            X: zero,
            Y: zero,
            Z: one
        );
        var normal = (grounded
            ? new FixedVector3(
                X: zero,
                Y: one,
                Z: zero
            )
            : FixedVector3.Zero
        );
        var dt = (one / FixedQ4816.FromInteger(value: 60));

        (ulong Hash, long Allocated, double Milliseconds) Run(bool reverse) {
            var points = new FixedSpatialPoint[Count];
            var next = new FixedSpatialPoint[Count];
            var velocities = new FixedVector3[Count];
            var nextVelocities = new FixedVector3[Count];
            var candidates = new FixedSpatialNeighbor[16];
            var neighbors = new FixedFlockNeighbor[16];
            var grid = new FixedSpatialNeighborhood(
                capacity: Count,
                cellWidth: radius
            );

            for (var index = 0; (index < Count); index++) {
                points[index] = new(
                    Index: index,
                    Position: (coincident
                    ? FixedVector3.Zero
                    : new(
                            X: FixedQ4816.FromInteger(value: (index % 16)),
                            Y: (grounded
                        ? zero
                        : FixedQ4816.FromInteger(value: (index / 256))),
                            Z: FixedQ4816.FromInteger(value: ((index / 16) % 16))
                        ))
                );
            }
            void Step(int step) {
                grid.Rebuild(points: points);
                for (var ordinal = 0; (ordinal < Count); ordinal++) {
                    var index = (reverse
                        ? ((Count - ordinal) - 1)
                        : ordinal
                    );
                    var point = points[index];
                    var work = grid.Query(
                        point.Position,
                        radius,
                        index,
                        32,
                        ((ulong)(index + (step * 32))),
                        candidates
                    );

                    if (
                        (work.CandidatesExamined > 32) ||
                        (work.NeighborsWritten > 16)
                    ) {
                        throw new InvalidOperationException(message: "Crowd density exceeded the explicit perception budget.");
                    }
                    for (var neighbor = 0; (neighbor < work.NeighborsWritten); neighbor++) {
                        var other = candidates[neighbor].Index;

                        neighbors[neighbor] = new(
                            other,
                            (points[other].Position - point.Position),
                            velocities[other],
                            (((other % 3) == 0)
                            ? zero
                            : one),
                            one
                        );
                    }
                    var desired = FixedFlockSteering.Evaluate(
                        index,
                        velocities[index],
                        goal,
                        normal,
                        neighbors.AsSpan(
                            0,
                            work.NeighborsWritten
                        ),
                        weights
                    ).Desired;

                    if (
                        (desired.LengthSquared.Value > (one.Value + 8)) ||
                        (grounded && (desired.Y != zero))
                    ) {
                        throw new InvalidOperationException(message: "Steering escaped its speed or support-plane bound.");
                    }
                    next[index] = new(
                        Index: index,
                        Position: (point.Position + (desired * dt))
                    );
                    nextVelocities[index] = desired;
                }
                (points, next) = (next, points);
                (velocities, nextVelocities) = (nextVelocities, velocities);
            }
            for (var step = 0; (step < 8); step++) { Step(step: step); }
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();

            for (var step = 8; (step < (Steps + 8)); step++) { Step(step: step); }
            var elapsed = Stopwatch.GetElapsedTime(startingTimestamp: start);

            allocated = (GC.GetAllocatedBytesForCurrentThread() - allocated);
            var hash = Fnv1aHash.Create();

            for (var index = 0; (index < Count); index++) {
                hash.Add(value: points[index].Position.X.Value); hash.Add(value: points[index].Position.Y.Value); hash.Add(value: points[index].Position.Z.Value);
                hash.Add(value: velocities[index].X.Value); hash.Add(value: velocities[index].Y.Value); hash.Add(value: velocities[index].Z.Value);
            }
            return (hash.Value, allocated, elapsed.TotalMilliseconds);
        }

        var forward = Run(reverse: false);
        var reverse = Run(reverse: true);

        Assert.Equal(
            actual: reverse.Hash,
            expected: forward.Hash
        );
        Assert.Equal(
            actual: forward.Allocated,
            expected: 0
        );
        Assert.Equal(
            actual: reverse.Allocated,
            expected: 0
        );
        TestContext.Current.TestOutputHelper!.WriteLine(message: (((string)$"4096-creature {(grounded
            ? "ground"
            : "volume")} kernel, coincident={coincident}: {(forward.Milliseconds / Steps):F3} ms/full update; reverse={(reverse.Milliseconds / Steps):F3} ms; zero allocations. ") +
            "Excludes body collision, world rules, navigation, and rendering; not an FPS measurement."));
    }
}
