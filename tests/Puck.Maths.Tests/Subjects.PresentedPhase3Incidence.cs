using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- phase 2 plumbing ----

    private static PresentedAlgebra<FixedQ4816, FixedMaterial> CompanionQuiver() =>
        PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(presentation: CodiscreteQuiver<FixedQ4816, FixedMaterial>(
            material: default,
            order: 2
        ));
    // Is `needle` a contiguous factor of `haystack`? A phase-independent witness that two constructions of one tiling
    // share a language.
    private static bool IsFactor(ReadOnlySpan<bool> haystack, ReadOnlySpan<bool> needle) {
        for (var start = 0; (start <= (haystack.Length - needle.Length)); ++start) {
            if (haystack.Slice(
                start: start,
                length: needle.Length
            ).SequenceEqual(other: needle)) { return true; }
        }

        return false;
    }
    private static BigInteger SmallInteger(long raw) =>
        (raw % 101L);
    private static ulong NextField(ref Pcg32XshRr rng, ulong modulus) =>
        (((((ulong)rng.NextUInt32()) << 32) | rng.NextUInt32()) % modulus);
    private static long NextRaw(ref Pcg32XshRr rng) =>
        unchecked((long)((((ulong)rng.NextUInt32()) << 32) | rng.NextUInt32()));
    private static bool IsSmooth(long value, ReadOnlySpan<ulong> primes) {
        var remaining = value;

        foreach (var prime in primes) {
            while (0L == (remaining % ((long)prime))) { remaining /= ((long)prime); }
        }

        return (1L == remaining);
    }
    private static BigInteger MobiusOracle(long value) {
        Span<uint> factors = stackalloc uint[32];
        var count = ((uint)value).Factorize(destination: factors);

        if (1L == value) { return BigInteger.One; }

        for (var index = 1; (index < count); ++index) {
            if (factors[index] == factors[(index - 1)]) { return BigInteger.Zero; }
        }

        return ((0 == (count & 1))
            ? BigInteger.One
            : BigInteger.MinusOne
        );
    }
    private static BigInteger DivisorCountOracle(long value) {
        Span<uint> factors = stackalloc uint[32];
        var count = ((uint)value).Factorize(destination: factors);

        if (1L == value) { return BigInteger.One; }

        var total = BigInteger.One;
        var index = 0;

        while (index < count) {
            var multiplicity = 1;

            while ((((index + multiplicity) < count) && (factors[(index + multiplicity)] == factors[index]))) { ++multiplicity; }

            total *= (multiplicity + 1);
            index += multiplicity;
        }

        return total;
    }
    private static PresentedAlgebra<BigInteger, IntegerMaterial>.Element RandomFreeCombination(PresentedAlgebra<BigInteger, IntegerMaterial> algebra, TokenPattern<BigInteger, IntegerMaterial> pattern, Random rng) {
        var result = algebra.Zero;

        for (var term = 0; (term < 3); ++term) {
            var word = algebra.Identity;
            var length = rng.Next(
                maxValue: 4,
                minValue: 0
            );

            for (var position = 0; (position < length); ++position) {
                word = algebra.Multiply(
                    left: word,
                    right: algebra.Generator(symbol: rng.Next(
                        maxValue: 2,
                        minValue: 0
                    ))
                );
            }

            result = algebra.Add(
                left: result,
                right: pattern.Scale(
                    value: word,
                    weight: rng.Next(
                        maxValue: 6,
                        minValue: 1
                    )
                )
            );
        }

        return result;
    }
    private static PresentedAlgebra<TValue, TOps>.Element AnyLetter<TValue, TOps>(TokenPattern<TValue, TOps> pattern)
        where TOps : struct, IMaterialOps<TValue, TOps> =>
        pattern.Predicate(letters: ((1UL << pattern.LetterCount) - 1UL));
    private static bool TryBuildPattern<TValue, TOps>(TokenPattern<TValue, TOps> pattern, Oracles.WordPattern tree, out PresentedAlgebra<TValue, TOps>.Element value)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        switch (tree.Kind) {
            case Oracles.WordPatternKind.Empty:
                value = pattern.EmptyWord;

                return true;
            case Oracles.WordPatternKind.Letter:
                value = pattern.Predicate(letters: (1UL << tree.Symbol));

                return true;
            case Oracles.WordPatternKind.Union: {
                    value = pattern.Algebra.Zero;

                    if (
                        !TryBuildPattern(
                        pattern: pattern,
                        tree: tree.Left!,
                        value: out var left
                    ) ||
                        !TryBuildPattern(
                        pattern: pattern,
                        tree: tree.Right!,
                        value: out var right
                    )
                    ) { return false; }

                    value = pattern.Union(
                        left: left,
                        right: right
                    );

                    return true;
                }
            case Oracles.WordPatternKind.Concatenate: {
                    value = pattern.Algebra.Zero;

                    if (
                        !TryBuildPattern(
                        pattern: pattern,
                        tree: tree.Left!,
                        value: out var left
                    ) ||
                        !TryBuildPattern(
                        pattern: pattern,
                        tree: tree.Right!,
                        value: out var right
                    )
                    ) { return false; }

                    value = pattern.Concatenate(
                        left: left,
                        right: right
                    );

                    return true;
                }
            default: {
                    value = pattern.Algebra.Zero;

                    if (!TryBuildPattern(
                        pattern: pattern,
                        tree: tree.Left!,
                        value: out var inner
                    )) { return false; }

                    return pattern.TryIterate(
                        iterated: out value,
                        obstruction: out _,
                        value: inner
                    );
                }
        }
    }
    private static string? EquivalenceAgreesWithEnumeration(PatternMatcher<ulong, PrimeFieldMaterial> left, PatternMatcher<ulong, PrimeFieldMaterial> right, bool expected) {
        var decided = PatternMatcher<ulong, PrimeFieldMaterial>.AreEquivalent(
            left: left,
            right: right,
            witness: out var witness
        );

        if (decided != expected) { return $"the pairing radical decided equivalence {decided}, expected {expected}"; }

        // The Myhill bound: two machines that agree on every span shorter than the sum of their state counts agree
        // everywhere, so a brute enumeration to that bound is a complete independent decision.
        var bound = Math.Min(
            val1: (left.StateCount + right.StateCount),
            val2: left.Window
        );
        var brute = true;
        int[]? shortest = null;

        foreach (var word in EnumerateWords(
            letterCount: left.LetterCount,
            maximumLength: bound
        )) {
            _ = left.TryMatch(
                letters: word,
                obstruction: out _,
                weight: out var leftWeight
            );
            _ = right.TryMatch(
                letters: word,
                obstruction: out _,
                weight: out var rightWeight
            );

            if (leftWeight == rightWeight) { continue; }

            brute = false;
            shortest ??= word;
        }

        if (brute != expected) { return $"the brute enumeration to {bound} decided {brute}, expected {expected}"; }

        if (
            !expected &&
            (witness.Word.Length > (shortest?.Length ?? 0))
        ) {
            return $"the reported witness has length {witness.Word.Length} but the enumeration found one of length {shortest!.Length}";
        }

        return null;
    }
    private static string? RefinementViolation<TPredicate, TRefinement>(TRefinement refinement, TPredicate[] predicates, ulong[] probes)
        where TRefinement : struct, IAlphabetRefinement<TPredicate> {
        var minterms = new TPredicate[AlphabetRefinement.MaximumMintermCount];
        var count = refinement.Minterms(
            minterms: minterms,
            predicates: predicates
        );

        if (count < 1) { return $"the refinement returned {count} block(s)"; }

        var shared = AlphabetRefinement.Refine<TPredicate, TRefinement>(
            minterms: new TPredicate[AlphabetRefinement.MaximumMintermCount],
            predicates: predicates,
            refinement: refinement
        );

        if (shared != count) { return $"the interface returned {count} block(s) and the shared loop {shared}"; }

        foreach (var token in probes) {
            var landed = -1;

            for (var block = 0; (block < count); ++block) {
                if (!refinement.Contains(
                    predicate: minterms[block],
                    token: token
                )) { continue; }

                if (landed >= 0) { return $"token {token} lies in blocks {landed} and {block}"; }

                landed = block;
            }

            if (landed < 0) { return $"token {token} lies in no block, so the partition is not total"; }

            // A block is inside a predicate or disjoint from it — never straddling — which is what makes a letter a
            // legitimate generator.
            for (var index = 0; (index < predicates.Length); ++index) {
                var inPredicate = refinement.Contains(
                    predicate: predicates[index],
                    token: token
                );
                var blockInside = !refinement.IsSatisfiable(predicate: refinement.Conjoin(
                    left: minterms[landed],
                    right: refinement.Complement(predicate: predicates[index])
                ));

                if (inPredicate != blockInside) { return $"block {landed} straddles predicate {index} at token {token}"; }
            }

            if (!refinement.Contains(
                predicate: refinement.Full,
                token: token
            )) { return $"the full predicate does not contain token {token}"; }
        }

        return null;
    }
    private static string Render(ReadOnlySpan<int> word) {
        var builder = new System.Text.StringBuilder();

        for (var index = 0; (index < word.Length); ++index) {
            _ = builder.Append(value: ((char)('a' + word[index])));
        }

        return ((0 == builder.Length)
            ? "ε"
            : builder.ToString()
        );
    }
    // Every word over the alphabet up to a length, shortest first and lexicographic within a length — the same
    // well-founded order the presentation numbers its normal forms in, so an enumerated counterexample is the least one.
    private static List<int[]> EnumerateWords(int letterCount, int maximumLength) {
        var words = new List<int[]>();
        var start = 0;

        words.Add(item: []);

        for (var length = 1; (length <= maximumLength); ++length) {
            var end = words.Count;

            for (var index = start; (index < end); ++index) {
                for (var letter = 0; (letter < letterCount); ++letter) {
                    var extended = new int[(words[index].Length + 1)];

                    words[index].CopyTo(
                        array: extended,
                        index: 0
                    );
                    extended[words[index].Length] = letter;
                    words.Add(item: extended);
                }
            }

            start = end;
        }

        return words;
    }
    private static TensorBinding<BigInteger, IntegerMaterial> IntegerTensorBinding() =>
        new(
            left: Presentations.Monogenic<BigInteger, IntegerMaterial>(
                modulus: [BigInteger.MinusOne, BigInteger.MinusOne],
                material: default
            ),
            right: Presentations.Monogenic<BigInteger, IntegerMaterial>(
                modulus: [BigInteger.One, BigInteger.Zero],
                material: default
            )
        );
    private static TensorBinding<FixedQ4816, FixedMaterial> FixedTensorBinding() =>
        new(
            left: Presentations.Monogenic<FixedQ4816, FixedMaterial>(
                modulus: [Raw(value: -OneRaw), Raw(value: -OneRaw)],
                material: default
            ),
            right: Presentations.Monogenic<FixedQ4816, FixedMaterial>(
                modulus: [Raw(value: OneRaw), FixedQ4816.Zero],
                material: default
            )
        );

    /// <summary>Two degree-two factor algebras and the tensor of their presentations, with the lane encoding both
    /// statements of the pair-up theorem read their operands through.</summary>
    private sealed class TensorBinding<TValue, TOps>
        where TOps : struct, IMaterialOps<TValue, TOps> {
        private readonly PresentedAlgebra<TValue, TOps> m_left;
        private readonly PresentedAlgebra<TValue, TOps> m_right;
        private readonly PresentedAlgebra<TValue, TOps> m_tensor;

        public TensorBinding(ChargedPresentation<TValue, TOps> left, ChargedPresentation<TValue, TOps> right) {
            m_left = PresentedAlgebra<TValue, TOps>.Create(presentation: left);
            m_right = PresentedAlgebra<TValue, TOps>.Create(presentation: right);
            m_tensor = PresentedAlgebra<TValue, TOps>.Create(presentation: Presentations.Tensor<TValue, TOps>(
                left: left,
                right: right
            ));
        }

        private PresentedAlgebra<TValue, TOps>.Element Pair(in PresentedAlgebra<TValue, TOps>.Element leftFactor, in PresentedAlgebra<TValue, TOps>.Element rightFactor) =>
            m_tensor.PairUp(
                left: leftFactor,
                right: rightFactor,
                rightKeyCount: TensorFactorKeys
            );
        private static PresentedAlgebra<TValue, TOps>.Element Read(PresentedAlgebra<TValue, TOps> algebra, ReadOnlySpan<long> lanes, int offset, Func<long, TValue> map) =>
            algebra.FromSupport(
                keys: [0L, 1L],
                coefficients: [map(lanes[offset]), map(lanes[(offset + 1)])]
            );

        /// <summary>The termwise product of the two factors' behaviors.</summary>
        public TValue BehaviorProduct(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Func<long, TValue> map) =>
            m_left.Presentation.Material.Multiply(
                left: m_left.Behavior(
                    initial: Read(
                        algebra: m_left,
                        lanes: left,
                        map: map,
                        offset: 0
                    ),
                    value: Read(
                        algebra: m_left,
                        lanes: right,
                        map: map,
                        offset: 0
                    ),
                    readout: Read(
                        algebra: m_left,
                        lanes: left,
                        map: map,
                        offset: 4
                    )
                ),
                right: m_right.Behavior(
                    initial: Read(
                        algebra: m_right,
                        lanes: left,
                        map: map,
                        offset: 2
                    ),
                    value: Read(
                        algebra: m_right,
                        lanes: right,
                        map: map,
                        offset: 2
                    ),
                    readout: Read(
                        algebra: m_right,
                        lanes: right,
                        map: map,
                        offset: 4
                    )
                )
            );
        /// <summary>The behavior of the pair-up: one initial vector, one step and one readout, all in the tensor.</summary>
        public TValue PairedBehavior(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Func<long, TValue> map) =>
            m_tensor.Behavior(
                initial: Pair(
                    leftFactor: Read(
                        algebra: m_left,
                        lanes: left,
                        map: map,
                        offset: 0
                    ),
                    rightFactor: Read(
                        algebra: m_right,
                        lanes: left,
                        map: map,
                        offset: 2
                    )
                ),
                value: Pair(
                    leftFactor: Read(
                        algebra: m_left,
                        lanes: right,
                        map: map,
                        offset: 0
                    ),
                    rightFactor: Read(
                        algebra: m_right,
                        lanes: right,
                        map: map,
                        offset: 2
                    )
                ),
                readout: Pair(
                    leftFactor: Read(
                        algebra: m_left,
                        lanes: left,
                        map: map,
                        offset: 4
                    ),
                    rightFactor: Read(
                        algebra: m_right,
                        lanes: right,
                        map: map,
                        offset: 4
                    )
                )
            );
    }
    /// <summary>One presented algebra over the house scalar bound to a lane vector: the permutation between a lane and
    /// a normal-form key, plus the support buffers the encoding reuses.</summary>
    private sealed class FixedLaneAlgebra {
        private readonly FixedQ4816[] m_coefficients;
        private readonly long[] m_keys;
        private readonly FixedQ4816[] m_productCoefficients;
        private readonly long[] m_productKeys;

        public FixedLaneAlgebra(ChargedPresentation<FixedQ4816, FixedMaterial> presentation, int[] keyToLane) {
            Algebra = PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(presentation: presentation);
            KeyToLane = keyToLane;
            m_coefficients = new FixedQ4816[keyToLane.Length];
            m_keys = new long[keyToLane.Length];
            m_productCoefficients = new FixedQ4816[Algebra.MaximumSupportCount];
            m_productKeys = new long[Algebra.MaximumSupportCount];
        }

        public PresentedAlgebra<FixedQ4816, FixedMaterial> Algebra { get; }
        public int[] KeyToLane { get; }

        private PresentedAlgebra<FixedQ4816, FixedMaterial>.Element Negate(in PresentedAlgebra<FixedQ4816, FixedMaterial>.Element element) {
            var material = default(FixedMaterial);

            for (var index = 0; (index < element.SupportCount); ++index) {
                m_coefficients[index] = material.Negate(value: element.Coefficients[index]);
                m_keys[index] = element.Keys[index];
            }

            return Algebra.FromSupport(
                keys: m_keys.AsSpan(
                    start: 0,
                    length: element.SupportCount
                ),
                coefficients: m_coefficients.AsSpan(
                    start: 0,
                    length: element.SupportCount
                )
            );
        }
        private PresentedAlgebra<FixedQ4816, FixedMaterial>.Element Read(ReadOnlySpan<long> lanes) {
            var support = 0;

            for (var key = 0; (key < KeyToLane.Length); ++key) {
                var raw = lanes[KeyToLane[key]];

                if (0L == raw) { continue; }

                m_coefficients[support] = Raw(value: raw);
                m_keys[support] = key;
                ++support;
            }

            return Algebra.FromSupport(
                keys: m_keys.AsSpan(
                    length: support,
                    start: 0
                ),
                coefficients: m_coefficients.AsSpan(
                    length: support,
                    start: 0
                )
            );
        }
        private void Write(in PresentedAlgebra<FixedQ4816, FixedMaterial>.Element element, Span<long> result) {
            result.Clear();

            for (var index = 0; (index < element.SupportCount); ++index) {
                result[KeyToLane[((int)element.Keys[index])]] = element.Coefficients[index].Value;
            }
        }

        public void Associator(ReadOnlySpan<long> a, ReadOnlySpan<long> b, ReadOnlySpan<long> c, Span<long> result) {
            var algebra = Algebra;
            var x = Read(lanes: a);
            var y = Read(lanes: b);
            var z = Read(lanes: c);
            var before = algebra.Multiply(
                left: algebra.Multiply(
                    left: x,
                    right: y
                ),
                right: z
            );
            var after = algebra.Multiply(
                left: x,
                right: algebra.Multiply(
                    left: y,
                    right: z
                )
            );

            Write(
                element: algebra.Add(
                    left: before,
                    right: Negate(element: after)
                ),
                result: result
            );
        }
        public void Multiply(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
            Write(
                element: Algebra.Multiply(
                    left: Read(lanes: left),
                    right: Read(lanes: right)
                ),
                result: result
            );
        /// <summary>The same product written into buffers this binding owns, through the allocation-free overload.</summary>
        public void MultiplyInto(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
            var support = Algebra.MultiplyInto(
                left: Read(lanes: left),
                right: Read(lanes: right),
                keys: m_productKeys,
                coefficients: m_productCoefficients
            );

            result.Clear();

            for (var index = 0; (index < support); ++index) {
                result[KeyToLane[((int)m_productKeys[index])]] = m_productCoefficients[index].Value;
            }
        }
    }
    /// <summary>One presented monogenic algebra over <see cref="ParityMaterial"/> bound to a coefficient-bit vector; the
    /// key IS the exponent, so lane and key coincide.</summary>
    private sealed class ParityLaneAlgebra {
        private readonly PresentedAlgebra<ulong, ParityMaterial> m_algebra;
        private readonly ulong[] m_coefficients;
        private readonly long[] m_keys;

        public ParityLaneAlgebra(int degree, ulong reductionTail) {
            var tail = new ulong[degree];

            for (var exponent = 0; (exponent < degree); ++exponent) { tail[exponent] = (reductionTail >> exponent) & 1UL; }

            m_algebra = PresentedAlgebra<ulong, ParityMaterial>.Create(presentation: Presentations.Monogenic<ulong, ParityMaterial>(
                material: default,
                modulus: tail
            ));
            m_coefficients = new ulong[degree];
            m_keys = new long[degree];
        }

        private PresentedAlgebra<ulong, ParityMaterial>.Element Read(ReadOnlySpan<long> lanes) {
            var support = 0;

            for (var lane = 0; (lane < m_keys.Length); ++lane) {
                if (0L == (lanes[lane] & 1L)) { continue; }

                m_coefficients[support] = 1UL;
                m_keys[support] = lane;
                ++support;
            }

            return m_algebra.FromSupport(
                keys: m_keys.AsSpan(
                    length: support,
                    start: 0
                ),
                coefficients: m_coefficients.AsSpan(
                    length: support,
                    start: 0
                )
            );
        }

        public void Multiply(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
            var product = m_algebra.Multiply(
                left: Read(lanes: left),
                right: Read(lanes: right)
            );

            result.Clear();

            for (var index = 0; (index < product.SupportCount); ++index) { result[((int)product.Keys[index])] = 1L; }
        }
    }

    // ---- phase 3: incidence, and the exterior calculus that rides it ----

    // The complexes whose Euler characteristic is hand-computable, so the Möbius mass has something to answer to that
    // was not computed by any of the machinery under test.
    private static readonly (string Name, int[][] TopFaces, int Expected)[] EulerWorlds = [
        ("a filled triangle", [[0, 1, 2]], 1),
        ("the boundary of a tetrahedron", [[0, 1, 2], [0, 1, 3], [0, 2, 3], [1, 2, 3]], 2),
        ("a circle", [[0, 1], [1, 2], [0, 2]], 0),
        ("a solid tetrahedron", [[0, 1, 2, 3]], 1),
        ("two points", [[0], [1]], 2),
    ];
    // The three worlds every chain-level statement runs at: the smallest with a two-dimensional cell, the smallest
    // closed surface, and the smallest world with a hole.
    private static readonly int[] ChainWorlds = [0, 1, 2];

    // The house scalar's headroom: a coboundary or boundary coefficient is a signed sum of at most three operand raws
    // at these worlds, so three bits of headroom keep it inside the carrier. The full-range behaviour is the
    // divergence witness the claim pins beside it.
    private const int StokesHeadroomShift = 3;

    /// <summary>Proves the Euler characteristic of a complex is the Möbius mass of its bounded face order, against an
    /// alternating cell count and against three hand-computed values, and proves the incidence algebra underneath it
    /// is the presented one — basis-associative, unital, and inverting zeta by the guarded sum alone.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? IncidenceEulerMass() {
        foreach (var (name, topFaces, expected) in EulerWorlds) {
            var (dimensions, incidences) = SimplicialComplex(topFaces: topFaces);
            var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: dimensions,
                incidences: incidences,
                material: default
            );
            var poset = calculus.Poset;

            if (dimensions.Length != calculus.CellCount) { return $"{name}: the calculus holds {calculus.CellCount} cell(s) where the complex has {dimensions.Length}"; }

            if (poset.IntervalCount != poset.Algebra.MaximumSupportCount) {
                return $"{name}: the order holds {poset.IntervalCount} interval(s) but the algebra bounds a support at {poset.Algebra.MaximumSupportCount}";
            }

            // The key scheme is a bijection onto the comparable pairs, and comparability is decided independently here
            // by closing the declared relations rather than by asking the entry that built them.
            var order = ComparabilityClosure(
                elementCount: poset.ElementCount,
                dimensions: dimensions,
                incidences: incidences
            );

            for (var key = 0; (key < poset.IntervalCount); ++key) {
                var (lower, upper) = poset.Interval(key: key);

                if (
                    !poset.TryKey(
                    key: out var recovered,
                    lower: lower,
                    upper: upper
                ) ||
                    (recovered != key)
                ) {
                    return $"{name}: key {key} names [{lower}, {upper}], which mapped back to {recovered}";
                }
            }

            for (var lower = 0; (lower < poset.ElementCount); ++lower) {
                for (var upper = 0; (upper < poset.ElementCount); ++upper) {
                    if (order[((lower * poset.ElementCount) + upper)] != poset.TryKey(
                        key: out _,
                        lower: lower,
                        upper: upper
                    )) {
                        return $"{name}: [{lower}, {upper}] is an interval exactly when the two are comparable, and the two disagreed";
                    }
                }
            }

            if (!poset.TryMobius(
                mobius: out var mobius,
                obstruction: out var refusal
            )) {
                return $"{name}: the Möbius element was refused (attempted {refusal.Attempted}, steps {refusal.StepsTaken}, key {refusal.SupportKey})";
            }

            if (!poset.Algebra.AreEqual(
                left: poset.Algebra.Multiply(
                    left: mobius,
                    right: poset.Zeta
                ),
                right: poset.Algebra.Identity
            )) {
                return $"{name}: mu convolved with zeta is not the unit of the incidence algebra";
            }

            // The defining recursion, evaluated here rather than taken from the star: mu is one on a singleton
            // interval and cancels the sum below it everywhere else.
            for (var key = 0; (key < poset.IntervalCount); ++key) {
                var (lower, upper) = poset.Interval(key: key);

                if (lower == upper) {
                    if (BigInteger.One != mobius[key]) { return $"{name}: mu[{lower}, {lower}] is {mobius[key]}, not one"; }

                    continue;
                }

                var below = BigInteger.Zero;

                for (var middle = 0; (middle < poset.ElementCount); ++middle) {
                    if (
                        (middle == upper) ||
                        !poset.TryKey(
                        key: out var head,
                        lower: lower,
                        upper: middle
                    ) ||
                        !poset.TryKey(
                        key: out _,
                        lower: middle,
                        upper: upper
                    )
                    ) { continue; }

                    below += mobius[head];
                }

                if (mobius[key] != -below) { return $"{name}: mu[{lower}, {upper}] is {mobius[key]}, the recursion below it says {-below}"; }
            }

            if (!calculus.TryEulerCharacteristic(
                characteristic: out var characteristic,
                obstruction: out var eulerRefusal
            )) {
                return $"{name}: the Euler characteristic was refused (attempted {eulerRefusal.Attempted})";
            }

            var alternating = BigInteger.Zero;

            foreach (var dimension in dimensions) {
                alternating += ((0 == (dimension & 1))
                ? BigInteger.One
                : BigInteger.MinusOne
            );
            }

            if (characteristic != alternating) { return $"{name}: the Möbius mass reads {characteristic} where the alternating cell count reads {alternating}"; }

            if (characteristic != expected) { return $"{name}: the Euler characteristic reads {characteristic}, and it is {expected}"; }
        }

        // The certificate, at the smallest world: the incidence algebra is a genuine associative unital algebra, and
        // its annihilating basis pairs — every mismatch of endpoints — are reported as the zero divisors they are.
        {
            var (dimensions, incidences) = SimplicialComplex(topFaces: EulerWorlds[2].TopFaces);
            var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: dimensions,
                incidences: incidences,
                material: default
            );
            var certificate = calculus.Poset.Algebra.Certify(overlapLimit: (1L << 22));

            if (ClosureOutcome.BasisAssociativityVerified != certificate.Outcome) { return $"the circle's incidence algebra certifies {certificate.Outcome}"; }

            if (
                !certificate.IsAssociative ||
                !certificate.HasIdentity ||
                !certificate.IsCoherent ||
                !certificate.IsAlternative
            ) {
                return $"the circle's incidence algebra reports associative {certificate.IsAssociative}, unital {certificate.HasIdentity}, coherent {certificate.IsCoherent}, alternative {certificate.IsAlternative}";
            }

            if (certificate.IsCommutative) { return "the circle's incidence algebra reports commutative, and a nontrivial order composes in one direction only"; }

            if (0L != certificate.NonAssociativeTripleCount) { return $"the circle's incidence algebra reports {certificate.NonAssociativeTripleCount} nonassociative basis triple(s)"; }

            if (0 == certificate.ZeroDivisorWitness.Length) { return "the circle's incidence algebra reports no zero divisors, and every endpoint mismatch is one"; }
        }

        return null;
    }
    /// <summary>Proves the Möbius function of the divisibility ORDER agrees with the Dirichlet window's, interval by
    /// interval, and that the two are related as an algebra to its quotient rather than as a specialization.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The window presents the free commutative monoid on the primes, so its basis is the integers; the
    /// incidence algebra of the same order has the ordered divisor PAIRS for a basis. The interval type
    /// <c>[a, b] ↦ b / a</c> is the map between them, and it is onto and not injective — which is exactly what makes
    /// the window the reduced incidence algebra and not a specialization of this entry.</remarks>
    public static string? IncidenceMobiusMatchesWindow() {
        const long Bound = 30L;

        ulong[] primes = [2UL, 3UL, 5UL, 7UL, 11UL, 13UL, 17UL, 19UL, 23UL, 29UL];

        var relations = new List<(int Lower, int Upper)>();

        for (var divisor = 1L; (divisor <= Bound); ++divisor) {
            for (var multiple = (divisor + 1L); (multiple <= Bound); ++multiple) {
                if (0L == (multiple % divisor)) { relations.Add(item: ((((int)divisor) - 1), (((int)multiple) - 1))); }
            }
        }

        var poset = IncidenceAlgebra<BigInteger, IntegerMaterial>.Create(
            elementCount: ((int)Bound),
            relations: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: relations),
            material: default
        );
        var window = DivisibilityAlgebra<BigInteger, IntegerMaterial>.Create(
            material: default,
            primes: primes,
            window: Bound
        );

        // The window's own precondition first, exactly as the classical identities require it: these primes cover
        // every integer through the bound, so the two structures are stated over the same set of integers.
        if (Bound != window.ConsecutiveBound) { return $"the window covers every integer only through {window.ConsecutiveBound}, so it is not the whole order's reduced algebra"; }

        var divisorPairs = 0;

        for (var multiple = 1L; (multiple <= Bound); ++multiple) {
            for (var divisor = 1L; (divisor <= multiple); ++divisor) {
                if (0L == (multiple % divisor)) { ++divisorPairs; }
            }
        }

        if (poset.IntervalCount != divisorPairs) { return $"the divisibility order holds {poset.IntervalCount} interval(s) against {divisorPairs} divisor pair(s) through {Bound}"; }

        if (poset.IntervalCount <= window.NormalFormCount) {
            return $"the order's {poset.IntervalCount} interval(s) do not outnumber the window's {window.NormalFormCount} integer(s), so the two would be the same algebra";
        }

        if (!poset.TryMobius(
            mobius: out var intervalMobius,
            obstruction: out var refusal
        )) {
            return $"the order's Möbius element was refused (attempted {refusal.Attempted}, steps {refusal.StepsTaken})";
        }

        if (!window.TryMobius(
            mobius: out var windowMobius,
            obstruction: out var windowRefusal
        )) {
            return $"the window's Möbius element was refused (attempted {windowRefusal.Attempted}, steps {windowRefusal.StepsTaken})";
        }

        if (!poset.Algebra.AreEqual(
            left: poset.Algebra.Multiply(
                left: intervalMobius,
                right: poset.Zeta
            ),
            right: poset.Algebra.Identity
        )) {
            return "mu convolved with zeta is not the unit of the divisibility order's incidence algebra";
        }

        var reached = new int[(Bound + 1L)];

        for (var key = 0; (key < poset.IntervalCount); ++key) {
            var (lower, upper) = poset.Interval(key: key);
            var divisor = (lower + 1L);
            var multiple = (upper + 1L);
            var type = (multiple / divisor);

            ++reached[type];

            if (!window.TryKey(
                value: type,
                out var windowKey
            )) { return $"the interval [{divisor}, {multiple}] has type {type}, which the window does not hold"; }

            if (intervalMobius[key] != windowMobius[windowKey]) {
                return $"mu[{divisor}, {multiple}] is {intervalMobius[key]} where the window's mu({type}) is {windowMobius[windowKey]}";
            }

            if (intervalMobius[key] != MobiusOracle(value: type)) {
                return $"mu[{divisor}, {multiple}] is {intervalMobius[key]} where the factorization says mu({type}) is {MobiusOracle(value: type)}";
            }
        }

        var repeated = 0;

        for (var type = 1L; (type <= Bound); ++type) {
            if (0 == reached[type]) { return $"no interval of the order has type {type}, so the interval type does not reach the whole window"; }

            if (reached[type] > 1) { ++repeated; }
        }

        if (0 == repeated) { return "every interval type is reached exactly once, so the window would be a relabelling of the order rather than its quotient"; }

        return null;
    }
    /// <summary>Proves the whole exterior-calculus surface: the incidence element squares to zero, the boundary and
    /// the coboundary are that one element multiplied on the two sides and agree with a plain incidence-table loop,
    /// the pairing is the sum it claims to be, and Stokes' identity holds exactly — with the house scalar's carrier
    /// bound measured rather than assumed.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? StokesAdjunction() {
        foreach (var world in ChainWorlds) {
            var (name, topFaces, _) = EulerWorlds[world];
            var (dimensions, incidences) = SimplicialComplex(topFaces: topFaces);
            var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: dimensions,
                incidences: incidences,
                material: default
            );
            var algebra = calculus.Poset.Algebra;
            var cellCount = calculus.CellCount;

            // The chain-complex condition, stated where it actually lives: ONE element of the incidence algebra,
            // squared. Both operator statements below are readings of it.
            if (0 != algebra.Multiply(
                left: calculus.Incidence,
                right: calculus.Incidence
            ).SupportCount) {
                return $"{name}: the incidence element does not square to zero, so the declared numbers are no chain complex";
            }

            for (var cell = 0; (cell < cellCount); ++cell) {
                var chain = algebra.FromSupport(
                    keys: [calculus.ChainKey(cell: cell)],
                    coefficients: [BigInteger.One]
                );
                var cochain = algebra.FromSupport(
                    keys: [calculus.CochainKey(cell: cell)],
                    coefficients: [BigInteger.One]
                );
                var boundary = calculus.Boundary(chain: chain);
                var coboundary = calculus.Coboundary(cochain: cochain);

                if (0 != calculus.Boundary(chain: boundary).SupportCount) { return $"{name}: the boundary of the boundary of cell {cell} is not zero"; }

                if (0 != calculus.Coboundary(cochain: coboundary).SupportCount) { return $"{name}: the coboundary of the coboundary of cell {cell} is not zero"; }

                // Against a plain loop over the declared table, which shares no step with the product.
                for (var other = 0; (other < cellCount); ++other) {
                    var lowered = BigInteger.Zero;
                    var raised = BigInteger.Zero;

                    foreach (var (face, coface, sign) in incidences) {
                        if (
                            (face == other) &&
                            (coface == cell)
                        ) { lowered += sign; }
                        if (
                            (face == cell) &&
                            (coface == other)
                        ) { raised += sign; }
                    }

                    if (boundary[calculus.ChainKey(cell: other)] != lowered) {
                        return $"{name}: the boundary of cell {cell} carries {boundary[calculus.ChainKey(cell: other)]} at cell {other} where the incidence table carries {lowered}";
                    }

                    if (coboundary[calculus.CochainKey(cell: other)] != raised) {
                        return $"{name}: the coboundary of cell {cell} carries {coboundary[calculus.CochainKey(cell: other)]} at cell {other} where the incidence table carries {raised}";
                    }
                }
            }

            // Stokes over every ordered basis pair, then over dense operands whose pairing is checked against the sum
            // it is supposed to be.
            for (var left = 0; (left < cellCount); ++left) {
                var cochain = algebra.FromSupport(
                    keys: [calculus.CochainKey(cell: left)],
                    coefficients: [BigInteger.One]
                );

                for (var right = 0; (right < cellCount); ++right) {
                    var chain = algebra.FromSupport(
                        keys: [calculus.ChainKey(cell: right)],
                        coefficients: [BigInteger.One]
                    );
                    var raised = calculus.Pair(
                        cochain: calculus.Coboundary(cochain: cochain),
                        chain: chain
                    );
                    var lowered = calculus.Pair(
                        cochain: cochain,
                        chain: calculus.Boundary(chain: chain)
                    );

                    if (raised != lowered) { return $"{name}: Stokes fails at cochain {left} and chain {right}: {raised} against {lowered}"; }
                }
            }

            for (var shift = 0; (shift < cellCount); ++shift) {
                var chainValues = new BigInteger[cellCount];
                var cochainValues = new BigInteger[cellCount];

                for (var cell = 0; (cell < cellCount); ++cell) {
                    chainValues[cell] = ((cell + 1) * ((0 == ((cell + shift) & 1))
                        ? 3
                        : -5));
                    cochainValues[cell] = ((cell + 2) * ((0 == ((cell + shift) & 3))
                        ? -7
                        : 2));
                }

                var chain = calculus.Chain(values: chainValues);
                var cochain = calculus.Cochain(values: cochainValues);
                var total = BigInteger.Zero;

                for (var cell = 0; (cell < cellCount); ++cell) { total += (chainValues[cell] * cochainValues[cell]); }

                if (calculus.Pair(
                    chain: chain,
                    cochain: cochain
                ) != total) { return $"{name}: the pairing is not the sum of the coefficientwise products at shift {shift}"; }

                if (calculus.Pair(
                    cochain: calculus.Coboundary(cochain: cochain),
                    chain: chain
                ) != calculus.Pair(
                    cochain: cochain,
                    chain: calculus.Boundary(chain: chain)
                )) {
                    return $"{name}: Stokes fails at dense operands, shift {shift}";
                }
            }

            // The same chain-complex condition over GF(2), where the orientation signs collapse and the statement is
            // about the parity of the covering count alone.
            var parity = ExteriorCalculus<ulong, ParityMaterial>.Create(
                dimensions: dimensions,
                incidences: incidences,
                material: default
            );

            if (0 != parity.Poset.Algebra.Multiply(
                left: parity.Incidence,
                right: parity.Incidence
            ).SupportCount) {
                return $"{name}: the incidence element does not square to zero over the parity material";
            }
        }

        // The house scalar's boundary, witnessed rather than described: the identity survives the one-rounding fold,
        // and what breaks it is a coboundary or boundary coefficient wrapping the carrier — so at raws chosen to wrap
        // it, the two sides MUST differ.
        {
            var (dimensions, incidences) = SimplicialComplex(topFaces: EulerWorlds[0].TopFaces);
            var calculus = ExteriorCalculus<FixedQ4816, FixedMaterial>.Create(
                dimensions: dimensions,
                incidences: incidences,
                material: default
            );
            long[] ladder = [long.MinValue, long.MaxValue, (long.MaxValue - 1L), 1L, -1L, (1L << 62), -(1L << 62)];
            var divergences = 0;

            foreach (var cochainRaw in ladder) {
                foreach (var chainRaw in ladder) {
                    var chainValues = new FixedQ4816[calculus.CellCount];
                    var cochainValues = new FixedQ4816[calculus.CellCount];

                    for (var cell = 0; (cell < calculus.CellCount); ++cell) {
                        chainValues[cell] = Raw(value: chainRaw);
                        cochainValues[cell] = Raw(value: cochainRaw);
                    }

                    var chain = calculus.Chain(values: chainValues);
                    var cochain = calculus.Cochain(values: cochainValues);

                    if (calculus.Pair(
                        cochain: calculus.Coboundary(cochain: cochain),
                        chain: chain
                    ).Value != calculus.Pair(
                        cochain: cochain,
                        chain: calculus.Boundary(chain: chain)
                    ).Value) {
                        ++divergences;
                    }
                }
            }

            // A floor a third below the 12 of 49 wrapping pairs the shipped ladder separates on the filled triangle,
            // which is the row-15 shape: a real regression fails the case and sampling drift cannot. The floor was 4,
            // set without the measurement, and would have survived a two-thirds collapse of the effect.
            if (divergences < 8) {
                return $"the carrier bound is not load-bearing: only {divergences} of {(ladder.Length * ladder.Length)} wrapping operand pairs separated the two sides";
            }
        }

        return null;
    }
    /// <summary>Stokes' identity over the house scalar, bit for bit, at operands reduced by the carrier headroom the
    /// intermediate sums need.</summary>
    /// <returns>The swept claim.</returns>
    /// <remarks>The one-rounding discipline is what makes it hold: every incidence number is an exact integer of the
    /// carrier, so the coboundary and the boundary coefficients are exact and the single fused fold on each side sums
    /// the identical exact quantity. What the headroom keeps out is carrier overflow, not rounding.
    /// The bit-identity of the two sides is INTRA-PRESENTED — one algebra, one fused fold, read twice — so the claim
    /// also carries the third leg (worklist A12): the raised pairing's VALUE against the same quantity accumulated
    /// exactly in <c>Int128</c>/<c>BigInteger</c> from the incidence table and the operand raws and rounded once by
    /// <see cref="Oracles.RoundDyadic"/> at shift sixteen. That is the only absolute statement over the house scalar
    /// anywhere in the Stokes slice, and it is what the case's divergence canary needs beside it.</remarks>
    public static Func<long[], long[], string?> StokesAdjunctionFixed() {
        var (dimensions, incidences) = SimplicialComplex(topFaces: EulerWorlds[0].TopFaces);
        var calculus = ExteriorCalculus<FixedQ4816, FixedMaterial>.Create(
            dimensions: dimensions,
            incidences: incidences,
            material: default
        );
        var chainValues = new FixedQ4816[calculus.CellCount];
        var cochainValues = new FixedQ4816[calculus.CellCount];
        var chainRaws = new long[calculus.CellCount];
        var cochainRaws = new long[calculus.CellCount];
        var coboundaryRaws = new Int128[calculus.CellCount];

        return (left, right) => {
            for (var cell = 0; (cell < calculus.CellCount); ++cell) {
                chainRaws[cell] = (right[cell] >> StokesHeadroomShift);
                cochainRaws[cell] = (left[cell] >> StokesHeadroomShift);
                chainValues[cell] = Raw(value: chainRaws[cell]);
                cochainValues[cell] = Raw(value: cochainRaws[cell]);
                coboundaryRaws[cell] = Int128.Zero;
            }

            var chain = calculus.Chain(values: chainValues);
            var cochain = calculus.Cochain(values: cochainValues);
            var raised = calculus.Pair(
                cochain: calculus.Coboundary(cochain: cochain),
                chain: chain
            );
            var lowered = calculus.Pair(
                cochain: cochain,
                chain: calculus.Boundary(chain: chain)
            );

            if (raised.Value != lowered.Value) { return $"Stokes reads {raised.Value} raised and {lowered.Value} lowered"; }

            // The third leg. The coboundary of a cochain is the signed sum of the coefficients of a cell's own faces
            // (ExteriorCalculus.Coboundary), which the incidence table states outright; the pairing is Σ ω(σ)·c(σ) with
            // exactly one rounding. Both are re-formed here from the raws and the table alone, reaching no kernel of the
            // algebra under test and no house rounder.
            foreach (var (lower, upper, sign) in incidences) {
                coboundaryRaws[upper] += (sign * ((Int128)cochainRaws[lower]));
            }

            var exact = BigInteger.Zero;

            for (var cell = 0; (cell < calculus.CellCount); ++cell) {
                exact += (((BigInteger)coboundaryRaws[cell]) * chainRaws[cell]);
            }

            var expected = Oracles.RoundDyadic(
                exact: exact,
                shift: 16
            );

            return ((raised.Value == expected)
                ? null
                : $"the raised pairing reads {raised.Value} where one rounding of the exact Σ ω(σ)·c(σ) is {expected}"
            );
        };
    }
    /// <summary>Proves the incidence and exterior-calculus surfaces refuse the data that names no such object, and
    /// refuse it at construction with a named parameter rather than anonymously.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when every refusal held.</returns>
    public static string? IncidenceLimitsRefuse() {
        var chain = new List<(int Lower, int Upper)>();

        for (var lower = 0; (lower < 30); ++lower) {
            for (var upper = (lower + 1); (upper < 30); ++upper) { chain.Add(item: (lower, upper)); }
        }

        var wide = chain.ToArray();

        return (RefusesDeclaration(
            name: "a relation closing into a cycle",
            build: static () => _ = Presentations.IntervalPoset<BigInteger, IntegerMaterial>(
                elementCount: 3,
                material: default,
                relations: [(0, 1), (1, 2), (2, 0)]
            )
        )
            ?? (RefusesDeclaration(
            name: "a relation leaving the element range",
            build: static () => _ = Presentations.IntervalPoset<BigInteger, IntegerMaterial>(
                elementCount: 2,
                material: default,
                relations: [(0, 2)]
            )
        )
            ?? (RefusesDeclaration(
            name: "an order on no elements",
            build: static () => _ = Presentations.IntervalPoset<BigInteger, IntegerMaterial>(
                elementCount: 0,
                material: default,
                relations: []
            )
        )
            ?? (RefusesDeclaration(
            name: "an order past the element cap",
            build: static () => _ = Presentations.IntervalPoset<BigInteger, IntegerMaterial>(
                elementCount: 257,
                material: default,
                relations: []
            )
        )
            ?? (RefusesDeclaration(
            name: "an order with more intervals than a finite basis holds",
            build: () => _ = Presentations.IntervalPoset<BigInteger, IntegerMaterial>(
                elementCount: 30,
                material: default,
                relations: wide
            )
        )
            ?? (RefusesDeclaration(
            name: "an oriented complex over an unsigned material",
            build: static () => _ = ExteriorCalculus<BigInteger, CountingMaterial>.Create(
                dimensions: [0, 0, 1],
                incidences: [(0, 2, 1), (1, 2, -1)],
                material: default
            )
        )
            ?? (RefusesDeclaration(
            name: "an incidence naming a cell outside the complex",
            build: static () => _ = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: [0, 0, 1],
                incidences: [(0, 3, 1)],
                material: default
            )
        )
            // Three cells, so the complex reaches degree two on its own and the refusal is the incidence's step rather
            // than the dimension label's size.
            ?? (RefusesDeclaration(
            name: "an incidence stepping the dimension by two",
            build: static () => _ = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: [0, 0, 2],
                incidences: [(0, 2, 1)],
                material: default
            )
        )
            ?? (RefusesDeclaration(
            name: "an orientation number that is not a sign",
            build: static () => _ = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: [0, 0, 1],
                incidences: [(0, 2, 2), (1, 2, -1)],
                material: default
            )
        )
            ?? (RefusesDeclaration(
            name: "one incidence pair declared twice",
            build: static () => _ = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: [0, 0, 1],
                incidences: [(0, 2, 1), (0, 2, -1)],
                material: default
            )
        )
            ?? (RefusesDeclaration(
            name: "a complex with a negative dimension",
            build: static () => _ = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: [-1],
                incidences: [],
                material: default
            )
        )
            ?? (RefusesDeclaration(
            name: "a complex with no cells",
            build: static () => _ = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: [],
                incidences: [],
                material: default
            )
        )
            // The cell cap is REACHABLE, which is the whole point of it being 84: a bounded face order carries 3n + 3
            // intervals before a single incidence is declared, so 84 cells is 255 of the incidence algebra's 256 and 85
            // is refused for every incidence list. A cap of 128 could never bite and the refusal came out of the poset
            // naming the wrong parameter.
            ?? (RefusesDeclaration(
            name: "a complex past the cell cap",
            build: static () => _ = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: new int[(ExteriorCalculus<BigInteger, IntegerMaterial>.MaximumCellCount + 1)],
                incidences: [],
                material: default
            )
        )
            ?? (((84 == ExteriorCalculus<BigInteger, IntegerMaterial>.MaximumCellCount)
            ? AdmitsTheWholeCellCap()
            : $"the cell cap is {ExteriorCalculus<BigInteger, IntegerMaterial>.MaximumCellCount}, where the interval cap of 256 admits exactly 84 cells at 3n + 3 intervals")
            ?? (RefusesDeclaration(
            name: "a key naming no interval",
            build: static () => {
                var poset = IncidenceAlgebra<BigInteger, IntegerMaterial>.Create(
                    elementCount: 2,
                    material: default,
                    relations: [(0, 1)]
                );

                _ = poset.Interval(key: poset.IntervalCount);
            }
        )
            ?? (RefusesDeclaration(
            name: "an endpoint naming no element",
            build: static () => {
                var poset = IncidenceAlgebra<BigInteger, IntegerMaterial>.Create(
                    elementCount: 2,
                    material: default,
                    relations: [(0, 1)]
                );

                _ = poset.TryKey(
                    key: out _,
                    lower: 0,
                    upper: 2
                );
            }
        )
            ?? (RefusesDeclaration(
            name: "a chain carrying more values than the complex has cells",
            build: static () => {
                var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                    dimensions: [0, 0, 1],
                    incidences: [(0, 2, 1), (1, 2, -1)],
                    material: default
                );

                _ = calculus.Chain(values: [BigInteger.One, BigInteger.One, BigInteger.One, BigInteger.One]);
            }
        )
            ?? (RefusesDeclaration(
            name: "a cochain carrying more values than the complex has cells",
            build: static () => {
                var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                    dimensions: [0, 0, 1],
                    incidences: [(0, 2, 1), (1, 2, -1)],
                    material: default
                );

                _ = calculus.Cochain(values: [BigInteger.One, BigInteger.One, BigInteger.One, BigInteger.One]);
            }
        )
            ?? (RefusesDeclaration(
            name: "a key request for a cell outside the complex",
            build: static () => {
                var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                    dimensions: [0, 0, 1],
                    incidences: [(0, 2, 1), (1, 2, -1)],
                    material: default
                );

                _ = calculus.ChainKey(cell: 3);
            }
        )
            ?? (RefusesDeclaration(
            name: "a coefficient request for a cell outside the complex",
            build: static () => {
                var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                    dimensions: [0, 0, 1],
                    incidences: [(0, 2, 1), (1, 2, -1)],
                    material: default
                );

                _ = calculus.CochainKey(cell: -1);
            }
        )
            ?? RefusesInversion()))))))))))))))))))));
    }
    /// <summary>Proves a cell's dimension is a degree the complex's cell count can reach: an oversized label is refused
    /// by name, and the widest grading the cell cap allows is admitted whole.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the bound held.</returns>
    /// <remarks>A dimension is a LABEL, and the graded reading runs densely from zero to the top, so the label alone
    /// sizes every per-degree table — a one-cell complex labelled a billion would demand a billion degrees. Each case
    /// below asserts the refusal; none provokes the allocation.</remarks>
    public static string? CellDimensionBoundHolds() =>
        (RefusesDimension(
            dimension: 1_000_000_000,
            dimensions: [1_000_000_000]
        )
            ?? (RefusesDimension(
            dimension: int.MaxValue,
            dimensions: [int.MaxValue]
        )
            ?? (RefusesDimension(
            dimension: 1_000_000_000,
            dimensions: [0, 0, 1_000_000_000]
        )
            ?? (RefusesDimension(
            dimension: int.MaxValue,
            dimensions: [0, int.MaxValue, 1]
        )
            // One past the top the cells reach, which is the boundary the bound actually draws rather than an
            // astronomical label a size check would catch on its own.
            ?? (RefusesDimension(
            dimension: 2,
            dimensions: [0, 2]
        )
            ?? AdmitsTheWholeGrading())))));

    // The bound is REACHABLE, which is what keeps it from being a wall: 84 cells at one cell per degree present a
    // grading topping out at 83, every degree holding the cell declared there.
    private static string? AdmitsTheWholeGrading() {
        var cellCount = ExteriorCalculus<BigInteger, IntegerMaterial>.MaximumCellCount;
        var dimensions = new int[cellCount];

        for (var cell = 0; (cell < cellCount); ++cell) { dimensions[cell] = cell; }

        var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
            dimensions: dimensions,
            incidences: [],
            material: default
        );

        if ((cellCount - 1) != calculus.Dimension) {
            return $"a complex of {cellCount} cells declared one cell per degree tops out at dimension {calculus.Dimension} where it reaches {(cellCount - 1)}";
        }

        for (var degree = 0; (degree < cellCount); ++degree) {
            var cells = calculus.CellsOfDegree(degree: degree);

            if (
                (1 != cells.Length) ||
                (degree != cells[0])
            ) {
                return $"degree {degree} of the widest grading holds {cells.Length} cell(s) rather than the one cell declared there";
            }
        }

        return null;
    }
    private static string? RefusesDimension(int[] dimensions, int dimension) {
        var cellCount = dimensions.Length;

        try {
            _ = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: dimensions,
                incidences: [],
                material: default
            );
        } catch (ArgumentOutOfRangeException refusal) {
            if ("dimensions" != refusal.ParamName) {
                return $"a cell at dimension {dimension} in a complex of {cellCount} cell(s) was refused naming '{refusal.ParamName}' rather than the dimensions it arrived in";
            }

            // The refusal has to say WHICH label and WHAT it exceeded, or a caller holding a long list learns only that
            // one of them is too large.
            if (refusal.ActualValue is not int actual) {
                return $"a cell at dimension {dimension} in a complex of {cellCount} cell(s) was refused without carrying the offending dimension as its actual value";
            }

            if (dimension != actual) {
                return $"a cell at dimension {dimension} in a complex of {cellCount} cell(s) was refused naming dimension {actual} instead";
            }

            return (refusal.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: $"{(cellCount - 1)}"
            )
                ? null
                : $"a cell at dimension {dimension} in a complex of {cellCount} cell(s) was refused without naming the top of {(cellCount - 1)} that it exceeded"
            );
        }

        return $"a complex of {cellCount} cell(s) admitted a cell at dimension {dimension}, where the degrees the cells reach end at {(cellCount - 1)}";
    }
    // Möbius inversion alternates in sign, so an unsigned material carries no mu at all — and the refusal names the
    // inversion rather than the negation it would otherwise reach first.
    private static string? RefusesInversion() {
        var poset = IncidenceAlgebra<BigInteger, CountingMaterial>.Create(
            elementCount: 2,
            material: default,
            relations: [(0, 1)]
        );

        try {
            _ = poset.TryMobius(
                mobius: out _,
                obstruction: out _
            );
        } catch (InvalidOperationException) {
            return null;
        }

        return "Möbius inversion over an unsigned material was admitted; a statement with no value is refused rather than approximated";
    }

}
