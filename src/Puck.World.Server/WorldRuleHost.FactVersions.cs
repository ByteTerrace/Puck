using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    // The host sources a rule schedule can name (FactRead.Source). Each one's versions are derived from the value the
    // rule read answers, never recorded by a writer: RefreshFacts compares what each body answers now with what it
    // answered at the last refresh, so no door that moves a body can forget to move its version.
    private const int BodyPoseSource = 0;
    private const int BodyFactsSource = 1;
    private const int LiveBodiesSource = 2;
    private const int PlacementPoseSource = 3;
    private const int InputSource = 4;
    private const int ClockSource = 5;
    private const int TagPositionSource = 6;
    private const int OccupancySource = 7;

    private readonly Dictionary<int, HashSet<int>> m_positionTags = [];
    private readonly List<(string Name, int Ordinal, ulong Version)> m_regions = [];
    private CompiledRule[]? m_watchedInteractions;
    private StateArena? m_interactionArena;

    // Rebuild subscriptions from the installed family. A new compilation or arena can bind the same names to other rows.
    private void BindInteractionInputs() {
        if (ReferenceEquals(m_interactions, m_watchedInteractions) && ReferenceEquals(Arena, m_interactionArena)) { return; }
        m_watchedInteractions = m_interactions;
        m_interactionArena = Arena;
        m_factMovesAll = true;
        m_positionTags.Clear();
        m_regions.Clear();
        foreach (var rule in m_interactions) {
            if (rule is not CompiledWorldFactsRule { Interaction: { } interaction }) { continue; }
            if (interaction.CoOccurrence == WorldInteractionCoOccurrence.Region) {
                var ordinal = Host.PlacementOrdinalOf(id: interaction.Right);
                if (!m_regions.Exists(region => region.Ordinal == ordinal)) {
                    m_regions.Add((interaction.Right, ordinal, Host.Events.OccupancyVersion(interaction.Right)));
                }
            }
        }
    }

    bool IRuleOwner.TryScheduleIteration(CompiledRule rule, List<int> rows, List<FactRead> facts) {
        if (rule is not CompiledWorldFactsRule { Interaction: { } interaction }) { return false; }
        var distance = interaction.CoOccurrence == WorldInteractionCoOccurrence.Distance;
        facts.Add(new FactRead(LiveBodiesSource, FactRead.Whole));
        var tracked = Side(interaction.Left, interaction.LeftPool, interaction.LeftCarrier);
        if (distance) {
            tracked &= Side(interaction.Right, interaction.RightPool, interaction.RightCarrier);
        } else {
            facts.Add(new FactRead(OccupancySource, Host.PlacementOrdinalOf(id: interaction.Right)));
        }
        return tracked;

        bool Side(string name, StatePoolDescriptor? pool, CompiledPoolBodyCarrier? carrier) {
            if (pool is not null && carrier is not null) {
                rows.Add(pool.DomainRowOrdinal);
                rows.Add(pool.GenerationRowOrdinal);
                rows.Add(pool.Fields[carrier.FieldOrdinal].RowOrdinal);
                foreach (var binding in carrier.Bindings) {
                    if (binding.Kind == CompiledBodyRefKind.Placement || (distance && binding.Index >= 0)) {
                        _ = TrySchedulePose(body: binding, facts: facts);
                    }
                }
                return true;
            }
            var row = DocumentRowOrdinal(name: name);
            if (row < 0) { return false; }
            rows.Add(row);
            if (distance) {
                WatchPoses();
                facts.Add(new FactRead(TagPositionSource, row));
            }
            return !Arena.Layout[row].HasTraits && !Arena.Catalog.Descriptors[row].HostOwned;
        }
    }

    // What an input-history read can observe of one owner: which owner object answers, its epoch (a switch, a reset,
    // a calibration), the ring's occupancy and newest identity (an admission, an expiry), its folded image and mapping
    // (a hold's continuation), and its restores (after which identities can repeat). Identities only grow between
    // restores, so a read of an event, its time, value or tick, a hold, or the music clock at an event is a function
    // of these alone.
    private readonly record struct InputSeen(WorldInputOwner? Owner, ulong Restores, ulong Epoch, int Count, ulong Newest, Puck.World.Protocol.PlayerIntent Image, WorldInputMapping? Mapping) {
        public static InputSeen Of(WorldInputOwner? owner) => ((owner is null)
            ? default
            : new InputSeen(Count: owner.Count, Epoch: owner.Epoch, Image: owner.Image, Mapping: owner.Mapping, Newest: ((owner.Count > 0) ? owner.Record(index: (owner.Count - 1)).Id : 0UL), Owner: owner, Restores: owner.Restores));
    }

    private readonly List<Puck.Commands.Principal> m_inputPrincipals = [];
    private readonly List<InputSeen> m_seenInputs = [];
    private readonly List<ulong> m_inputVersions = [];
    private bool m_inputsStale = true;
    private readonly long[] m_seenClocks = new long[4];
    private readonly ulong[] m_clockVersions = new ulong[4];
    private readonly bool[] m_watchClocks = new bool[4];

    // What a pose read answers of one population slot: its position, orientation and up, every input of
    // Read(BodyPositionOperand), Read(BoardCellOfOperand) and Read(UprightOperand); zero, identity and +Y with no body
    // stand for "no live body", which those reads answer by themselves.
    private readonly record struct BodyPose(FixedVector3 Position, FixedQuaternion Orientation, FixedVector3 Up) {
        public static BodyPose Of(WorldBody? body) => ((body is not null)
            ? new BodyPose(Orientation: body.FixedOrientation, Position: body.FixedPosition, Up: body.FixedUp)
            : default);
    }

    // Which bodies inhabit a placement, as of the placement table version it was built for: a move of one of them
    // moves the placement source.
    private bool[] m_inhabitant = [];
    private ulong m_inhabitantsFor = ulong.MaxValue;
    private bool m_watchPlacements;

    private BodyPose[] m_seenPose = [];
    private BodyFacts[] m_seenFacts = [];
    private bool[] m_seenLive = [];
    private ulong[] m_poseVersions = [];
    private ulong[] m_factsVersions = [];
    private ulong m_liveVersion;
    private bool m_watchPoses;
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
    /// <remarks>A body's action state is an arena row (its slot's row in the participant or identity lane); its pose
    /// (position, orientation, up) and its facts are versioned per body by <see cref="RefreshFacts"/>. Each is named
    /// for a body reference that is a literal index or the body bound to <c>$each</c>, and a pose also for a placement
    /// reference; any other reference resolves through state and stays volatile, as does every other world fact.</remarks>
    public bool TryScheduleFact(ICompiledFact fact, List<int> rows, List<FactRead> facts) {
        ArgumentNullException.ThrowIfNull(argument: fact);
        ArgumentNullException.ThrowIfNull(argument: rows);
        ArgumentNullException.ThrowIfNull(argument: facts);

        if (fact is WorldClockOperand clock) {
            WatchClock(facet: clock.Reading);
            facts.Add(item: new FactRead(Body: (int)clock.Reading, Source: ClockSource));

            return true;
        }
        if (fact is not WorldBodyFactOperand { Payload: { } payload }) {
            return false;
        }

        switch (payload) {
            case WorldInputOperand input when TryInputScope(input: input, scope: out var inputScope):
                facts.Add(item: new FactRead(Body: inputScope, Source: InputSource));

                return true;
            case ClockOperand { Input: { } input } when TryInputScope(input: input, scope: out var clockScope):
                facts.Add(item: new FactRead(Body: clockScope, Source: InputSource));

                return true;
            case BodyStateOperand state when TryBodyScope(body: state.Body, scope: out _):
                var row = Host.Population.ActionStateLane.RowOrdinal(slot: state.Slot);

                // A slot the bound catalog does not store reads zero whatever moves.
                if (row >= 0) {
                    rows.Add(item: row);
                }

                return true;
            // A position, the cell under a body (its position over a fixed topology) and its uprightness (its
            // orientation against its up) are each a function of its pose.
            case BodyPositionOperand position:
                return TrySchedulePose(body: position.Body, facts: facts);
            case BoardCellOfOperand cellOf:
                return TrySchedulePose(body: cellOf.BodyA, facts: facts);
            case UprightOperand upright:
                return TrySchedulePose(body: upright.BodyA, facts: facts);
            case BodyFactOperand bodyFact when TryBodyScope(body: bodyFact.Body, scope: out var factScope):
                WatchFacts();
                facts.Add(item: new FactRead(Body: factScope, Source: BodyFactsSource));

                return true;
            default:
                return false;
        }
    }
    // A pose read names its body's pose, or, for a placement reference, the pose of whichever body inhabits the
    // placement: a literal placement's ordinal, or the placement $each names.
    private bool TrySchedulePose(in CompiledBodyRef body, List<FactRead> facts) {
        if (TryBodyScope(body: body, scope: out var scope)) {
            WatchPoses();
            facts.Add(item: new FactRead(Body: scope, Source: BodyPoseSource));

            return true;
        }
        if (body.Kind != CompiledBodyRefKind.Placement) {
            return false;
        }

        WatchPoses();
        WatchPlacements();
        facts.Add(item: new FactRead(Body: ((body.PlacementOrdinals is not null) ? FactRead.EachBody : body.Index), Source: PlacementPoseSource));

        return true;
    }
    /// <inheritdoc/>
    public bool TryScheduleLiveBodies(List<FactRead> facts) {
        ArgumentNullException.ThrowIfNull(argument: facts);

        facts.Add(item: new FactRead(Body: FactRead.Whole, Source: LiveBodiesSource));

        return true;
    }
    /// <inheritdoc/>
    /// <remarks>A body source's <c>$each</c> is the body index the bound key spells; the placement source's is the
    /// placement the bound key names, and its version pairs the placement table's version (which body inhabits the
    /// placement) with that body's pose version.</remarks>
    public bool TryFactVersion(int source, int body, out ulong version) {
        version = 0UL;
        DeriveFacts();
        if (source == InputSource) {
            if (((uint)body) >= ((uint)m_inputVersions.Count)) {
                return false;
            }
            version = m_inputVersions[body];

            return true;
        }
        if (source == ClockSource) {
            if ((((uint)body) >= ((uint)m_watchClocks.Length)) || !m_watchClocks[body]) {
                return false;
            }
            version = m_clockVersions[body];

            return true;
        }
        if (source == PlacementPoseSource) {
            var ordinal = ((body == FactRead.EachBody) ? EachPlacementOrdinal() : body);

            if (ordinal < 0) {
                return false;
            }

            var inhabitant = Host.Population.BodyForPlacementOrdinal(ordinal: ordinal);

            version = ((Host.Population.PlacementTableVersion << 32) + ((((uint)inhabitant) < ((uint)m_poseVersions.Length)) ? m_poseVersions[inhabitant] : 0UL));

            return true;
        }
        if (body == FactRead.EachBody) {
            body = BoundEachIndex();
        }
        switch (source) {
            case BodyPoseSource when (((uint)body) < ((uint)m_poseVersions.Length)):
                version = m_poseVersions[body];

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
        BodyPoseSource => "bodyPose",
        BodyFactsSource => "fact",
        LiveBodiesSource => "bodies",
        PlacementPoseSource => "placementPose",
        InputSource => "input",
        ClockSource => "clock",
        TagPositionSource => "carrierPositions",
        OccupancySource => "regionOccupancy",
        _ => $"source {source}",
    });

    // The placement the bound key names, as ResolveBodyRef resolves a placement:$each reference, or -1.
    private int EachPlacementOrdinal() => (Host.Arena.Keys.TryGetName(key: BoundEachKey, name: out var name)
        ? Host.PlacementOrdinalOf(id: name.Value)
        : -1);
    /// <inheritdoc/>
    /// <remarks>Marks the derived versions stale; they are derived again before the next version read or drain
    /// (<see cref="DeriveFacts"/>), so several firings with arms in a row cost one comparison, not one each.</remarks>
    public void RefreshFacts() {
        m_factsStale = true;
        m_inputsStale = true;
    }

    // Whether host state may have moved since the versions were last derived.
    private bool m_factsStale = true;

    // Compares every population slot's liveness, and its pose and facts when a schedule names them, with what the slot
    // answered when the versions were last derived, exactly as Read(BodyPositionOperand), Read(BoardCellOfOperand),
    // Read(UprightOperand) and Read(BodyFactOperand) answer them. Every reader of a version or of the moved feed
    // passes through here first, so a version is current wherever it is read.
    private void DeriveFacts() {
        DeriveInputs();
        if (!m_factsStale) {
            return;
        }
        m_factsStale = false;
        DeriveClocks();
        BindInteractionInputs();
        var regions = CollectionsMarshal.AsSpan(m_regions);
        foreach (ref var region in regions) {
            var version = Host.Events.OccupancyVersion(region.Name);
            if (version != region.Version) {
                region.Version = version;
                m_factMoves.Add(new FactRead(OccupancySource, region.Ordinal));
            }
        }

        var population = Host.Population;
        var capacity = population.Capacity;

        if (m_seenLive.Length != capacity) {
            Array.Resize(array: ref m_seenLive, newSize: capacity);
            Array.Resize(array: ref m_seenPose, newSize: capacity);
            Array.Resize(array: ref m_seenFacts, newSize: capacity);
            Array.Resize(array: ref m_poseVersions, newSize: capacity);
            Array.Resize(array: ref m_factsVersions, newSize: capacity);
            SeeEverything();
            m_factMovesAll = true;

            return;
        }

        var placementMoved = (m_watchPlacements && SeeInhabitants());

        WorldTickWork.Process.Add(amount: 1L, kind: WorldTickWork.FactDerives);
        if (m_watchPoses || m_watchFacts) {
            WorldTickWork.Process.Add(amount: capacity, kind: WorldTickWork.FactSlots);
        }

        for (var index = 0; (index < capacity); index++) {
            _ = See(move: new FactRead(Body: index, Source: LiveBodiesSource), now: population.IsActive(index: index), seen: ref m_seenLive[index], version: ref m_liveVersion);
            if (!m_watchPoses && !m_watchFacts) {
                continue;
            }

            var body = Host.Body(index: index);

            var pose = m_watchPoses ? BodyPose.Of(body: body) : default;
            if (m_watchPoses && (pose.Position != m_seenPose[index].Position)) {
                foreach (var (row, carriers) in m_positionTags) {
                    if (carriers.Contains(index)) { m_factMoves.Add(new FactRead(TagPositionSource, row)); }
                }
            }
            if (m_watchPoses && See(move: new FactRead(Body: index, Source: BodyPoseSource), now: pose, seen: ref m_seenPose[index], version: ref m_poseVersions[index])) {
                placementMoved |= (m_watchPlacements && (index < m_inhabitant.Length) && m_inhabitant[index]);
            }
            if (m_watchFacts) {
                _ = See(move: new FactRead(Body: index, Source: BodyFactsSource), now: ((body is not null) ? body.Facts : BodyFacts.None), seen: ref m_seenFacts[index], version: ref m_factsVersions[index]);
            }
        }
        if (placementMoved) {
            m_factMoves.Add(item: new FactRead(Body: FactRead.Whole, Source: PlacementPoseSource));
        }
    }
    // An input-history read is versioned when its owner is a literal identity (a seat or a session, never a body whose
    // owner the population and grants resolve) and it reads only what InputSeen covers: an event, its time, value or
    // tick, a hold, or the clock at an event. A pattern's match and an event's age move with time, and stay volatile.
    private bool TryInputScope(WorldInputOperand input, out int scope) {
        scope = -1;
        if ((input.Owner.Body is not null) || (input.Pattern >= 0) || (input.Facet == WorldInputFacet.Age)) {
            return false;
        }

        scope = m_inputPrincipals.IndexOf(item: input.Owner.Principal);
        if (scope < 0) {
            scope = m_inputPrincipals.Count;
            m_inputPrincipals.Add(item: input.Owner.Principal);
            m_seenInputs.Add(item: InputSeen.Of(owner: InputOwner(principal: input.Owner.Principal)));
            m_inputVersions.Add(item: 1UL);
            m_factMovesAll = true;
        }

        return true;
    }
    private WorldInputOwner? InputOwner(Puck.Commands.Principal principal) => (Host.Input.TryGet(input: out var owner, principal: principal) ? owner : null);
    // Compares every watched owner with what it answered when the versions were last derived. An admission, an expiry,
    // a switch or an owner replaced moves its version whichever door made the change.
    private void DeriveInputs() {
        if (!m_inputsStale) {
            return;
        }
        m_inputsStale = false;

        var seen = CollectionsMarshal.AsSpan(list: m_seenInputs);
        var versions = CollectionsMarshal.AsSpan(list: m_inputVersions);

        for (var index = 0; (index < seen.Length); index++) {
            _ = See(move: new FactRead(Body: index, Source: InputSource), now: InputSeen.Of(owner: InputOwner(principal: m_inputPrincipals[index])), seen: ref seen[index], version: ref versions[index]);
        }
    }
    // A clock read names the clock's current position, which moves once a step: its beat, bar and beat in bar only on a
    // beat's edge, its phase error every tick.
    private void WatchClock(ClockFacet facet) {
        var index = (int)facet;

        if (m_watchClocks[index]) {
            return;
        }
        m_watchClocks[index] = true;
        m_seenClocks[index] = Host.ReadClock(facet: facet);
        m_clockVersions[index]++;
        m_factMovesAll = true;
    }
    private void DeriveClocks() {
        for (var index = 0; (index < m_watchClocks.Length); index++) {
            if (m_watchClocks[index]) {
                _ = See(move: new FactRead(Body: index, Source: ClockSource), now: Host.ReadClock(facet: (ClockFacet)index), seen: ref m_seenClocks[index], version: ref m_clockVersions[index]);
            }
        }
    }
    // The one comparison every derived version rests on: what a source answers now against what it answered when the
    // versions were last derived. A difference takes the new answer as seen, moves the version and hands the due list
    // the move.
    private bool See<T>(ref T seen, T now, ref ulong version, FactRead move) {
        if (EqualityComparer<T>.Default.Equals(x: seen, y: now)) {
            return false;
        }
        seen = now;
        version++;
        m_factMoves.Add(item: move);

        return true;
    }
    // Rebuilds which bodies inhabit a placement when the population's table moved, reporting whether it did.
    private bool SeeInhabitants() {
        var population = Host.Population;

        if (population.PlacementTableVersion == m_inhabitantsFor) {
            return false;
        }

        m_inhabitantsFor = population.PlacementTableVersion;
        if (m_inhabitant.Length != population.Capacity) {
            m_inhabitant = new bool[population.Capacity];
        } else {
            Array.Clear(array: m_inhabitant);
        }
        for (var ordinal = 0; (ordinal < Host.Document.Definition.Placements.Count); ordinal++) {
            var body = population.BodyForPlacementOrdinal(ordinal: ordinal);

            if (((uint)body) < ((uint)m_inhabitant.Length)) {
                m_inhabitant[body] = true;
            }
        }

        return true;
    }
    private void WatchPlacements() {
        if (m_watchPlacements) {
            return;
        }
        m_watchPlacements = true;
        _ = SeeInhabitants();
        m_factMovesAll = true;
    }
    /// <inheritdoc/>
    public bool DrainFactMoves(List<FactRead> into) {
        ArgumentNullException.ThrowIfNull(argument: into);
        DeriveFacts();

        var all = m_factMovesAll;

        into.AddRange(collection: m_factMoves);
        m_factMoves.Clear();
        m_factMovesAll = false;

        return all;
    }

    // A source starts being watched the moment a schedule first names it: what every slot answers now becomes the
    // version-0 baseline, so a change after this point is a refresh's to see, and no capture can predate the baseline.
    private void WatchPoses() {
        if (m_watchPoses) {
            return;
        }
        m_watchPoses = true;
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
            m_seenPose[index] = BodyPose.Of(body: body);
            m_seenFacts[index] = ((body is not null) ? body.Facts : BodyFacts.None);
            m_poseVersions[index]++;
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
