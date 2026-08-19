using System.Net;
using System.Text;
using Puck.Networking.Peers;

int? listenPort = null;
string? keyPath = null;
var dialTargets = new List<string>();
for (var index = 0; (index < args.Length); index++) {
    switch (args[index]) {
        case "--listen":
            listenPort = int.Parse(s: args[++index]);

            break;
        case "--dial":
            dialTargets.Add(item: args[++index]);

            break;
        case "--key":
            keyPath = args[++index];

            break;
        default:
            Console.Error.WriteLine(value: $"unrecognized argument '{args[index]}'");

            return 1;
    }
}
if (!QuicPeerTransport.IsSupported) {
    Console.Error.WriteLine(value: "QUIC is not available on this host (msquic with TLS 1.3 support is required)");

    return 2;
}
var identity = (((keyPath is { } loadPath) && File.Exists(path: loadPath))
    ? PeerIdentity.Load(path: loadPath)
    : PeerIdentity.Create());
if ((keyPath is { } savePath) && !File.Exists(path: savePath)) {
    identity.Save(path: savePath);
}
Console.WriteLine(value: $"peer.id {identity.Id.Domain}");
await using var peer = new Peer(
    identity: identity,
    transport: new QuicPeerTransport(certificate: identity.CreateTransportCertificate())
);
if (listenPort is { } port) {
    var bound = await peer.ListenAsync(endpoint: new IPEndPoint(
        address: IPAddress.Any,
        port: port
    )).ConfigureAwait(continueOnCapturedContext: false);

    Console.WriteLine(value: $"listening {bound}");
}
_ = Task.Run(function: async () => {
    await foreach (var link in peer.IncomingLinks.ReadAllAsync().ConfigureAwait(continueOnCapturedContext: false)) {
        Console.WriteLine(value: $"link.up {link.RemoteId.Domain} {link.RemoteEndpoint}");
        _ = PumpLinkEventsAsync(link: link);
    }
});
_ = Task.Run(function: async () => {
    await foreach (var refused in peer.HandshakeRefusals.ReadAllAsync().ConfigureAwait(continueOnCapturedContext: false)) {
        Console.WriteLine(value: $"handshake.refused {refused.RemoteEndpoint} {refused.Failure}");
    }
});
foreach (var target in dialTargets) {
    await DialAsync(target: target).ConfigureAwait(continueOnCapturedContext: false);
}
string? line;
while ((line = await Console.In.ReadLineAsync().ConfigureAwait(continueOnCapturedContext: false)) is not null) {
    var parts = line.Split(
        count: 3,
        options: StringSplitOptions.RemoveEmptyEntries,
        separator: ' '
    );

    if (parts.Length == 0) {
        continue;
    }

    switch (parts[0]) {
        case "quit":
            goto done;

        case "peers":
            foreach (var link in peer.Links) {
                Console.WriteLine(value: $"peer {link.RemoteId.Domain} {link.RemoteEndpoint}");
            }

            break;

        case "dial" when (parts.Length >= 2):
            await DialAsync(target: parts[1]).ConfigureAwait(continueOnCapturedContext: false);

            break;

        case "send" when (parts.Length >= 3):
            var target = peer.Links.FirstOrDefault(predicate: candidate => candidate.RemoteId.Domain.StartsWith(
                value: parts[1],
                comparisonType: StringComparison.Ordinal
            ));

            if (target is null) {
                Console.WriteLine(value: $"refused unknown-peer: no open link's identity starts with '{parts[1]}'");

                break;
            }

            try {
                await target.SendAsync(payload: Encoding.UTF8.GetBytes(s: parts[2])).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception exception) when ((exception is IOException or ObjectDisposedException)) {
                Console.WriteLine(value: $"refused link-closed: {exception.Message}");
            }

            break;

        default:
            Console.WriteLine(value: $"refused unrecognized-command: '{line}'");

            break;
    }
}
done:

return 0;
async Task DialAsync(string target) {
    if (!IPEndPoint.TryParse(
        result: out var endpoint,
        s: target
    )) {
        Console.WriteLine(value: $"refused dial-target-unparseable: '{target}' is not a parseable \"ip:port\" endpoint");

        return;
    }

    try {
        var link = await peer.DialAsync(endpoint: endpoint).ConfigureAwait(continueOnCapturedContext: false);

        Console.WriteLine(value: $"link.up {link.RemoteId.Domain} {link.RemoteEndpoint}");
        _ = PumpLinkEventsAsync(link: link);
    } catch (PeerRefusedException exception) {
        Console.WriteLine(value: $"refused {exception.Failure}");
    } catch (IOException exception) {
        Console.WriteLine(value: $"refused {exception.GetType().Name}: {exception.Message}");
    }
}
async Task PumpLinkEventsAsync(PeerLink link) {
    await foreach (var @event in link.Events.ReadAllAsync().ConfigureAwait(continueOnCapturedContext: false)) {
        switch (@event) {
            case PeerEvent.Received received:
                Console.WriteLine(value: $"recv {link.RemoteId.Domain} {Encoding.UTF8.GetString(bytes: received.Payload.Span)}");

                break;

            case PeerEvent.Refused refused:
                Console.WriteLine(value: $"refused {refused.Failure}");

                break;

            case PeerEvent.Closed closed:
                Console.WriteLine(value: $"link.down {link.RemoteId.Domain} {closed.Reason}");

                break;
        }
    }
}
