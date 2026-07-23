using System.Buffers;

namespace Puck.Maths;

/// <summary>
/// The quadratic extension field <c>F_{p^2} = F_p(sqrt(d))</c> over a <see cref="PrimeField64"/> and a fixed quadratic
/// non-square <c>d</c>. An element is a pair <c>(A, B)</c> standing for <c>A + B * sqrt(d)</c> with both parts reduced
/// in the base field.
/// </summary>
/// <remarks>
/// <para>
/// The extension exists because <c>d</c> is a non-square, so <c>t^2 - d</c> is irreducible and its root <c>sqrt(d)</c>
/// generates a two-dimensional space over <c>F_p</c>. Multiplication folds the square of the root back to <c>d</c>;
/// inversion divides the conjugate by the norm, which is a single base-field inversion. The non-trivial field
/// automorphism is <see cref="Frobenius(Element)"/>, whose fixed subfield is the base field, and the
/// <see cref="Norm(Element)"/> and <see cref="Trace(Element)"/> land there.
/// </para>
/// <para>
/// This is the odd-characteristic surd field the engine reaches for whenever a computation over <c>F_p</c> needs an
/// element that is not itself a square in the base field — exact odd-characteristic surd arithmetic for procedural
/// incidence structures, and the working field for varying-prime special-value measurements. Two extension fields are
/// equal when their base fields and non-squares agree.
/// </para>
/// </remarks>
public readonly record struct QuadraticExtensionField64 {
    /// <summary>Creates an extension from its already-validated base field and non-square.</summary>
    /// <param name="baseField">The base field.</param>
    /// <param name="nonSquare">The quadratic non-square defining the extension.</param>
    private QuadraticExtensionField64(PrimeField64 baseField, ulong nonSquare) {
        BaseField = baseField;
        NonSquare = nonSquare;
    }

    /// <summary>An element <c>A + B * sqrt(d)</c> of the extension, with both parts reduced in the base field.</summary>
    /// <param name="A">The base-field part.</param>
    /// <param name="B">The coefficient of the adjoined root.</param>
    public readonly record struct Element(ulong A, ulong B);

    /// <summary>Creates the extension <c>F_p(sqrt(<paramref name="nonSquare"/>))</c>.</summary>
    /// <param name="baseField">The base field <c>F_p</c>.</param>
    /// <param name="nonSquare">The reduced quadratic non-square that the extension adjoins a root of.</param>
    /// <returns>The described extension.</returns>
    /// <exception cref="ArgumentException"><paramref name="nonSquare"/> is zero or a square in <paramref name="baseField"/>, in which case <c>t^2 - nonSquare</c> factors and the quotient is not a field.</exception>
    public static QuadraticExtensionField64 Create(PrimeField64 baseField, ulong nonSquare) {
        if (1 != -baseField.LegendreCharacter(value: nonSquare)) {
            throw new ArgumentException(message: "The extension generator must be a quadratic non-square in the base field.", paramName: nameof(nonSquare));
        }

        return new QuadraticExtensionField64(baseField: baseField, nonSquare: nonSquare);
    }
    /// <summary>Creates the extension over a base field with the canonical smallest non-square generator.</summary>
    /// <param name="baseField">The base field <c>F_p</c>.</param>
    /// <returns>The extension adjoining a root of the smallest non-square, which <see cref="SmallestNonSquare(PrimeField64)"/> selects deterministically.</returns>
    public static QuadraticExtensionField64 CreateCanonical(PrimeField64 baseField) =>
        new(baseField: baseField, nonSquare: SmallestNonSquare(baseField: baseField));
    /// <summary>Selects the smallest quadratic non-square of a base field.</summary>
    /// <param name="baseField">The base field to search.</param>
    /// <returns>The least value in <c>2, 3, 5, ...</c> whose quadratic character is <c>-1</c>. Perfect squares along the way are skipped by the character itself.</returns>
    /// <remarks>The search is deterministic and terminates quickly: non-squares are half of the non-zero residues, so the smallest is small for every prime.</remarks>
    public static ulong SmallestNonSquare(PrimeField64 baseField) {
        var candidate = 2UL;

        while (1 != -baseField.LegendreCharacter(value: candidate)) { ++candidate; }

        return candidate;
    }

    /// <summary>Gets the base field the extension sits over.</summary>
    public PrimeField64 BaseField { get; }
    /// <summary>Gets the multiplicative identity.</summary>
    public Element One => new(A: 1UL, B: 0UL);
    /// <summary>Gets the quadratic non-square whose root the extension adjoins.</summary>
    public ulong NonSquare { get; }
    /// <summary>Gets the additive identity.</summary>
    public Element Zero => new(A: 0UL, B: 0UL);

    /// <summary>Adds two extension elements.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum.</returns>
    public Element Add(Element left, Element right) =>
        new(A: BaseField.Add(left: left.A, right: right.A), B: BaseField.Add(left: left.B, right: right.B));
    /// <summary>Inverts every element of a region in place through a single base-field inversion.</summary>
    /// <param name="values">The non-zero elements to invert; each is overwritten with its inverse.</param>
    /// <remarks>The running-product method carried into the extension: one extension inversion — itself one base-field inversion — turns the whole product over, and a backward pass peels each element off. The partial-product scratch is stack-allocated for small batches and pooled for large ones, so nothing is allocated on the managed heap.</remarks>
    /// <exception cref="DivideByZeroException">Any element has zero norm; the shared product then has zero norm and no inverse.</exception>
    public void BatchInverse(Span<Element> values) {
        var count = values.Length;

        if (0 == count) { return; }

        const int StackThreshold = 512;
        var pooled = ((count > StackThreshold) ? ArrayPool<Element>.Shared.Rent(minimumLength: count) : null);
        Span<Element> stackScratch = stackalloc Element[((pooled is null) ? StackThreshold : 0)];
        var prefix = ((pooled is null) ? stackScratch : pooled.AsSpan());

        try {
            var running = One;

            for (var index = 0; (index < count); ++index) {
                running = Multiply(left: running, right: values[index]);
                prefix[index] = running;
            }

            var inverse = Inverse(value: running);

            for (var index = (count - 1); (index >= 1); --index) {
                var element = values[index];

                values[index] = Multiply(left: inverse, right: prefix[index - 1]);
                inverse = Multiply(left: inverse, right: element);
            }

            values[0] = inverse;
        }
        finally {
            if (pooled is not null) { ArrayPool<Element>.Shared.Return(array: pooled); }
        }
    }
    /// <summary>Applies the non-trivial field automorphism, the conjugation that sends <c>sqrt(d)</c> to its negation.</summary>
    /// <param name="value">The element to conjugate.</param>
    /// <returns>The element <c>A - B * sqrt(d)</c>, which is <c>value</c> raised to the base characteristic <c>p</c>.</returns>
    /// <remarks>The fixed points of this map are exactly the base-field elements, whose root coefficient is zero.</remarks>
    public Element Frobenius(Element value) =>
        new(A: value.A, B: BaseField.Negate(value: value.B));
    /// <summary>Lifts a base-field element into the extension.</summary>
    /// <param name="value">The reduced base-field element.</param>
    /// <returns>The element with base-field part <paramref name="value"/> and a zero root coefficient.</returns>
    public Element FromBase(ulong value) =>
        new(A: value, B: 0UL);
    /// <summary>Computes the multiplicative inverse of a non-zero extension element.</summary>
    /// <param name="value">The element to invert; its norm must be non-zero, which holds for every non-zero element of a field.</param>
    /// <returns>The unique element whose product with <paramref name="value"/> is <see cref="One"/>.</returns>
    /// <remarks>The inverse is the conjugate divided by the norm: <c>(A - B * sqrt(d)) / (A^2 - d * B^2)</c>. One base-field inversion turns the norm over.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="value"/> is zero.</exception>
    public Element Inverse(Element value) {
        var inverseNorm = BaseField.Inverse(value: Norm(value: value));

        return new Element(
            A: BaseField.Multiply(left: value.A, right: inverseNorm),
            B: BaseField.Negate(value: BaseField.Multiply(left: value.B, right: inverseNorm))
        );
    }
    /// <summary>Multiplies two extension elements.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <returns>The product, with the square of the adjoined root folded back to the non-square.</returns>
    public Element Multiply(Element left, Element right) {
        var outer = BaseField.Multiply(left: left.A, right: right.A);
        var inner = BaseField.Multiply(left: BaseField.Multiply(left: left.B, right: right.B), right: NonSquare);
        var cross = BaseField.Add(
            left: BaseField.Multiply(left: left.A, right: right.B),
            right: BaseField.Multiply(left: left.B, right: right.A)
        );

        return new Element(A: BaseField.Add(left: outer, right: inner), B: cross);
    }
    /// <summary>Negates an extension element.</summary>
    /// <param name="value">The element to negate.</param>
    /// <returns>The additive inverse.</returns>
    public Element Negate(Element value) =>
        new(A: BaseField.Negate(value: value.A), B: BaseField.Negate(value: value.B));
    /// <summary>Computes the field norm, the product of an element with its conjugate.</summary>
    /// <param name="value">The element whose norm is taken.</param>
    /// <returns>The base-field element <c>A^2 - d * B^2</c>.</returns>
    public ulong Norm(Element value) =>
        BaseField.Subtract(
            left: BaseField.Multiply(left: value.A, right: value.A),
            right: BaseField.Multiply(left: BaseField.Multiply(left: value.B, right: value.B), right: NonSquare)
        );
    /// <summary>Raises an extension element to a power.</summary>
    /// <param name="value">The element to raise.</param>
    /// <param name="exponent">The exponent; zero yields <see cref="One"/> for every <paramref name="value"/>.</param>
    /// <returns><paramref name="value"/> raised to <paramref name="exponent"/>.</returns>
    /// <remarks>Square-and-multiply over the exponent's binary expansion, so the operation count depends on the exponent and the routine is not constant-time in it.</remarks>
    public Element Pow(Element value, ulong exponent) {
        var power = value;
        var result = One;

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) { result = Multiply(left: result, right: power); }

            exponent >>>= 1;

            if (0UL != exponent) { power = Multiply(left: power, right: power); }
        }

        return result;
    }
    /// <summary>Subtracts one extension element from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference.</returns>
    public Element Subtract(Element left, Element right) =>
        new(A: BaseField.Subtract(left: left.A, right: right.A), B: BaseField.Subtract(left: left.B, right: right.B));
    /// <summary>Computes the field trace, the sum of an element with its conjugate.</summary>
    /// <param name="value">The element whose trace is taken.</param>
    /// <returns>The base-field element <c>2 * A</c>.</returns>
    public ulong Trace(Element value) =>
        BaseField.Add(left: value.A, right: value.A);
}
