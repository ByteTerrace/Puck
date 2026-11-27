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
        InputReadbacksEdit.ReadHeld = default;
        InputReadbacksEdit.ReadComposed = default;
        InputReadbacksEdit.ReadScalar = default;
        InputReadbacksEdit.EngagedIntent = default;
    }

    /// <summary>The input tuple currently installed on this body's folded intent.</summary>
    public ushort InputMappingId => InputImages.Mapping;
    /// <summary>The latest installed owner epoch; an address never carries authority without this boundary.</summary>
    public ulong InputEpoch => InputImages.Epoch;

    /// <summary>Installs a committed input barrier by logical identity, clearing released edges and timed input.</summary>
    /// <remarks>Every image the body holds by channel ordinal is addressed under the installed mapping once this
    /// returns. The submitted and held images are rebuilt from <paramref name="accepted"/>; the tape's segments, the
    /// producer's staged image and <paramref name="steering"/> cross by logical identity (<c>CarryAcross</c>), so a
    /// step that resolves its intent after the barrier never reads the replaced mapping's ordinals as the new one's.
    /// This is the one door that changes the mapping under live images; a restore rebinds the bank its images were
    /// captured with (<see cref="RestoreInputMapping"/>).</remarks>
    /// <param name="catalog">The input catalog that says which logical channels keep their identity.</param>
    /// <param name="previous">The mapping being replaced.</param>
    /// <param name="next">The mapping to install.</param>
    /// <param name="kit">The body's kit as compiled for <paramref name="next"/>.</param>
    /// <param name="accepted">The owner's accepted image, addressed under <paramref name="next"/>.</param>
    /// <param name="epoch">The owner's epoch at the barrier.</param>
    /// <param name="reset">Whether no channel continues across the barrier.</param>
    /// <param name="decisions">The barrier's continuation decision for each logical channel it judged.</param>
    /// <param name="steering">The steering image the population keeps for the body between producer runs, addressed as
    /// the producer's staged image is; re-addressed in place.</param>
    /// <param name="recompiled">Whether the mapping was recompiled, so every surviving logical channel keeps its identity.</param>
    public void InstallInputMapping(WorldInputCatalog catalog, WorldInputMapping previous, WorldInputMapping next,
        FixedWorldKit kit, PlayerIntent accepted, ulong epoch, bool reset, ReadOnlySpan<WorldInputContinuation> decisions, ref PlayerIntent steering, bool recompiled = false) {
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
        var carried = next.Channels.RemapTo(target: previous.Channels);

        foreach (var logical in next.Live) {
            var destination = next.Address(logicalId: logical);

            if ((destination < 0) || reset || (!recompiled && !catalog.Keeps(from: previous, logicalId: logical, to: next)) ||
                (carried[destination] < 0)) { continue; }
            var source = carried[destination];
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
        InputImagesEdit.Submitted = submitted;
        InputImagesEdit.Held = held;
        InputReadbacksEdit.ReadComposed = accepted;
        InputReadbacksEdit.ReadHeld = held;
        InputReadbacksEdit.ReadScalar = accepted;
        InputReadbacksEdit.EngagedIntent = accepted;
        InputImagesEdit.TransferHeld = default;
        InputImagesEdit.HasTransferHeld = false;
        for (var offset = 0; (offset < m_tapeCount); offset++) {
            ref var segment = ref m_tape[((m_tapeHead + offset) % m_tape.Length)];

            segment.Intent = CarryAcross(carried: carried, continuing: continuing, image: in segment.Intent, next: next, reset: reset);
        }
        if (InputImages.HasProducer) {
            var staged = InputImages.Producer;
            var producer = CarryAcross(carried: carried, continuing: continuing, image: in staged, next: next, reset: reset);

            if (staged != producer) { InputImagesEdit.Producer = producer; }
        }
        steering = CarryAcross(carried: carried, continuing: continuing, image: in steering, next: next, reset: reset);
        BindInputMapping(epoch: epoch, kit: kit, next: next, recencySources: recencySources);
    }
    // Carries an image addressed under the mapping being replaced across the barrier: a channel that continues takes
    // its value to its new ordinal, and every other ordinal reads zero.
    private static PlayerIntent CarryAcross(in PlayerIntent image, WorldInputMapping next, ReadOnlySpan<bool> continuing, ReadOnlySpan<int> carried, bool reset) {
        var across = default(PlayerIntent);

        if (!reset) {
            foreach (var logical in next.Live) {
                var destination = next.Address(logicalId: logical);

                if ((destination >= 0) && continuing[destination]) {
                    across = across.WithChannel(ordinal: destination, value: image[carried[destination]]);
                }
            }
        }
        return across;
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
        InputImagesEdit.Mapping = next.Id;
        InputImagesEdit.Epoch = epoch;
    }
}
