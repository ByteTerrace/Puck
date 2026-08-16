namespace Puck.Maths;

/// <summary>
/// A refined alphabet: the minterm partition of a predicate set, numbered as the letters of a presentation. It is the
/// only place the refinement axis and the presented kernel touch, and the whole of what crosses is a letter count.
/// </summary>
/// <typeparam name="TPredicate">The predicate form.</typeparam>
/// <typeparam name="TRefinement">The predicate algebra.</typeparam>
/// <remarks>Immutable and eager: the partition is computed once, in <see cref="Create"/>, so a classification cannot
/// build state lazily and nothing can race.</remarks>
public sealed class MintermAlphabet<TPredicate, TRefinement>
    where TRefinement : struct, IAlphabetRefinement<TPredicate> {
    private readonly TPredicate[] m_minterms;
    private readonly TRefinement m_refinement;

    private MintermAlphabet(TRefinement refinement, TPredicate[] minterms) {
        LetterCount = minterms.Length;
        m_minterms = minterms;
        m_refinement = refinement;
    }

    /// <summary>Gets the number of letters, which is the number of blocks in the partition.</summary>
    public int LetterCount { get; }

    /// <summary>Refines a predicate set into a letter alphabet.</summary>
    /// <param name="refinement">The predicate algebra.</param>
    /// <param name="predicates">The predicates the patterns will name.</param>
    /// <returns>The described alphabet.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The partition would exceed
    /// <see cref="AlphabetRefinement.MaximumMintermCount"/>.</exception>
    /// <remarks>The block of tokens satisfying no listed predicate is a letter like any other, which is what lets a
    /// complemented pattern match outside every named predicate rather than silently stopping at the named ones.</remarks>
    public static MintermAlphabet<TPredicate, TRefinement> Create(TRefinement refinement, ReadOnlySpan<TPredicate> predicates) {
        var minterms = new TPredicate[AlphabetRefinement.MaximumMintermCount];
        var count = refinement.Minterms(
            minterms: minterms,
            predicates: predicates
        );

        if (count < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(predicates),
                message: "The minterm partition of that predicate set exceeds the letter cap."
            );
        }

        return new(
            refinement: refinement,
            minterms: minterms.AsSpan(
                length: count,
                start: 0
            ).ToArray()
        );
    }
    /// <summary>Returns the letters a predicate accepts, as a mask over letter numbers.</summary>
    /// <param name="predicate">The predicate; it must be one this alphabet was refined against, or a union of blocks
    /// of the partition, since only those are exactly covered.</param>
    /// <returns>The mask of letters the predicate accepts.</returns>
    /// <exception cref="ArgumentException">A letter is split by the predicate — some of the block's tokens satisfy it
    /// and some do not — so no mask over this alphabet's letters names the predicate.</exception>
    /// <remarks>A letter is the smallest thing a mask can name, so the requirement in the parameter's own description is
    /// checked rather than assumed: a block that merely intersects the predicate would, if returned, hand back a letter
    /// that also accepts tokens the predicate rejects, and the pattern built from it would silently match more than it
    /// was asked for. Refine the alphabet against the predicate and the split disappears by construction.</remarks>
    public ulong LettersOf(TPredicate predicate) {
        var rejected = m_refinement.Complement(predicate: predicate);
        var mask = 0UL;

        for (var letter = 0; (letter < LetterCount); ++letter) {
            var block = m_minterms[letter];

            if (!m_refinement.IsSatisfiable(predicate: m_refinement.Conjoin(
                left: block,
                right: predicate
            ))) { continue; }

            if (m_refinement.IsSatisfiable(predicate: m_refinement.Conjoin(
                left: block,
                right: rejected
            ))) {
                throw new ArgumentException(
                    message: $"Letter {letter} is split by the predicate {predicate}: the block accepts tokens the predicate rejects, so no letter mask names that predicate. A predicate must be a union of whole letters.",
                    paramName: nameof(predicate)
                );
            }

            mask |= (1UL << letter);
        }

        return mask;
    }
    /// <summary>Returns one letter's block.</summary>
    /// <param name="letter">The letter number.</param>
    /// <returns>The predicate the block accepts.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The letter number is outside <see cref="LetterCount"/>.</exception>
    public TPredicate Minterm(int letter) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: letter);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: letter,
            other: LetterCount
        );

        return m_minterms[letter];
    }
    /// <summary>Classifies one token into its letter.</summary>
    /// <param name="token">The token.</param>
    /// <param name="letter">On success, the letter the token falls in.</param>
    /// <returns><see langword="true"/> when some block accepts the token; otherwise <see langword="false"/>.</returns>
    /// <remarks>Allocation-free: a membership run calls this once per token and it walks the blocks in letter order,
    /// which are disjoint, so the first accepting block is the answer.</remarks>
    public bool TryLetterOf(ulong token, out int letter) {
        for (var index = 0; (index < LetterCount); ++index) {
            if (m_refinement.Contains(
                predicate: m_minterms[index],
                token: token
            )) {
                letter = index;

                return true;
            }
        }

        letter = -1;

        return false;
    }
}
/// <summary>
/// The pattern surface: a language IS an element of the free monoid on the alphabet's letters, so union is the
/// algebra's sum, concatenation is its product, and iteration is its guarded sum over all lengths. There is no pattern
/// tree, no pattern node type, and no second arithmetic.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// <b>The derivative is not implemented here.</b> Differentiating a pattern by a letter is
/// <see cref="PresentedAlgebra{TValue, TOps}.Residual"/> at <see cref="ResidualTwist.Counit"/> and literally nothing
/// else — <see cref="Derivative"/> is a one-line delegation, and compiling a pattern to a machine routes through
/// <see cref="PresentedAlgebra{TValue, TOps}.TryCompileClosure"/>, which calls the same operator. The classical rules
/// for the derivative of a union, a concatenation and an iteration hold here as theorems about the shared operator
/// applied to the materialized element; none of them is a code path.
/// </para>
/// <para>
/// <b>The window is the boundary, and it is load-bearing.</b> A positive <see cref="Window"/> bounds the word length,
/// which makes the normal-form set finite: that is what licenses iteration (the star terminates because the window
/// annihilates long words) and complementation (there is a finite basis to complement against), and it is why both are
/// exact only up to that length. A zero window leaves the monoid free: words of any length are represented exactly,
/// derivatives are the true left quotients with no truncation, and iteration and complementation are unavailable
/// because an infinite language is not a finite support. The two regimes are one parameter, not two types.
/// </para>
/// <para>
/// <b>Or better, by material.</b> Over <see cref="BooleanMaterial"/> a coefficient is membership; over
/// <see cref="CountingMaterial"/> it is the number of ways the pattern matches the word, which is its ambiguity
/// degree; over <see cref="TropicalMaterial"/> it is the best cost; over <see cref="MostLikelyPathMaterial"/> it is the
/// likelihood of the best parse, and over <see cref="FuzzyMaterial"/> the degree to which the word belongs. The pattern
/// code is identical at all of them, because the only thing that changed is the material type argument.
/// </para>
/// <para>Not thread-safe, because <see cref="PresentedAlgebra{TValue, TOps}"/> is not; give each thread its own.</para>
/// </remarks>
public sealed class TokenPattern<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private readonly TOps m_material;

    private TokenPattern(PresentedAlgebra<TValue, TOps> algebra, int letterCount, int window) {
        Algebra = algebra;
        EmptyWord = algebra.Identity;
        LetterCount = letterCount;
        Window = window;
        m_material = algebra.Presentation.Material;
    }

    /// <summary>Gets the algebra whose elements the patterns are.</summary>
    public PresentedAlgebra<TValue, TOps> Algebra { get; }
    /// <summary>Gets the pattern matching exactly the empty token span — the algebra's unit.</summary>
    public PresentedAlgebra<TValue, TOps>.Element EmptyWord { get; }
    /// <summary>Gets the number of letters, which is the alphabet's minterm count.</summary>
    public int LetterCount { get; }
    /// <summary>Gets the longest token span the patterns represent, or zero when the monoid is left free.</summary>
    public int Window { get; }

    /// <summary>Concatenates two patterns.</summary>
    /// <param name="left">The pattern matched first.</param>
    /// <param name="right">The pattern matched after it.</param>
    /// <returns>The pattern matching a span split into a left match followed by a right match — the algebra's product,
    /// which over a counting material sums the ways to split and over a tropical one takes the best split.</returns>
    /// <exception cref="ArgumentException">An operand belongs to another pattern algebra.</exception>
    public PresentedAlgebra<TValue, TOps>.Element Concatenate(in PresentedAlgebra<TValue, TOps>.Element left, in PresentedAlgebra<TValue, TOps>.Element right) {
        Algebra.RequireOwned(
            value: left,
            paramName: nameof(left)
        );
        Algebra.RequireOwned(
            value: right,
            paramName: nameof(right)
        );

        return Algebra.Multiply(
            left: left,
            right: right
        );
    }
    /// <summary>Creates the pattern surface of a letter alphabet.</summary>
    /// <param name="letterCount">The number of letters, at most sixty-four.</param>
    /// <param name="window">The longest token span to represent, or zero to leave the monoid free.</param>
    /// <param name="material">The material.</param>
    /// <returns>The described surface.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="letterCount"/> is negative or above sixty-four, or
    /// <paramref name="window"/> is negative.</exception>
    /// <remarks>A window admitting more words than the presentation's normal-form cap silently leaves the dense path,
    /// which costs iteration and complementation; the letter count and the window together are what decide that, and
    /// <see cref="PresentedAlgebra{TValue, TOps}.MaximumSupportCount"/> reports which side it landed on.</remarks>
    public static TokenPattern<TValue, TOps> Create(int letterCount, int window, TOps material) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: window);

        return new(
            algebra: PresentedAlgebra<TValue, TOps>.Create(presentation: Presentations.FreeMonoid<TValue, TOps>(
                letterCount: letterCount,
                material: material,
                windowDegree: window
            )),
            letterCount: letterCount,
            window: window
        );
    }
    /// <summary>Differentiates a pattern by one letter.</summary>
    /// <param name="letter">The letter to differentiate by.</param>
    /// <param name="value">The pattern.</param>
    /// <returns>The pattern matching exactly the spans that complete a match after that letter.</returns>
    /// <exception cref="ArgumentException">The pattern belongs to another pattern algebra.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The letter names no generator.</exception>
    /// <remarks>The shared residual operator at the counit twist and nothing else. The counit kills every non-empty
    /// prefix, which is exactly what collapses the twisted Leibniz sum to the leading occurrence and makes the residual
    /// the left quotient.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element Derivative(int letter, in PresentedAlgebra<TValue, TOps>.Element value) {
        Algebra.RequireOwned(
            value: value,
            paramName: nameof(value)
        );

        return Algebra.Residual(
            symbol: letter,
            value: value,
            twist: ResidualTwist.Counit
        );
    }
    /// <summary>Intersects two patterns.</summary>
    /// <param name="left">The first pattern.</param>
    /// <param name="right">The second pattern.</param>
    /// <returns>The pattern matching a span exactly when both do, its coefficients the products of theirs.</returns>
    /// <exception cref="ArgumentException">An operand belongs to another pattern algebra.</exception>
    /// <remarks>
    /// This is the diagonal of the pair-up: the tensor of two algebras carries the cell <c>(i, j)</c> at key
    /// <c>i·n + j</c>, and the intersection of two elements of the same algebra is the part of their pair-up sitting on
    /// <c>i = j</c>. It is computed here rather than through
    /// <see cref="PresentedAlgebra{TValue, TOps}.PairUp"/> because the tensor of this presentation with itself needs
    /// one generator per key pair and so exceeds the presentation cap for every useful window;
    /// <see cref="PatternMatcher{TValue, TOps}.Intersect"/> is the same construction at the machine, where the state
    /// counts are small enough for the genuine pair-up.
    /// </remarks>
    public PresentedAlgebra<TValue, TOps>.Element Intersect(in PresentedAlgebra<TValue, TOps>.Element left, in PresentedAlgebra<TValue, TOps>.Element right) {
        Algebra.RequireOwned(
            value: left,
            paramName: nameof(left)
        );
        Algebra.RequireOwned(
            value: right,
            paramName: nameof(right)
        );

        var leftKeys = left.Keys;
        var material = m_material;
        var rightKeys = right.Keys;
        var coefficients = new List<TValue>();
        var keys = new List<long>();
        var leftIndex = 0;
        var rightIndex = 0;

        while (
            (leftIndex < leftKeys.Length) &&
            (rightIndex < rightKeys.Length)
        ) {
            var leftKey = leftKeys[leftIndex];
            var rightKey = rightKeys[rightIndex];

            if (leftKey < rightKey) {
                ++leftIndex;
            } else if (rightKey < leftKey) {
                ++rightIndex;
            } else {
                var value = material.Multiply(
                    left: left.Coefficients[leftIndex++],
                    right: right.Coefficients[rightIndex++]
                );

                if (material.IsZero(value: value)) { continue; }

                coefficients.Add(item: value);
                keys.Add(item: leftKey);
            }
        }

        return Algebra.FromSupport(
            keys: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: keys),
            coefficients: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: coefficients)
        );
    }
    /// <summary>Returns the pattern matching one token, given the letters its predicate accepts.</summary>
    /// <param name="letters">The mask of letters the predicate accepts, from
    /// <see cref="MintermAlphabet{TPredicate, TRefinement}.LettersOf"/>.</param>
    /// <returns>The pattern matching exactly a one-token span whose token falls in one of those letters.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The mask names a letter outside <see cref="LetterCount"/>.</exception>
    /// <remarks>The predicate leaf, and the only constructor the refinement axis reaches: the kernel receives a mask of
    /// letter numbers and never sees a predicate.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element Predicate(ulong letters) {
        if (
            (64 != LetterCount) &&
            (0UL != (letters >>> LetterCount))
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(letters),
                message: "The mask names a letter this alphabet does not carry."
            );
        }

        var value = Algebra.Zero;

        for (var letter = 0; (letter < LetterCount); ++letter) {
            if (0UL == (letters & (1UL << letter))) { continue; }

            value = Algebra.Add(
                left: value,
                right: Algebra.Generator(symbol: letter)
            );
        }

        return value;
    }
    /// <summary>Weights a pattern by a material value.</summary>
    /// <param name="value">The pattern.</param>
    /// <param name="weight">The weight.</param>
    /// <returns>The pattern with every coefficient scaled — a cost over a tropical material, a multiplicity over a
    /// counting one, and a gate over a Boolean one.</returns>
    /// <exception cref="ArgumentException">The pattern belongs to another pattern algebra.</exception>
    /// <remarks>It is the product with the weighted unit, so it runs through the same kernel and rounds once per
    /// returned coefficient exactly as every other product does.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element Scale(in PresentedAlgebra<TValue, TOps>.Element value, TValue weight) {
        Algebra.RequireOwned(
            value: value,
            paramName: nameof(value)
        );

        var unit = Algebra.Identity;
        var charges = new TValue[unit.SupportCount];

        for (var index = 0; (index < charges.Length); ++index) {
            charges[index] = m_material.Multiply(
                left: unit.Coefficients[index],
                right: weight
            );
        }

        return Algebra.Multiply(
            left: Algebra.FromSupport(
                keys: unit.Keys,
                coefficients: charges
            ),
            right: value
        );
    }
    /// <summary>Attempts to iterate a pattern — the sum of every number of repetitions, the empty span included.</summary>
    /// <param name="value">The pattern to iterate.</param>
    /// <param name="iterated">On success, the iterated pattern.</param>
    /// <param name="obstruction">On failure, the certificate attempted and where the attempt stopped.</param>
    /// <returns><see langword="true"/> when a closure certificate was issued; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">The pattern belongs to another pattern algebra.</exception>
    /// <remarks>
    /// <para>
    /// The shared guarded sum over all lengths, with a computed certificate rather than an assumed one. Under a window
    /// it usually succeeds: an idempotent material stabilizes, and a non-idempotent one is annihilated once the
    /// repetition outgrows the window, so the sum is <see cref="ClosureCertificate.Nilpotent"/> there. It is refused on
    /// a free monoid, where an iterated pattern has infinite support and is not an element at all — that refusal is the
    /// dichotomy this object dissolves by compiling to a machine, not a defect to work around.
    /// </para>
    /// <para>
    /// <b>A pattern that already matches the empty span never outgrows the window,</b> so it is the windowed case that
    /// also refuses: the powers of <c>(ε | a)</c> keep a constant term forever, no power is zero and no partial sum
    /// stabilizes without an idempotent addition, and the call returns <see langword="false"/> with
    /// <see cref="ClosureCertificate.Nilpotent"/> attempted. The refusal is correct rather than conservative — the sum
    /// genuinely has no value at a counting material — and the answer wanted is the iteration of the empty-span-free
    /// part, which carries the same spans.
    /// </para>
    /// </remarks>
    public bool TryIterate(in PresentedAlgebra<TValue, TOps>.Element value, out PresentedAlgebra<TValue, TOps>.Element iterated, out SumClosureObstruction obstruction) {
        Algebra.RequireOwned(
            value: value,
            paramName: nameof(value)
        );

        return Algebra.TrySumOverAllLengths(
            obstruction: out obstruction,
            total: out iterated,
            value: value
        );
    }
    /// <summary>Attempts to read the weight a pattern gives one token span.</summary>
    /// <param name="value">The pattern.</param>
    /// <param name="letters">The span, as letter numbers.</param>
    /// <param name="weight">On success, the coefficient the pattern carries at that span.</param>
    /// <returns><see langword="true"/> when the span is inside the window; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">The pattern belongs to another pattern algebra.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A letter names no generator.</exception>
    /// <remarks>It is the pairing of the span's basis element with the pattern, so the answer is a duality readout and
    /// not a table lookup; it is the slow, structural readout, and <see cref="PatternMatcher{TValue, TOps}"/> is the
    /// one built for running.</remarks>
    public bool TryWeigh(in PresentedAlgebra<TValue, TOps>.Element value, ReadOnlySpan<int> letters, out TValue weight) {
        Algebra.RequireOwned(
            value: value,
            paramName: nameof(value)
        );

        weight = m_material.Zero;

        if (
            (0 != Window) &&
            (letters.Length > Window)
        ) { return false; }

        var word = Algebra.Identity;

        for (var index = 0; (index < letters.Length); ++index) {
            word = Algebra.Multiply(
                left: word,
                right: Algebra.Generator(symbol: letters[index])
            );
        }

        weight = Algebra.Pair(
            covector: word,
            value: value
        );

        return true;
    }
    /// <summary>Unites two patterns.</summary>
    /// <param name="left">The first pattern.</param>
    /// <param name="right">The second pattern.</param>
    /// <returns>The pattern matching a span exactly when either does — the algebra's sum, which over a counting
    /// material adds the ways and over a tropical one takes the cheaper.</returns>
    /// <exception cref="ArgumentException">An operand belongs to another pattern algebra.</exception>
    public PresentedAlgebra<TValue, TOps>.Element Union(in PresentedAlgebra<TValue, TOps>.Element left, in PresentedAlgebra<TValue, TOps>.Element right) {
        Algebra.RequireOwned(
            value: left,
            paramName: nameof(left)
        );
        Algebra.RequireOwned(
            value: right,
            paramName: nameof(right)
        );

        return Algebra.Add(
            left: left,
            right: right
        );
    }
}
/// <summary>Complementation, which exists only where the material has a De Morgan complement.</summary>
/// <remarks>
/// It is a separate surface because that is what makes the gate a compile error rather than a documented footgun: the
/// constraint is <see cref="IComplementedMaterial{TValue, TSelf}"/>, so <c>pattern.Complement(value)</c> does not
/// resolve at a counting, tropical, fixed-point or field material and no runtime refusal is needed. The reason is a
/// theorem rather than a taste: a semiring carrying a De Morgan complement and a top element satisfies
/// <c>1 + 1 = 1</c>, which those materials do not. Two materials do: <see cref="BooleanMaterial"/>, where the
/// complement is negation and a coefficient is membership, and <see cref="FuzzyMaterial"/>, where it is the exact
/// <c>1 − x</c> and a coefficient is a degree of membership — so a complemented pattern there is graded rather than
/// two-valued, and complementing twice returns the original pattern at both.
/// </remarks>
public static class PatternComplement {
    /// <summary>Complements a pattern within its window.</summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material; it must carry a De Morgan complement.</typeparam>
    /// <param name="pattern">The pattern surface.</param>
    /// <param name="value">The pattern to complement.</param>
    /// <returns>The pattern matching exactly the token spans inside the window that this one does not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> belongs to another pattern algebra.</exception>
    /// <exception cref="InvalidOperationException">The monoid was left free, so there is no finite basis to complement
    /// against.</exception>
    /// <remarks><b>The complement is relative to the window.</b> It is the set difference against every span of at most
    /// <see cref="TokenPattern{TValue, TOps}.Window"/> tokens, not against the infinite label space — which is the same
    /// boundary iteration carries, stated on the other side.</remarks>
    public static PresentedAlgebra<TValue, TOps>.Element Complement<TValue, TOps>(this TokenPattern<TValue, TOps> pattern, in PresentedAlgebra<TValue, TOps>.Element value)
        where TOps : struct, IComplementedMaterial<TValue, TOps> {
        ArgumentNullException.ThrowIfNull(argument: pattern);

        var algebra = pattern.Algebra;
        var presentation = algebra.Presentation;

        algebra.RequireOwned(
            value: value,
            paramName: nameof(value)
        );

        if (!presentation.HasCompiledNormalFormBasis) {
            throw new InvalidOperationException(message: "A complement is taken against a finite basis, which a free monoid does not have.");
        }

        var material = presentation.Material;
        var count = presentation.NormalFormCount;
        var coefficients = new TValue[count];
        var keys = new long[count];

        for (var key = 0; (key < count); ++key) {
            // An owner-less default has no material with which its indexer could manufacture zero. Once this operation
            // receives it, however, this pattern's material supplies the universal-zero interpretation.
            coefficients[key] = material.Complement(value: ((0 == value.SupportCount)
                ? material.Zero
                : value[key]));
            keys[key] = key;
        }

        return algebra.FromSupport(
            coefficients: coefficients,
            keys: keys
        );
    }
}
