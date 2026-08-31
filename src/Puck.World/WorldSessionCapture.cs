using Puck.Hosting;
using Puck.Launcher;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>world.save</c> session-capture fold. A running world holds live session state that is
/// not part of the loaded definition: the render levers the graphics verbs move (<see cref="WorldRenderSettings"/>), the
/// peer-source default the population verb moves (<see cref="WorldPopulation.DefaultPeerSource"/>), the machines a
/// runtime <c>screen.insert</c> booted onto declared screens (<see cref="WorldScreenBinder"/>), and the forced
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
/// <para><b>Advancing state settles at save too.</b> A row/cell's <c>WorldStateAdvance</c>
/// epoch is session-relative (ticks since process start), so writing it verbatim leaves a reloaded document reading
/// frozen until the next session's tick counter climbs back past the old epoch — the fresh session's clock restarts at
/// 0. <see cref="CaptureState"/> folds every advancing row's slot cell and every advancing keyed cell's own base into
/// what it reads at the save tick, and resets the projected epoch to 0, so tick 0 of the next session already reads
/// that value and keeps advancing immediately. Projection only: the live document's own base/epoch is never
/// touched, exactly like every other dimension this class folds.</para></remarks>
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
            canonicalize: static (document, source) => Puck.Forge.Authoring.CreationCanonicalizer.Canonicalize(
                document: document,
                source: source
            ),
            replace: static (creation, canonical) => (creation with { Document = canonical.Document, HashRaw = canonical.Hash })
        );
    // Fold the two host live levers (world.target's present Hz, world.timing's armed state) into the host section; every
    // boot-only field is preserved as authored.
    private static WorldHostDefaults CaptureHost(WorldHostDefaults host, PresentPacingControl pacing) =>
        (host with { TargetHertz = pacing.TargetHertz, Timing = GpuTimingControl.Shared.Armed });
    // The cable port each screen should carry after a save, from the binder's link table — the authoritative set
    // (declared groups reconcile into it, dormant included, and screen.link/.unlink edit it): a member screen folds
    // its (name, position) home onto its row's machine source, and a screen in no link folds null (an unlink clears
    // the port). A link over a screen whose folded source is not a machine is unrepresentable in the document and is
    // left out — the runtime group simply does not survive the save.
    private static Dictionary<int, WorldMachineCable> BuildCableMap(WorldScreenBinder binder) {
        var map = new Dictionary<int, WorldMachineCable>();

        foreach (var group in binder.CaptureLinks()) {
            for (var position = 0; (position < group.Screens.Count); position++) {
                map[group.Screens[position]] = new WorldMachineCable(
                    Name: group.Name,
                    Position: position
                );
            }
        }

        return map;
    }
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
    // Fold a live machine insert on each declared screen back into that row's Machine source, the live cable-link
    // table back into each machine source's cable port, and the live magazine selector back into that row's
    // Magazine.Selected; a screen with no live insert / no link / no magazine keeps its declared row untouched.
    private static IReadOnlyList<WorldScreen> CaptureScreens(IReadOnlyList<WorldScreen> screens, WorldScreenBinder binder) {
        var captured = new List<WorldScreen>(capacity: screens.Count);
        var cables = BuildCableMap(binder: binder);

        foreach (var screen in screens) {
            var row = (binder.TryReadMachineInsert(
                index: screen.Index,
                engine: out var engine,
                contentPath: out var contentPath,
                options: out var options
            )
                ? (screen with {
                    Source = new WorldScreenSource.Machine(
                    ContentPath: contentPath,
                    Engine: engine,
                    Options: options,
                    Cable: (screen.Source as WorldScreenSource.Machine)?.Cable
                ),
                })
                : screen
            );

            if (row.Source is WorldScreenSource.Machine machine) {
                var cable = cables.GetValueOrDefault(key: screen.Index);

                if (machine.Cable != cable) {
                    row = (row with { Source = (machine with { Cable = cable }) });
                }
            }

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
    // computed value at `tick`, epoch projected to 0; a KEYED row's independently-advancing cells (WorldStateCell.Advance)
    // settle the same way, one at a time, leaving any non-advancing cell in the same row untouched. Both read through
    // WorldStateAdvance.ComputeCurrentValue — the SAME computation world.state/a rule gate/a HUD binding already read live
    // — so the projected base is exactly what an observer would have seen this session, never a re-derived guess. A
    // Dynamics trait settles the same way but on the TRAIT alone, never the cell's own stored truth: Y0/V0 become the
    // live eased value/velocity WorldStateReader.TryEvaluateDynamics reports at `tick`, epoch projected to 0, so a
    // reloaded session's follower resumes exactly where this one left it rather than snapping back to rest. A row
    // with nothing advancing or easing returns unchanged (no allocation), matching CaptureLinks/CaptureScreens' own
    // "nothing drifted, hand back the original list" idiom.
    private static IReadOnlyList<WorldStateRow> CaptureState(WorldDefinition definition, ulong tick) {
        var rows = definition.State;

        if (rows.Count == 0) {
            return rows;
        }

        List<WorldStateRow>? captured = null;

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var settledRow = SettleRow(
                definition: definition,
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
        var cables = BuildCableMap(binder: binder);

        foreach (var screen in screens) {
            if (
                binder.TryReadMachineInsert(
                index: screen.Index,
                engine: out var engine,
                contentPath: out var contentPath,
                options: out var options
            ) &&
                ((screen.Source is not WorldScreenSource.Machine machine) ||
                 !string.Equals(
                a: machine.Engine,
                b: engine,
                comparisonType: StringComparison.Ordinal
            ) ||
                 !string.Equals(
                a: machine.ContentPath,
                b: contentPath,
                comparisonType: StringComparison.Ordinal
            ) ||
                 !string.Equals(
                a: machine.Options,
                b: options,
                comparisonType: StringComparison.Ordinal
            ))
            ) {
                return true;
            }

            // Cable drift: the live link table's port for this screen differs from the declared machine source's —
            // a runtime screen.link, an unlink, or a member/order change (the same comparison the save's fold makes).
            if (
                (screen.Source is WorldScreenSource.Machine declaredMachine) &&
                (declaredMachine.Cable != cables.GetValueOrDefault(key: screen.Index))
            ) {
                return true;
            }

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
    private static WorldStateRow SettleRow(WorldDefinition definition, WorldStateRow row, ulong tick) {
        // A slot-shaped row's OWN trait governs its one cell — the row-level counterpart of a keyed cell's own trait
        // below, and never both on the SAME cell (the validator refuses a slot-shaped row from declaring Advance or
        // Dynamics beside a keyed cells array, or the two together, in the first place).
        if (row.Advance is { } rowAdvance) {
            var slot = row.Cells![0];
            var settledValue = rowAdvance.ComputeCurrentValue(
                row: row,
                baseValue: slot.Value,
                currentTick: tick
            );

            return (row with {
                Advance = (rowAdvance with { EpochTick = 0 }),
                Cells = [(slot with { Value = settledValue })],
            });
        }

        if (row.Dynamics is { } rowDynamics) {
            var slot = row.Cells![0];

            if (!WorldStateReader.TryEvaluateDynamics(
                cell: slot,
                definition: definition,
                row: row,
                sample: out var sample,
                tick: tick,
                trait: out _
            )) {
                return row;
            }

            return (row with {
                Dynamics = (rowDynamics with {
                    EpochTick = 0,
                    V0 = WorldStateReader.DynamicsFixedToRaw(row: row, value: sample.Velocity),
                    Y0 = WorldStateReader.DynamicsFixedToRaw(row: row, value: sample.Value),
                }),
            });
        }

        if (row.Cells is not { Count: > 0 } cells) {
            return row;
        }

        List<WorldStateCell>? settledCells = null;

        for (var index = 0; (index < cells.Count); index++) {
            var cell = cells[index];

            if (cell.Advance is { } cellAdvance) {
                settledCells ??= new List<WorldStateCell>(collection: cells);
                settledCells[index] = (cell with {
                    Value = cellAdvance.ComputeCurrentValue(
                    row: row,
                    baseValue: cell.Value,
                    currentTick: tick
                ),
                    Advance = (cellAdvance with { EpochTick = 0 }),
                });

                continue;
            }

            if (
                (cell.Dynamics is { } cellDynamics) &&
                WorldStateReader.TryEvaluateDynamics(
                cell: cell,
                definition: definition,
                row: row,
                sample: out var cellSample,
                tick: tick,
                trait: out _
            )
            ) {
                settledCells ??= new List<WorldStateCell>(collection: cells);
                settledCells[index] = (cell with {
                    Dynamics = (cellDynamics with {
                        EpochTick = 0,
                        V0 = WorldStateReader.DynamicsFixedToRaw(row: row, value: cellSample.Velocity),
                        Y0 = WorldStateReader.DynamicsFixedToRaw(row: row, value: cellSample.Value),
                    }),
                });
            }
        }

        return ((settledCells is null)
            ? row
            : (row with { Cells = settledCells })
        );
    }

    /// <summary>Composes the save snapshot: the live definition with the session dimensions (render levers, the
    /// peer-source default, screen inserts, the master-volume lever, the primary seat's forced binding-bar
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
    /// <param name="tick">The server's completed tick — the instant <c>state</c>'s advancing rows/cells settle at.</param>
    /// <returns>The snapshot definition to serialize.</returns>
    public static WorldDefinition Capture(WorldDefinition definition, WorldRenderSettings render, WorldPopulation population, WorldScreenBinder binder, WorldAudioDirector audio, PresentPacingControl pacing, WorldBindingBarVisibility bindingBar, ulong tick) {
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
