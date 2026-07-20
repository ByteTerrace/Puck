using Puck.Abstractions.Gpu;
using Puck.Launcher;
using Puck.Scene;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>world.save</c> session-capture fold. A running world holds live SESSION state that is
/// not part of the loaded definition: the render levers the graphics verbs move (<see cref="WorldRenderSettings"/>), the
/// peer-source default the population verb moves (<see cref="WorldPopulation.DefaultPeerSource"/>), and the machines a
/// runtime <c>screen.insert</c> booted onto declared screens (<see cref="WorldScreenBinder"/>). The live census COUNT
/// (<see cref="WorldPopulation.SimulatedCount"/>) is deliberately NOT folded — R-C made <c>networkPlayers</c> a durable
/// remote-admission cap, not the transient running count, so a save persists the authored cap and the running census is
/// session-only. <see cref="Capture"/> composes a snapshot definition — the live definition
/// (mutations already applied) with those three session dimensions folded into their document homes — so a save is a
/// faithful snapshot of what is playing, and re-booting the saved file reproduces it.
/// </summary>
/// <remarks>SAVED-BYTES-ONLY (the default policy): capture composes the snapshot the writer serializes; it never mutates
/// the in-memory definition or the journal (a save is a snapshot, not a mutation). The fold is exactly IDEMPOTENT on a
/// freshly booted world — live session state equals the document defaults at boot — so the ouroboros gate (load→save→load
/// byte-identity) still holds after a save learns to fold. <see cref="DescribeDrift"/> is the honest cheap witness of
/// whether the live session has since diverged from the loaded document, reported by <c>world.status</c> at verb time.</remarks>
internal static class WorldSessionCapture {
    /// <summary>Composes the save snapshot: the live definition with the session dimensions (render levers, the
    /// peer-source default, screen inserts, the master-volume lever) folded into <see cref="WorldDefinition.Render"/>,
    /// <see cref="WorldDefinition.Population"/>, the <see cref="WorldDefinition.Screens"/> rows' machine sources,
    /// and <see cref="WorldDefinition.Audio"/>'s master gain. The transient census COUNT is not folded (R-C).</summary>
    /// <param name="definition">The server's live definition (mutations already applied).</param>
    /// <param name="render">The live render levers.</param>
    /// <param name="population">The live entity table (census + peer-source default).</param>
    /// <param name="binder">The live screen binder (runtime machine inserts).</param>
    /// <param name="audio">The audio director (the <c>world.volume</c> session lever).</param>
    /// <param name="pacing">The live present-pacing control (the <c>world.target</c> session lever).</param>
    /// <returns>The snapshot definition to serialize.</returns>
    public static WorldDefinition Capture(WorldDefinition definition, WorldRenderSettings render, WorldPopulation population, WorldScreenBinder binder, WorldAudioDirector audio, PresentPacingControl pacing) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: render);
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: binder);
        ArgumentNullException.ThrowIfNull(argument: audio);
        ArgumentNullException.ThrowIfNull(argument: pacing);

        return (definition with {
            Render = CaptureRender(render: render, defaults: definition.Render),
            Population = CapturePopulation(population: population, defaults: definition.Population),
            Screens = CaptureScreens(screens: definition.Screens, binder: binder),
            // The live cable-link set folds into the Links section (declared + runtime), so a save reproduces the groups.
            Links = CaptureLinks(definition: definition, binder: binder),
            Creations = CaptureCreations(creations: definition.Creations),
            Tunes = CaptureTunes(tunes: definition.Tunes),
            Patches = CapturePatches(patches: definition.Patches),
            Audio = CaptureAudio(audio: audio, defaults: definition.Audio),
            Host = CaptureHost(host: definition.Host, pacing: pacing),
        });
    }

    // The audio-asset twins of CaptureCreations: every tune/patch row re-crosses its ONE canonicalize pipeline so
    // the persisted doc + hash come from the SAME canonical result — idempotent at compose time, drift-proof on disk.
    private static IReadOnlyList<WorldTune> CaptureTunes(IReadOnlyList<WorldTune> tunes) {
        if (tunes.Count == 0) {
            return tunes;
        }

        var captured = new List<WorldTune>(capacity: tunes.Count);

        foreach (var tune in tunes) {
            var canonical = Puck.Authoring.AudioCanonicalizer.Canonicalize(document: tune.Document, source: tune.Id);

            captured.Add(item: (tune with { Document = canonical.Document, Hash = canonical.Hash }));
        }

        return captured;
    }

    private static IReadOnlyList<WorldPatch> CapturePatches(IReadOnlyList<WorldPatch> patches) {
        if (patches.Count == 0) {
            return patches;
        }

        var captured = new List<WorldPatch>(capacity: patches.Count);

        foreach (var patch in patches) {
            var canonical = Puck.Authoring.SynthPatchCanonicalizer.Canonicalize(document: patch.Document, source: patch.Id);

            captured.Add(item: (patch with { Document = canonical.Document, Hash = canonical.Hash }));
        }

        return captured;
    }

    // The world.save hash recompute: every creation row re-crosses the ONE canonicalize pipeline so the persisted
    // doc + hash come from the SAME CanonicalCreation. Rows are already canonical at compose time, so this is exactly
    // idempotent (no drift dimension) — it exists so the SAVED file's pin can never diverge from its embedded bytes.
    private static IReadOnlyList<WorldCreation> CaptureCreations(IReadOnlyList<WorldCreation> creations) {
        if (creations.Count == 0) {
            return creations;
        }

        var captured = new List<WorldCreation>(capacity: creations.Count);

        foreach (var creation in creations) {
            var canonical = Puck.Authoring.CreationCanonicalizer.Canonicalize(document: creation.Document, source: creation.Id);

            captured.Add(item: (creation with { Document = canonical.Document, Hash = canonical.Hash }));
        }

        return captured;
    }

    /// <summary>A cheap, verb-time (never per-tick) description of which session dimensions have drifted from the loaded
    /// document's defaults: <c>none</c> when a save would reproduce the file, else a <c>+</c>-joined list of the drifted
    /// dimensions (<c>render</c>, <c>population</c>, <c>screens</c>, <c>audio</c>) — the honest <c>world.status</c> session-drift hint.</summary>
    /// <param name="definition">The server's live definition.</param>
    /// <param name="render">The live render levers.</param>
    /// <param name="population">The live entity table.</param>
    /// <param name="binder">The live screen binder.</param>
    /// <param name="audio">The audio director (the master-volume lever).</param>
    /// <param name="pacing">The live present-pacing control (the <c>world.target</c> lever).</param>
    /// <returns>The drift hint token.</returns>
    public static string DescribeDrift(WorldDefinition definition, WorldRenderSettings render, WorldPopulation population, WorldScreenBinder binder, WorldAudioDirector audio, PresentPacingControl pacing) {
        var drifted = new List<string>(capacity: 5);

        if (CaptureRender(render: render, defaults: definition.Render) != definition.Render) {
            drifted.Add(item: "render");
        }

        if (CapturePopulation(population: population, defaults: definition.Population) != definition.Population) {
            drifted.Add(item: "population");
        }

        if (ScreensDrifted(screens: definition.Screens, binder: binder)) {
            drifted.Add(item: "screens");
        }

        // Links drift: the folded live link set differs by content from the document's rows (a runtime screen.link, an
        // unlink, or a member-set change). A reference compare would be a false positive — CaptureLinks returns a FRESH
        // list whenever the binder holds any link, so a purely-declared link set (which a save reproduces byte-for-byte)
        // would otherwise report drift forever.
        if (LinksDrifted(definition: definition, binder: binder)) {
            drifted.Add(item: "links");
        }

        if (audio.MasterVolumeLeverEngaged && (audio.EffectiveMasterVolume != definition.Audio.MasterGain)) {
            drifted.Add(item: "audio");
        }

        // The host live levers (world.target / world.timing) folded home differ from the document's host row — the same
        // comparison a save would make, so 'host' shows exactly when a world.save would rewrite the host section.
        if (CaptureHost(host: definition.Host, pacing: pacing) != definition.Host) {
            drifted.Add(item: "host");
        }

        return ((drifted.Count == 0) ? "none" : string.Join(separator: '+', values: drifted));
    }

    // Fold the live render levers into the document's render-lever boot defaults, quantizing the continuous shadow reach
    // and render scale back to their tiered document homes and preserving the quality-preset table (session-inert).
    private static WorldRenderDefaults CaptureRender(WorldRenderSettings render, WorldRenderDefaults defaults) => (defaults with {
        Shadows = ShadowTiers.Tier(reach: render.ShadowReach),
        ShadowCrowdRadius = render.ShadowCrowdRadius,
        AmbientOcclusion = render.AmbientOcclusion,
        RenderScale = NearestRenderScaleTier(scale: render.RenderScale),
        UpscaleSharpness = render.UpscaleSharpness,
    });

    // Fold the world.volume session lever into the document's audio master gain (the render-levers asymmetry: the
    // lever owns "now", the document owns boot, a save reconciles them). EffectiveMasterVolume equals the document
    // value while the lever is unengaged, so the fold is exactly idempotent on a fresh boot (the ouroboros holds).
    private static WorldAudioDefaults CaptureAudio(WorldAudioDirector audio, WorldAudioDefaults defaults) => (defaults with {
        MasterGain = audio.EffectiveMasterVolume,
    });

    // Fold the two host live levers (world.target's present Hz, world.timing's armed state) into the host section; every
    // boot-only field is preserved as authored. A world that authored no host section (null) keeps it absent when the
    // levers still sit at their document defaults, so a fresh default world saves without gaining a `host` key (the
    // ouroboros); a live lever moved off default materializes the section from WorldHostDefaults.Default.
    private static WorldHostDefaults? CaptureHost(WorldHostDefaults? host, PresentPacingControl pacing) {
        var basis = (host ?? WorldHostDefaults.Default);
        var folded = (basis with { TargetHertz = pacing.TargetHertz, Timing = GpuTimingControl.Shared.Armed });

        return (((host is null) && (folded == WorldHostDefaults.Default)) ? null : folded);
    }

    // Fold the live peer-source default; the local-seat count and the networkPlayers CAP are durable document config, not
    // live figures (R-C: networkPlayers is a remote admission cap, not the live census count — the running count is
    // transient session state that world.save does not persist), so they stay as authored. This keeps a fresh default
    // world byte-clean through a boot-and-save round-trip even though its boot census is zero.
    private static WorldPopulationDefaults CapturePopulation(WorldPopulation population, WorldPopulationDefaults defaults) => (defaults with {
        DefaultPeerSource = population.DefaultPeerSource,
    });

    // Fold a live machine insert on each declared screen back into that row's Machine source, and the live magazine
    // selector back into that row's Magazine.Selected; a screen with no live insert / no magazine keeps its declared
    // source / magazine untouched.
    private static IReadOnlyList<WorldScreen> CaptureScreens(IReadOnlyList<WorldScreen> screens, WorldScreenBinder binder) {
        var captured = new List<WorldScreen>(capacity: screens.Count);

        foreach (var screen in screens) {
            var row = (binder.TryReadMachineInsert(index: screen.Index, engine: out var engine, contentPath: out var contentPath, options: out var options)
                ? (screen with { Source = new WorldScreenSource.Machine(Engine: engine, ContentPath: contentPath, Options: options) })
                : screen);

            if ((row.Magazine is { } magazine) && binder.TryMagazine(index: screen.Index, selected: out var selected, magazine: out _) && (selected != magazine.Selected)) {
                row = (row with { Magazine = (magazine with { Selected = selected }) });
            }

            captured.Add(item: row);
        }

        return captured;
    }

    // Fold the live cable-link set back into the Links section (the world.save home for screen.link / world.link.set).
    // When the binder holds no runtime links, the document's own Links carries forward unchanged — so a world with no
    // links (null) keeps its `links` key absent (the ouroboros), and declared links not yet established at boot are
    // preserved rather than dropped.
    private static IReadOnlyList<WorldScreenLink>? CaptureLinks(WorldDefinition definition, WorldScreenBinder binder) {
        var live = binder.CaptureLinks();

        return ((live.Count == 0) ? definition.Links : live);
    }

    // Content-compare the folded live link set against the document's Links rows (name + ordered members), the same way
    // ScreensDrifted compares machine sources: true exactly when a world.save would rewrite the Links section. The capture
    // preserves declared-link order (ReconcileLinks establishes rows in declared order), so a save that reproduces the file
    // reports no drift.
    private static bool LinksDrifted(WorldDefinition definition, WorldScreenBinder binder) {
        var captured = CaptureLinks(definition: definition, binder: binder);

        if (ReferenceEquals(objA: captured, objB: definition.Links)) {
            return false;
        }

        var declared = definition.Links;
        var capturedCount = (captured?.Count ?? 0);

        if (capturedCount != (declared?.Count ?? 0)) {
            return true;
        }

        for (var index = 0; (index < capturedCount); index++) {
            var live = captured![index];
            var row = declared![index];

            if (!string.Equals(a: live.Name, b: row.Name, comparisonType: StringComparison.Ordinal) || (live.Screens.Count != row.Screens.Count)) {
                return true;
            }

            for (var member = 0; (member < live.Screens.Count); member++) {
                if (live.Screens[member] != row.Screens[member]) {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ScreensDrifted(IReadOnlyList<WorldScreen> screens, WorldScreenBinder binder) {
        foreach (var screen in screens) {
            if (binder.TryReadMachineInsert(index: screen.Index, engine: out var engine, contentPath: out var contentPath, options: out var options) &&
                (screen.Source is not WorldScreenSource.Machine machine ||
                 !string.Equals(a: machine.Engine, b: engine, comparisonType: StringComparison.Ordinal) ||
                 !string.Equals(a: machine.ContentPath, b: contentPath, comparisonType: StringComparison.Ordinal) ||
                 !string.Equals(a: machine.Options, b: options, comparisonType: StringComparison.Ordinal))) {
                return true;
            }

            // Selector drift: the live magazine pointer moved off the row's authored Selected.
            if ((screen.Magazine is { } magazine) && binder.TryMagazine(index: screen.Index, selected: out var selected, magazine: out _) && (selected != magazine.Selected)) {
                return true;
            }
        }

        return false;
    }

    // The nearest safe render-scale tier to a continuous live scale — the reverse of WorldRenderScaleTiers.Scale, matching
    // WorldCommandModule.RenderScaleName's tolerance so a tier round-trips exactly and a continuous override quantizes to
    // its closest tier (the document holds only tiers). WorldRenderScaleTiers lives in Puck.Scene (out of this scope), so
    // the reverse mapping is computed here against its forward table.
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
}
