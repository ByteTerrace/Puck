using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    /// <summary>Starts a cross-authority arrival with no held, queued or partially recognized input.</summary>
    internal void ResetArrivalInput() {
        ClearTransientInput();
        ClearTape();
        Array.Clear(array: m_laneTimers);
        Array.Clear(array: m_channelTimerValues);
        Array.Clear(array: m_pendingDefaultChannelPress);
        Array.Clear(array: m_pendingDefaultChannelValue);
        Array.Clear(array: m_previousChannelBit);
        Array.Clear(array: m_laneActions);
        m_channelReadHeld = default;
        m_channelReadComposed = default;
        m_channelReadScalar = default;
        m_engagedIntent = default;
    }

    private readonly LaneActionRuntime[] m_inputActionScratch = new LaneActionRuntime[ActionLaneCount];

    /// <summary>The input tuple currently installed on this body's folded intent.</summary>
    public ushort InputMappingId { get; private set; }
    /// <summary>The latest installed owner epoch; an address never carries authority without this boundary.</summary>
    public ulong InputEpoch { get; private set; }

    /// <summary>Installs a committed input barrier by logical identity, clearing released edges and timed input.</summary>
    public void InstallInputMapping(WorldInputCatalog catalog, WorldInputMapping previous, WorldInputMapping next,
        FixedWorldKit kit, PlayerIntent accepted, ulong epoch, bool reset, ReadOnlySpan<WorldInputContinuation> decisions, bool recompiled = false) {
        Span<ulong> timers = stackalloc ulong[ActionLaneCount];
        Span<FixedQ4816> timerValues = stackalloc FixedQ4816[ActionLaneCount];
        Span<bool> previousBits = stackalloc bool[ActionLaneCount];
        Span<bool> continuing = stackalloc bool[ActionLaneCount];

        timers.Clear(); timerValues.Clear(); previousBits.Clear();
        continuing.Clear();
        Array.Clear(array: m_inputActionScratch);
        Array.Clear(array: m_inputScriptActorScratch);
        foreach (var logical in next.Live) {
            var destination = next.Address(logicalId: logical);

            if ((destination < 0) || reset || (!recompiled && !catalog.Keeps(from: previous, logicalId: logical, to: next)) ||
                !previous.Channels.TryGetOrdinal(name: next.Channels.Name(ordinal: destination)!, ordinal: out var source)) { continue; }
            var released = false;

            foreach (var decision in decisions) {
                if ((decision.Channel == logical) && (decision.Reason != WorldInputContinuationReason.Kept)) { released = true; break; }
            }
            if (released) { continue; }
            continuing[destination] = true;
            timers[destination] = m_laneTimers[source];
            timerValues[destination] = m_channelTimerValues[source];
            m_inputScriptActorScratch[destination] = m_scriptActors[source];
            previousBits[destination] = (m_previousChannelBit[source] || (accepted[destination] != FixedQ4816.Zero));
            m_inputActionScratch[destination] = m_laneActions[source];
        }
        timers.CopyTo(destination: m_laneTimers);
        timerValues.CopyTo(destination: m_channelTimerValues);
        m_inputScriptActorScratch.CopyTo(array: m_scriptActors, index: 0);
        previousBits.CopyTo(destination: m_previousChannelBit);
        m_inputActionScratch.CopyTo(array: m_laneActions, index: 0);
        Array.Clear(array: m_pendingDefaultChannelPress);
        Array.Clear(array: m_pendingDefaultChannelValue);
        var submitted = default(PlayerIntent);
        var held = default(PlayerIntent);

        foreach (var logical in next.Live) {
            var address = next.Address(logicalId: logical);

            if (address < 0) { continue; }
            if (next.Channels.IsRole(ordinal: address)) { submitted = submitted.WithChannel(ordinal: address, value: accepted[address]); } else { held = held.WithChannel(ordinal: address, value: accepted[address]); }
        }
        m_submittedIntent = submitted;
        m_heldChannels = held;
        m_channelReadComposed = accepted;
        m_channelReadHeld = held;
        m_channelReadScalar = accepted;
        m_engagedIntent = accepted;
        m_transferHeldChannels = default;
        m_hasTransferHeldChannels = false;
        for (var offset = 0; (offset < m_tapeCount); offset++) {
            ref var segment = ref m_tape[((m_tapeHead + offset) % m_tape.Length)];
            var image = default(PlayerIntent);

            if (!reset) {
                foreach (var logical in next.Live) {
                    var destination = next.Address(logicalId: logical);

                    if ((destination >= 0) && continuing[destination]) {
                        _ = previous.Channels.TryGetOrdinal(name: next.Channels.Name(ordinal: destination)!, ordinal: out var source);
                        image = image.WithChannel(ordinal: destination, value: segment.Intent[source]);
                    }
                }
            }
            segment.Intent = image;
        }
        RestoreInputMapping(epoch: epoch, kit: kit, next: next);
    }
    /// <summary>Rebinds a restored bank without altering checkpointed levels, timers or edge latches.</summary>
    public void RestoreInputMapping(WorldInputMapping next, FixedWorldKit kit, ulong epoch) {
        SetChannelTable(channels: next.Channels);
        CopyChannelBindings(kit.Actions, kit.ActionThresholds, kit.ActionShapes, kit.RoleMask);
        m_roleOrdinals = kit.RoleOrdinals;
        InputMappingId = next.Id;
        InputEpoch = epoch;
    }
}
