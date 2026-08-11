using System.Net;
using System.Net.Sockets;
using System.Text;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The remote implementation of the authority contract used by transfer and continuous projection.</summary>
internal sealed class WorldRemoteAuthority : IDisposable {
    private readonly IPEndPoint m_endpoint;
    private readonly CancellationTokenSource m_lifetime = new();
    private readonly Dictionary<int, (ulong TransferId, int Ordinal)> m_credentials = new();
    private readonly WorldFederatedServerLink m_link;
    private WorldDefinition m_definition;

    public WorldRemoteAuthority(string endpoint, WorldDefinition placeholder) {
        if (!IPEndPoint.TryParse(endpoint, out var parsed)) {
            throw new FormatException($"host.authority '{endpoint}' is not a parseable IP endpoint");
        }

        m_endpoint = parsed;
        m_definition = placeholder;
        Endpoint = endpoint;
        m_link = new WorldFederatedServerLink(authority: this);
    }

    public string Endpoint { get; }
    public WorldDefinition Definition => Volatile.Read(ref m_definition);
    public IServerLink Link => m_link;

    public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) {
        var frame = RoundTrip(kind: WorldFederationWireFormat.RequestKind.Reserve, body: WorldFederationWireFormat.EncodeReservation(request: request));
        if (frame.Kind != WorldFederationWireFormat.ResponseKind.Reservation) {
            return WorldTransferReservationReply.Refused(reason: DecodeRefusal(frame));
        }

        var reply = WorldFederationWireFormat.DecodeReservationReply(body: frame.Body);
        if (reply.Accepted) {
            for (var ordinal = 0; ordinal < reply.BodyIndices.Count; ordinal++) {
                m_credentials[reply.BodyIndices[ordinal]] = (request.TransferId, ordinal);
            }
        }
        return reply;
    }

    public bool Commit(ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason) {
        var frame = RoundTrip(kind: WorldFederationWireFormat.RequestKind.Commit, body: WorldFederationWireFormat.EncodeCommit(transferId: transferId, members: members));

        if (frame.Kind != WorldFederationWireFormat.ResponseKind.Commit) {
            reason = DecodeRefusal(frame);
            return false;
        }

        using var input = new MemoryStream(frame.Body, writable: false);
        using var reader = new BinaryReader(input);
        var accepted = reader.ReadBoolean();
        reason = reader.ReadString();
        return accepted;
    }

    public void Abort(ulong transferId) {
        var body = new byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(body, transferId);
        _ = RoundTrip(kind: WorldFederationWireFormat.RequestKind.Abort, body: body);
    }

    public IDisposable AttachSink(IClientSink sink) {
        ArgumentNullException.ThrowIfNull(sink);
        var lease = CancellationTokenSource.CreateLinkedTokenSource(m_lifetime.Token);
        _ = Task.Run(function: () => ObserveAsync(sink: sink, ct: lease.Token));
        return lease;
    }

    public void Dispose() => m_lifetime.Cancel();

    internal bool TryCredential(int bodyIndex, out ulong transferId, out int ordinal) {
        if ((bodyIndex < 0) && (m_credentials.Count > 0)) {
            var first = m_credentials.OrderBy(pair => pair.Key).First().Value;
            transferId = first.TransferId;
            ordinal = first.Ordinal;
            return true;
        }
        if (m_credentials.TryGetValue(key: bodyIndex, value: out var credential)) {
            transferId = credential.TransferId;
            ordinal = credential.Ordinal;
            return true;
        }
        transferId = 0; ordinal = -1; return false;
    }

    internal (WorldFederationWireFormat.ResponseKind Kind, byte[] Body) RoundTrip(WorldFederationWireFormat.RequestKind kind, byte[] body) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(m_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        client.NoDelay = true;
        client.ConnectAsync(remoteEP: m_endpoint, cancellationToken: timeout.Token).AsTask().GetAwaiter().GetResult();
        using var stream = client.GetStream();
        WorldFederationWireFormat.WriteHelloAsync(stream: stream, ct: timeout.Token).GetAwaiter().GetResult();
        WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: kind, body: body, ct: timeout.Token).GetAwaiter().GetResult();
        var frame = WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: timeout.Token).GetAwaiter().GetResult() ?? throw new IOException("remote authority closed without a verdict");
        return ((WorldFederationWireFormat.ResponseKind)frame.Kind, frame.Body);
    }

    private async Task ObserveAsync(IClientSink sink, CancellationToken ct) {
        try {
            using var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(remoteEP: m_endpoint, cancellationToken: ct).ConfigureAwait(false);
            using var stream = client.GetStream();
            await WorldFederationWireFormat.WriteHelloAsync(stream: stream, ct: ct).ConfigureAwait(false);
            await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.Observe, body: [], ct: ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested) {
                var frame = await WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: ct).ConfigureAwait(false);
                if (frame is null) {
                    return;
                }

                switch ((WorldFederationWireFormat.ResponseKind)frame.Value.Kind) {
                    case WorldFederationWireFormat.ResponseKind.Definition: {
                            var definition = WorldFederationWireFormat.DecodeDefinition(body: frame.Value.Body);
                            Volatile.Write(ref m_definition, definition);
                            sink.DeliverDefinition(definition: definition);
                            break;
                        }
                    case WorldFederationWireFormat.ResponseKind.Snapshot: {
                            var snapshot = WorldFederationWireFormat.DecodeSnapshot(body: frame.Value.Body);
                            sink.DeliverSnapshot(snapshot: in snapshot);
                            break;
                        }
                }
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[world.projection: remote observer '{Endpoint}' ended ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")})]");
        }
    }

    private static string DecodeRefusal((WorldFederationWireFormat.ResponseKind Kind, byte[] Body) frame) =>
        ((frame.Kind == WorldFederationWireFormat.ResponseKind.Refusal) ? Encoding.UTF8.GetString(frame.Body) : $"unexpected federation response {frame.Kind}");
}
