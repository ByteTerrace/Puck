using System.Numerics;
using Puck.Maths;
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
            expected: encoded,
            actual: reencoded
        );
        Assert.Equal(
            expected: ContentAddressedStore.ComputeHash(content: encoded),
            actual: ContentAddressedStore.ComputeHash(content: reencoded)
        );
        Assert.Equal(
            expected: "4ac441487cdcc97eaf5e534c2bd116ea83985a1a8859aada2b374dcfffd1c004",
            actual: ContentAddressedStore.ComputeHash(content: encoded)
        );

        for (ulong index = 0; (index < 4096); ++index) {
            Assert.Equal(
                expected: original.ValueAt(index: index),
                actual: decoded.ValueAt(index: index)
            );
        }
    }

    [Fact]
    public void QuadraticOstrowskiSequenceRoundTrips() {
        var numeration = IntegerNumerationSystem.QuadraticOstrowski(basis: QuadraticSurd.Create(
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
        var decoded = AutomaticIntegerSequenceCodec.Decode(
            content: AutomaticIntegerSequenceCodec.Encode(sequence: original)
        );

        foreach (var index in new BigInteger[] { 0, 1, 2, 3, 55, 65_535, BigInteger.Pow(value: 10, exponent: 80) }) {
            Assert.Equal(
                expected: original.ValueAt(index: index),
                actual: decoded.ValueAt(index: index)
            );
        }
    }

    [Fact]
    public void DecoderRejectsTrailingBytesAndCeilingBreaches() {
        var encoded = AutomaticIntegerSequenceCodec.Encode(sequence: BinaryParitySequence());
        var withTrailingByte = new byte[encoded.Length + 1];

        encoded.CopyTo(array: withTrailingByte, index: 0);
        Assert.Throws<InvalidDataException>(() => AutomaticIntegerSequenceCodec.Decode(content: withTrailingByte));
        Assert.Throws<InvalidDataException>(() => AutomaticIntegerSequenceCodec.Decode(
            content: encoded,
            limits: new AutomaticSequenceDecodeLimits(maximumArtifactBytes: (encoded.Length - 1))
        ));
    }
}
