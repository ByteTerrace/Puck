using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Puck.Carriage;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>--connect</c> boot shape: a minimal, self-contained TCP socket client that speaks the wire protocol
/// directly instead of composing a local <see cref="Puck.World.Server.WorldServer"/> (no GPU, no local authoritative
/// server, no <c>CommandRegistry</c>). It runs the Hello (protocol-version) handshake, then the admission door's
/// challenge-response identity check (see <see cref="RunAsync"/> remarks), then reads a small stdin verb grammar
/// that encodes through the same <see cref="WorldSubmissionCodec"/> leaf codecs the loopback path uses, over the
/// socket instead of in-process. Every verb this client owns is spelled <c>peer.*</c> so it can never be read as a
/// console verb: the mutation verbs <c>peer.hud.panel.remove &lt;id&gt;</c> and
/// <c>peer.hud.element.remove &lt;panel&gt; &lt;element&gt;</c>, which build their mutation kinds directly rather
/// than through the registry; the client-local <c>peer.sleep &lt;ms&gt;</c>, which holds the connection open and
/// never reaches the wire; and <c>peer.quit</c>. <c>player.where &lt;n&gt;</c> keeps its console spelling because it
/// is that console verb's query (<see cref="WorldQuery.PlayerWhere"/>), asked over the socket. This client supports
/// only these verbs, not the full console surface a windowed or headless boot registers.
/// <para>Every mutation this client encodes stamps <see cref="WorldPrincipal.Console"/> as a placeholder; the host's
/// <c>WorldTcpHost.StampPrincipal</c> re-stamps the connection's own admitted identity onto the payload before it
/// reaches the ordered domain, so the identity the client's bytes claim is never the identity the server authorizes
/// against.</para>
/// </summary>
internal static class WorldRemoteClient {
    /// <summary>Connects to a TCP socket host and runs the stdin verb loop until EOF, <c>peer.quit</c>, or
    /// disconnect. After the protocol-version check passes, answers the door's identity challenge
    /// (<see cref="WorldAdmissionDoor"/>): with <paramref name="identityDir"/>, signs the exact challenge nonce with
    /// the identity it names; without one, signs with a freshly minted, unregistered P-256 key, which the host is
    /// expected to refuse by name.</summary>
    /// <param name="connect">The <c>host:port</c> endpoint to connect to.</param>
    /// <param name="identityDir">A directory holding <c>private-key.pkcs8</c> (an ECDsa PKCS8 private key),
    /// <c>domain.txt</c>, <c>subject.txt</c> (the signing subject key's own platform user id — required whether the
    /// identity is <c>signsDirectly</c> or <c>vouches</c>: a claim is always signed by a subject key, even when the
    /// admission entry that admits it pins only a root), <c>algorithm.txt</c> (defaults to
    /// <c>ecdsa-p256-sha256</c> if absent), and an optional <c>chain/</c> subdirectory holding <c>1.envelope</c> and
    /// (for a two-hop <c>vouches</c> identity) <c>2.envelope</c> — carriage-codec-encoded key-binding envelopes,
    /// root-to-subject order; absent for a <c>signsDirectly</c> identity. <see langword="null"/> mints the
    /// throwaway identity described above.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string connect, string? identityDir) {
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
            var reason = WorldTcpWireFormat.DecodeText(body: helloFrame.Body);

            Console.Error.WriteLine(value: $"[world.connect: refused — {reason}]");

            return 1;
        }

        if (helloFrame.Kind != WorldTcpWireFormat.DownstreamKind.HelloChallenge) {
            Console.Error.WriteLine(value: $"[world.connect: unexpected first frame kind {helloFrame.Kind}]");

            return 1;
        }

        // Door 2 of 2 — the identity challenge. Sign the EXACT challenge bytes (opaque claim payload) with either
        // the caller-supplied identity or a fresh throwaway one, and answer with a HelloIdentity frame.
        var challenge = helloFrame.Body;
        var codec = new FixedLayoutCarriageCodec();
        var (claimBytes, chainBytes) = BuildIdentityResponse(codec: codec, challenge: challenge, identityDir: identityDir);

        await WorldTcpWireFormat.WriteHelloIdentityAsync(stream: stream, chain: chainBytes, claim: claimBytes, ct: cts.Token).ConfigureAwait(continueOnCapturedContext: false);

        var admission = await WorldTcpWireFormat.TryReadDownstreamAsync(stream: stream, ct: cts.Token).ConfigureAwait(continueOnCapturedContext: false);

        if (admission is not { } admissionFrame) {
            Console.Error.WriteLine(value: "[world.connect: the host closed the connection before completing the identity challenge]");

            return 1;
        }

        if (admissionFrame.Kind == WorldTcpWireFormat.DownstreamKind.HelloRefused) {
            var reason = WorldTcpWireFormat.DecodeText(body: admissionFrame.Body);

            Console.Error.WriteLine(value: $"[world.connect: refused — {reason}]");

            return 1;
        }

        if (admissionFrame.Kind != WorldTcpWireFormat.DownstreamKind.HelloAccepted) {
            Console.Error.WriteLine(value: $"[world.connect: unexpected frame kind {admissionFrame.Kind} after the identity challenge]");

            return 1;
        }

        var peerIndex = BinaryPrimitives.ReadInt32LittleEndian(source: admissionFrame.Body);
        var generation = BinaryPrimitives.ReadInt32LittleEndian(source: admissionFrame.Body.AsSpan(start: sizeof(int)));
        var connectionId = BinaryPrimitives.ReadInt32LittleEndian(source: admissionFrame.Body.AsSpan(start: (2 * sizeof(int))));

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

    // Answers the identity challenge: either the caller's own configured identity (--connect-identity-dir), or a
    // freshly minted, unregistered, throwaway P-256 key that no admission entry on the far side will ever name — a
    // deliberate way to exercise the identity door's refusal path without requiring a pre-arranged identity for
    // every run. Either way the claim's payload is the EXACT challenge bytes (opaque), signed under a short window
    // around "now" and directed at the door's fixed purpose/audience — never a durable carried claim (no sequence).
    private static (byte[] Claim, byte[][] Chain) BuildIdentityResponse(FixedLayoutCarriageCodec codec, byte[] challenge, string? identityDir) {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const long notBeforeSlackSeconds = 30L;
        const long windowSeconds = 300L;

        if (identityDir is null) {
            using var throwawayKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
            var throwawaySpki = throwawayKey.ExportSubjectPublicKeyInfo();
            var throwawayDomain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: throwawaySpki);
            var claim = CarriageSigner.SignClaim(
                codec: codec,
                domain: throwawayDomain,
                subject: "unregistered-throwaway",
                signerKey: throwawayKey,
                signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
                purpose: WorldAdmissionDoor.Purpose,
                notBefore: (now - notBeforeSlackSeconds),
                notAfter: (now + windowSeconds),
                audience: WorldAdmissionDoor.Audience,
                sequence: null,
                claimBytes: challenge
            );

            Console.Error.WriteLine(value: $"[world.connect: no --connect-identity-dir given — signing with a fresh unregistered key (domain {throwawayDomain}); expect the host to refuse this identity by name]");

            return (codec.EncodeEnvelope(envelope: claim), []);
        }

        var algorithmPath = Path.Combine(path1: identityDir, path2: "algorithm.txt");
        var algorithm = (File.Exists(path: algorithmPath) ? File.ReadAllText(path: algorithmPath).Trim() : CarriageAlgorithms.EcdsaP256Sha256);
        var domain = File.ReadAllText(path: Path.Combine(path1: identityDir, path2: "domain.txt")).Trim();
        // REQUIRED regardless of mode: a claim is always signed by a SUBJECT key (SignClaim's own shape), even when
        // the admission ENTRY that admits it pins only a root under WorldAdmissionTrustMode.Vouches.
        var subject = File.ReadAllText(path: Path.Combine(path1: identityDir, path2: "subject.txt")).Trim();

        using var signingKey = ECDsa.Create();

        signingKey.ImportPkcs8PrivateKey(source: File.ReadAllBytes(path: Path.Combine(path1: identityDir, path2: "private-key.pkcs8")), bytesRead: out _);

        var chainDir = Path.Combine(path1: identityDir, path2: "chain");
        var chain = new List<byte[]>();

        if (Directory.Exists(path: chainDir)) {
            foreach (var fileName in new[] { "1.envelope", "2.envelope" }) {
                var chainPath = Path.Combine(path1: chainDir, path2: fileName);

                if (File.Exists(path: chainPath)) {
                    chain.Add(item: File.ReadAllBytes(path: chainPath));
                }
            }
        }

        // A signsDirectly identity signs directly with its own key; a vouches (chain-carrying) identity signs with
        // that SAME key too — the chain proves the key up to the root, the claim itself is always signed by the
        // subject key regardless of which shape admitted it.
        var signedClaim = CarriageSigner.SignClaim(
            codec: codec,
            domain: domain,
            subject: subject,
            signerKey: signingKey,
            signerAlgorithm: algorithm,
            purpose: WorldAdmissionDoor.Purpose,
            notBefore: (now - notBeforeSlackSeconds),
            notAfter: (now + windowSeconds),
            audience: WorldAdmissionDoor.Audience,
            sequence: null,
            claimBytes: challenge
        );

        return (codec.EncodeEnvelope(envelope: signedClaim), [.. chain]);
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
                    var reason = WorldTcpWireFormat.DecodeText(body: frameReply.Body);

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
