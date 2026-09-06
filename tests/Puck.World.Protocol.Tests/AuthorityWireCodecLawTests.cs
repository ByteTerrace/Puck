using System.Security.Cryptography;

using Puck.Attestation;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>The blob-store wire shapes, the peer downstream reply grammar, and the federation identity door all
/// hold no <c>Puck.World.Server</c> reference — this suite proves each round-trips from this project alone.</summary>
public sealed class AuthorityWireCodecLawTests {
    [Fact]
    public void LatestPointerRoundTripsAndRefusesAForeignMagic() {
        var encoded = WorldAuthorityStoreWireCodec.EncodeLatestPointer(ordinal: 7, tick: 12345UL, hash: "abc123");

        Assert.True(WorldAuthorityStoreWireCodec.TryDecodeLatestPointer(bytes: encoded, ordinal: out var ordinal, tick: out var tick, hash: out var hash, reason: out var reason), reason);
        Assert.Equal((7L, 12345UL, "abc123"), (ordinal, tick, hash));

        var corrupted = encoded.ToArray();
        corrupted[0] ^= 0xFF;
        Assert.False(WorldAuthorityStoreWireCodec.TryDecodeLatestPointer(bytes: corrupted, ordinal: out _, tick: out _, hash: out _, reason: out var refusal));
        Assert.NotEqual(string.Empty, refusal);
    }

    [Fact]
    public void JournalPageRoundTripsEveryEntryInAppendOrder() {
        IReadOnlyList<WorldMutationJournalEntry> entries = [
            new WorldMutationJournalEntry(Tick: 1UL, Encoded: new byte[] { 1, 2, 3 }),
            new WorldMutationJournalEntry(Tick: 2UL, Encoded: new byte[] { 4, 5 }),
        ];
        var encoded = WorldAuthorityStoreWireCodec.EncodeJournalPage(entries: entries);

        Assert.True(WorldAuthorityStoreWireCodec.TryDecodeJournalPage(bytes: encoded, entries: out var decoded, reason: out var reason), reason);
        Assert.Equal(2, decoded.Count);
        Assert.Equal(1UL, decoded[0].Tick);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded[0].Encoded.ToArray());
        Assert.Equal(2UL, decoded[1].Tick);
        Assert.Equal(new byte[] { 4, 5 }, decoded[1].Encoded.ToArray());
    }

    [Fact]
    public void DownstreamAckFrameRoundTripsThroughTheSharedResultGrammar() {
        var written = WriteResultSync(result: WorldSubmissionResult.Ack.Instance);

        Assert.True(WorldPeerWireFormat.TryDecodeDownstream(frame: written, kind: out var kind, body: out var body));
        Assert.Equal(WorldPeerWireFormat.DownstreamKind.Ack, kind);
        Assert.True(WorldPeerWireFormat.TryReadResult(kind: kind, body: body.Span, result: out var decoded, reason: out var reason));
        Assert.Equal(string.Empty, reason);
        Assert.IsType<WorldSubmissionResult.Ack>(decoded);
    }

    [Fact]
    public void DownstreamSessionFrameRoundTripsItsReply() {
        var reply = new SessionReply(Accepted: true, AssignedIndex: 3, RosterEcho: string.Empty, Reason: "seated");
        var written = WriteResultSync(result: new WorldSubmissionResult.Session(Reply: reply));

        Assert.True(WorldPeerWireFormat.TryDecodeDownstream(frame: written, kind: out var kind, body: out var body));
        Assert.True(WorldPeerWireFormat.TryReadResult(kind: kind, body: body.Span, result: out var decoded, reason: out _));
        var session = Assert.IsType<WorldSubmissionResult.Session>(decoded);
        Assert.Equal((true, 3, "seated"), (session.Reply.Accepted, session.Reply.AssignedIndex, session.Reply.Reason));
    }

    [Fact]
    public void ATruncatedFrameNeverDecodes() {
        Assert.False(WorldPeerWireFormat.TryDecodeDownstream(frame: new byte[] { 1, 2 }, kind: out _, body: out _));
    }

    private static byte[] WriteResultSync(WorldSubmissionResult result) {
        using var stream = new MemoryStream();
        WorldPeerWireFormat.WriteResultAsync(stream: stream, result: result, ct: CancellationToken.None).GetAwaiter().GetResult();

        return stream.ToArray();
    }

    [Fact]
    public void AnAttestedAuthenticatorProvesAndVerifiesAgainstItsOwnPinnedTrustEntry() {
        var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: key.ExportSubjectPublicKeyInfo());
        var oracle = new LocalKeySigningOracle(key: key, subject: "authority-a", validity: TimeSpan.FromMinutes(value: 1));
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

        Assert.True(verifier.TryVerify(challenge: challenge, proof: proof, sourceAuthority: out var sourceAuthority));
        Assert.Equal("authority-a", sourceAuthority);
    }

    [Fact]
    public void AVerifierWithNoMatchingTrustEntryRefuses() {
        var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var oracle = new LocalKeySigningOracle(key: key, subject: "authority-a", validity: TimeSpan.FromMinutes(value: 1));
        var prover = new WorldAttestedAuthenticator(oracle: oracle);
        var verifier = new WorldAttestedAuthenticator(trustEntries: () => []);

        var challenge = verifier.NewChallenge();
        var proof = prover.Prove(challenge: challenge);

        Assert.False(verifier.TryVerify(challenge: challenge, proof: proof, sourceAuthority: out var sourceAuthority));
        Assert.Null(sourceAuthority);
    }

    [Fact]
    public void AReplayCodecExceptionCarriesItsMessageAndStandsApartFromInvalidOperationException() {
        var exception = new WorldReplayCodecException(message: "an authority-entry kind the tape codec cannot represent");

        Assert.Equal("an authority-entry kind the tape codec cannot represent", exception.Message);
        Assert.IsNotType<InvalidOperationException>(exception);
    }
}
