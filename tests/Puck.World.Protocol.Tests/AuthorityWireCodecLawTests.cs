using System.Security.Cryptography;

using Puck.Attestation;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>The blob-store wire shapes, the peer downstream reply grammar, and the federation identity door all
/// hold no <c>Puck.World.Server</c> reference — this suite proves each round-trips from this project alone.</summary>
public sealed class AuthorityWireCodecLawTests {
    private static byte[] WriteResultSync(WorldSubmissionResult result) {
        using var stream = new MemoryStream();

        WorldPeerWireFormat.WriteResultAsync(
            stream: stream,
            result: result,
            ct: CancellationToken.None
        ).GetAwaiter().GetResult();

        return stream.ToArray();
    }

    [Fact]
    public void AReplayCodecExceptionCarriesItsMessageAndStandsApartFromInvalidOperationException() {
        var exception = new WorldReplayCodecException(message: "an authority-entry kind the tape codec cannot represent");

        Assert.Equal(
            "an authority-entry kind the tape codec cannot represent",
            exception.Message
        );
        Assert.IsNotType<InvalidOperationException>(@object: exception);
    }
    [Fact]
    public void ATruncatedFrameNeverDecodes() {
        Assert.False(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out _,
            frame: new byte[] { 1, 2 },
            kind: out _
        ));
    }
    [Fact]
    public void AVerifierWithNoMatchingTrustEntryRefuses() {
        var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var oracle = new LocalKeySigningOracle(
            key: key,
            subject: "authority-a",
            validity: TimeSpan.FromMinutes(value: 1)
        );
        var prover = new WorldAttestedAuthenticator(oracle: oracle);
        var verifier = new WorldAttestedAuthenticator(trustEntries: () => []);

        var challenge = verifier.NewChallenge();
        var proof = prover.Prove(challenge: challenge);

        Assert.False(condition: verifier.TryVerify(
            challenge: challenge,
            proof: proof,
            sourceAuthority: out var sourceAuthority
        ));
        Assert.Null(@object: sourceAuthority);
    }
    [Fact]
    public void AnAttestedAuthenticatorProvesAndVerifiesAgainstItsOwnPinnedTrustEntry() {
        var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: key.ExportSubjectPublicKeyInfo());
        var oracle = new LocalKeySigningOracle(
            key: key,
            subject: "authority-a",
            validity: TimeSpan.FromMinutes(value: 1)
        );
        var entry = new WorldAdmissionEntry(
            Domain: domain,
            Subject: "authority-a",
            Mode: WorldAdmissionTrustMode.SignsDirectly,
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            PublicKey: Convert.ToBase64String(inArray: key.ExportSubjectPublicKeyInfo()),
            Grants: []
        );
        var prover = new WorldAttestedAuthenticator(oracle: oracle);
        var verifier = new WorldAttestedAuthenticator(trustEntries: () => [entry]);

        var challenge = verifier.NewChallenge();
        var proof = prover.Prove(challenge: challenge);

        Assert.True(condition: verifier.TryVerify(
            challenge: challenge,
            proof: proof,
            sourceAuthority: out var sourceAuthority
        ));
        Assert.Equal(
            actual: sourceAuthority,
            expected: "authority-a"
        );
    }
    [Fact]
    public void DownstreamAckFrameRoundTripsThroughTheSharedResultGrammar() {
        var written = WriteResultSync(result: WorldSubmissionResult.Ack.Instance);

        Assert.True(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out var body,
            frame: written,
            kind: out var kind
        ));
        Assert.Equal(
            actual: kind,
            expected: WorldPeerWireFormat.DownstreamKind.Ack
        );
        Assert.True(condition: WorldPeerWireFormat.TryReadResult(
            kind: kind,
            body: body.Span,
            result: out var decoded,
            reason: out var reason
        ));
        Assert.Equal(
            actual: reason,
            expected: string.Empty
        );
        Assert.IsType<WorldSubmissionResult.Ack>(@object: decoded);
    }
    [Fact]
    public void DownstreamSessionFrameRoundTripsItsReply() {
        var reply = new SessionReply(
            Accepted: true,
            AssignedIndex: 3,
            Reason: "seated",
            RosterEcho: string.Empty
        );
        var written = WriteResultSync(result: new WorldSubmissionResult.Session(Reply: reply));

        Assert.True(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out var body,
            frame: written,
            kind: out var kind
        ));
        Assert.True(condition: WorldPeerWireFormat.TryReadResult(
            kind: kind,
            body: body.Span,
            result: out var decoded,
            reason: out _
        ));
        var session = Assert.IsType<WorldSubmissionResult.Session>(@object: decoded);

        Assert.Equal(
            (true, 3, "seated"),
            (session.Reply.Accepted, session.Reply.AssignedIndex, session.Reply.Reason)
        );
    }
    [Fact]
    public void JournalPageRoundTripsEveryEntryInAppendOrder() {
        IReadOnlyList<WorldMutationJournalEntry> entries = [
            new WorldMutationJournalEntry(
                Encoded: new byte[] { 1, 2, 3 },
                Tick: 1UL,
                EngineTick: 1UL
            ),
            new WorldMutationJournalEntry(
                Encoded: new byte[] { 4, 5 },
                Tick: 2UL,
                EngineTick: 2UL
            ),
        ];
        var encoded = WorldAuthorityStoreWireCodec.EncodeJournalPage(entries: entries);

        Assert.True(
            condition: WorldAuthorityStoreWireCodec.TryDecodeJournalPage(
                bytes: encoded,
                entries: out var decoded,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            2,
            decoded.Count
        );
        Assert.Equal(
            1UL,
            decoded[0].Tick
        );
        Assert.Equal(
            new byte[] { 1, 2, 3 },
            decoded[0].Encoded.ToArray()
        );
        Assert.Equal(
            2UL,
            decoded[1].Tick
        );
        Assert.Equal(
            new byte[] { 4, 5 },
            decoded[1].Encoded.ToArray()
        );
    }
    [Fact]
    public void LatestPointerRoundTripsAndRefusesAForeignMagic() {
        var encoded = WorldAuthorityStoreWireCodec.EncodeLatestPointer(
            hash: "abc123",
            ordinal: 7,
            tick: 12345UL
        );

        Assert.True(
            condition: WorldAuthorityStoreWireCodec.TryDecodeLatestPointer(
                bytes: encoded,
                hash: out var hash,
                ordinal: out var ordinal,
                reason: out var reason,
                tick: out var tick
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: (ordinal, tick, hash),
            expected: (7L, 12345UL, "abc123")
        );

        var corrupted = encoded.ToArray();

        corrupted[0] ^= 0xFF;
        Assert.False(condition: WorldAuthorityStoreWireCodec.TryDecodeLatestPointer(
            bytes: corrupted,
            hash: out _,
            ordinal: out _,
            reason: out var refusal,
            tick: out _
        ));
        Assert.NotEqual(
            actual: refusal,
            expected: string.Empty
        );
    }
}
