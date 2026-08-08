namespace Puck.Maths;

/// <summary>The refusal an eager residual closure returns when it could not finish inside its state budget.</summary>
/// <param name="StatesExplored">The number of distinct residual states discovered before the budget ran out.</param>
/// <param name="BlockedSymbol">The generator whose residual would have exceeded the budget, or <c>-1</c> when the
/// budget itself was never the obstacle.</param>
public readonly record struct ClosureObstruction(long StatesExplored, int BlockedSymbol);

/// <summary>
/// The eager determinization of one element by its residuals: the finitely many distinct residuals of a seed, numbered
/// canonically, together with the quiver-presented machine whose transition elements ARE those residuals' arrows.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// This is the presentation morphism that dissolves the one forced dichotomy. An element of a free presentation may have
/// unbounded support and no finite table; its image here has finite support, because the closure is a functor into a
/// codiscrete-state algebra where the star of a finite matrix is a finite matrix. There are not two representations of
/// the object — there is one finite-support element type at two presentations, and this is the computed map between
/// them.
/// </para>
/// <para>
/// <b>The state numbering is canonical.</b> States are numbered by the shortest generator word that reaches them, in
/// the presentation's own well-founded order — ascending by length, then lexicographically by symbol — which is the
/// same order the presentation numbers its normal forms in. The numbering is therefore a function of the seed and the
/// twist alone: it never depends on a hash, an insertion order, or the machine it ran on.
/// </para>
/// <para>
/// Eager and immutable: everything is computed by <see cref="PresentedAlgebra{TValue, TOps}.TryCompileClosure"/> before
/// the value exists, so nothing here can be built lazily and nothing can race.
/// </para>
/// </remarks>
public sealed class ResidualClosure<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private readonly int m_generatorCount;
    private readonly PresentedAlgebra<TValue, TOps>.Element[] m_states;
    private readonly int[] m_transition;

    internal ResidualClosure(
        PresentedMachine<TValue, TOps> machine,
        PresentedAlgebra<TValue, TOps>.Element[] states,
        int[] transition,
        int generatorCount
    ) {
        Machine = machine;
        StateCount = states.Length;
        m_generatorCount = generatorCount;
        m_states = states;
        m_transition = transition;
    }

    /// <summary>Gets the determinized machine: a quiver presentation on the discovered states, one transition element
    /// per generator, the seed state as the initial vector, and each state's trace as its readout.</summary>
    public PresentedMachine<TValue, TOps> Machine { get; }
    /// <summary>Gets the number of distinct residual states.</summary>
    public int StateCount { get; }

    /// <summary>Returns the residual element one state stands for, in the algebra the seed came from.</summary>
    /// <param name="state">The state number.</param>
    /// <returns>The element.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state number is outside <see cref="StateCount"/>.</exception>
    public PresentedAlgebra<TValue, TOps>.Element State(int state) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: state);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: state, other: StateCount);

        return m_states[state];
    }

    /// <summary>Returns the state one generator moves a state to.</summary>
    /// <param name="state">The state number.</param>
    /// <param name="symbol">The generator's symbol.</param>
    /// <returns>The target state, or <c>-1</c> when the residual is zero and the arrow is therefore absent.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state number or the symbol is outside its range.</exception>
    public long Transition(int state, int symbol) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: state);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: state, other: StateCount);
        ArgumentOutOfRangeException.ThrowIfNegative(value: symbol);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: symbol, other: m_generatorCount);

        return m_transition[((state * m_generatorCount) + symbol)];
    }
}
public sealed partial class PresentedAlgebra<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    /// <summary>The largest number of residual states a closure can carry, inherited from the object cap of the quiver
    /// presentation the closure compiles into.</summary>
    public const int MaximumClosureStates = 16;

    /// <summary>Compiles the residuals of one element into a quiver presentation, eagerly and bounded.</summary>
    /// <param name="seed">The element whose residuals are closed over.</param>
    /// <param name="twist">The twist the residual carries.</param>
    /// <param name="shiftSymbol">The shift generator, read only at <see cref="ResidualTwist.ShiftGenerator"/>.</param>
    /// <param name="stateLimit">The maximum number of distinct residual states to admit, at most
    /// <see cref="MaximumClosureStates"/>.</param>
    /// <param name="closure">On success, the closure.</param>
    /// <param name="obstruction">On failure, the states explored and the generator that overran the budget.</param>
    /// <returns><see langword="true"/> when the closure finished inside the budget; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">The seed belongs to another algebra.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stateLimit"/> is outside one through
    /// <see cref="MaximumClosureStates"/>, or a symbol names no generator.</exception>
    /// <remarks>
    /// <para>
    /// It routes through <see cref="Residual"/> and through nothing else: there is no second derivative implementation
    /// here, and no branch on which twist was chosen. The closure is a breadth-first walk from the seed, trying
    /// generators in ascending symbol order, so the state numbering is the canonical one
    /// <see cref="ResidualClosure{TValue, TOps}"/> documents.
    /// </para>
    /// <para>
    /// A zero residual is NOT a state: it becomes an absent arrow, which is the same charge-zero annihilation a
    /// degenerate generator already uses, and it keeps the discovered state set minimal by construction.
    /// </para>
    /// <para>
    /// The cost is dominated by building the quiver presentation, which is quadratic in the state count for its
    /// generators and quartic for its rules. Sixteen states is the cap and is already substantial work; a closure that
    /// wants more wants a sparse presentation, which is not this phase.
    /// </para>
    /// </remarks>
    public bool TryCompileClosure(
        in Element seed,
        ResidualTwist twist,
        int shiftSymbol,
        int stateLimit,
        out ResidualClosure<TValue, TOps> closure,
        out ClosureObstruction obstruction
    ) {
        RequireOwned(value: seed, paramName: nameof(seed));

        ArgumentOutOfRangeException.ThrowIfLessThan(value: stateLimit, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: stateLimit, other: MaximumClosureStates);

        var generatorCount = Presentation.GeneratorCount;
        var material = m_material;
        var states = new List<Element> { seed };
        var transition = new List<int>();

        closure = null!;
        obstruction = default;

        for (var state = 0; (state < states.Count); ++state) {
            for (var symbol = 0; (symbol < generatorCount); ++symbol) {
                var residual = Residual(symbol: symbol, value: states[state], twist: twist, shiftSymbol: shiftSymbol);

                if (0 == residual.SupportCount) {
                    transition.Add(item: -1);

                    continue;
                }

                var target = -1;

                for (var probe = 0; (probe < states.Count); ++probe) {
                    if (AreEqual(left: states[probe], right: residual)) {
                        target = probe;

                        break;
                    }
                }

                if (target < 0) {
                    if (states.Count >= stateLimit) {
                        obstruction = new(StatesExplored: states.Count, BlockedSymbol: symbol);

                        return false;
                    }

                    target = states.Count;

                    states.Add(item: residual);
                }

                transition.Add(item: target);
            }
        }

        var stateCount = states.Count;
        var arrows = transition.ToArray();
        var quiver = PresentedAlgebra<TValue, TOps>.Create(presentation: Presentations.Quiver<TValue, TOps>(objectCount: stateCount, arrows: [], material: material));
        var stepKeys = new List<long>();
        var stepValues = new List<TValue>();
        var steps = new Element[generatorCount];

        for (var symbol = 0; (symbol < generatorCount); ++symbol) {
            stepKeys.Clear();
            stepValues.Clear();

            for (var state = 0; (state < stateCount); ++state) {
                var target = arrows[((state * generatorCount) + symbol)];

                if (target < 0) { continue; }

                stepKeys.Add(item: ((state * (long)stateCount) + target));
                stepValues.Add(item: material.One);
            }

            steps[symbol] = quiver.FromSupport(keys: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: stepKeys), coefficients: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: stepValues));
        }

        var readoutKeys = new long[stateCount];
        var readoutValues = new TValue[stateCount];

        for (var state = 0; (state < stateCount); ++state) {
            readoutKeys[state] = state;
            readoutValues[state] = Trace(value: states[state]);
        }

        closure = new ResidualClosure<TValue, TOps>(
            machine: PresentedMachine<TValue, TOps>.Create(
                algebra: quiver,
                initial: quiver.FromSupport(keys: [0L], coefficients: [material.One]),
                steps: steps,
                readout: quiver.FromSupport(keys: readoutKeys, coefficients: readoutValues)
            ),
            states: [.. states],
            transition: arrows,
            generatorCount: generatorCount
        );

        return true;
    }
}
