using Puck.Abstractions.Machines;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The last hardware-binding outcome; unavailable observations retain their last accepted value.</summary>
/// <param name="Generation">The instance incarnation the observation belongs to.</param>
/// <param name="Status">Availability of the most recent attempt.</param>
/// <param name="LastValue">The last successfully transferred world value, or null before any success.</param>
/// <param name="Reason">The provider or conversion refusal.</param>
public readonly record struct WorldMachineBindingState(ulong Generation, MachineAccessStatus Status, long? LastValue, string? Reason);

public sealed partial class WorldServer {
    private readonly Dictionary<(string Machine, string Binding), BindingObservation> m_namedMemory = [];
    private IReadOnlyList<WorldMachine>? m_namedMemoryRows;

    private sealed class BindingObservation(WorldMachineMemory binding, ulong generation) {
        public WorldMachineMemory Binding { get; } = binding;
        public WorldMachineBindingState State { get; set; } = new(generation, MachineAccessStatus.Unavailable, null, "Not observed yet.");
    }

    /// <summary>Reads a named binding's availability and last accepted value.</summary>
    /// <param name="machine">The instance name.</param>
    /// <param name="binding">The binding name within the instance.</param>
    /// <returns>The current incarnation's observation, or null before synchronization.</returns>
    public WorldMachineBindingState? MachineBindingState(string machine, string binding) {
        if (!m_namedMemory.TryGetValue((machine, binding), out var observation) ||
            m_machines.InstanceState(machine) is not { } instance || instance.Generation != observation.State.Generation ||
            m_definition.Machines.FirstOrDefault(row => row.Name == machine)?.Memory?.Contains(observation.Binding) != true) {
            return null;
        }
        return observation.State;
    }

    // Same phase as the original mirror: rules, ordered bindings, then machine advance. Each hardware scalar
    // crosses the provider barrier once, so a word cannot combine bytes from different guest steps.
    private void SyncNamedMachineMemory(ulong tick) {
        if (!ReferenceEquals(m_namedMemoryRows, m_definition.MachinesRaw)) {
            m_namedMemoryRows = m_definition.MachinesRaw;
            var retained = new HashSet<(string Machine, string Binding)>();
            foreach (var machine in m_definition.Machines) {
                foreach (var binding in machine.Memory ?? []) {
                    retained.Add((machine.Name, binding.Name));
                }
            }
            foreach (var key in m_namedMemory.Keys.Where(key => !retained.Contains(key)).ToArray()) {
                m_namedMemory.Remove(key);
            }
        }
        foreach (var machine in m_definition.Machines) {
            foreach (var binding in machine.Memory ?? []) {
                var generation = m_machines.InstanceState(machine.Name)?.Generation ?? 0;
                var key = (machine.Name, binding.Name);
                if (!m_namedMemory.TryGetValue(key, out var observation) || observation.Binding != binding ||
                    observation.State.Generation != generation) {
                    observation = new(binding, generation);
                    m_namedMemory[key] = observation;
                }
                if (!m_machines.TryBindingAddress(machine.Name, binding.Name, out var address)) {
                    Observe(observation, new(MachineAccessStatus.Unavailable, Reason: "The binding has no prepared address."));
                    continue;
                }
                try {
                    if (binding.Direction == WorldMachineMemoryDirection.Write) {
                        SyncNamedWrite(machine.Name, binding, address, observation, tick);
                    } else {
                        SyncNamedRead(machine.Name, binding, address, observation, tick);
                    }
                } catch (OverflowException) {
                    Observe(observation, new(MachineAccessStatus.Refused, Reason: $"Value cannot fit {binding.Format} under checked conversion."));
                }
            }
        }
    }

    private static void Observe(BindingObservation observation, MachineAccessResult result, long? accepted = null) =>
        observation.State = observation.State with {
            Status = result.Status, Reason = result.Reason,
            LastValue = result.Status == MachineAccessStatus.Available ? accepted : observation.State.LastValue,
        };

    private void SyncNamedWrite(string machine, WorldMachineMemory binding, MachineMemoryAddress address,
        BindingObservation observation, ulong tick) {
        if (!WorldStateReader.TryRead(m_definition, binding.Row, binding.Key, tick, out _, out var raw, out _) || raw is not { } value) {
            Observe(observation, new(MachineAccessStatus.Unavailable, Reason: "The world-state cell is unavailable."));
            return;
        }
        if (binding.Update == "onChange" && observation.State.Status == MachineAccessStatus.Available &&
            observation.State.LastValue == value) {
            return;
        }
        var mode = binding.Access == "bus" ? MachineAccessMode.Bus : MachineAccessMode.Patch;
        var result = m_machines.WriteHardware(machine, observation.State.Generation, address, binding.Encode(value), mode);
        Observe(observation, result, value);
    }

    private void SyncNamedRead(string machine, WorldMachineMemory binding, MachineMemoryAddress address,
        BindingObservation observation, ulong tick) {
        var result = m_machines.Inspect(machine, address);
        if (result.Status != MachineAccessStatus.Available) {
            Observe(observation, result);
            return;
        }
        var value = binding.Decode(result.Value);
        if (binding.Update == "onChange" && observation.State.LastValue == value) {
            Observe(observation, result, value);
            return;
        }
        var applied = TryApplyMutation(new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.World, Row: binding.Row, Key: binding.Key ?? WorldStateRow.SlotKey.Value,
            Value: value, Kind: WorldDocumentWriteKind.Set), tick, SubmissionEnvelope.LocalConnectionId, 0, preMetered: false);
        if (applied) {
            Observe(observation, result, value);
            m_output.DeliverState(m_definition);
        } else {
            Observe(observation, new(MachineAccessStatus.Refused, Reason: "The world-state mirror write was refused."));
        }
    }
}
