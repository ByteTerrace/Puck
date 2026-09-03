using System.Globalization;
using Puck.Maths;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateSocial(WorldDefinition definition, List<string> errors) {
        if (definition.StateRaw?.Social is not { } policy) { return; }
        try { _ = CompiledWorldSocialPolicy.Compile(policy); }
        catch (ArgumentException exception) { errors.Add($"state.social: {exception.Message}"); }
    }
    /// <summary>Validates the bounded value surface affected by one scalar state-cell mutation. The containing
    /// definition has already passed full validation; this check is therefore intentionally limited to properties
    /// an <c>UpsertStateCell</c> mutation can change.</summary>
    public static bool TryValidateRuntimeStateCell(WorldDefinition definition, string rowName, string key, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        reason = string.Empty;

        if (
            WorldDefinitionRows.FindStateRow(rows: definition.State, name: rowName) is not { } row ||
            !WorldCellName.TryParse(candidate: key, name: out var cellKey, reason: out _) ||
            WorldDefinitionRows.FindCell(cells: row.Cells, key: cellKey) is not { } cell
        ) {
            reason = $"state row '{rowName}' cell '{key}' did not resolve after composition";
            return false;
        }

        if (row.Tokens is not null || row.Zone is not null || row.KeysFrom is not null || row.Phase is not null || row.Knowledge is not null || cell.Visibility is not null || cell.Observation is not null) {
            return TryValidateLocally(definition, out reason);
        }
        var capacity = Math.Clamp(value: (row.Capacity ?? row.CellCeiling), min: 1, max: row.CellCeiling);

        if ((row.Cells?.Count ?? 0) > capacity) {
            reason = $"state row '{rowName}' cell count exceeds its capacity of {capacity}";
            return false;
        }

        if (row.Board is { } board && (WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology || !topology.TryCell(key, out _))) {
            reason = $"state row '{rowName}' cell '{key}' is outside its topology";
            return false;
        }
        if (row.Kind == CellKind.Text) {
            if (cell.Text is null || cell.Text.Length > WorldStateCapacity.MaxTextValueLength) {
                reason = $"state row '{rowName}' cell '{key}' text is missing or exceeds {WorldStateCapacity.MaxTextValueLength} characters";
                return false;
            }

            return true;
        }

        if ((row.Kind == CellKind.Bool) && (cell.Value is not (0 or 1))) {
            reason = $"state row '{rowName}' cell '{key}' value {cell.Value} must be 0 or 1";
            return false;
        }

        if (row.NonNegative && (cell.Value < 0L)) {
            reason = $"state row '{rowName}' cell '{key}' value is negative";
            return false;
        }

        if ((row.Min is { } min) && (row.Max is { } max) && ((cell.Value < min) || (cell.Value > max))) {
            reason = $"state row '{rowName}' cell '{key}' value {DescribeValue(kind: row.Kind, raw: cell.Value)} is outside its declared range {DescribeValue(kind: row.Kind, raw: min)}..{DescribeValue(kind: row.Kind, raw: max)}";
            return false;
        }

        if ((row.Kind == CellKind.Int) && ((cell.Value < WorldStateCapacity.MinIntCellValue) || (cell.Value > WorldStateCapacity.MaxIntCellValue))) {
            reason = $"state row '{rowName}' cell '{key}' value {cell.Value} is outside the representable int range {WorldStateCapacity.MinIntCellValue}..{WorldStateCapacity.MaxIntCellValue}";
            return false;
        }

        // The value checks above are the cheap prefix; the state section's own walk is what refuses the shapes a
        // cell write can still produce — a keyed cell minted beside a row-level advance, dynamics or cycle, the
        // reserved slot key on a keyed row, cells on a lattice row, a lattice-node phase out of range — so a write the
        // live door admits is never one the next boot refuses.
        var errors = new List<string>();
        var dynamicsNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var dynamics in (definition.DynamicsRaw ?? [])) {
            if (dynamics?.Name is { } dynamicsName) {
                dynamicsNames.Add(item: dynamicsName);
            }
        }

        _ = ValidateState(
            rows: definition.State,
            generators: definition.Generators,
            dynamicsNames: dynamicsNames,
            errors: errors
        );

        if (errors.Count > 0) {
            reason = errors[0];
            return false;
        }

        return true;
    }

    // Fixed-kind values speak DECIMAL in refusal text — never the raw Q48.16 bit pattern — matching the document
    // JSON, console verb, and read-back conventions the same value crosses.
    private static string DescribeValue(CellKind kind, long raw) =>
        ((kind == CellKind.Fixed)
            ? FixedQ4816.FromRawBits(value: raw).ToString()
            : raw.ToString(provider: CultureInfo.InvariantCulture)
        );
    /// <summary>Validates a row's authored <see cref="WorldStateAdvance"/> continuous-accumulation trait. Whether
    /// reaching a declared envelope bound clamps the computed value (it never rewrites the stored base/epoch) is the
    /// settled read-side half of the envelope duality, documented on <see cref="WorldStateAdvance"/> itself and not a
    /// validator concern — this method refuses only shapes the read side could not honestly compute over.</summary>
    private static void ValidateAdvance(WorldStateRow row, bool numeric, string path, List<string> errors) {
        if (row.Advance is not { } advance) {
            return;
        }

        if (row.Draw is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares both draw and advance — a row is an authored-randomness draw site or a continuous accumulator, never both.");
        }

        if (!numeric) {
            errors.Add(item: $"{path} ('{row.Name}') declares advance on a {WorldRefusalSpelling.Kind(kind: row.Kind)} row — only int/fixed rows accumulate.");
        }

        if (advance.RateDenominator <= 0) {
            errors.Add(item: $"{path}.advance.rateDenominator {advance.RateDenominator} must be positive.");
        }

        RequireNonNegativeEpoch(
            value: advance.EpochTick,
            name: $"{path}.advance.epochTick",
            errors: errors
        );

        // Advance is a SCALAR (slot) trait: legitimate only on a row declaring no capacity and holding at most its
        // one slot cell — empty (declared, never yet set) or exactly one cell keyed WorldStateRow.SlotKey. A row that
        // has grown past that (a keyed table, or a slot that later gained a second author-keyed cell) is refused
        // here rather than silently accumulating a value nothing addresses as "the" row value.
        var cells = (row.Cells ?? []);
        var slotEligible = ((row.Capacity is null) && ((cells.Count == 0) || ((cells.Count == 1) && (cells[0].Key == WorldStateRow.SlotKey))));

        if (!slotEligible) {
            errors.Add(item: $"{path} ('{row.Name}') declares advance on a keyed row — advance is legitimate only on a scalar (slot) row, authored with 'value' or left empty until the first explicit set.");
        }
    }
    /// <summary>Validates one cell's own <see cref="WorldStateAdvance"/> — the keyed counterpart of
    /// <see cref="ValidateAdvance"/>, stated separately because it governs the opposite shape: a cell inside a
    /// table rather than a row's own slot. The two never overlap by construction (this rejects the slot key
    /// outright), so a cell's advance and its row's advance can never both claim the same cell.</summary>
    private static void ValidateCellAdvance(WorldStateRow row, WorldStateCell cell, WorldStateAdvance advance, bool numeric, string cellPath, List<string> errors) {
        // The slot key's own accumulation is authored at the ROW level (beside 'value'), never here — refusing this
        // combination outright is what keeps "which advance governs the slot cell" from ever being two mechanisms
        // reading the same address.
        if (cell.Key == WorldStateRow.SlotKey) {
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares its own advance on the reserved slot key — a scalar row's accumulation is authored at the ROW level ('advance' beside 'value'), never on the cell itself.");
        }

        if (!numeric) {
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares advance on a {WorldRefusalSpelling.Kind(kind: row.Kind)} cell — only int/fixed cells accumulate.");
        }

        if (advance.RateDenominator <= 0) {
            errors.Add(item: $"{cellPath}.advance.rateDenominator {advance.RateDenominator} must be positive.");
        }

        RequireNonNegativeEpoch(
            value: advance.EpochTick,
            name: $"{cellPath}.advance.epochTick",
            errors: errors
        );
    }
    /// <summary>Validates a row's authored <see cref="WorldStateCycle"/> rotation trait: the generator, power and step length the
    /// read side can compute over, an output the row's kind can carry, and the same scalar-row exclusivity the other
    /// traits hold.</summary>
    private static void ValidateCycle(WorldStateRow row, bool numeric, string path, List<string> errors) {
        if (row.Cycle is not { } cycle) {
            return;
        }

        if (row.Draw is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares both draw and cycle — a row is an authored-randomness draw site or a tick-indexed rotation, never both.");
        }

        if (row.Advance is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares both advance and cycle — a row is a linear accumulator or a tick-indexed rotation, never both.");
        }

        if (row.Dynamics is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares both dynamics and cycle — a row is a second-order easing cell or a tick-indexed rotation, never both.");
        }

        ValidateCycleShape(
            cycle: cycle,
            errors: errors,
            kind: row.Kind,
            numeric: numeric,
            path: $"{path}.cycle",
            subject: $"{path} ('{row.Name}')"
        );

        var cells = (row.Cells ?? []);
        var slotEligible = ((row.Capacity is null) && ((cells.Count == 0) || ((cells.Count == 1) && (cells[0].Key == WorldStateRow.SlotKey))));

        if (!slotEligible) {
            errors.Add(item: $"{path} ('{row.Name}') declares cycle on a keyed row — cycle is legitimate only on a scalar (slot) row, authored with 'value' or left empty; a keyed row's cells each declare their own 'cycle'.");
        }

        if (WorldStateCycle.IsLatticeOutput(output: cycle.Output) && (cells.Count == 1) && (WorldStateCycle.Phase(baseValue: cells[0].Value, kind: row.Kind) is < 0 or >= SymmetryLattice.NodeCount)) {
            errors.Add(item: $"{path} ('{row.Name}') value {DescribeValue(kind: row.Kind, raw: cells[0].Value)} is not a symmetry-lattice node — a {WorldRefusalSpelling.CycleOutput(output: cycle.Output)} cycle stores the node its ring walk starts from, 0..{SymmetryLattice.NodeCount - 1}.");
        }
    }
    /// <summary>Validates one cell's own <see cref="WorldStateCycle"/> — the keyed counterpart of
    /// <see cref="ValidateCycle"/>, refusing the slot key so a cell's cycle and its row's cycle can never both claim
    /// the same cell.</summary>
    private static void ValidateCellCycle(WorldStateRow row, WorldStateCell cell, WorldStateCycle cycle, bool numeric, string cellPath, List<string> errors) {
        // A drive gate is resolved once per install and read as of tick zero (WorldGrants.SyncState); a cell that
        // turns with the tick would gate on a value nothing ever refreshes, so a gate row's cells never cycle.
        if (row.GatesDrive) {
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares cycle on a gatesDrive row — a drive gate is resolved once per install, so a cell that turns with the tick would gate on a stale value; drive the gate through an explicit write instead.");
        }

        if (cell.Key == WorldStateRow.SlotKey) {
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares its own cycle on the reserved slot key — a scalar row's rotation is authored at the ROW level ('cycle' beside 'value').");
        }

        ValidateCycleShape(
            cycle: cycle,
            errors: errors,
            kind: row.Kind,
            numeric: numeric,
            path: $"{cellPath}.cycle",
            subject: $"{cellPath} ('{row.Name}'.'{cell.Key}')"
        );

        if (WorldStateCycle.IsLatticeOutput(output: cycle.Output) && (WorldStateCycle.Phase(baseValue: cell.Value, kind: row.Kind) is < 0 or >= SymmetryLattice.NodeCount)) {
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') value {DescribeValue(kind: row.Kind, raw: cell.Value)} is not a symmetry-lattice node — a {WorldRefusalSpelling.CycleOutput(output: cycle.Output)} cycle stores the node its ring walk starts from, 0..{SymmetryLattice.NodeCount - 1}.");
        }
    }
    // The field checks a row-level and a cell-level cycle share: a word that bakes to a moving generator, a power
    // that is not the identity, step length, epoch, and an output the carrying kind can read (step/node on int, the
    // fixed outputs on fixed, nothing on bool/text).
    private static void ValidateCycleShape(WorldStateCycle cycle, CellKind kind, bool numeric, string path, string subject, List<string> errors) {
        if (!Enum.IsDefined(value: cycle.Output)) {
            errors.Add(item: $"{path}.output '{cycle.Output}' is not a defined WorldCycleOutput.");
        }

        if (!numeric) {
            errors.Add(item: $"{subject} declares cycle on a {WorldRefusalSpelling.Kind(kind: kind)} row — only int/fixed cells turn.");
        }
        else if (WorldStateCycle.IsIntegerOutput(output: cycle.Output) != (kind == CellKind.Int)) {
            errors.Add(item: $"{path}.output '{WorldRefusalSpelling.CycleOutput(output: cycle.Output)}' does not suit a {WorldRefusalSpelling.Kind(kind: kind)} cell — Step, Node and Ring are read by int cells, Turns/Cos/Sin/ProjectionX/ProjectionY by fixed cells.");
        }

        if (!cycle.TryResolveGenerator(generator: out var generator, reason: out var wordReason)) {
            errors.Add(item: $"{path}.{wordReason}.");
        }
        else if (generator.IsIdentity) {
            errors.Add(item: $"{path}.word [{string.Join(separator: ',', values: cycle.Word!)}] moves no node — its order is 1, so a cycle carrying it would never turn; author a word whose reflections do not cancel.");
        }
        else if ((cycle.Power <= -generator.Order) || (cycle.Power >= generator.Order)) {
            errors.Add(item: $"{path}.power {cycle.Power} is outside the generator's order {generator.Order} — a power reduces modulo the order, so author one in -{generator.Order - 1}..{generator.Order - 1}.");
        }
        else if (cycle.Power == 0) {
            errors.Add(item: $"{path}.power 0 is the identity — a cycle carrying it would never turn; author a nonzero power.");
        }

        if (cycle.TicksPerStep <= 0) {
            errors.Add(item: $"{path}.ticksPerStep {cycle.TicksPerStep} must be positive.");
        }

        if ((cycle.SubstepTicks < 0) || (cycle.SubstepTicks >= cycle.TicksPerStep)) {
            errors.Add(item: $"{path}.substepTicks {cycle.SubstepTicks} must be in 0..ticksPerStep-1.");
        }

        RequireNonNegativeEpoch(
            value: cycle.EpochTick,
            name: $"{path}.epochTick",
            errors: errors
        );
    }
    /// <summary>Validates a row's authored <see cref="WorldStateDynamics"/> easing trait — the closed-form
    /// counterpart to <see cref="ValidateAdvance"/>, so shares its scalar-row/exclusivity shape.</summary>
    private static void ValidateDynamicsTrait(WorldStateRow row, bool numeric, ISet<string> dynamicsNames, string path, List<string> errors) {
        if (row.Dynamics is not { } dynamics) {
            return;
        }

        if (row.Draw is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares both draw and dynamics — a row is an authored-randomness draw site or a second-order easing cell, never both.");
        }

        if (row.Advance is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares both advance and dynamics — a row is a linear accumulator or a second-order easing cell, never both.");
        }

        if (!numeric) {
            errors.Add(item: $"{path} ('{row.Name}') declares dynamics on a {WorldRefusalSpelling.Kind(kind: row.Kind)} row — only int/fixed rows ease.");
        }

        RequireDeclared(
            value: dynamics.Row,
            declaredSet: dynamicsNames,
            path: path,
            field: "dynamics.row",
            rowNoun: "dynamics",
            errors: errors
        );

        RequireNonNegativeEpoch(
            value: dynamics.EpochTick,
            name: $"{path}.dynamics.epochTick",
            errors: errors
        );

        // Dynamics is a SCALAR (slot) trait, exactly like Advance — the same slot-eligibility test.
        var cells = (row.Cells ?? []);
        var slotEligible = ((row.Capacity is null) && ((cells.Count == 0) || ((cells.Count == 1) && (cells[0].Key == WorldStateRow.SlotKey))));

        if (!slotEligible) {
            errors.Add(item: $"{path} ('{row.Name}') declares dynamics on a keyed row — dynamics is legitimate only on a scalar (slot) row; a keyed row's own cells ease independently.");
        }
    }
    /// <summary>Validates one cell's own <see cref="WorldStateDynamics"/> — the keyed counterpart of
    /// <see cref="ValidateDynamicsTrait"/>, governing the opposite shape: a cell inside a table rather than a row's
    /// own slot.</summary>
    private static void ValidateCellDynamics(WorldStateRow row, WorldStateCell cell, WorldStateDynamics dynamics, bool numeric, ISet<string> dynamicsNames, string cellPath, List<string> errors) {
        if (cell.Key == WorldStateRow.SlotKey) {
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares its own dynamics on the reserved slot key — a scalar row's easing trait is authored at the ROW level, never on the cell itself.");
        }

        if (!numeric) {
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares dynamics on a {WorldRefusalSpelling.Kind(kind: row.Kind)} cell — only int/fixed cells ease.");
        }

        RequireDeclared(
            value: dynamics.Row,
            declaredSet: dynamicsNames,
            path: cellPath,
            field: "dynamics.row",
            rowNoun: "dynamics",
            errors: errors
        );

        RequireNonNegativeEpoch(
            value: dynamics.EpochTick,
            name: $"{cellPath}.dynamics.epochTick",
            errors: errors
        );
    }
    /// <summary>Validates a state row's authored <see cref="WorldDraw"/> site — its own shape rules, then the shared
    /// site rule with the row's own envelope as the admissible domain.</summary>
    private static void ValidateDraw(WorldStateRow row, IReadOnlyList<WorldGeneratorRow>? generators, string path, List<string> errors) {
        if (row.Draw is not { } draw) {
            // A lattice row painted by a draw fill carries the same cursor/decks bookkeeping for its whole-field
            // passes (see WorldLatticeFill.Draw), so only a row with neither facet is refused here.
            var latticeDraws = (WorldLatticeFill.FindDraw(trait: row.Lattice) is not null);

            if ((row.DrawCursor != 0L) && !latticeDraws) {
                errors.Add(item: $"{path} ('{row.Name}') declares drawCursor without draw — drawCursor is engine bookkeeping for a draw site alone.");
            }

            if ((row.DrawDecks is { Count: > 0 }) && !latticeDraws) {
                errors.Add(item: $"{path} ('{row.Name}') declares drawDecks without draw — drawDecks is engine bookkeeping for a draw site alone.");
            }

            if (latticeDraws && (row.DrawCursor < 0L)) {
                errors.Add(item: $"{path} ('{row.Name}') drawCursor {row.DrawCursor} is negative.");
            }

            return;
        }

        if (row.Capacity is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares a draw beside capacity — a draw site is a scalar (slot) row; a keyed row has no ONE cell for a draw to fill.");
        }

        if (row.DrawCursor < 0L) {
            errors.Add(item: $"{path}.drawCursor {row.DrawCursor} is negative — a draw cursor is a non-negative sample count the engine only ever advances.");
        }

        var (domainLow, domainHigh) = (row.Kind switch {
            CellKind.Bool => (0L, 1L),
            // A fixed cell carries RAW FixedQ4816 bits and legitimately spans the whole long (see MaxIntCellValue).
            CellKind.Fixed => (long.MinValue, long.MaxValue),
            _ => (WorldStateCapacity.MinIntCellValue, WorldStateCapacity.MaxIntCellValue),
        });

        // The site's admissible domain is the row's OWN — the declared envelope and the non-negative floor included,
        // never just the kind's representable band.
        if (row.NonNegative) {
            domainLow = Math.Max(
                val1: domainLow,
                val2: 0L
            );
        }

        if (row.Min is { } declaredMinimum) {
            domainLow = Math.Max(
                val1: domainLow,
                val2: declaredMinimum
            );
        }

        if (row.Max is { } declaredMaximum) {
            domainHigh = Math.Min(
                val1: domainHigh,
                val2: declaredMaximum
            );
        }

        ValidateDrawSite(
            draw: draw,
            generators: generators,
            targetKind: row.Kind,
            bootOnly: false,
            domainLow: domainLow,
            domainHigh: domainHigh,
            path: $"{path}.draw",
            errors: errors
        );

        if (WorldGeneratorEngine.TryResolveSource(
            generators: generators,
            draw: draw,
            generator: out var dealtSource,
            reason: out _
        )) {
            ValidateDrawDecks(
                decks: row.DrawDecks,
                errors: errors,
                generator: dealtSource,
                path: $"{path}.drawDecks"
            );
        }
    }
    /// <summary>Refuses persisted dealt masks that do not fit the site's source — a mask left behind by an earlier
    /// source shape would otherwise be read as cards already dealt, silently skipping outcomes or declaring a bag
    /// dealt out early. Stated once, for a draw site and for a lattice row's draw fill alike.</summary>
    internal static void ValidateDrawDecks(WorldGenerator generator, IReadOnlyList<ClosedBitset256>? decks, string path, List<string> errors) {
        if (decks is not { Count: > 0 }) {
            return;
        }

        if (!WorldGeneratorEngine.Deals(source: generator.Source) || (generator.Mode == WorldGeneratorMode.WithReplacement)) {
            errors.Add(item: $"{path} carries {decks.Count} dealt mask(s) but the site's source never deals (source={WorldRefusalSpelling.GeneratorSource(source: generator.Source)}, mode={generator.Mode}) — clear drawDecks when re-authoring a site to a non-dealing source.");

            return;
        }

        if (generator.Source is WorldGeneratorSource.WeightedNumeric or WorldGeneratorSource.SymmetryOrbit) {
            if (decks.Count != 1) {
                errors.Add(item: $"{path} carries {decks.Count} dealt masks but a {WorldRefusalSpelling.GeneratorSource(source: generator.Source)} source deals through exactly one — clear drawDecks when re-authoring a site from a Markov source.");

                return;
            }

            var cards = ((generator.Source == WorldGeneratorSource.WeightedNumeric)
                ? CountCards(counts: (generator.Weighted ?? []).Select(selector: static outcome => outcome.Count))
                : (WorldGeneratorEngine.TryResolveOrbit(generator: generator, nodes: out var orbitNodes, reason: out _) ? orbitNodes.Length : WorldGeneratorCapacity.MaxCardsPerSet));

            RefuseMaskPastCards(
                cards: cards,
                errors: errors,
                mask: decks[0],
                path: $"{path}[0]"
            );

            return;
        }

        var contexts = (generator.Contexts ?? []);

        if (decks.Count > contexts.Count) {
            errors.Add(item: $"{path} carries {decks.Count} dealt masks but the Markov source declares {contexts.Count} context(s) — clear drawDecks when re-authoring a site's source.");

            return;
        }

        for (var index = 0; (index < decks.Count); index++) {
            RefuseMaskPastCards(
                cards: CountCards(counts: (contexts[index]?.Alternatives ?? []).Select(selector: static alternative => alternative.Count)),
                errors: errors,
                mask: decks[index],
                path: $"{path}[{index}]"
            );
        }
    }
    private static int CountCards(IEnumerable<int?> counts) {
        var cards = 0;

        foreach (var count in counts) {
            cards += Math.Max(val1: 1, val2: (count ?? 1));
        }

        return cards;
    }
    private static void RefuseMaskPastCards(ClosedBitset256 mask, int cards, string path, List<string> errors) {
        var bits = mask;

        if ((cards < WorldGeneratorCapacity.MaxCardsPerSet) && !bits.Fits(cards)) {
            errors.Add(item: $"{path} marks a card past the {cards} the source holds (mask 0x{bits}) — a stale deck from an earlier source shape; clear drawDecks.");
        }
    }
    /// <summary>
    /// Applies the one site rule — asked identically by a <c>state</c> draw row and by both boot-only field sites.
    /// Resolves the facet's source (named or inline), holds the pairing to the one kind predicate, refuses a source
    /// the site's timing cannot drive, and narrows the source's numeric domain against what the site can actually
    /// hold.
    /// </summary>
    /// <remarks>The domain narrowing is the difference between a refusal at authoring and a coin-flip refusal at
    /// boot: without it a draw whose shape the validator admits can produce a value the same validator refuses on the
    /// resolved document, so whether the world boots depends on what it rolled — a refusal that moves with the world
    /// seed and the instance identity. Refusing the authoring mismatch makes the door the type rather than the
    /// outcome.</remarks>
    /// <param name="draw">The site's authored facet.</param>
    /// <param name="generators">The document's declared sources, for reference resolution.</param>
    /// <param name="targetKind">The kind the site can hold.</param>
    /// <param name="bootOnly">Whether the site is a boot-only document field (see <see cref="WorldDrawSites"/>).</param>
    /// <param name="domainLow">The lowest numeric value the site admits (ignored for a text site).</param>
    /// <param name="domainHigh">The highest numeric value the site admits (ignored for a text site).</param>
    /// <param name="path">The document path this site reports under.</param>
    /// <param name="errors">The accumulating error list.</param>
    private static void ValidateDrawSite(WorldDraw draw, IReadOnlyList<WorldGeneratorRow>? generators, CellKind targetKind, bool bootOnly, long domainLow, long domainHigh, string path, List<string> errors) {
        if (!Enum.IsDefined(value: draw.Timing)) {
            errors.Add(item: $"{path}.timing '{draw.Timing}' is not a defined WorldDrawTiming.");
        } else if (
            bootOnly &&
            (draw.Timing != WorldDrawTiming.Boot)
        ) {
            errors.Add(item: $"{path}.timing={draw.Timing.ToString().ToLowerInvariant()} — this is a BOOT-ONLY document field, read once at composition; nothing could observe a later redraw, so only timing=boot is admissible here.");
        }

        if (!WorldGeneratorEngine.TryResolveSource(
            draw: draw,
            generator: out var generator,
            generators: generators,
            reason: out var resolveReason
        )) {
            errors.Add(item: $"{path} {resolveReason}.");

            return;
        }

        // An INLINE source is shaped here (a declared one was already shaped by ValidateGenerators) — the identical
        // rules either way, so inlining is sugar and never a second, laxer door.
        if (draw.Generator is not null) {
            ValidateSource(
                errors: errors,
                generator: generator,
                path: $"{path}.generator"
            );
        }

        // The ONE kind predicate, shared with every firing door.
        if (!WorldGeneratorEngine.TryCheckTargetKind(
            source: generator.Source,
            targetKind: targetKind,
            reason: out var kindReason
        )) {
            errors.Add(item: $"{path} {kindReason}.");

            return;
        }

        // A dealing source at a settle-and-clear boot site declares state across draws that this site can never have:
        // it draws once and its facet is erased, so the deck could not survive to be dealt from again.
        if (
            bootOnly &&
            (generator.Mode != WorldGeneratorMode.WithReplacement)
        ) {
            errors.Add(item: $"{path} draws from a source declaring mode={generator.Mode.ToString().ToLowerInvariant()} — a boot-only site draws once and its facet is cleared, so a deck has no second draw to deal into.");
        }

        if (WorldGeneratorEngine.WritesText(source: generator.Source)) {
            return;
        }

        ValidateSourceDomain(
            domainHigh: domainHigh,
            domainLow: domainLow,
            errors: errors,
            generator: generator,
            path: path,
            targetKind: targetKind
        );
    }
    /// <summary>Validates the document's <c>generators</c> section — the declared stochastic sources sites reference
    /// by name. A source is a pure shape here; whether any particular site may draw from it (kind, timing) is the
    /// site's question, asked in <see cref="ValidateDrawSite"/>, because the same source is legitimately shared by
    /// sites that answer it differently.</summary>
    private static void ValidateGenerators(IReadOnlyList<WorldGeneratorRow>? generators, List<string> errors) {
        var rows = (generators ?? []);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (rows.Count > WorldGeneratorCapacity.MaxDeclaredSources) {
            errors.Add(item: $"generators count {rows.Count} exceeds the maximum of {WorldGeneratorCapacity.MaxDeclaredSources}.");
        }

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"generators[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!names.Add(item: row.Name)) {
                errors.Add(item: $"{path}.name '{row.Name}' is duplicated — a site resolves a source by name, so two rows under one name resolve nothing honestly.");
            }

            if (row.Name.Value.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldStateRow.ReservedNamePrefix
            )) {
                errors.Add(item: $"{path}.name '{row.Name}' starts with the reserved prefix '{WorldStateRow.ReservedNamePrefix}'.");
            }

            if (row.Generator is null) {
                errors.Add(item: $"{path}.generator is required.");

                continue;
            }

            ValidateSource(
                generator: row.Generator,
                path: path,
                errors: errors
            );
        }
    }
    private static void ValidateMarkovSource(WorldGenerator generator, string path, List<string> errors) {
        if (generator.Contexts is not { Count: > 0 } contexts) {
            errors.Add(item: $"{path}.contexts must declare at least one context for source=markov.");

            return;
        }

        if (contexts.Count > WorldGeneratorCapacity.MaxContexts) {
            errors.Add(item: $"{path}.contexts count {contexts.Count} exceeds the maximum of {WorldGeneratorCapacity.MaxContexts}.");
        }

        if (
            (generator.Bound < 1) ||
            (generator.Bound > WorldGeneratorCapacity.MaxEmissionBound)
        ) {
            errors.Add(item: $"{path}.bound {generator.Bound} must be between 1 and {WorldGeneratorCapacity.MaxEmissionBound}.");
        }

        var keys = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < contexts.Count); index++) {
            var context = contexts[index];
            var contextPath = $"{path}.contexts[{index}]";

            if (context is null) {
                errors.Add(item: $"{contextPath} is required.");

                continue;
            }

            if (!keys.Add(item: context.Key)) {
                errors.Add(item: $"{contextPath}.key '{context.Key}' is duplicated.");
            }

            if (context.Key.Value.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldStateRow.ReservedNamePrefix
            )) {
                errors.Add(item: $"{contextPath}.key '{context.Key}' starts with the reserved prefix '{WorldStateRow.ReservedNamePrefix}'.");
            }

            var alternatives = (context.Alternatives ?? []);

            if (alternatives.Count > WorldGeneratorCapacity.MaxAlternativesPerContext) {
                errors.Add(item: $"{contextPath}.alternatives count {alternatives.Count} exceeds the maximum of {WorldGeneratorCapacity.MaxAlternativesPerContext} (one deck-mask bit per alternative).");
            }

            var anyWeight = false;
            var cards = 0L;

            for (var alternative = 0; (alternative < alternatives.Count); alternative++) {
                var entry = alternatives[alternative];
                var entryPath = $"{contextPath}.alternatives[{alternative}]";

                if (entry is null) {
                    errors.Add(item: $"{entryPath} is required.");

                    continue;
                }

                if (string.IsNullOrEmpty(value: entry.Token)) {
                    errors.Add(item: $"{entryPath}.token must be non-empty.");
                } else if (entry.Token.Length > WorldGeneratorCapacity.MaxTokenLength) {
                    errors.Add(item: $"{entryPath}.token length {entry.Token.Length} exceeds the maximum of {WorldGeneratorCapacity.MaxTokenLength}.");
                }

                if (entry.Count is { } count && (count < 1)) {
                    errors.Add(item: $"{entryPath}.count {count} must be at least 1 — the cards one deck pass holds of this alternative.");
                }

                cards += Math.Max(val1: 1, val2: (entry.Count ?? 1));
                anyWeight |= (entry.Weight != 0UL);
            }

            if (cards > WorldGeneratorCapacity.MaxCardsPerSet) {
                errors.Add(item: $"{contextPath}.alternatives hold {cards} cards counting each alternative's count, exceeding the {WorldGeneratorCapacity.MaxCardsPerSet} a deck mask can deal.");
            }

            if (
                (alternatives.Count > 0) &&
                !anyWeight
            ) {
                errors.Add(item: $"{contextPath}.alternatives declare no non-zero weight — a context that can pick nothing is a stall, not a terminal (a terminal context declares NO alternatives).");
            }
        }

        // Next-context and start resolution run in a second pass so a forward reference is legitimate.
        if (generator.Start is not { } start) {
            errors.Add(item: $"{path} declares source=markov without 'start'.");
        } else if (!keys.Contains(item: start)) {
            errors.Add(item: $"{path}.start '{start}' names no declared context.");
        }

        for (var index = 0; (index < contexts.Count); index++) {
            foreach (var entry in ((contexts[index]?.Alternatives) ?? [])) {
                if (
                    (entry is not null) &&
                    !keys.Contains(item: entry.Next)
                ) {
                    errors.Add(item: $"{path}.contexts[{index}] alternative '{entry.Token}' names next context '{entry.Next}', which is not declared.");
                }
            }
        }
    }
    /// <summary>Validates one <see cref="WorldGenerator"/>'s own shape — dispatching on
    /// <see cref="WorldGenerator.Source"/>, since each source owns a disjoint field set. Shared by a declared source
    /// row and a site's inline source, so the two spellings are held to the identical rules rather than to two
    /// readings of them.</summary>
    private static void ValidateSource(WorldGenerator generator, string path, List<string> errors) {
        if (!Enum.IsDefined(value: generator.Source)) {
            errors.Add(item: $"{path}.source '{generator.Source}' is not a defined WorldGeneratorSource.");

            return;
        }

        if (!Enum.IsDefined(value: generator.Mode)) {
            errors.Add(item: $"{path}.mode '{generator.Mode}' is not a defined WorldGeneratorMode.");
        }

        // Each source's fields are BOTH-OR-NEITHER against the fields the others own — a foreign field present is
        // refused by name rather than silently ignored, the same "no dual discriminator" discipline WorldStateRow
        // itself already follows for value/cells.
        var declaresMarkovFields = ((generator.Start is not null) || (generator.Contexts is not null));
        var declaresRangeFields = ((generator.RangeMin is not null) || (generator.RangeMax is not null));
        var declaresWeighted = (generator.Weighted is not null);
        var declaresOrbitFields = ((generator.Ring is not null) || (generator.Node is not null) || (generator.Word is not null));

        if (declaresOrbitFields && (generator.Source != WorldGeneratorSource.SymmetryOrbit)) {
            errors.Add(item: $"{path} declares source={WorldRefusalSpelling.GeneratorSource(source: generator.Source)} beside ring/node/word, which belong to source=symmetryOrbit.");
        }

        // Bound is Markov-only and Mode belongs to the dealing shapes, but both are NON-NULLABLE, so the
        // both-or-neither sweep above cannot see them and a source carrying one it does not read would parse,
        // validate, and then be silently ignored at fire time. Refused against the DECLARED DEFAULT — the most a
        // non-nullable field can distinguish, and exactly the set of values that could mislead.
        if (
            !WorldGeneratorEngine.Deals(source: generator.Source) &&
            (generator.Mode != WorldGeneratorMode.WithReplacement)
        ) {
            errors.Add(item: $"{path}.source={WorldRefusalSpelling.GeneratorSource(source: generator.Source)} declares mode={generator.Mode.ToString().ToLowerInvariant()} — only markov, weightedNumeric and symmetryOrbit deal; uniformRange and streamDraw have no entry set to deal from.");
        }

        if (generator.Source != WorldGeneratorSource.Markov) {
            if (generator.Bound != WorldGenerator.DefaultBound) {
                errors.Add(item: $"{path}.source={WorldRefusalSpelling.GeneratorSource(source: generator.Source)} declares bound {generator.Bound} — a numeric source is always exactly ONE draw, and 'bound' belongs to source=markov.");
            }

        }

        switch (generator.Source) {
            case WorldGeneratorSource.Markov:
                if (declaresRangeFields) {
                    errors.Add(item: $"{path} declares source=markov beside rangeMin/rangeMax, which belong to source=uniformRange.");
                }

                if (declaresWeighted) {
                    errors.Add(item: $"{path} declares source=markov beside 'weighted', which belongs to source=weightedNumeric.");
                }

                ValidateMarkovSource(
                    errors: errors,
                    generator: generator,
                    path: path
                );

                return;
            case WorldGeneratorSource.UniformRange:
                if (declaresMarkovFields) {
                    errors.Add(item: $"{path} declares source=uniformRange beside start/contexts, which belong to source=markov.");
                }

                if (declaresWeighted) {
                    errors.Add(item: $"{path} declares source=uniformRange beside 'weighted', which belongs to source=weightedNumeric.");
                }

                if (
                    (generator.RangeMin is not { } rangeMin) ||
                    (generator.RangeMax is not { } rangeMax)
                ) {
                    errors.Add(item: $"{path} declares source=uniformRange without both rangeMin and rangeMax — a range is authored as a pair or not at all.");

                    return;
                }

                if (
                    (rangeMin < WorldGeneratorCapacity.MinRangeBound) ||
                    (rangeMin > WorldGeneratorCapacity.MaxRangeBound)
                ) {
                    errors.Add(item: $"{path}.rangeMin {rangeMin} must be between {WorldGeneratorCapacity.MinRangeBound} and {WorldGeneratorCapacity.MaxRangeBound}.");
                }

                if (
                    (rangeMax < WorldGeneratorCapacity.MinRangeBound) ||
                    (rangeMax > WorldGeneratorCapacity.MaxRangeBound)
                ) {
                    errors.Add(item: $"{path}.rangeMax {rangeMax} must be between {WorldGeneratorCapacity.MinRangeBound} and {WorldGeneratorCapacity.MaxRangeBound}.");
                }

                if (rangeMin > rangeMax) {
                    errors.Add(item: $"{path}.rangeMin {rangeMin} exceeds rangeMax {rangeMax}.");
                }

                return;
            case WorldGeneratorSource.WeightedNumeric:
                if (declaresMarkovFields) {
                    errors.Add(item: $"{path} declares source=weightedNumeric beside start/contexts, which belong to source=markov.");
                }

                if (declaresRangeFields) {
                    errors.Add(item: $"{path} declares source=weightedNumeric beside rangeMin/rangeMax, which belong to source=uniformRange.");
                }

                if (generator.Weighted is not { Count: > 0 } outcomes) {
                    errors.Add(item: $"{path}.weighted must declare at least one outcome for source=weightedNumeric.");

                    return;
                }

                if (outcomes.Count > WorldGeneratorCapacity.MaxWeightedOutcomes) {
                    errors.Add(item: $"{path}.weighted count {outcomes.Count} exceeds the maximum of {WorldGeneratorCapacity.MaxWeightedOutcomes}.");
                }

                var anyOutcomeWeight = false;
                var outcomeCards = 0L;

                for (var index = 0; (index < outcomes.Count); index++) {
                    if (outcomes[index] is null) {
                        errors.Add(item: $"{path}.weighted[{index}] is required.");

                        continue;
                    }

                    if (outcomes[index].Count is { } outcomeCount && (outcomeCount < 1)) {
                        errors.Add(item: $"{path}.weighted[{index}].count {outcomeCount} must be at least 1 — the cards one deck pass holds of this outcome.");
                    }

                    outcomeCards += Math.Max(val1: 1, val2: (outcomes[index].Count ?? 1));
                    anyOutcomeWeight |= (outcomes[index].Weight != 0UL);
                }

                if (outcomeCards > WorldGeneratorCapacity.MaxCardsPerSet) {
                    errors.Add(item: $"{path}.weighted holds {outcomeCards} cards counting each outcome's count, exceeding the {WorldGeneratorCapacity.MaxCardsPerSet} a deck mask can deal.");
                }

                if (!anyOutcomeWeight) {
                    errors.Add(item: $"{path}.weighted declares no non-zero weight — a table that can pick nothing is a stall.");
                }

                return;
            case WorldGeneratorSource.StreamDraw:
                if (
                    declaresMarkovFields ||
                    declaresRangeFields ||
                    declaresWeighted
                ) {
                    errors.Add(item: $"{path} declares source=streamDraw beside start/contexts/rangeMin/rangeMax/weighted — a stream draw reads none of them.");
                }

                return;
            case WorldGeneratorSource.SymmetryOrbit:
                if (
                    declaresMarkovFields ||
                    declaresRangeFields ||
                    declaresWeighted
                ) {
                    errors.Add(item: $"{path} declares source=symmetryOrbit beside start/contexts/rangeMin/rangeMax/weighted — an orbit source reads only ring, or node with an optional word.");
                }

                if (!WorldGeneratorEngine.TryResolveOrbit(
                    generator: generator,
                    nodes: out _,
                    reason: out var orbitReason
                )) {
                    errors.Add(item: $"{path} {orbitReason}.");
                }

                return;
            default:
                return;
        }
    }
    /// <summary>Narrows a numeric source's own declared band against the site's admissible domain — see
    /// <see cref="ValidateDrawSite"/>'s remarks for why this is an authoring refusal rather than a boot-time
    /// one.</summary>
    private static void ValidateSourceDomain(WorldGenerator generator, CellKind targetKind, long domainLow, long domainHigh, string path, List<string> errors) {
        switch (generator.Source) {
            case WorldGeneratorSource.UniformRange:
                if (
                    (generator.RangeMin is { } rangeMin) &&
                    (generator.RangeMax is { } rangeMax) &&
                    ((rangeMin < domainLow) || (rangeMax > domainHigh))
                ) {
                    errors.Add(item: $"{path} draws {rangeMin}..{rangeMax}, which is outside the site's admissible domain {domainLow}..{domainHigh}.");
                }

                break;
            case WorldGeneratorSource.WeightedNumeric:
                foreach (var outcome in (generator.Weighted ?? [])) {
                    if (
                        (outcome is not null) &&
                        ((outcome.Value < domainLow) || (outcome.Value > domainHigh))
                    ) {
                        errors.Add(item: $"{path} draws outcome {outcome.Value}, which is outside the site's admissible domain {domainLow}..{domainHigh}.");
                    }
                }

                break;
            case WorldGeneratorSource.StreamDraw:
                // A raw draw's band is the generator's own and nothing narrows it, so a site that cannot hold the
                // whole 32-bit band is refused HERE rather than by whatever it happened to roll.
                if (
                    (domainLow > 0L) ||
                    (domainHigh < uint.MaxValue)
                ) {
                    errors.Add(item: $"{path} draws source=streamDraw, whose raw band 0..{uint.MaxValue} is outside the site's admissible domain {domainLow}..{domainHigh} — author a uniformRange or weightedNumeric source inside the site's range.");
                }

                break;
            case WorldGeneratorSource.SymmetryOrbit:
                if (WorldGeneratorEngine.TryResolveOrbit(
                    generator: generator,
                    nodes: out var orbitNodes,
                    reason: out _
                )) {
                    foreach (var node in orbitNodes) {
                        var encoded = WorldGeneratorEngine.EncodeNode(node: node, targetKind: targetKind);

                        if ((encoded < domainLow) || (encoded > domainHigh)) {
                            errors.Add(item: $"{path} draws node {node}, which is outside the site's admissible domain {DescribeValue(kind: targetKind, raw: domainLow)}..{DescribeValue(kind: targetKind, raw: domainHigh)} — an orbit site holds node indices 0..{SymmetryLattice.NodeCount - 1} in its own unit.");

                            break;
                        }
                    }
                }

                break;
            default:
                break;
        }
    }
    // The state section: schema cap (MaxRows), name uniqueness, both-or-neither Min/Max range (applied to every
    // cell's value), text-cell length against MaxTextValueLength, and per-row cell-count ceiling (MaxCellsPerRow,
    // optionally narrowed by an authored Capacity). WorldCellName already refuses an empty/unsafe/dotted row name at
    // JSON parse, so this pass checks only uniqueness. Returns the declared rows by name so ValidateHud can refuse
    // an unknown state.<row>/state.<row>.<key> binding.
    private static Dictionary<string, WorldStateRow> ValidateState(IReadOnlyList<WorldStateRow> rows, IReadOnlyList<WorldGeneratorRow>? generators, ISet<string> dynamicsNames, List<string> errors) {
        var byName = new Dictionary<string, WorldStateRow>(comparer: StringComparer.Ordinal);

        if (rows is null) {
            errors.Add(item: "state is required.");

            return byName;
        }

        if (rows.Count > WorldStateCapacity.MaxRows) {
            errors.Add(item: $"state count {rows.Count} exceeds the maximum of {WorldStateCapacity.MaxRows}.");
        }

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"state[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!byName.TryAdd(
                key: row.Name,
                value: row
            )) {
                errors.Add(item: $"{path}.name '{row.Name}' is duplicated.");
            }

            // A lattice-shaped row is per-cell fixed-point substrate: its cells live in the lattice (checkpointed,
            // snapshot-delivered), never as authored slot/keyed cells, and every keyed-row trait is refused at this
            // door so the shape cannot be held by convention.
            if (row.Lattice is not null) {
                if (row.Kind != CellKind.Fixed) {
                    errors.Add(item: $"{path} ('{row.Name}') declares a lattice trait with kind '{row.Kind}' — a lattice row is kind 'fixed'.");
                }
                if (row.Cells is { Count: > 0 }) {
                    errors.Add(item: $"{path} ('{row.Name}') declares both a lattice trait and cells — a lattice row's cells are the lattice's.");
                }
                if (row.Capacity is not null) {
                    errors.Add(item: $"{path} ('{row.Name}') declares both a lattice trait and capacity — the topology sizes a lattice row.");
                }
                if (row.Advance is not null) {
                    errors.Add(item: $"{path} ('{row.Name}') declares both a lattice trait and advance.");
                }
                if (row.Dynamics is not null) {
                    errors.Add(item: $"{path} ('{row.Name}') declares both a lattice trait and dynamics.");
                }
                if (row.Cycle is not null) {
                    errors.Add(item: $"{path} ('{row.Name}') declares both a lattice trait and cycle.");
                }
                if (row.Draw is not null) {
                    errors.Add(item: $"{path} ('{row.Name}') declares both a lattice trait and a draw facet — a lattice row draws per cell through a 'draw' entry in its paint, never through the slot-row facet.");
                }
            }

            // The reserved prefix is ENGINE-MINTED ONLY, and the rule lives HERE — in the validator every ingress
            // passes (boot, live mutation, undo replay), never in one door a hand-authored file walks around.
            // Nothing mints a state ROW, so the prefix is refused outright on a row name; that is also what keeps a
            // reserved rule channel ($tick/$population/$region:) from ever being shadowed by a real row.
            if (row.Name.Value.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldStateRow.ReservedNamePrefix
            )) {
                errors.Add(item: $"{path}.name '{row.Name}' starts with the reserved prefix '{WorldStateRow.ReservedNamePrefix}' — reserved for engine-minted names and the rules section's own channels ({WorldRuleFacts.Tick}, {WorldRuleFacts.Population}, {WorldRuleFacts.RegionPrefix}<placementId>).");
            }

            if (!Enum.IsDefined(value: row.Kind)) {
                errors.Add(item: $"{path}.kind '{row.Kind}' is not a defined CellKind.");

                continue;
            }

            var numeric = ((row.Kind == CellKind.Int) || (row.Kind == CellKind.Fixed));

            // Min/Max/NonNegative are envelope traits over a NUMBER — legitimate only for Int/Fixed, the same rule a
            // scalar row's range always followed, now stated once instead of per case.
            if (
                !numeric &&
                ((row.Min is not null) || (row.Max is not null))
            ) {
                errors.Add(item: $"{path} ('{row.Name}') declares min/max on a {WorldRefusalSpelling.Kind(kind: row.Kind)} row — only int/fixed rows carry a range.");
            } else if ((row.Min is null) != (row.Max is null)) {
                errors.Add(item: $"{path} declares only one of min/max — a range is authored as a pair or not at all.");
            } else if (
                (row.Min is { } lo) &&
                (row.Max is { } hi) &&
                (lo >= hi)
            ) {
                errors.Add(item: $"{path} min {DescribeValue(
                    kind: row.Kind,
                    raw: lo
                )} must be less than max {DescribeValue(
                    kind: row.Kind,
                    raw: hi
                )}.");
            }

            if (
                !numeric &&
                row.NonNegative
            ) {
                errors.Add(item: $"{path} ('{row.Name}') declares nonNegative on a {WorldRefusalSpelling.Kind(kind: row.Kind)} row — only int/fixed rows carry a floor.");
            }

            // GatesDrive is the composition-lane's drive-admission gate (WorldGrants.TryGetDriveGate) — a nonzero
            // per-body cell there refuses that body's drive/action intents regardless of any grant held. It reads a
            // cell as zero/nonzero, so a text row has no honest reading for it, and it is read per BODY (one cell
            // per entity index), so only a keyed (table) row — one declaring Capacity — has a body to address; a
            // slot has exactly one value shared by every body, which is not what a per-body gate means.
            if (
                (row.Kind == CellKind.Text) &&
                row.GatesDrive
            ) {
                errors.Add(item: $"{path} ('{row.Name}') declares gatesDrive on a text row — a drive gate reads a cell as zero/nonzero, which a text cell has no honest reading for.");
            }

            if (
                row.GatesDrive &&
                (row.Capacity is null)
            ) {
                errors.Add(item: $"{path} ('{row.Name}') declares gatesDrive without a capacity — a drive gate is read per body (one cell keyed by the body's entity index), which only a keyed (table) row can carry; a slot has no ONE body to gate.");
            }

            // Evicts is the row's own overflow policy: drop-oldest instead of refuse. It reads exactly one bound —
            // Capacity — so the only shape it can legitimately name is a keyed row that declares one; a slot never
            // declares Capacity (WorldStateRow.IsSlot), so this one check refuses both "no capacity at all" and "on a
            // slot row" by the same name, with the remedy spelled out.
            if (
                row.Evicts &&
                (row.Capacity is null)
            ) {
                errors.Add(item: $"{path} ('{row.Name}') declares evicts without a capacity — eviction drops the oldest cell once a write would exceed the declared bound, which only a keyed (table) row declaring capacity can carry; a slot has no bound to evict against. Declare a capacity, or drop evicts.");
            }

            if (
                (row.Capacity is { } declaredCapacity) &&
                ((declaredCapacity < 1) || (declaredCapacity > row.CellCeiling))
            ) {
                errors.Add(item: $"{path}.capacity {declaredCapacity} must be between 1 and {row.CellCeiling}.");
            }

            ValidateDraw(
                errors: errors,
                generators: generators,
                path: path,
                row: row
            );
            ValidateAdvance(
                errors: errors,
                numeric: numeric,
                path: path,
                row: row
            );
            ValidateDynamicsTrait(
                dynamicsNames: dynamicsNames,
                errors: errors,
                numeric: numeric,
                path: path,
                row: row
            );
            ValidateCycle(
                errors: errors,
                numeric: numeric,
                path: path,
                row: row
            );
            var effectiveCapacity = Math.Clamp(
                value: (row.Capacity ?? row.CellCeiling),
                min: 1,
                max: row.CellCeiling
            );
            var cells = (row.Cells ?? []);

            if (cells.Count > effectiveCapacity) {
                errors.Add(item: $"{path} ('{row.Name}') cell count {cells.Count} exceeds its capacity of {effectiveCapacity}.");
            }

            // The reserved slot key is the `value` sugar's own address — a keyed row (a declared Capacity, or more
            // than one cell) may never use it as one of its own keys, or the sugar and an authored key could address
            // the same cell two ways and disagree about which shape they named.
            var reservesSlotKey = ((row.Capacity is not null) || (cells.Count != 1));

            var keys = new HashSet<string>(comparer: StringComparer.Ordinal);
            var rangeDeclared = (numeric && (row.Min is { } rangeLo) && (row.Max is { } rangeHi) && (rangeLo < rangeHi));

            for (var cellIndex = 0; (cellIndex < cells.Count); cellIndex++) {
                var cell = cells[cellIndex];
                var cellPath = $"{path}.cells[{cellIndex}]";

                if (cell is null) {
                    errors.Add(item: $"{cellPath} is required.");

                    continue;
                }

                // A cell key can no longer be empty, dotted, or otherwise unsafe — WorldCellName refuses that at JSON
                // parse, before this method ever sees the cell — so this checks only uniqueness and the reserved key.
                if (!keys.Add(item: cell.Key)) {
                    errors.Add(item: $"{path} ('{row.Name}') key '{cell.Key}' is duplicated.");
                } else if (
                    reservesSlotKey &&
                    (cell.Key == WorldStateRow.SlotKey)
                ) {
                    errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' uses the reserved slot key '{WorldStateRow.SlotKey}' as an authored cell key.");
                } else if (!WorldStateReservedCells.TryValidateReservedCell(
                    row: row,
                    key: cell.Key,
                    reason: out var reservedReason
                )) {
                    // Any other reserved-prefix key is refused unless it is exactly the engine-minted key legitimate
                    // for this row's shape, carrying a value the engine could have written (a non-negative cursor; a
                    // deck mask inside its context's alternative count under a deck mode). The rule lives in
                    // WorldGeneratorCells so UpsertStateCell's compose arm refuses the identical shape from the
                    // identical code.
                    errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' {reservedReason}.");
                }

                if (
                    (cell.Advance is not null) &&
                    (cell.Dynamics is not null)
                ) {
                    errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares both advance and dynamics — a cell is a linear accumulator or a second-order easing cell, never both.");
                }

                if ((cell.Cycle is not null) && ((cell.Advance is not null) || (cell.Dynamics is not null))) {
                    errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares cycle beside advance or dynamics — a cell is a linear accumulator, a second-order easing cell or a tick-indexed rotation, never two of them.");
                }

                if (cell.Cycle is { } cellCycle) {
                    ValidateCellCycle(
                        cell: cell,
                        cellPath: cellPath,
                        cycle: cellCycle,
                        errors: errors,
                        numeric: numeric,
                        row: row
                    );
                }

                if (cell.Advance is { } cellAdvance) {
                    ValidateCellAdvance(
                        advance: cellAdvance,
                        cell: cell,
                        cellPath: cellPath,
                        errors: errors,
                        numeric: numeric,
                        row: row
                    );
                }

                if (cell.Dynamics is { } cellDynamics) {
                    ValidateCellDynamics(
                        cell: cell,
                        cellPath: cellPath,
                        dynamics: cellDynamics,
                        dynamicsNames: dynamicsNames,
                        errors: errors,
                        numeric: numeric,
                        row: row
                    );
                }

                if (
                    (cell.Provenance is { } provenance) &&
                    (provenance.Length > WorldStateCapacity.MaxProvenanceLength)
                ) {
                    errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' provenance length {provenance.Length} exceeds the maximum of {WorldStateCapacity.MaxProvenanceLength}.");
                }

                if (row.Kind == CellKind.Text) {
                    if (cell.Text is null) {
                        errors.Add(item: $"{cellPath}.text is required.");
                    } else if (cell.Text.Length > WorldStateCapacity.MaxTextValueLength) {
                        errors.Add(item: $"{path} ('{row.Name}') text value length {cell.Text.Length} exceeds the maximum of {WorldStateCapacity.MaxTextValueLength}.");
                    }

                    continue;
                }

                if (row.Kind == CellKind.Bool) {
                    if (cell.Value is not (0 or 1)) {
                        errors.Add(item: $"{cellPath}.value {cell.Value} must be 0 or 1 for a bool row.");
                    }

                    continue;
                }

                // Int/Fixed: the row's DECLARED non-negative floor (enforced regardless of any authored Min — this is
                // what "timer" meant before the kind vocabularies reconciled), then the declared range. This walk is
                // the floor's authority; the cross-document write-back channel (Server.WorldOwnedWorlds.Decide) reads
                // the SAME row trait at its own door precisely so it can never admit a value this walk would refuse
                // at the owned world's next boot.
                if (
                    row.NonNegative &&
                    (cell.Value < 0)
                ) {
                    errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' value {DescribeValue(
                        kind: row.Kind,
                        raw: cell.Value
                    )} is negative — this row's floor is non-negative.");
                }

                if (
                    rangeDeclared &&
                    ((cell.Value < row.Min) || (cell.Value > row.Max))
                ) {
                    errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' value {DescribeValue(
                        kind: row.Kind,
                        raw: cell.Value
                    )} is outside its declared range {DescribeValue(
                        kind: row.Kind,
                        raw: row.Min!.Value
                    )}..{DescribeValue(
                        kind: row.Kind,
                        raw: row.Max!.Value
                    )}.");
                }

                // An INT cell is read as fixed point wherever the engine reads it at all (a world rule's gate, its
                // comparand, its live copy operand), and that lift throws outside FixedQ4816's integer band. Refused
                // HERE so every ingress meets the same door: without it an ordinary authored number kills the process
                // on the first tick a rule touches the row, with no refusal anywhere. A FIXED cell carries raw bits
                // and spans the whole long, so it is exempt by construction.
                if (
                    (row.Kind == CellKind.Int) &&
                    ((cell.Value < WorldStateCapacity.MinIntCellValue) || (cell.Value > WorldStateCapacity.MaxIntCellValue))
                ) {
                    errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' value {cell.Value} is outside the representable int range {WorldStateCapacity.MinIntCellValue}..{WorldStateCapacity.MaxIntCellValue} — every engine read of an int cell lifts it to fixed point.");
                }
            }
        }

        return byName;
    }
}
