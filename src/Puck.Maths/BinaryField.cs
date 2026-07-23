using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The finite field <c>GF(2^k)</c>: the quotient of <c>GF(2)[t]</c> by a fixed irreducible polynomial. Elements are
/// packed <typeparamref name="T"/> values whose bit <c>i</c> is the coefficient of <c>t^i</c>, reduced to a degree
/// below <see cref="Degree"/>. Addition is XOR; multiplication, inversion, division, and exponentiation are the field
/// operations under the modulus <c>t^Degree + ReductionTail</c>.
/// </summary>
/// <typeparam name="T">The packed element carrier: <see cref="byte"/>, <see cref="ushort"/>, <see cref="uint"/>, <see cref="ulong"/>, or <see cref="UInt128"/>.</typeparam>
/// <remarks>
/// The modulus is stored as its tail because a degree-<c>k</c> modulus needs <c>k + 1</c> bits and would not fit the
/// element carrier at the largest degree each carrier supports; the leading <c>t^Degree</c> term is implicit. The
/// hardware carryless-multiply instruction is used for the product when its instruction set is available; otherwise a
/// table-free masked-comb fallback produces the identical product. Reduction, inversion, division, and exponentiation
/// are one implementation shared by both paths. Multiplication, inversion, and division require reduced operands and
/// an irreducible modulus; <see cref="IsReduced(T)"/> and <see cref="IsIrreducible"/> test both, and neither is
/// enforced on the hot path. The type describes a structure rather than being a value in one, so it carries no
/// arithmetic operators of its own; two fields are equal when their degree and tail agree.
/// </remarks>
public readonly record struct BinaryField<T> where T : IBinaryInteger<T>, IUnsignedNumber<T> {
    /// <summary>Creates a field from its already-validated degree and tail.</summary>
    /// <param name="degree">The field's degree.</param>
    /// <param name="reductionTail">The modulus tail.</param>
    private BinaryField(int degree, T reductionTail) {
        Degree = degree;
        ReductionTail = reductionTail;
    }

    /// <summary>Creates the field <c>GF(2^<paramref name="degree"/>)</c> under the modulus <c>t^degree + reductionTail</c>.</summary>
    /// <param name="degree">The field's degree, from one through the carrier's width in bits.</param>
    /// <param name="reductionTail">The modulus below its leading term; its constant term must be non-zero and its degree must be below <paramref name="degree"/>.</param>
    /// <returns>The described field.</returns>
    /// <remarks>
    /// Nothing is precomputed — the value is exactly the degree and the tail — so constructing a field costs nothing
    /// and no class initializer sits in front of any operation. Irreducibility is deliberately not tested here: the
    /// test is a real fraction of a millisecond at the top degrees and callers that already validated their modulus
    /// would pay for it twice. <see cref="IsIrreducible"/> performs it on demand.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="reductionTail"/> has a zero constant term, in which case <c>t</c> divides the modulus and the quotient is not a field, or it has a non-zero coefficient at or above <paramref name="degree"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degree"/> is below one or above the carrier's width in bits.</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is not one of the supported element carriers. A binary field requires a fixed carrier width.</exception>
    public static BinaryField<T> Create(int degree, T reductionTail) {
        if (!IsSupportedCarrier()) {
            throw new NotSupportedException(message: $"{typeof(T)} is not a supported binary-field element carrier. A binary field requires a fixed carrier width.");
        }

        var width = BinaryFieldKernels.CarrierBitCount<T>();

        ArgumentOutOfRangeException.ThrowIfLessThan(value: degree, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: degree, other: width);

        if (T.Zero == (reductionTail & T.One)) {
            throw new ArgumentException(
                message: "The reduction tail must have a non-zero constant term; otherwise t divides the modulus and the quotient is not a field.",
                paramName: nameof(reductionTail)
            );
        }

        if ((width != degree) && (T.Zero != (reductionTail >>> degree))) {
            throw new ArgumentException(
                message: "The reduction tail must have degree below the field's degree.",
                paramName: nameof(reductionTail)
            );
        }

        return new BinaryField<T>(degree: degree, reductionTail: reductionTail);
    }
    /// <summary>Creates the field defined by a whole modulus polynomial, whose leading term is stripped to form the tail.</summary>
    /// <param name="modulus">The modulus, whose degree becomes the field's and whose constant term must be non-zero.</param>
    /// <returns>The described field.</returns>
    /// <remarks>The modulus arrives as a <see cref="BinaryPolynomial"/>, whose packed carrier tops out at degree 63, so the degree-64 through degree-128 fields — including the catalog's own <c>Degree64</c> and <c>Degree128</c> — can only be built through <see cref="Create(int, T)"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="modulus"/> has degree below one or a zero constant term.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modulus"/> has a degree above the carrier's width in bits.</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is not one of the supported element carriers. A binary field requires a fixed carrier width.</exception>
    public static BinaryField<T> FromModulus(BinaryPolynomial modulus) {
        var degree = modulus.Degree;

        if (1 > degree) {
            throw new ArgumentException(message: "A binary-field modulus must have degree at least one.", paramName: nameof(modulus));
        }

        return Create(degree: degree, reductionTail: T.CreateTruncating(value: (modulus.Bits ^ (1UL << degree))));
    }

    /// <summary>Gets the field's degree, so that the field has <c>2^Degree</c> elements.</summary>
    public int Degree { get; }
    /// <summary>Gets the multiplicative identity.</summary>
    public T One => T.One;
    /// <summary>Gets the modulus below its leading term; the modulus itself is <c>t^Degree + ReductionTail</c>.</summary>
    public T ReductionTail { get; }
    /// <summary>Gets the additive identity.</summary>
    public T Zero => T.Zero;

    /// <summary>Adds two field elements.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum, which in characteristic two is the exclusive or and is also the difference.</returns>
    public T Add(T left, T right) =>
        (left ^ right);
    /// <summary>Adds one region of field elements into another, elementwise.</summary>
    /// <param name="destination">The region to add into.</param>
    /// <param name="source">The region to add.</param>
    /// <remarks>
    /// Addition in characteristic two is the exclusive or, so this operation is degree-independent and needs no reduced
    /// operands. The two regions must be disjoint or exactly identical, and nothing is allocated.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="source"/> and <paramref name="destination"/> overlap without being the same region.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The two regions have different lengths.</exception>
    public void AddRegion(Span<T> destination, ReadOnlySpan<T> source) {
        ValidateRegion(destination: destination, source: source);
        BinaryFieldKernels.AddRegion(destination: destination, source: source);
    }
    /// <summary>Divides one field element by another.</summary>
    /// <param name="left">The reduced dividend.</param>
    /// <param name="right">The reduced, non-zero divisor.</param>
    /// <returns>The unique element whose product with <paramref name="right"/> is <paramref name="left"/>.</returns>
    /// <remarks>Both operands must already be reduced and the modulus must be irreducible; neither precondition is enforced.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> is zero.</exception>
    public T Divide(T left, T right) =>
        BinaryFieldKernels.Multiply(
            left: left,
            right: BinaryFieldKernels.Inverse(value: right, degree: Degree, tail: ReductionTail),
            degree: Degree,
            tail: ReductionTail
        );
    /// <summary>Raises a field element to a power.</summary>
    /// <param name="value">The reduced element to raise.</param>
    /// <param name="exponent">The exponent; zero yields <see cref="One"/> for every <paramref name="value"/>, including <see cref="Zero"/>.</param>
    /// <returns><paramref name="value"/> raised to <paramref name="exponent"/>.</returns>
    /// <remarks>Square-and-multiply over the exponent's binary expansion, so the operation count depends on the exponent and the routine is not constant-time in it.</remarks>
    public T Exponentiate(T value, ulong exponent) =>
        BinaryFieldKernels.Exponentiate(value: value, exponent: exponent, degree: Degree, tail: ReductionTail);
    /// <summary>Computes the multiplicative inverse of a non-zero field element.</summary>
    /// <param name="value">The reduced, non-zero element to invert.</param>
    /// <returns>The unique element whose product with <paramref name="value"/> is <see cref="One"/>.</returns>
    /// <remarks>The Itoh–Tsujii Frobenius addition chain, whose shape depends only on <see cref="Degree"/> and never on <paramref name="value"/>. The operand must already be reduced and the modulus must be irreducible; neither precondition is enforced.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="value"/> is zero.</exception>
    public T Inverse(T value) =>
        BinaryFieldKernels.Inverse(value: value, degree: Degree, tail: ReductionTail);
    /// <summary>Gets whether the modulus is irreducible, which is what makes the quotient ring a field.</summary>
    /// <returns><see langword="true"/> when the modulus is irreducible; otherwise <see langword="false"/>.</returns>
    /// <remarks>The Ben-Or/Rabin criterion. It is construction-time validation rather than a hot path, and <see cref="Inverse(T)"/> and <see cref="Divide(T, T)"/> are meaningless without it.</remarks>
    public bool IsIrreducible() =>
        BinaryFieldKernels.IsIrreducible(degree: Degree, tail: ReductionTail);
    /// <summary>Gets whether a packed value is already a reduced field element.</summary>
    /// <param name="value">The packed value to test.</param>
    /// <returns><see langword="true"/> when every non-zero coefficient of <paramref name="value"/> sits below <see cref="Degree"/>; otherwise <see langword="false"/>.</returns>
    public bool IsReduced(T value) =>
        ((BinaryFieldKernels.CarrierBitCount<T>() == Degree) || (T.Zero == (value >>> Degree)));
    /// <summary>Multiplies two field elements.</summary>
    /// <param name="left">The reduced first factor.</param>
    /// <param name="right">The reduced second factor.</param>
    /// <returns>The reduced product.</returns>
    /// <remarks>Both operands must already be reduced; the precondition is not enforced. The product is exactly associative, commutative, and distributive over addition, so a chain of field products may be reassociated freely.</remarks>
    public T Multiply(T left, T right) =>
        BinaryFieldKernels.Multiply(left: left, right: right, degree: Degree, tail: ReductionTail);
    /// <summary>Adds a scaled region of field elements into another region.</summary>
    /// <param name="destination">The region to add into.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <remarks>
    /// The bulk primitive that erasure coding, syndrome evaluation, and elimination over the field all sit on. Every
    /// element must already be reduced; the precondition is not enforced. The two regions must be disjoint or exactly
    /// identical, and nothing is allocated. Byte-wide and sixteen-bit fields run on the hardware Galois-field affine
    /// transform, and byte-wide fields also on a nibble-split byte shuffle, whenever either is available; every such
    /// path produces exactly the elements the element-at-a-time loop would have produced.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="source"/> and <paramref name="destination"/> overlap without being the same region.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The two regions have different lengths.</exception>
    public void MultiplyAccumulateRegion(Span<T> destination, ReadOnlySpan<T> source, T scalar) {
        ValidateRegion(destination: destination, source: source);
        BinaryFieldKernels.MultiplyAccumulateRegion(
            destination: destination,
            source: source,
            scalar: scalar,
            accumulate: true,
            degree: Degree,
            tail: ReductionTail
        );
    }
    /// <summary>Reduces an arbitrary packed value into the field.</summary>
    /// <param name="value">The packed value to reduce, of any degree the carrier holds.</param>
    /// <returns>The unique field element congruent to <paramref name="value"/> modulo <c>t^Degree + ReductionTail</c>.</returns>
    public T Reduce(T value) =>
        BinaryFieldKernels.ReduceWide(low: value, high: T.Zero, degree: Degree, tail: ReductionTail);
    /// <summary>Writes a scaled region of field elements into another region.</summary>
    /// <param name="destination">The region to write.</param>
    /// <param name="source">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <remarks>Every element must already be reduced; the precondition is not enforced. The two regions must be disjoint or exactly identical, and nothing is allocated.</remarks>
    /// <exception cref="ArgumentException"><paramref name="source"/> and <paramref name="destination"/> overlap without being the same region.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The two regions have different lengths.</exception>
    public void ScaleRegion(Span<T> destination, ReadOnlySpan<T> source, T scalar) {
        ValidateRegion(destination: destination, source: source);
        BinaryFieldKernels.MultiplyAccumulateRegion(
            destination: destination,
            source: source,
            scalar: scalar,
            accumulate: false,
            degree: Degree,
            tail: ReductionTail
        );
    }
    /// <summary>Scales a region of field elements where it sits.</summary>
    /// <param name="values">The reduced region to scale.</param>
    /// <param name="scalar">The reduced element to scale by.</param>
    /// <remarks>Every element must already be reduced; the precondition is not enforced. Nothing is allocated.</remarks>
    public void ScaleRegionInPlace(Span<T> values, T scalar) =>
        BinaryFieldKernels.MultiplyAccumulateRegion(
            destination: values,
            source: values,
            scalar: scalar,
            accumulate: false,
            degree: Degree,
            tail: ReductionTail
        );
    /// <summary>Squares a field element.</summary>
    /// <param name="value">The reduced element to square.</param>
    /// <returns>The reduced square.</returns>
    /// <remarks>Squaring is the Frobenius map, and it is additive: the square of a sum is the sum of the squares. There is deliberately no separate squaring kernel on any tier.</remarks>
    public T Square(T value) =>
        BinaryFieldKernels.Multiply(left: value, right: value, degree: Degree, tail: ReductionTail);
    /// <summary>Computes the square root of a field element.</summary>
    /// <param name="value">The reduced element to take the root of.</param>
    /// <returns>The unique element whose square is <paramref name="value"/>.</returns>
    /// <remarks>Squaring is a bijection in characteristic two, so every element has exactly one square root; it is <c>value</c> raised to <c>2^(Degree - 1)</c>, which is <see cref="Degree"/> minus one further squarings.</remarks>
    public T SquareRoot(T value) =>
        BinaryFieldKernels.SquareRoot(value: value, degree: Degree, tail: ReductionTail);

    /// <summary>Gets whether <typeparamref name="T"/> is one of the supported element carriers.</summary>
    /// <returns><see langword="true"/> when the carrier has one of the five supported fixed widths; otherwise <see langword="false"/>.</returns>
    private static bool IsSupportedCarrier() =>
        (((typeof(T) == typeof(byte)) || (typeof(T) == typeof(ushort))) ||
         ((typeof(T) == typeof(uint)) || (typeof(T) == typeof(ulong))) ||
         (typeof(T) == typeof(UInt128)));
    /// <summary>Validates the two regions a region operation reads and writes.</summary>
    /// <param name="destination">The region to write.</param>
    /// <param name="source">The region to read.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> and <paramref name="destination"/> overlap without being the same region.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The two regions have different lengths.</exception>
    private static void ValidateRegion(Span<T> destination, ReadOnlySpan<T> source) {
        ArgumentOutOfRangeException.ThrowIfNotEqual(value: source.Length, other: destination.Length);

        // A region operation reads and writes the same element index, so a region laid exactly on top of another is
        // well defined and is what in-place scaling relies on. Only a shifted overlap has no defined result, which is
        // why the offset rather than the fact of overlap decides.
        if (destination.Overlaps(other: source, elementOffset: out var elementOffset) && (0 != elementOffset)) {
            throw new ArgumentException(
                message: "The source and destination regions must be disjoint or exactly identical; a shifted overlap has no defined result.",
                paramName: nameof(source)
            );
        }
    }
}
