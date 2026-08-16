using Xunit;

using static Puck.Attestation.Tests.AttestationTestSupport;

namespace Puck.Attestation.Tests;

public sealed class ParserLaxityTests {
    private static byte[] BuildWellFormedWire(CborAttestationCodec codec) {
        var keys = MintDomainKeys(subject: "user:hana");
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "a well-formed claim");

        return codec.EncodeAttestation(attestation: claim);
    }

    [Fact]
    public void HonestlyEncodedAttestation_Decodes() {
        var codec = new CborAttestationCodec();
        var wire = BuildWellFormedWire(codec: codec);

        var exception = Record.Exception(testCode: () => _ = codec.DecodeAttestation(wire: wire));

        Assert.Null(@object: exception);
    }
    [Fact]
    public void TrailingGarbage_OneByteAppended_Refuses() {
        var codec = new CborAttestationCodec();
        var wire = BuildWellFormedWire(codec: codec);

        var exception = Assert.Throws<FormatException>(testCode: () => _ = codec.DecodeAttestation(wire: [.. wire, 0x00]));

        Assert.Contains(expectedSubstring: "trailing", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
    // Every proper prefix of a valid attestation must refuse, and must refuse as a FormatException rather than
    // by indexing off the end — the "never trust a length beyond the bytes that arrived" claim, checked
    // over every possible truncation instead of asserted for one.
    [Fact]
    public void TruncationSweep_EveryProperPrefixRefusesAsFormatException() {
        var codec = new CborAttestationCodec();
        var wire = BuildWellFormedWire(codec: codec);
        var misbehaved = new List<string>();

        for (var length = 0; (length < wire.Length); length += 1) {
            try {
                _ = codec.DecodeAttestation(wire: wire.AsSpan(length: length, start: 0));
                misbehaved.Add(item: $"length {length} decoded instead of refusing");
            } catch (FormatException) {
                // Expected.
            } catch (Exception exception) {
                misbehaved.Add(item: $"length {length} threw {exception.GetType().Name}");
            }
        }

        Assert.Empty(collection: misbehaved);
    }
    [Fact]
    public void NonCanonical_OuterArrayReencodedAsIndefiniteLength_Refuses() {
        var codec = new CborAttestationCodec();
        var wire = BuildWellFormedWire(codec: codec);
        var indefiniteWire = BuildIndefiniteLengthAttestation(wire: wire);

        var exception = Assert.Throws<FormatException>(testCode: () => _ = codec.DecodeAttestation(wire: indefiniteWire));

        Assert.Contains(expectedSubstring: "indefinite-length", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
    // 0x82 (array, 2 elements, minimally encoded) rewritten as 0x98 0x02 (array, count in a following byte).
    // Well-formed CBOR, accepted by Strict conformance, and a different byte string for the same attestation.
    [Fact]
    public void NonCanonical_OuterArrayLengthWrittenNonMinimally_Refuses() {
        var codec = new CborAttestationCodec();
        var wire = BuildWellFormedWire(codec: codec);
        byte[] nonMinimalWire = [0x98, 0x02, .. wire[1..]];

        var exception = Assert.Throws<FormatException>(testCode: () => _ = codec.DecodeAttestation(wire: nonMinimalWire));

        Assert.Contains(expectedSubstring: "not canonically encoded", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void FingerprintWidth_ThirtyOneByteDomain_Refuses() {
        var codec = new CborAttestationCodec();

        var exception = Assert.Throws<FormatException>(testCode: () => _ = codec.DecodeAttestation(wire: BuildHandWrittenAttestation(domainWidth: 31)));

        Assert.Contains(expectedSubstring: "fingerprint field is exactly 32", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void FingerprintWidth_ThirtyTwoByteDomainControl_Decodes() {
        var codec = new CborAttestationCodec();

        var exception = Record.Exception(testCode: () => _ = codec.DecodeAttestation(wire: BuildHandWrittenAttestation()));

        Assert.Null(@object: exception);
    }
    // 258 truncates to 2 (key binding) in a byte-wide model, so a decoder that casts rather than checks
    // would hand the verifier a chain hop dressed as a claim.
    [Fact]
    public void PayloadKindOutOfRange_ValueTruncatingToALegitimateKind_Refuses() {
        var codec = new CborAttestationCodec();

        var exception = Assert.Throws<FormatException>(testCode: () => _ = codec.DecodeAttestation(wire: BuildHandWrittenAttestation(payloadKind: 258UL)));

        Assert.Contains(expectedSubstring: "outside the closed set", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
}
