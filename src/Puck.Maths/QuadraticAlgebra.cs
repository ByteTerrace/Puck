using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The unifying two-dimensional number system: the ring obtained by adjoining a root <c>x</c> of
/// <c>x² = P·x + Q</c> to a carrier ring <typeparamref name="TScalar"/>. Every planar number system in this library is
/// one instance of this object — the pair <c>(P, Q)</c> is the whole of the choice, and the discriminant
/// <c>Δ = P² + 4Q</c> classifies the world it names: <c>Δ &lt; 0</c> gives rotation (the complex numbers, e.g.
/// <c>(0, −1)</c>), <c>Δ = 0</c> gives shear (the dual numbers, <c>(0, 0)</c>), and <c>Δ &gt; 0</c> gives scaling (the
/// split-complex numbers, <c>(0, 1)</c>). Over a finite field the same <c>Δ</c> decides whether the modulus stays inert,
/// ramifies, or splits.
/// </summary>
/// <typeparam name="TScalar">
/// The carrier ring. The constraint set is exactly the one <see cref="FixedDual{TValue}"/> uses — the minimal group of
/// generic-math operator and identity interfaces that expresses the ring operations while admitting both the house
/// fixed-point type <see cref="FixedQ4816"/> and <see cref="System.Numerics.BigInteger"/>. No ordering, no division, and
/// no formatting are required, so exact carriers (integers, rationals) and rounded carriers (fixed point) compose alike;
/// the descriptor never assumes the arithmetic is associative under bitwise equality, so a rounded carrier is
/// well-defined here.
/// </typeparam>
/// <param name="P">The linear coefficient of the defining relation <c>x² = P·x + Q</c>; the companion trace.</param>
/// <param name="Q">The constant coefficient of the defining relation; the negated companion determinant.</param>
public readonly record struct QuadraticAlgebra<TScalar>(TScalar P, TScalar Q)
    where TScalar : IAdditionOperators<TScalar, TScalar, TScalar>,
                    ISubtractionOperators<TScalar, TScalar, TScalar>,
                    IMultiplyOperators<TScalar, TScalar, TScalar>,
                    IUnaryNegationOperators<TScalar, TScalar>,
                    IAdditiveIdentity<TScalar, TScalar>,
                    IMultiplicativeIdentity<TScalar, TScalar> {
    /// <summary>An element <c>U + V·x</c> of the algebra, with both parts in the carrier ring.</summary>
    /// <param name="U">The scalar part.</param>
    /// <param name="V">The coefficient of the adjoined root <c>x</c>.</param>
    public readonly record struct Element(TScalar U, TScalar V);

    /// <summary>A projective pair <c>(numerator : denominator)</c> standing for the ratio <c>numerator / denominator</c>
    /// without dividing — the state a continued-fraction convergent or a <see cref="MobiusStep"/> iterate carries.</summary>
    /// <param name="Numerator">The numerator.</param>
    /// <param name="Denominator">The denominator.</param>
    public readonly record struct Projective(TScalar Numerator, TScalar Denominator);

    /// <summary>Creates the algebra <c>TScalar[x] / (x² − P·x − Q)</c>.</summary>
    /// <param name="p">The linear coefficient of the defining relation.</param>
    /// <param name="q">The constant coefficient of the defining relation.</param>
    /// <returns>The described algebra.</returns>
    /// <remarks>Every <c>(p, q)</c> defines a valid unital rank-two algebra over the carrier, so nothing is rejected; the
    /// factory exists to mirror the validated construction of the sibling structures and to give the discriminant a home.
    /// Whether the result is a field, a product of two lines, or has nilpotents is read from <see cref="Discriminant"/>
    /// rather than enforced here.</remarks>
    public static QuadraticAlgebra<TScalar> Create(TScalar p, TScalar q) =>
        new(P: p, Q: q);

    /// <summary>Gets the discriminant <c>Δ = P² + 4Q</c>, whose character classifies the algebra.</summary>
    /// <remarks>A negative value names a rotation algebra (the complex numbers), zero a shear algebra (the dual numbers),
    /// and a positive value a scaling algebra (the split-complex numbers). Over a finite field the quadratic character of
    /// this value decides inert versus split; a zero value marks the ramified case.</remarks>
    public TScalar Discriminant =>
        ((P * P) + (Q + Q) + (Q + Q));
    /// <summary>Gets the multiplicative identity, <c>1 + 0·x</c>.</summary>
    public Element One => new(
        U: TScalar.MultiplicativeIdentity,
        V: TScalar.AdditiveIdentity
    );
    /// <summary>Gets the adjoined root itself, the element <c>0 + 1·x</c> — the generator whose powers drive the companion sequences.</summary>
    public Element Root => new(
        U: TScalar.AdditiveIdentity,
        V: TScalar.MultiplicativeIdentity
    );
    /// <summary>Gets the additive identity, <c>0 + 0·x</c>.</summary>
    public Element Zero => new(
        U: TScalar.AdditiveIdentity,
        V: TScalar.AdditiveIdentity
    );

    /// <summary>Adds two elements.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public Element Add(Element left, Element right) =>
        new(
        U: (left.U + right.U),
        V: (left.V + right.V)
    );
    /// <summary>Returns the conjugate — the image under the non-trivial algebra involution.</summary>
    /// <param name="value">The element to conjugate.</param>
    /// <returns>The element <c>(U + P·V) − V·x</c>, the second root of the shared minimal polynomial.</returns>
    /// <remarks>Over <c>(0, −1)</c> this is the complex conjugate <c>U − V·x</c>; the extra <c>P·V</c> term is the
    /// companion trace acting when the basis root is not trace-free.</remarks>
    public Element Conjugate(Element value) =>
        new(
        U: (value.U + (P * value.V)),
        V: -value.V
    );
    /// <summary>Multiplies two elements, folding <c>x²</c> back through the defining relation.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product <c>(u₁u₂ + Q·v₁v₂) + (u₁v₂ + u₂v₁ + P·v₁v₂)·x</c>.</returns>
    public Element Multiply(Element left, Element right) {
        var scalarProduct = (left.U * right.U);
        var rootProduct = (left.V * right.V);
        var cross = ((left.U * right.V) + (left.V * right.U));

        return new(
            U: (scalarProduct + (Q * rootProduct)),
            V: (cross + (P * rootProduct))
        );
    }
    /// <summary>Negates an element.</summary>
    /// <param name="value">The element to negate.</param>
    /// <returns>The componentwise negation.</returns>
    public Element Negate(Element value) =>
        new(
        U: -value.U,
        V: -value.V
    );
    /// <summary>Computes the algebra norm, the product of an element with its <see cref="Conjugate"/>.</summary>
    /// <param name="value">The element whose norm is taken.</param>
    /// <returns>The scalar <c>U² + P·U·V − Q·V²</c>.</returns>
    /// <remarks>This is the determinant of multiplication-by-<paramref name="value"/>; an element is a unit exactly when
    /// its norm is one of the carrier's units, and the zero divisors are precisely the norm-zero elements.</remarks>
    public TScalar Norm(Element value) =>
        (((value.U * value.U) + ((P * value.U) * value.V)) - ((Q * value.V) * value.V));
    /// <summary>Computes the algebra trace, the sum of an element with its <see cref="Conjugate"/>.</summary>
    /// <param name="value">The element whose trace is taken.</param>
    /// <returns>The scalar <c>2U + P·V</c>.</returns>
    public TScalar Trace(Element value) =>
        ((value.U + value.U) + (P * value.V));
    /// <summary>Subtracts one element from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public Element Subtract(Element left, Element right) =>
        new(
        U: (left.U - right.U),
        V: (left.V - right.V)
    );

    /// <summary>Advances one step of the companion Möbius map <c>y ↦ P + Q/y</c> on a projective pair, without dividing.</summary>
    /// <param name="pair">The current convergent <c>(numerator : denominator)</c>.</param>
    /// <returns>The next convergent <c>(P·numerator + Q·denominator : numerator)</c> — one row of the companion action.</returns>
    /// <remarks>Iterating from <c>(1 : 0)</c> enumerates the convergents of the periodic continued fraction whose value is
    /// the fixed point of the map; the metallic means are the case <c>(P, Q) = (k, 1)</c>.</remarks>
    public Projective MobiusStep(Projective pair) =>
        new(
        Numerator: ((P * pair.Numerator) + (Q * pair.Denominator)),
        Denominator: pair.Numerator
    );
    /// <summary>Raises the adjoined root to a power by fast exponentiation — the closed-form engine for the companion sequences.</summary>
    /// <param name="exponent">The power; zero yields <see cref="One"/>.</param>
    /// <returns>The element <c>x^exponent</c>. Its <see cref="Element.V"/> is the <paramref name="exponent"/>-th term of
    /// the sequence satisfying <c>a_{n+1} = P·a_n + Q·a_{n-1}</c>, and <see cref="Element.U"/> is <c>Q</c> times the
    /// previous term; for <c>(P, Q) = (k, 1)</c> these are the <c>k</c>-metallic sequences.</returns>
    /// <remarks>Square-and-multiply over the exponent's binary expansion, so the operation count depends on the exponent
    /// and the routine is not constant-time in it. Over an exact carrier this is the standard logarithmic-time evaluator
    /// for a second-order linear recurrence.</remarks>
    public Element CompanionPower(ulong exponent) {
        var result = One;
        var power = Root;

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) { result = Multiply(left: result, right: power); }

            exponent >>>= 1;

            if (0UL != exponent) { power = Multiply(left: power, right: power); }
        }

        return result;
    }
}
