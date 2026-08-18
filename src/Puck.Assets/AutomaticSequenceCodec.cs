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
        int maximumArtifactBytes = 64 * 1024 * 1024,
        int maximumBigIntegerBytes = 1024 * 1024,
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
            var kind = (IntegerNumerationKind)reader.ReadByte();
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
        } catch (Exception exception) when (exception is ArgumentException or ArithmeticException or OverflowException) {
            throw new InvalidDataException(
                message: "the automatic-sequence artifact violates its structural contract",
                innerException: exception
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

        writer.Write(Magic);
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

    private static QuadraticSurd ReadSurd(ref CanonicalBinaryReader reader, int maximumBigIntegerBytes) =>
        QuadraticSurd.Create(
            denominator: reader.ReadBigInteger(maximumByteCount: maximumBigIntegerBytes),
            radicand: reader.ReadBigInteger(maximumByteCount: maximumBigIntegerBytes),
            rationalNumerator: reader.ReadBigInteger(maximumByteCount: maximumBigIntegerBytes),
            surdNumerator: reader.ReadBigInteger(maximumByteCount: maximumBigIntegerBytes)
        );

    private static void WriteSurd(ArrayBufferWriter<byte> writer, QuadraticSurd value) {
        writer.WriteBigInteger(value: value.Denominator);
        writer.WriteBigInteger(value: value.Radicand);
        writer.WriteBigInteger(value: value.RationalNumerator);
        writer.WriteBigInteger(value: value.SurdNumerator);
    }
}

internal static class CanonicalBinaryWriterExtensions {
    public static void WriteBigInteger(this ArrayBufferWriter<byte> writer, BigInteger value) {
        if (value.IsZero) {
            writer.WriteByte(value: 0);
            writer.WriteVarUInt(value: 0);
            return;
        }

        writer.WriteByte(value: ((value.Sign > 0) ? ((byte)1) : ((byte)2)));
        var magnitude = BigInteger.Abs(value: value);
        var byteCount = magnitude.GetByteCount(isUnsigned: true);
        writer.WriteVarUInt(value: checked((uint)byteCount));
        var destination = writer.GetSpan(sizeHint: byteCount)[..byteCount];

        if (!magnitude.TryWriteBytes(
            bytesWritten: out var written,
            destination: destination,
            isBigEndian: true,
            isUnsigned: true
        ) || (written != byteCount)) {
            throw new InvalidOperationException(message: "BigInteger did not write its canonical magnitude");
        }

        writer.Advance(count: byteCount);
    }

    public static void WriteByte(this ArrayBufferWriter<byte> writer, byte value) {
        var destination = writer.GetSpan(sizeHint: 1);
        destination[0] = value;
        writer.Advance(count: 1);
    }

    public static void WriteVarUInt(this ArrayBufferWriter<byte> writer, uint value) {
        do {
            var current = ((byte)(value & 0x7f));
            value >>= 7;
            if (value != 0) { current |= 0x80; }
            writer.WriteByte(value: current);
        } while (value != 0);
    }
}

internal ref struct CanonicalBinaryReader {
    private readonly ReadOnlySpan<byte> m_content;
    private int m_offset;

    public CanonicalBinaryReader(ReadOnlySpan<byte> content) {
        m_content = content;
    }

    public void Expect(ReadOnlySpan<byte> value) {
        if (
            ((m_content.Length - m_offset) < value.Length) ||
            !m_content.Slice(
                start: m_offset,
                length: value.Length
            ).SequenceEqual(other: value)
        ) {
            throw new InvalidDataException(message: "the artifact magic is invalid");
        }

        m_offset += value.Length;
    }

    public void ExpectEnd() {
        if (m_offset != m_content.Length) {
            throw new InvalidDataException(message: "the artifact contains trailing bytes");
        }
    }

    public BigInteger ReadBigInteger(int maximumByteCount) {
        var sign = ReadByte();
        var byteCount = ReadBoundedInt(maximum: maximumByteCount);

        if (byteCount == 0) {
            if (sign != 0) {
                throw new InvalidDataException(message: "zero has a nonzero sign marker");
            }
            return BigInteger.Zero;
        }
        if ((sign != 1) && (sign != 2)) {
            throw new InvalidDataException(message: "a nonzero integer has an invalid sign marker");
        }
        if ((m_content.Length - m_offset) < byteCount) {
            throw new InvalidDataException(message: "an integer magnitude is truncated");
        }

        var bytes = m_content.Slice(
            start: m_offset,
            length: byteCount
        );
        if (bytes[0] == 0) {
            throw new InvalidDataException(message: "an integer magnitude contains a leading zero byte");
        }

        m_offset += byteCount;
        var magnitude = new BigInteger(
            value: bytes,
            isBigEndian: true,
            isUnsigned: true
        );
        return ((sign == 1) ? magnitude : -magnitude);
    }

    public int ReadBoundedInt(int maximum) {
        var value = ReadVarUInt();
        if (value > ((uint)maximum)) {
            throw new InvalidDataException(message: "an artifact count exceeds its configured ceiling");
        }
        return checked((int)value);
    }

    public byte ReadByte() {
        if (m_offset >= m_content.Length) {
            throw new InvalidDataException(message: "the artifact is truncated");
        }
        return m_content[m_offset++];
    }

    private uint ReadVarUInt() {
        uint value = 0;

        for (var index = 0; (index < 5); ++index) {
            var current = ReadByte();
            if ((index == 4) && ((current & 0xf0) != 0)) {
                throw new InvalidDataException(message: "a variable-width integer overflows UInt32");
            }

            value |= (((uint)(current & 0x7f)) << (7 * index));
            if ((current & 0x80) != 0) { continue; }
            if ((index > 0) && ((current & 0x7f) == 0)) {
                throw new InvalidDataException(message: "a variable-width integer is not minimally encoded");
            }
            return value;
        }

        throw new InvalidDataException(message: "a variable-width integer is too long");
    }
}
