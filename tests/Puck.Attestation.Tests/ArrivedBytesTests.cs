using System.Reflection;
using System.Text;

using Xunit;

using static Puck.Attestation.Tests.AttestationTestSupport;

namespace Puck.Attestation.Tests;

public sealed class ArrivedBytesTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    private static (CborAttestationCodec Codec, DomainKeys Keys, TrustList Trust, SignedAttestation Claim, byte[] Wire) BuildFixture() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:jun");
        var trust = BuildDirectTrustList(keys: keys, reach: DefaultReach);
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "jun's claim");
        var wire = codec.EncodeAttestation(attestation: claim);

        return (codec, keys, trust, claim, wire);
    }
    private static AttestationVerifyResult VerifyWire(CborAttestationCodec codec, TrustList trust, byte[] bytes) =>
        AttestationVerifier.VerifyChain(codec: codec, claim: codec.DecodeAttestation(wire: bytes), chain: null, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

    [Fact]
    public void HonestlyEncodedAttestation_DecodesAndVerifies() {
        var (codec, _, trust, _, wire) = BuildFixture();

        var result = VerifyWire(bytes: wire, codec: codec, trust: trust);

        AssertAccepted(result: result);
    }
    [Fact]
    public void RawSignedPortionConstruction_IsNotPublic() {
        var method = typeof(SignedAttestation).GetMethod(name: nameof(SignedAttestation.FromSignedPortion), bindingAttr: BindingFlags.Public | BindingFlags.Static);

        Assert.Null(@object: method);
    }
    // The object-boundary attack the factory must fail closed against: a valid signature and its authentic
    // bytes, with a separately projected payload substituted for what the application would read after
    // verification.
    [Fact]
    public void SubstitutedPayloadProjection_CannotRideAValidSignedPortion() {
        var (codec, _, trust, claim, _) = BuildFixture();
        var forgedProjection = SignedAttestation.FromSignedPortion(header: claim.Header, payloadKind: claim.PayloadKind, payloadBytes: Encoding.UTF8.GetBytes(s: "attacker-substituted payload"), signature: claim.Signature, signedPortion: claim.SignedPortion);

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: forgedProjection, chain: null, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(reasonMustContain: "parsed fields", result: result);
    }
    // ReadOnlyMemory is only a read-only VIEW; when it wraps a byte[], whoever owns that array can still
    // mutate it, so signing must take its own copy rather than retaining caller storage.
    [Fact]
    public void CallerOwnedPayloadStorage_CannotMutateASignedAttestation() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:jun");
        var callerOwnedPayload = Encoding.UTF8.GetBytes(s: "caller-owned payload");
        var callerOwnedControl = callerOwnedPayload.ToArray();
        var isolatedClaim = AttestationSigner.SignClaim(codec: codec, domain: keys.Domain, subject: keys.Subject, signerKey: keys.SubjectSigningKey, signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, claimBytes: callerOwnedPayload);

        callerOwnedPayload.AsSpan().Fill(value: 0xA5);

        Assert.True(condition: isolatedClaim.PayloadBytes.Span.SequenceEqual(other: callerOwnedControl));
    }
    [Fact]
    public void MutatingCallersSourceArray_LeavesTheIsolatedAttestationVerifiable() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:jun");
        var trust = BuildDirectTrustList(keys: keys, reach: DefaultReach);
        var callerOwnedPayload = Encoding.UTF8.GetBytes(s: "caller-owned payload");
        var isolatedClaim = AttestationSigner.SignClaim(codec: codec, domain: keys.Domain, subject: keys.Subject, signerKey: keys.SubjectSigningKey, signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, claimBytes: callerOwnedPayload);

        callerOwnedPayload.AsSpan().Fill(value: 0xA5);

        var result = AttestationVerifier.VerifyChain(codec: codec, claim: isolatedClaim, chain: null, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertAccepted(result: result);
    }
    [Fact]
    public void DecodedAttestation_CarriesTheSignedPortionVerbatimNotAReencoding() {
        var (codec, _, _, claim, wire) = BuildFixture();

        var decoded = codec.DecodeAttestation(wire: wire);

        Assert.True(condition: decoded.SignedPortion.Span.SequenceEqual(other: claim.SignedPortion.Span));
    }
    // The general property, checked rather than argued: every byte of a valid attestation is inside either the
    // signed portion or the signature, so no single-byte change can produce an accepted claim.
    [Fact]
    public void SingleByteMutationSweep_NoMutationIsAcceptedOrCrashes() {
        var (codec, _, trust, _, wire) = BuildFixture();
        var accepted = new List<string>();
        var crashed = new List<string>();

        for (var offset = 0; (offset < wire.Length); offset += 1) {
            for (var value = 0; (value < 256); value += 1) {
                if (value == wire[offset]) {
                    continue;
                }

                var mutated = ((byte[])wire.Clone());

                mutated[offset] = ((byte)value);

                try {
                    var result = VerifyWire(bytes: mutated, codec: codec, trust: trust);

                    if (result.Verified) {
                        accepted.Add(item: $"offset {offset} = 0x{value:X2}");
                    }
                } catch (FormatException) {
                    // Expected: refused at decode.
                } catch (Exception exception) {
                    crashed.Add(item: $"offset {offset} = 0x{value:X2} threw {exception.GetType().Name}");
                }
            }
        }

        Assert.Empty(collection: accepted);
        Assert.Empty(collection: crashed);
    }
}
