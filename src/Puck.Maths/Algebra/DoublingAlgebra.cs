using System.Runtime.CompilerServices;

using LeafComplex = Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>;
using LeafQuaternion = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>;
using LeafOctonion = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>>;

namespace Puck.Maths;

/// <summary>
/// A carrier ring that additionally names its conjugation involution — the minimal surface the doubling construction
/// needs from one floor to build the next. It is the ring operations (<see cref="Add"/>, <see cref="Subtract"/>,
/// <see cref="Multiply"/>, <see cref="Negate"/>) plus the two identities and a <see cref="Conjugate"/> anti-automorphism
/// <c>x ↦ x̄</c> that fixes the real subring, reverses products (<c>(x·y)̄ = ȳ·x̄</c>), and squares to the identity.
/// Nothing here asserts commutativity or associativity, and nothing asserts bitwise-exact ring laws — a rounded
/// fixed-point carrier satisfies the shape without satisfying the laws, exactly as the sibling planar types do.
/// </summary>
/// <typeparam name="TSelf">The implementing type, carried as a curiously-recurring type parameter so the static
/// operations return the concrete carrier.</typeparam>
public interface IConjugationRing<TSelf>
    where TSelf : IConjugationRing<TSelf> {
    /// <summary>Gets the additive identity, <c>0</c>.</summary>
    static abstract TSelf AdditiveIdentity { get; }
    /// <summary>Gets the multiplicative identity, <c>1</c>.</summary>
    static abstract TSelf MultiplicativeIdentity { get; }

    /// <summary>Adds two elements.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum <c><paramref name="left"/> + <paramref name="right"/></c>.</returns>
    static abstract TSelf Add(TSelf left, TSelf right);
    /// <summary>Returns the conjugate — the image under the ring's conjugation involution.</summary>
    /// <param name="value">The element to conjugate.</param>
    /// <returns>The conjugate <c>valuē</c>; for a real carrier this is the identity.</returns>
    static abstract TSelf Conjugate(TSelf value);
    /// <summary>Multiplies two elements, respecting operand order (the product need not commute).</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product <c><paramref name="left"/> · <paramref name="right"/></c>.</returns>
    static abstract TSelf Multiply(TSelf left, TSelf right);
    /// <summary>Negates an element.</summary>
    /// <param name="value">The element to negate.</param>
    /// <returns>The additive inverse <c>−<paramref name="value"/></c>.</returns>
    static abstract TSelf Negate(TSelf value);
    /// <summary>Subtracts one element from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference <c><paramref name="left"/> − <paramref name="right"/></c>.</returns>
    static abstract TSelf Subtract(TSelf left, TSelf right);
}

/// <summary>
/// The doubling construction: the functor that builds each algebra in the real division-algebra ladder from ordered
/// pairs of the floor beneath it. An element is a pair <c>(<see cref="Left"/>, <see cref="Right"/>)</c> of the inner
/// carrier <typeparamref name="TInner"/>, multiplied by the doubling product and conjugated by the doubling involution
/// below; the result is itself an <see cref="IConjugationRing{TSelf}"/>, so the construction stacks without limit —
/// <c>DoublingAlgebra&lt;DoublingAlgebra&lt;…&gt;&gt;</c> climbs one floor per wrap.
/// </summary>
/// <remarks>
/// <para>
/// <b>The product convention.</b> The pair product is
/// <c>(a, b)·(c, d) = (a·c − d̄·b, d·a + b·c̄)</c> and the conjugation is <c>(a, b)̄ = (ā, −b)</c>, where the bar is the
/// inner conjugation. This is one of several equivalent doubling conventions; it is the one whose floor-two instance
/// reproduces the house Hamilton quaternion component-for-component under the identification
/// <c>(w, x, y, z) ↦ ((w, x), (y, z))</c> — <c>Left.Left = w</c>, <c>Left.Right = x</c>, <c>Right.Left = y</c>,
/// <c>Right.Right = z</c>. The mixed operand orders (<c>d·a</c> and <c>b·c̄</c>, not <c>a·d</c> and <c>c̄·b</c>) are
/// load-bearing once the inner floor stops commuting.
/// </para>
/// <para>
/// <b>The price ladder.</b> Each floor buys reach by spending an algebraic law, and this type measures the price
/// instead of asserting the absence of it. The real scalar (floor 0) and its first double, the complex plane (floor 1),
/// are commutative and associative. The second double, the quaternions (floor 2), keep associativity but lose
/// commutativity — <see cref="Commutator"/> is the witness. The third double, the octonions (floor 3), keep only
/// alternativity (the associator vanishes whenever two of its arguments coincide) and lose associativity outright —
/// <see cref="Associator"/> is the witness. Higher floors shed alternativity too. Because these predicates are exposed
/// as elements, the exact floor at which each law breaks is a computed value, not a comment.
/// </para>
/// <para>
/// <b>Norm.</b> <see cref="Norm"/> returns <c>a·ā + b̄·b</c>, the value <c>Left(x·x̄)</c>, which lands in the real
/// subalgebra of <typeparamref name="TInner"/> (every imaginary part cancels). It is multiplicative,
/// <c>Norm(x·y) = Norm(x)·Norm(y)</c>, over any exact carrier and on any floor whose product composes — the defining
/// property of a composition algebra. It sits one floor down as a "real embedded in the inner algebra"; projecting
/// through <see cref="Left"/> repeatedly reaches the base scalar (that projection is structural, not a re-norming — a
/// second norm would square the value).
/// </para>
/// <para>
/// <b>Rounding.</b> When <typeparamref name="TInner"/> bottoms out in a rounded fixed-point scalar, the ring laws hold
/// only up to the carrier's rounding, exactly as for the hand-written planar and quaternion types; over an exact
/// integer or rational carrier they hold bit-exactly. The construction never assumes the arithmetic is associative
/// under bitwise equality. For the closed <see cref="FixedScalarRing"/>-leaf towers <see cref="Multiply"/> and
/// <see cref="Norm"/> delegate to the hand-written types where one exists: the complex floor routes through
/// <see cref="FixedComplex"/> and the quaternion floor through <see cref="FixedQuaternion"/> (reaching their operators by
/// bit-cast or an explicit coordinate map), so the two are bit-identical <em>by construction</em> across the full raw
/// range, not by a maintained parallel kernel. The octonion floor has no hand-written oracle, so it keeps its own fused
/// eight-product kernel — the same accumulate-at-full-width, round-each-component-once discipline. The <c>typeof</c>
/// guards fold to JIT-time constants, so every other carrier keeps the generic per-product path byte for byte.
/// </para>
/// </remarks>
/// <typeparam name="TInner">The floor being doubled: any conjugation ring, including another
/// <see cref="DoublingAlgebra{TInner}"/>.</typeparam>
/// <param name="Left">The first coordinate of the pair — the "scalar-side" half.</param>
/// <param name="Right">The second coordinate of the pair — the "imaginary-side" half, negated by conjugation.</param>
public readonly record struct DoublingAlgebra<TInner>(TInner Left, TInner Right)
    : IConjugationRing<DoublingAlgebra<TInner>>
    where TInner : IConjugationRing<TInner> {
    /// <summary>Gets the additive identity, <c>(0, 0)</c>.</summary>
    public static DoublingAlgebra<TInner> AdditiveIdentity => new(
        Left: TInner.AdditiveIdentity,
        Right: TInner.AdditiveIdentity
    );
    /// <summary>Gets the multiplicative identity, <c>(1, 0)</c>.</summary>
    public static DoublingAlgebra<TInner> MultiplicativeIdentity => new(
        Left: TInner.MultiplicativeIdentity,
        Right: TInner.AdditiveIdentity
    );

    /// <summary>Adds two elements componentwise.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static DoublingAlgebra<TInner> Add(DoublingAlgebra<TInner> left, DoublingAlgebra<TInner> right) =>
        new(
        Left: TInner.Add(left: left.Left, right: right.Left),
        Right: TInner.Add(left: left.Right, right: right.Right)
    );
    /// <summary>Returns the conjugate <c>(ā, −b)</c> — the inner conjugate of the scalar half, the negation of the
    /// imaginary half.</summary>
    /// <param name="value">The element to conjugate.</param>
    /// <returns>The doubling conjugate.</returns>
    public static DoublingAlgebra<TInner> Conjugate(DoublingAlgebra<TInner> value) =>
        new(
        Left: TInner.Conjugate(value: value.Left),
        Right: TInner.Negate(value: value.Right)
    );
    /// <summary>Multiplies two elements by the doubling product <c>(a, b)·(c, d) = (a·c − d̄·b, d·a + b·c̄)</c>.</summary>
    /// <param name="left">The multiplicand <c>(a, b)</c>.</param>
    /// <param name="right">The multiplier <c>(c, d)</c>.</param>
    /// <returns>The pair product; operand order is preserved so the result is correct once the inner floor stops
    /// commuting.</returns>
    /// <remarks>Over the closed <see cref="FixedScalarRing"/>-leaf towers the complex floor delegates to
    /// <see cref="FixedComplex"/> and the quaternion floor to <see cref="FixedQuaternion"/> — bit-identical by
    /// construction — while the octonion floor keeps its own fused eight-product kernel. Every other carrier takes the
    /// generic per-product path below.</remarks>
    public static DoublingAlgebra<TInner> Multiply(DoublingAlgebra<TInner> left, DoublingAlgebra<TInner> right) {
        // JIT-constant guards: exactly one branch survives for a closed value-type instantiation, and every non-leaf
        // carrier compiles straight to the generic path with no raw casts.
        if (typeof(TInner) == typeof(FixedScalarRing)) {
            return MultiplyLeafComplex(left: left, right: right);
        }

        if (typeof(TInner) == typeof(LeafComplex)) {
            return MultiplyLeafQuaternion(left: left, right: right);
        }

        if (typeof(TInner) == typeof(LeafQuaternion)) {
            return MultiplyLeafOctonion(left: left, right: right);
        }

        var a = left.Left;
        var b = left.Right;
        var c = right.Left;
        var d = right.Right;

        return new(
            Left: TInner.Subtract(left: TInner.Multiply(left: a, right: c), right: TInner.Multiply(left: TInner.Conjugate(value: d), right: b)),
            Right: TInner.Add(left: TInner.Multiply(left: d, right: a), right: TInner.Multiply(left: b, right: TInner.Conjugate(value: c)))
        );
    }
    /// <summary>Negates an element componentwise.</summary>
    /// <param name="value">The element to negate.</param>
    /// <returns>The componentwise negation.</returns>
    public static DoublingAlgebra<TInner> Negate(DoublingAlgebra<TInner> value) =>
        new(
        Left: TInner.Negate(value: value.Left),
        Right: TInner.Negate(value: value.Right)
    );
    /// <summary>Subtracts one element from another componentwise.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static DoublingAlgebra<TInner> Subtract(DoublingAlgebra<TInner> left, DoublingAlgebra<TInner> right) =>
        new(
        Left: TInner.Subtract(left: left.Left, right: right.Left),
        Right: TInner.Subtract(left: left.Right, right: right.Right)
    );

    /// <summary>Computes the norm form <c>a·ā + b̄·b</c> — the scalar part of <c><paramref name="value"/> · valuē</c>,
    /// carried as a real element of the inner algebra <typeparamref name="TInner"/>.</summary>
    /// <param name="value">The element whose norm is taken.</param>
    /// <returns>The value <c>a·ā + b̄·b</c>. It lies in the real subalgebra of <typeparamref name="TInner"/>; project
    /// through <see cref="Left"/> to descend to the base scalar. The norm is multiplicative on every composition floor
    /// (through the octonions) and over any exact carrier.</returns>
    public static TInner Norm(DoublingAlgebra<TInner> value) {
        // Floors 1–2 delegate their norm to the hand-written complex/quaternion product-with-conjugate; the octonion floor
        // sums its leaf squares with one rounding. The guards fold to JIT-time constants exactly as in Multiply.
        if (typeof(TInner) == typeof(FixedScalarRing)) {
            return NormLeafComplex(value: value);
        }

        if (typeof(TInner) == typeof(LeafComplex)) {
            return NormLeafQuaternion(value: value);
        }

        if (typeof(TInner) == typeof(LeafQuaternion)) {
            return NormLeafOctonion(value: value);
        }

        return TInner.Add(
            left: TInner.Multiply(left: value.Left, right: TInner.Conjugate(value: value.Left)),
            right: TInner.Multiply(left: TInner.Conjugate(value: value.Right), right: value.Right)
        );
    }
    /// <summary>Computes the commutator <c>a·b − b·a</c> — the exact, testable amount by which multiplication fails to
    /// commute on this floor.</summary>
    /// <param name="left">The left operand <c>a</c>.</param>
    /// <param name="right">The right operand <c>b</c>.</param>
    /// <returns>The additive identity on the commutative floors (real, complex); a nonzero witness from the quaternion
    /// floor up.</returns>
    public static DoublingAlgebra<TInner> Commutator(DoublingAlgebra<TInner> left, DoublingAlgebra<TInner> right) =>
        Subtract(left: Multiply(left: left, right: right), right: Multiply(left: right, right: left));
    /// <summary>Computes the associator <c>(a·b)·c − a·(b·c)</c> — the exact, testable amount by which multiplication
    /// fails to associate on this floor.</summary>
    /// <param name="left">The first operand <c>a</c>.</param>
    /// <param name="middle">The second operand <c>b</c>.</param>
    /// <param name="right">The third operand <c>c</c>.</param>
    /// <returns>The additive identity on the associative floors (real, complex, quaternion) and whenever two arguments
    /// coincide on the octonion floor (alternativity); a nonzero witness for three independent octonions.</returns>
    public static DoublingAlgebra<TInner> Associator(DoublingAlgebra<TInner> left, DoublingAlgebra<TInner> middle, DoublingAlgebra<TInner> right) =>
        Subtract(
            left: Multiply(left: Multiply(left: left, right: middle), right: right),
            right: Multiply(left: left, right: Multiply(left: middle, right: right))
        );

    // Wraps a raw Q16 value as a leaf scalar; the leaf towers are built from these.
    private static FixedScalarRing Leaf(long raw) =>
        new(Value: FixedQ4816.FromRawBits(value: raw));

    // FLOOR 1 — complex leaf. (a, b)·(c, d) = (a·c − d·b, d·a + b·c) with a scalar (self-conjugate, commutative) inner
    // floor is exactly the FixedComplex product, so this DELEGATES to it. The leaf pair (Left, Right) shares FixedComplex's
    // (Real, Imaginary) layout, so a raw BitCast maps both ways with no copy — identity by construction.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static DoublingAlgebra<TInner> MultiplyLeafComplex(DoublingAlgebra<TInner> left, DoublingAlgebra<TInner> right) {
        var product = (Unsafe.BitCast<DoublingAlgebra<TInner>, FixedComplex>(source: left) * Unsafe.BitCast<DoublingAlgebra<TInner>, FixedComplex>(source: right));

        return Unsafe.BitCast<FixedComplex, DoublingAlgebra<TInner>>(source: product);
    }

    // FLOOR 2 — quaternion leaf. The doubling product over the complex inner floor, flattened under
    // (w, x, y, z) ↦ ((w, x), (y, z)), is exactly the Hamilton product, so this DELEGATES to FixedQuaternion. The leaf
    // nesting orders the raws (w, x, y, z) while FixedQuaternion stores (X, Y, Z, W), so the pair coordinates are mapped
    // explicitly at the boundary rather than bit-cast — identity by construction.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static DoublingAlgebra<TInner> MultiplyLeafQuaternion(DoublingAlgebra<TInner> left, DoublingAlgebra<TInner> right) {
        var l = Unsafe.BitCast<DoublingAlgebra<TInner>, LeafQuaternion>(source: left);
        var r = Unsafe.BitCast<DoublingAlgebra<TInner>, LeafQuaternion>(source: right);
        var product = (new FixedQuaternion(X: l.Left.Right.Value, Y: l.Right.Left.Value, Z: l.Right.Right.Value, W: l.Left.Left.Value)
                     * new FixedQuaternion(X: r.Left.Right.Value, Y: r.Right.Left.Value, Z: r.Right.Right.Value, W: r.Left.Left.Value));

        return Unsafe.BitCast<LeafQuaternion, DoublingAlgebra<TInner>>(source: new LeafQuaternion(
            Left: new LeafComplex(Left: new FixedScalarRing(Value: product.W), Right: new FixedScalarRing(Value: product.X)),
            Right: new LeafComplex(Left: new FixedScalarRing(Value: product.Y), Right: new FixedScalarRing(Value: product.Z))
        ));
    }

    // FLOOR 3 — octonion leaf. The doubling product over the quaternion inner floor, flattened to eight leaf raws
    // a0..a7 (left) and c0..c7 (right) under the nested pairing: each returned component is an eight-product fused sum.
    // No hand-written oracle exists; the one-rounding discipline itself is the contract. Eight raw Q32 products fit
    // Int64 when every operand is below 2^29 (product below 2^58, the eight-term sum below 2^61).
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static DoublingAlgebra<TInner> MultiplyLeafOctonion(DoublingAlgebra<TInner> left, DoublingAlgebra<TInner> right) {
        var l = Unsafe.BitCast<DoublingAlgebra<TInner>, LeafOctonion>(source: left);
        var r = Unsafe.BitCast<DoublingAlgebra<TInner>, LeafOctonion>(source: right);
        var a0 = l.Left.Left.Left.Value.Value;
        var a1 = l.Left.Left.Right.Value.Value;
        var a2 = l.Left.Right.Left.Value.Value;
        var a3 = l.Left.Right.Right.Value.Value;
        var a4 = l.Right.Left.Left.Value.Value;
        var a5 = l.Right.Left.Right.Value.Value;
        var a6 = l.Right.Right.Left.Value.Value;
        var a7 = l.Right.Right.Right.Value.Value;
        var c0 = r.Left.Left.Left.Value.Value;
        var c1 = r.Left.Left.Right.Value.Value;
        var c2 = r.Left.Right.Left.Value.Value;
        var c3 = r.Left.Right.Right.Value.Value;
        var c4 = r.Right.Left.Left.Value.Value;
        var c5 = r.Right.Left.Right.Value.Value;
        var c6 = r.Right.Right.Left.Value.Value;
        var c7 = r.Right.Right.Right.Value.Value;
        const ulong NarrowLimit = (1UL << 29);
        var combinedMagnitude = FixedVectorMath.RawMagnitude(value: a0) | FixedVectorMath.RawMagnitude(value: a1) |
                                 FixedVectorMath.RawMagnitude(value: a2) | FixedVectorMath.RawMagnitude(value: a3) |
                                 FixedVectorMath.RawMagnitude(value: a4) | FixedVectorMath.RawMagnitude(value: a5) |
                                 FixedVectorMath.RawMagnitude(value: a6) | FixedVectorMath.RawMagnitude(value: a7) |
                                 FixedVectorMath.RawMagnitude(value: c0) | FixedVectorMath.RawMagnitude(value: c1) |
                                 FixedVectorMath.RawMagnitude(value: c2) | FixedVectorMath.RawMagnitude(value: c3) |
                                 FixedVectorMath.RawMagnitude(value: c4) | FixedVectorMath.RawMagnitude(value: c5) |
                                 FixedVectorMath.RawMagnitude(value: c6) | FixedVectorMath.RawMagnitude(value: c7);
        long o0;
        long o1;
        long o2;
        long o3;
        long o4;
        long o5;
        long o6;
        long o7;

        if (combinedMagnitude < NarrowLimit) {
            o0 = FixedQ4816.RoundProductSum(productSum: unchecked(((((((((a0 * c0) - (a1 * c1)) - (a2 * c2)) - (a3 * c3)) - (a4 * c4)) - (a5 * c5)) - (a6 * c6)) - (a7 * c7))));
            o1 = FixedQ4816.RoundProductSum(productSum: unchecked(((((((((a0 * c1) + (a1 * c0)) + (a2 * c3)) - (a3 * c2)) + (a4 * c5)) - (a5 * c4)) - (a6 * c7)) + (a7 * c6))));
            o2 = FixedQ4816.RoundProductSum(productSum: unchecked(((((((((a0 * c2) - (a1 * c3)) + (a2 * c0)) + (a3 * c1)) + (a4 * c6)) + (a5 * c7)) - (a6 * c4)) - (a7 * c5))));
            o3 = FixedQ4816.RoundProductSum(productSum: unchecked(((((((((a0 * c3) + (a1 * c2)) - (a2 * c1)) + (a3 * c0)) + (a4 * c7)) - (a5 * c6)) + (a6 * c5)) - (a7 * c4))));
            o4 = FixedQ4816.RoundProductSum(productSum: unchecked(((((((((a0 * c4) - (a1 * c5)) - (a2 * c6)) - (a3 * c7)) + (a4 * c0)) + (a5 * c1)) + (a6 * c2)) + (a7 * c3))));
            o5 = FixedQ4816.RoundProductSum(productSum: unchecked(((((((((a0 * c5) + (a1 * c4)) - (a2 * c7)) + (a3 * c6)) - (a4 * c1)) + (a5 * c0)) - (a6 * c3)) + (a7 * c2))));
            o6 = FixedQ4816.RoundProductSum(productSum: unchecked(((((((((a0 * c6) + (a1 * c7)) + (a2 * c4)) - (a3 * c5)) - (a4 * c2)) + (a5 * c3)) + (a6 * c0)) - (a7 * c1))));
            o7 = FixedQ4816.RoundProductSum(productSum: unchecked(((((((((a0 * c7) - (a1 * c6)) + (a2 * c5)) + (a3 * c4)) - (a4 * c3)) - (a5 * c2)) + (a6 * c1)) + (a7 * c0))));
        } else {
            o0 = FixedQ4816.RoundProductSum(productSum: unchecked((((((((((Int128)a0 * c0) - ((Int128)a1 * c1)) - ((Int128)a2 * c2)) - ((Int128)a3 * c3)) - ((Int128)a4 * c4)) - ((Int128)a5 * c5)) - ((Int128)a6 * c6)) - ((Int128)a7 * c7))));
            o1 = FixedQ4816.RoundProductSum(productSum: unchecked((((((((((Int128)a0 * c1) + ((Int128)a1 * c0)) + ((Int128)a2 * c3)) - ((Int128)a3 * c2)) + ((Int128)a4 * c5)) - ((Int128)a5 * c4)) - ((Int128)a6 * c7)) + ((Int128)a7 * c6))));
            o2 = FixedQ4816.RoundProductSum(productSum: unchecked((((((((((Int128)a0 * c2) - ((Int128)a1 * c3)) + ((Int128)a2 * c0)) + ((Int128)a3 * c1)) + ((Int128)a4 * c6)) + ((Int128)a5 * c7)) - ((Int128)a6 * c4)) - ((Int128)a7 * c5))));
            o3 = FixedQ4816.RoundProductSum(productSum: unchecked((((((((((Int128)a0 * c3) + ((Int128)a1 * c2)) - ((Int128)a2 * c1)) + ((Int128)a3 * c0)) + ((Int128)a4 * c7)) - ((Int128)a5 * c6)) + ((Int128)a6 * c5)) - ((Int128)a7 * c4))));
            o4 = FixedQ4816.RoundProductSum(productSum: unchecked((((((((((Int128)a0 * c4) - ((Int128)a1 * c5)) - ((Int128)a2 * c6)) - ((Int128)a3 * c7)) + ((Int128)a4 * c0)) + ((Int128)a5 * c1)) + ((Int128)a6 * c2)) + ((Int128)a7 * c3))));
            o5 = FixedQ4816.RoundProductSum(productSum: unchecked((((((((((Int128)a0 * c5) + ((Int128)a1 * c4)) - ((Int128)a2 * c7)) + ((Int128)a3 * c6)) - ((Int128)a4 * c1)) + ((Int128)a5 * c0)) - ((Int128)a6 * c3)) + ((Int128)a7 * c2))));
            o6 = FixedQ4816.RoundProductSum(productSum: unchecked((((((((((Int128)a0 * c6) + ((Int128)a1 * c7)) + ((Int128)a2 * c4)) - ((Int128)a3 * c5)) - ((Int128)a4 * c2)) + ((Int128)a5 * c3)) + ((Int128)a6 * c0)) - ((Int128)a7 * c1))));
            o7 = FixedQ4816.RoundProductSum(productSum: unchecked((((((((((Int128)a0 * c7) - ((Int128)a1 * c6)) + ((Int128)a2 * c5)) + ((Int128)a3 * c4)) - ((Int128)a4 * c3)) - ((Int128)a5 * c2)) + ((Int128)a6 * c1)) + ((Int128)a7 * c0))));
        }

        return Unsafe.BitCast<LeafOctonion, DoublingAlgebra<TInner>>(source: new LeafOctonion(
            Left: new LeafQuaternion(
                Left: new LeafComplex(Left: Leaf(raw: o0), Right: Leaf(raw: o1)),
                Right: new LeafComplex(Left: Leaf(raw: o2), Right: Leaf(raw: o3))
            ),
            Right: new LeafQuaternion(
                Left: new LeafComplex(Left: Leaf(raw: o4), Right: Leaf(raw: o5)),
                Right: new LeafComplex(Left: Leaf(raw: o6), Right: Leaf(raw: o7))
            )
        ));
    }

    // FLOOR 1 norm — a·ā + b̄·b collapses to re² + im², which is the real component of the FixedComplex product with the
    // conjugate (the imaginary component cancels to exactly zero), so this DELEGATES to that product's one fused rounding.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static TInner NormLeafComplex(DoublingAlgebra<TInner> value) {
        var complex = Unsafe.BitCast<DoublingAlgebra<TInner>, FixedComplex>(source: value);
        var product = (complex * complex.Conjugate());

        return Unsafe.BitCast<FixedScalarRing, TInner>(source: new FixedScalarRing(Value: product.Real));
    }

    // FLOOR 2 norm — a·ā + b̄·b collapses to (w² + x² + y² + z², 0), which is the scalar component of the FixedQuaternion
    // product with the conjugate (the vector part cancels to exactly zero), so this DELEGATES to that product's one
    // rounding and packages the scalar back onto the real leaf.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static TInner NormLeafQuaternion(DoublingAlgebra<TInner> value) {
        var v = Unsafe.BitCast<DoublingAlgebra<TInner>, LeafQuaternion>(source: value);
        var quaternion = new FixedQuaternion(X: v.Left.Right.Value, Y: v.Right.Left.Value, Z: v.Right.Right.Value, W: v.Left.Left.Value);
        var product = (quaternion * quaternion.Conjugate());

        return Unsafe.BitCast<LeafComplex, TInner>(source: new LeafComplex(Left: new FixedScalarRing(Value: product.W), Right: FixedScalarRing.AdditiveIdentity));
    }

    // FLOOR 3 norm — a·ā + b̄·b collapses to (Σ o², 0, 0, 0), one fused rounding on the real leaf. Eight raw Q32 squares
    // fit Int64 below 2^29.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static TInner NormLeafOctonion(DoublingAlgebra<TInner> value) {
        var v = Unsafe.BitCast<DoublingAlgebra<TInner>, LeafOctonion>(source: value);
        var o0 = v.Left.Left.Left.Value.Value;
        var o1 = v.Left.Left.Right.Value.Value;
        var o2 = v.Left.Right.Left.Value.Value;
        var o3 = v.Left.Right.Right.Value.Value;
        var o4 = v.Right.Left.Left.Value.Value;
        var o5 = v.Right.Left.Right.Value.Value;
        var o6 = v.Right.Right.Left.Value.Value;
        var o7 = v.Right.Right.Right.Value.Value;
        long norm;

        if ((FixedVectorMath.RawMagnitude(value: o0) | FixedVectorMath.RawMagnitude(value: o1) |
             FixedVectorMath.RawMagnitude(value: o2) | FixedVectorMath.RawMagnitude(value: o3) |
             FixedVectorMath.RawMagnitude(value: o4) | FixedVectorMath.RawMagnitude(value: o5) |
             FixedVectorMath.RawMagnitude(value: o6) | FixedVectorMath.RawMagnitude(value: o7)) < (1UL << 29)) {
            norm = FixedQ4816.RoundProductSum(productSum: unchecked(((((((((o0 * o0) + (o1 * o1)) + (o2 * o2)) + (o3 * o3)) + (o4 * o4)) + (o5 * o5)) + (o6 * o6)) + (o7 * o7))));
        } else {
            norm = FixedQ4816.RoundProductSum(productSum: unchecked((((((((((Int128)o0 * o0) + ((Int128)o1 * o1)) + ((Int128)o2 * o2)) + ((Int128)o3 * o3)) + ((Int128)o4 * o4)) + ((Int128)o5 * o5)) + ((Int128)o6 * o6)) + ((Int128)o7 * o7))));
        }

        return Unsafe.BitCast<LeafQuaternion, TInner>(source: new LeafQuaternion(
            Left: new LeafComplex(Left: Leaf(raw: norm), Right: FixedScalarRing.AdditiveIdentity),
            Right: LeafComplex.AdditiveIdentity
        ));
    }
}

/// <summary>
/// The floor-zero carrier for the doubling construction: the raw signed fixed-point scalar <see cref="FixedQ4816"/>
/// presented as an <see cref="IConjugationRing{TSelf}"/> whose conjugation is the identity (a real number is its own
/// conjugate). Doubling this reproduces <see cref="FixedComplex"/>; doubling twice reproduces
/// <see cref="FixedQuaternion"/> — bit-for-bit wherever the inputs avoid rounding.
/// </summary>
/// <param name="Value">The wrapped scalar.</param>
public readonly record struct FixedScalarRing(FixedQ4816 Value)
    : IConjugationRing<FixedScalarRing> {
    /// <summary>Gets the additive identity, zero.</summary>
    public static FixedScalarRing AdditiveIdentity => new(Value: FixedQ4816.Zero);
    /// <summary>Gets the multiplicative identity, one.</summary>
    public static FixedScalarRing MultiplicativeIdentity => new(Value: FixedQ4816.One);

    /// <summary>Adds two scalars.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum.</returns>
    public static FixedScalarRing Add(FixedScalarRing left, FixedScalarRing right) =>
        new(Value: (left.Value + right.Value));
    /// <summary>Returns the conjugate — the identity, since a real scalar is self-conjugate.</summary>
    /// <param name="value">The scalar.</param>
    /// <returns><paramref name="value"/> unchanged.</returns>
    public static FixedScalarRing Conjugate(FixedScalarRing value) =>
        value;
    /// <summary>Multiplies two scalars (rounding to nearest, ties to even, as the carrier does).</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The rounded product.</returns>
    public static FixedScalarRing Multiply(FixedScalarRing left, FixedScalarRing right) =>
        new(Value: (left.Value * right.Value));
    /// <summary>Negates a scalar.</summary>
    /// <param name="value">The scalar to negate.</param>
    /// <returns>The negation.</returns>
    public static FixedScalarRing Negate(FixedScalarRing value) =>
        new(Value: -value.Value);
    /// <summary>Subtracts one scalar from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference.</returns>
    public static FixedScalarRing Subtract(FixedScalarRing left, FixedScalarRing right) =>
        new(Value: (left.Value - right.Value));
}

/// <summary>
/// The complex plane presented as an <see cref="IConjugationRing{TSelf}"/>: the house <see cref="FixedComplex"/> with
/// its native fused multiply and its complex conjugation. Doubling this yields the quaternions built directly on
/// <see cref="FixedComplex"/> — the same floor-two algebra reached by doubling <see cref="FixedScalarRing"/> twice,
/// differing only in where the fixed-point rounding falls, and identical to it wherever the inputs avoid rounding.
/// </summary>
/// <param name="Value">The wrapped complex number.</param>
public readonly record struct FixedComplexRing(FixedComplex Value)
    : IConjugationRing<FixedComplexRing> {
    /// <summary>Gets the additive identity, zero.</summary>
    public static FixedComplexRing AdditiveIdentity => new(Value: FixedComplex.AdditiveIdentity);
    /// <summary>Gets the multiplicative identity, one.</summary>
    public static FixedComplexRing MultiplicativeIdentity => new(Value: FixedComplex.MultiplicativeIdentity);

    /// <summary>Adds two complex numbers.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum.</returns>
    public static FixedComplexRing Add(FixedComplexRing left, FixedComplexRing right) =>
        new(Value: (left.Value + right.Value));
    /// <summary>Returns the complex conjugate (the imaginary part negated).</summary>
    /// <param name="value">The complex number.</param>
    /// <returns>The conjugate.</returns>
    public static FixedComplexRing Conjugate(FixedComplexRing value) =>
        new(Value: value.Value.Conjugate());
    /// <summary>Multiplies two complex numbers by the native fused complex product.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product.</returns>
    public static FixedComplexRing Multiply(FixedComplexRing left, FixedComplexRing right) =>
        new(Value: (left.Value * right.Value));
    /// <summary>Negates a complex number.</summary>
    /// <param name="value">The complex number to negate.</param>
    /// <returns>The negation.</returns>
    public static FixedComplexRing Negate(FixedComplexRing value) =>
        new(Value: -value.Value);
    /// <summary>Subtracts one complex number from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference.</returns>
    public static FixedComplexRing Subtract(FixedComplexRing left, FixedComplexRing right) =>
        new(Value: (left.Value - right.Value));
}
