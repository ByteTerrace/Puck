using System.Text.Json;
using Puck.Hosting;
using Puck.Launcher;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>world.save</c> session-capture fold. A running world holds live session state that is
/// not part of the loaded definition: the render levers the graphics verbs move (<see cref="WorldRenderSettings"/>), the
/// peer-source default the population verb moves (<see cref="WorldPopulation.DefaultPeerSource"/>), the host-owned
/// named machine declarations (<see cref="WorldMachineHost.CaptureInstances"/>), and the forced
/// binding-bar visibility the <c>world.binding-bar</c> lever writes (<see cref="WorldBindingBarVisibility"/>). The live census count
/// (<see cref="WorldPopulation.SimulatedCount"/>) is deliberately not folded — <c>networkPlayers</c> is a durable
/// remote-admission cap, not the transient running count, so a save persists the authored cap and the running census is
/// session-only. <see cref="Capture"/> composes a snapshot definition — the live definition
/// (mutations already applied) with those session dimensions folded into their document homes — so a save is a
/// faithful snapshot of what is playing, and re-booting the saved file reproduces it.
/// </summary>
/// <remarks>Saved-bytes-only (the default policy): capture composes the snapshot the writer serializes; it never mutates
/// the in-memory definition or the journal (a save is a snapshot, not a mutation). Every other dimension this class
/// folds is exactly idempotent on a freshly booted, untouched world — live session state equals the document defaults
/// at boot — so the load-save-load byte-identity round-trip still holds for those; <c>state</c>'s
/// advancing-row settle (below) is the one dimension that is not idempotent at boot, and honestly so — some ticks have
/// always elapsed by the time a save can be requested at all, so a document carrying an advancing row settles to a
/// slightly larger value even on an otherwise-untouched world, never to the exact bytes it loaded from.
/// <see cref="DescribeDrift"/> is the honest cheap witness of whether the live session has since diverged from the
/// loaded document, reported by <c>world.status</c> at verb time; it does not (and need not) cover this dimension, since
/// an advancing row is expected to keep moving regardless of any save.
/// <para><b>Advancing state settles at save too.</b> A row/cell's <c>StateAdvance</c>
/// epoch is session-relative (engine ticks since process start), so writing it verbatim leaves a reloaded document
/// reading frozen until the next session's engine-tick counter climbs back past the old epoch — the fresh session's
/// clock restarts at 0. <see cref="CaptureState"/> folds every advancing row's slot cell and every advancing keyed
/// cell's own base into what it reads at the save's completed engine tick, and resets the projected epoch to 0, so
/// engine tick 0 of the next session already reads that value and keeps advancing immediately. Projection only: the
/// live document's own base/epoch is never touched, exactly like every other dimension this class folds.</para></remarks>
internal static class WorldSessionCapture {
    // Fold the world.volume session lever into the document's audio master gain (the render-levers asymmetry: the
    // lever owns "now", the document owns boot, a save reconciles them). EffectiveMasterVolume equals the document
    // value while the lever is unengaged, so the fold is exactly idempotent on a fresh boot (the ouroboros holds).
    private static WorldAudioDefaults CaptureAudio(WorldAudioDirector audio, WorldAudioDefaults defaults) => (defaults with {
        MasterGain = audio.EffectiveMasterVolume,
    });
    // Fold the binding-bar session lever into the world's own bar authoring. The lever is per-seat and the document
    // has exactly one world-scoped bar row, so the PRIMARY local seat (slot 0, player 1) is the seat that folds; the
    // other seats' overrides are live-only, having no document home to land in. An unengaged seat 0 (auto) leaves the
    // authored value untouched, the same lever-owns-now/document-owns-boot asymmetry CaptureAudio states.
    private static IReadOnlyList<WorldBindingOverlay>? CaptureBindingOverlays(WorldDefinition definition, WorldBindingBarVisibility visibility) {
        var overlays = definition.BindingOverlaysRaw;

        if (
            (overlays is not { Count: > 0 }) ||
            (overlays[0]?.BindingBar is not { } bar) ||
            (visibility.Override(slot: 0) is not { } forced) ||
            (bar.Enabled == forced)
        ) {
            return overlays;
        }

        var captured = new List<WorldBindingOverlay>(collection: overlays);

        captured[0] = (overlays[0] with { BindingBar = (bar with { Enabled = forced }) });

        return captured;
    }
    /// <summary>Owns canonical document and hash capture for every persisted asset-row family.</summary>
    private static IReadOnlyList<TAsset> CaptureCanonicalAssets<TAsset, TDocument>(
        IReadOnlyList<TAsset> assets,
        Func<TAsset, string> id,
        Func<TAsset, TDocument> document,
        Func<TDocument, string, Puck.Assets.Documents.CanonicalDocument<TDocument>> canonicalize,
        Func<TAsset, Puck.Assets.Documents.CanonicalDocument<TDocument>, TAsset> replace) {
        if (assets.Count == 0) {
            return assets;
        }

        var captured = new List<TAsset>(capacity: assets.Count);

        foreach (var asset in assets) {
            var canonical = canonicalize(
                arg1: document(arg: asset),
                arg2: id(arg: asset)
            );

            captured.Add(item: replace(
                arg1: asset,
                arg2: canonical
            ));
        }

        return captured;
    }
    // The world.save hash recompute: every creation row re-crosses the ONE canonicalize pipeline so the persisted
    // doc + hash come from the SAME CanonicalCreation. Rows are already canonical at compose time, so this is exactly
    // idempotent (no drift dimension) — it exists so the SAVED file's pin can never diverge from its embedded bytes.
    private static IReadOnlyList<WorldPrototype> CaptureCreations(IReadOnlyList<WorldPrototype> creations) =>
        CaptureCanonicalAssets(
            assets: creations,
            id: static creation => creation.Id,
            document: static creation => creation.Document,
            canonicalize: static (document, source) => Puck.World.Authoring.CreationCanonicalizer.Canonicalize(
                document: document,
                source: source
            ),
            replace: static (creation, canonical) => (creation with { Document = canonical.Document, HashRaw = canonical.Hash })
        );
    // Fold the two host live levers (world.target's present Hz, world.timing's armed state) into the host section; every
    // boot-only field is preserved as authored.
    private static WorldHostDefaults CaptureHost(WorldHostDefaults host, PresentPacingControl pacing) =>
        (host with { TargetHertz = pacing.TargetHertz, Timing = GpuTimingControl.Shared.Armed });
    // Fold the live peer-source default; the local-seat count and the networkPlayers CAP are durable document config, not
    // live figures (R-C: networkPlayers is a remote admission cap, not the live census count — the running count is
    // transient session state that world.save does not persist), so they stay as authored. This keeps a fresh default
    // world byte-clean through a boot-and-save round-trip even though its boot census is zero.
    private static WorldBodiesDefaults CapturePopulation(WorldPopulation population, WorldBodiesDefaults defaults) => (defaults with {
        DefaultPeerSourceRaw = population.DefaultPeerSource,
    });
    // Fold the live render levers into the document's render-lever boot defaults, quantizing the continuous shadow reach
    // and render scale back to their tiered document homes and preserving the quality-preset table (session-inert).
    private static WorldRenderDefaults CaptureRender(WorldRenderSettings render, WorldRenderDefaults defaults) => (defaults with {
        Shadows = ShadowTiers.Tier(reach: render.ShadowReach),
        ShadowCrowdRadius = render.ShadowCrowdRadius,
        AmbientOcclusion = render.AmbientOcclusion,
        RenderScale = NearestRenderScaleTier(scale: render.RenderScale),
        UpscaleSharpness = render.UpscaleSharpness,
    });
    // Screen capture folds only presentation state. Named machine declarations and their runtime configuration are
    // captured from the host below, so a display consumer never becomes the persistence owner of a machine.
    private static IReadOnlyList<WorldScreen> CaptureScreens(IReadOnlyList<WorldScreen> screens, WorldScreenBinder binder) {
        var captured = new List<WorldScreen>(capacity: screens.Count);

        foreach (var screen in screens) {
            var row = screen;

            if (
                (row.Magazine is { } magazine) &&
                binder.TryMagazine(
                index: screen.Index,
                selected: out var selected,
                magazine: out _
            ) &&
                (selected != magazine.Selected)
            ) {
                row = (row with { Magazine = (magazine with { Selected = selected }) });
            }

            captured.Add(item: row);
        }

        return captured;
    }
    // The save-time settle: a row declaring its OWN Advance (a slot-shaped row) gets its one cell rebased to the live
    // computed value at `engineTick`, epoch projected to 0; a KEYED row's independently-advancing cells
    // (StateCell.Advance) settle the same way, one at a time, leaving any non-advancing cell in the same row
    // untouched. Both read through StateAdvance.ComputeCurrentValue — the SAME computation world.state/a rule
    // gate/a HUD binding already read live — so the projected base is exactly what an observer would have seen
    // this session, never a re-derived guess. Cycle and Dynamics settle against the simulation tick (`tick`)
    // instead — only Advance reads the engine clock. A Dynamics trait settles the same way but on the TRAIT
    // alone, never the cell's own stored truth: Y0/V0 become the live eased value/velocity
    // WorldStateReader.TryEvaluateDynamics reports at `tick`, epoch projected to 0, so a reloaded session's
    // follower resumes exactly where this one left it rather than snapping back to rest. A row with nothing
    // advancing or easing returns unchanged (no allocation), matching CaptureLinks/CaptureScreens' own "nothing
    // drifted, hand back the original list" idiom.
    private static IReadOnlyList<WorldStateRow> CaptureState(WorldDefinition definition, ulong tick, ulong engineTick) {
        var rows = definition.State;

        if (rows.Count == 0) {
            return rows;
        }

        List<WorldStateRow>? captured = null;

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var settledRow = SettleRow(
                definition: definition,
                engineTick: engineTick,
                row: row,
                tick: tick
            );

            if (ReferenceEquals(
                objA: settledRow,
                objB: row
            )) {
                continue;
            }

            captured ??= new List<WorldStateRow>(collection: rows);
            captured[index] = settledRow;
        }

        return (((IReadOnlyList<WorldStateRow>?)captured) ?? rows);
    }
    private static bool MachinesDrifted(IReadOnlyList<WorldMachine> authored, WorldScreenBinder binder) {
        var current = binder.CaptureInstances();

        if (current.Count != authored.Count) {
            return true;
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
                return true;
            }
        }

        return false;
    }
    // The nearest safe render-scale tier to a continuous live scale — the reverse of WorldRenderScaleTiers.Scale, matching
    // WorldCommandModule.RenderScaleName's tolerance so a tier round-trips exactly and a continuous override quantizes to
    // its closest tier (the document holds only tiers). WorldRenderScaleTiers lives with the document model, so the
    // reverse mapping is computed here against its forward table.
    private static WorldRenderScaleTier NearestRenderScaleTier(float scale) {
        var best = WorldRenderScaleTier.Native;
        var bestDelta = float.MaxValue;

        foreach (var tier in Enum.GetValues<WorldRenderScaleTier>()) {
            var delta = MathF.Abs(x: (scale - WorldRenderScaleTiers.Scale(tier: tier)));

            if (delta < bestDelta) {
                best = tier;
                bestDelta = delta;
            }
        }

        return best;
    }
    private static bool ScreensDrifted(IReadOnlyList<WorldScreen> screens, WorldScreenBinder binder) {
        foreach (var screen in screens) {
            // Selector drift: the live magazine pointer moved off the row's authored Selected.
            if (
                (screen.Magazine is { } magazine) &&
                binder.TryMagazine(
                index: screen.Index,
                selected: out var selected,
                magazine: out _
            ) &&
                (selected != magazine.Selected)
            ) {
                return true;
            }
        }

        return false;
    }
    // Settles every cell the row carries at `tick`/`engineTick`, whatever its effective behavior (its own, or its
    // row's default) resolves to: an advancing cell's live accumulated value (read against `engineTick`) becomes
    // its new stored base; a cycling cell's live rotation (or node) becomes its new stored phase, carrying its
    // substep remainder; a dynamics cell's sampled position/velocity replace its clock — every clock settles to
    // epoch zero (both EpochTick and EpochEngineTick), so a save/reload continues from exactly where the live
    // world stood. A cell whose effective behavior is none is untouched.
    private static WorldStateRow SettleRow(WorldDefinition definition, WorldStateRow row, ulong tick, ulong engineTick) {
        if (row.Cells is not { Count: > 0 } cells) {
            return row;
        }

        List<StateCell>? settledCells = null;

        for (var index = 0; (index < cells.Count); index++) {
            var cell = cells[index];
            var behavior = EffectiveBehavior.Resolve(
                cell: cell,
                row: row
            );
            var clock = cell.Clock;

            if (behavior.Advance is { } advance) {
                settledCells ??= new List<StateCell>(collection: cells);
                settledCells[index] = (cell with {
                    Value = advance.ComputeCurrentValue(
                    baseValue: cell.Value,
                    currentEngineTick: engineTick,
                    epochEngineTick: (clock?.EpochEngineTick ?? 0L),
                    row: row
                ),
                    Clock = new StateCellClock(EpochEngineTick: 0, EpochTick: 0),
                });

                continue;
            }

            if (behavior.Cycle is { } cycle) {
                var epochTick = (clock?.EpochTick ?? 0L);
                var substepTicks = (clock?.SubstepTicks ?? 0L);

                settledCells ??= new List<StateCell>(collection: cells);
                settledCells[index] = (cell with {
                    Value = cycle.SettledPhase(
                    baseValue: cell.Value,
                    currentTick: tick,
                    epochTick: epochTick,
                    row: row,
                    substepTicks: substepTicks
                ),
                    Clock = new StateCellClock(EpochTick: 0, SubstepTicks: cycle.SettledSubstep(
                    currentTick: tick,
                    epochTick: epochTick,
                    substepTicks: substepTicks
                )),
                });

                continue;
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
                settledCells ??= new List<StateCell>(collection: cells);
                settledCells[index] = (cell with {
                    Clock = new StateCellClock(
                    EpochTick: 0,
                    V0: StateReader.DynamicsFixedToTraitRaw(value: sample.Velocity),
                    Y0: StateReader.DynamicsFixedToTraitRaw(value: sample.Value)
                ),
                });
            }
        }

        return ((settledCells is null)
            ? row
            : (row with { Cells = settledCells })
        );
    }

    /// <summary>Composes the save snapshot: the live definition with the session dimensions (render levers, the
    /// peer-source default, named machine declarations, the master-volume lever, the primary seat's forced binding-bar
    /// visibility) folded into <see cref="WorldDefinition.Render"/>,
    /// <see cref="WorldDefinition.Population"/>, the <see cref="WorldDefinition.Screens"/> rows' machine sources,
    /// <see cref="WorldDefinition.Audio"/>'s master gain, <see cref="WorldDefinition.BindingOverlays"/>'s first row,
    /// and every advancing <see cref="WorldDefinition.State"/> row/cell
    /// settled at <paramref name="tick"/> (see this type's remarks). The transient census count is not folded.</summary>
    /// <param name="definition">The server's live definition (mutations already applied).</param>
    /// <param name="render">The live render levers.</param>
    /// <param name="population">The live entity table (census + peer-source default).</param>
    /// <param name="binder">The live screen binder (runtime machine inserts).</param>
    /// <param name="audio">The audio director (the <c>world.volume</c> session lever).</param>
    /// <param name="pacing">The live present-pacing control (the <c>world.target</c> session lever).</param>
    /// <param name="bindingBar">The live per-seat binding-bar visibility (the <c>world.binding-bar</c> session lever).</param>
    /// <param name="tick">The server's completed tick — the instant <c>state</c>'s cycling/dynamics rows/cells
    /// settle at.</param>
    /// <param name="engineTick">The server's completed engine tick — the instant <c>state</c>'s advancing
    /// rows/cells settle at.</param>
    /// <returns>The snapshot definition to serialize.</returns>
    public static WorldDefinition Capture(WorldDefinition definition, WorldRenderSettings render, WorldPopulation population, WorldScreenBinder binder, WorldAudioDirector audio, PresentPacingControl pacing, WorldBindingBarVisibility bindingBar, ulong tick, ulong engineTick) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: render);
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: binder);
        ArgumentNullException.ThrowIfNull(argument: audio);
        ArgumentNullException.ThrowIfNull(argument: pacing);
        ArgumentNullException.ThrowIfNull(argument: bindingBar);

        return (definition with {
            BindingOverlaysRaw = CaptureBindingOverlays(
            definition: definition,
            visibility: bindingBar
        ),
            RenderRaw = CaptureRender(
            render: render,
            defaults: definition.Render
        ),
            PopulationRaw = CapturePopulation(
            population: population,
            defaults: definition.Population
        ),
            MachinesRaw = binder.CaptureInstances(),
            ScreensRaw = CaptureScreens(
            screens: definition.Screens,
            binder: binder
        ),
            CreationsRaw = CaptureCreations(creations: definition.Creations),
            AudioRaw = CaptureAudio(
            audio: audio,
            defaults: definition.Audio
        ),
            HostRaw = CaptureHost(
            host: definition.Host,
            pacing: pacing
        ),
            StateRaw = ((definition.StateRaw ?? new WorldStateSection()) with {
                World = CaptureState(
            definition: definition,
            engineTick: engineTick,
            tick: tick
        ),
            }),
        });
    }
    /// <summary>A cheap, verb-time (never per-tick) description of which session dimensions have drifted from the loaded
    /// document's defaults: <c>none</c> when a save would reproduce the file, else a <c>+</c>-joined list of the drifted
    /// dimensions (<c>render</c>, <c>population</c>, <c>screens</c>, <c>audio</c>, <c>host</c>, <c>bindings</c>) — the honest <c>world.status</c> session-drift hint.</summary>
    /// <param name="definition">The server's live definition.</param>
    /// <param name="render">The live render levers.</param>
    /// <param name="population">The live entity table.</param>
    /// <param name="binder">The live screen binder.</param>
    /// <param name="audio">The audio director (the master-volume lever).</param>
    /// <param name="pacing">The live present-pacing control (the <c>world.target</c> lever).</param>
    /// <param name="bindingBar">The live per-seat binding-bar visibility (the <c>world.binding-bar</c> lever).</param>
    /// <returns>The drift hint token.</returns>
    public static string DescribeDrift(WorldDefinition definition, WorldRenderSettings render, WorldPopulation population, WorldScreenBinder binder, WorldAudioDirector audio, PresentPacingControl pacing, WorldBindingBarVisibility bindingBar) {
        var drifted = new List<string>(capacity: 6);

        if (CaptureRender(
            render: render,
            defaults: definition.Render
        ) != definition.Render) {
            drifted.Add(item: "render");
        }

        if (CapturePopulation(
            population: population,
            defaults: definition.Population
        ) != definition.Population) {
            drifted.Add(item: "population");
        }

        if (MachinesDrifted(
            authored: definition.Machines,
            binder: binder
        )) {
            drifted.Add(item: "machines");
        }

        if (ScreensDrifted(
            screens: definition.Screens,
            binder: binder
        )) {
            drifted.Add(item: "screens");
        }

        if (
            audio.MasterVolumeLeverEngaged &&
            (audio.EffectiveMasterVolume != definition.Audio.MasterGain)
        ) {
            drifted.Add(item: "audio");
        }

        // The host live levers (world.target / world.timing) folded home differ from the document's host row — the same
        // comparison a save would make, so 'host' shows exactly when a world.save would rewrite the host section.
        if (CaptureHost(
            host: definition.Host,
            pacing: pacing
        ) != definition.Host) {
            drifted.Add(item: "host");
        }

        // Reference-compares against the raw rows: CaptureBindingOverlays hands the SAME list back whenever nothing
        // folded, so an unforced (or already-agreeing) bar never reports drift.
        if (!ReferenceEquals(
            objA: CaptureBindingOverlays(
                definition: definition,
                visibility: bindingBar
            ),
            objB: definition.BindingOverlaysRaw
        )) {
            drifted.Add(item: "bindings");
        }

        return ((drifted.Count == 0)
            ? "none"
            : string.Join(
                separator: '+',
                values: drifted
            )
        );
    }
}
