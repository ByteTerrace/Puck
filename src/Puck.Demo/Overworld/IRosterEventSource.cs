using Puck.Commands;
using Puck.Input;

namespace Puck.Demo.Overworld;

/// <summary>
/// Supplies the roster mutations (joins/leaves) for a tick — kept SEPARATE from the intent stream so the simulation
/// stays a pure function of <c>(seed, intents, roster events)</c>. A local source diffs connected controllers; a replay
/// source re-emits a recording's events; a scripted source drives the determinism gate.
/// </summary>
public interface IRosterEventSource {
    /// <summary>The roster events to apply at the START of <paramref name="tick"/>, before that tick's intents.</summary>
    IReadOnlyList<RosterEvent> EventsForTick(ulong tick);
}

/// <summary>Re-emits a recording's roster events verbatim, for replay.</summary>
public sealed class ReplayRosterEventSource : IRosterEventSource {
    private readonly OverworldRecording m_recording;

    public ReplayRosterEventSource(OverworldRecording recording) {
        ArgumentNullException.ThrowIfNull(recording);

        m_recording = recording;
    }

    /// <inheritdoc/>
    public IReadOnlyList<RosterEvent> EventsForTick(ulong tick) {
        return m_recording.RosterEventsForTick(tick: tick);
    }
}

/// <summary>A fixed schedule of roster events at specific ticks — drives the determinism gate through join/leave/recycle.</summary>
public sealed class ScriptedRosterEventSource : IRosterEventSource {
    private readonly IReadOnlyList<(ulong Tick, RosterEvent Event)> m_schedule;

    public ScriptedRosterEventSource(IReadOnlyList<(ulong Tick, RosterEvent Event)> schedule) {
        ArgumentNullException.ThrowIfNull(schedule);

        m_schedule = schedule;
    }

    /// <inheritdoc/>
    public IReadOnlyList<RosterEvent> EventsForTick(ulong tick) {
        var events = new List<RosterEvent>();

        foreach (var entry in m_schedule) {
            if (entry.Tick == tick) {
                events.Add(item: entry.Event);
            }
        }

        return events;
    }
}

/// <summary>
/// The LIVE roster source: it diffs the connected controllers against the bind table each tick and emits a Join for a
/// newly connected controller (minting + binding its player identity) and a Leave for a vanished one. Single-threaded
/// alongside the gamepad drain in the render node, so the snapshot is consistent. The render node applies the events to
/// the world and records them with the resolved slot.
/// </summary>
public sealed class LocalRosterEventSource : IRosterEventSource {
    private readonly GamepadManager m_manager;
    private readonly ControllerPlayerRegistry m_registry;

    public LocalRosterEventSource(GamepadManager manager, ControllerPlayerRegistry registry) {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(registry);

        m_manager = manager;
        m_registry = registry;
    }

    /// <inheritdoc/>
    public IReadOnlyList<RosterEvent> EventsForTick(ulong tick) {
        var current = m_manager.ConnectedDevices();
        var connected = new HashSet<InputDeviceId>(collection: current);
        var events = new List<RosterEvent>();

        foreach (var device in current) {
            if (!m_registry.TryGetPlayer(device: device, player: out _)) {
                var player = m_registry.Bind(device: device);

                events.Add(item: new RosterEvent(Kind: RosterEventKind.Join, PlayerId: player, Slot: -1));
            }
        }

        foreach (var device in m_registry.Bindings.Keys.ToArray()) {
            if (!connected.Contains(item: device)) {
                _ = m_registry.Unbind(device: device, player: out var player);

                events.Add(item: new RosterEvent(Kind: RosterEventKind.Leave, PlayerId: player, Slot: -1));
            }
        }

        return events;
    }
}
