using System.Collections.Immutable;
using System.Globalization;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    /// <summary>Binds the arena slot lanes this body's named action state is stored in, at its entity index.</summary>
    /// <param name="lane">The server's slot-lane binding.</param>
    /// <param name="ordinal">This body's entity index.</param>
    /// <remarks>Binding is an address, not an admission: the ordinal is admitted and born by the entity table's own
    /// occupancy flag (<c>WorldPopulation.Entry.Active</c>), so a body reads and writes nothing until its slot is
    /// marked occupied.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="lane"/> is <see langword="null"/>.</exception>
    public void BindActionStateLane(WorldActionStateLane lane, int ordinal) {
        ArgumentNullException.ThrowIfNull(argument: lane);

        m_stateLane = lane;
        m_stateOrdinal = ordinal;
    }

    // The action-state portion of WorldStateHashComposition's authoritative boundary: the declaration and the
    // per-lane trigger runtime. The stored values are the arena's own, folded by the Arena component.
    internal void AppendActionStateHash(ref Fnv1aHash hash) {
        hash.Add(value: ((uint)m_actionStateDefinitions.Length));

        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            var definition = m_actionStateDefinitions[slot];

            hash.Add(value: Fnv1aHash.Compute(values: definition.Name.AsSpan()));
            hash.Add(value: ((byte)definition.Kind));
            hash.Add(value: ((byte)definition.Lifetime));
        }

        hash.Add(value: ((uint)ActionLaneCount));

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            hash.Add(value: Action.LaneLatch[lane]);
            hash.Add(value: Action.LaneFactHeld[lane]);
            var recency = LaneRecency(lane: lane);

            hash.Add(value: ((uint)recency.Length));
            for (var index = 0; (index < recency.Length); index++) {
                hash.Add(value: recency[index]);
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
                ? ((double)StateCounter(slot: slot)).ToString(
                    format: "0.####",
                    provider: CultureInfo.InvariantCulture
                )
                : StateTimer(slot: slot).ToString(provider: CultureInfo.InvariantCulture)
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
                WriteStateRaw(
                    raw: InitialRaw(definition: in m_actionStateDefinitions[slot]),
                    slot: slot
                );
                m_actionStateRequested[slot] = InitialRaw(definition: in m_actionStateDefinitions[slot]);
                m_actionStateLastWriter[slot] = "author";
                m_actionStateLastReason[slot] = "identity reset";
                ClearRegisterWrite(slot: slot);
            }
        }
        ClearDurableInput();
    }
    internal void RestoreSubmittedInput(in WorldSubmittedInput input) {
        if (input.HasIntent) {
            SubmitIntent(intent: input.Intent);
        }

        // This is the input-hold runtime replaying its selected historical image, not a new writer publication.
        // Keep restored fold residue until SetHeldChannels supplies a replacement image (neutral included).
        InputImagesEdit.Held = input.HeldChannels;
    }
    internal void TakeDurableStateOutputs(ulong tick, int entityIndex, List<DurableStateOutput> outputs) {
        // A register write is marked through the holder's write side, which moves its version: while the version
        // stands as the last drain left it, no slot is marked and the drain has nothing to read.
        if (m_registerWrites.Version == m_registerWritesDrained) {
            return;
        }
        if (Profile is null) {
            for (var slot = 0; (slot < m_registerWrites.Count); slot++) { ClearRegisterWrite(slot: slot); }
            m_registerWritesDrained = m_registerWrites.Version;
            return;
        }

        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            var staged = m_registerWrites.Read[slot];

            if (
                !staged.Dirty ||
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
                    Value: ((staged.Kind == WorldDocumentWriteKind.Add)
                ? staged.Operand
                : StateCounter(slot: slot)),
                    TimerTicks: StateTimer(slot: slot)
                ),
                Kind: staged.Kind,
                StorageKind: m_actionStateDefinitions[slot].Kind
            ));
            ClearRegisterWrite(slot: slot);
        }
        m_registerWritesDrained = m_registerWrites.Version;
    }
    // The version of the register writes' holder as the last drain left it (TakeDurableStateOutputs): a memo of this
    // process, never state. Zero is no holder's version, so a body drains at least once, and new register definitions
    // resize the holder, which moves its version.
    private ulong m_registerWritesDrained;

    public WorldSubmittedInput TakeSubmittedInput() {
        var input = new WorldSubmittedInput(
            HasIntent: InputImages.HasSubmitted,
            HeldChannels: InputImages.Held,
            Intent: InputImages.Submitted
        );

        InputImagesEdit.Submitted = default;
        InputImagesEdit.HasSubmitted = false;
        InputImagesEdit.Held = default;

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
        value = StateCounter(slot: slot);
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

        // The table is immutable behind its door: the staged rows are rebuilt and written once.
        var rows = DurableInput.Rows.AsSpan().ToArray();

        if (
            (DurableInput.Tick != 0) &&
            (DurableInput.Tick != tick)
        ) {
            for (var slot = 0; (slot < rows.Length); slot++) { rows[slot] = (rows[slot] with { Present = false }); }
        }

        foreach (var value in values) {
            rows[FindActionState(name: value.Name)] = new(Present: true, Timer: value.TimerTicks, Value: value.Value, Writer: writer);
        }
        m_durableInput.Write = new(Rows: ImmutableArray.Create(items: rows), Tick: tick);

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
                var initial = InitialRaw(definition: in definition);

                // A reset already at its value changes no selector. Avoid opening a speculative
                // write and synchronizing every input owner for the same value on every tick.
                if ((definition.Kind == ActionStateKind.Counter) && (StateCounter(slot: slot).Value == initial)) {
                    continue;
                }
                if ((definition.Kind == ActionStateKind.Counter) && (m_stateLane?.Input is { } input) &&
                    !input.TryValidateReset(body: m_stateOrdinal, raw: initial, reason: out var reason, slot: slot)) {
                    m_actionStateRequested[slot] = initial;
                    m_actionStateLastWriter[slot] = "kit-reset";
                    m_actionStateLastReason[slot] = reason;
                    continue;
                }
                WriteStateRaw(
                    raw: initial,
                    slot: slot
                );
                if (definition.Kind == ActionStateKind.Counter) { m_stateLane?.Input?.CommitKitEffects(m_stateOrdinal); }
            } else if (definition.Kind == ActionStateKind.Timer) {
                var previous = StateTimer(slot: slot);
                var next = previous.SubtractSaturating(right: stepTicks);

                if (next != previous) {
                    WriteStateRaw(
                        raw: unchecked((long)next),
                        slot: slot
                    );
                    MarkDurableDirty(slot: slot);
                }
            }
        }
    }
    private void ApplyDurableInput(ulong tick) {
        if (DurableInput.Tick == 0) {
            return;
        }
        if (DurableInput.Tick != tick) {
            if (DurableInput.Tick < tick) {
                ClearDurableInput();
            }
            return;
        }

        // The staged table is immutable, so the rows read here are the staged ones whatever a register write does.
        var rows = DurableInput.Rows;

        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            if (!rows[slot].Present) {
                continue;
            }
            var definition = m_actionStateDefinitions[slot];
            var requested = ((definition.Kind == ActionStateKind.Counter)
                ? rows[slot].Value.Value
                : checked((long)rows[slot].Timer)
            );

            ApplyRawState(
                slot: slot,
                requested: requested,
                writer: rows[slot].Writer,
                reason: "tick-stamped durable input"
            );
        }
        ClearDurableInput();
    }
    // Drops the staged incoming write: no row is present and no tick is targeted. A row keeps the value, timer and
    // writer it was staged with, which the section carries.
    private void ClearDurableInput() {
        var rows = DurableInput.Rows.AsSpan();
        var present = false;

        for (var slot = 0; (slot < rows.Length); slot++) { present |= rows[slot].Present; }
        if (!present) {
            if (DurableInput.Tick != 0) { m_durableInput.Write = (DurableInput with { Tick = 0UL }); }
            return;
        }

        var cleared = rows.ToArray();

        for (var slot = 0; (slot < cleared.Length); slot++) { cleared[slot] = (cleared[slot] with { Present = false }); }
        m_durableInput.Write = new(Rows: ImmutableArray.Create(items: cleared), Tick: 0UL);
    }
    // Drops a register's staged durable write. The staged kind and operand stay as they were: the section carries them.
    private void ClearRegisterWrite(int slot) {
        if (m_registerWrites.Read[slot].Dirty) { m_registerWrites.Write[slot] = (m_registerWrites.Read[slot] with { Dirty = false }); }
    }

    private ref readonly BodyDurableInputSection DurableInput => ref m_durableInput.Read;

    /// <summary>Writes one register a world rule's <c>setBodyState</c> addresses, settled through the register's
    /// envelope as a kit action's write is. The lane write joins the firing's journal scope, so a rewound firing
    /// rewinds it.</summary>
    /// <param name="slot">The register's slot in the world's register file.</param>
    /// <param name="requested">The requested value, in raw counter bits.</param>
    /// <param name="rule">The firing effect's authored spelling, for the read-back.</param>
    internal void ApplyRuleState(int slot, long requested, string rule) => ApplyRawState(
        reason: rule,
        requested: requested,
        slot: slot,
        writer: "rule"
    );
    /// <summary>Writes one register a crossing through a scaled portal pair addresses: the body's size, which the caller
    /// has already checked against the register's envelope.</summary>
    /// <param name="slot">The register's slot in the world's register file.</param>
    /// <param name="requested">The new value, in raw counter bits.</param>
    internal void ApplyCrossingState(int slot, long requested) => ApplyRawState(
        reason: "scaled portal crossing",
        requested: requested,
        slot: slot,
        writer: "crossing"
    );

    private void ApplyRawState(int slot, long requested, string writer, string reason) {
        var definition = m_actionStateDefinitions[slot];
        var effective = (definition.Envelope?.Clamp(
            value: requested,
            initial: InitialRaw(definition: in definition)
        ) ?? requested);

        m_actionStateRequested[slot] = requested;
        WriteStateRaw(
            raw: ((definition.Kind == ActionStateKind.Counter)
            ? effective
            : Math.Max(
                val1: 0L,
                val2: effective
            )),
            slot: slot
        );
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
        InputImagesEdit.Submitted = default;
        InputImagesEdit.HasSubmitted = false;
        InputImagesEdit.Producer = default;
        InputImagesEdit.HasProducer = false;
        InputImagesEdit.Held = default;
        InputImagesEdit.TransferHeld = default;
        InputImagesEdit.HasTransferHeld = false;
    }
    // The stored values are not re-seeded here: they live in the arena's slot lanes, which a kit recompile leaves
    // alone and a re-declared register file carries across by name inside the arena's own relayout. Only the
    // read-back register (what was requested, who wrote it, why) is rebuilt, and a slot declared again under the same
    // name, kind and lifetime keeps its read-back so it still describes the value the lane holds.
    private void CompileActionState(CompiledActionStateSlot[]? state) {
        MarkKitLocalStale();
        var previousDefinitions = m_actionStateDefinitions;
        var previousRequested = m_actionStateRequested;
        var previousWriters = m_actionStateLastWriter;
        var previousReasons = m_actionStateLastReason;

        m_actionStateDefinitions = ((state is null)
            ? []
            : [.. state]
        );
        m_actionStateRequested = new long[m_actionStateDefinitions.Length];
        m_actionStateLastWriter = new string[m_actionStateDefinitions.Length];
        m_actionStateLastReason = new string[m_actionStateDefinitions.Length];
        m_registerWrites.Resize(count: m_actionStateDefinitions.Length).Clear();
        m_durableInput.Write = BodyDurableInputSection.Empty(registers: m_actionStateDefinitions.Length);

        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            var definition = m_actionStateDefinitions[slot];
            var preserved = -1;

            for (var prior = 0; (prior < previousDefinitions.Length); prior++) {
                if (
                    (previousDefinitions[prior].Lifetime == definition.Lifetime) &&
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
            ActionFact.Rising => ((IsRigid ? m_rigidVelocity.Y : m_verticalVelocity) > FixedQ4816.Zero),
            ActionFact.Falling => ((IsRigid ? m_rigidVelocity.Y : m_verticalVelocity) < FixedQ4816.Zero),
            ActionFact.InMedium => m_inMedium,
            ActionFact.AtMediumBand => m_atMediumBand,
            ActionFact.HoldingUnwalkable => HoldsUnwalkableSurface(),
            ActionFact.Unsupported => HoldsFree(),
            ActionFact.Resting => m_resting,
            ActionFact.Weightless => (TrySolvedGravityMagnitude(magnitude: out var pull) && (pull < MinFieldUpMagnitude)),
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
    private static long InitialRaw(in CompiledActionStateSlot definition) => WorldActionStateLane.InitialRaw(definition: in definition);
    private FixedQ4816 StateCounter(int slot) => ((m_stateLane is { } lane)
        ? lane.Counter(
            ordinal: m_stateOrdinal,
            slot: slot
        )
        : FixedQ4816.Zero
    );
    private ulong StateTimer(int slot) => ((m_stateLane is { } lane)
        ? lane.Timer(
            ordinal: m_stateOrdinal,
            slot: slot
        )
        : 0UL
    );
    // A body a kit partition steps never writes a register (StepIsLocal): the lanes are the arena's, which one thread writes.
    private void WriteStateRaw(int slot, long raw) => ((m_stepLog is null) ? m_stateLane : throw new InvalidOperationException(message: "A body stepped in a kit partition wrote an action-state register."))?.Write(
        ordinal: m_stateOrdinal,
        raw: raw,
        slot: slot
    );
    private void MarkDurableDirty(int slot, WorldDocumentWriteKind kind = WorldDocumentWriteKind.Set, FixedQ4816 operand = default) {
        if (
            (slot >= 0) &&
            (m_actionStateDefinitions[slot].Lifetime == ActionStateLifetime.Durable)
        ) {
            var staged = m_registerWrites.Read[slot];

            if (
                staged.Dirty &&
                (staged.Kind != WorldDocumentWriteKind.Add)
            ) {
                kind = WorldDocumentWriteKind.Set;
            } else if (
                staged.Dirty &&
                (kind == WorldDocumentWriteKind.Add)
            ) {
                operand += staged.Operand;
            }

            var marked = new WorldRegisterWriteImage(Dirty: true, Kind: kind, Operand: operand);

            if (marked != staged) { m_registerWrites.Write[slot] = marked; }
        }
    }
    // Resolve argument-less channel taps at the only boundary that knows the host's actual fixed-step period. A
    // pending tap merges into the lane timer through the SAME MergeLaneTimer PressChannel's timed overload uses —
    // one merge rule for both press paths, never two that can quietly drift apart.
    private void MaterializeDefaultLanePresses(ulong stepTicks) {
        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (!Action.PendingPress[ordinal]) {
                continue;
            }

            MergeLaneTimer(
                actor: ScriptActors.Pending[ordinal],
                ordinal: ordinal,
                value: Action.PendingValue[ordinal],
                holdTicks: stepTicks
            );
            ActionEdit.PendingPress[ordinal] = false;
        }
    }
    // Merges (ordinal, value, holdTicks) into the lane-timer slot: a same-value re-press only extends an in-flight
    // hold (the longer of the two durations wins — repeatedly resubmitting one held key must not truncate itself);
    // a different actor or value is a distinct action and replaces the hold outright — its own duration, not merged with
    // whatever ticks remained. Shared by PressChannel's timed overload and MaterializeDefaultLanePresses so the
    // wire-timed and host-step-tap press paths can never drift onto two different merge rules.
    private void MergeLaneTimer(int ordinal, FixedQ4816 value, ulong holdTicks, Puck.Commands.Principal actor) {
        var isSameValueRepress = ((Action.TimerTicks[ordinal] > 0) && (Action.TimerValues[ordinal] == value) && (ScriptActors.Timers[ordinal] == actor));

        if (isSameValueRepress) {
            ActionEdit.TimerTicks[ordinal] = Math.Max(
                val1: Action.TimerTicks[ordinal],
                val2: holdTicks
            );
        } else {
            ActionEdit.TimerValues[ordinal] = value;
            ActionEdit.TimerTicks[ordinal] = holdTicks;
            SetScriptActor(actor: actor, ordinal: ordinal, pending: false);
        }
    }
    // The first shaping row whose gate opens, or -1 when none does (an unmatched tick, or an empty/absent table).
    // ExecuteProgram calls this exactly once after refreshing recency clocks and stores the result in its scratch,
    // so turn, ShapeVelocity, and ApplyMedium share one pre-operation fact/channel snapshot for the whole tick.
    private int ResolveGoverningShapingRow(in PlayerIntent intent) {
        var shaping = m_tuning.Shaping;

        for (var index = 0; (index < shaping.Length); index++) {
            if (GateOpen(
                gate: shaping[index].When,
                intent: in intent,
                recencyLane: -1
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
        var inputEpoch = InputEpoch;

        AdvanceActionState(stepTicks: scratch.StepTicks);
        if (InputEpoch != inputEpoch) { scratch.Intent = InputReadbacks.EngagedIntent; return; }

        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (m_laneBindings[ordinal] is not { } binding) {
                continue;
            }

            var recencyStart = m_laneRecencyStart[ordinal];
            var logicalChannel = (((m_stateLane?.Input is { } inputRuntime) && m_channels.IsDeclared(ordinal: ordinal) &&
                inputRuntime.Catalog.TryGetLogicalId(m_channels.Name(ordinal: ordinal)!, out var channel)) ? channel : -1);
            var bit = (scratch.Intent[ordinal] >= m_channelThresholds[ordinal]);
            var pressed = (bit && !Action.PreviousBit[ordinal]);
            var released = (!bit && Action.PreviousBit[ordinal]);

            for (var slot = 0; (slot < binding.RecencyFacts.Length); slot++) {
                var clock = (FactHolds(fact: binding.RecencyFacts[slot])
                    ? binding.RecencyWindows[slot]
                    : m_laneRecency.Read[(recencyStart + slot)].SubtractSaturating(right: scratch.StepTicks)
                );

                if (clock != m_laneRecency.Read[(recencyStart + slot)]) { m_laneRecency.Write[(recencyStart + slot)] = clock; }
            }

            if (binding.OnPress is { } press) {
                var latch = (pressed
                    ? press.LatchTicks
                    : Action.LaneLatch[ordinal].SubtractSaturating(right: scratch.StepTicks)
                );

                if (Action.LaneLatch[ordinal] != latch) { ActionEdit.LaneLatch[ordinal] = latch; }

                // LatchSeconds 0 means THIS TICK ONLY, which is what the field always documented. Demanding a
                // strictly positive latch made zero structurally dead — a zero-latch press could never fire, however
                // open its gate was. The press is pending when its latch is still running OR when this very tick is
                // its edge; consuming it still clears the latch.
                if (
                    (pressed || (Action.LaneLatch[ordinal] > 0)) &&
                    GateOpen(
                    gate: press.Gate,
                    intent: in scratch.Intent,
                    recencyLane: ordinal
                )
                ) {
                    if (ApplyEffects(
                        effects: press.Effects,
                        scratch: ref scratch,
                        logicalChannel: logicalChannel
                    )) {
                        if (InputEpoch != inputEpoch) { return; }
                        ActionEdit.LaneLatch[ordinal] = 0;
                    }
                }
            }

            if (
                released &&
                (binding.OnRelease is { } release) &&
                GateOpen(
                gate: release.Gate,
                intent: in scratch.Intent,
                recencyLane: ordinal
            )
            ) {
                ApplyEffects(
                    effects: release.Effects,
                    scratch: ref scratch,
                    logicalChannel: logicalChannel
                );
                if (InputEpoch != inputEpoch) { return; }
            }

            for (var rule = 0; (rule < binding.OnFact.Length); rule++) {
                var trigger = binding.OnFact[rule];
                var holds = (FactHolds(fact: trigger.Fact) && GateOpen(
                    gate: trigger.Gate,
                    intent: in scratch.Intent,
                    recencyLane: ordinal
                ));
                var wasHeld = ((Action.LaneFactHeld[ordinal] & (1UL << rule)) != 0UL);

                if (wasHeld != holds) {
                    ActionEdit.LaneFactHeld[ordinal] = (holds
                        ? Action.LaneFactHeld[ordinal] | (1UL << rule)
                        : Action.LaneFactHeld[ordinal] & ~(1UL << rule)
                    );
                }

                // ONE edge vocabulary, the same ActionTriggerMode a world rule reads: EDGE fires on the crossing
                // alone and re-arms when the condition (fact AND gate together) stops holding; LEVEL fires every
                // tick it holds, which is what every fact trigger did before the mode existed.
                if (
                    holds &&
                    !((trigger.Mode == ActionTriggerMode.Edge) && wasHeld)
                ) {
                    ApplyEffects(
                        effects: trigger.Effects,
                        scratch: ref scratch,
                        logicalChannel: logicalChannel
                    );
                    if (InputEpoch != inputEpoch) { return; }
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
        value = StateCounter(slot: slot);
        timerTicks = StateTimer(slot: slot);
        return true;
    }
}
