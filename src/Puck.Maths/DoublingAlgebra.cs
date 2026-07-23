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
/// <c>Right.Right = z</c> — verified against that oracle (<c>tools/doubling-algebra-verifier.cs</c>) rather than adopted
/// on faith. The mixed operand orders (<c>d·a</c> and <c>b·c̄</c>, not <c>a·d</c> and <c>c̄·b</c>) are load-bearing once
/// the inner floor stops commuting.
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
/// under bitwise equality.
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
    public static DoublingAlgebra<TInner> Multiply(DoublingAlgebra<TInner> left, DoublingAlgebra<TInner> right) {
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
    public static TInner Norm(DoublingAlgebra<TInner> value) =>
        TInner.Add(
            left: TInner.Multiply(left: value.Left, right: TInner.Conjugate(value: value.Left)),
            right: TInner.Multiply(left: TInner.Conjugate(value: value.Right), right: value.Right)
        );
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
