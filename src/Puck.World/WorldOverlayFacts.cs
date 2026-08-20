using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Evaluates <see cref="OverlayPredicate"/>s per local seat over the presentation facts an overlay element
/// may read (<see cref="OverlayFact"/>). Facts come straight from their owners each evaluation — nothing is
/// duplicated here — except the recency clocks, which remember the last tick each fact held. Simulation ticks
/// (<see cref="WorldServer.NextInputTick"/>) are the time base, so a window in seconds compiles through
/// <see cref="WorldSimulationTickConversion.CompiledDuration"/> exactly as an action's does.</summary>
internal sealed class WorldOverlayFacts {
    private static readonly int FactCount = Enum.GetValues<OverlayFact>().Length;

    private readonly WorldClient m_client;
    private readonly IConsoleSessions? m_consoles;
    private readonly WorldSeatBindings m_seatBindings;
    private readonly WorldPointer? m_pointer;
    private readonly PlayerRoster m_roster;
    private readonly Func<InputRouter> m_router;
    private readonly WorldServer m_server;
    private readonly Func<WorldWheelFeed?> m_wheel;

    private readonly ulong[] m_lastHeldTick = new ulong[(PlayerRoster.MaxSlots * FactCount)];
    private readonly ulong[] m_seenInputTick = new ulong[PlayerRoster.MaxSlots];
    private readonly ulong[] m_inputEdgeTick = new ulong[PlayerRoster.MaxSlots];
    private readonly ulong[] m_seenMotionSequence = new ulong[PlayerRoster.MaxSlots];
    private readonly ulong[] m_motionEdgeTick = new ulong[PlayerRoster.MaxSlots];

    /// <summary>Initializes the evaluator over the fact owners. Presentation-only owners are optional: a headless
    /// composition has no pointer, wheel, or console, and their facts read false there.</summary>
    public WorldOverlayFacts(WorldClient client, PlayerRoster roster, WorldServer server, WorldSeatBindings seatBindings, Func<InputRouter> router, Func<WorldWheelFeed?> wheel, WorldPointer? pointer, IConsoleSessions? consoles) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(seatBindings);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(wheel);
        m_client = client;
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
    private bool Recently(int slot, OverlayFact fact, float windowSeconds) {
        var holds = Holds(
            fact: fact,
            lastHeldTick: out var lastHeldTick,
            slot: slot
        );

        if (holds) {
            return true;
        }

        var window = WorldSimulationTickConversion.CompiledDuration(
            ratePerSecond: ((uint)m_client.Definition.SimulationRateHz),
            seconds: windowSeconds
        );

        if (
            window.IsNever ||
            (lastHeldTick == 0UL)
        ) {
            return false;
        }

        return ((CompletedTick - lastHeldTick) < ((ulong)window.Ticks));
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
        OverlayFact.SeatFlying => m_seatBindings.IsCameraModeActive(slot: slot),
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
