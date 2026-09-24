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
    /// <summary>One site's numeric samples, positioned once at the site's cursor and then drawn in order: the first
    /// <see cref="TryNext"/> answers exactly what <see cref="TryFire"/> would at the cursor, and the k-th what it would at
    /// the cursor plus k. Opening seeks once; each sample after that is one generator step, or one keyed sample of a
    /// secret site, so a pass of n samples costs one seek plus n constant steps rather than n seeks.</summary>
    /// <remarks>An extended source draws in place from the generator its compiled source caches, so no other draw of
    /// the same source may run between opening a stream and its last sample. A mutable value: hold it in one local and
    /// draw through that local.</remarks>
    public struct DrawStream {
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
        private long m_samples;

        /// <summary>Gets how many samples the stream has drawn.</summary>
        public readonly long Samples => m_samples;
        /// <summary>Gets the site's cursor after the samples drawn: the value a caller stores back into the site.</summary>
        public readonly long Cursor => (m_start + m_samples);
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
        /// <param name="cursor">The site's current sample count.</param>
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
            stream.m_base.Advance(count: unchecked((((ulong)(skip + cursor)) * AdvancesPerSample(source: generator.Source))));

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
            if (m_secret is { } key) {
                value = PrivateDraw.Sample(
                    cursor: Cursor,
                    secret: key,
                    seed: m_seedState,
                    stream: m_stream
                );
                reason = string.Empty;
                m_samples++;

                return true;
            }

            var drawn = ((m_extended is { } extended)
                ? TryDrawNumeric(
                    generator: m_generator,
                    mask: ref m_mask,
                    reason: out reason,
                    rng: ref extended.ExtendedGenerator,
                    targetKind: m_targetKind,
                    value: out value
                )
                : TryDrawNumeric(
                    generator: m_generator,
                    mask: ref m_mask,
                    reason: out reason,
                    rng: ref m_base,
                    targetKind: m_targetKind,
                    value: out value
                ));

            if (drawn) {
                m_samples++;
            }

            return drawn;
        }
    }

    // The admission every positioned draw shares: a non-negative cursor and seek, a defined mode, a source that may
    // write the site's kind, and a secret only on an integer streamDraw site drawing with replacement.
    private static bool TryCheckFire(StateGenerator generator, CellKind targetKind, long cursor, long skip, ClosedBitset256? secret, out string reason) {
        if (cursor < 0) {
            reason = $"cursor {cursor} is negative — a draw cursor is a non-negative sample count";

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
