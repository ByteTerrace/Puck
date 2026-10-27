using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
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
        Array.Clear(m_inputActionScratch);
        foreach (var logical in next.Live) {
            var destination = next.Address(logical);
            if (destination < 0 || reset || !recompiled && !catalog.Keeps(previous, next, logical) ||
                !previous.Channels.TryGetOrdinal(next.Channels.Name(destination)!, out var source)) { continue; }
            var released = false;
            foreach (var decision in decisions) {
                if (decision.Channel == logical && decision.Reason != WorldInputContinuationReason.Kept) { released = true; break; }
            }
            if (released) { continue; }
            continuing[destination] = true;
            timers[destination] = m_laneTimers[source];
            timerValues[destination] = m_channelTimerValues[source];
            previousBits[destination] = m_previousChannelBit[source] || accepted[destination] != FixedQ4816.Zero;
            m_inputActionScratch[destination] = m_laneActions[source];
        }
        timers.CopyTo(m_laneTimers);
        timerValues.CopyTo(m_channelTimerValues);
        previousBits.CopyTo(m_previousChannelBit);
        m_inputActionScratch.CopyTo(m_laneActions, 0);
        Array.Clear(m_pendingDefaultChannelPress);
        Array.Clear(m_pendingDefaultChannelValue);
        var submitted = default(PlayerIntent);
        var held = default(PlayerIntent);
        foreach (var logical in next.Live) {
            var address = next.Address(logical);
            if (address < 0) { continue; }
            if (next.Channels.IsRole(address)) { submitted = submitted.WithChannel(address, accepted[address]); }
            else { held = held.WithChannel(address, accepted[address]); }
        }
        m_submittedIntent = submitted;
        m_heldChannels = held;
        m_channelReadComposed = accepted;
        m_channelReadHeld = held;
        m_channelReadScalar = accepted;
        m_engagedIntent = accepted;
        m_transferHeldChannels = default;
        m_hasTransferHeldChannels = false;
        for (var offset = 0; offset < m_tapeCount; offset++) {
            ref var segment = ref m_tape[(m_tapeHead + offset) % m_tape.Length];
            var image = default(PlayerIntent);
            if (!reset) {
                foreach (var logical in next.Live) {
                    var destination = next.Address(logical);
                    if (destination >= 0 && continuing[destination]) {
                        _ = previous.Channels.TryGetOrdinal(next.Channels.Name(destination)!, out var source);
                        image = image.WithChannel(destination, segment.Intent[source]);
                    }
                }
            }
            segment.Intent = image;
        }
        RestoreInputMapping(next, kit, epoch);
    }

    /// <summary>Rebinds a restored bank without altering checkpointed levels, timers or edge latches.</summary>
    public void RestoreInputMapping(WorldInputMapping next, FixedWorldKit kit, ulong epoch) {
        SetChannelTable(next.Channels);
        CopyChannelBindings(kit.Actions, kit.ActionThresholds, kit.ActionShapes, kit.RoleMask);
        m_roleOrdinals = kit.RoleOrdinals;
        InputMappingId = next.Id;
        InputEpoch = epoch;
    }
}
