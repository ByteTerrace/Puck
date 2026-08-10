namespace Puck.Maths;

/// <summary>The refusal a membership run returns when the span it was handed is longer than the pattern represents.</summary>
/// <param name="Length">The number of tokens offered.</param>
/// <param name="Window">The longest span the pattern represents.</param>
/// <remarks>A run that walks off the machine is an answer — the span does not match — while a span past the window is a
/// refusal, because beyond it the compiled machine has no arrows and could only report a false negative.</remarks>
public readonly record struct MatchObstruction(int Length, int Window);

/// <summary>
/// A pattern compiled to a finite machine: the eager residual closure, flattened into a transition table and an
/// acceptance weight per state, so a membership run is a table read per token and allocates nothing.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// <b>It contributes no automaton construction.</b> The states, the transitions and the state numbering all come from
/// <see cref="PresentedAlgebra{TValue, TOps}.TryCompileClosure"/>, which computes them from
/// <see cref="PresentedAlgebra{TValue, TOps}.Residual"/> at <see cref="ResidualTwist.Counit"/>. What is added here is
/// the flattening: the closure's arrows copied into one <see cref="int"/> array and its readout into one weight array,
/// which is what makes a step a single indexed read.
/// </para>
/// <para>
/// <b>Acceptance is the trace.</b> A state IS the residual of the pattern by the word that reached it, and the weight
/// the run returns is that residual's pairing with the unit — the coefficient of the empty span. So the run answers
/// membership over a Boolean material, the number of matches over a counting one and the best cost over a tropical one
/// with no change of code, because the readout was a duality pairing all along.
/// </para>
/// <para>
/// <b>The window materializes into the state count.</b> The residual of a windowed pattern carries the remaining
/// budget, so a pattern that iterates has at least one state per remaining token and the state count is at least the
/// window. That is the same fact the residual's own boundary states: a window is a relation the derivation does not
/// annihilate, and here it is visible as states rather than as a lost identity.
/// </para>
/// <para>
/// Immutable once compiled, so <see cref="TryMatch"/>, <see cref="Step"/> and <see cref="Accept"/> read only arrays
/// that were finished before the value existed and are safe to run from several threads at once. Compiling one is not,
/// and neither is anything reached through <see cref="Closure"/> that multiplies, because the algebra underneath keeps
/// mutable working buffers.
/// </para>
/// </remarks>
public sealed class PatternMatcher<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private readonly TValue[] m_accept;
    private readonly object? m_alphabetIdentity;
    private readonly TOps m_material;
    private readonly int[] m_transition;

    private PatternMatcher(ResidualClosure<TValue, TOps> closure, int letterCount, int window, TOps material, object? alphabetIdentity) {
        var readout = closure.Machine.Readout;
        var accept = new TValue[closure.StateCount];
        var transition = new int[(closure.StateCount * letterCount)];

        for (var state = 0; (state < closure.StateCount); ++state) {
            accept[state] = readout[state];

            for (var letter = 0; (letter < letterCount); ++letter) {
                transition[((state * letterCount) + letter)] = ((int)closure.Transition(state: state, symbol: letter));
            }
        }

        Closure = closure;
        LetterCount = letterCount;
        StateCount = closure.StateCount;
        Window = window;
        m_accept = accept;
        m_alphabetIdentity = alphabetIdentity;
        m_material = material;
        m_transition = transition;
    }

    /// <summary>Gets the residual closure this was flattened from.</summary>
    public ResidualClosure<TValue, TOps> Closure { get; }
    /// <summary>Gets the number of letters the machine steps on.</summary>
    public int LetterCount { get; }
    /// <summary>Gets the number of states, which is the number of distinct residuals of the pattern.</summary>
    public int StateCount { get; }
    /// <summary>Gets the longest token span the pattern represents, or zero when the monoid was left free.</summary>
    public int Window { get; }

    /// <summary>Decides whether two compiled patterns weigh every token span alike, and returns the shortest span that
    /// separates them when they do not.</summary>
    /// <param name="left">The first matcher.</param>
    /// <param name="right">The second matcher.</param>
    /// <param name="witness">On inequivalence, the shortest separating span and the two weights it produced.</param>
    /// <returns><see langword="true"/> when the weights agree on every span; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">A matcher is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The matchers step on a different number of letters.</exception>
    /// <exception cref="InvalidOperationException">The material is not a certified field.</exception>
    /// <remarks>
    /// The shared machine equivalence, which quotients by a pairing radical and therefore needs the material's
    /// inverses. That is a real boundary and it is worth stating plainly: over
    /// <see cref="BooleanMaterial"/> this is refused, and equivalence there is decided instead by comparing the two
    /// pattern elements, which the window has already made finite. Where it does run — a prime field, an exact
    /// rational — it decides equality of the whole weighted series without enumerating spans, which is the capability
    /// the enumeration cannot give.
    /// </remarks>
    public static bool AreEquivalent(PatternMatcher<TValue, TOps> left, PatternMatcher<TValue, TOps> right, out EquivalenceWitness<TValue> witness) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        return PresentedMachine<TValue, TOps>.AreEquivalent(left: left.Closure.Machine, right: right.Closure.Machine, witness: out witness);
    }

    /// <summary>Intersects two compiled patterns by pairing their machines.</summary>
    /// <param name="left">The first matcher.</param>
    /// <param name="right">The second matcher.</param>
    /// <returns>The machine whose weight on every span is the product of the two machines' weights, which over a
    /// Boolean material is their conjunction.</returns>
    /// <exception cref="ArgumentNullException">A matcher is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The matchers step on a different number of letters.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The paired state space exceeds the tensor presentation's
    /// generator cap, which admits a product of state counts of at most eight.</exception>
    /// <remarks>
    /// <para>
    /// The genuine pair-up: <see cref="Presentations.Tensor"/> builds the paired presentation out of the two factors'
    /// own compiled cells, and <see cref="PresentedAlgebra{TValue, TOps}.PairUp"/> pairs the initial vector, every step
    /// and the readout. No product construction is written here.
    /// </para>
    /// <para>
    /// <b>The construction survives every material; the theorem does not.</b> That the pair's weight is the product of
    /// the two weights holds on an exact material and fails over the house scalar, because a pair's cells are not
    /// products of already-rounded cells. Over <see cref="BooleanMaterial"/>, <see cref="CountingMaterial"/> and the
    /// exact fields it is intersection, ambiguity multiplication and conjunction exactly.
    /// </para>
    /// </remarks>
    public static PresentedMachine<TValue, TOps> Intersect(PatternMatcher<TValue, TOps> left, PatternMatcher<TValue, TOps> right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        if (left.LetterCount != right.LetterCount) {
            throw new ArgumentException(message: "Two machines pair only when they step on the same letters.", paramName: nameof(right));
        }

        var leftMachine = left.Closure.Machine;
        var rightMachine = right.Closure.Machine;
        var rightPresentation = rightMachine.Algebra.Presentation;
        var stride = rightPresentation.NormalFormCount;
        var tensor = PresentedAlgebra<TValue, TOps>.Create(
            presentation: Presentations.Tensor(left: leftMachine.Algebra.Presentation, right: rightPresentation)
        );
        var steps = new PresentedAlgebra<TValue, TOps>.Element[left.LetterCount];

        for (var letter = 0; (letter < steps.Length); ++letter) {
            steps[letter] = tensor.PairUp(left: leftMachine.Step(index: letter), right: rightMachine.Step(index: letter), rightKeyCount: stride);
        }

        return PresentedMachine<TValue, TOps>.Create(
            algebra: tensor,
            initial: tensor.PairUp(left: leftMachine.Initial, right: rightMachine.Initial, rightKeyCount: stride),
            steps: steps,
            readout: tensor.PairUp(left: leftMachine.Readout, right: rightMachine.Readout, rightKeyCount: stride)
        );
    }

    /// <summary>Compiles a pattern to a finite machine, eagerly and bounded.</summary>
    /// <param name="pattern">The pattern surface the value belongs to.</param>
    /// <param name="value">The pattern.</param>
    /// <param name="stateLimit">The maximum number of residual states to admit, at most
    /// <see cref="PresentedAlgebra{TValue, TOps}.MaximumClosureStates"/>.</param>
    /// <param name="matcher">On success, the compiled matcher.</param>
    /// <param name="obstruction">On failure, the states explored and the letter that overran the budget.</param>
    /// <returns><see langword="true"/> when the closure finished inside the budget; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> belongs to another pattern algebra.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stateLimit"/> is outside one through
    /// <see cref="PresentedAlgebra{TValue, TOps}.MaximumClosureStates"/>.</exception>
    /// <remarks>Eager, so the table is complete and immutable before the matcher exists and no state can be built while
    /// a run is reading it. State blowup is a refusal carrying what was explored, never an exception.</remarks>
    public static bool TryCompile(
        TokenPattern<TValue, TOps> pattern,
        in PresentedAlgebra<TValue, TOps>.Element value,
        int stateLimit,
        out PatternMatcher<TValue, TOps> matcher,
        out ClosureObstruction obstruction
    ) {
        ArgumentNullException.ThrowIfNull(argument: pattern);
        pattern.Algebra.RequireOwned(value: value, paramName: nameof(value));

        return TryCompileCore(
            pattern: pattern,
            seed: value,
            stateLimit: stateLimit,
            alphabetIdentity: null,
            matcher: out matcher,
            obstruction: out obstruction
        );
    }

    /// <summary>Compiles a pattern to a finite machine bound to the exact refined alphabet whose letter numbering the
    /// pattern uses.</summary>
    /// <typeparam name="TPredicate">The predicate form.</typeparam>
    /// <typeparam name="TRefinement">The predicate algebra.</typeparam>
    /// <param name="pattern">The pattern surface the value belongs to.</param>
    /// <param name="value">The pattern.</param>
    /// <param name="alphabet">The exact alphabet instance that assigned the pattern's letter numbers.</param>
    /// <param name="stateLimit">The maximum number of residual states to admit.</param>
    /// <param name="matcher">On success, the compiled matcher.</param>
    /// <param name="obstruction">On failure, the states explored and the letter that overran the budget.</param>
    /// <returns><see langword="true"/> when the closure finished inside the budget; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> or <paramref name="alphabet"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> belongs to another pattern algebra, or the pattern
    /// and alphabet have different letter counts.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stateLimit"/> is outside the supported range.</exception>
    /// <remarks>
    /// The binding is intentionally by instance identity. A recreated partition may happen to have the same size — or
    /// even the same current blocks — but it is not silently treated as the declaration that assigned this machine's
    /// letter numbers. An explicit verified remapping belongs in a separate API rather than in positional coincidence.
    /// </remarks>
    public static bool TryCompile<TPredicate, TRefinement>(
        TokenPattern<TValue, TOps> pattern,
        in PresentedAlgebra<TValue, TOps>.Element value,
        MintermAlphabet<TPredicate, TRefinement> alphabet,
        int stateLimit,
        out PatternMatcher<TValue, TOps> matcher,
        out ClosureObstruction obstruction
    )
        where TRefinement : struct, IAlphabetRefinement<TPredicate> {
        ArgumentNullException.ThrowIfNull(argument: pattern);
        ArgumentNullException.ThrowIfNull(argument: alphabet);
        pattern.Algebra.RequireOwned(value: value, paramName: nameof(value));

        if (pattern.LetterCount != alphabet.LetterCount) {
            throw new ArgumentException(message: "A pattern can be bound only to an alphabet with the same letter count.", paramName: nameof(alphabet));
        }

        return TryCompileCore(
            seed: value,
            pattern: pattern,
            stateLimit: stateLimit,
            alphabetIdentity: alphabet,
            matcher: out matcher,
            obstruction: out obstruction
        );
    }

    private static bool TryCompileCore(
        TokenPattern<TValue, TOps> pattern,
        in PresentedAlgebra<TValue, TOps>.Element seed,
        int stateLimit,
        object? alphabetIdentity,
        out PatternMatcher<TValue, TOps> matcher,
        out ClosureObstruction obstruction
    ) {
        matcher = null!;

        if (!pattern.Algebra.TryCompileClosure(
            seed: seed,
            twist: ResidualTwist.Counit,
            shiftSymbol: -1,
            stateLimit: stateLimit,
            closure: out var closure,
            obstruction: out obstruction
        )) {
            return false;
        }

        matcher = new(
            closure: closure,
            letterCount: pattern.LetterCount,
            window: pattern.Window,
            material: pattern.Algebra.Presentation.Material,
            alphabetIdentity: alphabetIdentity
        );

        return true;
    }

    /// <summary>Returns the weight one state accepts with.</summary>
    /// <param name="state">The state number.</param>
    /// <returns>The residual's trace: membership over a Boolean material, a match count over a counting one, a cost
    /// over a tropical one.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state number is outside <see cref="StateCount"/>.</exception>
    public TValue Accept(int state) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: state);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: state, other: StateCount);

        return m_accept[state];
    }
    /// <summary>Returns the state one letter moves a state to.</summary>
    /// <param name="state">The state number.</param>
    /// <param name="letter">The letter.</param>
    /// <returns>The target state, or <c>-1</c> when the residual is zero and no span can complete a match from here.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state number or the letter is outside its range.</exception>
    public int Step(int state, int letter) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: state);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: state, other: StateCount);
        ArgumentOutOfRangeException.ThrowIfNegative(value: letter);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: letter, other: LetterCount);

        return m_transition[((state * LetterCount) + letter)];
    }

    // The bare table reads, for callers inside the library whose state is this machine's own output and therefore
    // already in range. The public Step and Accept re-check it for callers whose state is not.
    internal TValue AcceptCore(int state) =>
        m_accept[state];
    internal int StepCore(int state, int letter) =>
        m_transition[((state * LetterCount) + letter)];
    internal bool IsBoundTo(object alphabet) =>
        ReferenceEquals(objA: m_alphabetIdentity, objB: alphabet);

    // The material's zero, without walking the closure's chain back to the presentation to find the same value.
    internal TValue Zero =>
        m_material.Zero;

    /// <summary>Runs a token span, already classified into letters, through the machine.</summary>
    /// <param name="letters">The span, as letter numbers.</param>
    /// <param name="weight">On success, the weight the pattern gives the span; the material's zero for no match.</param>
    /// <param name="obstruction">On refusal, the length offered and the window.</param>
    /// <returns><see langword="true"/> when the span was inside the window and the run decided it; otherwise
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A letter is outside <see cref="LetterCount"/>.</exception>
    /// <remarks>One indexed read per token and nothing else: no allocation, no lookup structure built on the way, and
    /// no state constructed during the run. A span that walks off the machine is decided immediately and the remaining
    /// tokens are not read.</remarks>
    public bool TryMatch(ReadOnlySpan<int> letters, out TValue weight, out MatchObstruction obstruction) {
        var letterCount = LetterCount;

        obstruction = default;
        weight = m_material.Zero;

        if ((0 != Window) && (letters.Length > Window)) {
            obstruction = new(Length: letters.Length, Window: Window);

            return false;
        }

        var state = 0;

        for (var index = 0; (index < letters.Length); ++index) {
            var letter = letters[index];

            ArgumentOutOfRangeException.ThrowIfNegative(value: letter, paramName: nameof(letters));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: letter, other: letterCount, paramName: nameof(letters));

            state = m_transition[((state * letterCount) + letter)];

            if (state < 0) { return true; }
        }

        weight = m_accept[state];

        return true;
    }
}

/// <summary>Running a compiled pattern over raw tokens, with the refinement axis classifying each one.</summary>
/// <remarks>This is the only place the two axes meet during a run: the alphabet turns a token into a letter and the
/// machine steps on the letter. A token no block accepts decides the span immediately, because it lies outside every
/// predicate the pattern was refined against.</remarks>
public static class TokenMatching {
    /// <summary>Runs a raw token span through a compiled pattern.</summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material.</typeparam>
    /// <typeparam name="TPredicate">The predicate form.</typeparam>
    /// <typeparam name="TRefinement">The predicate algebra.</typeparam>
    /// <param name="matcher">The compiled pattern.</param>
    /// <param name="alphabet">The refined alphabet the pattern was built on.</param>
    /// <param name="tokens">The tokens to run.</param>
    /// <param name="weight">On success, the weight the pattern gives the span; the material's zero for no match.</param>
    /// <param name="obstruction">On refusal, the length offered and the window.</param>
    /// <returns><see langword="true"/> when the span was inside the window and the run decided it; otherwise
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">The matcher or the alphabet is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="matcher"/> was not compiled with this exact
    /// <paramref name="alphabet"/> instance.</exception>
    /// <remarks>Allocation-free per token: one classification, one range check and one indexed read, all of which walk
    /// arrays that were built before the run started. The state is the machine's own output, so it is not re-checked;
    /// the letter comes from the alphabet and is.</remarks>
    public static bool TryMatch<TValue, TOps, TPredicate, TRefinement>(
        PatternMatcher<TValue, TOps> matcher,
        MintermAlphabet<TPredicate, TRefinement> alphabet,
        ReadOnlySpan<ulong> tokens,
        out TValue weight,
        out MatchObstruction obstruction
    )
        where TOps : struct, IMaterialOps<TValue, TOps>
        where TRefinement : struct, IAlphabetRefinement<TPredicate> {
        ArgumentNullException.ThrowIfNull(argument: matcher);
        ArgumentNullException.ThrowIfNull(argument: alphabet);

        if (!matcher.IsBoundTo(alphabet: alphabet)) {
            throw new ArgumentException(message: "A raw-token run requires the exact refined alphabet instance the matcher was compiled with.", paramName: nameof(alphabet));
        }

        obstruction = default;
        weight = matcher.Zero;

        if ((0 != matcher.Window) && (tokens.Length > matcher.Window)) {
            obstruction = new(Length: tokens.Length, Window: matcher.Window);

            return false;
        }

        var letterCount = matcher.LetterCount;
        var state = 0;

        for (var index = 0; (index < tokens.Length); ++index) {
            if (!alphabet.TryLetterOf(token: tokens[index], letter: out var letter)) { return true; }

            // Checked per token, never hoisted: an alphabet with more blocks than the matcher has letters still decides
            // every span whose tokens land inside the machine, and a pre-loop comparison would refuse those.
            ArgumentOutOfRangeException.ThrowIfNegative(value: letter);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: letter, other: letterCount);

            state = matcher.StepCore(state: state, letter: letter);

            if (state < 0) { return true; }
        }

        weight = matcher.AcceptCore(state: state);

        return true;
    }
}
