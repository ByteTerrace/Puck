namespace Puck.Scripting.Simulation;

/// <summary>One validated input-channel act: a declared channel driven through a Drive handle, its payload lane
/// already domain-checked against the HOST table's shape (never the guest's own declaration) when the channel
/// resolved. Contribution semantics are per-tick declarative — there is no phase.</summary>
/// <param name="Ordinal">The cell's 0-based position in the guest's output batch — the correlation key any refusal
/// answer carries back.</param>
/// <param name="ChannelName">The declared channel name text, for diagnostics and the fold's discrepancy report.</param>
/// <param name="Resolved">Whether the declared channel resolved against the host's channel table at handshake. An
/// unresolved act is still structurally valid — it is report-and-inert, not a fault — and carries no domain-checked
/// <see cref="Value"/>; the consumer answers it <see cref="AddonVerdict.AttenuatedToEmpty"/> and folds nothing.</param>
/// <param name="ChannelOrdinal">The host-owned channel ordinal the declared name resolved to, or <c>-1</c> when
/// <paramref name="Resolved"/> is <see langword="false"/>.</param>
/// <param name="Shape">The value shape the host table declares for <paramref name="ChannelOrdinal"/>; meaningless
/// when <paramref name="Resolved"/> is <see langword="false"/>.</param>
/// <param name="Value">The single payload lane, <c>FixedQ4816</c> raw bits (or the literal <c>0</c>/<see
/// cref="AddonAbi.One"/> for a <see cref="AddonChannelValueShape.Binary"/> channel).</param>
/// <param name="HandleIndex">The Drive handle index the act drives through — resolved at application, never here.</param>
/// <param name="HandleGeneration">The Drive handle generation, checked at application against the live table.</param>
public readonly record struct AddonActSubmission(ushort Ordinal, string ChannelName, bool Resolved, int ChannelOrdinal, AddonChannelValueShape Shape, long Value, ushort HandleIndex, ushort HandleGeneration);

/// <summary>One validated request-channel act: a pinned query verb dispatched through a handle. A
/// <see cref="AddonAbi.RequestVerbs.BodyPose"/> query resolves through an Observe handle at the DRAIN point (after
/// the authoritative step of the tick it was written in) and carries no payload (<see cref="A"/>/<see cref="B"/>/
/// <see cref="C"/> are all zero, structurally enforced at decode). A
/// <see cref="AddonAbi.RequestVerbs.SubmitMutation"/> act resolves through a Mutate handle at DECODE time (the same
/// Step, before intents — the addon mutation seam's own timing contract) and carries the declared mutation-kind
/// ordinal in <see cref="A"/>, an unsigned guest-memory pointer in <see cref="B"/>, and an unsigned byte length in
/// <see cref="C"/>.</summary>
/// <param name="Ordinal">The cell's 0-based position in the guest's output batch — every answer part repeats it.</param>
/// <param name="Verb">The pinned request-verb ordinal (<see cref="AddonAbi.RequestVerbs"/>).</param>
/// <param name="HandleIndex">The handle index the act resolves through (Observe for <c>BodyPose</c>, Mutate for
/// <c>SubmitMutation</c>).</param>
/// <param name="HandleGeneration">The handle generation, checked at application.</param>
/// <param name="A">Verb-dependent payload lane 1: zero for <c>BodyPose</c>; the declared mutation-kind ordinal for
/// <c>SubmitMutation</c>.</param>
/// <param name="B">Verb-dependent payload lane 2: zero for <c>BodyPose</c>; the guest-memory pointer (reinterpreted
/// unsigned) for <c>SubmitMutation</c>.</param>
/// <param name="C">Verb-dependent payload lane 3: zero for <c>BodyPose</c>; the payload byte length (reinterpreted
/// unsigned, structurally bounded by <see cref="AddonAbi.MaxMutationPayloadBytes"/>) for <c>SubmitMutation</c>.</param>
public readonly record struct AddonQuerySubmission(ushort Ordinal, ushort Verb, ushort HandleIndex, ushort HandleGeneration, long A = 0L, long B = 0L, long C = 0L);

/// <summary>One validated ask: a request for a handle over a subject the guest names, resolved
/// <c>requested ∧ granted</c> at the drain point. The capability mask has exactly one bit set (the one-handle-per-
/// answer rule) and the cell's handle fields were verified zero.</summary>
/// <param name="Ordinal">The cell's 0-based position in the guest's output batch.</param>
/// <param name="SubjectKind">The pinned subject-kind ordinal (<see cref="AddonSubjectKind.Body"/> or
/// <see cref="AddonSubjectKind.Section"/> are admitted today).</param>
/// <param name="SubjectIndex">On <see cref="AddonSubjectKind.Body"/>, the body's 0-based entity index (the
/// <c>A</c> lane). On <see cref="AddonSubjectKind.Section"/>, the guest-memory byte offset of the section NAME's
/// UTF-8 bytes (also the <c>A</c> lane, reinterpreted as an unsigned pointer) — name-keyed, never a baked
/// <c>WorldSection</c> ordinal; see <see cref="NameLength"/> and <c>Server.WorldAddonRuntime.ResolveAsks</c>.</param>
/// <param name="CapabilityMask">The requested capability as a single-bit mask (the <c>B</c> lane).</param>
/// <param name="NameLength">On <see cref="AddonSubjectKind.Section"/>, the section name's UTF-8 byte length (the
/// <c>C</c> lane, reinterpreted unsigned) — the pointer-safety copy at <see cref="SubjectIndex"/> reads exactly
/// this many bytes. Always <c>0</c> on <see cref="AddonSubjectKind.Body"/>, where the <c>C</c> lane carries no
/// meaning and must be zero.</param>
public readonly record struct AddonAskSubmission(ushort Ordinal, AddonSubjectKind SubjectKind, long SubjectIndex, ulong CapabilityMask, long NameLength = 0L);

/// <summary>
/// The Simulation lane's one crossing from guest bytes to typed submissions — drives an <see cref="AddonInstance"/>
/// one tick and validates every returned cell against the lane's VOCABULARY: which channel kinds admit which cell
/// kinds, verb ranges, required-zero reserved bits, payload domains per channel shape, duplicate declared
/// ordinals within a batch, and the ask rules. Structure was already validated by the core's decode; authority
/// (handles, grants) is deliberately NOT checked here — the capability is checked at application, and only there.
/// </summary>
/// <remarks>A vocabulary violation refuses the WHOLE batch and faults the instance through
/// <see cref="AddonInstance.FaultProtocol"/> — the same sticky posture as a structural decode error, so a guest's
/// observable effect can never depend on where in its own batch a bug sat. Authority denials are per-record verdict
/// answers produced by the consumer at application; they never fault and never touch this type. Reused per host —
/// the submission arrays are overwritten every pump, valid until the next call, single sim-tick thread only.</remarks>
public sealed class AddonSimulationPump {
    private readonly AddonActSubmission[] m_acts = new AddonActSubmission[AddonAbi.MaxOutCells];
    private readonly AddonAskSubmission[] m_asks = new AddonAskSubmission[AddonAbi.MaxOutCells];
    private readonly AddonQuerySubmission[] m_queries = new AddonQuerySubmission[AddonAbi.MaxOutCells];
    private int m_actCount;
    private int m_askCount;
    private int m_queryCount;

    /// <summary>Gets the validated input-channel acts of the most recent successful <see cref="Pump"/>.</summary>
    public ReadOnlySpan<AddonActSubmission> Acts => m_acts.AsSpan(
        length: m_actCount,
        start: 0
    );
    /// <summary>Gets the validated asks of the most recent successful <see cref="Pump"/>.</summary>
    public ReadOnlySpan<AddonAskSubmission> Asks => m_asks.AsSpan(
        length: m_askCount,
        start: 0
    );
    /// <summary>Gets the validated request-channel queries of the most recent successful <see cref="Pump"/>.</summary>
    public ReadOnlySpan<AddonQuerySubmission> Queries => m_queries.AsSpan(
        length: m_queryCount,
        start: 0
    );
    /// <summary>Gets the fuel consumed by the most recent <see cref="Pump"/> call's tick — set whether the tick
    /// succeeded or faulted (a trap can burn its whole budget before faulting), zero if <see cref="Pump"/> has never
    /// run. The cost-surface read: the consumer's tick loop accumulates this into a per-guest running total.</summary>
    public ulong FuelConsumed { get; private set; }

    /// <summary>Drives one addon one tick and validates its whole output batch. On success the typed submissions are
    /// readable from <see cref="Acts"/>/<see cref="Queries"/>/<see cref="Asks"/> until the next call. On a tick fault
    /// or a vocabulary violation (which faults the instance, whole batch refused) every submission list is empty and
    /// this returns <see langword="false"/>. <see cref="FuelConsumed"/> is set from the tick result either way.</summary>
    /// <param name="instance">The admitted, enabled instance to drive.</param>
    /// <param name="input">The host-composed input batch for this tick, within the guest's declared capacity.</param>
    /// <returns><see langword="true"/> when the batch decoded and validated whole; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    public bool Pump(AddonInstance instance, ReadOnlySpan<AddonInCell> input) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        m_actCount = 0;
        m_askCount = 0;
        m_queryCount = 0;

        var result = instance.Tick(input: input);

        FuelConsumed = result.FuelConsumed;

        if (result.Status != AddonTickStatus.Ok) {
            return false;
        }

        var cells = instance.OutCells;
        var channels = instance.Channels;
        var bindings = instance.ChannelBindings;
        // A 64-bit seen-mask over DECLARED channel ordinals (bounded by AddonAbi.MaxChannelNames), reset every
        // Pump call. Two Acts naming the SAME declared ordinal in one batch have no meaning under the per-tick
        // declarative contract — there is no "later act wins" any more (see TryValidateInputAct's own doc) — so
        // this is a protocol fault, the same posture as any other malformed record, never a silent overwrite.
        var seenChannelOrdinals = 0UL;

        for (var index = 0; (index < cells.Length); ++index) {
            ref readonly var cell = ref cells[index];
            var channelKind = channels[cell.Channel].Kind;
            var ordinal = (ushort)index;

            switch (cell.Kind) {
                case AddonOutCellKind.Act when (channelKind == AddonChannelKind.Input): {
                        if (!TryValidateInputAct(bindings: bindings, cell: in cell, ordinal: ordinal, error: out var actError)) {
                            return Refuse(instance: instance, ordinal: ordinal, reason: actError);
                        }

                        // Structurally valid, so the declared ordinal is guaranteed < bindings.Length <=
                        // AddonAbi.MaxChannelNames (64) — the shift below can never lose a bit.
                        var declaredOrdinal = (cell.Verb >> AddonAbi.InputVerbReservedBits);
                        var declaredBit = (1UL << declaredOrdinal);

                        if ((seenChannelOrdinals & declaredBit) != 0UL) {
                            return Refuse(instance: instance, ordinal: ordinal, reason: $"names declared channel ordinal {declaredOrdinal} more than once in one batch — duplicate channel acts are a protocol fault under the per-tick declarative contract");
                        }

                        seenChannelOrdinals |= declaredBit;

                        break;
                    }
                case AddonOutCellKind.Act when (channelKind == AddonChannelKind.Request): {
                        if (!TryValidateQuery(cell: in cell, ordinal: ordinal, verbCount: channels[cell.Channel].VerbCount, error: out var queryError)) {
                            return Refuse(instance: instance, ordinal: ordinal, reason: queryError);
                        }

                        break;
                    }
                case AddonOutCellKind.Ask when (channelKind == AddonChannelKind.Request): {
                        if (!TryValidateAsk(cell: in cell, ordinal: ordinal, error: out var askError)) {
                            return Refuse(instance: instance, ordinal: ordinal, reason: askError);
                        }

                        break;
                    }
                default:
                    return Refuse(instance: instance, ordinal: ordinal, reason: $"a {cell.Kind} cell is not admissible on a {channelKind} channel");
            }
        }

        return true;
    }

    // The input act vocabulary: verb = (declaredOrdinal << AddonAbi.InputVerbReservedBits), low bits REQUIRED
    // ZERO (contribution semantics are per-tick declarative — there is no phase to decode any more), declared
    // ordinal within the DECLARED table, single payload lane domain-checked against the HOST table's shape — the
    // consumer receiving an out-of-domain value is already too late (it is the point somebody reaches for a
    // clamp, and a clamp is a silent mapping change). An UNRESOLVED declaration (the host table has no channel
    // for the name) is never a decode fault: the act still decodes, carrying Resolved = false, and the consumer
    // answers it AttenuatedToEmpty at the fold — report-and-inert, not a protocol violation.
    private bool TryValidateInputAct(ReadOnlySpan<AddonChannelBinding> bindings, in AddonOutCell cell, ushort ordinal, out string error) {
        var reserved = cell.Verb & AddonAbi.InputVerbReservedMask;

        if (reserved != 0) {
            error = $"channel act reserved verb bits must be zero, got 0x{reserved:x} (contribution semantics are per-tick declarative)";
            return false;
        }

        var declaredOrdinal = (cell.Verb >> AddonAbi.InputVerbReservedBits);

        if (declaredOrdinal >= bindings.Length) {
            error = $"names declared channel ordinal {declaredOrdinal}, declared table holds {bindings.Length}";
            return false;
        }

        var binding = bindings[declaredOrdinal];

        // B and C are required-zero regardless of resolution — the wire shape is fixed independent of whether the
        // host recognizes the name.
        if ((cell.B != 0L) || (cell.C != 0L)) {
            error = $"channel act on '{binding.Name}' requires B = C = 0 (B={cell.B}, C={cell.C})";
            return false;
        }

        if (binding.Resolved) {
            // Range checks compare directly rather than through Math.Abs: the lane is guest-supplied raw i64 bits,
            // and Abs(long.MinValue) throws ON THE HOST — a malformed act must fault the GUEST, never unwind the
            // server.
            switch (binding.Shape) {
                case AddonChannelValueShape.Bipolar when ((cell.A > AddonAbi.One) || (cell.A < -AddonAbi.One)):
                    error = $"bipolar '{binding.Name}' requires |A| <= one (A={cell.A})";
                    return false;
                case AddonChannelValueShape.Binary when (cell.A is not (0L or AddonAbi.One)):
                    error = $"binary '{binding.Name}' requires A in {{0, one}} — the literal fixed-point values, never a boolean 0/1 (A={cell.A})";
                    return false;
                case AddonChannelValueShape.Unipolar when ((cell.A > AddonAbi.One) || (cell.A < 0L)):
                    error = $"unipolar '{binding.Name}' requires A in [0, one] (A={cell.A})";
                    return false;
            }
        }

        m_acts[m_actCount++] = new AddonActSubmission(
            ChannelName: binding.Name,
            ChannelOrdinal: binding.Ordinal,
            HandleGeneration: cell.HandleGeneration,
            HandleIndex: cell.HandleIndex,
            Ordinal: ordinal,
            Resolved: binding.Resolved,
            Shape: binding.Shape,
            Value: cell.A
        );
        error = "";
        return true;
    }
    // The request vocabulary: a pinned ordinal within the guest's declared range. BodyPose carries every payload
    // lane zero. SubmitMutation carries a memory payload: A names the declared mutation-
    // kind ordinal (structurally bounded to a byte, the wire-level ceiling — WorldMutationKindCatalog.MaxOrdinal's
    // own 0..63 bound is a WORLD-side rule this lane-neutral pump cannot see and does not enforce), B is the
    // guest-memory pointer and C is the payload byte length, BOTH reinterpreted unsigned — a "negative" signed i64
    // bit pattern is not an impossible value here, it IS the (very large) unsigned value the wire actually names,
    // exactly like a genuinely oversized length. Neither is bounded at THIS layer: pointer/length validity
    // (including a negative-reinterpreted-as-huge one) is a PER-ACT dispatch-door refusal
    // (AddonVerdict.MalformedPayload/PayloadTooLarge, stages 5/5 of the addon mutation dispatch door — see
    // AddonInstance.TryCopyMemory and WorldAddonRuntime.ResolveMutations), never a whole-batch protocol fault: a
    // guest naming an out-of-range pointer or an oversized length must get a same-shape refusal on THAT act, not
    // have its entire tick's batch thrown out and the instance sticky-faulted. This method only rejects the ONE
    // wire-shape violation that is genuinely impossible regardless of signedness: the kind ordinal, which the ABI
    // pins to a single byte (0..255) and can never legitimately be negative OR require reinterpretation.
    private bool TryValidateQuery(in AddonOutCell cell, ushort ordinal, int verbCount, out string error) {
        if (cell.Verb >= verbCount) {
            error = $"request verb {cell.Verb} outside the declared range [0, {verbCount})";
            return false;
        }

        if (cell.Verb == AddonAbi.RequestVerbs.SubmitMutation) {
            if ((cell.A < 0L) || (cell.A > byte.MaxValue)) {
                error = $"submit-mutation kind ordinal {cell.A} outside [0, {byte.MaxValue}]";
                return false;
            }

            m_queries[m_queryCount++] = new AddonQuerySubmission(
                Ordinal: ordinal,
                Verb: cell.Verb,
                HandleIndex: cell.HandleIndex,
                HandleGeneration: cell.HandleGeneration,
                A: cell.A,
                B: cell.B,
                C: cell.C
            );
            error = "";
            return true;
        }

        if (cell.Verb == AddonAbi.RequestVerbs.Designate) {
            if ((cell.A < 0L) || (cell.A > int.MaxValue) || (cell.B < 0L) || (cell.B > int.MaxValue) || (cell.C != 0L)) {
                error = $"designate requires target body and register indices in [0, {int.MaxValue}] and C = 0 (A={cell.A}, B={cell.B}, C={cell.C})";
                return false;
            }

            m_queries[m_queryCount++] = new AddonQuerySubmission(
                Ordinal: ordinal,
                Verb: cell.Verb,
                HandleIndex: cell.HandleIndex,
                HandleGeneration: cell.HandleGeneration,
                A: cell.A,
                B: cell.B
            );
            error = "";
            return true;
        }

        if ((cell.A != 0L) || (cell.B != 0L) || (cell.C != 0L)) {
            error = $"request verb {cell.Verb} requires A = B = C = 0 (A={cell.A}, B={cell.B}, C={cell.C})";
            return false;
        }

        m_queries[m_queryCount++] = new AddonQuerySubmission(
            Ordinal: ordinal,
            Verb: cell.Verb,
            HandleIndex: cell.HandleIndex,
            HandleGeneration: cell.HandleGeneration
        );
        error = "";
        return true;
    }
    // The ask vocabulary: an admitted subject kind, a single-bit capability mask within THAT KIND's own admitted
    // set, and zero handle fields (unused fields never float). Body pairs with {Drive, Observe} (the pre-existing
    // shape), A = the body's 0-based entity index, C required zero. Section pairs with {Mutate} ALONE — the addon
    // mutation seam's own handle shape — and is NAME-KEYED: A = a guest-memory pointer, C = the name's UTF-8 byte
    // length, the same ptr/len wire shape RequestVerbs.SubmitMutation already uses for a payload. This method only
    // enforces the SHAPE (a nonzero length is structurally required; a name can never be zero bytes) — the length
    // CEILING (AddonAbi.MaxSectionNameBytes) and the guest-memory copy are guest-controlled MAGNITUDES, deferred to
    // the resolve-time refusal WorldAddonRuntime.ResolveAsks already gives an oversized/out-of-bounds mutation
    // payload, never a whole-batch fault here.
    private bool TryValidateAsk(in AddonOutCell cell, ushort ordinal, out string error) {
        if ((cell.Verb != (ushort)AddonSubjectKind.Body) && (cell.Verb != (ushort)AddonSubjectKind.Section)) {
            error = $"ask subject kind {cell.Verb} is not admitted (admitted: {(byte)AddonSubjectKind.Body} = Body, {(byte)AddonSubjectKind.Section} = Section)";
            return false;
        }

        var subjectKind = (AddonSubjectKind)cell.Verb;

        if ((cell.HandleIndex != 0) || (cell.HandleGeneration != 0)) {
            error = $"ask handle fields must be zero (index={cell.HandleIndex}, generation={cell.HandleGeneration})";
            return false;
        }

        if (cell.A < 0L) {
            error = $"ask subject index {cell.A} is negative";
            return false;
        }

        var mask = (ulong)cell.B;
        var admissible = ((subjectKind == AddonSubjectKind.Section) ? AddonCapabilityMask.Mutate : AddonCapabilityMask.Drive | AddonCapabilityMask.Observe);

        if ((mask == 0UL) || ((mask & (mask - 1UL)) != 0UL) || ((mask & ~admissible) != 0UL)) {
            error = $"ask capability mask 0x{mask:x} must be exactly one of the {subjectKind}-admissible bits (0x{admissible:x})";
            return false;
        }

        if (subjectKind == AddonSubjectKind.Body) {
            if (cell.C != 0L) {
                error = $"a Body ask requires C = 0 (C={cell.C})";
                return false;
            }
        } else if (cell.C <= 0L) {
            // A Section ask's C lane is its name's UTF-8 byte length — zero or negative can never name a real
            // WorldSection member (the shortest is non-empty), so this is a shape fault, not a magnitude one.
            error = $"a Section ask's name length {cell.C} must be positive";
            return false;
        }

        m_asks[m_askCount++] = new AddonAskSubmission(
            Ordinal: ordinal,
            SubjectKind: subjectKind,
            SubjectIndex: cell.A,
            CapabilityMask: mask,
            NameLength: ((subjectKind == AddonSubjectKind.Section) ? cell.C : 0L)
        );
        error = "";
        return true;
    }
    private bool Refuse(AddonInstance instance, ushort ordinal, string reason) {
        m_actCount = 0;
        m_askCount = 0;
        m_queryCount = 0;
        instance.FaultProtocol(reason: $"cell {ordinal} {reason}");
        return false;
    }
}
