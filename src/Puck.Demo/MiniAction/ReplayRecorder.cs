namespace Puck.Demo.MiniAction;

/// <summary>
/// A complete recording of a session: the seed, the initial roster, the per-tick ROSTER EVENTS (joins/leaves), and the
/// per-tick per-slot INTENTS. Because the simulation is a pure function of <c>(seed, intents, roster events)</c>,
/// replaying these reproduces the session bit-for-bit through join, leave, and slot recycling. Versioned binary; v2 adds
/// the roster-event channel and fixes the intent row at <see cref="MiniActionWorld.MaxPlayers"/>.
/// </summary>
public sealed class MiniActionReplay {
    private const uint Magic = 0x4D41_5252u; // "MARR"
    private const uint Version = 2u;

    /// <summary>The simulation seed.</summary>
    public required uint Seed { get; init; }
    /// <summary>Players present before tick 0 (usually empty — players join via roster events).</summary>
    public required IReadOnlyList<Guid> InitialRoster { get; init; }
    /// <summary>The per-slot intents for each tick: <c>Ticks[t][slot]</c>, fixed width <see cref="MiniActionWorld.MaxPlayers"/>.</summary>
    public required IReadOnlyList<PlayerIntent[]> Ticks { get; init; }
    /// <summary>The roster events applied at the start of each tick: <c>RosterEvents[t]</c>, parallel to <see cref="Ticks"/>.</summary>
    public required IReadOnlyList<IReadOnlyList<RosterEvent>> RosterEvents { get; init; }

    /// <summary>Serializes the recording to a stream.</summary>
    public void Write(Stream stream) {
        ArgumentNullException.ThrowIfNull(stream);

        using var writer = new BinaryWriter(output: stream, encoding: System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(value: Magic);
        writer.Write(value: Version);
        writer.Write(value: Seed);

        writer.Write(value: InitialRoster.Count);

        foreach (var player in InitialRoster) {
            writer.Write(buffer: player.ToByteArray());
        }

        writer.Write(value: Ticks.Count);

        foreach (var tick in Ticks) {
            for (var slot = 0; (slot < MiniActionWorld.MaxPlayers); slot++) {
                WriteIntent(writer: writer, intent: tick[slot]);
            }
        }

        foreach (var events in RosterEvents) {
            writer.Write(value: events.Count);

            foreach (var rosterEvent in events) {
                writer.Write(value: (byte)rosterEvent.Kind);
                writer.Write(buffer: rosterEvent.PlayerId.ToByteArray());
                writer.Write(value: rosterEvent.Slot);
            }
        }
    }

    /// <summary>Reads a recording from a stream, validating the magic + version.</summary>
    public static MiniActionReplay Read(Stream stream) {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(input: stream, encoding: System.Text.Encoding.UTF8, leaveOpen: true);

        if ((reader.ReadUInt32() != Magic) || (reader.ReadUInt32() != Version)) {
            throw new InvalidDataException(message: "Not a MiniAction replay, or an unsupported version.");
        }

        var seed = reader.ReadUInt32();
        var initialCount = reader.ReadInt32();
        var initialRoster = new Guid[initialCount];

        for (var index = 0; (index < initialCount); index++) {
            initialRoster[index] = new Guid(b: reader.ReadBytes(count: 16));
        }

        var tickCount = reader.ReadInt32();
        var ticks = new PlayerIntent[tickCount][];

        for (var index = 0; (index < tickCount); index++) {
            var row = new PlayerIntent[MiniActionWorld.MaxPlayers];

            for (var slot = 0; (slot < MiniActionWorld.MaxPlayers); slot++) {
                row[slot] = ReadIntent(reader: reader);
            }

            ticks[index] = row;
        }

        var rosterEvents = new IReadOnlyList<RosterEvent>[tickCount];

        for (var index = 0; (index < tickCount); index++) {
            var count = reader.ReadInt32();
            var events = new RosterEvent[count];

            for (var eventIndex = 0; (eventIndex < count); eventIndex++) {
                var kind = (RosterEventKind)reader.ReadByte();
                var playerId = new Guid(b: reader.ReadBytes(count: 16));
                var slot = reader.ReadInt32();

                events[eventIndex] = new RosterEvent(Kind: kind, PlayerId: playerId, Slot: slot);
            }

            rosterEvents[index] = events;
        }

        return new MiniActionReplay { InitialRoster = initialRoster, RosterEvents = rosterEvents, Seed = seed, Ticks = ticks };
    }

    private static void WriteIntent(BinaryWriter writer, PlayerIntent intent) {
        writer.Write(value: intent.Move.X);
        writer.Write(value: intent.Move.Y);
        writer.Write(value: (byte)((intent.JumpHeld ? 1 : 0) | (intent.JumpPressed ? 2 : 0) | (intent.JumpReleased ? 4 : 0)));
    }
    private static PlayerIntent ReadIntent(BinaryReader reader) {
        var x = reader.ReadSingle();
        var y = reader.ReadSingle();
        var flags = reader.ReadByte();

        return new PlayerIntent(
            Move: new System.Numerics.Vector2(x: x, y: y),
            JumpHeld: (0 != (flags & 1)),
            JumpPressed: (0 != (flags & 2)),
            JumpReleased: (0 != (flags & 4))
        );
    }
}

/// <summary>
/// Accumulates a session's per-tick roster events + intents into a <see cref="MiniActionReplay"/>. The node records
/// exactly the events it applied and the intents it stepped, so the recording is a faithful, replayable log.
/// </summary>
public sealed class ReplayRecorder {
    private readonly List<PlayerIntent[]> m_ticks = [];
    private readonly List<IReadOnlyList<RosterEvent>> m_rosterEvents = [];
    private readonly List<RosterEvent> m_pending = [];
    private readonly uint m_seed;
    private readonly IReadOnlyList<Guid> m_initialRoster;

    /// <summary>Begins a recording for an initial roster and seed.</summary>
    public ReplayRecorder(uint seed, IReadOnlyList<Guid> initialRoster) {
        ArgumentNullException.ThrowIfNull(initialRoster);

        m_seed = seed;
        m_initialRoster = [.. initialRoster];
    }

    /// <summary>Queues a roster event for the next recorded tick (flushed by the following <see cref="Record"/>).</summary>
    public void RecordRosterEvent(RosterEvent rosterEvent) {
        m_pending.Add(item: rosterEvent);
    }

    /// <summary>Appends one tick: the queued roster events plus its per-slot intents (copied, so the caller may reuse it).</summary>
    public void Record(PlayerIntent[] intentsBySlot) {
        ArgumentNullException.ThrowIfNull(intentsBySlot);

        m_rosterEvents.Add(item: m_pending.ToArray());
        m_pending.Clear();
        m_ticks.Add(item: (PlayerIntent[])intentsBySlot.Clone());
    }

    /// <summary>Builds the immutable recording.</summary>
    public MiniActionReplay ToReplay() {
        return new MiniActionReplay {
            InitialRoster = m_initialRoster,
            RosterEvents = m_rosterEvents,
            Seed = m_seed,
            Ticks = m_ticks,
        };
    }
}
