using System.Buffers;

namespace Puck.Maths;

/// <summary>
/// The three operations the running-product batch inversion needs of a ring: the multiplicative identity that seeds the
/// running product, the product itself, and the one inversion that turns the whole product over.
/// </summary>
/// <typeparam name="TElement">The ring's element type.</typeparam>
/// <remarks>
/// The implementing descriptor is constrained to a struct wherever this interface is consumed, so
/// <see cref="BatchInverseKernel"/>'s instantiation resolves all three operations statically. A boxed descriptor or a
/// delegate-shaped ring would put an indirect call in the innermost loop, which is the whole cost the batch method
/// exists to avoid.
/// </remarks>
internal interface IBatchInvertible<TElement> {
    /// <summary>Gets the multiplicative identity.</summary>
    TElement One { get; }

    /// <summary>Computes the multiplicative inverse of an element.</summary>
    /// <param name="value">The element to invert.</param>
    /// <returns>The element whose product with <paramref name="value"/> is <see cref="One"/>.</returns>
    TElement Inverse(TElement value);
    /// <summary>Multiplies two elements.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <returns>The product.</returns>
    TElement Multiply(TElement left, TElement right);
}
/// <summary>
/// Provides the running-product batch inversion shared by every ring whose descriptor implements
/// <see cref="IBatchInvertible{TElement}"/>.
/// </summary>
/// <remarks>
/// One implementation rather than one per ring is what keeps the operation ORDER — forward prefix pass, single
/// inversion, backward peel — a single decision. That order is what fixes each slot's bits, so two hand-kept copies
/// could drift into two different answers while both still looked like the same algorithm.
/// </remarks>
internal static class BatchInverseKernel {
    /// <summary>The batch length above which the partial-product scratch is pooled rather than stack-allocated.</summary>
    private const int StackThreshold = 512;

    /// <summary>Inverts every element of a region in place through a single ring inversion.</summary>
    /// <typeparam name="TRing">The ring descriptor, which must be a struct so the three operations devirtualize.</typeparam>
    /// <typeparam name="TElement">The ring's element type, which must be unmanaged so the scratch can live on the stack.</typeparam>
    /// <param name="ring">The ring the elements live in.</param>
    /// <param name="values">The invertible elements; each is overwritten with its inverse.</param>
    /// <remarks>
    /// A forward pass accumulates the partial products <c>a_0, a_0 a_1, ...</c>, one inversion turns the whole product
    /// over, and a backward pass peels each element off that inverse. The cost is one inversion plus about three
    /// multiplications per element, replacing the <c>n</c> inversions the naive loop would perform. The
    /// partial-product scratch is stack-allocated for small batches and pooled for large ones, so nothing is allocated
    /// on the managed heap; a pooled scratch has its written prefix cleared before it is returned, so the
    /// caller-derived partial products never re-enter the shared pool.
    /// </remarks>
    /// <exception cref="DivideByZeroException">The running product has no inverse, which happens exactly when some element does not.</exception>
    internal static void Invert<TRing, TElement>(in TRing ring, Span<TElement> values)
        where TRing : struct, IBatchInvertible<TElement>
        where TElement : unmanaged {
        var count = values.Length;

        if (0 == count) { return; }

        var pooled = ((count > StackThreshold)
            ? ArrayPool<TElement>.Shared.Rent(minimumLength: count)
            : null
        );
        // Sized to the batch, not the threshold: the assembly compiles without SkipLocalsInit, so the localloc zeroes
        // what it reserves — a fixed 512-element reservation would memset 4-8 KiB even for a one-element batch.
        Span<TElement> stackScratch = stackalloc TElement[((pooled is null)
            ? count
            : 0)];
        var prefix = ((pooled is null)
            ? stackScratch
            : pooled.AsSpan()
        );

        try {
            var running = ring.One;

            for (var index = 0; (index < count); ++index) {
                running = ring.Multiply(
                    left: running,
                    right: values[index]
                );
                prefix[index] = running;
            }

            var inverse = ring.Inverse(value: running);

            // Both peels keep the operand order the prefix P_i = P_(i-1)·a_i dictates, so the kernel holds in a
            // noncommutative ring: a_i⁻¹ = P_i⁻¹·P_(i-1), and the next running inverse is P_(i-1)⁻¹ = a_i·P_i⁻¹.
            for (var index = (count - 1); (index >= 1); --index) {
                var element = values[index];

                values[index] = ring.Multiply(
                    left: inverse,
                    right: prefix[(index - 1)]
                );
                inverse = ring.Multiply(
                    left: element,
                    right: inverse
                );
            }

            values[0] = inverse;
        } finally {
            if (pooled is not null) {
                // Only the first count slots were written; clear exactly those before the array re-enters the shared
                // pool, so the caller-derived partial products cannot be read back by an unrelated renter.
                pooled.AsSpan(
                    length: count,
                    start: 0
                ).Clear();
                ArrayPool<TElement>.Shared.Return(array: pooled);
            }
        }
    }
}
