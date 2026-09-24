using System.Text.Json;

namespace Puck.World.Server;

/// <summary>
/// The authority half of the <c>world.save</c> snapshot. A running world holds session state its loaded definition
/// does not: the peer-source default the population verb moves (<see cref="WorldPopulation.DefaultPeerSource"/>), the
/// machine host's named declarations and live magazine selectors (<see cref="IWorldMachineHost.CaptureInstances"/>,
/// <see cref="IWorldMachineHost.TryMagazine"/>), and the live reading of every advancing, cycling, or easing
/// <c>state</c> cell. <see cref="Capture"/> folds each into its document home. The presentation levers (render,
/// audio, host pacing, the binding bar) fold afterwards through <c>WorldSessionLevers.Fold</c> in
/// <c>Puck.World.Client</c>; the composition root runs the two in that order before it writes.
/// </summary>
/// <remarks><para><b>A save writes the authored document.</b> Every fold reads a section's raw member, never its
/// effective accessor, and hands that raw member back unchanged, as the same instance, whenever the session agrees
/// with it. An absent section therefore stays absent, and an authored section keeps exactly the members its author
/// wrote. An effective value is the engine's resolution of what the author left out, so a fold never writes one into
/// a document. A session value folds only into a section the document authors; an absent section has no document
/// home for it, the rule the binding-bar lever already follows for its secondary seats. Named machine declarations
/// are the one exception, because they are content rather than a lever: a machine the session added is written into
/// <c>machines</c>.</para>
/// <para>The live document is never touched: a save is a snapshot, not a mutation. The count of live peers is not
/// folded: <c>networkPlayers</c> is a remote-admission cap, and the running count is session-only.</para>
/// <para><b>Moving state settles.</b> A cell's <c>StateAdvance</c> epoch is session-relative (engine ticks since
/// process start), so writing it verbatim would leave a reloaded document reading frozen until the next session's
/// engine-tick counter climbed back past the old epoch. <see cref="Capture"/> writes each advancing cell's live value
/// at the save's completed engine tick as its new base and projects its epoch to 0, so engine tick 0 of the next
/// session already reads that value. A cycling cell settles its phase and substep at the completed tick, and an
/// easing cell its follower's value and velocity. A cell whose clock starts at epoch 0 and whose reading has not moved
/// keeps its authored form. Only the authored rows settle; the storage a pool generates carries its live state in the
/// pool's own continuation.</para></remarks>
public static class WorldSessionCapture {
    /// <summary>Composes the authority half of a save snapshot: <paramref name="definition"/> with the live
    /// peer-source default folded into its authored <c>bodies</c> section, the machine host's named declarations into
    /// <c>machines</c>, the live magazine selectors into the authored <c>screens</c> rows, every authored creation pin
    /// that no longer matches its document refreshed, and every moving authored <c>state</c> cell settled at
    /// <paramref name="tick"/> and <paramref name="engineTick"/> (see this type's remarks).</summary>
    /// <param name="definition">The server's live definition, with its mutations applied.</param>
    /// <param name="population">The live entity table, whose peer-source default folds.</param>
    /// <param name="machines">The live machine host, whose declarations and magazine selectors fold.</param>
    /// <param name="tick">The server's completed tick, the instant cycling and easing cells settle at.</param>
    /// <param name="engineTick">The server's completed engine tick, the instant advancing cells settle at.</param>
    /// <returns>The snapshot definition.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static WorldDefinition Capture(WorldDefinition definition, WorldPopulation population, IWorldMachineHost machines, ulong tick, ulong engineTick) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: machines);

        return (definition with {
            PopulationRaw = CapturePopulation(
                definition: definition,
                population: population
            ),
            MachinesRaw = CaptureMachines(
                definition: definition,
                machines: machines
            ),
            ScreensRaw = CaptureScreens(
                definition: definition,
                machines: machines
            ),
            CreationsRaw = CaptureCreations(definition: definition),
            StateRaw = CaptureState(
                definition: definition,
                engineTick: engineTick,
                tick: tick
            ),
        });
    }
    /// <summary>Names the session dimensions a save writes differently from <paramref name="definition"/>: each
    /// section, among <c>render</c>, <c>population</c>, <c>machines</c>, <c>screens</c>, <c>audio</c>, <c>host</c>,
    /// and <c>bindings</c>, whose raw member <paramref name="snapshot"/> replaced. Settled <c>state</c> is not named,
    /// since an advancing cell keeps moving whether or not anything is saved.</summary>
    /// <param name="definition">The server's live definition.</param>
    /// <param name="snapshot">The whole save snapshot composed from it, both halves folded.</param>
    /// <returns><c>none</c> when a save writes the definition as it is, else the drifted dimensions joined by
    /// <c>+</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static string DescribeDrift(WorldDefinition definition, WorldDefinition snapshot) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: snapshot);

        // Every fold hands an unchanged section back as the same instance, so a section drifted exactly when its raw
        // member is no longer equal: reference equality for a class or list, value equality for the bodies struct.
        (string Name, bool Drifted)[] dimensions = [
            ("render", !Equals(objA: snapshot.RenderRaw, objB: definition.RenderRaw)),
            ("population", !Equals(objA: snapshot.PopulationRaw, objB: definition.PopulationRaw)),
            ("machines", !Equals(objA: snapshot.MachinesRaw, objB: definition.MachinesRaw)),
            ("screens", !Equals(objA: snapshot.ScreensRaw, objB: definition.ScreensRaw)),
            ("audio", !Equals(objA: snapshot.AudioRaw, objB: definition.AudioRaw)),
            ("host", !Equals(objA: snapshot.HostRaw, objB: definition.HostRaw)),
            ("bindings", !Equals(objA: snapshot.BindingOverlaysRaw, objB: definition.BindingOverlaysRaw)),
        ];
        var drifted = dimensions.Where(predicate: static dimension => dimension.Drifted).Select(selector: static dimension => dimension.Name).ToArray();

        return ((drifted.Length == 0)
            ? "none"
            : string.Join(
                separator: '+',
                value: drifted
            )
        );
    }

    // A creation row's pin is derived from its document when the author wrote none, so only an authored pin can go
    // stale; one that disagrees with its document is re-pinned through the one canonicalize pipeline the validator
    // checks against. The document itself is written as authored, never as its canonical form.
    private static IReadOnlyList<WorldPrototype>? CaptureCreations(WorldDefinition definition) {
        var creations = definition.CreationsRaw;

        if (creations is null) {
            return null;
        }

        List<WorldPrototype>? captured = null;

        for (var index = 0; (index < creations.Count); index++) {
            var creation = creations[index];

            if (creation.HashRaw is not { } authored) {
                continue;
            }

            var canonical = Puck.World.Authoring.CreationCanonicalizer.Canonicalize(
                document: creation.Document,
                source: creation.Id
            );

            if (string.Equals(
                a: authored,
                b: canonical.Hash,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            captured ??= new List<WorldPrototype>(collection: creations);
            captured[index] = (creation with { HashRaw = canonical.Hash });
        }

        return (((IReadOnlyList<WorldPrototype>?)captured) ?? creations);
    }
    // The host owns the named declarations: it adopts each accepted machine operation's canonical declaration and
    // each run-time insert, so its list is what is playing. The authored list stands whenever the two agree.
    private static IReadOnlyList<WorldMachine>? CaptureMachines(WorldDefinition definition, IWorldMachineHost machines) {
        var current = machines.CaptureInstances();

        return (MachinesAgree(
            authored: definition.Machines,
            current: current
        )
            ? definition.MachinesRaw
            : current
        );
    }
    // The live peer-source default folds into an authored bodies section only; the local-seat count and the
    // networkPlayers cap are durable document configuration and stay as authored.
    private static WorldBodiesDefaults? CapturePopulation(WorldDefinition definition, WorldPopulation population) => (
        ((definition.PopulationRaw is { } authored) && (population.DefaultPeerSource != authored.DefaultPeerSource))
        ? (authored with { DefaultPeerSourceRaw = population.DefaultPeerSource })
        : definition.PopulationRaw
    );
    // The live magazine selector folds into its authored screen row when it moved off the row's Selected.
    private static IReadOnlyList<WorldScreen>? CaptureScreens(WorldDefinition definition, IWorldMachineHost machines) {
        var screens = definition.ScreensRaw;

        if (screens is null) {
            return null;
        }

        List<WorldScreen>? captured = null;

        for (var index = 0; (index < screens.Count); index++) {
            var screen = screens[index];

            if (
                (screen.Magazine is { } magazine) &&
                machines.TryMagazine(
                index: screen.Index,
                magazine: out _,
                selected: out var selected
            ) &&
                (selected != magazine.Selected)
            ) {
                captured ??= new List<WorldScreen>(collection: screens);
                captured[index] = (screen with { Magazine = (magazine with { Selected = selected }) });
            }
        }

        return (((IReadOnlyList<WorldScreen>?)captured) ?? screens);
    }
    private static WorldStateSection? CaptureState(WorldDefinition definition, ulong tick, ulong engineTick) {
        if (definition.StateRaw is not { World: { Count: > 0 } rows } state) {
            return definition.StateRaw;
        }

        List<WorldStateRow>? captured = null;

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var settled = SettleRow(
                definition: definition,
                engineTick: engineTick,
                row: row,
                tick: tick
            );

            if (ReferenceEquals(
                objA: settled,
                objB: row
            )) {
                continue;
            }

            captured ??= new List<WorldStateRow>(collection: rows);
            captured[index] = settled;
        }

        return ((captured is null)
            ? state
            : (state with { World = captured })
        );
    }
    private static bool MachinesAgree(IReadOnlyList<WorldMachine> authored, IReadOnlyList<WorldMachine> current) {
        if (current.Count != authored.Count) {
            return false;
        }

        for (var index = 0; (index < authored.Count); index++) {
            var expected = authored[index];
            var actual = current[index];

            if (
                !string.Equals(
                a: expected.Name,
                b: actual.Name,
                comparisonType: StringComparison.Ordinal
            ) ||
                !string.Equals(
                a: expected.Engine,
                b: actual.Engine,
                comparisonType: StringComparison.Ordinal
            ) ||
                (expected.Running != actual.Running) ||
                !JsonElement.DeepEquals(
                element1: expected.Configuration,
                element2: actual.Configuration
            ) ||
                !Equals(
                objA: expected.Memory,
                objB: actual.Memory
            ) ||
                !Equals(
                objA: expected.Cable,
                objB: actual.Cable
            )
            ) {
                return false;
            }
        }

        return true;
    }
    // Advance and Cycle are legitimate only for Int and Fixed rows, so a settled raw value is always one of those two.
    private static CellValue RawCellValue(CellKind kind, long raw) => ((kind == CellKind.Fixed)
        ? CellValue.Fixed(rawBits: raw)
        : CellValue.Int(value: raw)
    );
    // Settles one cell at `tick`/`engineTick` by its effective behavior (its own, or its row's default), reading
    // through the same computation world.state, a rule gate, and a HUD binding read live, so the projected value is
    // exactly what an observer saw this session. An advancing cell's live value becomes its stored base; a cycling
    // cell's live phase (or node) becomes its stored phase, carrying its substep remainder; an easing cell's sampled
    // value and velocity replace its clock and leave its stored truth alone. Every clock settles to epoch 0. A cell
    // with no moving behavior is returned as it is.
    private static StateCell SettleCell(WorldDefinition definition, WorldStateRow row, StateCell cell, ulong tick, ulong engineTick) {
        var behavior = EffectiveBehavior.Resolve(
            cell: cell,
            row: row
        );
        var clock = cell.Clock;

        if (behavior.Advance is { } advance) {
            return (cell with {
                Value = RawCellValue(kind: row.Kind, raw: advance.ComputeCurrentValue(
                baseValue: cell.Value.Raw,
                currentEngineTick: engineTick,
                epochEngineTick: (clock?.EpochEngineTick ?? 0L),
                row: row
            )),
                Clock = new StateCellClock(EpochEngineTick: 0, EpochTick: 0),
            });
        }

        if (behavior.Cycle is { } cycle) {
            var epochTick = (clock?.EpochTick ?? 0L);
            var substepTicks = (clock?.SubstepTicks ?? 0L);

            return (cell with {
                Value = RawCellValue(kind: row.Kind, raw: cycle.SettledPhase(
                baseValue: cell.Value.Raw,
                currentTick: tick,
                epochTick: epochTick,
                row: row,
                substepTicks: substepTicks
            )),
                Clock = new StateCellClock(EpochTick: 0, SubstepTicks: cycle.SettledSubstep(
                currentTick: tick,
                epochTick: epochTick,
                substepTicks: substepTicks
            )),
            });
        }

        if (
            (behavior.Dynamics is not null) &&
            WorldStateReader.TryEvaluateDynamics(
            cell: cell,
            definition: definition,
            row: row,
            sample: out var sample,
            tick: tick,
            trait: out _
        )
        ) {
            return (cell with {
                Clock = new StateCellClock(
                EpochTick: 0,
                V0: StateReader.DynamicsFixedToTraitRaw(value: sample.Velocity),
                Y0: StateReader.DynamicsFixedToTraitRaw(value: sample.Value)
            ),
            });
        }

        return cell;
    }
    // Settles every moving cell the row carries. A cell whose clock already starts at epoch 0, and whose settled form
    // at the save's instant equals its settled form at that epoch, is in the state a reload begins from, so it keeps
    // the form its author wrote. A row with nothing moving comes back as the same instance.
    private static WorldStateRow SettleRow(WorldDefinition definition, WorldStateRow row, ulong tick, ulong engineTick) {
        if (row.Cells is not { Count: > 0 } cells) {
            return row;
        }

        List<StateCell>? settledCells = null;

        for (var index = 0; (index < cells.Count); index++) {
            var cell = cells[index];
            var settled = SettleCell(
                cell: cell,
                definition: definition,
                engineTick: engineTick,
                row: row,
                tick: tick
            );

            if (
                ReferenceEquals(
                objA: settled,
                objB: cell
            ) ||
                (
                    ((cell.Clock is null) || ((cell.Clock.EpochTick == 0L) && (cell.Clock.EpochEngineTick == 0L))) &&
                    (settled == SettleCell(
                    cell: cell,
                    definition: definition,
                    engineTick: 0UL,
                    row: row,
                    tick: 0UL
                ))
                )
            ) {
                continue;
            }

            settledCells ??= new List<StateCell>(collection: cells);
            settledCells[index] = settled;
        }

        return ((settledCells is null)
            ? row
            : (row with { Cells = settledCells })
        );
    }
}
