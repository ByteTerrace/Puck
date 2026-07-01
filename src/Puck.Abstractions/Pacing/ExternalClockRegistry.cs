namespace Puck.Abstractions.Pacing;

/// <summary>
/// The registry behind genlock's external-clock seam: every rhythm producer — a camera pane, a capture card, a network
/// feed — registers a NAMED source and publishes its arrivals into its own per-source channel (each source free-runs at
/// its own rate), and the HOST's election policy decides which single source, if any, is forwarded to the pacer-facing
/// <see cref="PacerClock"/>. Producers never decide; the pacer never sees more than one rhythm (a deadline grid can
/// phase-align to exactly one clock — plurality is a selection problem, not a controller problem).
/// <para>Election policy (the host's genlock configuration): <c>"off"</c> = never forward; a source id =
/// forward exactly that source once it registers; absent (<see langword="null"/>) = AUTO — forward when exactly one
/// source is registered, and refuse (silently un-elect) the moment a second appears, so plural rhythms never race for
/// the pacer by accident. Registration is idempotent (a re-opened device keeps its channel), and network sources fit
/// the same contract: whatever ingests the feed stamps <em>local receipt time</em> (<see cref="System.Diagnostics.Stopwatch"/>)
/// exactly as a local grabber thread does.</para>
/// </summary>
public sealed class ExternalClockRegistry {
    /// <summary>The genlock-policy token that disables forwarding entirely.</summary>
    public const string PolicyOff = "off";

    private readonly Lock m_gate = new();
    private readonly string? m_policy;
    private readonly Dictionary<string, ExternalClockSource> m_sources = new(comparer: StringComparer.OrdinalIgnoreCase);
    private volatile ExternalClockSource? m_elected;
    private int m_electionGeneration;
    private volatile bool m_isContended;

    /// <summary>Initializes a new instance of the <see cref="ExternalClockRegistry"/> class.</summary>
    /// <param name="electionPolicy"><see cref="PolicyOff"/>, a source id to elect, or <see langword="null"/> for AUTO
    /// (elect only while exactly one source is registered).</param>
    public ExternalClockRegistry(string? electionPolicy = null) {
        m_policy = (string.IsNullOrWhiteSpace(value: electionPolicy) ? null : electionPolicy.Trim());
    }

    /// <summary>Advances every time the election is re-evaluated (a new source registered), so a host can observe
    /// election changes — pair with <see cref="IsContended"/> and <see cref="SourceIds"/> — without polling strings.</summary>
    public int ElectionGeneration => Volatile.Read(location: ref m_electionGeneration);

    /// <summary>Whether plural sources are registered with no election to break the tie (the AUTO policy never picks
    /// an arbitrary winner), so nothing forwards to <see cref="PacerClock"/> until the host names a source.</summary>
    public bool IsContended => m_isContended;

    /// <summary>The single channel the render pacer reads — fed only by the elected source.</summary>
    public ExternalPresentClock PacerClock { get; } = new();

    /// <summary>The currently registered source ids (diagnostics).</summary>
    public IReadOnlyList<string> SourceIds {
        get {
            lock (m_gate) {
                return [.. m_sources.Keys];
            }
        }
    }

    /// <summary>Registers (or re-fetches) a named rhythm source; idempotent, so a re-opened device keeps publishing
    /// into the same channel. Election is re-evaluated against the policy on every new registration.</summary>
    /// <param name="sourceId">The stable source identity (e.g. <c>"camera:0"</c>, <c>"capture:desktop"</c>, <c>"net:metronome"</c>).</param>
    /// <returns>The source's publish channel.</returns>
    /// <exception cref="ArgumentException"><paramref name="sourceId"/> is empty.</exception>
    public ExternalClockSource RegisterSource(string sourceId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourceId);

        lock (m_gate) {
            if (!m_sources.TryGetValue(key: sourceId, value: out var source)) {
                source = new ExternalClockSource(registry: this, sourceId: sourceId);
                m_sources[sourceId] = source;

                ReevaluateElection();
            }

            return source;
        }
    }

    // Whether this source's publishes are forwarded to the pacer; read lock-free on every publish.
    internal bool IsElected(ExternalClockSource source) =>
        ReferenceEquals(m_elected, source);

    // Applies the policy over the current registrations. Named: elect the named source when present. Auto: elect the
    // sole source, and un-elect when plurality appears (no arbitrary winner, ever). Off: never elect. The structural
    // outcome (ElectionGeneration + IsContended) is published so a host can observe — and announce — election changes;
    // the registry itself never logs.
    private void ReevaluateElection() {
        ++m_electionGeneration;

        if (string.Equals(m_policy, PolicyOff, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            m_elected = null;
            m_isContended = false;

            return;
        }

        if (m_policy is not null) {
            m_elected = (m_sources.TryGetValue(key: m_policy, value: out var named) ? named : null);
            m_isContended = false;

            return;
        }

        m_elected = ((1 == m_sources.Count) ? m_sources.Values.First() : null);
        m_isContended = (m_sources.Count > 1);
    }
}

/// <summary>
/// One registered rhythm source: its own conflating channel (so every source's latest arrival and rate stay
/// independent — sources at different rates never pollute each other), plus the forwarding hook that feeds the
/// registry's pacer channel while — and only while — this source is elected.
/// </summary>
public sealed class ExternalClockSource {
    // This source's own latest-arrival channel; every publish lands here regardless of election.
    private readonly ExternalPresentClock m_channel = new();
    private readonly ExternalClockRegistry m_registry;

    internal ExternalClockSource(ExternalClockRegistry registry, string sourceId) {
        m_registry = registry;
        SourceId = sourceId;
    }

    /// <summary>The stable source identity this channel registered under.</summary>
    public string SourceId { get; }

    /// <summary>Publishes a frame arrival (from the producer's own thread at its own cadence); forwarded to the pacer
    /// only while this source is elected.</summary>
    /// <param name="arrivalTimestamp">The arrival time in <see cref="System.Diagnostics.Stopwatch"/> ticks (local receipt time for a network source).</param>
    /// <param name="frameVersion">The producer's monotonically increasing frame counter at this arrival.</param>
    public void Publish(long arrivalTimestamp, long frameVersion) {
        m_channel.Publish(arrivalTimestamp: arrivalTimestamp, frameVersion: frameVersion);

        if (m_registry.IsElected(source: this)) {
            m_registry.PacerClock.Publish(arrivalTimestamp: arrivalTimestamp, frameVersion: frameVersion);
        }
    }
}
