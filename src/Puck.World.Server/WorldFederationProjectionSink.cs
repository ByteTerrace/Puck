using Puck.Commands;
using Puck.Networking;
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
    WorldRecipientView? view = null) : IWorldDetachableSink, IWorldInterestSink {
    /// <summary>The maximum encoded records one projection subscriber retains while its wire consumer is behind.</summary>
    public const int PendingDeliveryLimit = 8;
    /// <summary>The named reason for detaching an observer whose wire queue fills.</summary>
    public const string BackpressureDetachReason = "world.observation.backpressure";
    /// <summary>The named reason for detaching a projection whose authority route is no longer current.</summary>
    public const string InvalidatedDetachReason = "world.observation.invalidated";
    /// <summary>The named reason for detaching a projection whose dependencies are no longer disclosed.</summary>
    public const string DisclosureDetachReason = "world.observation.disclosure";

    private readonly Channel<Record> m_frames = Channel.CreateBounded<Record>(options: new BoundedChannelOptions(capacity: PendingDeliveryLimit) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly WorldProjectionSampler m_sampler = new(updateSeconds: disclosure().Policy.UpdateSeconds);
    private readonly WorldProjectionFeed? m_feed = ((tier == WorldDisclosureTier.Presentation)
        ? new WorldProjectionFeed(
            recipient: recipient,
            seeds: (server ?? throw new ArgumentNullException(paramName: nameof(server), message: "A presentation-tier projection reads its authority's store, clock and document.")).ClockSeeds
        )
        : null);
    private readonly WorldBodyHistory m_history = new();

    private readonly record struct Record(WorldFederationResponse Kind, ReadOnlyMemory<byte> Head,
        ReadOnlyMemory<byte> Bodies = default, ReadOnlyMemory<byte> Tail = default) {
        public async Task WriteAsync(Stream output, CancellationToken ct) {
            var prefix = new byte[FrameCodec.PrefixBytes + Head.Length];
            FrameCodec.WritePrefix(prefix, (byte)Kind, checked(Head.Length + Bodies.Length + Tail.Length));
            Head.CopyTo(prefix.AsMemory(FrameCodec.PrefixBytes));
            await output.WriteAsync(prefix, ct).ConfigureAwait(false);
            if (!Bodies.IsEmpty) { await output.WriteAsync(Bodies, ct).ConfigureAwait(false); }
            if (!Tail.IsEmpty) { await output.WriteAsync(Tail, ct).ConfigureAwait(false); }
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
    }
    private readonly WorldDeliveryCursor m_cursor = new();
    private readonly WorldDeliveryRows m_rows = new();
    private readonly WorldSolidScaleDisclosure m_scales = new();
    private readonly WorldRecipientView? m_view = (view ?? server?.DeliveryView(recipient, tier));

    private WorldDocumentVersion Version => (server?.DocumentVersion ?? default) with { Sequence = m_cursor.Sequence };

    private bool m_invalidated;

    /// <summary>The number of encoded records currently retained for this subscriber.</summary>
    public int PendingDeliveries => m_frames.Reader.Count;

    internal bool HoldsProjection => ((m_view is null) || (m_feed?.Holds(view: m_view) ?? m_cursor.Holds(view: m_view)));

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
            await item.WriteAsync(output, ct).ConfigureAwait(false);
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
                WriteRecord(
                    WorldFederationResponse.Definition,
                    WorldFederationCodec.DocumentLeaf(
                        payload: delivery.Payload,
                        tier: tier,
                        version: version
                    )
                );
                break;
            case WorldProjectionDeliveryKind.Delta:
                WriteRecord(
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
    // Queues one observation record behind the stream stamp it takes, so the peer's ingress orders it.
    private void WriteRecord(WorldFederationResponse kind, byte[] body) {
        if (m_invalidated) {
            return;
        }
        Write(kind, WorldFederationCodec.Stamped(body: body, stream: m_cursor.Stamp()));
    }
    // Reads the live body policy and records it on the cursor, which decides a cut when the policy turns redacting.
    private WorldSinkDisclosure ObserveBodies() {
        var bodies = disclosure();
        m_cursor.ObserveBodies(bodies.Policy);
        return bodies;
    }
    // Queues the record a cut the cursor decided owes, once: the new generation, with no content. True when one was
    // queued, after which the stream owes its whole disclosed document and a primer before anything newer.
    private bool AnnounceCut() {
        if (!m_cursor.TakeCut()) {
            return false;
        }
        m_history.Clear();
        Write(WorldFederationResponse.Cut, WorldFederationCodec.EncodeCut(stream: m_cursor.Stamp(), terminal: false));

        return true;
    }
    // After a cut, the whole disclosed document under the new generation; the next snapshot this stream sends
    // completes it as the primer.
    private void PrimeAfterCut() {
        if (m_view is null) {
            return;
        }
        if (m_feed is not null) {
            var time = server!.DeliveryTime;

            m_feed.Release();
            Present(
                delivery: m_feed.Compose(cursor: m_cursor, view: m_view),
                engineTick: time.EngineTick,
                tick: time.Tick,
                version: Version
            );

            return;
        }
        m_cursor.ForgetDocument();
        if (m_cursor.DeliverDefinition(view: m_view) is { } whole) {
            WriteRecord(WorldFederationResponse.Definition, WorldFederationCodec.DocumentLeaf(
                payload: WorldDefinitionSerialization.SerializeCompact(definition: whole), tier: tier, version: Version));
        }
    }
    private void Write(WorldFederationResponse kind, byte[] body) => Write(new Record(kind, body));

    private void Write(Record record) {
        if (m_invalidated) {
            return;
        }
        if (!m_frames.Writer.TryWrite(record)) {
            // The stream's primer, definition revisions, and authority route cannot be reconstructed from a lone
            // latest snapshot. Detach so the peer reopens with a fresh primer instead of accepting an ambiguous gap.
            // Completing the channel successfully keeps the queued records ahead of the terminal reason; the hub
            // detaches on the reason after this delivery, whether or not the wire consumer ever drains again.
            End(reason: BackpressureDetachReason);
        }
    }

    public void DeliverAnswer(in QueryAnswer answer) { }
    public void DeliverComposition(WorldComposition composition) { }
    public void DeliverCut(in WorldStreamStamp stream, bool terminal) {
        // The hub's fan-out has one span that never ends; this stream decides its own cuts.
    }
    public void CloseSpan(ulong stream) { }
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) => DeliverDocument(replaced: true);
    // Queues what a document owes the peer: replaced is whether it came through the whole-document door (a shape change
    // or a jump) rather than a state delivery whose frame moved.
    private void DeliverDocument(bool replaced, WorldPresentationRows? rows = null) {
        if (!Current()) {
            return;
        }
        if (m_view is null) { throw new InvalidOperationException(message: "A document delivery requires its authority view."); }
        server!.AttachCursor(cursor: m_cursor, recipient: recipient);
        // An install can change the body policy while no snapshot follows (a paused world): it is observed here too, so
        // a policy that turns redacting cuts with the document that carries it.
        _ = ObserveBodies();

        if (m_feed is not null) {
            var time = server!.DeliveryTime;
            var cut = false;

            // Only a disclosure refusal ends the stream by name; any other failure to compose is a fault, which the
            // hub detaches the sink for.
            try {
                var delivery = rows is null ? m_feed.Compose(m_view, m_cursor) : m_feed.ComposeAppearance(m_view, m_cursor, rows);

                if (AnnounceCut()) {
                    cut = true;
                    m_feed.Release();
                    delivery = m_feed.Compose(cursor: m_cursor, view: m_view);
                }
                Present(
                    delivery: delivery,
                    engineTick: time.EngineTick,
                    tick: time.Tick,
                    version: Version
                );
            } catch (WorldDisclosureException) {
                End(reason: DisclosureDetachReason);
            }
            if (cut) { PrimeSnapshot(); }

            return;
        }

        if (m_cursor.DeliverDefinition(view: m_view) is { } delivered) {
            var cut = AnnounceCut();

            WriteRecord(WorldFederationResponse.Definition, WorldFederationCodec.DocumentLeaf(
                payload: WorldDefinitionSerialization.SerializeCompact(definition: delivered), tier: tier, version: Version));
            // The same decision a session sink completes its replica by. The running tick's snapshot reaches the peer
            // whatever its cadence while the cursor owes rows (DeliverSnapshot), so it counts as following.
            var completion = m_cursor.Completion(cut: cut, replaced: replaced, snapshotFollows: server!.SnapshotFollows);
            if (completion != WorldReplicaCompletion.None) { PrimeSnapshot(cells: (completion == WorldReplicaCompletion.Whole)); }
        } else if (AnnounceCut()) {
            PrimeAfterCut();
            PrimeSnapshot();
        }
    }
    // Completes a document delivery with the primer snapshot it owes, outside the cadence: the authority's current table
    // under the live body policy, so the peer is never left on a cut with no bodies to show, or on a replaced arena
    // without its restricted rows' headers, while no tick follows. Without cells it carries the body table alone, whose
    // rows carry those headers.
    private void PrimeSnapshot(bool cells = true) {
        if (m_invalidated || (server is null)) {
            return;
        }
        var primer = server.Document.BuildPrimerSnapshot(cells: cells);

        var frame = WorldInterestFrame.ForPrimer(primer);
        WriteSnapshot(primer with { Entries = frame.Project(disclosure()), Cues = default, MusicBoundary = 0, MusicSenses = 0 }, frame);
    }
    public void DeliverSessionLever(WorldSessionLever lever) { }

    public void DeliverPresentation(WorldDefinition definition, WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) {
        if (!Current()) { return; }
        // A body policy that moved with the edit owes a cut, which the row path refuses so the document door primes it.
        _ = ObserveBodies();
        if (tier != WorldDisclosureTier.Replica || m_view is null || !m_cursor.TryDeliverPresentation(m_view, rows, out var moved)) {
            DeliverDocument(replaced: true, rows: rows);
        } else if (moved) {
            WriteRecord(WorldFederationResponse.PresentationRows, WorldFederationCodec.DocumentLeaf(rows.Payload.Span, tier, Version));
            WorldOutputHub.CountPresentationRows(rows);
        }
    }

    private WorldInputPublication[] m_inputDisclosure = [];
    private WorldInputAnswer[] m_answerDisclosure = [];

    public void DeliverSnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream) {
        DeliverSnapshot(snapshot, WorldInterestFrame.ForPrimer(snapshot));
    }
    void IWorldInterestSink.DeliverSnapshot(in WorldSnapshot snapshot, WorldInterestFrame frame) => DeliverSnapshot(snapshot, frame);

    private void DeliverSnapshot(in WorldSnapshot snapshot, WorldInterestFrame frame) {
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
        // The body policy is observed at every authoritative tick, sampled or not, so a policy that turns redacting cuts
        // at once rather than at the cadence's next sample. A tick is sampled whatever the cadence while a cut, or the
        // restricted rows' headers a replaced document could not carry, are owed.
        var currentDisclosure = ObserveBodies();
        var bodies = new WorldBodyView(frame, currentDisclosure);
        var atCadence = m_view is null ? snapshot : server!.PresentCues(snapshot, m_view, currentDisclosure.IsFull, bodies);
        m_sampler.SetUpdateSeconds(currentDisclosure.Policy.UpdateSeconds);
        if (m_sampler.TryProject(atCadence, out var projected, (m_cursor.CutOwed || m_cursor.OwesRows), bodies)) {
            WriteSnapshot(projected, frame);
        }
    }
    // Writes one sampled or primer snapshot as the peer may see it: redacted under the live body policy, its cells and
    // sizes as its view discloses them. A cut the rows or the body policy decided primes the peer alone first, and this
    // snapshot completes it, its rows advanced again against the generation's whole document so that every observation
    // the document owes the peer rides it.
    private void WriteSnapshot(WorldSnapshot projected, WorldInterestFrame frame) {
        var cells = projected.FieldCells;
        var replica = (tier == WorldDisclosureTier.Replica);

        if (m_view is not null) { projected = projected with { FieldCells = m_rows.Advance(m_view, m_cursor, cells, replica) }; } else if (!cells.IsEmpty) { throw new InvalidOperationException(message: "A state change delivery requires its authority view."); }
        projected = projected with { SolidScales = ((m_view is null) ? default : m_scales.Filter(m_view, projected.SolidScales)) };
        projected = projected with {
            Inputs = ((recipient is { } inputOwner) ? WorldOwnedRecords.ForOwner(projected.Inputs, inputOwner, ref m_inputDisclosure) : default),
            InputAnswers = ((recipient is { } answerOwner) ? WorldOwnedRecords.ForOwner(projected.InputAnswers, answerOwner, ref m_answerDisclosure) : default),
        };
        if (AnnounceCut()) {
            PrimeAfterCut();
            if (m_view is not null) { projected = projected with { FieldCells = m_rows.Advance(m_view, m_cursor, cells, replica) }; }
        }
        projected = projected with { Revision = checked((int)m_cursor.Sequence) };
        var stamp = m_cursor.Stamp();
        var (ordinal, baseline) = m_history.Baseline(stamp);
        var image = frame.Capture(projected.Entries);
        var changes = frame.Changes(image, baseline);
        var head = new WireWriter();
        head.WriteBytes(WorldFederationCodec.Stamped(stamp, []));
        if (baseline is not null) {
            head.WriteInt64(ordinal);
            head.WriteInt32(changes.Removed.Length);
            foreach (var slot in changes.Removed) { head.WriteInt32(slot); }
        }
        WorldFederationCodec.WriteSnapshotHeader(head, projected, changes.Count);
        var tail = new WireWriter();
        WorldFederationCodec.WriteSnapshotTail(tail, projected);
        m_history.Remember(stamp, image);
        Write(new Record(baseline is null ? WorldFederationResponse.Snapshot : WorldFederationResponse.SnapshotDelta,
            head.WrittenMemory, changes.Bytes, tail.WrittenMemory));
    }
    // The replica wire carries one definition-frame kind, so a value-only delivery rides the same encode; a
    // presentation-tier peer is owed only the members of its projection that the values moved.
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) {
        if ((m_feed is not null) || !stamp.ArenaValues || ((m_view is not null) && m_cursor.FrameMoved(view: m_view))) { DeliverDocument(replaced: false); }
    }
    public void PrimeRoute(in WorldAuthorityRouteDescription route) => WriteRecord(
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
        m_history.Clear();
        m_feed?.Release(); m_cursor.Release(); server?.DetachCursor(cursor: m_cursor, recipient: recipient);
    }
    public Task StreamAsync(Stream output, CancellationToken ct) =>
        WorldProjectionStream.RunAsync(
            output,
            token => PumpAsync(
                ct: token,
                output: output
            ),
            (stamp, _, _) => { m_history.Acknowledge(stamp); return ValueTask.CompletedTask; },
            ct
        );
}

/// <summary>Pumps projection records and receives their acknowledgements until either half closes.</summary>
internal static class WorldProjectionStream {
    public static async Task RunAsync(Stream output, Func<CancellationToken, Task> produce,
        Func<WorldStreamStamp, ReadOnlyMemory<byte>, CancellationToken, ValueTask> acknowledge, CancellationToken ct) {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: ct);
        var pump = produce(lifetime.Token);
        var closed = ReadAcknowledgements();
        async Task ReadAcknowledgements() {
            var bytes = new byte[WorldFederationCodec.StreamStampBytes];
            while (!lifetime.IsCancellationRequested) {
                try { await output.ReadExactlyAsync(bytes, lifetime.Token).ConfigureAwait(false); }
                catch (EndOfStreamException) { return; }
                if (!WorldFederationCodec.TryReadStamped(bytes, out var stamp, out _, out _)) { return; }
                await acknowledge(stamp, bytes, lifetime.Token).ConfigureAwait(false);
            }
        }

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
