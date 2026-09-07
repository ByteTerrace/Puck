using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The <c>screens[].memory</c> seam: a <see cref="WorldScreenMemoryDirection.Read"/> binding mirrors a
/// declared machine's live bus bytes into an ordinary kind=Int <c>state.world</c> cell; a
/// <see cref="WorldScreenMemoryDirection.Write"/> binding pokes the cell's own value into the machine's bus. Both
/// directions run once per tick, right before <see cref="IWorldMachineHost.Advance"/> steps every booted machine
/// (<see cref="WorldServer.Step"/>), so a Write binding's poke reaches the machine before this tick's step (visible
/// to the cartridge on its next frame) and a Read binding mirrors the byte the previous step left behind — the same
/// evaluation point <c>WorldRuleFacts.MachinePrefix</c>'s live <c>$machine:</c> rule read already answers from. A
/// mirrored write applies through the ordinary <see cref="WorldMutation.UpsertStateCell"/> door
/// (<see cref="WorldServer.TryApplyMutation"/>), exactly as <c>WorldServer.Fields.cs</c>'s own engine-driven per-tick
/// cell writes already do — the same mutation shape a rule-authored frame's own single-cell write folds into, without
/// this seam reaching into that frame itself. A poke is a mutation of the machine's guest state through the host
/// seam rather than the document: deterministic under replay because the byte it carries is read off simulation
/// state a replay reproduces bit-for-bit, unlike <see cref="Puck.Abstractions.Machines.IMachineMemoryPeek.PokeByte"/>'s
/// own debug-mutation caveat about an unrecorded input.</summary>
public sealed partial class WorldServer {
    // Per-binding memo, keyed by (engine screen index, bus address): the last value each direction observed. A Read
    // entry updates only on a successful peek (an absent/peek-incapable machine leaves it untouched, so the mirror
    // retries every tick until one appears rather than latching a stale value forever); a Write entry updates only
    // on a successful poke, for the identical reason in the other direction. Either way, an unmoved value costs one
    // dictionary lookup and nothing else — the quiet-machine guarantee.
    private readonly Dictionary<(int Screen, int Address), long> m_machineMemoryReadObserved = [];
    private readonly Dictionary<(int Screen, int Address), long> m_machineMemoryWriteObserved = [];

    private void SyncMachineMemory(ulong tick) {
        var screens = m_definition.Screens;

        for (var index = 0; (index < screens.Count); index++) {
            var screen = screens[index];

            if (screen.Memory is not { Count: > 0 } bindings) {
                continue;
            }

            for (var bindingIndex = 0; (bindingIndex < bindings.Count); bindingIndex++) {
                var binding = bindings[bindingIndex];

                if (binding.Direction == WorldScreenMemoryDirection.Write) {
                    SyncMachineMemoryWrite(screen: screen.Index, binding: binding, tick: tick);
                } else {
                    SyncMachineMemoryRead(screen: screen.Index, binding: binding, tick: tick);
                }
            }
        }
    }
    private void SyncMachineMemoryWrite(int screen, WorldScreenMemory binding, ulong tick) {
        if (
            !WorldStateReader.TryRead(definition: m_definition, rowName: binding.Row, key: binding.Key, tick: tick, row: out _, rawValue: out var raw, text: out _) ||
            (raw is not { } value)
        ) {
            return;
        }

        var key = (Screen: screen, binding.Address);

        if (m_machineMemoryWriteObserved.TryGetValue(key: key, value: out var last) && (last == value)) {
            return;
        }

        var (lowOk, _) = m_machines.TryPokeMessage(index: screen, address: binding.Address, value: unchecked((byte)value));
        var highOk = true;

        if (binding.Width == 2) {
            (highOk, _) = m_machines.TryPokeMessage(index: screen, address: (binding.Address + 1), value: unchecked((byte)(value >> 8)));
        }

        if (lowOk && highOk) {
            m_machineMemoryWriteObserved[key] = value;
        }
    }
    private void SyncMachineMemoryRead(int screen, WorldScreenMemory binding, ulong tick) {
        var (lowOk, _) = m_machines.TryPeekMessage(index: screen, address: binding.Address, value: out var low);

        if (!lowOk) {
            return;
        }

        var value = ((long)low);

        if (binding.Width == 2) {
            var (highOk, _) = m_machines.TryPeekMessage(index: screen, address: (binding.Address + 1), value: out var high);

            if (!highOk) {
                return;
            }

            value |= (((long)high) << 8);
        }

        var key = (Screen: screen, binding.Address);

        if (m_machineMemoryReadObserved.TryGetValue(key: key, value: out var last) && (last == value)) {
            return;
        }

        var applied = TryApplyMutation(
            mutation: new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.World,
                Row: binding.Row,
                Key: (binding.Key ?? WorldStateRow.SlotKey.Value),
                Value: value,
                Kind: WorldDocumentWriteKind.Set
            ),
            tick: tick,
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            preMetered: false
        );

        if (!applied) {
            return;
        }

        m_machineMemoryReadObserved[key] = value;
        m_output.DeliverState(definition: m_definition);
    }
    /// <summary>Returns the last value a <c>screens[].memory</c> binding at <paramref name="address"/> observed in
    /// <paramref name="direction"/> — the byte(s) last mirrored into its cell (<see cref="WorldScreenMemoryDirection.Read"/>)
    /// or last poked into the machine (<see cref="WorldScreenMemoryDirection.Write"/>) — the <c>screen.state</c>
    /// read-back's source.</summary>
    /// <param name="screen">The engine screen-surface index.</param>
    /// <param name="address">The binding's bus address.</param>
    /// <param name="direction">Which binding direction to read.</param>
    /// <param name="value">The last observed value.</param>
    /// <returns><see langword="false"/> when this binding has not observed a value yet.</returns>
    public bool TryMachineMemoryObserved(int screen, int address, WorldScreenMemoryDirection direction, out long value) {
        var key = (Screen: screen, Address: address);

        return ((direction == WorldScreenMemoryDirection.Write)
            ? m_machineMemoryWriteObserved.TryGetValue(key: key, value: out value)
            : m_machineMemoryReadObserved.TryGetValue(key: key, value: out value)
        );
    }
}
