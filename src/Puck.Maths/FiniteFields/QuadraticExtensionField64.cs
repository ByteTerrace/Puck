using System.Runtime.CompilerServices;
using System.Text;

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
/// <para>
/// A DEFAULT-INITIALIZED value names no field: its non-square is zero, which every factory refuses, and its base field
/// is itself default-initialized. Every member that performs or asserts field arithmetic — the identities included —
/// therefore throws <see cref="InvalidOperationException"/> rather than answering, so coordinate addition cannot return
/// unreduced arithmetic while the product and inverse paths fail incidentally on a zero modulus.
/// <see cref="BaseField"/> and <see cref="NonSquare"/> are the exception and report the uninitialized state as it
/// stands, so a default value remains printable, comparable and inspectable in a debugger. <see cref="PrimeField64"/>
/// and <see cref="BinaryField{T}"/> carry the same policy.
/// </para>
/// </remarks>
public readonly record struct QuadraticExtensionField64 : IBatchInvertible<QuadraticExtensionField64.Element> {
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

    /// <summary>Reads the base field's modulus, refusing a base field that names no prime field at all.</summary>
    /// <param name="baseField">The base field a factory was handed.</param>
    /// <returns>The base field's modulus, which is non-zero.</returns>
    /// <remarks>Every factory reads the modulus before it does anything else, so the descriptor is diagnosed where the caller supplied it rather than surfacing later as a generator complaint or as a divide by zero.</remarks>
    /// <exception cref="ArgumentException"><paramref name="baseField"/> is default-initialized.</exception>
    private static ulong BaseModulus(PrimeField64 baseField) {
        var modulus = baseField.Modulus;

        if (0UL == modulus) {
            throw new ArgumentException(
                message: "The base field is default-initialized; construct it with PrimeField64.Create before building an extension over it.",
                paramName: nameof(baseField)
            );
        }

        return modulus;
    }
    /// <summary>Prints the descriptor's two data, the base field and the non-square.</summary>
    /// <param name="builder">The builder the record's <c>ToString</c> assembles into.</param>
    /// <returns><see langword="true"/>, because members were written.</returns>
    /// <remarks>Hand-written because the compiler-synthesized body walks every public readable instance property — the guarded identities <see cref="One"/> and <see cref="Zero"/> included — which would make <c>ToString</c> throw on the default value this type promises stays printable, and would attribute the failure to the BASE field, whose own guarded <c>One</c> prints ahead of the extension's.</remarks>
    private bool PrintMembers(StringBuilder builder) {
        builder.Append(value: "BaseField = ");
        builder.Append(value: BaseField.ToString());
        builder.Append(value: ", NonSquare = ");
        builder.Append(value: NonSquare);

        return true;
    }
    /// <summary>Refuses a default-initialized descriptor, which names no field at all.</summary>
    /// <remarks>The throw sits behind a non-inlined helper so the guard an operation carries is one never-taken compare and branch.</remarks>
    /// <exception cref="InvalidOperationException">The extension is default-initialized.</exception>
    private void ThrowIfUninitialized() {
        // Every constructed extension carries a non-zero generator: Create refuses zero through the character, the
        // reduced bound refuses the modulus, and CreateCanonical's walk starts at two. Zero is therefore exactly the
        // uninitialized state, and it implies the base field is uninitialized too.
        if (0UL == NonSquare) { ThrowUninitialized(); }
    }
    /// <summary>Throws the uninitialized-descriptor diagnosis.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUninitialized() =>
        throw new InvalidOperationException(message: "The quadratic extension is default-initialized; construct it with QuadraticExtensionField64.Create or QuadraticExtensionField64.CreateCanonical before using it.");

    /// <summary>Adds two extension elements.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum.</returns>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public Element Add(Element left, Element right) {
        ThrowIfUninitialized();

        return new Element(
            A: BaseField.Add(
                left: left.A,
                right: right.A
            ),
            B: BaseField.Add(
                left: left.B,
                right: right.B
            )
        );
    }
    /// <summary>Inverts every element of a region in place through a single base-field inversion.</summary>
    /// <param name="values">The non-zero elements to invert; each is overwritten with its inverse.</param>
    /// <remarks>The running-product method carried into the extension: one extension inversion — itself one base-field inversion — turns the whole product over, and a backward pass peels each element off. The partial-product scratch is stack-allocated for small batches and pooled for large ones, so nothing is allocated on the managed heap; a pooled scratch is cleared of the caller-derived partial products before it returns to the shared pool.</remarks>
    /// <exception cref="DivideByZeroException">Any element has zero norm; the shared product then has zero norm and no inverse.</exception>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field. The descriptor is read before the span is, so an EMPTY batch is refused too.</exception>
    public void BatchInverse(Span<Element> values) {
        ThrowIfUninitialized();
        BatchInverseKernel.Invert(
            ring: this,
            values: values
        );
    }
    /// <summary>Creates the extension <c>F_p(sqrt(<paramref name="nonSquare"/>))</c>.</summary>
    /// <param name="baseField">The base field <c>F_p</c>.</param>
    /// <param name="nonSquare">The reduced quadratic non-square that the extension adjoins a root of.</param>
    /// <returns>The described extension.</returns>
    /// <remarks>
    /// The reduced-generator contract is ENFORCED rather than canonicalized, and the enforcement is what makes the
    /// record's equality mean "the supplied reduced generator agrees": an unreduced argument is a caller mistake, and
    /// silently folding it would leave <c>Create(f, d)</c> and <c>Create(f, d + p)</c> two unequal descriptors of one
    /// extension. The bound is also what keeps the modulus itself out — <c>p</c> reduces to zero in the base field's
    /// residue ring, where the character's exponentiation path reports it as a non-square, and the resulting quotient
    /// <c>F_p[t]/(t^2)</c> has a nilpotent and is not a field.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="baseField"/> is default-initialized and names no prime field, or <paramref name="nonSquare"/> is zero or a square in <paramref name="baseField"/>, in which case <c>t^2 - nonSquare</c> factors and the quotient is not a field.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nonSquare"/> is at or above the base field's modulus, so it is not a reduced base-field element.</exception>
    public static QuadraticExtensionField64 Create(PrimeField64 baseField, ulong nonSquare) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: nonSquare,
            other: BaseModulus(baseField: baseField),
            paramName: nameof(nonSquare)
        );

        if (1 != -baseField.LegendreCharacter(value: nonSquare)) {
            throw new ArgumentException(
                message: "The extension generator must be a quadratic non-square in the base field.",
                paramName: nameof(nonSquare)
            );
        }

        return new QuadraticExtensionField64(
            baseField: baseField,
            nonSquare: nonSquare
        );
    }
    /// <summary>Creates the extension over a base field with the canonical smallest non-square generator.</summary>
    /// <param name="baseField">The base field <c>F_p</c>.</param>
    /// <returns>The extension adjoining a root of the smallest non-square, which <see cref="SmallestNonSquare(PrimeField64)"/> selects deterministically.</returns>
    /// <exception cref="ArgumentException"><paramref name="baseField"/> is default-initialized and names no prime field.</exception>
    public static QuadraticExtensionField64 CreateCanonical(PrimeField64 baseField) =>
        new(
            baseField: baseField,
            nonSquare: SmallestNonSquare(baseField: baseField)
        );
    /// <summary>Applies the non-trivial field automorphism, the conjugation that sends <c>sqrt(d)</c> to its negation.</summary>
    /// <param name="value">The element to conjugate.</param>
    /// <returns>The element <c>A - B * sqrt(d)</c>, which is <c>value</c> raised to the base characteristic <c>p</c>.</returns>
    /// <remarks>The fixed points of this map are exactly the base-field elements, whose root coefficient is zero.</remarks>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public Element Frobenius(Element value) {
        ThrowIfUninitialized();

        return new Element(
            A: value.A,
            B: BaseField.Negate(value: value.B)
        );
    }
    /// <summary>Lifts a base-field element into the extension.</summary>
    /// <param name="value">The reduced base-field element.</param>
    /// <returns>The element with base-field part <paramref name="value"/> and a zero root coefficient.</returns>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public Element FromBase(ulong value) {
        ThrowIfUninitialized();

        return new Element(
            A: value,
            B: 0UL
        );
    }
    /// <summary>Computes the multiplicative inverse of a non-zero extension element.</summary>
    /// <param name="value">The element to invert; its norm must be non-zero, which holds for every non-zero element of a field.</param>
    /// <returns>The unique element whose product with <paramref name="value"/> is <see cref="One"/>.</returns>
    /// <remarks>The inverse is the conjugate divided by the norm: <c>(A - B * sqrt(d)) / (A^2 - d * B^2)</c>. One base-field inversion turns the norm over.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="value"/> is zero.</exception>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public Element Inverse(Element value) {
        ThrowIfUninitialized();

        var inverseNorm = BaseField.Inverse(value: Norm(value: value));

        return new Element(
            A: BaseField.Multiply(
                left: value.A,
                right: inverseNorm
            ),
            B: BaseField.Negate(value: BaseField.Multiply(
                left: value.B,
                right: inverseNorm
            ))
        );
    }
    /// <summary>Multiplies two extension elements.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <returns>The product, with the square of the adjoined root folded back to the non-square.</returns>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public Element Multiply(Element left, Element right) {
        ThrowIfUninitialized();

        var outer = BaseField.Multiply(
            left: left.A,
            right: right.A
        );
        var inner = BaseField.Multiply(
            left: BaseField.Multiply(
                left: left.B,
                right: right.B
            ),
            right: NonSquare
        );
        var cross = BaseField.Add(
            left: BaseField.Multiply(
                left: left.A,
                right: right.B
            ),
            right: BaseField.Multiply(
                left: left.B,
                right: right.A
            )
        );

        return new Element(
            A: BaseField.Add(
                left: outer,
                right: inner
            ),
            B: cross
        );
    }
    /// <summary>Negates an extension element.</summary>
    /// <param name="value">The element to negate.</param>
    /// <returns>The additive inverse.</returns>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public Element Negate(Element value) {
        ThrowIfUninitialized();

        return new Element(
            A: BaseField.Negate(value: value.A),
            B: BaseField.Negate(value: value.B)
        );
    }
    /// <summary>Computes the field norm, the product of an element with its conjugate.</summary>
    /// <param name="value">The element whose norm is taken.</param>
    /// <returns>The base-field element <c>A^2 - d * B^2</c>.</returns>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public ulong Norm(Element value) {
        ThrowIfUninitialized();

        return BaseField.Subtract(
            left: BaseField.Multiply(
                left: value.A,
                right: value.A
            ),
            right: BaseField.Multiply(
                left: BaseField.Multiply(
                    left: value.B,
                    right: value.B
                ),
                right: NonSquare
            )
        );
    }
    /// <summary>Raises an extension element to a power.</summary>
    /// <param name="value">The element to raise.</param>
    /// <param name="exponent">The exponent; zero yields <see cref="One"/> for every <paramref name="value"/>.</param>
    /// <returns><paramref name="value"/> raised to <paramref name="exponent"/>.</returns>
    /// <remarks>Square-and-multiply over the exponent's binary expansion, so the operation count depends on the exponent and the routine is not constant-time in it.</remarks>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public Element Pow(Element value, ulong exponent) {
        ThrowIfUninitialized();

        var power = value;
        var result = One;

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) {
                result = Multiply(
                    left: result,
                    right: power
                );
            }

            exponent >>>= 1;

            if (0UL != exponent) {
                power = Multiply(
                    left: power,
                    right: power
                );
            }
        }

        return result;
    }
    /// <summary>Selects the smallest quadratic non-square of a base field.</summary>
    /// <param name="baseField">The base field to search.</param>
    /// <returns>The least value in <c>2, 3, 5, ...</c> whose quadratic character is <c>-1</c>. Perfect squares along the way are skipped by the character itself.</returns>
    /// <remarks>The search is deterministic and terminates quickly: non-squares are half of the non-zero residues, so the smallest is small for every prime.</remarks>
    /// <exception cref="ArgumentException"><paramref name="baseField"/> is default-initialized and names no prime field.</exception>
    public static ulong SmallestNonSquare(PrimeField64 baseField) {
        _ = BaseModulus(baseField: baseField);

        var candidate = 2UL;

        while (1 != -baseField.LegendreCharacter(value: candidate)) { ++candidate; }

        return candidate;
    }
    /// <summary>Subtracts one extension element from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference.</returns>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public Element Subtract(Element left, Element right) {
        ThrowIfUninitialized();

        return new Element(
            A: BaseField.Subtract(
                left: left.A,
                right: right.A
            ),
            B: BaseField.Subtract(
                left: left.B,
                right: right.B
            )
        );
    }
    /// <summary>Computes the field trace, the sum of an element with its conjugate.</summary>
    /// <param name="value">The element whose trace is taken.</param>
    /// <returns>The base-field element <c>2 * A</c>.</returns>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public ulong Trace(Element value) {
        ThrowIfUninitialized();

        return BaseField.Add(
            left: value.A,
            right: value.A
        );
    }

    /// <summary>Gets the base field the extension sits over.</summary>
    public PrimeField64 BaseField { get; }
    /// <summary>Gets the quadratic non-square whose root the extension adjoins.</summary>
    public ulong NonSquare { get; }
    /// <summary>Gets the multiplicative identity.</summary>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public Element One {
        get {
            ThrowIfUninitialized();

            return new Element(
                A: 1UL,
                B: 0UL
            );
        }
    }
    /// <summary>Gets the additive identity.</summary>
    /// <exception cref="InvalidOperationException">The extension is default-initialized and names no field.</exception>
    public Element Zero {
        get {
            ThrowIfUninitialized();

            return new Element(
                A: 0UL,
                B: 0UL
            );
        }
    }
}
