using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Command-level proofs that execute the real <c>world.update</c>/<c>world.groups</c>/<c>world.market</c>/
/// <c>world.wait</c>/<c>world.population.spawn</c> verbs through their registered <see cref="ICommandModule"/>
/// (<c>Puck.World.Console</c>, referenced directly since <c>Puck.World</c> the composition root is out of scope for
/// this project) — the coverage <c>CommandEchoTests</c>' builder-only proofs and a hand-built
/// <see cref="CommandEcho"/> line cannot provide, because neither one ever calls into the verb whose output it
/// claims to describe. Pins the CommandEcho Head/Field segment grammar (a segment is either a run of
/// <c>key=value</c> fields or one declared HEAD word followed by fields) across the empty, singleton, and
/// multi-segment shapes each read-back actually emits, including the trailing-separator drop and the quoting a value
/// carrying one of the grammar's reserved characters takes.</summary>
public sealed class ConsoleEchoGrammarCommandModuleTests {
    // Resolves every invocation to the one row this fixture built — the desktop's own WorldBootConsoleAuthority
    // shape, minus the WorldInstanceHost indirection this project has no reason to construct.
    private sealed class FakeConsoleAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }
    // Always answers the same gate, like the desktop's one process-wide WorldConsoleWaitGate.
    private sealed class FakeWaitGateResolver(WorldConsoleWaitGate gate) : IWorldWaitGateResolver {
        public WorldConsoleWaitGate GateFor(WorldInstance instance) => gate;
    }

    [Fact]
    public void WorldUpdate_NoSectionAuthored_EchoesNone() {
        using var row = HostRow.Build(name: "boot", definition: Fixtures.BuildDocument());
        var registry = new CommandRegistry(modules: [new WorldUpdateCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance))]);

        var result = registry.Submit(line: "world.update");

        Assert.Equal(expected: "[world.update: none]", actual: result.Output);
    }
    [Fact]
    public void WorldUpdate_AuthoredSection_EchoesKeyValueFields() {
        var document = (Fixtures.BuildDocument() with {
            Update = new WorldUpdateDefaults(CacheRoot: "cache", Channel: "stable", CheckIntervalSeconds: 3600, KeepVersions: 2),
        });
        using var row = HostRow.Build(definition: document, name: "boot");
        var registry = new CommandRegistry(modules: [new WorldUpdateCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance))]);

        var result = registry.Submit(line: "world.update");

        // The staged key=value migration (was whitespace-separated "channel stable ...") — intentional, pinned here.
        Assert.Equal(expected: "[world.update: channel=stable cacheRoot=cache checkIntervalSeconds=3600 keepVersions=2]", actual: result.Output);
    }
    [Fact]
    public void WorldGroups_NoSectionAuthored_EchoesNoGroupsSection() {
        using var row = HostRow.Build(name: "boot", definition: Fixtures.BuildDocument());
        var registry = new CommandRegistry(modules: [new WorldGroupCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance), link: row.Instance.Link)]);

        var result = registry.Submit(line: "world.groups");

        Assert.Equal(expected: "[world.groups: (no groups section)]", actual: result.Output);
    }
    [Fact]
    public void WorldGroups_AuthoredKindGroupAndOwnership_EchoesHeadFieldSegments() {
        var groups = new WorldGroupsSection(
            Kinds: [
                new WorldGroupKind(
                    Name: "party",
                    Roles: [new WorldGroupRole(Capabilities: [WorldCapability.Drive], Name: "leader")],
                    OwnershipPolicy: WorldGroupOwnershipPolicy.LeaderDecides,
                    Lifetime: WorldGroupLifetime.Ephemeral,
                    EvictionPolicy: WorldGroupEvictionPolicy.Remove,
                    Capacity: 4
                ),
            ],
            Groups: [
                new WorldGroup(Id: WorldSafeName.Parse(candidate: "alpha"), KindName: "party", Members: [WorldPrincipal.Seat(slot: 0)]),
            ],
            Ownership: [
                new WorldOwnership(
                    Subject: new OwnershipSubject(Id: "alpha", Kind: OwnershipSubjectKind.Group),
                    Owner: new OwnershipOwner(Kind: OwnershipOwnerKind.Principal, Principal: WorldPrincipal.Seat(slot: 1))
                ),
            ]
        );
        using var row = HostRow.Build(name: "boot", definition: (Fixtures.BuildDocument() with { Groups = groups }));
        var registry = new CommandRegistry(modules: [new WorldGroupCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance), link: row.Instance.Link)]);

        var result = registry.Submit(line: "world.groups");

        // Every segment head is a declared word (kind/group/ownership) followed by key=value fields — the honest
        // grammar CommandEcho now documents — and the final segment carries no trailing " |". The bracketed LIST
        // values (roles, members) are quoted: their ']' is a reserved character, and unquoted it would close the
        // envelope early for a driver scanning for the first bracket.
        Assert.Equal(
            expected: "[world.groups: kind name=party roles=\"[leader=Drive]\" ownership=LeaderDecides lifetime=Ephemeral eviction=Remove cap=4 | group id=alpha kind=party members=\"[seat1]\" | ownership subject=Group:alpha owner=seat2]",
            actual: result.Output
        );

        // The id-filtered form is the singleton case: one segment, still no trailing separator.
        var filtered = registry.Submit(line: "world.groups alpha");

        Assert.Equal(expected: "[world.groups: group id=alpha kind=party members=\"[seat1]\"]", actual: filtered.Output);
    }
    [Fact]
    public void WorldMarket_NoSectionAuthored_EchoesNoMarketSection() {
        using var row = HostRow.Build(name: "boot", definition: Fixtures.BuildDocument());
        var registry = new CommandRegistry(modules: [new WorldMarketCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance), link: row.Instance.Link)]);

        var result = registry.Submit(line: "world.market");

        Assert.Equal(expected: "[world.market: (no market section)]", actual: result.Output);
    }
    [Fact]
    public void WorldMarket_AuthoredWithNoListings_EchoesConfigOnlyWithNoTrailingPipe() {
        using var row = HostRow.Build(name: "boot", definition: MarketFixtures.BuildDocument());
        var registry = new CommandRegistry(modules: [new WorldMarketCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance), link: row.Instance.Link)]);

        var result = registry.Submit(line: "world.market");

        // The staged trailing-pipe drop (was "... feeReserve=0 |]") — intentional, pinned here as the empty-listings
        // case. formats and duration are bracketed values, so CommandEcho quotes them rather than letting their ']'
        // read as the envelope's own.
        Assert.Equal(expected: "[world.market: formats=\"[English,Buyout]\" feeBasisPoints=1000 duration=\"[1..3600]\" retentionSeconds=604800 feeReserve=0]", actual: result.Output);
    }
    [Fact]
    public void WorldMarket_WithOneListing_EchoesHeadFieldListingSegment() {
        var market = (MarketFixtures.BuildDocument().Market! with {
            Listings = [
                new WorldMarketListing(
                    Id: 1,
                    Seller: WorldPrincipal.Seat(slot: 0),
                    ItemRow: MarketFixtures.AppleRow,
                    Quantity: 1,
                    CurrencyRow: MarketFixtures.GoldRow,
                    Format: WorldMarketFormat.English,
                    StartPrice: 10,
                    DeadlineTick: 1000
                ),
            ],
            NextListingId = 2,
        });
        using var row = HostRow.Build(name: "boot", definition: (MarketFixtures.BuildDocument() with { Market = market }));
        var registry = new CommandRegistry(modules: [new WorldMarketCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance), link: row.Instance.Link)]);

        var result = registry.Submit(line: "world.market");

        Assert.Equal(
            expected: "[world.market: formats=\"[English,Buyout]\" feeBasisPoints=1000 duration=\"[1..3600]\" retentionSeconds=604800 feeReserve=0 | listing id=1 seller=seat1 item=1xapple currency=gold format=English startPrice=10 deadlineTick=1000 status=Active currentBid=0]",
            actual: result.Output
        );
    }
    [Theory]
    [InlineData("1", true, 1UL)]
    [InlineData("+1", false, 0UL)]
    [InlineData("18446744073709551616", false, 0UL)] // one past ulong.MaxValue
    public void WorldWait_DigitsOnlyTickGrammar_RefusesPlusSignAndOverflow(string token, bool accepted, ulong ticksIfAccepted) {
        using var row = HostRow.Build(name: "boot", definition: Fixtures.BuildDocument());
        var registry = new CommandRegistry(modules: [
            new WorldWaitCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance), gates: new FakeWaitGateResolver(gate: new WorldConsoleWaitGate())),
        ]);

        var result = registry.Submit(line: $"world.wait {token}");

        if (accepted) {
            Assert.False(condition: result.IsError);
            Assert.Contains(actualString: result.Output, comparisonType: StringComparison.Ordinal, expectedSubstring: $"{ticksIfAccepted} ticks from");
        } else {
            Assert.True(condition: result.IsError);
            Assert.Equal(expected: $"[world.wait: '{token}' is not a whole number of ticks]", actual: result.Output);
        }
    }
    [Fact]
    public void WorldPopulationSpawn_NonFiniteRadius_RefusesByName() {
        using var row = HostRow.Build(name: "boot", definition: Fixtures.BuildDocument());
        var registry = new CommandRegistry(modules: [new WorldLookCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance), link: row.Instance.Link)]);

        var result = registry.Submit(line: "world.population.spawn disc NaN 5");

        Assert.True(condition: result.IsError);
        Assert.Equal(expected: "[world.population.spawn: disc needs a <radius> number and <sampleCount> integer]", actual: result.Output);

        // Control: the identical grammar with a finite radius succeeds.
        var control = registry.Submit(line: "world.population.spawn disc 40 5");

        Assert.False(condition: control.IsError);
    }
}
