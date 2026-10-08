using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Puck.World.Server.Tests;

[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed partial class WorldWaitReadyLawTests {
    private sealed class IndirectReadiness : IWorldIndirectReadiness {
        public IReadOnlyList<WorldIndirectReadyIdentity>? Captured { get; set; }
        public long Frame { get; set; } = 20;

        public bool TryBegin([NotNullWhen(true)] out IWorldIndirectWait? wait, out string reason) {
            wait = new WorldIndirectWait(() => Frame, () => Captured);
            reason = string.Empty;
            return true;
        }
    }

    [Fact]
    public void AnIndirectWaitNeedsANewerFrameAndEveryCurrentSourceFenceBeforeItsExactVerdict() {
        using var row = HostRow.Build(definition: Fixtures.BuildDocument(), name: "boot");
        var readiness = new IndirectReadiness();

        var (source, session, answered) = Console(row, readiness: null, indirect: readiness);
        session.Enqueue(line: "world.wait indirect 180");
        session.Enqueue(line: "probe");
        source.Collect();
        readiness.Captured = [new WorldIndirectReadyIdentity(Allocation: 7, Epoch: 3, Generation: 1, Residency: "world", Source: 23, Stamp: 19)];
        source.Collect();
        Assert.Single(collection: answered);
        readiness.Frame++;
        readiness.Captured = null;
        source.Collect();
        Assert.Single(collection: answered);
        readiness.Captured = [new WorldIndirectReadyIdentity(Allocation: 8, Epoch: 4, Generation: 0, Residency: "world", Source: 31, Stamp: 29),
            new WorldIndirectReadyIdentity(Allocation: 9, Epoch: 4, Generation: 0, Residency: "observer", Source: 32, Stamp: 29)];
        var original = System.Console.Error;
        using var captured = new StringWriter();

        try {
            System.Console.SetError(newError: captured);
            source.Collect();
        } finally {
            System.Console.SetError(newError: original);
        }
        var error = captured.ToString();

        Assert.Equal(["world.wait indirect 180", "probe"], answered.Select(selector: item => item.Line));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "residency=world allocation=8 epoch=4 generation=0 stamp=29 source=31");
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "residency=observer allocation=9 epoch=4 generation=0 stamp=29 source=32");
        Assert.DoesNotContain(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "allocation=7");
    }
    [Fact]
    public void ACompletedIndirectWaitRetainsItsCapturedIdentityWhenTheLiveSourceChanges() {
        var frame = 0L;
        var identities = new[] { new WorldIndirectReadyIdentity(Allocation: 11, Epoch: 2, Generation: 1, Residency: "world", Source: 7, Stamp: 5) };
        var wait = new WorldIndirectWait(capture: () => identities, framesProduced: () => frame);

        Assert.False(condition: wait.IsSettled);
        frame++;
        Assert.True(condition: wait.IsSettled);
        identities[0] = new WorldIndirectReadyIdentity(Allocation: 13, Epoch: 3, Generation: 0, Residency: "world", Source: 17, Stamp: 9);
        Assert.True(condition: wait.IsSettled);
        Assert.Equal(new WorldIndirectReadyIdentity(Allocation: 11, Epoch: 2, Generation: 1, Residency: "world", Source: 7, Stamp: 5), Assert.Single(collection: wait.Identities));
    }
    [InlineData("world.wait indirect 5", true)]
    [InlineData("world.wait indirect 0", false)]
    [InlineData("world.wait indirect 601", false)]
    [Theory]
    public void AnIndirectWaitRefusesWithoutItsRendererOrOutsideItsDeadline(string line, bool absent) {
        using var row = HostRow.Build(definition: Fixtures.BuildDocument(), name: "boot");

        var (source, session, answered) = Console(row, readiness: null, indirect: (absent ? null : new IndirectReadiness()));
        session.Enqueue(line: line);
        source.Collect();
        Assert.True(condition: Assert.Single(collection: answered).Result.IsError);
    }
}
