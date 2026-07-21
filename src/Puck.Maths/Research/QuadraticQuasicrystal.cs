using Puck.Maths.Research;

namespace Puck.Maths;

/// <summary>
/// A periodic-tail inflation representative of the tiling language beneath any quadratic irrational, generated for an
/// arbitrary continued-fraction period. The repeating block <c>[b₀, …, b_{ℓ-1}]</c> that <see cref="ContinuedFraction"/>
/// reads becomes the
/// composed two-letter substitution <c>τ_{b₀} ∘ … ∘ τ_{b_{ℓ-1}}</c>, where each factor <c>τₖ</c> sends
/// <c>long → longᵏ short</c> and <c>short → long</c>; its incidence matrix is exactly the substitution matrix
/// <see cref="QuadraticInflation"/> reads from the same period, so the two lenses agree by construction. The fixed point
/// of the composed substitution is an aperiodic, never-repeating tiling — a genuine one-dimensional quasicrystal
/// (Sturmian word) associated with that periodic tail. The finite preperiod is deliberately not applied: quadratic
/// irrationals with the same oriented repeating block therefore produce the same representative, even when their full
/// continued fractions differ. This is the general form the metallic siblings specialize: the
/// golden Fibonacci word of <see cref="MetallicQuasicrystal"/> is the period <c>[1]</c> and its silver Pell word is the
/// period <c>[2]</c>.
/// </summary>
/// <remarks>
/// <see cref="Word(long, long, long, long, Span{bool})"/> streams a run of the word — <see langword="false"/> for the
/// short tile, <see langword="true"/> for the long — in amortized constant time per tile: it precomputes the two letter
/// images of the composed substitution once, bounded to the requested output length, then reads the growing output as its
/// own expansion queue. The word is self-similar under inflation and has complexity <c>k + 1</c> for every factor length
/// <c>k</c> (the defining Sturmian property). For a substitution matrix <c>[[A,B],[C,D]]</c>, three ratios must be kept
/// distinct: the inflation scale is its Perron eigenvalue <c>λ</c>, the geometric long/short length ratio is
/// <c>C/(λ−A)</c>, and the asymptotic long/short frequency ratio is <c>B/(λ−A)</c>. They coincide in the relevant ways
/// for a single-term metallic period, but not in general. <see cref="InflationFactor(long, long, long, long)"/> returns the
/// scale through
/// <see cref="QuadraticInflation.InflationFactor"/>, and <see cref="Positions(long, long, long, long, ReadOnlySpan{bool}, Span{FixedQ4816})"/>
/// lays the tiles on the line with the short tile as the unit and the long tile as <see cref="LongTileLength(long, long, long, long)"/> —
/// the one approximate seam, matching <see cref="MetallicQuasicrystal.Position(int, long, long)"/>; the word above it is exact combinatorics.
/// </remarks>
public static class QuadraticQuasicrystal {
    /// <summary>Compiles an exact logarithmic-time random-access index for the periodic-tail representative.</summary>
    public static QuadraticQuasicrystalIndex Compile(long p, long q, long d, long r) =>
        new(p, q, d, r);

    /// <summary>Fills a leading run of the periodic-tail representative for <c>(p + q·√d) / r</c>: the fixed point of the substitution its repeating continued-fraction block composes.</summary>
    /// <param name="p">The rational part of the numerator.</param>
    /// <param name="q">The coefficient of the surd; it must be positive.</param>
    /// <param name="d">The radicand; it must be at least two and not a perfect square.</param>
    /// <param name="r">The denominator; it must be non-zero.</param>
    /// <param name="tiles">Receives the tiling in physical order: <see langword="false"/> is the short tile, <see langword="true"/> the long. Every element is written.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="q"/> is not positive, <paramref name="d"/> is below two or a perfect square, or <paramref name="r"/> is zero.</exception>
    public static void Word(long p, long q, long d, long r, Span<bool> tiles) {
        Span<long> terms = stackalloc long[128];
        int periodStart;
        int periodLength;

        while (true) {
            try {
                _ = ContinuedFraction.Expand(
                    p: p,
                    q: q,
                    d: d,
                    r: r,
                    terms: terms,
                    periodStart: out periodStart,
                    periodLength: out periodLength
                );

                break;
            } catch (ArgumentException exception) when ((exception.ParamName == nameof(terms)) && (terms.Length < int.MaxValue)) {
                var nextLength = ((terms.Length <= (int.MaxValue / 2)) ? (terms.Length * 2) : int.MaxValue);

                terms = new long[nextLength];
            }
        }

        if (tiles.IsEmpty) { return; }

        var period = terms.Slice(start: periodStart, length: periodLength);

        // The composed substitution acts on two letters, so its whole action is the two images σ(long) and σ(short),
        // built once. The long image begins with the long tile, so the fixed point seeds from it.
        var longImage = ComposeImage(period: period, seedLong: true, maximumLength: tiles.Length);
        var shortImage = ComposeImage(period: period, seedLong: false, maximumLength: tiles.Length);

        // Stream the fixed point w = σ(w) = σ(w₀) σ(w₁) … : the output doubles as its own expansion queue. Each already
        // written tile is expanded once, in order, and appended — amortized O(1) per tile with no per-tile allocation.
        var write = Emit(image: longImage, tiles: tiles, at: 0);
        var read = 1;

        while (write < tiles.Length) {
            write = Emit(image: (tiles[read] ? longImage : shortImage), tiles: tiles, at: write);
            read += 1;
        }
    }
    /// <summary>Lays a run of the tiling word on the line, returning the start coordinate of each tile.</summary>
    /// <param name="p">The rational part of the numerator.</param>
    /// <param name="q">The coefficient of the surd; it must be positive.</param>
    /// <param name="d">The radicand; it must be at least two and not a perfect square.</param>
    /// <param name="r">The denominator; it must be non-zero.</param>
    /// <param name="tiles">The tiling word — as filled by <see cref="Word(long, long, long, long, Span{bool})"/>.</param>
    /// <param name="positions">Receives the start coordinate of each tile, with the short tile as the unit and the long tile as <see cref="LongTileLength(long, long, long, long)"/>. It must be at least as long as <paramref name="tiles"/>.</param>
    /// <returns>The coordinate just past the final tile — the total length of the run.</returns>
    /// <exception cref="ArgumentException"><paramref name="positions"/> is shorter than <paramref name="tiles"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The surd parameters are out of range — see <see cref="ContinuedFraction.Expand"/>.</exception>
    /// <exception cref="OverflowException">A tile coordinate exceeds the range of <see cref="FixedQ4816"/>.</exception>
    public static FixedQ4816 Positions(long p, long q, long d, long r, ReadOnlySpan<bool> tiles, Span<FixedQ4816> positions) {
        if (positions.Length < tiles.Length) {
            throw new ArgumentException(
                message: "the positions span must be at least as long as the tiling word",
                paramName: nameof(positions)
            );
        }

        var longTile = LongTileLength(p: p, q: q, d: d, r: r);
        var cursor = FixedQ4816.Zero;

        for (var index = 0; (index < tiles.Length); ++index) {
            positions[index] = cursor;
            cursor = checked(cursor + (tiles[index] ? longTile : FixedQ4816.One));
        }

        return cursor;
    }
    /// <summary>Returns the length of the long tile with the short tile as the unit — the ratio that makes the tiling self-similar under inflation.</summary>
    /// <param name="p">The rational part of the numerator.</param>
    /// <param name="q">The coefficient of the surd; it must be positive.</param>
    /// <param name="d">The radicand; it must be at least two and not a perfect square.</param>
    /// <param name="r">The denominator; it must be non-zero.</param>
    /// <returns>The long-tile length <c>(λ − D) / B = C / (λ − A)</c> from the substitution matrix — the left Perron eigenvector, which coincides with <see cref="InflationFactor(long, long, long, long)"/> only for a single-term period. The one approximate operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The surd parameters are out of range — see <see cref="ContinuedFraction.Expand"/>.</exception>
    public static FixedQ4816 LongTileLength(long p, long q, long d, long r) {
        // The tile lengths are the left Perron eigenvector (ℓ_long, ℓ_short) of the substitution matrix. Use the
        // algebraically equivalent ratio (λ − D) / B instead of C / (λ − A): λ − A can be smaller than half a Q48.16
        // ULP for a large partial quotient and round to zero even though the resulting tile length is representable.
        var inflation = QuadraticInflation.FromQuadraticIrrational(p: p, q: q, d: d, r: r);

        return ((inflation.InflationFactor() - FixedQ4816.FromInteger(value: inflation.D)) / FixedQ4816.FromInteger(value: inflation.B));
    }
    /// <summary>Returns the self-similarity scale of the periodic-tail quasicrystal for <c>(p + q·√d) / r</c>.</summary>
    /// <param name="p">The rational part of the numerator.</param>
    /// <param name="q">The coefficient of the surd; it must be positive.</param>
    /// <param name="d">The radicand; it must be at least two and not a perfect square.</param>
    /// <param name="r">The denominator; it must be non-zero.</param>
    /// <returns>The Perron eigenvalue as a fixed-point value, read through <see cref="QuadraticInflation.InflationFactor"/>; the one approximate operation. For a multi-term period, this is generally neither the geometric tile-length ratio nor the letter-frequency ratio.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The surd parameters are out of range — see <see cref="ContinuedFraction.Expand"/>.</exception>
    public static FixedQ4816 InflationFactor(long p, long q, long d, long r) =>
        QuadraticInflation.FromQuadraticIrrational(p: p, q: q, d: d, r: r).InflationFactor();

    /// <summary>Builds one letter image of the composed substitution by applying the period's factors from the innermost outward.</summary>
    /// <param name="period">The continued-fraction period; each entry <c>k</c> is the factor <c>τₖ</c>: <c>long → longᵏ short</c>, <c>short → long</c>.</param>
    /// <param name="seedLong">The seed letter — <see langword="true"/> for the long image, <see langword="false"/> for the short.</param>
    /// <param name="maximumLength">The longest prefix the caller can observe.</param>
    /// <returns>The observable prefix of the seed letter's image under the whole period.</returns>
    private static bool[] ComposeImage(ReadOnlySpan<long> period, bool seedLong, int maximumLength) {
        var current = new List<bool>(capacity: 4) { seedLong };

        // σ = τ_{b₀} ∘ … ∘ τ_{b_{ℓ-1}} applies the last factor first, so its incidence matrix is the period product
        // ∏ [[bᵢ, 1], [1, 0]] — the very matrix QuadraticInflation reads from the same period.
        for (var index = (period.Length - 1); (index >= 0); --index) {
            var factor = period[index];
            var doubledCount = ((current.Count <= (maximumLength / 2)) ? (current.Count * 2) : maximumLength);
            var next = new List<bool>(capacity: Math.Min(doubledCount, maximumLength));

            foreach (var isLong in current) {
                if (next.Count == maximumLength) { break; }

                if (isLong) {
                    var longCount = ((int)Math.Min(factor, (long)(maximumLength - next.Count)));

                    for (var repeat = 0; (repeat < longCount); ++repeat) { next.Add(item: true); }

                    if (((long)longCount == factor) && (next.Count < maximumLength)) { next.Add(item: false); }
                } else {
                    next.Add(item: true);
                }
            }

            current = next;
        }

        return current.ToArray();
    }
    /// <summary>Copies as much of a letter image as fits into the output, returning the new write cursor.</summary>
    /// <param name="image">The letter image to append.</param>
    /// <param name="tiles">The output word.</param>
    /// <param name="at">The write cursor.</param>
    /// <returns>The advanced write cursor, clamped to the output length.</returns>
    private static int Emit(ReadOnlySpan<bool> image, Span<bool> tiles, int at) {
        var count = Math.Min(image.Length, (tiles.Length - at));

        image.Slice(start: 0, length: count).CopyTo(destination: tiles.Slice(start: at, length: count));

        return (at + count);
    }
}
