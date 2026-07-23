using System.Numerics;

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
public readonly struct MonogenicAlgebra<TScalar>
    where TScalar : IAdditionOperators<TScalar, TScalar, TScalar>,
                    ISubtractionOperators<TScalar, TScalar, TScalar>,
                    IMultiplyOperators<TScalar, TScalar, TScalar>,
                    IUnaryNegationOperators<TScalar, TScalar>,
                    IAdditiveIdentity<TScalar, TScalar>,
                    IMultiplicativeIdentity<TScalar, TScalar> {
    /// <summary>The modulus tail <c>[m_0, m_1, …, m_{n-1}]</c>, low exponent first; the leading <c>x^n</c> is implicit.</summary>
    private readonly TScalar[] _modulus;

    /// <summary>Wraps an already-validated modulus tail.</summary>
    /// <param name="modulus">The low-to-high coefficient tail, its length being the degree.</param>
    private MonogenicAlgebra(TScalar[] modulus) =>
        _modulus = modulus;

    /// <summary>
    /// An element of the algebra, the immutable coefficient vector <c>[c_0, c_1, …, c_{n-1}]</c> of
    /// <c>c_0 + c_1·x + … + c_{n-1}·x^{n-1}</c> in the power basis.
    /// </summary>
    /// <remarks>
    /// The vector is array-backed, so every algebra operation that returns an <see cref="Element"/> allocates one
    /// carrier array of length <c>n</c> (and <see cref="Multiply"/> a further length-<c>(2n-1)</c> scratch buffer). This
    /// is the honest cost of a degree the type cannot know at compile time; fixed-width fast tiers — a degree-2 inline
    /// pair, or a bit-packed two-element carrier — can specialize the common degrees later without changing this shape.
    /// A default <see cref="Element"/> carries no vector and is not a member of any algebra; obtain elements from the
    /// algebra's <see cref="Zero"/>, <see cref="One"/>, <see cref="Root"/>, or <see cref="FromCoordinates"/>.
    /// </remarks>
    public readonly struct Element {
        /// <summary>The power-basis coordinate vector, low exponent first.</summary>
        private readonly TScalar[] _coordinates;

        /// <summary>Adopts a coordinate vector without copying; internal callers never alias.</summary>
        /// <param name="coordinates">The vector to adopt.</param>
        internal Element(TScalar[] coordinates) =>
            _coordinates = coordinates;

        /// <summary>Gets the power-basis coordinate vector, low exponent first.</summary>
        public ReadOnlySpan<TScalar> Coordinates =>
            _coordinates;
        /// <summary>Gets the number of coordinates, which is the algebra's degree.</summary>
        public int Dimension =>
            _coordinates.Length;
        /// <summary>Gets the coefficient of <c>x^<paramref name="exponent"/></c>.</summary>
        /// <param name="exponent">The basis exponent in <c>[0, Dimension)</c>.</param>
        /// <returns>The coordinate at <paramref name="exponent"/>.</returns>
        public TScalar this[int exponent] =>
            _coordinates[exponent];
    }

    /// <summary>
    /// A projective coordinate vector <c>[v_0, v_1, …, v_{n-1}]</c> — the sliding window of an order-<c>n</c> linear
    /// recurrence, most recent term first — that a <see cref="ProjectiveStep"/> iterate carries without dividing.
    /// </summary>
    /// <remarks>The degree-2 case is the <c>(numerator : denominator)</c> pair a continued-fraction convergent holds.</remarks>
    public readonly struct Projective {
        /// <summary>The window coordinates, most recent term first.</summary>
        private readonly TScalar[] _coordinates;

        /// <summary>Adopts a coordinate vector without copying; internal callers never alias.</summary>
        /// <param name="coordinates">The vector to adopt.</param>
        internal Projective(TScalar[] coordinates) =>
            _coordinates = coordinates;

        /// <summary>Gets the window coordinates, most recent term first.</summary>
        public ReadOnlySpan<TScalar> Coordinates =>
            _coordinates;
        /// <summary>Gets the number of coordinates, which is the algebra's degree.</summary>
        public int Dimension =>
            _coordinates.Length;
        /// <summary>Gets the window coordinate at <paramref name="offset"/> terms back.</summary>
        /// <param name="offset">The window offset in <c>[0, Dimension)</c>.</param>
        /// <returns>The coordinate at <paramref name="offset"/>.</returns>
        public TScalar this[int offset] =>
            _coordinates[offset];
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
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="monicModulus"/> is empty; the degree must be at least one.</exception>
    public static MonogenicAlgebra<TScalar> Create(ReadOnlySpan<TScalar> monicModulus) {
        if (monicModulus.IsEmpty) {
            throw new ArgumentException(message: "A monogenic algebra needs a monic modulus of degree at least one.", paramName: nameof(monicModulus));
        }

        return new MonogenicAlgebra<TScalar>(modulus: monicModulus.ToArray());
    }

    /// <summary>Gets the algebra's degree <c>n</c>, so the algebra has rank <c>n</c> over the carrier.</summary>
    public int Degree =>
        _modulus.Length;
    /// <summary>Gets the modulus tail <c>[m_0, …, m_{n-1}]</c>, low exponent first; the modulus is <c>x^n</c> plus this.</summary>
    public ReadOnlySpan<TScalar> Modulus =>
        _modulus;
    /// <summary>Gets the multiplicative identity, <c>1</c>.</summary>
    public Element One {
        get {
            var coordinates = ZeroVector();

            coordinates[0] = TScalar.MultiplicativeIdentity;

            return new Element(coordinates: coordinates);
        }
    }
    /// <summary>Gets the adjoined root itself, the element <c>x</c> — the generator whose powers drive the companion sequences.</summary>
    /// <remarks>At degree one the root reduces to the scalar <c>−m_0</c>, there being no <c>x</c> in the basis.</remarks>
    public Element Root =>
        MultiplyByRoot(value: One);
    /// <summary>Gets the additive identity, <c>0</c>.</summary>
    public Element Zero =>
        new(coordinates: ZeroVector());

    /// <summary>Creates an element from its power-basis coordinates, low exponent first.</summary>
    /// <param name="coordinates">The coordinate vector, whose length must equal the algebra's <see cref="Degree"/>.</param>
    /// <returns>The described element.</returns>
    /// <exception cref="ArgumentException"><paramref name="coordinates"/> does not have length <see cref="Degree"/>.</exception>
    public Element FromCoordinates(ReadOnlySpan<TScalar> coordinates) {
        if (coordinates.Length != Degree) {
            throw new ArgumentException(message: $"An element of a degree-{Degree} algebra needs exactly {Degree} coordinates.", paramName: nameof(coordinates));
        }

        return new Element(coordinates: coordinates.ToArray());
    }
    /// <summary>Creates a projective coordinate window from its coordinates, most recent term first.</summary>
    /// <param name="window">The window coordinates, whose length must equal the algebra's <see cref="Degree"/>.</param>
    /// <returns>The described window.</returns>
    /// <exception cref="ArgumentException"><paramref name="window"/> does not have length <see cref="Degree"/>.</exception>
    public Projective FromWindow(ReadOnlySpan<TScalar> window) {
        if (window.Length != Degree) {
            throw new ArgumentException(message: $"A window over a degree-{Degree} algebra needs exactly {Degree} coordinates.", paramName: nameof(window));
        }

        return new Projective(coordinates: window.ToArray());
    }

    /// <summary>Adds two elements.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The coordinate-wise sum.</returns>
    public Element Add(Element left, Element right) {
        var n = Degree;
        var result = new TScalar[n];

        for (var index = 0; (index < n); ++index) { result[index] = (left[index] + right[index]); }

        return new Element(coordinates: result);
    }
    /// <summary>Subtracts one element from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The coordinate-wise difference.</returns>
    public Element Subtract(Element left, Element right) {
        var n = Degree;
        var result = new TScalar[n];

        for (var index = 0; (index < n); ++index) { result[index] = (left[index] - right[index]); }

        return new Element(coordinates: result);
    }
    /// <summary>Negates an element.</summary>
    /// <param name="value">The element to negate.</param>
    /// <returns>The coordinate-wise negation.</returns>
    public Element Negate(Element value) {
        var n = Degree;
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
    public Element Multiply(Element left, Element right) {
        var n = Degree;
        var wide = new TScalar[((2 * n) - 1)];

        for (var index = 0; (index < wide.Length); ++index) { wide[index] = TScalar.AdditiveIdentity; }

        for (var i = 0; (i < n); ++i) {
            for (var j = 0; (j < n); ++j) { wide[i + j] = (wide[i + j] + (left[i] * right[j])); }
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
    public Element MultiplyByRoot(Element value) {
        var n = Degree;
        var top = value[n - 1];
        var result = new TScalar[n];

        result[0] = (TScalar.AdditiveIdentity - (top * _modulus[0]));

        for (var index = 1; (index < n); ++index) { result[index] = (value[index - 1] - (top * _modulus[index])); }

        return new Element(coordinates: result);
    }
    /// <summary>Raises the adjoined root to a power by fast exponentiation — the closed-form engine for the companion sequences.</summary>
    /// <param name="exponent">The power; zero yields <see cref="One"/>.</param>
    /// <returns>The element <c>x^exponent</c>, reduced.</returns>
    /// <remarks>
    /// Square-and-multiply over the exponent's binary expansion, so the operation count depends on the exponent and the
    /// routine is not constant-time in it. Over an exact carrier this evaluates the order-<c>n</c> linear recurrence
    /// <c>a_k = −Σ_j m_j·a_{k-n+j}</c> in logarithmic time — the closed form the term-by-term recurrence unrolls to.
    /// </remarks>
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
    /// <summary>Advances one step of the companion action on a projective coordinate window, without dividing.</summary>
    /// <param name="window">The current window <c>[a_k, a_{k-1}, …, a_{k-n+1}]</c>, most recent term first.</param>
    /// <returns>The next window <c>[a_{k+1}, a_k, …, a_{k-n+2}]</c>, where <c>a_{k+1} = −Σ_i m_{n-1-i}·window[i]</c>.</returns>
    /// <remarks>
    /// The degree-<c>n</c> generalization of the Möbius step: the companion-matrix action on projective coordinates. The
    /// leading coordinate is the next recurrence term and the rest slide down by one, so iterating from a seed window
    /// enumerates the recurrence's convergents. At degree two this is exactly the <c>(numerator : denominator) ↦
    /// (P·numerator + Q·denominator : numerator)</c> continued-fraction step.
    /// </remarks>
    public Projective ProjectiveStep(Projective window) {
        var n = Degree;
        var head = TScalar.AdditiveIdentity;

        for (var index = 0; (index < n); ++index) { head = (head - (_modulus[(n - 1) - index] * window[index])); }

        var result = new TScalar[n];

        result[0] = head;

        for (var index = 1; (index < n); ++index) { result[index] = window[index - 1]; }

        return new Projective(coordinates: result);
    }
    /// <summary>Computes the algebra trace of an element — the trace of multiplication-by-<paramref name="value"/>.</summary>
    /// <param name="value">The element whose trace is taken.</param>
    /// <returns>The sum of the diagonal of the multiplication matrix, the sum of <paramref name="value"/>'s conjugates.</returns>
    /// <remarks>Builds the <c>n×n</c> multiplication matrix one column at a time by repeated <see cref="MultiplyByRoot"/> and sums its diagonal — <c>O(n²)</c> carrier multiplications.</remarks>
    public TScalar Trace(Element value) {
        var n = Degree;
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
    public TScalar Norm(Element value) {
        var n = Degree;
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
    public TScalar CharacteristicDiscriminant() {
        var n = Degree;

        if (1 == n) { return TScalar.MultiplicativeIdentity; }

        // f, high exponent first: leading 1 then the reversed tail. f' likewise, its degree-(n-1) coefficient being n·1.
        var modulusHigh = new TScalar[n + 1];

        modulusHigh[0] = TScalar.MultiplicativeIdentity;

        for (var index = 1; (index <= n); ++index) { modulusHigh[index] = _modulus[n - index]; }

        var derivativeHigh = new TScalar[n];

        for (var index = 0; (index < n); ++index) {
            // Coefficient of x^(n-1-index) in f' is (n-index)·[coefficient of x^(n-index) in f].
            derivativeHigh[index] = IntegerScale(value: modulusHigh[index], count: (n - index));
        }

        var sylvester = SylvesterMatrix(highFirstModulus: modulusHigh, highFirstDerivative: derivativeHigh);
        var resultant = Determinant(matrix: sylvester, order: ((2 * n) - 1));
        // (-1)^(n(n-1)/2): negate exactly when n ≡ 2 or 3 (mod 4).
        var negate = (0 != ((((n * (n - 1)) / 2)) & 1));

        return negate ? -resultant : resultant;
    }

    /// <summary>Reduces a wide convolution buffer in place by the modulus, high exponent to low.</summary>
    /// <param name="wide">The length-<c>(2n-1)</c> buffer; on return its low <c>n</c> entries are the reduced element.</param>
    private void ReduceInPlace(TScalar[] wide) {
        var n = Degree;

        for (var degree = (wide.Length - 1); (degree >= n); --degree) {
            var carry = wide[degree];

            for (var j = 0; (j < n); ++j) { wide[(degree - n) + j] = (wide[(degree - n) + j] - (carry * _modulus[j])); }

            wide[degree] = TScalar.AdditiveIdentity;
        }
    }
    /// <summary>Builds the row-major <c>n×n</c> matrix of multiplication-by-<paramref name="value"/> in the power basis.</summary>
    /// <param name="value">The element whose multiplication matrix is built.</param>
    /// <returns>The matrix, entry <c>[row·n + column]</c> being the <c>row</c> coordinate of <c>value·x^column</c>.</returns>
    private TScalar[] MultiplicationMatrix(Element value) {
        var n = Degree;
        var matrix = new TScalar[n * n];
        var column = value;

        for (var columnIndex = 0; (columnIndex < n); ++columnIndex) {
            for (var rowIndex = 0; (rowIndex < n); ++rowIndex) { matrix[(rowIndex * n) + columnIndex] = column[rowIndex]; }

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
        var matrix = new TScalar[order * order];

        for (var index = 0; (index < matrix.Length); ++index) { matrix[index] = TScalar.AdditiveIdentity; }

        // n-1 rows of the modulus, each shifted right by one; then n rows of the derivative, each shifted right by one.
        for (var shift = 0; (shift < (n - 1)); ++shift) {
            for (var index = 0; (index < highFirstModulus.Length); ++index) { matrix[((shift * order) + shift) + index] = highFirstModulus[index]; }
        }

        for (var shift = 0; (shift < n); ++shift) {
            var row = ((n - 1) + shift);

            for (var index = 0; (index < highFirstDerivative.Length); ++index) { matrix[((row * order) + shift) + index] = highFirstDerivative[index]; }
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
        var minor = new TScalar[(order - 1) * (order - 1)];

        for (var column = 0; (column < order); ++column) {
            var target = 0;

            for (var row = 1; (row < order); ++row) {
                for (var minorColumn = 0; (minorColumn < order); ++minorColumn) {
                    if (minorColumn == column) { continue; }

                    minor[target++] = matrix[(row * order) + minorColumn];
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
            var toeplitzColumn = new TScalar[stage + 1];

            toeplitzColumn[0] = TScalar.MultiplicativeIdentity;
            toeplitzColumn[1] = -matrix[(submatrixOrder * order) + submatrixOrder];

            // vector = S, then repeatedly A·S, A²·S, …; each contributes −(R · Aᵏ · S) to the Toeplitz column.
            var vector = new TScalar[submatrixOrder];

            for (var row = 0; (row < submatrixOrder); ++row) { vector[row] = matrix[(row * order) + submatrixOrder]; }

            for (var power = 1; (power <= submatrixOrder); ++power) {
                var entry = TScalar.AdditiveIdentity;

                for (var column = 0; (column < submatrixOrder); ++column) { entry = (entry + (matrix[(submatrixOrder * order) + column] * vector[column])); }

                toeplitzColumn[power + 1] = -entry;

                if (power < submatrixOrder) {
                    var next = new TScalar[submatrixOrder];

                    for (var row = 0; (row < submatrixOrder); ++row) {
                        var accumulated = TScalar.AdditiveIdentity;

                        for (var column = 0; (column < submatrixOrder); ++column) { accumulated = (accumulated + (matrix[(row * order) + column] * vector[column])); }

                        next[row] = accumulated;
                    }

                    vector = next;
                }
            }

            polynomial = MultiplyToeplitz(column: toeplitzColumn, vector: polynomial);
        }

        var determinant = polynomial[order];

        return (0 == (order & 1)) ? determinant : -determinant;
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
