using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>A second consumer of the tape's input capture, independent of the operator's recording controls.</summary>
public sealed class WorldReplayProbe : IDisposable {
    private readonly WorldReplayTape m_tape;
    private readonly WorldServer m_server;
    private readonly WorldAuthorityCheckpoint m_start;

    private readonly List<WorldReplayTickInput> m_inputs = [];
    private readonly List<ulong> m_hashes = [];

    /// <summary>Gets the per-tick authoritative hashes recorded by <see cref="WorldReplayTape.NoteTick"/> while this probe was attached.</summary>
    public IReadOnlyList<ulong> Hashes => m_hashes;

    private readonly ulong[] m_startComponents;

    private int m_skipAuthority;
    private int m_skipIntents;

    private bool m_attached = true;
    private byte[] m_finalExport = [];
    private IReadOnlyList<WorldReplayMachineDigest> m_finalMachines = [];
    private WorldReplayTickInput m_tail = new(Authority: [], Intents: []);

    internal WorldReplayProbe(WorldReplayTape tape, WorldServer server, int skipAuthority, int skipIntents) {
        m_tape = tape;
        m_server = server;
        m_skipAuthority = skipAuthority;
        m_skipIntents = skipIntents;
        if (!server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason)) {
            throw new InvalidOperationException(message: $"Replay probe cannot capture its start: {reason}");
        }
        m_start = checkpoint!;
        m_startComponents = WorldStateHashComposition.AuthoritativeComponents(server: server, tick: (server.NextInputTick - 1));
    }

    internal void NoteTick(WorldReplayTickInput input, ulong hash) {
        m_inputs.Add(item: new WorldReplayTickInput(Authority: [.. input.Authority.Skip(count: m_skipAuthority)], Intents: [.. input.Intents.Skip(count: m_skipIntents)]));
        m_skipAuthority = 0;
        m_skipIntents = 0;
        m_hashes.Add(item: hash);
    }

    /// <summary>Restores the start into this authority and feeds its recorded inputs through the ordinary replay doors.</summary>
    /// <param name="progress">Optional completed-tick notification for the worker's progress watchdog.</param>
    /// <returns>The first divergent recorded tick, or -1 when every hash, machine core and final export matches.</returns>
    public int Verify(Action<int>? progress = null) {
        Dispose();
        m_tape.SuspendCapture();
        try {
            m_server.Persistence.RestoreScenario(checkpoint: m_start);
            var startDifference = WorldStateHashComposition.DifferingComponents(left: m_startComponents,
                right: WorldStateHashComposition.AuthoritativeComponents(server: m_server, tick: (m_server.NextInputTick - 1)));

            if (startDifference.Count != 0) { throw new InvalidOperationException(message: $"restored start differs: {string.Join(separator: ", ", values: startDifference)}"); }
            var outcomes = new Queue<bool>();
            var step = FixedTickConversion.PerRate(ratePerSecond: ((uint)m_server.Configuration.SimulationRateHz));

            for (var tick = 0; (tick < m_inputs.Count); tick++) {
                var expected = new List<bool>();

                WorldReplaySnapshot.ApplyRecordedTick(m_server, m_server.Population, m_inputs[tick], expected, outcomes,
                    static rebuild => (null, rebuild.ContentHash));
                // The companion's tape carries its own delivered input. Re-emitting a screen session's
                // delivery here would mutate that still-live companion a second time.
                m_server.ReplaysInput = true;
                m_server.Advance(stepTicks: step);
                m_server.EnforceJournalDepth();
                WorldReplaySnapshot.VerifyRecordedMutationOutcomes(expected: expected, replayed: outcomes, tick: tick);
                if (WorldStateHashComposition.HashAuthoritative(server: m_server, tick: (m_server.NextInputTick - 1)) != m_hashes[tick]) {
                    return tick;
                }
                progress?.Invoke((tick + 1));
            }
            // The export can fall between this authority's ticks. Session/transfer events have already
            // applied, while queued input still awaits a step: replay that boundary without advancing it.
            WorldReplaySnapshot.ApplyRecordedTick(m_server, m_server.Population, m_tail, [], outcomes,
                static rebuild => (null, rebuild.ContentHash));
            return ((m_finalExport.AsSpan().SequenceEqual(other: WorldStateExport.ToCanonicalJson(server: m_server)) &&
                m_finalMachines.SequenceEqual(second: WorldReplaySnapshot.MachineDigests(machines: m_server.Machines))) ? -1 : m_inputs.Count);
        } finally { m_tape.ResumeCapture(); }
    }
    /// <summary>Detaches capture without changing the authority.</summary>
    public void Dispose() {
        if (!m_attached) { return; }
        m_attached = false;
        m_tail = m_tape.CaptureOpenTick(skipAuthority: m_skipAuthority, skipIntents: m_skipIntents);
        m_finalExport = WorldStateExport.ToCanonicalJson(server: m_server);
        m_finalMachines = WorldReplaySnapshot.MachineDigests(machines: m_server.Machines);
        m_tape.DetachProbe(probe: this);
    }
}
public sealed partial class WorldReplayTape {
    private readonly List<WorldReplayProbe> m_probes = [];

    internal WorldReplayTickInput CaptureOpenTick(int skipAuthority, int skipIntents) => new(
        Authority: [.. m_currentAuthority.Skip(count: skipAuthority)], Intents: [.. m_currentIntents.Skip(count: skipIntents)]);

    /// <summary>Observes the same input stream as history and operator recording, starting at this settled boundary.</summary>
    public WorldReplayProbe ObserveReplay() {
        var probe = new WorldReplayProbe(server: m_liveServer, tape: this,
            skipAuthority: m_currentAuthority.Count, skipIntents: m_currentIntents.Count);

        m_probes.Add(item: probe);
        RefreshCapture();
        return probe;
    }
    /// <summary>Observes another local authority through the same tape construction used for companion recordings.</summary>
    public WorldReplayProbe ObserveReplay(WorldInstance instance) {
        if (ReferenceEquals(objA: instance.Server, objB: m_liveServer)) { return ObserveReplay(); }
        if (instance.Link is not LoopbackTransport transport) {
            throw new InvalidOperationException(message: $"Authority '{instance.Name}' has no local replay input stream.");
        }
        instance.Tape ??= CompanionTape(row: instance, transport: transport);
        return instance.Tape.ObserveReplay();
    }

    internal void DetachProbe(WorldReplayProbe probe) { m_probes.Remove(item: probe); RefreshCapture(); }
}
