using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The one thick startup gate for World-local authored data. Runtime systems may assume these structural and
/// platform-facing invariants instead of silently overwriting duplicate indices or discovering bad extents on a GPU
/// allocation path.</summary>
/// <remarks>
/// <para><b>Field taxonomy — the checklist a future field lands against.</b> A definition field is one of two kinds,
/// and that kind decides its numeric contract:</para>
/// <para><b>SIM-AFFECTING</b> — quantized to fixed point ONCE at compile via the <c>Fixed*</c> compilers (exactly the
/// <see cref="FixedWorldKit"/> / <see cref="FixedMotionTuning"/> / <see cref="FixedWanderTuning"/> pattern) and then
/// deterministic: the profileless <see cref="WorldDefinition.Motion"/> and <see cref="WorldDefinition.Wander"/> tunings,
/// every kit's tuning/flavor/action rows, the seat <see cref="WorldDefinition.SpawnPoints"/>, the
/// <see cref="WorldDefinition.Population"/> census, and the <see cref="WorldDefinition.Assignment"/> policy. These are
/// gated here for finiteness and physical sign (speeds/gravities/radii positive; windows/cooldowns non-negative) so a
/// bad authored value cannot poison the quantized sim.</para>
/// <para><b>PRESENTATION-ONLY</b> — stays float, never enters sim state: the scene albedos, the camera FOV/extent, the
/// screen geometry/sources, and the render-lever defaults. These are gated only for structural GPU safety (finite
/// frames, bounded extents, non-degenerate bases).</para>
/// </remarks>
internal static class WorldDefinitionValidator {
    // World-local CPU/GPU screen sources are intentionally presentation-sized. A bad authored extent must fail here,
    // before it can become an unchecked pixel-buffer or offscreen-render allocation.
    private const int MaxSurfaceDimension = 4096;
    // A look scale feeds the stamp pool's per-instance bound radius; an unbounded one is a GPU-SAFETY issue (a
    // spatial-cull metadata blow-up), not a taste one, so it carries a hard ceiling beside MaxSurfaceDimension.
    private const float MaxLookScale = 16f;
    private const float MinimumBasisLengthSquared = 1e-8f;
    // Each camera can carry a persistent offscreen render (a View screen samples it); the bound keeps a floody document
    // from declaring thousands of budgeted offscreen engines (ViewStack.MaxRegisteredViews is the runtime floor).
    private const int MaxCameras = 64;

    /// <summary>Validates a candidate definition without throwing — the apply-time seam a buffered mutation runs its
    /// composed candidate through before the server swaps it in. On failure, <paramref name="reason"/> carries the
    /// collapsed one-line error list for the loud rejection console line.</summary>
    /// <param name="definition">The candidate definition.</param>
    /// <param name="reason">The collapsed failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the candidate is valid.</returns>
    public static bool TryValidate(WorldDefinition definition, out string reason) {
        try {
            Validate(definition: definition);
            reason = string.Empty;

            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }

    public static void Validate(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = new List<string>();

        if (!string.Equals(a: definition.Schema, b: WorldDefinition.SchemaVersion, comparisonType: StringComparison.Ordinal)) {
            errors.Add(item: $"schema '{definition.Schema ?? "(absent)"}' is not '{WorldDefinition.SchemaVersion}'.");
        }

        // The profileless fallback tunings (SIM-AFFECTING): a stand-in with no seated profile advances on Motion, and
        // the wander producer reads Wander — both compile to fixed point, so a non-finite or unphysical value here
        // poisons the quantized sim.
        ValidateMotionTuning(tuning: definition.Motion, path: "motion", errors: errors);
        ValidateWanderTuning(wander: definition.Wander, path: "wander", errors: errors);

        if ((definition.Population.LocalPlayers < 1) || (definition.Population.LocalPlayers > WorldPopulation.LocalSeatCount)) {
            errors.Add(item: $"population.localPlayers {definition.Population.LocalPlayers} is outside 1..{WorldPopulation.LocalSeatCount}.");
        }

        if ((definition.Population.NetworkPlayers < 0) || (definition.Population.NetworkPlayers > WorldPopulation.MaxPopulationSimulated)) {
            errors.Add(item: $"population.networkPlayers {definition.Population.NetworkPlayers} is outside 0..{WorldPopulation.MaxPopulationSimulated}.");
        }

        // The audio asset sections come FIRST among the row sets: emission facets on scene rows/placements and the
        // speaker rows below all resolve against the tune/patch id sets. The audio defaults coalesce here (the
        // WorldAuthoringDefaults absence convention) so every downstream read sees a concrete row.
        var audio = (definition.Audio ?? WorldAudioDefaults.Default);
        var tuneIds = ValidateTunes(tunes: definition.Tunes, errors: errors);
        var patchIds = ValidatePatches(patches: definition.Patches, errors: errors);

        ValidateScene(scene: definition.Scene, patchIds: patchIds, errors: errors);

        var spawnPointIds = ValidateSpawnPoints(spawnPoints: definition.SpawnPoints, errors: errors);

        // The spawn policy runs AFTER ValidateSpawnPoints has produced the id set a `points` policy resolves against.
        ValidateSpawnPolicy(policy: definition.Population.SpawnPolicy, spawnPointIds: spawnPointIds, errors: errors);

        if (definition.Render is null) {
            errors.Add(item: "render is required.");
        }

        var (kitNames, attendCapableKits) = ValidateKits(definition: definition, errors: errors);

        ValidateAssignment(assignment: definition.Assignment, kitNames: kitNames, errors: errors);
        ValidateAddons(addons: definition.Addons, errors: errors);
        ValidateBindingOverlays(overlays: definition.BindingOverlays, errors: errors);
        ValidateStorage(storage: definition.Storage, errors: errors);

        // The host section: absent-in-JSON coalesces to the built-in default HERE (the same absence convention the
        // storage/authoring/audio sections use). Called early — it references no other section.
        ValidateHost(host: (definition.Host ?? WorldHostDefaults.Default), errors: errors);

        // The editor/authoring policy row: absent-in-JSON coalesces to the built-in default HERE (the
        // same absence-coalesce convention WorldStorageDefaults uses) so every downstream read below sees a concrete row, never null.
        var authoring = (definition.Authoring ?? WorldAuthoringDefaults.Default);

        ValidateAuthoring(authoring: authoring, errors: errors);

        // The contact-solver tuning (SIM-AFFECTING): absent-in-JSON coalesces to WorldCollision.None (collision off,
        // the plan-wide new-section idiom), so every downstream solidity read sees a concrete provider.
        var collision = (definition.Collision ?? WorldCollision.None);

        ValidateCollision(collision: collision, errors: errors);

        var creationIds = ValidateCreations(creations: definition.Creations, errors: errors);

        // The LOOK rows go AFTER ValidateCreations (a creation look resolves its CreationId against the id-set that
        // returns) and BEFORE ValidatePlacements (a future Inhabit facet will resolve its Look against the look-name set
        // this returns) — the same forward-threading creationIds already rides.
        var lookNames = ValidateLooks(looks: definition.Looks, creationIds: creationIds, errors: errors);

        ValidateLookAssignment(assignment: definition.LookAssignment, lookNames: lookNames, errors: errors);

        var placementIds = ValidatePlacements(placements: definition.Placements, definition: definition, creationIds: creationIds, lookNames: lookNames, kitNames: kitNames, attendCapableKits: attendCapableKits, authoring: authoring, patchIds: patchIds, provider: collision.Provider, errors: errors);

        var cameras = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (definition.Cameras is not { } authoredCameras) {
            errors.Add(item: "cameras is required.");
        } else {
            if (authoredCameras.Count > MaxCameras) {
                errors.Add(item: $"cameras count {authoredCameras.Count} exceeds the maximum of {MaxCameras}.");
            }

            for (var index = 0; (index < authoredCameras.Count); index++) {
                var camera = authoredCameras[index];
                var path = $"cameras[{index}]";

                if (camera is null) {
                    errors.Add(item: $"{path} is required.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value: camera.Name)) {
                    errors.Add(item: $"{path}.name is required.");
                } else if (!cameras.Add(item: camera.Name)) {
                    errors.Add(item: $"{path}.name '{camera.Name}' is duplicated.");
                }

                // WHERE it rides: a null anchor poses directly in world space (offset IS the eye), any anchor kind
                // resolves through the one shared resolver (P9) — a placement-anchored camera is no longer rejected.
                if (camera.Anchor is { } anchor) {
                    ValidateAnchor(anchor: anchor, placements: definition.Placements, placementIds: placementIds, creations: definition.Creations, path: $"{path}.anchor", errors: errors);
                }

                if (!IsFinite(value: camera.Offset)) {
                    errors.Add(item: $"{path}.offset must contain finite coordinates.");
                }

                // HOW it frames.
                ValidateRig(rig: camera.Rig, path: $"{path}.rig", errors: errors);

                if ((camera.RenderWidth == 0U) || (camera.RenderHeight == 0U) ||
                    (camera.RenderWidth > MaxSurfaceDimension) || (camera.RenderHeight > MaxSurfaceDimension)) {
                    errors.Add(item: $"{path} render dimensions must be within 1..{MaxSurfaceDimension}.");
                }
            }
        }

        // The window-composition defaults: absent-in-JSON coalesces to the built-in default (empty layouts -> the
        // built-in seat ladder), so every downstream read sees a concrete row. Named cameras a layout slot references
        // must resolve against the camera set just built.
        ValidateViews(views: (definition.Views ?? WorldViewDefaults.Default), cameras: cameras, errors: errors);

        var screenIndices = new HashSet<int>();
        // The declared-live console sources (screens[*].source, NOT magazine entries): the feed owns ONE upload surface,
        // so at most one may be live at a time. A console entry sitting unselected in a magazine is legal.
        var consoleLiveIndices = new List<int>();
        // The derived-face slots the binder reserves up front (Program.cs concatenates them after the document screens):
        // a document screen at one of these indices would silently collide with the reserved placeholder in the binder's
        // dict-fill, so the range is carved out of the authored screen-index space here.
        var reservedFaceStart = Puck.World.Client.WorldCreationFacets.DerivedFaceBase;
        var reservedFaceEnd = (reservedFaceStart + (definition.Authoring ?? WorldAuthoringDefaults.Default).DerivedFaceScreens);

        if (definition.Screens is not { } screens) {
            errors.Add(item: "screens is required.");
        } else {
            for (var index = 0; (index < screens.Count); index++) {
                var screen = screens[index];
                var path = $"screens[{index}]";

                if (screen is null) {
                    errors.Add(item: $"{path} is required.");
                    continue;
                }

                if ((screen.Index < 0) || (screen.Index >= SdfProgramBuilder.MaxScreenSurfaces)) {
                    errors.Add(item: $"{path}.index {screen.Index} is outside 0..{(SdfProgramBuilder.MaxScreenSurfaces - 1)}.");
                } else if (!screenIndices.Add(item: screen.Index)) {
                    errors.Add(item: $"{path}.index {screen.Index} is duplicated.");
                } else if ((screen.Index >= reservedFaceStart) && (screen.Index < reservedFaceEnd)) {
                    errors.Add(item: $"{path}.index {screen.Index} is inside the reserved derived-face range {reservedFaceStart}..{(reservedFaceEnd - 1)} (creation faces bind there — author screens below {reservedFaceStart}).");
                }

                if (!IsFinite(value: screen.Origin) || !IsFinite(value: screen.Right) || !IsFinite(value: screen.Up)) {
                    errors.Add(item: $"{path} frame vectors must contain finite coordinates.");
                } else if ((screen.Right.LengthSquared() <= MinimumBasisLengthSquared) ||
                    (screen.Up.LengthSquared() <= MinimumBasisLengthSquared) ||
                    (Vector3.Cross(vector1: screen.Right, vector2: screen.Up).LengthSquared() <= MinimumBasisLengthSquared)) {
                    errors.Add(item: $"{path} right/up vectors must be non-zero and linearly independent.");
                }

                if (!float.IsFinite(f: screen.HalfWidth) || !float.IsFinite(f: screen.HalfHeight) || !float.IsFinite(f: screen.HalfDepth) ||
                    (screen.HalfWidth <= 0f) || (screen.HalfHeight <= 0f) || (screen.HalfDepth <= 0f)) {
                    errors.Add(item: $"{path} half extents must be finite and positive.");
                }

                // The declared source and each magazine entry cross the SAME source gate (a magazine entry could
                // otherwise name an undeclared camera). A declared console source counts against the one-live ceiling;
                // a console entry sitting in the magazine does not.
                if (ValidateScreenSource(source: screen.Source, path: $"{path}.source", cameras: cameras, errors: errors)) {
                    consoleLiveIndices.Add(item: screen.Index);
                }

                ValidateRoute(route: screen.Route, path: $"{path}.route", errors: errors);
                ValidateMagazine(magazine: screen.Magazine, path: $"{path}.magazine", cameras: cameras, errors: errors);

                // The screen's solidity facet — a box collider from the slab's frame + margin (R3). The effective
                // per-axis extent must stay positive (a margin that inverts the box is rejected by name).
                if (screen.Solid is { } screenSolid) {
                    RequireFinite(value: screenSolid.Margin, name: $"{path}.solid.margin", errors: errors);
                    RequirePositiveEffectiveExtent(halfExtents: new Vector3(x: screen.HalfWidth, y: screen.HalfHeight, z: screen.HalfDepth), margin: screenSolid.Margin, path: $"{path}.solid.margin", errors: errors);
                }
            }
        }

        // The one-live-console ceiling: the console feed owns a single upload surface, so a second declared console
        // screen is an error naming both indices.
        if (consoleLiveIndices.Count > 1) {
            errors.Add(item: $"at most one screen may declare a console source, but screens {string.Join(separator: " and ", values: consoleLiveIndices)} both do.");
        }

        // The cable links resolve against the declared screen index set built above.
        ValidateLinks(links: definition.Links, screenIndices: screenIndices, errors: errors);

        // Speakers and the audio defaults validate LAST: their references span every earlier row set (the screen
        // index set, the placement rows, the tune/patch ids, the camera names — and the cue table's emitter
        // placements name speaker rows, so the speaker pass hands its name set forward).
        var speakerNames = ValidateSpeakers(definition: definition, screenIndices: screenIndices, placementIds: placementIds, tuneIds: tuneIds, patchIds: patchIds, errors: errors);

        ValidateAudioDefaults(audio: audio, cameras: cameras, patchIds: patchIds, speakerNames: speakerNames, errors: errors);

        if (errors.Count > 0) {
            throw new InvalidOperationException(message: $"Invalid WorldDefinition:{Environment.NewLine} - {string.Join(separator: $"{Environment.NewLine} - ", values: errors)}");
        }
    }

    // The tune ASSET rows: id presence/uniqueness, the document's own strict schema + structural invariants through
    // AudioCanonicalizer (the ONE pipeline — never a re-implementation), and the hash pin (the carried hash
    // must equal the canonical hash — a tampered/corrupt row rejects loudly). Returns the id set for the source gate.
    private static HashSet<string> ValidateTunes(IReadOnlyList<WorldTune> tunes, List<string> errors) {
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (tunes is null) {
            errors.Add(item: "tunes is required.");

            return ids;
        }

        for (var index = 0; (index < tunes.Count); index++) {
            var tune = tunes[index];
            var path = $"tunes[{index}]";

            if (tune is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: tune.Id)) {
                errors.Add(item: $"{path}.id is required.");
            } else if (!ids.Add(item: tune.Id)) {
                errors.Add(item: $"{path}.id '{tune.Id}' is duplicated.");
            }

            if (tune.Document is null) {
                errors.Add(item: $"{path}.document is required.");

                continue;
            }

            var violations = Puck.Authoring.AudioCanonicalizer.Validate(document: tune.Document);

            if (violations.Count > 0) {
                foreach (var violation in violations) {
                    errors.Add(item: $"{path}.document.{violation.Path}: {violation.Message}");
                }

                continue;
            }

            var canonical = Puck.Authoring.AudioCanonicalizer.Canonicalize(document: tune.Document, source: tune.Id);

            if (!string.Equals(a: tune.Hash, b: canonical.Hash, comparisonType: StringComparison.Ordinal)) {
                errors.Add(item: $"{path}.hash '{tune.Hash}' does not match the canonical sha256 '{canonical.Hash}'.");
            }
        }

        return ids;
    }

    // The synth-patch ASSET rows: the same strict pipeline + hash pin through SynthPatchCanonicalizer. Returns the id
    // set for the source and emission-facet gates.
    private static HashSet<string> ValidatePatches(IReadOnlyList<WorldPatch> patches, List<string> errors) {
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (patches is null) {
            errors.Add(item: "patches is required.");

            return ids;
        }

        for (var index = 0; (index < patches.Count); index++) {
            var patch = patches[index];
            var path = $"patches[{index}]";

            if (patch is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: patch.Id)) {
                errors.Add(item: $"{path}.id is required.");
            } else if (!ids.Add(item: patch.Id)) {
                errors.Add(item: $"{path}.id '{patch.Id}' is duplicated.");
            }

            if (patch.Document is null) {
                errors.Add(item: $"{path}.document is required.");

                continue;
            }

            var violations = Puck.Authoring.SynthPatchCanonicalizer.Validate(document: patch.Document);

            if (violations.Count > 0) {
                foreach (var violation in violations) {
                    errors.Add(item: $"{path}.document.{violation.Path}: {violation.Message}");
                }

                continue;
            }

            var canonical = Puck.Authoring.SynthPatchCanonicalizer.Canonicalize(document: patch.Document, source: patch.Id);

            if (!string.Equals(a: patch.Hash, b: canonical.Hash, comparisonType: StringComparison.Ordinal)) {
                errors.Add(item: $"{path}.hash '{patch.Hash}' does not match the canonical sha256 '{canonical.Hash}'.");
            }
        }

        return ids;
    }

    // The speaker rows (PRESENTATION-ONLY — audio never enters sim state): name presence/uniqueness, the per-kind
    // pose/extent invariants, the feed (source resolution, channel token, the gain ceiling), and the attenuation
    // policy. A Machine source checks only that the screen row EXISTS — never its declared source kind (runtime
    // inserts overlay declared sources; no live machine at drain time is silence, not a reject). Returns the name
    // set (the cue table's emitter placements resolve against it).
    private static HashSet<string> ValidateSpeakers(WorldDefinition definition, HashSet<int> screenIndices, HashSet<string> placementIds, HashSet<string> tuneIds, HashSet<string> patchIds, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (definition.Speakers is not { } speakers) {
            errors.Add(item: "speakers is required.");

            return names;
        }

        for (var index = 0; (index < speakers.Count); index++) {
            var speaker = speakers[index];
            var path = $"speakers[{index}]";

            if (speaker is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: speaker.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (!names.Add(item: speaker.Name)) {
                errors.Add(item: $"{path}.name '{speaker.Name}' is duplicated.");
            }

            switch (speaker) {
                case WorldSpeaker.Fixed fixedSpeaker:
                    if (!IsFinite(value: fixedSpeaker.Position)) {
                        errors.Add(item: $"{path}.position must contain finite coordinates.");
                    }

                    break;
                case WorldSpeaker.Anchored anchoredSpeaker:
                    // Speakers resolve EVERY anchor kind (placements included — unlike the camera pose path), so the
                    // shared anchor gate runs without the camera's placement rejection.
                    ValidateAnchor(anchor: anchoredSpeaker.Anchor, placements: definition.Placements, placementIds: placementIds, creations: definition.Creations, path: $"{path}.anchor", errors: errors);

                    if (!IsFinite(value: anchoredSpeaker.Offset)) {
                        errors.Add(item: $"{path}.offset must contain finite coordinates.");
                    }

                    break;
                case WorldSpeaker.Bed bed:
                    if (!IsFinite(value: bed.Center)) {
                        errors.Add(item: $"{path}.center must contain finite coordinates.");
                    }

                    RequirePositive(value: bed.Radius, name: $"{path}.radius", errors: errors);

                    // The inner radius must leave a live envelope band: the mixer's finite-support law needs
                    // inner < outer (inner == outer would divide the smoothstep by zero support).
                    if ((bed.InnerRadius is { } innerRadius) &&
                        (!float.IsFinite(f: innerRadius) || (innerRadius < 0f) || (float.IsFinite(f: bed.Radius) && (innerRadius >= bed.Radius)))) {
                        errors.Add(item: $"{path}.innerRadius {innerRadius} must be finite, non-negative, and less than radius {bed.Radius}.");
                    }

                    if (bed.FadeSeconds is { } fadeSeconds) {
                        RequireNonNegative(value: fadeSeconds, name: $"{path}.fadeSeconds", errors: errors);
                    }

                    break;
                default:
                    errors.Add(item: $"{path} is an unknown speaker kind.");

                    break;
            }

            ValidateFeed(feed: speaker.Feed, screenIndices: screenIndices, tuneIds: tuneIds, patchIds: patchIds, path: $"{path}.feed", errors: errors);

            if (speaker.Attenuation is { } attenuation) {
                RequirePositive(value: attenuation.Radius, name: $"{path}.attenuation.radius", errors: errors);

                if ((attenuation.Curve is { } curve) && !string.Equals(a: curve, b: WorldAudioDefaults.CurveSmoothstep, comparisonType: StringComparison.Ordinal)) {
                    errors.Add(item: $"{path}.attenuation.curve '{curve}' must be '{WorldAudioDefaults.CurveSmoothstep}' or null.");
                }
            }
        }

        return names;
    }

    private static void ValidateFeed(WorldSpeakerFeed? feed, HashSet<int> screenIndices, HashSet<string> tuneIds, HashSet<string> patchIds, string path, List<string> errors) {
        if (feed is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if (feed.Channel is not (WorldSpeakerFeed.ChannelMix or WorldSpeakerFeed.ChannelLeft or WorldSpeakerFeed.ChannelRight)) {
            errors.Add(item: $"{path}.channel '{feed.Channel}' must be '{WorldSpeakerFeed.ChannelMix}', '{WorldSpeakerFeed.ChannelLeft}', or '{WorldSpeakerFeed.ChannelRight}'.");
        }

        RequireGain(value: feed.Gain, name: $"{path}.gain", errors: errors);

        switch (feed.Source) {
            case null:
                errors.Add(item: $"{path}.source is required.");

                break;
            case WorldSpeakerSource.Machine machine when !screenIndices.Contains(item: machine.ScreenIndex):
                errors.Add(item: $"{path}.source.screenIndex {machine.ScreenIndex} names no declared screen.");

                break;
            case WorldSpeakerSource.Tune tune when (string.IsNullOrWhiteSpace(value: tune.TuneId) || !tuneIds.Contains(item: tune.TuneId)):
                errors.Add(item: $"{path}.source.tuneId '{tune.TuneId}' names no tune row.");

                break;
            case WorldSpeakerSource.Synth synth when (string.IsNullOrWhiteSpace(value: synth.PatchId) || !patchIds.Contains(item: synth.PatchId)):
                errors.Add(item: $"{path}.source.patchId '{synth.PatchId}' names no patch row.");

                break;
        }
    }

    // An emission facet (scene rows + placements): the patch resolves, the level rides the shared gain ceiling, the
    // optional radius is a positive finite support.
    private static void ValidateEmission(WorldEmission? emission, HashSet<string> patchIds, string path, List<string> errors) {
        if (emission is null) {
            return;
        }

        if (string.IsNullOrWhiteSpace(value: emission.PatchId) || !patchIds.Contains(item: emission.PatchId)) {
            errors.Add(item: $"{path}.patchId '{emission.PatchId}' names no patch row.");
        }

        RequireGain(value: emission.Level, name: $"{path}.level", errors: errors);

        if (emission.Radius is { } radius) {
            RequirePositive(value: radius, name: $"{path}.radius", errors: errors);
        }
    }

    // The audio host-section defaults: the master gain rides the shared ceiling, the coalescing radius/fade are
    // physical, the curve token is v1's one recognized value, the listener policy resolves (focus | seat:<n> |
    // a declared camera name), and every cue-table row resolves (a CLOSED event token, a live patch id, the gain
    // ceiling in thousandths, a placement token whose emitter form names a declared speaker).
    private static void ValidateAudioDefaults(WorldAudioDefaults audio, HashSet<string> cameras, HashSet<string> patchIds, HashSet<string> speakerNames, List<string> errors) {
        RequireGain(value: audio.MasterGain, name: "audio.masterGain", errors: errors);
        RequirePositive(value: audio.DefaultSpeakerRadius, name: "audio.defaultSpeakerRadius", errors: errors);
        RequireNonNegative(value: audio.DefaultBedFadeSeconds, name: "audio.defaultBedFadeSeconds", errors: errors);

        if (!string.Equals(a: audio.DefaultCurve, b: WorldAudioDefaults.CurveSmoothstep, comparisonType: StringComparison.Ordinal)) {
            errors.Add(item: $"audio.defaultCurve '{audio.DefaultCurve ?? "(absent)"}' must be '{WorldAudioDefaults.CurveSmoothstep}'.");
        }

        var listener = audio.Listener;

        if (string.IsNullOrWhiteSpace(value: listener)) {
            errors.Add(item: "audio.listener is required ('focus', 'seat:<n>', or a declared camera name).");
        } else if (!string.Equals(a: listener, b: WorldAudioDefaults.ListenerFocus, comparisonType: StringComparison.Ordinal) && !cameras.Contains(item: listener)) {
            if (listener.StartsWith(value: WorldAudioDefaults.ListenerSeatPrefix, comparisonType: StringComparison.Ordinal)) {
                if (!int.TryParse(s: listener.AsSpan(start: WorldAudioDefaults.ListenerSeatPrefix.Length), result: out var seat) ||
                    (seat < 1) || (seat > WorldPopulation.LocalSeatCount)) {
                    errors.Add(item: $"audio.listener '{listener}' names no seat (expected seat:1..seat:{WorldPopulation.LocalSeatCount}).");
                }
            } else {
                errors.Add(item: $"audio.listener '{listener}' is not 'focus', 'seat:<n>', or a declared camera name.");
            }
        }

        ValidateCues(cues: audio.Cues, patchIds: patchIds, speakerNames: speakerNames, errors: errors);
    }

    // THE CUE TABLE: absent is empty; each row's event token must sit in the CLOSED published vocabulary,
    // its patch must resolve, its gain rides the shared ceiling in thousandths, and an emitter placement must name
    // a declared speaker (at-site and listener are the only other recognized placements).
    private static void ValidateCues(IReadOnlyList<WorldAudioCue>? cues, HashSet<string> patchIds, HashSet<string> speakerNames, List<string> errors) {
        if (cues is null) {
            return;
        }

        for (var index = 0; (index < cues.Count); index++) {
            var cue = cues[index];
            var path = $"audio.cues[{index}]";

            if (cue is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!WorldAudioCue.IsEventToken(token: cue.Event)) {
                errors.Add(item: $"{path}.event '{cue.Event}' is not a published cue event token ({string.Join(separator: " | ", values: WorldAudioCue.EventTokens)}).");
            }

            if (string.IsNullOrWhiteSpace(value: cue.PatchId) || !patchIds.Contains(item: cue.PatchId)) {
                errors.Add(item: $"{path}.patchId '{cue.PatchId}' names no patch row.");
            }

            if ((cue.GainThousandths is { } gain) && ((gain < 0) || (gain > (int)(Puck.Authoring.CreationSoundDocument.MaxLevel * 1000f)))) {
                errors.Add(item: $"{path}.gainThousandths {gain} must be within [0, {(int)(Puck.Authoring.CreationSoundDocument.MaxLevel * 1000f)}].");
            }

            switch (cue.Placement) {
                case WorldAudioCue.PlacementAtSite:
                case WorldAudioCue.PlacementListener:
                    break;
                case { } placement when placement.StartsWith(value: WorldAudioCue.PlacementEmitterPrefix, comparisonType: StringComparison.Ordinal):
                    var speaker = placement[WorldAudioCue.PlacementEmitterPrefix.Length..];

                    if (!speakerNames.Contains(item: speaker)) {
                        errors.Add(item: $"{path}.placement 'emitter:{speaker}' names no declared speaker.");
                    }

                    break;
                default:
                    errors.Add(item: $"{path}.placement '{cue.Placement}' must be '{WorldAudioCue.PlacementAtSite}', '{WorldAudioCue.PlacementListener}', or '{WorldAudioCue.PlacementEmitterPrefix}<speaker-name>'.");

                    break;
            }
        }
    }

    // The one audio gain rule: finite, non-negative, and within the shared ceiling
    // (Puck.Authoring.CreationSoundDocument.MaxLevel — one vocabulary for every audio gain-shaped field).
    private static void RequireGain(float value, string name, List<string> errors) {
        if (!float.IsFinite(f: value) || (value < 0f) || (value > Puck.Authoring.CreationSoundDocument.MaxLevel)) {
            errors.Add(item: $"{name} {value} must be within [0, {Puck.Authoring.CreationSoundDocument.MaxLevel}].");
        }
    }

    // The static scene (PRESENTATION-ONLY): albedos and row geometry are gated only for structural GPU safety
    // (finite colors, finite centers, positive extents, non-negative blends) plus row-id presence and uniqueness,
    // and each row's optional emission facet (patch resolution + gain/radius bounds).
    private static void ValidateScene(WorldScene scene, HashSet<string> patchIds, List<string> errors) {
        if (scene is null) {
            errors.Add(item: "scene is required.");

            return;
        }

        if (!IsFinite(value: scene.GroundAlbedo) || !IsFinite(value: scene.StoneAlbedo)) {
            errors.Add(item: "scene albedos must contain finite components.");
        }

        if (scene.Rows is not { } rows) {
            errors.Add(item: "scene.rows is required.");

            return;
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"scene.rows[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: row.Id)) {
                errors.Add(item: $"{path}.id is required.");
            } else if (!ids.Add(item: row.Id)) {
                errors.Add(item: $"{path}.id '{row.Id}' is duplicated.");
            }

            if (!IsFinite(value: row.Center)) {
                errors.Add(item: $"{path}.center must contain finite coordinates.");
            }

            ValidateEmission(emission: row.Emission, patchIds: patchIds, path: $"{path}.emission", errors: errors);

            switch (row) {
                case WorldSceneRow.Boulder boulder:
                    RequirePositive(value: boulder.Radius, name: $"{path}.radius", errors: errors);
                    RequireNonNegative(value: boulder.Smooth, name: $"{path}.smooth", errors: errors);

                    // The solidity facet's effective-extent check: a margin that inverts the sphere is rejected by name,
                    // not turned into a negative-radius collider.
                    if (boulder.Solid is { } boulderSolid) {
                        RequireFinite(value: boulderSolid.Margin, name: $"{path}.solid.margin", errors: errors);

                        if (float.IsFinite(f: boulder.Radius) && float.IsFinite(f: boulderSolid.Margin) && ((boulder.Radius + boulderSolid.Margin) <= 0f)) {
                            errors.Add(item: $"{path}.solid.margin {boulderSolid.Margin} inverts the collider (radius + margin must be > 0).");
                        }
                    }

                    break;
                case WorldSceneRow.Slab slab:
                    RequirePositive(value: slab.HalfExtents.X, name: $"{path}.halfExtents.x", errors: errors);
                    RequirePositive(value: slab.HalfExtents.Y, name: $"{path}.halfExtents.y", errors: errors);
                    RequirePositive(value: slab.HalfExtents.Z, name: $"{path}.halfExtents.z", errors: errors);
                    RequireNonNegative(value: slab.Round, name: $"{path}.round", errors: errors);
                    RequireNonNegative(value: slab.Smooth, name: $"{path}.smooth", errors: errors);

                    if (!IsFinite(value: slab.Albedo)) {
                        errors.Add(item: $"{path}.albedo must contain finite components.");
                    }

                    if (slab.Solid is { } slabSolid) {
                        RequireFinite(value: slabSolid.Margin, name: $"{path}.solid.margin", errors: errors);
                        RequirePositiveEffectiveExtent(halfExtents: slab.HalfExtents, margin: slabSolid.Margin, path: $"{path}.solid.margin", errors: errors);
                    }

                    break;
                default:
                    errors.Add(item: $"{path} is an unknown scene-row kind.");

                    break;
            }
        }
    }

    // The seat spawns (SIM-AFFECTING placement): id presence/uniqueness plus finite positions, and enough rows to cover
    // every local slot (order maps slots).
    private static HashSet<string> ValidateSpawnPoints(IReadOnlyList<WorldSpawnPoint> spawnPoints, List<string> errors) {
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (spawnPoints is null) {
            errors.Add(item: "spawnPoints is required.");

            return ids;
        }

        if (spawnPoints.Count < WorldPopulation.LocalSeatCount) {
            errors.Add(item: $"spawnPoints provides {spawnPoints.Count} entries; {WorldPopulation.LocalSeatCount} local slots require at least that many.");
        }

        for (var index = 0; (index < spawnPoints.Count); index++) {
            var spawn = spawnPoints[index];

            if (string.IsNullOrWhiteSpace(value: spawn.Id)) {
                errors.Add(item: $"spawnPoints[{index}].id is required.");
            } else if (!ids.Add(item: spawn.Id)) {
                errors.Add(item: $"spawnPoints[{index}].id '{spawn.Id}' is duplicated.");
            }

            if (!IsFinite(value: spawn.Position)) {
                errors.Add(item: $"spawnPoints[{index}].position must contain finite coordinates.");
            }
        }

        return ids;
    }

    // The spawn policy (SIM-AFFECTING): a phyllotaxis radius that is finite and non-negative, or a `points` cycle that
    // names at least one spawn point, every id resolving, with a finite non-negative jitter. Null coalesces to the
    // default (nothing to validate).
    private static void ValidateSpawnPolicy(WorldSpawnPolicy? policy, HashSet<string> spawnPointIds, List<string> errors) {
        switch (policy) {
            case null:
                return;
            case WorldSpawnPolicy.Phyllotaxis phyllotaxis:
                RequireNonNegative(value: phyllotaxis.Radius, name: "population.spawnPolicy.radius", errors: errors);

                break;
            case WorldSpawnPolicy.PointCycle points:
                if ((points.Points is not { Count: > 0 } ids)) {
                    errors.Add(item: "population.spawnPolicy.points must name at least one spawn point.");
                } else {
                    for (var index = 0; (index < ids.Count); index++) {
                        if (string.IsNullOrWhiteSpace(value: ids[index]) || !spawnPointIds.Contains(item: ids[index])) {
                            errors.Add(item: $"population.spawnPolicy.points[{index}] '{ids[index]}' names no spawn point.");
                        }
                    }
                }

                RequireNonNegative(value: points.Jitter, name: "population.spawnPolicy.jitter", errors: errors);

                break;
            default:
                errors.Add(item: $"population.spawnPolicy is an unknown kind '{policy.GetType().Name}'.");

                break;
        }
    }

    // The LOOK rows (PRESENTATION-ONLY): name presence/uniqueness (mirroring the kit-name rule), a source over the
    // closed catalog|creation set with a loud unknown default, a resolvable creation reference, a positive scale under
    // the GPU-safety MaxLookScale ceiling, and non-negative motion values — rejecting a zero-hold replay (an infinite
    // loop) and a timeline replay on a catalog source (no timeline to replay) LOUDLY, never silently. Returns the
    // resolved look-name set (a future Inhabit facet resolves its Look against it).
    private static HashSet<string> ValidateLooks(IReadOnlyList<WorldLook>? looks, HashSet<string> creationIds, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (looks is null) {
            return names;
        }

        for (var index = 0; (index < looks.Count); index++) {
            var look = looks[index];
            var path = $"looks[{index}]";

            if (look is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: look.Name)) {
                errors.Add(item: $"{path} requires a name.");
            } else if (!names.Add(item: look.Name)) {
                errors.Add(item: $"{path} duplicates the name '{look.Name}'.");
            }

            var isCatalog = false;

            switch (look.Source) {
                case WorldLookSource.Catalog catalog:
                    isCatalog = true;

                    if (catalog.Index is { } catalogIndex) {
                        RequireIntRange(value: catalogIndex, min: 0, max: (WorldPopulation.MaxPopulation - 1), name: $"{path}.source.index", errors: errors);
                    }

                    break;
                case WorldLookSource.Creation creation:
                    if (string.IsNullOrWhiteSpace(value: creation.CreationId) || !creationIds.Contains(item: creation.CreationId)) {
                        errors.Add(item: $"{path}.source.creationId '{creation.CreationId}' names no creation row.");
                    }

                    break;
                default:
                    errors.Add(item: $"{path}.source is an unknown kind '{look.Source?.GetType().Name ?? "(null)"}'.");

                    break;
            }

            RequirePositive(value: look.Scale, name: $"{path}.scale", errors: errors);

            if (float.IsFinite(f: look.Scale) && (look.Scale > MaxLookScale)) {
                errors.Add(item: $"{path}.scale {look.Scale} exceeds the {MaxLookScale} look-scale ceiling.");
            }

            RequireNonNegative(value: look.Motion.GaitAmplitude, name: $"{path}.motion.gaitAmplitude", errors: errors);
            RequireNonNegative(value: look.Motion.SecondsPerFrame, name: $"{path}.motion.secondsPerFrame", errors: errors);

            if (look.Motion.ReplayFrames && isCatalog) {
                errors.Add(item: $"{path}.motion.replayFrames cannot be set on a catalog source — there is no timeline to replay.");
            }

            if (look.Motion.ReplayFrames && (!float.IsFinite(f: look.Motion.SecondsPerFrame) || (look.Motion.SecondsPerFrame <= 0f))) {
                errors.Add(item: $"{path}.motion.replayFrames requires a positive secondsPerFrame (a zero-hold replay is an infinite loop).");
            }
        }

        return names;
    }

    // The look assignment policy (PRESENTATION-ONLY): null coalesces to the hash default (nothing to validate); a table
    // needs a non-empty cycle whose every entry resolves to a declared look name. Reuses the assignment shape verbatim.
    // The optional look assignment (PRESENTATION-ONLY, a trailing nullable section): absent is valid; a present row
    // validates through the shared row-assignment gate against the look-name set.
    private static void ValidateLookAssignment(WorldRowAssignment? assignment, HashSet<string> lookNames, List<string> errors) {
        if (assignment is null) {
            return;
        }

        ValidateRowAssignment(assignment: assignment, section: "lookAssignment", rowNoun: "look", rowNames: lookNames, errors: errors);
    }

    // The shared hash/table policy gate for a row-assignment section (kit assignment and look assignment carry the SAME
    // shape): a defined policy, a non-empty cycle under the table policy, and every table entry resolving to a declared
    // row name. Each caller owns its own null policy (kit assignment is required; look assignment is optional).
    private static void ValidateRowAssignment(WorldRowAssignment assignment, string section, string rowNoun, HashSet<string> rowNames, List<string> errors) {
        var isHash = string.Equals(a: assignment.Policy, b: WorldRowAssignment.HashPolicy, comparisonType: StringComparison.Ordinal);
        var isTable = string.Equals(a: assignment.Policy, b: WorldRowAssignment.TablePolicy, comparisonType: StringComparison.Ordinal);

        if (!isHash && !isTable) {
            errors.Add(item: $"{section}.policy '{assignment.Policy ?? "(absent)"}' must be '{WorldRowAssignment.HashPolicy}' or '{WorldRowAssignment.TablePolicy}'.");
        }

        var table = (assignment.Table ?? []);

        if (isTable && (table.Count == 0)) {
            errors.Add(item: $"{section}.table must be non-empty under the '{WorldRowAssignment.TablePolicy}' policy.");
        }

        for (var index = 0; (index < table.Count); index++) {
            if (!rowNames.Contains(item: table[index])) {
                errors.Add(item: $"{section}.table[{index}] '{table[index]}' names no {rowNoun} row.");
            }
        }
    }

    // The kit rows (SIM-AFFECTING): name presence/uniqueness, the seat-kit reference, a defined motion model, and the
    // tuning/flavor/action rows that compile to fixed point. Returns the resolved kit-name set for the assignment gate.
    private static (HashSet<string> KitNames, HashSet<string> AttendCapable) ValidateKits(WorldDefinition definition, List<string> errors) {
        var kitNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var attendCapable = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (definition.Kits is not { Count: > 0 } kits) {
            errors.Add(item: "kits requires at least one row.");

            return (kitNames, attendCapable);
        }

        for (var index = 0; (index < kits.Count); index++) {
            var kit = kits[index];
            var path = $"kits[{index}]";

            if (kit is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: kit.Name)) {
                errors.Add(item: $"{path} requires a name.");
            } else if (!kitNames.Add(item: kit.Name)) {
                errors.Add(item: $"{path} duplicates the name '{kit.Name}'.");
            }

            if (!Enum.IsDefined(value: kit.Model)) {
                errors.Add(item: $"{path}.model '{kit.Model}' is not a defined MotionModel.");
            }

            ValidateMotionTuning(tuning: kit.Tuning, path: $"{path}.tuning", errors: errors);
            ValidateWanderFlavor(flavor: kit.Flavor, path: $"{path}.flavor", errors: errors);
            ValidateActionSpec(spec: kit.PrimaryAction, path: $"{path}.primaryAction", errors: errors);
            ValidateActionSpec(spec: kit.SecondaryAction, path: $"{path}.secondaryAction", errors: errors);
            ValidateCollider(collider: kit.Collider, path: $"{path}.collider", errors: errors);

            if (kit.Attend is { } attend) {
                ValidateAttendFlavor(attend: attend, path: $"{path}.attend", errors: errors);

                if (!string.IsNullOrWhiteSpace(value: kit.Name)) {
                    _ = attendCapable.Add(item: kit.Name);
                }
            }
        }

        if (!kitNames.Contains(item: definition.DefaultSeatKit)) {
            errors.Add(item: $"defaultSeatKit '{definition.DefaultSeatKit}' names no kit row.");
        }

        return (kitNames, attendCapable);
    }

    // The attend-producer flavor (SIM-AFFECTING): the three radii positive; the two deflections in 0..1; and the
    // ordering hysteresis rule ReleaseRadius > NoticeRadius >= StandoffRadius as one named error (the band that stops
    // edge flicker must be non-degenerate).
    private static void ValidateAttendFlavor(AttendFlavor attend, string path, List<string> errors) {
        RequirePositive(value: attend.NoticeRadius, name: $"{path}.noticeRadius", errors: errors);
        RequirePositive(value: attend.ReleaseRadius, name: $"{path}.releaseRadius", errors: errors);
        RequirePositive(value: attend.StandoffRadius, name: $"{path}.standoffRadius", errors: errors);
        RequireUnitInterval(value: attend.Approach, name: $"{path}.approach", errors: errors);
        RequireUnitInterval(value: attend.Orbit, name: $"{path}.orbit", errors: errors);

        if (!Enum.IsDefined(value: attend.Target)) {
            errors.Add(item: $"{path}.target '{attend.Target}' is not a defined AttendTarget.");
        }

        if (float.IsFinite(f: attend.NoticeRadius) && float.IsFinite(f: attend.ReleaseRadius) && float.IsFinite(f: attend.StandoffRadius) &&
            !((attend.ReleaseRadius > attend.NoticeRadius) && (attend.NoticeRadius >= attend.StandoffRadius))) {
            errors.Add(item: $"{path} radii must satisfy releaseRadius ({attend.ReleaseRadius}) > noticeRadius ({attend.NoticeRadius}) >= standoffRadius ({attend.StandoffRadius}).");
        }
    }

    private static void RequireUnitInterval(float value, string name, List<string> errors) {
        if (!float.IsFinite(f: value) || (value < 0f) || (value > 1f)) {
            errors.Add(item: $"{name} {value} must be within 0..1.");
        }
    }

    // The kit assignment policy (SIM-AFFECTING): hash needs nothing more; table needs a non-empty cycle whose every
    // entry resolves to a declared kit name.
    private static void ValidateAssignment(WorldRowAssignment assignment, HashSet<string> kitNames, List<string> errors) {
        if (assignment is null) {
            errors.Add(item: "assignment is required.");

            return;
        }

        ValidateRowAssignment(assignment: assignment, section: "assignment", rowNoun: "kit", rowNames: kitNames, errors: errors);
    }

    // The data-side addon descriptors: non-empty, unique names (the rest is Phase 2b's concern).
    private static void ValidateAddons(IReadOnlyList<WorldAddonRow> addons, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var addon in (addons ?? [])) {
            if ((addon is null) || string.IsNullOrWhiteSpace(value: addon.Name)) {
                errors.Add(item: "an addon requires a name.");
            } else if (!names.Add(item: addon.Name)) {
                errors.Add(item: $"addon name '{addon.Name}' is duplicated.");
            }
        }
    }

    // The per-world binding overlays: non-empty unique ids, and the COMPOSED result (engine default ⊕ every
    // overlay) passes the existing binding compiler — a partial overlay page that only makes sense post-merge still
    // gates against the real runtime artifact, and the binding validator is never reimplemented.
    private static void ValidateBindingOverlays(IReadOnlyList<WorldBindingOverlay> overlays, List<string> errors) {
        if (overlays is null) {
            errors.Add(item: "bindingOverlays is required.");

            return;
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);
        var layers = new List<BindingProfileDocument?> { WorldDefaultBindings.BuildDocument() };

        for (var index = 0; (index < overlays.Count); index++) {
            var overlay = overlays[index];
            var path = $"bindingOverlays[{index}]";

            if (overlay is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: overlay.Id)) {
                errors.Add(item: $"{path}.id is required.");
            } else if (!ids.Add(item: overlay.Id)) {
                errors.Add(item: $"{path}.id '{overlay.Id}' is duplicated.");
            }

            if (overlay.Document is null) {
                errors.Add(item: $"{path}.document is required.");
            } else {
                layers.Add(item: overlay.Document);
            }
        }

        try {
            _ = BindingProfile.Compile(document: WorldBindingComposer.Compose(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: layers)));
        } catch (ArgumentException exception) {
            errors.Add(item: $"bindingOverlays do not compose into a valid mapping: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }
    }

    // The editor/authoring policy row: every field finite/positive with a sane ceiling. The BOOT-CONSUMED
    // fields (headroom, max-repeat-per-segment) are additionally capped against the engine's own limits — see
    // WorldAuthoringDefaults' remarks for which fields are boot-consumed vs. live-consumed — so a bad authored value
    // can never reach a boot's frozen render-envelope probe (a live-consumed field's bad value is caught the same
    // way, on every mutation, since the validator re-runs on every composed candidate).
    private static void ValidateAuthoring(WorldAuthoringDefaults authoring, List<string> errors) {
        RequireIntRange(value: authoring.AuthoringHeadroomRows, min: 0, max: 256, name: "authoring.authoringHeadroomRows", errors: errors);
        RequireIntRange(value: authoring.AuthoringHeadroomScreens, min: 0, max: SdfProgramBuilder.MaxScreenSurfaces, name: "authoring.authoringHeadroomScreens", errors: errors);
        RequireIntRange(value: authoring.AuthoringHeadroomPlacements, min: 0, max: 256, name: "authoring.authoringHeadroomPlacements", errors: errors);
        RequireIntRange(value: authoring.MaxRepeatPerSegment, min: 1, max: 64, name: "authoring.maxRepeatPerSegment", errors: errors);

        RequirePositive(value: authoring.MinPlacementScale, name: "authoring.minPlacementScale", errors: errors);
        RequirePositive(value: authoring.MaxPlacementScale, name: "authoring.maxPlacementScale", errors: errors);

        if (float.IsFinite(f: authoring.MinPlacementScale) && float.IsFinite(f: authoring.MaxPlacementScale) &&
            (authoring.MinPlacementScale > authoring.MaxPlacementScale)) {
            errors.Add(item: $"authoring.minPlacementScale {authoring.MinPlacementScale} exceeds authoring.maxPlacementScale {authoring.MaxPlacementScale}.");
        }

        RequirePositive(value: authoring.CandidateRadius, name: "authoring.candidateRadius", errors: errors);
        RequireIntRange(value: authoring.CandidateCap, min: 1, max: 256, name: "authoring.candidateCap", errors: errors);

        if (!float.IsFinite(f: authoring.WorkbenchFraction) || (authoring.WorkbenchFraction <= 0f) || (authoring.WorkbenchFraction >= 1f)) {
            errors.Add(item: $"authoring.workbenchFraction {authoring.WorkbenchFraction} must be finite and strictly between 0 and 1.");
        }

        RequireIntRange(value: authoring.PreviewDeadlineFrames, min: 1, max: 600, name: "authoring.previewDeadlineFrames", errors: errors);
        // The derived-face reserve: the slots boot-registered at [DerivedFaceBase, DerivedFaceBase + count). The ceiling
        // is the screen-surface span ABOVE the reserved base — a larger count would push a reserved slot past the
        // engine's MaxScreenSurfaces and throw at the first frame.
        RequireIntRange(value: authoring.DerivedFaceScreens, min: 0, max: (SdfProgramBuilder.MaxScreenSurfaces - Puck.World.Client.WorldCreationFacets.DerivedFaceBase), name: "authoring.derivedFaceScreens", errors: errors);
    }

    // The creation ASSET rows: id presence/uniqueness, the document's own strict schema + structural invariants
    // through CreationCanonicalizer (the ONE pipeline — never a re-implementation), the hash pin (the carried
    // hash must equal the canonical hash — a tampered/corrupt row rejects loudly), and the per-stamp shape budget
    // (word-exact ceiling). Returns the resolved id set for the placement gate.
    private static HashSet<string> ValidateCreations(IReadOnlyList<WorldCreation> creations, List<string> errors) {
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (creations is null) {
            errors.Add(item: "creations is required.");

            return ids;
        }

        for (var index = 0; (index < creations.Count); index++) {
            var creation = creations[index];
            var path = $"creations[{index}]";

            if (creation is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: creation.Id)) {
                errors.Add(item: $"{path}.id is required.");
            } else if (!ids.Add(item: creation.Id)) {
                errors.Add(item: $"{path}.id '{creation.Id}' is duplicated.");
            }

            if (creation.Document is null) {
                errors.Add(item: $"{path}.doc is required.");

                continue;
            }

            var violations = Puck.Authoring.CreationCanonicalizer.Validate(document: creation.Document);

            if (violations.Count > 0) {
                foreach (var violation in violations) {
                    errors.Add(item: $"{path}.doc.{violation.Path}: {violation.Message}");
                }

                continue;
            }

            // The hash pin: recompute through the ONE pipeline and compare — the only accepted hash is the one the
            // pipeline itself computes over this document's canonical bytes.
            var canonical = Puck.Authoring.CreationCanonicalizer.Canonicalize(document: creation.Document, source: creation.Id);

            if (!string.Equals(a: creation.Hash, b: canonical.Hash, comparisonType: StringComparison.Ordinal)) {
                errors.Add(item: $"{path}.hash '{creation.Hash}' does not match the canonical sha256 '{canonical.Hash}'.");
            }

            var stampShapes = creation.Document.StampShapeCount();

            if (stampShapes > WorldPlacementPolicy.MaxShapesPerStamp) {
                errors.Add(item: $"{path} stamps {stampShapes} shapes, exceeding the {WorldPlacementPolicy.MaxShapesPerStamp}-shape per-stamp budget.");
            }

            // Derived-camera names are `creation:{placementId}:{feed}` (Arc 7), so two eyes sharing a feed name would
            // collide — reject the duplicate at the source. A null Feed derives from the eye's own id (unique already).
            var feeds = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var camera in (creation.Document.Cameras ?? [])) {
                var feed = (camera.Feed ?? camera.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));

                if (!feeds.Add(item: feed)) {
                    errors.Add(item: $"{path}.doc.cameras feed '{feed}' is declared by more than one eye.");
                }
            }
        }

        return ids;
    }

    // The placement INSTANCE rows: id presence/uniqueness, the creation reference, finite transform, the policy
    // scale envelope, the repeat facet's positive counts / finite spacings, the mirror token, and the animated-row
    // constraints (static-only facets; the reserved replay-pool ceiling, word-exact). Returns the resolved id set for
    // the anchor-union gate (a WorldAnchor.Placement resolves against it).
    private static HashSet<string> ValidatePlacements(IReadOnlyList<WorldPlacement> placements, WorldDefinition definition, HashSet<string> creationIds, HashSet<string> lookNames, HashSet<string> kitNames, HashSet<string> attendCapableKits, WorldAuthoringDefaults authoring, HashSet<string> patchIds, WorldContactProvider provider, List<string> errors) {
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);
        var creations = definition.Creations;

        if (placements is null) {
            errors.Add(item: "placements is required.");

            return ids;
        }

        var animatedCount = 0;
        var inhabitantCount = 0;

        for (var index = 0; (index < placements.Count); index++) {
            var placement = placements[index];
            var path = $"placements[{index}]";

            if (placement is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: placement.Id)) {
                errors.Add(item: $"{path}.id is required.");
            } else if (!ids.Add(item: placement.Id)) {
                errors.Add(item: $"{path}.id '{placement.Id}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(value: placement.CreationId) || !creationIds.Contains(item: placement.CreationId)) {
                errors.Add(item: $"{path}.creationId '{placement.CreationId}' names no creation row.");
            }

            if (!IsFinite(value: placement.Position)) {
                errors.Add(item: $"{path}.position must contain finite coordinates.");
            }

            RequireFinite(value: placement.YawDegrees, name: $"{path}.yawDegrees", errors: errors);

            if (!float.IsFinite(f: placement.Scale) || (placement.Scale < authoring.MinPlacementScale) || (placement.Scale > authoring.MaxPlacementScale)) {
                errors.Add(item: $"{path}.scale {placement.Scale} is outside {authoring.MinPlacementScale}..{authoring.MaxPlacementScale}.");
            }

            if (placement.Repeat is { } repeat) {
                if ((repeat.CountX < 1) || (repeat.CountZ < 1)) {
                    errors.Add(item: $"{path}.repeat counts must be at least 1.");
                }

                RequireFinite(value: repeat.SpacingX, name: $"{path}.repeat.spacingX", errors: errors);
                RequireFinite(value: repeat.SpacingZ, name: $"{path}.repeat.spacingZ", errors: errors);
            }

            if ((placement.Mirror is { } mirror) &&
                !string.Equals(a: mirror, b: "x", comparisonType: StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(a: mirror, b: "z", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                errors.Add(item: $"{path}.mirror '{mirror}' must be 'x', 'z', or null.");
            }

            // The emission facet binds to the placement ROOT under a repeat (documented on WorldPlacement) — no
            // per-copy constraint to gate; only patch resolution and the shared gain/radius bounds.
            ValidateEmission(emission: placement.Emission, patchIds: patchIds, path: $"{path}.emission", errors: errors);

            // The R3 provider-coverage rule: a creation stamp has no honest convex proxy the document alone can derive,
            // so a solid placement needs the FIELD provider (Arc 2). Under analytic it is a loud error by name rather
            // than a silently-inert facet. Arc 2 deletes this rejection when it lands the provider that can answer.
            if (placement.Solid is { } placementSolid) {
                RequireFinite(value: placementSolid.Margin, name: $"{path}.solid.margin", errors: errors);

                if (provider == WorldContactProvider.Analytic) {
                    errors.Add(item: $"{path}.solid needs the field contact provider; set collision.provider to 'field' or drop the facet.");
                }
            }

            // The animated-row constraints: a placement of a framed creation replays through the reserved dynamic
            // pool — single copy only (repeat/mirror are static-stamp facets), and at most the reserved pool count.
            if (FindCreation(creations: creations, id: placement.CreationId) is { Document.Frames.Count: > 0 }) {
                animatedCount++;

                if ((placement.Repeat is not null) || (placement.Mirror is not null)) {
                    errors.Add(item: $"{path} is ANIMATED (its creation carries timeline frames) — repeat/mirror facets are static-stamp-only.");
                }
            }

            // The INHABIT facet: a placement's binding to live population bodies (Arc 7). Resolve its kit, gate its
            // source/look/count, and reject the lattice facets (one body cannot be a repeat grid).
            if (placement.Inhabit is { } inhabit) {
                inhabitantCount += Math.Max(val1: inhabit.Count, val2: 0);
                ValidateInhabit(inhabit: inhabit, placement: placement, path: $"{path}.inhabit", definition: definition, kitNames: kitNames, attendCapableKits: attendCapableKits, lookNames: lookNames, errors: errors);

                if ((placement.Repeat is not null) || (placement.Mirror is not null)) {
                    errors.Add(item: $"{path} INHABITS — repeat/mirror facets are incompatible (one body is not a lattice).");
                }
            }

            // The per-instance FACE overrides: each names a declared creation face, no duplicates. The View source's
            // camera name is resolved LENIENTLY (a derived creation-camera name is unknown to the document validator; the
            // binder lights an unresolved feed with its no-signal card, never a hard reject).
            ValidateFaceSources(faceSources: placement.FaceSources, placement: placement, creations: creations, path: $"{path}.faceSources", errors: errors);
        }

        if (animatedCount > WorldPlacementPolicy.MaxStampRegistrations) {
            errors.Add(item: $"{animatedCount} animated placements exceed the {WorldPlacementPolicy.MaxStampRegistrations}-slot replay pool.");
        }

        // The census fit rule (R6): network peers pack up from slot 4, inhabited bodies down from slot 127, so their
        // sum must not exceed the simulated ceiling — reported naming both terms.
        if ((definition.Population.NetworkPlayers + inhabitantCount) > WorldPopulation.MaxPopulationSimulated) {
            errors.Add(item: $"population.networkPlayers ({definition.Population.NetworkPlayers}) + inhabited bodies ({inhabitantCount}) exceed the {WorldPopulation.MaxPopulationSimulated}-body simulated ceiling.");
        }

        return ids;
    }

    // The INHABIT facet: the kit must resolve (its explicit kit name OR the creation's Locomotion token as a kit name),
    // an Attend source needs an attend-capable kit, a named look must be declared, and the count/radius are bounded.
    private static void ValidateInhabit(WorldPlacementInhabit inhabit, WorldPlacement placement, string path, WorldDefinition definition, HashSet<string> kitNames, HashSet<string> attendCapableKits, HashSet<string> lookNames, List<string> errors) {
        var resolvedKit = (inhabit.Kit ?? ResolveLocomotionKit(definition: definition, creationId: placement.CreationId));

        if ((resolvedKit is null) || !kitNames.Contains(item: resolvedKit)) {
            errors.Add(item: $"{path} names no kit; the world declares: {string.Join(separator: ", ", values: kitNames)}.");
        } else if ((inhabit.Source == Puck.World.Protocol.IntentSource.Attend) && !attendCapableKits.Contains(item: resolvedKit)) {
            errors.Add(item: $"{path}.source is Attend but kit '{resolvedKit}' declares no attend flavor.");
        }

        if (!Enum.IsDefined(value: inhabit.Source)) {
            errors.Add(item: $"{path}.source '{inhabit.Source}' is not a defined IntentSource.");
        }

        if ((inhabit.Look is { Length: > 0 } lookName) && !lookNames.Contains(item: lookName)) {
            errors.Add(item: $"{path}.look '{lookName}' names no look row.");
        }

        if ((inhabit.Count < 1) || (inhabit.Count > WorldPopulation.MaxPopulationSimulated)) {
            errors.Add(item: $"{path}.count {inhabit.Count} is outside 1..{WorldPopulation.MaxPopulationSimulated}.");
        }

        RequireNonNegative(value: inhabit.Radius, name: $"{path}.radius", errors: errors);
    }

    // The creation's Locomotion token, resolved as a kit name (the creator's rule; null when the creation/token is absent).
    private static string? ResolveLocomotionKit(WorldDefinition definition, string creationId) {
        foreach (var creation in (definition.Creations ?? [])) {
            if ((creation is not null) && string.Equals(a: creation.Id, b: creationId, comparisonType: StringComparison.Ordinal)) {
                return creation.Document.Behavior?.Locomotion;
            }
        }

        return null;
    }

    // The per-instance face overrides: each names a declared creation face, no duplicate face names.
    private static void ValidateFaceSources(IReadOnlyList<WorldPlacementFace>? faceSources, WorldPlacement placement, IReadOnlyList<WorldCreation> creations, string path, List<string> errors) {
        if (faceSources is not { Count: > 0 } sources) {
            return;
        }

        var creation = FindCreation(creations: creations, id: placement.CreationId);
        var faceNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var face in (creation?.Document.Behavior?.Faces ?? [])) {
            _ = faceNames.Add(item: face.Name);
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < sources.Count); index++) {
            var source = sources[index];
            var facePath = $"{path}[{index}]";

            if ((source is null) || string.IsNullOrWhiteSpace(value: source.Face)) {
                errors.Add(item: $"{facePath}.face is required.");

                continue;
            }

            if (!faceNames.Contains(item: source.Face)) {
                errors.Add(item: $"{facePath}.face '{source.Face}' names no declared face on creation '{placement.CreationId}'.");
            }

            if (!seen.Add(item: source.Face)) {
                errors.Add(item: $"{facePath}.face '{source.Face}' is overridden more than once.");
            }

            if (source.Source is null) {
                errors.Add(item: $"{facePath}.source is required.");
            }
        }
    }

    private static WorldCreation? FindCreation(IReadOnlyList<WorldCreation> creations, string id) {
        foreach (var creation in (creations ?? [])) {
            if ((creation is not null) && string.Equals(a: creation.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return creation;
            }
        }

        return null;
    }

    private static WorldPlacement? FindPlacement(IReadOnlyList<WorldPlacement> placements, string id) {
        foreach (var placement in (placements ?? [])) {
            if ((placement is not null) && string.Equals(a: placement.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return placement;
            }
        }

        return null;
    }

    // The WorldAnchor union: the shared pose-target vocabulary a camera (and a future speaker) rides.
    // Entity/EntityLeaf are index/role bounded; Placement resolves its row and, when ShapeId is present, that the id
    // names a real shape in the referenced placement's creation document, the same rule
    // Puck.Authoring.CreationCameraDocument enforces.
    private static void ValidateAnchor(WorldAnchor anchor, IReadOnlyList<WorldPlacement> placements, HashSet<string> placementIds, IReadOnlyList<WorldCreation> creations, string path, List<string> errors) {
        switch (anchor) {
            case null:
                errors.Add(item: $"{path} is required.");

                break;
            case WorldAnchor.Entity entity:
                if ((entity.Index < 0) || (entity.Index >= WorldPopulation.MaxPopulation)) {
                    errors.Add(item: $"{path}.index {entity.Index} is outside 0..{(WorldPopulation.MaxPopulation - 1)}.");
                }

                break;
            case WorldAnchor.EntityLeaf leaf:
                if ((leaf.Index < 0) || (leaf.Index >= WorldPopulation.MaxPopulation)) {
                    errors.Add(item: $"{path}.index {leaf.Index} is outside 0..{(WorldPopulation.MaxPopulation - 1)}.");
                }

                if (!WorldAvatarCatalog.TryHumanoidRole(token: leaf.Leaf, role: out _)) {
                    errors.Add(item: $"{path}.leaf '{leaf.Leaf}' names no humanoid role (expected one of: {string.Join(separator: ", ", values: WorldAvatarCatalog.HumanoidAnchorRoles)}).");
                }

                break;
            case WorldAnchor.Placement placement:
                if (string.IsNullOrWhiteSpace(value: placement.PlacementId) || !placementIds.Contains(item: placement.PlacementId)) {
                    errors.Add(item: $"{path}.placementId '{placement.PlacementId}' names no placement row.");

                    break;
                }

                if (placement.ShapeId is { } shapeId) {
                    var row = FindPlacement(placements: placements, id: placement.PlacementId);
                    var creation = ((row is null) ? null : FindCreation(creations: creations, id: row.CreationId));

                    if ((creation?.Document.Shapes is not { } shapes) || !ShapesContain(shapes: shapes, id: shapeId)) {
                        errors.Add(item: $"{path}.shapeId {shapeId} names no shape in placement '{placement.PlacementId}''s creation.");
                    }
                }

                break;
            case WorldAnchor.Group group:
                if (group.Indices is { } indices) {
                    for (var index = 0; (index < indices.Count); index++) {
                        if ((indices[index] < 0) || (indices[index] >= WorldPopulation.MaxPopulation)) {
                            errors.Add(item: $"{path}.indices[{index}] {indices[index]} is outside 0..{(WorldPopulation.MaxPopulation - 1)}.");
                        }
                    }
                }

                if (!float.IsFinite(f: group.SmoothRate) || (group.SmoothRate <= 0f)) {
                    errors.Add(item: $"{path}.smoothRate must be positive and finite.");
                }

                break;
            default:
                errors.Add(item: $"{path} is an unknown anchor kind.");

                break;
        }
    }

    // A camera rig (PRESENTATION-ONLY): the field of view finite and in (0, pi), and every rig kind's offsets/scalars
    // finite with the physical signs its engine rig needs (a positive orbit distance and dolly duration, a non-negative
    // focus distance). A closed switch with a loud unknown-kind branch.
    private static void ValidateRig(WorldRig rig, string path, List<string> errors) {
        if (rig is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if (!float.IsFinite(f: rig.FieldOfViewRadians) || (rig.FieldOfViewRadians <= 0f) || (rig.FieldOfViewRadians >= MathF.PI)) {
            errors.Add(item: $"{path}.fieldOfViewRadians must be finite and between 0 and pi.");
        }

        switch (rig) {
            case WorldRig.Chase chase:
                if (!IsFinite(value: chase.EyeOffset) || !IsFinite(value: chase.TargetOffset)) {
                    errors.Add(item: $"{path} needs finite eye and target offsets.");
                }

                if (!float.IsFinite(f: chase.SpreadPullback)) {
                    errors.Add(item: $"{path}.spreadPullback must be finite.");
                }

                break;
            case WorldRig.FirstPerson firstPerson:
                if (!IsFinite(value: firstPerson.EyeOffset)) {
                    errors.Add(item: $"{path}.eyeOffset must contain finite coordinates.");
                }

                if (!float.IsFinite(f: firstPerson.FocusDistance) || (firstPerson.FocusDistance < 0f)) {
                    errors.Add(item: $"{path}.focusDistance must be finite and non-negative.");
                }

                break;
            case WorldRig.Orbit orbit:
                if (!float.IsFinite(f: orbit.Distance) || (orbit.Distance <= 0f)) {
                    errors.Add(item: $"{path}.distance must be positive and finite.");
                }

                if (!float.IsFinite(f: orbit.Yaw) || !float.IsFinite(f: orbit.Pitch) || !IsFinite(value: orbit.PivotLift)) {
                    errors.Add(item: $"{path} needs a finite yaw, pitch, and pivot lift.");
                }

                break;
            case WorldRig.LookAt lookAt:
                if (!IsFinite(value: lookAt.Target)) {
                    errors.Add(item: $"{path}.target must contain finite coordinates.");
                }

                break;
            case WorldRig.Dolly dolly:
                if (!IsFinite(value: dolly.Start) || !IsFinite(value: dolly.End)) {
                    errors.Add(item: $"{path} needs finite start and end points.");
                }

                if (!float.IsFinite(f: dolly.DurationSeconds) || (dolly.DurationSeconds <= 0f)) {
                    errors.Add(item: $"{path}.durationSeconds must be positive and finite.");
                }

                break;
            default:
                errors.Add(item: $"{path} is an unknown rig kind.");

                break;
        }
    }

    // The window-composition defaults (PRESENTATION-ONLY): the seat rig valid, layout names unique, slot rects inside
    // [0,1] and non-degenerate, and every named-camera slot resolving against the authored camera set.
    private static void ValidateViews(WorldViewDefaults views, HashSet<string> cameras, List<string> errors) {
        if (views is null) {
            errors.Add(item: "views is required.");

            return;
        }

        ValidateRig(rig: views.SeatRig, path: "views.seatRig", errors: errors);

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var layouts = (views.Layouts ?? []);

        for (var index = 0; (index < layouts.Count); index++) {
            var layout = layouts[index];
            var path = $"views.layouts[{index}]";

            if (layout is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: layout.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (!names.Add(item: layout.Name)) {
                errors.Add(item: $"{path}.name '{layout.Name}' is duplicated.");
            }

            if (layout.SeatCount < 0) {
                errors.Add(item: $"{path}.seatCount {layout.SeatCount} must be non-negative.");
            }

            if (!float.IsFinite(f: layout.TransitionSeconds) || (layout.TransitionSeconds < 0f)) {
                errors.Add(item: $"{path}.transitionSeconds must be finite and non-negative.");
            }

            if (!float.IsFinite(f: layout.TransitionRenderScale) || (layout.TransitionRenderScale <= 0f) || (layout.TransitionRenderScale > 1f)) {
                errors.Add(item: $"{path}.transitionRenderScale must be finite and within (0, 1].");
            }

            var slots = (layout.Slots ?? []);

            if (slots.Count == 0) {
                errors.Add(item: $"{path}.slots must declare at least one slot.");
            }

            for (var slotIndex = 0; (slotIndex < slots.Count); slotIndex++) {
                var slot = slots[slotIndex];
                var slotPath = $"{path}.slots[{slotIndex}]";

                if (!float.IsFinite(f: slot.X) || !float.IsFinite(f: slot.Y) || !float.IsFinite(f: slot.Width) || !float.IsFinite(f: slot.Height) ||
                    (slot.X < 0f) || (slot.Y < 0f) || (slot.Width <= 0f) || (slot.Height <= 0f) ||
                    ((slot.X + slot.Width) > 1.0001f) || ((slot.Y + slot.Height) > 1.0001f)) {
                    errors.Add(item: $"{slotPath} rect must lie within [0, 1] with positive extents.");
                }

                if ((slot.Camera is { } camera) && !cameras.Contains(item: camera)) {
                    errors.Add(item: $"{slotPath}.camera '{camera}' names no camera row.");
                }
            }
        }
    }

    private static bool ShapesContain(IReadOnlyList<Puck.Authoring.ShapeDocument> shapes, int id) {
        for (var index = 0; (index < shapes.Count); index++) {
            if (shapes[index].Id == id) {
                return true;
            }
        }

        return false;
    }

    // The storage host-section (RESERVED): an endpoint must be an absolute URI when present; a user-id must be
    // non-empty when present. Both null (WorldStorageDefaults.None) is the built-in — cloud unwired, identity declined.
    private static void ValidateStorage(WorldStorageDefaults storage, List<string> errors) {
        if (storage is null) {
            errors.Add(item: "storage is required.");

            return;
        }

        if ((storage.Endpoint is { } endpoint) &&
            (string.IsNullOrWhiteSpace(value: endpoint) || !Uri.TryCreate(uriString: endpoint, uriKind: UriKind.Absolute, result: out _))) {
            errors.Add(item: $"storage.endpoint '{endpoint}' must be an absolute URI or null.");
        }

        if ((storage.UserId is { } userId) && string.IsNullOrWhiteSpace(value: userId)) {
            errors.Add(item: "storage.userId must be non-empty or null.");
        }
    }

    // The host section (PRESENTATION-ONLY): window extents bounded, exit/pacing non-negative, the closed engine enums
    // in range (a mutation can carry an out-of-range cast the JSON converter alone would not catch), and the surface
    // format not the Unknown hole. Genlock is SHAPE-only (null or non-whitespace) — unlike storage.endpoint (nothing yet
    // consumes it), genlock IS wired at boot into the external-clock election, which tolerates an unknown source id.
    private static void ValidateHost(WorldHostDefaults host, List<string> errors) {
        RequireIntRange(value: host.Width, min: 1, max: 16384, name: "host.width", errors: errors);
        RequireIntRange(value: host.Height, min: 1, max: 16384, name: "host.height", errors: errors);
        RequireIntRange(value: host.ExitAfterSeconds, min: 0, max: int.MaxValue, name: "host.exitAfterSeconds", errors: errors);

        if (!double.IsFinite(d: host.TargetHertz) || (host.TargetHertz < 0.0)) {
            errors.Add(item: $"host.targetHertz {host.TargetHertz} must be finite and non-negative (0 = automatic display pacing).");
        }

        if (!Enum.IsDefined(value: host.Backend)) {
            errors.Add(item: $"host.backend '{host.Backend}' is not a defined WorldBackendPreference.");
        }

        if (!Enum.IsDefined(value: host.PresentMode)) {
            errors.Add(item: $"host.presentMode '{host.PresentMode}' is not a defined PresentMode.");
        }

        if (!Enum.IsDefined(value: host.SurfaceFormat) || (host.SurfaceFormat == SurfaceFormat.Unknown)) {
            errors.Add(item: $"host.surfaceFormat '{host.SurfaceFormat}' must be a defined non-Unknown SurfaceFormat.");
        }

        if ((host.Genlock is { } genlock) && string.IsNullOrWhiteSpace(value: genlock)) {
            errors.Add(item: "host.genlock must be non-whitespace or null.");
        }
    }

    // A locomotion/jump tuning: speeds/gravities/max-fall positive, time windows non-negative, everything finite.
    private static void ValidateMotionTuning(in MotionTuning tuning, string path, List<string> errors) {
        RequirePositive(value: tuning.MoveSpeed, name: $"{path}.moveSpeed", errors: errors);
        RequirePositive(value: tuning.TurnSpeed, name: $"{path}.turnSpeed", errors: errors);
        RequireFinite(value: tuning.GroundY, name: $"{path}.groundY", errors: errors);
        RequireNonNegative(value: tuning.JumpSpeed, name: $"{path}.jumpSpeed", errors: errors);
        RequirePositive(value: tuning.RiseGravity, name: $"{path}.riseGravity", errors: errors);
        RequirePositive(value: tuning.FallGravity, name: $"{path}.fallGravity", errors: errors);
        RequirePositive(value: tuning.MaxFallSpeed, name: $"{path}.maxFallSpeed", errors: errors);
        RequireNonNegative(value: tuning.JumpCutMultiplier, name: $"{path}.jumpCutMultiplier", errors: errors);
        RequireNonNegative(value: tuning.CoyoteTime, name: $"{path}.coyoteTime", errors: errors);
        RequireNonNegative(value: tuning.JumpBufferTime, name: $"{path}.jumpBufferTime", errors: errors);
        ValidateResponse(response: tuning.Response, path: $"{path}.response", errors: errors);
    }

    // A kit/motion velocity-response table (SIM-AFFECTING): each row's engage/release rates must be positive (a zero
    // rate never converges — a stuck body, not a feel), each gate is a body-fact-only predicate (the lane-scoped
    // CooldownElapsed/UsesBelow kinds are rejected by name), and a null (always) gate before the final row makes every
    // later row unreachable.
    private static void ValidateResponse(IReadOnlyList<MotionResponse>? response, string path, List<string> errors) {
        if (response is null) {
            return;
        }

        for (var index = 0; (index < response.Count); index++) {
            var row = response[index];
            var rowPath = $"{path}[{index}]";

            if (row is null) {
                errors.Add(item: $"{rowPath} is required.");

                continue;
            }

            RequirePositive(value: row.EngageRate, name: $"{rowPath}.engageRate", errors: errors);
            RequirePositive(value: row.ReleaseRate, name: $"{rowPath}.releaseRate", errors: errors);
            ValidateMotionGate(predicate: row.Gate, path: $"{rowPath}.gate", errors: errors);

            if ((row.Gate is null) && (index < (response.Count - 1))) {
                errors.Add(item: $"{rowPath}.gate is the always-row (null) but is not last — every later row is unreachable.");
            }
        }
    }

    // A motion-response gate: the body-fact predicate vocabulary ONLY. Now/Recently/All are accepted; the lane-scoped
    // CooldownElapsed/UsesBelow kinds are rejected by name ("lane-scoped predicates apply only to action lanes"); an
    // unknown kind is loud. Mirrors ValidatePredicate's structure but narrows the admissible set.
    private static void ValidateMotionGate(ActionPredicate? predicate, string path, List<string> errors) {
        switch (predicate) {
            case null:
                break;
            case ActionPredicate.Now now when !Enum.IsDefined(value: now.Fact):
                errors.Add(item: $"{path}.fact '{now.Fact}' is not a defined ActionFact.");
                break;
            case ActionPredicate.Now:
                break;
            case ActionPredicate.Recently recently:
                if (!Enum.IsDefined(value: recently.Fact)) {
                    errors.Add(item: $"{path}.fact '{recently.Fact}' is not a defined ActionFact.");
                }

                if (!float.IsFinite(f: recently.WindowSeconds) || (recently.WindowSeconds <= 0f)) {
                    errors.Add(item: $"{path}.windowSeconds must be finite and greater than 0.");
                }

                break;
            case ActionPredicate.All all:
                if (all.Predicates is not { Count: > 0 } inner) {
                    errors.Add(item: $"{path}.all must contain at least one predicate.");

                    break;
                }

                for (var index = 0; (index < inner.Count); index++) {
                    ValidateMotionGate(predicate: inner[index], path: $"{path}.all[{index}]", errors: errors);
                }

                break;
            case ActionPredicate.CooldownElapsed:
            case ActionPredicate.UsesBelow:
                errors.Add(item: $"{path} is a lane-scoped predicate ('{PredicateKind(predicate: predicate)}') — lane-scoped predicates apply only to action lanes, not a motion response gate.");
                break;
            default:
                errors.Add(item: $"{path} is an unknown predicate kind.");
                break;
        }
    }

    private static string PredicateKind(ActionPredicate predicate) => predicate switch {
        ActionPredicate.CooldownElapsed => "cooldownElapsed",
        ActionPredicate.UsesBelow => "usesBelow",
        _ => "?",
    };

    // The contact-solver tuning (SIM-AFFECTING). ContactSkin positive; MaxIterations 1..8 (above 8 is a solver
    // pathology, not a choice); the provider defined; MaxSlopeDegrees in (0, 90) — 0 grounds nothing, 90 grounds a wall;
    // GradientProbe non-negative, and > 0 is an error under analytic (the analytic set has no gradient to probe).
    private static void ValidateCollision(WorldCollision collision, List<string> errors) {
        RequirePositive(value: collision.ContactSkin, name: "collision.contactSkin", errors: errors);
        RequireIntRange(value: collision.MaxIterations, min: 1, max: 8, name: "collision.maxIterations", errors: errors);

        if (!Enum.IsDefined(value: collision.Provider)) {
            errors.Add(item: $"collision.provider '{collision.Provider}' is not a defined WorldContactProvider.");
        }

        if (!float.IsFinite(f: collision.MaxSlopeDegrees) || (collision.MaxSlopeDegrees <= 0f) || (collision.MaxSlopeDegrees >= 90f)) {
            errors.Add(item: $"collision.maxSlopeDegrees must be in (0, 90) (was {collision.MaxSlopeDegrees}).");
        }

        RequireNonNegative(value: collision.GradientProbe, name: "collision.gradientProbe", errors: errors);

        if ((collision.Provider == WorldContactProvider.Analytic) && float.IsFinite(f: collision.GradientProbe) && (collision.GradientProbe > 0f)) {
            errors.Add(item: "collision.gradientProbe > 0 is meaningless under provider 'analytic' (the analytic set has no gradient to probe) — set it to 0 or switch to 'field'.");
        }
    }

    // A wander tuning: disc radii positive, drift/frequencies non-negative, the rest finite.
    private static void ValidateWanderTuning(in WanderTuning wander, string path, List<string> errors) {
        RequireNonNegative(value: wander.DriftSpeed, name: $"{path}.driftSpeed", errors: errors);
        RequirePositive(value: wander.SoftRadius, name: $"{path}.softRadius", errors: errors);
        RequirePositive(value: wander.SpawnRadius, name: $"{path}.spawnRadius", errors: errors);
        RequireFinite(value: wander.WeaveAmplitude, name: $"{path}.weaveAmplitude", errors: errors);
        RequireFinite(value: wander.InwardGain, name: $"{path}.inwardGain", errors: errors);
        RequireFinite(value: wander.GoldenAngle, name: $"{path}.goldenAngle", errors: errors);
        RequireNonNegative(value: wander.WeaveFrequencyBase, name: $"{path}.weaveFrequencyBase", errors: errors);
        RequireNonNegative(value: wander.WeaveFrequencyRange, name: $"{path}.weaveFrequencyRange", errors: errors);
    }

    // A kit's wander-producer flavor: waves finite, the primary-press threshold and altitude range non-negative.
    private static void ValidateWanderFlavor(in WanderFlavor flavor, string path, List<string> errors) {
        RequireFinite(value: flavor.Forward, name: $"{path}.forward", errors: errors);
        RequireFinite(value: flavor.StrafeWave, name: $"{path}.strafeWave", errors: errors);
        RequireFinite(value: flavor.TurnWave, name: $"{path}.turnWave", errors: errors);
        RequireFinite(value: flavor.UpWave, name: $"{path}.upWave", errors: errors);
        RequireFinite(value: flavor.PitchWave, name: $"{path}.pitchWave", errors: errors);
        RequireFinite(value: flavor.RollTurn, name: $"{path}.rollTurn", errors: errors);
        RequireNonNegative(value: flavor.PrimaryThreshold, name: $"{path}.primaryThreshold", errors: errors);
        RequireFinite(value: flavor.AltitudeBase, name: $"{path}.altitudeBase", errors: errors);
        RequireNonNegative(value: flavor.AltitudeRange, name: $"{path}.altitudeRange", errors: errors);
    }

    // A lane binding: both trigger channels are optional, but a present trigger's latch must be non-negative and its
    // effects non-empty, and its gate structurally sound.
    private static void ValidateActionSpec(ActionSpec? spec, string path, List<string> errors) {
        if (spec is null) {
            return;
        }

        ValidateTrigger(trigger: spec.OnPress, path: $"{path}.onPress", errors: errors);
        ValidateTrigger(trigger: spec.OnRelease, path: $"{path}.onRelease", errors: errors);
    }

    private static void ValidateTrigger(ActionTrigger? trigger, string path, List<string> errors) {
        if (trigger is null) {
            return;
        }

        RequireNonNegative(value: trigger.LatchSeconds, name: $"{path}.latchSeconds", errors: errors);
        ValidatePredicate(predicate: trigger.Gate, path: $"{path}.gate", errors: errors);

        if (trigger.Effects is not { Count: > 0 } effects) {
            errors.Add(item: $"{path}.effects must be non-empty on a present trigger.");

            return;
        }

        for (var index = 0; (index < effects.Count); index++) {
            ValidateEffect(effect: effects[index], path: $"{path}.effects[{index}]", errors: errors);
        }
    }

    private static void ValidatePredicate(ActionPredicate? predicate, string path, List<string> errors) {
        switch (predicate) {
            case null:
                break;
            case ActionPredicate.Now now when !Enum.IsDefined(value: now.Fact):
                errors.Add(item: $"{path}.fact '{now.Fact}' is not a defined ActionFact.");
                break;
            case ActionPredicate.Now:
                break;
            case ActionPredicate.Recently recently:
                if (!Enum.IsDefined(value: recently.Fact)) {
                    errors.Add(item: $"{path}.fact '{recently.Fact}' is not a defined ActionFact.");
                }

                if (!float.IsFinite(f: recently.WindowSeconds) || (recently.WindowSeconds <= 0f)) {
                    errors.Add(item: $"{path}.windowSeconds must be finite and greater than 0.");
                }

                break;
            case ActionPredicate.CooldownElapsed:
                break;
            case ActionPredicate.UsesBelow uses when (uses.Limit < 1):
                errors.Add(item: $"{path}.limit {uses.Limit} must be at least 1.");
                break;
            case ActionPredicate.UsesBelow:
                break;
            case ActionPredicate.All all:
                if (all.Predicates is not { Count: > 0 } inner) {
                    errors.Add(item: $"{path}.all must contain at least one predicate.");

                    break;
                }

                for (var index = 0; (index < inner.Count); index++) {
                    ValidatePredicate(predicate: inner[index], path: $"{path}.all[{index}]", errors: errors);
                }

                break;
            default:
                errors.Add(item: $"{path} is an unknown predicate kind.");
                break;
        }
    }

    private static void ValidateEffect(ActionEffect effect, string path, List<string> errors) {
        switch (effect) {
            case null:
                errors.Add(item: $"{path} is required.");
                break;
            case ActionEffect.SetVerticalVelocity set:
                RequireFinite(value: set.Velocity, name: $"{path}.velocity", errors: errors);
                break;
            case ActionEffect.ScaleVerticalVelocity scale:
                RequireFinite(value: scale.Factor, name: $"{path}.factor", errors: errors);
                break;
            case ActionEffect.PlanarImpulse impulse:
                RequireFinite(value: impulse.Speed, name: $"{path}.speed", errors: errors);
                RequireNonNegative(value: impulse.DurationSeconds, name: $"{path}.durationSeconds", errors: errors);

                if (!IsFinite(value: impulse.BodyDirection) || (impulse.BodyDirection.LengthSquared() <= MinimumBasisLengthSquared)) {
                    errors.Add(item: $"{path}.bodyDirection must be finite and non-zero.");
                }

                break;
            case ActionEffect.StartCooldown cooldown:
                RequireNonNegative(value: cooldown.Seconds, name: $"{path}.seconds", errors: errors);
                break;
            case ActionEffect.ConsumeUse:
                break;
            default:
                errors.Add(item: $"{path} is an unknown effect kind.");
                break;
        }
    }

    // The one screen-source gate, shared by a declared source and every magazine entry — a pure extraction that closes a
    // real duplication risk (a magazine entry could otherwise name an undeclared camera). Returns whether the source is a
    // live CONSOLE (the caller counts these against the one-live ceiling).
    private static bool ValidateScreenSource(WorldScreenSource source, string path, HashSet<string> cameras, List<string> errors) {
        switch (source) {
            case null:
                errors.Add(item: $"{path} is required.");

                return false;
            case WorldScreenSource.Machine machine:
                if (string.IsNullOrWhiteSpace(value: machine.Engine)) {
                    errors.Add(item: $"{path}.machine.engine is required.");
                }

                // An empty contentPath is a valid "unconfigured" screen; the binder faults the slot gracefully at boot.
                // A present-but-missing file is a runtime fact, not a structural authoring error.
                return false;
            case WorldScreenSource.TestPattern pattern:
                if ((pattern.Width <= 0) || (pattern.Height <= 0) ||
                    (pattern.Width > MaxSurfaceDimension) || (pattern.Height > MaxSurfaceDimension)) {
                    errors.Add(item: $"{path} test-pattern dimensions must be within 1..{MaxSurfaceDimension}.");
                }

                return false;
            case WorldScreenSource.Camera camera:
                ValidateProfile(profile: camera.Profile, path: $"{path}.camera", errors: errors);

                return false;
            case WorldScreenSource.Capture capture:
                // Selector: monitor mode validates the index; window mode requires a title (its unused counterpart).
                if (capture.MonitorIndex is { } monitorIndex) {
                    if (monitorIndex < 0) {
                        errors.Add(item: $"{path}.capture.monitorIndex must be non-negative.");
                    }
                } else if (string.IsNullOrWhiteSpace(value: capture.WindowTitle)) {
                    errors.Add(item: $"{path}.capture.windowTitle is required.");
                }

                ValidateProfile(profile: capture.Profile, path: $"{path}.capture", errors: errors);

                return false;
            case WorldScreenSource.View view:
                if (!cameras.Contains(item: view.CameraName)) {
                    errors.Add(item: $"{path}.view references undeclared camera '{view.CameraName}'.");
                }

                return false;
            case WorldScreenSource.Console console:
                if ((console.Rows < 1) || (console.Rows > 120)) {
                    errors.Add(item: $"{path}.console.rows {console.Rows} is outside 1..120.");
                }

                if ((console.Columns < 1) || (console.Columns > 400)) {
                    errors.Add(item: $"{path}.console.columns {console.Columns} is outside 1..400.");
                }

                return true;
            default:
                return false;
        }
    }

    // The engage-route policy: a finite non-negative radius (today unvalidated, and a real gap — a NaN reaches
    // MathF.Sqrt in the engage handler), plus the authored world-event channel names (kebab-case, non-empty; a channel
    // on a non-engageable route is an authoring mistake, not a configuration).
    private static void ValidateRoute(WorldScreenRoute route, string path, List<string> errors) {
        if (!float.IsFinite(f: route.EngageRadius) || (route.EngageRadius < 0f)) {
            errors.Add(item: $"{path}.engageRadius {route.EngageRadius} must be finite and non-negative.");
        }

        ValidateChannel(channel: route.EngageChannel, name: $"{path}.engageChannel", errors: errors);
        ValidateChannel(channel: route.CycleChannel, name: $"{path}.cycleChannel", errors: errors);

        if (!route.Engageable && ((route.EngageChannel is not null) || (route.CycleChannel is not null))) {
            errors.Add(item: $"{path} names an engageChannel/cycleChannel but engageable is false — a screen cannot answer a gesture it can never be engaged from.");
        }
    }

    // A world-event channel name, when present: non-empty kebab-case (lowercase, digits, single hyphens).
    private static void ValidateChannel(string? channel, string name, List<string> errors) {
        if ((channel is not null) && !IsKebabCase(value: channel)) {
            errors.Add(item: $"{name} '{channel}' must be non-empty kebab-case.");
        }
    }

    // The per-screen magazine: at least one entry, a selected index in range, and each entry crossing the SAME source
    // gate as a declared source.
    private static void ValidateMagazine(WorldScreenMagazine? magazine, string path, HashSet<string> cameras, List<string> errors) {
        if (magazine is not { } value) {
            return;
        }

        if ((value.Entries is null) || (value.Entries.Count == 0)) {
            errors.Add(item: $"{path}.entries requires at least one entry.");

            return;
        }

        if ((value.Selected < 0) || (value.Selected >= value.Entries.Count)) {
            errors.Add(item: $"{path}.selected {value.Selected} is outside 0..{(value.Entries.Count - 1)}.");
        }

        for (var index = 0; (index < value.Entries.Count); index++) {
            _ = ValidateScreenSource(source: value.Entries[index], path: $"{path}.entries[{index}]", cameras: cameras, errors: errors);
        }
    }

    // The cable links: name required/kebab/unique; two or more screens; every index declared; no duplicate within a link;
    // no screen in two links. NOT validated: engine identity of the members — that is a RUNTIME fact (a screen.insert
    // changes it), so the binder reports a dormant link with a reason rather than the validator rejecting the row.
    private static void ValidateLinks(IReadOnlyList<WorldScreenLink>? links, HashSet<int> screenIndices, List<string> errors) {
        if (links is null) {
            return;
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var claimed = new HashSet<int>();

        for (var index = 0; (index < links.Count); index++) {
            var link = links[index];
            var path = $"links[{index}]";

            if (link is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: link.Name) || !IsKebabCase(value: link.Name)) {
                errors.Add(item: $"{path}.name '{link.Name}' must be non-empty kebab-case.");
            } else if (!names.Add(item: link.Name)) {
                errors.Add(item: $"{path}.name '{link.Name}' is duplicated.");
            }

            if ((link.Screens is null) || (link.Screens.Count < 2)) {
                errors.Add(item: $"{path}.screens requires two or more screen indices.");

                continue;
            }

            var withinLink = new HashSet<int>();

            foreach (var screen in link.Screens) {
                if (!screenIndices.Contains(item: screen)) {
                    errors.Add(item: $"{path}.screens names undeclared screen {screen}.");
                } else if (!withinLink.Add(item: screen)) {
                    errors.Add(item: $"{path}.screens names screen {screen} twice.");
                } else if (!claimed.Add(item: screen)) {
                    errors.Add(item: $"{path}.screens: screen {screen} is already in another link.");
                }
            }
        }
    }

    // A non-empty kebab-case token: lowercase ASCII letters/digits, single hyphens between them, no leading/trailing
    // hyphen. The channel/link-name grammar.
    private static bool IsKebabCase(string value) {
        if (string.IsNullOrEmpty(value: value) || (value[index: 0] == '-') || (value[index: (value.Length - 1)] == '-')) {
            return false;
        }

        var previousHyphen = false;

        foreach (var character in value) {
            var isLower = ((character >= 'a') && (character <= 'z'));
            var isDigit = ((character >= '0') && (character <= '9'));

            if (character == '-') {
                if (previousHyphen) {
                    return false;
                }

                previousHyphen = true;
            } else if (isLower || isDigit) {
                previousHyphen = false;
            } else {
                return false;
            }
        }

        return true;
    }

    private static void ValidateProfile(WorldFeedProfile profile, string path, List<string> errors) {
        if ((profile.Width <= 0) || (profile.Height <= 0) ||
            (profile.Width > MaxSurfaceDimension) || (profile.Height > MaxSurfaceDimension)) {
            errors.Add(item: $"{path} dimensions must be within 1..{MaxSurfaceDimension}.");
        }

        try {
            _ = EngineTicks.PerRate(ratePerSecond: profile.RefreshRateHz);
        } catch (ArgumentException exception) {
            errors.Add(item: $"{path}.refreshRateHz is invalid: {exception.Message}");
        }
    }

    // A kit's body VOLUME (SIM-AFFECTING): radius positive, height finite and at least twice the radius (a capsule
    // shorter than its diameter is a sphere; the validator names the fix rather than silently clamping).
    private static void ValidateCollider(WorldCollider? collider, string path, List<string> errors) {
        if (collider is not { } value) {
            return;
        }

        RequirePositive(value: value.Radius, name: $"{path}.radius", errors: errors);
        RequireFinite(value: value.Height, name: $"{path}.height", errors: errors);

        if (float.IsFinite(f: value.Height) && float.IsFinite(f: value.Radius) && (value.Height < (2f * value.Radius))) {
            errors.Add(item: $"{path}.height {value.Height} is a capsule shorter than its diameter ({2f * value.Radius}) — raise height or lower radius.");
        }
    }

    // The per-axis effective-extent check for a box solidity facet: a margin that inverts any axis (halfExtent + margin
    // <= 0) is rejected by name, not turned into a negative-extent collider.
    private static void RequirePositiveEffectiveExtent(Vector3 halfExtents, float margin, string path, List<string> errors) {
        if (!float.IsFinite(f: margin)) {
            return;
        }

        if (((halfExtents.X + margin) <= 0f) || ((halfExtents.Y + margin) <= 0f) || ((halfExtents.Z + margin) <= 0f)) {
            errors.Add(item: $"{path} {margin} inverts the collider (halfExtent + margin must be > 0 on every axis).");
        }
    }

    private static void RequireFinite(float value, string name, List<string> errors) {
        if (!float.IsFinite(f: value)) {
            errors.Add(item: $"{name} must be finite.");
        }
    }

    private static void RequirePositive(float value, string name, List<string> errors) {
        if (!float.IsFinite(f: value) || (value <= 0f)) {
            errors.Add(item: $"{name} must be finite and positive.");
        }
    }

    private static void RequireNonNegative(float value, string name, List<string> errors) {
        if (!float.IsFinite(f: value) || (value < 0f)) {
            errors.Add(item: $"{name} must be finite and non-negative.");
        }
    }

    private static void RequireIntRange(int value, int min, int max, string name, List<string> errors) {
        if ((value < min) || (value > max)) {
            errors.Add(item: $"{name} {value} is outside {min}..{max}.");
        }
    }

    private static bool IsFinite(Vector3 value) => (float.IsFinite(f: value.X) && float.IsFinite(f: value.Y) && float.IsFinite(f: value.Z));
}
