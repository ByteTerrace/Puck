using System.Buffers;
using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// Systematic Reed–Solomon coding over <see cref="BinaryField{T}"/>: the generator polynomial whose roots are
/// consecutive powers of a chosen element, the check symbols a message's division by it leaves behind, and the
/// syndromes that read a codeword back.
/// </summary>
/// <remarks>
/// <para>
/// A code is named by three data rather than by an object: the field, the element whose consecutive powers are the
/// generator's roots, and the exponent the run of roots starts at. Everything else is a span the caller owns, so a
/// consumer that encodes the same shape repeatedly builds its generator once and keeps it, and nothing here holds a
/// cache, a lock, or a class initializer. The symbol order is highest-order coefficient first throughout — generator,
/// message, check symbols, and the codeword the syndromes read — so a systematic codeword is the message span followed
/// by the check span with no reversal anywhere.
/// </para>
/// <para>
/// The division's inner loop is one <see cref="BinaryField{T}.MultiplyAccumulateRegion"/> per message symbol over the
/// generator's tail, so a code whose check-symbol count is long enough to fill a vector rides the region ladder without
/// this type knowing which rung ran. Nothing is allocated on the managed heap: the working buffer is stack-allocated
/// for short codewords and pooled for long ones, and a pooled buffer is cleared before it re-enters the shared pool.
/// </para>
/// <para>
/// Correctness rests on the roots being distinct, which holds when <c>rootBase</c>'s multiplicative order exceeds the
/// largest root exponent the generator uses; a primitive element gives the longest code the field admits. The
/// precondition is documented rather than enforced, the same posture <see cref="BinaryField{T}"/> takes toward
/// irreducibility, and for the same reason: the test costs more than the operation and a caller that already chose its
/// element would pay for it on every call.
/// </para>
/// </remarks>
public static class ReedSolomon {
    /// <summary>The codeword length above which the division's working buffer is pooled rather than stack-allocated.</summary>
    private const int StackThreshold = 512;

    /// <summary>Builds the generator polynomial whose roots are a consecutive run of powers of one field element.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="field">The field the coefficients live in.</param>
    /// <param name="rootBase">The element whose consecutive powers are the roots; its multiplicative order must exceed the largest root exponent used.</param>
    /// <param name="firstRootExponent">The exponent the run of roots starts at, which is zero for the common convention and one for the other.</param>
    /// <param name="generator">Receives the coefficients, highest-order first, of <c>∏(t + rootBase^(firstRootExponent + i))</c> for <c>i</c> below the polynomial's degree; its length is one more than that degree, and its first element is always <see cref="BinaryField{T}.One"/>.</param>
    /// <remarks>
    /// The product is accumulated in place, one root at a time, each pass widening the polynomial leftward by one
    /// coefficient. In characteristic two subtraction is addition, so the factor for a root is <c>t + root</c> rather
    /// than <c>t - root</c> and no sign is carried anywhere. The cost is quadratic in the degree, which is why a
    /// consumer that encodes repeatedly builds its generators once.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="generator"/> holds fewer than two coefficients, so it names no polynomial of degree one or more, or <paramref name="firstRootExponent"/> is negative.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="field"/> is default-initialized and names no field.</exception>
    public static void BuildGenerator<T>(BinaryField<T> field, T rootBase, int firstRootExponent, Span<T> generator)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> {
        ArgumentOutOfRangeException.ThrowIfNegative(value: firstRootExponent);

        if (2 > generator.Length) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(generator),
                actualValue: generator.Length,
                message: "The generator must hold at least two coefficients; a Reed-Solomon generator has degree one or more."
            );
        }

        var count = generator.Length;
        var degree = (count - 1);
        var fieldDegree = field.Degree;
        var fieldTail = field.ReductionTail;

        // The accumulator starts as the constant polynomial 1, parked at the far right so every pass can widen leftward
        // into slots that are already zero. Reading field.One is also the guard that refuses a default-initialized field.
        generator.Clear();
        generator[degree] = field.One;

        for (var index = 0; (index < degree); index++) {
            var root = field.Exponentiate(value: rootBase, exponent: ((ulong)(firstRootExponent + index)));

            // Multiplying by (t + root) sends coefficient c_m to c_m·root + c_(m-1), and c_(m-1) is exactly the slot
            // being written while c_m sits one slot to its right. Sweeping left to right therefore reads each old
            // coefficient before it is overwritten, so the widening needs no second buffer. The two ends fall out of the
            // same expression: the new leading slot still holds zero, so its scaled term vanishes, and the slot past the
            // old constant term reads as zero, so the last write is the constant term times the root.
            for (var slot = ((degree - index) - 1); (slot < count); slot++) {
                var shifted = (((slot + 1) < count) ? generator[(slot + 1)] : T.Zero);

                generator[slot] = shifted ^ BinaryFieldKernels.Multiply(
                    left: generator[slot],
                    right: root,
                    degree: fieldDegree,
                    tail: fieldTail
                );
            }
        }
    }

    /// <summary>Computes a message's check symbols — the remainder of its division by the generator.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="field">The field the symbols live in.</param>
    /// <param name="generator">The monic generator polynomial, highest-order first, as <see cref="BuildGenerator{T}(BinaryField{T}, T, int, Span{T})"/> writes it.</param>
    /// <param name="message">The message symbols, highest-order first.</param>
    /// <param name="checkSymbols">Receives the remainder, highest-order first; its length must be the generator's degree.</param>
    /// <remarks>
    /// The systematic codeword is <paramref name="message"/> followed by <paramref name="checkSymbols"/>, and it is
    /// divisible by the generator by construction, so <see cref="ComputeSyndromes{T}(BinaryField{T}, T, int, ReadOnlySpan{T}, Span{T})"/>
    /// reads it back as zeros. The remainder's degree is strictly below the generator's, which is what makes the check
    /// span exactly the generator's degree long. Every symbol must already be reduced; the precondition is not enforced.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="generator"/> is not monic, so the division it defines has no remainder of the promised degree.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="generator"/> holds fewer than two coefficients, or <paramref name="checkSymbols"/>'s length is not the generator's degree.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="field"/> is default-initialized and names no field.</exception>
    public static void ComputeCheckSymbols<T>(BinaryField<T> field, ReadOnlySpan<T> generator, ReadOnlySpan<T> message, Span<T> checkSymbols)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> {
        if (2 > generator.Length) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(generator),
                actualValue: generator.Length,
                message: "The generator must hold at least two coefficients; a Reed-Solomon generator has degree one or more."
            );
        }

        var degree = (generator.Length - 1);

        if (checkSymbols.Length != degree) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(checkSymbols),
                actualValue: checkSymbols.Length,
                message: $"The check-symbol span must be the generator's degree, which is {degree}, elements long."
            );
        }

        // Reading field.One is also the guard that refuses a default-initialized field.
        if (generator[0] != field.One) {
            throw new ArgumentException(
                message: "The generator must be monic; its leading coefficient is the one the division divides through by.",
                paramName: nameof(generator)
            );
        }

        var length = message.Length;
        var total = (length + degree);
        var pooled = ((total > StackThreshold) ? ArrayPool<T>.Shared.Rent(minimumLength: total) : null);
        // Sized to the codeword rather than to the threshold: the assembly compiles without SkipLocalsInit, so the
        // localloc zeroes what it reserves and a fixed reservation would memset the ceiling for every short block.
        Span<T> stackScratch = stackalloc T[((pooled is null) ? total : 0)];
        var working = ((pooled is null) ? stackScratch : pooled.AsSpan(start: 0, length: total));

        try {
            var fieldDegree = field.Degree;
            var fieldTail = field.ReductionTail;
            var tail = generator[1..];

            message.CopyTo(destination: working);
            working[length..].Clear();

            // Synthetic division of the message shifted up by the generator's degree. Each pass clears the leading
            // symbol against the monic leading coefficient and folds that symbol's multiple of the generator's tail into
            // the window that follows, which is one region operation over a span the caller never sees. A zero leading
            // symbol contributes nothing, and skipping it is the difference between a data-independent instruction count
            // and a data-independent RESULT; only the latter is what determinism asks for. The region runs on the kernel
            // rather than on BinaryField's checked face because the destination is stack or pooled memory the caller
            // cannot alias, so the disjointness the checked face re-tests on every symbol holds by construction here.
            for (var index = 0; (index < length); index++) {
                var coefficient = working[index];

                if (T.Zero != coefficient) {
                    BinaryFieldKernels.MultiplyAccumulateRegion(
                        destination: working.Slice(start: (index + 1), length: degree),
                        source: tail,
                        scalar: coefficient,
                        accumulate: true,
                        degree: fieldDegree,
                        tail: fieldTail
                    );
                }
            }

            working.Slice(start: length, length: degree).CopyTo(destination: checkSymbols);
        } finally {
            if (pooled is not null) {
                // Only the first total slots were written; clear exactly those before the array re-enters the shared
                // pool, so one caller's message cannot be read back by an unrelated renter.
                pooled.AsSpan(start: 0, length: total).Clear();
                ArrayPool<T>.Shared.Return(array: pooled);
            }
        }
    }

    /// <summary>Evaluates a codeword at each of the generator's roots.</summary>
    /// <typeparam name="T">The packed element carrier.</typeparam>
    /// <param name="field">The field the symbols live in.</param>
    /// <param name="rootBase">The element whose consecutive powers are the roots, matching the generator's.</param>
    /// <param name="firstRootExponent">The exponent the run of roots starts at, matching the generator's.</param>
    /// <param name="codeword">The codeword symbols, highest-order first.</param>
    /// <param name="syndromes">Receives one evaluation per root, in root order.</param>
    /// <remarks>
    /// Every syndrome is zero exactly when the codeword is divisible by the generator, so this is the read side of
    /// <see cref="ComputeCheckSymbols{T}(BinaryField{T}, ReadOnlySpan{T}, ReadOnlySpan{T}, Span{T})"/> and the opening
    /// measurement any locator would start from. Each evaluation is Horner's rule over the codeword, so the cost is one
    /// field multiply per symbol per syndrome and nothing is allocated. Every symbol must already be reduced; the
    /// precondition is not enforced.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstRootExponent"/> is negative.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="field"/> is default-initialized and names no field.</exception>
    public static void ComputeSyndromes<T>(BinaryField<T> field, T rootBase, int firstRootExponent, ReadOnlySpan<T> codeword, Span<T> syndromes)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> {
        ArgumentOutOfRangeException.ThrowIfNegative(value: firstRootExponent);

        // Horner seeds at the additive identity, and reading it from the field is also the guard that refuses a
        // default-initialized descriptor before a zero-length request could slip past the loop.
        var seed = field.Zero;
        var fieldDegree = field.Degree;
        var fieldTail = field.ReductionTail;

        for (var index = 0; (index < syndromes.Length); index++) {
            var root = field.Exponentiate(value: rootBase, exponent: ((ulong)(firstRootExponent + index)));
            var accumulator = seed;

            foreach (var symbol in codeword) {
                accumulator = BinaryFieldKernels.Multiply(
                    left: accumulator,
                    right: root,
                    degree: fieldDegree,
                    tail: fieldTail
                ) ^ symbol;
            }

            syndromes[index] = accumulator;
        }
    }
}
