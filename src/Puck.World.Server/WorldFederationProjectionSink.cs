using System.Threading.Channels;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Copies borrowed projection records into a bounded wire queue; no socket writes run on the authority tick.</summary>
internal sealed class WorldFederationProjectionSink(WorldDisclosureTier tier, string authority, Func<int> revision,
    Func<bool>? isCurrent = null, WorldPrincipal? recipient = null) : IClientSink {
    private readonly Channel<(WorldFederationResponse Kind, byte[] Body)> m_frames = Channel.CreateBounded<(WorldFederationResponse, byte[])>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private bool m_invalidated;

    private bool Current() {
        if (m_invalidated) { return false; }
        if (isCurrent?.Invoke() != false) { return true; }
        m_invalidated = true;
        m_frames.Writer.TryComplete();
        return false;
    }
    private void Write(WorldFederationResponse kind, byte[] body) {
        if (!m_frames.Writer.TryWrite((kind, body))) {
            m_frames.Writer.TryComplete(new IOException("federation observer exceeded its bounded projection backlog"));
        }
    }
    public void PrimeRoute(in WorldAuthorityRouteDescription route) => Write(WorldFederationResponse.Route,
        WorldFederationCodec.EncodeRoute(in route, tier, authority, revision(), recipient));
    public void DeliverAnswer(in QueryAnswer answer) { }
    public void DeliverComposition(WorldComposition composition) { }
    public void DeliverSessionLever(WorldSessionLever lever) { }
    public void DeliverDefinition(WorldDefinition definition) {
        if (Current()) { Write(WorldFederationResponse.Definition, WorldFederationCodec.EncodeDocument(definition, tier, authority, revision(), recipient)); }
    }
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        if (Current()) { Write(WorldFederationResponse.Snapshot, WorldFederationCodec.EncodeSnapshot(in snapshot)); }
    }

    public Task StreamAsync(Stream output, CancellationToken ct) =>
        WorldProjectionStream.RunAsync(output, token => PumpAsync(output, token), ct);
    private async Task PumpAsync(Stream output, CancellationToken ct) {
        await foreach (var item in m_frames.Reader.ReadAllAsync(ct).ConfigureAwait(false)) {
            await WorldFederationCodec.WriteResponseAsync(output, item.Kind, item.Body, ct).ConfigureAwait(false);
        }
        if (m_invalidated) {
            await WorldFederationCodec.WriteResponseAsync(output, WorldFederationResponse.ProjectionInvalidated, default, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Ends a one-way projection when either its producer ends or its consumer disconnects.</summary>
internal static class WorldProjectionStream {
    public static async Task RunAsync(Stream output, Func<CancellationToken, Task> produce, CancellationToken ct) {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = produce(lifetime.Token);
        // Projection is one-way. EOF or unexpected input terminates it even while the world is paused.
        var closed = output.ReadAsync(new byte[1], lifetime.Token).AsTask();
        await Task.WhenAny(pump, closed).ConfigureAwait(false);
        lifetime.Cancel();
        try { await Task.WhenAll(pump, closed).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}
