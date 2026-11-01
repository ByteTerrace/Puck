using Puck.Maths;

namespace Puck.State;

/// <summary>A draw site's seed-ladder fold and stream id: everything a draw of the site seeks from besides its cursor.
/// Both depend only on the document seed, the running instance's identity and the site descriptor, so a host computes
/// the pair once per site and every draw it makes afterwards costs the same whatever those strings hold.</summary>
/// <param name="State">The <c>Pcg32XshRr</c> starting state (see <see cref="GeneratorEngine.ComputeSeedState"/>).</param>
/// <param name="Stream">The <c>Pcg32XshRr</c> stream id (see <see cref="GeneratorEngine.ComputeStreamId"/>).</param>
public readonly record struct DrawSeed(ulong State, ulong Stream);
/// <summary>The seed of every draw site one host draws for, folded when the host is built: the instance identity
/// once, then each site descriptor once. A draw then reads its seed by catalog ordinal, in constant work.</summary>
public sealed class ArenaDrawSeeds {
    private readonly DrawSeed[] m_seeds;

    /// <summary>Initializes the table over one descriptor per catalog ordinal.</summary>
    /// <param name="documentSeed">The document's own reroll lever.</param>
    /// <param name="instanceIdentity">The running instance's identity.</param>
    /// <param name="sites">The site descriptor per catalog ordinal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instanceIdentity"/> or <paramref name="sites"/> is
    /// <see langword="null"/>.</exception>
    public ArenaDrawSeeds(ulong documentSeed, string instanceIdentity, IReadOnlyList<string> sites) {
        ArgumentNullException.ThrowIfNull(argument: sites);

        var instance = GeneratorEngine.FoldInstance(
            documentSeed: documentSeed,
            instanceIdentity: instanceIdentity
        );

        m_seeds = new DrawSeed[sites.Count];

        for (var ordinal = 0; (ordinal < m_seeds.Length); ordinal++) {
            m_seeds[ordinal] = new DrawSeed(
                State: GeneratorEngine.ComputeSiteSeed(
                    instance: instance,
                    site: sites[ordinal]
                ),
                Stream: GeneratorEngine.ComputeStreamId(site: sites[ordinal])
            );
        }
    }

    /// <summary>Gets how many sites the table seeds.</summary>
    public int Count => m_seeds.Length;

    /// <summary>Gets the seed of the site at a catalog ordinal.</summary>
    /// <param name="rowOrdinal">The site row's catalog ordinal.</param>
    /// <returns>The site's seed.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="rowOrdinal"/> names no site of the table.</exception>
    public DrawSeed this[int rowOrdinal] => m_seeds[rowOrdinal];
}
public static partial class GeneratorEngine {
    /// <summary>One site's numeric results, positioned once at its raw cursor. Every successful result advances
    /// by the raw words it actually consumed; reopening at that cursor continues exactly, including after rejection.
    /// A refused result restores the stream's position and masks.</summary>
    /// <remarks>An extended source draws in place from the generator its compiled source caches, so no other draw of
    /// the same source may run between opening a stream and its last sample. A mutable value: hold it in one local and
    /// draw through that local.</remarks>
    public struct DrawStream : IRawWordSource {
        private StateGenerator m_generator;
        private CellKind m_targetKind;
        private Pcg32XshRr m_base;
        private CompiledSource? m_extended;
        private ClosedBitset256? m_secret;
        private ulong m_seedState;
        private ulong m_stream;
        private long m_start;
        private ClosedBitset256 m_mask;
        private bool m_exhausts;
        private long m_skip;
        private long m_advances;

        /// <summary>Gets how many raw words the stream has consumed, including rejections.</summary>
        public readonly long Advances => m_advances;
        /// <summary>Gets the site's cursor after the samples drawn: the value a caller stores back into the site.</summary>
        public readonly long Cursor => (m_start + m_advances);
        /// <summary>Gets the drawn masks the site persists after the samples drawn, or <see langword="null"/> when its
        /// source never exhausts and the site's stored masks stand.</summary>
        public readonly IReadOnlyList<ClosedBitset256>? MasksAfter => (m_exhausts
            ? [m_mask]
            : null
        );

        /// <summary>Opens a stream at a site's cursor.</summary>
        /// <param name="generator">The site's resolved numeric source; a text source is refused.</param>
        /// <param name="targetKind">The site's declared cell kind; a mismatch refuses by name before anything is drawn.</param>
        /// <param name="seed">The site's seed.</param>
        /// <param name="cursor">The site's current raw-word position.</param>
        /// <param name="masks">The site's current drawn masks (may be empty).</param>
        /// <param name="stream">The positioned stream, on success.</param>
        /// <param name="reason">Why the stream was refused, or empty on success.</param>
        /// <param name="secret">The site's authority-provisioned secret; admitted only for an integer streamDraw source
        /// with replacement.</param>
        /// <param name="skip">The site's authored seek; never applied to a secret draw.</param>
        /// <returns><see langword="true"/> when the stream is positioned.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="generator"/> is <see langword="null"/>.</exception>
        public static bool TryOpen(StateGenerator generator, CellKind targetKind, DrawSeed seed, long cursor, IReadOnlyList<ClosedBitset256>? masks, out DrawStream stream, out string reason, ClosedBitset256? secret = null, long skip = 0L) {
            ArgumentNullException.ThrowIfNull(argument: generator);

            stream = default;

            if (!TryCheckFire(
                cursor: cursor,
                generator: generator,
                reason: out reason,
                secret: secret,
                skip: skip,
                targetKind: targetKind
            )) {
                return false;
            }
            if (WritesText(source: generator.Source)) {
                reason = $"source={StateSpelling.GeneratorSource(source: generator.Source)} writes text, which a stream of samples cannot draw";

                return false;
            }

            var exhausts = (StateGenerator.Exhausts(source: generator.Source) && (generator.Mode != GeneratorMode.WithReplacement));

            stream = new DrawStream {
                m_exhausts = exhausts,
                m_generator = generator,
                m_mask = ((exhausts && (masks is { Count: > 0 }))
                    ? masks[0]
                    : default),
                m_secret = secret,
                m_seedState = seed.State,
                m_start = cursor,
                m_skip = skip,
                m_stream = seed.Stream,
                m_targetKind = targetKind,
            };

            if (secret is not null) {
                return true;
            }
            if (generator.Extended is not null) {
                var compiled = Compiled(generator: generator);

                if (!TryPositionExtended(
                    compiled: compiled,
                    cursor: cursor,
                    generator: generator,
                    reason: out reason,
                    seedState: seed.State,
                    skip: skip,
                    stream: seed.Stream
                )) {
                    stream = default;

                    return false;
                }

                stream.m_extended = compiled;

                return true;
            }

            stream.m_base = Pcg32XshRr.Create(
                state: seed.State,
                stream: seed.Stream
            );
            stream.m_base.Advance(count: (((ulong)skip) + ((ulong)cursor)));

            return true;
        }
        /// <summary>Draws the stream's next sample.</summary>
        /// <param name="value">The raw sample, on success.</param>
        /// <param name="reason">Why the sample was refused (an exhausted source), or empty on success.</param>
        /// <returns><see langword="true"/> when a sample was drawn.</returns>
        /// <exception cref="InvalidOperationException">The stream was never opened.</exception>
        public bool TryNext(out long value, out string reason) {
            if (m_generator is null) {
                throw new InvalidOperationException(message: "The draw stream was never opened.");
            }
            if (Cursor == long.MaxValue) { value = 0; reason = "draw cursor would overflow"; return false; }
            if (m_secret is { } key) {
                value = PrivateDraw.Sample(
                    cursor: Cursor,
                    secret: key,
                    seed: m_seedState,
                    stream: m_stream
                );
                reason = string.Empty;
                m_advances++;

                return true;
            }

            var before = this;
            var mask = m_mask;
            long advances;
            var drawn = ((m_extended is { } extended)
                ? TryDrawNumeric(m_generator, m_targetKind, ref extended.ExtendedGenerator, ref mask, out value, out advances, out reason)
                : TryDrawNumeric(advances: out advances, generator: m_generator, mask: ref mask, reason: out reason, rng: ref m_base, targetKind: m_targetKind, value: out value));

            if (drawn && (advances <= (long.MaxValue - Cursor))) {
                m_advances += advances;
                m_mask = mask;
                return true;
            }
            // A refused sample owns no continuation. Restore both the raw source and its masks before retrying.
            Restore(before: before);
            if (drawn) { reason = "draw cursor would overflow"; }
            value = 0;
            return false;
        }
        /// <summary>Draws a bounded index from a raw stream, counting rejected public or private words.</summary>
        /// <param name="exclusiveHigh">The nonzero exclusive upper bound.</param>
        /// <param name="value">The accepted index, on success.</param>
        /// <param name="sample">The last raw word consumed, on success.</param>
        /// <param name="reason">Why the source or its finite work budget refused.</param>
        /// <returns>Whether an index was drawn. Failure leaves the stream at its previous cursor.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The bound is zero.</exception>
        /// <exception cref="InvalidOperationException">The stream is unopened or is not a raw source.</exception>
        public bool TryNextBounded(uint exclusiveHigh, out uint value, out long sample, out string reason) {
            ArgumentOutOfRangeException.ThrowIfZero(exclusiveHigh);
            if (m_generator?.Source != GeneratorSource.StreamDraw) { throw new InvalidOperationException(message: "Bounded indices require an open raw stream."); }
            var before = this;

            if (BoundedRandomSampling.TrySampleRange(
                maxAttempts: GeneratorCapacity.MaxBoundedAttempts,
                maximum: (exclusiveHigh - 1U),
                minimum: 0U,
                reason: out reason,
                source: ref this,
                value: out value,
                word: out var word,
                words: out _
            )) {
                sample = word;
                return true;
            }
            Restore(before: before);
            value = 0;
            sample = 0;
            return false;
        }
        bool IRawWordSource.TryNextWord(out uint word, out string reason) {
            var drawn = TryNext(reason: out reason, value: out var raw);

            word = ((uint)raw);
            return drawn;
        }

        private void Restore(DrawStream before) {
            this = before;
            if (m_extended is { } cache) {
                _ = TryPositionExtended(cache, m_generator, m_seedState, m_stream, m_skip, Cursor, out _);
            }
        }
    }

    // The admission every positioned draw shares: a non-negative cursor and seek, a defined mode, a source that may
    // write the site's kind, and a secret only on an integer streamDraw site drawing with replacement.
    private static bool TryCheckFire(StateGenerator generator, CellKind targetKind, long cursor, long skip, ClosedBitset256? secret, out string reason) {
        if (cursor < 0) {
            reason = $"cursor {cursor} is negative — a draw cursor is a non-negative raw-word count";

            return false;
        }
        if (skip < 0) {
            reason = $"skip {skip} is negative — an authored seek is a non-negative offset";

            return false;
        }
        if (!IsDefinedMode(mode: generator.Mode)) {
            reason = $"mode '{generator.Mode}' is not a defined GeneratorMode";

            return false;
        }
        if (!TryCheckTargetKind(
            source: generator.Source,
            targetKind: targetKind,
            reason: out reason
        )) {
            return false;
        }
        if (
            (secret is { } key) &&
            (key.IsEmpty ||
            (generator.Source != GeneratorSource.StreamDraw) ||
            (targetKind != CellKind.Int) ||
            (generator.Mode != GeneratorMode.WithReplacement))
        ) {
            reason = "secret draws require a nonzero key and an integer streamDraw source with replacement";

            return false;
        }

        reason = string.Empty;

        return true;
    }
}
