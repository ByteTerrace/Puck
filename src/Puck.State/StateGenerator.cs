using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>How a <see cref="StateGenerator"/>'s entries — a Markov context's alternatives, or a weighted numeric
/// source's outcomes — are consumed: the multiset-sampling vocabulary. Authored, never inferred: exhaustion behaviour
/// is a declaration, not a fallback the engine picks.</summary>
[JsonConverter(typeof(StrictEnumConverter<GeneratorMode>))]
public enum GeneratorMode : byte {
    /// <summary>Every sample leaves the entry set unchanged — the ordinary weighted draw.</summary>
    WithReplacement,

    /// <summary>Each entry may be drawn at most once per pass; a set whose entries are all drawn refuses the whole
    /// emission by name (never a silent stall, never a re-draw).</summary>
    WithoutReplacement,

    /// <summary>Each entry may be drawn at most once per pass; a set whose entries are all drawn clears its mask and
    /// draws again from the full set, deterministically, in the same emission — the shuffle bag.</summary>
    RestartOnExhaustion,
}
/// <summary>One weighted alternative of a <see cref="GeneratorContext"/>: the token it emits, its relative
/// weight, and the context the walk moves into after it is picked. The authored <see cref="Next"/> is what makes this
/// a real Markov process rather than a bag of independent draws — the context key is the process state, so an author
/// folds exactly as much history into it as the chain needs.</summary>
/// <param name="Token">The opaque game-authored token this alternative emits. The engine never interprets it; it is
/// space-joined with the emission's other tokens and written into the target text cell. Bounded by
/// <see cref="GeneratorCapacity.MaxTokenLength"/>.</param>
/// <param name="Weight">The alternative's positive relative weight. At least one alternative in a context must carry
/// a non-zero weight.</param>
/// <param name="Next">The context the walk moves into after this alternative is picked. Must name a declared
/// <see cref="GeneratorContext.Key"/>; naming a context that declares NO alternatives ends the emission (a
/// terminal is a context with nothing to say, never a reserved token spelling).</param>
/// <param name="Multiplicity">How many units of this alternative one pass holds, at least one; <see langword="null"/>
/// is one. Under <see cref="GeneratorMode.WithReplacement"/> a multiplicity only scales the weight; under an
/// exhausting mode each unit is drawn once per pass. A context's units total at most
/// <see cref="GeneratorCapacity.MaxEntriesPerSet"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GeneratorAlternative(string Token, ulong Weight, CellName Next, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Multiplicity = null);
/// <summary>One named context of a <see cref="StateGenerator"/> — the state the walk may be sitting in and the
/// weighted alternatives it may pick while there. A context declaring NO alternatives is TERMINAL: reaching it ends
/// the emission.</summary>
/// <param name="Key">The stable context key, unique within the generator.</param>
/// <param name="Alternatives">The weighted alternatives out of this context, or empty for a terminal context.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GeneratorContext(CellName Key, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GeneratorAlternative>? Alternatives = null);
/// <summary>The closed vocabulary of a <see cref="StateGenerator"/>'s draw shape — which of its fields are read, and
/// what one emission produces: a Markov text walk, a multiset draw, a uniform range, a weighted numeric table, and a
/// raw stream draw are sources of one family, never parallel primitives with their own seeding, cursoring, and
/// refusal stories.</summary>
[JsonConverter(typeof(StrictEnumConverter<GeneratorSource>))]
public enum GeneratorSource : byte {
    /// <summary>The weighted-transition walk over <see cref="StateGenerator.Contexts"/> — reads
    /// <see cref="StateGenerator.Start"/>/<see cref="StateGenerator.Bound"/>/<see cref="StateGenerator.Contexts"/>/
    /// <see cref="StateGenerator.Mode"/>, writes text. The only source that deals (see
    /// <see cref="GeneratorMode"/>) and the only one whose emission costs more than one sample.</summary>
    Markov,

    /// <summary>One draw over the closed integer range
    /// <c>[<see cref="StateGenerator.RangeMin"/>, <see cref="StateGenerator.RangeMax"/>]</c> — reads only those two
    /// fields, writes an int or fixed value. One fixed-cost advance per draw: a multiply-high map of a
    /// <c>UnitFraction32</c>, rather than the rejection-sampled <c>Pcg32XshRr.NextUInt32(min, max)</c>, which keeps
    /// the cursor seekable. The map is uniform rather than exactly uniform: with <c>n</c> the range's value count,
    /// each outcome claims either <c>⌊2³²/n⌋</c> or <c>⌈2³²/n⌉</c> of the 2³² fractions — a relative deviation of at
    /// most <c>n/2³²</c>, and zero whenever <c>n</c> divides 2³².</summary>
    UniformRange,

    /// <summary>One alias-table draw over <see cref="StateGenerator.Weighted"/>'s numeric outcomes — reads that field
    /// and <see cref="StateGenerator.Mode"/>, writes an int or fixed value. Exactly two advances per draw, the same
    /// fixed alias-table cost the Markov walk pays per token; under an exhausting mode the outcomes are drawn through
    /// the site's one <see cref="StateRow.DrawnMasks"/> mask, which is the numeric shuffle bag.</summary>
    WeightedNumeric,

    /// <summary>One raw, unshaped 32-bit draw off the site's own stream — no range, no weights — widened into the
    /// target's raw value as-is. One fixed-cost advance per draw. The unshaped-entropy primitive: no distribution is
    /// applied.</summary>
    StreamDraw,

    /// <summary>One uniform draw over an orbit of the symmetry lattice: the thirty nodes of <see cref="StateGenerator.Ring"/>,
    /// or the nodes <see cref="StateGenerator.Node"/> visits under <see cref="StateGenerator.Word"/> (the lattice's
    /// own cycle when none is authored, so the node's ring) — reads those fields and <see cref="StateGenerator.Mode"/>,
    /// writes the node index in the site's displayed unit (a fixed site stores <c>node.0</c>, the phase a cycle trait
    /// reads). The units are the orbit's nodes in walk order, equally weighted, drawn under
    /// an exhausting mode through the site's one <see cref="StateRow.DrawnMasks"/> mask exactly as a weighted numeric
    /// source is; exactly two advances per draw.</summary>
    SymmetryOrbit,
}
/// <summary>One numeric outcome of a <see cref="GeneratorSource.WeightedNumeric"/> source: the raw value it
/// writes and its relative weight — the numeric twin of <see cref="GeneratorAlternative"/>, minus
/// <c>Token</c> (nothing to join into text) and <c>Next</c> (a numeric draw is one terminal pick, never a
/// walk).</summary>
/// <param name="Value">The raw value this outcome writes on selection (a plain integer for
/// <see cref="CellKind.Int"/>, raw <c>FixedQ4816</c> bits for <see cref="CellKind.Fixed"/>).</param>
/// <param name="Weight">The outcome's relative weight, fed straight to <c>Puck.Maths.WeightedSampler</c>'s exact
/// <c>ulong</c> overload. At least one outcome must carry a non-zero weight.</param>
/// <param name="Multiplicity">How many units of this outcome one pass holds, at least one; <see langword="null"/> is
/// one. Under <see cref="GeneratorMode.WithReplacement"/> a multiplicity only scales the weight; under an
/// exhausting mode each unit is drawn once per pass, so an outcome that should come out twice per pass declares
/// <c>2</c> rather than being authored twice. A source's units total at most
/// <see cref="GeneratorCapacity.MaxEntriesPerSet"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GeneratorWeightedNumeric(long Value, ulong Weight, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Multiplicity = null);
/// <summary>
/// A source's extended-generator facet: an authored <c>Pcg32Extended</c> table replacing that generator's own
/// self-seeding, so a site drawing from it is k-dimensionally equidistributed rather than merely 1-dimensionally so.
/// The table is document data, exactly one rebuild cost (<c>GeneratorEngine</c> caches the built generator beside
/// the cursor it corresponds to) — nothing about the site's persisted shape changes.
/// </summary>
/// <remarks>Exactly one of <see cref="Table"/> and <see cref="Script"/> is declared. <see cref="Table"/> is the whole
/// extension table verbatim, for any source. <see cref="Script"/> authors the site's own first draws directly in the
/// SOURCE'S OUTPUT space — the value <c>streamDraw</c> writes, or the value <c>uniformRange</c> maps to — and compiles
/// to a table at boot resolution: word <c>i</c> is <c>wanted_i XOR base_i</c>, where <c>base_i</c> is the base
/// generator's own <c>i</c>-th raw draw (independent of any table content) and <c>wanted_i</c> is the raw draw the
/// source's own sampling maps onto the scripted value. Only <c>streamDraw</c> and <c>uniformRange</c> admit a script:
/// both sample in one fixed-cost draw with no drawn-mask state, so a wanted output maps back to a single raw draw;
/// <c>markov</c>, <c>weightedNumeric</c> and <c>symmetryOrbit</c> draw through an alias table whose selection depends
/// on the site's own drawn masks, so no single raw draw maps a scripted value back. Words past the script's length are
/// the self-seeded table's own.</remarks>
/// <param name="K">The extension table size: a power of two in <c>[2, 1024]</c>.</param>
/// <param name="Table">The whole extension table, exactly <see cref="K"/> words — or <see langword="null"/> when
/// <see cref="Script"/> authors it instead.</param>
/// <param name="Script">Up to <see cref="K"/> values in the source's own output space, authoring the site's first
/// draws directly — or <see langword="null"/> when <see cref="Table"/> is authored instead.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GeneratorExtended(
    int K,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<uint>? Table = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<long>? Script = null
);
/// <summary>
/// An authored stochastic source — the vocabulary for every randomness declaration in the document: a name
/// generator, a dialogue line, a loot roll, a flat weighted draw, a multiset sample, a random census, and a drawn
/// host backend all reduce to a source of this family, sampled at an authored moment into an authored site (see
/// <see cref="Draw"/>).
/// </summary>
/// <remarks>
/// <para>A source is a pure declaration; it holds no position. The cursor and the drawn masks live on the site that
/// draws (<see cref="StateRow.DrawCursor"/>/<see cref="StateRow.DrawnMasks"/>), which is what lets two
/// sites reference one declared source and draw independent sequences from it. A source may be declared once in the
/// document's <c>generators</c> section (see <see cref="GeneratorRow"/>) and referenced by name, or inlined at
/// a single site as sugar — the two spellings compile to the identical record.</para>
/// <para>A Markov emission is one walk: it begins at <see cref="Start"/> and repeats — sample the current context's
/// alternatives, append the picked token, move to its <see cref="GeneratorAlternative.Next"/> — until it
/// reaches a terminal context (one declaring no alternatives). A walk that has emitted <see cref="Bound"/> tokens
/// without terminating refuses the whole emission by name rather than truncating it. A single self-terminating
/// context with <see cref="Bound"/> 1 is the degenerate flat weighted text draw.</para>
/// <para>The other sources are numeric and always exactly one draw — <see cref="Bound"/> is meaningless beside
/// them and must be left at its default. Each source's fields are both-or-neither against the fields the others
/// own: declaring <see cref="Contexts"/> beside <see cref="RangeMin"/> is refused by name rather than silently
/// ignored.</para>
/// <para><see cref="Mode"/> belongs to the alias-table shapes — per context for Markov, over the outcome set for
/// weighted numeric, over the orbit for a symmetry orbit — and persists across emissions in the drawing site's own <see cref="StateRow.DrawnMasks"/>
/// masks. <see cref="GeneratorSource.UniformRange"/> and <see cref="GeneratorSource.StreamDraw"/> have no
/// entry set and never exhaust.</para>
/// </remarks>
/// <param name="Source">Which draw shape this source fires.</param>
/// <param name="Start">Markov only: the context every emission begins from. Must name a declared context.</param>
/// <param name="Bound">Markov only: the maximum tokens one emission may draw before refusing by name,
/// <c>1..</c><see cref="GeneratorCapacity.MaxEmissionBound"/>. Left at <see cref="DefaultBound"/> by a numeric
/// source.</param>
/// <param name="Contexts">Markov only: the declared contexts, at least one, uniquely keyed.</param>
/// <param name="Mode">Markov, weighted numeric and symmetry orbit: how the entries are consumed (see <see cref="GeneratorMode"/>).</param>
/// <param name="RangeMin"><see cref="GeneratorSource.UniformRange"/> only: the closed range's inclusive lower
/// bound — both bounds present or neither. Raw-encoded per the destination site's <see cref="CellKind"/> (raw
/// <c>FixedQ4816</c> bits for a <c>fixed</c> site) — unlike a site row's own <c>min</c>/<c>max</c>, which a
/// <c>fixed</c> row authors as decimal text, since a source is not bound to one site and cannot know the kind it
/// will write.</param>
/// <param name="RangeMax"><see cref="GeneratorSource.UniformRange"/> only: the inclusive upper bound, same
/// encoding as <see cref="RangeMin"/>.</param>
/// <param name="Weighted"><see cref="GeneratorSource.WeightedNumeric"/> only: the weighted numeric outcomes, at
/// least one, at least one carrying a non-zero weight; under an exhausting <see cref="Mode"/> each outcome contributes
/// <see cref="GeneratorWeightedNumeric.Multiplicity"/> units to the pass.</param>
/// <param name="Ring"><see cref="GeneratorSource.SymmetryOrbit"/> only: the ring, 0..7, whose thirty nodes are
/// the units — exactly one of <see cref="Ring"/> and <see cref="Node"/>.</param>
/// <param name="Node"><see cref="GeneratorSource.SymmetryOrbit"/> only: the node, 0..239, whose orbit under
/// <see cref="Word"/> is the units.</param>
/// <param name="Word"><see cref="GeneratorSource.SymmetryOrbit"/> beside <see cref="Node"/> only: the word of
/// reflections (one to eight mirror nodes, applied first to last) the orbit is taken under, or <see langword="null"/>
/// for the lattice's own cycle — the same generator vocabulary a <see cref="StateCycle"/> authors.</param>
/// <param name="Extended">The extended-generator facet (see <see cref="GeneratorExtended"/>), or
/// <see langword="null"/> for the ordinary <c>Pcg32XshRr</c> generator every other source draws through.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateGenerator(
    GeneratorSource Source = GeneratorSource.Markov,
    // Each source reads a disjoint field set, so the canonical writer omits the ones this source does not own rather
    // than emitting a wall of nulls a reader has to discount.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CellName? Start = null,
    int Bound = StateGenerator.DefaultBound,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GeneratorContext>? Contexts = null,
    GeneratorMode Mode = GeneratorMode.WithReplacement,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? RangeMin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? RangeMax = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GeneratorWeightedNumeric>? Weighted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Ring = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Node = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? Word = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GeneratorExtended? Extended = null
) {
    /// <summary>The <see cref="Bound"/> an undeclared source carries — one emitted token. <see cref="Bound"/> is
    /// Markov-only and not nullable, so "left at its default" is the only reading of "not declared" available to it; a
    /// numeric source carrying anything else is refused against this constant rather than left to parse and then be
    /// ignored.</summary>
    public const int DefaultBound = 1;
}
/// <summary>One row of the document's <c>generators</c> section: a stochastic source declared under a name, so that
/// any number of <see cref="Draw"/> sites may reference it (<see cref="Draw.Source"/>). Declaring a source
/// once and referencing it is what makes an NPC-bark site and a loot site able to share one authored table while
/// still drawing independent sequences — the source carries the shape, each site carries its own position.</summary>
/// <param name="Name">The source's name, unique within the section, and the spelling a site's
/// <see cref="Draw.Source"/> resolves against.</param>
/// <param name="Generator">The source itself.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GeneratorRow(CellName Name, StateGenerator Generator);
/// <summary>The <see cref="StateGenerator"/> caps the document validator enforces.</summary>
/// <remarks>The context cap and the alternative cap are load-bearing together, not decorative: an exhausting mode
/// records one <see cref="StateRow.DrawnMasks"/> mask per context, and each such mask is a 256-bit drawn set (so
/// a context can hold no more alternatives than the membership set has bits).</remarks>
public static class GeneratorCapacity {
    /// <summary>A context's alternative-count ceiling — one bit per alternative in its drawn mask.</summary>
    public const int MaxAlternativesPerContext = 256;
    /// <summary>A source's context-count ceiling.</summary>
    public const int MaxContexts = 32;
    /// <summary>The document's declared-source count ceiling.</summary>
    public const int MaxDeclaredSources = 64;
    /// <summary>The declared <see cref="StateGenerator.Bound"/> ceiling.</summary>
    public const int MaxEmissionBound = 64;
    /// <summary>The greatest value a <see cref="GeneratorSource.UniformRange"/> bound may hold. The draw is a
    /// single fixed-cost multiply-high map whose span must fit a <c>uint</c> without truncation; this bound is what
    /// keeps it there.</summary>
    public const long MaxRangeBound = int.MaxValue;
    /// <summary>One emitted token's length ceiling, in UTF-16 code units (the JOINED emission is separately bounded
    /// by <see cref="StateCapacity.MaxTextValueLength"/>).</summary>
    public const int MaxTokenLength = 64;
    /// <summary>A <see cref="GeneratorSource.WeightedNumeric"/> source's outcome-count ceiling — matches
    /// <see cref="MaxAlternativesPerContext"/> since both build an alias table over an authored entry list.</summary>
    public const int MaxWeightedOutcomes = 256;
    /// <summary>The most units one drawn set may hold — a context's alternatives or a weighted source's outcomes,
    /// each counted <c>Multiplicity</c> times — since a drawn mask is one 256-bit set with one bit per unit.</summary>
    public const int MaxEntriesPerSet = 256;
    /// <summary>The least value a <see cref="GeneratorSource.UniformRange"/> bound may hold — see
    /// <see cref="MaxRangeBound"/>.</summary>
    public const long MinRangeBound = int.MinValue;
    /// <summary>The greatest <see cref="GeneratorExtended.K"/> a source may declare — <c>Pcg32Extended</c>'s own
    /// ceiling.</summary>
    public const int MaxExtendedTableSize = 1024;
    /// <summary>The least <see cref="GeneratorExtended.K"/> a source may declare — <c>Pcg32Extended</c>'s own
    /// floor.</summary>
    public const int MinExtendedTableSize = 2;
}
