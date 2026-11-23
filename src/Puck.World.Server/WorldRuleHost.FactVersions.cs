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
    private const int ValueSource = 8;
    private const int FieldRowSource = 9;
    private const int ProducerSource = 10;

    // The facts versioned by the value they answer: a read that names no binding (a literal seat, a compiled census)
    // and answers from host state alone, so its value moves only where the host's state does. Each is read again
    // whenever that state may have moved (DeriveValues) and its version moves when the answer differs. One entry
    // serves every operand that spells the same fact.
    private readonly List<WorldFactOperand> m_valueFacts = [];
    private readonly List<Fact> m_seenValues = [];
    private readonly List<ulong> m_valueVersions = [];
    // Whether the fact answers from input images, pointer rays or the grant table, which a commit's resynchronisation
    // can move; a census of the bodies moves only with them.
    private readonly List<bool> m_valueReadsInput = [];
    private readonly List<bool> m_valueReadsArena = [];
    private long m_seenValueMoves = -1L;
    private readonly Dictionary<(Type Kind, string Spelling), int> m_valueFactOf = [];
    private readonly List<int> m_fieldRows = [];
    private readonly List<(object? Lattice, ulong Revision)> m_seenFieldRows = [];
    private readonly List<ulong> m_fieldRowVersions = [];
    private readonly List<int> m_factSlotScratch = [];
    private bool m_evaluatingWorldRules;

    private readonly Dictionary<int, HashSet<int>> m_positionTags = [];
    // The regions whose occupancy a schedule names, each with the version last derived: an interaction's region
    // (rebuilt with the interaction family) and the regions a rule reads or sweeps (kept until the rules recompile).
    private readonly List<(string Name, int Ordinal, ulong Version)> m_regions = [];
    private readonly List<(string Name, int Ordinal, ulong Version)> m_ruleRegions = [];
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
            if (Arena.Layout[row].HasTraits) { return false; }
            if (Arena.Catalog.Descriptors[row].HostOwned) {
                if (!TryScheduleHostOwnedRow(facts: facts, rowOrdinal: row)) { return false; }
            } else {
                rows.Add(row);
            }
            if (distance) {
                WatchPoses();
                facts.Add(new FactRead(TagPositionSource, row));
            }
            return true;
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
    private BodyProducerStandDown[] m_seenProducer = [];
    private ulong[] m_producerVersions = [];
    private ulong m_liveVersion;
    private bool m_watchPoses;
    private bool m_watchFacts;
    private bool m_watchProducers;
    private bool m_factMovesAll = true;
    private readonly List<FactRead> m_factMoves = [];

    /// <inheritdoc/>
    /// <remarks>This host never judges a hypothetical position: its ply is the reader's constant default.</remarks>
    public bool SearchPlyIsFixed => true;
    /// <inheritdoc/>
    /// <remarks>An action-state read resolves to the arena row its slot is stored in, which a register-file rebind
    /// moves; a fact versioned by its value is read through the operand a compilation created, which an install
    /// replaces. Either moves the epoch.</remarks>
    public int FactScheduleEpoch {
        get {
            var binding = Host.Population.ActionStateLane.Binding;

            if (binding != m_scheduledBinding) {
                m_scheduledBinding = binding;
                m_scheduleEpoch++;
            }

            return m_scheduleEpoch;
        }
    }

    private int m_scheduledBinding;
    private int m_scheduleEpoch;

    // Forgets the facts versioned by value: their operands belong to the compilation an install just replaced, and a
    // fact of the same spelling may now read something else. The epoch moves, so every schedule that named one is
    // derived again before it is read.
    private void ForgetValueFacts() {
        m_valueFacts.Clear();
        m_seenValues.Clear();
        m_valueVersions.Clear();
        m_valueReadsInput.Clear();
        m_valueReadsArena.Clear();
        m_seenValueMoves = -1L;
        m_valueFactOf.Clear();
        m_fieldRows.Clear();
        m_seenFieldRows.Clear();
        m_fieldRowVersions.Clear();
        m_ruleRegions.Clear();
        m_scheduleEpoch++;
        m_factMovesAll = true;
    }

    // Starts versioning one region's occupancy for a rule that reads or sweeps it, and answers the scope its fact
    // names it by: the placement's ordinal, as an interaction's region is named.
    internal int WatchRegion(string placementId) {
        var ordinal = Host.PlacementOrdinalOf(id: placementId);

        if (!m_ruleRegions.Exists(match: region => (region.Ordinal == ordinal))) {
            m_ruleRegions.Add(item: (placementId, ordinal, Host.Events.OccupancyVersion(region: placementId)));
            m_factMovesAll = true;
        }

        return ordinal;
    }

    /// <inheritdoc/>
    public bool TryScheduleHostOwnedRow(int rowOrdinal, List<FactRead> facts) {
        ArgumentNullException.ThrowIfNull(argument: facts);

        if (!TryHostOwnedField(field: out _, rowOrdinal: rowOrdinal)) {
            return false;
        }

        var scope = m_fieldRows.IndexOf(item: rowOrdinal);

        if (scope < 0) {
            scope = m_fieldRows.Count;
            m_fieldRows.Add(item: rowOrdinal);
            m_seenFieldRows.Add(item: ReadFieldRevision(rowOrdinal: rowOrdinal));
            m_fieldRowVersions.Add(item: 1UL);
            m_factMovesAll = true;
        }
        facts.Add(item: new FactRead(Body: scope, Source: FieldRowSource));
        return true;
    }

    private (object? Lattice, ulong Revision) ReadFieldRevision(int rowOrdinal) => (
        TryHostOwnedField(field: out var name, rowOrdinal: rowOrdinal) &&
        Host.Population.TryFieldForRow(field: out var field, name: name, region: out var region))
        ? ((object?)region!.Lattice, region.Lattice.ValueRevision(field: field))
        : (null, 0UL);

    /// <inheritdoc/>
    /// <remarks>A body's action state is an arena row (its slot's row in the participant or identity lane); its pose
    /// (position, orientation, up) and its facts are versioned per body by <see cref="RefreshFacts"/>. Each is named
    /// for a body reference that is a literal index or the body bound to <c>$each</c>, and a pose also for a placement
    /// reference; any other reference resolves through state and stays volatile, as does every other world fact.</remarks>
    public bool TryScheduleFact(ICompiledFact fact, List<int> rows, List<FactRead> facts) {
        ArgumentNullException.ThrowIfNull(argument: fact);
        ArgumentNullException.ThrowIfNull(argument: rows);
        ArgumentNullException.ThrowIfNull(argument: facts);

        // The work sheet prices a fact that rests by its changes (WorldFactsCompileContext.Rests), which holds only
        // while the fact is one this host versions: a rule reading an unversioned fact is judged every tick.
        return (ScheduleFact(fact: fact, facts: facts, rows: rows) || (WorldFactsCompileContext.Rests(each: out _, fact: fact)
            ? throw new InvalidOperationException(message: $"The work sheet prices '{fact}' as a fact at rest, and the rule host names no version for it.")
            : false));
    }
    private bool ScheduleFact(ICompiledFact fact, List<int> rows, List<FactRead> facts) {
        if (fact is WorldClockOperand clock) {
            WatchClock(facet: clock.Reading);
            facts.Add(item: new FactRead(Body: (int)clock.Reading, Source: ClockSource));

            return true;
        }
        if (fact is WorldRegionOccupancyOperand occupancy) {
            facts.Add(item: new FactRead(Body: WatchRegion(placementId: occupancy.PlacementId), Source: OccupancySource));

            return true;
        }
        if (fact is WorldPhysicsQuiescentOperand or WorldPopulationOperand) {
            var census = (WorldFactOperand)fact;

            facts.Add(item: new FactRead(Body: WatchValue(fact: census, readsInput: false, spelling: (ReferenceEquals(objA: census, objB: WorldPhysicsQuiescentOperand.Instance) ? string.Empty : null)), Source: ValueSource));

            return true;
        }
        if (fact is not WorldBodyFactOperand { Payload: { } payload } bodyOperand) {
            return false;
        }

        switch (payload) {
            // A seat's channel, its pointer and its place at a board are read for a literal seat: from the image and
            // ray its body integrated, its application set and its body's position.
            case ChannelOperand or PointerOperand or BoardSeatOperand:
                facts.Add(item: new FactRead(Body: WatchValue(fact: bodyOperand, readsInput: true, spelling: bodyOperand.Describe), Source: ValueSource));

                return true;
            // A census of the placements a roster row named when the rule compiled: the bodies that inhabit them.
            case PhysicsQuiescentOperand:
                facts.Add(item: new FactRead(Body: WatchValue(fact: bodyOperand, readsInput: false, spelling: bodyOperand.Describe), Source: ValueSource));

                return true;
            case NearestOperand nearest when (nearest.BodyA.Kind == CompiledBodyRefKind.Literal): {
                var tagRow = OrdinalOf(handle: nearest.StateHandle);

                if (tagRow >= 0) {
                    rows.Add(item: tagRow);
                }
                facts.Add(item: new FactRead(Body: WatchValue(fact: bodyOperand, readsArena: true, readsInput: false, spelling: bodyOperand.Describe), Source: ValueSource));

                return true;
            }
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
            // Why a body's producer stands down: the reasons a hold effect and the engagement fold write.
            case ProducerOperand producer when TryBodyScope(body: producer.BodyA, scope: out var producerScope):
                WatchProducers();
                facts.Add(item: new FactRead(Body: producerScope, Source: ProducerSource));

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
        if (source == ValueSource) {
            if (((uint)body) >= ((uint)m_valueVersions.Count)) {
                return false;
            }
            version = m_valueVersions[body];

            return true;
        }
        if (source == FieldRowSource) {
            if (((uint)body) >= ((uint)m_fieldRowVersions.Count)) {
                return false;
            }
            version = m_fieldRowVersions[body];

            return true;
        }
        if (source == OccupancySource) {
            // The scope is the placement's ordinal; the version is the one last derived, so it is the version the
            // moved feed has reported.
            foreach (var regions in (ReadOnlySpan<List<(string Name, int Ordinal, ulong Version)>>)[m_ruleRegions, m_regions]) {
                foreach (var region in regions) {
                    if (region.Ordinal == body) {
                        version = region.Version;

                        return true;
                    }
                }
            }

            return false;
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
            case ProducerSource when (((uint)body) < ((uint)m_producerVersions.Length)):
                version = m_producerVersions[body];

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
        ValueSource => "value",
        FieldRowSource => "field",
        ProducerSource => "producer",
        _ => $"source {source}",
    });

    // The placement the bound key names, as ResolveBodyRef resolves a placement:$each reference, or -1. A sweep asks
    // once per binding per tick, so the answer is kept per key ordinal beside the name it was resolved for: a key
    // table can hand an ordinal to another name after a rewind, and then the name differs and the placement is
    // resolved again.
    private int EachPlacementOrdinal() {
        var key = BoundEachKey;

        if (!Host.Arena.Keys.TryGetName(key: key, name: out var name)) {
            return -1;
        }

        var placements = Host.Document.Definition.Placements;

        if (!ReferenceEquals(objA: m_keyPlacementsFor, objB: placements)) {
            m_keyPlacementsFor = placements;
            Array.Clear(array: m_keyPlacementNames);
        }
        if (key.Ordinal >= m_keyPlacementNames.Length) {
            var room = Math.Max(val1: (key.Ordinal + 1), val2: (m_keyPlacementNames.Length * 2));

            Array.Resize(array: ref m_keyPlacementNames, newSize: room);
            Array.Resize(array: ref m_keyPlacements, newSize: room);
        }
        if (!ReferenceEquals(objA: m_keyPlacementNames[key.Ordinal], objB: name.Value)) {
            m_keyPlacementNames[key.Ordinal] = name.Value;
            m_keyPlacements[key.Ordinal] = Host.PlacementOrdinalOf(id: name.Value);
        }

        return m_keyPlacements[key.Ordinal];
    }

    private object? m_keyPlacementsFor;
    private string?[] m_keyPlacementNames = [];
    private int[] m_keyPlacements = [];
    /// <inheritdoc/>
    /// <remarks>Marks the derived versions stale; they are derived again before the next version read or drain
    /// (<see cref="DeriveFacts"/>), so several firings with arms in a row cost one comparison, not one each.</remarks>
    public void RefreshFacts() {
        if (m_evaluatingWorldRules) {
            return;
        }
        m_factsStale = true;
        m_inputsStale = true;
    }

    // Whether host state may have moved since the versions were last derived.
    private bool m_factsStale = true;

    // Compares candidate population slots' liveness, and their pose and facts when a schedule names them, with what each
    // slot answered when the versions were last derived, exactly as Read(BodyPositionOperand), Read(BoardCellOfOperand),
    // Read(UprightOperand) and Read(BodyFactOperand) answer them. Parked rigid islands, asleep bodies and inactive slots
    // retain their seen answers until woken, disturbed or changed. Every reader of a version or of the moved feed passes
    // through here first, so a version is current wherever it is read.
    private void DeriveFacts() {
        var inputsStale = m_inputsStale;
        var arenaMoved = ((m_valueFacts.Count != 0) && (Arena.MoveSequence != m_seenValueMoves));

        DeriveInputs();
        if (inputsStale || m_factsStale || arenaMoved) {
            m_seenValueMoves = Arena.MoveSequence;
            DeriveValues(arenaMoved: arenaMoved, bodies: m_factsStale, inputs: inputsStale);
        }
        if (!m_factsStale) {
            return;
        }
        m_factsStale = false;
        DeriveClocks();
        DeriveFields();
        BindInteractionInputs();
        foreach (var watched in (ReadOnlySpan<List<(string Name, int Ordinal, ulong Version)>>)[m_regions, m_ruleRegions]) {
            foreach (ref var region in CollectionsMarshal.AsSpan(list: watched)) {
                var version = Host.Events.OccupancyVersion(region: region.Name);

                if (version != region.Version) {
                    region.Version = version;
                    m_factMoves.Add(item: new FactRead(Body: region.Ordinal, Source: OccupancySource));
                }
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
            Array.Resize(array: ref m_seenProducer, newSize: capacity);
            Array.Resize(array: ref m_producerVersions, newSize: capacity);
            population.ResetFactCandidateTracking();
            SeeEverything();
            m_factMovesAll = true;

            return;
        }

        var placementMoved = (m_watchPlacements && SeeInhabitants());

        population.CollectFactCandidateSlots(fullScan: out var fullScan, into: m_factSlotScratch);
        var slotCount = (fullScan ? capacity : m_factSlotScratch.Count);

        WorldTickWork.Process.Add(amount: 1L, kind: WorldTickWork.FactDerives);
        if (m_watchPoses || m_watchFacts || m_watchProducers) {
            WorldTickWork.Process.Add(amount: slotCount, kind: WorldTickWork.FactSlots);
        }

        for (var cursor = 0; (cursor < slotCount); cursor++) {
            var index = (fullScan ? cursor : m_factSlotScratch[cursor]);

            _ = See(move: new FactRead(Body: index, Source: LiveBodiesSource), now: population.IsActive(index: index), seen: ref m_seenLive[index], version: ref m_liveVersion);
            if (m_watchProducers) {
                _ = See(move: new FactRead(Body: index, Source: ProducerSource), now: population.ProducerStandDown(index: index), seen: ref m_seenProducer[index], version: ref m_producerVersions[index]);
            }
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

    private void DeriveFields() {
        var seen = CollectionsMarshal.AsSpan(list: m_seenFieldRows);
        var versions = CollectionsMarshal.AsSpan(list: m_fieldRowVersions);

        for (var index = 0; (index < seen.Length); index++) {
            _ = See(move: new FactRead(Body: index, Source: FieldRowSource), now: ReadFieldRevision(rowOrdinal: m_fieldRows[index]), seen: ref seen[index], version: ref versions[index]);
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
    // Starts versioning a fact by its value, or finds the entry an operand spelling the same fact already has. A null
    // spelling names the operand instance alone.
    private int WatchValue(WorldFactOperand fact, bool readsInput, string? spelling, bool readsArena = false) {
        var key = (fact.GetType(), spelling ?? string.Empty);

        if (spelling is not null) {
            if (m_valueFactOf.TryGetValue(key: key, value: out var known)) {
                return known;
            }
        } else {
            for (var held = 0; (held < m_valueFacts.Count); held++) {
                if (ReferenceEquals(objA: m_valueFacts[held], objB: fact)) {
                    return held;
                }
            }
        }

        var index = m_valueFacts.Count;

        m_valueFacts.Add(item: fact);
        m_seenValues.Add(item: ReadValue(fact: fact));
        m_valueVersions.Add(item: 1UL);
        m_valueReadsInput.Add(item: readsInput);
        m_valueReadsArena.Add(item: readsArena);
        if (spelling is not null) {
            m_valueFactOf.Add(key: key, value: index);
        }
        m_factMovesAll = true;

        return index;
    }
    // One watched fact's answer now, read as a rule reads it but outside any evaluation's observation: a read the host
    // makes to version a fact is not a read the evaluation in flight made of the seat's input.
    private Fact ReadValue(WorldFactOperand fact) {
        var observing = Host.Input.SuspendObservation();

        try {
            return fact.Read(facet: this, reader: this);
        } finally {
            Host.Input.ResumeObservation(observing: observing);
        }
    }
    // Reads every watched fact again and moves the version of each whose answer differs. Called wherever host state
    // may have moved since the versions were derived: `bodies` when the bodies may have, `inputs` when input or grants
    // may have, and `arenaMoved` when a fact also reads an arena row.
    private void DeriveValues(bool bodies, bool inputs, bool arenaMoved) {
        var seen = CollectionsMarshal.AsSpan(list: m_seenValues);
        var versions = CollectionsMarshal.AsSpan(list: m_valueVersions);

        for (var index = 0; (index < seen.Length); index++) {
            if (bodies || (inputs && m_valueReadsInput[index]) || (arenaMoved && m_valueReadsArena[index])) {
                _ = See(move: new FactRead(Body: index, Source: ValueSource), now: ReadValue(fact: m_valueFacts[index]), seen: ref seen[index], version: ref versions[index]);
            }
        }
    }
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
    private void WatchProducers() {
        if (m_watchProducers) {
            return;
        }
        m_watchProducers = true;
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
            m_seenProducer[index] = population.ProducerStandDown(index: index);
            m_poseVersions[index]++;
            m_factsVersions[index]++;
            m_producerVersions[index]++;
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
