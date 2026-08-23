using System.Numerics;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Evaluates <see cref="OverlayPredicate"/>s per local seat over the presentation facts an overlay element
/// may read (<see cref="OverlayFact"/>). Facts come straight from their owners each evaluation — nothing is
/// duplicated here — except the recency clocks, which remember the last tick each fact held. Simulation ticks
/// (<see cref="WorldServer.NextInputTick"/>) are the time base; presentation converts the elapsed integer tick
/// distance to seconds only when resolving a fade. Subject-bearing predicates (<see cref="OverlayPredicate.Speaking"/>,
/// <see cref="OverlayPredicate.Near"/>) resolve their subject to a body through <see cref="WorldPerceptionAnchor"/>
/// so possession follows, and to a position off the client's render poses or the stamp pool.</summary>
internal sealed class WorldOverlayFacts : IOverlayPredicateEvaluator {
    private static readonly int FactCount = Enum.GetValues<OverlayFact>().Length;

    private readonly WorldClient m_client;
    private readonly IConsoleSessions? m_consoles;
    private readonly WorldSeatBindings m_seatBindings;
    private readonly WorldPointer? m_pointer;
    private readonly PlayerRoster m_roster;
    private readonly Func<InputRouter> m_router;
    private readonly WorldServer m_server;
    private readonly Func<WorldWheelFeed?> m_wheel;
    private readonly WorldPerceptionAnchor m_perception;
    private readonly WorldStampPool m_stamps;
    private readonly WorldSpeechClock m_speech;

    private readonly ulong[] m_lastHeldTick = new ulong[(PlayerRoster.MaxSlots * FactCount)];
    private readonly ulong[] m_seenInputTick = new ulong[PlayerRoster.MaxSlots];
    private readonly ulong[] m_inputEdgeTick = new ulong[PlayerRoster.MaxSlots];
    private readonly ulong[] m_seenMotionSequence = new ulong[PlayerRoster.MaxSlots];
    private readonly ulong[] m_motionEdgeTick = new ulong[PlayerRoster.MaxSlots];

    /// <summary>Initializes the evaluator over the fact owners. Presentation-only owners are optional: a headless
    /// composition has no pointer, wheel, or console, and their facts read false there.</summary>
    public WorldOverlayFacts(WorldClient client, PlayerRoster roster, WorldServer server, WorldSeatBindings seatBindings, WorldPerceptionAnchor perception, WorldStampPool stamps, WorldSpeechClock speech, Func<InputRouter> router, Func<WorldWheelFeed?> wheel, WorldPointer? pointer, IConsoleSessions? consoles) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(seatBindings);
        ArgumentNullException.ThrowIfNull(perception);
        ArgumentNullException.ThrowIfNull(stamps);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(wheel);
        m_client = client;
        m_perception = perception;
        m_stamps = stamps;
        m_speech = speech;
        m_roster = roster;
        m_server = server;
        m_seatBindings = seatBindings;
        m_router = router;
        m_wheel = wheel;
        m_pointer = pointer;
        m_consoles = consoles;
    }

    private ulong CompletedTick {
        get {
            var next = m_server.NextInputTick;

            return ((next == 0UL)
                ? 0UL
                : (next - 1UL)
            );
        }
    }

    /// <summary>The body that most recently spoke, or -1 when nothing has.</summary>
    public int RecentSpeakerBody => m_speech.RecentSpeakerBody;
    /// <summary>Gets the speech clock the speaking facts read.</summary>
    public WorldSpeechClock Speech => m_speech;

    /// <summary>Stamps that <paramref name="bodyIndex"/> spoke on the current completed tick.</summary>
    /// <param name="bodyIndex">The 0-based body index.</param>
    public void NoteSpoke(int bodyIndex) => NoteSpoke(
        bodyIndex: bodyIndex,
        tick: CompletedTick
    );
    /// <summary>Stamps that <paramref name="bodyIndex"/> spoke on <paramref name="tick"/>.</summary>
    /// <param name="bodyIndex">The 0-based body index.</param>
    /// <param name="tick">The completed simulation tick.</param>
    public void NoteSpoke(int bodyIndex, ulong tick) => m_speech.NoteSpoke(
        bodyIndex: bodyIndex,
        tick: tick
    );
    // Distance between two subjects off the presentation poses; an unresolvable subject is infinitely far. An AnySeat
    // on either side quantifies over the joined seats (the nearest pair decides).
    private float Distance(int slot, OverlaySubject subject, OverlaySubject? of) {
        var nearest = float.PositiveInfinity;

        if (subject is OverlaySubject.AnySeat) {
            for (var seat = 0; (seat < PlayerRoster.MaxSlots); seat++) {
                if (m_roster.IsJoined(slot: seat)) {
                    nearest = MathF.Min(
                        x: nearest,
                        y: Distance(
                            of: of,
                            slot: slot,
                            subject: new OverlaySubject.Seat(Number: (seat + 1))
                        )
                    );
                }
            }

            return nearest;
        }

        if (of is OverlaySubject.AnySeat) {
            for (var seat = 0; (seat < PlayerRoster.MaxSlots); seat++) {
                if (m_roster.IsJoined(slot: seat)) {
                    nearest = MathF.Min(
                        x: nearest,
                        y: Distance(
                            of: new OverlaySubject.Seat(Number: (seat + 1)),
                            slot: slot,
                            subject: subject
                        )
                    );
                }
            }

            return nearest;
        }

        if (
            !TryResolvePosition(
            position: out var from,
            slot: slot,
            subject: subject
        ) ||
            !TryResolvePosition(
            position: out var to,
            slot: slot,
            subject: (of ?? new OverlaySubject.Seat())
        )
        ) {
            return float.PositiveInfinity;
        }

        return Vector3.Distance(
            value1: from,
            value2: to
        );
    }
    // A fact's recency clock: refreshed to the current tick while it holds. Sampling is idempotent within a tick.
    private bool Holds(int slot, OverlayFact fact, out ulong lastHeldTick) {
        var tick = CompletedTick;
        var index = ((slot * FactCount) + ((int)fact));
        var holds = Sample(
            fact: fact,
            slot: slot,
            tick: tick
        );

        if (holds) {
            m_lastHeldTick[index] = tick;
        }

        lastHeldTick = m_lastHeldTick[index];

        return holds;
    }
    // The pointer's motion sequence advanced since the last look.
    private bool PointerMoved(int slot) {
        if (m_pointer is not { } pointer) {
            return false;
        }

        var sequence = pointer.MotionSequence(slot: slot);

        if (sequence == m_seenMotionSequence[slot]) {
            return false;
        }

        m_seenMotionSequence[slot] = sequence;

        return true;
    }
    private bool Recently(int slot, OverlayFact fact, float windowSeconds, float fadeSeconds) =>
        (RecentPresence(
            fact: fact,
            fadeSeconds: fadeSeconds,
            slot: slot,
            windowSeconds: windowSeconds
        ) > 0f);
    // A recency fact's PRESENCE: 1 while it holds and for the window after; then, across the fade, 1 down to 0 on
    // the completed-tick clock (presentation reads it, the simulation never does); 0 once the fade has elapsed.
    private float RecentPresence(int slot, OverlayFact fact, float windowSeconds, float fadeSeconds) {
        var holds = Holds(
            fact: fact,
            lastHeldTick: out var lastHeldTick,
            slot: slot
        );

        if (holds) {
            return 1f;
        }

        return RecentPresence(
            fadeSeconds: fadeSeconds,
            lastHeldTick: lastHeldTick,
            windowSeconds: windowSeconds
        );
    }
    private float RecentPresence(ulong lastHeldTick, float windowSeconds, float fadeSeconds) => OverlayRecency.Presence(
        completedTick: CompletedTick,
        fadeSeconds: fadeSeconds,
        lastHeldTick: lastHeldTick,
        rateHz: m_client.Definition.SimulationRateHz,
        windowSeconds: windowSeconds
    );
    // A subject's speaking presence: the speech clock's last stamp for its body, through the same window/fade curve
    // a recency fact reads. AnySeat takes the maximum over the joined seats.
    private float SpeakingPresence(int slot, OverlayPredicate.Speaking speaking) {
        if (speaking.Subject is OverlaySubject.AnySeat) {
            var presence = 0f;

            for (var seat = 0; (seat < PlayerRoster.MaxSlots); seat++) {
                if (m_roster.IsJoined(slot: seat)) {
                    presence = MathF.Max(
                        x: presence,
                        y: SpeakingPresence(
                            slot: slot,
                            speaking: (speaking with { Subject = new OverlaySubject.Seat(Number: (seat + 1)) })
                        )
                    );
                }
            }

            return presence;
        }

        if (!TryResolveBody(
            body: out var body,
            slot: slot,
            subject: speaking.Subject
        )) {
            return 0f;
        }

        return RecentPresence(
            fadeSeconds: speaking.FadeSeconds,
            lastHeldTick: m_speech.LastSpokeTick(bodyIndex: body),
            windowSeconds: speaking.WindowSeconds
        );
    }
    // Subject to body: Seat through the perception anchor (possession follows), Entity by index, RecentSpeaker
    // through the speech clock, a placement through its inhabitant body when one exists. AnySeat is a quantifier and
    // resolves no single body here.
    private bool TryResolveBody(int slot, OverlaySubject subject, out int body) {
        body = subject switch {
            OverlaySubject.Seat seat => m_perception.PerceivedBody(slot: ((seat.Number is { } number) ? (number - 1) : slot)),
            OverlaySubject.Entity entity => entity.Index,
            OverlaySubject.RecentSpeaker => m_speech.RecentSpeakerBody,
            OverlaySubject.Placement placement => (m_client.TryInhabitantBody(
                index: out var inhabitant,
                placementId: placement.PlacementId
            )
                ? inhabitant
                : -1),
            _ => -1,
        };

        return (((uint)body) < ((uint)WorldClient.EntityCapacity));
    }
    // Subject to position: a body subject reads the client's render pose; a placement reads its live stamped shape
    // position, else the static stamp math.
    private bool TryResolvePosition(int slot, OverlaySubject subject, out Vector3 position) {
        if (subject is OverlaySubject.Placement placement) {
            if (m_stamps.TryShapePosition(
                client: m_client,
                placementId: placement.PlacementId,
                position: out position,
                shapeId: placement.ShapeId
            )) {
                return true;
            }

            position = WorldAnchorGeometry.StaticPlacementPosition(
                definition: m_client.Definition,
                placementId: placement.PlacementId,
                shapeId: placement.ShapeId
            );

            return true;
        }

        if (
            !TryResolveBody(
            body: out var body,
            slot: slot,
            subject: subject
        ) ||
            !m_client.IsActive(index: body)
        ) {
            position = default;

            return false;
        }

        position = m_client.Position(index: body);

        return true;
    }
    /// <summary>Evaluates a predicate's PRESENCE for one seat: 1 while it fully holds, 0 when it does not, and the
    /// eased value in between while a <see cref="OverlayPredicate.Recently"/> fades — <c>all</c> takes the minimum,
    /// <c>any</c> the maximum, <c>not</c> the complement. <see cref="Evaluate"/> is this above zero.</summary>
    /// <param name="slot">The 0-based local seat.</param>
    /// <param name="predicate">The predicate, or <see langword="null"/> (always fully present).</param>
    public float Presence(int slot, OverlayPredicate? predicate) {
        switch (predicate) {
            case null:
                return 1f;
            case OverlayPredicate.Now now:
                return (Holds(
                    fact: now.Fact,
                    lastHeldTick: out _,
                    slot: slot
                )
                    ? 1f
                    : 0f);
            case OverlayPredicate.Recently recently:
                return RecentPresence(
                    fact: recently.Fact,
                    fadeSeconds: recently.FadeSeconds,
                    slot: slot,
                    windowSeconds: recently.WindowSeconds
                );
            case OverlayPredicate.All all: {
                var presence = 1f;

                foreach (var inner in (all.Predicates ?? [])) {
                    presence = MathF.Min(
                        x: presence,
                        y: Presence(
                            predicate: inner,
                            slot: slot
                        )
                    );
                }

                return presence;
            }
            case OverlayPredicate.Any any: {
                var presence = 0f;

                foreach (var inner in (any.Predicates ?? [])) {
                    presence = MathF.Max(
                        x: presence,
                        y: Presence(
                            predicate: inner,
                            slot: slot
                        )
                    );
                }

                return presence;
            }
            case OverlayPredicate.Not not:
                return (1f - Presence(
                    predicate: not.Predicate,
                    slot: slot
                ));
            case OverlayPredicate.Speaking speaking:
                return SpeakingPresence(
                    slot: slot,
                    speaking: speaking
                );
            case OverlayPredicate.Near near:
                return ((Distance(
                    of: near.Of,
                    slot: slot,
                    subject: near.Subject
                ) <= near.Distance)
                    ? 1f
                    : 0f);
            case OverlayPredicate.State state:
                return (OverlayStateComparison.Holds(
                    definition: m_client.Definition,
                    state: state,
                    tick: m_client.Tick
                )
                    ? 1f
                    : 0f);
            default:
                return 1f;
        }
    }
    // Samples a fact from its owner for one seat. SeatInput/PointerMotion are edge facts: they hold for the whole
    // evaluation tick on which their owner is first observed advanced (an overlay evaluates once per frame, several
    // sim ticks apart, so "arrived this tick" would miss most inputs, and every element evaluating in that frame must
    // read the same answer); the rest are states read live. Every fact is credited to the CURRENT sim tick — the
    // router's own input tick counts the pump's steps, a different base from the server's tick once a world reloads,
    // so it is only ever compared against itself.
    private bool Sample(int slot, OverlayFact fact, ulong tick) {
        switch (fact) {
            case OverlayFact.SeatInput:
                if (
                    m_router().TryGetLastInputTick(
                    slot: slot,
                    tick: out var inputTick
                ) &&
                    (inputTick != m_seenInputTick[slot])
                ) {
                    m_seenInputTick[slot] = inputTick;
                    m_inputEdgeTick[slot] = tick;
                }

                return (m_inputEdgeTick[slot] == tick);
            case OverlayFact.PointerMotion:
                if (PointerMoved(slot: slot)) {
                    m_motionEdgeTick[slot] = tick;
                }

                return (m_motionEdgeTick[slot] == tick);
            default:
                return SampleState(
                    fact: fact,
                    slot: slot
                );
        }
    }
    private bool SampleState(int slot, OverlayFact fact) => fact switch {
        OverlayFact.WheelOpen => ((m_wheel() is { } wheel) && wheel.StatusFor(slot: slot).Open),
        OverlayFact.ConsoleOpen => ((m_consoles is { } consoles) && consoles.TryGetVisible(
        slot: slot,
        visible: out var visible
    ) && visible),
        OverlayFact.SeatCameraApplication => m_seatBindings.IsCameraModeActive(slot: slot),
        _ => false,
    };

    /// <summary>Evaluates a predicate for one local seat; a <see langword="null"/> predicate is true.</summary>
    /// <param name="slot">The 0-based local seat.</param>
    /// <param name="predicate">The predicate, or <see langword="null"/>.</param>
    public bool Evaluate(int slot, OverlayPredicate? predicate) {
        switch (predicate) {
            case null:
                return true;
            case OverlayPredicate.Now now:
                return Holds(
                    fact: now.Fact,
                    lastHeldTick: out _,
                    slot: slot
                );
            case OverlayPredicate.Recently recently:
                return Recently(
                    fact: recently.Fact,
                    fadeSeconds: recently.FadeSeconds,
                    slot: slot,
                    windowSeconds: recently.WindowSeconds
                );
            case OverlayPredicate.All all:
                foreach (var inner in (all.Predicates ?? [])) {
                    if (!Evaluate(
                        predicate: inner,
                        slot: slot
                    )) {
                        return false;
                    }
                }

                return true;
            case OverlayPredicate.Any any:
                foreach (var inner in (any.Predicates ?? [])) {
                    if (Evaluate(
                        predicate: inner,
                        slot: slot
                    )) {
                        return true;
                    }
                }

                return false;
            case OverlayPredicate.Not not:
                return !Evaluate(
                    predicate: not.Predicate,
                    slot: slot
                );
            case OverlayPredicate.Speaking speaking:
                return (SpeakingPresence(
                    slot: slot,
                    speaking: speaking
                ) > 0f);
            case OverlayPredicate.Near near:
                return (Distance(
                    of: near.Of,
                    slot: slot,
                    subject: near.Subject
                ) <= near.Distance);
            case OverlayPredicate.State state:
                return OverlayStateComparison.Holds(
                    definition: m_client.Definition,
                    state: state,
                    tick: m_client.Tick
                );
            default:
                return true;
        }
    }
    /// <summary>Evaluates a predicate for the world scope: true when it holds for any joined local seat (or when
    /// no seat is joined and the predicate is <see langword="null"/>).</summary>
    /// <param name="predicate">The predicate, or <see langword="null"/>.</param>
    public bool EvaluateAnySeat(OverlayPredicate? predicate) {
        if (predicate is null) {
            return true;
        }

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (
                m_roster.IsJoined(slot: slot) &&
                Evaluate(
                predicate: predicate,
                slot: slot
            )
            ) {
                return true;
            }
        }

        return false;
    }
}
