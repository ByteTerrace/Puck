using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The any-degree adjunction: the ring obtained by adjoining one root <c>x</c> of one monic polynomial
/// <c>x^n + m_{n-1}·x^{n-1} + … + m_1·x + m_0</c> to a carrier ring <typeparamref name="TScalar"/>. It frees the degree
/// that <see cref="QuadraticAlgebra{TScalar}"/> fixes at two: degree 2 IS <see cref="QuadraticAlgebra{TScalar}"/> — the
/// modulus <c>x² − P·x − Q</c> — and degree <c>k</c> over the two-element carrier is the <see cref="BinaryField{T}"/>
/// tower, the modulus being that field's irreducible polynomial. One root, one monic modulus, one swappable carrier.
/// </summary>
/// <typeparam name="TScalar">
/// The carrier ring. The constraint set is exactly the one <see cref="QuadraticAlgebra{TScalar}"/> uses — the minimal
/// group of generic-math operator and identity interfaces that expresses a commutative ring while admitting the house
/// fixed-point types, <see cref="System.Numerics.BigInteger"/>, a modular residue, and a one-bit two-element carrier
/// alike. No ordering and, deliberately, no division: every operation below is expressed in ring additions, subtractions
/// and multiplications only, so the two-element carrier — where the only non-zero pivot is its own inverse and true
/// division would stall on a zero pivot — is as valid a substitution as an exact integer.
/// </typeparam>
/// <remarks>
/// A default descriptor names no algebra because it carries no modulus; every public property and semantic operation
/// rejects that state with <see cref="InvalidOperationException"/>. Elements and projective windows have structural
/// receiver affinity: an operation requires their coordinate count to equal the receiver's degree, but deliberately
/// interprets an equal-length vector under the receiver's modulus even when another same-degree algebra created it.
/// Default nested values carry no vector and are likewise invalid.
/// <para>
/// Equality follows that split exactly, and is structural on both sides of it rather than the array reference identity
/// the backing vectors would otherwise supply: a descriptor is its modulus tail, so two descriptors built from equal
/// tails are equal; an <see cref="Element"/> or <see cref="Projective"/> is its coordinate vector alone, carrying no
/// modulus, so two of them agree exactly when their coordinates agree — which is what the reinterpretation contract
/// above already lets a receiver assume. A default value on either side equals only another default. Hashing folds with
/// <see cref="Fnv1aHash"/> rather than <see cref="System.HashCode"/>, whose seed is randomized per process, so a digest
/// is the same value on every run and every machine wherever the carrier's own hash is.</para>
/// <para>
/// Over the house scalar <see cref="FixedQ4816"/> the fused one-rounding discipline is unconditional: for any monic tail
/// and any degree <c>n ≥ 2</c>, every returned component of <see cref="Multiply"/>, <see cref="Norm"/>,
/// <see cref="Trace"/>, and <see cref="ProjectiveStep"/> is one ties-to-even rounding of the exact rational value of the
/// ideal expression, wrapped to the raw carrier — there is no degree cliff and no coefficient-magnitude cliff.
/// <see cref="CompanionPower"/> inherits that discipline per step rather than per result: it is square-and-multiply over
/// the one-rounding <see cref="Multiply"/>, so its coordinates carry a chain of roundings whose length grows with the
/// exponent's bit count. <b>Degree 2 is the quadratic algebra</b>: the tail <c>[m_0, m_1]</c> is the relation
/// <c>(P, Q) = (−m_1, −m_0)</c>, so the whole degree-2 surface delegates to a stored
/// <see cref="QuadraticAlgebra{TScalar}"/> twin, making the discipline (and the bit-identity with it) structural rather
/// than a reproduced kernel. A tail the carrier cannot negate exactly — a coefficient at the house carrier's raw
/// <see cref="long.MinValue"/>, which negates to itself — builds no twin and runs the general lanes below instead, the
/// relation never being negated there. Beyond degree 2 two lanes serve the house scalar, chosen once per algebra by tail
/// shape. The <em>integer lane</em> folds the widened Q32 coordinate columns in-cascade at <c>Int128</c> width, the
/// integer coefficients entering as plain multipliers, and rounds each coordinate once at shift <c>16</c>; the fold is
/// congruent modulo <c>2^128</c> and the shift never exceeds <c>64</c>, so an overflowing column wraps harmlessly. The
/// <em>limb lane</em> is an exact signed multi-limb accumulator — little-endian
/// sign-magnitude <c>ulong</c> spans, schoolbook add/subtract/multiply-by-integer/shift, one final ties-to-even rounding
/// at the known shift, wrapped to <c>64</c> bits — that carries fractional higher-degree <see cref="Multiply"/>,
/// <see cref="Trace"/>, and <see cref="Norm"/>, and every integer-tail <see cref="Norm"/> determinant from degree
/// <c>5</c> up, where the <c>Int128</c> first-row cofactor expansion (written for order <c>≤ 4</c>) runs out and the
/// rounding shift <c>16(n−1)</c> reaches its harmless bound of <c>64</c>. Its limb width is derived at construction from
/// the degree and the maximum tail magnitude; the multiply and trace accumulators are stack-allocated within a bounded
/// degree and pooled beyond, while the norm determinant pools at every degree it serves. Every other carrier keeps the
/// generic per-product path unchanged.</para>
/// </remarks>
public readonly struct MonogenicAlgebra<TScalar> : IEquatable<MonogenicAlgebra<TScalar>>
    where TScalar : IAdditionOperators<TScalar, TScalar, TScalar>,
                    ISubtractionOperators<TScalar, TScalar, TScalar>,
                    IMultiplyOperators<TScalar, TScalar, TScalar>,
                    IUnaryNegationOperators<TScalar, TScalar>,
                    IAdditiveIdentity<TScalar, TScalar>,
                    IMultiplicativeIdentity<TScalar, TScalar> {
    /// <summary>The modulus tail <c>[m_0, m_1, …, m_{n-1}]</c>, low exponent first; the leading <c>x^n</c> is implicit.</summary>
    private readonly TScalar[] _modulus;
    // Derived at construction over the house scalar FixedQ4816: the fused one-rounding discipline is UNCONDITIONAL for
    // every degree n ≥ 2 and every coefficient — there is no degree cliff and no coefficient-magnitude cliff.
    // _integerTail records that every tail coefficient is an exact integer (raw an exact multiple of 2^16, any magnitude
    // the carrier can hold); together with the degree it selects the per-operation lane (see the routing tables on each
    // op). An integer tail enters the sums as a plain integer multiplier, so the fold cascade runs in-cascade at Int128
    // width and rounds each coordinate once: the wrap-harmlessness theorem makes that congruence exact modulo 2^128 and
    // harmless because the rounding shift stays at or below 64 bits. A fractional tail routes degree 2 to the
    // QuadraticAlgebra Q48 lane it must reproduce bit-for-bit and any higher degree to the exact multi-limb lane; the
    // widest integer-tail Norms fall to the limb lane too. The flag is value-independent per algebra: the discipline
    // holds across the whole raw range.
    private readonly bool _integerTail;
    // Whether the degree-2 surface (Multiply, Norm, Trace, CompanionPower, ProjectiveStep) delegates to the stored twin.
    // Degree 2 IS QuadraticAlgebra: the tail [m_0, m_1] is the relation (P, Q) = (−m_1, −m_0), so delegating maps the
    // pair coordinates at the boundary — identity by construction rather than a maintained parallel kernel. Set only at
    // degree 2 (where the constraint set matches exactly) and only for a tail the carrier negates exactly; a tail
    // carrying the house carrier's raw long.MinValue, which negates to itself, would build a sign-flipped relation, so
    // it keeps the general lanes, which never negate the modulus.
    private readonly bool _delegatesQuadratic;
    // The degree-2 twin, built when _delegatesQuadratic is set and left default otherwise.
    private readonly QuadraticAlgebra<TScalar> _quadratic;

    /// <summary>Wraps an already-validated modulus tail, classifying the fused lane once and building the degree-2 twin where the tail admits one.</summary>
    /// <param name="modulus">The low-to-high coefficient tail, its length being the degree.</param>
    private MonogenicAlgebra(TScalar[] modulus) {
        _modulus = modulus;
        _integerTail = (IsHouseCarrier && (modulus.Length >= 2) && HasIntegerTail(modulus: modulus));
        _delegatesQuadratic = ((2 == modulus.Length) && HasNegatableTail(modulus: modulus));
        _quadratic = (_delegatesQuadratic
            ? QuadraticAlgebra<TScalar>.Create(p: -modulus[1], q: -modulus[0])
            : default);
    }

    /// <summary>
    /// An element of the algebra, the immutable coefficient vector <c>[c_0, c_1, …, c_{n-1}]</c> of
    /// <c>c_0 + c_1·x + … + c_{n-1}·x^{n-1}</c> in the power basis.
    /// </summary>
    /// <remarks>
    /// The vector is array-backed, so every algebra operation that returns an <see cref="Element"/> allocates one
    /// carrier array of length <c>n</c> (and <see cref="Multiply"/> a further length-<c>(2n-1)</c> scratch buffer). This
    /// is the honest cost of a degree the type cannot know at compile time. Degree 2 delegates, wherever a twin is built,
    /// to a stored <see cref="QuadraticAlgebra{TScalar}"/> twin, whose scalar-pair <see cref="QuadraticAlgebra{TScalar}.Element"/>
    /// eliminates every scratch and intermediate array; only the returned coordinate pair is materialized (see
    /// <see cref="Create"/>).
    /// Further tiers (a bit-packed two-element carrier) can specialize other common degrees without changing this shape.
    /// A default <see cref="Element"/> carries no vector and is not a member of any algebra; its public accessors throw
    /// <see cref="InvalidOperationException"/>. Obtain elements from the algebra's <see cref="Zero"/>, <see cref="One"/>,
    /// <see cref="Root"/>, or <see cref="FromCoordinates"/>. Elements do not retain a modulus identity: receiver
    /// operations reject a different dimension but intentionally reinterpret equal-dimensional coordinates under the
    /// receiver's modulus.
    /// </remarks>
    public readonly struct Element : IEquatable<Element> {
        /// <summary>The power-basis coordinate vector, low exponent first.</summary>
        private readonly TScalar[] _coordinates;

        /// <summary>Adopts a coordinate vector without copying; internal callers never alias.</summary>
        /// <param name="coordinates">The vector to adopt.</param>
        internal Element(TScalar[] coordinates) =>
            _coordinates = coordinates;

        /// <summary>Gets the power-basis coordinate vector, low exponent first.</summary>
        /// <exception cref="InvalidOperationException">This is a default element and carries no coordinate vector.</exception>
        public ReadOnlySpan<TScalar> Coordinates =>
            GetCoordinates();
        /// <summary>Gets the number of coordinates, which is the algebra's degree.</summary>
        /// <exception cref="InvalidOperationException">This is a default element and carries no coordinate vector.</exception>
        public int Dimension =>
            GetCoordinates().Length;
        /// <summary>Gets the coefficient of <c>x^<paramref name="exponent"/></c>.</summary>
        /// <param name="exponent">The basis exponent in <c>[0, Dimension)</c>.</param>
        /// <returns>The coordinate at <paramref name="exponent"/>.</returns>
        /// <exception cref="InvalidOperationException">This is a default element and carries no coordinate vector.</exception>
        public TScalar this[int exponent] =>
            GetCoordinates()[exponent];

        internal int RawDimension =>
            (_coordinates?.Length ?? 0);

        /// <summary>Indicates whether this element carries the same coordinates as another.</summary>
        /// <param name="other">The element to compare against.</param>
        /// <returns><see langword="true"/> when both carry equal-length, coordinate-wise equal vectors, or both are
        /// default; otherwise <see langword="false"/>.</returns>
        /// <remarks>An element's identity is its coordinate vector alone — the modulus is deliberately no part of it.
        /// An element stores no modulus to compare, and the receiver-affinity contract is exactly that an
        /// equal-dimensional vector is reinterpreted under whichever algebra consumes it, so two elements carrying the
        /// same coordinates are the same value however they were built. Modulus identity lives on the descriptor,
        /// which compares its own tail.</remarks>
        public bool Equals(Element other) =>
            VectorsEqual(left: _coordinates, right: other._coordinates);

        /// <inheritdoc/>
        public override bool Equals(object? obj) =>
            ((obj is Element other) && Equals(other: other));

        /// <summary>Returns a hash code over the coordinate vector.</summary>
        /// <returns>A hash code consistent with <see cref="Equals(Element)"/>.</returns>
        /// <remarks>An <see cref="Fnv1aHash"/> fold over the carrier's own hash codes — pure integer arithmetic with
        /// no per-process seed, unlike <see cref="System.HashCode"/>.</remarks>
        public override int GetHashCode() =>
            VectorHash(vector: _coordinates);

        /// <summary>Indicates whether two elements carry the same coordinates.</summary>
        /// <param name="left">The first element.</param>
        /// <param name="right">The second element.</param>
        /// <returns><see langword="true"/> when the coordinate vectors are equal; otherwise <see langword="false"/>.</returns>
        public static bool operator ==(Element left, Element right) =>
            left.Equals(other: right);

        /// <summary>Indicates whether two elements carry different coordinates.</summary>
        /// <param name="left">The first element.</param>
        /// <param name="right">The second element.</param>
        /// <returns><see langword="true"/> when the coordinate vectors differ; otherwise <see langword="false"/>.</returns>
        public static bool operator !=(Element left, Element right) =>
            !left.Equals(other: right);

        /// <summary>Returns the coordinate vector or deliberately diagnoses a default element.</summary>
        /// <returns>The initialized coordinate vector.</returns>
        /// <exception cref="InvalidOperationException">This is a default element.</exception>
        private TScalar[] GetCoordinates() =>
            (_coordinates ?? ThrowUninitialized());

        /// <summary>Throws the default-element diagnosis.</summary>
        /// <returns>Never returns.</returns>
        /// <exception cref="InvalidOperationException">Always.</exception>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static TScalar[] ThrowUninitialized() =>
            throw new InvalidOperationException(
                message: "The monogenic element is default-initialized and belongs to no algebra; obtain it from a MonogenicAlgebra<TScalar> descriptor."
            );
    }

    /// <summary>
    /// A projective coordinate vector <c>[v_0, v_1, …, v_{n-1}]</c> — the sliding window of an order-<c>n</c> linear
    /// recurrence, most recent term first — that a <see cref="ProjectiveStep"/> iterate carries without dividing.
    /// </summary>
    /// <remarks>
    /// The degree-2 case is the <c>(numerator : denominator)</c> pair a continued-fraction convergent holds. A default
    /// <see cref="Projective"/> carries no vector and its public accessors throw <see cref="InvalidOperationException"/>.
    /// Windows do not retain a modulus identity: a receiver rejects a different dimension but intentionally reinterprets
    /// an equal-dimensional window under its own modulus.
    /// </remarks>
    public readonly struct Projective : IEquatable<Projective> {
        /// <summary>The window coordinates, most recent term first.</summary>
        private readonly TScalar[] _coordinates;

        /// <summary>Adopts a coordinate vector without copying; internal callers never alias.</summary>
        /// <param name="coordinates">The vector to adopt.</param>
        internal Projective(TScalar[] coordinates) =>
            _coordinates = coordinates;

        /// <summary>Gets the window coordinates, most recent term first.</summary>
        /// <exception cref="InvalidOperationException">This is a default window and carries no coordinate vector.</exception>
        public ReadOnlySpan<TScalar> Coordinates =>
            GetCoordinates();
        /// <summary>Gets the number of coordinates, which is the algebra's degree.</summary>
        /// <exception cref="InvalidOperationException">This is a default window and carries no coordinate vector.</exception>
        public int Dimension =>
            GetCoordinates().Length;
        /// <summary>Gets the window coordinate at <paramref name="offset"/> terms back.</summary>
        /// <param name="offset">The window offset in <c>[0, Dimension)</c>.</param>
        /// <returns>The coordinate at <paramref name="offset"/>.</returns>
        /// <exception cref="InvalidOperationException">This is a default window and carries no coordinate vector.</exception>
        public TScalar this[int offset] =>
            GetCoordinates()[offset];

        internal int RawDimension =>
            (_coordinates?.Length ?? 0);

        /// <summary>Indicates whether this window carries the same coordinates as another.</summary>
        /// <param name="other">The window to compare against.</param>
        /// <returns><see langword="true"/> when both carry equal-length, coordinate-wise equal vectors, or both are
        /// default; otherwise <see langword="false"/>.</returns>
        /// <remarks>A window's identity is its coordinate vector alone, for the same reason
        /// <see cref="Element.Equals(Element)"/> gives: it stores no modulus, and a receiver deliberately reinterprets
        /// an equal-dimensional window under its own.</remarks>
        public bool Equals(Projective other) =>
            VectorsEqual(left: _coordinates, right: other._coordinates);

        /// <inheritdoc/>
        public override bool Equals(object? obj) =>
            ((obj is Projective other) && Equals(other: other));

        /// <summary>Returns a hash code over the coordinate vector.</summary>
        /// <returns>A hash code consistent with <see cref="Equals(Projective)"/>.</returns>
        /// <remarks>An <see cref="Fnv1aHash"/> fold over the carrier's own hash codes — pure integer arithmetic with
        /// no per-process seed, unlike <see cref="System.HashCode"/>.</remarks>
        public override int GetHashCode() =>
            VectorHash(vector: _coordinates);

        /// <summary>Indicates whether two windows carry the same coordinates.</summary>
        /// <param name="left">The first window.</param>
        /// <param name="right">The second window.</param>
        /// <returns><see langword="true"/> when the coordinate vectors are equal; otherwise <see langword="false"/>.</returns>
        public static bool operator ==(Projective left, Projective right) =>
            left.Equals(other: right);

        /// <summary>Indicates whether two windows carry different coordinates.</summary>
        /// <param name="left">The first window.</param>
        /// <param name="right">The second window.</param>
        /// <returns><see langword="true"/> when the coordinate vectors differ; otherwise <see langword="false"/>.</returns>
        public static bool operator !=(Projective left, Projective right) =>
            !left.Equals(other: right);

        /// <summary>Returns the coordinate vector or deliberately diagnoses a default window.</summary>
        /// <returns>The initialized coordinate vector.</returns>
        /// <exception cref="InvalidOperationException">This is a default window.</exception>
        private TScalar[] GetCoordinates() =>
            (_coordinates ?? ThrowUninitialized());

        /// <summary>Throws the default-window diagnosis.</summary>
        /// <returns>Never returns.</returns>
        /// <exception cref="InvalidOperationException">Always.</exception>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static TScalar[] ThrowUninitialized() =>
            throw new InvalidOperationException(
                message: "The monogenic projective window is default-initialized and belongs to no algebra; obtain it from MonogenicAlgebra<TScalar>.FromWindow."
            );
    }

    /// <summary>Creates the algebra <c>TScalar[x] / (x^n + m_{n-1}·x^{n-1} + … + m_0)</c> from the monic modulus' tail.</summary>
    /// <param name="monicModulus">
    /// The modulus below its leading term, low exponent first: entry <c>i</c> is the coefficient of <c>x^i</c> for
    /// <c>i</c> in <c>[0, n)</c>, and the leading <c>x^n</c> is implicit. Its length is the degree <c>n ≥ 1</c>.
    /// </param>
    /// <returns>The described algebra.</returns>
    /// <remarks>
    /// The quadratic relation <c>x² = P·x + Q</c> is the tail <c>[−Q, −P]</c>; the degree-8 byte field is the tail of its
    /// irreducible modulus. Every tail defines a valid unital rank-<c>n</c> algebra over the carrier, so nothing but the
    /// empty tail is rejected; whether the result is a field, a product of rings, or carries nilpotents is read from
    /// <see cref="CharacteristicDiscriminant"/> rather than enforced here.
    /// <para>Over the house scalar <see cref="FixedQ4816"/> the fused one-rounding discipline is unconditional — every
    /// returned component of <see cref="Multiply"/>, <see cref="Norm"/>, <see cref="Trace"/>, and
    /// <see cref="ProjectiveStep"/> is one ties-to-even rounding of the exact rational value of the ideal expression,
    /// wrapped to the raw carrier, at every degree <c>n ≥ 2</c> and every coefficient magnitude; the
    /// <see cref="CompanionPower"/> they drive is square-and-multiply over that <see cref="Multiply"/>, so it rounds once
    /// per step rather than once per result.
    /// <b>Degree 2 delegates wholesale to a stored <see cref="QuadraticAlgebra{TScalar}"/> twin</b> under the tail
    /// correspondence <c>[−Q, −P] ↔ (P, Q)</c>, so it IS that algebra — and thus <see cref="FixedComplex"/>,
    /// <see cref="FixedSplit"/>, and <see cref="FixedDual{TValue}"/> at <c>(0, −1)</c>/<c>(0, +1)</c>/<c>(0, 0)</c> — by
    /// construction, integer and fractional tails alike. The lone exception is a tail the carrier cannot negate exactly
    /// (a coefficient at the house carrier's raw <see cref="long.MinValue"/>): no twin is built, and degree 2 runs the
    /// general lanes, which never negate the relation and round exactly once all the same. Beyond degree 2 the tail shape
    /// selects the lane. An integer tail folds in-cascade at <c>Int128</c> width: every raw Q32 coordinate product is
    /// widened once, the companion fold runs on the wide columns with the integer coefficients as plain multipliers
    /// (congruent modulo <c>2^128</c>), and each output coordinate rounds once at shift <c>16</c> — a shift at or below
    /// <c>64</c>, so a column that overflows <c>128</c> bits wraps harmlessly. A fractional higher-degree tail routes to
    /// the exact multi-limb lane. <see cref="ProjectiveStep"/> rounds its single leading combination once at every
    /// degree; every integer-tail <see cref="Norm"/> determinant from degree <c>5</c> up (past the <c>Int128</c> cofactor
    /// expansion's order, and at the harmless bound of its rounding shift <c>16(n−1)</c>) and every fractional
    /// higher-degree <see cref="Norm"/>, <see cref="Multiply"/>, and <see cref="Trace"/> fall to the exact multi-limb
    /// lane, which forms the whole integer numerator and rounds once. Every other carrier keeps the per-product path,
    /// which rounds each carrier product before combining — the only discipline a carrier that cannot express raw fusion
    /// can offer.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="monicModulus"/> is empty; the degree must be at least one.</exception>
    public static MonogenicAlgebra<TScalar> Create(ReadOnlySpan<TScalar> monicModulus) {
        if (monicModulus.IsEmpty) {
            throw new ArgumentException(message: "A monogenic algebra needs a monic modulus of degree at least one.", paramName: nameof(monicModulus));
        }

        return new MonogenicAlgebra<TScalar>(modulus: monicModulus.ToArray());
    }

    // The house-carrier test. typeof(T) == typeof(U) is a JIT-time constant for every closed value-type instantiation,
    // so this compiles to a constant per carrier and a carrier other than FixedQ4816 never enters the fused kernels or
    // their raw casts — the reason those kernels may cast raws without a runtime type check.
    private static bool IsHouseCarrier =>
        (typeof(TScalar) == typeof(FixedQ4816));

    // True when the fused lanes serve this algebra: the house carrier at degree 2 or more. The degree test is a genuine
    // runtime test — degree 1 is the degenerate single-product case, left on the generic path.
    private bool UsesFusedLanes =>
        (IsHouseCarrier && (Degree >= 2));

    // True when every tail coefficient is an exact integer in the Q48.16 carrier (raw an exact multiple of 2^16), of any
    // magnitude the carrier can hold — there is no small-integer cap. The caller has already established the FixedQ4816
    // carrier, so the raw cast is safe.
    private static bool HasIntegerTail(TScalar[] modulus) {
        for (var index = 0; (index < modulus.Length); ++index) {
            var raw = Unsafe.BitCast<TScalar, FixedQ4816>(source: modulus[index]).Value;

            if (!FixedQ4816.IsExactInteger(raw: raw)) { return false; }
        }

        return true;
    }

    // True when the carrier negates every tail coefficient exactly. Over the house scalar the raw long.MinValue negates
    // to itself (the carrier's negation is unchecked), so the relation (P, Q) = (−m_1, −m_0) a degree-2 twin would be
    // built from carries a flipped sign; such a tail is reported here and left to the general lanes. Every other carrier
    // in the constraint set negates exactly, and the raw cast is reached only under the house-carrier test.
    private static bool HasNegatableTail(TScalar[] modulus) {
        if (!IsHouseCarrier) { return true; }

        for (var index = 0; (index < modulus.Length); ++index) {
            if (long.MinValue == Unsafe.BitCast<TScalar, FixedQ4816>(source: modulus[index]).Value) { return false; }
        }

        return true;
    }

    /// <summary>Gets the algebra's degree <c>n</c>, so the algebra has rank <c>n</c> over the carrier.</summary>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public int Degree =>
        GetModulus().Length;
    /// <summary>Gets the modulus tail <c>[m_0, …, m_{n-1}]</c>, low exponent first; the modulus is <c>x^n</c> plus this.</summary>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public ReadOnlySpan<TScalar> Modulus =>
        GetModulus();
    /// <summary>Gets the multiplicative identity, <c>1</c>.</summary>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Element One {
        get {
            var coordinates = ZeroVector();

            coordinates[0] = TScalar.MultiplicativeIdentity;

            return new Element(coordinates: coordinates);
        }
    }
    /// <summary>Gets the adjoined root itself, the element <c>x</c> — the generator whose powers drive the companion sequences.</summary>
    /// <remarks>At degree one the root reduces to the scalar <c>−m_0</c>, there being no <c>x</c> in the basis.</remarks>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Element Root =>
        MultiplyByRoot(value: One);
    /// <summary>Gets the additive identity, <c>0</c>.</summary>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Element Zero =>
        new(coordinates: ZeroVector());

    /// <summary>Creates an element from its power-basis coordinates, low exponent first.</summary>
    /// <param name="coordinates">The coordinate vector, whose length must equal the algebra's <see cref="Degree"/>.</param>
    /// <returns>The described element.</returns>
    /// <exception cref="ArgumentException"><paramref name="coordinates"/> does not have length <see cref="Degree"/>.</exception>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Element FromCoordinates(ReadOnlySpan<TScalar> coordinates) {
        var n = Degree;

        if (coordinates.Length != n) {
            throw new ArgumentException(message: $"An element of a degree-{n} algebra needs exactly {n} coordinates.", paramName: nameof(coordinates));
        }

        return new Element(coordinates: coordinates.ToArray());
    }
    /// <summary>Creates a projective coordinate window from its coordinates, most recent term first.</summary>
    /// <param name="window">The window coordinates, whose length must equal the algebra's <see cref="Degree"/>.</param>
    /// <returns>The described window.</returns>
    /// <exception cref="ArgumentException"><paramref name="window"/> does not have length <see cref="Degree"/>.</exception>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Projective FromWindow(ReadOnlySpan<TScalar> window) {
        var n = Degree;

        if (window.Length != n) {
            throw new ArgumentException(message: $"A window over a degree-{n} algebra needs exactly {n} coordinates.", paramName: nameof(window));
        }

        return new Projective(coordinates: window.ToArray());
    }

    /// <summary>Adds two elements.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The coordinate-wise sum.</returns>
    /// <exception cref="ArgumentException"><paramref name="left"/> or <paramref name="right"/> does not have this
    /// algebra's degree.</exception>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Element Add(Element left, Element right) {
        var n = ValidateElement(value: left, paramName: nameof(left));

        ValidateElement(value: right, expectedDimension: n, paramName: nameof(right));

        var result = new TScalar[n];

        for (var index = 0; (index < n); ++index) { result[index] = (left[index] + right[index]); }

        return new Element(coordinates: result);
    }
    /// <summary>Subtracts one element from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The coordinate-wise difference.</returns>
    /// <exception cref="ArgumentException"><paramref name="left"/> or <paramref name="right"/> does not have this
    /// algebra's degree.</exception>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Element Subtract(Element left, Element right) {
        var n = ValidateElement(value: left, paramName: nameof(left));

        ValidateElement(value: right, expectedDimension: n, paramName: nameof(right));

        var result = new TScalar[n];

        for (var index = 0; (index < n); ++index) { result[index] = (left[index] - right[index]); }

        return new Element(coordinates: result);
    }
    /// <summary>Negates an element.</summary>
    /// <param name="value">The element to negate.</param>
    /// <returns>The coordinate-wise negation.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> does not have this algebra's degree.</exception>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Element Negate(Element value) {
        var n = ValidateElement(value: value, paramName: nameof(value));
        var result = new TScalar[n];

        for (var index = 0; (index < n); ++index) { result[index] = -value[index]; }

        return new Element(coordinates: result);
    }
    /// <summary>Multiplies two elements, folding every power at or above <c>x^n</c> back through the companion recurrence.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The reduced product.</returns>
    /// <remarks>
    /// Schoolbook convolution into a degree-<c>(2n-2)</c> buffer, then a single high-to-low reduction pass that rewrites
    /// each <c>x^d</c> with <c>d ≥ n</c> as <c>−Σ_j m_j·x^{d-n+j}</c> — the companion recurrence, using only carrier
    /// multiplications and subtractions, never a division. The cost is <c>O(n²)</c> carrier multiplications.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="left"/> or <paramref name="right"/> does not have this
    /// algebra's degree.</exception>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Element Multiply(Element left, Element right) {
        var n = ValidateElement(value: left, paramName: nameof(left));

        ValidateElement(value: right, expectedDimension: n, paramName: nameof(right));

        // Degree 2 IS the quadratic algebra: delegate to the twin, mapping the coordinate pair at the boundary. Every
        // fused lane (integer, fractional) and the exact generic path live in QuadraticAlgebra, so the bit-identity is
        // structural — this carries no degree-2 kernel of its own. A degree-2 tail with no exact negation builds no twin
        // and takes the lanes below.
        if (_delegatesQuadratic) {
            var product = _quadratic.Multiply(
                left: new QuadraticAlgebra<TScalar>.Element(U: left[0], V: left[1]),
                right: new QuadraticAlgebra<TScalar>.Element(U: right[0], V: right[1])
            );

            return new Element(coordinates: [product.U, product.V]);
        }

        if (UsesFusedLanes) {
            return (_integerTail
                ? MultiplyIntegerCascade(left: left, right: right)
                : MultiplyLimb(left: left, right: right));
        }

        var wide = new TScalar[((2 * n) - 1)];

        for (var index = 0; (index < wide.Length); ++index) { wide[index] = TScalar.AdditiveIdentity; }

        for (var i = 0; (i < n); ++i) {
            for (var j = 0; (j < n); ++j) { wide[(i + j)] = (wide[(i + j)] + (left[i] * right[j])); }
        }

        ReduceInPlace(wide: wide);

        var result = new TScalar[n];

        Array.Copy(sourceArray: wide, destinationArray: result, length: n);

        return new Element(coordinates: result);
    }
    /// <summary>Multiplies an element by the adjoined root — one companion step, division-free.</summary>
    /// <param name="value">The element to advance.</param>
    /// <returns>The product <c>value·x</c>, reduced.</returns>
    /// <remarks>
    /// The coordinates shift up one exponent and the displaced top coordinate <c>c_{n-1}</c>, now the coefficient of
    /// <c>x^n</c>, is folded back as <c>−c_{n-1}·m_j</c> into each coordinate. Over the two-element carrier this is exactly
    /// <see cref="BinaryField{T}"/>'s multiply-by-<c>t</c>: shift left, and exclusive-or the modulus tail when the top bit
    /// falls out.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="value"/> does not have this algebra's degree.</exception>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Element MultiplyByRoot(Element value) {
        var n = ValidateElement(value: value, paramName: nameof(value));
        var top = value[(n - 1)];
        var result = new TScalar[n];

        result[0] = (TScalar.AdditiveIdentity - (top * _modulus[0]));

        for (var index = 1; (index < n); ++index) { result[index] = (value[(index - 1)] - (top * _modulus[index])); }

        return new Element(coordinates: result);
    }
    /// <summary>Raises the adjoined root to a power by fast exponentiation — the closed-form engine for the companion sequences.</summary>
    /// <param name="exponent">The power; zero yields <see cref="One"/>.</param>
    /// <returns>The element <c>x^exponent</c>, reduced.</returns>
    /// <remarks>
    /// Square-and-multiply over the exponent's binary expansion, so the operation count depends on the exponent and the
    /// routine is not constant-time in it. Over an exact carrier this evaluates the order-<c>n</c> linear recurrence
    /// <c>a_k = −Σ_j m_j·a_{k-n+j}</c> in logarithmic time — the closed form the term-by-term recurrence unrolls to. Over
    /// the house scalar <see cref="FixedQ4816"/> each <see cref="Multiply"/> in the chain rounds once, so a companion
    /// power carries one rounding per squaring and per multiply — not one rounding of the ideal power.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Element CompanionPower(ulong exponent) {
        _ = GetModulus();

        if (_delegatesQuadratic) {
            var twinPower = _quadratic.CompanionPower(exponent: exponent);

            return new Element(coordinates: [twinPower.U, twinPower.V]);
        }

        // Everything else drives the routed Multiply: an integer-tail FixedQ4816 algebra inherits the in-cascade Int128
        // rounding, a fractional one the exact limb rounding, and every other carrier the schoolbook per-product path.
        var result = One;
        var power = Root;

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) { result = Multiply(left: result, right: power); }

            exponent >>>= 1;

            if (0UL != exponent) { power = Multiply(left: power, right: power); }
        }

        return result;
    }
    /// <summary>Advances one step of the companion action on a projective coordinate window, without dividing.</summary>
    /// <param name="window">The current window <c>[a_k, a_{k-1}, …, a_{k-n+1}]</c>, most recent term first.</param>
    /// <returns>The next window <c>[a_{k+1}, a_k, …, a_{k-n+2}]</c>, where <c>a_{k+1} = −Σ_i m_{n-1-i}·window[i]</c>.</returns>
    /// <remarks>
    /// The degree-<c>n</c> generalization of the Möbius step: the companion-matrix action on projective coordinates. The
    /// leading coordinate is the next recurrence term and the rest slide down by one, so iterating from a seed window
    /// enumerates the recurrence's convergents. At degree two this is exactly the <c>(numerator : denominator) ↦
    /// (P·numerator + Q·denominator : numerator)</c> continued-fraction step.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="window"/> does not have this algebra's degree.</exception>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public Projective ProjectiveStep(Projective window) {
        var n = ValidateProjective(value: window, paramName: nameof(window));

        // Degree 2 IS the quadratic algebra's Möbius step: the window [v_0, v_1] (most recent first) is the projective
        // pair (numerator : denominator), so delegate to the twin and map at the boundary — identity by construction.
        if (_delegatesQuadratic) {
            var next = _quadratic.MobiusStep(pair: new QuadraticAlgebra<TScalar>.Projective(Numerator: window[0], Denominator: window[1]));

            return new Projective(coordinates: [next.Numerator, next.Denominator]);
        }

        // Over FixedQ4816 the leading term is a single linear combination Σ_i m_{n-1-i}·v_i, all summands at the same Q32
        // scale, so ONE rounding at shift 16 expresses it exactly at every degree — integer tails land on the exact
        // integer value, fractional tails on its correct rounding. The Int128 sum is unchecked: with the shift at 16 ≤ 64
        // an overflow past 128 bits wraps harmlessly (see FixedQ4816.RoundProductSum). Reached only under the
        // house-carrier guard, so the raw casts are safe.
        if (UsesFusedLanes) {
            return ProjectiveStepFused(window: window);
        }

        var head = TScalar.AdditiveIdentity;

        for (var index = 0; (index < n); ++index) { head = (head - (_modulus[((n - 1) - index)] * window[index])); }

        var result = new TScalar[n];

        result[0] = head;

        for (var index = 1; (index < n); ++index) { result[index] = window[(index - 1)]; }

        return new Projective(coordinates: result);
    }
    /// <summary>Computes the algebra trace of an element — the trace of multiplication-by-<paramref name="value"/>.</summary>
    /// <param name="value">The element whose trace is taken.</param>
    /// <returns>The sum of the diagonal of the multiplication matrix, the sum of <paramref name="value"/>'s conjugates.</returns>
    /// <remarks>Builds the <c>n×n</c> multiplication matrix one column at a time by repeated <see cref="MultiplyByRoot"/> and sums its diagonal — <c>O(n²)</c> carrier multiplications.</remarks>
    /// <exception cref="ArgumentException"><paramref name="value"/> does not have this algebra's degree.</exception>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public TScalar Trace(Element value) {
        var n = ValidateElement(value: value, paramName: nameof(value));

        // Degree 2 delegates its trace 2U + P·V to the twin under (P, Q) = (−m_1, −m_0) — identity by construction.
        if (_delegatesQuadratic) {
            return _quadratic.Trace(value: new QuadraticAlgebra<TScalar>.Element(U: value[0], V: value[1]));
        }

        // A fractional tail must round the diagonal exactly once: the generic path below rounds every MultiplyByRoot
        // product, so it routes to the exact limb lane. An integer tail keeps the generic path — its products are exact
        // (integer multipliers), so the diagonal sum is exact modulo the carrier's 2^64 wrap.
        if (UsesFusedLanes && !_integerTail) {
            return TraceLimb(value: value);
        }

        var column = value;
        var trace = column[0];

        for (var index = 1; (index < n); ++index) {
            column = MultiplyByRoot(value: column);
            trace = (trace + column[index]);
        }

        return trace;
    }
    /// <summary>Computes the algebra norm of an element — the determinant of multiplication-by-<paramref name="value"/>.</summary>
    /// <param name="value">The element whose norm is taken.</param>
    /// <returns>The determinant of the multiplication matrix, the product of <paramref name="value"/>'s conjugates.</returns>
    /// <remarks>
    /// Builds the <c>n×n</c> multiplication matrix (column <c>i</c> being <c>value·x^i</c>) and takes its determinant by
    /// exact cofactor expansion for <c>n ≤ 4</c> and by the division-free Samuelson–Berkowitz elimination beyond. Both are
    /// exact and use only carrier ring operations; a fraction-free (Bareiss) elimination is deliberately not used because
    /// it divides by pivots the carrier is not required to be able to invert — the two-element carrier stalls on a zero
    /// pivot — whereas Berkowitz needs no division at all. An element is a unit exactly when its norm is a carrier unit,
    /// and the zero divisors are the norm-zero elements. Cost is <c>O(n³)</c> for the small cofactor cases and
    /// <c>O(n⁴)</c> for Berkowitz.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="value"/> does not have this algebra's degree.</exception>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public TScalar Norm(Element value) {
        var n = ValidateElement(value: value, paramName: nameof(value));

        // Degree 2 delegates its norm U² + P·U·V − Q·V² to the twin under (P, Q) = (−m_1, −m_0) — identity by
        // construction, routing through the same fused integer/fractional lanes it once reproduced.
        if (_delegatesQuadratic) {
            return _quadratic.Norm(value: new QuadraticAlgebra<TScalar>.Element(U: value[0], V: value[1]));
        }

        // The determinant of the n×n multiplication matrix scales the exact norm by 2^(16(n−1)) (integer tail) — the
        // rounding shift. An integer tail through degree 4 keeps that shift below 64 AND expands by first-row cofactor,
        // which Int128CofactorDeterminant implements for order ≤ 4, so the whole determinant computes at Int128 width,
        // congruent modulo 2^128, and rounds once (harmless). From degree 5 the order leaves that expansion (its shift is
        // exactly 64 there, the last harmless value, and exceeds it from degree 6); that lane and every fractional
        // determinant (whose exact entries and products overflow 128 bits) route to the exact limb lane, which builds the
        // exact integer matrix and rounds its division-free determinant once at the exact shift.
        if (UsesFusedLanes) {
            return ((_integerTail && (Degree <= 4))
                ? NormIntegerInt128(value: value)
                : NormLimb(value: value));
        }

        var matrix = MultiplicationMatrix(value: value);

        return Determinant(matrix: matrix, order: n);
    }
    /// <summary>
    /// Computes the characteristic discriminant — the resultant of the modulus and its formal derivative, up to the
    /// standard sign — whose square class and vanishing classify the algebra.
    /// </summary>
    /// <returns>
    /// <c>(−1)^{n(n-1)/2}·Res(f, f′)</c> for the monic modulus <c>f</c>. At degree two this is the quadratic discriminant
    /// <c>Δ = P² + 4Q</c>; a zero value marks the ramified (repeated-root, nilpotent-bearing) case, and over a field its
    /// square class decides split from inert.
    /// </returns>
    /// <remarks>
    /// The resultant is the determinant of the Sylvester matrix of the modulus and its derivative, taken by the same
    /// division-free routine <see cref="Norm"/> uses, so the whole computation stays inside the carrier ring with no
    /// division — the fraction-free guarantee a subresultant Euclidean chain gives, realized without the exact divisions
    /// that chain would need. A degree-one modulus has the empty discriminant, reported as the multiplicative identity.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized and names no algebra.</exception>
    public TScalar CharacteristicDiscriminant() {
        var n = Degree;

        if (1 == n) { return TScalar.MultiplicativeIdentity; }

        // f, high exponent first: leading 1 then the reversed tail. f' likewise, its degree-(n-1) coefficient being n·1.
        var modulusHigh = new TScalar[(n + 1)];

        modulusHigh[0] = TScalar.MultiplicativeIdentity;

        for (var index = 1; (index <= n); ++index) { modulusHigh[index] = _modulus[(n - index)]; }

        var derivativeHigh = new TScalar[n];

        for (var index = 0; (index < n); ++index) {
            // Coefficient of x^(n-1-index) in f' is (n-index)·[coefficient of x^(n-index) in f].
            derivativeHigh[index] = IntegerScale(value: modulusHigh[index], count: (n - index));
        }

        var sylvester = SylvesterMatrix(highFirstModulus: modulusHigh, highFirstDerivative: derivativeHigh);
        var resultant = Determinant(matrix: sylvester, order: ((2 * n) - 1));
        // (-1)^(n(n-1)/2): negate exactly when n ≡ 2 or 3 (mod 4).
        var negate = (0 != ((((n * (n - 1)) / 2)) & 1));

        return (negate ? -resultant : resultant);
    }

    /// <summary>Indicates whether another descriptor names the same algebra.</summary>
    /// <param name="other">The descriptor to compare against.</param>
    /// <returns><see langword="true"/> when both carry the same modulus tail, or both are default; otherwise
    /// <see langword="false"/>.</returns>
    /// <remarks>The modulus tail is the whole of a descriptor's identity: the degree is its length, and the lane
    /// classification and degree-2 twin are pure functions of its contents, so descriptors built from equal tails
    /// compute identical results and are interchangeable. Comparing the tail array would be reference identity —
    /// every <see cref="Create"/> call copies its span, so no two descriptors would ever be equal.</remarks>
    public bool Equals(MonogenicAlgebra<TScalar> other) =>
        VectorsEqual(left: _modulus, right: other._modulus);

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        ((obj is MonogenicAlgebra<TScalar> other) && Equals(other: other));

    /// <summary>Returns a hash code over the modulus tail.</summary>
    /// <returns>A hash code consistent with <see cref="Equals(MonogenicAlgebra{TScalar})"/>.</returns>
    /// <remarks>An <see cref="Fnv1aHash"/> fold over the carrier's own hash codes — pure integer arithmetic with no
    /// per-process seed, unlike <see cref="System.HashCode"/>.</remarks>
    public override int GetHashCode() =>
        VectorHash(vector: _modulus);

    /// <summary>Indicates whether two descriptors name the same algebra.</summary>
    /// <param name="left">The first descriptor.</param>
    /// <param name="right">The second descriptor.</param>
    /// <returns><see langword="true"/> when the modulus tails are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(MonogenicAlgebra<TScalar> left, MonogenicAlgebra<TScalar> right) =>
        left.Equals(other: right);

    /// <summary>Indicates whether two descriptors name different algebras.</summary>
    /// <param name="left">The first descriptor.</param>
    /// <param name="right">The second descriptor.</param>
    /// <returns><see langword="true"/> when the modulus tails differ; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(MonogenicAlgebra<TScalar> left, MonogenicAlgebra<TScalar> right) =>
        !left.Equals(other: right);

    // Structural comparison of two coordinate vectors, shared by the descriptor's modulus tail and by the
    // Element/Projective coordinate vectors. A null vector is the default value and matches only another default.
    private static bool VectorsEqual(TScalar[]? left, TScalar[]? right) {
        if (ReferenceEquals(objA: left, objB: right)) { return true; }

        if ((left is null) || (right is null) || (left.Length != right.Length)) { return false; }

        for (var index = 0; (index < left.Length); ++index) {
            if (!EqualityComparer<TScalar>.Default.Equals(x: left[index], y: right[index])) { return false; }
        }

        return true;
    }

    // The hash matching VectorsEqual: an Fnv1aHash fold of the length and the carrier's own hash codes. Pure integer
    // arithmetic and no per-process seed, so the digest is stable across runs and machines wherever the carrier's hash
    // is; a null vector folds to the empty digest, which no length-bearing vector can produce.
    private static int VectorHash(TScalar[]? vector) {
        var hash = Fnv1aHash.Create();

        if (vector is not null) {
            hash.Add(value: (uint)vector.Length);

            for (var index = 0; (index < vector.Length); ++index) {
                var item = vector[index];

                hash.Add(value: unchecked((uint)((item is null) ? 0 : EqualityComparer<TScalar>.Default.GetHashCode(obj: item))));
            }
        }

        return unchecked((int)(hash.Value ^ (hash.Value >> 32)));
    }

    /// <summary>Returns the modulus or deliberately diagnoses a default descriptor.</summary>
    /// <returns>The initialized modulus tail.</returns>
    /// <exception cref="InvalidOperationException">The descriptor is default-initialized.</exception>
    private TScalar[] GetModulus() =>
        (_modulus ?? ThrowUninitialized());

    /// <summary>Validates an element against this receiver and returns the receiver degree.</summary>
    /// <param name="value">The element to validate.</param>
    /// <param name="paramName">The public parameter name to report.</param>
    /// <returns>The receiver degree.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> has a different dimension.</exception>
    /// <exception cref="InvalidOperationException">The receiver is default-initialized.</exception>
    private int ValidateElement(Element value, string paramName) {
        var expectedDimension = GetModulus().Length;

        ValidateElement(value: value, expectedDimension: expectedDimension, paramName: paramName);

        return expectedDimension;
    }

    /// <summary>Validates an element against a known receiver degree.</summary>
    /// <param name="value">The element to validate.</param>
    /// <param name="expectedDimension">The receiver degree.</param>
    /// <param name="paramName">The public parameter name to report.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> has a different dimension.</exception>
    private static void ValidateElement(Element value, int expectedDimension, string paramName) {
        var actualDimension = value.RawDimension;

        if (actualDimension != expectedDimension) {
            ThrowOperandDimension(
                paramName: paramName,
                operandKind: "element",
                expectedDimension: expectedDimension,
                actualDimension: actualDimension
            );
        }
    }

    /// <summary>Validates a projective window against this receiver and returns the receiver degree.</summary>
    /// <param name="value">The window to validate.</param>
    /// <param name="paramName">The public parameter name to report.</param>
    /// <returns>The receiver degree.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> has a different dimension.</exception>
    /// <exception cref="InvalidOperationException">The receiver is default-initialized.</exception>
    private int ValidateProjective(Projective value, string paramName) {
        var expectedDimension = GetModulus().Length;
        var actualDimension = value.RawDimension;

        if (actualDimension != expectedDimension) {
            ThrowOperandDimension(
                paramName: paramName,
                operandKind: "projective window",
                expectedDimension: expectedDimension,
                actualDimension: actualDimension
            );
        }

        return expectedDimension;
    }

    /// <summary>Throws the default-descriptor diagnosis.</summary>
    /// <returns>Never returns.</returns>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TScalar[] ThrowUninitialized() =>
        throw new InvalidOperationException(
            message: "The monogenic algebra is default-initialized and names no modulus; construct it with MonogenicAlgebra<TScalar>.Create before using it."
        );

    /// <summary>Throws a receiver/operand dimension-affinity diagnosis.</summary>
    /// <param name="paramName">The public parameter name.</param>
    /// <param name="operandKind">The operand kind for the message.</param>
    /// <param name="expectedDimension">The receiver's degree.</param>
    /// <param name="actualDimension">The supplied vector's dimension, or zero for a default value.</param>
    /// <exception cref="ArgumentException">Always.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOperandDimension(string paramName, string operandKind, int expectedDimension, int actualDimension) =>
        throw new ArgumentException(
            message: $"A {operandKind} consumed by a degree-{expectedDimension} monogenic algebra must have dimension {expectedDimension}; the supplied value has dimension {actualDimension}.",
            paramName: paramName
        );

    /// <summary>Multiplies two integer-tail elements over FixedQ4816 by the in-cascade Int128 kernel, rounding each coordinate once.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The reduced product, each coordinate one ties-to-even rounding of the exact reduced convolution.</returns>
    /// <remarks>
    /// The <c>2n−1</c> convolution columns are formed exactly at Int128 width, then the companion fold runs directly on
    /// the wide columns — <c>wide[d−n+j] −= m_j·wide[d]</c> with the integer coefficient as a plain multiplier — before a
    /// single <c>RoundProductSum</c> (shift 16) per output coordinate. Every column is an integer-coefficient polynomial
    /// in the raw inputs, so its unchecked Int128 image is congruent to the true value modulo <c>2^128</c>; a shift of 16
    /// (≤ 64) makes the wrap a multiple of <c>2^112</c> on the rounded result, which vanishes under the final 64-bit raw
    /// wrap without changing tie parity. Harmless at every degree and every integer coefficient magnitude. One buffer
    /// holds the columns and the widened tail integers, stack-allocated up to a bounded degree and pooled beyond.
    /// </remarks>
    private Element MultiplyIntegerCascade(Element left, Element right) {
        var n = Degree;
        var total = ((2 * n) - 1);
        // The (2n−1) convolution columns followed by the n tail integers, which the O(n²) fold reads once per (d, j) pair
        // and would otherwise re-derive there. The stack tier covers 3n−1 cells, so every degree through 32.
        const int stackCellLimit = 96;
        var cellCount = (total + n);
        Int128[]? rented = null;
        var cells = ((cellCount <= stackCellLimit)
            ? stackalloc Int128[stackCellLimit]
            : (rented = ArrayPool<Int128>.Shared.Rent(minimumLength: cellCount)));
        var columns = cells.Slice(start: 0, length: total);
        var tailInteger = cells.Slice(start: total, length: n);

        columns.Clear();

        for (var j = 0; (j < n); ++j) {
            tailInteger[j] = (Unsafe.BitCast<TScalar, FixedQ4816>(source: _modulus[j]).Value >> FixedQ4816.FractionBitCount);
        }

        for (var i = 0; (i < n); ++i) {
            var leftRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: left[i]).Value;

            for (var j = 0; (j < n); ++j) {
                columns[(i + j)] = unchecked((columns[(i + j)] + ((Int128)leftRaw * Unsafe.BitCast<TScalar, FixedQ4816>(source: right[j]).Value)));
            }
        }

        for (var d = (total - 1); (d >= n); --d) {
            var carry = columns[d];

            for (var j = 0; (j < n); ++j) {
                columns[((d - n) + j)] = unchecked((columns[((d - n) + j)] - (tailInteger[j] * carry)));
            }
        }

        var result = new TScalar[n];

        for (var k = 0; (k < n); ++k) {
            result[k] = Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: columns[k])));
        }

        if (rented is not null) { ArrayPool<Int128>.Shared.Return(array: rented); }

        return new Element(coordinates: result);
    }
    /// <summary>Advances one companion step over a degree-3-or-higher FixedQ4816 algebra, rounding the single leading combination once.</summary>
    /// <param name="window">The current window, most recent term first.</param>
    /// <returns>The next window; the leading term is one rounding of <c>−Σ_i m_{n-1-i}·window[i]</c>, the rest exact shifts.</returns>
    /// <remarks>The leading term sums same-scale Q32 products in unchecked Int128 and rounds at shift 16: an overflow past
    /// 128 bits wraps harmlessly (shift ≤ 64). Integer tails land on the exact value; fractional tails on its rounding.</remarks>
    private Projective ProjectiveStepFused(Projective window) {
        var n = Degree;
        var head = Int128.Zero;

        for (var index = 0; (index < n); ++index) {
            var mRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: _modulus[((n - 1) - index)]).Value;
            var wRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: window[index]).Value;

            head = unchecked((head - ((Int128)mRaw * wRaw)));
        }

        var result = new TScalar[n];

        result[0] = Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: head)));

        for (var index = 1; (index < n); ++index) { result[index] = window[(index - 1)]; }

        return new Projective(coordinates: result);
    }
    /// <summary>Computes the norm of an integer-tail FixedQ4816 element of degree 3 or 4 at Int128 width, rounding once.</summary>
    /// <param name="value">The element whose norm is taken.</param>
    /// <returns>The determinant of the multiplication matrix, one rounding of the exact rational norm.</returns>
    /// <remarks>
    /// The multiplication matrix is built at Int128 width by exact integer <c>MultiplyByRoot</c> steps (the integer tail
    /// keeps every entry congruent modulo <c>2^128</c> with no intermediate rounding), the determinant taken by first-row
    /// cofactor, and the result rounded once at shift <c>16(n−1)</c>. The determinant is an integer-coefficient polynomial
    /// in the raw entries, so its unchecked Int128 image is congruent modulo <c>2^128</c>; at degree 4 the shift is 48, so
    /// the wrap lands on <c>2^80</c> in the rounded result and vanishes under the final 64-bit wrap without changing tie
    /// parity. Reached only under the house-carrier guard in <see cref="Norm"/>, so the raw casts are safe.
    /// </remarks>
    private TScalar NormIntegerInt128(Element value) {
        var n = Degree;
        Span<Int128> matrix = stackalloc Int128[16];
        Span<Int128> column = stackalloc Int128[4];
        Span<long> tailInteger = stackalloc long[4];

        for (var index = 0; (index < n); ++index) {
            tailInteger[index] = (Unsafe.BitCast<TScalar, FixedQ4816>(source: _modulus[index]).Value >> FixedQ4816.FractionBitCount);
            column[index] = ((Int128)Unsafe.BitCast<TScalar, FixedQ4816>(source: value[index]).Value);
        }

        for (var columnIndex = 0; (columnIndex < n); ++columnIndex) {
            for (var rowIndex = 0; (rowIndex < n); ++rowIndex) { matrix[((rowIndex * n) + columnIndex)] = column[rowIndex]; }

            if (columnIndex < (n - 1)) {
                // In-place exact MultiplyByRoot: new[0] = −top·m_0, new[k] = old[k−1] − top·m_k, computed high-to-low.
                var top = column[(n - 1)];

                for (var k = (n - 1); (k >= 1); --k) { column[k] = unchecked((column[(k - 1)] - ((Int128)tailInteger[k] * top))); }

                column[0] = unchecked(-((Int128)tailInteger[0] * top));
            }
        }

        var determinant = Int128CofactorDeterminant(matrix: matrix, order: n);
        var shift = (FixedQ4816.FractionBitCount * (n - 1));

        return Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(product: determinant, fractionBitCount: shift)));
    }
    /// <summary>Computes an Int128 determinant by first-row cofactor expansion for orders up to four, unchecked.</summary>
    /// <param name="matrix">The row-major matrix.</param>
    /// <param name="order">The matrix order, at most four.</param>
    /// <returns>The determinant, congruent to the true value modulo <c>2^128</c>.</returns>
    private static Int128 Int128CofactorDeterminant(ReadOnlySpan<Int128> matrix, int order) {
        if (2 == order) {
            return unchecked(((matrix[0] * matrix[3]) - (matrix[1] * matrix[2])));
        }

        Span<Int128> minor = stackalloc Int128[9];
        var result = Int128.Zero;

        for (var column = 0; (column < order); ++column) {
            var target = 0;

            for (var row = 1; (row < order); ++row) {
                for (var minorColumn = 0; (minorColumn < order); ++minorColumn) {
                    if (minorColumn == column) { continue; }

                    minor[target++] = matrix[((row * order) + minorColumn)];
                }
            }

            var cofactor = unchecked((matrix[column] * Int128CofactorDeterminant(matrix: minor.Slice(start: 0, length: ((order - 1) * (order - 1))), order: (order - 1))));

            result = ((0 == (column & 1)) ? unchecked((result + cofactor)) : unchecked((result - cofactor)));
        }

        return result;
    }

    /// <summary>Multiplies two fractional higher-degree elements over FixedQ4816 through the exact limb lane, rounding each coordinate once.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The reduced product, each coordinate one ties-to-even rounding of the exact rational value.</returns>
    /// <remarks>
    /// The convolution columns are lifted to a common scale <c>2^(16(n−1))</c> beyond Q32, then the companion fold divides
    /// each carry by <c>2^16</c> as it descends — the lift supplies exactly <c>(n−1)</c> such divisions, all exact — so the
    /// whole reduction stays an integer. The final column <c>k</c> is the exact numerator at scale <c>2^(16n)</c>, rounded
    /// once. All arithmetic is exact multi-limb; no wrap-harmlessness argument is needed. Limb buffers are stack-allocated
    /// within a bounded degree and pooled beyond.
    /// </remarks>
    private Element MultiplyLimb(Element left, Element right) {
        var n = Degree;
        var total = ((2 * n) - 1);
        var lift = (FixedQ4816.FractionBitCount * (n - 1));
        var width = LimbWidthForMultiply(degree: n);
        // Columns (total × width) plus one per-fold product scratch — one pooled block, sliced.
        var cellCount = (total + 1);
        ulong[]? rented = null;
        var pool = RentLimbCells(cellCount: cellCount, width: width, stackBuffer: stackalloc ulong[LimbStackCellLimit], rented: out rented);
        var productScratch = pool.Slice(start: (total * width), length: width);
        var signs = ((total <= LimbStackColumnLimit) ? stackalloc sbyte[LimbStackColumnLimit] : new sbyte[total]);

        signs = signs.Slice(start: 0, length: total);

        for (var d = 0; (d < total); ++d) { signs[d] = 0; }

        // Convolution columns at Q32, then lifted by 2^lift into the common integer scale.
        for (var i = 0; (i < n); ++i) {
            var leftRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: left[i]).Value;

            for (var j = 0; (j < n); ++j) {
                var product = ((Int128)leftRaw * Unsafe.BitCast<TScalar, FixedQ4816>(source: right[j]).Value);
                var productSign = LimbBig.SetFromInt128(magnitude: productScratch, value: product);
                var target = pool.Slice(start: ((i + j) * width), length: width);

                signs[(i + j)] = LimbBig.AddInto(destination: target, destinationSign: signs[(i + j)], addend: productScratch, addendSign: productSign);
            }
        }

        for (var d = 0; (d < total); ++d) { LimbBig.ShiftLeft(magnitude: pool.Slice(start: (d * width), length: width), bits: lift); }

        // Companion fold on the lifted columns: carry·m_j divided exactly by 2^16 into the lower column.
        for (var d = (total - 1); (d >= n); --d) {
            var carry = pool.Slice(start: (d * width), length: width);
            var carrySign = signs[d];

            for (var j = 0; (j < n); ++j) {
                var mRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: _modulus[j]).Value;
                var productSign = LimbBig.MultiplyByInt64(destination: productScratch, source: carry, sourceSign: carrySign, multiplier: mRaw);

                LimbBig.ShiftRightExact(magnitude: productScratch, bits: FixedQ4816.FractionBitCount);

                var target = pool.Slice(start: (((d - n) + j) * width), length: width);

                signs[((d - n) + j)] = LimbBig.AddInto(destination: target, destinationSign: signs[((d - n) + j)], addend: productScratch, addendSign: (sbyte)(-productSign));
            }
        }

        var result = new TScalar[n];
        var shift = (FixedQ4816.FractionBitCount * n);

        for (var k = 0; (k < n); ++k) {
            var raw = LimbBig.RoundAtShift(magnitude: pool.Slice(start: (k * width), length: width), sign: signs[k], shift: shift);

            result[k] = Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: raw));
        }

        // Returned dirty: RentLimbCells zeroes every block on rent, and limb cells hold no references.
        if (rented is not null) { ArrayPool<ulong>.Shared.Return(array: rented, clearArray: false); }

        return new Element(coordinates: result);
    }
    /// <summary>Computes the trace of a fractional higher-degree FixedQ4816 element through the exact limb lane, rounding once.</summary>
    /// <param name="value">The element whose trace is taken.</param>
    /// <returns>The diagonal sum of the multiplication matrix, one ties-to-even rounding of the exact rational trace.</returns>
    /// <remarks>The diagonal entry from column <c>i</c> carries denominator <c>2^(16 i)</c>; aligning all to
    /// <c>2^(16(n−1))</c> and summing gives the exact numerator, rounded once at shift <c>16(n−1)</c>.</remarks>
    private TScalar TraceLimb(Element value) {
        var n = Degree;
        var width = LimbWidthForNorm(degree: n);
        // Two columns (current, next), an accumulator, and one product scratch.
        var cellCount = ((2 * n) + 2);
        ulong[]? rented;
        var pool = RentLimbCells(cellCount: cellCount, width: width, stackBuffer: stackalloc ulong[LimbStackCellLimit], rented: out rented);
        var columnBlock = pool.Slice(start: 0, length: (n * width));
        var nextBlock = pool.Slice(start: (n * width), length: (n * width));
        var accumulator = pool.Slice(start: ((2 * n) * width), length: width);
        var productScratch = pool.Slice(start: (((2 * n) + 1) * width), length: width);
        var columnSigns = ((n <= LimbStackColumnLimit) ? stackalloc sbyte[LimbStackColumnLimit] : new sbyte[n]);
        var nextSigns = ((n <= LimbStackColumnLimit) ? stackalloc sbyte[LimbStackColumnLimit] : new sbyte[n]);

        columnSigns = columnSigns.Slice(start: 0, length: n);
        nextSigns = nextSigns.Slice(start: 0, length: n);

        for (var index = 0; (index < n); ++index) { columnSigns[index] = LimbBig.SetFromInt64(magnitude: columnBlock.Slice(start: (index * width), length: width), value: Unsafe.BitCast<TScalar, FixedQ4816>(source: value[index]).Value); }

        var accumulatorSign = (sbyte)0;

        for (var i = 0; (i < n); ++i) {
            // Diagonal entry (column i, coordinate i), aligned to 2^(16(n-1)) by an extra 2^(16(n-1-i)).
            LimbBig.CopyMagnitude(destination: productScratch, source: columnBlock.Slice(start: (i * width), length: width));
            LimbBig.ShiftLeft(magnitude: productScratch, bits: (FixedQ4816.FractionBitCount * ((n - 1) - i)));

            accumulatorSign = LimbBig.AddInto(destination: accumulator, destinationSign: accumulatorSign, addend: productScratch, addendSign: columnSigns[i]);

            if (i < (n - 1)) {
                LimbColumnByRoot(source: columnBlock, sourceSigns: columnSigns, destination: nextBlock, destinationSigns: nextSigns, width: width, productScratch: productScratch);
                nextBlock.CopyTo(destination: columnBlock);
                nextSigns.CopyTo(destination: columnSigns);
            }
        }

        var raw = LimbBig.RoundAtShift(magnitude: accumulator, sign: accumulatorSign, shift: (FixedQ4816.FractionBitCount * (n - 1)));

        // Returned dirty: RentLimbCells zeroes every block on rent, and limb cells hold no references.
        if (rented is not null) { ArrayPool<ulong>.Shared.Return(array: rented, clearArray: false); }

        return Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: raw));
    }
    /// <summary>Computes the norm of a FixedQ4816 element through the exact limb lane, rounding the division-free determinant once.</summary>
    /// <param name="value">The element whose norm is taken.</param>
    /// <returns>The determinant of the multiplication matrix, one ties-to-even rounding of the exact rational norm.</returns>
    /// <remarks>
    /// The multiplication matrix is built with the <c>2^16</c> denominators cleared — column <c>i</c> is scaled by
    /// <c>2^(16 i)</c> — giving an exact integer matrix whose division-free (Samuelson–Berkowitz) determinant scales the
    /// norm by <c>2^(16(n(n+1)/2 − 1))</c>, the rounding shift. Integer tails enter this with <c>m_j = m_int·2^16</c>, so
    /// the same builder and shift serve both tail shapes. Buffers are pooled; the accumulators are exact.
    /// </remarks>
    private TScalar NormLimb(Element value) {
        var n = Degree;
        var width = LimbWidthForNorm(degree: n);
        // Full n×n matrix plus two working columns and one product scratch cell, one rented block. (n²+2n+1)·width cells
        // outgrow the stack budget from degree 3 up, so this lane carries no stack tier.
        var cellCount = (((n * n) + (2 * n)) + 1);
        var pool = RentLimbCells(cellCount: cellCount, width: width, rented: out var rented);
        var matrix = pool.Slice(start: 0, length: ((n * n) * width));
        var columnBlock = pool.Slice(start: ((n * n) * width), length: (n * width));
        var nextBlock = pool.Slice(start: (((n * n) + n) * width), length: (n * width));
        var productScratch = pool.Slice(start: (((n * n) + (2 * n)) * width), length: width);
        // The n×n sign array is a matrix-cell count, so it shares the pooled block's stack budget, not the column budget.
        var matrixSigns = (((n * n) <= LimbStackCellLimit) ? stackalloc sbyte[LimbStackCellLimit] : new sbyte[(n * n)]);
        var columnSigns = ((n <= LimbStackColumnLimit) ? stackalloc sbyte[LimbStackColumnLimit] : new sbyte[n]);
        var nextSigns = ((n <= LimbStackColumnLimit) ? stackalloc sbyte[LimbStackColumnLimit] : new sbyte[n]);

        matrixSigns = matrixSigns.Slice(start: 0, length: (n * n));
        columnSigns = columnSigns.Slice(start: 0, length: n);
        nextSigns = nextSigns.Slice(start: 0, length: n);

        for (var index = 0; (index < n); ++index) { columnSigns[index] = LimbBig.SetFromInt64(magnitude: columnBlock.Slice(start: (index * width), length: width), value: Unsafe.BitCast<TScalar, FixedQ4816>(source: value[index]).Value); }

        // Row-major matrix[row*n + col] = coordinate row of value·x^col, columns cleared of their 2^16 denominators.
        for (var columnIndex = 0; (columnIndex < n); ++columnIndex) {
            for (var rowIndex = 0; (rowIndex < n); ++rowIndex) {
                columnBlock.Slice(start: (rowIndex * width), length: width).CopyTo(destination: matrix.Slice(start: (((rowIndex * n) + columnIndex) * width), length: width));
                matrixSigns[((rowIndex * n) + columnIndex)] = columnSigns[rowIndex];
            }

            if (columnIndex < (n - 1)) {
                LimbColumnByRoot(source: columnBlock, sourceSigns: columnSigns, destination: nextBlock, destinationSigns: nextSigns, width: width, productScratch: productScratch);
                nextBlock.CopyTo(destination: columnBlock);
                nextSigns.CopyTo(destination: columnSigns);
            }
        }

        var determinantSign = LimbBerkowitzDeterminant(matrix: matrix, matrixSigns: matrixSigns, order: n, width: width, result: productScratch);
        var shift = (FixedQ4816.FractionBitCount * (((n * (n + 1)) / 2) - 1));
        var raw = LimbBig.RoundAtShift(magnitude: productScratch, sign: determinantSign, shift: shift);

        // Returned dirty: RentLimbCells zeroes every block on rent, and limb cells hold no references.
        ArrayPool<ulong>.Shared.Return(array: rented, clearArray: false);

        return Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: raw));
    }
    /// <summary>Advances one denominator-cleared companion column in the limb lane: <c>new[0] = −top·m_0</c>, <c>new[k] = (old[k−1]≪16) − top·m_k</c>.</summary>
    private void LimbColumnByRoot(ReadOnlySpan<ulong> source, ReadOnlySpan<sbyte> sourceSigns, Span<ulong> destination, Span<sbyte> destinationSigns, int width, Span<ulong> productScratch) {
        var n = Degree;
        var top = source.Slice(start: ((n - 1) * width), length: width);
        var topSign = sourceSigns[(n - 1)];

        for (var k = (n - 1); (k >= 1); --k) {
            var cell = destination.Slice(start: (k * width), length: width);

            // cell = (source[k−1] ≪ 16) − top·m_k. Source and destination blocks never alias, so the cell can hold the
            // shifted lower coordinate before the fold term is subtracted in place.
            source.Slice(start: ((k - 1) * width), length: width).CopyTo(destination: cell);
            LimbBig.ShiftLeft(magnitude: cell, bits: FixedQ4816.FractionBitCount);

            var mRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: _modulus[k]).Value;
            var productSign = LimbBig.MultiplyByInt64(destination: productScratch, source: top, sourceSign: topSign, multiplier: mRaw);

            destinationSigns[k] = LimbBig.AddInto(destination: cell, destinationSign: sourceSigns[(k - 1)], addend: productScratch, addendSign: (sbyte)(-productSign));
        }

        var lowCell = destination.Slice(start: 0, length: width);
        var m0 = Unsafe.BitCast<TScalar, FixedQ4816>(source: _modulus[0]).Value;

        destinationSigns[0] = (sbyte)(-LimbBig.MultiplyByInt64(destination: lowCell, source: top, sourceSign: topSign, multiplier: m0));
    }
    /// <summary>Computes the division-free Samuelson–Berkowitz determinant of a limb matrix, writing the signed magnitude into <paramref name="result"/>.</summary>
    private static sbyte LimbBerkowitzDeterminant(ReadOnlySpan<ulong> matrix, ReadOnlySpan<sbyte> matrixSigns, int order, int width, Span<ulong> result) {
        // One rented block holds every working buffer, sliced by width-strided cell offset:
        //   polynomial | toeplitz | vector | nextVector | product | accumulator | toeplitzScratch.
        // (5·order+5)·width cells outgrow the stack budget at every order the norm lane calls this with, so it pools.
        var cellCount = ((5 * order) + 5);
        var pool = RentLimbCells(cellCount: cellCount, width: width, rented: out var rented);

        try {
            var polynomial = pool.Slice(start: 0, length: ((order + 1) * width));
            var toeplitz = pool.Slice(start: ((order + 1) * width), length: ((order + 1) * width));
            var vector = pool.Slice(start: ((2 * (order + 1)) * width), length: (order * width));
            var nextVector = pool.Slice(start: (((2 * (order + 1)) + order) * width), length: (order * width));
            var product = pool.Slice(start: (((2 * (order + 1)) + (2 * order)) * width), length: width);
            var accumulator = pool.Slice(start: ((((2 * (order + 1)) + (2 * order)) + 1) * width), length: width);
            var toeplitzScratch = pool.Slice(start: ((((2 * (order + 1)) + (2 * order)) + 2) * width), length: ((order + 1) * width));
            var polynomialSigns = (((order + 1) <= LimbStackColumnLimit) ? stackalloc sbyte[LimbStackColumnLimit] : new sbyte[(order + 1)]);
            var toeplitzSigns = (((order + 1) <= LimbStackColumnLimit) ? stackalloc sbyte[LimbStackColumnLimit] : new sbyte[(order + 1)]);
            var vectorSigns = ((order <= LimbStackColumnLimit) ? stackalloc sbyte[LimbStackColumnLimit] : new sbyte[order]);
            var nextVectorSigns = ((order <= LimbStackColumnLimit) ? stackalloc sbyte[LimbStackColumnLimit] : new sbyte[order]);

            polynomialSigns = polynomialSigns.Slice(start: 0, length: (order + 1));
            toeplitzSigns = toeplitzSigns.Slice(start: 0, length: (order + 1));
            vectorSigns = vectorSigns.Slice(start: 0, length: order);
            nextVectorSigns = nextVectorSigns.Slice(start: 0, length: order);

            // polynomial = [1, −M[0,0]].
            var polynomialLength = 2;

            polynomialSigns[0] = LimbBig.SetFromInt64(magnitude: polynomial.Slice(start: 0, length: width), value: 1L);
            MatrixCopy(matrix: matrix, matrixSigns: matrixSigns, index: 0, width: width, destination: polynomial.Slice(start: width, length: width), destinationSign: out polynomialSigns[1]);
            polynomialSigns[1] = (sbyte)(-polynomialSigns[1]);

            for (var stage = 2; (stage <= order); ++stage) {
                var submatrixOrder = (stage - 1);
                var toeplitzLength = (stage + 1);

                for (var index = 0; (index < toeplitzLength); ++index) { toeplitz.Slice(start: (index * width), length: width).Clear(); }

                toeplitzSigns[0] = LimbBig.SetFromInt64(magnitude: toeplitz.Slice(start: 0, length: width), value: 1L);
                MatrixCopy(matrix: matrix, matrixSigns: matrixSigns, index: ((submatrixOrder * order) + submatrixOrder), width: width, destination: toeplitz.Slice(start: width, length: width), destinationSign: out toeplitzSigns[1]);
                toeplitzSigns[1] = (sbyte)(-toeplitzSigns[1]);

                for (var row = 0; (row < submatrixOrder); ++row) { MatrixCopy(matrix: matrix, matrixSigns: matrixSigns, index: ((row * order) + submatrixOrder), width: width, destination: vector.Slice(start: (row * width), length: width), destinationSign: out vectorSigns[row]); }

                for (var power = 1; (power <= submatrixOrder); ++power) {
                    // entry = Σ_col M[submatrixOrder, col]·vector[col]; toeplitz[power+1] = −entry.
                    accumulator.Clear();

                    var entrySign = (sbyte)0;

                    for (var column = 0; (column < submatrixOrder); ++column) {
                        var factorSign = LimbBig.MultiplyFull(destination: product, left: matrix.Slice(start: (((submatrixOrder * order) + column) * width), length: width), leftSign: matrixSigns[((submatrixOrder * order) + column)], right: vector.Slice(start: (column * width), length: width), rightSign: vectorSigns[column]);

                        entrySign = LimbBig.AddInto(destination: accumulator, destinationSign: entrySign, addend: product, addendSign: factorSign);
                    }

                    accumulator.CopyTo(destination: toeplitz.Slice(start: ((power + 1) * width), length: width));
                    toeplitzSigns[(power + 1)] = (sbyte)(-entrySign);

                    if (power < submatrixOrder) {
                        for (var row = 0; (row < submatrixOrder); ++row) {
                            accumulator.Clear();

                            var rowSign = (sbyte)0;

                            for (var column = 0; (column < submatrixOrder); ++column) {
                                var factorSign = LimbBig.MultiplyFull(destination: product, left: matrix.Slice(start: (((row * order) + column) * width), length: width), leftSign: matrixSigns[((row * order) + column)], right: vector.Slice(start: (column * width), length: width), rightSign: vectorSigns[column]);

                                rowSign = LimbBig.AddInto(destination: accumulator, destinationSign: rowSign, addend: product, addendSign: factorSign);
                            }

                            accumulator.CopyTo(destination: nextVector.Slice(start: (row * width), length: width));
                            nextVectorSigns[row] = rowSign;
                        }

                        nextVector.CopyTo(destination: vector);
                        nextVectorSigns.CopyTo(destination: vectorSigns);
                    }
                }

                polynomialLength = LimbToeplitzMultiply(column: toeplitz, columnSigns: toeplitzSigns, columnLength: toeplitzLength, vector: polynomial, vectorSigns: polynomialSigns, vectorLength: polynomialLength, width: width, product: product, resultScratch: toeplitzScratch);
            }

            polynomial.Slice(start: (order * width), length: width).CopyTo(destination: result);

            return ((0 == (order & 1)) ? polynomialSigns[order] : (sbyte)(-polynomialSigns[order]));
        } finally {
            // Returned dirty: RentLimbCells zeroes every block on rent, and limb cells hold no references.
            ArrayPool<ulong>.Shared.Return(array: rented, clearArray: false);
        }
    }
    /// <summary>Multiplies a lower-triangular Toeplitz matrix (given by its first column) by a polynomial vector, in the limb lane, overwriting the vector.</summary>
    private static int LimbToeplitzMultiply(Span<ulong> column, ReadOnlySpan<sbyte> columnSigns, int columnLength, Span<ulong> vector, Span<sbyte> vectorSigns, int vectorLength, int width, Span<ulong> product, Span<ulong> resultScratch) {
        var resultMagnitudes = resultScratch.Slice(start: 0, length: (columnLength * width));
        var resultSigns = ((columnLength <= LimbStackColumnLimit) ? stackalloc sbyte[LimbStackColumnLimit] : new sbyte[columnLength]);

        resultSigns = resultSigns.Slice(start: 0, length: columnLength);

        for (var row = 0; (row < columnLength); ++row) {
            resultMagnitudes.Slice(start: (row * width), length: width).Clear();

            var rowSign = (sbyte)0;

            for (var index = 0; (index < vectorLength); ++index) {
                var diagonal = (row - index);

                if ((0 <= diagonal) && (diagonal < columnLength)) {
                    var factorSign = LimbBig.MultiplyFull(destination: product, left: column.Slice(start: (diagonal * width), length: width), leftSign: columnSigns[diagonal], right: vector.Slice(start: (index * width), length: width), rightSign: vectorSigns[index]);

                    rowSign = LimbBig.AddInto(destination: resultMagnitudes.Slice(start: (row * width), length: width), destinationSign: rowSign, addend: product, addendSign: factorSign);
                }
            }

            resultSigns[row] = rowSign;
        }

        resultMagnitudes.CopyTo(destination: vector.Slice(start: 0, length: (columnLength * width)));
        resultSigns.CopyTo(destination: vectorSigns.Slice(start: 0, length: columnLength));

        return columnLength;
    }
    /// <summary>Copies a signed matrix entry into a destination cell.</summary>
    private static void MatrixCopy(ReadOnlySpan<ulong> matrix, ReadOnlySpan<sbyte> matrixSigns, int index, int width, Span<ulong> destination, out sbyte destinationSign) {
        matrix.Slice(start: (index * width), length: width).CopyTo(destination: destination);
        destinationSign = matrixSigns[index];
    }

    // The limb-lane stack budget: a block up to this many ulongs stays on the stack (~2 KiB), and any column count up to
    // this many stays on the stack. It keeps the Multiply cascade (through degree 7) and the Trace diagonal (through
    // degree 3) heap-free; wider degrees, and the Norm determinant at every degree, pool.
    private const int LimbStackCellLimit = 256;
    private const int LimbStackColumnLimit = 64;

    // Rents one contiguous, zeroed limb block of cellCount×width ulongs — stack-backed within LimbStackCellLimit, pooled
    // beyond. The stack buffer is supplied by the caller (a stackalloc at the call site, so it lives for the caller's
    // frame); the pooled array is returned through <paramref name="rented"/> for release.
    private static Span<ulong> RentLimbCells(int cellCount, int width, Span<ulong> stackBuffer, out ulong[]? rented) {
        var needed = (cellCount * width);
        Span<ulong> pool;

        if (needed <= stackBuffer.Length) {
            rented = null;
            pool = stackBuffer.Slice(start: 0, length: needed);
        } else {
            rented = ArrayPool<ulong>.Shared.Rent(minimumLength: needed);
            pool = rented.AsSpan(start: 0, length: needed);
        }

        pool.Clear();

        return pool;
    }

    // Rents one contiguous, zeroed limb block of cellCount×width ulongs from the pool, for the lanes whose blocks never
    // fit LimbStackCellLimit. The clear is load-bearing: the callers accumulate into cells they never initialize.
    private static Span<ulong> RentLimbCells(int cellCount, int width, out ulong[] rented) {
        var needed = (cellCount * width);

        rented = ArrayPool<ulong>.Shared.Rent(minimumLength: needed);

        var pool = rented.AsSpan(start: 0, length: needed);

        pool.Clear();

        return pool;
    }

    // Limb width (in ulongs) for the fractional Multiply cascade. The lifted convolution starts near 2^(132 + 16(n−1))
    // and each of the (n−1) folds multiplies a column by a full-range Q16 coefficient (≤ 2^63) and shifts right 16, so the
    // magnitude grows by at most 2^(63) per level; 2^(132 + 96(n−1)) is a comfortable ceiling, whose products with a Q16
    // coefficient still fit. Converted to ulongs with guard limbs it never truncates the exact numerator.
    private static int LimbWidthForMultiply(int degree) =>
        (((160 + (96 * (degree - 1))) / 64) + 4);

    // Limb width (in ulongs) for the Norm determinant and Trace diagonal. Column 0 of the denominator-cleared matrix
    // holds the raw coordinates (below 2^63) and LimbColumnByRoot grows a column by at most 2^64 per level (a shift of 16
    // plus a product with a coefficient below 2^63), so the entry in column i is below 2^(63 + 64 i) and every entry is
    // below 2^(64 n). An intermediate Samuelson–Berkowitz number (a signed sum of products of up to n such entries) is
    // therefore below 2^(64 n²) and any product of two working numbers below 2^(128 n²). The derived ceiling
    // 130 n² + 512 bits, converted to ulongs with guard limbs, dominates every accumulator, matrix–vector product, and
    // Toeplitz product.
    private static int LimbWidthForNorm(int degree) =>
        (((((130 * degree) * degree) + 512) / 64) + 6);
    /// <summary>Reduces a wide convolution buffer in place by the modulus, high exponent to low.</summary>
    /// <param name="wide">The length-<c>(2n-1)</c> buffer; on return its low <c>n</c> entries are the reduced element.</param>
    private void ReduceInPlace(TScalar[] wide) {
        var n = Degree;

        for (var degree = (wide.Length - 1); (degree >= n); --degree) {
            var carry = wide[degree];

            for (var j = 0; (j < n); ++j) { wide[((degree - n) + j)] = (wide[((degree - n) + j)] - (carry * _modulus[j])); }

            wide[degree] = TScalar.AdditiveIdentity;
        }
    }
    /// <summary>Builds the row-major <c>n×n</c> matrix of multiplication-by-<paramref name="value"/> in the power basis.</summary>
    /// <param name="value">The element whose multiplication matrix is built.</param>
    /// <returns>The matrix, entry <c>[row·n + column]</c> being the <c>row</c> coordinate of <c>value·x^column</c>.</returns>
    private TScalar[] MultiplicationMatrix(Element value) {
        var n = Degree;
        var matrix = new TScalar[(n * n)];
        var column = value;

        for (var columnIndex = 0; (columnIndex < n); ++columnIndex) {
            for (var rowIndex = 0; (rowIndex < n); ++rowIndex) { matrix[((rowIndex * n) + columnIndex)] = column[rowIndex]; }

            if (columnIndex < (n - 1)) { column = MultiplyByRoot(value: column); }
        }

        return matrix;
    }
    /// <summary>Builds the row-major Sylvester matrix of the modulus and its derivative, both high exponent first.</summary>
    /// <param name="highFirstModulus">The degree-<c>n</c> modulus coefficients, high exponent first (length <c>n+1</c>).</param>
    /// <param name="highFirstDerivative">The degree-<c>(n-1)</c> derivative coefficients, high exponent first (length <c>n</c>).</param>
    /// <returns>The <c>(2n-1)×(2n-1)</c> Sylvester matrix.</returns>
    private static TScalar[] SylvesterMatrix(TScalar[] highFirstModulus, TScalar[] highFirstDerivative) {
        var n = highFirstDerivative.Length;
        var order = ((2 * n) - 1);
        var matrix = new TScalar[(order * order)];

        for (var index = 0; (index < matrix.Length); ++index) { matrix[index] = TScalar.AdditiveIdentity; }

        // n-1 rows of the modulus, each shifted right by one; then n rows of the derivative, each shifted right by one.
        for (var shift = 0; (shift < (n - 1)); ++shift) {
            for (var index = 0; (index < highFirstModulus.Length); ++index) { matrix[(((shift * order) + shift) + index)] = highFirstModulus[index]; }
        }

        for (var shift = 0; (shift < n); ++shift) {
            var row = ((n - 1) + shift);

            for (var index = 0; (index < highFirstDerivative.Length); ++index) { matrix[(((row * order) + shift) + index)] = highFirstDerivative[index]; }
        }

        return matrix;
    }
    /// <summary>Scales a carrier value by a non-negative integer using repeated doubling — the derivative's coefficient map.</summary>
    /// <param name="value">The value to scale.</param>
    /// <param name="count">The non-negative multiplier.</param>
    /// <returns>The sum of <paramref name="count"/> copies of <paramref name="value"/>.</returns>
    private static TScalar IntegerScale(TScalar value, int count) {
        var result = TScalar.AdditiveIdentity;
        var addend = value;

        while (0 != count) {
            if (0 != (count & 1)) { result = (result + addend); }

            count >>>= 1;

            if (0 != count) { addend = (addend + addend); }
        }

        return result;
    }
    /// <summary>Computes the determinant of a row-major square matrix by cofactor expansion for small orders and Berkowitz beyond.</summary>
    /// <param name="matrix">The row-major matrix.</param>
    /// <param name="order">The matrix order.</param>
    /// <returns>The determinant, using only carrier ring operations.</returns>
    private static TScalar Determinant(TScalar[] matrix, int order) {
        if (1 == order) { return matrix[0]; }
        if (4 >= order) { return CofactorDeterminant(matrix: matrix, order: order); }

        return BerkowitzDeterminant(matrix: matrix, order: order);
    }
    /// <summary>Computes the determinant by Laplace cofactor expansion along the first row — exact for the small orders.</summary>
    /// <param name="matrix">The row-major matrix.</param>
    /// <param name="order">The matrix order, at most four.</param>
    /// <returns>The determinant.</returns>
    private static TScalar CofactorDeterminant(TScalar[] matrix, int order) {
        if (1 == order) { return matrix[0]; }

        if (2 == order) {
            return ((matrix[0] * matrix[3]) - (matrix[1] * matrix[2]));
        }

        var result = TScalar.AdditiveIdentity;
        var minor = new TScalar[((order - 1) * (order - 1))];

        for (var column = 0; (column < order); ++column) {
            var target = 0;

            for (var row = 1; (row < order); ++row) {
                for (var minorColumn = 0; (minorColumn < order); ++minorColumn) {
                    if (minorColumn == column) { continue; }

                    minor[target++] = matrix[((row * order) + minorColumn)];
                }
            }

            var cofactor = (matrix[column] * CofactorDeterminant(matrix: minor, order: (order - 1)));

            result = ((0 == (column & 1)) ? (result + cofactor) : (result - cofactor));
        }

        return result;
    }
    /// <summary>Computes the determinant by the division-free Samuelson–Berkowitz algorithm, in <c>O(order⁴)</c> ring operations.</summary>
    /// <param name="matrix">The row-major matrix.</param>
    /// <param name="order">The matrix order.</param>
    /// <returns>The determinant, using no division.</returns>
    /// <remarks>
    /// Accumulates the characteristic polynomial coefficient vector as a product of lower-triangular Toeplitz matrices,
    /// one per growing leading principal submatrix. The determinant is <c>(−1)^order</c> times the vector's last entry.
    /// </remarks>
    private static TScalar BerkowitzDeterminant(TScalar[] matrix, int order) {
        // characteristic-polynomial coefficient vector, leading entry the multiplicative identity.
        var polynomial = new TScalar[] { TScalar.MultiplicativeIdentity, -matrix[0] };

        for (var stage = 2; (stage <= order); ++stage) {
            var submatrixOrder = (stage - 1);
            var toeplitzColumn = new TScalar[(stage + 1)];

            toeplitzColumn[0] = TScalar.MultiplicativeIdentity;
            toeplitzColumn[1] = -matrix[((submatrixOrder * order) + submatrixOrder)];

            // vector = S, then repeatedly A·S, A²·S, …; each contributes −(R · Aᵏ · S) to the Toeplitz column.
            var vector = new TScalar[submatrixOrder];

            for (var row = 0; (row < submatrixOrder); ++row) { vector[row] = matrix[((row * order) + submatrixOrder)]; }

            for (var power = 1; (power <= submatrixOrder); ++power) {
                var entry = TScalar.AdditiveIdentity;

                for (var column = 0; (column < submatrixOrder); ++column) { entry = (entry + (matrix[((submatrixOrder * order) + column)] * vector[column])); }

                toeplitzColumn[(power + 1)] = -entry;

                if (power < submatrixOrder) {
                    var next = new TScalar[submatrixOrder];

                    for (var row = 0; (row < submatrixOrder); ++row) {
                        var accumulated = TScalar.AdditiveIdentity;

                        for (var column = 0; (column < submatrixOrder); ++column) { accumulated = (accumulated + (matrix[((row * order) + column)] * vector[column])); }

                        next[row] = accumulated;
                    }

                    vector = next;
                }
            }

            polynomial = MultiplyToeplitz(column: toeplitzColumn, vector: polynomial);
        }

        var determinant = polynomial[order];

        return ((0 == (order & 1)) ? determinant : -determinant);
    }
    /// <summary>Multiplies a lower-triangular Toeplitz matrix, given by its first column, by a vector.</summary>
    /// <param name="column">The Toeplitz first column, of length <c>vector.Length + 1</c>.</param>
    /// <param name="vector">The vector to multiply.</param>
    /// <returns>The product, of length <c>column.Length</c>.</returns>
    private static TScalar[] MultiplyToeplitz(TScalar[] column, TScalar[] vector) {
        var result = new TScalar[column.Length];

        for (var row = 0; (row < result.Length); ++row) {
            var accumulated = TScalar.AdditiveIdentity;

            for (var index = 0; (index < vector.Length); ++index) {
                var diagonal = (row - index);

                if ((0 <= diagonal) && (diagonal < column.Length)) { accumulated = (accumulated + (column[diagonal] * vector[index])); }
            }

            result[row] = accumulated;
        }

        return result;
    }
    /// <summary>Allocates a fresh zero coordinate vector of the algebra's degree.</summary>
    /// <returns>A length-<see cref="Degree"/> vector of additive identities.</returns>
    private TScalar[] ZeroVector() {
        var result = new TScalar[Degree];

        for (var index = 0; (index < result.Length); ++index) { result[index] = TScalar.AdditiveIdentity; }

        return result;
    }
}
