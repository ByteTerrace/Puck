using Xunit;

using static Puck.Carriage.Tests.CarriageTestSupport;

namespace Puck.Carriage.Tests;

public sealed class ParserLaxityTests {
    private static byte[] BuildWellFormedWire(CborCarriageCodec codec) {
        var keys = MintDomainKeys(subject: "user:hana");
        var claim = SignTestClaim(codec: codec, keys: keys, purpose: "test.claim", notBefore: (Epoch - 60), notAfter: (Epoch + 3_600), audience: "world:home", sequence: null, text: "a well-formed claim");

        return codec.EncodeEnvelope(envelope: claim);
    }

    [Fact]
    public void HonestlyEncodedEnvelope_Decodes() {
        var codec = new CborCarriageCodec();
        var wire = BuildWellFormedWire(codec: codec);

        var exception = Record.Exception(testCode: () => _ = codec.DecodeEnvelope(wire: wire));

        Assert.Null(@object: exception);
    }

    [Fact]
    public void TrailingGarbage_OneByteAppended_Refuses() {
        var codec = new CborCarriageCodec();
        var wire = BuildWellFormedWire(codec: codec);

        var exception = Assert.Throws<FormatException>(testCode: () => _ = codec.DecodeEnvelope(wire: [.. wire, 0x00]));

        Assert.Contains(expectedSubstring: "trailing", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    // Every proper prefix of a valid envelope must refuse, and must refuse as a FormatException rather than
    // by indexing off the end — the "never trust a length beyond the bytes that arrived" claim, checked
    // over every possible truncation instead of asserted for one.
    [Fact]
    public void TruncationSweep_EveryProperPrefixRefusesAsFormatException() {
        var codec = new CborCarriageCodec();
        var wire = BuildWellFormedWire(codec: codec);
        var misbehaved = new List<string>();

        for (var length = 0; (length < wire.Length); length += 1) {
            try {
                _ = codec.DecodeEnvelope(wire: wire.AsSpan(start: 0, length: length));
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
        var codec = new CborCarriageCodec();
        var wire = BuildWellFormedWire(codec: codec);
        var indefiniteWire = BuildIndefiniteLengthEnvelope(wire: wire);

        var exception = Assert.Throws<FormatException>(testCode: () => _ = codec.DecodeEnvelope(wire: indefiniteWire));

        Assert.Contains(expectedSubstring: "indefinite-length", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    // 0x82 (array, 2 elements, minimally encoded) rewritten as 0x98 0x02 (array, count in a following byte).
    // Well-formed CBOR, accepted by Strict conformance, and a different byte string for the same envelope.
    [Fact]
    public void NonCanonical_OuterArrayLengthWrittenNonMinimally_Refuses() {
        var codec = new CborCarriageCodec();
        var wire = BuildWellFormedWire(codec: codec);
        byte[] nonMinimalWire = [0x98, 0x02, .. wire[1..]];

        var exception = Assert.Throws<FormatException>(testCode: () => _ = codec.DecodeEnvelope(wire: nonMinimalWire));

        Assert.Contains(expectedSubstring: "not canonically encoded", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FingerprintWidth_ThirtyOneByteDomain_Refuses() {
        var codec = new CborCarriageCodec();

        var exception = Assert.Throws<FormatException>(testCode: () => _ = codec.DecodeEnvelope(wire: BuildHandWrittenEnvelope(domainWidth: 31)));

        Assert.Contains(expectedSubstring: "fingerprint field is exactly 32", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FingerprintWidth_ThirtyTwoByteDomainControl_Decodes() {
        var codec = new CborCarriageCodec();

        var exception = Record.Exception(testCode: () => _ = codec.DecodeEnvelope(wire: BuildHandWrittenEnvelope()));

        Assert.Null(@object: exception);
    }

    // 258 truncates to 2 (key binding) in a byte-wide model, so a decoder that casts rather than checks
    // would hand the verifier a chain hop dressed as a claim.
    [Fact]
    public void PayloadKindOutOfRange_ValueTruncatingToALegitimateKind_Refuses() {
        var codec = new CborCarriageCodec();

        var exception = Assert.Throws<FormatException>(testCode: () => _ = codec.DecodeEnvelope(wire: BuildHandWrittenEnvelope(payloadKind: 258UL)));

        Assert.Contains(expectedSubstring: "outside the closed set", actualString: exception.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
}
