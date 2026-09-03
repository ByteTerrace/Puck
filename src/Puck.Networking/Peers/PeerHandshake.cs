using System.Security.Cryptography;
using Puck.Attestation;

namespace Puck.Networking.Peers;

/// <summary>The one handshake both a dialer and an acceptor run over the control stream: write a Hello offer
/// without waiting to read the peer's, bind the identity the peer offered to the key its transport proved, then
/// exchange proofs over each other's challenge through <see cref="PeerAuthenticator"/>. Identical code on both
/// sides is what makes the connection symmetric — nothing here distinguishes who dialed or who opened the
/// stream. Every refusal decided after this side's offer is written is sent to the peer as a
/// <see cref="PeerFrameKind.HelloRefused"/> frame through <see cref="RefuseAsync"/>; only a connection the peer
/// closed first is returned silently, since there is nobody left to tell.</summary>
internal static class PeerHandshake {
    /// <summary>Turns a <see cref="PeerFrameKind.HelloRefused"/> frame the handshake received into the refusal
    /// this side reports: the peer's own name (<see cref="PeerRefusal.RefusedByPeer"/>) is returned as it stands,
    /// since the peer that sent it is already refusing and draining, while a body that does not decode is this
    /// side's <see cref="PeerRefusal.HandshakeMalformed"/> decision and is sent through <see cref="RefuseAsync"/>
    /// like every other refusal this side decides.</summary>
    /// <param name="stream">The control stream.</param>
    /// <param name="frame">The frame, already known to carry the refused kind.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The failure to report.</returns>
    private static async Task<PeerFailure> ReadRefusedFrameAsync(Stream stream, WireFrameRead frame, CancellationToken ct) {
        var refusal = ReadRefusal(frame: frame);

        if (refusal.Refusal == PeerRefusal.RefusedByPeer) {
            return refusal;
        }

        return await RefuseAsync(
            ct: ct,
            failure: refusal,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Writes a <see cref="PeerFrameKind.HelloRefused"/> frame naming <paramref name="failure"/>'s
    /// refusal (only the refusal byte crosses; the detail stays local), then drains the stream until the peer
    /// closes or <see cref="PeerWireProtocol.RefusalDrainTimeout"/> elapses, so two sides refusing each other
    /// do not both wait for <see cref="PeerWireProtocol.HandshakeTimeout"/>. Transport failures while refusing
    /// are swallowed: the refusal is already decided.</summary>
    /// <param name="stream">The control stream.</param>
    /// <param name="failure">The refusal to send and return.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><paramref name="failure"/>, unchanged.</returns>
    private static async Task<PeerFailure> RefuseAsync(Stream stream, PeerFailure failure, CancellationToken ct) {
        try {
            await WireFrame.WriteAsync(
                body: new[] { ((byte)failure.Refusal) },
                ct: ct,
                kind: ((byte)PeerFrameKind.HelloRefused),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            using var drain = CancellationTokenSource.CreateLinkedTokenSource(token: ct);

            drain.CancelAfter(delay: PeerWireProtocol.RefusalDrainTimeout);

            await StreamDrain.UntilClosedAsync(
                ct: drain.Token,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException or OperationCanceledException)) {
        }

        return failure;
    }
    /// <summary>Turns a failed frame read into the handshake's refusal: a peer that closed the connection is
    /// reported as <see cref="PeerRefusal.ConnectionClosed"/> without writing anything (there is nobody to
    /// tell), while any other read refusal — a frame over <see cref="PeerWireProtocol.MaxFrameBytes"/> — is a
    /// grammar violation the peer is told about as <see cref="PeerRefusal.HandshakeMalformed"/>.</summary>
    /// <param name="stream">The control stream.</param>
    /// <param name="failure">The read's failure.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The failure to report.</returns>
    private static async Task<PeerFailure> RefuseReadFailureAsync(Stream stream, WireFailure failure, CancellationToken ct) {
        if (failure.Refusal == WireRefusal.ConnectionClosed) {
            return new PeerFailure(
                Detail: failure.ToString(),
                Refusal: PeerRefusal.ConnectionClosed
            );
        }

        return await RefuseAsync(
            ct: ct,
            failure: new PeerFailure(
                Detail: failure.ToString(),
                Refusal: PeerRefusal.HandshakeMalformed
            ),
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);
    }

    /// <summary>Decodes a <see cref="PeerFrameKind.HelloRefused"/> frame into the refusal this side reports: the
    /// peer's name as <see cref="PeerRefusal.RefusedByPeer"/>, or <see cref="PeerRefusal.HandshakeMalformed"/> when
    /// the frame's body does not hold exactly one known refusal byte. Decodes only: the handshake sends the
    /// malformed case to the peer through <see cref="ReadRefusedFrameAsync"/>, and <see cref="PeerLink"/>, whose
    /// read loop can receive one after both proofs were sent, renames it to its own
    /// <see cref="PeerRefusal.FrameMalformed"/>.</summary>
    /// <param name="frame">The frame, already known to carry the refused kind.</param>
    /// <returns>The failure to report.</returns>
    internal static PeerFailure ReadRefusal(WireFrameRead frame) {
        var reader = new WireReader(bytes: frame.Body.Span);
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
    /// <returns>The established link, or the refusal that stopped the handshake:
    /// <see cref="PeerRefusal.ConnectionClosed"/> when the peer closed first (returned without writing anything),
    /// <see cref="PeerRefusal.RefusedByPeer"/> when the peer refused by name, and otherwise a refusal this side
    /// decided and sent — <see cref="PeerRefusal.HandshakeMalformed"/>, <see cref="PeerRefusal.ProtocolMismatch"/>,
    /// <see cref="PeerRefusal.ChannelUnbound"/> (including a transport that proved no key at all),
    /// <see cref="PeerRefusal.IdentityKeyInvalid"/>, or <see cref="PeerRefusal.IdentityUnproven"/>.</returns>
    public static async Task<(PeerLink? Link, PeerFailure Failure)> RunAsync(PeerIdentity local, IPeerConnection connection, Stream stream, Action<PeerLink>? onClosed, Func<DateTimeOffset>? now, CancellationToken ct) {
        var ownChallenge = RandomNumberGenerator.GetBytes(count: PeerWireProtocol.ChallengeBytes);

        var offer = new WireWriter();

        offer.WriteUInt64(value: PeerWireProtocol.ProtocolKey);
        offer.WriteBlock(value: local.SubjectPublicKeyInfo);
        offer.WriteBlock(value: ownChallenge);

        await WireFrame.WriteAsync(
            body: offer.WrittenMemory,
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
            return (null, await RefuseReadFailureAsync(
                ct: ct,
                failure: offerFrame.Failure,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        if (offerFrame.Kind == ((byte)PeerFrameKind.HelloRefused)) {
            return (null, await ReadRefusedFrameAsync(
                ct: ct,
                frame: offerFrame,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        if (offerFrame.Kind != ((byte)PeerFrameKind.HelloOffer)) {
            return (null, await RefuseAsync(
                ct: ct,
                failure: new PeerFailure(
                    Detail: $"expected a Hello offer, got frame kind {offerFrame.Kind}",
                    Refusal: PeerRefusal.HandshakeMalformed
                ),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        var offerReader = new WireReader(bytes: offerFrame.Body.Span);
        var protocolKey = offerReader.ReadUInt64();
        // Read under the frame cap, not the attestation profile's SPKI cap: an oversized key (an RSA-4096 SPKI is
        // 550 bytes, over AttestationResourceLimits.SubjectPublicKeyInfoBytes) is an honest offer of the wrong key,
        // refused below as IdentityKeyInvalid, not a grammar violation. The frame's own cap already bounds the
        // allocation.
        var peerSubjectPublicKeyInfo = offerReader.ReadBlock(
            field: "subject public key info",
            maxBytes: PeerWireProtocol.MaxFrameBytes
        );
        var peerChallenge = offerReader.ReadBlock(
            field: "challenge",
            maxBytes: PeerWireProtocol.ChallengeBytes
        );

        if (!offerReader.TryFinish(failure: out var offerFailure)) {
            return (null, await RefuseAsync(
                ct: ct,
                failure: new PeerFailure(
                    Detail: offerFailure.ToString(),
                    Refusal: PeerRefusal.HandshakeMalformed
                ),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
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
            return (null, await RefuseAsync(
                ct: ct,
                failure: new PeerFailure(
                    Detail: $"the offered challenge is {peerChallenge.Length} bytes; {PeerWireProtocol.ChallengeBytes} are required",
                    Refusal: PeerRefusal.HandshakeMalformed
                ),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        // An empty transport key would compare equal to an empty offered SPKI, so binding is refused outright
        // rather than left to the comparison: a transport that proved no key cannot bind anything to the channel.
        if (connection.RemoteTransportKey.IsEmpty) {
            return (null, await RefuseAsync(
                ct: ct,
                failure: new PeerFailure(
                    Detail: "the transport proved no key",
                    Refusal: PeerRefusal.ChannelUnbound
                ),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
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

        // No P-256 SPKI is anywhere near the attestation profile's cap, so a longer one is refused as the wrong key
        // before it is imported — the same name an RSA or P-384 key gets, whatever its size.
        if (peerSubjectPublicKeyInfo.Length > AttestationResourceLimits.SubjectPublicKeyInfoBytes) {
            return (null, await RefuseAsync(
                ct: ct,
                failure: new PeerFailure(
                    Detail: $"the offered subject public key info is {peerSubjectPublicKeyInfo.Length} bytes; no P-256 key is longer than {AttestationResourceLimits.SubjectPublicKeyInfoBytes}",
                    Refusal: PeerRefusal.IdentityKeyInvalid
                ),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        // The authenticator's constructor builds the trust list that pins the offered SPKI, and building it imports
        // the key: malformed bytes, trailing bytes, or a key on any curve but P-256 surface here, as the exceptions
        // the attestation layer names, and are refused by name rather than escaping the handshake task. The import
        // can also raise PlatformNotSupportedException — a host whose elliptic-curve implementation is named-curves
        // only refuses an SPKI carrying explicit curve parameters that way — and that is the same decision: the
        // offered key is not one this side can use as P-256.
        PeerAuthenticator authenticator;

        try {
            authenticator = new PeerAuthenticator(
                expectedAudience: local.Id.Domain,
                expectedSubjectPublicKeyInfo: peerSubjectPublicKeyInfo,
                now: now,
                prover: local
            );
        } catch (Exception exception) when ((exception is ArgumentException or CryptographicException or PlatformNotSupportedException)) {
            return (null, await RefuseAsync(
                ct: ct,
                failure: new PeerFailure(
                    Detail: $"the offered subject public key info is not a P-256 key this host can import — {exception.GetType().Name}: {exception.Message}",
                    Refusal: PeerRefusal.IdentityKeyInvalid
                ),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        var proofWriter = new WireWriter();

        proofWriter.WriteBlock(value: authenticator.Prove(challenge: peerChallenge));

        await WireFrame.WriteAsync(
            body: proofWriter.WrittenMemory,
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
            return (null, await RefuseReadFailureAsync(
                ct: ct,
                failure: proofFrame.Failure,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        if (proofFrame.Kind == ((byte)PeerFrameKind.HelloRefused)) {
            return (null, await ReadRefusedFrameAsync(
                ct: ct,
                frame: proofFrame,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        if (proofFrame.Kind != ((byte)PeerFrameKind.HelloProof)) {
            return (null, await RefuseAsync(
                ct: ct,
                failure: new PeerFailure(
                    Detail: $"expected a Hello proof, got frame kind {proofFrame.Kind}",
                    Refusal: PeerRefusal.HandshakeMalformed
                ),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
        }

        var proofReader = new WireReader(bytes: proofFrame.Body.Span);
        var peerProof = proofReader.ReadBlock(
            field: "proof",
            maxBytes: AttestationResourceLimits.AttestationBytes
        );

        if (!proofReader.TryFinish(failure: out var proofFailure)) {
            return (null, await RefuseAsync(
                ct: ct,
                failure: new PeerFailure(
                    Detail: proofFailure.ToString(),
                    Refusal: PeerRefusal.HandshakeMalformed
                ),
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false));
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
