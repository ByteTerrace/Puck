using System.Diagnostics;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class NavigationLawTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedNavigationHashHandlesLargeDomainsWithoutAllocating(bool dense) {
        var document = SharedNavigationDocument(goals: 16, budget: dense ? 65_536 : 1);
        var domain = document.Navigation.Rows[0] with { Width = 256, Depth = 256, Layers = 1 };
        using var fixture = Fixtures.FreshServer(document with {
            NavigationRaw = new([domain]),
            TargetRegistersRaw = [document.TargetRegisters[0] with { MaximumRange = 400 }],
        });
        _ = JoinNavigator(fixture, dense ? SharedGoal(255, 0, 255) : SharedGoal(4, 0, 0));
        fixture.Step(); fixture.Step();
        var expected = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 2);
        for (var iteration = 0; iteration < 100; iteration++) {
            Assert.Equal(expected, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 2));
        }
        ulong actual = 0;
        var allocated = long.MaxValue;
        var elapsed = TimeSpan.MaxValue;

        // The hash is immutable across these samples. Taking the lower envelope excludes a one-time background JIT
        // or runtime bookkeeping allocation under full-suite load; a real hash-path allocation repeats in every
        // 1,000-call window and still fails the exact-zero assertion.
        for (var sample = 0; sample < 3; sample++) {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();

            for (var iteration = 0; iteration < 1000; iteration++) {
                actual = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 2);
            }

            var sampleAllocated = (GC.GetAllocatedBytesForCurrentThread() - before);
            if (sampleAllocated < allocated) {
                allocated = sampleAllocated;
                elapsed = Stopwatch.GetElapsedTime(start);
            }
        }

        TestContext.Current.TestOutputHelper!.WriteLine($"Shared navigation {(dense ? "dense" : "sparse")} 65,536-cell hash: {elapsed.TotalMilliseconds:F3} ms / 1000, {allocated} allocated bytes");
        Assert.Equal(expected, actual);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SharedNavigationHashChangesForNewWorkAndRestoresExactly() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(budget: 1));
        _ = JoinNavigator(fixture, SharedGoal());
        fixture.Step();
        Assert.True(fixture.Server.TryCaptureCheckpoint(checkpoint: out var original, reason: out var reason, hostRow: EmptyHostRow()), reason);
        var before = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
        fixture.Step();
        Assert.NotEqual(before, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
        fixture.Server.RestoreCheckpoint(original!);
        Assert.Equal(before, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
    }

    [Fact]
    public void SharedNavigationHashDoesNotDependOnWhichCachesAreWarm() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(goals: 1, budget: 3));
        _ = JoinNavigator(fixture, SharedGoal());
        for (var tick = 0; tick < 100; tick++) {
            if (tick % 25 == 24) {
                Assert.True(fixture.Server.ApplyDesignation(new(0, RegisterName, default,
                    SharedGoal(2 + tick / 25, 3, 1)), WorldPrincipal.Seat(0)));
            }
            fixture.Step();
            var warm = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
            Assert.True(fixture.Server.TryCaptureCheckpoint(checkpoint: out var captured, reason: out var reason, hostRow: EmptyHostRow()), reason);
            fixture.Server.RestoreCheckpoint(captured!);
            Assert.Equal(warm, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NavigationCheckpointRejectsSharedStatusOrWorkInTheWrongOwner(bool shared) {
        using var fixture = Fixtures.FreshServer(shared ? SharedNavigationDocument() : NavigationDocument(VolumeDomain()));
        _ = JoinNavigator(fixture, SharedGoal());
        fixture.Step();
        var captured = fixture.Server.Population.Capture();
        var entry = Assert.Single(captured.Entries);
        var route = entry.Navigation!.Value;
        var invalid = route with { ExpandedLast = shared ? 1 : 0, Path = [], Waypoint = 0, Status = WorldNavigationStatus.Pending };
        var malformed = captured with { Entries = [entry with { Navigation = invalid }] };
        var before = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
        Assert.Contains("shared", Assert.Throws<InvalidOperationException>(() =>
            fixture.Server.Population.Restore(malformed, fixture.Server.Definition.PlayerDefaults, 1)).Message);
        Assert.Equal(before, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
    }
}
