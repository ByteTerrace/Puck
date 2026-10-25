using System.Security.Cryptography;
using System.Text;
using Puck.Testing;
using Puck.Commands;
using Puck.Scripting;
using Puck.World.Addons;
using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>
/// THE LAW: a federation seam's liveness is a tick-derived fact a world can act on, and every input it derives from
/// is taped. Four halves, each paired with a control that differs in exactly one authored or observed value:
/// <list type="bullet">
/// <item>the drop edge fires exactly once, on the tick the authored grace elapses, and never again;</item>
/// <item>a delivered refresh re-arms the seam — staleness returns to zero, and the establish edge fires only after a
/// drop, never on an ordinary refresh;</item>
/// <item>a rule gated on <c>$link:</c> fires when the seam is stale and stays closed when the identical document is
/// refreshed every tick;</item>
/// <item>the taped <c>LinkDelivery</c> leaf is sufficient — a feed driven only by the recorded booleans produces the
/// identical edge sequence as the live one, and the leaf survives the on-disk tape round trip.</item>
/// </list>
/// </summary>
public sealed class LinkLivenessLawTests {
    private const string LinkRow = "north";

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AGuestResolvesLinkEdgesThroughItsOwnManifest(bool wildcard) {
        using var directory = new TemporaryDirectory(prefix: "puck-link-events-");
        var definition = SeamDocument(graceSeconds: 0.2f);
        var north = definition.Adjacencies!.Single()!;
        var east = north with { Name = SafeName.Parse(candidate: "east"), LivenessGraceSeconds = 0.3f };
        var northGrace = definition.AdjacencyLivenessGraceTicks(adjacency: north).Ticks;
        var eastGrace = definition.AdjacencyLivenessGraceTicks(adjacency: east).Ticks;
        var northIndex = (wildcard ? -1 : 0);
        var eastIndex = (wildcard ? -1 : 2);
        // The real guest traps if any drop names the wrong link or loses its staleness, or if an establish
        // carries a document ordinal. Interleaved, duplicate and ungranted requests must not renumber its table.
        var wat = $$"""
            (module
              (memory (export "memory") 1)
              (data (i32.const 256) "\02\00\01\00\00\00\00\00\00\00\00\00\00\00\00\00\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
              (func (export "puck_abi_version") (result i32) (i32.const 1))
              (func (export "puck_channels_ptr") (result i32) (i32.const 256))
              (func (export "puck_channels_count") (result i32) (i32.const 2))
              (func (export "puck_out_ptr") (result i32) (i32.const 4096))
              (func (export "puck_out_cap") (result i32) (i32.const 0))
              (func (export "puck_in_ptr") (result i32) (i32.const 8192))
              (func (export "puck_in_cap") (result i32) (i32.const 64))
              (func (export "puck_on_tick") (param $count i32) (result i32)
                (local $i i32) (local $p i32) (local $a i64) (local $b i64)
                (block $done (loop $next
                  (br_if $done (i32.ge_u (local.get $i) (local.get $count)))
                  (local.set $p (i32.add (i32.const 8192) (i32.mul (local.get $i) (i32.const 32))))
                  (if (i32.eq (i32.load8_u (local.get $p)) (i32.const 3)) (then
                    (local.set $a (i64.load offset=16 (local.get $p)))
                    (local.set $b (i64.load offset=24 (local.get $p)))
                    (if (i32.eq (i32.load8_u offset=9 (local.get $p)) (i32.const 12)) (then
                      (if (i64.eq (local.get $b) (i64.const {{northGrace}}))
                        (then (if (i64.ne (local.get $a) (i64.const {{northIndex}})) (then unreachable)))
                        (else
                          (if (i64.ne (local.get $b) (i64.const {{eastGrace}})) (then unreachable))
                          (if (i64.ne (local.get $a) (i64.const {{eastIndex}})) (then unreachable))))))
                    (if (i32.eq (i32.load8_u offset=9 (local.get $p)) (i32.const 11)) (then
                      (if (i64.ne (local.get $b) (i64.const 0)) (then unreachable))
                      (if (i32.and (i64.ne (local.get $a) (i64.const {{northIndex}}))
                            (i64.ne (local.get $a) (i64.const {{eastIndex}}))) (then unreachable))))))
                  (local.set $i (i32.add (local.get $i) (i32.const 1))) (br $next)))
                (i32.const 0)))
            """;
        var bytes = Encoding.UTF8.GetBytes(s: wat);
        var path = directory.PathOf(name: "links.wat").Replace(newChar: '/', oldChar: '\\');

        File.WriteAllBytes(bytes: bytes, path: path);
        var principal = Principal.Addon(name: "links");
        var northSubject = GrantSubject.Adjacency(name: LinkRow);
        var eastSubject = GrantSubject.Adjacency(name: "east");

        definition = definition with {
            Adjacencies = [east, north with { Name = SafeName.Parse(candidate: "hidden") }, north],
            AddonsRaw = [new WorldAddonRow("links", path,
                ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes))), 1000000UL, true,
                (wildcard ? [new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: GrantSubject.All)] : [
                    new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: northSubject),
                    new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: GrantSubject.Body(index: 0)),
                    new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: GrantSubject.Adjacency(name: "hidden")),
                    new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: eastSubject),
                    new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: northSubject),
                ]))],
            GrantsRaw = [
                new WorldGrant(principal, WorldCapability.Observe, eastSubject, Exclusive: false, Budget: 4, EventBudget: 4),
                new WorldGrant(principal, WorldCapability.Observe, northSubject, Exclusive: false, Budget: 4, EventBudget: 4),
            ],
        };
        using var fixture = Fixtures.FreshServer(definition);
        using var runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);

        for (var tick = 0; (tick < (eastGrace + 2)); tick++) { fixture.Step(); }
        fixture.Server.Events.ObserveLinkDelivery(adjacencyName: LinkRow);
        fixture.Server.Events.ObserveLinkDelivery(adjacencyName: "east");
        fixture.Step();
        fixture.Step();
        fixture.Step();
        var cost = Assert.Single(collection: runtime.DescribeCost());

        Assert.Equal(AddonState.Enabled, cost.State);
        Assert.Equal(4UL, cost.EventCellsDelivered);
        Assert.Equal(0UL, cost.EventGaps);
    }

    private static long AlarmCell(WorldFixture fixture) =>
        fixture.Server.Definition.State.Single(predicate: static row => (row.Name.Value == "alarm")).Cells!.Single().Value.AsInt;
    private static WorldDefinition GatedSeamDocument(float graceSeconds) {
        var definition = SeamDocument(graceSeconds: graceSeconds);
        var alarm = CellName.Parse(candidate: "alarm");
        var grace = definition.AdjacencyLivenessGraceTicks(adjacency: definition.Adjacencies!.Single()!).Ticks;

        return definition with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                Name: alarm,
                Kind: CellKind.Int,
                Min: 0L,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 0L)
                    )]
            )
            ]),
            Rules = [
                new WorldRule(
                Name: CellName.Parse(candidate: "seam-alarm"),
                Gate: new ActionPredicate.CompareState(
                    State: $"{WorldRuleFacts.LinkPrefix}{LinkRow}",
                    Comparison: ExpressionOp.GreaterOrEqual,
                    Value: grace
                ),
                Effects: [new ActionEffect.SetState(
                        State: alarm.Value,
                        Value: 1m
                    )]
            )
            ],
        };
    }
    private static IEnumerable<string> LinkEdges(WorldFixture fixture) =>
        fixture.Server.Events.Edges
            .Where(predicate: static edge => (edge.Family is WorldEventFamily.LinkDropped or WorldEventFamily.LinkEstablished))
            .Select(selector: static edge => $"{edge.Family}:{edge.A}:{edge.B}")
            .ToArray();
    // A document carrying ONE adjacency row. The row's destination/counterpart never resolve in this project (no
    // adjacency source is installed), which is exactly the state under test: nothing is delivered unless a law says
    // so.
    private static WorldDefinition SeamDocument(float graceSeconds) => Fixtures.BuildDocument() with {
        References = [
            new WorldReference(
            Name: SafeName.Parse(candidate: "north-neighbour"),
            Document: "north"
        )
        ],
        Destinations = [
            new WorldDestination(
            Name: SafeName.Parse(candidate: "north-destination"),
            Reference: "north-neighbour",
            Durability: WorldDestinationDurability.Persisted,
            Scope: WorldDestinationScope.Global
        )
        ],
        Adjacencies = [
            new WorldAdjacency(
            Name: SafeName.Parse(candidate: LinkRow),
            Destination: "north-destination",
            Counterpart: "south",
            Boundary: new WorldAdjacencyBoundary(
                Center: new System.Numerics.Vector3(
                    x: 0f,
                    y: 0f,
                    z: 8f
                ),
                OutwardYawDegrees: 0f,
                OutwardPitchDegrees: 0f,
                Width: 16f,
                Height: 8f
            ),
            LivenessGraceSeconds: graceSeconds
        )
        ],
    };

    [Fact]
    public void ARefreshReArmsTheSeamAndEstablishFiresOnlyAfterADrop() {
        var definition = SeamDocument(graceSeconds: 0.05f);
        var grace = definition.AdjacencyLivenessGraceTicks(adjacency: definition.Adjacencies!.Single()!).Ticks;

        using var fixture = Fixtures.FreshServer(definition: definition);

        // A refresh on a seam that never dropped re-arms the counter and emits NOTHING — the discriminating case
        // against an implementation that fires an establish edge on every delivery.
        fixture.Server.Events.ObserveLinkDelivery(adjacencyName: LinkRow);
        fixture.Step();
        Assert.Equal(
            expected: 0L,
            actual: fixture.Server.Events.LinkStalenessTicks(adjacencyName: LinkRow)
        );
        Assert.DoesNotContain(
            collection: fixture.Server.Events.Edges,
            filter: static edge => (edge.Family is WorldEventFamily.LinkDropped or WorldEventFamily.LinkEstablished)
        );

        for (var tick = 0; (tick < grace); tick++) {
            fixture.Step();
        }

        Assert.Contains(
            collection: fixture.Server.Events.Edges,
            filter: static edge => (edge.Family == WorldEventFamily.LinkDropped)
        );

        fixture.Server.Events.ObserveLinkDelivery(adjacencyName: LinkRow);
        fixture.Step();

        Assert.Equal(
            expected: 1,
            actual: fixture.Server.Events.Edges.Count(predicate: static edge => (edge.Family == WorldEventFamily.LinkEstablished))
        );
        Assert.Equal(
            expected: 0L,
            actual: fixture.Server.Events.LinkStalenessTicks(adjacencyName: LinkRow)
        );

        // Re-established, so nothing further fires until the grace elapses again.
        fixture.Step();
        Assert.DoesNotContain(
            collection: fixture.Server.Events.Edges,
            filter: static edge => (edge.Family is WorldEventFamily.LinkDropped or WorldEventFamily.LinkEstablished)
        );
    }
    [Fact]
    public void ARuleGatedOnLinkStalenessFiresOnlyWhenTheSeamIsStale() {
        var definition = GatedSeamDocument(graceSeconds: 0.05f);
        var grace = definition.AdjacencyLivenessGraceTicks(adjacency: definition.Adjacencies!.Single()!).Ticks;

        using var denied = Fixtures.FreshServer(definition: definition);
        using var control = Fixtures.FreshServer(definition: definition);

        // ONE value different between the two runs: the control observes a delivered refresh every tick, the denied
        // run observes none. Same document, same rule, same tick count.
        for (var tick = 0; (tick <= (grace + 1)); tick++) {
            control.Server.Events.ObserveLinkDelivery(adjacencyName: LinkRow);
            control.Step();
            denied.Step();
        }

        Assert.Equal(
            expected: 1L,
            actual: AlarmCell(fixture: denied)
        );
        Assert.Equal(
            expected: 0L,
            actual: AlarmCell(fixture: control)
        );
    }
    [Fact]
    public void AnUnauthoredGraceSensesNothingAndReadsZeroForever() {
        // The control for the law above with exactly one value different: livenessGraceSeconds 0 rather than 0.05.
        using var fixture = Fixtures.FreshServer(definition: SeamDocument(graceSeconds: 0f));

        for (var tick = 0; (tick < 4); tick++) {
            fixture.Step();
            Assert.DoesNotContain(
                collection: fixture.Server.Events.Edges,
                filter: static edge => (edge.Family is WorldEventFamily.LinkDropped or WorldEventFamily.LinkEstablished)
            );
            Assert.Equal(
                expected: 0L,
                actual: fixture.Server.Events.LinkStalenessTicks(adjacencyName: LinkRow)
            );
        }
    }
    [Fact]
    public void AnUntrustedObserveAdjacencyRowRequiresAnEventBudget_AndATrustedPrincipalIsRefusedOne() {
        using var fixture = Fixtures.FreshServer(definition: SeamDocument(graceSeconds: 0.05f));

        var addon = Principal.Addon(name: "probe");
        var subject = GrantSubject.Adjacency(name: LinkRow);

        Laws.RefusalWithControl(
            lawId: "authority.untrusted-observe-adjacency-requires-events",
            deniedOutcome: () => {
                fixture.Server.Grant(
                    actor: Principal.Console,
                    grant: new WorldGrant(
                        Budget: 4,
                        Capability: WorldCapability.Observe,
                        Exclusive: false,
                        Grantee: addon,
                        Subject: subject
                    )
                );

                return fixture.Server.Grants.Allows(
                    capability: WorldCapability.Observe,
                    principal: addon,
                    subject: subject
                ).IsAllowed;
            },
            controlOutcome: () => {
                fixture.Server.Grant(
                    actor: Principal.Console,
                    grant: new WorldGrant(
                        Budget: 4,
                        Capability: WorldCapability.Observe,
                        EventBudget: 4,
                        Exclusive: false,
                        Grantee: addon,
                        Subject: subject
                    )
                );

                return (
                    fixture.Server.Grants.Allows(
                    capability: WorldCapability.Observe,
                    principal: addon,
                    subject: subject
                ).IsAllowed &&
                    fixture.Server.Grants.TryGetEventBudget(
                    budget: out _,
                    capability: WorldCapability.Observe,
                    principal: addon,
                    subject: subject
                )
                );
            }
        );

        // A trusted principal has no consumer for the event-only subject — refused on the same terms as
        // region/seat, so the row cannot sit in a seat's set as an inert hold.
        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Capability: WorldCapability.Observe,
                Exclusive: false,
                Grantee: Principal.Seat(slot: 1),
                Subject: subject
            )
        );

        Assert.Equal(
            expected: GrantRule.WildcardHold,
            actual: fixture.Server.Grants.Allows(
                capability: WorldCapability.Observe,
                principal: Principal.Seat(slot: 1),
                subject: subject
            ).Rule
        );
    }
    [Fact]
    public void DropEdgeFiresExactlyOnceOnTheTickTheAuthoredGraceElapses() {
        var definition = SeamDocument(graceSeconds: 0.05f);
        var grace = definition.AdjacencyLivenessGraceTicks(adjacency: definition.Adjacencies!.Single()!).Ticks;

        Assert.True(
            condition: (grace > 1),
            userMessage: $"the fixture's authored grace must span more than one tick to discriminate; it compiled to {grace}"
        );

        using var fixture = Fixtures.FreshServer(definition: definition);
        var drops = 0;

        for (var tick = 1; (tick <= (grace + 3)); tick++) {
            fixture.Step();

            var fired = fixture.Server.Events.Edges.Count(predicate: static edge => (edge.Family == WorldEventFamily.LinkDropped));

            drops += fired;
            // Strictly before the threshold nothing may fire; on the threshold tick exactly one edge; after it, none.
            Assert.Equal(
                actual: fired,
                expected: ((tick == grace)
                ? 1
                : 0)
            );
            Assert.Equal(
                expected: ((long)tick),
                actual: fixture.Server.Events.LinkStalenessTicks(adjacencyName: LinkRow)
            );
        }

        Assert.Equal(
            actual: drops,
            expected: 1
        );
    }
    [Fact]
    public void EverySubmittedMutationTapesWithTheEnvelopesOwnActor() {
        using var fixture = Fixtures.FreshServer();

        var observed = new List<Principal>();

        fixture.Server.MutationTap = (_, actor) => observed.Add(item: actor);

        var peer = Principal.Peer(
            generation: 1,
            index: 4
        );

        // A submission that never touches the loopback — the shape a forwarded traveller's write and an admitted
        // socket peer's write both take.
        fixture.Server.Submit(envelope: new SubmissionEnvelope(
            ConnectionId: 4,
            SessionGeneration: 1,
            Sequence: 1,
            CorrelationId: 1,
            Principal: peer,
            // A mutation envelope is bound to its operation id at the ingress, and one without an id is refused
            // there, before the tape's tap.
            OperationId: new Guid(g: "6f1d2a3e-0000-4000-8000-000000000004"),
            Payload: new WorldSubmissionPayload.Mutation(Value: new WorldMutation.UpsertStateRow(
                Principal: peer,
                Row: new WorldStateRow(
                    Name: CellName.Parse(candidate: "peer-probe"),
                    Kind: CellKind.Int
                )
            ))
        ));

        Assert.Equal(
            actual: observed,
            expected: [peer]
        );

        // The control: the two internal producers reach EnqueueMutation directly and must NOT tape — they re-derive
        // during a drive, so taping them would apply each twice.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: new WorldStateRow(
                Name: CellName.Parse(candidate: "internal-probe"),
                Kind: CellKind.Int
            )
        ));

        Assert.Equal(
            actual: observed,
            expected: [peer]
        );
    }
    [Fact]
    public void LinkEdgesGateOnTheAdjacencyRowsOwnSubject() {
        var definition = SeamDocument(graceSeconds: 0.05f);
        var grace = definition.AdjacencyLivenessGraceTicks(adjacency: definition.Adjacencies!.Single()!).Ticks;

        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var tick = 0; (tick < grace); tick++) {
            fixture.Step();
        }

        // The gate is the row's OWN subject — the grantable `observe adjacency:<name>` an addon's event filter
        // consults — never the wildcard, which no untrusted principal can ever hold.
        var dropped = fixture.Server.Events.Edges.Single(predicate: static edge => (edge.Family == WorldEventFamily.LinkDropped));

        Assert.Equal(
            expected: GrantSubject.Adjacency(name: LinkRow),
            actual: dropped.GateA
        );
        Assert.Null(@object: dropped.GateB);

        fixture.Server.Events.ObserveLinkDelivery(adjacencyName: LinkRow);
        fixture.Step();

        var established = fixture.Server.Events.Edges.Single(predicate: static edge => (edge.Family == WorldEventFamily.LinkEstablished));

        Assert.Equal(
            expected: GrantSubject.Adjacency(name: LinkRow),
            actual: established.GateA
        );
    }
    [Fact]
    public void TheLinkChannelRefusesAnAdjacencyRowTheDocumentDoesNotDeclare() {
        var denied = GatedSeamDocument(graceSeconds: 0.05f) with { Adjacencies = [] };
        var control = GatedSeamDocument(graceSeconds: 0.05f);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: denied,
            reason: out var reason
        ));
        Assert.Contains(
            expectedSubstring: nameof(WorldRuleRefusal.LinkChannelMalformed),
            actualString: reason,
            comparisonType: StringComparison.Ordinal
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: control,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    [Fact]
    public void TheLinkDeliveryLeafSurvivesTheOnDiskTapeRoundTrip() {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-");

        using var fixture = Fixtures.FreshServer(definition: SeamDocument(graceSeconds: 0.05f));

        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
            liveServer: fixture.Server,
            profiles: fixture.Server.Profiles,
            transport: transport,
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            addonHostFactory: static (_, _) => new NullAddonHost()
        );
        var name = $"link-delivery-capture-{Guid.NewGuid():N}";

        Assert.True(
            condition: tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: $"refused to arm: {refusal}"
        );

        fixture.Server.LinkDeliveryTap?.Invoke(obj: LinkRow);
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();

        using var stream = File.OpenRead(path: tape.PathFor(name: name));

        var snapshot = WorldReplaySnapshot.Read(stream: stream);
        var kinds = snapshot.Ticks
            .SelectMany(selector: static tick => tick.Authority)
            .Select(selector: static entry => entry.GetType().Name)
            .ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Contains(
            collection: kinds,
            expected: "LinkDelivery"
        );
    }
    [Fact]
    public void TheTapedDeliveryBooleanAloneReproducesTheLiveEdgeSequence() {
        var definition = SeamDocument(graceSeconds: 0.05f);
        var grace = definition.AdjacencyLivenessGraceTicks(adjacency: definition.Adjacencies!.Single()!).Ticks;
        // The live run: delivered on the first tick, then dark long enough to drop, then delivered again.
        var deliveries = new bool[(grace + 4)];

        deliveries[0] = true;
        deliveries[^1] = true;

        using var live = Fixtures.FreshServer(definition: definition);
        using var taped = Fixtures.FreshServer(definition: definition);
        var recorded = new List<string>();
        var replayed = new List<string>();

        for (var tick = 0; (tick < deliveries.Length); tick++) {
            if (deliveries[tick]) {
                // The live entry point reports a REFRESH only on a strictly higher neighbour tick — the value the
                // tape's own leaf is derived from.
                Assert.True(condition: live.Server.Events.ObserveLinkDelivery(
                    adjacencyName: LinkRow,
                    deliveredTick: ((ulong)(tick + 1))
                ));
                // Re-delivering the SAME neighbour tick is not a refresh and must not reach the tape.
                Assert.False(condition: live.Server.Events.ObserveLinkDelivery(
                    adjacencyName: LinkRow,
                    deliveredTick: ((ulong)(tick + 1))
                ));
                taped.Server.Events.ObserveLinkDelivery(adjacencyName: LinkRow);
            }

            live.Step();
            taped.Step();
            recorded.AddRange(collection: LinkEdges(fixture: live));
            replayed.AddRange(collection: LinkEdges(fixture: taped));
        }

        Assert.NotEmpty(collection: recorded);
        Assert.Equal(
            actual: replayed,
            expected: recorded
        );
    }
}
