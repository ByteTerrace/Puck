namespace Puck.Maths.Tests;

/// <summary>
/// Module 1 — operand sources over the <see cref="FixedQ4816"/> raw space and pairs/quads thereof. Three streams feed
/// every law: a single committed <see cref="EdgeRaws"/> set (with a budget-bounded cross product), an edge-biased
/// deterministic sampler seeded by the house <see cref="Pcg32XshRr"/>, and the rolling <see cref="Frontier"/> mapped
/// through the house stratified <see cref="DigitalNetSampler"/>. No law generates its own operands; each declares a
/// <see cref="Domain"/> and receives cases from here.
/// </summary>
internal static class Domains {
    /// <summary>The named edge raws — extremes and off-by-ones around every seam the fixed-point kernels branch on
    /// (the narrow/wide product boundary, the wrap extremes, the fractional-bit boundary). The single committed copy;
    /// every edge battery draws from this array.</summary>
    public static readonly long[] EdgeRaws = [
        0L, 1L, -1L, long.MinValue, (long.MinValue + 1L), long.MaxValue,
        ((1L << 31) - 1L), -((1L << 31) - 1L), (1L << 31), -(1L << 31), ((1L << 31) + 1L), -((1L << 31) + 1L),
        (1L << 47), -(1L << 47), ((1L << 47) + 1L), ((1L << 47) - 1L), -((1L << 47) + 1L), -((1L << 47) - 1L),
        65536L, -65536L, 32768L, -32768L, 256L, -256L,
    ];

    // The house stratified sampler's plane direction numbers, built once. SamplePlane maps a point index to a
    // two-dimensional Sobol' point (x, y) whose first 2^m points hit every dyadic box exactly once.
    private static readonly uint[] PlaneDirectionNumbers = BuildPlaneDirectionNumbers();

    /// <summary>Maps a frontier sample index to a full-range raw operand. Documented mapping: the index selects a
    /// two-dimensional stratified <see cref="DigitalNetSampler"/> point <c>(x, y)</c>, and the raw is
    /// <c>(long)((x &lt;&lt; 32) | y)</c> — the high and low halves of the 64-bit raw are the two Sobol' coordinates,
    /// so progressive indices sweep the raw space with joint stratification of its halves.</summary>
    /// <param name="index">The frontier sample index.</param>
    /// <returns>The mapped raw operand.</returns>
    public static long FrontierRaw(long index) {
        var (x, y) = DigitalNetSampler.SamplePlane(
            directionNumbers: PlaneDirectionNumbers,
            index: unchecked((uint)index),
            scramble: (0U, 0U)
        );

        return unchecked((long)((((ulong)x) << 32) | y));
    }
    /// <summary>Enumerates element-component pairs <c>(u, v)</c> for a domain: an edge battery, an edge-biased random
    /// batch, and the domain's frontier block.</summary>
    /// <param name="domain">The operand domain.</param>
    /// <param name="index">The domain's current frontier counter.</param>
    /// <param name="tier">The tier, which sizes the batches.</param>
    /// <returns>The lazily generated pairs.</returns>
    public static IEnumerable<(long U, long V)> Pairs(Domain domain, long index, Tier tier) {
        var edges = domain.MapEdges(source: EdgeRaws);

        // Edge battery: every (u, v) over the (mapped) edge set — the exhaustive square, cheap enough for every tier.
        foreach (var u in edges) {
            foreach (var v in edges) {
                yield return (u, v);
            }
        }

        // Edge-biased random batch, deterministically seeded from the domain and its frontier position.
        var rng = domain.Rng(index: index);
        var randomCount = RandomCount(tier: tier);

        for (var sample = 0; (sample < randomCount); ++sample) {
            yield return (domain.NextRaw(rng: ref rng), domain.NextRaw(rng: ref rng));
        }

        // Frontier block: contiguous stratified indices, mapped and folded to the domain.
        var block = domain.Block;
        var start = (index * block);

        for (var offset = 0; ((offset + 1) < block); offset += 2) {
            yield return (domain.Fold(raw: FrontierRaw(index: (start + offset))), domain.Fold(raw: FrontierRaw(index: ((start + offset) + 1L))));
        }
    }
    /// <summary>Enumerates operand quads <c>(u1, v1, u2, v2)</c> for a binary law: an edge battery (bounded on the
    /// default tier, exhaustive on deep), an edge-biased random batch, and the domain's frontier block.</summary>
    /// <param name="domain">The operand domain.</param>
    /// <param name="index">The domain's current frontier counter.</param>
    /// <param name="tier">The tier, which sizes the batches and selects the edge-battery breadth.</param>
    /// <returns>The lazily generated quads.</returns>
    public static IEnumerable<(long U1, long V1, long U2, long V2)> Quads(Domain domain, long index, Tier tier) {
        var edges = domain.MapEdges(source: EdgeRaws);

        if (tier == Tier.Deep) {
            // The full four-operand cross product over the edge set — the exhaustive narrow/wide-seam battery.
            foreach (var u1 in edges) {
                foreach (var v1 in edges) {
                    foreach (var u2 in edges) {
                        foreach (var v2 in edges) {
                            yield return (u1, v1, u2, v2);
                        }
                    }
                }
            }
        } else {
            // Budget-bounded battery: every left pair over the edge square, each against a rotating right pair, so the
            // seam boundaries appear on both operands without paying the full 2^4 combinatorial cost on the fast tiers.
            var count = edges.Length;
            var rotation = 0;

            foreach (var u1 in edges) {
                foreach (var v1 in edges) {
                    yield return (u1, v1, edges[(rotation % count)], edges[((rotation + (count / 2)) % count)]);

                    ++rotation;
                }
            }
        }

        var rng = domain.Rng(index: index);
        var randomCount = RandomCount(tier: tier);

        for (var sample = 0; (sample < randomCount); ++sample) {
            yield return (domain.NextRaw(rng: ref rng), domain.NextRaw(rng: ref rng), domain.NextRaw(rng: ref rng), domain.NextRaw(rng: ref rng));
        }

        var block = domain.Block;
        var start = (index * block);

        for (var offset = 0; ((offset + 3) < block); offset += 4) {
            yield return (
                domain.Fold(raw: FrontierRaw(index: (start + offset))),
                domain.Fold(raw: FrontierRaw(index: ((start + offset) + 1L))),
                domain.Fold(raw: FrontierRaw(index: ((start + offset) + 2L))),
                domain.Fold(raw: FrontierRaw(index: ((start + offset) + 3L)))
            );
        }
    }
    /// <summary>Enumerates operand pairs of lane vectors for a multi-lane law: an edge battery that broadcasts each edge
    /// raw one lane at a time and then across every lane at once, an edge-biased random batch, and the domain's frontier
    /// block. Single-lane operands isolate one basis position's contribution; the fully broadcast ones drive every lane
    /// of the product at once, which is where a fused accumulator's term count actually bites.</summary>
    /// <param name="domain">The operand domain.</param>
    /// <param name="index">The domain's current frontier counter.</param>
    /// <param name="tier">The tier, which sizes the batches.</param>
    /// <param name="width">The lane count.</param>
    /// <returns>The lazily generated pairs. The two buffers are REUSED across iterations — every law here consumes a
    /// pair before requesting the next — so a consumer that wants to retain one must copy it.</returns>
    public static IEnumerable<(long[] Left, long[] Right)> Vectors(Domain domain, long index, Tier tier, int width) {
        var edges = domain.MapEdges(source: EdgeRaws);
        var count = edges.Length;
        var left = new long[width];
        var right = new long[width];

        // One lane at a time: the left operand carries a single edge raw at lane a, the right a rotated edge at a
        // rotated lane, so every (lane, lane) pairing appears with every edge over the sweep.
        for (var edge = 0; (edge < count); ++edge) {
            for (var lane = 0; (lane < width); ++lane) {
                Array.Clear(array: left);
                Array.Clear(array: right);

                left[lane] = edges[edge];
                right[((lane + edge) % width)] = edges[(((edge + 1) + lane) % count)];

                yield return (left, right);
            }
        }

        // Every lane at once: full-support operands, the case that fills the per-key term buffer.
        for (var edge = 0; (edge < count); ++edge) {
            var other = edges[((edge + (count / 2)) % count)];

            for (var lane = 0; (lane < width); ++lane) {
                left[lane] = edges[edge];
                right[lane] = other;
            }

            yield return (left, right);
        }

        var rng = domain.Rng(index: index);
        var randomCount = RandomCount(tier: tier);

        for (var sample = 0; (sample < randomCount); ++sample) {
            for (var lane = 0; (lane < width); ++lane) {
                left[lane] = domain.NextRaw(rng: ref rng);
                right[lane] = domain.NextRaw(rng: ref rng);
            }

            yield return (left, right);
        }

        var block = domain.Block;
        var start = (index * block);
        var stride = (2 * width);

        for (var offset = 0; ((offset + stride) <= block); offset += stride) {
            for (var lane = 0; (lane < width); ++lane) {
                left[lane] = domain.Fold(raw: FrontierRaw(index: ((start + offset) + lane)));
                right[lane] = domain.Fold(raw: FrontierRaw(index: (((start + offset) + width) + lane)));
            }

            yield return (left, right);
        }
    }
    /// <summary>Enumerates operand triples of lane vectors for a ternary law, with the same three streams
    /// <see cref="Vectors"/> uses.</summary>
    /// <param name="domain">The operand domain.</param>
    /// <param name="index">The domain's current frontier counter.</param>
    /// <param name="tier">The tier, which sizes the batches.</param>
    /// <param name="width">The lane count.</param>
    /// <returns>The lazily generated triples, over three REUSED buffers.</returns>
    public static IEnumerable<(long[] A, long[] B, long[] C)> VectorTriples(Domain domain, long index, Tier tier, int width) {
        var edges = domain.MapEdges(source: EdgeRaws);
        var count = edges.Length;
        var a = new long[width];
        var b = new long[width];
        var c = new long[width];

        // One lane at a time, on all three operands: the triples that isolate a single associator entry.
        for (var edge = 0; (edge < count); ++edge) {
            for (var lane = 0; (lane < width); ++lane) {
                Array.Clear(array: a);
                Array.Clear(array: b);
                Array.Clear(array: c);

                a[lane] = edges[edge];
                b[((lane + 1) % width)] = edges[((edge + 1) % count)];
                c[((lane + edge) % width)] = edges[(((edge + 2) + lane) % count)];

                yield return (a, b, c);
            }
        }

        for (var edge = 0; (edge < count); ++edge) {
            for (var lane = 0; (lane < width); ++lane) {
                a[lane] = edges[edge];
                b[lane] = edges[((edge + (count / 3)) % count)];
                c[lane] = edges[((edge + ((2 * count) / 3)) % count)];
            }

            yield return (a, b, c);
        }

        var rng = domain.Rng(index: index);
        var randomCount = RandomCount(tier: tier);

        for (var sample = 0; (sample < randomCount); ++sample) {
            for (var lane = 0; (lane < width); ++lane) {
                a[lane] = domain.NextRaw(rng: ref rng);
                b[lane] = domain.NextRaw(rng: ref rng);
                c[lane] = domain.NextRaw(rng: ref rng);
            }

            yield return (a, b, c);
        }

        var block = domain.Block;
        var start = (index * block);
        var stride = (3 * width);

        for (var offset = 0; ((offset + stride) <= block); offset += stride) {
            for (var lane = 0; (lane < width); ++lane) {
                a[lane] = domain.Fold(raw: FrontierRaw(index: ((start + offset) + lane)));
                b[lane] = domain.Fold(raw: FrontierRaw(index: (((start + offset) + width) + lane)));
                c[lane] = domain.Fold(raw: FrontierRaw(index: (((start + offset) + (2 * width)) + lane)));
            }

            yield return (a, b, c);
        }
    }

    private static int RandomCount(Tier tier) =>
        tier switch {
            Tier.Smoke => 16,
            Tier.Deep => 4096,
            _ => 256,
        };
    private static uint[] BuildPlaneDirectionNumbers() {
        var destination = new uint[DigitalNetSampler.PlaneDirectionNumberCount];

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: destination);

        return destination;
    }
}
/// <summary>
/// An operand domain: a named region of the raw space with its own committed frontier counter and its own edge-biased
/// sampler configuration. Distinct keys track independent progressive coverage. The default domain is the full signed
/// raw range; a <see cref="SublatticeShift"/> folds operands onto the exact fixed-point sublattice for laws that
/// require rounding-free arithmetic.
/// </summary>
/// <param name="Key">The persisted frontier key.</param>
/// <param name="Block">The frontier block size <c>B</c> — sample indices consumed per run.</param>
/// <param name="EdgeFraction">The fraction of random draws taken from the raw edge set.</param>
/// <param name="NeighborhoodFraction">The fraction of random draws taken from an edge plus a small delta.</param>
/// <param name="SublatticeShift">When non-zero, folds every operand to a multiple of <c>2^SublatticeShift</c> bounded
/// in magnitude, so pairwise products are exact in Q16.</param>
internal readonly record struct Domain(string Key, int Block, double EdgeFraction, double NeighborhoodFraction, int SublatticeShift = 0) {
    // The bounded sublattice's integer span. ODD by choice: the shared edge set is mostly powers of two, and a
    // power-of-two span would reduce nearly all of them onto zero, while an odd one sends them to distinct residues.
    // The magnitude bound is what makes the domain exact — with |integer part| < 2^12 every element component and
    // element norm the laws form stays below 2^42 of the signed carrier, so nothing rounds and nothing wraps there.
    // (A product of two element norms can still leave the carrier; both sides of the multiplicativity law compute the
    // same exact integer, so they wrap alike.)
    private const long SublatticeSpan = 4093L;

    /// <summary>Derives the deterministic generator for this domain at a frontier position — the seed is a pure
    /// function of the key and the counter, so the enriched failure message can reproduce it.</summary>
    /// <param name="index">The frontier counter.</param>
    /// <returns>A ready-to-draw generator.</returns>
    public Pcg32XshRr Rng(long index) {
        var hash = StableHash(text: Key);

        return Pcg32XshRr.Create(state: MixSeed(hash: hash, index: index), stream: hash & Pcg32XshRr.MaxStream);
    }
    /// <summary>The seed this domain uses at a frontier position, for the enriched failure message. Equal by
    /// construction to the state the <see cref="Rng"/> generator starts from, so a reported seed reproduces the run.</summary>
    /// <param name="index">The frontier counter.</param>
    /// <returns>The generator seed.</returns>
    public ulong Seed(long index) =>
        MixSeed(hash: StableHash(text: Key), index: index);
    /// <summary>Draws the next domain-folded raw from an edge-biased mixture.</summary>
    /// <param name="rng">The generator, advanced in place.</param>
    /// <returns>A raw operand in the domain.</returns>
    public long NextRaw(ref Pcg32XshRr rng) {
        var pick = (rng.NextUInt32() * (1.0 / 4294967296.0));
        long raw;

        if (pick < EdgeFraction) {
            raw = Domains.EdgeRaws[rng.NextUInt32(minimum: 0U, maximum: ((uint)(Domains.EdgeRaws.Length - 1)))];
        } else if (pick < (EdgeFraction + NeighborhoodFraction)) {
            var edge = Domains.EdgeRaws[rng.NextUInt32(minimum: 0U, maximum: ((uint)(Domains.EdgeRaws.Length - 1)))];
            var delta = (((long)rng.NextUInt32(maximum: 8U, minimum: 0U)) - 4L);

            raw = unchecked((edge + delta));
        } else {
            raw = unchecked((long)((((ulong)rng.NextUInt32()) << 32) | rng.NextUInt32()));
        }

        return Fold(raw: raw);
    }
    /// <summary>Folds a raw onto the domain — the identity for the full-range domain, or a bounded sublattice value.</summary>
    /// <param name="raw">The candidate raw.</param>
    /// <returns>The domain-legal raw.</returns>
    public long Fold(long raw) {
        if (SublatticeShift == 0) {
            return raw;
        }

        // Reduce the WHOLE raw rather than one bit window of it, so every bit of the draw steers the result: a
        // full-range random draw and the frontier's stratified sweep (which varies its high bits first) both spread
        // across the sublattice instead of collapsing onto the few values a fixed window can see. The C# remainder
        // keeps the draw's sign, and the shift places the value on the lattice.
        return ((raw % SublatticeSpan) << SublatticeShift);
    }
    /// <summary>Maps the shared edge set into the domain (folding onto the sublattice when configured).</summary>
    /// <param name="source">The shared edge set.</param>
    /// <returns>The domain-mapped edges.</returns>
    public long[] MapEdges(long[] source) {
        if (SublatticeShift == 0) {
            return source;
        }

        var mapped = new long[source.Length];

        for (var i = 0; (i < source.Length); ++i) {
            mapped[i] = Fold(raw: source[i]);
        }

        return mapped;
    }

    // Mixes the domain hash with the frontier counter into a generator state, so the seed is a pure function of the key
    // and the counter (the golden-ratio odd constant decorrelates adjacent counters). The one home of the mix, shared by
    // the generator and its reported seed.
    private static ulong MixSeed(ulong hash, long index) =>
        unchecked(hash ^ (((ulong)index) * 0x9E3779B97F4A7C15UL));
    private static ulong StableHash(string text) {
        // The house 64-bit FNV-1a over the key's code units; deterministic and machine-independent, used only to derive
        // seeds. Reuses Puck.Maths' accumulator rather than re-deriving the offset basis and prime here.
        var hash = Fnv1aHash.Create();

        foreach (var character in text) {
            hash.Add(value: ((uint)character));
        }

        return hash.Value;
    }
}
