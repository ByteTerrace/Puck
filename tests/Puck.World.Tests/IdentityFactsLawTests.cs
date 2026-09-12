using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the facts an identity carries: written by a rule effect into the body's lane and the identity's own
/// persisted row together, reloaded into the lane when a fresh boot binds the same identity, refused by name in a
/// world declaring no lane and for a seat driving under no identity, and allocation-free on a tick whose write
/// changes nothing.</summary>
[Collection(AllocationCollection.Name)]
public sealed class IdentityFactsLawTests(ITestOutputHelper output) {
    private const string IdentityName = "amber";
    private const string OtherIdentityName = "beryl";
    private const string Fact = "dived";
    private const int LaneCapacity = 16;

    private static readonly WorldPrincipal Seat = WorldPrincipal.Seat(slot: 0);

    [Fact]
    public void EffectWritesLaneAndPersistedRowTogether_LaneZeroesWhenSeatLeaves() {
        using var fixture = Fixtures.FreshServer(definition: Document(rules: [WriteRule(mode: ActionTriggerMode.Edge), ReadRule()]));

        fixture.Step();

        Assert.Empty(collection: LaneCells(fixture: fixture));
        Assert.Equal(expected: 0L, actual: Slot(fixture: fixture, row: "seen"));

        Join(fixture: fixture, identity: IdentityName);
        fixture.Step();

        Assert.Equal(expected: 1L, actual: Lane(fixture: fixture, body: 0, fact: Fact));

        fixture.Step();

        Assert.Equal(expected: 1L, actual: Slot(fixture: fixture, row: "seen"));

        var identity = Assert.IsType<WorldIdentity>(@object: fixture.Server.Profiles.FindById(id: IdentityName));
        var persisted = Assert.IsType<WorldStateRow>(@object: identity.Facts);

        Assert.Equal(expected: identity.FactsDefinition.State, actual: persisted.Name);
        Assert.Equal(expected: identity.FactsDefinition.Capacity, actual: persisted.Capacity);
        Assert.Equal(expected: 1L, actual: Assert.Single(collection: persisted.Cells!).Value);
        Assert.Equal(expected: 1L, actual: PersistedFact(directory: fixture.Server.Profiles.FilePath, identity: IdentityName, fact: Fact));

        // The write fired once, on the seat's arrival; a seat swapping to an identity carrying no facts zeroes the
        // body's lane, the first identity's own row keeps what it wrote, and swapping back reloads it.
        var other = Assert.IsType<WorldIdentity>(@object: fixture.Server.Profiles.Create(name: SafeName.Parse(candidate: OtherIdentityName), colorHex: "#336699", reason: out _));

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.SetIdentity(Principal: Seat, Slot: 0, IdentityName: other.Name)).Accepted);
        fixture.Step();

        Assert.Equal(expected: 0L, actual: Lane(fixture: fixture, body: 0, fact: Fact));
        Assert.Equal(expected: 1L, actual: PersistedFact(directory: fixture.Server.Profiles.FilePath, identity: IdentityName, fact: Fact));

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.SetIdentity(Principal: Seat, Slot: 0, IdentityName: IdentityName)).Accepted);
        fixture.Step();

        Assert.Equal(expected: 1L, actual: Lane(fixture: fixture, body: 0, fact: Fact));
    }

    [Fact]
    public void FreshBootBindingTheSameIdentityReloadsTheLane() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-identity-facts-").FullName;

        try {
            using (var first = Boot(definition: Document(rules: [WriteRule()]), directory: directory)) {
                Join(fixture: first, identity: IdentityName);
                first.Step();

                Assert.Equal(expected: 1L, actual: Lane(fixture: first, body: 0, fact: Fact));
            }

            using var second = Boot(definition: Document(rules: []), directory: directory);

            second.Step();

            Assert.Empty(collection: LaneCells(fixture: second));

            Join(fixture: second, identity: IdentityName);
            second.Step();

            Assert.Equal(expected: 1L, actual: Lane(fixture: second, body: 0, fact: Fact));
            Assert.Empty(collection: second.Server.RuleRuntimeDiagnostics());
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }

    [Fact]
    public void WorldDeclaringNoLaneRefusesTheEffectAndTheChannelByName() {
        foreach (var rule in new[] { WriteRule(), ReadRule() }) {
            var definition = Document(rules: [rule], lane: false);

            Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason));
            Assert.Contains(expectedSubstring: WorldIdentityFactLane.RowName, actualString: reason);

            var refused = Assert.Throws<RuleException>(testCode: () => WorldRuleCompiler.CompileAll(definition: definition));

            Assert.Equal(expected: WorldRuleRefusal.IdentityLaneUndeclared, actual: refused.Refusal);
        }

        var declared = Document(rules: [WriteRule(), ReadRule()]);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: declared, reason: out _));
    }

    [Fact]
    public void SeatDrivingUnderNoIdentityIsRefusedNotMinted() {
        using var fixture = Fixtures.FreshServer(definition: Document(rules: [WriteRule()]));

        Join(fixture: fixture, identity: null);
        fixture.Step();
        fixture.Step();

        Assert.Empty(collection: LaneCells(fixture: fixture));

        var diagnostic = Assert.Single(collection: fixture.Server.RuleRuntimeDiagnostics());

        Assert.Equal(expected: WorldRuleEffectRefusal.IdentityUnbound, actual: diagnostic.Refusal);
        Assert.All(collection: fixture.Server.Profiles.All, action: identity => Assert.Null(@object: identity.Facts));
    }

    [Fact]
    public void UnchangedWriteCostsAnOrdinaryWrite_ChangedWriteIsBoundedByThePersistedDocument() {
        using var steady = Fixtures.FreshServer(definition: Document(rules: [WriteRule()]));
        using var steadyControl = Fixtures.FreshServer(definition: Document(rules: [SteadyControlRule()]));
        using var moving = Fixtures.FreshServer(definition: Document(rules: [TickRule()]));
        using var control = Fixtures.FreshServer(definition: Document(rules: [ControlRule()]));

        Join(fixture: steady, identity: IdentityName);
        Join(fixture: steadyControl, identity: IdentityName);
        Join(fixture: moving, identity: IdentityName);
        Join(fixture: control, identity: IdentityName);

        for (var tick = 0; (tick < 1); tick++) {
            steady.Step();
            steadyControl.Step();
            moving.Step();
            control.Step();
        }

        Assert.Equal(expected: 1L, actual: Lane(fixture: steady, body: 0, fact: Fact));

        var steadySamples = Measure(fixture: steady);
        var steadyControlSamples = Measure(fixture: steadyControl);
        var movingSamples = Measure(fixture: moving);
        var controlSamples = Measure(fixture: control);
        var identity = Assert.IsType<WorldIdentity>(@object: moving.Server.Profiles.FindById(id: IdentityName));
        var documentBytes = WorldDefinitionSerialization.Serialize(definition: identity.Document!).Length;
        var beyond = (movingSamples.Median - controlSamples.Median);

        output.WriteLine($"unchanged fact: median {steadySamples.Median:N0} bytes/tick, widest {steadySamples.Widest:N0}; unchanged ordinary cell: median {steadyControlSamples.Median:N0}; changing fact: median {movingSamples.Median:N0} bytes/tick, widest {movingSamples.Widest:N0}; changing ordinary cell: median {controlSamples.Median:N0} bytes/tick, widest {controlSamples.Widest:N0}; persisted identity document {documentBytes:N0} bytes; a changing fact's cost beyond an ordinary changing write {beyond:N0} bytes ({((double)beyond / documentBytes):0.0} documents)");

        // An unchanged fact queues no lane write and persists nothing: the tick costs what an unchanged ordinary
        // cell write costs. A changing fact pays the identity's own save (the catalog reads the file back, serializes
        // the document, and writes it) on top of the ordinary fold, bounded here as a multiple of the document.
        Assert.Equal(expected: steadyControlSamples.Median, actual: steadySamples.Median);
        Assert.True(condition: (beyond <= (32L * documentBytes)), userMessage: $"a changing fact allocated {beyond:N0} bytes beyond an ordinary changing write, against a {documentBytes:N0}-byte persisted document");
        Assert.Equal(expected: ((long)(moving.Server.NextInputTick - 1UL)), actual: Lane(fixture: moving, body: 0, fact: "ticks"));
    }

    [Fact]
    public void IdenticalDrivesReproduceTheLaneHashForHash_AnUnboundSeatDiverges() {
        var document = Document(rules: [WriteRule(mode: ActionTriggerMode.Edge), ReadRule()]);
        var first = Trace(document: document, identity: IdentityName);
        var second = Trace(document: document, identity: IdentityName);
        var unbound = Trace(document: document, identity: null);

        Assert.Equal(expected: first, actual: second);
        Assert.NotEqual(expected: first, actual: unbound);
    }

    // The authoritative scope, which folds the document's state rows — the lane among them — where the population
    // trace folds bodies alone.
    private static ulong[] Trace(WorldDefinition document, string? identity) {
        using var fixture = Fixtures.FreshServer(definition: document);
        var hashes = new ulong[12];

        Join(fixture: fixture, identity: identity);

        for (var tick = 0; (tick < hashes.Length); tick++) {
            fixture.Step();
            hashes[tick] = WorldRuntimeStateHash.HashAuthoritative(server: fixture.Server, tick: (fixture.Server.NextInputTick - 1UL));
        }

        return hashes;
    }
    private static (long Median, long Widest) Measure(WorldFixture fixture) {
        var samples = new long[32];

        for (var tick = 0; (tick < samples.Length); tick++) {
            var before = GC.GetAllocatedBytesForCurrentThread();

            fixture.Step();
            samples[tick] = (GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Array.Sort(array: samples);

        return (samples[(samples.Length / 2)], samples[^1]);
    }
    private static WorldFixture Boot(WorldDefinition definition, string directory) {
        definition = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));

        var population = new WorldPopulation(definition: definition);
        var machines = new WorldMachineHost(screens: definition.Screens, engines: []);
        var profiles = new WorldOwnedWorlds(template: definition, directory: directory, machineId: Guid.NewGuid());
        var server = new WorldServer(definition: definition, population: population, profiles: profiles, envelope: new WorldRenderEnvelope(), machines: machines, narrationSink: new WorldConsoleNarrationSink());

        return new WorldFixture(machines: machines, server: server, stateDirectory: Directory.CreateTempSubdirectory(prefix: "puck-identity-facts-scratch-").FullName);
    }
    private static void Join(WorldFixture fixture, string? identity) {
        var reply = fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: Seat, Slot: 0, IdentityName: identity, WireProtocolKey: WorldProtocol.WireProtocolKey));

        Assert.True(condition: reply.Accepted, userMessage: reply.Reason);
        Assert.Equal(expected: identity, actual: fixture.Server.Body(index: 0)?.Profile?.Id);
    }
    private static WorldDefinition Document(IReadOnlyList<WorldRule> rules, bool lane = true) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: (lane
            ? [LaneRow(), IntSlot(name: "seen")]
            : [IntSlot(name: "seen")]
        )),
        Rules = rules,
    };
    private static WorldStateRow LaneRow() => new(Name: CellName.Parse(candidate: WorldIdentityFactLane.RowName), Kind: CellKind.Int, Capacity: LaneCapacity);
    private static WorldStateRow IntSlot(string name) => new(Name: CellName.Parse(candidate: name), Kind: CellKind.Int, Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0L)]);
    // The seat's body is body 0; the gate holds once a body is seated at all.
    private static WorldRule WriteRule(ActionTriggerMode mode = ActionTriggerMode.Level) => new(
        Name: CellName.Parse(candidate: "write"),
        Effects: [new WorldEffect.SetIdentityFact(Key: "0", Fact: Fact, Value: 1m)],
        Gate: new ActionPredicate.CompareState(State: WorldRuleFacts.Population, Comparison: ActionStateComparison.GreaterOrEqual, Value: 1m),
        Mode: mode
    );
    private static WorldRule SteadyControlRule() => new(
        Name: CellName.Parse(candidate: "steady"),
        Effects: [new ActionEffect.SetState(State: "seen", Value: 1m)],
        Gate: new ActionPredicate.CompareState(State: WorldRuleFacts.Population, Comparison: ActionStateComparison.GreaterOrEqual, Value: 1m)
    );
    private static WorldRule TickRule() => new(
        Name: CellName.Parse(candidate: "ticks"),
        Effects: [new WorldEffect.SetIdentityFact(Key: "0", Fact: "ticks", Expression: ValueExpression.Parse(text: RuleFacts.Tick))],
        Gate: new ActionPredicate.CompareState(State: WorldRuleFacts.Population, Comparison: ActionStateComparison.GreaterOrEqual, Value: 1m)
    );
    private static WorldRule ControlRule() => new(
        Name: CellName.Parse(candidate: "control"),
        Effects: [new ActionEffect.SetState(State: "seen", Expression: ValueExpression.Parse(text: RuleFacts.Tick))],
        Gate: new ActionPredicate.CompareState(State: WorldRuleFacts.Population, Comparison: ActionStateComparison.GreaterOrEqual, Value: 1m)
    );
    private static WorldRule ReadRule() => new(
        Name: CellName.Parse(candidate: "read"),
        Effects: [new ActionEffect.SetState(State: "seen", Value: 1m)],
        Gate: new ActionPredicate.CompareState(State: $"{WorldRuleFacts.IdentityPrefix}body:0:{Fact}", Comparison: ActionStateComparison.Equal, Value: 1m)
    );
    private static IReadOnlyList<StateCell> LaneCells(WorldFixture fixture) => (WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: WorldIdentityFactLane.RowName)?.Cells ?? []);
    private static long Lane(WorldFixture fixture, int body, string fact) {
        var cell = StateRows.FindCell(cells: LaneCells(fixture: fixture), key: CellName.Parse(candidate: WorldIdentityFactLane.Key(bodyIndex: body, fact: fact)));

        return Assert.IsType<StateCell>(@object: cell).Value;
    }
    private static long Slot(WorldFixture fixture, string row) => StateRows.FindCell(cells: WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: row)!.Cells, key: WorldStateRow.SlotKey)!.Value;
    private static long PersistedFact(string directory, string identity, string fact) {
        var document = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: WorldOwnedWorldFileName.For(id: SafeName.Parse(candidate: identity)))));
        var facts = document.Identity!.FactsOrDefault;
        var row = Assert.IsType<WorldStateRow>(@object: WorldDefinitionRows.FindStateRow(rows: document.State, name: facts.State));

        return Assert.IsType<StateCell>(@object: StateRows.FindCell(cells: row.Cells, key: CellName.Parse(candidate: fact))).Value;
    }
}
