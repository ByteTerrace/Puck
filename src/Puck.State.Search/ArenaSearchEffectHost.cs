using Puck.State.Rules;

namespace Puck.State;

/// <summary>The effect host a candidate's judge fires through: an arena host whose registered arms are queued and
/// dropped instead of fired, because every candidate scope a search opens is rewound.</summary>
/// <remarks>
/// <para>An arm that declares <see cref="EffectNeeds.Irreversible"/> is queued by the firing and handed to
/// <see cref="Fire"/> after the firing's own scope commits. That scope is nested inside the candidate's, which the
/// walk rewinds, so an arm that acted here would act for a position the search only imagined. This host therefore
/// counts the arm and does nothing outward, and <see cref="DiscardQueuedArms"/> drops the count when the candidate
/// is left.</para>
/// <para>An arm that reaches its host through a facet instead of <see cref="IEffectHost.Fire"/> never gets that
/// far: this host advertises no facet, so a rule needing one is refused by name when the job set is
/// installed.</para>
/// </remarks>
public class ArenaSearchEffectHost : ArenaEffectHost, IStateReader {
    private int m_queued;
    private PrivateDrawSample? m_imagined;
    private ulong m_imaginedSeed;

    /// <summary>Gets or sets the hypothetical ply supplied by the search judge.</summary>
    public int SearchPly { get; set; }

    /// <summary>Gets the sampler every private draw site a candidate samples draws from, keyed by the current
    /// imagination seed (<see cref="Imagine"/>): words no authority issued, so a candidate never reads the
    /// authority's stream, its read count or its timeline.</summary>
    protected override PrivateDrawSample? ImaginedSampler => (m_imagined ??= Key(seed: m_imaginedSeed));

    /// <summary>Keys every private draw site a candidate samples to <paramref name="seed"/>, the seed the job imagines
    /// with at its current inputs.</summary>
    /// <param name="seed">The imagination seed.</param>
    public void Imagine(ulong seed) {
        if ((m_imagined is not null) && (seed == m_imaginedSeed)) {
            return;
        }

        m_imaginedSeed = seed;
        m_imagined = null;
    }
    private static PrivateDrawSample Key(ulong seed) {
        var generator = Puck.Maths.Pcg32XshRr.Create(state: seed, stream: 0UL);

        return PrivateDrawStream.Imagined(key: new ClosedBitset256(
            Word0: ((((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32()),
            Word1: ((((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32()),
            Word2: ((((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32()),
            Word3: ((((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32())
        ));
    }

    /// <summary>Initializes a host over the arena a search opens its candidate scopes on.</summary>
    /// <param name="arena">The store every read and write addresses.</param>
    /// <param name="generators">The section's declared draw sources.</param>
    /// <param name="ticksPerSecond">The simulation rate a dynamics follower is stepped at.</param>
    /// <param name="dynamics">The declared dynamics rows a <see cref="StateDynamics"/> trait resolves against.</param>
    /// <param name="documentSeed">The document's own reroll lever, folded into every draw site's seed.</param>
    /// <param name="instanceIdentity">The running instance's identity, folded into every draw site's seed.</param>
    /// <param name="sites">The site descriptor per catalog ordinal, the same table the live host draws with, so a
    /// candidate reproduces public draws while it draws a private site from its own imagined sampler; <see langword="null"/>
    /// names each site by its catalog row name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sites"/> does not hold exactly one descriptor per catalog
    /// ordinal of <paramref name="arena"/>.</exception>
    public ArenaSearchEffectHost(
        StateArena arena, IReadOnlyList<GeneratorRow>? generators = null, int ticksPerSecond = 0, IReadOnlyList<DynamicsRow>? dynamics = null, ulong documentSeed = 0UL, string instanceIdentity = "", IReadOnlyList<string>? sites = null
    ) : base(
        arena: arena,
        documentSeed: documentSeed,
        dynamics: dynamics,
        generators: generators,
        instanceIdentity: instanceIdentity,
        sites: sites,
        ticksPerSecond: ticksPerSecond
    ) { }

    /// <summary>Gets how many arms this host has dropped without firing.</summary>
    public long DiscardedArms { get; private set; }
    /// <summary>Gets how many arms are queued for the candidate being judged.</summary>
    public int QueuedArms => m_queued;

    /// <summary>Drops every arm queued for the candidate being judged.</summary>
    public void DiscardQueuedArms() {
        DiscardedArms += m_queued;
        m_queued = 0;
    }
    /// <inheritdoc/>
    public override bool Fire(ICompiledFact effect, in EffectFiring firing, out EffectRefusal refusal) {
        refusal = EffectRefusal.None;

        // A preflight call asks whether the arm would be admitted against the state the firing proposes; it fires
        // nothing either way, so only the real call counts as an arm this host is holding back.
        if (!firing.Preflight) {
            m_queued++;
        }

        return true;
    }
}
