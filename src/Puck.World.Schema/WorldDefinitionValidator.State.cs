using System.Globalization;
using Puck.Maths;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    /// <summary>Validates the one row an <c>UpsertStateCell</c>/<c>RemoveStateCell</c> mutation touched — the same
    /// per-row and cross-row checks <see cref="TryValidateTouchedStateRows"/> runs for any state mutation, with no
    /// rule, interaction, pattern, table, search plan, or flock affinity compiled.</summary>
    public static bool TryValidateRuntimeStateCell(WorldDefinition definition, string rowName, string key, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (
            (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: rowName
        ) is not { } row) ||
            !CellName.TryParse(
            candidate: key,
            name: out var cellKey,
            reason: out _
        ) ||
            (StateRows.FindCell(
            cells: row.Cells,
            key: cellKey
        ) is null)
        ) {
            reason = $"state row '{rowName}' cell '{key}' did not resolve after composition";

            return false;
        }

        return TryValidateTouchedStateRows(
            definition: definition,
            reason: out reason,
            rowNames: [rowName]
        );
    }

    // Fixed-kind values speak DECIMAL in refusal text — never the raw Q48.16 bit pattern — matching the document
    // JSON, console verb, and read-back conventions the same value crosses.
    private static string DescribeValue(CellKind kind, long raw) =>
        ((kind == CellKind.Fixed)
            ? FixedQ4816.FromRawBits(value: raw).ToString()
            : raw.ToString(provider: CultureInfo.InvariantCulture)
        );
    private static void ValidateAdvanceFields(StateAdvance advance, string path, List<string> errors) {
        if (advance.PerSecondDenominator <= 0) {
            errors.Add(item: $"{path}.advance.perSecondDenominator {advance.PerSecondDenominator} must be positive.");
        }
    }
    private static void ValidateDynamicsFields(StateDynamics dynamics, ISet<string> names, string path, List<string> errors) {
        RequireDeclared(
            value: dynamics.Row,
            declaredSet: names,
            path: path,
            field: "dynamics.row",
            rowNoun: "dynamics",
            errors: errors
        );
    }
    // The timing state every effective behavior reads off the cell it governs (see StateCellClock) — an epoch never
    // negative, and (for a cycling cell alone) a substep remainder inside its cycle's own step length.
    private static void ValidateClock(StateCellClock? clock, StateCycle? effectiveCycle, string path, List<string> errors) {
        if (clock is not { } c) {
            return;
        }

        RequireNonNegativeEpoch(
            c.EpochTick,
            $"{path}.epochTick",
            errors
        );
        RequireNonNegativeEpoch(
            c.EpochEngineTick,
            $"{path}.epochEngineTick",
            errors
        );

        if (
            (effectiveCycle is { } cycle) &&
            ((c.SubstepTicks < 0) || (c.SubstepTicks >= cycle.TicksPerStep))
        ) {
            errors.Add(item: $"{path}.substepTicks {c.SubstepTicks} must be in 0..ticksPerStep-1.");
        }
    }
    /// <summary>Validates a row's authored <see cref="StateAdvance"/> continuous-accumulation trait. Whether
    /// reaching a declared envelope bound clamps the computed value (it never rewrites the stored base/epoch) is the
    /// settled read-side half of the envelope duality, documented on <see cref="StateAdvance"/> itself and not a
    /// validator concern — this method refuses only shapes the read side could not honestly compute over.</summary>
    private static void ValidateAdvance(WorldStateRow row, bool numeric, string path, List<string> errors) {
        if (row.Advance is not { } advance) {
            return;
        }

        if (row.Draw is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares both draw and advance — a row is an authored-randomness draw site or a continuous accumulator, never both.");
        }

        if (!numeric) {
            errors.Add(item: $"{path} ('{row.Name}') declares advance on a {StateSpelling.Kind(kind: row.Kind)} row — only int/fixed rows accumulate.");
        }

        ValidateAdvanceFields(
            advance: advance,
            errors: errors,
            path: path
        );
    }
    /// <summary>Validates one cell's own <see cref="StateAdvance"/> — the keyed counterpart of
    /// <see cref="ValidateAdvance"/>, stated separately because it governs the opposite shape: a cell inside a
    /// table rather than a row's own slot. The two never overlap by construction (this rejects the slot key
    /// outright), so a cell's advance and its row's advance can never both claim the same cell.</summary>
    private static void ValidateCellAdvance(WorldStateRow row, StateCell cell, StateAdvance advance, bool numeric, string cellPath, List<string> errors) {
        // The slot key's own accumulation is authored at the ROW level (beside 'value'), never here — refusing this
        // combination outright is what keeps "which advance governs the slot cell" from ever being two mechanisms
        // reading the same address.
        if (cell.Key == WorldStateRow.SlotKey) {
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares its own advance on the reserved slot key — a scalar row's accumulation is authored at the ROW level ('advance' beside 'value'), never on the cell itself.");
        }

        if (!numeric) {
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares advance on a {StateSpelling.Kind(kind: row.Kind)} cell — only int/fixed cells accumulate.");
        }

        ValidateAdvanceFields(
            advance: advance,
            errors: errors,
            path: cellPath
        );
    }
    /// <summary>Validates a row's authored <see cref="StateCycle"/> rotation trait: the generator, power and step length the
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

        if (row.GatesDrive) {
            errors.Add(item: $"{path} ('{row.Name}') declares cycle on a gatesDrive row — a drive gate is resolved once per install, so a cell that turns with the tick would gate on a stale value; drive the gate through an explicit write instead.");
        }

        ValidateCycleShape(
            cycle: cycle,
            errors: errors,
            kind: row.Kind,
            numeric: numeric,
            path: $"{path}.cycle",
            subject: $"{path} ('{row.Name}')"
        );

        // Every cell that INHERITS this default (declares no override of its own) reads it as its effective
        // behavior, so a lattice output's node-range check runs over each one — the row-level counterpart of
        // ValidateCellCycle's identical check for a cell's own cycle.
        if (StateCycle.IsLatticeOutput(output: cycle.Output)) {
            foreach (var cell in (row.Cells ?? [])) {
                if (
                    (cell.Advance is not null) ||
                    (cell.Dynamics is not null) ||
                    (cell.Cycle is not null) ||
                    (cell.Behavior == StateCellBehavior.None)
                ) {
                    continue;
                }

                ValidateLatticeCell(
                    cell: cell,
                    cycle: cycle,
                    errors: errors,
                    row: row,
                    subject: $"{path} ('{row.Name}') cell '{cell.Key}'"
                );
            }
        }
    }
    /// <summary>Validates one cell's own <see cref="StateCycle"/> — the keyed counterpart of
    /// <see cref="ValidateCycle"/>, refusing the slot key so a cell's cycle and its row's cycle can never both claim
    /// the same cell.</summary>
    private static void ValidateCellCycle(WorldStateRow row, StateCell cell, StateCycle cycle, bool numeric, string cellPath, List<string> errors) {
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

        ValidateLatticeCell(
            cell: cell,
            cycle: cycle,
            errors: errors,
            row: row,
            subject: $"{cellPath} ('{row.Name}'.'{cell.Key}')"
        );
    }
    private static void ValidateLatticeCell(WorldStateRow row, StateCell cell, StateCycle cycle, string subject, List<string> errors) {
        if (
            StateCycle.IsLatticeOutput(output: cycle.Output) &&
            (StateCycle.Phase(
            baseValue: cell.Value.Raw,
            kind: row.Kind
        ) is < 0 or >= SymmetryLattice.NodeCount)
        ) {
            errors.Add(item: $"{subject} value {DescribeValue(
                kind: row.Kind,
                raw: cell.Value.Raw
            )} is not a symmetry-lattice node — a {StateSpelling.CycleOutput(output: cycle.Output)} cycle stores the node its ring walk starts from, 0..{(SymmetryLattice.NodeCount - 1)}.");
        }
    }
    // The field checks a row-level and a cell-level cycle share: a word that bakes to a moving generator, a power
    // that is not the identity, step length, epoch, and an output the carrying kind can read (step/node on int, the
    // fixed outputs on fixed, nothing on bool/text).
    private static void ValidateCycleShape(StateCycle cycle, CellKind kind, bool numeric, string path, string subject, List<string> errors) {
        if (!Enum.IsDefined(value: cycle.Output)) {
            errors.Add(item: $"{path}.output '{cycle.Output}' is not a defined CycleOutput.");
        }

        if (!numeric) {
            errors.Add(item: $"{subject} declares cycle on a {StateSpelling.Kind(kind: kind)} row — only int/fixed cells turn.");
        } else if (StateCycle.IsIntegerOutput(output: cycle.Output) != (kind == CellKind.Int)) {
            errors.Add(item: $"{path}.output '{StateSpelling.CycleOutput(output: cycle.Output)}' does not suit a {StateSpelling.Kind(kind: kind)} cell — Step, Node and Ring are read by int cells, Turns/Cos/Sin/ProjectionX/ProjectionY by fixed cells.");
        }

        if (!cycle.TryResolveGenerator(
            generator: out var generator,
            reason: out var wordReason
        )) {
            errors.Add(item: $"{path}.{wordReason}.");
        } else if (generator.IsIdentity) {
            errors.Add(item: $"{path}.word [{string.Join(
                separator: ',',
                values: cycle.Word!
            )}] moves no node — its order is 1, so a cycle carrying it would never turn; author a word whose reflections do not cancel.");
        } else if (
            (cycle.Power <= -generator.Order) ||
            (cycle.Power >= generator.Order)
        ) {
            errors.Add(item: $"{path}.power {cycle.Power} is outside the generator's order {generator.Order} — a power reduces modulo the order, so author one in -{(generator.Order - 1)}..{(generator.Order - 1)}.");
        } else if (cycle.Power == 0) {
            errors.Add(item: $"{path}.power 0 is the identity — a cycle carrying it would never turn; author a nonzero power.");
        }

        if (cycle.TicksPerStep <= 0) {
            errors.Add(item: $"{path}.ticksPerStep {cycle.TicksPerStep} must be positive.");
        }
    }
    /// <summary>Validates a row's authored <see cref="StateDynamics"/> easing trait — the closed-form
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
            errors.Add(item: $"{path} ('{row.Name}') declares dynamics on a {StateSpelling.Kind(kind: row.Kind)} row — only int/fixed rows ease.");
        }

        ValidateDynamicsFields(
            dynamics: dynamics,
            errors: errors,
            names: dynamicsNames,
            path: path
        );
    }
    /// <summary>Validates one cell's own <see cref="StateDynamics"/> — the keyed counterpart of
    /// <see cref="ValidateDynamicsTrait"/>, governing the opposite shape: a cell inside a table rather than a row's
    /// own slot.</summary>
    private static void ValidateCellDynamics(WorldStateRow row, StateCell cell, StateDynamics dynamics, bool numeric, ISet<string> dynamicsNames, string cellPath, List<string> errors) {
        if (cell.Key == WorldStateRow.SlotKey) {
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares its own dynamics on the reserved slot key — a scalar row's easing trait is authored at the ROW level, never on the cell itself.");
        }

        if (!numeric) {
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares dynamics on a {StateSpelling.Kind(kind: row.Kind)} cell — only int/fixed cells ease.");
        }

        ValidateDynamicsFields(
            dynamics: dynamics,
            errors: errors,
            names: dynamicsNames,
            path: cellPath
        );
    }
    /// <summary>Validates a state row's authored <see cref="Draw"/> site — its own shape rules, then the shared
    /// site rule with the row's own envelope as the admissible domain.</summary>
    private static void ValidateDraw(WorldStateRow row, IReadOnlyList<GeneratorRow>? generators, string path, List<string> errors) {
        if (row.Draw is not { } draw) {
            // A lattice row painted by a draw fill carries the same cursor/masks bookkeeping for its whole-field
            // passes (see WorldLatticeFill.Draw), so only a row with neither facet is refused here.
            var latticeDraws = (WorldLatticeFill.FindDraw(trait: row.Field) is not null);

            if (
                (row.DrawCursor != 0L) &&
                !latticeDraws
            ) {
                errors.Add(item: $"{path} ('{row.Name}') declares drawCursor without draw — drawCursor is engine bookkeeping for a draw site alone.");
            }

            if (
                (row.DrawnMasks is { Count: > 0 }) &&
                !latticeDraws
            ) {
                errors.Add(item: $"{path} ('{row.Name}') declares drawnMasks without draw — drawnMasks is engine bookkeeping for a draw site alone.");
            }

            if (
                latticeDraws &&
                (row.DrawCursor < 0L)
            ) {
                errors.Add(item: $"{path} ('{row.Name}') drawCursor {row.DrawCursor} is negative.");
            }

            return;
        }

        if (row.Kind == CellKind.Vector) {
            errors.Add(item: $"{path} ('{row.Name}') declares draw on a vector row — vector rows cannot draw.");

            return;
        }

        if (
            row.IsKeyed &&
            GeneratorEngine.TryResolveSource(
            draw: draw,
            generator: out var keyedSource,
            generators: generators,
            reason: out _
        ) &&
            GeneratorEngine.WritesText(source: keyedSource.Source)
        ) {
            errors.Add(item: $"{path} ('{row.Name}') is a keyed draw site over a text source — a keyed site fills one numeric sample per cell; a text emission has no per-cell shape.");
        }

        if (row.DrawCursor < 0L) {
            errors.Add(item: $"{path}.drawCursor {row.DrawCursor} is negative — a draw cursor is a non-negative sample count the engine only ever advances.");
        }

        var (domainLow, domainHigh) = (row.Kind switch {
            CellKind.Bool => (0L, 1L),
            // A fixed cell carries RAW FixedQ4816 bits; an int cell is a whole long. Both span the carrier.
            _ => (long.MinValue, long.MaxValue),
        });

        // The site's admissible domain is the row's OWN declared envelope, never just the kind's representable band.
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

        var errorsBeforeSite = errors.Count;
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

        if (errors.Count != errorsBeforeSite) { return; }

        if (GeneratorEngine.TryResolveSource(
            draw: draw,
            generator: out var drawnSource,
            generators: generators,
            reason: out _
        )) {
            ValidateDrawnMasks(
                masks: row.DrawnMasks,
                errors: errors,
                generator: drawnSource,
                path: $"{path}.drawnMasks"
            );
        }
    }

    /// <summary>Refuses persisted drawn masks that do not fit the site's source — a mask left behind by an earlier
    /// source shape would otherwise be read as units already drawn, silently skipping outcomes or declaring a set
    /// drawn out early. Stated once, for a draw site and for a lattice row's draw fill alike.</summary>
    internal static void ValidateDrawnMasks(StateGenerator generator, IReadOnlyList<ClosedBitset256>? masks, string path, List<string> errors) {
        // Source validators report missing outcomes and alternatives. Tolerate them while collecting mask
        // diagnostics too, so an invalid declaration is refused rather than throwing during this secondary check.
        if (masks is not { Count: > 0 }) {
            return;
        }

        if (
            !StateGenerator.Exhausts(source: generator.Source) ||
            (generator.Mode == GeneratorMode.WithReplacement)
        ) {
            errors.Add(item: $"{path} carries {masks.Count} drawn mask(s) but the site's source never exhausts (source={StateSpelling.GeneratorSource(source: generator.Source)}, mode={generator.Mode}) — clear drawnMasks when re-authoring a site to a non-exhausting source.");

            return;
        }

        if (generator.Source is GeneratorSource.WeightedNumeric or GeneratorSource.SymmetryOrbit) {
            if (masks.Count != 1) {
                errors.Add(item: $"{path} carries {masks.Count} drawn masks but a {StateSpelling.GeneratorSource(source: generator.Source)} source exhausts through exactly one — clear drawnMasks when re-authoring a site from a Markov source.");

                return;
            }

            var units = ((generator.Source == GeneratorSource.WeightedNumeric)
                ? CountEntries(counts: (generator.Weighted ?? []).Select(selector: static outcome => outcome?.Multiplicity))
                : (GeneratorEngine.TryResolveOrbit(
                    generator: generator,
                    nodes: out var orbitNodes,
                    reason: out _
                )
                    ? orbitNodes.Length
                    : GeneratorCapacity.MaxEntriesPerSet
            ));

            RefuseMaskPastEntries(
                entries: units,
                errors: errors,
                mask: masks[0],
                path: $"{path}[0]"
            );

            return;
        }

        var contexts = (generator.Contexts ?? []);

        if (masks.Count > contexts.Count) {
            errors.Add(item: $"{path} carries {masks.Count} drawn masks but the Markov source declares {contexts.Count} context(s) — clear drawnMasks when re-authoring a site's source.");

            return;
        }

        for (var index = 0; (index < masks.Count); index++) {
            RefuseMaskPastEntries(
                entries: CountEntries(counts: (contexts[index]?.Alternatives ?? []).Select(selector: static alternative => alternative?.Multiplicity)),
                errors: errors,
                mask: masks[index],
                path: $"{path}[{index}]"
            );
        }
    }

    private static int CountEntries(IEnumerable<int?> counts) {
        var units = 0;

        foreach (var count in counts) {
            units += Math.Max(
                val1: 1,
                val2: (count ?? 1)
            );
        }

        return units;
    }
    private static void RefuseMaskPastEntries(ClosedBitset256 mask, int entries, string path, List<string> errors) {
        var bits = mask;

        if (
            (entries < GeneratorCapacity.MaxEntriesPerSet) &&
            !bits.Fits(count: entries)
        ) {
            errors.Add(item: $"{path} marks an entry past the {entries} the source holds (mask 0x{bits}) — a stale mask from an earlier source shape; clear drawnMasks.");
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
    private static void ValidateDrawSite(Draw draw, IReadOnlyList<GeneratorRow>? generators, CellKind targetKind, bool bootOnly, long domainLow, long domainHigh, string path, List<string> errors) {
        if (!Enum.IsDefined(value: draw.Timing)) {
            errors.Add(item: $"{path}.timing '{draw.Timing}' is not a defined DrawTiming.");
        } else if (
            bootOnly &&
            (draw.Timing != DrawTiming.Boot)
        ) {
            errors.Add(item: $"{path}.timing={draw.Timing.ToString().ToLowerInvariant()} — this is a BOOT-ONLY document field, read once at composition; nothing could observe a later redraw, so only timing=boot is admissible here.");
        }

        if (draw.Skip < 0L) {
            errors.Add(item: $"{path}.skip {draw.Skip} is negative — an authored seek is a non-negative offset.");
        }

        if (!GeneratorEngine.TryResolveSource(
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
        if (!GeneratorEngine.TryCheckTargetKind(
            source: generator.Source,
            targetKind: targetKind,
            reason: out var kindReason
        )) {
            errors.Add(item: $"{path} {kindReason}.");

            return;
        }

        // An exhausting source at a settle-and-clear boot site declares state across draws that this site can never
        // have: it draws once and its facet is erased, so the drawn mask could not survive to be drawn from again.
        if (
            bootOnly &&
            (generator.Mode != GeneratorMode.WithReplacement)
        ) {
            errors.Add(item: $"{path} draws from a source declaring mode={generator.Mode.ToString().ToLowerInvariant()} — a boot-only site draws once and its facet is cleared, so a drawn mask has no second draw to accumulate into.");
        }

        if (GeneratorEngine.WritesText(source: generator.Source)) {
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
    private static void ValidateGenerators(IReadOnlyList<GeneratorRow>? generators, List<string> errors) {
        var rows = (generators ?? []);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (rows.Count > GeneratorCapacity.MaxDeclaredSources) {
            errors.Add(item: $"generators count {rows.Count} exceeds the maximum of {GeneratorCapacity.MaxDeclaredSources}.");
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
    private static void ValidateMarkovSource(StateGenerator generator, string path, List<string> errors) {
        if (generator.Contexts is not { Count: > 0 } contexts) {
            errors.Add(item: $"{path}.contexts must declare at least one context for source=markov.");

            return;
        }

        if (contexts.Count > GeneratorCapacity.MaxContexts) {
            errors.Add(item: $"{path}.contexts count {contexts.Count} exceeds the maximum of {GeneratorCapacity.MaxContexts}.");
        }

        if (
            (generator.Bound < 1) ||
            (generator.Bound > GeneratorCapacity.MaxEmissionBound)
        ) {
            errors.Add(item: $"{path}.bound {generator.Bound} must be between 1 and {GeneratorCapacity.MaxEmissionBound}.");
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

            if (alternatives.Count > GeneratorCapacity.MaxAlternativesPerContext) {
                errors.Add(item: $"{contextPath}.alternatives count {alternatives.Count} exceeds the maximum of {GeneratorCapacity.MaxAlternativesPerContext} (one drawn-mask bit per alternative).");
            }

            var anyWeight = false;
            var units = 0L;

            for (var alternative = 0; (alternative < alternatives.Count); alternative++) {
                var entry = alternatives[alternative];
                var entryPath = $"{contextPath}.alternatives[{alternative}]";

                if (entry is null) {
                    errors.Add(item: $"{entryPath} is required.");

                    continue;
                }

                if (string.IsNullOrEmpty(value: entry.Token)) {
                    errors.Add(item: $"{entryPath}.token must be non-empty.");
                } else if (entry.Token.Length > GeneratorCapacity.MaxTokenLength) {
                    errors.Add(item: $"{entryPath}.token length {entry.Token.Length} exceeds the maximum of {GeneratorCapacity.MaxTokenLength}.");
                }

                if (
                    (entry.Multiplicity is { } multiplicity) &&
                    (multiplicity < 1)
                ) {
                    errors.Add(item: $"{entryPath}.multiplicity {multiplicity} must be at least 1 — the units one pass holds of this alternative.");
                }

                units += Math.Max(
                    val1: 1,
                    val2: (entry.Multiplicity ?? 1)
                );
                anyWeight |= (entry.Weight != 0UL);
            }

            if (units > GeneratorCapacity.MaxEntriesPerSet) {
                errors.Add(item: $"{contextPath}.alternatives hold {units} units counting each alternative's multiplicity, exceeding the {GeneratorCapacity.MaxEntriesPerSet} a drawn mask can hold.");
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
    /// <summary>Validates one <see cref="StateGenerator"/>'s own shape — dispatching on
    /// <see cref="StateGenerator.Source"/>, since each source owns a disjoint field set. Shared by a declared source
    /// row and a site's inline source, so the two spellings are held to the identical rules rather than to two
    /// readings of them.</summary>
    private static void ValidateSource(StateGenerator generator, string path, List<string> errors) {
        if (!Enum.IsDefined(value: generator.Source)) {
            errors.Add(item: $"{path}.source '{generator.Source}' is not a defined GeneratorSource.");

            return;
        }

        if (!Enum.IsDefined(value: generator.Mode)) {
            errors.Add(item: $"{path}.mode '{generator.Mode}' is not a defined GeneratorMode.");
        }

        if (!GeneratorEngine.TryCheckExtendedShape(
            generator: generator,
            reason: out var extendedReason
        )) {
            errors.Add(item: $"{path} {extendedReason}.");
        }

        // Each source's fields are BOTH-OR-NEITHER against the fields the others own — a foreign field present is
        // refused by name rather than silently ignored, the same "no dual discriminator" discipline WorldStateRow
        // itself already follows for value/cells.
        var declaresMarkovFields = ((generator.Start is not null) || (generator.Contexts is not null));
        var declaresRangeFields = ((generator.RangeMin is not null) || (generator.RangeMax is not null));
        var declaresWeighted = (generator.Weighted is not null);
        var declaresOrbitFields = ((generator.Ring is not null) || (generator.Node is not null) || (generator.Word is not null));

        if (
            declaresOrbitFields &&
            (generator.Source != GeneratorSource.SymmetryOrbit)
        ) {
            errors.Add(item: $"{path} declares source={StateSpelling.GeneratorSource(source: generator.Source)} beside ring/node/word, which belong to source=symmetryOrbit.");
        }

        // Bound is Markov-only and Mode belongs to the exhausting shapes, but both are NON-NULLABLE, so the
        // both-or-neither sweep above cannot see them and a source carrying one it does not read would parse,
        // validate, and then be silently ignored at fire time. Refused against the DECLARED DEFAULT — the most a
        // non-nullable field can distinguish, and exactly the set of values that could mislead.
        if (
            !StateGenerator.Exhausts(source: generator.Source) &&
            (generator.Mode != GeneratorMode.WithReplacement)
        ) {
            errors.Add(item: $"{path}.source={StateSpelling.GeneratorSource(source: generator.Source)} declares mode={generator.Mode.ToString().ToLowerInvariant()} — only markov, weightedNumeric and symmetryOrbit exhaust; uniformRange and streamDraw have no entry set to draw from.");
        }

        if (generator.Source != GeneratorSource.Markov) {
            if (generator.Bound != StateGenerator.DefaultBound) {
                errors.Add(item: $"{path}.source={StateSpelling.GeneratorSource(source: generator.Source)} declares bound {generator.Bound} — a numeric source is always exactly ONE draw, and 'bound' belongs to source=markov.");
            }

        }

        switch (generator.Source) {
            case GeneratorSource.Markov:
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
            case GeneratorSource.UniformRange:
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
                    (rangeMin < GeneratorCapacity.MinRangeBound) ||
                    (rangeMin > GeneratorCapacity.MaxRangeBound)
                ) {
                    errors.Add(item: $"{path}.rangeMin {rangeMin} must be between {GeneratorCapacity.MinRangeBound} and {GeneratorCapacity.MaxRangeBound}.");
                }

                if (
                    (rangeMax < GeneratorCapacity.MinRangeBound) ||
                    (rangeMax > GeneratorCapacity.MaxRangeBound)
                ) {
                    errors.Add(item: $"{path}.rangeMax {rangeMax} must be between {GeneratorCapacity.MinRangeBound} and {GeneratorCapacity.MaxRangeBound}.");
                }

                if (rangeMin > rangeMax) {
                    errors.Add(item: $"{path}.rangeMin {rangeMin} exceeds rangeMax {rangeMax}.");
                }

                return;
            case GeneratorSource.WeightedNumeric:
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

                if (outcomes.Count > GeneratorCapacity.MaxWeightedOutcomes) {
                    errors.Add(item: $"{path}.weighted count {outcomes.Count} exceeds the maximum of {GeneratorCapacity.MaxWeightedOutcomes}.");
                }

                var anyOutcomeWeight = false;
                var outcomeUnits = 0L;

                for (var index = 0; (index < outcomes.Count); index++) {
                    if (outcomes[index] is null) {
                        errors.Add(item: $"{path}.weighted[{index}] is required.");

                        continue;
                    }

                    if (
                        (outcomes[index].Multiplicity is { } outcomeMultiplicity) &&
                        (outcomeMultiplicity < 1)
                    ) {
                        errors.Add(item: $"{path}.weighted[{index}].multiplicity {outcomeMultiplicity} must be at least 1 — the units one pass holds of this outcome.");
                    }

                    outcomeUnits += Math.Max(
                        val1: 1,
                        val2: (outcomes[index].Multiplicity ?? 1)
                    );
                    anyOutcomeWeight |= (outcomes[index].Weight != 0UL);
                }

                if (outcomeUnits > GeneratorCapacity.MaxEntriesPerSet) {
                    errors.Add(item: $"{path}.weighted holds {outcomeUnits} units counting each outcome's multiplicity, exceeding the {GeneratorCapacity.MaxEntriesPerSet} a drawn mask can hold.");
                }

                if (!anyOutcomeWeight) {
                    errors.Add(item: $"{path}.weighted declares no non-zero weight — a table that can pick nothing is a stall.");
                }

                return;
            case GeneratorSource.StreamDraw:
                if (
                    declaresMarkovFields ||
                    declaresRangeFields ||
                    declaresWeighted
                ) {
                    errors.Add(item: $"{path} declares source=streamDraw beside start/contexts/rangeMin/rangeMax/weighted — a stream draw reads none of them.");
                }

                return;
            case GeneratorSource.SymmetryOrbit:
                if (
                    declaresMarkovFields ||
                    declaresRangeFields ||
                    declaresWeighted
                ) {
                    errors.Add(item: $"{path} declares source=symmetryOrbit beside start/contexts/rangeMin/rangeMax/weighted — an orbit source reads only ring, or node with an optional word.");
                }

                if (!GeneratorEngine.TryResolveOrbit(
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
    private static void ValidateSourceDomain(StateGenerator generator, CellKind targetKind, long domainLow, long domainHigh, string path, List<string> errors) {
        switch (generator.Source) {
            case GeneratorSource.UniformRange:
                if (
                    (generator.RangeMin is { } rangeMin) &&
                    (generator.RangeMax is { } rangeMax) &&
                    ((rangeMin < domainLow) || (rangeMax > domainHigh))
                ) {
                    errors.Add(item: $"{path} draws {rangeMin}..{rangeMax}, which is outside the site's admissible domain {domainLow}..{domainHigh}.");
                }

                break;
            case GeneratorSource.WeightedNumeric:
                foreach (var outcome in (generator.Weighted ?? [])) {
                    if (
                        (outcome is not null) &&
                        ((outcome.Value < domainLow) || (outcome.Value > domainHigh))
                    ) {
                        errors.Add(item: $"{path} draws outcome {outcome.Value}, which is outside the site's admissible domain {domainLow}..{domainHigh}.");
                    }
                }

                break;
            case GeneratorSource.StreamDraw:
                // A raw draw's band is the generator's own and nothing narrows it, so a site that cannot hold the
                // whole 32-bit band is refused HERE rather than by whatever it happened to roll.
                if (
                    (domainLow > 0L) ||
                    (domainHigh < uint.MaxValue)
                ) {
                    errors.Add(item: $"{path} draws source=streamDraw, whose raw band 0..{uint.MaxValue} is outside the site's admissible domain {domainLow}..{domainHigh} — author a uniformRange or weightedNumeric source inside the site's range.");
                }

                break;
            case GeneratorSource.SymmetryOrbit:
                if (GeneratorEngine.TryResolveOrbit(
                    generator: generator,
                    nodes: out var orbitNodes,
                    reason: out _
                )) {
                    foreach (var node in orbitNodes) {
                        var encoded = GeneratorEngine.EncodeNode(
                            node: node,
                            targetKind: targetKind
                        );

                        if (
                            (encoded < domainLow) ||
                            (encoded > domainHigh)
                        ) {
                            errors.Add(item: $"{path} draws node {node}, which is outside the site's admissible domain {DescribeValue(
                                kind: targetKind,
                                raw: domainLow
                            )}..{DescribeValue(
                                kind: targetKind,
                                raw: domainHigh
                            )} — an orbit site holds node indices 0..{(SymmetryLattice.NodeCount - 1)} in its own unit.");

                            break;
                        }
                    }
                }

                break;
            default:
                break;
        }
    }
    // The state section: schema cap (MaxRows), name uniqueness, the independently-optional Min/Max range (each
    // bound applied to every cell's value; a one-sided range is legal), text-cell length against
    // MaxTextValueLength, and per-row cell-count ceiling (MaxCellsPerRow,
    // optionally narrowed by an authored Capacity). CellName already refuses an empty/unsafe/dotted row name at
    // JSON parse, so this pass checks only uniqueness. Returns the declared rows by name so ValidateHud can refuse
    // an unknown state.<row>/state.<row>.<key> binding.
    // An owned identity's facts row is read raw at every seat bind, so its shape is held here rather than at the
    // write door alone: a keyed int row whose capacity is the one number identity.facts.capacity states.
    private static void ValidateIdentityFacts(WorldIdentityDefinition? identity, IReadOnlyDictionary<string, WorldStateRow> stateRows, List<string> errors) {
        if (identity is null) {
            return;
        }

        var facts = identity.FactsOrDefault;

        if (
            (facts.Capacity < 1) ||
            (facts.Capacity > StateCapacity.MaxCellsPerRow)
        ) {
            errors.Add(item: $"identity.facts.capacity {facts.Capacity} must be 1..{StateCapacity.MaxCellsPerRow}.");
        }
        if (!stateRows.TryGetValue(
            key: facts.State,
            value: out var row
        )) {
            return;
        }
        if (row is not { Kind: CellKind.Int, IsKeyed: true }) {
            errors.Add(item: $"identity.facts.state '{facts.State}' must name a keyed int state row.");
        } else if (row.Capacity != facts.Capacity) {
            errors.Add(item: $"identity.facts.state '{facts.State}' declares capacity {(row.Capacity?.ToString(provider: CultureInfo.InvariantCulture) ?? "none")}; identity.facts.capacity is {facts.Capacity} — the two are one number.");
        }
    }
    // The reserved lane a world declares to carry facts: a bounded keyed int row whose every authored cell key is a
    // (body, fact) pair, since the server keys the cells it loads that way.
    private static void ValidateIdentityFactLane(IReadOnlyDictionary<string, WorldStateRow> stateRows, List<string> errors) {
        if (!stateRows.TryGetValue(
            key: WorldIdentityFactLane.RowName,
            value: out var lane
        )) {
            return;
        }
        if (lane is not { Kind: CellKind.Int, IsKeyed: true, Capacity: > 0 }) {
            errors.Add(item: $"state.world row '{WorldIdentityFactLane.RowName}' is the reserved identity fact lane: it must be a keyed int row declaring a capacity.");

            return;
        }

        foreach (var cell in (lane.Cells ?? [])) {
            if (!WorldIdentityFactLane.TryParse(
                key: cell.Key.Value,
                bodyIndex: out _,
                fact: out _
            )) {
                errors.Add(item: $"state.world row '{WorldIdentityFactLane.RowName}' cell '{cell.Key}' is not a '<bodyIndex>{WorldIdentityFactLane.Separator}<fact>' lane key.");
            }
        }
    }
    private static Dictionary<string, StateEnum> ValidateEnums(IReadOnlyList<StateEnum>? enums, List<string> errors) {
        var byName = new Dictionary<string, StateEnum>(comparer: StringComparer.Ordinal);

        if (enums is null) {
            return byName;
        }

        if (enums.Count > StateCapacity.MaxEnums) {
            errors.Add(item: $"state enums count {enums.Count} exceeds the maximum of {StateCapacity.MaxEnums}.");
        }

        for (var index = 0; (index < enums.Count); index++) {
            var symbols = enums[index];
            var path = $"state.enums[{index}]";

            if (symbols is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }
            if (!byName.TryAdd(
                key: symbols.Name.Value,
                value: symbols
            )) {
                errors.Add(item: $"{path}.name '{symbols.Name}' is duplicated.");
            }
            if (!symbols.TryValidate(reason: out var reason)) {
                errors.Add(item: $"{path} {reason}");
            }
        }

        return byName;
    }
    private static Dictionary<string, StateSpace> ValidateSpaces(IReadOnlyList<StateSpace>? spaces, List<string> errors) {
        var byName = new Dictionary<string, StateSpace>(comparer: StringComparer.Ordinal);

        if (spaces is null) {
            return byName;
        }

        if (spaces.Count > StateCapacity.MaxVectorSpaces) {
            errors.Add(item: $"state spaces count {spaces.Count} exceeds the maximum of {StateCapacity.MaxVectorSpaces}.");
        }

        for (var index = 0; (index < spaces.Count); index++) {
            var space = spaces[index];
            var path = $"state.spaces[{index}]";

            if (space is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!byName.TryAdd(key: space.Name.Value, value: space)) {
                errors.Add(item: $"{path}.name '{space.Name}' is duplicated.");
            }

            if (space.Name.Value.Length > StateSpace.MaxNameLength) {
                errors.Add(item: $"{path}.name '{space.Name}' length {space.Name.Value.Length} exceeds the maximum of {StateSpace.MaxNameLength}.");
            }

            if (string.IsNullOrWhiteSpace(value: space.Model) || (space.Model.Length > StateSpace.MaxModelLength)) {
                errors.Add(item: $"{path}.model must be non-empty and at most {StateSpace.MaxModelLength} characters.");
            }

            if (string.IsNullOrWhiteSpace(value: space.Revision) || (space.Revision.Length > StateSpace.MaxRevisionLength)) {
                errors.Add(item: $"{path}.revision must be non-empty and at most {StateSpace.MaxRevisionLength} characters.");
            }

            if ((space.Dimensions < StateCapacity.MinVectorDimensions) || (space.Dimensions > StateCapacity.MaxVectorDimensions)) {
                errors.Add(item: $"{path}.dimensions {space.Dimensions} must be between {StateCapacity.MinVectorDimensions} and {StateCapacity.MaxVectorDimensions}.");
            }
        }

        return byName;
    }
    private static void ValidateVectorRow(WorldStateRow row, string path, IReadOnlyDictionary<string, StateSpace>? spaces, out StateSpace? resolvedSpace, List<string> errors) {
        resolvedSpace = null;

        if (row.Kind != CellKind.Vector) {
            if (row.Space is not null) {
                errors.Add(item: $"{path} ('{row.Name}') declares space '{row.Space}' on a {StateSpelling.Kind(kind: row.Kind)} row — only vector rows carry a space.");
            }

            return;
        }

        if (row.EffectiveDomain is not (StateDomain.Slot or StateDomain.Keys)) {
            errors.Add(item: $"{path} ('{row.Name}') has kind 'vector' but domain '{row.EffectiveDomain.GetType().Name}' — vector rows admit only slot or keyed domains.");
        }

        if (row.ValuesFrom is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares valuesFrom on a vector row — only int and fixed rows carry valuesFrom.");
        }

        if (row.Phase is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares phase on a vector row.");
        }

        if (row.PhaseOf is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares phaseOf on a vector row.");
        }

        if (row.Inverse is not null) {
            errors.Add(item: $"{path} ('{row.Name}') declares inverse on a vector row.");
        }

        if (spaces is not null) {
            if (row.Space is not null) {
                if (!spaces.TryGetValue(key: row.Space, value: out resolvedSpace)) {
                    errors.Add(item: $"{path} ('{row.Name}') declares space '{row.Space}' which is not a declared space.");
                }
            } else if (spaces.Count == 1) {
                resolvedSpace = spaces.Values.First();
            } else if (spaces.Count == 0) {
                errors.Add(item: $"{path} ('{row.Name}') is kind 'vector' but no spaces are declared.");
            } else {
                errors.Add(item: $"{path} ('{row.Name}') is kind 'vector' without a declared space and multiple spaces are declared.");
            }
        }

        if (resolvedSpace is not null) {
            var effectiveCapacity = (row.Capacity ?? (row.IsSlot ? 1 : row.CellCeiling));
            var rowVectorBytes = checked((((long)effectiveCapacity) * resolvedSpace.Dimensions));

            if (rowVectorBytes > StateCapacity.MaxVectorRowBytes) {
                errors.Add(item: $"{path} ('{row.Name}') vector byte size {rowVectorBytes} ({effectiveCapacity} * {resolvedSpace.Dimensions}) exceeds the maximum per-row ceiling of {StateCapacity.MaxVectorRowBytes}.");
            }
        }
    }
    private static Dictionary<string, WorldStateRow> ValidateState(IReadOnlyList<WorldStateRow> rows, IReadOnlyList<GeneratorRow>? generators, ISet<string> dynamicsNames, IReadOnlyDictionary<string, StateSpace> spaces, IReadOnlyDictionary<string, StateEnum> enums, List<string> errors) {
        // The map every other section resolves a row name through holds authored rows alone: a generated storage
        // row is validated and counted here, and no authored name reaches it.
        var byName = new Dictionary<string, WorldStateRow>(comparer: StringComparer.Ordinal);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (rows is null) {
            errors.Add(item: "state is required.");

            return byName;
        }

        if (rows.Count > StateCapacity.MaxRows) {
            errors.Add(item: $"state count {rows.Count} exceeds the maximum of {StateCapacity.MaxRows}.");
        }

        var totalVectorBytes = 0L;

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"state[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!names.Add(item: row.Name)) {
                errors.Add(item: $"{path}.name '{row.Name}' is duplicated.");
            } else if (!row.Generated) {
                byName.Add(
                    key: row.Name,
                    value: row
                );
            }

            ValidateStateRow(
                dynamicsNames: dynamicsNames,
                enums: enums,
                errors: errors,
                generators: generators,
                path: path,
                row: row,
                spaces: spaces
            );

            if (row.Kind == CellKind.Vector) {
                var vectorSpace = ((row.Space is not null)
                    ? spaces.GetValueOrDefault(key: row.Space)
                    : ((spaces.Count == 1) ? spaces.Values.First() : null)
                );

                if (vectorSpace is not null) {
                    var effectiveCapacity = (row.Capacity ?? (row.IsSlot ? 1 : row.CellCeiling));

                    totalVectorBytes += checked((((long)effectiveCapacity) * vectorSpace.Dimensions));
                }
            }
        }

        if (totalVectorBytes > StateCapacity.MaxVectorSectionBytes) {
            errors.Add(item: $"state total vector byte size {totalVectorBytes} exceeds the maximum section ceiling of {StateCapacity.MaxVectorSectionBytes}.");
        }

        var distinctKeys = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var row in rows) {
            foreach (var cell in (row?.Cells ?? [])) {
                distinctKeys.Add(item: cell.Key.Value);
            }
        }
        if (distinctKeys.Count > StateCapacity.MaxCellKeys) {
            errors.Add(item: $"state declares {distinctKeys.Count} distinct cell keys, more than the {StateCapacity.MaxCellKeys} one catalog interns.");
        }

        return byName;
    }
    // Every check a single state row (and its own cells) can fail on its own terms — the field trait shape, the
    // reserved-name prefix, the range/overflow/gatesDrive/evicts/capacity envelope, the draw/history/advance/
    // dynamics/cycle traits, and each cell's own key uniqueness, reserved-key rule, per-trait checks, and value. The
    // whole-document walk (<see cref="ValidateState"/>, one row per authored index) and a state mutation's touched-
    // row walk (<see cref="TryValidateTouchedStateRows"/>, one row by name) both call this — the same failure
    // either door reaches, in exactly one place. Cross-row invariants (a keysOf zone's domain, a cellsOf board's
    // topology, a knowledge board's source/mask, an inverse board's derivation) are NOT here — see
    // <see cref="ValidateTokenAndPhaseRow"/>, <see cref="ValidateBoardRow"/> and <see cref="ValidateDisclosureRow"/>.
    private static void ValidateStateRow(WorldStateRow row, IReadOnlyList<GeneratorRow>? generators, ISet<string> dynamicsNames, string path, List<string> errors, IReadOnlyDictionary<string, StateSpace>? spaces = null, IReadOnlyDictionary<string, StateEnum>? enums = null) {
        // A field-shaped row is per-cell fixed-point substrate: its cells live in the lattice (checkpointed,
        // snapshot-delivered), never as authored slot/keyed cells, and every keyed-row trait is refused at this
        // door so the shape cannot be held by convention.
        if (row.Field is not null) {
            if (row.EffectiveDomain is not StateDomain.CellsOf) {
                errors.Add(item: $"{path} ('{row.Name}') declares a field trait without a cellsOf domain — a field row's domain names the topology it lies over.");
            }
            if (row.Kind != CellKind.Fixed) {
                errors.Add(item: $"{path} ('{row.Name}') declares a field trait with kind '{row.Kind}' — a field row is kind 'fixed'.");
            }
            if (row.Cells is { Count: > 0 }) {
                errors.Add(item: $"{path} ('{row.Name}') declares both a field trait and cells — a field row's cells are the lattice's.");
            }
            if (row.Capacity is not null) {
                errors.Add(item: $"{path} ('{row.Name}') declares both a field trait and capacity — the topology sizes a field row.");
            }
            if (row.Advance is not null) {
                errors.Add(item: $"{path} ('{row.Name}') declares both a field trait and advance.");
            }
            if (row.Dynamics is not null) {
                errors.Add(item: $"{path} ('{row.Name}') declares both a field trait and dynamics.");
            }
            if (row.Cycle is not null) {
                errors.Add(item: $"{path} ('{row.Name}') declares both a field trait and cycle.");
            }
            if (row.Draw is not null) {
                errors.Add(item: $"{path} ('{row.Name}') declares both a field trait and a draw facet — a field row draws per cell through a 'draw' entry in its paint, never through the slot-row facet.");
            }
        }

        // The reserved prefix is ENGINE-MINTED ONLY, and the rule lives HERE — in the validator every ingress
        // passes (boot, live mutation, undo replay), never in one door a hand-authored file walks around.
        // Pool expansion mints protected rows with this prefix. Their mark has no wire representation, and
        // authored rows are checked before expansion, so an authored row cannot impersonate generated storage.
        if (!row.Generated && row.Name.Value.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldStateRow.ReservedNamePrefix
        )) {
            errors.Add(item: $"{path}.name '{row.Name}' starts with the reserved prefix '{WorldStateRow.ReservedNamePrefix}' — reserved for engine-minted names and the rules section's own channels ({RuleFacts.Tick}, {WorldRuleFacts.Population}, {WorldRuleFacts.RegionPrefix}<placementId>).");
        }

        if (!Enum.IsDefined(value: row.Kind)) {
            errors.Add(item: $"{path}.kind '{row.Kind}' is not a defined CellKind.");

            return;
        }

        ValidateVectorRow(
            errors: errors,
            path: path,
            resolvedSpace: out var resolvedSpace,
            row: row,
            spaces: spaces
        );

        StateEnum? resolvedEnum = null;

        if (row.Enum is { } symbols) {
            if (row.Kind != CellKind.Int) {
                errors.Add(item: $"{path} ('{row.Name}') names enum '{symbols}' on a {StateSpelling.Kind(kind: row.Kind)} row — only int rows carry a symbolic domain.");
            } else if (
                (enums is null) ||
                !enums.TryGetValue(
                key: symbols.Value,
                value: out resolvedEnum
            )
            ) {
                errors.Add(item: $"{path} ('{row.Name}') names enum '{symbols}', which the state section does not declare.");
            }
        }

        var numeric = ((row.Kind == CellKind.Int) || (row.Kind == CellKind.Fixed));

        // Min/Max/Overflow are envelope traits over a NUMBER — legitimate only for Int/Fixed, the same rule a
        // scalar row's range always followed, now stated once instead of per case. Min and Max are each
        // independently optional — a one-sided range (a floor with no ceiling, or the reverse) is legal.
        if (
            !numeric &&
            ((row.Min is not null) || (row.Max is not null))
        ) {
            errors.Add(item: $"{path} ('{row.Name}') declares min/max on a {StateSpelling.Kind(kind: row.Kind)} row — only int/fixed rows carry a range.");
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
            (row.Overflow != StateOverflow.Refuse)
        ) {
            errors.Add(item: $"{path} ('{row.Name}') declares overflow on a {StateSpelling.Kind(kind: row.Kind)} row — only int/fixed rows carry an overflow policy.");
        }

        // GatesDrive is the composition-lane's drive-admission gate (WorldGrants.TryGetDriveGate) — a nonzero
        // per-body cell there refuses that body's drive/action intents regardless of any grant held. It reads a
        // cell as zero/nonzero, so a text row has no honest reading for it, and it is read per BODY (one cell
        // per entity index), so only a keyed (table) row — one declaring Capacity — has a body to address; a
        // slot has exactly one value shared by every body, which is not what a per-body gate means.
        if (
            ((row.Kind == CellKind.Text) || (row.Kind == CellKind.Vector)) &&
            row.GatesDrive
        ) {
            errors.Add(item: $"{path} ('{row.Name}') declares gatesDrive on a {row.Kind.ToString().ToLowerInvariant()} row — a drive gate reads a cell as zero/nonzero, which a {row.Kind.ToString().ToLowerInvariant()} cell has no honest reading for.");
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
        ValidateHistory(
            errors: errors,
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
        var minDeclared = (numeric ? row.Min : null);
        var maxDeclared = (numeric ? row.Max : null);

        for (var cellIndex = 0; (cellIndex < cells.Count); cellIndex++) {
            var cell = cells[cellIndex];
            var cellPath = $"{path}.cells[{cellIndex}]";

            if (cell is null) {
                errors.Add(item: $"{cellPath} is required.");

                continue;
            }

            // A cell whose value is not of its row's kind is refused before anything reads that value: every check
            // below may read the number, the text or the vector the row's kind promises.
            if (!row.TryAdmitKind(
                reason: out var kindReason,
                value: cell.Value
            )) {
                errors.Add(item: $"{cellPath} {kindReason}.");

                continue;
            }

            // A cell key can no longer be empty, dotted, or otherwise unsafe — CellName refuses that at JSON
            // parse, before this method ever sees the cell — so this checks only uniqueness and the reserved key.
            if (!keys.Add(item: cell.Key)) {
                errors.Add(item: $"{path} ('{row.Name}') key '{cell.Key}' is duplicated.");
            } else if (
                reservesSlotKey &&
                (cell.Key == WorldStateRow.SlotKey)
            ) {
                errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' uses the reserved slot key '{WorldStateRow.SlotKey}' as an authored cell key.");
            } else if (
                (row.EffectiveDomain is StateDomain.Slot) &&
                (cell.Key != WorldStateRow.SlotKey)
            ) {
                // A slot row holds exactly one cell, addressed by the reserved key; the store addresses no other
                // position of it, so a differently named cell is refused here rather than dropped at load.
                errors.Add(item: $"{path} ('{row.Name}') is a slot row, whose one cell is '{WorldStateRow.SlotKey}' — it addresses no cell '{cell.Key}'.");
            } else if (!StateReservedCells.TryValidateReservedCell(
                row: row,
                key: cell.Key,
                reason: out var reservedReason
            )) {
                // Any reserved-prefix key but the slot key itself is refused: draw and generator bookkeeping
                // (the cursor, the drawn masks) lives in the row's own typed fields, never a cell. The rule lives
                // in StateReservedCells so UpsertStateCell's compose arm refuses the identical shape from
                // the identical code.
                errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' {reservedReason}.");
            }

            if (
                (cell.Advance is not null) &&
                (cell.Dynamics is not null)
            ) {
                errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares both advance and dynamics — a cell is a linear accumulator or a second-order easing cell, never both.");
            }

            if (
                (cell.Cycle is not null) &&
                ((cell.Advance is not null) || (cell.Dynamics is not null))
            ) {
                errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares cycle beside advance or dynamics — a cell is a linear accumulator, a second-order easing cell or a tick-indexed rotation, never two of them.");
            }

            if (
                (cell.Behavior == StateCellBehavior.None) &&
                ((cell.Advance is not null) || (cell.Dynamics is not null) || (cell.Cycle is not null))
            ) {
                errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares behavior 'none' beside its own advance/dynamics/cycle — a cell that opts out declares no trait of its own either.");
            }

            if (
                (cell.Behavior == StateCellBehavior.None) &&
                (cell.Key == WorldStateRow.SlotKey)
            ) {
                errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares behavior 'none' on the reserved slot key — a slot's one cell has no separate default of its own to opt out of.");
            }

            // The effective behavior's own trait shape (declared parameters, an unresolvable dynamics reference,
            // a lattice node past the cycle's range) is validated wherever it is authored — its own cell or its
            // row's default; only the CLOCK is validated here, uniformly, since it lives on the cell regardless of
            // which side declared the trait.
            ValidateClock(
                clock: cell.Clock,
                effectiveCycle: EffectiveBehavior.Resolve(
                cell: cell,
                row: row
            ).Cycle,
                errors: errors,
                path: cellPath
            );

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
                (provenance.Length > StateCapacity.MaxProvenanceLength)
            ) {
                errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' provenance length {provenance.Length} exceeds the maximum of {StateCapacity.MaxProvenanceLength}.");
            }

            if (row.Kind == CellKind.Vector) {
                if ((resolvedSpace is not null) && (cell.Value.AsVector.Length != resolvedSpace.Dimensions)) {
                    errors.Add(item: $"{cellPath}.vector dimensions {cell.Value.AsVector.Length} must match space '{resolvedSpace.Name}' dimensions {resolvedSpace.Dimensions}.");
                }

                if (cell.Advance is not null) {
                    errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares advance on a vector cell — vector cells do not accumulate.");
                }

                if (cell.Dynamics is not null) {
                    errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares dynamics on a vector cell — vector cells do not ease.");
                }

                if (cell.Cycle is not null) {
                    errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares cycle on a vector cell — vector cells do not turn.");
                }

                continue;
            }

            if (row.Kind == CellKind.Text) {
                if (cell.Value.AsText.Length > StateCapacity.MaxTextValueLength) {
                    errors.Add(item: $"{path} ('{row.Name}') text value length {cell.Value.AsText.Length} exceeds the maximum of {StateCapacity.MaxTextValueLength}.");
                }

                continue;
            }

            if (row.Kind == CellKind.Bool) {
                continue;
            }

            // Int/Fixed: the row's declared Min/Max, each independently. This walk is the envelope's authority; the
            // cross-document write-back channel (Server.WorldOwnedWorlds.Decide) reads the SAME row trait at its
            // own door precisely so it can never admit a value this walk would refuse at the owned world's next
            // boot.
            var raw = cell.Value.Raw;

            if (
                (minDeclared is { } lowerBound) &&
                (raw < lowerBound)
            ) {
                errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' value {DescribeValue(
                    kind: row.Kind,
                    raw: raw
                )} is below its declared minimum {DescribeValue(
                    kind: row.Kind,
                    raw: lowerBound
                )}.");
            }

            if (
                (maxDeclared is { } upperBound) &&
                (raw > upperBound)
            ) {
                errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' value {DescribeValue(
                    kind: row.Kind,
                    raw: raw
                )} is above its declared maximum {DescribeValue(
                    kind: row.Kind,
                    raw: upperBound
                )}.");
            }

            // The store's own admission door refuses a symbolic value outside its domain on every write, so an
            // authored cell that names one has to refuse here rather than at the arena's load.
            if (
                (resolvedEnum is { } symbolic) &&
                !symbolic.Admits(value: raw)
            ) {
                errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' value {raw} is outside enum '{symbolic.Name}', whose members are 0..{(symbolic.Count - 1)}.");
            }
        }
    }
}
