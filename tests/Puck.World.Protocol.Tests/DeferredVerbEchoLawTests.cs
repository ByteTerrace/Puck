using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>
/// Laws for <see cref="WorldDeferredVerbEchoes"/>, the pending-verb table a buffered-mutation verb registers its
/// minted correlation id into so the <c>WorldServer.EchoTap</c> subscriber can print a per-verb refusal line: an
/// entry is taken exactly once, correlation 0 never registers, and the table stays bounded under entries whose
/// verdict never fires.
/// </summary>
public sealed class DeferredVerbEchoLawTests {
    [Fact]
    public void RegisteredEntry_IsTakenExactlyOnce() {
        var echoes = new WorldDeferredVerbEchoes();

        echoes.Register(
            correlationId: 7,
            verb: "world.row.set"
        );

        Assert.True(condition: echoes.TryTake(
            correlationId: 7,
            verb: out var verb
        ));
        Assert.Equal(
            actual: verb,
            expected: "world.row.set"
        );
        Assert.False(condition: echoes.TryTake(
            correlationId: 7,
            verb: out _
        ));
    }
    [Fact]
    public void ZeroCorrelation_NeverRegisters() {
        var echoes = new WorldDeferredVerbEchoes();

        echoes.Register(
            correlationId: 0,
            verb: "world.row.set"
        );

        Assert.False(condition: echoes.TryTake(
            correlationId: 0,
            verb: out _
        ));
    }
    [Fact]
    public void UnknownCorrelation_TakesNothing() {
        var echoes = new WorldDeferredVerbEchoes();

        Assert.False(condition: echoes.TryTake(
            correlationId: 42,
            verb: out _
        ));
    }
    [Fact]
    public void PendingEntries_EvictOldestPastCapacity() {
        var echoes = new WorldDeferredVerbEchoes();

        for (var id = 1L; (id <= (WorldDeferredVerbEchoes.Capacity + 1)); id++) {
            echoes.Register(
                correlationId: id,
                verb: "world.row.set"
            );
        }

        // The oldest entry fell off the bound; the newest survives.
        Assert.False(condition: echoes.TryTake(
            correlationId: 1,
            verb: out _
        ));
        Assert.True(condition: echoes.TryTake(
            correlationId: (WorldDeferredVerbEchoes.Capacity + 1),
            verb: out _
        ));
    }
    [Fact]
    public void TakenEntries_DoNotConsumeTheBound() {
        var echoes = new WorldDeferredVerbEchoes();

        // Register-and-take far past the bound, then prove a fresh entry still registers: the evicted-id queue's
        // stale rows never crowd out live ones.
        for (var id = 1L; (id <= (WorldDeferredVerbEchoes.Capacity * 2)); id++) {
            echoes.Register(
                correlationId: id,
                verb: "world.row.set"
            );
            Assert.True(condition: echoes.TryTake(
                correlationId: id,
                verb: out _
            ));
        }

        echoes.Register(
            correlationId: 100_000,
            verb: "world.row.step"
        );

        Assert.True(condition: echoes.TryTake(
            correlationId: 100_000,
            verb: out var verb
        ));
        Assert.Equal(
            actual: verb,
            expected: "world.row.step"
        );
    }
}
