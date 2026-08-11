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
    private readonly Dictionary<int, (string SourceAuthority, ulong TransferId, int Ordinal)> m_credentials = new();
    private readonly WorldFederatedServerLink m_link;
    private readonly WorldFederationSecurity m_security;
    private readonly string m_observerAuthority;
    private WorldDefinition m_definition;

    public WorldRemoteAuthority(string endpoint, WorldDefinition placeholder, WorldFederationSecurity security, string observerAuthority) {
        if (!IPEndPoint.TryParse(endpoint, out var parsed)) {
            throw new FormatException($"host.authority '{endpoint}' is not a parseable IP endpoint");
        }

        m_endpoint = parsed;
        m_definition = placeholder;
        m_security = security ?? throw new ArgumentNullException(paramName: nameof(security));
        m_observerAuthority = observerAuthority;
        Endpoint = endpoint;
        m_link = new WorldFederatedServerLink(authority: this);
    }

    public string Endpoint { get; }
    public WorldDefinition Definition => Volatile.Read(ref m_definition);
    public IServerLink Link => m_link;

    public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) {
        var frame = RoundTrip(sourceAuthority: request.SourceAuthority, kind: WorldFederationWireFormat.RequestKind.Reserve, body: WorldFederationWireFormat.EncodeReservation(request: request));
        if (frame.Kind != WorldFederationWireFormat.ResponseKind.Reservation) {
            return WorldTransferReservationReply.Refused(reason: DecodeRefusal(frame));
        }

        var reply = WorldFederationWireFormat.DecodeReservationReply(body: frame.Body);
        if (reply.Accepted) {
            if ((reply.BodyIndices.Count != request.Members.Count) || (reply.DestinationDefinition is null)) {
                try {
                    Abort(sourceAuthority: request.SourceAuthority, transferId: request.TransferId);
                } catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) {
                    // The malformed acceptance is already a terminal refusal locally; a failed best-effort abort
                    // expires under the destination lease rather than being treated as a valid binding.
                }

                return WorldTransferReservationReply.Refused(reason: "remote authority returned a malformed accepted reservation (body count or destination definition missing)");
            }

            for (var ordinal = 0; ordinal < reply.BodyIndices.Count; ordinal++) {
                m_credentials[reply.BodyIndices[ordinal]] = (request.SourceAuthority, request.TransferId, ordinal);
            }
        }
        return reply;
    }

    public bool Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason) {
        var frame = RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.Commit, body: WorldFederationWireFormat.EncodeCommit(sourceAuthority: sourceAuthority, transferId: transferId, members: members));

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

    public void Abort(string sourceAuthority, ulong transferId) {
        _ = RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.Abort, body: WorldFederationWireFormat.EncodeTransferKey(sourceAuthority: sourceAuthority, transferId: transferId));
    }

    public WorldTransferStatus Status(string sourceAuthority, ulong transferId) {
        var frame = RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.Status, body: WorldFederationWireFormat.EncodeTransferKey(sourceAuthority: sourceAuthority, transferId: transferId));

        if ((frame.Kind != WorldFederationWireFormat.ResponseKind.Status) || (frame.Body.Length != 1) || !Enum.IsDefined(value: (WorldTransferStatus)frame.Body[0])) {
            throw new IOException(DecodeRefusal(frame));
        }

        return (WorldTransferStatus)frame.Body[0];
    }

    public IDisposable AttachSink(IClientSink sink) {
        ArgumentNullException.ThrowIfNull(sink);
        var lease = CancellationTokenSource.CreateLinkedTokenSource(m_lifetime.Token);
        _ = Task.Run(function: () => ObserveAsync(sink: sink, ct: lease.Token));
        return lease;
    }

    public void Dispose() => m_lifetime.Cancel();

    internal bool TryCredential(int bodyIndex, out string sourceAuthority, out ulong transferId, out int ordinal) {
        if ((bodyIndex < 0) && (m_credentials.Count > 0)) {
            var first = m_credentials.OrderBy(pair => pair.Key).First().Value;
            sourceAuthority = first.SourceAuthority;
            transferId = first.TransferId;
            ordinal = first.Ordinal;
            return true;
        }
        if (m_credentials.TryGetValue(key: bodyIndex, value: out var credential)) {
            sourceAuthority = credential.SourceAuthority;
            transferId = credential.TransferId;
            ordinal = credential.Ordinal;
            return true;
        }
        sourceAuthority = string.Empty; transferId = 0; ordinal = -1; return false;
    }

    internal (WorldFederationWireFormat.ResponseKind Kind, byte[] Body) RoundTrip(string sourceAuthority, WorldFederationWireFormat.RequestKind kind, byte[] body) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(m_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        client.NoDelay = true;
        client.ConnectAsync(remoteEP: m_endpoint, cancellationToken: timeout.Token).AsTask().GetAwaiter().GetResult();
        using var stream = client.GetStream();
        WorldFederationWireFormat.WriteHelloAsync(stream: stream, ct: timeout.Token).GetAwaiter().GetResult();
        AuthenticateAsync(stream: stream, sourceAuthority: sourceAuthority, ct: timeout.Token).GetAwaiter().GetResult();
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
            await AuthenticateAsync(stream: stream, sourceAuthority: m_observerAuthority, ct: ct).ConfigureAwait(false);
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

    private async Task AuthenticateAsync(NetworkStream stream, string sourceAuthority, CancellationToken ct) {
        var challenge = await WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: ct).ConfigureAwait(false);

        if ((challenge is null) || (challenge.Value.Kind != (byte)WorldFederationWireFormat.ResponseKind.Challenge) || (challenge.Value.Body.Length != WorldFederationSecurity.ChallengeBytes)) {
            throw new IOException(challenge is null ? "remote authority closed before federation challenge" : DecodeRefusal(((WorldFederationWireFormat.ResponseKind)challenge.Value.Kind, challenge.Value.Body)));
        }

        var proof = m_security.Prove(sourceAuthority: sourceAuthority, challenge: challenge.Value.Body);
        await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.Authenticate, body: WorldFederationWireFormat.EncodeAuthentication(sourceAuthority: sourceAuthority, proof: proof), ct: ct).ConfigureAwait(false);
        var verdict = await WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: ct).ConfigureAwait(false);

        if ((verdict is null) || (verdict.Value.Kind != (byte)WorldFederationWireFormat.ResponseKind.Ack)) {
            throw new IOException(verdict is null ? "remote authority closed during federation authentication" : DecodeRefusal(((WorldFederationWireFormat.ResponseKind)verdict.Value.Kind, verdict.Value.Body)));
        }
    }
}
