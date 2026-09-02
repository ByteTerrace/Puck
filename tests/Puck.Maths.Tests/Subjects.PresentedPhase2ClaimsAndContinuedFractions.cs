using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- phase 2 structural claims ----

    /// <summary>Proves the three residual twists are the three documented operators, and that the counit twist is the
    /// left quotient a language derivative needs.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ResidualTwistsSeparate() {
        var pattern = TokenPattern<BigInteger, IntegerMaterial>.Create(
            letterCount: 2,
            material: default,
            window: 0
        );
        var algebra = pattern.Algebra;

        // The free monoid is where the twisted Leibniz rule is an identity rather than an approximation: with no
        // relation to annihilate, every twist satisfies its own rule at every operand.
        if (algebra.Presentation.HasFiniteNormalForms) { return "the unwindowed free monoid reports a finite basis, so the residual would be measured against a truncation"; }

        var x = algebra.Generator(symbol: 0);
        var y = algebra.Generator(symbol: 1);
        var xy = algebra.Multiply(
            left: x,
            right: y
        );
        var xyx = algebra.Multiply(
            left: xy,
            right: x
        );

        // Counit: the leading occurrence only, so D_x(x·y·x) is the single word y·x.
        var counit = algebra.Residual(
            symbol: 0,
            value: xyx,
            twist: ResidualTwist.Counit
        );

        if (
            (1 != counit.SupportCount) ||
            !algebra.AreEqual(
            left: counit,
            right: algebra.Multiply(
                left: y,
                right: x
            )
        )
        ) {
            return $"the counit residual of x·y·x is not the left quotient y·x ({counit.SupportCount} term(s))";
        }

        // Identity: both occurrences, so the derivative carries y·x from position zero and x·y from position two.
        var identity = algebra.Residual(
            symbol: 0,
            value: xyx,
            twist: ResidualTwist.Identity
        );
        var expected = algebra.Add(
            left: algebra.Multiply(
                left: y,
                right: x
            ),
            right: xy
        );

        if (!algebra.AreEqual(
            left: identity,
            right: expected
        )) { return "the identity residual of x·y·x is not (y·x) + (x·y)"; }

        // Shift: each prefix letter is preceded by the shift generator, so the second occurrence contributes y·x·y·y
        // rather than x·y — the prefix x·y becomes (y·x)·(y·y) with the shift generator y written before each letter.
        var shift = algebra.Residual(
            shiftSymbol: 1,
            symbol: 0,
            twist: ResidualTwist.ShiftGenerator,
            value: xyx
        );
        var shifted = algebra.Add(
            left: algebra.Multiply(
                left: y,
                right: x
            ),
            right: algebra.Multiply(
                left: algebra.Multiply(
                    left: y,
                    right: x
                ),
                right: algebra.Multiply(
                    left: y,
                    right: y
                )
            )
        );

        if (!algebra.AreEqual(
            left: shift,
            right: shifted
        )) { return "the shift residual of x·y·x did not precede each prefix letter with the shift generator"; }

        // The rule itself, on the presentation where it cannot be broken by a relation.
        var rng = new Random(Seed: 0x51D3);

        for (var trial = 0; (trial < 64); ++trial) {
            var u = RandomFreeCombination(
                algebra: algebra,
                pattern: pattern,
                rng: rng
            );
            var v = RandomFreeCombination(
                algebra: algebra,
                pattern: pattern,
                rng: rng
            );
            var whole = algebra.Residual(
                symbol: 0,
                value: algebra.Multiply(
                    left: u,
                    right: v
                ),
                twist: ResidualTwist.Identity
            );
            var parts = algebra.Add(
                left: algebra.Multiply(
                    left: algebra.Residual(
                        symbol: 0,
                        value: u,
                        twist: ResidualTwist.Identity
                    ),
                    right: v
                ),
                right: algebra.Multiply(
                    left: u,
                    right: algebra.Residual(
                        symbol: 0,
                        value: v,
                        twist: ResidualTwist.Identity
                    )
                )
            );

            if (!algebra.AreEqual(
                left: whole,
                right: parts
            )) { return $"the Leibniz rule failed on the free monoid at trial {trial}"; }
        }

        return null;
    }
    /// <summary>Proves the Möbius element is the convolution inverse of zeta on a divisibility window, and that the
    /// window's arithmetic agrees with the shipped factorization and prime-counting kernels.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MobiusStarRoundTrip() {
        // EVERY prime through the window, so the window holds every integer through it and the classical identities —
        // which quantify over all n, not over the smooth ones — are stated in full rather than truncated.
        ulong[] primes = [2UL, 3UL, 5UL, 7UL, 11UL, 13UL, 17UL, 19UL, 23UL, 29UL, 31UL, 37UL, 41UL, 43UL, 47UL, 53UL, 59UL];
        const int SievePrimes = 4;
        const long Window = 60L;

        var divisibility = DivisibilityAlgebra<BigInteger, IntegerMaterial>.Create(
            material: default,
            primes: primes,
            window: Window
        );
        var algebra = divisibility.Algebra;

        if (Window != divisibility.Window) { return $"the window reports a bound of {divisibility.Window}, expected {Window}"; }

        // The precondition of every classical identity below, computed rather than assumed: these primes cover every
        // integer through the window, so the covering reach IS the window and the sums range over all n, not the
        // smooth ones. A window whose generators stopped covering would silently answer a smaller number instead.
        if (Window != divisibility.ConsecutiveBound) {
            return $"the window covers every integer only through {divisibility.ConsecutiveBound}, so the classical identities are not statements about it through {Window}";
        }

        if (divisibility.NormalFormCount != algebra.MaximumSupportCount) {
            return $"the window holds {divisibility.NormalFormCount} integer(s) but the algebra bounds a support at {algebra.MaximumSupportCount}";
        }

        // The key scheme is a bijection onto the smooth integers of the window, and it is bisected rather than hashed.
        for (var key = 0; (key < divisibility.NormalFormCount); ++key) {
            var value = divisibility.Value(key: key);

            if (
                (value < 1L) ||
                (value > Window)
            ) { return $"key {key} names {value}, which is outside the window"; }

            if (
                !divisibility.TryKey(
                value: value,
                out var recovered
            ) ||
                (recovered != key)
            ) { return $"key {key} named {value}, which mapped back to {recovered}"; }

            if (!IsSmooth(
                primes: primes,
                value: value
            )) { return $"key {key} names {value}, which is not smooth for this prime set"; }
        }

        for (var value = 1L; (value <= Window); ++value) {
            if (IsSmooth(
                primes: primes,
                value: value
            ) != divisibility.TryKey(
                value: value,
                out _
            )) {
                return $"{value} is smooth exactly when the window admits it, and the two disagreed";
            }
        }

        if (!divisibility.TryMobius(
            mobius: out var mobius,
            obstruction: out var refusal
        )) {
            return $"the Möbius element was refused (attempted {refusal.Attempted}, steps {refusal.StepsTaken}, key {refusal.SupportKey})";
        }

        // The round trip, which is the whole statement: mu convolved with zeta IS the unit of the window.
        var round = algebra.Multiply(
            left: mobius,
            right: divisibility.Zeta
        );

        if (
            (1 != round.SupportCount) ||
            (0L != round.Keys[0]) ||
            (BigInteger.One != round.Coefficients[0])
        ) {
            return $"mu ⋆ zeta is not the unit: {round.SupportCount} term(s), leading ({((round.SupportCount > 0)
                ? round.Keys[0]
                : -1L)},{((round.SupportCount > 0)
                ? round.Coefficients[0]
                : BigInteger.Zero)})";
        }

        if (!algebra.AreEqual(
            left: round,
            right: algebra.Identity
        )) { return "mu ⋆ zeta differs from the algebra's own identity"; }

        // Every coefficient against the shipped factorization, and the divisor counts against the same.
        var divisors = divisibility.DivisorCounts(order: 2);
        var mertens = BigInteger.Zero;

        for (var key = 0; (key < divisibility.NormalFormCount); ++key) {
            var value = divisibility.Value(key: key);

            if (mobius[key] != MobiusOracle(value: value)) { return $"mu({value}) = {mobius[key]}, the factorization says {MobiusOracle(value: value)}"; }

            if (divisors[key] != DivisorCountOracle(value: value)) { return $"d({value}) = {divisors[key]}, the factorization says {DivisorCountOracle(value: value)}"; }
        }

        for (var value = 1L; (value <= Window); ++value) {
            if (divisibility.TryKey(
                value: value,
                out var key
            )) { mertens += mobius[key]; }
        }

        if (algebra.Pair(
            covector: divisibility.Indicator(bound: Window),
            value: mobius
        ) != mertens) {
            return $"the Mertens partial sum through {Window} disagrees with the summed coefficients";
        }

        // Legendre's sieve as one product and one pairing: the integers through the bound divisible by none of the four
        // smallest primes are one, plus every prime above seven — which is the shipped prime count minus four.
        var legendre = algebra.Pair(
            covector: divisibility.FloorCovector(bound: Window),
            value: divisibility.Sieve(primeCount: SievePrimes)
        );
        var expected = (((BigInteger)(((uint)Window).PrimeCountingFunction() - SievePrimes)) + BigInteger.One);

        if (legendre != expected) { return $"the Legendre count through {Window} is {legendre}, the prime-counting function implies {expected}"; }

        // Pairing the floor covector with mu is exactly one, for every bound the window covers.
        for (var bound = 1L; (bound <= Window); ++bound) {
            if (algebra.Pair(
                covector: divisibility.FloorCovector(bound: bound),
                value: mobius
            ) != BigInteger.One) {
                return $"Σ mu(n)·⌊{bound}/n⌋ is not one";
            }
        }

        return null;
    }
    /// <summary>Proves a scaled pattern carries its weight into every span's readout — a multiplicity at a counting
    /// material and an added cost at a tropical one — with the unit and absorbing weights pinned as the two edges.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The statement every other pattern law misses. The Leibniz and matcher cases quantify over elements, so
    /// a <c>Scale</c> that dropped its weight argument entirely would still return valid elements and leave them green;
    /// this one reads the weight back out and fails on the value.</remarks>
    public static string? PatternScaleWeights() {
        const int LetterCount = 2;
        const int Window = 4;

        var words = EnumerateWords(
            letterCount: LetterCount,
            maximumLength: Window
        );

        // "a span with an a in it, counted once per occurrence at the counting material" — a pattern with weights 0, 1
        // and 2 among its spans, so a scaling is visible at more than one magnitude.
        static PresentedAlgebra<TValue, TOps>.Element Subject<TValue, TOps>(TokenPattern<TValue, TOps> pattern)
            where TOps : struct, IMaterialOps<TValue, TOps> =>
            pattern.Union(
                left: pattern.Concatenate(
                    left: AnyLetter(pattern: pattern),
                    right: pattern.Predicate(letters: 1UL)
                ),
                right: pattern.Concatenate(
                    left: pattern.Predicate(letters: 1UL),
                    right: AnyLetter(pattern: pattern)
                )
            );

        var counting = TokenPattern<BigInteger, CountingMaterial>.Create(
            letterCount: LetterCount,
            material: default,
            window: Window
        );
        var countingSubject = Subject(pattern: counting);

        // The readout itself, before any scaling: the pattern is "two letters, at least one of them a", so a span of
        // length two carries one weight per branch that matches it and every other span carries none. Written out here
        // from the pattern's DEFINITION rather than read back from TryWeigh, so the scaling laws below are not the only
        // thing standing between a wrong readout and a green run.
        foreach (var word in words) {
            var matches = ((((2 == word.Length) && (0 == word[0]))
                ? 1
                : 0) + (((2 == word.Length) && (0 == word[1]))
                ? 1
                : 0));

            if (!counting.TryWeigh(
                letters: word,
                value: countingSubject,
                weight: out var counted
            )) {
                return $"the counting readout refused the span [{string.Join(
                    separator: ",",
                    values: word
                )}] inside the window";
            }

            if (counted != new BigInteger(value: matches)) {
                return $"the counting readout gives [{string.Join(
                    separator: ",",
                    values: word
                )}] the weight {counted}, where the pattern matches it {matches} way(s)";
            }
        }

        foreach (var weight in ((BigInteger[])[BigInteger.Zero, BigInteger.One, ((BigInteger)2), ((BigInteger)7)])) {
            var scaled = counting.Scale(
                value: countingSubject,
                weight: weight
            );

            foreach (var word in words) {
                if (
                    !counting.TryWeigh(
                    letters: word,
                    value: countingSubject,
                    weight: out var plain
                ) ||
                    !counting.TryWeigh(
                    letters: word,
                    value: scaled,
                    weight: out var carried
                )
                ) {
                    return $"the counting readout refused the span [{string.Join(
                        separator: ",",
                        values: word
                    )}] inside the window";
                }

                if (carried != (weight * plain)) {
                    return $"scaling by {weight} gave [{string.Join(
                        separator: ",",
                        values: word
                    )}] the weight {carried}, expected {(weight * plain)}";
                }
            }
        }

        // At the tropical material the product IS addition, so a scale is a cost added to every span the pattern
        // matches and the plus-infinity of a span it does not match stays absorbing.
        var tropical = TokenPattern<FixedQ4816, TropicalMaterial>.Create(
            letterCount: LetterCount,
            material: default,
            window: Window
        );
        var tropicalSubject = Subject(pattern: tropical);
        var infinity = default(TropicalMaterial).Zero;

        // The tropical readout, likewise written out from the pattern rather than read back from it (worklist C21): in
        // (min, +) a matching span costs the multiplicative unit — the carrier's zero — and a span the pattern misses
        // costs +∞. This is the leg the scaling statements below lean on and did not have.
        foreach (var word in words) {
            var matched = ((2 == word.Length) && ((0 == word[0]) || (0 == word[1])));

            if (!tropical.TryWeigh(
                letters: word,
                value: tropicalSubject,
                weight: out var cost
            )) {
                return $"the tropical readout refused the span [{string.Join(
                    separator: ",",
                    values: word
                )}] inside the window";
            }

            if (cost != (matched
                ? FixedQ4816.Zero
                : infinity)) {
                return $"the tropical readout gives [{string.Join(
                    separator: ",",
                    values: word
                )}] the raw cost {cost.Value}, where the pattern {(matched
                    ? "matches it, so it costs the unit"
                    : "misses it, so it costs +∞")}";
            }
        }

        foreach (var cost in ((long[])[0L, 1L, 4096L, 65536L])) {
            var weighted = Raw(value: cost);
            var scaled = tropical.Scale(
                value: tropicalSubject,
                weight: weighted
            );

            foreach (var word in words) {
                if (
                    !tropical.TryWeigh(
                    letters: word,
                    value: tropicalSubject,
                    weight: out var plain
                ) ||
                    !tropical.TryWeigh(
                    letters: word,
                    value: scaled,
                    weight: out var carried
                )
                ) {
                    return $"the tropical readout refused the span [{string.Join(
                        separator: ",",
                        values: word
                    )}] inside the window";
                }

                var expected = ((infinity == plain)
                    ? infinity
                    : Raw(value: (plain.Value + cost))
                );

                if (carried != expected) {
                    return $"a tropical cost of {cost} gave [{string.Join(
                        separator: ",",
                        values: word
                    )}] the raw weight {carried.Value}, expected {expected.Value}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves the compiled matcher weighs every span exactly as a shared-nothing backtracking oracle does, at
    /// a Boolean and at a counting material, and that its refusals are refusals rather than wrong answers.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MatcherMatchesBacktrackingOracle() {
        const int LetterCount = 2;
        const int Window = 5;

        var any = Oracles.WordPattern.Union(
            left: Oracles.WordPattern.Letter(letter: 0),
            right: Oracles.WordPattern.Letter(letter: 1)
        );
        var trees = new (string Name, Oracles.WordPattern Tree)[] {
            ("Σ*·a", Oracles.WordPattern.Concatenate(
            left: Oracles.WordPattern.Iterate(value: any),
            right: Oracles.WordPattern.Letter(letter: 0)
        )),
            ("a·Σ*", Oracles.WordPattern.Concatenate(
            left: Oracles.WordPattern.Letter(letter: 0),
            right: Oracles.WordPattern.Iterate(value: any)
        )),
            ("(a·b)*", Oracles.WordPattern.Iterate(value: Oracles.WordPattern.Concatenate(
            left: Oracles.WordPattern.Letter(letter: 0),
            right: Oracles.WordPattern.Letter(letter: 1)
        ))),
            ("Σ·a·Σ | ε", Oracles.WordPattern.Union(
            left: Oracles.WordPattern.Concatenate(
                left: any,
                right: Oracles.WordPattern.Concatenate(
                    left: Oracles.WordPattern.Letter(letter: 0),
                    right: any
                )
            ),
            right: Oracles.WordPattern.Empty
        )),
        };
        var words = EnumerateWords(
            letterCount: LetterCount,
            maximumLength: Window
        );

        var booleanPattern = TokenPattern<bool, BooleanMaterial>.Create(
            letterCount: LetterCount,
            material: default,
            window: Window
        );
        var countingPattern = TokenPattern<BigInteger, CountingMaterial>.Create(
            letterCount: LetterCount,
            material: default,
            window: Window
        );

        if (
            (LetterCount != booleanPattern.LetterCount) ||
            (Window != booleanPattern.Window)
        ) {
            return $"the pattern surface reports {booleanPattern.LetterCount} letter(s) and window {booleanPattern.Window}";
        }

        foreach (var (name, tree) in trees) {
            if (!TryBuildPattern<bool, BooleanMaterial>(
                pattern: booleanPattern,
                tree: tree,
                value: out var booleanValue
            )) {
                return $"{name}: the Boolean pattern could not be built (an iteration was refused)";
            }

            if (!TryBuildPattern<BigInteger, CountingMaterial>(
                pattern: countingPattern,
                tree: tree,
                value: out var countingValue
            )) {
                return $"{name}: the counting pattern could not be built (an iteration was refused)";
            }

            if (!PatternMatcher<bool, BooleanMaterial>.TryCompile(
                matcher: out var booleanMatcher,
                obstruction: out var booleanRefusal,
                pattern: booleanPattern,
                stateLimit: PresentedAlgebra<bool, BooleanMaterial>.MaximumClosureStates,
                value: booleanValue
            )) {
                return $"{name}: the Boolean matcher refused to compile after {booleanRefusal.StatesExplored} state(s), blocked at symbol {booleanRefusal.BlockedSymbol}";
            }

            if (!PatternMatcher<BigInteger, CountingMaterial>.TryCompile(
                matcher: out var countingMatcher,
                obstruction: out _,
                pattern: countingPattern,
                stateLimit: PresentedAlgebra<BigInteger, CountingMaterial>.MaximumClosureStates,
                value: countingValue
            )) {
                return $"{name}: the counting matcher refused to compile";
            }

            if (
                (booleanMatcher.StateCount != booleanMatcher.Closure.StateCount) ||
                (LetterCount != booleanMatcher.LetterCount) ||
                (Window != booleanMatcher.Window)
            ) {
                return $"{name}: the flattened matcher and its closure disagree on shape";
            }

            // The flattening is a COPY of the closure's own arrows, never a second construction, and a state IS the
            // residual the word that reached it left behind — its acceptance is that residual's own empty-span weight.
            for (var state = 0; (state < booleanMatcher.StateCount); ++state) {
                for (var letter = 0; (letter < LetterCount); ++letter) {
                    if (booleanMatcher.Step(
                        letter: letter,
                        state: state
                    ) != ((int)booleanMatcher.Closure.Transition(
                        state: state,
                        symbol: letter
                    ))) {
                        return $"{name}: the flattened transition at ({state},{letter}) is not the closure's own arrow";
                    }
                }

                if (
                    !booleanPattern.TryWeigh(
                    value: booleanMatcher.Closure.State(state: state),
                    letters: [],
                    weight: out var residualWeight
                ) ||
                    (residualWeight != booleanMatcher.Accept(state: state))
                ) {
                    return $"{name}: state {state} accepts with {booleanMatcher.Accept(state: state)} but its residual weighs the empty span {residualWeight}";
                }
            }

            var complement = booleanPattern.Complement(value: booleanValue);

            foreach (var word in words) {
                var derivations = Oracles.WordDerivations(
                    pattern: tree,
                    word: word
                );
                var member = !derivations.IsZero;

                if (
                    !booleanMatcher.TryMatch(
                    letters: word,
                    obstruction: out _,
                    weight: out var machineWeight
                ) ||
                    (machineWeight != member)
                ) {
                    return $"{name}: the Boolean machine weighs [{Render(word: word)}] {machineWeight}, the oracle counts {derivations} derivation(s)";
                }

                if (
                    !booleanPattern.TryWeigh(
                    letters: word,
                    value: booleanValue,
                    weight: out var elementWeight
                ) ||
                    (elementWeight != member)
                ) {
                    return $"{name}: the Boolean ELEMENT weighs [{Render(word: word)}] {elementWeight}, the oracle counts {derivations} derivation(s)";
                }

                if (
                    !booleanPattern.TryWeigh(
                    letters: word,
                    value: complement,
                    weight: out var complementWeight
                ) ||
                    (complementWeight == member)
                ) {
                    return $"{name}: the complement weighs [{Render(word: word)}] {complementWeight} alongside {member}";
                }

                if (
                    !countingMatcher.TryMatch(
                    letters: word,
                    obstruction: out _,
                    weight: out var countingWeight
                ) ||
                    (countingWeight != derivations)
                ) {
                    return $"{name}: the counting machine weighs [{Render(word: word)}] {countingWeight}, the oracle counts {derivations}";
                }

                // The flattened run and the module run are the same statement: a machine adds no arithmetic.
                if (countingMatcher.Closure.Machine.Run(word: word) != derivations) {
                    return $"{name}: the counting MODULE runs [{Render(word: word)}] to {countingMatcher.Closure.Machine.Run(word: word)}, the oracle counts {derivations}";
                }

                if (
                    !countingPattern.TryWeigh(
                    letters: word,
                    value: countingValue,
                    weight: out var countingElement
                ) ||
                    (countingElement != derivations)
                ) {
                    return $"{name}: the counting ELEMENT weighs [{Render(word: word)}] {countingElement}, the oracle counts {derivations}";
                }

                // Acceptance IS the trace of the residual the run reached, so walking the table by hand agrees.
                var state = 0;
                var live = true;

                foreach (var letter in word) {
                    state = booleanMatcher.Step(
                        letter: letter,
                        state: state
                    );

                    if (state < 0) {
                        live = false;

                        break;
                    }
                }

                if (member != (live && booleanMatcher.Accept(state: state))) {
                    return $"{name}: stepping the table by hand disagrees with the run on [{Render(word: word)}]";
                }

                // The derivative is the left quotient, so weighing the derivative at the tail is weighing the value at
                // the whole word.
                if (word.Length > 0) {
                    var derivative = booleanPattern.Derivative(
                        letter: word[0],
                        value: booleanValue
                    );

                    if (
                        !booleanPattern.TryWeigh(
                        value: derivative,
                        letters: word.AsSpan(start: 1),
                        weight: out var quotientWeight
                    ) ||
                        (quotientWeight != member)
                    ) {
                        return $"{name}: the derivative by the leading letter does not weigh the tail as the value weighs the word";
                    }
                }
            }

            // A span past the window is a REFUSAL carrying its length and the window, never a false negative.
            var overlong = new int[(Window + 1)];

            if (booleanMatcher.TryMatch(
                letters: overlong,
                obstruction: out var overrun,
                weight: out _
            )) {
                return $"{name}: a span of {overlong.Length} inside a window of {Window} was decided rather than refused";
            }

            if (
                (overlong.Length != overrun.Length) ||
                (Window != overrun.Window)
            ) { return $"{name}: the overrun reports ({overrun.Length},{overrun.Window})"; }

            if (booleanPattern.TryWeigh(
                letters: overlong,
                value: booleanValue,
                weight: out _
            )) { return $"{name}: the element weighed a span past its own window"; }
        }

        // Intersection at the element and at the machine: the element-level meet is the DIAGONAL of the pair-up, and
        // the machine-level one is the genuine tensor, so the two must land on the same weights.
        var suffixA = booleanPattern.Concatenate(
            left: AnyLetter(pattern: booleanPattern),
            right: booleanPattern.Predicate(letters: 0b01UL)
        );
        var prefixA = booleanPattern.Concatenate(
            left: booleanPattern.Predicate(letters: 0b01UL),
            right: AnyLetter(pattern: booleanPattern)
        );
        var meet = booleanPattern.Intersect(
            left: suffixA,
            right: prefixA
        );

        foreach (var word in words) {
            var expected = ((2 == word.Length) && (0 == word[0]) && (0 == word[1]));

            if (
                !booleanPattern.TryWeigh(
                letters: word,
                value: meet,
                weight: out var weight
            ) ||
                (weight != expected)
            ) {
                return $"the element meet weighs [{Render(word: word)}] {weight}, expected {expected}";
            }
        }

        var letterA = booleanPattern.Predicate(letters: 0b01UL);
        var eitherLetter = AnyLetter(pattern: booleanPattern);

        if (
            !PatternMatcher<bool, BooleanMaterial>.TryCompile(
            matcher: out var singleMatcher,
            obstruction: out _,
            pattern: booleanPattern,
            stateLimit: 4,
            value: letterA
        ) ||
            !PatternMatcher<bool, BooleanMaterial>.TryCompile(
            matcher: out var anyMatcher,
            obstruction: out _,
            pattern: booleanPattern,
            stateLimit: 4,
            value: eitherLetter
        )
        ) {
            return "the one-letter matchers did not compile";
        }

        var paired = PatternMatcher<bool, BooleanMaterial>.Intersect(
            left: singleMatcher,
            right: anyMatcher
        );

        if (paired.StepCount != LetterCount) { return $"the paired machine takes {paired.StepCount} symbol(s)"; }

        foreach (var word in words) {
            if (word.Length > 2) { continue; }

            _ = singleMatcher.TryMatch(
                letters: word,
                obstruction: out _,
                weight: out var leftWeight
            );
            _ = anyMatcher.TryMatch(
                letters: word,
                obstruction: out _,
                weight: out var rightWeight
            );

            if (paired.Run(word: word) != (leftWeight && rightWeight)) {
                return $"the paired machine weighs [{Render(word: word)}] differently from the product of the two behaviors";
            }
        }

        // A state budget too small to hold the residual set REFUSES, with what it explored.
        if (PatternMatcher<bool, BooleanMaterial>.TryCompile(
            matcher: out _,
            obstruction: out var budget,
            pattern: booleanPattern,
            stateLimit: 2,
            value: suffixA
        )) {
            return "a two-state budget compiled a pattern needing more";
        }

        if (
            (budget.StatesExplored < 1L) ||
            (budget.BlockedSymbol < 0)
        ) { return $"the budget refusal reports explored={budget.StatesExplored} blocked={budget.BlockedSymbol}"; }

        return null;
    }
    /// <summary>Proves the alphabet-refinement axis partitions a token space into minterms the kernel can consume as a
    /// letter set, at a finite alphabet and at an unbounded range algebra alike.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? AlphabetRefinementPartitions() {
        // The finite axis: tokens are named, a predicate is a subset, and the partition is by shared membership.
        var finite = FiniteTokenAlphabet.Create(tokens: [10UL, 20UL, 30UL, 40UL]);

        if (4 != finite.TokenCount) { return $"the finite alphabet carries {finite.TokenCount} token(s)"; }

        var low = finite.Predicate(tokens: [10UL, 20UL]);
        var middle = finite.Predicate(tokens: [20UL, 30UL]);

        if (RefinementViolation<ulong, FiniteTokenAlphabet>(
            predicates: [low, middle],
            probes: [10UL, 20UL, 30UL, 40UL],
            refinement: finite
        ) is { } finiteDetail) {
            return $"finite alphabet: {finiteDetail}";
        }

        // The unbounded axis: a predicate is a set of ranges over the whole token space, and the same shared loop
        // partitions it.
        var ranges = default(TokenRangeAlphabet);
        var lowRange = TokenRangeSet.Create(ranges: [new TokenRange(
                First: 0UL,
                Last: 99UL
            )]);
        var highRange = TokenRangeSet.Create(ranges: [new TokenRange(
                First: 50UL,
                Last: 199UL
            ), new TokenRange(
                First: 1_000UL,
                Last: ulong.MaxValue
            )]);

        if (
            lowRange.IsEmpty ||
            highRange.IsEmpty
        ) { return "an authored range set reported itself empty"; }

        if (
            (1 != lowRange.Ranges.Length) ||
            (0UL != lowRange.Ranges[0].First) ||
            (99UL != lowRange.Ranges[0].Last)
        ) { return "the canonical form of a single range moved it"; }

        if (RefinementViolation<TokenRangeSet, TokenRangeAlphabet>(
            predicates: [lowRange, highRange],
            probes: [0UL, 49UL, 50UL, 99UL, 100UL, 199UL, 200UL, 999UL, 1_000UL, ulong.MaxValue],
            refinement: ranges
        ) is { } rangeDetail) {
            return $"range alphabet: {rangeDetail}";
        }

        if (
            !ranges.IsSatisfiable(predicate: lowRange) ||
            ranges.IsSatisfiable(predicate: ranges.Conjoin(
            left: lowRange,
            right: TokenRangeSet.Create(ranges: [new TokenRange(
                    First: 200UL,
                    Last: 300UL
                )])
        ))
        ) {
            return "satisfiability disagreed with an authored disjoint pair";
        }

        if (highRange.Ranges.Length > TokenRangeSet.MaximumRangeCount) { return "an authored range set exceeded its own run cap"; }

        for (var index = 0; (index < finite.TokenCount); ++index) {
            if (!finite.Contains(
                predicate: finite.Full,
                token: finite.Token(index: index)
            )) { return $"the finite alphabet does not contain its own token {index}"; }
        }

        // The refinement CAP is a refusal carrying −1, not a throw: m pairwise-disjoint predicates cut m + 1 blocks, so
        // one below the cap fits and the cap itself does not. The cap is also the free monoid's letter cap, which is why
        // a partition that overruns it can never become a generator set.
        var disjoint = new TokenRangeSet[AlphabetRefinement.MaximumMintermCount];

        for (var index = 0; (index < disjoint.Length); ++index) {
            disjoint[index] = TokenRangeSet.Create(ranges: [new TokenRange(
                    First: ((ulong)index),
                    Last: ((ulong)index)
                )]);
        }

        var wide = new TokenRangeSet[AlphabetRefinement.MaximumMintermCount];

        if (AlphabetRefinement.MaximumMintermCount != AlphabetRefinement.Refine<TokenRangeSet, TokenRangeAlphabet>(
            refinement: ranges,
            predicates: disjoint.AsSpan(
                length: (AlphabetRefinement.MaximumMintermCount - 1),
                start: 0
            ),
            minterms: wide
        )) {
            return $"{(AlphabetRefinement.MaximumMintermCount - 1)} disjoint predicates did not cut exactly {AlphabetRefinement.MaximumMintermCount} blocks";
        }

        if (-1 != AlphabetRefinement.Refine<TokenRangeSet, TokenRangeAlphabet>(
            minterms: wide,
            predicates: disjoint,
            refinement: ranges
        )) {
            return $"{AlphabetRefinement.MaximumMintermCount} disjoint predicates were refined rather than refused";
        }

        // The kernel never sees a predicate: the alphabet hands it a letter count and a mask, and nothing else.
        var alphabet = MintermAlphabet<TokenRangeSet, TokenRangeAlphabet>.Create(
            predicates: [lowRange, highRange],
            refinement: ranges
        );
        var pattern = TokenPattern<bool, BooleanMaterial>.Create(
            letterCount: alphabet.LetterCount,
            window: 3,
            material: default
        );
        var value = pattern.Concatenate(
            left: pattern.Predicate(letters: alphabet.LettersOf(predicate: lowRange)),
            right: pattern.Predicate(letters: alphabet.LettersOf(predicate: highRange))
        );

        if (!PatternMatcher<bool, BooleanMaterial>.TryCompile(
            alphabet: alphabet,
            matcher: out var matcher,
            obstruction: out _,
            pattern: pattern,
            stateLimit: PresentedAlgebra<bool, BooleanMaterial>.MaximumClosureStates,
            value: value
        )) {
            return "the range-alphabet pattern did not compile";
        }

        foreach (var (first, second, expected) in (((ulong First, ulong Second, bool Expected)[])[
            (10UL, 60UL, true), (10UL, 1_500UL, true), (60UL, 60UL, true), (10UL, 10UL, false), (500UL, 60UL, false), (60UL, 500UL, false)])) {
            if (!TokenMatching.TryMatch(
                alphabet: alphabet,
                matcher: matcher,
                obstruction: out _,
                tokens: [first, second],
                weight: out var weight
            )) {
                return $"the raw token span ({first},{second}) was refused";
            }

            if (weight != expected) { return $"the raw token span ({first},{second}) weighs {weight}, expected {expected}"; }
        }

        for (var letter = 0; (letter < alphabet.LetterCount); ++letter) {
            if (!ranges.IsSatisfiable(predicate: alphabet.Minterm(letter: letter))) { return $"minterm {letter} is unsatisfiable, so it should not be a letter"; }
        }

        // Classification and membership are the same statement: the letter a token lands in is the block containing it.
        foreach (var token in ((ulong[])[0UL, 49UL, 60UL, 150UL, 500UL, 1_500UL, ulong.MaxValue])) {
            if (!alphabet.TryLetterOf(
                letter: out var letter,
                token: token
            )) { return $"token {token} was classified into no letter"; }

            if (!ranges.Contains(
                predicate: alphabet.Minterm(letter: letter),
                token: token
            )) { return $"token {token} was classified into letter {letter}, which does not contain it"; }

            if (ranges.Contains(
                predicate: lowRange,
                token: token
            ) != (0UL != (alphabet.LettersOf(predicate: lowRange) & (1UL << letter)))) {
                return $"the letter mask of the low range disagrees with membership at token {token}";
            }
        }

        return null;
    }
    /// <summary>Proves exact machine equivalence against brute word enumeration, and proves the pairing-radical quotient
    /// canonical: same behavior at every word, minimal dimension, and stable under a second quotient.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MachineEquivalenceMatchesEnumeration() {
        var material = PrimeFieldMaterial.Create(modulus: 65_521UL);
        var pattern = TokenPattern<ulong, PrimeFieldMaterial>.Create(
            letterCount: 2,
            material: material,
            window: 4
        );
        var a = pattern.Predicate(letters: 0b01UL);
        var b = pattern.Predicate(letters: 0b10UL);
        var distributed = pattern.Concatenate(
            left: pattern.Union(
                left: a,
                right: b
            ),
            right: a
        );
        var expanded = pattern.Union(
            left: pattern.Concatenate(
                left: a,
                right: a
            ),
            right: pattern.Concatenate(
                left: b,
                right: a
            )
        );
        var partial = pattern.Concatenate(
            left: a,
            right: a
        );

        if (
            !PatternMatcher<ulong, PrimeFieldMaterial>.TryCompile(
            matcher: out var left,
            obstruction: out _,
            pattern: pattern,
            stateLimit: 8,
            value: distributed
        ) ||
            !PatternMatcher<ulong, PrimeFieldMaterial>.TryCompile(
            matcher: out var right,
            obstruction: out _,
            pattern: pattern,
            stateLimit: 8,
            value: expanded
        ) ||
            !PatternMatcher<ulong, PrimeFieldMaterial>.TryCompile(
            matcher: out var narrow,
            obstruction: out _,
            pattern: pattern,
            stateLimit: 8,
            value: partial
        )
        ) {
            return "a field-material matcher did not compile";
        }

        // The enumeration below decides by running the subject's own TryMatch, so it separates two decision PROCEDURES
        // and says nothing about the weights either machine gives. This does (worklist C23): the same three patterns
        // written as trees the subject does not have, their derivations counted by backtracking in BigInteger, and
        // reduced into the field — the only leg here that reads the machines against something outside them.
        var aTree = Oracles.WordPattern.Letter(letter: 0);
        var bTree = Oracles.WordPattern.Letter(letter: 1);

        foreach (var (name, matcher, tree) in (((string Name, PatternMatcher<ulong, PrimeFieldMaterial> Matcher, Oracles.WordPattern Tree)[])[
            ("(a|b)·a", left, Oracles.WordPattern.Concatenate(
                left: Oracles.WordPattern.Union(
                    left: aTree,
                    right: bTree
                ),
                right: aTree
            )),
            ("a·a | b·a", right, Oracles.WordPattern.Union(
                left: Oracles.WordPattern.Concatenate(
                    left: aTree,
                    right: aTree
                ),
                right: Oracles.WordPattern.Concatenate(
                    left: bTree,
                    right: aTree
                )
            )),
            ("a·a", narrow, Oracles.WordPattern.Concatenate(
                left: aTree,
                right: aTree
            )),
        ])) {
            foreach (var word in EnumerateWords(
                letterCount: 2,
                maximumLength: 4
            )) {
                var expected = ((ulong)(Oracles.WordDerivations(
                    pattern: tree,
                    word: word
                ) % 65_521));

                if (
                    !matcher.TryMatch(
                    letters: word,
                    obstruction: out _,
                    weight: out var weight
                ) ||
                    (weight != expected)
                ) {
                    return $"the {name} machine weighs [{string.Join(
                        separator: ",",
                        values: word
                    )}] at {weight}, where the backtracking derivation count over the field is {expected}";
                }
            }
        }

        if (EquivalenceAgreesWithEnumeration(
            expected: true,
            left: left,
            right: right
        ) is { } sameDetail) { return sameDetail; }

        if (EquivalenceAgreesWithEnumeration(
            expected: false,
            left: left,
            right: narrow
        ) is { } differDetail) { return differDetail; }

        if (!PatternMatcher<ulong, PrimeFieldMaterial>.AreEquivalent(
            left: left,
            right: narrow,
            witness: out var witness
        )) {
            if (2 != witness.Word.Length) { return $"the shortest distinguishing span has length {witness.Word.Length}, expected two"; }

            _ = left.TryMatch(
                letters: witness.Word,
                weight: out var leftWeight,
                obstruction: out _
            );
            _ = narrow.TryMatch(
                letters: witness.Word,
                weight: out var rightWeight,
                obstruction: out _
            );

            if (
                (witness.LeftValue != leftWeight) ||
                (witness.RightValue != rightWeight) ||
                (leftWeight == rightWeight)
            ) {
                return $"the witness carries ({witness.LeftValue},{witness.RightValue}) but the machines weigh ({leftWeight},{rightWeight})";
            }
        }

        // A machine carrying an unobservable direction: the reachable span has rank three and the observation span rank
        // two, so the quotient must land on two states — the reduction the duality is for.
        var quiver = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(presentation: Presentations.Quiver<ulong, PrimeFieldMaterial>(
            arrows: [],
            material: material,
            objectCount: 3
        ));
        var redundant = PresentedMachine<ulong, PrimeFieldMaterial>.Create(
            algebra: quiver,
            initial: quiver.FromSupport(
                coefficients: [1UL],
                keys: [0L]
            ),
            steps: [quiver.FromSupport(
                    coefficients: [1UL, 1UL],
                    keys: [1L, 5L]
                )],
            readout: quiver.FromSupport(
                coefficients: [1UL, 1UL],
                keys: [0L, 1L]
            )
        );

        if (
            (1 != redundant.StepCount) ||
            (1 != redundant.Initial.SupportCount) ||
            (2 != redundant.Readout.SupportCount) ||
            (9 != redundant.Algebra.MaximumSupportCount)
        ) {
            return "the hand-built machine is not the three-state one this claim describes";
        }

        var minimal = redundant.MinimizeByPairingRadical();

        if (4 != minimal.Algebra.MaximumSupportCount) {
            return $"the pairing-radical quotient landed on a quiver of {minimal.Algebra.MaximumSupportCount} cell(s), expected the two-state four";
        }

        foreach (var word in EnumerateWords(
            letterCount: 1,
            maximumLength: 6
        )) {
            if (!minimal.Run(word: word).Equals(obj: redundant.Run(word: word))) {
                return $"the minimal machine disagrees with the original on a span of {word.Length}";
            }
        }

        if (!PresentedMachine<ulong, PrimeFieldMaterial>.AreEquivalent(
            left: redundant,
            right: minimal,
            witness: out var quotientWitness
        )) {
            return $"the quotient is not equivalent to what it quotients, first at a span of {quotientWitness.Word.Length}";
        }

        var again = minimal.MinimizeByPairingRadical();

        if (again.Algebra.MaximumSupportCount != minimal.Algebra.MaximumSupportCount) {
            return $"a second quotient moved the dimension from {minimal.Algebra.MaximumSupportCount} to {again.Algebra.MaximumSupportCount}";
        }

        if (!PresentedMachine<ulong, PrimeFieldMaterial>.AreEquivalent(
            left: minimal,
            right: again,
            witness: out _
        )) { return "the quotient is not idempotent"; }

        // The step elements are ordinary algebra elements, so stepping is Multiply and the readout is Pair.
        var state = redundant.Initial;

        for (var position = 0; (position < 2); ++position) {
            state = redundant.Algebra.Multiply(
                left: state,
                right: redundant.Step(index: 0)
            );
        }

        if (!redundant.Algebra.Pair(
            covector: redundant.Readout,
            value: state
        ).Equals(obj: redundant.Run(word: [0, 0]))) {
            return "running a word is not the readout paired with the stepped state";
        }

        if (!redundant.Algebra.Behavior(
            initial: redundant.Initial,
            value: redundant.Step(index: 0),
            readout: redundant.Readout
        ).Equals(obj: redundant.Run(word: [0]))) {
            return "the behavior of one step is not the run of the one-letter word";
        }

        // The trace is the pairing with the unit, which at a quiver is the matrix trace with no special case.
        if (!quiver.Trace(value: quiver.Identity).Equals(obj: 3UL)) { return "the trace of a three-object quiver's unit is not three"; }

        return null;
    }
    /// <summary>Proves the resolvent of an absorbing chain exact by re-multiplication, and proves the iterative star
    /// refuses exactly where the resolvent answers.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ResolventRemultiplies() {
        const int Order = 3;

        var algebra = PresentedAlgebra<RealQuadratic, RationalMaterial>.Create(presentation: CodiscreteQuiver<RealQuadratic, RationalMaterial>(
            material: default,
            order: Order
        ));
        var keys = new long[(Order * Order)];
        var transitions = new RealQuadratic[(Order * Order)];
        var quarter = RealQuadratic.Rational(
            denominator: 4,
            numerator: 1
        );

        for (var cell = 0; (cell < keys.Length); ++cell) {
            keys[cell] = cell;
            transitions[cell] = quarter;
        }

        var chain = algebra.FromSupport(
            coefficients: transitions,
            keys: keys
        );

        // A substochastic chain's powers neither vanish nor stabilize, so the ITERATIVE star must refuse — and the
        // refusal names the certificate it attempted rather than inventing one.
        if (algebra.TrySumOverAllLengths(
            obstruction: out var iterative,
            total: out _,
            value: chain
        )) {
            return "the iterative star issued a certificate for a substochastic chain";
        }

        if (
            (ClosureCertificate.Nilpotent != iterative.Attempted) ||
            (0L >= iterative.StepsTaken)
        ) {
            return $"the iterative refusal reports attempted={iterative.Attempted} steps={iterative.StepsTaken}";
        }

        if (!algebra.TryResolvent(
            obstruction: out var resolventRefusal,
            resolvent: out var fundamental,
            value: chain
        )) {
            return $"the resolvent was refused (attempted {resolventRefusal.Attempted}, key {resolventRefusal.SupportKey}, rank {resolventRefusal.StepsTaken})";
        }

        var negated = new RealQuadratic[chain.SupportCount];

        for (var index = 0; (index < negated.Length); ++index) { negated[index] = -chain.Coefficients[index]; }

        var divisor = algebra.Add(
            left: algebra.Identity,
            right: algebra.FromSupport(
                keys: chain.Keys,
                coefficients: negated
            )
        );

        // The re-multiplication IS the proof: what came back is the unique two-sided inverse, not a truncation.
        if (!algebra.AreEqual(
            left: algebra.Multiply(
                left: divisor,
                right: fundamental
            ),
            right: algebra.Identity
        )) { return "(I − Q)·N is not the unit"; }

        if (!algebra.AreEqual(
            left: algebra.Multiply(
                left: fundamental,
                right: divisor
            ),
            right: algebra.Identity
        )) { return "N·(I − Q) is not the unit"; }

        // And it is the analytic continuation of the sum: the resolvent minus the truncation is exactly the tail.
        for (var bound = 0; (bound <= 6); ++bound) {
            var truncated = algebra.TruncatedSum(
                bound: bound,
                value: chain
            );
            var tail = algebra.Multiply(
                left: algebra.Power(
                    exponent: ((ulong)(bound + 1)),
                    value: chain
                ),
                right: fundamental
            );
            var difference = algebra.Subtract(
                left: fundamental,
                right: truncated
            );

            if (!algebra.AreEqual(
                left: difference,
                right: tail
            )) { return $"the resolvent minus the truncation at {bound} is not Q^{(bound + 1)}·N"; }
        }

        // Each row sums to three quarters, so every expected absorption count is four; the exact value is the point.
        for (var source = 0; (source < Order); ++source) {
            var total = RealQuadratic.Zero;

            for (var target = 0; (target < Order); ++target) { total += fundamental[((source * ((long)Order)) + target)]; }

            if (total != RealQuadratic.Rational(
                denominator: 1,
                numerator: 4
            )) { return $"the expected absorption count from state {source} is {total}, expected four"; }
        }

        // A singular divisor is a REFUSAL naming the column that found no pivot, never an exception and never an
        // approximation.
        if (algebra.TrySolve(
            divisor: algebra.FromSupport(
                keys: [0L],
                coefficients: [RealQuadratic.One]
            ),
            target: algebra.Identity,
            quotient: out _,
            obstruction: out var division
        )) {
            return "a single idempotent arrow divided the unit";
        }

        if (
            (division.BlockedKey < 0L) ||
            (division.RankReached < 0L) ||
            (division.RankReached > division.BlockedKey)
        ) {
            return $"the division refusal reports blocked={division.BlockedKey} rank={division.RankReached}";
        }

        // The exact solve where it does work: the chain's own action is invertible, so dividing back returns the unit.
        if (
            !algebra.TrySolve(
            divisor: divisor,
            obstruction: out _,
            quotient: out var one,
            target: divisor
        ) ||
            !algebra.AreEqual(
            left: one,
            right: algebra.Identity
        )
        ) {
            return "dividing a unit by itself did not return the algebra's unit";
        }

        // A refused resolvent names the certificate it attempted, and this is the only path in the library that names
        // FieldResolvent: the unit's own resolvent divides by zero, so it must refuse under that certificate.
        if (algebra.TryResolvent(
            value: algebra.Identity,
            resolvent: out _,
            obstruction: out var singularRefusal
        )) {
            return "the unit's resolvent divides by zero and must be refused";
        }

        if (ClosureCertificate.FieldResolvent != singularRefusal.Attempted) {
            return $"the refused resolvent attempted {singularRefusal.Attempted}, expected the field resolvent";
        }

        return null;
    }
    /// <summary>Proves the antidifference is the guarded star of the shift, is the exact inverse of the backward
    /// difference, and reproduces the shipped exactly-inverted prefix sums.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? AntidifferenceMatchesLayerSequence() {
        const int DegreeBound = 24;

        var calculus = FiniteCalculus<BigInteger, IntegerMaterial>.Create(
            degreeBound: DegreeBound,
            material: default
        );

        if (DegreeBound != calculus.DegreeBound) { return $"the calculus reports a degree bound of {calculus.DegreeBound}"; }

        if (!calculus.TryAntidifference(
            antidifference: out var summation,
            obstruction: out var refusal
        )) {
            return $"the antidifference was refused (attempted {refusal.Attempted}, steps {refusal.StepsTaken})";
        }

        // The complementary element is a UNIT, so ITS star is refused: which of the two is starred is what makes the
        // summation operator the one that exists.
        if (calculus.Algebra.TrySumOverAllLengths(
            value: calculus.Difference,
            total: out _,
            obstruction: out var differenceRefusal
        )) {
            return "the backward difference is a unit, so its own sum over all lengths must be refused";
        }

        if (ClosureCertificate.Nilpotent != differenceRefusal.Attempted) { return $"the refused star attempted {differenceRefusal.Attempted}"; }

        if (!calculus.Algebra.AreEqual(
            left: calculus.Algebra.Multiply(
                left: calculus.Difference,
                right: summation
            ),
            right: calculus.Algebra.Identity
        )) {
            return "the antidifference is not the exact inverse of the backward difference";
        }

        if (
            (1 != calculus.Shift.SupportCount) ||
            (1L != calculus.Shift.Keys[0])
        ) { return "the shift is not the degree-one generator"; }

        var sequences = new (string Name, LayerSequence Sequence)[] {
            ("triangular", LayerSequence.Triangular),
            ("pronic", LayerSequence.Pronic),
            ("square", LayerSequence.Square),
            ("centered-square", LayerSequence.CenteredSquare),
            ("centered-hexagonal", LayerSequence.CenteredHexagonal),
            ("polygonal(7)", LayerSequence.Polygonal(sides: 7L)),
            ("centered(5)", LayerSequence.Centered(sides: 5L)),
            ("linear(6,3)", LayerSequence.Linear(
            seed: 3L,
            size: 6L
        )),
        };

        foreach (var (name, sequence) in sequences) {
            var sizes = new BigInteger[(DegreeBound + 1)];

            for (var layer = 0; (layer <= DegreeBound); ++layer) { sizes[layer] = sequence.LayerSize(layer: layer); }

            var prefix = calculus.Algebra.Multiply(
                left: summation,
                right: calculus.Sequence(values: sizes)
            );

            for (var layer = 0; (layer <= DegreeBound); ++layer) {
                if (prefix[layer] != sequence.Count(layerCount: layer)) {
                    return $"{name}: the prefix sum at layer {layer} is {prefix[layer]}, the shipped quadratic inversion says {sequence.Count(layerCount: layer)}";
                }
            }

            // The difference undoes it, one place at a time.
            var restored = calculus.Algebra.Multiply(
                left: calculus.Difference,
                right: prefix
            );

            for (var layer = 0; (layer <= DegreeBound); ++layer) {
                if (restored[layer] != sizes[layer]) { return $"{name}: differencing the prefix sum did not restore layer {layer}"; }
            }
        }

        // The same operator against the shipped cumulative measure, which floors an affine rate at every boundary and so
        // inverts its own prefix sums by a construction sharing nothing with a convolution.
        foreach (var (numerator, denominator) in (((long Numerator, long Denominator)[])[(1L, 1L), (2L, 1L), (3L, 2L), (5L, 3L), (7L, 4L)])) {
            var measure = DiscreteMeasure.Create(
                rate: RealQuadratic.Rational(
                    denominator: denominator,
                    numerator: numerator
                ),
                offset: RealQuadratic.Zero
            );
            var steps = new BigInteger[(DegreeBound + 1)];

            for (var place = 0; (place <= DegreeBound); ++place) { steps[place] = measure.AmountAt(index: place); }

            var prefix = calculus.Algebra.Multiply(
                left: summation,
                right: calculus.Sequence(values: steps)
            );

            for (var place = 0; (place <= DegreeBound); ++place) {
                var expected = measure.Cumulative(index: (place + 1));

                if (prefix[place] != expected) { return $"rate {numerator}/{denominator}: the prefix sum at {place} is {prefix[place]}, the cumulative measure says {expected}"; }
            }
        }

        return null;
    }
    /// <summary>Proves the derived monogenic algebra over a prime-field material equal to the shipped quadratic
    /// extension field at degree two, and equal to a shared-nothing polynomial oracle above it.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeExtensionTwinsMonogenic() {
        const ulong Modulus = 1_000_003UL;

        var baseField = PrimeField64.Create(modulus: Modulus);
        var extension = QuadraticExtensionField64.CreateCanonical(baseField: baseField);
        var material = PrimeFieldMaterial.Create(modulus: Modulus);

        // x² ≡ nonSquare, so the monic tail [m₀, m₁] of x² → −m₀ − m₁·x is [−nonSquare, 0].
        var algebra = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(presentation: Presentations.Monogenic<ulong, PrimeFieldMaterial>(
            modulus: [material.Negate(value: extension.NonSquare), 0UL],
            material: material
        ));
        var rng = Pcg32XshRr.Create(
            state: 0x9E37UL,
            stream: 3UL
        );

        for (var trial = 0; (trial < 400); ++trial) {
            var a = new QuadraticExtensionField64.Element(
                A: NextField(
                    modulus: Modulus,
                    rng: ref rng
                ),
                B: NextField(
                    modulus: Modulus,
                    rng: ref rng
                )
            );
            var b = new QuadraticExtensionField64.Element(
                A: NextField(
                    modulus: Modulus,
                    rng: ref rng
                ),
                B: NextField(
                    modulus: Modulus,
                    rng: ref rng
                )
            );
            var expected = extension.Multiply(
                left: a,
                right: b
            );
            var actual = algebra.Multiply(
                left: algebra.FromSupport(
                    keys: [0L, 1L],
                    coefficients: [a.A, a.B]
                ),
                right: algebra.FromSupport(
                    keys: [0L, 1L],
                    coefficients: [b.A, b.B]
                )
            );

            if (
                (actual[0L] != expected.A) ||
                (actual[1L] != expected.B)
            ) {
                return $"({a.A},{a.B})·({b.A},{b.B}) derived ({actual[0L]},{actual[1L]}), the extension field says ({expected.A},{expected.B})";
            }

            // The shipped power schedule too: the derived Power is the same ascending-bit schedule, exactly here.
            var derivedPower = algebra.Power(
                value: algebra.FromSupport(
                    keys: [0L, 1L],
                    coefficients: [a.A, a.B]
                ),
                exponent: 13UL
            );
            var fieldPower = extension.Pow(
                exponent: 13UL,
                value: a
            );

            if (
                (derivedPower[0L] != fieldPower.A) ||
                (derivedPower[1L] != fieldPower.B)
            ) {
                return $"({a.A},{a.B})^13 derived ({derivedPower[0L]},{derivedPower[1L]}), the extension field says ({fieldPower.A},{fieldPower.B})";
            }
        }

        // Above degree two nothing in the tree constructs the field at all, so the cross-check is the shared-nothing
        // schoolbook polynomial reduction.
        foreach (var tail in new ulong[][] { [3UL, 0UL, 0UL], [1UL, 2UL, 0UL, 5UL] }) {
            var higher = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(presentation: Presentations.Monogenic<ulong, PrimeFieldMaterial>(
                material: material,
                modulus: tail
            ));
            var degree = tail.Length;
            var keys = new long[degree];
            var left = new ulong[degree];
            var right = new ulong[degree];
            var oracle = new ulong[degree];

            for (var exponent = 0; (exponent < degree); ++exponent) { keys[exponent] = exponent; }

            for (var trial = 0; (trial < 200); ++trial) {
                for (var exponent = 0; (exponent < degree); ++exponent) {
                    left[exponent] = NextField(
                        modulus: Modulus,
                        rng: ref rng
                    );
                    right[exponent] = NextField(
                        modulus: Modulus,
                        rng: ref rng
                    );
                }

                Oracles.PrimeFieldPolynomialProduct(
                    left: left,
                    modulus: Modulus,
                    result: oracle,
                    right: right,
                    tail: tail
                );

                var product = higher.Multiply(
                    left: higher.FromSupport(
                        coefficients: left,
                        keys: keys
                    ),
                    right: higher.FromSupport(
                        coefficients: right,
                        keys: keys
                    )
                );

                for (var exponent = 0; (exponent < degree); ++exponent) {
                    if (product[exponent] != oracle[exponent]) {
                        return $"degree {degree}: coefficient {exponent} is {product[exponent]}, the polynomial oracle says {oracle[exponent]}";
                    }
                }
            }
        }

        return null;
    }
    /// <summary>Proves the companion quiver reproduces the shipped projective steps, at degree two through the quadratic
    /// twin and above it through the monogenic one.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CompanionQuiverTwinsProjectiveStep() {
        var rng = Pcg32XshRr.Create(
            state: 0xC0FFEEUL,
            stream: 5UL
        );

        // Degree two: the companion matrix step IS the Möbius step, so the two agree over the whole raw range.
        foreach (var (pRaw, qRaw) in (((long P, long Q)[])[(65_536L, 65_536L), (0L, -65_536L), (131_072L, 65_536L)])) {
            var quadratic = QuadraticAlgebra<FixedQ4816>.Create(
                p: Raw(value: pRaw),
                q: Raw(value: qRaw)
            );
            var step = PresentedCompanionMobius(
                pRaw: pRaw,
                qRaw: qRaw
            );

            for (var trial = 0; (trial < 400); ++trial) {
                var n = NextRaw(rng: ref rng);
                var d = NextRaw(rng: ref rng);
                var expected = quadratic.MobiusStep(pair: new(
                    Numerator: Raw(value: n),
                    Denominator: Raw(value: d)
                ));

                var (numerator, denominator) = step(
                    n,
                    d
                );

                if (
                    (numerator != expected.Numerator.Value) ||
                    (denominator != expected.Denominator.Value)
                ) {
                    return $"companion step of ({n},{d}) at ({pRaw},{qRaw}) is ({numerator},{denominator}), MobiusStep says ({expected.Numerator.Value},{expected.Denominator.Value})";
                }
            }
        }

        // Degree three: the companion quiver on three objects against MonogenicAlgebra's own projective window.
        var tail = new[] { Raw(value: -65_536L), Raw(value: 131_072L), Raw(value: -65_536L) };
        var monogenic = MonogenicAlgebra<FixedQ4816>.Create(monicModulus: tail);
        var algebra = PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(presentation: CodiscreteQuiver<FixedQ4816, FixedMaterial>(
            material: default,
            order: 3
        ));
        var material = default(FixedMaterial);
        var companionKeys = new long[5];
        var companionValues = new FixedQ4816[5];

        // The companion of xⁿ + m_{n−1}x^{n−1} + … + m₀ acting on the window [v₀, v₁, v₂]: the top row carries −m,
        // reversed, and the subdiagonal shifts.
        companionKeys[0] = 0L;
        companionValues[0] = material.Negate(value: tail[2]);
        companionKeys[1] = 1L;
        companionValues[1] = material.Negate(value: tail[1]);
        companionKeys[2] = 2L;
        companionValues[2] = material.Negate(value: tail[0]);
        companionKeys[3] = 3L;
        companionValues[3] = FixedQ4816.One;
        companionKeys[4] = 7L;
        companionValues[4] = FixedQ4816.One;

        var companion = algebra.FromSupport(
            coefficients: companionValues,
            keys: companionKeys
        );

        for (var trial = 0; (trial < 400); ++trial) {
            var window = new[] { Raw(value: NextRaw(rng: ref rng)), Raw(value: NextRaw(rng: ref rng)), Raw(value: NextRaw(rng: ref rng)) };
            var expected = monogenic.ProjectiveStep(window: monogenic.FromWindow(window: window));
            var stepped = algebra.Multiply(
                left: companion,
                right: algebra.FromSupport(
                    coefficients: window,
                    keys: [0L, 3L, 6L]
                )
            );

            for (var place = 0; (place < 3); ++place) {
                if (stepped[(place * 3L)].Value != expected[place].Value) {
                    return $"the degree-three companion step differs at place {place}: {stepped[(place * 3L)].Value} versus {expected[place].Value}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves the top-grade coefficient of a triple outer product equal to an independent determinant, and
    /// proves the non-metric complement's own equations and incidences.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? OrientationTwinsDeterminant() {
        var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: 0,
            material: default,
            negativeCount: 0,
            positiveCount: 3
        ));
        var complement = GradedComplement<BigInteger, IntegerMaterial>.Create(algebra: algebra);
        var presentation = algebra.Presentation;
        var vectorKey = new long[3];
        var topKey = -1L;

        if (!ReferenceEquals(
            objA: complement.Algebra,
            objB: algebra
        )) { return "the complement does not carry the algebra it was built from"; }

        for (var key = 0; (key < presentation.NormalFormCount); ++key) {
            var word = presentation.NormalFormWord(key: key);

            if (1 == word.Length) { vectorKey[word[0]] = key; }

            if (3 == word.Length) { topKey = key; }
        }

        if (
            (1 != complement.Pseudoscalar.SupportCount) ||
            (complement.Pseudoscalar.Keys[0] != topKey)
        ) { return "the pseudoscalar is not the top-grade basis blade"; }

        var rng = Pcg32XshRr.Create(
            state: 0xDE7EC7UL,
            stream: 7UL
        );
        var rows = new BigInteger[9];

        for (var trial = 0; (trial < 300); ++trial) {
            var vectors = new PresentedAlgebra<BigInteger, IntegerMaterial>.Element[3];

            for (var row = 0; (row < 3); ++row) {
                var coefficients = new BigInteger[3];

                for (var column = 0; (column < 3); ++column) {
                    var entry = (((long)rng.NextUInt32(
                        maximum: 120U,
                        minimum: 0U
                    )) - 60L);

                    coefficients[column] = entry;
                    rows[((row * 3) + column)] = entry;
                }

                vectors[row] = algebra.FromSupport(
                    coefficients: coefficients,
                    keys: vectorKey
                );
            }

            var wedge = complement.OuterProduct(
                left: complement.OuterProduct(
                    left: vectors[0],
                    right: vectors[1]
                ),
                right: vectors[2]
            );
            var determinant = Oracles.Determinant3(rows: rows);

            if (wedge[topKey] != determinant) { return $"the top-grade coefficient is {wedge[topKey]}, the determinant oracle says {determinant}"; }

            // Orientation is the SIGN of that coefficient, so a repeated vector must join to exactly zero.
            var degenerate = complement.OuterProduct(
                left: complement.OuterProduct(
                    left: vectors[0],
                    right: vectors[1]
                ),
                right: vectors[0]
            );

            if (0 != degenerate.SupportCount) { return "a repeated vector joined to a non-zero blade"; }
        }

        // The complement's own equations, on every basis blade: each is charged so that its defining join holds.
        for (var key = 0; (key < presentation.NormalFormCount); ++key) {
            var basis = algebra.FromSupport(
                keys: [key],
                coefficients: [BigInteger.One]
            );
            var rightJoin = complement.OuterProduct(
                left: basis,
                right: complement.RightComplement(value: basis)
            );
            var leftJoin = complement.OuterProduct(
                left: complement.LeftComplement(value: basis),
                right: basis
            );

            if (!complement.Algebra.AreEqual(
                left: rightJoin,
                right: complement.Pseudoscalar
            )) { return $"key {key}: x ∧ rightComplement(x) is not the pseudoscalar"; }

            if (!complement.Algebra.AreEqual(
                left: leftJoin,
                right: complement.Pseudoscalar
            )) { return $"key {key}: leftComplement(x) ∧ x is not the pseudoscalar"; }

            if (!complement.Algebra.AreEqual(
                left: complement.LeftComplement(value: complement.RightComplement(value: basis)),
                right: basis
            )) {
                return $"key {key}: the two complements are not mutual inverses";
            }

            var grade = presentation.NormalFormWord(key: key).Length;
            var twice = complement.RightComplement(value: complement.RightComplement(value: basis));
            var sign = ((0 == ((grade * (3 - grade)) & 1))
                ? BigInteger.One
                : BigInteger.MinusOne
            );

            if (
                (1 != twice.SupportCount) ||
                (twice.Keys[0] != key) ||
                (twice.Coefficients[0] != sign)
            ) {
                return $"key {key}: the double right complement is not (−1)^(g·(n−g)) times the argument";
            }
        }

        // The meet is the join read through the complement, and it is non-metric: two planes meet in their shared line.
        var planeKey = new long[3];

        for (var key = 0; (key < presentation.NormalFormCount); ++key) {
            var word = presentation.NormalFormWord(key: key);

            if (2 != word.Length) { continue; }

            planeKey[(3 - (word[0] + word[1]))] = key;
        }

        var meet = complement.RegressiveProduct(
            left: algebra.FromSupport(
                keys: [planeKey[2]],
                coefficients: [BigInteger.One]
            ),
            right: algebra.FromSupport(
                keys: [planeKey[1]],
                coefficients: [BigInteger.One]
            )
        );

        if (
            (1 != meet.SupportCount) ||
            (meet.Keys[0] != vectorKey[0]) ||
            (BigInteger.Abs(value: meet.Coefficients[0]) != BigInteger.One)
        ) {
            return "the meet of the two planes carrying generator zero is not that generator's line";
        }

        return null;
    }
    /// <summary>Proves the transfer functor's convergent recurrence equal to a shared-nothing rational evaluation, and
    /// proves running it as a module equal to evaluating it as a product.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? TransferTwinsConvergents() {
        var transfer = ConvergentTransfer<BigInteger, IntegerMaterial>.Create(material: default);

        if (4 != transfer.Algebra.MaximumSupportCount) { return $"the transfer algebra carries {transfer.Algebra.MaximumSupportCount} cell(s), expected the codiscrete four"; }

        var rng = Pcg32XshRr.Create(
            state: 0xCF12UL,
            stream: 9UL
        );

        for (var trial = 0; (trial < 120); ++trial) {
            var length = ((int)rng.NextUInt32(
                maximum: 12U,
                minimum: 1U
            ));
            var quotients = new BigInteger[length];

            for (var index = 0; (index < length); ++index) {
                quotients[index] = (1L + rng.NextUInt32(
                maximum: 8U,
                minimum: 0U
            ));
            }

            // The shared-nothing convergent recurrence: h_k = a_k·h_{k−1} + h_{k−2}, seeded (1, 0) and (0, 1).
            var numerator = (BigInteger.One, BigInteger.Zero);
            var denominator = (BigInteger.Zero, BigInteger.One);

            foreach (var quotient in quotients) {
                numerator = (((quotient * numerator.Item1) + numerator.Item2), numerator.Item1);
                denominator = (((quotient * denominator.Item1) + denominator.Item2), denominator.Item1);
            }

            var evaluated = transfer.Evaluate(partialQuotients: quotients);

            if (
                (transfer.Entry(
                column: 0,
                row: 0,
                value: evaluated
            ) != numerator.Item1) ||
                (transfer.Entry(
                column: 1,
                row: 0,
                value: evaluated
            ) != numerator.Item2) ||
                (transfer.Entry(
                column: 0,
                row: 1,
                value: evaluated
            ) != denominator.Item1) ||
                (transfer.Entry(
                column: 1,
                row: 1,
                value: evaluated
            ) != denominator.Item2)
            ) {
                return $"the transfer product of {length} quotient(s) is not the convergent recurrence";
            }

            for (var row = 0; (row < 2); ++row) {
                for (var column = 0; (column < 2); ++column) {
                    if (transfer.Run(
                        column: column,
                        partialQuotients: quotients,
                        row: row
                    ) != transfer.Entry(
                        column: column,
                        row: row,
                        value: evaluated
                    )) {
                        return $"running the word as a module disagrees with evaluating it as a product at ({row},{column})";
                    }
                }
            }
        }

        var digit = transfer.Digit(partialQuotient: 7);

        if (
            (transfer.Entry(
            column: 0,
            row: 0,
            value: digit
        ) != 7) ||
            (transfer.Entry(
            column: 1,
            row: 0,
            value: digit
        ) != BigInteger.One) ||
            (transfer.Entry(
            column: 0,
            row: 1,
            value: digit
        ) != BigInteger.One) ||
            (!transfer.Entry(
            column: 1,
            row: 1,
            value: digit
        ).IsZero)
        ) {
            return "the digit element is not [[a, 1], [1, 0]]";
        }

        if (!transfer.Algebra.AreEqual(
            left: transfer.Evaluate(partialQuotients: []),
            right: transfer.Algebra.Identity
        )) { return "the empty word does not evaluate to the unit"; }

        return null;
    }
    // ---- the continued-fraction lenses: the certified sequence and the quasicrystal chain ----

    /// <summary>Proves the equidistribution certificate equal to the largest partial quotient of the generator's
    /// continued fraction, against a shared-nothing <see cref="BigInteger"/> walk of that fraction.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CertificateMatchesPartialQuotients() {
        (long P, long Q, long D, long R)[] cases = [
            (1L, 1L, 5L, 2L),      // the golden ratio, [1; (1)] — the Hurwitz-optimal certificate one
            (1L, 1L, 2L, 1L),      // the silver ratio, [2; (2)]
            (0L, 1L, 2L, 1L),      // √2 = [1; (2)]
            (0L, 1L, 13L, 1L),     // √13 = [3; (1, 1, 1, 1, 6)]
            (0L, 1L, 19L, 1L),     // √19 = [4; (2, 1, 3, 1, 2, 8)]
            (0L, 1L, 50L, 1L),     // √50 = [7; (14)]
            (0L, 1L, 2501L, 1L),   // √2501 = [50; (100)] — a deliberately badly-certified generator
            (201L, 1L, 5L, 2L),    // 100 + φ: a large INTEGER part must not inflate the certificate
            (3L, 1L, 11L, 1L),     // a non-unit numerator over a multi-term period
        ];

        foreach (var (p, q, d, r) in cases) {
            var certificate = CertifiedLowDiscrepancy.FromQuadraticIrrational(
                d: d,
                p: p,
                q: q,
                r: r
            ).Certificate;
            var walked = Oracles.MaximumPartialQuotient(
                d: d,
                p: p,
                q: q,
                r: r
            );

            if (certificate != walked) {
                return $"the certificate of ({p} + {q}√{d})/{r} is {certificate}, the walked maximum partial quotient {walked}";
            }
        }

        // Every metallic mean is the all-n expansion, so its certificate is exactly n — the badly-approximable family the
        // quasicrystal chains ride. Both the subject and the independent walk must say so.
        for (var n = 1; (n <= 8); ++n) {
            var certificate = CertifiedLowDiscrepancy.MetallicMean(n: n).Certificate;
            var walked = Oracles.MaximumPartialQuotient(
                d: ((((long)n) * n) + 4L),
                p: n,
                q: 1L,
                r: 2L
            );

            if (
                (certificate != n) ||
                (walked != n)
            ) {
                return $"the metallic mean {n} certifies at {certificate}, walked {walked}, expected {n}";
            }
        }

        return null;
    }
    /// <summary>Proves the chain's ring-coordinate random access to be the same tiling the substitution word streams:
    /// the walk stays inside the acceptance window, inverts, steps by one tile vector, advances, agrees with
    /// <c>Contains</c> over a covered coordinate box, and reads out as a factor of the streamed word.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ChainWalkMatchesStreamedWord() {
        (long P, long Q, long D, long R)[] cases = [
            (1L, 1L, 5L, 2L),     // the golden period [1] — the single-term case the metallic family also covers
            (0L, 1L, 3L, 1L),     // √3 = [1; (1, 2)]
            (0L, 1L, 13L, 1L),    // √13 = [3; (1, 1, 1, 1, 6)]
        ];
        const long Box = 24L;
        const int WalkLength = 1200;
        var streamed = new bool[8000];
        var walk = new bool[WalkLength];

        foreach (var (p, q, d, r) in cases) {
            var chain = QuadraticQuasicrystal.Chain.FromQuadraticIrrational(
                d: d,
                p: p,
                q: q,
                r: r
            );

            if (!chain.Contains(
                a: 0L,
                b: 0L
            )) { return $"the origin is not a vertex of the chain of ({p} + {q}√{d})/{r}"; }

            var point = (A: 0L, B: 0L);
            var visited = new HashSet<(long A, long B)>();

            for (var step = 0; (step < WalkLength); ++step) {
                _ = visited.Add(item: point);

                var isLong = chain.StartsLongTile(
                    a: point.A,
                    b: point.B
                );
                var next = chain.Next(
                    a: point.A,
                    b: point.B
                );

                walk[step] = isLong;

                if (!chain.Contains(
                    a: next.A,
                    b: next.B
                )) { return $"the chain walk of √{d} left the acceptance window at step {step}"; }
                if (chain.Previous(
                    a: next.A,
                    b: next.B
                ) != point) { return $"Previous does not invert Next on the chain of √{d} at step {step}"; }
                if (((next.A - point.A), (next.B - point.B)) != (isLong
                    ? (1L, 0L)
                    : (0L, 1L))) {
                    return $"the chain step of √{d} is neither the long nor the short tile vector at step {step}";
                }
                if (chain.Position(
                    a: next.A,
                    b: next.B
                ) <= chain.Position(
                    a: point.A,
                    b: point.B
                )) {
                    return $"the chain positions of √{d} do not increase at step {step}";
                }

                point = next;
            }

            // Contains must equal the walked vertex set over a box the monotone walk has fully passed: a mis-sized
            // acceptance window would survive the walk and still admit ghost points beside it.
            if (
                (point.A <= Box) ||
                (point.B <= Box)
            ) { return $"the chain walk of √{d} did not cover the coordinate box"; }

            for (var a = 0L; (a <= Box); ++a) {
                for (var b = 0L; (b <= Box); ++b) {
                    if (chain.Contains(
                        a: a,
                        b: b
                    ) != visited.Contains(item: (a, b))) {
                        return $"Contains disagrees with the walked vertex set of √{d} at ({a},{b})";
                    }
                }
            }

            // The streamed substitution word is the independent implementation of the same tiling; the phase of the walk
            // is not part of the claim, so the witness is factorhood rather than equality.
            QuadraticQuasicrystal.Word(
                d: d,
                p: p,
                q: q,
                r: r,
                tiles: streamed
            );

            if (!IsFactor(
                haystack: streamed,
                needle: walk
            )) {
                return $"the chain walk word of √{d} is not a factor of the streamed substitution word";
            }
        }

        return null;
    }

    /// <summary>The lane width both statements of the pair-up theorem run at: two lanes each for the two factors'
    /// states, steps and readouts.</summary>
    public const int TensorLaneWidth = 6;

}
