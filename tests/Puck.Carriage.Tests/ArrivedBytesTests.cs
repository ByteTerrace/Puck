using System.Reflection;
using System.Text;

using Xunit;

using static Puck.Carriage.Tests.CarriageTestSupport;

namespace Puck.Carriage.Tests;

public sealed class ArrivedBytesTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    private static (CborCarriageCodec Codec, DomainKeys Keys, TrustList Trust, SignedCarriageEnvelope Claim, byte[] Wire) BuildFixture() {
        var codec = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:jun");
        var trust = BuildDirectTrustList(keys: keys, reach: DefaultReach);
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "jun's claim");
        var wire = codec.EncodeEnvelope(envelope: claim);

        return (codec, keys, trust, claim, wire);
    }

    private static CarriageVerifyResult VerifyWire(CborCarriageCodec codec, TrustList trust, byte[] bytes) =>
        CarriageVerifier.VerifyChain(codec: codec, claim: codec.DecodeEnvelope(wire: bytes), chain: null, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

    [Fact]
    public void HonestlyEncodedEnvelope_DecodesAndVerifies() {
        var (codec, _, trust, _, wire) = BuildFixture();

        var result = VerifyWire(codec: codec, trust: trust, bytes: wire);

        AssertAccepted(result: result);
    }

    [Fact]
    public void RawSignedPortionConstruction_IsNotPublic() {
        var method = typeof(SignedCarriageEnvelope).GetMethod(name: nameof(SignedCarriageEnvelope.FromSignedPortion), bindingAttr: (BindingFlags.Public | BindingFlags.Static));

        Assert.Null(@object: method);
    }

    // The object-boundary attack the factory must fail closed against: a valid signature and its authentic
    // bytes, with a separately projected payload substituted for what the application would read after
    // verification.
    [Fact]
    public void SubstitutedPayloadProjection_CannotRideAValidSignedPortion() {
        var (codec, _, trust, claim, _) = BuildFixture();
        var forgedProjection = SignedCarriageEnvelope.FromSignedPortion(header: claim.Header, payloadKind: claim.PayloadKind, payloadBytes: Encoding.UTF8.GetBytes(s: "attacker-substituted payload"), signature: claim.Signature, signedPortion: claim.SignedPortion);

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: forgedProjection, chain: null, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertRefused(result: result, reasonMustContain: "parsed fields");
    }

    // ReadOnlyMemory is only a read-only VIEW; when it wraps a byte[], whoever owns that array can still
    // mutate it, so signing must take its own copy rather than retaining caller storage.
    [Fact]
    public void CallerOwnedPayloadStorage_CannotMutateASignedEnvelope() {
        var codec = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:jun");
        var callerOwnedPayload = Encoding.UTF8.GetBytes(s: "caller-owned payload");
        var callerOwnedControl = callerOwnedPayload.ToArray();
        var isolatedClaim = CarriageSigner.SignClaim(codec: codec, domain: keys.Domain, subject: keys.Subject, signerKey: keys.SubjectSigningKey, signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, claimBytes: callerOwnedPayload);

        callerOwnedPayload.AsSpan().Fill(value: 0xA5);

        Assert.True(condition: isolatedClaim.PayloadBytes.Span.SequenceEqual(other: callerOwnedControl));
    }

    [Fact]
    public void MutatingCallersSourceArray_LeavesTheIsolatedEnvelopeVerifiable() {
        var codec = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:jun");
        var trust = BuildDirectTrustList(keys: keys, reach: DefaultReach);
        var callerOwnedPayload = Encoding.UTF8.GetBytes(s: "caller-owned payload");
        var isolatedClaim = CarriageSigner.SignClaim(codec: codec, domain: keys.Domain, subject: keys.Subject, signerKey: keys.SubjectSigningKey, signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, claimBytes: callerOwnedPayload);

        callerOwnedPayload.AsSpan().Fill(value: 0xA5);

        var result = CarriageVerifier.VerifyChain(codec: codec, claim: isolatedClaim, chain: null, trustList: trust, now: Now, expectedPurpose: "test.claim", expectedAudience: "world:home");

        AssertAccepted(result: result);
    }

    [Fact]
    public void DecodedEnvelope_CarriesTheSignedPortionVerbatimNotAReencoding() {
        var (codec, _, _, claim, wire) = BuildFixture();

        var decoded = codec.DecodeEnvelope(wire: wire);

        Assert.True(condition: decoded.SignedPortion.Span.SequenceEqual(other: claim.SignedPortion.Span));
    }

    // The general property, checked rather than argued: every byte of a valid envelope is inside either the
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

                var mutated = (byte[])wire.Clone();

                mutated[offset] = (byte)value;

                try {
                    var result = VerifyWire(codec: codec, trust: trust, bytes: mutated);

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
