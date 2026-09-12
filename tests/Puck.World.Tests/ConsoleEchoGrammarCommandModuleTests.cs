using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Command-level proofs that execute the real <c>world.update</c>/<c>world.groups</c>/
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
    public async Task WorldWaitHoldsOnlyItsIssuerAndUsesIndependentDeadlines() {
        using var row = HostRow.Build(name: "boot", definition: Fixtures.BuildDocument());
        var gate = new WorldConsoleWaitGate();
        var registry = new CommandRegistry(modules: [
            new WorldWaitCommandModule(authority: new FakeConsoleAuthority(row.Instance), gates: new FakeWaitGateResolver(gate)),
        ]);
        var source = new TextCommandSource(registry);
        using var first = source.CreateSession(CommandPrincipal.Console);
        using var second = source.CreateSession(CommandPrincipal.Console);
        first.Enqueue("world.wait 3");
        var afterFirst = first.InvokeAsync(() => gate.Tick, cancellationToken: TestContext.Current.CancellationToken);
        second.Enqueue("world.wait 1");
        var afterSecond = second.InvokeAsync(() => gate.Tick, cancellationToken: TestContext.Current.CancellationToken);
        source.Collect();
        Assert.False(afterFirst.IsCompleted);
        Assert.False(afterSecond.IsCompleted);
        gate.PublishTick(1);
        source.Collect();
        Assert.Equal(1UL, await afterSecond);
        Assert.False(afterFirst.IsCompleted);
        gate.PublishTick(3);
        source.Collect();
        Assert.Equal(3UL, await afterFirst);
        Assert.False(gate.ReleaseStalled());
        Assert.True(registry.Submit("world.wait 1").IsError);
    }

    [Fact]
    public async Task StalledReleaseAndClockResetReleaseEveryWaitingSession() {
        var gate = new WorldConsoleWaitGate();
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        using var first = source.CreateSession(CommandPrincipal.Console);
        using var second = source.CreateSession(CommandPrincipal.Console);
        gate.PublishTick(10);
        _ = gate.Arm(first, 2);
        _ = gate.Arm(second, 3);
        var a = first.InvokeAsync(() => true, cancellationToken: TestContext.Current.CancellationToken);
        var b = second.InvokeAsync(() => true, cancellationToken: TestContext.Current.CancellationToken);
        source.Collect();
        Assert.False(a.IsCompleted);
        Assert.False(b.IsCompleted);
        Assert.True(gate.ReleaseStalled());
        source.Collect();
        Assert.True(await a);
        Assert.True(await b);
        _ = gate.Arm(first, 2);
        var reset = first.InvokeAsync(() => true, cancellationToken: TestContext.Current.CancellationToken);
        gate.PublishTick(0);
        source.Collect();
        Assert.True(await reset);
        _ = gate.Arm(first, 1);
        gate.PublishTick(1);
        Assert.False(gate.ReleaseStalled());
    }

    [Fact]
    public async Task ClockResetCannotReviveAnExpiredWaitThatThePumpHasNotObserved() {
        var gate = new WorldConsoleWaitGate();
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        using var session = source.CreateSession(CommandPrincipal.Console);
        _ = gate.Arm(session, 2);
        var pending = session.InvokeAsync(() => true, TestContext.Current.CancellationToken);
        gate.PublishTick(2);
        // No Collect between expiry and reset: the session still holds the old predicate.
        gate.PublishTick(0);
        source.Collect();
        Assert.True(pending.IsCompleted);
        Assert.True(await pending);
        _ = gate.Arm(session, 1);
        var next = session.InvokeAsync(() => true, TestContext.Current.CancellationToken);
        source.Collect();
        Assert.False(next.IsCompleted);
        gate.PublishTick(1);
        source.Collect();
        Assert.True(await next);
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
                    Lifetime: WorldGroupLifetime.Ephemeral,
                    EvictionPolicy: WorldGroupEvictionPolicy.Remove,
                    Capacity: 4
                ),
            ],
            Groups: [
                new WorldGroup(Id: SafeName.Parse(candidate: "alpha"), KindName: "party", Members: [new WorldGroupMember(WorldMemberRef.Local(principal: WorldPrincipal.Seat(slot: 0)), null, 0)]),
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
    [Theory]
    [InlineData("1", true, 1UL)]
    [InlineData("+1", false, 0UL)]
    [InlineData("18446744073709551616", false, 0UL)] // one past ulong.MaxValue
    public void WorldWait_DigitsOnlyTickGrammar_RefusesPlusSignAndOverflow(string token, bool accepted, ulong ticksIfAccepted) {
        using var row = HostRow.Build(name: "boot", definition: Fixtures.BuildDocument());
        var registry = new CommandRegistry(modules: [
            new WorldWaitCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance), gates: new FakeWaitGateResolver(gate: new WorldConsoleWaitGate())),
        ]);

        CommandResult result = default;
        var source = new TextCommandSource(registry);
        using var session = source.CreateSession(principal: CommandPrincipal.Console, onResult: (_, value) => result = value);
        session.Enqueue(line: $"world.wait {token}");
        source.Collect();

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
