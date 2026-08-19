using System.Security.Cryptography;
using Puck.Attestation;

namespace Puck.Networking.Peers;

/// <summary>The one handshake both a dialer and an acceptor run over the control stream: write a Hello offer
/// without waiting to read the peer's, bind the identity the peer offered to the key its transport proved, then
/// exchange proofs over each other's challenge through <see cref="PeerAuthenticator"/>. Identical code on both
/// sides is what makes the connection symmetric — nothing here distinguishes who dialed or who opened the
/// stream.</summary>
internal static class PeerHandshake {
    private static async Task DrainUntilPeerClosesAsync(Stream stream, CancellationToken ct) {
        var sink = new byte[256];

        try {
            while (await stream.ReadAsync(
                buffer: sink,
                cancellationToken: ct
            ).ConfigureAwait(continueOnCapturedContext: false) > 0) {
            }
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException or OperationCanceledException)) {
        }
    }
    private static async Task<PeerFailure> RefuseAsync(Stream stream, PeerFailure failure, CancellationToken ct) {
        try {
            await WireFrame.WriteAsync(
                body: new[] { ((byte)failure.Refusal) },
                ct: ct,
                kind: ((byte)PeerFrameKind.HelloRefused),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
            await DrainUntilPeerClosesAsync(
                ct: ct,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException or OperationCanceledException)) {
        }

        return failure;
    }
    private static PeerFailure ReadRefusal(WireFrameRead frame) {
        var reader = new WireReader(bytes: frame.Body);
        var refusal = reader.ReadByte();

        if (
            !reader.TryFinish(failure: out var wireFailure) ||
            !Enum.IsDefined(value: ((PeerRefusal)refusal))
        ) {
            return new PeerFailure(
                Detail: (wireFailure.IsRefusal
                    ? wireFailure.ToString()
                    : $"the peer's refusal frame names an unknown refusal ({refusal})"
                ),
                Refusal: PeerRefusal.HandshakeMalformed
            );
        }

        return new PeerFailure(
            Detail: $"the peer refused this side's handshake as {((PeerRefusal)refusal)}",
            Refusal: PeerRefusal.RefusedByPeer
        );
    }

    /// <summary>Runs the handshake to completion or refusal.</summary>
    /// <param name="local">This side's identity.</param>
    /// <param name="connection">The transport connection.</param>
    /// <param name="stream">The control stream, opened by one side and accepted by the other.</param>
    /// <param name="onClosed">Invoked once the resulting link closes.</param>
    /// <param name="now">The verification-boundary clock read, overridable for tests.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The established link, or the refusal that stopped the handshake.</returns>
    public static async Task<(PeerLink? Link, PeerFailure Failure)> RunAsync(PeerIdentity local, IPeerConnection connection, Stream stream, Action<PeerLink>? onClosed, Func<DateTimeOffset>? now, CancellationToken ct) {
        var ownChallenge = RandomNumberGenerator.GetBytes(count: PeerWireProtocol.ChallengeBytes);

        var offer = new WireWriter();

        offer.WriteUInt64(value: PeerWireProtocol.ProtocolKey);
        offer.WriteBlock(value: local.SubjectPublicKeyInfo);
        offer.WriteBlock(value: ownChallenge);

        await WireFrame.WriteAsync(
            body: offer.ToArray(),
            ct: ct,
            kind: ((byte)PeerFrameKind.HelloOffer),
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        var offerFrame = await WireFrame.ReadAsync(
            ct: ct,
            maxFrameBytes: PeerWireProtocol.MaxFrameBytes,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!offerFrame.Ok) {
            return (null, new PeerFailure(
                Detail: offerFrame.Failure.ToString(),
                Refusal: PeerRefusal.ConnectionClosed
            ));
        }

        if (offerFrame.Kind == ((byte)PeerFrameKind.HelloRefused)) {
            return (null, ReadRefusal(frame: offerFrame));
        }

        if (offerFrame.Kind != ((byte)PeerFrameKind.HelloOffer)) {
            return (null, new PeerFailure(
                Detail: $"expected a Hello offer, got frame kind {offerFrame.Kind}",
                Refusal: PeerRefusal.HandshakeMalformed
            ));
        }

        var offerReader = new WireReader(bytes: offerFrame.Body);
        var protocolKey = offerReader.ReadUInt64();
        var peerSubjectPublicKeyInfo = offerReader.ReadBlock(
            field: "subject public key info",
            maxBytes: AttestationResourceLimits.SubjectPublicKeyInfoBytes
        );
        var peerChallenge = offerReader.ReadBlock(
            field: "challenge",
            maxBytes: PeerWireProtocol.ChallengeBytes
        );

        if (!offerReader.TryFinish(failure: out var offerFailure)) {
            return (null, new PeerFailure(
                Detail: offerFailure.ToString(),
                Refusal: PeerRefusal.HandshakeMalformed
            ));
        }

        if (protocolKey != PeerWireProtocol.ProtocolKey) {
            return (null, await RefuseAsync(
                ct: ct,
                failure: new PeerFailure(
                    Detail: $"offered protocol key 0x{protocolKey:x16} != 0x{PeerWireProtocol.ProtocolKey:x16}",
                    Refusal: PeerRefusal.ProtocolMismatch
                ),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        if (peerChallenge.Length != PeerWireProtocol.ChallengeBytes) {
            return (null, new PeerFailure(
                Detail: $"the offered challenge is {peerChallenge.Length} bytes; {PeerWireProtocol.ChallengeBytes} are required",
                Refusal: PeerRefusal.HandshakeMalformed
            ));
        }

        if (!connection.RemoteTransportKey.Span.SequenceEqual(other: peerSubjectPublicKeyInfo)) {
            return (null, await RefuseAsync(
                ct: ct,
                failure: new PeerFailure(
                    Detail: "the identity the peer offered is not the key its transport proved possession of",
                    Refusal: PeerRefusal.ChannelUnbound
                ),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        var authenticator = new PeerAuthenticator(
            expectedAudience: local.Id.Domain,
            expectedSubjectPublicKeyInfo: peerSubjectPublicKeyInfo,
            now: now,
            prover: local
        );
        var proofWriter = new WireWriter();

        proofWriter.WriteBlock(value: authenticator.Prove(challenge: peerChallenge));

        await WireFrame.WriteAsync(
            body: proofWriter.ToArray(),
            ct: ct,
            kind: ((byte)PeerFrameKind.HelloProof),
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        var proofFrame = await WireFrame.ReadAsync(
            ct: ct,
            maxFrameBytes: PeerWireProtocol.MaxFrameBytes,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!proofFrame.Ok) {
            return (null, new PeerFailure(
                Detail: proofFrame.Failure.ToString(),
                Refusal: PeerRefusal.ConnectionClosed
            ));
        }

        if (proofFrame.Kind == ((byte)PeerFrameKind.HelloRefused)) {
            return (null, ReadRefusal(frame: proofFrame));
        }

        if (proofFrame.Kind != ((byte)PeerFrameKind.HelloProof)) {
            return (null, new PeerFailure(
                Detail: $"expected a Hello proof, got frame kind {proofFrame.Kind}",
                Refusal: PeerRefusal.HandshakeMalformed
            ));
        }

        var proofReader = new WireReader(bytes: proofFrame.Body);
        var peerProof = proofReader.ReadBlock(
            field: "proof",
            maxBytes: AttestationResourceLimits.AttestationBytes
        );

        if (!proofReader.TryFinish(failure: out var proofFailure)) {
            return (null, new PeerFailure(
                Detail: proofFailure.ToString(),
                Refusal: PeerRefusal.HandshakeMalformed
            ));
        }

        if (
            !authenticator.TryVerify(
            challenge: ownChallenge,
            proof: peerProof,
            sourceAuthority: out var peerFingerprint
        ) ||
            (peerFingerprint is null)
        ) {
            return (null, await RefuseAsync(
                ct: ct,
                failure: new PeerFailure(
                    Detail: "the peer's proof did not verify against the identity it offered",
                    Refusal: PeerRefusal.IdentityUnproven
                ),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        var remoteId = KeyId.ForSubject(
            algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            domain: peerFingerprint,
            subject: peerFingerprint,
            subjectPublicKeyInfo: peerSubjectPublicKeyInfo
        );
        var link = new PeerLink(
            connection: connection,
            local: local,
            now: now,
            onClosed: onClosed,
            remoteId: remoteId,
            remoteSubjectPublicKeyInfo: peerSubjectPublicKeyInfo,
            stream: stream
        );

        return (link, default);
    }
}
