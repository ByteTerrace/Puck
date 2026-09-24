using Puck.Commands;
using System.Threading.Channels;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Samples borrowed authority snapshots at the live disclosure cadence, redacts only frames that are due,
/// and copies them into a bounded wire queue; no socket writes run on the authority tick.</summary>
internal sealed class WorldFederationProjectionSink(WorldDisclosureTier tier, string authority, Func<int> revision,
    Func<WorldSinkDisclosure> disclosure, Func<bool>? isCurrent = null, Principal? recipient = null) : IClientSink {
    private readonly Channel<(WorldFederationResponse Kind, byte[] Body)> m_frames = Channel.CreateBounded<(WorldFederationResponse, byte[])>(options: new BoundedChannelOptions(capacity: 8) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly WorldProjectionSampler m_sampler = new(updateSeconds: disclosure().Policy.UpdateSeconds);
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
    private void Write(WorldFederationResponse kind, byte[] body) {
        if (!m_frames.Writer.TryWrite(item: (kind, body))) {
            m_frames.Writer.TryComplete(error: new IOException(message: "federation observer exceeded its bounded projection backlog"));
        }
    }

    public void DeliverAnswer(in QueryAnswer answer) { }
    public void DeliverComposition(WorldComposition composition) { }
    public void DeliverDefinition(WorldDefinition definition) {
        if (Current()) {
            Write(
            WorldFederationResponse.Definition,
            WorldFederationCodec.EncodeDocument(
                definition,
                tier,
                authority,
                revision(),
                recipient
            )
        );
        }
    }
    public void DeliverSessionLever(WorldSessionLever lever) { }
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        if (!Current()) {
            return;
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
    // The wire carries one definition-frame kind; a value-only delivery rides the same encode until the wire
    // grammar grows its own state/definition split.
    public void DeliverState(WorldDefinition definition, in WorldStateStamp stamp) => DeliverDefinition(definition: definition);
    public void PrimeRoute(in WorldAuthorityRouteDescription route) => Write(
        WorldFederationResponse.Route,
        WorldFederationCodec.EncodeRoute(
            in route,
            tier,
            authority,
            revision()
        )
    );
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
