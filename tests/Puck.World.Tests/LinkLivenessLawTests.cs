using Xunit;

using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

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

    [Fact]
    public void DropEdgeFiresExactlyOnceOnTheTickTheAuthoredGraceElapses() {
        var definition = SeamDocument(graceSeconds: 0.05f);
        var grace = definition.AdjacencyLivenessGraceTicks(adjacency: definition.Adjacencies!.Single()!).Ticks;

        Assert.True(condition: (grace > 1), userMessage: $"the fixture's authored grace must span more than one tick to discriminate; it compiled to {grace}");

        using var fixture = Fixtures.FreshServer(definition: definition);
        var drops = 0;

        for (var tick = 1; (tick <= (grace + 3)); tick++) {
            fixture.Step();

            var fired = fixture.Server.Events.Edges.Count(predicate: static edge => (edge.Family == WorldEventFamily.LinkDropped));

            drops += fired;
            // Strictly before the threshold nothing may fire; on the threshold tick exactly one edge; after it, none.
            Assert.Equal(actual: fired, expected: ((tick == grace) ? 1 : 0));
            Assert.Equal(expected: ((long)tick), actual: fixture.Server.Events.LinkStalenessTicks(adjacencyName: LinkRow));
        }

        Assert.Equal(actual: drops, expected: 1);
    }
    [Fact]
    public void AnUnauthoredGraceSensesNothingAndReadsZeroForever() {
        // The control for the law above with exactly one value different: livenessGraceSeconds 0 rather than 0.05.
        using var fixture = Fixtures.FreshServer(definition: SeamDocument(graceSeconds: 0f));

        for (var tick = 0; (tick < 32); tick++) {
            fixture.Step();
            Assert.DoesNotContain(collection: fixture.Server.Events.Edges, filter: static edge => (edge.Family is WorldEventFamily.LinkDropped or WorldEventFamily.LinkEstablished));
            Assert.Equal(expected: 0L, actual: fixture.Server.Events.LinkStalenessTicks(adjacencyName: LinkRow));
        }
    }
    [Fact]
    public void ARefreshReArmsTheSeamAndEstablishFiresOnlyAfterADrop() {
        var definition = SeamDocument(graceSeconds: 0.05f);
        var grace = definition.AdjacencyLivenessGraceTicks(adjacency: definition.Adjacencies!.Single()!).Ticks;

        using var fixture = Fixtures.FreshServer(definition: definition);

        // A refresh on a seam that never dropped re-arms the counter and emits NOTHING — the discriminating case
        // against an implementation that fires an establish edge on every delivery.
        fixture.Server.Events.ObserveLinkDelivery(adjacencyName: LinkRow);
        fixture.Step();
        Assert.Equal(expected: 0L, actual: fixture.Server.Events.LinkStalenessTicks(adjacencyName: LinkRow));
        Assert.DoesNotContain(collection: fixture.Server.Events.Edges, filter: static edge => (edge.Family is WorldEventFamily.LinkDropped or WorldEventFamily.LinkEstablished));

        for (var tick = 0; (tick < grace); tick++) {
            fixture.Step();
        }

        Assert.Contains(collection: fixture.Server.Events.Edges, filter: static edge => (edge.Family == WorldEventFamily.LinkDropped));

        fixture.Server.Events.ObserveLinkDelivery(adjacencyName: LinkRow);
        fixture.Step();

        Assert.Equal(expected: 1, actual: fixture.Server.Events.Edges.Count(predicate: static edge => (edge.Family == WorldEventFamily.LinkEstablished)));
        Assert.Equal(expected: 0L, actual: fixture.Server.Events.LinkStalenessTicks(adjacencyName: LinkRow));

        // Re-established, so nothing further fires until the grace elapses again.
        fixture.Step();
        Assert.DoesNotContain(collection: fixture.Server.Events.Edges, filter: static edge => (edge.Family is WorldEventFamily.LinkDropped or WorldEventFamily.LinkEstablished));
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

        Assert.Equal(expected: 1L, actual: AlarmCell(fixture: denied));
        Assert.Equal(expected: 0L, actual: AlarmCell(fixture: control));
    }
    [Fact]
    public void TheLinkChannelRefusesAnAdjacencyRowTheDocumentDoesNotDeclare() {
        var denied = GatedSeamDocument(graceSeconds: 0.05f) with { Adjacencies = [] };
        var control = GatedSeamDocument(graceSeconds: 0.05f);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(expectedSubstring: nameof(WorldRuleRefusal.LinkChannelMalformed), actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
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
        Assert.Equal(actual: replayed, expected: recorded);
    }
    [Fact]
    public void TheLinkDeliveryLeafSurvivesTheOnDiskTapeRoundTrip() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer(definition: SeamDocument(graceSeconds: 0.05f));

        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            liveServer: fixture.Server,
            profiles: fixture.Server.Profiles,
            transport: transport,
            engines: [],
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

        using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name));

        var snapshot = WorldReplaySnapshot.Read(stream: stream);
        var kinds = snapshot.Ticks
            .SelectMany(selector: static tick => tick.Authority)
            .Select(selector: static entry => entry.GetType().Name)
            .ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Contains(collection: kinds, expected: "LinkDelivery");
    }
    [Fact]
    public void EverySubmittedMutationTapesWithTheEnvelopesOwnActor() {
        using var fixture = Fixtures.FreshServer();

        var observed = new List<WorldPrincipal>();

        fixture.Server.MutationTap = (_, actor) => observed.Add(item: actor);

        var peer = WorldPrincipal.Peer(
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
            Payload: new WorldSubmissionPayload.Mutation(Value: new WorldMutation.UpsertStateRow(
                Principal: peer,
                Row: new WorldStateRow(Name: WorldCellName.Parse(candidate: "peer-probe"), Kind: CellKind.Int)
            ))
        ));

        Assert.Equal(actual: observed, expected: [peer]);

        // The control: the two internal producers reach EnqueueMutation directly and must NOT tape — they re-derive
        // during a drive, so taping them would apply each twice.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: new WorldStateRow(Name: WorldCellName.Parse(candidate: "internal-probe"), Kind: CellKind.Int)
        ));

        Assert.Equal(actual: observed, expected: [peer]);
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

        Assert.Equal(expected: GrantSubject.Adjacency(name: LinkRow), actual: dropped.GateA);
        Assert.Null(@object: dropped.GateB);

        fixture.Server.Events.ObserveLinkDelivery(adjacencyName: LinkRow);
        fixture.Step();

        var established = fixture.Server.Events.Edges.Single(predicate: static edge => (edge.Family == WorldEventFamily.LinkEstablished));

        Assert.Equal(expected: GrantSubject.Adjacency(name: LinkRow), actual: established.GateA);
    }
    [Fact]
    public void AnUntrustedObserveAdjacencyRowRequiresAnEventBudget_AndATrustedPrincipalIsRefusedOne() {
        using var fixture = Fixtures.FreshServer(definition: SeamDocument(graceSeconds: 0.05f));

        var addon = WorldPrincipal.Addon(name: "probe");
        var subject = GrantSubject.Adjacency(name: LinkRow);

        Laws.RefusalWithControl(
            lawId: "authority.untrusted-observe-adjacency-requires-events",
            deniedOutcome: () => {
                fixture.Server.Grant(
                    actor: WorldPrincipal.Console,
                    grant: new WorldGrant(
                        Budget: 4,
                        Capability: WorldCapability.Observe,
                        Exclusive: false,
                        Principal: addon,
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
                    actor: WorldPrincipal.Console,
                    grant: new WorldGrant(
                        Budget: 4,
                        Capability: WorldCapability.Observe,
                        EventBudget: 4,
                        Exclusive: false,
                        Principal: addon,
                        Subject: subject
                    )
                );

                return (fixture.Server.Grants.Allows(
                    capability: WorldCapability.Observe,
                    principal: addon,
                    subject: subject
                ).IsAllowed &&
                    fixture.Server.Grants.TryGetEventBudget(
                    budget: out _,
                    capability: WorldCapability.Observe,
                    principal: addon,
                    subject: subject
                ));
            });

        // A trusted principal has no consumer for the event-only subject — refused on the same terms as
        // region/seat, so the row cannot sit in a seat's set as an inert hold.
        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: new WorldGrant(
                Capability: WorldCapability.Observe,
                Exclusive: false,
                Principal: WorldPrincipal.Seat(slot: 1),
                Subject: subject
            )
        );

        Assert.Equal(
            expected: GrantRule.WildcardHold,
            actual: fixture.Server.Grants.Allows(
                capability: WorldCapability.Observe,
                principal: WorldPrincipal.Seat(slot: 1),
                subject: subject
            ).Rule
        );
    }

    private static long AlarmCell(WorldFixture fixture) =>
        fixture.Server.Definition.State.Single(predicate: static row => (row.Name.Value == "alarm")).Cells!.Single().Value;
    private static WorldDefinition GatedSeamDocument(float graceSeconds) {
        var definition = SeamDocument(graceSeconds: graceSeconds);
        var alarm = WorldCellName.Parse(candidate: "alarm");
        var grace = definition.AdjacencyLivenessGraceTicks(adjacency: definition.Adjacencies!.Single()!).Ticks;

        return definition with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                    Name: alarm,
                    Kind: CellKind.Int,
                    NonNegative: true,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0L)])
            ]),
            Rules = [
                new WorldRule(
                    Name: WorldCellName.Parse(candidate: "seam-alarm"),
                    Gate: new ActionPredicate.CompareState(
                        State: $"{WorldRuleFacts.LinkPrefix}{LinkRow}",
                        Comparison: ActionStateComparison.GreaterOrEqual,
                        Value: grace
                    ),
                    Effects: [new ActionEffect.SetState(State: alarm.Value, Value: 1m)])
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
                Name: WorldSafeName.Parse(candidate: "north-neighbour"),
                Document: "north.world.json"
            )
        ],
        Destinations = [
            new WorldDestination(
                Name: WorldSafeName.Parse(candidate: "north-destination"),
                Reference: "north-neighbour",
                Durability: WorldDestinationDurability.Persisted,
                Scope: WorldDestinationScope.Global
            )
        ],
        Adjacencies = [
            new WorldAdjacency(
                Name: WorldSafeName.Parse(candidate: LinkRow),
                Destination: "north-destination",
                Counterpart: "south",
                Boundary: new WorldAdjacencyBoundary(
                    Center: new System.Numerics.Vector3(x: 0f, y: 0f, z: 8f),
                    OutwardYawDegrees: 0f,
                    OutwardPitchDegrees: 0f,
                    Width: 16f,
                    Height: 8f
                ),
                LivenessGraceSeconds: graceSeconds
            )
        ],
    };
}
