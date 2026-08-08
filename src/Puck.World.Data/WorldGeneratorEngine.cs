using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The ONE deterministic sampling core behind every <see cref="WorldDraw"/> site — shared by the LIVE <c>Generate</c>
/// mutation (<c>Puck.World.Server.WorldServer</c>) and the BOOT/first-fill resolver
/// (<c>Puck.World.WorldDrawBootResolver</c>), which runs before a <c>Server.WorldServer</c> exists at all and
/// therefore cannot reach into that project. Living here — the lowest layer both reach — is what keeps there from
/// ever being a second implementation of "how a draw draws" to disagree with the first.
/// </summary>
/// <remarks>
/// <para><b>The seed ladder, four rungs.</b> <see cref="ComputeSeedState"/> folds, in order:
/// <list type="number">
/// <item><description>the ENGINE CONSTANT — never authored, never varies by document; it exists only so this
/// system's streams cannot collide by accident with any other seeded system that folds the same document
/// values;</description></item>
/// <item><description>the document's own <c>generation.worldSeed</c> — the author's single "reroll the whole world"
/// lever, moving every site at once;</description></item>
/// <item><description>the running INSTANCE's identity — the boot instance's constant name, or a spawned
/// <c>world.instance.start</c> instance's own name. NOT document data: it is what lets three instances of ONE
/// document draw differently while each stays exactly reproducible from (document, instance name, draw
/// history);</description></item>
/// <item><description>the SITE DESCRIPTOR (see <see cref="WorldDrawSites"/>) — what separates two sites. An identity,
/// never a position: a positional ordinal is read off the live document's site set, which moves whenever the boot
/// resolver clears a settled facet, a <c>world.row.remove state</c> retires a draw row, or an <c>UpsertStateRow</c> adds
/// one — silently re-pointing a live site's stream while its cursor kept counting.</description></item>
/// </list>
/// Every rung is LENGTH-DELIMITED before its bytes, so no two different rung sequences can fold to the same
/// pre-image: without the delimiter an instance named <c>ab</c> at site <c>c</c> and one named <c>a</c> at site
/// <c>bc</c> present the same byte stream to the hash.</para>
/// <para><b>The stream id is derived from the site alone</b> and MASKED small
/// (<see cref="SiteStreamIdMask"/>) — <c>Pcg32XshRr</c> collapses increments whose ids sit <c>2^62</c> apart, which a
/// masked id can never reach. A stream-id collision between two sites is harmless because their SEEDS still differ by
/// the descriptor rung above.</para>
/// <para><b>Seeking, not replaying.</b> Every source costs a FIXED number of generator advances per sample
/// (<see cref="AdvancesPerSample"/>) — the uniform range is a multiply-high map rather than a rejection-sampled
/// bounded draw precisely so this stays true. Resuming a site at cursor <c>n</c> is therefore one
/// <c>Pcg32XshRr.Advance(n * cost)</c>, an O(1) jump, and NEVER a replay of the earlier draws. There is no per-tick
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
    /// <param name="Decks">The site's updated per-context dealt masks, or <see langword="null"/> when nothing dealt
    /// (every numeric source, and a Markov source under <see cref="WorldGeneratorMode.WithReplacement"/>).</param>
    public readonly record struct FireResult(string? Text, long? Numeric, long Samples, IReadOnlyList<long>? Decks);

    /// <summary>Returns how many <c>Pcg32XshRr</c> advances ONE sample of <paramref name="source"/> costs — the fixed-cost
    /// figure cursor seeking depends on being exact.</summary>
    /// <param name="source">The source shape.</param>
    /// <returns>The per-sample advance cost.</returns>
    public static ulong AdvancesPerSample(WorldGeneratorSource source) => source switch {
        // The two alias-table shapes; Puck.Maths documents the cost as exactly two advances per sample.
        WorldGeneratorSource.Markov or WorldGeneratorSource.WeightedNumeric => 2UL,
        // One fixed-cost draw each, never a rejection loop.
        WorldGeneratorSource.UniformRange or WorldGeneratorSource.StreamDraw => 1UL,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(source), actualValue: source, message: "unrecognized generator source"),
    };

    /// <summary>Determines whether one emission of <paramref name="source"/> is TEXT rather than a numeric value — the single
    /// fact behind the source/site kind rule (see <see cref="TryCheckTargetKind"/>).</summary>
    /// <param name="source">The source shape.</param>
    /// <returns><see langword="true"/> for <see cref="WorldGeneratorSource.Markov"/>.</returns>
    public static bool WritesText(WorldGeneratorSource source) => (source == WorldGeneratorSource.Markov);

    /// <summary>Returns the ONE source-to-site kind rule, asked by every door that can reach a draw: document validation, a
    /// rule's <c>generate</c> effect at rule-COMPILE time, the boot resolver, and the live mutation. Stated once
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

            reason = $"source={DescribeSource(source: source)} writes text, but the site is kind={DescribeKind(kind: targetKind)}";

            return false;
        }

        if (targetKind is CellKind.Int or CellKind.Fixed) {
            reason = string.Empty;

            return true;
        }

        reason = $"source={DescribeSource(source: source)} writes a numeric value, but the site is kind={DescribeKind(kind: targetKind)}";

        return false;
    }

    /// <summary>Resolves the source a site's facet draws from — the named row of the document's <c>generators</c>
    /// section, or the facet's own inline source. The ONE resolution both validation and every firing door share, so
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
                : "declares both 'source' and 'generator' — a draw names a declared source or inlines one, never both");

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
        FoldDelimited(hash: ref hash, text: instanceIdentity);
        FoldDelimited(hash: ref hash, text: site);

        return hash.Value;
    }

    /// <summary>Derives a site's <c>Pcg32XshRr</c> stream id from its descriptor alone, masked small — see this
    /// type's remarks.</summary>
    /// <param name="site">The site descriptor.</param>
    /// <returns>The stream id.</returns>
    public static ulong ComputeStreamId(string site) {
        ArgumentNullException.ThrowIfNull(argument: site);

        var hash = Fnv1aHash.Create();

        FoldDelimited(hash: ref hash, text: site);

        return (hash.Value & SiteStreamIdMask);
    }

    /// <summary>Fires ONE emission of <paramref name="generator"/> at a site already seeked to
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

        if (!TryCheckTargetKind(source: generator.Source, targetKind: targetKind, reason: out reason)) {
            return false;
        }

        var rng = Pcg32XshRr.Create(state: seedState, stream: stream);

        // The seek: one jump to the site's recorded position. Fixed per-sample cost is what makes this exact.
        rng.Advance(count: unchecked((ulong)cursor * AdvancesPerSample(source: generator.Source)));

        switch (generator.Source) {
            case WorldGeneratorSource.Markov:
                return TryFireMarkov(generator: generator, rng: ref rng, decks: decks, result: out result, reason: out reason);
            case WorldGeneratorSource.UniformRange: {
                var span = unchecked((uint)(generator.RangeMax!.Value - generator.RangeMin!.Value));
                var fraction = rng.NextUnitFraction32();
                // Multiply-high map of a uniform fraction onto [0, span] — one fixed-cost advance, no rejection, so
                // cursor seeking stays exact. The at-most-n/2^32 deviation this trades for an unbiased-via-rejection
                // draw is the deliberate price of being seekable (see WorldGeneratorSource.UniformRange).
                var offset = (uint)(((ulong)span + 1UL) * fraction.Value >> 32);

                result = new FireResult(Text: null, Numeric: (generator.RangeMin.Value + offset), Samples: 1L, Decks: null);
                reason = string.Empty;

                return true;
            }
            case WorldGeneratorSource.WeightedNumeric: {
                var outcomes = generator.Weighted!;
                var pool = new (long Value, ulong Weight)[outcomes.Count];

                for (var index = 0; (index < outcomes.Count); index++) {
                    pool[index] = (outcomes[index].Value, outcomes[index].Weight);
                }

                result = new FireResult(Text: null, Numeric: WeightedSampler.Create<long>(entries: pool).Sample(generator: ref rng), Samples: 1L, Decks: null);
                reason = string.Empty;

                return true;
            }
            case WorldGeneratorSource.StreamDraw:
                result = new FireResult(Text: null, Numeric: unchecked((long)rng.NextUInt32()), Samples: 1L, Decks: null);
                reason = string.Empty;

                return true;
            default:
                reason = $"unrecognized generator source '{generator.Source}'";

                return false;
        }
    }

    private static bool TryFireMarkov(WorldGenerator generator, ref Pcg32XshRr rng, IReadOnlyList<long>? decks, out FireResult result, out string reason) {
        var contexts = generator.Contexts!;
        var tokens = new List<string>(capacity: generator.Bound);
        var context = generator.Start!.Value;
        var samples = 0L;
        var working = new long[contexts.Count];
        var dealt = false;

        for (var index = 0; (index < working.Length); index++) {
            working[index] = (((decks is not null) && (index < decks.Count)) ? decks[index] : 0L);
        }

        while (true) {
            var ordinal = -1;

            for (var index = 0; (index < contexts.Count); index++) {
                if (contexts[index].Key == context) {
                    ordinal = index;

                    break;
                }
            }

            if (ordinal < 0) {
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
            var pool = new List<(string Token, ulong Weight)>(capacity: alternatives.Count);
            var indices = new List<int>(capacity: alternatives.Count);

            for (var index = 0; (index < alternatives.Count); index++) {
                if ((generator.Mode == WorldGeneratorMode.WithReplacement) || ((deck & (1UL << index)) == 0UL)) {
                    pool.Add(item: (alternatives[index].Token, alternatives[index].Weight));
                    indices.Add(item: index);
                }
            }

            if (pool.Count == 0) {
                // The DECK is dealt out. What happens next is AUTHORED, never inferred.
                if (generator.Mode == WorldGeneratorMode.WithoutReplacement) {
                    result = default;
                    reason = $"context '{context}' is dealt out ({alternatives.Count} alternatives, mode withoutReplacement) — declare mode reshuffleOnExhaustion to deal again";

                    return false;
                }

                deck = 0UL;

                for (var index = 0; (index < alternatives.Count); index++) {
                    pool.Add(item: (alternatives[index].Token, alternatives[index].Weight));
                    indices.Add(item: index);
                }
            }

            var table = WeightedSampler.Create<string>(entries: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: pool));
            var picked = indices[table.SampleIndex(generator: ref rng)];

            samples++;

            if (generator.Mode != WorldGeneratorMode.WithReplacement) {
                working[ordinal] = unchecked((long)(deck | (1UL << picked)));
                dealt = true;
            }

            tokens.Add(item: alternatives[picked].Token);
            context = alternatives[picked].Next;
        }

        result = new FireResult(Text: string.Join(separator: ' ', values: tokens), Numeric: null, Samples: samples, Decks: (dealt ? working : null));
        reason = string.Empty;

        return true;
    }

    // Each VARIABLE-LENGTH ladder rung folds its LENGTH before its content, so two different rung sequences can never
    // present the same byte stream to the hash; the fixed-width rungs added directly are self-delimiting (see this
    // type's remarks).
    private static void FoldDelimited(ref Fnv1aHash hash, string text) {
        hash.Add(value: (ulong)text.Length);

        foreach (var ch in text) {
            hash.Add(value: (uint)ch);
        }
    }

    /// <summary>Describes the authored spelling of a source shape, for a refusal message.</summary>
    private static string DescribeSource(WorldGeneratorSource source) =>
        (char.ToLowerInvariant(c: source.ToString()[0]) + source.ToString()[1..]);

    /// <summary>Describes the authored spelling of a cell kind, for a refusal message.</summary>
    private static string DescribeKind(CellKind kind) => kind.ToString().ToLowerInvariant();
}
