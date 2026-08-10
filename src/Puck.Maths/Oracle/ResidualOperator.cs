namespace Puck.Maths;

/// <summary>
/// The twist a residual carries — the algebra endomorphism <c>σ</c> that the twisted Leibniz rule
/// <c>D_g(u·v) = (D_g u)·v + σ(u)·(D_g v)</c> applies to the left factor. It is data the caller selects, not a family of
/// operators: one loop reads it and three regimes fall out.
/// </summary>
/// <remarks>Each case is an algebra endomorphism of the free algebra, named by the image it gives one letter: the
/// counit sends every letter to zero, the identity sends a letter to itself, and the shift sends a letter to the chosen
/// shift generator followed by that letter. Nothing else about the residual changes between them.</remarks>
public enum ResidualTwist {
    /// <summary>The counit: every non-empty word is killed, so only a prefix-free match survives and the residual is the
    /// left quotient <c>g⁻¹·u</c> — the language derivative.</summary>
    Counit,
    /// <summary>The identity: the left factor passes through unchanged, so the residual is an ordinary derivation — the
    /// jet and forward-differentiation regime.</summary>
    Identity,
    /// <summary>The shift: each letter of the left factor is preceded by a chosen shift generator, so the residual is
    /// the skew step behind a holonomic recurrence.</summary>
    ShiftGenerator,
}
public sealed partial class PresentedAlgebra<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    // The residual of a word can grow: the shift twist emits two symbols per prefix letter. The rewriter refuses a word
    // longer than its own cap and the refusal surfaces as the documented InvalidOperationException, so this only sizes
    // the staging buffer.
    private const int ResidualWordLimit = 1024;

    /// <summary>Differentiates an element by one generator under a twisted Leibniz rule.</summary>
    /// <param name="symbol">The generator to differentiate by.</param>
    /// <param name="value">The element to differentiate.</param>
    /// <param name="twist">The twist the rule applies to the left factor.</param>
    /// <param name="shiftSymbol">The shift generator, read only at <see cref="ResidualTwist.ShiftGenerator"/>.</param>
    /// <returns>The residual <c>D_symbol(value)</c>, its support canonically ordered and its zeros pruned.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> belongs to another algebra.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A symbol names no generator of this presentation.</exception>
    /// <exception cref="InvalidOperationException">A residual term exceeded the presentation's normalization budget or
    /// its key scheme.</exception>
    /// <remarks>
    /// <para>
    /// <b>What it computes.</b> On a word the operator is the sum over every occurrence of the generator of "twist the
    /// prefix, drop the occurrence, keep the suffix":
    /// <c>D_g(a₁…a_n) = Σ_{i : a_i = g} σ(a₁…a_{i−1})·(a_{i+1}…a_n)</c>. That single formula is the twisted Leibniz
    /// rule <c>D_g(u·v) = (D_g u)·v + σ(u)·(D_g v)</c> — the rule is what the formula satisfies, not a second code path
    /// — and the three twists are three choices of <c>σ</c>. At <see cref="ResidualTwist.Counit"/> only the leading
    /// occurrence survives, so the result is the left quotient; at <see cref="ResidualTwist.Identity"/> every occurrence
    /// contributes its prefix unchanged, which is the ordinary derivation; at
    /// <see cref="ResidualTwist.ShiftGenerator"/> every prefix letter is preceded by the shift generator.
    /// </para>
    /// <para>
    /// <b>The boundary, and it is the load-bearing one.</b> The rule is an identity of the free algebra, where it holds
    /// by construction. It descends to a presented quotient exactly when the derivation annihilates every relation, and
    /// it does not descend when it does not: on the jet presentation <c>Monogenic([0, 0])</c> the identity twist gives
    /// <c>D(x·x) = 2x</c> while the relation forces <c>x·x = 0</c>, so <c>D(u·v)</c> and <c>(D u)·v + u·(D v)</c> agree
    /// in the unit component — which is exactly <see cref="FixedDual{TScalar}"/>'s chain rule, <c>a·e + b·c</c> — and
    /// differ above it. The result is normalized after differentiating, so what is returned is always a genuine element;
    /// what is not promised is that the Leibniz rule survives a relation the derivation fails to annihilate. A
    /// presentation window is such a relation: it annihilates every term above its degree, and a derivation drops a
    /// degree, so a windowed presentation loses the rule at its own boundary exactly as a monic reduction does. On a
    /// free presentation with no relation at all — no reduction, no swap, no window — the rule holds at every operand,
    /// which is the statement worth checking.
    /// </para>
    /// <para>
    /// <b>Rounding.</b> Each returned coefficient is folded through exactly one
    /// <see cref="IMaterialOps{TValue, TOps}.FusedChargedLinear"/> over the rewrite charges its terms carried, so the
    /// one-rounding-per-returned-component discipline holds here as it does for the product. This is a structural
    /// operation rather than a steady-state kernel, and it allocates its per-key staging.
    /// </para>
    /// </remarks>
    public Element Residual(int symbol, in Element value, ResidualTwist twist, int shiftSymbol = -1) {
        var presentation = Presentation;
        var generatorCount = presentation.GeneratorCount;

        RequireOwned(value: value, paramName: nameof(value));
        ArgumentOutOfRangeException.ThrowIfNegative(value: symbol);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: symbol, other: generatorCount);

        if (ResidualTwist.ShiftGenerator == twist) {
            ArgumentOutOfRangeException.ThrowIfNegative(value: shiftSymbol);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: shiftSymbol, other: generatorCount);
        }

        var material = m_material;
        var charges = new List<TValue>();
        var combination = new ChargedPresentation<TValue, TOps>.KeyedCombination();
        var coefficients = new List<TValue>();
        var targets = new List<long>();
        var source = new int[ResidualWordLimit];
        var staged = new int[ResidualWordLimit];
        var words = new ChargedPresentation<TValue, TOps>.Combination();

        for (var index = 0; (index < value.SupportCount); ++index) {
            if (!TryWordOf(key: value.Keys[index], word: source, length: out var length)) {
                throw new InvalidOperationException(message: "A support key of this element does not name a word of the presentation.");
            }

            var coefficient = value.Coefficients[index];

            for (var position = 0; (position < length); ++position) {
                if (source[position] != symbol) { continue; }

                // The twisted prefix, then the untouched suffix. The counit kills every non-empty prefix, which is what
                // collapses the sum to its leading occurrence.
                if ((ResidualTwist.Counit == twist) && (0 != position)) { break; }

                var written = 0;

                if (ResidualTwist.Identity == twist) {
                    source.AsSpan(start: 0, length: position).CopyTo(destination: staged);
                    written = position;
                } else if (ResidualTwist.ShiftGenerator == twist) {
                    for (var prefix = 0; (prefix < position); ++prefix) {
                        if ((written + 2) > ResidualWordLimit) {
                            throw new InvalidOperationException(message: "A residual term outgrew the word cap of this presentation.");
                        }

                        staged[written++] = shiftSymbol;
                        staged[written++] = source[prefix];
                    }
                }

                var suffix = ((length - position) - 1);

                if ((written + suffix) > ResidualWordLimit) {
                    throw new InvalidOperationException(message: "A residual term outgrew the word cap of this presentation.");
                }

                source.AsSpan(start: (position + 1), length: suffix).CopyTo(destination: staged.AsSpan(start: written, length: suffix));
                written += suffix;

                if (!presentation.TryRewriteToKeys(word: staged.AsSpan(start: 0, length: written), charge: material.One, stepLimit: ResidualBudget, result: combination, combination: words, stepsTaken: out _)) {
                    throw new InvalidOperationException(message: "A residual term exceeded the normalization budget of this presentation.");
                }

                for (var term = 0; (term < combination.Count); ++term) {
                    charges.Add(item: combination.ChargeAt(index: term));
                    coefficients.Add(item: coefficient);
                    targets.Add(item: combination.KeyAt(index: term));
                }
            }
        }

        return FoldByTarget(targets: targets, charges: charges, values: coefficients);
    }

    // The normalization budget the residual and closure paths run under; the same order as the kernel's own.
    private const long ResidualBudget = (1L << 20);

    // The generator word one key names, under whichever key scheme the presentation uses.
    private bool TryWordOf(long key, Span<int> word, out int length) {
        if (!m_isDense) { return Presentation.TryUnpackWord(key: key, word: word, length: out length); }

        var normalForm = Presentation.NormalFormWord(key: key);

        length = normalForm.Length;

        if (length > word.Length) { return false; }

        normalForm.CopyTo(destination: word);

        return true;
    }
}
