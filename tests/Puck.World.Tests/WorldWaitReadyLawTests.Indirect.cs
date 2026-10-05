using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Puck.World.Tests;

[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed partial class WorldWaitReadyLawTests {
    private sealed class IndirectReadiness : IWorldIndirectReadiness {
        public long Frame { get; set; } = 20;
        public IReadOnlyList<WorldIndirectReadyIdentity>? Captured { get; set; }
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
        session.Enqueue("world.wait indirect 180");
        session.Enqueue("probe");
        source.Collect();
        readiness.Captured = [new WorldIndirectReadyIdentity("world", 7, 3, 1, 19, 23)];
        source.Collect();
        Assert.Single(answered);
        readiness.Frame++;
        readiness.Captured = null;
        source.Collect();
        Assert.Single(answered);
        readiness.Captured = [new WorldIndirectReadyIdentity("world", 8, 4, 0, 29, 31),
            new WorldIndirectReadyIdentity("observer", 9, 4, 0, 29, 32)];
        var original = System.Console.Error;
        using var captured = new StringWriter();
        try {
            System.Console.SetError(newError: captured);
            source.Collect();
        } finally {
            System.Console.SetError(newError: original);
        }
        var error = captured.ToString();
        Assert.Equal(["world.wait indirect 180", "probe"], answered.Select(item => item.Line));
        Assert.Contains("residency=world allocation=8 epoch=4 generation=0 stamp=29 source=31", error, StringComparison.Ordinal);
        Assert.Contains("residency=observer allocation=9 epoch=4 generation=0 stamp=29 source=32", error, StringComparison.Ordinal);
        Assert.DoesNotContain("allocation=7", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompletedIndirectWaitRetainsItsCapturedIdentityWhenTheLiveSourceChanges() {
        long frame = 0;
        var identities = new[] { new WorldIndirectReadyIdentity("world", 11, 2, 1, 5, 7) };
        var wait = new WorldIndirectWait(() => frame, () => identities);
        Assert.False(wait.IsSettled);
        frame++;
        Assert.True(wait.IsSettled);
        identities[0] = new WorldIndirectReadyIdentity("world", 13, 3, 0, 9, 17);
        Assert.True(wait.IsSettled);
        Assert.Equal(new WorldIndirectReadyIdentity("world", 11, 2, 1, 5, 7), Assert.Single(wait.Identities));
    }

    [Theory]
    [InlineData("world.wait indirect 5", true)]
    [InlineData("world.wait indirect 0", false)]
    [InlineData("world.wait indirect 601", false)]
    public void AnIndirectWaitRefusesWithoutItsRendererOrOutsideItsDeadline(string line, bool absent) {
        using var row = HostRow.Build(definition: Fixtures.BuildDocument(), name: "boot");
        var (source, session, answered) = Console(row, readiness: null, indirect: absent ? null : new IndirectReadiness());
        session.Enqueue(line);
        source.Collect();
        Assert.True(Assert.Single(answered).Result.IsError);
    }
}
