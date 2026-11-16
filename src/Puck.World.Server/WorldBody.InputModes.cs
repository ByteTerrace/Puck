using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    /// <summary>Starts a cross-authority arrival with no held, queued or partially recognized input.</summary>
    internal void ResetArrivalInput() {
        ClearTransientInput();
        ClearTape();
        ActionEdit = default;
        if (m_laneRecency.Read.IndexOfAnyExcept(value: 0UL) >= 0) { m_laneRecency.Write.Clear(); }
        m_channelReadHeld = default;
        m_channelReadComposed = default;
        m_channelReadScalar = default;
        m_engagedIntent = default;
    }

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
        Span<int> recencySources = stackalloc int[ActionLaneCount];

        // The whole section is rebuilt for the new mapping and written through the door once: a channel that keeps
        // its logical identity carries its timer, bit, latch and fact memory to its new ordinal, every other ordinal
        // starts clean, and no pending tap survives the barrier.
        var action = default(BodyActionSection);

        timers.Clear(); timerValues.Clear(); previousBits.Clear();
        continuing.Clear();
        recencySources.Fill(value: -1);
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
            timers[destination] = Action.TimerTicks[source];
            timerValues[destination] = Action.TimerValues[source];
            m_inputScriptActorScratch[destination] = ScriptActors.Timers[source];
            previousBits[destination] = (Action.PreviousBit[source] || (accepted[destination] != FixedQ4816.Zero));
            action.LaneLatch[destination] = Action.LaneLatch[source];
            action.LaneFactHeld[destination] = Action.LaneFactHeld[source];
            recencySources[destination] = source;
        }
        timers.CopyTo(destination: action.TimerTicks);
        timerValues.CopyTo(destination: action.TimerValues);
        SetScriptActors(pending: ScriptActors.Pending.AsSpan(), timers: m_inputScriptActorScratch);
        previousBits.CopyTo(destination: action.PreviousBit);
        ActionEdit = action;
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
        BindInputMapping(epoch: epoch, kit: kit, next: next, recencySources: recencySources);
    }
    /// <summary>Rebinds a restored bank without altering checkpointed levels, timers or edge latches.</summary>
    public void RestoreInputMapping(WorldInputMapping next, FixedWorldKit kit, ulong epoch) {
        Span<int> recencySources = stackalloc int[ActionLaneCount];

        for (var lane = 0; (lane < ActionLaneCount); lane++) { recencySources[lane] = lane; }
        BindInputMapping(epoch: epoch, kit: kit, next: next, recencySources: recencySources);
    }
    // Binds the bank's channels and lanes. A lane's recency clocks come from the lane recencySources names for it
    // under the bindings being replaced: the lane itself for a restored bank, the lane a channel kept its logical
    // identity from for an installed one.
    private void BindInputMapping(WorldInputMapping next, FixedWorldKit kit, ulong epoch, ReadOnlySpan<int> recencySources) {
        SetChannelTable(channels: next.Channels);
        CopyChannelBindings(kit.Actions, kit.ActionThresholds, kit.ActionShapes, kit.RoleMask);
        LayoutLaneRecency(sources: recencySources);
        m_roleOrdinals = kit.RoleOrdinals;
        InputMappingId = next.Id;
        InputEpoch = epoch;
    }
}
