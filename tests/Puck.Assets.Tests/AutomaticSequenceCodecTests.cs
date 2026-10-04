using System.Numerics;
using Puck.Maths;
using Puck.Testing;
using Xunit;

namespace Puck.Assets.Tests;

public sealed class AutomaticSequenceCodecTests {
    private static AutomaticIntegerSequence BinaryParitySequence() {
        var numeration = IntegerNumerationSystem.Positional(radix: 2);

        return new AutomaticIntegerSequence(
            automaton: new DeterministicOutputAutomaton(
                alphabetSize: 2,
                outputSymbols: [0, 1],
                transitions: [0, 1, 1, 0]
            ),
            numeration: numeration,
            outputAlphabet: [-BigInteger.One, BigInteger.One]
        );
    }

    [Fact]
    public void AutomaticSequenceRoundTripsCanonically() {
        var original = BinaryParitySequence();
        var encoded = AutomaticIntegerSequenceCodec.Encode(sequence: original);
        var decoded = AutomaticIntegerSequenceCodec.Decode(content: encoded);
        var reencoded = AutomaticIntegerSequenceCodec.Encode(sequence: decoded);

        Assert.Equal(
            actual: reencoded,
            expected: encoded
        );
        Assert.Equal(
            expected: ContentPin.Compute(content: encoded).Hex,
            actual: ContentPin.Compute(content: reencoded).Hex
        );
        // The artifact's header is the magic, the version byte, then the shape fingerprint the ledger records for this
        // codec; the pin covers every other byte, so it moves with the layout's bytes and never with the fingerprint.
        Assert.Equal(
            expected: FormatLedgerShapes.Of(id: "AutomaticIntegerSequenceCodec.Version"),
            actual: System.Text.Encoding.ASCII.GetString(bytes: encoded.AsSpan(
                length: 16,
                start: 5
            ))
        );
        Assert.Equal(
            expected: "4ac441487cdcc97eaf5e534c2bd116ea83985a1a8859aada2b374dcfffd1c004",
            actual: ContentPin.Compute(content: [.. encoded[..5], .. encoded[21..]]).Hex
        );

        for (ulong index = 0; (index < 4096); ++index) {
            Assert.Equal(
                expected: original.ValueAt(index: index),
                actual: decoded.ValueAt(index: index)
            );
        }
    }
    // The same magic and version under another shape fingerprint is refused by the fingerprint's name before any field is read.
    [Fact]
    public void AnArtifactOfTheSameVersionAndAnotherShapeIsRefusedByItsFingerprint() {
        var encoded = AutomaticIntegerSequenceCodec.Encode(sequence: BinaryParitySequence());
        var expected = System.Text.Encoding.ASCII.GetString(bytes: encoded.AsSpan(
            length: 16,
            start: 5
        ));
        var other = ((expected[0] == '0') ? ('1' + expected[1..]) : ('0' + expected[1..]));

        System.Text.Encoding.ASCII.GetBytes(
            bytes: encoded.AsSpan(start: 5),
            chars: other
        );

        var refusal = Assert.Throws<InvalidDataException>(testCode: () => AutomaticIntegerSequenceCodec.Decode(content: encoded));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: $"another shape than {expected}"
        );
    }
    [Fact]
    public void DecoderRejectsTrailingBytesAndCeilingBreaches() {
        var encoded = AutomaticIntegerSequenceCodec.Encode(sequence: BinaryParitySequence());
        var withTrailingByte = new byte[(encoded.Length + 1)];

        encoded.CopyTo(
            array: withTrailingByte,
            index: 0
        );
        Assert.Throws<InvalidDataException>(testCode: () => AutomaticIntegerSequenceCodec.Decode(content: withTrailingByte));
        Assert.Throws<InvalidDataException>(testCode: () => AutomaticIntegerSequenceCodec.Decode(
            content: encoded,
            limits: new AutomaticSequenceDecodeLimits(maximumArtifactBytes: (encoded.Length - 1))
        ));
    }
    [Fact]
    public void QuadraticOstrowskiSequenceRoundTrips() {
        var numeration = IntegerNumerationSystem.QuadraticOstrowski(basis: RealQuadratic.Create(
            denominator: 1,
            radicand: 2,
            rationalNumerator: 0,
            surdNumerator: 1
        ));
        var original = new AutomaticIntegerSequence(
            automaton: new DeterministicOutputAutomaton(
                alphabetSize: numeration.AlphabetSize,
                outputSymbols: [0, 1],
                transitions: [0, 1, 0, 1, 0, 1]
            ),
            numeration: numeration,
            outputAlphabet: [BigInteger.Zero, BigInteger.One]
        );
        var decoded = AutomaticIntegerSequenceCodec.Decode(content: AutomaticIntegerSequenceCodec.Encode(sequence: original));

        foreach (var index in new BigInteger[] { 0, 1, 2, 3, 55, 65_535, BigInteger.Pow(
            exponent: 80,
            value: 10
        ) }) {
            Assert.Equal(
                expected: original.ValueAt(index: index),
                actual: decoded.ValueAt(index: index)
            );
        }
    }
}
