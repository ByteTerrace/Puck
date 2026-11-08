using Puck.Commands;
using System.Text;
using System.Threading.Channels;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Samples borrowed authority snapshots at the live disclosure cadence, redacts only frames that are due,
/// and copies them into a bounded wire queue; no socket writes run on the authority tick. A presentation-tier peer is
/// fed by its own <see cref="WorldProjectionFeed"/> over the authority <paramref name="server"/> names: its whole
/// projection first, then projection deltas of the members that change, its state clocks' anchors among them, checked
/// at every authoritative tick whether or not the tick's frame is sampled; <see cref="Release"/> lets the feed go when
/// the stream ends.</summary>
/// <param name="tier">The tier the admission door decided for the peer.</param>
/// <param name="authority">The composing authority's addressable namespace.</param>
/// <param name="disclosure">Reads the live snapshot disclosure.</param>
/// <param name="isCurrent">Reads whether the projection's authority route still holds, or <see langword="null"/> for
/// one that always does.</param>
/// <param name="recipient">The authenticated recipient, or <see langword="null"/> for the public observer.</param>
/// <param name="server">The authority whose store, clock and document a presentation-tier feed reads; required at
/// <see cref="WorldDisclosureTier.Presentation"/>.</param>
/// <param name="view">An ingress-minted view with live admission, or null to mint it from the server and tier.</param>
public sealed class WorldFederationProjectionSink(WorldDisclosureTier tier, string authority,
    Func<WorldSinkDisclosure> disclosure, Func<bool>? isCurrent = null, Principal? recipient = null, WorldServer? server = null,
    WorldRecipientView? view = null) : IWorldDetachableSink {
    /// <summary>The maximum encoded records one projection subscriber retains while its wire consumer is behind.</summary>
    public const int PendingDeliveryLimit = 8;
    /// <summary>The named reason for detaching an observer whose wire queue fills.</summary>
    public const string BackpressureDetachReason = "world.observation.backpressure";
    /// <summary>The named reason for detaching a projection whose authority route is no longer current.</summary>
    public const string InvalidatedDetachReason = "world.observation.invalidated";
    /// <summary>The named reason for detaching a projection whose dependencies are no longer disclosed.</summary>
    public const string DisclosureDetachReason = "world.observation.disclosure";

    private readonly Channel<(WorldFederationResponse Kind, byte[] Body)> m_frames = Channel.CreateBounded<(WorldFederationResponse, byte[])>(options: new BoundedChannelOptions(capacity: PendingDeliveryLimit) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly WorldProjectionSampler m_sampler = new(updateSeconds: disclosure().Policy.UpdateSeconds);
    private readonly WorldProjectionFeed? m_feed = ((tier == WorldDisclosureTier.Presentation)
        ? new WorldProjectionFeed(
            recipient: recipient,
            seeds: (server ?? throw new ArgumentNullException(paramName: nameof(server), message: "A presentation-tier projection reads its authority's store, clock and document.")).ClockSeeds
        )
        : null);
    private EntitySnapshot[] m_redacted = [];
    private readonly WorldDeliveryCursor m_cursor = new();
    private readonly WorldDeliveryRows m_rows = new();
    private readonly WorldRecipientView? m_view = view ?? server?.DeliveryView(recipient, tier);
    private WorldDocumentVersion Version => (server?.DocumentVersion ?? default) with { Sequence = m_cursor.Sequence };

    private bool m_invalidated;

    /// <summary>The number of encoded records currently retained for this subscriber.</summary>
    public int PendingDeliveries => m_frames.Reader.Count;
    internal bool HoldsProjection => m_view is null || (m_feed?.Holds(m_view) ?? m_cursor.Holds(m_view));
    /// <inheritdoc/>
    public string? DetachReason { get; private set; }

    private bool Current() {
        if (m_invalidated) { return false; }
        if (isCurrent?.Invoke() != false) { return true; }
        End(reason: InvalidatedDetachReason);

        return false;
    }
    private void End(string reason) {
        DetachReason = reason;
        m_invalidated = true;
        Release();
        m_frames.Writer.TryComplete();
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
                body: ((DetachReason is { } reason)
                    ? Encoding.UTF8.GetBytes(s: reason)
                    : ReadOnlyMemory<byte>.Empty),
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
    private void Write(WorldFederationResponse kind, byte[] body) {
        if (m_invalidated) {
            return;
        }
        if (!m_frames.Writer.TryWrite(item: (kind, body))) {
            // The stream's primer, definition revisions, and authority route cannot be reconstructed from a lone
            // latest snapshot. Detach so the peer reopens with a fresh primer instead of accepting an ambiguous gap.
            // Completing the channel successfully keeps the queued records ahead of the terminal reason; the hub
            // detaches on the reason after this delivery, whether or not the wire consumer ever drains again.
            End(reason: BackpressureDetachReason);
        }
    }

    public void DeliverAnswer(in QueryAnswer answer) { }
    public void DeliverComposition(WorldComposition composition) { }
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) {
        if (!Current()) {
            return;
        }
        if (m_view is null) { throw new InvalidOperationException("A document delivery requires its authority view."); }
        server!.AttachCursor(recipient, m_cursor);

        if (m_feed is not null) {
            var time = server!.DeliveryTime;

            // Only a disclosure refusal ends the stream by name; any other failure to compose is a fault, which the
            // hub detaches the sink for.
            try {
                Present(
                    delivery: m_feed.Compose(m_view, m_cursor),
                    engineTick: time.EngineTick,
                    tick: time.Tick,
                    version: Version
                );
            } catch (WorldDisclosureException) {
                End(reason: DisclosureDetachReason);
            }

            return;
        }

        if (m_cursor.DeliverDefinition(m_view) is { } delivered) {
            Write(WorldFederationResponse.Definition, WorldFederationCodec.DocumentLeaf(
                WorldDefinitionSerialization.SerializeCompact(delivered), tier, Version));
        }
    }
    public void DeliverSessionLever(WorldSessionLever lever) { }

    private WorldInputPublication[] m_inputDisclosure = [];

    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        if (!Current()) {
            return;
        }
        // Every authoritative tick checks the peer's anchors, sampled or not: a prediction that misses is owed now.
        if (m_feed is not null) {
            Present(
                delivery: m_feed.Step(m_view!, m_cursor, snapshot.Tick, snapshot.EngineTick),
                engineTick: snapshot.EngineTick,
                tick: snapshot.Tick,
                version: Version
            );

            if (m_invalidated) {
                return;
            }
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
        if (m_view is not null) { projected = projected with { FieldCells = m_rows.Advance(m_view, m_cursor, projected.FieldCells, (tier == WorldDisclosureTier.Replica)) }; } else if (!projected.FieldCells.IsEmpty) { throw new InvalidOperationException(message: "A state change delivery requires its authority view."); }
        projected = projected with { Revision = checked((int)m_cursor.Sequence) };
        projected = projected with { Inputs = ((recipient is { } inputOwner) ? WorldInputPublication.ForOwner(projected.Inputs, inputOwner, ref m_inputDisclosure) : default) };
        Write(
            WorldFederationResponse.Snapshot,
            WorldFederationCodec.EncodeSnapshot(snapshot: in projected)
        );
    }
    // The replica wire carries one definition-frame kind, so a value-only delivery rides the same encode; a
    // presentation-tier peer is owed only the members of its projection that the values moved.
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) {
        if ((m_feed is not null) || !stamp.ArenaValues || ((m_view is not null) && m_cursor.FrameMoved(m_view))) { DeliverDefinition(definition: definition, version: version); }
    }
    public void PrimeRoute(in WorldAuthorityRouteDescription route) => Write(
        WorldFederationResponse.Route,
        WorldFederationCodec.EncodeRoute(
            authority: authority,
            revision: checked((int)m_cursor.Sequence),
            route: in route,
            tier: tier,
            time: (server?.DeliveryTime ?? ArenaTime.At(engineTick: 0UL, tick: 0UL))
        )
    );
    /// <summary>Releases what the peer's feed holds, its anchor rows included: the peer is gone. Called under the
    /// authority gate once the sink is detached, so no delivery follows it.</summary>
    public void Release() {
        m_feed?.Release(); m_cursor.Release(); server?.DetachCursor(recipient, m_cursor);
    }
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
