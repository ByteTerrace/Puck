using Xunit;

namespace Puck.State.Tests;

/// <summary>THE LAW: retained key names are future admission state even when no row references them. Their set
/// moves the arena hash and byte charge, while allocation order and rewound speculative names do not.</summary>
public sealed class ArenaKeyLedgerHashLawTests {
    [Fact]
    public void AnOrphanCommittedNameMovesTheArenaHash() {
        var (_, arena) = ArenaFixture.Build();
        var before = arena.ComputeHash();

        _ = arena.Keys.Intern(name: ArenaFixture.Name(value: "orphan"));

        Assert.NotEqual(
            actual: arena.ComputeHash(),
            expected: before
        );
    }
    [Fact]
    public void KeyLedgerHashIsIndependentOfInternOrder() {
        var (_, left) = ArenaFixture.Build();
        var (_, right) = ArenaFixture.Build();

        _ = left.Keys.Intern(name: ArenaFixture.Name(value: "alpha-orphan"));
        _ = left.Keys.Intern(name: ArenaFixture.Name(value: "beta-orphan"));
        _ = right.Keys.Intern(name: ArenaFixture.Name(value: "beta-orphan"));
        _ = right.Keys.Intern(name: ArenaFixture.Name(value: "alpha-orphan"));

        Assert.Equal(
            actual: right.ComputeHash(),
            expected: left.ComputeHash()
        );
    }
    [Fact]
    public void RewindRestoresTheKeyLedgerHashAndByteCharge() {
        var (_, arena) = ArenaFixture.Build();
        var beforeBytes = arena.Bytes;
        var beforeHash = arena.ComputeHash();
        var mark = arena.BeginScope();

        _ = arena.Keys.Intern(name: ArenaFixture.Name(value: "speculative-orphan"));

        Assert.True(condition: (arena.Bytes > beforeBytes));
        Assert.NotEqual(
            actual: arena.ComputeHash(),
            expected: beforeHash
        );

        arena.Rewind(mark: mark);

        Assert.Equal(
            actual: arena.Bytes,
            expected: beforeBytes
        );
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: beforeHash
        );
    }
    [Fact]
    public void RelayoutCarriesOrphanCommittedNamesAndTheirHash() {
        var (_, arena) = ArenaFixture.Build();
        var orphan = ArenaFixture.Name(value: "relayout-orphan");

        _ = arena.Keys.Intern(name: orphan);
        var beforeBytes = arena.Bytes;
        var beforeHash = arena.ComputeHash();
        var replacement = ArenaFixture.Section();

        Assert.True(condition: arena.TryRelayout(
            catalog: StateCatalog.Compile(section: replacement),
            reason: out var reason,
            section: replacement,
            time: ArenaTime.Origin
        ), reason);
        Assert.True(condition: arena.Keys.TryResolve(
            key: out _,
            name: orphan
        ));
        Assert.Equal(beforeBytes, arena.Bytes);
        Assert.Equal(beforeHash, arena.ComputeHash());
    }
}
