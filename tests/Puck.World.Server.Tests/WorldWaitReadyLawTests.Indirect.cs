using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Puck.World.Server.Tests;

[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed partial class WorldWaitReadyLawTests {
    private sealed class IndirectReadiness : IWorldIndirectReadiness {
        public IReadOnlyList<WorldIndirectReadyIdentity>? Captured { get; set; }
        public long Frame { get; set; } = 20;
        public string? Refusal { get; set; }
        public long FrameBound { get; set; } = 16;

        public bool TryBegin([NotNullWhen(true)] out IWorldIndirectWait? wait, out string reason) {
            wait = new WorldIndirectWait(() => Frame, () => Captured, () => Refusal);
            reason = string.Empty;
            return true;
        }
    }

    [Fact]
    public void AnIndirectWaitNeedsANewerFrameAndEveryCurrentSourceFenceBeforeItsExactVerdict() {
        using var row = HostRow.Build(definition: Fixtures.BuildDocument(), name: "boot");
        var readiness = new IndirectReadiness();

        var (source, session, answered) = Console(row, readiness: null, indirect: readiness);
        session.Enqueue(line: "world.wait indirect");
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

        Assert.Equal(["world.wait indirect", "probe"], answered.Select(selector: item => item.Line));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "residency=world allocation=8 epoch=4 generation=0 stamp=29 source=31");
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "residency=observer allocation=9 epoch=4 generation=0 stamp=29 source=32");
        Assert.DoesNotContain(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "allocation=7");
    }
    [Fact]
    public void AnIndirectWaitOnASolveThatCannotFinishReleasesAtOnceNamingWhy() {
        using var row = HostRow.Build(definition: Fixtures.BuildDocument(), name: "boot");
        var readiness = new IndirectReadiness();

        var (source, session, answered) = Console(row, readiness: null, indirect: readiness);
        session.Enqueue(line: "world.wait indirect");
        session.Enqueue(line: "probe");
        source.Collect();
        readiness.Frame++;
        source.Collect();
        Assert.Single(collection: answered);
        readiness.Refusal = "residency=world its solve still needs about 9000 produced frames at the measured prices, beyond the 4096-frame bound";
        var original = System.Console.Error;
        using var captured = new StringWriter();

        try {
            System.Console.SetError(newError: captured);
            source.Collect();
        } finally {
            System.Console.SetError(newError: original);
        }
        Assert.Equal(["world.wait indirect", "probe"], answered.Select(selector: item => item.Line));
        Assert.Contains(actualString: captured.ToString(), comparisonType: StringComparison.Ordinal,
            expectedSubstring: ": residency=world its solve still needs about 9000 produced frames");
        Assert.Contains(actualString: captured.ToString(), comparisonType: StringComparison.Ordinal, expectedSubstring: "[indirect: refused at tick ");
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
    [Fact]
    public void AnIndirectWaitIsBoundedByProducedFramesNotSeconds() {
        using var row = HostRow.Build(definition: Fixtures.BuildDocument(), name: "boot");
        var readiness = new IndirectReadiness();

        var (source, session, answered) = Console(row, readiness: null, indirect: readiness);
        session.Enqueue(line: "world.wait indirect");
        session.Enqueue(line: "probe");
        source.Collect();
        Assert.Contains(actualString: answered[0].Result.Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "at most 16 produced frames");
        // However long the machine takes, an unsettled wait holds until the caches' frame bound has been produced.
        readiness.Frame += (readiness.FrameBound - 1);
        source.Collect();
        Assert.Single(collection: answered);
        readiness.Frame++;
        var original = System.Console.Error;
        using var captured = new StringWriter();

        try {
            System.Console.SetError(newError: captured);
            source.Collect();
        } finally {
            System.Console.SetError(newError: original);
        }
        Assert.Equal(["world.wait indirect", "probe"], answered.Select(selector: item => item.Line));
        Assert.Contains(actualString: captured.ToString(), comparisonType: StringComparison.Ordinal,
            expectedSubstring: "[indirect: not settled after 16 produced frames, so world.wait released]");
    }
    [InlineData("world.wait indirect", true, "unavailable without a rendered host")]
    [InlineData("world.wait indirect 5", false, "takes no deadline")]
    [InlineData("world.wait indirect 180", false, "takes no deadline")]
    [Theory]
    public void AnIndirectWaitRefusesWithoutItsRendererOrWithASecondsDeadline(string line, bool absent, string named) {
        using var row = HostRow.Build(definition: Fixtures.BuildDocument(), name: "boot");

        var (source, session, answered) = Console(row, readiness: null, indirect: (absent ? null : new IndirectReadiness()));
        session.Enqueue(line: line);
        source.Collect();
        var result = Assert.Single(collection: answered).Result;

        Assert.True(condition: result.IsError);
        Assert.Contains(actualString: result.Output, comparisonType: StringComparison.Ordinal, expectedSubstring: named);
    }
}
