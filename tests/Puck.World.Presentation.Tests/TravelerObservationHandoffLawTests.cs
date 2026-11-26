using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a remote player's observation of its traveler is one stream for as long as the authority it
/// entered through holds its connection. The player's host hands the traveler to a destination behind its door and
/// observes it there; the traveler then walks to a second authority of the destination's host and back. The entry
/// authority keeps the stream open across both crossings: each reaches the reader as a cut of the stream it already
/// holds, followed by the whole document of the world the traveler now stands in, served by the entry authority
/// itself or relayed from the other one. The reader's one ingress takes every record, the stream's identity never
/// changes, its span is never closed, and the traveler is back at the entry authority as the peer principal it left
/// as, with the grants its admission gives an arrival.
/// </summary>
public sealed class TravelerObservationHandoffLawTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(seconds: 60);

    // A reader as a mirror is one: every record passes its one ingress, a cut or a generation step withdraws what it
    // held, and a close of the span ends it.
    private sealed class Reader : IClientSink {
        private readonly Lock m_gate = new();
        private readonly WorldStreamIngress m_ingress;
        private readonly List<string> m_authorities = [];
        private WorldDefinition? m_document;
        private string? m_authority;
        private int m_closes;
        private int m_cuts;
        private int m_terminalCuts;
        private int m_withdrawals;

        public Reader() => m_ingress = new WorldStreamIngress(withdraw: () => {
            m_document = null;
            m_authority = null;
            m_withdrawals++;
        });

        public WorldStreamSpan Span => m_ingress.Span;
        public long Refusals => m_ingress.Refusals;
        public int Closes { get { lock (m_gate) { return m_closes; } } }
        public int Cuts { get { lock (m_gate) { return m_cuts; } } }
        public int TerminalCuts { get { lock (m_gate) { return m_terminalCuts; } } }
        public int Withdrawals { get { lock (m_gate) { return m_withdrawals; } } }
        // Each authority a snapshot named after the one before it named another: the worlds the reader was shown, in order.
        public string[] Authorities { get { lock (m_gate) { return [.. m_authorities]; } } }
        // The authority whose document and bodies the reader holds now, or null while it holds either of neither.
        public string? Shown { get { lock (m_gate) { return ((m_document is null) ? null : m_authority); } } }

        public void DeliverSnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream) {
            lock (m_gate) {
                if (!m_ingress.Admit(stream: in stream)) { return; }
                m_authority = snapshot.Authority;
                if ((m_authorities.Count == 0) || (m_authorities[^1] != snapshot.Authority)) {
                    m_authorities.Add(item: snapshot.Authority);
                }
            }
        }
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) {
            lock (m_gate) {
                if (m_ingress.Admit(stream: in stream)) { m_document = definition; }
            }
        }
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) {
            lock (m_gate) {
                if (m_ingress.Admit(stream: in stream)) { m_document = definition; }
            }
        }
        public void DeliverPresentation(WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) {
            lock (m_gate) { _ = m_ingress.Admit(stream: in stream); }
        }
        public void DeliverCut(in WorldStreamStamp stream, bool terminal) {
            lock (m_gate) {
                if (!m_ingress.Cut(stream: in stream, terminal: terminal)) { return; }
                m_cuts++;
                if (terminal) { m_terminalCuts++; }
            }
        }
        public void CloseSpan(ulong stream) {
            lock (m_gate) {
                if (m_ingress.Close(stream: stream)) { m_closes++; }
            }
        }
        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) { }
        public void DeliverSessionLever(WorldSessionLever lever) { }
    }

    // Steps both hosts, as their loops would, until the condition holds.
    private static void StepUntil(FederatedHosts hosts, Func<bool> condition, string what) {
        using var deadline = new CancellationTokenSource(delay: Deadline);

        while (!condition()) {
            Assert.False(condition: deadline.IsCancellationRequested, userMessage: $"timed out waiting until {what}");
            hosts.Destination.DrainPendingTransfers();
            hosts.Destination.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            hosts.Source.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            Thread.Sleep(millisecondsTimeout: 1);
        }
    }
    // The body indices a row of the destination host holds remote travelers at, lowest first.
    private static int[] TravelerSlots(WorldServer server) => server.ExecuteAuthorityOperation(operation: () => {
        var slots = new List<int>();

        for (var index = server.Population.LocalSeatCount; (index < server.Population.Capacity); index++) {
            if (server.Population.IsActive(index: index) && server.Population.IsAdmittedPeer(bodyIndex: index)) {
                slots.Add(item: index);
            }
        }
        return slots.ToArray();
    });
    // The body index a row of the destination host holds its lowest remote traveler at, or -1 for none.
    private static int TravelerSlot(WorldServer server) => ((TravelerSlots(server: server) is [var lowest, ..]) ? lowest : -1);
    // Hands local seat `slot` of the source host to the destination behind its door.
    private static void Enter(FederatedHosts hosts, int slot) {
        hosts.JoinSeat(slot: slot, travelTurn: default);
        _ = hosts.Source.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: hosts.RemoteDestination,
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: WorldInstanceHost.BootInstanceName,
            sourceSlot: slot
        );
        hosts.DrainCrossing(slot: slot);
    }
    // Walks the source row's lowest traveler to another row of the destination host, under its own admitted principal,
    // as a crossing the row's own scan mints does.
    private static void Walk(FederatedHosts hosts, string from, WorldServer source, string to, WorldServer target) {
        var slot = TravelerSlot(server: source);

        Assert.True(condition: (slot >= 0), userMessage: $"'{from}' holds no remote traveler to walk to '{to}'");
        var traveling = source.ExecuteAuthorityOperation(operation: () => source.Population.PeerPrincipal(index: slot));
        var leaving = TravelerSlots(server: source).Length;
        var landing = TravelerSlots(server: target).Length;

        _ = hosts.Destination.EnqueueTransfer(
            actingPrincipal: traveling,
            destination: WorldInstanceHost.TransferDestination.Existing(name: to),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: from,
            sourceSlot: slot
        );
        StepUntil(
            condition: () => ((TravelerSlots(server: source).Length == (leaving - 1)) && (TravelerSlots(server: target).Length == (landing + 1))),
            hosts: hosts,
            what: $"the traveler landed at '{to}'"
        );
    }

    // THE LAW: the body index a traveler left an authority from is kept for its return while another index is free.
    // A second player arriving while the first is away takes the next index, where the lowest free index is the one
    // the first left; the first then comes back to its own index as the peer it left as. Red leg: without the kept
    // index the second arrival lands on the first's index and advances its generation, so the first returns elsewhere
    // as a new peer.
    [Fact]
    public void AnArrivalLeavesAnAbsentTravelersIndexForItsReturn() {
        using var hosts = FederatedHosts.Build();
        var entry = hosts.DestinationRow.Server;
        var onward = hosts.OnwardRow.Server;

        Enter(hosts: hosts, slot: 0);
        var body = TravelerSlot(server: entry);
        var admitted = entry.ExecuteAuthorityOperation(operation: () => entry.Population.PeerPrincipal(index: body));

        Walk(from: FederatedHosts.DestinationName, hosts: hosts, source: entry, target: onward, to: FederatedHosts.OnwardName);
        Assert.Empty(collection: TravelerSlots(server: entry));
        Enter(hosts: hosts, slot: 1);
        Assert.Equal(expected: [(body + 1)], actual: TravelerSlots(server: entry));

        Walk(from: FederatedHosts.OnwardName, hosts: hosts, source: onward, target: entry, to: FederatedHosts.DestinationName);
        Assert.Equal(expected: [body, (body + 1)], actual: TravelerSlots(server: entry));
        Assert.Equal(expected: admitted, actual: entry.ExecuteAuthorityOperation(operation: () => entry.Population.PeerPrincipal(index: body)));
        Assert.True(condition: entry.ExecuteAuthorityOperation(operation: () => entry.IsLivePeer(principal: admitted)));
    }

    // What a relaying authority wrote to its recipient's connection, read back record by record.
    private sealed class Relayed {
        private readonly Lock m_gate = new();
        private readonly List<(WorldFederationResponse Kind, WorldStreamStamp Stamp, string? Authority)> m_records = [];

        public Relayed(Stream input, CancellationToken ct) => Reading = Task.Run(function: async () => {
            while (true) {
                var frame = await WorldFederationCodec.ReadResponseAsync(ct: ct, stream: input);

                if (!frame.Ok) { return; }
                var kind = ((WorldFederationResponse)frame.Kind);
                var stamp = default(WorldStreamStamp);
                string? authority = null;

                if (kind == WorldFederationResponse.Cut) {
                    _ = WorldFederationCodec.TryDecodeCut(body: frame.Body.Span, stream: out stamp, terminal: out _);
                } else if (
                    WorldFederationCodec.TryReadStamped(record: frame.Body.Span, stream: out stamp, body: out var body, failure: out _) &&
                    (kind == WorldFederationResponse.Snapshot) &&
                    WorldFederationCodec.TryDecodeSnapshot(body: body, snapshot: out var snapshot, failure: out _)
                ) {
                    authority = snapshot.Authority;
                }
                lock (m_gate) { m_records.Add(item: (kind, stamp, authority)); }
            }
        }, cancellationToken: ct);

        public Task Reading { get; }
        public (WorldFederationResponse Kind, WorldStreamStamp Stamp, string? Authority)[] Records { get { lock (m_gate) { return [.. m_records]; } } }
        public bool Shows(string authority) => Records.Any(predicate: record => (record.Authority == authority));
    }

    // THE LAW: an authority whose traveler stands in another process serves its recipient's stream from there without
    // ending it. It lends the far authority the stream's counters with the request; the far authority stamps its
    // records as the stream's next generation, behind a cut; the relaying authority's lineage follows each stamp; and
    // when the traveler moves on, the far authority hands the stream back with a record the recipient never sees.
    // Asked again, the same stream goes on, here from the authority the traveler walked on to, behind another cut.
    [Fact]
    public async Task ARelayedSegmentContinuesItsStreamAcrossASocketAndIsHandedBackWhenTheTravelerMovesOn() {
        WorldRemoteAuthority? traveler = null;
        using var hosts = FederatedHosts.Build(travelerRouteStarted: wrapper => traveler = wrapper);
        var entry = hosts.DestinationRow.Server;
        var onward = hosts.OnwardRow.Server;

        Enter(hosts: hosts, slot: 0);
        Assert.NotNull(@object: traveler);
        Assert.True(condition: traveler.TryRouteCredential(bodyIndex: -1, credential: out var credential));

        // The relaying authority's stream has stamped one record of its own, so the relayed segment owes a cut.
        var lineage = new WorldStreamLineage();
        var local = new object();

        Assert.False(condition: lineage.Open(owner: local));
        var own = lineage.Stamp(owner: local);
        var pipe = new System.IO.Pipelines.Pipe(options: new System.IO.Pipelines.PipeOptions(pauseWriterThreshold: 0L));
        var session = new WorldProjectionSession(lineage: lineage, output: pipe.Writer.AsStream());
        var arm = new WorldRemoteForwardedAuthority(authority: traveler, credential: credential);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);
        var relayed = new Relayed(ct: lifetime.Token, input: pipe.Reader.AsStream());
        var first = arm.StreamProjectionAsync(ceiling: WorldDisclosureTier.Replica, ct: lifetime.Token, remainingHops: 8, session: session);

        StepUntil(condition: () => relayed.Shows(authority: entry.AuthorityIdentity), hosts: hosts, what: "the far authority's world is relayed");
        var opening = relayed.Records[0];

        Assert.Equal(expected: WorldFederationResponse.Cut, actual: opening.Kind);
        Assert.Equal(expected: new WorldStreamStamp(Generation: (own.Generation + 1L), Ordinal: (own.Ordinal + 1L), Stream: lineage.Stream), actual: opening.Stamp);

        Walk(from: FederatedHosts.DestinationName, hosts: hosts, source: entry, target: onward, to: FederatedHosts.OnwardName);
        StepUntil(condition: () => first.IsCompleted, hosts: hosts, what: "the far authority hands the stream back");
        Assert.Equal(expected: WorldProjectionEnd.Moved, actual: (await first).End);
        var handed = relayed.Records.Length;
        var last = relayed.Records[^1].Stamp;

        Assert.Equal(expected: last.Generation, actual: lineage.Generation);

        // Asked again, as the door that keeps the stream does: the far authority relays the one the traveler walked on to.
        var second = arm.StreamProjectionAsync(ceiling: WorldDisclosureTier.Replica, ct: lifetime.Token, remainingHops: 8, session: session);

        StepUntil(condition: () => relayed.Shows(authority: onward.AuthorityIdentity), hosts: hosts, what: "the neighbour's world is relayed");
        var records = relayed.Records;

        Assert.Equal(expected: WorldFederationResponse.Cut, actual: records[handed].Kind);
        Assert.True(condition: (records[handed].Stamp.Generation > last.Generation), userMessage: "the next segment opens a later generation");
        Assert.DoesNotContain(collection: records, filter: static record => (record.Kind is WorldFederationResponse.ProjectionMoved or WorldFederationResponse.ProjectionInvalidated or WorldFederationResponse.Refusal));
        for (var index = 0; (index < records.Length); index++) {
            Assert.Equal(expected: lineage.Stream, actual: records[index].Stamp.Stream);
            if (index > 0) {
                Assert.True(condition: (records[index].Stamp.Ordinal > records[(index - 1)].Stamp.Ordinal), userMessage: $"record {index} is not ahead of the one before it");
                Assert.True(condition: (records[index].Stamp.Generation >= records[(index - 1)].Stamp.Generation), userMessage: $"record {index} is of an earlier generation than the one before it");
            }
        }

        await lifetime.CancelAsync();
        try { _ = await second; } catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
        try { await relayed.Reading; } catch (OperationCanceledException) { }
    }
    [Fact]
    public void ARemoteTravelerWalksToAnotherInstanceAndBackOnOneObservationAsOnePeer() {
        WorldRemoteAuthority? traveler = null;
        using var hosts = FederatedHosts.Build(travelerRouteStarted: wrapper => traveler = wrapper);
        var entry = hosts.DestinationRow.Server;
        var onward = hosts.OnwardRow.Server;

        Enter(hosts: hosts, slot: 0);
        Assert.NotNull(@object: traveler);

        var body = TravelerSlot(server: entry);
        var admitted = entry.ExecuteAuthorityOperation(operation: () => entry.Population.PeerPrincipal(index: body));
        var subject = GrantSubject.Body(index: body);
        bool Holds(WorldCapability capability) => entry.ExecuteAuthorityOperation(operation: () =>
            entry.Grants.Allows(capability: capability, principal: admitted, subject: subject).IsAllowed);

        Assert.True(condition: Holds(capability: WorldCapability.Drive));
        Assert.True(condition: Holds(capability: WorldCapability.Observe));

        var reader = new Reader();

        using var observing = traveler.AttachSink(sink: reader);

        StepUntil(condition: () => (reader.Shown == entry.AuthorityIdentity), hosts: hosts, what: "the reader holds the entry authority's world");
        var bound = reader.Span;

        Assert.NotEqual(expected: 0UL, actual: bound.Stream);
        Assert.Equal(expected: 0L, actual: bound.Generation);

        // The traveler walks to the entry authority's colocated neighbour: the stream cuts to that world's relay.
        Walk(from: FederatedHosts.DestinationName, hosts: hosts, source: entry, target: onward, to: FederatedHosts.OnwardName);
        StepUntil(condition: () => (reader.Shown == onward.AuthorityIdentity), hosts: hosts, what: "the reader holds the neighbour's world");
        var away = reader.Span;

        Assert.Equal(expected: bound.Stream, actual: away.Stream);
        Assert.True(condition: (away.Generation > bound.Generation), userMessage: "the handoff to the neighbour opens a generation of the same stream");
        Assert.False(condition: away.Closed);
        Assert.False(condition: Holds(capability: WorldCapability.Drive), userMessage: "a traveler that left holds no Drive over the body index it left");

        // And back: the stream cuts to the entry authority's own view again, and the traveler is the peer it was.
        Walk(from: FederatedHosts.OnwardName, hosts: hosts, source: onward, target: entry, to: FederatedHosts.DestinationName);
        StepUntil(condition: () => (reader.Shown == entry.AuthorityIdentity), hosts: hosts, what: "the reader holds the entry authority's world again");
        var back = reader.Span;

        Assert.Equal(expected: bound.Stream, actual: back.Stream);
        Assert.True(condition: (back.Generation > away.Generation), userMessage: "the handoff back opens a later generation of the same stream");
        Assert.False(condition: back.Closed);
        Assert.Equal(expected: 0, actual: reader.Closes);
        Assert.Equal(expected: 0, actual: reader.TerminalCuts);
        Assert.Equal(expected: 0L, actual: reader.Refusals);
        Assert.Equal(expected: 2, actual: reader.Cuts);
        Assert.Equal(
            expected: [entry.AuthorityIdentity, onward.AuthorityIdentity, entry.AuthorityIdentity],
            actual: reader.Authorities
        );
        Assert.Equal(expected: body, actual: TravelerSlot(server: entry));
        Assert.Equal(expected: admitted, actual: entry.ExecuteAuthorityOperation(operation: () => entry.Population.PeerPrincipal(index: body)));
        Assert.True(condition: entry.ExecuteAuthorityOperation(operation: () => entry.IsLivePeer(principal: admitted)));
        Assert.True(condition: Holds(capability: WorldCapability.Drive));
        Assert.True(condition: Holds(capability: WorldCapability.Observe));
    }
}
