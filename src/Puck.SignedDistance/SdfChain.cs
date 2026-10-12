using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Puck.SignedDistance;

/// <summary>One instruction of a chain's key: its op and the header lanes that select the op's code
/// (<see cref="SdfOpRoles.CodeLanesOf"/>), each other lane zero.</summary>
/// <param name="Op">The op, never <see cref="SdfOp.ShapeBlend"/> or <see cref="SdfOp.PopField"/>, which end a chain.</param>
/// <param name="Shape">The shape lane when it selects code, otherwise zero.</param>
/// <param name="Blend">The blend lane when it selects code, otherwise zero.</param>
public readonly record struct SdfChainStep(SdfOp Op, uint Shape, uint Blend) {
    /// <summary>Returns the key step of an instruction: its op and its code-selecting lanes, its data dropped.</summary>
    /// <param name="instruction">The instruction, not a compose (<see cref="SdfOp.ShapeBlend"/> or
    /// <see cref="SdfOp.PopField"/>).</param>
    /// <returns>The step.</returns>
    /// <exception cref="InvalidOperationException">The instruction's op has no lane classification.</exception>
    public static SdfChainStep Of(in SdfInstruction instruction) {
        var lanes = SdfOpRoles.CodeLanesOf(op: instruction.Op);

        return new SdfChainStep(
            Blend: (lanes.HasFlag(flag: SdfInstructionLanes.Blend) ? instruction.Blend : 0u),
            Op: instruction.Op,
            Shape: (lanes.HasFlag(flag: SdfInstructionLanes.Shape) ? instruction.Shape : 0u)
        );
    }
}
/// <summary>How a chain ends.</summary>
public enum SdfChainEnd {
    /// <summary>At a <see cref="SdfOp.ShapeBlend"/>, whose shape type and blend belong to the vocabulary
    /// (<see cref="SdfSpecializationSet"/>), not to the chain.</summary>
    Shape,
    /// <summary>At a <see cref="SdfOp.PopField"/>, whose compose form belongs to the vocabulary.</summary>
    Pop,
    /// <summary>Where its segment range ends with no compose: before a <see cref="SdfOp.ResetPoint"/>, at an instance
    /// boundary, or at the end of the program.</summary>
    Cut,
}
/// <summary>
/// A chain: the run of instructions from the start of a segment range (<c>SdfProgram.SegmentRanges</c>: a
/// <see cref="SdfOp.ResetPoint"/> or an instance boundary), or from the previous compose, through the next compose. Its
/// key is each instruction's op and code-selecting lanes (<see cref="Steps"/>) and how it ends (<see cref="End"/>), never
/// its data, so a program keeps its chains through every edit that moves, scales or recolors what it draws. A segment of
/// the packed directory runs its chains in order; a merge of skippable ranges into one directory entry concatenates their
/// chains and never forms a new one.
/// <para><see cref="Identity"/> names the chain in 32 bits for the packed chain table; a program refuses two of its chains
/// that share one by name.</para>
/// </summary>
public sealed class SdfChain : IEquatable<SdfChain> {
    private readonly SdfChainStep[] m_steps;

    /// <summary>Initializes a new instance of the <see cref="SdfChain"/> class.</summary>
    /// <param name="steps">The key steps, in order; copied.</param>
    /// <param name="end">How the chain ends.</param>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A step is a compose, carries a lane that selects no code, or names an
    /// undefined op; <paramref name="end"/> is undefined; or the chain is empty and cut.</exception>
    public SdfChain(IReadOnlyList<SdfChainStep> steps, SdfChainEnd end) {
        ArgumentNullException.ThrowIfNull(argument: steps);

        if (!Enum.IsDefined(value: end)) {
            throw new ArgumentException(message: $"Chain end {end} is undefined.", paramName: nameof(end));
        }

        m_steps = [.. steps];

        foreach (var step in m_steps) {
            if (!Enum.IsDefined(value: step.Op) || (step.Op is SdfOp.ShapeBlend or SdfOp.PopField)) {
                throw new ArgumentException(message: $"A chain step cannot be {step.Op}: a chain holds the instructions before its compose.", paramName: nameof(steps));
            }

            var lanes = SdfOpRoles.CodeLanesOf(op: step.Op);

            if (
                ((step.Shape != 0u) && !lanes.HasFlag(flag: SdfInstructionLanes.Shape)) ||
                ((step.Blend != 0u) && !lanes.HasFlag(flag: SdfInstructionLanes.Blend))
            ) {
                throw new ArgumentException(message: $"A {step.Op} step carries a lane that selects no code; a chain's key holds no data.", paramName: nameof(steps));
            }
        }

        if ((m_steps.Length == 0) && (end == SdfChainEnd.Cut)) {
            throw new ArgumentException(message: "A cut chain holds at least one instruction.", paramName: nameof(steps));
        }

        End = end;
        Identity = IdentityOf(end: end, steps: m_steps);
    }

    /// <summary>Gets how the chain ends.</summary>
    public SdfChainEnd End { get; }
    /// <summary>Gets the chain's 32-bit identity: the first four bytes, little-endian, of the SHA-256 of its key (each
    /// step's op, shape lane and blend lane as little-endian 32-bit words, then <c>0x80000000 | End</c>). The same key has
    /// the same identity on every host.</summary>
    public uint Identity { get; }
    /// <summary>Gets the number of instructions the chain covers: its steps, and its compose unless it is cut.</summary>
    public int InstructionCount => (m_steps.Length + ((End == SdfChainEnd.Cut) ? 0 : 1));
    /// <summary>Gets the chain's key steps, in program order.</summary>
    public IReadOnlyList<SdfChainStep> Steps => m_steps;

    /// <inheritdoc/>
    public bool Equals(SdfChain? other) =>
        ((other is not null) && (End == other.End) && m_steps.AsSpan().SequenceEqual(other: other.m_steps));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(other: (obj as SdfChain));
    /// <inheritdoc/>
    public override int GetHashCode() => ((int)Identity);
    /// <summary>Returns the chain's name: each step's op, with its code-selecting lanes, then how it ends.</summary>
    /// <returns>The name, such as <c>ResetPoint Translate RotatePlane(shape 1, blend 0) | Shape</c>.</returns>
    public override string ToString() {
        var text = new StringBuilder();

        foreach (var step in m_steps) {
            var lanes = SdfOpRoles.CodeLanesOf(op: step.Op);

            text.Append(value: step.Op);

            if (lanes != SdfInstructionLanes.None) {
                text.Append(value: '(');

                if (lanes.HasFlag(flag: SdfInstructionLanes.Shape)) {
                    text.Append(provider: CultureInfo.InvariantCulture, handler: $"shape {step.Shape}");
                }

                if (lanes == (SdfInstructionLanes.Shape | SdfInstructionLanes.Blend)) {
                    text.Append(value: ", ");
                }

                if (lanes.HasFlag(flag: SdfInstructionLanes.Blend)) {
                    text.Append(provider: CultureInfo.InvariantCulture, handler: $"blend {step.Blend}");
                }

                text.Append(value: ')');
            }

            text.Append(value: ' ');
        }

        return text.Append(value: "| ").Append(value: End).ToString();
    }

    // The words a chain's key hashes: three a step, then the end word.
    internal static int KeyWords(int steps) => checked(((3 * steps) + 1));
    // Writes a chain's key words: each step's op, shape lane and blend lane, then 0x80000000 | end.
    internal static void WriteKey(ReadOnlySpan<SdfChainStep> steps, SdfChainEnd end, Span<uint> key) {
        for (var index = 0; (index < steps.Length); index++) {
            key[(3 * index)] = ((uint)steps[index].Op);
            key[((3 * index) + 1)] = steps[index].Shape;
            key[((3 * index) + 2)] = steps[index].Blend;
        }

        key[(3 * steps.Length)] = 0x80000000u | ((uint)end);
    }

    private static uint IdentityOf(ReadOnlySpan<SdfChainStep> steps, SdfChainEnd end) {
        var words = KeyWords(steps: steps.Length);
        var rented = ArrayPool<uint>.Shared.Rent(minimumLength: words);
        var bytes = ArrayPool<byte>.Shared.Rent(minimumLength: (4 * words));

        try {
            var key = rented.AsSpan(length: words, start: 0);

            WriteKey(end: end, key: key, steps: steps);

            for (var index = 0; (index < words); index++) {
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: (4 * index)), value: key[index]);
            }

            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];

            _ = SHA256.HashData(destination: hash, source: bytes.AsSpan(length: (4 * words), start: 0));

            return BinaryPrimitives.ReadUInt32LittleEndian(source: hash);
        } finally {
            ArrayPool<byte>.Shared.Return(array: bytes);
            ArrayPool<uint>.Shared.Return(array: rented);
        }
    }
}
