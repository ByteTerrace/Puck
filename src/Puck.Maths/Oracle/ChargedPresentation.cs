using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// One generator of a presentation: its symbol, its input and output boundaries (lists of colour indices), and its
/// grading degree. Arity is the input-boundary length and co-arity the output-boundary length; both are carried from
/// day one, and the phase-1 kernel implements the co-arity-one fragment.
/// </summary>
public readonly struct Generator {
    private readonly ReadOnlyMemory<int> m_inputs;
    private readonly ReadOnlyMemory<int> m_outputs;

    /// <summary>Creates a generator.</summary>
    /// <param name="symbol">The generator's symbol, which is its index in the presentation's generator list.</param>
    /// <param name="inputs">The input boundary as a list of colour indices.</param>
    /// <param name="outputs">The output boundary as a list of colour indices.</param>
    /// <param name="degree">The grading degree, read by the presentation window.</param>
    public Generator(int symbol, ReadOnlyMemory<int> inputs, ReadOnlyMemory<int> outputs, int degree) {
        Degree = degree;
        Symbol = symbol;
        m_inputs = inputs;
        m_outputs = outputs;
    }

    /// <summary>Gets the number of input wires, the input boundary's length.</summary>
    public int Arity => m_inputs.Length;
    /// <summary>Gets the number of output wires, the output boundary's length.</summary>
    public int Coarity => m_outputs.Length;
    /// <summary>Gets the grading degree.</summary>
    public int Degree { get; }
    /// <summary>Gets the input boundary as a list of colour indices.</summary>
    public ReadOnlySpan<int> Inputs => m_inputs.Span;
    /// <summary>Gets the output boundary as a list of colour indices.</summary>
    public ReadOnlySpan<int> Outputs => m_outputs.Span;
    /// <summary>Gets the generator's symbol.</summary>
    public int Symbol { get; }
}
/// <summary>The four kinds of charged rewrite rule. Nothing in the kernel privileges one over another; the kind names
/// the role a rule plays, and the normalizer reads it only to decide whether the rule is matched against a word or
/// consumed by the term flattener.</summary>
public enum RuleKind {
    /// <summary>The bracket-splicing charge. Not matched against a word: the term flattener multiplies this rule's
    /// charge in once per bracket it removes, so a charge of one flattens trees silently while a nontrivial charge makes
    /// re-association observable. A rule carrying one charge is that charge at every splice; a rule carrying one charge
    /// per ordered generator triple — row-major, <c>((first·n) + second)·n + third</c> — is the associator 3-cochain
    /// itself, read at the three keys the splice actually joins.</summary>
    Reassociate,
    /// <summary>A reordering rule — same length, lexicographically smaller — carrying the commutation charge.</summary>
    Swap,
    /// <summary>A length-reducing rule rewriting a pattern to a charged combination of shorter replacement terms.</summary>
    Reduce,
    /// <summary>A rule whose pattern rewrites to nothing: the charge-zero annihilation.</summary>
    Annihilate,
}
/// <summary>
/// One oriented charged rewrite rule: a pattern word rewrites to a charged combination of replacement terms.
/// Re-association, swap, reduction and annihilation are the same kind of datum.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <remarks>
/// <para>
/// <see cref="Replacement"/> packs the combination's terms back to back, each preceded by its symbol count —
/// <c>[len₀, s₀…, len₁, s₁…, …]</c> — with <see cref="Charges"/> carrying one charge per term in the same order.
/// <see cref="PackReplacement"/> builds that layout. An <see cref="RuleKind.Annihilate"/> rule carries no terms and no
/// charges.
/// </para>
/// <para>
/// A word-matched rule should be strictly decreasing in the presentation's well-founded order: a word is greater than
/// another when it is longer, or equal in length and lexicographically larger over the generator symbols. Reduction
/// shortens and swapping lowers the leading symbol, so both decrease. A rule that does not decrease is not rejected —
/// its normalization is simply bounded, reporting a <see cref="NormalizationObstruction"/> rather than looping.
/// </para>
/// <para>
/// An empty pattern matches the empty word only, which is how a presentation whose unit is a sum of idempotents (a
/// quiver's diagonal) states that fact as data instead of as a special case in the kernel.
/// </para>
/// </remarks>
public readonly struct RewriteRule<TValue> {
    private readonly ReadOnlyMemory<TValue> m_charges;
    private readonly ReadOnlyMemory<int> m_pattern;
    private readonly ReadOnlyMemory<int> m_replacement;

    /// <summary>Creates a charged rewrite rule.</summary>
    /// <param name="kind">The rule's kind.</param>
    /// <param name="pattern">The pattern word; empty matches the empty word only.</param>
    /// <param name="replacement">The length-prefixed packing of the replacement terms.</param>
    /// <param name="charges">One charge per replacement term.</param>
    public RewriteRule(RuleKind kind, ReadOnlyMemory<int> pattern, ReadOnlyMemory<int> replacement, ReadOnlyMemory<TValue> charges) {
        Kind = kind;
        m_charges = charges;
        m_pattern = pattern;
        m_replacement = replacement;
    }

    /// <summary>Gets the charges, one per replacement term.</summary>
    public ReadOnlySpan<TValue> Charges => m_charges.Span;
    /// <summary>Gets the rule's kind.</summary>
    public RuleKind Kind { get; }
    /// <summary>Gets the pattern word.</summary>
    public ReadOnlySpan<int> Pattern => m_pattern.Span;
    /// <summary>Gets the length-prefixed packing of the replacement terms.</summary>
    public ReadOnlySpan<int> Replacement => m_replacement.Span;
    /// <summary>Gets the number of replacement terms, which is the charge count.</summary>
    public int TermCount => m_charges.Length;

    /// <summary>Packs a combination of replacement words into the length-prefixed layout <see cref="Replacement"/> carries.</summary>
    /// <param name="terms">The replacement words, in the same order as their charges.</param>
    /// <returns>The packed replacement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="terms"/> is <see langword="null"/>.</exception>
    public static ReadOnlyMemory<int> PackReplacement(params int[][] terms) {
        ArgumentNullException.ThrowIfNull(argument: terms);

        var total = 0;

        foreach (var term in terms) { total += (term.Length + 1); }

        var packed = new int[total];
        var offset = 0;

        foreach (var term in terms) {
            packed[offset++] = term.Length;
            term.CopyTo(
                array: packed,
                index: offset
            );
            offset += term.Length;
        }

        return packed;
    }
}
/// <summary>What the declaration itself proves about the size of the normal-form language, independently of whether
/// the dense basis was compiled.</summary>
public enum NormalFormBoundedness {
    /// <summary>The declaration carries no size proof. This is uncertainty, not a claim that the language is infinite.</summary>
    Unknown,
    /// <summary>A positive degree window and strictly positive generator degrees bound every surviving word.</summary>
    DeclaredFinite,
}
/// <summary>The terminal outcome of the bounded dense-basis construction.</summary>
public enum NormalFormBasisOutcome {
    /// <summary>The normal forms and their full dense product table are available.</summary>
    Compiled,
    /// <summary>A finite representation capacity was reached before the dense basis could be completed.</summary>
    CapacityObstructed,
    /// <summary>A normalization run exhausted its rewrite-step budget before the dense basis could be completed.</summary>
    NormalizationExhausted,
}
/// <summary>The dense-basis construction stage that completed or was obstructed.</summary>
public enum NormalFormBasisStage {
    /// <summary>Closing normalized words under right extension.</summary>
    Discovery,
    /// <summary>Normalizing the empty word that names the multiplicative identity.</summary>
    IdentityNormalization,
    /// <summary>Compiling every ordered product of two discovered normal forms.</summary>
    ProductTable,
    /// <summary>The whole construction completed.</summary>
    Complete,
}
/// <summary>The public, typed account of dense normal-form construction.</summary>
/// <param name="Boundedness">What the declaration proves about mathematical boundedness.</param>
/// <param name="IsKnownFinite">Whether either the declaration or completed discovery proves the normal-form language
/// finite.</param>
/// <param name="Outcome">Whether the dense basis compiled, met a representation capacity, or exhausted normalization.</param>
/// <param name="Stage">The stage that completed or was obstructed.</param>
/// <param name="ConfiguredBound">The capacity or rewrite-step bound governing that stage.</param>
/// <param name="AmountReached">The count, word length, or rewrite-step count reached at the outcome.</param>
public readonly record struct NormalFormBasisStatus(
    NormalFormBoundedness Boundedness,
    bool IsKnownFinite,
    NormalFormBasisOutcome Outcome,
    NormalFormBasisStage Stage,
    long ConfiguredBound,
    long AmountReached
);
/// <summary>
/// The presentation: generators, charged rules, the grading, and the construction-time classifications the kernel
/// reads — the rounding lane, the normal-form enumeration, the compiled cell table, and the per-cell term bound.
/// Immutable once built, so it is shared freely and read from any thread.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// <b>Normal forms and keys.</b> Construction closes the generators under right multiplication and normalization,
/// discovering every irreducible word. When the dense basis is available the words are sorted into the canonical order —
/// ascending by length, then lexicographically by generator symbol — and a normal form's key is its index in that
/// order, so a caller who knows the presentation can recompute any key without asking. When the dense basis is not
/// available (<see cref="HasCompiledNormalFormBasis"/> is <see langword="false"/>) the key is instead the injective mixed-radix packing
/// <c>Σ (symbolᵢ + 1)·(GeneratorCount + 1)ⁱ</c>, which sends the empty word to zero.
/// </para>
/// <para>
/// <b>The window.</b> A positive <see cref="WindowDegree"/> annihilates every term whose summed generator degree
/// exceeds it — how a free presentation is made finite without one annihilation rule per over-long word.
/// </para>
/// <para>
/// <b>Admission owns every sequence.</b> Generator boundaries, generator charges, and every rule pattern, packed
/// replacement and charge sequence are copied before validation or compilation. Mutating caller-owned arrays after
/// <see cref="Create"/> thus cannot change the interpreted normalizer, the compiled cells, functor admission, or
/// concurrent readers.
/// </para>
/// <para>
/// <b>The compiled cells.</b> Every ordered pair of normal forms is grafted and rewritten once, at construction, by the
/// interpreted normalizer; the resulting charged combinations are the compiled product table. Nothing about that build
/// assumes the product associates, so a quasialgebra floor compiles exactly as an associative presentation does.
/// </para>
/// <para>
/// <b>Re-association.</b> A <see cref="RuleKind.Reassociate"/> rule carrying one charge gives every bracket splice that
/// charge; one carrying a charge per ordered generator triple declares the associator 3-cochain, and the splice charge
/// is then read at the three normal-form keys the bracket joins. The second shape is admitted only where a triple of
/// keys can name it: the generators must BE the normal forms, every product of two of them must be a single normal
/// form, and the two bracketings of a triple must reach the same one. Without those three facts a bracket has no charge
/// to carry at all, so the presentation is refused rather than approximated.
/// </para>
/// <para>
/// <b>Both shapes must be normalized at the unit</b>, which is the fourth refusal and is the one that keeps
/// <see cref="PresentedAlgebra{TValue, TOps}.TryNormalize"/> a function of the element rather than of its spelling. A
/// factor equal to the unit is written either as its own generator letter or as the empty product, and the empty
/// product carries no letter for a splice to charge with — so a charge sitting at a triple that names the unit is one
/// the term representation cannot carry, and the two spellings of one element would answer differently. Hence a
/// declared 3-cochain must charge the material's one wherever the unit key appears in the triple, and a uniform charge
/// must BE the material's one. Neither is a budget: no scalar can relate two spellings of the same element.
/// </para>
/// <para>
/// Those four are the facts that make the table readable, and they are all that is enforced. Whether a declared
/// cochain is the associator of this product — whether <c>σ(b,c)·σ(a,b⊗c)</c> is <c>α(a,b,c)·σ(a,b)·σ(a⊗b,c)</c> on
/// every triple — is a mathematical fact about the declared data, so it is computed rather than thrown: a term tree
/// normalizes to the bracketing's own nested products exactly when the declaration is faithful, and
/// <see cref="PresentationCertificate{TValue}.IsCoherent"/> reports separately whether the declaration is
/// route-independent. A cochain that is neither still runs; it simply describes a different object than the product
/// does, and the difference is observable rather than hidden.
/// </para>
/// </remarks>
public sealed class ChargedPresentation<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    // A colour index numbers a boundary wire, and ColourCount is one past the largest index any generator mentions — so
    // it is exactly the length a caller sizes a colour-indexed structure by, and a quiver-shaped one by its square. The
    // bound is therefore taken HERE, before that allocation is made against a number this class produced: unchecked,
    // (colour + 1) turns int.MaxValue into a negative and a negative colour into a count that forgets the wire entirely,
    // and either way ColourCount comes back zero for a presentation whose generators mention a colour.
    private const int MaximumColourCount = 4096;
    // The construction budget. Discovery stops with a typed capacity or normalization outcome once a bound is passed;
    // no such outcome is called infinite. The normal-form cap also bounds the dense cell table at its square.
    private const int MaximumNormalFormCount = 512;
    // A declared 3-cochain is one charge per ordered generator triple, so its size is the generator count cubed. The cap
    // is the tensor's, and it is on the DECLARATION rather than on the normal-form set, which the cube would otherwise
    // blow past long before the normal-form cap did.
    private const int MaximumReassociationGenerators = 64;
    private const int MaximumWordLength = 256;
    private const long RewriteStepLimit = (1L << 20);

    private readonly TValue[] m_cellCharge;
    private readonly long[] m_cellStart;
    private readonly long[] m_cellTarget;
    private readonly int[] m_emptyPatternRules;
    private readonly TValue[] m_generatorCharges;
    private readonly Generator[] m_generators;
    private readonly TValue[] m_identityCharges;
    private readonly long[] m_identityKeys;
    private readonly int[] m_normalFormStart;
    private readonly int[] m_normalFormSymbols;
    private readonly int[] m_ruleBucket;
    private readonly int[] m_ruleBucketStart;
    private readonly RewriteRule<TValue>[] m_rules;
    private readonly TValue[] m_spliceCharge;
    private readonly long[] m_spliceFold;
    private readonly int m_spliceScale;
    private readonly int m_spliceStride;
    private readonly long m_spliceUnit;
    private readonly int[] m_trieChild;
    private readonly int[] m_trieKey;

    private enum RewriteFailure {
        None,
        WordCapacity,
        StepLimit,
        KeyCapacity,
    }

    private ChargedPresentation(
        Generator[] generators,
        RewriteRule<TValue>[] rules,
        TValue[] generatorCharges,
        TOps material,
        int windowDegree,
        int colourCount
    ) {
        var declared = ReassociationRuleIndex(rules: rules);
        var declaredTable = ((declared >= 0) && (1 != rules[declared].TermCount));
        var boundedness = (((windowDegree > 0) && generators.All(predicate: static generator => (generator.Degree > 0)))
            ? NormalFormBoundedness.DeclaredFinite
            : NormalFormBoundedness.Unknown
        );

        BasisStatus = new(
            AmountReached: 0,
            Boundedness: boundedness,
            ConfiguredBound: MaximumNormalFormCount,
            IsKnownFinite: (NormalFormBoundedness.DeclaredFinite == boundedness),
            Outcome: NormalFormBasisOutcome.CapacityObstructed,
            Stage: NormalFormBasisStage.Discovery
        );
        ColourCount = colourCount;
        HasCompiledNormalFormBasis = false;
        HasFiniteNormalForms = BasisStatus.IsKnownFinite;
        Lane = ClassifyLane(
            generatorCharges: generatorCharges,
            rules: rules
        );
        Material = material;
        WindowDegree = windowDegree;
        m_cellCharge = [];
        m_cellStart = [];
        m_cellTarget = [];
        m_generators = generators;
        m_generatorCharges = generatorCharges;
        m_identityCharges = [];
        m_identityKeys = [];
        m_normalFormStart = [];
        m_normalFormSymbols = [];
        m_rules = rules;
        m_trieChild = [];
        m_trieKey = [];

        // The degenerate table IS the uniform regime: both strides are zero, so every splice lookup lands on the one
        // declared charge and the key fold is constant. There is one lookup and one walk, never a second path.
        m_spliceCharge = [(((declared < 0) || declaredTable)
            ? material.One
            : rules[declared].Charges[0])];
        m_spliceFold = [0L];
        m_spliceScale = 0;
        m_spliceStride = 0;
        m_spliceUnit = 0L;

        BuildRuleIndex(
            rules: rules,
            generatorCount: generators.Length,
            bucketStart: out m_ruleBucketStart,
            bucket: out m_ruleBucket,
            emptyPatternRules: out m_emptyPatternRules
        );

        var identity = new KeyedCombination();
        var identityScratch = new Combination();
        var discovered = Discover(
            boundedness: boundedness,
            status: out var discoveryStatus
        );

        if (discovered is null) {
            BasisStatus = discoveryStatus;
            HasFiniteNormalForms = BasisStatus.IsKnownFinite;

            // No finite basis inside the budget: the packed key scheme takes over and the unit is whatever the empty
            // word normalizes to under it.
            if (TryRewriteToKeys(
                word: [],
                charge: material.One,
                stepLimit: RewriteStepLimit,
                result: identity,
                combination: identityScratch,
                stepsTaken: out _
            )) {
                m_identityCharges = identity.ChargesToArray();
                m_identityKeys = identity.KeysToArray();
            }

            RequireIndexableTriples(declaredTable: declaredTable);

            return;
        }

        NormalFormCount = discovered.Count;
        m_normalFormStart = new int[(NormalFormCount + 1)];

        var totalSymbols = 0;

        for (var index = 0; (index < NormalFormCount); ++index) {
            m_normalFormStart[index] = totalSymbols;
            totalSymbols += discovered[index].Length;
        }

        m_normalFormStart[NormalFormCount] = totalSymbols;
        m_normalFormSymbols = new int[totalSymbols];

        for (var index = 0; (index < NormalFormCount); ++index) {
            discovered[index].CopyTo(
                array: m_normalFormSymbols,
                index: m_normalFormStart[index]
            );
        }

        BuildTrie(
            child: out var trieChild,
            key: out var trieKey,
            words: discovered
        );

        m_trieChild = trieChild;
        m_trieKey = trieKey;
        HasFiniteNormalForms = true;
        // Construction below normalizes into the discovered trie. The object is not observable until the constructor
        // returns, so enable dense key lookup now; either obstruction path clears the flag with the arrays.
        HasCompiledNormalFormBasis = true;

        if (!TryRewriteToKeysDetailed(
            word: [],
            charge: material.One,
            stepLimit: RewriteStepLimit,
            result: identity,
            combination: identityScratch,
            stepsTaken: out var identitySteps,
            failure: out var identityFailure,
            amountReached: out var identityAmount
        )) {
            BasisStatus = StatusForFailure(
                amountReached: ((RewriteFailure.StepLimit == identityFailure)
                ? identitySteps
                : identityAmount),
                boundedness: boundedness,
                failure: identityFailure,
                knownFinite: true,
                stage: NormalFormBasisStage.IdentityNormalization
            );
            HasCompiledNormalFormBasis = false;
            NormalFormCount = 0;
            m_normalFormStart = [];
            m_normalFormSymbols = [];
            m_trieChild = [];
            m_trieKey = [];

            // Discovery used dense keys, but this obstruction switches the presentation back to packed keys.
            if (TryRewriteToKeys(
                word: [],
                charge: material.One,
                stepLimit: RewriteStepLimit,
                result: identity,
                combination: identityScratch,
                stepsTaken: out _
            )) {
                m_identityCharges = identity.ChargesToArray();
                m_identityKeys = identity.KeysToArray();
            }

            RequireIndexableTriples(declaredTable: declaredTable);

            return;
        }

        if (!TryBuildCells(
            charge: out var cellCharge,
            maximumCellTerms: out var maximumCellTerms,
            start: out var cellStart,
            status: out var cellStatus,
            target: out var cellTarget
        )) {
            BasisStatus = cellStatus with { Boundedness = boundedness, IsKnownFinite = true };
            HasCompiledNormalFormBasis = false;
            NormalFormCount = 0;
            m_normalFormStart = [];
            m_normalFormSymbols = [];
            m_trieChild = [];
            m_trieKey = [];

            // The successfully normalized dense unit cannot survive after its trie is discarded; rebuild it in the
            // packed fallback key regime so an uncompiled presentation still exposes its multiplicative identity.
            if (TryRewriteToKeys(
                word: [],
                charge: material.One,
                stepLimit: RewriteStepLimit,
                result: identity,
                combination: identityScratch,
                stepsTaken: out _
            )) {
                m_identityCharges = identity.ChargesToArray();
                m_identityKeys = identity.KeysToArray();
            }

            RequireIndexableTriples(declaredTable: declaredTable);

            return;
        }

        BasisStatus = new(
            Boundedness: boundedness,
            IsKnownFinite: true,
            Outcome: NormalFormBasisOutcome.Compiled,
            Stage: NormalFormBasisStage.Complete,
            ConfiguredBound: MaximumNormalFormCount,
            AmountReached: NormalFormCount
        );
        MaximumCellTerms = maximumCellTerms;
        m_cellCharge = cellCharge;
        m_cellStart = cellStart;
        m_cellTarget = cellTarget;
        m_identityCharges = identity.ChargesToArray();
        m_identityKeys = identity.KeysToArray();

        if (declaredTable) {
            m_spliceCharge = rules[declared].Charges.ToArray();
            m_spliceFold = BuildSpliceFold();
            m_spliceScale = 1;
            m_spliceStride = NormalFormCount;
            m_spliceUnit = m_identityKeys[0];
        }
    }

    /// <summary>Gets the charges of the compiled cell entries, ordered by cell then by ascending target.</summary>
    internal ReadOnlySpan<TValue> CellCharges =>
        m_cellCharge;
    /// <summary>Gets the flat offset of every ordered key pair's cell, with one trailing total.</summary>
    internal ReadOnlySpan<long> CellStarts =>
        m_cellStart;
    /// <summary>Gets the target keys of the compiled cell entries.</summary>
    internal ReadOnlySpan<long> CellTargets =>
        m_cellTarget;
    /// <summary>Gets the charges of the unit's normal-form decomposition.</summary>
    internal ReadOnlySpan<TValue> IdentityCharges =>
        m_identityCharges;
    /// <summary>Gets the keys of the unit's normal-form decomposition.</summary>
    internal ReadOnlySpan<long> IdentityKeys =>
        m_identityKeys;
    /// <summary>Gets the charged rewrite rules, in the order the normalizer tries them.</summary>
    /// <remarks>The single home for reading a presentation's relations back, as <see cref="GeneratorOf"/> is for its
    /// generators. A morphism out of this presentation evaluates exactly these on its images.</remarks>
    internal ReadOnlySpan<RewriteRule<TValue>> Rules =>
        m_rules;
    /// <summary>Gets the key the unit word reaches, which is the accumulator a bracketless term starts from.</summary>
    internal long SpliceUnitKey =>
        m_spliceUnit;

    /// <summary>Gets the typed outcome of normal-form discovery and dense-basis compilation.</summary>
    public NormalFormBasisStatus BasisStatus { get; }
    /// <summary>Gets the number of boundary colours the generators mention.</summary>
    public int ColourCount { get; }
    /// <summary>Gets the number of generators.</summary>
    public int GeneratorCount => m_generators.Length;
    /// <summary>Indicates whether the complete normal-form list, trie, and dense product table are available.</summary>
    public bool HasCompiledNormalFormBasis { get; }
    /// <summary>Indicates whether the normal-form language is known finite, either from a declared positive-degree
    /// window or from completed discovery. This does not imply that the dense basis was small enough to compile; use
    /// <see cref="HasCompiledNormalFormBasis"/> for that capability.</summary>
    public bool HasFiniteNormalForms { get; }
    /// <summary>Indicates whether the presentation declares a re-association 3-cochain, so a bracket's charge depends on
    /// the three normal forms it joins rather than being one uniform charge.</summary>
    /// <remarks>It changes no cell and no product — the compiled table is the same table either way. What it changes is
    /// what a bracketed <see cref="Term"/> normalizes to, which under a uniform charge of one is bracket-inert.</remarks>
    public bool HasLiveReassociation =>
        (0 != m_spliceScale);
    /// <summary>Gets the value-independent classification of every charge the presentation carries.</summary>
    public ChargeLane Lane { get; }
    /// <summary>Gets the material.</summary>
    public TOps Material { get; }
    /// <summary>Gets the largest number of terms one result key can receive from a single product — the exact size of
    /// the fused accumulator, derived from the presentation as a multi-limb width is derived from a degree.</summary>
    public int MaximumCellTerms { get; }
    /// <summary>Gets the number of compiled normal forms, or zero when <see cref="HasCompiledNormalFormBasis"/> is
    /// <see langword="false"/>.</summary>
    public int NormalFormCount { get; }
    /// <summary>Gets the degree bound above which every term is annihilated, or zero for no bound.</summary>
    public int WindowDegree { get; }

    /// <summary>Gets the charge the basis element of one generator carries.</summary>
    internal TValue GeneratorCharge(int symbol) =>
        m_generatorCharges[symbol];
    /// <summary>Gets one generator, boundaries included.</summary>
    /// <remarks>The single home for reading a presentation's own generator data back. A catalogue entry that encodes
    /// something in the boundaries — a quiver's endpoints, a poset interval's — recovers it here rather than rebuilding
    /// the enumeration that produced it.</remarks>
    internal Generator GeneratorOf(int symbol) =>
        m_generators[symbol];
    /// <summary>Gets the charge one bracket splice contributes: <c>x·(y·z)</c> is that charge times <c>(x·y)·z</c>.</summary>
    /// <remarks>Under the uniform regime both strides are zero, so the three keys fall out of the index and the one
    /// declared charge is returned; under a declared 3-cochain the index is the row-major triple. It is the same table
    /// read either way.</remarks>
    internal TValue SpliceCharge(long left, long middle, long right) =>
        m_spliceCharge[((int)(((((left * m_spliceStride) + middle) * m_spliceStride) + right) * m_spliceScale))];
    /// <summary>Gets the key a word's normal form reaches after one more letter.</summary>
    /// <remarks>Under the uniform regime the stride is zero and the fold is constant, which is exactly right: a uniform
    /// charge does not read the keys it joins.</remarks>
    internal long SpliceFold(long key, int letter) =>
        m_spliceFold[((int)((((key * m_spliceStride) + letter) * m_spliceScale)))];
    /// <summary>Maps an irreducible word to its key under whichever key scheme this presentation uses.</summary>
    internal bool TryKeyOf(ReadOnlySpan<int> word, out long key) {
        if (!HasCompiledNormalFormBasis) {
            return TryPackWord(
                key: out key,
                word: word
            );
        }

        var generatorCount = GeneratorCount;
        var node = 0;

        for (var index = 0; (index < word.Length); ++index) {
            var next = m_trieChild[((node * generatorCount) + word[index])];

            if (next < 0) {
                key = 0L;

                return false;
            }

            node = next;
        }

        var found = m_trieKey[node];

        key = found;

        return (found >= 0);
    }
    /// <summary>Packs a word into the injective mixed-radix key the non-finite regime uses.</summary>
    internal bool TryPackWord(ReadOnlySpan<int> word, out long key) {
        var radix = (((long)GeneratorCount) + 1L);
        var scale = 1L;

        key = 0L;

        for (var index = 0; (index < word.Length); ++index) {
            if (scale > (long.MaxValue / radix)) {
                key = 0L;

                return false;
            }

            key += (scale * (word[index] + 1L));
            scale *= radix;
        }

        return true;
    }
    /// <summary>Rewrites a charged word to a charged combination of irreducible words, bounded.</summary>
    /// <returns><see langword="false"/> when the step limit was exhausted or a term outgrew the word cap.</returns>
    internal bool TryRewrite(ReadOnlySpan<int> word, TValue charge, long stepLimit, Combination combination, out long stepsTaken) =>
        TryRewriteDetailed(
            amountReached: out _,
            charge: charge,
            combination: combination,
            failure: out _,
            stepLimit: stepLimit,
            stepsTaken: out stepsTaken,
            word: word
        );
    /// <summary>Rewrites a charged word to a charged combination of normal-form keys through a reusable scratch, which
    /// <see cref="TryRewrite"/> clears at entry, so one may be held for the life of a loop.</summary>
    /// <returns><see langword="false"/> when the step limit was exhausted or a normal form fell outside the key scheme.</returns>
    internal bool TryRewriteToKeys(ReadOnlySpan<int> word, TValue charge, long stepLimit, KeyedCombination result, Combination combination, out long stepsTaken) {
        return TryRewriteToKeysDetailed(
            amountReached: out _,
            charge: charge,
            combination: combination,
            failure: out _,
            result: result,
            stepLimit: stepLimit,
            stepsTaken: out stepsTaken,
            word: word
        );
    }
    /// <summary>Unpacks a mixed-radix key back into its word.</summary>
    internal bool TryUnpackWord(long key, Span<int> word, out int length) {
        var radix = (((long)GeneratorCount) + 1L);

        length = 0;

        if (key < 0L) { return false; }
        if (radix <= 1L) { return (0L == key); }

        while (0L != key) {
            if (length >= word.Length) { return false; }

            var digit = ((int)(key % radix));

            if (0 == digit) { return false; }

            word[length++] = (digit - 1);
            key /= radix;
        }

        return true;
    }
    /// <summary>Copies out the generator word one key names, under whichever key scheme this presentation uses.</summary>
    /// <returns><see langword="false"/> when the key names no normal form, or its word does not fit the buffer.</returns>
    internal bool TryWordOf(long key, Span<int> word, out int length) {
        if (!HasCompiledNormalFormBasis) {
            return TryUnpackWord(
                key: key,
                length: out length,
                word: word
            );
        }

        length = 0;

        if (
            (key < 0L) ||
            (key >= NormalFormCount)
        ) { return false; }

        var index = ((int)key);
        var start = m_normalFormStart[index];
        var symbols = (m_normalFormStart[(index + 1)] - start);

        if (symbols > word.Length) { return false; }

        m_normalFormSymbols.AsSpan(
            length: symbols,
            start: start
        ).CopyTo(destination: word);
        length = symbols;

        return true;
    }

    // Returns the colour count one mentioned colour forces, refusing an index no colour-indexed structure could be
    // built over.
    private static int AdmitColour(int colour, int symbol, string boundary, string paramName) {
        if (colour < 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: colour,
                message: $"Generator {symbol} names {colour} on its {boundary} boundary; a colour index numbers a wire and starts at zero.",
                paramName: paramName
            );
        }

        if (colour >= MaximumColourCount) {
            throw new ArgumentOutOfRangeException(
                actualValue: colour,
                message: $"Generator {symbol} names colour {colour} on its {boundary} boundary, at or above the {MaximumColourCount} colours a presentation carries.",
                paramName: paramName
            );
        }

        return (colour + 1);
    }
    // Buckets the word-matched rules by their pattern's first symbol, so a redex search at one position tries only the
    // rules that can possibly match there. Declaration order is preserved inside every bucket, so the search is exactly
    // the leftmost-then-declaration-order rule it was before, only cheaper.
    private static void BuildRuleIndex(RewriteRule<TValue>[] rules, int generatorCount, out int[] bucketStart, out int[] bucket, out int[] emptyPatternRules) {
        var counts = new int[(generatorCount + 1)];
        var empties = new List<int>();

        for (var index = 0; (index < rules.Length); ++index) {
            var rule = rules[index];

            if (RuleKind.Reassociate == rule.Kind) { continue; }

            if (0 == rule.Pattern.Length) {
                empties.Add(item: index);

                continue;
            }

            ++counts[(rule.Pattern[0] + 1)];
        }

        bucketStart = new int[(generatorCount + 1)];

        for (var symbol = 0; (symbol < generatorCount); ++symbol) { bucketStart[(symbol + 1)] = (bucketStart[symbol] + counts[(symbol + 1)]); }

        bucket = new int[bucketStart[generatorCount]];
        emptyPatternRules = [.. empties];

        var cursor = new int[generatorCount];

        for (var index = 0; (index < rules.Length); ++index) {
            var rule = rules[index];

            if (
                (RuleKind.Reassociate == rule.Kind) ||
                (0 == rule.Pattern.Length)
            ) { continue; }

            var symbol = rule.Pattern[0];

            bucket[(bucketStart[symbol] + cursor[symbol])] = index;
            ++cursor[symbol];
        }
    }
    // The key fold a declared 3-cochain is read through, and the four facts that make the reading possible. Each
    // refusal is an impossibility rather than a budget: a triple of keys that names no bracket, a product that is not a
    // single normal form, two bracketings that reach different normal forms, or a charge sitting where the unit sits
    // all leave a splice with nothing to charge, or with a charge no spelling of the term can pay.
    private long[] BuildSpliceFold() {
        var count = NormalFormCount;

        if (count != GeneratorCount) {
            throw new ArgumentException(
                message: "A declared re-association 3-cochain indexes generator triples, which name normal forms only when every generator is its own normal form.",
                paramName: "rules"
            );
        }

        for (var key = 0; (key < count); ++key) {
            if (
                (1 != (m_normalFormStart[(key + 1)] - m_normalFormStart[key])) ||
                (key != m_normalFormSymbols[m_normalFormStart[key]])
            ) {
                throw new ArgumentException(
                    message: "A declared re-association 3-cochain indexes generator triples, which name normal forms only when every generator is its own normal form.",
                    paramName: "rules"
                );
            }
        }

        if (1 != m_identityKeys.Length) {
            throw new ArgumentException(
                message: "A declared re-association 3-cochain reads the key a bracket's contents reach, so the unit must be a single normal form.",
                paramName: "rules"
            );
        }

        var fold = new long[(count * count)];

        for (var cell = 0; (cell < fold.Length); ++cell) {
            if (1L != (m_cellStart[(cell + 1)] - m_cellStart[cell])) {
                throw new ArgumentException(
                    message: "A bracket carries a charge only where every product of two normal forms is a single normal form.",
                    paramName: "rules"
                );
            }

            fold[cell] = m_cellTarget[((int)m_cellStart[cell])];
        }

        for (var left = 0; (left < count); ++left) {
            for (var middle = 0; (middle < count); ++middle) {
                var pair = ((int)fold[((left * count) + middle)]);

                for (var right = 0; (right < count); ++right) {
                    if (fold[((pair * count) + right)] != fold[((left * count) + ((int)fold[((middle * count) + right)]))]) {
                        throw new ArgumentException(
                            message: "Re-association carries no charge here: the two bracketings of a triple reach different normal forms, so no charge relates them.",
                            paramName: "rules"
                        );
                    }
                }
            }
        }

        // Normalization at the unit, which the rebalancing walk assumes and cannot pay for: a factor equal to the unit
        // is spelled either as its own letter or as the empty product, and the empty product carries no letter to
        // charge with. A cochain charging where the unit key sits therefore names a charge no spelling can carry, and
        // the two spellings of one element would normalize to different values.
        var comparer = EqualityComparer<TValue>.Default;
        var one = Material.One;
        var unit = ((int)m_identityKeys[0]);

        for (var first = 0; (first < count); ++first) {
            for (var second = 0; (second < count); ++second) {
                if (
                    !comparer.Equals(
                    x: m_spliceCharge[((((unit * count) + first) * count) + second)],
                    y: one
                ) ||
                    !comparer.Equals(
                    x: m_spliceCharge[((((first * count) + unit) * count) + second)],
                    y: one
                ) ||
                    !comparer.Equals(
                    x: m_spliceCharge[((((first * count) + second) * count) + unit)],
                    y: one
                )
                ) {
                    throw new ArgumentException(
                        message: "A declared re-association 3-cochain charges the material's one wherever the unit appears in the triple: the unit spelled as the empty product carries no letter for a splice to charge with, so any other charge makes two spellings of one element differ.",
                        paramName: "rules"
                    );
                }
            }
        }

        return fold;
    }
    private void BuildTrie(List<int[]> words, out int[] child, out int[] key) {
        var generatorCount = GeneratorCount;
        var childList = new List<int>();
        var keyList = new List<int> { -1 };

        for (var slot = 0; (slot < generatorCount); ++slot) { childList.Add(item: -1); }

        for (var index = 0; (index < words.Count); ++index) {
            var node = 0;

            foreach (var symbol in words[index]) {
                var slot = ((node * generatorCount) + symbol);

                if (childList[slot] < 0) {
                    childList[slot] = keyList.Count;
                    keyList.Add(item: -1);

                    for (var fresh = 0; (fresh < generatorCount); ++fresh) { childList.Add(item: -1); }
                }

                node = childList[slot];
            }

            keyList[node] = index;
        }

        child = [.. childList];
        key = [.. keyList];
    }
    // Exact when the carrier is the house scalar and every charge is an exact integer of it; General otherwise. Every
    // other carrier is exact arithmetic outright, so its lane is reported Exact and its fused sums ignore the value.
    private static ChargeLane ClassifyLane(RewriteRule<TValue>[] rules, TValue[] generatorCharges) {
        if (typeof(TValue) != typeof(FixedQ4816)) { return ChargeLane.Exact; }

        foreach (var charge in generatorCharges) {
            if (!IsExactInteger(value: charge)) { return ChargeLane.General; }
        }

        foreach (var rule in rules) {
            foreach (var charge in rule.Charges) {
                if (!IsExactInteger(value: charge)) { return ChargeLane.General; }
            }
        }

        return ChargeLane.Exact;
    }
    private static int CompareWordArrays(int[] left, int[] right) =>
        CompareWords(
            left: left,
            right: right
        );
    // The canonical order: shorter first, then lexicographically by generator symbol. It is both the well-founded order
    // the rules decrease in and the enumeration order the keys index.
    private static int CompareWords(ReadOnlySpan<int> left, ReadOnlySpan<int> right) {
        if (left.Length != right.Length) {
            return ((left.Length < right.Length)
                ? -1
                : 1
            );
        }

        for (var index = 0; (index < left.Length); ++index) {
            if (left[index] != right[index]) {
                return ((left[index] < right[index])
                    ? -1
                    : 1
                );
            }
        }

        return 0;
    }
    private static Generator[] CopyGenerators(ReadOnlySpan<Generator> generators) {
        var copy = new Generator[generators.Length];

        for (var index = 0; (index < copy.Length); ++index) {
            var generator = generators[index];

            copy[index] = new Generator(
                symbol: generator.Symbol,
                inputs: generator.Inputs.ToArray(),
                outputs: generator.Outputs.ToArray(),
                degree: generator.Degree
            );
        }

        return copy;
    }
    private static RewriteRule<TValue>[] CopyRules(ReadOnlySpan<RewriteRule<TValue>> rules) {
        var copy = new RewriteRule<TValue>[rules.Length];

        for (var index = 0; (index < copy.Length); ++index) {
            var rule = rules[index];

            copy[index] = new RewriteRule<TValue>(
                kind: rule.Kind,
                pattern: rule.Pattern.ToArray(),
                replacement: rule.Replacement.ToArray(),
                charges: rule.Charges.ToArray()
            );
        }

        return copy;
    }
    // Closes the generators under right multiplication, collecting every irreducible word. A refusal carries the exact
    // capacity or normalization budget that stopped it; it never infers infinitude from reaching a finite budget.
    // Membership is decided by a canonically ordered list and a binary search, never by a hash of a rendered word.
    private List<int[]>? Discover(NormalFormBoundedness boundedness, out NormalFormBasisStatus status) {
        var combination = new Combination();
        var failureStatus = default(NormalFormBasisStatus);
        var frontier = new List<int[]>();
        var ordered = new List<int[]>();

        bool Admit(int[] candidate) {
            var low = 0;
            var high = ordered.Count;

            while (low < high) {
                var middle = ((low + high) >> 1);
                var order = CompareWordArrays(
                    left: ordered[middle],
                    right: candidate
                );

                if (0 == order) { return true; }

                if (order < 0) { low = (middle + 1); } else { high = middle; }
            }

            if (ordered.Count >= MaximumNormalFormCount) {
                failureStatus = new(
                    Boundedness: boundedness,
                    IsKnownFinite: (NormalFormBoundedness.DeclaredFinite == boundedness),
                    Outcome: NormalFormBasisOutcome.CapacityObstructed,
                    Stage: NormalFormBasisStage.Discovery,
                    ConfiguredBound: MaximumNormalFormCount,
                    AmountReached: ordered.Count
                );

                return false;
            }

            frontier.Add(item: candidate);
            ordered.Insert(
                index: low,
                item: candidate
            );

            return true;
        }

        bool AdmitNormalizationOf(ReadOnlySpan<int> word) {
            if (!TryRewriteDetailed(
                word: word,
                charge: Material.One,
                stepLimit: RewriteStepLimit,
                combination: combination,
                stepsTaken: out var stepsTaken,
                failure: out var failure,
                amountReached: out var amountReached
            )) {
                failureStatus = StatusForFailure(
                    amountReached: ((RewriteFailure.StepLimit == failure)
                    ? stepsTaken
                    : amountReached),
                    boundedness: boundedness,
                    failure: failure,
                    knownFinite: (NormalFormBoundedness.DeclaredFinite == boundedness),
                    stage: NormalFormBasisStage.Discovery
                );

                return false;
            }

            for (var index = 0; (index < combination.Count); ++index) {
                if (!Admit(candidate: combination.WordAt(index: index).ToArray())) { return false; }
            }

            return true;
        }

        if (!AdmitNormalizationOf(word: [])) {
            status = failureStatus;

            return null;
        }

        for (var symbol = 0; (symbol < GeneratorCount); ++symbol) {
            if (!AdmitNormalizationOf(word: [symbol])) {
                status = failureStatus;

                return null;
            }
        }

        Span<int> extended = stackalloc int[MaximumWordLength];

        for (var cursor = 0; (cursor < frontier.Count); ++cursor) {
            var word = frontier[cursor];

            if ((word.Length + 1) > MaximumWordLength) {
                status = new(
                    Boundedness: boundedness,
                    IsKnownFinite: (NormalFormBoundedness.DeclaredFinite == boundedness),
                    Outcome: NormalFormBasisOutcome.CapacityObstructed,
                    Stage: NormalFormBasisStage.Discovery,
                    ConfiguredBound: MaximumWordLength,
                    AmountReached: (word.Length + 1)
                );

                return null;
            }

            for (var symbol = 0; (symbol < GeneratorCount); ++symbol) {
                var candidate = extended.Slice(
                    start: 0,
                    length: (word.Length + 1)
                );

                word.CopyTo(destination: candidate);
                candidate[word.Length] = symbol;

                if (!AdmitNormalizationOf(word: candidate)) {
                    status = failureStatus;

                    return null;
                }
            }
        }

        status = new(
            Boundedness: boundedness,
            IsKnownFinite: true,
            Outcome: NormalFormBasisOutcome.Compiled,
            Stage: NormalFormBasisStage.Discovery,
            ConfiguredBound: MaximumNormalFormCount,
            AmountReached: ordered.Count
        );

        return ordered;
    }
    private bool ExceedsWindow(ReadOnlySpan<int> word) {
        if (0 == WindowDegree) { return false; }

        var degree = 0;

        foreach (var symbol in word) {
            degree += m_generators[symbol].Degree;

            if (degree > WindowDegree) { return true; }
        }

        return false;
    }
    private static bool IsExactInteger(TValue value) {
        var raw = Unsafe.BitCast<TValue, FixedQ4816>(source: value).Value;

        return (raw == ((raw >> FixedQ4816.FractionBitCount) << FixedQ4816.FractionBitCount));
    }
    // The one re-association rule a presentation honours: the first declared. Its charge count decides the regime — one
    // charge is the uniform splice, one per ordered generator triple is the associator 3-cochain.
    private static int ReassociationRuleIndex(RewriteRule<TValue>[] rules) {
        for (var index = 0; (index < rules.Length); ++index) {
            if (RuleKind.Reassociate == rules[index].Kind) { return index; }
        }

        return -1;
    }
    // A declared 3-cochain needs the compiled cells to index it, so a presentation that produced none cannot carry one.
    private static void RequireIndexableTriples(bool declaredTable) {
        if (declaredTable) {
            throw new ArgumentException(
                message: "A declared re-association 3-cochain is read through the compiled cells, which this presentation has no finite basis to build.",
                paramName: "rules"
            );
        }
    }
    private static NormalFormBasisStatus StatusForFailure(
        NormalFormBoundedness boundedness,
        bool knownFinite,
        NormalFormBasisStage stage,
        RewriteFailure failure,
        long amountReached
    ) =>
        new(
            AmountReached: amountReached,
            Boundedness: boundedness,
            ConfiguredBound: (failure switch {
                RewriteFailure.StepLimit => RewriteStepLimit,
                RewriteFailure.KeyCapacity => long.MaxValue,
                _ => MaximumWordLength,
            }),
            IsKnownFinite: knownFinite,
            Outcome: ((RewriteFailure.StepLimit == failure)
            ? NormalFormBasisOutcome.NormalizationExhausted
            : NormalFormBasisOutcome.CapacityObstructed),
            Stage: stage
        );
    // The compiled product table: every ordered pair of normal forms grafted and rewritten once. No step assumes the
    // product associates, so a quasialgebra floor compiles exactly as an associative presentation does. Cells are
    // produced in the canonical (left, right) order, so the flat arrays are already the table.
    private bool TryBuildCells(
        out long[] start,
        out long[] target,
        out TValue[] charge,
        out int maximumCellTerms,
        out NormalFormBasisStatus status
    ) {
        var cell = new KeyedCombination();
        var combination = new Combination();
        var count = NormalFormCount;
        var material = Material;
        var buildCharge = new List<TValue>();
        var buildTarget = new List<long>();
        var perTarget = new int[count];

        maximumCellTerms = 0;
        charge = [];
        start = new long[((count * count) + 1)];
        status = default;
        target = [];

        Span<int> graft = stackalloc int[MaximumWordLength];

        for (var left = 0; (left < count); ++left) {
            var leftWord = NormalFormWord(key: left);

            for (var right = 0; (right < count); ++right) {
                var rightWord = NormalFormWord(key: right);
                var total = (leftWord.Length + rightWord.Length);

                if (total > MaximumWordLength) {
                    status = new(
                        AmountReached: total,
                        Boundedness: NormalFormBoundedness.Unknown,
                        ConfiguredBound: MaximumWordLength,
                        IsKnownFinite: true,
                        Outcome: NormalFormBasisOutcome.CapacityObstructed,
                        Stage: NormalFormBasisStage.ProductTable
                    );

                    return false;
                }

                leftWord.CopyTo(destination: graft);
                rightWord.CopyTo(destination: graft.Slice(
                    start: leftWord.Length,
                    length: rightWord.Length
                ));

                if (!TryRewriteToKeysDetailed(
                    word: graft.Slice(
                        length: total,
                        start: 0
                    ),
                    charge: material.One,
                    stepLimit: RewriteStepLimit,
                    result: cell,
                    combination: combination,
                    stepsTaken: out var stepsTaken,
                    failure: out var failure,
                    amountReached: out var amountReached
                )) {
                    status = StatusForFailure(
                        amountReached: ((RewriteFailure.StepLimit == failure)
                        ? stepsTaken
                        : amountReached),
                        boundedness: NormalFormBoundedness.Unknown,
                        failure: failure,
                        knownFinite: true,
                        stage: NormalFormBasisStage.ProductTable
                    );

                    return false;
                }

                start[((left * count) + right)] = buildTarget.Count;

                for (var index = 0; (index < cell.Count); ++index) {
                    var key = cell.KeyAt(index: index);

                    buildCharge.Add(item: cell.ChargeAt(index: index));
                    buildTarget.Add(item: key);
                    ++perTarget[((int)key)];
                }
            }
        }

        start[(count * count)] = buildTarget.Count;
        charge = [.. buildCharge];
        target = [.. buildTarget];

        foreach (var terms in perTarget) {
            maximumCellTerms = Math.Max(
                val1: maximumCellTerms,
                val2: terms
            );
        }

        status = new(
            AmountReached: (count * count),
            Boundedness: NormalFormBoundedness.Unknown,
            ConfiguredBound: (MaximumNormalFormCount * MaximumNormalFormCount),
            IsKnownFinite: true,
            Outcome: NormalFormBasisOutcome.Compiled,
            Stage: NormalFormBasisStage.ProductTable
        );

        return true;
    }
    // Leftmost position, then declaration order among the rules whose pattern can start there. A Reassociate rule is
    // never matched here: it is the term flattener's charge, not a word rewrite. An empty pattern matches the empty
    // word only.
    private bool TryFindRedex(ReadOnlySpan<int> word, out int position, out int ruleIndex) {
        position = 0;

        if (0 == word.Length) {
            if (0 != m_emptyPatternRules.Length) {
                ruleIndex = m_emptyPatternRules[0];

                return true;
            }

            ruleIndex = -1;

            return false;
        }

        for (var start = 0; (start < word.Length); ++start) {
            var symbol = word[start];
            var last = m_ruleBucketStart[(symbol + 1)];

            for (var slot = m_ruleBucketStart[symbol]; (slot < last); ++slot) {
                var index = m_ruleBucket[slot];
                var pattern = m_rules[index].Pattern;

                if (
                    ((start + pattern.Length) <= word.Length) &&
                    word.Slice(
                    start: start,
                    length: pattern.Length
                ).SequenceEqual(other: pattern)
                ) {
                    position = start;
                    ruleIndex = index;

                    return true;
                }
            }
        }

        ruleIndex = -1;

        return false;
    }
    private bool TryRewriteDetailed(
        ReadOnlySpan<int> word,
        TValue charge,
        long stepLimit,
        Combination combination,
        out long stepsTaken,
        out RewriteFailure failure,
        out long amountReached
    ) {
        var material = Material;

        combination.Clear();
        amountReached = 0L;
        failure = RewriteFailure.None;
        stepsTaken = 0L;

        if (word.Length > MaximumWordLength) {
            amountReached = word.Length;
            failure = RewriteFailure.WordCapacity;

            return false;
        }

        if (
            material.IsZero(value: charge) ||
            ExceedsWindow(word: word)
        ) { return true; }

        Span<int> rewritten = stackalloc int[MaximumWordLength];

        combination.Merge(
            charge: charge,
            material: material,
            word: word
        );

        while (true) {
            var index = combination.Count;
            var position = 0;
            var ruleIndex = -1;

            while (--index >= 0) {
                if (TryFindRedex(
                    word: combination.WordAt(index: index),
                    position: out position,
                    ruleIndex: out ruleIndex
                )) { break; }
            }

            if (index < 0) { return true; }
            if (stepsTaken >= stepLimit) {
                amountReached = stepsTaken;
                failure = RewriteFailure.StepLimit;

                return false;
            }

            ++stepsTaken;

            var rule = m_rules[ruleIndex];
            var patternLength = rule.Pattern.Length;
            var source = combination.TakeAt(
                charge: out var sourceCharge,
                index: index
            );
            var replacement = rule.Replacement;
            var offset = 0;

            for (var term = 0; (term < rule.TermCount); ++term) {
                var length = replacement[offset++];
                var grown = ((source.Length - patternLength) + length);

                if (grown > MaximumWordLength) {
                    amountReached = grown;
                    failure = RewriteFailure.WordCapacity;

                    return false;
                }

                var termCharge = material.Multiply(
                    left: sourceCharge,
                    right: rule.Charges[term]
                );

                if (!material.IsZero(value: termCharge)) {
                    var product = rewritten.Slice(
                        length: grown,
                        start: 0
                    );

                    source.AsSpan(
                        length: position,
                        start: 0
                    ).CopyTo(destination: product);
                    replacement.Slice(
                        length: length,
                        start: offset
                    ).CopyTo(destination: product.Slice(
                        length: length,
                        start: position
                    ));
                    source.AsSpan(start: (position + patternLength)).CopyTo(destination: product.Slice(start: (position + length)));

                    if (!ExceedsWindow(word: product)) {
                        combination.Merge(
                            charge: termCharge,
                            material: material,
                            word: product
                        );
                    }
                }

                offset += length;
            }
        }
    }
    private bool TryRewriteToKeysDetailed(
        ReadOnlySpan<int> word,
        TValue charge,
        long stepLimit,
        KeyedCombination result,
        Combination combination,
        out long stepsTaken,
        out RewriteFailure failure,
        out long amountReached
    ) {
        result.Clear();

        if (!TryRewriteDetailed(
            amountReached: out amountReached,
            charge: charge,
            combination: combination,
            failure: out failure,
            stepLimit: stepLimit,
            stepsTaken: out stepsTaken,
            word: word
        )) {
            return false;
        }

        for (var index = 0; (index < combination.Count); ++index) {
            var normal = combination.WordAt(index: index);

            if (!TryKeyOf(
                key: out var key,
                word: normal
            )) {
                amountReached = normal.Length;
                failure = RewriteFailure.KeyCapacity;

                return false;
            }

            result.Merge(
                key: key,
                charge: combination.ChargeAt(index: index),
                material: Material
            );
        }

        amountReached = stepsTaken;
        failure = RewriteFailure.None;

        return true;
    }
    // Rule values remain caller-visible declarations, so presentation admission ENFORCES canonical charges instead of
    // silently changing those declarations. Element admission is different: FromSupport explicitly promises to
    // canonicalize, and does so through the same material member.
    private static void ValidateCanonicalCharges(
        ReadOnlySpan<RewriteRule<TValue>> rules,
        ReadOnlySpan<TValue> generatorCharges,
        TOps material
    ) {
        var comparer = EqualityComparer<TValue>.Default;

        for (var index = 0; (index < generatorCharges.Length); ++index) {
            TValue canonical;

            try {
                canonical = material.Canonicalize(value: generatorCharges[index]);
            } catch (ArgumentException exception) {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(generatorCharges),
                    actualValue: generatorCharges[index],
                    message: $"Generator charge {index} is not admitted by the material: {exception.Message}"
                );
            }

            if (!comparer.Equals(
                x: generatorCharges[index],
                y: canonical
            )) {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(generatorCharges),
                    message: $"Generator charge {index} is not the material's canonical representative."
                );
            }
        }

        for (var ruleIndex = 0; (ruleIndex < rules.Length); ++ruleIndex) {
            var charges = rules[ruleIndex].Charges;

            for (var term = 0; (term < charges.Length); ++term) {
                TValue canonical;

                try {
                    canonical = material.Canonicalize(value: charges[term]);
                } catch (ArgumentException exception) {
                    throw new ArgumentException(
                        message: $"Rule {ruleIndex} charge {term} is not admitted by the material.",
                        paramName: nameof(rules),
                        innerException: exception
                    );
                }

                if (!comparer.Equals(
                    x: charges[term],
                    y: canonical
                )) {
                    throw new ArgumentException(
                        message: $"Rule {ruleIndex} charge {term} is not the material's canonical representative.",
                        paramName: nameof(rules)
                    );
                }
            }
        }
    }
    private static void ValidateRules(ReadOnlySpan<RewriteRule<TValue>> rules, int generatorCount, TOps material) {
        for (var index = 0; (index < rules.Length); ++index) {
            var rule = rules[index];

            foreach (var symbol in rule.Pattern) {
                if (
                    (symbol < 0) ||
                    (symbol >= generatorCount)
                ) {
                    throw new ArgumentException(
                        message: "A rule pattern references a symbol outside the generator range.",
                        paramName: nameof(rules)
                    );
                }
            }

            if (RuleKind.Reassociate == rule.Kind) {
                // The uniform regime is normalized at the unit exactly when its one charge IS the unit of the material:
                // a factor equal to the algebra's unit is spelled either as a letter or as the empty product, and the
                // empty product splices one time fewer, so any other charge makes two spellings of one element answer
                // differently. Nothing relates them, so the declaration is refused rather than certified afterwards.
                if (1 == rule.TermCount) {
                    if (!EqualityComparer<TValue>.Default.Equals(
                        x: rule.Charges[0],
                        y: material.One
                    )) {
                        throw new ArgumentException(
                            message: "A uniform re-association charge is applied once per bracket splice, and the unit spelled as the empty product splices one time fewer, so a charge that is not the material's one makes two spellings of one element differ.",
                            paramName: nameof(rules)
                        );
                    }

                    continue;
                }

                if (generatorCount > MaximumReassociationGenerators) {
                    throw new ArgumentException(
                        message: $"A re-association 3-cochain is one charge per ordered generator triple, which is declared for at most {MaximumReassociationGenerators} generators.",
                        paramName: nameof(rules)
                    );
                }

                if (rule.TermCount != ((generatorCount * generatorCount) * generatorCount)) {
                    throw new ArgumentException(
                        message: "A re-association rule carries either one charge, the uniform per-splice charge, or one per ordered generator triple, the associator 3-cochain.",
                        paramName: nameof(rules)
                    );
                }

                continue;
            }

            var replacement = rule.Replacement;
            var offset = 0;

            for (var term = 0; (term < rule.TermCount); ++term) {
                if (offset >= replacement.Length) {
                    throw new ArgumentException(
                        message: "A rule's packed replacement carries fewer terms than it has charges.",
                        paramName: nameof(rules)
                    );
                }

                var length = replacement[offset++];

                if (
                    (length < 0) ||
                    ((offset + length) > replacement.Length)
                ) {
                    throw new ArgumentException(
                        message: "A rule's packed replacement is malformed.",
                        paramName: nameof(rules)
                    );
                }

                if (
                    (0 == rule.Pattern.Length) &&
                    (0 == length)
                ) {
                    throw new ArgumentException(
                        message: "An empty-pattern rule may not rewrite the unit word to itself.",
                        paramName: nameof(rules)
                    );
                }

                for (var symbol = 0; (symbol < length); ++symbol) {
                    var value = replacement[(offset + symbol)];

                    if (
                        (value < 0) ||
                        (value >= generatorCount)
                    ) {
                        throw new ArgumentException(
                            message: "A rule replacement references a symbol outside the generator range.",
                            paramName: nameof(rules)
                        );
                    }
                }

                offset += length;
            }

            if (offset != replacement.Length) {
                throw new ArgumentException(
                    message: "A rule's packed replacement carries more terms than it has charges.",
                    paramName: nameof(rules)
                );
            }
        }
    }

    /// <summary>Creates a presentation, closing its generators under normalization and classifying it once.</summary>
    /// <param name="generators">The generators; the generator at index <c>i</c> must carry symbol <c>i</c>.</param>
    /// <param name="rules">The charged rewrite rules, in the order the normalizer tries them.</param>
    /// <param name="material">The material.</param>
    /// <param name="windowDegree">A positive degree bound annihilating heavier terms, or zero for no bound.</param>
    /// <param name="generatorCharges">One coefficient per generator, carried by the basis element that generator names;
    /// an empty span gives every generator the material's one.</param>
    /// <returns>The described presentation.</returns>
    /// <exception cref="ArgumentException">A generator's symbol is not its index, a rule references a symbol outside
    /// the generator range, a rule's charge count disagrees with its packed replacement, an empty-pattern rule carries
    /// an empty replacement term, or a rule charge is not admitted by the material.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="windowDegree"/> is negative, a generator degree is
    /// negative, a generator names a boundary colour that is negative or too large for a colour-indexed structure to be
    /// sized by, <paramref name="generatorCharges"/> is neither empty nor one entry per generator, or one of its
    /// entries is not admitted by the material.</exception>
    public static ChargedPresentation<TValue, TOps> Create(
        ReadOnlySpan<Generator> generators,
        ReadOnlySpan<RewriteRule<TValue>> rules,
        TOps material,
        int windowDegree = 0,
        ReadOnlySpan<TValue> generatorCharges = default
    ) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: windowDegree);

        // Snapshot first: validation, discovery and compilation must all read the same immutable declaration even when
        // the public value structs were built over caller-owned arrays.
        var admittedGenerators = CopyGenerators(generators: generators);
        var admittedRules = CopyRules(rules: rules);
        var admittedGeneratorCharges = generatorCharges.ToArray();
        var colourCount = 0;

        for (var index = 0; (index < admittedGenerators.Length); ++index) {
            var generator = admittedGenerators[index];

            if (generator.Symbol != index) {
                throw new ArgumentException(
                    message: "A generator's symbol must equal its index in the generator list.",
                    paramName: nameof(generators)
                );
            }

            ArgumentOutOfRangeException.ThrowIfNegative(
                value: generator.Degree,
                paramName: nameof(generators)
            );

            foreach (var colour in generator.Inputs) {
                colourCount = Math.Max(
                    val1: colourCount,
                    val2: AdmitColour(
                        colour: colour,
                        symbol: index,
                        boundary: "input",
                        paramName: nameof(generators)
                    )
                );
            }
            foreach (var colour in generator.Outputs) {
                colourCount = Math.Max(
                    val1: colourCount,
                    val2: AdmitColour(
                        colour: colour,
                        symbol: index,
                        boundary: "output",
                        paramName: nameof(generators)
                    )
                );
            }
        }

        if (
            (0 != admittedGeneratorCharges.Length) &&
            (admittedGeneratorCharges.Length != admittedGenerators.Length)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(generatorCharges),
                message: "The generator charges must be empty or carry one entry per generator."
            );
        }

        ValidateCanonicalCharges(
            generatorCharges: admittedGeneratorCharges,
            material: material,
            rules: admittedRules
        );
        ValidateRules(
            rules: admittedRules,
            generatorCount: admittedGenerators.Length,
            material: material
        );

        var charges = new TValue[admittedGenerators.Length];

        for (var index = 0; (index < charges.Length); ++index) {
            charges[index] = ((0 == admittedGeneratorCharges.Length)
                ? material.One
                : material.Canonicalize(value: admittedGeneratorCharges[index])
            );
        }

        return new(
            colourCount: colourCount,
            generatorCharges: charges,
            generators: admittedGenerators,
            material: material,
            rules: admittedRules,
            windowDegree: windowDegree
        );
    }
    /// <summary>Returns the canonical generator word of a normal form.</summary>
    /// <param name="key">The normal form's key.</param>
    /// <returns>The word, low position first; empty for the unit word.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The key names no normal form of this presentation.</exception>
    public ReadOnlySpan<int> NormalFormWord(long key) {
        if (
            (key < 0L) ||
            (key >= NormalFormCount)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(key),
                message: "The key names no normal form of this presentation."
            );
        }

        var index = ((int)key);

        return m_normalFormSymbols.AsSpan(
            start: m_normalFormStart[index],
            length: (m_normalFormStart[(index + 1)] - m_normalFormStart[index])
        );
    }

    /// <summary>A charged combination of words, held in the canonical order so the greatest reducible term is the last
    /// one that matches. Merging combines like words through the material's addition, which never rounds.</summary>
    internal sealed class Combination {
        private readonly List<TValue> m_charges = [];
        private readonly List<int[]> m_words = [];

        /// <summary>Gets the number of terms.</summary>
        internal int Count => m_charges.Count;

        /// <summary>Gets the charge of one term.</summary>
        internal TValue ChargeAt(int index) =>
            m_charges[index];
        /// <summary>Empties the combination.</summary>
        internal void Clear() {
            m_charges.Clear();
            m_words.Clear();
        }
        /// <summary>Adds a charged word, combining it with an equal word already present and pruning a zero result.</summary>
        internal void Merge(ReadOnlySpan<int> word, TValue charge, TOps material) {
            if (material.IsZero(value: charge)) { return; }

            var low = 0;
            var high = m_words.Count;

            while (low < high) {
                var middle = ((low + high) >> 1);
                var order = CompareWords(
                    left: m_words[middle],
                    right: word
                );

                if (0 == order) {
                    var combined = material.Add(
                        left: m_charges[middle],
                        right: charge
                    );

                    if (material.IsZero(value: combined)) {
                        m_charges.RemoveAt(index: middle);
                        m_words.RemoveAt(index: middle);
                    } else {
                        m_charges[middle] = combined;
                    }

                    return;
                }

                if (order < 0) { low = (middle + 1); } else { high = middle; }
            }

            m_charges.Insert(
                index: low,
                item: charge
            );
            m_words.Insert(
                index: low,
                item: word.ToArray()
            );
        }
        /// <summary>Removes one term, returning its word and its charge.</summary>
        internal int[] TakeAt(int index, out TValue charge) {
            var word = m_words[index];

            charge = m_charges[index];

            m_charges.RemoveAt(index: index);
            m_words.RemoveAt(index: index);

            return word;
        }
        /// <summary>Gets the word of one term.</summary>
        internal ReadOnlySpan<int> WordAt(int index) =>
            m_words[index];
    }
    /// <summary>A charged combination of normal-form keys, held in ascending key order.</summary>
    internal sealed class KeyedCombination {
        private readonly List<TValue> m_charges = [];
        private readonly List<long> m_keys = [];

        /// <summary>Gets the number of terms.</summary>
        internal int Count => m_keys.Count;

        /// <summary>Gets the charge of one term.</summary>
        internal TValue ChargeAt(int index) =>
            m_charges[index];
        /// <summary>Copies the charges out.</summary>
        internal TValue[] ChargesToArray() =>
            [.. m_charges];
        /// <summary>Empties the combination.</summary>
        internal void Clear() {
            m_charges.Clear();
            m_keys.Clear();
        }
        /// <summary>Gets the key of one term.</summary>
        internal long KeyAt(int index) =>
            m_keys[index];
        /// <summary>Copies the keys out.</summary>
        internal long[] KeysToArray() =>
            [.. m_keys];
        /// <summary>Adds a charged key, combining it with an equal key already present and pruning a zero result.</summary>
        internal void Merge(long key, TValue charge, TOps material) {
            if (material.IsZero(value: charge)) { return; }

            var low = 0;
            var high = m_keys.Count;

            while (low < high) {
                var middle = ((low + high) >> 1);
                var probe = m_keys[middle];

                if (probe == key) {
                    var combined = material.Add(
                        left: m_charges[middle],
                        right: charge
                    );

                    if (material.IsZero(value: combined)) {
                        m_charges.RemoveAt(index: middle);
                        m_keys.RemoveAt(index: middle);
                    } else {
                        m_charges[middle] = combined;
                    }

                    return;
                }

                if (probe < key) { low = (middle + 1); } else { high = middle; }
            }

            m_charges.Insert(
                index: low,
                item: charge
            );
            m_keys.Insert(
                index: low,
                item: key
            );
        }
    }
}
