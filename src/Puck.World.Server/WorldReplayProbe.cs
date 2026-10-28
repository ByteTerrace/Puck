using Puck.Maths;
using Puck.World.Server;

namespace Puck.World;

/// <summary>A second consumer of the tape's input capture, independent of the operator's recording controls.</summary>
public sealed class WorldReplayProbe : IDisposable {
    private readonly WorldReplayTape m_tape;
    private readonly WorldServer m_server;
    private readonly WorldAuthorityCheckpoint m_start;
    private readonly List<WorldReplayTickInput> m_inputs = [];
    private readonly List<ulong> m_hashes = [];
    private readonly List<ulong[]> m_components = [];
    private readonly ulong[] m_startComponents;

    private bool m_attached = true;
    private byte[] m_finalExport = [];
    private IReadOnlyList<WorldReplayMachineDigest> m_finalMachines = [];

    internal WorldReplayProbe(WorldReplayTape tape, WorldServer server) {
        m_tape = tape;
        m_server = server;
        if (!server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason)) {
            throw new InvalidOperationException(message: $"Replay probe cannot capture its start: {reason}");
        }
        m_start = checkpoint!;
        m_startComponents = WorldStateHashComposition.AuthoritativeComponents(server: server, tick: (server.NextInputTick - 1));
    }

    internal void NoteTick(WorldReplayTickInput input, ulong hash) {
        m_inputs.Add(item: new WorldReplayTickInput(Authority: [.. input.Authority], Intents: [.. input.Intents]));
        m_hashes.Add(item: hash);
        m_components.Add(item: WorldStateHashComposition.AuthoritativeComponents(server: m_server, tick: (m_server.NextInputTick - 1)));
    }

    /// <summary>Restores the start into this authority and feeds its recorded inputs through the ordinary replay doors.</summary>
    /// <returns>The first divergent recorded tick, or -1 when every hash, machine core and final export matches.</returns>
    public int Verify() {
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
                m_server.Advance(stepTicks: step);
                WorldReplaySnapshot.VerifyRecordedMutationOutcomes(expected: expected, replayed: outcomes, tick: tick);
                if (WorldStateHashComposition.HashAuthoritative(server: m_server, tick: (m_server.NextInputTick - 1)) != m_hashes[tick]) {
                    var difference = WorldStateHashComposition.DifferingComponents(left: m_components[tick],
                        right: WorldStateHashComposition.AuthoritativeComponents(server: m_server, tick: (m_server.NextInputTick - 1)));

                    throw new InvalidOperationException(message: $"recorded tick {tick} differs: {string.Join(separator: ", ", values: difference)}");
                }
            }
            return ((m_finalExport.AsSpan().SequenceEqual(other: WorldStateExport.ToCanonicalJson(server: m_server)) &&
                m_finalMachines.SequenceEqual(second: WorldReplaySnapshot.MachineDigests(machines: m_server.Machines))) ? -1 : m_inputs.Count);
        } finally { m_tape.ResumeCapture(); }
    }
    /// <summary>Detaches capture without changing the authority.</summary>
    public void Dispose() {
        if (!m_attached) { return; }
        m_attached = false;
        m_finalExport = WorldStateExport.ToCanonicalJson(server: m_server);
        m_finalMachines = WorldReplaySnapshot.MachineDigests(machines: m_server.Machines);
        m_tape.DetachProbe(probe: this);
    }
}
public sealed partial class WorldReplayTape {
    private readonly List<WorldReplayProbe> m_probes = [];

    /// <summary>Observes the same input stream as history and operator recording, starting at this settled boundary.</summary>
    public WorldReplayProbe ObserveReplay() {
        var probe = new WorldReplayProbe(server: m_liveServer, tape: this);

        m_probes.Add(item: probe);
        RefreshCapture();
        return probe;
    }

    internal void DetachProbe(WorldReplayProbe probe) { m_probes.Remove(item: probe); RefreshCapture(); }
}
