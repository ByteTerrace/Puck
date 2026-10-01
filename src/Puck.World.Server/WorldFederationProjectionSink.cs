using Puck.Commands;
using System.Threading.Channels;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Samples borrowed authority snapshots at the live disclosure cadence, redacts only frames that are due,
/// and copies them into a bounded wire queue; no socket writes run on the authority tick. A presentation-tier peer is
/// fed by its own <see cref="WorldProjectionFeed"/>: its whole projection first, then projection deltas of the members
/// that change, its state clocks' anchors among them, checked at every authoritative tick whether or not the tick's
/// frame is sampled; the feed is released when the stream ends.</summary>
internal sealed class WorldFederationProjectionSink(WorldServer server, WorldDisclosureTier tier, Func<WorldSinkDisclosure> disclosure, Func<bool>? isCurrent = null, Principal? recipient = null) : IClientSink {
    private readonly Channel<(WorldFederationResponse Kind, byte[] Body)> m_frames = Channel.CreateBounded<(WorldFederationResponse, byte[])>(options: new BoundedChannelOptions(capacity: 8) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly WorldProjectionSampler m_sampler = new(updateSeconds: disclosure().Policy.UpdateSeconds);
    private readonly WorldProjectionFeed? m_feed = ((tier == WorldDisclosureTier.Presentation)
        ? new WorldProjectionFeed(
            recipient: recipient,
            seeds: server.ClockSeeds
        )
        : null);
    private EntitySnapshot[] m_redacted = [];

    private bool m_invalidated;

    private bool Current() {
        if (m_invalidated) { return false; }
        if (isCurrent?.Invoke() != false) { return true; }
        m_invalidated = true;
        m_frames.Writer.TryComplete();
        return false;
    }
    private async Task PumpAsync(Stream output, CancellationToken ct) {
        await foreach (var item in m_frames.Reader.ReadAllAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false)) {
            await WorldFederationCodec.WriteResponseAsync(
                body: item.Body,
                ct: ct,
                kind: item.Kind,
                stream: output
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        if (m_invalidated) {
            await WorldFederationCodec.WriteResponseAsync(
                body: default,
                ct: ct,
                kind: WorldFederationResponse.ProjectionInvalidated,
                stream: output
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    // Queues what the peer's feed owes it: a whole projection as a definition leaf, a delta as a projection delta leaf.
    private void Present(WorldProjectionDelivery delivery, WorldDocumentVersion version, ulong tick, ulong engineTick) {
        switch (delivery.Kind) {
            case WorldProjectionDeliveryKind.Document:
                Write(
                    WorldFederationResponse.Definition,
                    WorldFederationCodec.DocumentLeaf(
                        payload: delivery.Payload,
                        tier: tier,
                        version: version
                    )
                );
                break;
            case WorldProjectionDeliveryKind.Delta:
                Write(
                    WorldFederationResponse.ProjectionDelta,
                    WorldFederationCodec.EncodeProjectionDelta(
                        delta: delivery.Payload,
                        engineTick: engineTick,
                        tick: tick,
                        version: version
                    )
                );
                break;
        }
    }
    private void Project(WorldDefinition definition, WorldDocumentVersion version) {
        var time = server.DeliveryTime;

        Present(
            delivery: m_feed!.Compose(
                arena: server.Arena,
                authority: server.AuthorityIdentity,
                definition: definition,
                revision: server.Population.Revision,
                time: in time
            ),
            engineTick: time.EngineTick,
            tick: time.Tick,
            version: version
        );
    }
    private void Write(WorldFederationResponse kind, byte[] body) {
        if (!m_frames.Writer.TryWrite(item: (kind, body))) {
            m_frames.Writer.TryComplete(error: new IOException(message: "federation observer exceeded its bounded projection backlog"));
        }
    }

    public void DeliverAnswer(in QueryAnswer answer) { }
    public void DeliverComposition(WorldComposition composition) { }
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) {
        if (!Current()) {
            return;
        }

        if (m_feed is not null) {
            Project(
                definition: definition,
                version: version
            );

            return;
        }

        var time = server.DeliveryTime;

        Write(
            WorldFederationResponse.Definition,
            WorldFederationCodec.EncodeDocument(
                authority: server.AuthorityIdentity,
                definition: definition,
                recipient: recipient,
                revision: server.Population.Revision,
                tier: tier,
                time: in time,
                version: version
            )
        );
    }
    public void DeliverSessionLever(WorldSessionLever lever) { }
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        if (!Current()) {
            return;
        }
        // Every authoritative tick checks the peer's anchors, sampled or not: a prediction that misses is owed now.
        if (m_feed is not null) {
            Present(
                delivery: m_feed.Step(
                    definition: server.Definition,
                    engineTick: snapshot.EngineTick,
                    tick: snapshot.Tick
                ),
                engineTick: snapshot.EngineTick,
                tick: snapshot.Tick,
                version: server.DocumentVersion
            );
        }
        var currentDisclosure = disclosure();

        m_sampler.SetUpdateSeconds(updateSeconds: currentDisclosure.Policy.UpdateSeconds);
        if (!m_sampler.TryProject(
            projected: out var projected,
            snapshot: in snapshot
        )) {
            return;
        }
        if (!currentDisclosure.IsFull) {
            projected = WorldOutputHub.Redact(
                disclosure: in currentDisclosure,
                scratch: ref m_redacted,
                snapshot: in projected
            );
        }
        Write(
            WorldFederationResponse.Snapshot,
            WorldFederationCodec.EncodeSnapshot(snapshot: in projected)
        );
    }
    // The replica wire carries one definition-frame kind, so a value-only delivery rides the same encode; a
    // presentation-tier peer is owed only the members of its projection that the values moved.
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) => DeliverDefinition(
        definition: definition,
        version: version
    );
    public void PrimeRoute(in WorldAuthorityRouteDescription route) {
        var time = server.DeliveryTime;

        Write(
            WorldFederationResponse.Route,
            WorldFederationCodec.EncodeRoute(
                authority: server.AuthorityIdentity,
                revision: server.Population.Revision,
                route: in route,
                tier: tier,
                time: in time
            )
        );
    }
    /// <summary>Releases what the peer's feed holds, its anchor rows included: the peer is gone. Called under the
    /// authority gate, once the sink is detached, so no delivery follows it.</summary>
    public void Release() => m_feed?.Release();
    public Task StreamAsync(Stream output, CancellationToken ct) =>
        WorldProjectionStream.RunAsync(
            output,
            token => PumpAsync(
                ct: token,
                output: output
            ),
            ct
        );
}
/// <summary>Ends a one-way projection when either its producer ends or its consumer disconnects.</summary>
internal static class WorldProjectionStream {
    public static async Task RunAsync(Stream output, Func<CancellationToken, Task> produce, CancellationToken ct) {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: ct);
        var pump = produce(lifetime.Token);
        // Projection is one-way. EOF or unexpected input terminates it even while the world is paused.
        var closed = output.ReadAsync(
            buffer: new byte[1],
            cancellationToken: lifetime.Token
        ).AsTask();

        await Task.WhenAny(
            task1: pump,
            task2: closed
        ).ConfigureAwait(continueOnCapturedContext: false);
        lifetime.Cancel();
        try {
            await Task.WhenAll(
            pump,
            closed
        ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}
