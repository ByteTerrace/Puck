using System.Runtime.InteropServices;

namespace Puck.Maths;

/// <summary>
/// The presentation catalogue — instance data and only data. Every entry here builds a
/// <see cref="ChargedPresentation{TValue, TOps}"/> value; not one of them contributes a line of product code, which is
/// the whole claim the presented algebra makes.
/// </summary>
public static class Presentations {
    // The normal forms a finite basis of this library holds. A shuffle presentation's basis IS its word set, so this is
    // the cap every one of that entry's arguments is measured against rather than a second policy.
    private const int MaximumShuffleWords = 512;

    // The last boundary width whose planar diagrams fit a finite basis: the even-sum Catalan sum reads 377 at six and
    // 1182 at seven, against the 512 normal forms this library holds. It is derived, not chosen.
    private const int MaximumTangleWidth = 6;

    // The colour list every single-object presentation shares. A presentation's boundaries are data, but a
    // one-object presentation has exactly one of them, so it need not be rebuilt per generator.
    private static readonly ReadOnlyMemory<int> SingleColour = new int[] { 0 };

    /// <summary>
    /// Builds the Cayley–Dickson tower at a given number of floors: the twisted group algebra of <c>(ℤ/2)^floors</c>
    /// whose 2-cochain is computed by the doubling recursion rather than tabulated. Floor 2 is the quaternions, floor 3
    /// the octonions, floor 4 the sedenions, and the ladder continues.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material; it must be signed, since the cochain takes both signs.</typeparam>
    /// <param name="floors">The number of doublings, at most five.</param>
    /// <param name="basisRelabelling">A permutation of the <c>2^floors</c> basis indices sending a generator symbol to
    /// the tower coordinate it names, or empty for the identity. It is the one datum that lines this basis up with a
    /// nested doubling tower's coordinate order, and it is presentation data precisely so that it is measured once
    /// rather than hidden in code.</param>
    /// <param name="material">The material.</param>
    /// <param name="liveAssociator">Whether to declare the tower's associator 3-cochain as re-association rule data. It
    /// is computed from the same doubling recursion the product's 2-cochain comes from, as the four signs
    /// <c>σ(b,c)·σ(a,b⊕c)·σ(a,b)·σ(a⊕b,c)</c>, so a bracketing is charged during normalization instead of silently
    /// flattened. It changes no cell and no product: the compiled table of a floor is the same table either way, and
    /// what changes is what a bracketed <see cref="Term"/> normalizes to.</param>
    /// <returns>The presentation. Its generators are the basis elements themselves and its rules are the ordered
    /// products of pairs of them, so the product is a 2-cochain rather than a word rewriting — which is exactly why the
    /// non-associative floors work without re-associating anything.</returns>
    /// <exception cref="ArgumentException">The material is not signed, or the relabelling is not a permutation.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="floors"/> is negative or above five, or the
    /// relabelling is neither empty nor one entry per basis index.</exception>
    public static ChargedPresentation<TValue, TOps> CayleyDickson<TValue, TOps>(int floors, ReadOnlySpan<int> basisRelabelling, TOps material, bool liveAssociator = false)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentOutOfRangeException.ThrowIfNegative(value: floors);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: floors, other: 5);

        var dimension = (1 << floors);

        if ((0 != basisRelabelling.Length) && (basisRelabelling.Length != dimension)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(basisRelabelling), message: "The relabelling must be empty or carry one entry per basis index.");
        }

        var forward = new int[dimension];
        var inverse = new int[dimension];
        var negativeOne = NegativeOne<TValue, TOps>(material: material);
        var one = material.One;

        for (var index = 0; (index < dimension); ++index) {
            forward[index] = ((0 == basisRelabelling.Length) ? index : basisRelabelling[index]);
            inverse[index] = -1;
        }

        for (var index = 0; (index < dimension); ++index) {
            var image = forward[index];

            if ((image < 0) || (image >= dimension) || (-1 != inverse[image])) {
                throw new ArgumentException(message: "The basis relabelling must be a permutation of the basis indices.", paramName: nameof(basisRelabelling));
            }

            inverse[image] = index;
        }

        var generators = SingleColourGenerators(count: dimension);
        var rules = new List<RewriteRule<TValue>> {
            (liveAssociator
                ? CayleyDicksonAssociatorRule(forward: forward, floors: floors, one: one, negativeOne: negativeOne)
                : ReassociationRule(charge: one)),
            new(
                kind: RuleKind.Reduce,
                pattern: ReadOnlyMemory<int>.Empty,
                replacement: RewriteRule<TValue>.PackReplacement(terms: [[inverse[0]]]),
                charges: new[] { one }
            ),
        };

        for (var left = 0; (left < dimension); ++left) {
            for (var right = 0; (right < dimension); ++right) {
                var sign = CayleyDicksonSign(left: forward[left], right: forward[right], floors: floors);

                rules.Add(item: new(
                    kind: RuleKind.Reduce,
                    pattern: new[] { left, right },
                    replacement: RewriteRule<TValue>.PackReplacement(terms: [[inverse[forward[left] ^ forward[right]]]]),
                    charges: new[] { ((sign > 0) ? one : negativeOne) }
                ));
            }
        }

        return ChargedPresentation<TValue, TOps>.Create(
            generators: generators,
            rules: CollectionsMarshal.AsSpan(list: rules),
            material: material
        );
    }

    /// <summary>
    /// Builds the Clifford presentation of signature <c>(p, q, r)</c>: one generator per basis vector, a swap charge of
    /// minus one, and a reduction sending each generator's square to its signature value — with a degenerate
    /// generator's square annihilating outright, which is the charge-zero mechanism rather than a second kind of rule.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material; it must be signed, since the swap charge is minus one.</typeparam>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of generators squaring to <c>0</c>.</param>
    /// <param name="material">The material.</param>
    /// <returns>The presentation, whose normal forms are the ascending generator subsets — so a key is the index of its
    /// subset in ascending-length, then lexicographic, order.</returns>
    /// <exception cref="ArgumentException">The material is not signed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative, or the total exceeds nine generators.</exception>
    public static ChargedPresentation<TValue, TOps> Clifford<TValue, TOps>(int positiveCount, int negativeCount, int degenerateCount, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentOutOfRangeException.ThrowIfNegative(value: positiveCount);
        ArgumentOutOfRangeException.ThrowIfNegative(value: negativeCount);
        ArgumentOutOfRangeException.ThrowIfNegative(value: degenerateCount);

        var generatorCount = ((positiveCount + negativeCount) + degenerateCount);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: generatorCount, other: 9);

        var negativeOne = NegativeOne<TValue, TOps>(material: material);
        var one = material.One;
        var generators = SingleColourGenerators(count: generatorCount);
        var rules = new List<RewriteRule<TValue>> { ReassociationRule(charge: one) };

        for (var symbol = 0; (symbol < generatorCount); ++symbol) {
            var square = ((symbol < positiveCount)
                ? 1
                : ((symbol < (positiveCount + negativeCount)) ? -1 : 0));

            rules.Add(item: ((0 == square)
                ? new(
                    kind: RuleKind.Annihilate,
                    pattern: new[] { symbol, symbol },
                    replacement: ReadOnlyMemory<int>.Empty,
                    charges: ReadOnlyMemory<TValue>.Empty
                )
                : new(
                    kind: RuleKind.Reduce,
                    pattern: new[] { symbol, symbol },
                    replacement: RewriteRule<TValue>.PackReplacement(terms: [[]]),
                    charges: new[] { ((square > 0) ? one : negativeOne) }
                )));
        }

        AppendSwapRules(rules: rules, count: generatorCount, charge: negativeOne);

        return ChargedPresentation<TValue, TOps>.Create(
            generators: generators,
            rules: CollectionsMarshal.AsSpan(list: rules),
            material: material
        );
    }

    /// <summary>
    /// Builds a Coxeter presentation: one involutive generator per mirror, and one braid relation per pair of them,
    /// read off a bond matrix. It is the reflection regime — generators that square to the unit and pairs that satisfy
    /// <c>(s·t)^m = 1</c> — and its brackets stay uniform, because a Coxeter group associates.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material; every charge here is one, so any material serves.</typeparam>
    /// <param name="rank">The number of generators, from one through thirty-two.</param>
    /// <param name="bonds">The row-major bond matrix: one on the diagonal, and off it either a bond of two or more —
    /// the order of the pair's product — or zero, which declares no relation between the pair at all. The matrix must be
    /// symmetric.</param>
    /// <param name="material">The material.</param>
    /// <returns>The presentation. Its normal forms are the words no relation shortens or lowers, which for a rank-two
    /// bond of <c>m</c> are exactly the <c>2m</c> elements of that dihedral group.</returns>
    /// <exception cref="ArgumentException">The bond matrix is not symmetric, its diagonal is not one, or an off-diagonal
    /// bond is one.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rank"/> is outside one through thirty-two, the bond
    /// matrix is not <paramref name="rank"/> squared entries, or a bond is negative or above sixty-four.</exception>
    /// <remarks>
    /// <para>
    /// <b>Every rule decreases in the presentation's own well-founded order</b>, so normalization is bounded and needs
    /// no separate termination argument: an involution shortens, and a braid relation keeps the length while lowering
    /// the leading symbol, which is exactly what a <see cref="RuleKind.Swap"/> rule is. A bond of two is the ordinary
    /// swap rule, so the commuting case is not a special case.
    /// </para>
    /// <para>
    /// <b>What this system decides, and what it does not.</b> Involution plus braid is a complete rewriting system at
    /// every diagram whose connected pieces have rank at most two: there the irreducible words are exactly the group's
    /// elements and <see cref="ChargedPresentation{TValue, TOps}.NormalFormCount"/> is its order, one dihedral factor at
    /// a time and their product together. It is not complete once a piece reaches rank three: the alternating word of a
    /// rank-three Coxeter element repeats forever without ever exposing a redex, so the irreducible language is
    /// infinite, the presentation reports no finite basis, and every certificate it can offer says
    /// <see cref="ClosureOutcome.SearchLimitReached"/>. That is the word
    /// problem showing through, and it is refused rather than answered: completing the system is bounded completion,
    /// which this entry does not do. What still holds there is the group regime — every generator is its own inverse,
    /// certified in one product each by <see cref="PresentedGroup{TValue, TOps}"/> — and the action, which
    /// <see cref="ReflectionSystem"/> measures directly.
    /// </para>
    /// <para>
    /// A bond of zero is the infinite bond of a free product, so the pair generates an infinite dihedral group and the
    /// presentation has no finite basis by construction. It is admitted because refusing it would shrink an attempt
    /// rather than a guarantee.
    /// </para>
    /// <para>
    /// <b>Both caps are derived from what a rule can say.</b> The rank cap of 32 is the rank at which the braid rules
    /// alone — one per unordered pair — already outnumber the word cap this library rewrites under, so a diagram
    /// wider than that presents rules the normalizer cannot run to completion in any case; it is also twice the
    /// largest reflection diagram the lattice bridge can produce. The bond cap of 64 is the alternating word a braid
    /// rule writes out: a bond of <c>m</c> emits a pattern and a replacement of <c>m</c> letters each, so the cap keeps
    /// every rule inside a quarter of the 256-symbol word bound with the pair's own powers still expressible.
    /// </para>
    /// </remarks>
    public static ChargedPresentation<TValue, TOps> Coxeter<TValue, TOps>(int rank, ReadOnlySpan<int> bonds, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: rank, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: rank, other: 32);

        if (bonds.Length != (rank * rank)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(bonds), actualValue: bonds.Length, message: "A bond matrix carries one entry per ordered pair of generators.");
        }

        for (var high = 0; (high < rank); ++high) {
            for (var low = 0; (low < rank); ++low) {
                var bond = bonds[((high * rank) + low)];

                ArgumentOutOfRangeException.ThrowIfNegative(value: bond, paramName: nameof(bonds));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value: bond, other: 64, paramName: nameof(bonds));

                if (bond != bonds[((low * rank) + high)]) {
                    throw new ArgumentException(message: "A bond matrix is symmetric, since the order of a product of two reflections does not depend on which is written first.", paramName: nameof(bonds));
                }

                if ((high == low) != (1 == bond)) {
                    throw new ArgumentException(message: "A generator bonds with itself at one and with every other generator at zero or at two or more, since a bond of one would identify the two.", paramName: nameof(bonds));
                }
            }
        }

        var one = material.One;
        var rules = new List<RewriteRule<TValue>> { ReassociationRule(charge: one) };

        for (var symbol = 0; (symbol < rank); ++symbol) {
            rules.Add(item: new(
                kind: RuleKind.Reduce,
                pattern: new[] { symbol, symbol },
                replacement: RewriteRule<TValue>.PackReplacement(terms: [[]]),
                charges: new[] { one }
            ));
        }

        for (var high = 1; (high < rank); ++high) {
            for (var low = 0; (low < high); ++low) {
                var bond = bonds[((high * rank) + low)];

                if (0 != bond) { AppendBraidRule(rules: rules, high: high, low: low, bond: bond, charge: one); }
            }
        }

        return ChargedPresentation<TValue, TOps>.Create(
            generators: SingleColourGenerators(count: rank),
            rules: CollectionsMarshal.AsSpan(list: rules),
            material: material
        );
    }

    /// <summary>
    /// Builds the free commutative monoid on a set of pairwise coprime generators — the primes — windowed to the
    /// integers <c>[1, window]</c>. Its normal forms are those integers, its product is Dirichlet convolution, and the
    /// total-degree grading it carries is <c>Ω</c>, the count of prime factors with multiplicity.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material.</typeparam>
    /// <param name="primes">The generating primes, in any order; each must be prime, and no two may repeat.</param>
    /// <param name="window">The inclusive integer bound the window keeps.</param>
    /// <param name="material">The material.</param>
    /// <returns>The presentation, whose normal forms are the <paramref name="primes"/>-smooth integers in
    /// <c>[1, window]</c> — one per key, in the canonical ascending-length then lexicographic order rather than in
    /// integer order.</returns>
    /// <exception cref="ArgumentException">A generator is not prime, or two generators repeat.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is below one, more than 128 generators
    /// were given, or the window admits more than 512 integers, which no finite basis of this library holds.</exception>
    /// <remarks>
    /// <para>
    /// <b>Primality is tested, not assumed.</b> Pairwise coprimality is not enough: <c>{4, 9}</c> is coprime and builds
    /// a perfectly good free commutative monoid, but its normal forms are the <c>{4, 9}</c>-products rather than the
    /// integers they are named for, so <c>μ</c> would report minus one at four and plus one at thirty-six against the
    /// classical zero. The generators are therefore decided exactly by <see cref="PrimeField64.IsPrime(ulong)"/>, and
    /// a composite is refused at construction rather than answering arithmetic about a different monoid.
    /// </para>
    /// <para>
    /// <b>The window is the local-finiteness certificate.</b> Every generator has degree one, so a word's degree is
    /// <c>Ω</c> of the integer it names, and the presentation's degree window caps that at the largest power of the
    /// smallest prime the bound admits. Grading alone would leave infinitely many integers at each degree; the value
    /// bound is what makes each graded piece finite, and it is carried as annihilation rules over the minimal words
    /// whose product leaves the window — the same charge-zero mechanism a degenerate Clifford generator uses.
    /// </para>
    /// <para>
    /// <b>Generator symbols run descending by prime.</b> The well-founded order sorts a word ascending by symbol, so a
    /// normal form reads its largest prime first, and a minimal out-of-window word therefore begins with its largest
    /// prime. That places the bulk of the annihilation rules in the buckets of the rare large primes instead of in the
    /// bucket of two, which is worth roughly thirty times the normalization work at a window of a hundred.
    /// </para>
    /// <para>
    /// The window is closed downwards under divisibility, so a convolution's value at an integer inside it reads only
    /// coefficients that are also inside it: truncation moves no value that survives. That is why Möbius inversion,
    /// divisor counts and Mertens sums computed here are exact for every integer the window holds, and not merely
    /// approximate.
    /// </para>
    /// </remarks>
    public static ChargedPresentation<TValue, TOps> DivisibilityWindow<TValue, TOps>(ReadOnlySpan<ulong> primes, long window, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: window, other: 1L);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: primes.Length, other: 128);

        var descending = primes.ToArray();

        Array.Sort(array: descending);
        Array.Reverse(array: descending);

        // Distinct primes are coprime by theorem, so the only way this generating set fails pairwise coprimality is a
        // repeat — and on the sorted array a repeat is adjacent.
        for (var index = 0; (index < descending.Length); ++index) {
            if (!PrimeField64.IsPrime(value: descending[index])) {
                throw new ArgumentException(message: "A divisibility generator names a prime, and a composite one would present a monoid whose normal forms are not the integers they are read as.", paramName: nameof(primes));
            }

            if ((0 != index) && (descending[index] == descending[(index - 1)])) {
                throw new ArgumentException(message: "The divisibility generators must be distinct, since a repeated prime is not coprime with itself.", paramName: nameof(primes));
            }
        }

        var generatorCount = descending.Length;
        var one = material.One;
        var generators = SingleColourGenerators(count: generatorCount);
        var rules = new List<RewriteRule<TValue>> { ReassociationRule(charge: one) };

        AppendSwapRules(rules: rules, count: generatorCount, charge: one);

        var windowDegree = 0;

        if (0 != generatorCount) {
            var smallest = ((long)descending[(generatorCount - 1)]);
            var reach = 1L;

            while (reach <= (window / smallest)) {
                reach *= smallest;
                ++windowDegree;
            }
        }

        var admitted = 0;
        var word = new int[(windowDegree + 1)];

        void Extend(int length, long product, int first) {
            if (++admitted > 512) {
                throw new ArgumentOutOfRangeException(paramName: nameof(window), message: "This window admits more than 512 integers, which no finite basis of this library holds.");
            }

            for (var symbol = first; (symbol < generatorCount); ++symbol) {
                var prime = ((long)descending[symbol]);

                word[length] = symbol;

                if (product > (window / prime)) {
                    rules.Add(item: new(
                        kind: RuleKind.Annihilate,
                        pattern: word.AsSpan(start: 0, length: (length + 1)).ToArray(),
                        replacement: ReadOnlyMemory<int>.Empty,
                        charges: ReadOnlyMemory<TValue>.Empty
                    ));

                    continue;
                }

                Extend(length: (length + 1), product: (product * prime), first: symbol);
            }
        }

        Extend(length: 0, product: 1L, first: 0);

        return ChargedPresentation<TValue, TOps>.Create(
            generators: generators,
            rules: CollectionsMarshal.AsSpan(list: rules),
            material: material,
            windowDegree: windowDegree
        );
    }

    /// <summary>Builds the free monoid on a given number of letters — the associative regime, whose only rule is the
    /// re-association charge.</summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material.</typeparam>
    /// <param name="letterCount">The alphabet size, at most sixty-four.</param>
    /// <param name="material">The material.</param>
    /// <param name="windowDegree">A positive word-length bound, which makes the normal-form set finite and the compiled
    /// dense form available; zero leaves the monoid free, whose keys are then the mixed-radix packings of its words.</param>
    /// <returns>The presentation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="letterCount"/> is negative or above sixty-four, or
    /// <paramref name="windowDegree"/> is negative.</exception>
    public static ChargedPresentation<TValue, TOps> FreeMonoid<TValue, TOps>(int letterCount, TOps material, int windowDegree = 0)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentOutOfRangeException.ThrowIfNegative(value: letterCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: letterCount, other: 64);

        return ChargedPresentation<TValue, TOps>.Create(
            generators: SingleColourGenerators(count: letterCount),
            rules: new[] { ReassociationRule(charge: material.One) },
            material: material,
            windowDegree: windowDegree
        );
    }

    /// <summary>
    /// Builds the incidence algebra of a finite partially ordered set: the intervals are the generators, two of them
    /// compose exactly when the first one's upper endpoint is the second one's lower endpoint, and every other ordered
    /// pair annihilates. It is <see cref="Quiver"/>'s shape at a sub-quiver — the poset read as a category — so the
    /// convolution over the factorizations of an interval is the ordinary product and nothing here is a second kernel.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material.</typeparam>
    /// <param name="elementCount">The number of elements, from one through 256.</param>
    /// <param name="relations">Pairs <c>(lower, upper)</c> generating the strict order. They need not be transitively
    /// closed and need not be the covering relation: the entry closes them, so a caller that knows only what covers
    /// what may hand that over.</param>
    /// <param name="material">The material.</param>
    /// <returns>The presentation, whose generators are the intervals in ascending <c>(lower, upper)</c> order — so a
    /// key is that index — and whose unit is the sum of the singleton intervals.</returns>
    /// <exception cref="ArgumentException">A relation leaves the element range, or the relations close into a cycle and
    /// so order nothing.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elementCount"/> is outside one through 256, or the
    /// order has more than 256 intervals.</exception>
    /// <remarks>
    /// <para>
    /// <b>The zeta element is the coefficient one at every interval, and its convolution inverse is the Möbius
    /// function of the order.</b> The strict part carries no interval of chain length zero, so its <c>k</c>-th power
    /// carries none below chain length <c>k</c> and the sum over all lengths terminates at the order's height: the
    /// finiteness of the poset is the closure certificate, exactly as a divisibility window's bound is.
    /// </para>
    /// <para>
    /// <b>The divisibility window is this algebra's quotient, not this algebra.</b>
    /// <see cref="DivisibilityWindow"/> presents the free commutative monoid on a prime set, whose basis is the
    /// integers of the window; the incidence algebra of the same divisibility order has the ordered divisor pairs for
    /// a basis. The two are related by the interval type <c>[a, b] ↦ b / a</c>, under which the window is the reduced
    /// incidence algebra — so <c>μ([a, b])</c> here equals <c>μ(b / a)</c> there, and neither entry is a
    /// specialization of the other.
    /// </para>
    /// <para>
    /// Every relation is refused or closed at construction, and the cycle refusal is the only mathematical statement
    /// enforced: a cycle means the pairs order no set at all, so there are no intervals to name.
    /// </para>
    /// </remarks>
    public static ChargedPresentation<TValue, TOps> IntervalPoset<TValue, TOps>(int elementCount, ReadOnlySpan<(int Lower, int Upper)> relations, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: elementCount, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: elementCount, other: 256);

        var order = new bool[(elementCount * elementCount)];

        foreach (var (lower, upper) in relations) {
            if ((lower < 0) || (lower >= elementCount) || (upper < 0) || (upper >= elementCount)) {
                throw new ArgumentException(message: "A relation leaves the element range of this poset.", paramName: nameof(relations));
            }

            order[((lower * elementCount) + upper)] = true;
        }

        // The transitive closure of what was declared, so a covering relation and a fully written-out order present the
        // same poset. Composition needs it: an interval's factorizations run through every element between its
        // endpoints, and a missing middle would annihilate a pair that composes.
        for (var middle = 0; (middle < elementCount); ++middle) {
            for (var lower = 0; (lower < elementCount); ++lower) {
                if (!order[((lower * elementCount) + middle)]) { continue; }

                for (var upper = 0; (upper < elementCount); ++upper) {
                    if (order[((middle * elementCount) + upper)]) { order[((lower * elementCount) + upper)] = true; }
                }
            }
        }

        var symbolOf = new int[(elementCount * elementCount)];

        Array.Fill(array: symbolOf, value: -1);

        for (var index = 0; (index < elementCount); ++index) {
            if (order[((index * elementCount) + index)]) {
                throw new ArgumentException(message: "The declared relations close into a cycle, so they order no set and name no intervals.", paramName: nameof(relations));
            }

            // Reflexive from here on: an element's own singleton interval is the identity arrow at it.
            order[((index * elementCount) + index)] = true;
        }

        var lowerOf = new List<int>();
        var upperOf = new List<int>();

        for (var lower = 0; (lower < elementCount); ++lower) {
            for (var upper = 0; (upper < elementCount); ++upper) {
                if (!order[((lower * elementCount) + upper)]) { continue; }

                symbolOf[((lower * elementCount) + upper)] = lowerOf.Count;

                lowerOf.Add(item: lower);
                upperOf.Add(item: upper);
            }
        }

        var generatorCount = lowerOf.Count;

        if (generatorCount > 256) {
            throw new ArgumentOutOfRangeException(paramName: nameof(relations), message: "This order has more than 256 intervals, which no finite basis of this library holds.");
        }

        var one = material.One;
        var colours = new ReadOnlyMemory<int>[elementCount];
        var generators = new Generator[generatorCount];

        for (var index = 0; (index < elementCount); ++index) { colours[index] = new int[] { index }; }

        for (var symbol = 0; (symbol < generatorCount); ++symbol) {
            generators[symbol] = new Generator(symbol: symbol, inputs: colours[lowerOf[symbol]], outputs: colours[upperOf[symbol]], degree: 1);
        }

        var diagonal = new int[elementCount][];
        var diagonalCharges = new TValue[elementCount];

        for (var index = 0; (index < elementCount); ++index) {
            diagonal[index] = [symbolOf[((index * elementCount) + index)]];
            diagonalCharges[index] = one;
        }

        var rules = new List<RewriteRule<TValue>> {
            ReassociationRule(charge: one),
            new(
                kind: RuleKind.Reduce,
                pattern: ReadOnlyMemory<int>.Empty,
                replacement: RewriteRule<TValue>.PackReplacement(terms: diagonal),
                charges: diagonalCharges
            ),
        };

        // An interval's boundaries ARE its endpoints, so the shared derivation composes exactly the pairs whose middle
        // endpoints agree. Transitivity is what makes the composite an interval: the closure above guarantees the target
        // exists wherever the boundaries meet, so no composable pair falls through to annihilation.
        AppendBoundaryCompositionRules(
            rules: rules,
            generators: generators,
            composite: (left, right) => (symbolOf[((lowerOf[left] * elementCount) + upperOf[right])], one)
        );

        return ChargedPresentation<TValue, TOps>.Create(
            generators: generators,
            rules: CollectionsMarshal.AsSpan(list: rules),
            material: material
        );
    }

    /// <summary>
    /// Builds the monogenic presentation: one generator with a monic reduction <c>xⁿ → −Σ m_j·x^j</c>. Degree two is
    /// the quadratic algebra — the tail <c>[m₀, m₁]</c> being the relation <c>(P, Q) = (−m₁, −m₀)</c> — and over the
    /// parity material a degree-<c>k</c> tail is the binary field of that degree, modulus and all.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material; it must be signed, since the reduction negates the tail.</typeparam>
    /// <param name="modulus">The modulus tail <c>[m₀, m₁, …, m_{n−1}]</c>, low exponent first; the leading <c>xⁿ</c> is
    /// implicit.</param>
    /// <param name="material">The material.</param>
    /// <returns>The presentation, whose normal forms are the powers below <c>n</c>, so a key is its exponent.</returns>
    /// <exception cref="ArgumentException">The tail is empty, or the material is not signed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The tail is longer than 512 coefficients.</exception>
    public static ChargedPresentation<TValue, TOps> Monogenic<TValue, TOps>(ReadOnlySpan<TValue> modulus, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        if (0 == modulus.Length) {
            throw new ArgumentException(message: "A monic modulus carries at least one tail coefficient.", paramName: nameof(modulus));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: modulus.Length, other: 512);

        if (material is not ISignedMaterial<TValue, TOps> signed) {
            throw new ArgumentException(message: "A monic reduction negates its tail, which an unsigned material cannot express.", paramName: nameof(material));
        }

        var degree = modulus.Length;
        var charges = new TValue[degree];
        var pattern = new int[degree];
        var terms = new int[degree][];

        for (var exponent = 0; (exponent < degree); ++exponent) {
            charges[exponent] = signed.Negate(value: modulus[exponent]);
            terms[exponent] = new int[exponent];
        }

        return ChargedPresentation<TValue, TOps>.Create(
            generators: new[] { new Generator(symbol: 0, inputs: SingleColour, outputs: SingleColour, degree: 1) },
            rules: new[] {
                ReassociationRule(charge: material.One),
                new RewriteRule<TValue>(
                    kind: RuleKind.Reduce,
                    pattern: pattern,
                    replacement: RewriteRule<TValue>.PackReplacement(terms: terms),
                    charges: charges
                ),
            },
            material: material
        );
    }

    /// <summary>
    /// Builds the group algebra of a permutation group: the generators are the group's elements and the rules are its
    /// composition table, so a finite group enters the presented algebra the same way a Cayley-Dickson floor does —
    /// as a basis with an ordered product, not as a word rewriting.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material; every charge here is one, so any material serves.</typeparam>
    /// <param name="pointCount">The number of points the group permutes, from one through 512.</param>
    /// <param name="permutations">The elements as a row-major table of point images, one row of
    /// <paramref name="pointCount"/> entries per element, in any order. Composition follows word order:
    /// <c>(i·j)</c> sends a point through <c>i</c> and then through <c>j</c>.</param>
    /// <param name="material">The material.</param>
    /// <returns>The presentation, whose normal forms are the group's elements — so
    /// <see cref="ChargedPresentation{TValue, TOps}.NormalFormCount"/> is the group's order — keyed by ascending
    /// lexicographic order of their point images, which puts the identity at key zero.</returns>
    /// <exception cref="ArgumentException">A row is not a permutation of the points, two rows repeat, the identity is
    /// missing, or the table is not closed under composition.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pointCount"/> is outside one through 512, the table
    /// is not a whole number of rows, or it carries no rows or more than 256.</exception>
    /// <remarks>
    /// <para>
    /// <b>A permutation table is self-proving, which is why it is the input.</b> Composition of permutations associates
    /// by construction, so the only facts left to enforce are that each row is a permutation, that the identity is
    /// present, and that the set is closed — all three decided exactly, at construction, by a binary search over the
    /// lexicographically sorted table. An abstract multiplication table would need its associativity checked over every
    /// triple; this one carries the proof in its shape.
    /// </para>
    /// <para>
    /// <b>The caps are the boundary, and both are derived.</b> A group of more than 256 elements is refused, because
    /// its basis is its element set and the compiled table is that squared. So the reflection groups a bounded
    /// enumeration can reach are presentable here and the ones it cannot are not, which is the same limit stated twice
    /// rather than a second policy: <see cref="ReflectionSystem.TryEnumerateGroup"/> refuses to enumerate the whole
    /// lattice symmetry long before this entry would refuse to present it. The point cap of 512 is the other side of
    /// the table: the input is one row of point images per element, so admitting a point count above the element cap's
    /// own reach only widens rows that name the same group. It is set at twice the element cap so that a faithful
    /// action on more points than the group has elements — which is what a reflection world hands over, the lattice's
    /// sub-root system being larger than the small groups acting on it — is presentable rather than refused for the
    /// shape of its action.
    /// </para>
    /// </remarks>
    public static ChargedPresentation<TValue, TOps> PermutationGroup<TValue, TOps>(int pointCount, ReadOnlySpan<int> permutations, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: pointCount, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: pointCount, other: 512);

        if (0 != (permutations.Length % pointCount)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(permutations), actualValue: permutations.Length, message: "A permutation table carries one row of point images per element.");
        }

        var elementCount = (permutations.Length / pointCount);

        ArgumentOutOfRangeException.ThrowIfLessThan(value: elementCount, other: 1, paramName: nameof(permutations));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: elementCount, other: 256, paramName: nameof(permutations));

        var given = permutations.ToArray();
        var reached = new bool[pointCount];

        for (var element = 0; (element < elementCount); ++element) {
            Array.Clear(array: reached);

            for (var point = 0; (point < pointCount); ++point) {
                var image = given[((element * pointCount) + point)];

                if ((image < 0) || (image >= pointCount) || reached[image]) {
                    throw new ArgumentException(message: "Every row of a permutation table is a permutation of the points.", paramName: nameof(permutations));
                }

                reached[image] = true;
            }
        }

        var order = new int[elementCount];

        for (var element = 0; (element < elementCount); ++element) { order[element] = element; }

        Array.Sort(array: order, comparison: (left, right) => given.AsSpan(start: (left * pointCount), length: pointCount).SequenceCompareTo(other: given.AsSpan(start: (right * pointCount), length: pointCount)));

        var table = new int[given.Length];

        for (var element = 0; (element < elementCount); ++element) {
            given.AsSpan(start: (order[element] * pointCount), length: pointCount).CopyTo(destination: table.AsSpan(start: (element * pointCount), length: pointCount));

            if ((0 != element) && table.AsSpan(start: ((element - 1) * pointCount), length: pointCount).SequenceEqual(other: table.AsSpan(start: (element * pointCount), length: pointCount))) {
                throw new ArgumentException(message: "A permutation table names each element once, and a repeated row would name two generators for one element.", paramName: nameof(permutations));
            }
        }

        // The identity is the lexicographically smallest permutation, so a table that holds it holds it first.
        for (var point = 0; (point < pointCount); ++point) {
            if (table[point] != point) {
                throw new ArgumentException(message: "A group contains the identity, and this permutation table does not.", paramName: nameof(permutations));
            }
        }

        var one = material.One;
        var rules = new List<RewriteRule<TValue>> {
            ReassociationRule(charge: one),
            new(
                kind: RuleKind.Reduce,
                pattern: ReadOnlyMemory<int>.Empty,
                replacement: RewriteRule<TValue>.PackReplacement(terms: [[0]]),
                charges: new[] { one }
            ),
        };

        var composite = new int[pointCount];

        for (var left = 0; (left < elementCount); ++left) {
            for (var right = 0; (right < elementCount); ++right) {
                for (var point = 0; (point < pointCount); ++point) {
                    composite[point] = table[((right * pointCount) + table[((left * pointCount) + point)])];
                }

                var target = IndexOfRow(table: table, row: composite, pointCount: pointCount, elementCount: elementCount);

                if (target < 0) {
                    throw new ArgumentException(message: "A permutation table is closed under composition, and this one leaves itself, so it names no group.", paramName: nameof(permutations));
                }

                rules.Add(item: new(
                    kind: RuleKind.Reduce,
                    pattern: new[] { left, right },
                    replacement: RewriteRule<TValue>.PackReplacement(terms: [[target]]),
                    charges: new[] { one }
                ));
            }
        }

        return ChargedPresentation<TValue, TOps>.Create(
            generators: SingleColourGenerators(count: elementCount),
            rules: CollectionsMarshal.AsSpan(list: rules),
            material: material
        );
    }

    /// <summary>
    /// Builds the planar tangle algebra at a bounded boundary width: the generators are the planar diagrams and the
    /// rules are their composition table, so a generator whose output boundary is wider or narrower than its input
    /// boundary — a cup, a cap — is an ordinary basis element and co-arity greater than one costs the kernel nothing.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material; every charge here is a power of the loop charge, so any material serves.</typeparam>
    /// <param name="maximumWidth">The largest boundary width admitted, from zero through six.</param>
    /// <param name="loopCharge">The charge one closed loop carries. Composing two diagrams strands off zero or more
    /// closed loops, and the composite's charge is that many loop charges multiplied together — the same datum a
    /// Clifford generator's square is, read at a different pair.</param>
    /// <param name="material">The material.</param>
    /// <returns>The presentation, whose normal forms are the diagrams themselves — so a key is the diagram's index in
    /// the canonical order — and whose unit is the sum of the identity diagrams, one per width.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumWidth"/> is negative or above six.</exception>
    /// <remarks>
    /// <para>
    /// <b>A diagram is a non-crossing perfect matching of its boundary points, and that matching is its canonical
    /// form.</b> The <c>k</c> input points and <c>m</c> output points are read once around the boundary — the inputs in
    /// order, then the outputs in reverse — so a planar <c>(k, m)</c> diagram is a non-crossing perfect matching of
    /// <c>k + m</c> points on a circle. It exists only when <c>k + m</c> is even, planarity makes the matching unique
    /// rather than one representative of an isotopy class, and one stack scan over the boundary in balanced-parenthesis
    /// order — an opener where a point starts its arc, a closer where it ends one — packs it to a key in time linear in
    /// the boundary. Nothing is canonicalized up to isotopy and nothing searches.
    /// </para>
    /// <para>
    /// <b>Composition is arc tracing, which is why the table is self-proving.</b> The left diagram's outputs are glued
    /// to the right diagram's inputs; walking a free boundary point along the two matchings and the glue reaches another
    /// free point, and those walks are the composite's arcs, while whatever the walks never reach is a closed loop. The
    /// walk is linear in the glued boundary and it associates by construction, so no associativity check is owed — the
    /// <see cref="PermutationGroup"/> argument, at a category rather than a group.
    /// </para>
    /// <para>
    /// <b>The width cap is derived, not chosen.</b> The basis is every planar <c>(k, m)</c> diagram with both widths at
    /// most <paramref name="maximumWidth"/>, so its size is the sum of <c>C((k + m) / 2)</c> over the even-sum pairs,
    /// which reads 6, 15, 43, 123 and 377 at widths two through six and 1182 at width seven. A finite basis of this
    /// library holds 512 normal forms, so width six is the last width that has one and width seven names an object with
    /// no finite basis at all — every certificate, the compiled table, the guarded star and the tensor would refuse
    /// there, as they do at every other basis-less presentation. Width seven is therefore refused at construction
    /// rather than admitted and then found unusable.
    /// </para>
    /// <para>
    /// <b>The practical budget is tighter than the cap.</b> Construction is keys-squared interpreted normalizations and
    /// certification is keys-cubed, so width four (43 diagrams) is the width a fast suite carries. The wider sweeps
    /// belong to the deep tier, where <c>deep.presented-tangle-sweep</c> walks every width through six against the
    /// transcribed basis totals.
    /// </para>
    /// <para>
    /// The unit is the sum of the identity diagrams, one per width, which is the <see cref="Quiver"/> diagonal stated at
    /// a category whose objects are the widths: each identity is idempotent, distinct widths annihilate, and the empty
    /// word rewrites to their sum through the same empty-pattern rule a quiver already uses.
    /// </para>
    /// </remarks>
    public static ChargedPresentation<TValue, TOps> PlanarTangle<TValue, TOps>(int maximumWidth, TValue loopCharge, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentOutOfRangeException.ThrowIfNegative(value: maximumWidth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: maximumWidth, other: MaximumTangleWidth, paramName: nameof(maximumWidth));

        var basis = new PlanarBasis(maximumWidth: maximumWidth);
        var count = basis.Count;
        var colours = new ReadOnlyMemory<int>[(maximumWidth + 1)];
        var generators = new Generator[count];

        // One colour, repeated once per wire: a tangle is single-coloured, so what its boundaries carry is their LENGTH,
        // which is exactly the half of the shared composability test a quiver never exercises.
        for (var width = 0; (width <= maximumWidth); ++width) { colours[width] = new int[width]; }

        for (var symbol = 0; (symbol < count); ++symbol) {
            generators[symbol] = new Generator(symbol: symbol, inputs: colours[basis.InputWidth(symbol: symbol)], outputs: colours[basis.OutputWidth(symbol: symbol)], degree: 1);
        }

        // A loop closes over at least two glued wires, so a composition over a boundary of w wires strands off at most
        // w / 2 of them; the powers are formed once here rather than per ordered pair.
        var one = material.One;
        var loopPowers = new TValue[((maximumWidth >> 1) + 1)];

        loopPowers[0] = one;

        for (var index = 1; (index < loopPowers.Length); ++index) { loopPowers[index] = material.Multiply(left: loopPowers[(index - 1)], right: loopCharge); }

        var identityCharges = new TValue[(maximumWidth + 1)];
        var identityTerms = new int[(maximumWidth + 1)][];

        for (var width = 0; (width <= maximumWidth); ++width) {
            identityCharges[width] = one;
            identityTerms[width] = [basis.IdentitySymbol(width: width)];
        }

        var rules = new List<RewriteRule<TValue>> {
            ReassociationRule(charge: one),
            new(
                kind: RuleKind.Reduce,
                pattern: ReadOnlyMemory<int>.Empty,
                replacement: RewriteRule<TValue>.PackReplacement(terms: identityTerms),
                charges: identityCharges
            ),
        };

        AppendBoundaryCompositionRules(
            rules: rules,
            generators: generators,
            composite: (left, right) => {
                var target = basis.Compose(left: left, right: right, loops: out var loops);

                return (target, loopPowers[loops]);
            }
        );

        return ChargedPresentation<TValue, TOps>.Create(
            generators: generators,
            rules: CollectionsMarshal.AsSpan(list: rules),
            material: material
        );
    }

    /// <summary>
    /// Builds a quiver on a given number of objects: the arrows are the generators, composition is by endpoint match,
    /// and a mismatch annihilates. The codiscrete case — every ordered pair present — is the matrix algebra, so a
    /// transition matrix is an element of this algebra and stepping it is the ordinary product.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material.</typeparam>
    /// <param name="objectCount">The number of objects, from one through sixteen.</param>
    /// <param name="arrows">The weighted arrows. An arrow's weight becomes the charge its generator's basis element
    /// carries, so summing every generator builds the weighted adjacency element; a pair with no arrow keeps the
    /// material's zero, which is <c>false</c>, <c>0</c>, and the tropical <c>+∞</c> respectively — the right "no edge"
    /// value at each material without a special case. Repeated arrows add.</param>
    /// <param name="material">The material.</param>
    /// <returns>The presentation, whose generator and key for the pair <c>(i, j)</c> is <c>i·objectCount + j</c> and
    /// whose unit is the diagonal sum.</returns>
    /// <exception cref="ArgumentException">An arrow leaves the object range.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="objectCount"/> is outside one through sixteen.</exception>
    public static ChargedPresentation<TValue, TOps> Quiver<TValue, TOps>(int objectCount, ReadOnlySpan<(int Source, int Target, TValue Weight)> arrows, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: objectCount, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: objectCount, other: 16);

        var generatorCount = (objectCount * objectCount);
        var one = material.One;
        var colours = new ReadOnlyMemory<int>[objectCount];
        var generators = new Generator[generatorCount];
        var charges = new TValue[generatorCount];

        for (var index = 0; (index < objectCount); ++index) { colours[index] = new int[] { index }; }

        for (var source = 0; (source < objectCount); ++source) {
            for (var destination = 0; (destination < objectCount); ++destination) {
                var symbol = ((source * objectCount) + destination);

                charges[symbol] = material.Zero;
                generators[symbol] = new Generator(symbol: symbol, inputs: colours[source], outputs: colours[destination], degree: 1);
            }
        }

        foreach (var arrow in arrows) {
            if ((arrow.Source < 0) || (arrow.Source >= objectCount) || (arrow.Target < 0) || (arrow.Target >= objectCount)) {
                throw new ArgumentException(message: "An arrow leaves the object range of this quiver.", paramName: nameof(arrows));
            }

            var symbol = ((arrow.Source * objectCount) + arrow.Target);

            charges[symbol] = material.Add(left: charges[symbol], right: arrow.Weight);
        }

        var diagonal = new int[objectCount][];
        var diagonalCharges = new TValue[objectCount];

        for (var index = 0; (index < objectCount); ++index) {
            diagonal[index] = [((index * objectCount) + index)];
            diagonalCharges[index] = one;
        }

        var rules = new List<RewriteRule<TValue>> {
            ReassociationRule(charge: one),
            new(
                kind: RuleKind.Reduce,
                pattern: ReadOnlyMemory<int>.Empty,
                replacement: RewriteRule<TValue>.PackReplacement(terms: diagonal),
                charges: diagonalCharges
            ),
        };

        // An arrow's boundaries ARE its endpoints, so the shared derivation composes exactly the pairs that meet at one
        // object; the composite keeps the first arrow's source and the second one's target.
        AppendBoundaryCompositionRules(
            rules: rules,
            generators: generators,
            composite: (left, right) => ((((left / objectCount) * objectCount) + (right % objectCount)), one)
        );

        return ChargedPresentation<TValue, TOps>.Create(
            generators: generators,
            rules: CollectionsMarshal.AsSpan(list: rules),
            material: material,
            generatorCharges: charges
        );
    }

    /// <summary>
    /// Builds the shift on jets of bounded degree: one generator <c>x</c> with <c>x^(degreeBound+1) → 0</c>, so an
    /// element is a truncated sequence and multiplying by <c>x</c> delays it by one place.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material; it must be signed, since it is a monic reduction.</typeparam>
    /// <param name="degreeBound">The highest surviving degree; the jet holds <c>degreeBound + 1</c> places.</param>
    /// <param name="material">The material.</param>
    /// <returns>The presentation, whose normal forms are the powers of <c>x</c> below <c>degreeBound + 1</c>, so a key
    /// is its degree.</returns>
    /// <exception cref="ArgumentException">The material is not signed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeBound"/> is negative or above 511.</exception>
    /// <remarks>
    /// It is <see cref="Monogenic"/> at an all-zero modulus, named because the finite-calculus reading is what earns it:
    /// the shift is nilpotent, so the sum over all lengths <c>1 + x + x² + …</c> terminates under a computed
    /// <see cref="ClosureCertificate.Nilpotent"/> and is the antidifference — the prefix-sum operator whose inverse is
    /// the backward difference <c>1 − x</c>.
    /// </remarks>
    public static ChargedPresentation<TValue, TOps> Shift<TValue, TOps>(int degreeBound, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentOutOfRangeException.ThrowIfNegative(value: degreeBound);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: degreeBound, other: 511);

        var modulus = new TValue[(degreeBound + 1)];

        Array.Fill(array: modulus, value: material.Zero);

        return Monogenic<TValue, TOps>(modulus: modulus, material: material);
    }

    /// <summary>
    /// Builds the second product on words: the generators are the words of a bounded length and their product is the
    /// charged sum of the distinct interleavings, each carrying the number of ways it is interleaved. An empty
    /// <paramref name="letterProduct"/> is the shuffle exactly; a non-empty one lets two heads collide into one letter
    /// and is the quasi-shuffle. One entry, two products, and the degenerate case is the simpler one.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material; every charge here is a count, so any material serves.</typeparam>
    /// <param name="letterCount">The alphabet size, from zero through 512.</param>
    /// <param name="windowDegree">The longest word admitted, from zero through 511.</param>
    /// <param name="material">The material.</param>
    /// <param name="letterProduct">The row-major table of merged letters, one entry per ordered pair of letters, or
    /// empty for the shuffle. Entry <c>(i, j)</c> is the letter two colliding heads become.</param>
    /// <returns>The presentation, whose normal forms are the words themselves — so a key is the word's index in the
    /// canonical order, ascending by length and then lexicographically — and whose unit is the empty word.</returns>
    /// <exception cref="ArgumentException">A letter product names a letter this alphabet does not hold, named with the
    /// ordered pair that blocked.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="letterCount"/> is outside zero through 512,
    /// <paramref name="windowDegree"/> is outside zero through 511, the two together name more words than a finite basis
    /// holds, or <paramref name="letterProduct"/> is neither empty nor one entry per ordered pair of letters.</exception>
    /// <remarks>
    /// <para>
    /// <b>The generators are words rather than letters</b>, because the shuffle of a letter with itself is twice the
    /// two-letter word: the letters do not span this algebra over the integers, so there is no presentation on them. A
    /// generator's <see cref="Generator.Degree"/> is its word's length, which is the grading both products carry.
    /// </para>
    /// <para>
    /// <b>Cells are emitted from the recursion on the two heads</b> — <c>(xu)·(yv)</c> is
    /// <c>x·(u·yv) + y·(xu·v) + [x·y]·(u·v)</c>, the third term present only under a letter product — read off the cells
    /// of the three shorter pairs, which the emission order guarantees are already built. So each cell is formed once,
    /// from data, and nothing searches.
    /// </para>
    /// <para>
    /// <b>The window truncates by the result's length, and that is the only truncation an algebra admits here.</b> Every
    /// interleaving of two words is at least as long as the longer of them, so the words past the window span a
    /// two-sided ideal and the quotient by it is an algebra — which is what this entry builds, by dropping the over-long
    /// replacement terms of every pair. For the shuffle that is exactly a degree window: every interleaving of two words
    /// is their combined length, so either all of a pair's terms survive or none do, and the pairs whose lengths sum past
    /// the window annihilate outright. The quasi-shuffle is only filtered, not graded — a collision shortens the
    /// result — so a degree window there would annihilate pairs whose merged terms still fit, and the truncated product
    /// would not associate: at one letter and a window of three, <c>(a·a)·a²</c> would reach <c>3a³ + 2a²</c> where
    /// <c>a·(a·a²)</c> reaches <c>6a³ + 4a²</c>. Truncating by the result's length keeps both associative, which
    /// <see cref="PresentedAlgebra{TValue, TOps}.Certify"/> computes rather than this entry asserting it. So the
    /// presentation carries no <see cref="ChargedPresentation{TValue, TOps}.WindowDegree"/> of its own: the truncation
    /// sits in the cells, where a shuffle pair that outgrows the window annihilates and a quasi-shuffle pair keeps
    /// whatever its collisions shortened back into range.
    /// </para>
    /// <para>
    /// <b>Both caps are the 512 normal forms a finite basis holds.</b> The word count is
    /// <c>(k^(L+1) − 1) / (k − 1)</c>, so two letters at a window of eight name 511 words and are admitted while a
    /// window of nine names 1023 and is refused. A letter is a word of length one, so an alphabet above the cap names
    /// letters no basis holds; and at one letter the words are the lengths through the window, so a window above 511
    /// names more than 512 of them. Each is the same cap read at a different argument rather than a second policy.
    /// </para>
    /// <para>
    /// <b>The practical budget is tighter than the cap.</b> Construction is keys-squared, one cell per ordered pair, and
    /// certification is keys-cubed; the largest shuffle cell carries <c>C(L, ⌊L/2⌋)</c> terms and a quasi-shuffle cell
    /// carries more. So two letters at a window of four (31 words) is what a fast suite carries with the composition
    /// table built. The wider widths are reached without it: <c>presented.shuffle-near-cap-basis</c> takes the basis up
    /// to the 512-word cap by reading the generating set alone, which needs no cell per ordered pair.
    /// </para>
    /// <para>
    /// Nothing here requires the letter product to be commutative or associative, and neither is enforced: whether the
    /// resulting algebra is either is a mathematical fact about the declared data, so it is
    /// <see cref="PresentedAlgebra{TValue, TOps}.Certify"/>'s to compute. What is refused is a collision naming a letter
    /// outside the alphabet, because that term names no element of this algebra at all.
    /// </para>
    /// </remarks>
    public static ChargedPresentation<TValue, TOps> Shuffle<TValue, TOps>(int letterCount, int windowDegree, TOps material, ReadOnlySpan<int> letterProduct = default)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentOutOfRangeException.ThrowIfNegative(value: letterCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: letterCount, other: MaximumShuffleWords);
        ArgumentOutOfRangeException.ThrowIfNegative(value: windowDegree);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: windowDegree, other: (MaximumShuffleWords - 1));

        var merges = (0 != letterProduct.Length);

        if (merges) {
            if (letterProduct.Length != (letterCount * letterCount)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(letterProduct), actualValue: letterProduct.Length, message: "A letter product is empty, which is the shuffle, or carries one merged letter per ordered pair of letters, which is the quasi-shuffle.");
            }

            for (var left = 0; (left < letterCount); ++left) {
                for (var right = 0; (right < letterCount); ++right) {
                    var merged = letterProduct[((left * letterCount) + right)];

                    if ((merged < 0) || (merged >= letterCount)) {
                        throw new ArgumentException(message: $"The letters ({left}, {right}) collide into the letter {merged}, which this alphabet does not hold, so that collision term names no element of this algebra.", paramName: nameof(letterProduct));
                    }
                }
            }
        }

        var basis = new ShuffleBasis(letterCount: letterCount, windowDegree: windowDegree);
        var count = basis.Count;
        var one = material.One;
        var generators = new Generator[count];

        for (var symbol = 0; (symbol < count); ++symbol) {
            generators[symbol] = new Generator(symbol: symbol, inputs: SingleColour, outputs: SingleColour, degree: basis.LengthOf(symbol: symbol));
        }

        var rules = new List<RewriteRule<TValue>>(capacity: ((count * count) + 2)) {
            ReassociationRule(charge: one),
            new(
                kind: RuleKind.Reduce,
                pattern: ReadOnlyMemory<int>.Empty,
                replacement: RewriteRule<TValue>.PackReplacement(terms: [[0]]),
                charges: new[] { one }
            ),
        };

        AppendInterleavingRules(rules: rules, basis: basis, letterProduct: letterProduct, material: material);

        return ChargedPresentation<TValue, TOps>.Create(
            generators: generators,
            rules: CollectionsMarshal.AsSpan(list: rules),
            material: material
        );
    }

    /// <summary>
    /// Builds the tensor product of two finite presentations: the Kronecker pair-up, stated as a presentation whose
    /// generators are the pairs of basis elements and whose rules are the pairs of compiled cells.
    /// </summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material.</typeparam>
    /// <param name="left">The left factor.</param>
    /// <param name="right">The right factor.</param>
    /// <returns>The presentation, whose generator and key for the basis pair <c>(i, j)</c> is
    /// <c>i·right.NormalFormCount + j</c>.</returns>
    /// <exception cref="ArgumentNullException">A factor is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A factor has no finite basis, has no unit to pair, or the two factors carry
    /// different materials.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The paired basis exceeds sixty-four generators.</exception>
    /// <remarks>
    /// <para>
    /// It contributes no product code: the cells of the pair are read out of the two factors' own generated cells, so
    /// the tensor is the same kernel at a bigger presentation. That is why the pair-up needs no second multiply and no
    /// second element type.
    /// </para>
    /// <para>
    /// <b>The rounding boundary lives here.</b> A paired charge is the product of the two factors' charges, which over
    /// the house scalar is a rounding. Every real twist is an exact integer, so the paired presentation still classifies
    /// <see cref="ChargeLane.Exact"/>; what does not survive is the theorem that a pair's behavior is the product of the
    /// two behaviors, because a pair's cells are not products of already-rounded cells. That identity is exact-only.
    /// </para>
    /// <para>
    /// <b>One material, compared by value.</b> A paired charge is multiplied in a single material, so a tensor of two
    /// factors carrying different ones — two prime fields at different moduli, say — would reinterpret the right
    /// factor's charges in the left factor's arithmetic and build a presentation neither factor describes. The two are
    /// therefore required equal and an unequal pair is refused, which is the same statement as
    /// <see cref="PresentedMachine{TValue, TOps}.AreEquivalent"/>'s.
    /// </para>
    /// <para>
    /// <b>Brackets pair too.</b> A pair's re-association charge is the product of the two factors' at the paired triple,
    /// so a factor that charges its brackets keeps charging them through the pair; two uniform factors pair to one
    /// uniform charge, exactly as before. Dropping a factor's cochain here would hand back a presentation that silently
    /// flattens the brackets the factor charges for.
    /// </para>
    /// </remarks>
    public static ChargedPresentation<TValue, TOps> Tensor<TValue, TOps>(ChargedPresentation<TValue, TOps> left, ChargedPresentation<TValue, TOps> right)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        if (!left.HasCompiledNormalFormBasis || !right.HasCompiledNormalFormBasis) {
            throw new ArgumentException(message: "A tensor pairs two finite bases, and a factor without one has no cells to pair.", paramName: nameof(left));
        }

        if ((0 == left.IdentityKeys.Length) || (0 == right.IdentityKeys.Length)) {
            throw new ArgumentException(message: "A tensor pairs the two factors' units, and a factor without one has nothing to pair.", paramName: nameof(left));
        }

        if (!EqualityComparer<TOps>.Default.Equals(x: left.Material, y: right.Material)) {
            throw new ArgumentException(message: "A tensor multiplies the two factors' charges in one material, so the factors carry the same one.", paramName: nameof(right));
        }

        var leftCount = left.NormalFormCount;
        var rightCount = right.NormalFormCount;
        var generatorCount = (leftCount * rightCount);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: generatorCount, other: 64);

        var material = left.Material;
        var generators = SingleColourGenerators(count: generatorCount);
        var unitCharges = new List<TValue>();
        var unitTerms = new List<int[]>();

        for (var leftIndex = 0; (leftIndex < left.IdentityKeys.Length); ++leftIndex) {
            for (var rightIndex = 0; (rightIndex < right.IdentityKeys.Length); ++rightIndex) {
                unitCharges.Add(item: material.Multiply(left: left.IdentityCharges[leftIndex], right: right.IdentityCharges[rightIndex]));
                unitTerms.Add(item: [((int)((left.IdentityKeys[leftIndex] * rightCount) + right.IdentityKeys[rightIndex]))]);
            }
        }

        var rules = new List<RewriteRule<TValue>> {
            PairedReassociationRule(left: left, right: right, rightCount: rightCount, generatorCount: generatorCount, material: material),
            new(
                kind: RuleKind.Reduce,
                pattern: ReadOnlyMemory<int>.Empty,
                replacement: RewriteRule<TValue>.PackReplacement(terms: [.. unitTerms]),
                charges: unitCharges.ToArray()
            ),
        };

        var leftStarts = left.CellStarts;
        var leftTargets = left.CellTargets;
        var leftCharges = left.CellCharges;
        var rightStarts = right.CellStarts;
        var rightTargets = right.CellTargets;
        var rightCharges = right.CellCharges;

        for (var symbol = 0; (symbol < generatorCount); ++symbol) {
            var leftSource = (symbol / rightCount);
            var rightSource = (symbol % rightCount);

            for (var next = 0; (next < generatorCount); ++next) {
                var leftCell = ((leftSource * leftCount) + (next / rightCount));
                var rightCell = ((rightSource * rightCount) + (next % rightCount));
                var pairCharges = new List<TValue>();
                var pairTerms = new List<int[]>();

                for (var leftEntry = leftStarts[leftCell]; (leftEntry < leftStarts[(leftCell + 1)]); ++leftEntry) {
                    for (var rightEntry = rightStarts[rightCell]; (rightEntry < rightStarts[(rightCell + 1)]); ++rightEntry) {
                        pairCharges.Add(item: material.Multiply(left: leftCharges[((int)leftEntry)], right: rightCharges[((int)rightEntry)]));
                        pairTerms.Add(item: [((int)((leftTargets[((int)leftEntry)] * rightCount) + rightTargets[((int)rightEntry)]))]);
                    }
                }

                rules.Add(item: ((0 == pairTerms.Count)
                    ? new(
                        kind: RuleKind.Annihilate,
                        pattern: new[] { symbol, next },
                        replacement: ReadOnlyMemory<int>.Empty,
                        charges: ReadOnlyMemory<TValue>.Empty
                    )
                    : new(
                        kind: RuleKind.Reduce,
                        pattern: new[] { symbol, next },
                        replacement: RewriteRule<TValue>.PackReplacement(terms: [.. pairTerms]),
                        charges: pairCharges.ToArray()
                    )));
            }
        }

        return ChargedPresentation<TValue, TOps>.Create(
            generators: generators,
            rules: CollectionsMarshal.AsSpan(list: rules),
            material: material
        );
    }

    // The Cayley-Dickson 2-cochain, by the doubling recursion (a, b)·(c, d) = (a·c − d̄·b, d·a + b·c̄) with the
    // conjugation (a, b)‾ = (ā, −b) — the convention DoublingAlgebra uses, whose floor-two instance reproduces the
    // house quaternion component for component. The target index is always the exclusive-or of the two, so only the
    // sign is computed here.
    private static int CayleyDicksonSign(int left, int right, int floors) {
        if (0 == floors) { return 1; }

        var half = (1 << (floors - 1));
        var leftHigh = (left >= half);
        var leftLow = left & (half - 1);
        var rightHigh = (right >= half);
        var rightLow = right & (half - 1);

        if (!leftHigh && !rightHigh) { return CayleyDicksonSign(left: leftLow, right: rightLow, floors: (floors - 1)); }
        if (!leftHigh) { return CayleyDicksonSign(left: rightLow, right: leftLow, floors: (floors - 1)); }
        if (!rightHigh) { return (ConjugationSign(index: rightLow) * CayleyDicksonSign(left: leftLow, right: rightLow, floors: (floors - 1))); }

        return (-ConjugationSign(index: rightLow) * CayleyDicksonSign(left: rightLow, right: leftLow, floors: (floors - 1)));
    }

    // The tower's associator 3-cochain, declared as re-association data. It is the coboundary of the SAME 2-cochain the
    // reduction rules carry: e_a·(e_b·e_c) is σ(b,c)·σ(a,b⊕c) on the target, (e_a·e_b)·e_c is σ(a,b)·σ(a⊕b,c) on it, and
    // both bracketings reach the same target because the index law is exclusive-or. Every sign is its own inverse, so
    // the ratio the splice charges is the product of all four and no division is taken.
    private static RewriteRule<TValue> CayleyDicksonAssociatorRule<TValue>(int[] forward, int floors, TValue one, TValue negativeOne) {
        var dimension = forward.Length;
        var charges = new TValue[((dimension * dimension) * dimension)];

        for (var left = 0; (left < dimension); ++left) {
            var a = forward[left];

            for (var middle = 0; (middle < dimension); ++middle) {
                var b = forward[middle];

                for (var right = 0; (right < dimension); ++right) {
                    var c = forward[right];
                    var sign = ((CayleyDicksonSign(left: b, right: c, floors: floors) * CayleyDicksonSign(left: a, right: b ^ c, floors: floors))
                        * (CayleyDicksonSign(left: a, right: b, floors: floors) * CayleyDicksonSign(left: a ^ b, right: c, floors: floors)));

                    charges[((((left * dimension) + middle) * dimension) + right)] = ((sign > 0) ? one : negativeOne);
                }
            }
        }

        return new(
            kind: RuleKind.Reassociate,
            pattern: ReadOnlyMemory<int>.Empty,
            replacement: ReadOnlyMemory<int>.Empty,
            charges: charges
        );
    }
    private static int ConjugationSign(int index) =>
        ((0 == index) ? 1 : -1);

    // The paired re-association datum. A pair's brackets are the two factors' brackets side by side, so the charge is
    // the product of the two factors' — which for two uniform factors is one charge and for a live factor is a charge
    // per paired triple. Dropping a live factor's cochain here would hand back a presentation that silently flattens
    // the brackets the factor charges for, so it is carried rather than lost.
    private static RewriteRule<TValue> PairedReassociationRule<TValue, TOps>(ChargedPresentation<TValue, TOps> left, ChargedPresentation<TValue, TOps> right, int rightCount, int generatorCount, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        if (!left.HasLiveReassociation && !right.HasLiveReassociation) {
            return ReassociationRule(charge: material.Multiply(left: left.SpliceCharge(left: 0L, middle: 0L, right: 0L), right: right.SpliceCharge(left: 0L, middle: 0L, right: 0L)));
        }

        var charges = new TValue[((generatorCount * generatorCount) * generatorCount)];

        for (var first = 0; (first < generatorCount); ++first) {
            for (var second = 0; (second < generatorCount); ++second) {
                for (var third = 0; (third < generatorCount); ++third) {
                    charges[((((first * generatorCount) + second) * generatorCount) + third)] = material.Multiply(
                        left: left.SpliceCharge(left: (first / rightCount), middle: (second / rightCount), right: (third / rightCount)),
                        right: right.SpliceCharge(left: (first % rightCount), middle: (second % rightCount), right: (third % rightCount))
                    );
                }
            }
        }

        return new(
            kind: RuleKind.Reassociate,
            pattern: ReadOnlyMemory<int>.Empty,
            replacement: ReadOnlyMemory<int>.Empty,
            charges: charges
        );
    }

    // One same-length, lexicographically decreasing rule: the alternating word of the given length starting with the
    // higher symbol rewrites to the one starting with the lower. At a bond of two that IS the ordinary swap, and above
    // it, the braid relation of a Coxeter pair — one datum, so the commuting case is not a second shape.
    private static void AppendBraidRule<TValue>(List<RewriteRule<TValue>> rules, int high, int low, int bond, TValue charge) {
        var image = new int[bond];
        var pattern = new int[bond];

        for (var step = 0; (step < bond); ++step) {
            var leading = (0 == (step & 1));

            image[step] = (leading ? low : high);
            pattern[step] = (leading ? high : low);
        }

        rules.Add(item: new(
            kind: RuleKind.Swap,
            pattern: pattern,
            replacement: RewriteRule<TValue>.PackReplacement(terms: [image]),
            charges: new[] { charge }
        ));
    }

    // The composition rules of a category presented by its generators: one reduction per ordered pair whose boundaries
    // meet, and the charge-zero annihilation everywhere else. Composability is DERIVED from the generators themselves,
    // so a quiver's endpoint match, a poset interval's, and the wire count a tangle composes on are one comparison
    // stated once rather than the same fact written out at every entry. All the caller supplies is what the boundaries
    // cannot determine: the composite's symbol and the charge it carries.
    private static void AppendBoundaryCompositionRules<TValue>(List<RewriteRule<TValue>> rules, ReadOnlySpan<Generator> generators, Func<int, int, (int Symbol, TValue Charge)> composite) {
        for (var left = 0; (left < generators.Length); ++left) {
            for (var right = 0; (right < generators.Length); ++right) {
                if (!BoundariesMeet(left: generators[left], right: generators[right])) {
                    rules.Add(item: new(
                        kind: RuleKind.Annihilate,
                        pattern: new[] { left, right },
                        replacement: ReadOnlyMemory<int>.Empty,
                        charges: ReadOnlyMemory<TValue>.Empty
                    ));

                    continue;
                }

                var (symbol, charge) = composite(left, right);

                rules.Add(item: new(
                    kind: RuleKind.Reduce,
                    pattern: new[] { left, right },
                    replacement: RewriteRule<TValue>.PackReplacement(terms: [[symbol]]),
                    charges: new[] { charge }
                ));
            }
        }
    }

    // Two generators compose exactly when the wires the first hands over ARE the wires the second takes: the same
    // number of them, in the same colours, in the same order. One comparison says all of that, because a span
    // comparison compares length before contents: a diagram whose boundaries differ in width and a quiver arrow or
    // poset interval whose endpoints disagree in colour both fail this same line, and a one-colour arity-one
    // presentation runs it against one-entry lists — the same test in that degenerate case. Restating the width as
    // a separate conjunct would read as two independent halves and be neither — the length test it names is already
    // inside the one below, and a Generator's Coarity and Arity ARE those two lengths.
    private static bool BoundariesMeet(in Generator left, in Generator right) =>
        left.Outputs.SequenceEqual(other: right.Inputs);

    // The cells of the second product, one per ordered pair of words, emitted from the recursion on the two heads:
    // (xu)·(yv) is x·(u·yv) + y·(xu·v) + [x·y]·(u·v), the third term present only under a letter product. The pairs are
    // walked in increasing combined length, so the three shorter cells a pair reads are already emitted and every cell
    // is formed exactly once from data. A term that would outgrow the window is dropped — the quotient by the ideal the
    // over-long words span — and a pair left with no term at all annihilates, which is what every over-long pair of a
    // plain shuffle does.
    private static void AppendInterleavingRules<TValue, TOps>(List<RewriteRule<TValue>> rules, ShuffleBasis basis, ReadOnlySpan<int> letterProduct, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var count = basis.Count;
        var letterCount = basis.LetterCount;
        var merges = (0 != letterProduct.Length);
        var windowDegree = basis.WindowDegree;
        var accumulator = new TValue[count];
        var charges = new List<TValue>();
        var ruleOfPair = new int[(count * count)];
        var stamp = new int[count];
        var terms = new List<int[]>();
        var touched = new int[count];

        Array.Fill(array: stamp, value: -1);

        Span<int> sourceLetter = stackalloc int[3];
        Span<int> sourcePair = stackalloc int[3];

        for (var pairLength = 0; (pairLength <= (2 * windowDegree)); ++pairLength) {
            var longest = Math.Min(val1: windowDegree, val2: pairLength);

            for (var leftLength = Math.Max(val1: 0, val2: (pairLength - windowDegree)); (leftLength <= longest); ++leftLength) {
                var rightLength = (pairLength - leftLength);

                for (var left = basis.Start(length: leftLength); (left < basis.Start(length: (leftLength + 1))); ++left) {
                    for (var right = basis.Start(length: rightLength); (right < basis.Start(length: (rightLength + 1))); ++right) {
                        var pair = ((left * count) + right);
                        var sourceCount = 0;
                        var touchedCount = 0;

                        if (0 != leftLength) {
                            sourceLetter[sourceCount] = basis.Head(symbol: left);
                            sourcePair[sourceCount++] = ((basis.Tail(symbol: left) * count) + right);
                        }

                        if (0 != rightLength) {
                            sourceLetter[sourceCount] = basis.Head(symbol: right);
                            sourcePair[sourceCount++] = ((left * count) + basis.Tail(symbol: right));
                        }

                        if (merges && (0 != leftLength) && (0 != rightLength)) {
                            sourceLetter[sourceCount] = letterProduct[((basis.Head(symbol: left) * letterCount) + basis.Head(symbol: right))];
                            sourcePair[sourceCount++] = ((basis.Tail(symbol: left) * count) + basis.Tail(symbol: right));
                        }

                        // The recursion's base case, and the only pair with no shorter one to read: the empty word times
                        // itself is the empty word, which is this presentation's unit.
                        if (0 == pairLength) {
                            accumulator[0] = material.One;
                            stamp[0] = pair;
                            touched[touchedCount++] = 0;
                        }

                        for (var source = 0; (source < sourceCount); ++source) {
                            var child = rules[ruleOfPair[sourcePair[source]]];
                            var letter = sourceLetter[source];
                            var replacement = child.Replacement;
                            var offset = 0;

                            for (var term = 0; (term < child.TermCount); ++term) {
                                // Every replacement this entry emits is a single word, so the term's own symbol sits
                                // immediately after the length its packing carries.
                                var length = replacement[offset++];
                                var symbol = basis.Prepend(letter: letter, symbol: replacement[offset]);

                                offset += length;

                                if (symbol < 0) { continue; }

                                if (stamp[symbol] != pair) {
                                    accumulator[symbol] = material.Zero;
                                    stamp[symbol] = pair;
                                    touched[touchedCount++] = symbol;
                                }

                                accumulator[symbol] = material.Add(left: accumulator[symbol], right: child.Charges[term]);
                            }
                        }

                        Array.Sort(array: touched, index: 0, length: touchedCount);
                        charges.Clear();
                        terms.Clear();

                        for (var index = 0; (index < touchedCount); ++index) {
                            var symbol = touched[index];

                            if (material.IsZero(value: accumulator[symbol])) { continue; }

                            charges.Add(item: accumulator[symbol]);
                            terms.Add(item: [symbol]);
                        }

                        ruleOfPair[pair] = rules.Count;

                        rules.Add(item: ((0 == terms.Count)
                            ? new(
                                kind: RuleKind.Annihilate,
                                pattern: new[] { left, right },
                                replacement: ReadOnlyMemory<int>.Empty,
                                charges: ReadOnlyMemory<TValue>.Empty
                            )
                            : new(
                                kind: RuleKind.Reduce,
                                pattern: new[] { left, right },
                                replacement: RewriteRule<TValue>.PackReplacement(terms: [.. terms]),
                                charges: charges.ToArray()
                            )));
                    }
                }
            }
        }
    }

    // The ordered swap rules of a commuting-up-to-a-charge presentation: one rule per descending generator pair,
    // rewriting it to the ascending one at the given charge. Clifford charges minus one, a commutative window one.
    private static void AppendSwapRules<TValue>(List<RewriteRule<TValue>> rules, int count, TValue charge) {
        for (var high = 1; (high < count); ++high) {
            for (var low = 0; (low < high); ++low) {
                AppendBraidRule(rules: rules, high: high, low: low, bond: 2, charge: charge);
            }
        }
    }

    // The row's index in a lexicographically sorted permutation table, or minus one when the table does not hold it.
    private static int IndexOfRow(int[] table, ReadOnlySpan<int> row, int pointCount, int elementCount) {
        var low = 0;
        var high = elementCount;

        while (low < high) {
            var middle = ((low + high) >> 1);
            var comparison = table.AsSpan(start: (middle * pointCount), length: pointCount).SequenceCompareTo(other: row);

            if (0 == comparison) { return middle; }

            if (comparison < 0) { low = (middle + 1); } else { high = middle; }
        }

        return -1;
    }

    // The generator array of a one-object presentation: one degree-one generator per symbol, all sharing the single
    // boundary colour.
    private static Generator[] SingleColourGenerators(int count) {
        var generators = new Generator[count];

        for (var symbol = 0; (symbol < count); ++symbol) {
            generators[symbol] = new Generator(symbol: symbol, inputs: SingleColour, outputs: SingleColour, degree: 1);
        }

        return generators;
    }
    private static TValue NegativeOne<TValue, TOps>(TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        if (material is not ISignedMaterial<TValue, TOps> signed) {
            throw new ArgumentException(message: "This presentation carries a charge of minus one, which an unsigned material cannot express.", paramName: nameof(material));
        }

        return signed.Negate(value: material.One);
    }
    private static RewriteRule<TValue> ReassociationRule<TValue>(TValue charge) =>
        new(
            kind: RuleKind.Reassociate,
            pattern: ReadOnlyMemory<int>.Empty,
            replacement: ReadOnlyMemory<int>.Empty,
            charges: new[] { charge }
        );

    // The enumerated planar basis of one width bound, and the arc trace that composes two of its diagrams. It is
    // construction-time DATA with a walk over it: the walk runs once per ordered pair while the composition table is
    // built and never again, so no product path learns what a boundary point is.
    private sealed class PlanarBasis {
        private readonly int[] m_blockStart;
        private readonly int[] m_inputWidth;
        private readonly int[] m_outputWidth;
        private readonly int[][] m_partner;
        private readonly int m_stride;
        private readonly int[][] m_symbolOfCode;

        public PlanarBasis(int maximumWidth) {
            var stride = (maximumWidth + 1);
            var blockStart = new int[((stride * stride) + 1)];
            var partners = new List<int[]>();
            var symbolOfCode = new int[(stride * stride)][];

            // Blocks run input width major, output width minor, and an odd-sum block is empty, which the shared start
            // array states by giving it the same start as the block after it.
            for (var inputs = 0; (inputs <= maximumWidth); ++inputs) {
                for (var outputs = 0; (outputs <= maximumWidth); ++outputs) {
                    var block = ((inputs * stride) + outputs);
                    var points = (inputs + outputs);

                    blockStart[block] = partners.Count;
                    symbolOfCode[block] = [];

                    if (0 != (points & 1)) { continue; }

                    symbolOfCode[block] = new int[(1 << points)];

                    Array.Fill(array: symbolOfCode[block], value: -1);
                    Enumerate(points: points, partners: partners, symbolOfCode: symbolOfCode[block]);
                }
            }

            blockStart[(stride * stride)] = partners.Count;

            var inputWidth = new int[partners.Count];
            var outputWidth = new int[partners.Count];

            for (var inputs = 0; (inputs <= maximumWidth); ++inputs) {
                for (var outputs = 0; (outputs <= maximumWidth); ++outputs) {
                    var block = ((inputs * stride) + outputs);

                    for (var symbol = blockStart[block]; (symbol < blockStart[(block + 1)]); ++symbol) {
                        inputWidth[symbol] = inputs;
                        outputWidth[symbol] = outputs;
                    }
                }
            }

            m_blockStart = blockStart;
            m_inputWidth = inputWidth;
            m_outputWidth = outputWidth;
            m_partner = [.. partners];
            m_stride = stride;
            m_symbolOfCode = symbolOfCode;
        }

        public int Count => m_partner.Length;

        // The identity diagram of one width is the nested matching, whose word is every opener before every closer —
        // the lexicographically first word of its block, so it needs no lookup.
        public int IdentitySymbol(int width) =>
            m_blockStart[((width * m_stride) + width)];
        public int InputWidth(int symbol) =>
            m_inputWidth[symbol];
        public int OutputWidth(int symbol) =>
            m_outputWidth[symbol];

        // Glue the left diagram's outputs to the right diagram's inputs, walk each free boundary point along the two
        // matchings and the glue until it reaches another free point, and count what the walks never reach. The
        // composite's widths are the two free ends' widths and their sum is even — the left diagram's widths agree in
        // parity, so do the right one's, and the glued boundary is shared — so the composite is always a diagram of
        // this basis and a composable pair never falls through to annihilation.
        public int Compose(int left, int right, out int loops) {
            var leftInputs = m_inputWidth[left];
            var shared = m_outputWidth[left];
            var rightOutputs = m_outputWidth[right];
            var leftPoints = (leftInputs + shared);
            var rightPoints = (shared + rightOutputs);
            var total = (leftPoints + rightPoints);
            var leftPartner = m_partner[left];
            var rightPartner = m_partner[right];

            Span<int> glue = stackalloc int[total];
            Span<int> match = stackalloc int[total];
            Span<bool> seen = stackalloc bool[total];

            for (var point = 0; (point < leftPoints); ++point) {
                glue[point] = -1;
                match[point] = leftPartner[point];
                seen[point] = false;
            }

            for (var point = 0; (point < rightPoints); ++point) {
                glue[(leftPoints + point)] = -1;
                match[(leftPoints + point)] = (leftPoints + rightPartner[point]);
                seen[(leftPoints + point)] = false;
            }

            // The left diagram reads its outputs in reverse around the boundary, so its wire w sits at the point that
            // many places back from its end, while the right diagram reads its inputs forward from its start.
            for (var wire = 0; (wire < shared); ++wire) {
                var leftPoint = ((leftPoints - 1) - wire);
                var rightPoint = (leftPoints + wire);

                glue[leftPoint] = rightPoint;
                glue[rightPoint] = leftPoint;
            }

            var compositePoints = (leftInputs + rightOutputs);
            var outputBase = (leftPoints + shared);

            Span<int> partner = stackalloc int[compositePoints];

            for (var position = 0; (position < compositePoints); ++position) {
                var node = ((position < leftInputs) ? position : (outputBase + (position - leftInputs)));

                if (seen[node]) { continue; }

                var cursor = node;

                seen[cursor] = true;

                while (true) {
                    cursor = match[cursor];
                    seen[cursor] = true;

                    if (glue[cursor] < 0) { break; }

                    cursor = glue[cursor];
                    seen[cursor] = true;
                }

                var reached = ((cursor < leftInputs) ? cursor : ((cursor - outputBase) + leftInputs));

                partner[position] = reached;
                partner[reached] = position;
            }

            loops = 0;

            for (var node = 0; (node < total); ++node) {
                if (seen[node]) { continue; }

                var cursor = node;

                do {
                    seen[cursor] = true;
                    cursor = match[cursor];
                    seen[cursor] = true;
                    cursor = glue[cursor];
                } while (cursor != node);

                ++loops;
            }

            var code = 0;

            for (var position = 0; (position < compositePoints); ++position) {
                code = (code << 1) | ((partner[position] < position) ? 1 : 0);
            }

            return m_symbolOfCode[((leftInputs * m_stride) + rightOutputs)][code];
        }

        // Every planar diagram of one boundary shape, in the canonical order: the balanced-parenthesis words of the
        // boundary length, lexicographically with an opener before a closer, which IS non-crossing. The two prunings
        // are the whole admission test — half the points may be openers and a closer needs a depth to close — and
        // together they force the depth to zero at the end, so a reached leaf is balanced with nothing left to check.
        // One stack scan then turns the word into the matching.
        private static void Enumerate(int points, List<int[]> partners, int[] symbolOfCode) {
            var openers = (points >> 1);
            var stack = new int[points];
            var word = new int[points];

            void Extend(int position, int depth, int placed) {
                if (position == points) {
                    var code = 0;
                    var height = 0;
                    var partner = new int[points];

                    for (var scan = 0; (scan < points); ++scan) {
                        code = (code << 1) | word[scan];

                        if (0 == word[scan]) {
                            stack[height++] = scan;
                        } else {
                            var opener = stack[--height];

                            partner[opener] = scan;
                            partner[scan] = opener;
                        }
                    }

                    symbolOfCode[code] = partners.Count;

                    partners.Add(item: partner);

                    return;
                }

                if (placed < openers) {
                    word[position] = 0;

                    Extend(position: (position + 1), depth: (depth + 1), placed: (placed + 1));
                }

                if (depth > 0) {
                    word[position] = 1;

                    Extend(position: (position + 1), depth: (depth - 1), placed: placed);
                }
            }

            Extend(position: 0, depth: 0, placed: 0);
        }
    }

    // The words of one alphabet at one length window, and the arithmetic that walks between them. A word's KEY is its
    // index in the canonical order — ascending by length, then lexicographically — so taking a head, taking a tail and
    // prepending a letter are index arithmetic and no word is ever materialized. It is construction-time DATA, read once
    // per ordered pair while the cells are emitted and never again.
    private sealed class ShuffleBasis {
        private readonly int m_letterCount;
        private readonly int[] m_lengthOf;
        private readonly int[] m_scale;
        private readonly int[] m_start;
        private readonly int m_windowDegree;

        public ShuffleBasis(int letterCount, int windowDegree) {
            var scale = new int[(windowDegree + 1)];
            var start = new int[(windowDegree + 2)];
            var total = 1L;
            var words = 1L;

            scale[0] = 1;
            start[0] = 0;
            start[1] = 1;

            for (var length = 1; (length <= windowDegree); ++length) {
                words *= letterCount;
                total += words;

                // The cap is the 512 normal forms a finite basis holds, read at the word count the two arguments name
                // together: two letters reach 511 words at a window of eight and 1023 at nine.
                if (total > MaximumShuffleWords) {
                    throw new ArgumentOutOfRangeException(paramName: nameof(windowDegree), message: $"An alphabet of {letterCount} letter(s) already names {total} word(s) of length {length} or below, and no finite basis of this library holds more than {MaximumShuffleWords} normal forms.");
                }

                scale[length] = ((int)words);
                start[(length + 1)] = ((int)total);
            }

            var lengthOf = new int[((int)total)];

            for (var length = 0; (length <= windowDegree); ++length) {
                for (var symbol = start[length]; (symbol < start[(length + 1)]); ++symbol) { lengthOf[symbol] = length; }
            }

            m_letterCount = letterCount;
            m_lengthOf = lengthOf;
            m_scale = scale;
            m_start = start;
            m_windowDegree = windowDegree;
        }

        public int Count => m_lengthOf.Length;
        public int LetterCount => m_letterCount;
        public int WindowDegree => m_windowDegree;

        // The word's first letter: its index inside its own length block, divided by the block's stride.
        public int Head(int symbol) =>
            ((symbol - m_start[m_lengthOf[symbol]]) / m_scale[(m_lengthOf[symbol] - 1)]);
        public int LengthOf(int symbol) =>
            m_lengthOf[symbol];

        // One letter in front, or minus one where the word would outgrow the window and so leaves this basis.
        public int Prepend(int letter, int symbol) {
            var length = m_lengthOf[symbol];

            if (length >= m_windowDegree) { return -1; }

            return (m_start[(length + 1)] + ((letter * m_scale[length]) + (symbol - m_start[length])));
        }
        public int Start(int length) =>
            m_start[length];

        // Everything past the first letter: the index inside the block, modulo the block's stride.
        public int Tail(int symbol) {
            var length = m_lengthOf[symbol];

            return (m_start[(length - 1)] + ((symbol - m_start[length]) % m_scale[(length - 1)]));
        }
    }
}
