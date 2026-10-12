using System.Buffers;
using System.Numerics;
using Puck.Maths;

namespace Puck.Assets;

/// <summary>Sets allocation and integer-size ceilings for decoding untrusted automatic-sequence artifacts.</summary>
public sealed class AutomaticSequenceDecodeLimits {
    /// <summary>Initializes decoding ceilings.</summary>
    /// <param name="maximumArtifactBytes">The maximum complete artifact size, in bytes.</param>
    /// <param name="maximumBigIntegerBytes">The maximum magnitude size of one integer, in bytes.</param>
    /// <param name="maximumAlphabetSize">The maximum digit alphabet size.</param>
    /// <param name="maximumStateCount">The maximum DFAO state count.</param>
    /// <param name="maximumOutputCount">The maximum output-alphabet value count.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any ceiling is not positive.</exception>
    public AutomaticSequenceDecodeLimits(
        int maximumArtifactBytes = ((64 * 1024) * 1024),
        int maximumBigIntegerBytes = (1024 * 1024),
        int maximumAlphabetSize = 65_536,
        int maximumStateCount = 1_000_000,
        int maximumOutputCount = 65_536
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumArtifactBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBigIntegerBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAlphabetSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStateCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputCount);

        MaximumAlphabetSize = maximumAlphabetSize;
        MaximumArtifactBytes = maximumArtifactBytes;
        MaximumBigIntegerBytes = maximumBigIntegerBytes;
        MaximumOutputCount = maximumOutputCount;
        MaximumStateCount = maximumStateCount;
    }

    /// <summary>Gets the default decoding ceilings.</summary>
    public static AutomaticSequenceDecodeLimits Default { get; } = new();
    /// <summary>Gets the maximum digit alphabet size.</summary>
    public int MaximumAlphabetSize { get; }
    /// <summary>Gets the maximum complete artifact size, in bytes.</summary>
    public int MaximumArtifactBytes { get; }
    /// <summary>Gets the maximum magnitude size of one integer, in bytes.</summary>
    public int MaximumBigIntegerBytes { get; }
    /// <summary>Gets the maximum output-alphabet value count.</summary>
    public int MaximumOutputCount { get; }
    /// <summary>Gets the maximum DFAO state count.</summary>
    public int MaximumStateCount { get; }
}
/// <summary>Encodes and decodes the canonical versioned binary form of an <see cref="AutomaticIntegerSequence"/>.</summary>
public static class AutomaticIntegerSequenceCodec {
    private static ReadOnlySpan<byte> Magic => "PAIS"u8;

    private const byte Version = 1;

    private static RealQuadratic ReadSurd(ref CanonicalBinaryReader reader, int maximumBigIntegerBytes) =>
        RealQuadratic.Create(
            denominator: reader.ReadBigInteger(maximumByteCount: maximumBigIntegerBytes),
            radicand: reader.ReadBigInteger(maximumByteCount: maximumBigIntegerBytes),
            rationalNumerator: reader.ReadBigInteger(maximumByteCount: maximumBigIntegerBytes),
            surdNumerator: reader.ReadBigInteger(maximumByteCount: maximumBigIntegerBytes)
        );
    private static void WriteSurd(ArrayBufferWriter<byte> writer, RealQuadratic value) {
        writer.WriteBigInteger(value: value.Denominator);
        writer.WriteBigInteger(value: value.Radicand);
        writer.WriteBigInteger(value: value.RationalNumerator);
        writer.WriteBigInteger(value: value.SurdNumerator);
    }

    /// <summary>Decodes an untrusted canonical binary artifact under explicit allocation ceilings.</summary>
    /// <param name="content">The complete artifact bytes.</param>
    /// <param name="limits">The decoding ceilings, or <see langword="null"/> for <see cref="AutomaticSequenceDecodeLimits.Default"/>.</param>
    /// <returns>The structurally validated automatic integer sequence.</returns>
    /// <exception cref="InvalidDataException">The payload is malformed, noncanonical, unsupported, or exceeds a ceiling.</exception>
    public static AutomaticIntegerSequence Decode(
        ReadOnlySpan<byte> content,
        AutomaticSequenceDecodeLimits? limits = null
    ) {
        limits ??= AutomaticSequenceDecodeLimits.Default;
        if (content.Length > limits.MaximumArtifactBytes) {
            throw new InvalidDataException(message: "the automatic-sequence artifact exceeds the configured byte ceiling");
        }

        var reader = new CanonicalBinaryReader(content: content);

        reader.Expect(value: Magic);
        if (reader.ReadByte() != Version) {
            throw new InvalidDataException(message: "the automatic-sequence artifact version is unsupported");
        }

        try {
            var kind = ((IntegerNumerationKind)reader.ReadByte());
            IntegerNumerationSystem numeration;

            switch (kind) {
                case IntegerNumerationKind.Positional:
                    numeration = IntegerNumerationSystem.Positional(radix: reader.ReadBoundedInt(maximum: limits.MaximumAlphabetSize));
                    break;
                case IntegerNumerationKind.QuadraticOstrowski:
                    numeration = IntegerNumerationSystem.QuadraticOstrowski(basis: ReadSurd(
                        reader: ref reader,
                        maximumBigIntegerBytes: limits.MaximumBigIntegerBytes
                    ));
                    break;
                default:
                    throw new InvalidDataException(message: "the numeration kind is unsupported");
            }

            if (numeration.AlphabetSize > limits.MaximumAlphabetSize) {
                throw new InvalidDataException(message: "the numeration alphabet exceeds the configured ceiling");
            }

            var stateCount = reader.ReadBoundedInt(maximum: limits.MaximumStateCount);

            if (stateCount == 0) {
                throw new InvalidDataException(message: "an automatic sequence must contain at least one state");
            }
            var outputs = new int[stateCount];

            for (var state = 0; (state < stateCount); ++state) {
                outputs[state] = reader.ReadBoundedInt(maximum: limits.MaximumOutputCount);
            }

            var transitionCount = checked((stateCount * numeration.AlphabetSize));
            var transitions = new int[transitionCount];

            for (var index = 0; (index < transitionCount); ++index) {
                transitions[index] = reader.ReadBoundedInt(maximum: (stateCount - 1));
            }

            var outputCount = reader.ReadBoundedInt(maximum: limits.MaximumOutputCount);

            if (outputCount == 0) {
                throw new InvalidDataException(message: "the output alphabet cannot be empty");
            }
            var outputAlphabet = new BigInteger[outputCount];

            for (var index = 0; (index < outputCount); ++index) {
                outputAlphabet[index] = reader.ReadBigInteger(maximumByteCount: limits.MaximumBigIntegerBytes);
            }

            reader.ExpectEnd();
            var automaton = new DeterministicOutputAutomaton(
                alphabetSize: numeration.AlphabetSize,
                outputSymbols: outputs,
                transitions: transitions
            );

            if (automaton.StateCount != stateCount) {
                throw new InvalidDataException(message: "the encoded automaton contains unreachable states");
            }

            return new AutomaticIntegerSequence(
                automaton: automaton,
                numeration: numeration,
                outputAlphabet: outputAlphabet
            );
        } catch (InvalidDataException) {
            throw;
        } catch (Exception exception) when ((exception is ArgumentException or ArithmeticException or OverflowException)) {
            throw new InvalidDataException(
                innerException: exception,
                message: "the automatic-sequence artifact violates its structural contract"
            );
        }
    }
    /// <summary>Encodes an automatic integer sequence into its canonical version-one binary form.</summary>
    /// <param name="sequence">The sequence to encode.</param>
    /// <returns>The canonical bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sequence"/> is <see langword="null"/>.</exception>
    public static byte[] Encode(AutomaticIntegerSequence sequence) {
        ArgumentNullException.ThrowIfNull(sequence);
        var writer = new ArrayBufferWriter<byte>();

        writer.Write(value: Magic);
        writer.WriteByte(value: Version);
        writer.WriteByte(value: ((byte)sequence.Numeration.Kind));

        if (sequence.Numeration.Kind == IntegerNumerationKind.Positional) {
            writer.WriteVarUInt(value: checked((uint)sequence.Numeration.Radix));
        } else {
            WriteSurd(
                value: sequence.Numeration.Basis!.Value,
                writer: writer
            );
        }

        writer.WriteVarUInt(value: checked((uint)sequence.Automaton.StateCount));

        for (var state = 0; (state < sequence.Automaton.StateCount); ++state) {
            writer.WriteVarUInt(value: checked((uint)sequence.Automaton.OutputSymbol(state: state)));
        }

        for (var state = 0; (state < sequence.Automaton.StateCount); ++state) {
            for (var digit = 0; (digit < sequence.Automaton.AlphabetSize); ++digit) {
                writer.WriteVarUInt(value: checked((uint)sequence.Automaton.Transition(
                    digit: digit,
                    state: state
                )));
            }
        }

        writer.WriteVarUInt(value: checked((uint)sequence.OutputAlphabetSize));
        for (var symbol = 0; (symbol < sequence.OutputAlphabetSize); ++symbol) {
            writer.WriteBigInteger(value: sequence.OutputValue(symbol: symbol));
        }

        return writer.WrittenSpan.ToArray();
    }
}
