using Puck.Scene;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>world.save</c> session-capture fold. A running world holds live SESSION state that is
/// not part of the loaded definition: the render levers the graphics verbs move (<see cref="WorldRenderSettings"/>), the
/// live census the population verb moves (<see cref="WorldPopulation.SimulatedCount"/> /
/// <see cref="WorldPopulation.DefaultPeerSource"/>), and the machines a runtime <c>screen.insert</c> booted onto declared
/// screens (<see cref="WorldScreenBinder"/>). <see cref="Capture"/> composes a snapshot definition — the live definition
/// (mutations already applied) with those three session dimensions folded into their document homes — so a save is a
/// faithful snapshot of what is playing, and re-booting the saved file reproduces it.
/// </summary>
/// <remarks>SAVED-BYTES-ONLY (the default policy): capture composes the snapshot the writer serializes; it never mutates
/// the in-memory definition or the journal (a save is a snapshot, not a mutation). The fold is exactly IDEMPOTENT on a
/// freshly booted world — live session state equals the document defaults at boot — so the ouroboros gate (load→save→load
/// byte-identity) still holds after a save learns to fold. <see cref="DescribeDrift"/> is the honest cheap witness of
/// whether the live session has since diverged from the loaded document, reported by <c>world.status</c> at verb time.</remarks>
internal static class WorldSessionCapture {
    /// <summary>Composes the save snapshot: the live definition with the session dimensions (render levers, live
    /// census, screen inserts, the master-volume lever) folded into <see cref="WorldDefinition.Render"/>,
    /// <see cref="WorldDefinition.Population"/>, the <see cref="WorldDefinition.Screens"/> rows' machine sources,
    /// and <see cref="WorldDefinition.Audio"/>'s master gain.</summary>
    /// <param name="definition">The server's live definition (mutations already applied).</param>
    /// <param name="render">The live render levers.</param>
    /// <param name="population">The live entity table (census + peer-source default).</param>
    /// <param name="binder">The live screen binder (runtime machine inserts).</param>
    /// <param name="audio">The audio director (the <c>world.volume</c> session lever).</param>
    /// <returns>The snapshot definition to serialize.</returns>
    public static WorldDefinition Capture(WorldDefinition definition, WorldRenderSettings render, WorldPopulation population, WorldScreenBinder binder, WorldAudioDirector audio) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: render);
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: binder);
        ArgumentNullException.ThrowIfNull(argument: audio);

        return (definition with {
            Render = CaptureRender(render: render, defaults: definition.Render),
            Population = CapturePopulation(population: population, defaults: definition.Population),
            Screens = CaptureScreens(screens: definition.Screens, binder: binder),
            Creations = CaptureCreations(creations: definition.Creations),
            Tunes = CaptureTunes(tunes: definition.Tunes),
            Patches = CapturePatches(patches: definition.Patches),
            Audio = CaptureAudio(audio: audio, defaults: definition.Audio),
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
    /// <returns>The drift hint token.</returns>
    public static string DescribeDrift(WorldDefinition definition, WorldRenderSettings render, WorldPopulation population, WorldScreenBinder binder, WorldAudioDirector audio) {
        var drifted = new List<string>(capacity: 4);

        if (CaptureRender(render: render, defaults: definition.Render) != definition.Render) {
            drifted.Add(item: "render");
        }

        if (CapturePopulation(population: population, defaults: definition.Population) != definition.Population) {
            drifted.Add(item: "population");
        }

        if (ScreensDrifted(screens: definition.Screens, binder: binder)) {
            drifted.Add(item: "screens");
        }

        if (audio.MasterVolumeLeverEngaged && (audio.EffectiveMasterVolume != definition.Audio.MasterGain)) {
            drifted.Add(item: "audio");
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

    // Fold the live census: the current simulated stand-in count and the live peer-source default become the boot
    // census. The local-seat default is document-level, not a live seating figure, so it stays as authored.
    private static WorldPopulationDefaults CapturePopulation(WorldPopulation population, WorldPopulationDefaults defaults) => (defaults with {
        NetworkPlayers = population.SimulatedCount,
        DefaultPeerSource = population.DefaultPeerSource,
    });

    // Fold a live machine insert on each declared screen back into that row's Machine source; a screen with no live
    // insert keeps its declared source untouched.
    private static IReadOnlyList<WorldScreen> CaptureScreens(IReadOnlyList<WorldScreen> screens, WorldScreenBinder binder) {
        var captured = new List<WorldScreen>(capacity: screens.Count);

        foreach (var screen in screens) {
            captured.Add(item: (binder.TryReadMachineInsert(index: screen.Index, engine: out var engine, contentPath: out var contentPath, options: out var options)
                ? (screen with { Source = new WorldScreenSource.Machine(Engine: engine, ContentPath: contentPath, Options: options) })
                : screen));
        }

        return captured;
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
