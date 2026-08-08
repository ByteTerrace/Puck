using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>--connect</c> boot shape: a minimal, self-contained TCP socket client — NOT a second
/// <see cref="Puck.World.Server.WorldServer"/>/composition graph, deliberately. It skips the whole normal DI
/// composition (no GPU, no local authoritative server, no <c>CommandRegistry</c>) and instead speaks the wire
/// directly: Hello, then a small stdin verb grammar that encodes through the SAME
/// <see cref="WorldSubmissionCodec"/> leaf codecs the loopback always uses, over the socket instead of in-process.
/// It supports exactly the verbs needed to prove the round trip. Every token this harness OWNS is spelled
/// <c>peer.*</c> so it can never be read as a console verb: the two MUTATION verbs
/// <c>peer.hud.panel.remove &lt;id&gt;</c> and <c>peer.hud.element.remove &lt;panel&gt; &lt;element&gt;</c>, which build
/// their mutation kinds directly rather than through the registry (the peer door's own half of the mutation
/// substrate — one section, two kinds, strings only); the client-local <c>peer.sleep &lt;ms&gt;</c> (holds the
/// connection open; never reaches the wire); and <c>peer.quit</c>. The ONE token that keeps a console spelling is
/// <c>player.where &lt;n&gt;</c> (a <see cref="WorldQuery.PlayerWhere"/>), because it IS that console verb's query,
/// asked over the socket — a reader's knowledge transfers. This is never the full console surface a
/// windowed/headless boot registers; the console reaches the same HUD section through
/// <c>world.row.set hud.panels</c>/<c>world.row.remove hud.panels</c>, which have no element-level door at all.
/// <para>Every mutation this client encodes stamps <see cref="WorldPrincipal.Console"/> as a placeholder the wire
/// carries and the HOST overwrites: <c>WorldTcpHost.StampPrincipal</c> re-stamps the connection's own admitted
/// identity onto the payload before it reaches the ordered domain, so what the client's bytes CLAIM is never what
/// the server authorizes against. Sending the most privileged identity in the vocabulary from here and watching the
/// host authorize it as <c>peer:&lt;n&gt;:&lt;gen&gt;</c> is that property under test, not an oversight.</para>
/// </summary>
internal static class WorldRemoteClient {
    /// <summary>Connects to a TCP socket host and runs the stdin verb loop until EOF/<c>peer.quit</c>/disconnect.</summary>
    /// <param name="connect">The <c>host:port</c> endpoint to connect to.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string connect) {
        if (!IPEndPoint.TryParse(s: connect, result: out var endpoint)) {
            Console.Error.WriteLine(value: $"--connect '{connect}' is not a parseable \"ip:port\" endpoint (a hostname is not accepted).");

            return 1;
        }

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) => {
            e.Cancel = true;
            cts.Cancel();
        };

        using var client = new TcpClient();

        try {
            await client.ConnectAsync(remoteEP: endpoint, cancellationToken: cts.Token).ConfigureAwait(continueOnCapturedContext: false);
        } catch (SocketException ex) {
            Console.Error.WriteLine(value: $"[world.connect: could not reach {connect} — {ex.Message}]");

            return 1;
        }

        client.NoDelay = true;

        var stream = client.GetStream();

        await WorldTcpWireFormat.WriteHelloAsync(stream: stream, key: WorldProtocol.WireProtocolKey, ct: cts.Token).ConfigureAwait(continueOnCapturedContext: false);

        var hello = await WorldTcpWireFormat.TryReadDownstreamAsync(stream: stream, ct: cts.Token).ConfigureAwait(continueOnCapturedContext: false);

        if (hello is not { } helloFrame) {
            Console.Error.WriteLine(value: "[world.connect: the host closed the connection before completing Hello]");

            return 1;
        }

        if (helloFrame.Kind == WorldTcpWireFormat.DownstreamKind.HelloRefused) {
            var offset = 0;
            var reason = WorldTcpWireFormat.ReadLengthPrefixedString(body: helloFrame.Body, offset: ref offset);

            Console.Error.WriteLine(value: $"[world.connect: refused — {reason}]");

            return 1;
        }

        if (helloFrame.Kind != WorldTcpWireFormat.DownstreamKind.HelloAccepted) {
            Console.Error.WriteLine(value: $"[world.connect: unexpected first frame kind {helloFrame.Kind}]");

            return 1;
        }

        var peerIndex = BinaryPrimitives.ReadInt32LittleEndian(source: helloFrame.Body);
        var generation = BinaryPrimitives.ReadInt32LittleEndian(source: helloFrame.Body.AsSpan(start: sizeof(int)));
        var connectionId = BinaryPrimitives.ReadInt32LittleEndian(source: helloFrame.Body.AsSpan(start: (2 * sizeof(int))));

        Console.Out.WriteLine(value: $"[world.connect: accepted peer:{peerIndex}:{generation} connection:{connectionId} — player.where {(peerIndex + 1)}]");

        while (!cts.IsCancellationRequested) {
            var line = await Console.In.ReadLineAsync(cancellationToken: cts.Token).ConfigureAwait(continueOnCapturedContext: false);

            if (line is null) {
                break;
            }

            var trimmed = line.Trim();

            if ((trimmed.Length == 0) || trimmed.StartsWith(value: '#')) {
                continue;
            }

            if (string.Equals(a: trimmed, b: "peer.quit", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                break;
            }

            var tokens = trimmed.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);

            if ((tokens.Length == 2) && string.Equals(a: tokens[0], b: "player.where", comparisonType: StringComparison.OrdinalIgnoreCase) && int.TryParse(s: tokens[1], result: out var index)) {
                if (!await SubmitAsync(stream: stream, payload: new WorldSubmissionPayload.Query(Value: new WorldQuery.PlayerWhere(Index: index)), ct: cts.Token).ConfigureAwait(continueOnCapturedContext: false)) {
                    break;
                }
            } else if ((tokens.Length == 2) && string.Equals(a: tokens[0], b: "peer.hud.panel.remove", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                // A MUTATION over the wire — the peer door's own half of the mutation substrate, and the reason this
                // verb exists at all: without it nothing could drive a remote principal through
                // WorldServer.TryAdmitMutation, so the masks and the per-tick budget a peer's grant row carries had no
                // way to be exercised outside an addon. Two Hud kinds deliberately (RemoveHudPanel and
                // RemoveHudElement below): both take only strings, so this stays a socket harness rather than growing
                // a document-JSON parser, and both sit in ONE section, so a verbs:<one-of-them> row discriminates.
                // The peer.* spelling is what keeps this surface distinguishable from the console's own.
                if (!await SubmitAsync(stream: stream, payload: new WorldSubmissionPayload.Mutation(Value: new WorldMutation.RemoveHudPanel(Principal: WorldPrincipal.Console, Id: tokens[1])), ct: cts.Token).ConfigureAwait(continueOnCapturedContext: false)) {
                    break;
                }
            } else if ((tokens.Length == 3) && string.Equals(a: tokens[0], b: "peer.hud.element.remove", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                if (!await SubmitAsync(stream: stream, payload: new WorldSubmissionPayload.Mutation(Value: new WorldMutation.RemoveHudElement(Principal: WorldPrincipal.Console, PanelId: tokens[1], ElementId: tokens[2])), ct: cts.Token).ConfigureAwait(continueOnCapturedContext: false)) {
                    break;
                }
            } else if ((tokens.Length == 2) && string.Equals(a: tokens[0], b: "peer.sleep", comparisonType: StringComparison.OrdinalIgnoreCase) && int.TryParse(s: tokens[1], result: out var millis) && (millis >= 0)) {
                // A CLIENT-LOCAL script convenience only — never sent over the wire, never a submission. Holds the
                // connection open (no wall-clock equivalent to the server's world.wait exists client-side) so a
                // scripted smoke can keep a peer admitted while the host's own script reads it back.
                await Task.Delay(delay: TimeSpan.FromMilliseconds(value: millis), cancellationToken: cts.Token).ConfigureAwait(continueOnCapturedContext: false);
            } else {
                Console.Error.WriteLine(value: $"[world.connect: unknown client verb '{trimmed}' — supported: player.where <n>, peer.hud.panel.remove <id>, peer.hud.element.remove <panel> <element>, peer.sleep <ms>, peer.quit]");
            }
        }

        return 0;
    }

    // Encodes one payload through the SAME leaf codec the loopback uses, writes it, awaits the one downstream
    // reply (v1 is strictly request-then-response per connection — no correlation id needed), and prints it.
    // Returns false when the socket closed underneath the request, so the caller stops the stdin loop.
    private static async Task<bool> SubmitAsync(NetworkStream stream, WorldSubmissionPayload payload, CancellationToken ct) {
        if (!WorldFrameCodec.TryEncode(payload: payload, frame: out var frame, failure: out var failure)) {
            Console.Error.WriteLine(value: $"[world.connect: local codec refused — {failure.Refusal}: {failure.Detail}]");

            return true;
        }

        await stream.WriteAsync(buffer: frame, cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        var reply = await WorldTcpWireFormat.TryReadDownstreamAsync(stream: stream, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

        if (reply is not { } frameReply) {
            Console.Error.WriteLine(value: "[world.connect: the host closed the connection]");

            return false;
        }

        var offset = 0;

        switch (frameReply.Kind) {
            case WorldTcpWireFormat.DownstreamKind.Query: {
                    var refused = (frameReply.Body[offset++] != 0);
                    var text = WorldTcpWireFormat.ReadLengthPrefixedString(body: frameReply.Body, offset: ref offset);

                    if (refused) {
                        Console.Error.WriteLine(value: text);
                    } else {
                        Console.Out.WriteLine(value: text);
                    }

                    break;
                }
            // The buffered lane's completion: the ordered domain ACCEPTED the submission for apply at the next tick
            // boundary. It is NOT an apply verdict — a mutation's authority check and its compose/validate pass both
            // run later, at the host's own drain, and their loud lines land on the HOST's stderr, never here (v1 is
            // request-then-response with no correlation id, so there is no channel to carry a deferred verdict back).
            // Say that rather than printing "unexpected reply kind Ack" at a perfectly ordinary outcome.
            case WorldTcpWireFormat.DownstreamKind.Ack:
                Console.Out.WriteLine(value: "[world.connect: accepted for apply (the host decides authority and validity at its next tick boundary and narrates there)]");

                break;
            case WorldTcpWireFormat.DownstreamKind.Refusal: {
                    var reason = WorldTcpWireFormat.ReadLengthPrefixedString(body: frameReply.Body, offset: ref offset);

                    Console.Error.WriteLine(value: $"[world.connect: refused — {reason}]");

                    break;
                }
            default:
                Console.Error.WriteLine(value: $"[world.connect: unexpected reply kind {frameReply.Kind}]");

                break;
        }

        return true;
    }
}
