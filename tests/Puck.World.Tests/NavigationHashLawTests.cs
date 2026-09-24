using Puck.Commands;
using System.Diagnostics;
using Puck.Abstractions.Counting;
using Puck.Physics.Navigation;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class NavigationLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void SharedNavigationHashHandlesLargeDomainsWithoutAllocating(bool dense) {
        var document = SharedNavigationDocument(
            goals: 16,
            budget: (dense
            ? 65_536
            : 1)
        );
        var domain = document.Navigation.Rows[0] with { Width = 256, Depth = 256, Layers = 1 };
        using var fixture = Fixtures.FreshServer(document with {
            NavigationRaw = new(Domains: [domain]),
            TargetRegistersRaw = [document.TargetRegisters[0] with { MaximumRange = 400 }],
        });

        _ = JoinNavigator(
            fixture,
            (dense
            ? SharedGoal(
                    x: 255,
                    y: 0,
                    z: 255
                )
            : SharedGoal(
                    x: 4,
                    y: 0,
                    z: 0
                ))
        );
        fixture.Step(); fixture.Step();
        var expected = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 2
        );

        for (var iteration = 0; (iteration < 100); iteration++) {
            Assert.Equal(
                expected,
                WorldStateHashComposition.HashAuthoritative(
                    server: fixture.Server,
                    tick: 2
                )
            );
        }
        var actual = 0UL;
        var elapsed = TimeSpan.Zero;
        // The hash is immutable across windows; the window that reads zero is the last one run, so its time is the one
        // reported.
        var allocated = AllocationWindow.Least(window: () => {
            var start = Stopwatch.GetTimestamp();

            for (var iteration = 0; (iteration < 1000); iteration++) {
                actual = WorldStateHashComposition.HashAuthoritative(
                    server: fixture.Server,
                    tick: 2
                );
            }

            elapsed = Stopwatch.GetElapsedTime(startingTimestamp: start);
        });

        TestContext.Current.TestOutputHelper!.WriteLine(message: $"Shared navigation {(dense
            ? "dense"
            : "sparse")} 65,536-cell hash: {elapsed.TotalMilliseconds:F3} ms / 1000, {allocated} allocated bytes");
        Assert.Equal(
            actual: actual,
            expected: expected
        );
        Assert.Equal(
            actual: allocated,
            expected: 0
        );
    }
    [Fact]
    public void SharedNavigationHashChangesForNewWorkAndRestoresExactly() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(budget: 1));

        _ = JoinNavigator(
            fixture,
            SharedGoal()
        );
        fixture.Step();
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var original,
                reason: out var reason,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty
            ),
            userMessage: reason
        );
        var before = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 0
        );

        fixture.Step();
        Assert.NotEqual(
            before,
            WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 0
            )
        );
        fixture.Server.RestoreCheckpoint(checkpoint: original!);
        Assert.Equal(
            before,
            WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 0
            )
        );
    }
    [Fact]
    public void SharedNavigationHashDoesNotDependOnWhichCachesAreWarm() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(
            goals: 1,
            budget: 3
        ));

        _ = JoinNavigator(
            fixture,
            SharedGoal()
        );
        for (var tick = 0; (tick < 100); tick++) {
            if ((tick % 25) == 24) {
                Assert.True(condition: fixture.Server.ApplyDesignation(
                    new(
                        0,
                        RegisterName,
                        default,
                        SharedGoal(
                            x: (2 + (tick / 25)),
                            y: 3,
                            z: 1
                        )
                    ),
                    Principal.Seat(slot: 0)
                ));
            }
            fixture.Step();
            var warm = WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 0
            );

            Assert.True(
                condition: fixture.Server.TryCaptureCheckpoint(
                    checkpoint: out var captured,
                    reason: out var reason,
                    hostRow: WorldAuthorityHostRowCheckpoint.Empty
                ),
                userMessage: reason
            );
            fixture.Server.RestoreCheckpoint(checkpoint: captured!);
            Assert.Equal(
                warm,
                WorldStateHashComposition.HashAuthoritative(
                    server: fixture.Server,
                    tick: 0
                )
            );
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void NavigationCheckpointRejectsSharedStatusOrWorkInTheWrongOwner(bool shared) {
        using var fixture = Fixtures.FreshServer((shared
            ? SharedNavigationDocument()
            : NavigationDocument(VolumeDomain())));

        _ = JoinNavigator(
            fixture,
            SharedGoal()
        );
        fixture.Step();
        var captured = fixture.Server.Population.Capture();
        var entry = Assert.Single(collection: captured.Entries);
        var route = entry.Navigation!.Value;
        var invalid = route with {
            ExpandedLast = (shared
            ? 1
            : 0),
            Path = [],
            Waypoint = 0,
            Status = NavigationStatus.Pending,
        };
        var malformed = captured with { Entries = [entry with { Navigation = invalid }] };
        var before = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 0
        );

        Assert.Contains(
            "shared",
            Assert.Throws<InvalidOperationException>(testCode: () =>
            fixture.Server.Population.Restore(
                checkpoint: malformed,
                defaults: fixture.Server.Definition.PlayerDefaults,
                tick: 1
            )).Message
        );
        Assert.Equal(
            before,
            WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 0
            )
        );
    }
}
