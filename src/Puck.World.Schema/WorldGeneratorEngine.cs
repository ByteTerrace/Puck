using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The one deterministic sampling core behind every <see cref="WorldDraw"/> site — shared by the live <c>Generate</c>
/// mutation (<c>Puck.World.Server.WorldServer</c>) and the boot/first-fill resolver
/// (<c>Puck.World.WorldDrawBootResolver</c>), which runs before a <c>Server.WorldServer</c> exists at all and
/// therefore cannot reach into that project. Living here — the lowest layer both reach — is what keeps there from
/// ever being a second implementation of "how a draw draws" to disagree with the first.
/// </summary>
/// <remarks>
/// <para><b>The seed ladder, four rungs.</b> <see cref="ComputeSeedState"/> folds, in order:
/// <list type="number">
/// <item><description>the engine constant — never authored, never varies by document; it exists only so this
/// system's streams cannot collide by accident with any other seeded system that folds the same document
/// values;</description></item>
/// <item><description>the document's own <c>generation.worldSeed</c> — the author's single "reroll the whole world"
/// lever, moving every site at once;</description></item>
/// <item><description>the running instance's identity — the boot instance's constant name, or a spawned
/// <c>world.instance.start</c> instance's own name. Not document data: it is what lets three instances of one
/// document draw differently while each stays exactly reproducible from (document, instance name, draw
/// history);</description></item>
/// <item><description>the site descriptor (see <see cref="WorldDrawSites"/>) — what separates two sites. An identity,
/// never a position: a positional ordinal is read off the live document's site set, which moves whenever the boot
/// resolver clears a settled facet, a <c>world.row.remove state</c> retires a draw row, or an <c>UpsertStateRow</c> adds
/// one — silently re-pointing a live site's stream while its cursor kept counting.</description></item>
/// </list>
/// Every rung is length-delimited before its bytes, so no two different rung sequences can fold to the same
/// pre-image: without the delimiter an instance named <c>ab</c> at site <c>c</c> and one named <c>a</c> at site
/// <c>bc</c> present the same byte stream to the hash.</para>
/// <para><b>The stream id is derived from the site alone</b> and masked small
/// (<see cref="SiteStreamIdMask"/>) — <c>Pcg32XshRr</c> collapses increments whose ids sit <c>2^62</c> apart, which a
/// masked id can never reach. A stream-id collision between two sites is harmless because their seeds still differ by
/// the descriptor rung above.</para>
/// <para><b>Seeking, not replaying.</b> Every source costs a fixed number of generator advances per sample
/// (<see cref="AdvancesPerSample"/>) — the uniform range is a multiply-high map rather than a rejection-sampled
/// bounded draw precisely so this stays true. Resuming a site at cursor <c>n</c> is therefore one
/// <c>Pcg32XshRr.Advance(n * cost)</c>, an O(1) jump, and never a replay of the earlier draws. There is no per-tick
/// cadence ceiling: a rule redrawing a site on every tick costs the same at cursor 1,000,000 as at cursor 0.</para>
/// </remarks>
public static class WorldGeneratorEngine {
    /// <summary>The engine-wide constant folded into every site's seed — the ladder's first rung.</summary>
    private const ulong EngineConstant = 0x5075636B44726177UL; // "PuckDraw", ASCII, as a fixed 64-bit constant.
    /// <summary>The largest <c>Pcg32XshRr</c> stream id a site descriptor maps onto — see this type's remarks.</summary>
    private const ulong SiteStreamIdMask = 0xFFFFUL;

    /// <summary>One emission's result — exactly one of <see cref="Text"/>/<see cref="Numeric"/> is set, matching the
    /// firing <see cref="WorldGenerator.Source"/>.</summary>
    /// <param name="Text">The space-joined Markov emission, or <see langword="null"/> for a numeric source.</param>
    /// <param name="Numeric">The raw numeric draw, or <see langword="null"/> for a Markov source.</param>
    /// <param name="Samples">How many samples this emission consumed — the amount the site's cursor advances by.
    /// Always <c>1</c> for a numeric source; the walk's own token count for a Markov one.</param>
    /// <param name="Decks">The site's updated dealt masks, or <see langword="null"/> when nothing dealt (a source under
    /// <see cref="WorldGeneratorMode.WithReplacement"/>, and every source that cannot deal). A Markov source carries one
    /// mask per context, by declaration ordinal; a weighted numeric source carries exactly one.</param>
    public readonly record struct FireResult(string? Text, long? Numeric, long Samples, IReadOnlyList<long>? Decks);

    // What a source's declaration determines once: each context's entry set dealt as cards, a weighted source's
    // entry set dealt as cards, and the context key → ordinal map. Built once per WorldGenerator instance and held
    // weakly beside it, so a site drawn every tick pays the table build once rather than per emission. A card is one
    // deal of one entry: entry i contributes Count cards, each carrying the entry's weight and ordinal, and a deck
    // mask holds one bit per card. The full table samples a card ordinal, so a table over the same weights in the
    // same order picks the same card whatever the entries carry. A dealt-down pool (a deck mode mid-pass) is the
    // site's membership, not the source's, and is rebuilt allocation-free in stack storage for each emission.
    private sealed class CompiledSource {
        public CompiledSource(WorldGenerator generator) {
            var contexts = (generator.Contexts ?? []);

            Ordinals = new Dictionary<WorldCellName, int>(capacity: contexts.Count);
            Contexts = new EntrySet?[contexts.Count];

            for (var index = 0; (index < contexts.Count); index++) {
                Ordinals.TryAdd(key: contexts[index].Key, value: index);

                var alternatives = (contexts[index].Alternatives ?? []);
                var weights = new ulong[alternatives.Count];
                var counts = new int[alternatives.Count];

                for (var alternative = 0; (alternative < weights.Length); alternative++) {
                    weights[alternative] = alternatives[alternative].Weight;
                    counts[alternative] = (alternatives[alternative].Count ?? 1);
                }

                Contexts[index] = EntrySet.TryBuild(counts: counts, weights: weights);
            }

            var outcomes = (generator.Weighted ?? []);
            var outcomeWeights = new ulong[outcomes.Count];
            var outcomeCounts = new int[outcomes.Count];

            for (var index = 0; (index < outcomeWeights.Length); index++) {
                outcomeWeights[index] = outcomes[index].Weight;
                outcomeCounts[index] = (outcomes[index].Count ?? 1);
            }

            Weighted = EntrySet.TryBuild(counts: outcomeCounts, weights: outcomeWeights);
        }

        public EntrySet?[] Contexts { get; }
        public Dictionary<WorldCellName, int> Ordinals { get; }
        public EntrySet? Weighted { get; }
    }
    private sealed class EntrySet {
        private EntrySet(int[] cardEntries, ulong[] cardWeights, AliasTable<int>? full, long cardCount) {
            CardEntries = cardEntries;
            CardWeights = cardWeights;
            Full = full;
            CardCount = cardCount;
        }

        // The entry ordinal each card deals.
        public int[] CardEntries { get; }
        // Each card's weight — its entry's weight.
        public ulong[] CardWeights { get; }
        // Kept separately from the arrays so an invalid, over-capacity declaration can be represented and refused
        // without first attempting an attacker-sized allocation. Validated documents never take this path.
        public long CardCount { get; }
        // The alias table over every card, or null when no card weighs anything — the validator refuses that shape,
        // and the firing arm refuses it by name again rather than trusting it only ever sees validated documents.
        public AliasTable<int>? Full { get; }

        public static EntrySet? TryBuild(int[] counts, ulong[] weights) {
            if (weights.Length == 0) { return null; }

            var cards = 0L;

            for (var index = 0; (index < counts.Length); index++) {
                cards += Math.Max(val1: 1, val2: counts[index]);
            }

            if (cards > WorldGeneratorCapacity.MaxCardsPerSet) {
                return new EntrySet(
                    cardEntries: [],
                    cardWeights: [],
                    full: null,
                    cardCount: cards
                );
            }

            var cardEntries = new int[(int)cards];
            var cardWeights = new ulong[(int)cards];
            var entries = new (int Element, ulong Weight)[(int)cards];
            var any = false;
            var card = 0;

            for (var index = 0; (index < counts.Length); index++) {
                for (var copy = Math.Max(val1: 1, val2: counts[index]); (copy > 0); copy--) {
                    cardEntries[card] = index;
                    cardWeights[card] = weights[index];
                    entries[card] = (card, weights[index]);
                    card++;
                }

                any |= (weights[index] != 0UL);
            }

            return new EntrySet(
                cardEntries: cardEntries,
                cardWeights: cardWeights,
                full: (any ? WeightedSampler.Create<int>(entries: entries) : null),
                cardCount: cards
            );
        }
    }

    private static readonly ConditionalWeakTable<WorldGenerator, CompiledSource> s_compiled = new();

    private static CompiledSource Compiled(WorldGenerator generator) =>
        s_compiled.GetValue(
            key: generator,
            createValueCallback: static (WorldGenerator source) => new CompiledSource(generator: source)
        );

    // One deal from a card set under a mode: the full table when nothing is dealt (or the mode never deals), else a
    // table over the undealt cards. A dealt-out deck refuses under WithoutReplacement and clears under
    // ReshuffleOnExhaustion, in the same emission. Exactly two generator advances either way, so cursor seeking
    // stays exact. On success the picked card's bit is set in `deck` (deck modes only) and the card's ENTRY ordinal
    // is returned; -1 with `reason` set otherwise.
    private static int Deal(WorldGeneratorMode mode, EntrySet? set, ref ulong deck, ref Pcg32XshRr rng, string what, out string reason) {
        reason = string.Empty;

        if (set?.CardCount > WorldGeneratorCapacity.MaxCardsPerSet) {
            reason = $"{what} holds {set.CardCount} cards, more than the {WorldGeneratorCapacity.MaxCardsPerSet} a deck mask can deal";

            return -1;
        }

        if ((set is null) || (set.Full is null)) {
            reason = $"{what} declares no non-zero weight — nothing can be picked";

            return -1;
        }

        if (mode == WorldGeneratorMode.WithReplacement) {
            return set.CardEntries[set.Full.Sample(generator: ref rng)];
        }

        var cards = set.CardEntries.Length;

        int picked;

        if (deck == 0UL) {
            picked = set.Full.Sample(generator: ref rng);
        }
        else {
            Span<(int Element, ulong Weight)> buffer = stackalloc (int, ulong)[WorldGeneratorCapacity.MaxCardsPerSet];
            var pooled = 0;
            var anyWeight = false;

            for (var card = 0; (card < cards); card++) {
                if ((deck & (1UL << card)) == 0UL) {
                    buffer[pooled++] = (card, set.CardWeights[card]);
                    anyWeight |= (set.CardWeights[card] != 0UL);
                }
            }

            if ((pooled == 0) || !anyWeight) {
                // The deck is dealt out (or only weightless cards remain). What happens next is authored, never
                // inferred.
                if (mode == WorldGeneratorMode.WithoutReplacement) {
                    reason = ((pooled == 0)
                        ? $"{what} is dealt out ({cards} cards, mode withoutReplacement) — declare mode reshuffleOnExhaustion to deal again from the full set"
                        : $"{what} has only zero-weight cards left undealt (mode withoutReplacement) — declare mode reshuffleOnExhaustion or give every entry weight");

                    return -1;
                }

                deck = 0UL;
                picked = set.Full.Sample(generator: ref rng);
            }
            else {
                picked = SampleAlias(entries: buffer[..pooled], generator: ref rng);
            }
        }

        deck |= (1UL << picked);

        return set.CardEntries[picked];
    }
    // The exact Walker/Vose construction AliasTable<T> uses, specialized to a short-lived card-index table backed
    // entirely by stack storage. Entry order, LIFO partition order, UQ0.32 rounding, power-of-two padding, and the
    // two random advances are deliberately identical, so replacing the former heap table cannot move any stream.
    private static int SampleAlias(ReadOnlySpan<(int Element, ulong Weight)> entries, ref Pcg32XshRr generator) {
        var count = entries.Length;
        var columnCount = ((int)System.Numerics.BitOperations.RoundUpToPowerOf2(value: ((uint)count)));
        Span<UInt128> scaled = stackalloc UInt128[columnCount];
        Span<uint> thresholds = stackalloc uint[columnCount];
        Span<int> aliases = stackalloc int[columnCount];
        Span<int> small = stackalloc int[columnCount];
        Span<int> large = stackalloc int[columnCount];
        var totalWeight = UInt128.Zero;

        // Padding columns are zero-weight entries. Stack storage is otherwise uninitialized, so make that semantic
        // input explicit before filling the real entry prefix.
        scaled.Clear();

        for (var index = 0; (index < count); index++) {
            totalWeight += entries[index].Weight;
        }

        for (var index = 0; (index < count); index++) {
            scaled[index] = (((UInt128)entries[index].Weight) * ((uint)columnCount));
        }

        var smallCount = 0;
        var largeCount = 0;

        for (var index = 0; (index < columnCount); index++) {
            if (scaled[index] < totalWeight) {
                small[smallCount++] = index;
            }
            else {
                large[largeCount++] = index;
            }
        }

        while ((smallCount > 0) && (largeCount > 0)) {
            var s = small[--smallCount];
            var l = large[--largeCount];
            var threshold = ((ulong)(((scaled[s] << 32) + (totalWeight >> 1)) / totalWeight));

            if (threshold > uint.MaxValue) {
                aliases[s] = s;
                thresholds[s] = uint.MaxValue;
            }
            else {
                aliases[s] = l;
                thresholds[s] = ((uint)threshold);
            }

            scaled[l] -= (totalWeight - scaled[s]);

            if (scaled[l] < totalWeight) {
                small[smallCount++] = l;
            }
            else {
                large[largeCount++] = l;
            }
        }

        while (largeCount > 0) {
            var index = large[--largeCount];

            aliases[index] = index;
            thresholds[index] = uint.MaxValue;
        }

        while (smallCount > 0) {
            var index = small[--smallCount];

            aliases[index] = index;
            thresholds[index] = uint.MaxValue;
        }

        var column = ((int)(generator.NextUInt32() & ((uint)(columnCount - 1))));
        var selected = ((generator.NextUInt32() < thresholds[column]) ? column : aliases[column]);

        return entries[selected].Element;
    }
    private static bool TryFireMarkov(WorldGenerator generator, ref Pcg32XshRr rng, IReadOnlyList<long>? decks, out FireResult result, out string reason) {
        var contexts = generator.Contexts!;
        var compiled = Compiled(generator: generator);
        var tokens = new List<string>(capacity: generator.Bound);
        var context = generator.Start!.Value;
        var samples = 0L;
        var working = new long[contexts.Count];
        var dealt = false;

        for (var index = 0; (index < working.Length); index++) {
            working[index] = (((decks is not null) && (index < decks.Count))
                ? decks[index]
                : 0L
            );
        }

        while (true) {
            if (!compiled.Ordinals.TryGetValue(key: context, value: out var ordinal)) {
                result = default;
                reason = $"source has no context declared for '{context}'";

                return false;
            }

            var alternatives = (contexts[ordinal].Alternatives ?? []);

            // A TERMINAL context is one declaring no alternatives — a context with nothing to say ends the walk.
            if (alternatives.Count == 0) {
                break;
            }

            if (tokens.Count >= generator.Bound) {
                result = default;
                reason = $"source reached its emission bound of {generator.Bound} without terminating — refusing rather than truncating";

                return false;
            }

            var deck = unchecked((ulong)working[ordinal]);
            var picked = Deal(
                deck: ref deck,
                mode: generator.Mode,
                reason: out reason,
                rng: ref rng,
                set: compiled.Contexts[ordinal],
                what: $"context '{context}'"
            );

            if (picked < 0) {
                result = default;

                return false;
            }

            samples++;

            if (generator.Mode != WorldGeneratorMode.WithReplacement) {
                working[ordinal] = unchecked((long)deck);
                dealt = true;
            }

            tokens.Add(item: alternatives[picked].Token);
            context = alternatives[picked].Next;
        }

        result = new FireResult(
            Text: string.Join(
                separator: ' ',
                values: tokens
            ),
            Numeric: null,
            Samples: samples,
            Decks: (dealt
            ? working
            : null)
        );
        reason = string.Empty;

        return true;
    }
    // One numeric sample of a non-Markov source at the generator's current position, threading the one deck a
    // weighted source deals through. The single draw TryFire answers and every cell of a TryFireBatch fill share
    // this body, so a fill's cell k is exactly the sample a site at cursor + k would have drawn.
    private static bool TryDrawNumeric(WorldGenerator generator, ref Pcg32XshRr rng, ref ulong deck, out long value, out string reason) {
        switch (generator.Source) {
            case WorldGeneratorSource.UniformRange: {
                    var span = unchecked((uint)(generator.RangeMax!.Value - generator.RangeMin!.Value));
                    var fraction = rng.NextUnitFraction32();
                    // Multiply-high map of a uniform fraction onto [0, span] — one fixed-cost advance, no rejection, so
                    // cursor seeking stays exact. The at-most-n/2^32 deviation this trades for an unbiased-via-rejection
                    // draw is the deliberate price of being seekable (see WorldGeneratorSource.UniformRange).
                    var offset = ((uint)(((((ulong)span) + 1UL) * fraction.Value) >> 32));

                    value = (generator.RangeMin.Value + offset);
                    reason = string.Empty;

                    return true;
                }
            case WorldGeneratorSource.WeightedNumeric: {
                    var picked = Deal(
                        deck: ref deck,
                        mode: generator.Mode,
                        reason: out reason,
                        rng: ref rng,
                        set: Compiled(generator: generator).Weighted,
                        what: "weighted"
                    );

                    value = ((picked < 0) ? 0L : generator.Weighted![picked].Value);

                    return (picked >= 0);
                }
            case WorldGeneratorSource.StreamDraw:
                value = unchecked((long)rng.NextUInt32());
                reason = string.Empty;

                return true;
            default:
                value = 0L;
                reason = $"unrecognized generator source '{generator.Source}'";

                return false;
        }
    }
    private static bool TryRunBatch(WorldGenerator generator, CellKind targetKind, ulong seedState, ulong stream, long cursor, IReadOnlyList<long>? decks, Span<long> values, int sampleCount, bool writeValues, out IReadOnlyList<long>? decksAfter, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: generator);

        decksAfter = null;

        if (cursor < 0) {
            reason = $"cursor {cursor} is negative — a draw cursor is a non-negative sample count";

            return false;
        }

        if (sampleCount < 0) {
            reason = $"sample count {sampleCount} is negative";

            return false;
        }

        if (!Enum.IsDefined(value: generator.Mode)) {
            reason = $"mode '{generator.Mode}' is not a defined WorldGeneratorMode";

            return false;
        }

        if (WritesText(source: generator.Source)) {
            reason = $"source={WorldRefusalSpelling.GeneratorSource(source: generator.Source)} writes text and cannot fill cells";

            return false;
        }

        if (!TryCheckTargetKind(
            source: generator.Source,
            targetKind: targetKind,
            reason: out reason
        )) {
            return false;
        }

        var rng = Pcg32XshRr.Create(
            state: seedState,
            stream: stream
        );

        rng.Advance(count: unchecked((((ulong)cursor) * AdvancesPerSample(source: generator.Source))));

        var deals = (generator.Mode != WorldGeneratorMode.WithReplacement);
        var deck = ((deals && (decks is { Count: > 0 })) ? unchecked((ulong)decks[0]) : 0UL);

        for (var sample = 0; (sample < sampleCount); sample++) {
            if (!TryDrawNumeric(
                deck: ref deck,
                generator: generator,
                reason: out reason,
                rng: ref rng,
                value: out var value
            )) {
                return false;
            }

            if (writeValues) {
                values[sample] = value;
            }
        }

        decksAfter = (deals ? [unchecked((long)deck)] : null);
        reason = string.Empty;

        return true;
    }
    /// <summary>Fills <paramref name="values"/> with consecutive samples of a numeric <paramref name="generator"/> at
    /// a site already seeked to <paramref name="cursor"/> — the per-cell fill a lattice row's <c>draw</c> paint takes.
    /// Cell <c>k</c> receives exactly the sample a single <see cref="TryFire"/> at <c>cursor + k</c> would draw, with a
    /// weighted source's deck threaded from cell to cell, so one pass over a field is one run of the site's stream.</summary>
    /// <param name="generator">The resolved source; a text source is refused.</param>
    /// <param name="targetKind">The cells' declared kind; a mismatch refuses by name before any draw runs.</param>
    /// <param name="seedState">The seed-ladder fold (see <see cref="ComputeSeedState"/>).</param>
    /// <param name="stream">The site's stream id (see <see cref="ComputeStreamId"/>).</param>
    /// <param name="cursor">The site's current sample count.</param>
    /// <param name="decks">The site's current dealt masks (may be empty).</param>
    /// <param name="values">Receives one raw sample per cell; its length is the sample count the cursor advances by.</param>
    /// <param name="decksAfter">The site's dealt masks after the fill, or <see langword="null"/> when the source never deals.</param>
    /// <param name="reason">Why the fill was refused, on failure.</param>
    /// <returns><see langword="true"/> when every cell was filled.</returns>
    public static bool TryFireBatch(WorldGenerator generator, CellKind targetKind, ulong seedState, ulong stream, long cursor, IReadOnlyList<long>? decks, Span<long> values, out IReadOnlyList<long>? decksAfter, out string reason) => TryRunBatch(
        generator: generator,
        targetKind: targetKind,
        seedState: seedState,
        stream: stream,
        cursor: cursor,
        decks: decks,
        values: values,
        sampleCount: values.Length,
        writeValues: true,
        decksAfter: out decksAfter,
        reason: out reason
    );
    /// <summary>Advances a numeric generator through <paramref name="sampleCount"/> consecutive samples without
    /// materializing their values. This is the compose-side half of a whole-field redraw: it computes the pass's
    /// final deck while the apply side later emits the same pass directly into the live field.</summary>
    /// <param name="generator">The resolved source; a text source is refused.</param>
    /// <param name="targetKind">The cells' declared kind.</param>
    /// <param name="seedState">The seed-ladder fold.</param>
    /// <param name="stream">The site's stream id.</param>
    /// <param name="cursor">The site's current sample count.</param>
    /// <param name="decks">The site's current dealt masks.</param>
    /// <param name="sampleCount">How many samples to consume.</param>
    /// <param name="decksAfter">The dealt masks after the pass, or <see langword="null"/> when the source never deals.</param>
    /// <param name="reason">Why the pass was refused, on failure.</param>
    /// <returns><see langword="true"/> when all samples were consumed.</returns>
    public static bool TryAdvanceBatch(WorldGenerator generator, CellKind targetKind, ulong seedState, ulong stream, long cursor, IReadOnlyList<long>? decks, int sampleCount, out IReadOnlyList<long>? decksAfter, out string reason) => TryRunBatch(
        generator: generator,
        targetKind: targetKind,
        seedState: seedState,
        stream: stream,
        cursor: cursor,
        decks: decks,
        values: Span<long>.Empty,
        sampleCount: sampleCount,
        writeValues: false,
        decksAfter: out decksAfter,
        reason: out reason
    );
    /// <summary>Checks whether a numeric source can complete one batch from its current dealt masks without
    /// executing it. Only <see cref="WorldGeneratorMode.WithoutReplacement"/> can run out mid-batch; other modes
    /// either never deal or reshuffle in the same sample.</summary>
    /// <param name="generator">The resolved source.</param>
    /// <param name="decks">The site's current dealt masks.</param>
    /// <param name="sampleCount">The required batch length.</param>
    /// <param name="reason">Why the source cannot supply the batch.</param>
    /// <returns><see langword="true"/> when batch execution cannot exhaust the source.</returns>
    public static bool TryCheckBatchCapacity(WorldGenerator generator, IReadOnlyList<long>? decks, long sampleCount, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: generator);

        reason = string.Empty;

        if (
            (sampleCount <= 0L) ||
            (generator.Source != WorldGeneratorSource.WeightedNumeric) ||
            (generator.Mode != WorldGeneratorMode.WithoutReplacement)
        ) {
            return true;
        }

        var outcomes = (generator.Weighted ?? []);
        var cards = 0L;

        foreach (var outcome in outcomes) {
            if (outcome is not null) {
                cards += Math.Max(val1: 1, val2: (outcome.Count ?? 1));
            }
        }

        // The source-shape validator owns this earlier error. Stop here rather than attempting to enumerate an
        // invalid oversized deck or emitting a misleading second batch-capacity diagnosis.
        if (cards > WorldGeneratorCapacity.MaxCardsPerSet) {
            return true;
        }

        var deck = ((decks is { Count: > 0 }) ? unchecked((ulong)decks[0]) : 0UL);
        var card = 0;
        var available = 0L;

        foreach (var outcome in outcomes) {
            if (outcome is null) {
                continue;
            }

            for (var copy = Math.Max(val1: 1, val2: (outcome.Count ?? 1)); (copy > 0); copy--) {
                if ((outcome.Weight != 0UL) && ((deck & (1UL << card)) == 0UL)) {
                    available++;
                }

                card++;
            }
        }

        if (available >= sampleCount) {
            return true;
        }

        reason = $"can supply only {available} positive-weight undealt card{((available == 1L) ? string.Empty : "s")} in mode=withoutReplacement, but the lattice pass requires {sampleCount} samples";

        return false;
    }
    // Each VARIABLE-LENGTH ladder rung folds its LENGTH before its content, so two different rung sequences can never
    // present the same byte stream to the hash; the fixed-width rungs added directly are self-delimiting (see this
    // type's remarks).
    private static void FoldDelimited(ref Fnv1aHash hash, string text) {
        hash.Add(value: ((ulong)text.Length));

        foreach (var ch in text) {
            hash.Add(value: ((uint)ch));
        }
    }
    /// <summary>Determines whether a source of <paramref name="source"/> shape may deal — carry a
    /// <see cref="WorldGeneratorMode"/> other than <see cref="WorldGeneratorMode.WithReplacement"/> and persist dealt masks
    /// on its site.</summary>
    /// <param name="source">The source shape.</param>
    /// <returns><see langword="true"/> for the two alias-table shapes, <see cref="WorldGeneratorSource.Markov"/> and
    /// <see cref="WorldGeneratorSource.WeightedNumeric"/>.</returns>
    public static bool Deals(WorldGeneratorSource source) => (source is WorldGeneratorSource.Markov or WorldGeneratorSource.WeightedNumeric);
    /// <summary>Returns how many <c>Pcg32XshRr</c> advances one sample of <paramref name="source"/> costs — the fixed-cost
    /// figure cursor seeking depends on being exact.</summary>
    /// <param name="source">The source shape.</param>
    /// <returns>The per-sample advance cost.</returns>
    public static ulong AdvancesPerSample(WorldGeneratorSource source) => source switch {
        // The two alias-table shapes; Puck.Maths documents the cost as exactly two advances per sample.
        WorldGeneratorSource.Markov or WorldGeneratorSource.WeightedNumeric => 2UL,
        // One fixed-cost draw each, never a rejection loop.
        WorldGeneratorSource.UniformRange or WorldGeneratorSource.StreamDraw => 1UL,
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(source),
        actualValue: source,
        message: "unrecognized generator source"
    ),
    };
    /// <summary>Folds the seed ladder's four rungs into one <c>Pcg32XshRr</c> starting state — see this type's remarks
    /// for the ladder and why each rung is length-delimited. Allocation-free.</summary>
    /// <param name="worldSeed">The document's <c>generation.worldSeed</c>.</param>
    /// <param name="instanceIdentity">The running instance's own identity.</param>
    /// <param name="site">The site descriptor (see <see cref="WorldDrawSites"/>).</param>
    /// <returns>The <c>Pcg32XshRr.Create</c> <c>state</c> argument.</returns>
    public static ulong ComputeSeedState(ulong worldSeed, string instanceIdentity, string site) {
        ArgumentNullException.ThrowIfNull(argument: instanceIdentity);
        ArgumentNullException.ThrowIfNull(argument: site);

        var hash = Fnv1aHash.Create();

        hash.Add(value: EngineConstant);
        hash.Add(value: worldSeed);
        FoldDelimited(
            hash: ref hash,
            text: instanceIdentity
        );
        FoldDelimited(
            hash: ref hash,
            text: site
        );

        return hash.Value;
    }
    /// <summary>Derives a site's <c>Pcg32XshRr</c> stream id from its descriptor alone, masked small — see this
    /// type's remarks.</summary>
    /// <param name="site">The site descriptor.</param>
    /// <returns>The stream id.</returns>
    public static ulong ComputeStreamId(string site) {
        ArgumentNullException.ThrowIfNull(argument: site);

        var hash = Fnv1aHash.Create();

        FoldDelimited(
            hash: ref hash,
            text: site
        );

        return hash.Value & SiteStreamIdMask;
    }
    /// <summary>Returns the one source-to-site kind rule, asked by every door that can reach a draw: document validation, a
    /// rule's <c>generate</c> effect at rule-compile time, the boot resolver, and the live mutation. Stated once
    /// because four readings of it is how they drift.</summary>
    /// <param name="source">The source shape.</param>
    /// <param name="targetKind">The site's declared cell kind.</param>
    /// <param name="reason">Why the pairing was refused, in the author's own vocabulary, or empty when it holds.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> may write a <paramref name="targetKind"/> site.</returns>
    public static bool TryCheckTargetKind(WorldGeneratorSource source, CellKind targetKind, out string reason) {
        if (WritesText(source: source)) {
            if (targetKind == CellKind.Text) {
                reason = string.Empty;

                return true;
            }

            reason = $"source={WorldRefusalSpelling.GeneratorSource(source: source)} writes text, but the site is kind={WorldRefusalSpelling.Kind(kind: targetKind)}";

            return false;
        }

        if (targetKind is CellKind.Int or CellKind.Fixed) {
            reason = string.Empty;

            return true;
        }

        reason = $"source={WorldRefusalSpelling.GeneratorSource(source: source)} writes a numeric value, but the site is kind={WorldRefusalSpelling.Kind(kind: targetKind)}";

        return false;
    }
    /// <summary>Fires one emission of <paramref name="generator"/> at a site already seeked to
    /// <paramref name="cursor"/>.</summary>
    /// <param name="generator">The resolved source.</param>
    /// <param name="targetKind">The site's declared cell kind; a mismatch refuses by name before any draw runs.</param>
    /// <param name="seedState">The seed-ladder fold (see <see cref="ComputeSeedState"/>).</param>
    /// <param name="stream">The site's stream id (see <see cref="ComputeStreamId"/>).</param>
    /// <param name="cursor">The site's current sample count.</param>
    /// <param name="decks">The site's current per-context dealt masks (may be empty).</param>
    /// <param name="result">The emission, on success.</param>
    /// <param name="reason">Why the emission was refused, on failure.</param>
    /// <returns><see langword="true"/> on a successful emission.</returns>
    public static bool TryFire(WorldGenerator generator, CellKind targetKind, ulong seedState, ulong stream, long cursor, IReadOnlyList<long>? decks, out FireResult result, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: generator);

        result = default;

        if (cursor < 0) {
            reason = $"cursor {cursor} is negative — a draw cursor is a non-negative sample count";

            return false;
        }

        if (!Enum.IsDefined(value: generator.Mode)) {
            reason = $"mode '{generator.Mode}' is not a defined WorldGeneratorMode";

            return false;
        }

        if (!TryCheckTargetKind(
            source: generator.Source,
            targetKind: targetKind,
            reason: out reason
        )) {
            return false;
        }

        var rng = Pcg32XshRr.Create(
            state: seedState,
            stream: stream
        );

        // The seek: one jump to the site's recorded position. Fixed per-sample cost is what makes this exact.
        rng.Advance(count: unchecked((((ulong)cursor) * AdvancesPerSample(source: generator.Source))));

        switch (generator.Source) {
            case WorldGeneratorSource.Markov:
                return TryFireMarkov(
                    decks: decks,
                    generator: generator,
                    reason: out reason,
                    result: out result,
                    rng: ref rng
                );
            default: {
                    var deals = (generator.Mode != WorldGeneratorMode.WithReplacement);
                    var deck = ((deals && (decks is { Count: > 0 })) ? unchecked((ulong)decks[0]) : 0UL);

                    if (!TryDrawNumeric(
                        deck: ref deck,
                        generator: generator,
                        reason: out reason,
                        rng: ref rng,
                        value: out var value
                    )) {
                        return false;
                    }

                    result = new FireResult(
                        Text: null,
                        Numeric: value,
                        Samples: 1L,
                        Decks: (deals ? [unchecked((long)deck)] : null)
                    );
                    reason = string.Empty;

                    return true;
                }
        }
    }
    /// <summary>Resolves the source a site's facet draws from — the named row of the document's <c>generators</c>
    /// section, or the facet's own inline source. The one resolution both validation and every firing door share, so
    /// "which source is this site's" is never answered twice.</summary>
    /// <param name="generators">The document's declared sources.</param>
    /// <param name="draw">The site's facet.</param>
    /// <param name="generator">The resolved source, on success.</param>
    /// <param name="reason">Why resolution was refused, on failure.</param>
    /// <returns><see langword="true"/> when the facet resolves to exactly one source.</returns>
    public static bool TryResolveSource(IReadOnlyList<WorldGeneratorRow>? generators, WorldDraw draw, out WorldGenerator generator, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: draw);

        generator = null!;

        if ((draw.Source is null) == (draw.Generator is null)) {
            reason = ((draw.Source is null)
                ? "declares neither 'source' nor 'generator' — a draw names a declared source or inlines one"
                : "declares both 'source' and 'generator' — a draw names a declared source or inlines one, never both"
            );

            return false;
        }

        if (draw.Generator is { } inline) {
            generator = inline;
            reason = string.Empty;

            return true;
        }

        foreach (var row in (generators ?? [])) {
            if (row.Name == draw.Source!.Value) {
                generator = row.Generator;
                reason = string.Empty;

                return true;
            }
        }

        reason = $"names no declared generator '{draw.Source}' — add it to the document's 'generators' section or inline the source at the site";

        return false;
    }
    /// <summary>Determines whether one emission of <paramref name="source"/> is text rather than a numeric value — the single
    /// fact behind the source/site kind rule (see <see cref="TryCheckTargetKind"/>).</summary>
    /// <param name="source">The source shape.</param>
    /// <returns><see langword="true"/> for <see cref="WorldGeneratorSource.Markov"/>.</returns>
    public static bool WritesText(WorldGeneratorSource source) => (source == WorldGeneratorSource.Markov);
}
