using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    // The host sources a rule schedule can name (FactRead.Source). Each one's versions are derived from the value the
    // rule read answers, never recorded by a writer: RefreshFacts compares what each body answers now with what it
    // answered at the last refresh, so no door that moves a body can forget to move its version.
    private const int BodyPositionSource = 0;
    private const int BodyFactsSource = 1;
    private const int LiveBodiesSource = 2;

    private FixedVector3[] m_seenPosition = [];
    private BodyFacts[] m_seenFacts = [];
    private bool[] m_seenLive = [];
    private ulong[] m_positionVersions = [];
    private ulong[] m_factsVersions = [];
    private ulong m_liveVersion;
    private bool m_watchPositions;
    private bool m_watchFacts;
    private bool m_factMovesAll = true;
    private readonly List<FactRead> m_factMoves = [];

    /// <inheritdoc/>
    /// <remarks>This host never judges a hypothetical position: its ply is the reader's constant default.</remarks>
    public bool SearchPlyIsFixed => true;
    /// <inheritdoc/>
    /// <remarks>An action-state read resolves to the arena row its slot is stored in, which a register-file rebind
    /// moves.</remarks>
    public int FactScheduleEpoch => Host.Population.ActionStateLane.Binding;

    /// <inheritdoc/>
    /// <remarks>A body's action state is an arena row (its slot's row in the participant or identity lane); its
    /// position and its facts are versioned per body by <see cref="RefreshFacts"/>. Each is named only for a body
    /// reference that is a literal index or the body bound to <c>$each</c>; any other reference resolves through
    /// state or the host and stays volatile, as does every other world fact.</remarks>
    public bool TryScheduleFact(ICompiledFact fact, List<int> rows, List<FactRead> facts) {
        ArgumentNullException.ThrowIfNull(argument: fact);
        ArgumentNullException.ThrowIfNull(argument: rows);
        ArgumentNullException.ThrowIfNull(argument: facts);

        if (fact is not WorldBodyFactOperand { Payload: { } payload }) {
            return false;
        }

        switch (payload) {
            case BodyStateOperand state when TryBodyScope(body: state.Body, scope: out _):
                var row = Host.Population.ActionStateLane.RowOrdinal(slot: state.Slot);

                // A slot the bound catalog does not store reads zero whatever moves.
                if (row >= 0) {
                    rows.Add(item: row);
                }

                return true;
            case BodyPositionOperand position when TryBodyScope(body: position.Body, scope: out var positionScope):
                WatchPositions();
                facts.Add(item: new FactRead(Body: positionScope, Source: BodyPositionSource));

                return true;
            case BodyFactOperand bodyFact when TryBodyScope(body: bodyFact.Body, scope: out var factScope):
                WatchFacts();
                facts.Add(item: new FactRead(Body: factScope, Source: BodyFactsSource));

                return true;
            default:
                return false;
        }
    }
    /// <inheritdoc/>
    public bool TryScheduleLiveBodies(List<FactRead> facts) {
        ArgumentNullException.ThrowIfNull(argument: facts);

        facts.Add(item: new FactRead(Body: FactRead.Whole, Source: LiveBodiesSource));

        return true;
    }
    /// <inheritdoc/>
    public bool TryFactVersion(int source, int body, out ulong version) {
        version = 0UL;
        switch (source) {
            case BodyPositionSource when (((uint)body) < ((uint)m_positionVersions.Length)):
                version = m_positionVersions[body];

                return true;
            case BodyFactsSource when (((uint)body) < ((uint)m_factsVersions.Length)):
                version = m_factsVersions[body];

                return true;
            case LiveBodiesSource when (body == FactRead.Whole):
                version = m_liveVersion;

                return true;
            default:
                return false;
        }
    }
    /// <inheritdoc/>
    public string DescribeFactSource(int source) => (source switch {
        BodyPositionSource => "bodyPosition",
        BodyFactsSource => "fact",
        LiveBodiesSource => "bodies",
        _ => $"source {source}",
    });
    /// <inheritdoc/>
    /// <remarks>Compares every population slot's liveness, and its position and facts when a schedule names them,
    /// with what the slot answered at the last refresh, exactly as <see cref="Read(BodyPositionOperand)"/> and
    /// <see cref="Read(BodyFactOperand)"/> answer them.</remarks>
    public void RefreshFacts() {
        var population = Host.Population;
        var capacity = population.Capacity;

        if (m_seenLive.Length != capacity) {
            Array.Resize(array: ref m_seenLive, newSize: capacity);
            Array.Resize(array: ref m_seenPosition, newSize: capacity);
            Array.Resize(array: ref m_seenFacts, newSize: capacity);
            Array.Resize(array: ref m_positionVersions, newSize: capacity);
            Array.Resize(array: ref m_factsVersions, newSize: capacity);
            SeeEverything();
            m_factMovesAll = true;

            return;
        }

        for (var index = 0; (index < capacity); index++) {
            var live = population.IsActive(index: index);

            if (live != m_seenLive[index]) {
                m_seenLive[index] = live;
                m_liveVersion++;
                m_factMoves.Add(item: new FactRead(Body: index, Source: LiveBodiesSource));
            }
            if (!m_watchPositions && !m_watchFacts) {
                continue;
            }

            var body = Host.Body(index: index);

            if (m_watchPositions) {
                var position = ((body is not null) ? body.FixedPosition : FixedVector3.Zero);

                if (position != m_seenPosition[index]) {
                    m_seenPosition[index] = position;
                    m_positionVersions[index]++;
                    m_factMoves.Add(item: new FactRead(Body: index, Source: BodyPositionSource));
                }
            }
            if (m_watchFacts) {
                var facts = ((body is not null) ? body.Facts : BodyFacts.None);

                if (facts != m_seenFacts[index]) {
                    m_seenFacts[index] = facts;
                    m_factsVersions[index]++;
                    m_factMoves.Add(item: new FactRead(Body: index, Source: BodyFactsSource));
                }
            }
        }
    }
    /// <inheritdoc/>
    public bool DrainFactMoves(List<FactRead> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        var all = m_factMovesAll;

        into.AddRange(collection: m_factMoves);
        m_factMoves.Clear();
        m_factMovesAll = false;

        return all;
    }

    // A source starts being watched the moment a schedule first names it: what every slot answers now becomes the
    // version-0 baseline, so a change after this point is a refresh's to see, and no capture can predate the baseline.
    private void WatchPositions() {
        if (m_watchPositions) {
            return;
        }
        m_watchPositions = true;
        SeeEverything();
    }
    private void WatchFacts() {
        if (m_watchFacts) {
            return;
        }
        m_watchFacts = true;
        SeeEverything();
    }
    // Takes what every slot answers now as seen, moving every version so nothing captured before can match.
    private void SeeEverything() {
        var population = Host.Population;

        for (var index = 0; (index < m_seenLive.Length) && (index < population.Capacity); index++) {
            var body = Host.Body(index: index);

            m_seenLive[index] = population.IsActive(index: index);
            m_seenPosition[index] = ((body is not null) ? body.FixedPosition : FixedVector3.Zero);
            m_seenFacts[index] = ((body is not null) ? body.Facts : BodyFacts.None);
            m_positionVersions[index]++;
            m_factsVersions[index]++;
        }
        m_liveVersion++;
        m_factMovesAll = true;
    }
    // The body a reference names for every evaluation, when a version can follow it: a literal index, or the body bound
    // to $each. A reference that resolves through a cell, a placement or an extreme reads state the host resolves.
    private static bool TryBodyScope(in CompiledBodyRef body, out int scope) {
        switch (body.Kind) {
            case CompiledBodyRefKind.Literal when (body.Index >= 0):
                scope = body.Index;

                return true;
            case CompiledBodyRefKind.Binding when (((BoundKey)body.Index) == BoundKey.Each):
                scope = FactRead.EachBody;

                return true;
            default:
                scope = 0;

                return false;
        }
    }
}
