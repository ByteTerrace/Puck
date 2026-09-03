using System.Globalization;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // The action-state portion of WorldRuntimeStateHash's authoritative boundary. Definition order is the compiled
    // register order, so no sort or temporary collection is needed.
    internal void AppendActionStateHash(ref Fnv1aHash hash) {
        hash.Add(value: ((uint)m_actionStateDefinitions.Length));

        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            var definition = m_actionStateDefinitions[slot];

            hash.Add(value: Fnv1aHash.Compute(values: definition.Name.AsSpan()));
            hash.Add(value: ((byte)definition.Kind));
            hash.Add(value: ((byte)definition.Lifetime));
            hash.Add(value: m_actionStateValues[slot].Value);
            hash.Add(value: m_actionStateTimers[slot]);
        }

        hash.Add(value: ((uint)m_laneActions.Length));

        for (var lane = 0; (lane < m_laneActions.Length); lane++) {
            ref var runtime = ref m_laneActions[lane];

            hash.Add(value: runtime.Latch);
            hash.Add(value: runtime.FactHeld);
            hash.Add(value: ((uint)(runtime.Recency?.Length ?? 0)));

            if (runtime.Recency is { } recency) {
                for (var index = 0; (index < recency.Length); index++) {
                    hash.Add(value: recency[index]);
                }
            }
        }
    }
    internal void AppendDurableStateDeclarations(List<(string Name, ActionStateKind Kind)> declarations) {
        foreach (var definition in m_actionStateDefinitions) {
            if (definition.Lifetime == ActionStateLifetime.Durable) {
                declarations.Add(item: (definition.Name, definition.Kind));
            }
        }
    }
    internal string DescribeActionState() {
        if (m_actionStateDefinitions.Length == 0) {
            return "state=none";
        }

        var values = new string[m_actionStateDefinitions.Length];

        for (var slot = 0; (slot < values.Length); slot++) {
            var definition = m_actionStateDefinitions[slot];
            var value = ((definition.Kind == ActionStateKind.Counter)
                ? ((double)m_actionStateValues[slot]).ToString(
                    format: "0.####",
                    provider: CultureInfo.InvariantCulture
                )
                : m_actionStateTimers[slot].ToString(provider: CultureInfo.InvariantCulture)
            );
            var requested = DescribeRaw(
                definition: in definition,
                raw: m_actionStateRequested[slot]
            );
            var envelope = DescribeEnvelope(
                envelope: definition.Envelope,
                kind: definition.Kind
            );

            values[slot] = $"{definition.Name}:{definition.Kind.ToString().ToLowerInvariant()}/{definition.Lifetime.ToString().ToLowerInvariant()} writable={definition.PlayerWritable.ToString().ToLowerInvariant()} envelope={envelope} requested={requested} effective={value} writer={m_actionStateLastWriter[slot]} reason={m_actionStateLastReason[slot]}";
        }

        return string.Join(
            separator: " ",
            values: values
        );
    }
    /// <summary>Clears player-owned durable values to their authored initial values when identity changes.</summary>
    internal void ResetDurableState() {
        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            if (m_actionStateDefinitions[slot].Lifetime == ActionStateLifetime.Durable) {
                m_actionStateValues[slot] = m_actionStateDefinitions[slot].InitialValue;
                m_actionStateTimers[slot] = m_actionStateDefinitions[slot].InitialTicks;
                m_actionStateRequested[slot] = InitialRaw(definition: in m_actionStateDefinitions[slot]);
                m_actionStateLastWriter[slot] = "author";
                m_actionStateLastReason[slot] = "identity reset";
                m_actionStateDirty[slot] = false;
            }
        }
        Array.Clear(array: m_durableInputPresent);
        m_durableInputTick = 0;
    }
    internal void RestoreSubmittedInput(in WorldSubmittedInput input) {
        if (input.HasIntent) {
            SubmitIntent(intent: input.Intent);
        }

        // This is the input-hold runtime replaying its selected historical image, not a new writer publication.
        // Do not clear the authority-handoff bridge here: only ApplyIntentSubmission's SetHeldChannels call proves
        // the destination stream has actually supplied a replacement image (neutral included).
        m_heldChannels = input.HeldChannels;
    }
    internal void TakeDurableStateOutputs(ulong tick, int entityIndex, List<DurableStateOutput> outputs) {
        if (Profile is null) {
            Array.Clear(array: m_actionStateDirty);
            return;
        }

        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            if (
                !m_actionStateDirty[slot] ||
                (m_actionStateDefinitions[slot].Lifetime != ActionStateLifetime.Durable)
            ) {
                continue;
            }

            outputs.Add(item: new DurableStateOutput(
                Tick: tick,
                PlayerId: Profile.Id,
                EntityIndex: entityIndex,
                Value: new DurableStateValue(
                    Name: m_actionStateDefinitions[slot].Name,
                    Value: ((m_actionStateDirtyKind[slot] == WorldDocumentWriteKind.Add)
                ? m_actionStateDirtyOperand[slot]
                : m_actionStateValues[slot]),
                    TimerTicks: m_actionStateTimers[slot]
                ),
                Kind: m_actionStateDirtyKind[slot],
                StorageKind: m_actionStateDefinitions[slot].Kind
            ));
            m_actionStateDirty[slot] = false;
        }
    }
    internal WorldSubmittedInput TakeSubmittedInput() {
        var input = new WorldSubmittedInput(
            HasIntent: m_hasSubmittedIntent,
            HeldChannels: m_heldChannels,
            Intent: m_submittedIntent
        );

        m_submittedIntent = default;
        m_hasSubmittedIntent = false;
        m_heldChannels = default;

        return input;
    }
    /// <summary>Reads one effective durable counter for a visited-world decision.</summary>
    internal bool TryReadDurableCounter(string name, out FixedQ4816 value) {
        var slot = FindActionState(name: name);

        if (
            (slot < 0) ||
            (m_actionStateDefinitions[slot].Kind != ActionStateKind.Counter) ||
            (m_actionStateDefinitions[slot].Lifetime != ActionStateLifetime.Durable)
        ) {
            value = default;
            return false;
        }
        value = m_actionStateValues[slot];
        return true;
    }
    /// <summary>Stages durable values for one explicit simulation tick. Repeated inputs in that tick compose by
    /// submission order; the last value for a slot wins.</summary>
    internal bool TryStageDurableState(ulong tick, IReadOnlyList<DurableStateValue> values, bool requirePlayerWritable, string writer, out string reason) {
        if (Profile is null) {
            reason = "the body has no player identity";
            return false;
        }

        foreach (var value in values) {
            var slot = FindActionState(name: value.Name);

            if (
                (slot < 0) ||
                (m_actionStateDefinitions[slot].Lifetime != ActionStateLifetime.Durable)
            ) {
                reason = $"state '{value.Name}' names no durable slot";
                return false;
            }
            if (
                requirePlayerWritable &&
                !m_actionStateDefinitions[slot].PlayerWritable
            ) {
                reason = $"state '{value.Name}' is not player-writable";
                return false;
            }
            if (
                ((m_actionStateDefinitions[slot].Kind == ActionStateKind.Counter) && (value.TimerTicks != 0)) ||
                ((m_actionStateDefinitions[slot].Kind == ActionStateKind.Timer) && (value.Value != FixedQ4816.Zero))
            ) {
                reason = $"state '{value.Name}' carries the wrong value kind";
                return false;
            }
            var raw = Raw(
                value: value,
                kind: m_actionStateDefinitions[slot].Kind
            );

            if (
                requirePlayerWritable &&
                (m_actionStateDefinitions[slot].Envelope is { } envelope) &&
                !envelope.Contains(value: raw)
            ) {
                reason = $"state '{value.Name}' value lies outside the authored envelope";
                return false;
            }
        }

        if (
            (m_durableInputTick != 0) &&
            (m_durableInputTick != tick)
        ) {
            Array.Clear(array: m_durableInputPresent);
        }
        m_durableInputTick = tick;

        foreach (var value in values) {
            var slot = FindActionState(name: value.Name);

            m_durableInputPresent[slot] = true;
            m_durableInputValues[slot] = value.Value;
            m_durableInputTimers[slot] = value.TimerTicks;
            m_durableInputWriters[slot] = writer;
        }

        reason = string.Empty;
        return true;
    }

    private void AdvanceActionState(ulong stepTicks) {
        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            var definition = m_actionStateDefinitions[slot];

            if (
                (definition.ResetFact is { } reset) &&
                FactHolds(fact: reset)
            ) {
                m_actionStateValues[slot] = definition.InitialValue;
                m_actionStateTimers[slot] = definition.InitialTicks;
            } else if (definition.Kind == ActionStateKind.Timer) {
                var previous = m_actionStateTimers[slot];

                m_actionStateTimers[slot] = SubtractSaturating(
                    amount: stepTicks,
                    value: previous
                );
                if (m_actionStateTimers[slot] != previous) {
                    MarkDurableDirty(slot: slot);
                }
            }
        }
    }
    private void ApplyDurableInput(ulong tick) {
        if (m_durableInputTick == 0) {
            return;
        }
        if (m_durableInputTick != tick) {
            if (m_durableInputTick < tick) {
                Array.Clear(array: m_durableInputPresent);
                m_durableInputTick = 0;
            }
            return;
        }

        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            if (!m_durableInputPresent[slot]) {
                continue;
            }
            var definition = m_actionStateDefinitions[slot];
            var requested = ((definition.Kind == ActionStateKind.Counter)
                ? m_durableInputValues[slot].Value
                : checked((long)m_durableInputTimers[slot])
            );

            ApplyRawState(
                slot: slot,
                requested: requested,
                writer: m_durableInputWriters[slot],
                reason: "tick-stamped durable input"
            );
        }
        Array.Clear(array: m_durableInputPresent);
        m_durableInputTick = 0;
    }
    private void ApplyRawState(int slot, long requested, string writer, string reason) {
        var definition = m_actionStateDefinitions[slot];
        var effective = (definition.Envelope?.Clamp(
            value: requested,
            initial: InitialRaw(definition: in definition)
        ) ?? requested);

        m_actionStateRequested[slot] = requested;
        if (definition.Kind == ActionStateKind.Counter) {
            m_actionStateValues[slot] = FixedQ4816.FromRawBits(value: effective);
        } else {
            m_actionStateTimers[slot] = checked((ulong)Math.Max(
                val1: 0L,
                val2: effective
            ));
        }
        m_actionStateLastWriter[slot] = writer;
        m_actionStateLastReason[slot] = ((effective == requested)
            ? reason
            : $"{reason}; clamped by visited world"
        );
    }
    // The kit's own held speed multiplier — a boost/sprint under the shared speed.held name, resolved once at
    // kit-compile time (FixedSpeed.HeldOrdinal), applied AFTER any envelope clamp on baseSpeed.
    private FixedQ4816 ApplySpeedHeld(FixedQ4816 baseSpeed, in PlayerIntent intent) => (((m_tuning.Speed.HeldOrdinal >= 0) && (intent[m_tuning.Speed.HeldOrdinal] >= m_channelThresholds[m_tuning.Speed.HeldOrdinal]))
        ? (baseSpeed * m_tuning.Speed.HeldMultiplier)
        : baseSpeed
    );
    private PlayerIntent ClampRole(PlayerIntent intent, ChannelRole role) {
        var ordinal = m_roleOrdinals[role];

        return ((ordinal >= 0)
            ? intent.WithChannel(
                ordinal: ordinal,
                value: FixedQ4816.Clamp(
                    value: intent[ordinal],
                    minimum: NegativeOne,
                    maximum: FixedQ4816.One
                )
            )
            : intent
        );
    }
    // The shared stick-range clamp both one-tick images pass through — the six movement-role ordinals only;
    // composition ordinals are validated {0, One} at their own doors (the affordance gate, the pump) and pass through
    // unchanged here. [-One, One] is safe here because every role channel IS bipolar by validator rule
    // (WorldDefinitionValidator.ValidateChannels refuses any other declared shape on a role channel).
    private PlayerIntent Clamped(in PlayerIntent intent) {
        var result = intent;

        result = ClampRole(
            intent: result,
            role: ChannelRole.MoveAdvance
        );
        result = ClampRole(
            intent: result,
            role: ChannelRole.MoveStrafe
        );
        result = ClampRole(
            intent: result,
            role: ChannelRole.Turn
        );
        result = ClampRole(
            intent: result,
            role: ChannelRole.MoveUp
        );
        result = ClampRole(
            intent: result,
            role: ChannelRole.Pitch
        );
        result = ClampRole(
            intent: result,
            role: ChannelRole.Roll
        );

        return result;
    }
    // Drop the staged one-tick input images (the submitted, producer, and held-channel images) — the source/engagement
    // transition hygiene. The tape and any timed channel press are left running — deliberate for a source/engagement
    // switch (the hold belongs to whichever target now owns the intent), but NOT for Stop, which clears timed presses
    // itself right after calling this (see Stop's own remarks).
    private void ClearTransientInput() {
        m_submittedIntent = default;
        m_hasSubmittedIntent = false;
        m_producerIntent = default;
        m_hasProducerIntent = false;
        m_heldChannels = default;
        m_transferHeldChannels = default;
        m_hasTransferHeldChannels = false;
    }
    private void CompileActionState(CompiledActionStateSlot[]? state) {
        var previousDefinitions = m_actionStateDefinitions;
        var previousValues = m_actionStateValues;
        var previousTimers = m_actionStateTimers;
        var previousRequested = m_actionStateRequested;
        var previousWriters = m_actionStateLastWriter;
        var previousReasons = m_actionStateLastReason;

        m_actionStateDefinitions = ((state is null)
            ? []
            : [.. state]
        );
        m_actionStateValues = new FixedQ4816[m_actionStateDefinitions.Length];
        m_actionStateTimers = new ulong[m_actionStateDefinitions.Length];
        m_actionStateRequested = new long[m_actionStateDefinitions.Length];
        m_actionStateLastWriter = new string[m_actionStateDefinitions.Length];
        m_actionStateLastReason = new string[m_actionStateDefinitions.Length];
        m_actionStateDirty = new bool[m_actionStateDefinitions.Length];
        m_actionStateDirtyKind = new WorldDocumentWriteKind[m_actionStateDefinitions.Length];
        m_actionStateDirtyOperand = new FixedQ4816[m_actionStateDefinitions.Length];
        m_durableInputPresent = new bool[m_actionStateDefinitions.Length];
        m_durableInputValues = new FixedQ4816[m_actionStateDefinitions.Length];
        m_durableInputTimers = new ulong[m_actionStateDefinitions.Length];
        m_durableInputWriters = new string[m_actionStateDefinitions.Length];
        m_durableInputTick = 0;

        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            var definition = m_actionStateDefinitions[slot];
            var preserved = -1;

            if (definition.Lifetime == ActionStateLifetime.Durable) {
                for (var prior = 0; (prior < previousDefinitions.Length); prior++) {
                    if (
                        (previousDefinitions[prior].Lifetime == ActionStateLifetime.Durable) &&
                        (previousDefinitions[prior].Kind == definition.Kind) &&
                        string.Equals(
                        a: previousDefinitions[prior].Name,
                        b: definition.Name,
                        comparisonType: StringComparison.Ordinal
                    )
                    ) {
                        preserved = prior;
                        break;
                    }
                }
            }

            m_actionStateValues[slot] = ((preserved >= 0)
                ? previousValues[preserved]
                : definition.InitialValue
            );
            m_actionStateTimers[slot] = ((preserved >= 0)
                ? previousTimers[preserved]
                : definition.InitialTicks
            );
            m_actionStateRequested[slot] = ((preserved >= 0)
                ? previousRequested[preserved]
                : InitialRaw(definition: in definition)
            );
            m_actionStateLastWriter[slot] = ((preserved >= 0)
                ? previousWriters[preserved]
                : "author"
            );
            m_actionStateLastReason[slot] = ((preserved >= 0)
                ? previousReasons[preserved]
                : "initial value"
            );
        }
    }
    private static string DescribeEnvelope(CompiledActionStateEnvelope? envelope, ActionStateKind kind) {
        if (envelope is null) {
            return "none";
        }
        string Describe(long raw) => ((kind == ActionStateKind.Counter)
            ? ((double)FixedQ4816.FromRawBits(value: raw)).ToString(
                format: "0.####",
                provider: CultureInfo.InvariantCulture
            )
            : raw.ToString(provider: CultureInfo.InvariantCulture)
        );
        return ((envelope.Values is { } values)
            ? $"set({string.Join(
                separator: ',',
                values.Select(selector: Describe)
            )})"
            : $"range({Describe(raw: envelope.Minimum)}..{Describe(raw: envelope.Maximum)})"
        );
    }
    private static string DescribeRaw(in CompiledActionStateSlot definition, long raw) => ((definition.Kind == ActionStateKind.Counter)
        ? ((double)FixedQ4816.FromRawBits(value: raw)).ToString(
            format: "0.####",
            provider: CultureInfo.InvariantCulture
        )
        : raw.ToString(provider: CultureInfo.InvariantCulture)
    );
    private bool FactHolds(ActionFact fact) {
        return fact switch {
            ActionFact.Grounded => m_grounded,
            ActionFact.Airborne => !m_grounded,
            ActionFact.Rising => (m_verticalVelocity > FixedQ4816.Zero),
            ActionFact.Falling => (m_verticalVelocity < FixedQ4816.Zero),
            ActionFact.Submerged => m_submerged,
            ActionFact.AtSurface => m_atSurface,
            ActionFact.Climbing => HoldsUnwalkableSurface(),
            ActionFact.Flying => HoldsFree(),
            _ => (m_affectingSubject >= 0),
        };
    }
    private int FindActionState(string name) {
        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            if (string.Equals(
                a: m_actionStateDefinitions[slot].Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return slot;
            }
        }
        return -1;
    }
    private bool GateOpen(CompiledPredicate[] gate, in LaneActionRuntime state) {
        if (gate.Length == 0) {
            return true;
        }

        Span<bool> stack = stackalloc bool[CompiledPredicateCapacity.MaxTokens];
        var top = 0;

        foreach (var predicate in gate) {
            if (predicate.Kind == CompiledPredicateKind.Not) {
                stack[top - 1] = !stack[top - 1];
                continue;
            }
            if (predicate.Kind is CompiledPredicateKind.All or CompiledPredicateKind.Any) {
                var start = (top - predicate.Arity);
                var holdsGroup = (predicate.Kind == CompiledPredicateKind.All);

                for (var index = start; index < top; index++) {
                    holdsGroup = ((predicate.Kind == CompiledPredicateKind.All)
                        ? (holdsGroup && stack[index])
                        : (holdsGroup || stack[index]));
                }

                top = start;
                stack[top++] = holdsGroup;
                continue;
            }

            var holds = predicate.Kind switch {
                CompiledPredicateKind.Now => FactHolds(fact: predicate.Fact),
                CompiledPredicateKind.Recently => (state.Recency![predicate.RecencySlot] > 0),
                CompiledPredicateKind.CompareState => predicate.Comparison.Holds(
                value: m_actionStateValues[predicate.StateSlot],
                expected: predicate.Value
            ),
                _ => (m_actionStateTimers[predicate.StateSlot] == 0),
            };

            stack[top++] = holds;
        }

        return ((top == 1) && stack[0]);
    }
    private static long InitialRaw(in CompiledActionStateSlot definition) => ((definition.Kind == ActionStateKind.Counter)
        ? definition.InitialValue.Value
        : checked((long)definition.InitialTicks)
    );
    private void MarkDurableDirty(int slot, WorldDocumentWriteKind kind = WorldDocumentWriteKind.Set, FixedQ4816 operand = default) {
        if (
            (slot >= 0) &&
            (m_actionStateDefinitions[slot].Lifetime == ActionStateLifetime.Durable)
        ) {
            if (
                m_actionStateDirty[slot] &&
                (m_actionStateDirtyKind[slot] != WorldDocumentWriteKind.Add)
            ) {
                kind = WorldDocumentWriteKind.Set;
            } else if (
                m_actionStateDirty[slot] &&
                (kind == WorldDocumentWriteKind.Add)
            ) {
                operand += m_actionStateDirtyOperand[slot];
            }
            m_actionStateDirty[slot] = true;
            m_actionStateDirtyKind[slot] = kind;
            m_actionStateDirtyOperand[slot] = operand;
        }
    }
    // Resolve argument-less channel taps at the only boundary that knows the host's actual fixed-step period. A
    // pending tap merges into the lane timer through the SAME MergeLaneTimer PressChannel's timed overload uses —
    // one merge rule for both press paths, never two that can quietly drift apart.
    private void MaterializeDefaultLanePresses(ulong stepTicks) {
        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (!m_pendingDefaultChannelPress[ordinal]) {
                continue;
            }

            MergeLaneTimer(
                ordinal: ordinal,
                value: m_pendingDefaultChannelValue[ordinal],
                holdTicks: stepTicks
            );
            m_pendingDefaultChannelPress[ordinal] = false;
        }
    }
    // Merges (ordinal, value, holdTicks) into the lane-timer slot: a same-value re-press only extends an in-flight
    // hold (the longer of the two durations wins — repeatedly resubmitting one held key must not truncate itself);
    // a DIFFERENT value is a distinct action and replaces the hold outright — its own duration, not merged with
    // whatever ticks remained. Shared by PressChannel's timed overload and MaterializeDefaultLanePresses so the
    // wire-timed and host-step-tap press paths can never drift onto two different merge rules.
    private void MergeLaneTimer(int ordinal, FixedQ4816 value, ulong holdTicks) {
        var isSameValueRepress = ((m_laneTimers[ordinal] > 0) && (m_channelTimerValues[ordinal] == value));

        if (isSameValueRepress) {
            m_laneTimers[ordinal] = Math.Max(
                val1: m_laneTimers[ordinal],
                val2: holdTicks
            );
        } else {
            m_channelTimerValues[ordinal] = value;
            m_laneTimers[ordinal] = holdTicks;
        }
    }
    // A shaping-row gate: a postfix Boolean program of body-fact predicates plus 'held' (a live channel-threshold
    // read — the validator admits no other action-state predicate on a shaping gate).
    private bool MotionGateOpen(CompiledPredicate[] gate, in PlayerIntent intent) {
        if (gate.Length == 0) {
            return true;
        }

        Span<bool> stack = stackalloc bool[CompiledPredicateCapacity.MaxTokens];
        var top = 0;

        foreach (var predicate in gate) {
            if (predicate.Kind == CompiledPredicateKind.Not) {
                stack[top - 1] = !stack[top - 1];
                continue;
            }
            if (predicate.Kind is CompiledPredicateKind.All or CompiledPredicateKind.Any) {
                var start = (top - predicate.Arity);
                var holdsGroup = (predicate.Kind == CompiledPredicateKind.All);

                for (var index = start; index < top; index++) {
                    holdsGroup = ((predicate.Kind == CompiledPredicateKind.All)
                        ? (holdsGroup && stack[index])
                        : (holdsGroup || stack[index]));
                }

                top = start;
                stack[top++] = holdsGroup;
                continue;
            }

            var holds = predicate.Kind switch {
                CompiledPredicateKind.Now => FactHolds(fact: predicate.Fact),
                CompiledPredicateKind.Recently => (m_motionRecency[predicate.RecencySlot] > 0),
                CompiledPredicateKind.Held => (intent[predicate.ChannelOrdinal] >= m_channelThresholds[predicate.ChannelOrdinal]),
                _ => false,
            };

            stack[top++] = holds;
        }

        return ((top == 1) && stack[0]);
    }
    // The first shaping row whose gate opens, or -1 when none does (an unmatched tick, or an empty/absent table).
    // ExecuteProgram calls this exactly once after refreshing recency clocks and stores the result in its scratch,
    // so turn, ShapeVelocity, and ApplyMedium share one pre-operation fact/channel snapshot for the whole tick.
    private int ResolveGoverningShapingRow(in PlayerIntent intent) {
        var shaping = m_tuning.Shaping;

        for (var index = 0; (index < shaping.Length); index++) {
            if (MotionGateOpen(
                gate: shaping[index].When,
                intent: in intent
            )) {
                return index;
            }
        }

        return -1;
    }
    // The per-tick action machinery: for each ordinal carrying a compiled binding, derive its edge (the folded value
    // crossing the channel's threshold against the previous sub-step — never carried), refresh the recency clocks (a
    // Recently window refills while its fact holds and decays otherwise), advance named state, latch a press edge, then
    // fire the press trigger while its latch is pending and its gate holds, and the release trigger on
    // its edge — each fire applying its compiled effects in order and consuming the latch. Runs after
    // attitude/planar integration and before gravity/vertical resolution, so effects shape the same tick.
    private void ProcessLaneActions(ref BodyMotionScratch scratch) {
        AdvanceActionState(stepTicks: scratch.StepTicks);

        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (m_laneBindings[ordinal] is not { } binding) {
                continue;
            }

            ref var state = ref m_laneActions[ordinal];
            var bit = (scratch.Intent[ordinal] >= m_channelThresholds[ordinal]);
            var pressed = (bit && !m_previousChannelBit[ordinal]);
            var released = (!bit && m_previousChannelBit[ordinal]);

            for (var slot = 0; (slot < binding.RecencyFacts.Length); slot++) {
                state.Recency![slot] = (FactHolds(fact: binding.RecencyFacts[slot])
                    ? binding.RecencyWindows[slot]
                    : SubtractSaturating(
                        value: state.Recency[slot],
                        amount: scratch.StepTicks
                    )
                );
            }

            if (binding.OnPress is { } press) {
                state.Latch = (pressed
                    ? press.LatchTicks
                    : SubtractSaturating(
                        amount: scratch.StepTicks,
                        value: state.Latch
                    )
                );

                // LatchSeconds 0 means THIS TICK ONLY, which is what the field always documented. Demanding a
                // strictly positive latch made zero structurally dead — a zero-latch press could never fire, however
                // open its gate was. The press is pending when its latch is still running OR when this very tick is
                // its edge; consuming it still clears the latch.
                if (
                    (pressed || (state.Latch > 0)) &&
                    GateOpen(
                    gate: press.Gate,
                    state: in state
                )
                ) {
                    ApplyEffects(
                        effects: press.Effects,
                        scratch: ref scratch
                    );
                    state.Latch = 0;
                }
            }

            if (
                released &&
                (binding.OnRelease is { } release) &&
                GateOpen(
                gate: release.Gate,
                state: in state
            )
            ) {
                ApplyEffects(
                    effects: release.Effects,
                    scratch: ref scratch
                );
            }

            for (var rule = 0; (rule < binding.OnFact.Length); rule++) {
                var trigger = binding.OnFact[rule];
                var holds = (FactHolds(fact: trigger.Fact) && GateOpen(
                    gate: trigger.Gate,
                    state: in state
                ));
                var wasHeld = ((state.FactHeld & (1UL << rule)) != 0UL);

                state.FactHeld = (holds
                    ? state.FactHeld | (1UL << rule)
                    : state.FactHeld & ~(1UL << rule)
                );

                // ONE edge vocabulary, the same ActionTriggerMode a world rule reads: EDGE fires on the crossing
                // alone and re-arms when the condition (fact AND gate together) stops holding; LEVEL fires every
                // tick it holds, which is what every fact trigger did before the mode existed.
                if (
                    holds &&
                    !((trigger.Mode == ActionTriggerMode.Edge) && wasHeld)
                ) {
                    ApplyEffects(
                        effects: trigger.Effects,
                        scratch: ref scratch
                    );
                }
            }
        }
    }
    private static long Raw(DurableStateValue value, ActionStateKind kind) => ((kind == ActionStateKind.Counter)
        ? value.Value.Value
        : checked((long)value.TimerTicks)
    );
    private FixedQ4816 Role(in PlayerIntent intent, ChannelRole role) => m_roleOrdinals.Read(
        intent: in intent,
        role: role
    );

    /// <summary>Reads one declared action-state slot without changing it.</summary>
    /// <param name="name">The slot name.</param>
    /// <param name="kind">The storage kind.</param>
    /// <param name="lifetime">The declared lifetime.</param>
    /// <param name="playerWritable">Whether the identity may submit a value.</param>
    /// <param name="value">The counter value.</param>
    /// <param name="timerTicks">The timer remainder.</param>
    /// <returns>Whether the slot exists.</returns>
    public bool TryDescribeActionState(string name, out ActionStateKind kind, out ActionStateLifetime lifetime, out bool playerWritable, out FixedQ4816 value, out ulong timerTicks) {
        var slot = FindActionState(name: name);

        if (slot < 0) {
            kind = default;
            lifetime = default;
            playerWritable = false;
            value = default;
            timerTicks = 0;
            return false;
        }

        kind = m_actionStateDefinitions[slot].Kind;
        lifetime = m_actionStateDefinitions[slot].Lifetime;
        playerWritable = m_actionStateDefinitions[slot].PlayerWritable;
        value = m_actionStateValues[slot];
        timerTicks = m_actionStateTimers[slot];
        return true;
    }

    private struct LaneActionRuntime {
        public ulong Latch;
        // One bit per OnFact trigger of this lane's binding, recording whether its condition (fact AND gate) held on
        // the previous evaluation — the edge latch. A lane's OnFact list is bounded by the same authored-effects
        // budget everything else here is; a 64-bit word is the same shape every other mask in this engine uses.
        public ulong FactHeld;
        public ulong[]? Recency;
    }
}
