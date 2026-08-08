using System.Runtime.InteropServices;

namespace Puck.Maths;

/// <summary>
/// The non-metric complement of a finite graded presentation, and the join and meet it generates. It uses no metric at
/// all: the complement of a basis blade is the blade on the complementary generator set, charged by whatever the
/// presentation's OWN cell says the two multiply to. Signature never enters, which is why a degenerate generator is
/// served exactly as a non-degenerate one is.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// <b>The sign convention, which is grade-dependent and is the thing to read twice.</b> The RIGHT complement is defined
/// by <c>x ∧ rightComplement(x) = pseudoscalar</c> and the LEFT complement by
/// <c>leftComplement(x) ∧ x = pseudoscalar</c>; each is charged so that its own equation holds exactly, with no
/// convention smuggled in. The two are mutual inverses, so <c>leftComplement(rightComplement(x)) = x</c> exactly, at
/// every grade and for every element. Applying the SAME complement twice does not return the argument: on a basis blade
/// of grade <c>g</c> over <c>n</c> generators it returns <c>(−1)^(g·(n−g))</c> times it. So double complement is plus or
/// minus the identity per grade, and it is the identity outright only when every grade present has <c>g·(n−g)</c> even.
/// </para>
/// <para>
/// <b>What it requires.</b> An <see cref="IExactSemiringMaterial{TValue, TSelf}"/> and a presentation whose normal
/// forms are exactly the ascending subsets of its generators — every exact-material Clifford signature, and nothing
/// else in this library. Exact scalar associativity and distributivity are what extend the basis-key inverse checks to
/// every linear combination. Both requirements are checked, and a presentation that fails either is refused at
/// construction rather than silently mis-signed.
/// </para>
/// <para>
/// Eager: the complement key, the two charges, the join table and the pseudoscalar are computed once, from the
/// compiled cells, before the value exists. The join and the two complements then stage into construction-time buffers
/// cleared at entry, so a call's only allocation is the result's own arrays and one instance is not safe for
/// concurrent use.
/// </para>
/// </remarks>
public sealed class GradedComplement<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private readonly PresentedAlgebra<TValue, TOps> m_algebra;
    private readonly int[] m_complementKey;
    private readonly List<TValue> m_joinCharges;
    private readonly List<TValue> m_joinLeft;
    private readonly List<TValue> m_joinRight;
    private readonly List<long> m_joinTargets;
    private readonly int m_keyCount;
    private readonly TValue[] m_leftCharge;
    private readonly TOps m_material;
    private readonly int[] m_productKey;
    private readonly TValue[] m_productCharge;
    private readonly List<TValue> m_rechargeCoefficients;
    private readonly List<long> m_rechargeKeys;
    private readonly TValue[] m_rightCharge;

    private GradedComplement(
        PresentedAlgebra<TValue, TOps> algebra,
        int keyCount,
        int joinPairCount,
        int[] complementKey,
        TValue[] rightCharge,
        TValue[] leftCharge,
        int[] productKey,
        TValue[] productCharge,
        PresentedAlgebra<TValue, TOps>.Element pseudoscalar
    ) {
        Pseudoscalar = pseudoscalar;
        m_algebra = algebra;
        m_complementKey = complementKey;
        m_joinCharges = new(capacity: joinPairCount);
        m_joinLeft = new(capacity: joinPairCount);
        m_joinRight = new(capacity: joinPairCount);
        m_joinTargets = new(capacity: joinPairCount);
        m_keyCount = keyCount;
        m_leftCharge = leftCharge;
        m_material = algebra.Presentation.Material;
        m_productCharge = productCharge;
        m_productKey = productKey;
        m_rechargeCoefficients = new(capacity: keyCount);
        m_rechargeKeys = new(capacity: keyCount);
        m_rightCharge = rightCharge;
    }

    /// <summary>Gets the algebra this complement is taken in.</summary>
    public PresentedAlgebra<TValue, TOps> Algebra => m_algebra;
    /// <summary>Gets the top-grade basis element — the blade on every generator, which every complement lands against.</summary>
    public PresentedAlgebra<TValue, TOps>.Element Pseudoscalar { get; }

    /// <summary>Builds the complement of a graded presentation.</summary>
    /// <param name="algebra">The algebra, whose normal forms must be the ascending subsets of its generators.</param>
    /// <returns>The described complement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="algebra"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The material does not certify the exact semiring laws, the presentation's
    /// normal forms are not the ascending generator subsets, a complementary pair does not multiply to the top-grade
    /// blade, or a complement charge has no inverse in the material.</exception>
    public static GradedComplement<TValue, TOps> Create(PresentedAlgebra<TValue, TOps> algebra) {
        ArgumentNullException.ThrowIfNull(argument: algebra);

        var presentation = algebra.Presentation;
        var generatorCount = presentation.GeneratorCount;

        if (presentation.Material is not IExactSemiringMaterial<TValue, TOps>) {
            throw new ArgumentException(
                message: "A graded complement needs an exact semiring material so its basis-key inverse checks extend to every element.",
                paramName: nameof(algebra)
            );
        }

        if (!presentation.HasCompiledNormalFormBasis || (generatorCount < 1) || (generatorCount > 30)) {
            throw new ArgumentException(message: "A graded complement needs a finite presentation on one through thirty generators.", paramName: nameof(algebra));
        }

        var keyCount = presentation.NormalFormCount;
        var maskCount = (1 << generatorCount);

        if (keyCount != maskCount) {
            throw new ArgumentException(message: "A graded complement needs one normal form per generator subset.", paramName: nameof(algebra));
        }

        var keyOfMask = new int[maskCount];
        var maskOfKey = new int[keyCount];

        Array.Fill(array: keyOfMask, value: -1);

        for (var key = 0; (key < keyCount); ++key) {
            var word = presentation.NormalFormWord(key: key);
            var mask = 0;
            var previous = -1;

            foreach (var symbol in word) {
                if (symbol <= previous) {
                    throw new ArgumentException(message: "A graded complement needs every normal form to be an ascending generator subset.", paramName: nameof(algebra));
                }

                mask |= (1 << symbol);
                previous = symbol;
            }

            if (-1 != keyOfMask[mask]) {
                throw new ArgumentException(message: "A graded complement needs every generator subset to name exactly one normal form.", paramName: nameof(algebra));
            }

            keyOfMask[mask] = key;
            maskOfKey[key] = mask;
        }

        var comparer = EqualityComparer<TValue>.Default;
        var full = (maskCount - 1);
        var material = presentation.Material;
        var one = material.One;
        var pseudoscalarKey = keyOfMask[full];
        var complementKey = new int[keyCount];
        var leftCharge = new TValue[keyCount];
        var rightCharge = new TValue[keyCount];

        for (var key = 0; (key < keyCount); ++key) {
            var complement = keyOfMask[full ^ maskOfKey[key]];

            complementKey[key] = complement;
            rightCharge[key] = InverseOf(
                algebra: algebra,
                value: SingleCharge(algebra: algebra, keyCount: keyCount, leftKey: key, rightKey: complement, expected: pseudoscalarKey),
                one: one,
                comparer: comparer
            );
            leftCharge[key] = InverseOf(
                algebra: algebra,
                value: SingleCharge(algebra: algebra, keyCount: keyCount, leftKey: complement, rightKey: key, expected: pseudoscalarKey),
                one: one,
                comparer: comparer
            );
        }

        // Each charge above makes ITS OWN pseudoscalar equation true. Over a general field that does not imply that
        // the two maps undo one another: a complementary product can carry q with q² != 1, in which case the
        // composition scales a blade. Admission therefore proves the public inverse contract on every basis key,
        // in both orders, before any element can be complemented.
        for (var key = 0; (key < keyCount); ++key) {
            var complement = complementKey[key];
            var leftAfterRight = material.Multiply(left: leftCharge[complement], right: rightCharge[key]);
            var rightAfterLeft = material.Multiply(left: rightCharge[complement], right: leftCharge[key]);

            if (!comparer.Equals(x: leftAfterRight, y: one)) {
                throw new ArgumentException(
                    message: $"The graded complement's left-after-right composition scales basis key {key} by {leftAfterRight} rather than the material's one.",
                    paramName: nameof(algebra)
                );
            }

            if (!comparer.Equals(x: rightAfterLeft, y: one)) {
                throw new ArgumentException(
                    message: $"The graded complement's right-after-left composition scales basis key {key} by {rightAfterLeft} rather than the material's one.",
                    paramName: nameof(algebra)
                );
            }
        }

        // The join table: for every disjoint ordered pair the presentation's own cell must be the single blade on the
        // union, which is what makes the outer product a projection of the product rather than a second product.
        var productCharge = new TValue[(keyCount * keyCount)];
        var productKey = new int[(keyCount * keyCount)];

        Array.Fill(array: productKey, value: -1);

        // Its own count is the exact bound on what one join can stage, since the walk stages a contribution only where
        // this table has a cell, so the staging buffers below never grow.
        var joinPairCount = 0;

        for (var left = 0; (left < keyCount); ++left) {
            for (var right = 0; (right < keyCount); ++right) {
                if (0 != (maskOfKey[left] & maskOfKey[right])) { continue; }

                var union = keyOfMask[maskOfKey[left] | maskOfKey[right]];
                var slot = ((left * keyCount) + right);

                productCharge[slot] = SingleCharge(algebra: algebra, keyCount: keyCount, leftKey: left, rightKey: right, expected: union);
                productKey[slot] = union;

                ++joinPairCount;
            }
        }

        return new(
            algebra: algebra,
            keyCount: keyCount,
            joinPairCount: joinPairCount,
            complementKey: complementKey,
            rightCharge: rightCharge,
            leftCharge: leftCharge,
            productKey: productKey,
            productCharge: productCharge,
            pseudoscalar: algebra.FromSupport(keys: [pseudoscalarKey], coefficients: [one])
        );
    }

    /// <summary>Returns the left complement of an element.</summary>
    /// <param name="value">The element.</param>
    /// <returns>The element <c>y</c> with <c>y ∧ value = pseudoscalar</c> on each basis blade.</returns>
    /// <exception cref="ArgumentException">The element belongs to another algebra.</exception>
    /// <remarks>The exact inverse of <see cref="RightComplement"/>: composing the two either way returns the argument.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element LeftComplement(in PresentedAlgebra<TValue, TOps>.Element value) =>
        Recharge(value: value, charges: m_leftCharge);

    /// <summary>Returns the outer product — the join — of two elements.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <returns>The join. Blades that share a generator contribute nothing, so a point already on a line joins it to
    /// exactly zero.</returns>
    /// <exception cref="ArgumentException">An operand belongs to another algebra.</exception>
    /// <remarks>Each returned coefficient is folded through exactly one
    /// <see cref="IMaterialOps{TValue, TOps}.FusedChargedSum"/>, the same rounding discipline the product keeps.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element OuterProduct(in PresentedAlgebra<TValue, TOps>.Element left, in PresentedAlgebra<TValue, TOps>.Element right) {
        m_algebra.RequireOwned(value: left, paramName: nameof(left));
        m_algebra.RequireOwned(value: right, paramName: nameof(right));

        var charges = m_joinCharges;
        var keyCount = m_keyCount;
        var leftCoefficients = left.Coefficients;
        var leftKeys = left.Keys;
        var leftValues = m_joinLeft;
        var productCharge = m_productCharge;
        var productKey = m_productKey;
        var rightCoefficients = right.Coefficients;
        var rightKeys = right.Keys;
        var rightValues = m_joinRight;
        var targets = m_joinTargets;

        charges.Clear();
        leftValues.Clear();
        rightValues.Clear();
        targets.Clear();

        for (var leftIndex = 0; (leftIndex < leftKeys.Length); ++leftIndex) {
            var row = (((int)leftKeys[leftIndex]) * keyCount);

            for (var rightIndex = 0; (rightIndex < rightKeys.Length); ++rightIndex) {
                var slot = (row + ((int)rightKeys[rightIndex]));
                var target = productKey[slot];

                if (target < 0) { continue; }

                charges.Add(item: productCharge[slot]);
                leftValues.Add(item: leftCoefficients[leftIndex]);
                rightValues.Add(item: rightCoefficients[rightIndex]);
                targets.Add(item: target);
            }
        }

        return m_algebra.FoldByTarget(targets: targets, charges: charges, left: leftValues, right: rightValues);
    }

    /// <summary>Returns the regressive product — the meet — of two elements.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <returns>The meet <c>leftComplement(rightComplement(left) ∧ rightComplement(right))</c>.</returns>
    /// <exception cref="ArgumentException">An operand belongs to another algebra.</exception>
    /// <remarks>The join and the meet are the same operation read through the complement, which is what makes incidence
    /// non-metric: two planes meet in the line they share, and a point lies on a line exactly when their join is zero,
    /// with no signature anywhere in either statement.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element RegressiveProduct(in PresentedAlgebra<TValue, TOps>.Element left, in PresentedAlgebra<TValue, TOps>.Element right) {
        m_algebra.RequireOwned(value: left, paramName: nameof(left));
        m_algebra.RequireOwned(value: right, paramName: nameof(right));

        return LeftComplement(value: OuterProduct(left: RightComplement(value: left), right: RightComplement(value: right)));
    }

    /// <summary>Returns the right complement of an element.</summary>
    /// <param name="value">The element.</param>
    /// <returns>The element <c>y</c> with <c>value ∧ y = pseudoscalar</c> on each basis blade.</returns>
    /// <exception cref="ArgumentException">The element belongs to another algebra.</exception>
    public PresentedAlgebra<TValue, TOps>.Element RightComplement(in PresentedAlgebra<TValue, TOps>.Element value) =>
        Recharge(value: value, charges: m_rightCharge);

    // The charge one ordered pair of complementary blades carries, insisting it is a single entry at the expected key.
    // The algebra travels in so a refusal names Create's own parameter rather than a local the caller cannot see.
    private static TValue SingleCharge(PresentedAlgebra<TValue, TOps> algebra, int keyCount, int leftKey, int rightKey, int expected) {
        var presentation = algebra.Presentation;
        var cell = ((leftKey * keyCount) + rightKey);
        var start = presentation.CellStarts[cell];

        if ((1L != (presentation.CellStarts[(cell + 1)] - start)) || (expected != presentation.CellTargets[((int)start)])) {
            throw new ArgumentException(message: "A graded complement needs every disjoint blade pair to multiply to the single blade on their union.", paramName: nameof(algebra));
        }

        return presentation.CellCharges[((int)start)];
    }

    // The charge that makes the complement's own equation hold. Every real complement charge is a sign, which is its
    // own inverse; a material that is a certified field serves the rest, and anything else is refused rather than
    // approximated.
    private static TValue InverseOf(PresentedAlgebra<TValue, TOps> algebra, TValue value, TValue one, EqualityComparer<TValue> comparer) {
        var material = algebra.Presentation.Material;

        if (comparer.Equals(x: material.Multiply(left: value, right: value), y: one)) { return value; }

        if ((material is IFieldMaterial<TValue, TOps> field) && field.TryInvert(value: value, out var inverse)) { return inverse; }

        throw new ArgumentException(message: "A complement charge of this presentation is not a sign and has no inverse in its material.", paramName: nameof(algebra));
    }
    private PresentedAlgebra<TValue, TOps>.Element Recharge(in PresentedAlgebra<TValue, TOps>.Element value, TValue[] charges) {
        m_algebra.RequireOwned(value: value, paramName: nameof(value));

        var coefficients = m_rechargeCoefficients;
        var complementKey = m_complementKey;
        var keys = m_rechargeKeys;
        var material = m_material;
        var sourceCoefficients = value.Coefficients;
        var sourceKeys = value.Keys;

        coefficients.Clear();
        keys.Clear();

        for (var index = 0; (index < sourceKeys.Length); ++index) {
            var key = ((int)sourceKeys[index]);
            var recharged = material.Multiply(left: charges[key], right: sourceCoefficients[index]);

            if (material.IsZero(value: recharged)) { continue; }

            coefficients.Add(item: recharged);
            keys.Add(item: complementKey[key]);
        }

        return m_algebra.FromSupport(keys: CollectionsMarshal.AsSpan(list: keys), coefficients: CollectionsMarshal.AsSpan(list: coefficients));
    }
}
