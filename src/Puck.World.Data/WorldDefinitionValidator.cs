using System.Numerics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Presentation;
using Puck.Carriage;
using Puck.Commands;
using Puck.Abstractions.Documents;
using Puck.Forge.Authoring;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Protocol;
using Puck.World.Qr;

namespace Puck.World;

/// <summary>The one thick startup gate for World-local authored data. Runtime systems may assume these structural and
/// platform-facing invariants instead of silently overwriting duplicate indices or discovering bad extents on a GPU
/// allocation path.</summary>
/// <remarks>
/// <para><b>Field taxonomy — the checklist a future field lands against.</b> A definition field is one of two kinds,
/// and that kind decides its numeric contract:</para>
/// <para><b>Sim-affecting</b> — quantized to fixed point once at compile via the <c>Fixed*</c> compilers (exactly the
/// <see cref="FixedWorldKit"/> / <see cref="FixedMotionTuning"/> pattern) and then deterministic: the profileless
/// <see cref="WorldDefinition.Motion"/> tuning, every kit's tuning/producer/action rows, the named
/// <see cref="WorldDefinition.SpawnPoints"/>, the
/// <see cref="WorldDefinition.Population"/> census, and the <see cref="WorldDefinition.Assignment"/> policy. These are
/// gated here for finiteness and physical sign (speeds/gravities/radii positive; durations non-negative) so a
/// bad authored value cannot poison the quantized sim.</para>
/// <para><b>Presentation-only</b> — stays float, never enters sim state: the scene albedos, the camera FOV/extent, the
/// screen geometry/sources, and the render-lever defaults. These are gated only for structural GPU safety (finite
/// frames, bounded extents, non-degenerate bases).</para>
/// </remarks>
public static class WorldDefinitionValidator {
    // World-local CPU/GPU screen sources are intentionally presentation-sized. A bad authored extent must fail here,
    // before it can become an unchecked pixel-buffer or offscreen-render allocation. Public: it is also the structural
    public const int MaxSurfaceDimension = 4096;
    // A look scale feeds the stamp pool's per-instance bound radius; an unbounded one is a GPU-SAFETY issue (a
    // spatial-cull metadata blow-up), not a taste one, so it carries a hard ceiling beside MaxSurfaceDimension.
    private const float MaxLookScale = 16f;
    private const float MinimumBasisLengthSquared = 1e-8f;
    // PlanarImpulse.BodyDirection quantizes to FixedQ4816 (step 2^-16) before reaching the sim; that rounds a unit
    // vector's length by at most ~1.3e-5. This tolerance sits ~8x above that quantization floor and far below any
    // unnormalized axis (e.g. (3, 0, 4) is off by 4).
    private const float PlanarImpulseUnitDirectionTolerance = 1e-4f;
    // Each camera can carry a persistent offscreen render (a View screen samples it); the bound keeps a floody document
    // from declaring thousands of budgeted offscreen engines (ViewStack.MaxRegisteredViews is the runtime floor).
    private const int MaxCameras = 64;

    /// <summary>Validates a candidate definition, including its cross-document adjacency claims, without throwing. On
    /// failure, <paramref name="reason"/> carries the collapsed one-line error list.</summary>
    /// <param name="definition">The candidate definition.</param>
    /// <param name="reason">The collapsed failure reason, or empty on success.</param>
    /// <param name="neighbours">The injected neighbour resolver a cross-document adjacency proof reads (see
    /// <see cref="Validate"/>). Required — not defaulted — so every call site states whether it can resolve a
    /// neighbour rather than silently opting out; <see langword="null"/> remains a legitimate, explicit answer for a
    /// call site that must refuse an unprovable claim. It is consulted only when a document authors a
    /// <see cref="WorldDefinition.Adjacencies"/>.</param>
    /// <returns><see langword="true"/> when the candidate is valid.</returns>
    public static bool TryValidate(WorldDefinition definition, out string reason, IWorldNeighbourResolver? neighbours) {
        try {
            ValidateCore(definition: definition, neighbours: neighbours, validateAdjacencyClaims: true);
            reason = string.Empty;

            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }

    /// <summary>Validates every fact owned by one document without re-reading neighbour documents. This is the
    /// apply-time gate for a candidate whose cross-document adjacency claims were already proved at its load boundary;
    /// callers must separately refuse edits that change an existing claim or any input to its derived floor.</summary>
    /// <param name="definition">The candidate definition.</param>
    /// <param name="reason">The collapsed failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the document-local facts are valid.</returns>
    public static bool TryValidateLocally(WorldDefinition definition, out string reason) {
        try {
            ValidateCore(definition: definition, neighbours: null, validateAdjacencyClaims: false);
            reason = string.Empty;

            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }

    /// <summary>Validates a candidate definition, throwing <see cref="InvalidOperationException"/> naming every
    /// collected failure when it is not sound.</summary>
    /// <param name="definition">The candidate definition.</param>
    /// <param name="neighbours">The injected seam an adjacency uses to resolve its counterpart document. The
    /// implementation must obtain an authority-delivered document,
    /// not assume that the neighbour shares a host, process, or filesystem (see <see cref="IWorldNeighbourResolver"/>).
    /// Required — not defaulted — so a caller states its answer rather than silently declining to decide; pass
    /// <see langword="null"/> explicitly for a call site with no reachable resolver (see <see cref="TryValidate"/>).
    /// A document authoring no adjacency is unaffected either way. A document that does author one, validated with
    /// a null (or unreachable) resolver, is refused by name.</param>
    public static void Validate(WorldDefinition definition, IWorldNeighbourResolver? neighbours) {
        ValidateCore(definition: definition, neighbours: neighbours, validateAdjacencyClaims: true);
    }

    private static void ValidateCore(WorldDefinition definition, IWorldNeighbourResolver? neighbours, bool validateAdjacencyClaims) {
        ArgumentNullException.ThrowIfNull(definition);

        RequireSections(definition: definition);

        var errors = new List<string>();

        if (!string.Equals(a: definition.Schema, b: WorldDefinition.SchemaVersion, comparisonType: StringComparison.Ordinal)) {
            errors.Add(item: $"schema '{definition.Schema ?? "(absent)"}' is not '{WorldDefinition.SchemaVersion}'.");
        }

        ValidateExtensions(extensions: definition.Extensions, errors: errors);

        // Validated FIRST among the sim-affecting sections: definition.SimulationRateHz (read by
        // PopulationReconnectGraceTicks and CompiledInputHold below, and by every downstream Puck.World.Server/
        // Puck.Launcher consumer) must be a sane rate before anything derived from it is worth checking.
        ValidateSimulation(simulation: definition.Simulation, errors: errors);

        // The profileless fallback tuning is SIM-AFFECTING and compiles to fixed point.
        ValidateMotionDefaults(motion: definition.Motion, path: "motion", errors: errors);
        ValidatePlayerDefaults(defaults: definition.PlayerDefaults, errors: errors);

        ValidateSeatActivation(seatActivation: definition.Population.SeatActivation, errors: errors);

        if ((definition.Population.Capacity < WorldPopulationLimits.LocalSeatCount) || (definition.Population.Capacity > WorldPopulationLimits.CapacityCeiling)) {
            errors.Add(item: $"population.capacity {definition.Population.Capacity} is outside {WorldPopulationLimits.LocalSeatCount}..{WorldPopulationLimits.CapacityCeiling}.");
        }

        if (definition.Population.CapacityDraw is { } capacityDraw) {
            // The census coherence rule: the admissible domain is not the ceiling alone. networkPlayers is checked
            // against capacity MINUS the local seats, so a drawn capacity below that sum is a document THIS SAME
            // validator refuses once the draw resolves — the roll would decide whether the world boots. Narrow the
            // domain statically instead.
            var censusFloor = (WorldPopulationLimits.LocalSeatCount + definition.Population.NetworkPlayers);

            // The draw domain floors at the census the document already owes room for (seats plus declared network
            // players) — the same coherence rule an authored literal capacity answers to. The sub-ceiling crash that
            // once forced a floor at the ceiling itself was fixed at its root (the avatar catalog now scans the live
            // population's own capacity), so a drawn capacity below 128 is an ordinary authored value.
            var drawFloor = censusFloor;

            ValidateDrawSite(
                draw: capacityDraw,
                generators: definition.Generators,
                targetKind: CellKind.Int,
                bootOnly: true,
                domainLow: drawFloor,
                domainHigh: WorldPopulationLimits.CapacityCeiling,
                path: "population.capacityDraw",
                errors: errors
            );
        }

        var peerCapacity = Math.Max(val1: 0, val2: (definition.Population.Capacity - WorldPopulationLimits.LocalSeatCount));
        if ((definition.Population.NetworkPlayers < 0) || (definition.Population.NetworkPlayers > peerCapacity)) {
            errors.Add(item: $"population.networkPlayers {definition.Population.NetworkPlayers} is outside 0..{peerCapacity} for the authored capacity.");
        }

        // 0 disables the reconnect grace window (immediate teardown); the 600s ceiling mirrors world.wait's own
        // ten-minute bound. Validated in authored seconds, not derived ticks — at rate 0 a positive value means
        // NEVER, not a tick count (see CompiledTickDuration).
        if (!float.IsFinite(f: definition.Population.ReconnectGraceSeconds) || (definition.Population.ReconnectGraceSeconds < 0f) || (definition.Population.ReconnectGraceSeconds > 600f)) {
            errors.Add(item: $"population.reconnectGraceSeconds {definition.Population.ReconnectGraceSeconds} is outside 0..600.");
        }

        // The audio asset sections come FIRST among the row sets: emission facets on scene rows/placements and the
        // speaker rows below all resolve against the tune/patch id sets.
        var tuneIds = ValidateAssets(rows: definition.Tunes, section: "tunes", id: static tune => tune.Id, hash: static tune => tune.Hash, check: CheckTune, errors: errors);
        var patchIds = ValidateAssets(rows: definition.Patches, section: "patches", id: static patch => patch.Id, hash: static patch => patch.Hash, check: CheckPatch, errors: errors);

        var spawnPointIds = ValidateSpawnPoints(spawnPoints: definition.SpawnPoints, errors: errors);

        ValidateSeatSpawns(seatSpawns: definition.Population.SeatSpawns, spawnPointIds: spawnPointIds, errors: errors);
        // Validated in the AUTHORED seconds, not definition.CompiledInputHold: every compiled *Ticks field
        // collapses to 0 at simulation.rateHz 0 (a legal, non-stepping world), so a ticks-domain check would refuse
        // an ordinary input-hold section on such a world. The rate is threaded through so ValidateInputHold can also
        // prove the authored value actually compiles (see its own remarks).
        ValidateInputHold(settings: definition.InputHold, ratePerSecond: (uint)definition.SimulationRateHz, populationCapacity: definition.Population.Capacity, errors: errors);

        // Population distributions run after spawn points so an explicit-points region can resolve every name.
        ValidateDistribution(distribution: definition.Population.Distribution, path: "population.distribution", spawnPointIds: spawnPointIds, allowDisc: true, allowPoints: true, allowLattice: false, allowZeroDisc: false, errors: errors);
        ValidatePopulationVariation(variation: definition.Population.PeerVariation, path: "population.peerVariation", minIndex: WorldPopulationLimits.LocalSeatCount, errors: errors);
        ValidatePopulationVariation(variation: definition.Population.SeatVariation, path: "population.seatVariation", minIndex: 0, errors: errors);
        ValidateSequence(sequence: definition.Population.PeerColors, path: "population.peerColors", minIndex: WorldPopulationLimits.LocalSeatCount, errors: errors, WorldSequence.Additive, WorldSequence.R1);

        // State validates BEFORE Kits and Hud: the rows-by-name map it returns is what a kit's own `generate` effect
        // resolves against (a kit naming an undeclared generator or destination row refuses HERE, at load, rather
        // than at first fire) and what a state.<row>/state.<row>.<key> HUD binding resolves against —
        // refuse-unknown-by-name, the same discipline every other HudBindingVocabulary token gets.
        var stateRows = ValidateState(rows: definition.State, generators: definition.Generators, errors: errors);
        var targetRegisterNames = ValidateTargetRegisters(registers: definition.TargetRegisters, errors: errors);
        var programs = ValidateBodyMotionPrograms(programs: definition.BodyMotionPrograms, targetRegisterNames: targetRegisterNames, errors: errors);
        var (allChannelNames, compositionChannelNames) = ValidateChannels(definition: definition, programs: programs, errors: errors);
        if ((definition.Channels.Count + definition.TargetRegisters.Count) > ChannelLimits.MaxChannels) {
            errors.Add(item: $"channels and targetRegisters declare {definition.Channels.Count + definition.TargetRegisters.Count} shared Drive-reach ordinals; the maximum is {ChannelLimits.MaxChannels}.");
        }
        var kitNames = ValidateKits(definition: definition, programs: programs, allChannelNames: allChannelNames, compositionChannelNames: compositionChannelNames, stateRows: stateRows, errors: errors);

        ValidateAssignment(assignment: definition.Assignment, kitNames: kitNames, errors: errors);
        ValidateDefaultPeerSource(definition: definition, errors: errors);
        var addonNames = ValidateAddons(addons: definition.Addons, populationCapacity: definition.Population.Capacity, errors: errors);
        ValidateBindingOverlays(overlays: definition.BindingOverlays, channels: CompilableChannelTable(channels: definition.Channels), errors: errors);
        ValidateStorage(storage: definition.Storage, errors: errors);

        // When definition.Identity is not null (an owned world), WorldIdentity.Hud reads Panels.FirstOrDefault() as
        // the player-scope seat panel: its elements are capped at the tighter WorldHudCapacity.MaxElementsPerSeatPanel
        // and WorldHudLayer.Replace is refused (a panel confined to one seat's viewport has no base slot to take
        // over). A plain world document (Identity null) keeps the world-scope MaxElementsPerPanel/Replace-allowed
        // rules.
        ValidateHud(hud: definition.Hud, stateRows: stateRows, isIdentityScope: (definition.Identity is not null), errors: errors);
        ValidateIdentityMotionState(identity: definition.Identity, stateRows: stateRows, errors: errors);

        // The declared SOURCES validate before anything resolves a reference against them, so a site's refusal is
        // "no such source" rather than a second reading of a source this pass would have refused anyway.
        ValidateGenerators(generators: definition.Generators, errors: errors);

        // Rules validate LAST among the row sets: WorldRuleCompiler resolves every name a rule can spell against the
        // whole candidate (state rows, generator rows, placement regions), so it runs once those are known good. The
        // compiler is the ONE authority on what a rule may say — this pass calls it rather than restating its rules,
        // and turns its by-name refusal into an aggregated error line.
        ValidateRules(rules: definition.Rules, definition: definition, errors: errors);

        // Properties/Interactions validate right after Rules: the property registry's own shape check (each name
        // backed by a declared keyed int state row) needs only stateRows (already built above), and Interactions
        // compiles through the SAME WorldRuleCompiler path Rules just did, over the whole candidate.
        ValidateProperties(properties: definition.Properties, stateRows: stateRows, errors: errors);
        ValidateInteractions(interactions: definition.Interactions, definition: definition, errors: errors);

        // Called early — the host section references no other section.
        ValidateHost(host: definition.Host, generators: definition.Generators, errors: errors);

        ValidateWater(water: definition.Water, errors: errors);

        // The local auction house: null IS today's no-market world, matching Water's own posture. Needs only
        // stateRows (already built above) to resolve a listing's item/currency rows.
        ValidateMarket(market: definition.Market, stateRows: stateRows, errors: errors);

        // Groups validates HERE — ahead of its original position ahead of Grants below — because a destination row's
        // scope=group selector (docs/world-model.md Campaign 1 item 3) resolves against the SAME declared group-id set
        // Grants already needed: both are threaded from this ONE call rather than validating groups twice. Only
        // stateRows (already built above) is a dependency, so the move changes no other ordering.
        var groupIds = ValidateGroups(groups: definition.Groups, stateRows: stateRows, errors: errors);

        // A destination row's own `reference` resolves against this set — captured here rather than re-walked, the
        // same forward-threading creationIds/lookNames already ride.
        var referenceNames = ValidateReferences(references: definition.References, errors: errors);

        ValidatePortals(portals: definition.Portals, errors: errors);

        // A portal facet's destination resolves against THIS set (below, inside ValidatePlacements), never against
        // referenceNames directly — the destinations section is the layer a portal facet now names, exactly the
        // model docs/world-model.md "Reference, destination and session are different facts" describes. A scope=group
        // row's selector resolves against groupIds (see the move above).
        var destinationNames = ValidateDestinations(destinations: definition.Destinations, references: definition.References, referenceNames: referenceNames, groupIds: groupIds, errors: errors);

        ValidateAdjacencies(definition: definition, destinationNames: destinationNames, neighbours: (validateAdjacencyClaims ? neighbours : null), proveNeighbours: validateAdjacencyClaims, errors: errors);

        var authoring = definition.Authoring;

        ValidateAuthoring(authoring: authoring, errors: errors);

        var collision = definition.Collision;

        ValidateCollision(collision: collision, errors: errors);

        var creationIds = ValidateCreations(creations: definition.Creations, errors: errors);

        // The LOOK rows go AFTER ValidateCreations (a creation look resolves its CreationId against the id-set that
        // returns) and BEFORE ValidatePlacements (a future Inhabit facet will resolve its Look against the look-name set
        // this returns) — the same forward-threading creationIds already rides.
        var lookNames = ValidateLooks(looks: definition.Looks, creationIds: creationIds, errors: errors);

        ValidateLookAssignment(assignment: definition.LookAssignment, lookNames: lookNames, errors: errors);

        var placementIds = ValidatePlacements(placements: definition.Placements, definition: definition, creationIds: creationIds, lookNames: lookNames, kitNames: kitNames, authoring: authoring, patchIds: patchIds, requiresField: WorldContactSelection.RequiresField(collision: collision), destinationNames: destinationNames, errors: errors);

        // Document-wide, independent of any single placement's own row checks above — see its own remarks.
        ValidateSessionWindowBudget(placements: definition.Placements, errors: errors);

        var cameras = new HashSet<string>(comparer: StringComparer.Ordinal);

        {
            var authoredCameras = definition.Cameras;

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

                // A null anchor resolves the world reference frame.
                if (camera.Anchor is { } anchor) {
                    ValidateAnchor(anchor: anchor, placements: definition.Placements, placementIds: placementIds, creations: definition.Creations, populationCapacity: definition.Population.Capacity, path: $"{path}.anchor", errors: errors);
                }

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
        ValidateViews(views: definition.Views, cameras: cameras, errors: errors);

        var screenIndices = new HashSet<int>();
        // The declared-live console sources (screens[*].source, NOT magazine entries): the feed owns ONE upload surface,
        // so at most one may be live at a time. A console entry sitting unselected in a magazine is legal.
        var consoleLiveIndices = new List<int>();
        // The derived-face slots the binder reserves up front (Program.cs concatenates them after the document screens):
        // a document screen at one of these indices would silently collide with the reserved placeholder in the binder's
        // dict-fill, so the range is carved out of the authored screen-index space here. The membership test itself is
        // WorldCreationFacets.IsReservedFaceIndex — shared with the authoring-headroom scan, which excludes the same
        // band; these two are the only rules that hand out a screen index, and they must exclude ONE set.
        var reservedFaceStart = WorldPlacementPolicy.DerivedFaceBase;
        var reservedFaceEnd = (reservedFaceStart + authoring.DerivedFaceScreens);

        {
            var screens = definition.Screens;

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
                } else if (WorldPlacementPolicy.IsReservedFaceIndex(index: screen.Index, derivedFaceScreens: authoring.DerivedFaceScreens)) {
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
                if (ValidateScreenSource(source: screen.Source, path: $"{path}.source", cameras: cameras, destinationNames: destinationNames, errors: errors)) {
                    consoleLiveIndices.Add(item: screen.Index);
                }

                ValidateRoute(route: screen.Route, path: $"{path}.route", channelNames: allChannelNames, errors: errors);
                ValidateMagazine(magazine: screen.Magazine, path: $"{path}.magazine", cameras: cameras, destinationNames: destinationNames, errors: errors);

                // The screen's solidity facet — a box collider from the slab's frame + margin. The effective
                // per-axis extent must stay positive (a margin that inverts the box is rejected by name).
                if (screen.Solid is { } screenSolid) {
                    RequireFinite(value: screenSolid.Margin, name: $"{path}.solid.margin", errors: errors);
                    RequirePositiveEffectiveExtent(halfExtents: new Vector3(x: screen.HalfWidth, y: screen.HalfHeight, z: screen.HalfDepth), margin: screenSolid.Margin, path: $"{path}.solid.margin", errors: errors);
                }
            }

            var availableHeadroom = (SdfProgramBuilder.MaxScreenSurfaces - authoring.DerivedFaceScreens - screenIndices.Count);

            if (authoring.AuthoringHeadroomScreens > availableHeadroom) {
                errors.Add(item: $"authoring.authoringHeadroomScreens asks for {authoring.AuthoringHeadroomScreens} slot(s), but only {Math.Max(val1: 0, val2: availableHeadroom)} remain after {screenIndices.Count} authored screen(s) and {authoring.DerivedFaceScreens} derived-face reservation(s).");
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

        ValidateAudioDefaults(audio: definition.Audio, cameras: cameras, patchIds: patchIds, speakerNames: speakerNames, errors: errors);

        // Groups validates before Grants: a grant row may target a group principal, so Grants needs the declared
        // group-id set already resolved.
        ValidateGrants(grants: definition.Grants, addonNames: addonNames, groupIds: groupIds, populationCapacity: definition.Population.Capacity, errors: errors);

        // Admission validates last of all: it references nothing else in the document except the population
        // capacity its templates' body subjects bound against (already in scope), and it is the newest section,
        // appended over the already-shipped set for the same reason Grants/Links are.
        ValidateAdmission(entries: definition.Admission, populationCapacity: definition.Population.Capacity, errors: errors);

        if (errors.Count > 0) {
            throw new InvalidOperationException(message: $"Invalid WorldDefinition:{Environment.NewLine} - {string.Join(separator: $"{Environment.NewLine} - ", values: errors)}");
        }
    }

    // Every section the canonical writer emits is REQUIRED. A document missing one is incomplete, and an incomplete
    // document is rejected by name rather than silently absorbing a default for a section its author never wrote — the
    // loud fallback WorldDefinitionLoader promises. Source generation leaves an absent JSON property null even where
    // the type declares the member non-nullable, so absence arrives here as null; every pass below dereferences these,
    // so the gate throws on its own rather than joining the error list.
    private static void RequireSections(WorldDefinition definition) {
        var missing = new List<string>();

        Require(section: definition.SpawnPoints, name: "spawnPoints", missing: missing);
        Require(section: definition.Render, name: "render", missing: missing);
        Require(section: definition.Screens, name: "screens", missing: missing);
        Require(section: definition.Cameras, name: "cameras", missing: missing);
        Require(section: definition.PlayerDefaults, name: "playerDefaults", missing: missing);
        Require(section: definition.Channels, name: "channels", missing: missing);
        Require(section: definition.TargetRegisters, name: "targetRegisters", missing: missing);
        Require(section: definition.BodyMotionPrograms, name: "bodyMotionPrograms", missing: missing);
        Require(section: definition.Kits, name: "kits", missing: missing);
        Require(section: definition.DefaultSeatKit, name: "defaultSeatKit", missing: missing);
        Require(section: definition.Assignment, name: "assignment", missing: missing);
        Require(section: definition.Addons, name: "addons", missing: missing);
        Require(section: definition.BindingOverlays, name: "bindingOverlays", missing: missing);
        Require(section: definition.Storage, name: "storage", missing: missing);
        Require(section: definition.Creations, name: "creations", missing: missing);
        Require(section: definition.Placements, name: "placements", missing: missing);
        Require(section: definition.Authoring, name: "authoring", missing: missing);
        Require(section: definition.Speakers, name: "speakers", missing: missing);
        Require(section: definition.Tunes, name: "tunes", missing: missing);
        Require(section: definition.Patches, name: "patches", missing: missing);
        Require(section: definition.Audio, name: "audio", missing: missing);
        Require(section: definition.Collision, name: "collision", missing: missing);
        Require(section: definition.Host, name: "host", missing: missing);
        Require(section: definition.Views, name: "views", missing: missing);
        Require(section: definition.Looks, name: "looks", missing: missing);
        Require(section: definition.LookAssignment, name: "lookAssignment", missing: missing);
        Require(section: definition.Links, name: "links", missing: missing);
        Require(section: definition.Grants, name: "grants", missing: missing);
        Require(section: definition.Hud, name: "hud", missing: missing);
        Require(section: definition.State, name: "state", missing: missing);
        // InputHold is now a reference-typed (WorldInputHoldAuthoring) section like every other row here — checked
        // on the section itself, not (as when it was a struct with no honest "absent" state) on its Participants field.
        Require(section: definition.InputHold, name: "inputHold", missing: missing);

        if (missing.Count > 0) {
            throw new InvalidOperationException(message: $"Incomplete WorldDefinition:{Environment.NewLine} - {string.Join(separator: $"{Environment.NewLine} - ", values: missing)}");
        }
    }

    private static void Require(object? section, string name, List<string> missing) {
        if (section is null) {
            missing.Add(item: $"{name} is required.");
        }
    }

    // Unknown top-level members were captured into the root's [JsonExtensionData] rather than rejected. This document
    // validates them through the shared DocumentExtensionsPolicy regime: a reserved-prefix key ('$' schema-like keys,
    // '_' comments) is an intentional escape hatch; anything else at the top level is an authoring mistake (most
    // often a mis-cased or mistyped section name), reported here rather than silently absorbed.
    private static void ValidateExtensions(IDictionary<string, JsonElement>? extensions, List<string> errors) {
        DocumentExtensionsPolicy.ValidateKeys(extensions: extensions, report: key => errors.Add(item: $"unknown top-level member '{key}'; top-level keys are case-sensitive camelCase."));
    }

    // An authored simulation rate must be exactly 0 (resident, non-stepping — see WorldSimulationDefaults.RateHz) or
    // a positive divisor of the fixed 50400 engine-tick base (FixedTickConversion.TicksPerSecond; duplicated here
    // because Puck.World.Data cannot reference Puck.Hosting, where EngineTicks lives), so EngineTicks.PerRate always
    // derives a whole engine-tick step width. A negative rate is refused outright; the divisor check applies only to
    // a positive rate (0 divides nothing).
    private static void ValidateSimulation(WorldSimulationDefaults? simulation, List<string> errors) {
        if (simulation is null) {
            // Unauthored — WorldDefinition.SimulationRateHz falls back to WorldSimulationDefaults.DefaultRateHz
            // (240), a divisor of 50400 by construction. Nothing to check.
            return;
        }

        var rate = simulation.RateHz;

        if (rate < 0) {
            errors.Add(item: $"simulation.rateHz {rate} must not be negative.");

            return;
        }

        if (rate == 0) {
            // The resident, non-stepping rate — a legal, distinct value (WorldSimulationDefaults.RateHz's own
            // remarks), never refused. No divisor check applies: there is no engine-tick step width to derive when
            // the world never steps at all.
            return;
        }

        if ((FixedTickConversion.TicksPerSecond % (ulong)rate) != 0UL) {
            var (lower, upper) = NearestRateDivisors(rate: rate);
            var nearest = ((lower, upper) switch {
                ({ } low, { } high) => $"{low} and {high}",
                ({ } low, null) => $"{low}",
                (null, { } high) => $"{high}",
                (null, null) => "none", // unreachable: 1 and 50400 always bracket a positive rate.
            });

            errors.Add(item: $"simulation.rateHz {rate} does not divide {FixedTickConversion.TicksPerSecond} engine ticks per second exactly; nearest valid rates are {nearest} Hz.");
        }
    }

    // Linear scan over every divisor of 50400 (108 of them) — run at most once per Validate call, so simplicity beats
    // a precomputed table here.
    private static (int? Lower, int? Upper) NearestRateDivisors(int rate) {
        int? lower = null;
        int? upper = null;

        for (var candidate = 1; ((ulong)candidate <= FixedTickConversion.TicksPerSecond); candidate++) {
            if ((FixedTickConversion.TicksPerSecond % (ulong)candidate) != 0UL) {
                continue;
            }

            if (candidate <= rate) {
                lower = candidate;
            }
            if ((candidate >= rate) && (upper is null)) {
                upper = candidate;
            }
        }

        return (lower, upper);
    }

    // Validated in the AUTHORED unit (seconds), not the compiled tick count: DurationTicks' rounding-up guarantee
    // only holds once its FixedQ4816 conversion sees a nonzero value, so a positive value below half a Q48.16 LSB
    // quantizes to zero ticks at any rate — LowerAfterSeconds must be checked as "positive AND does not quantize to
    // FixedQ4816.Zero", not merely positive. `defaultSeconds > ceilingSeconds` is also not exactly equivalent to the
    // ticks-domain `defaultTicks > ceilingTicks` comparison it replaces (DurationTicks' ceiling-rounding is
    // monotonic non-decreasing, so two seconds values under one tick apart can compile to the same count) — this
    // refuses strictly more, never less, which is the safe direction. Every seconds field is also checked finite
    // (NaN/Infinity evade the ordered comparisons and can overflow the checked casts inside Compile) and checked to
    // fit the runtime's int-typed compiled fields via FitsCompiledRange before Compile ever runs.
    private static void ValidateInputHold(WorldInputHoldAuthoring settings, uint ratePerSecond, int populationCapacity, List<string> errors) {
        var ceilingFinite = float.IsFinite(f: settings.CeilingSeconds);
        var defaultFinite = float.IsFinite(f: settings.DefaultSeconds);

        if (!ceilingFinite) {
            errors.Add(item: $"inputHold.ceilingSeconds {settings.CeilingSeconds} must be a finite number.");
        } else if (!FitsCompiledRange(seconds: settings.CeilingSeconds, ratePerSecond: ratePerSecond)) {
            errors.Add(item: $"inputHold.ceilingSeconds {settings.CeilingSeconds} compiles to more simulation ticks than the runtime's compiled field can hold at {ratePerSecond} Hz.");
        }

        if (!float.IsFinite(f: settings.LowerAfterSeconds)) {
            errors.Add(item: $"inputHold.lowerAfterSeconds {settings.LowerAfterSeconds} must be a finite number.");
        } else if (!(settings.LowerAfterSeconds > 0f)) {
            errors.Add(item: $"inputHold.lowerAfterSeconds {settings.LowerAfterSeconds} must be positive.");
        } else if (FixedQ4816.FromDouble(value: settings.LowerAfterSeconds) == FixedQ4816.Zero) {
            errors.Add(item: $"inputHold.lowerAfterSeconds {settings.LowerAfterSeconds} is positive but quantizes to zero in fixed point (Q48.16) — too small to represent as a duration at ANY rate; author a larger value.");
        } else if (!FitsCompiledRange(seconds: settings.LowerAfterSeconds, ratePerSecond: ratePerSecond)) {
            errors.Add(item: $"inputHold.lowerAfterSeconds {settings.LowerAfterSeconds} compiles to more simulation ticks than the runtime's compiled field can hold at {ratePerSecond} Hz.");
        }

        if (!defaultFinite) {
            errors.Add(item: $"inputHold.defaultSeconds {settings.DefaultSeconds} must be a finite number.");
        } else if (!FitsCompiledRange(seconds: settings.DefaultSeconds, ratePerSecond: ratePerSecond)) {
            errors.Add(item: $"inputHold.defaultSeconds {settings.DefaultSeconds} compiles to more simulation ticks than the runtime's compiled field can hold at {ratePerSecond} Hz.");
        }

        if (ceilingFinite && defaultFinite && (settings.DefaultSeconds > settings.CeilingSeconds)) {
            errors.Add(item: $"inputHold.defaultSeconds {settings.DefaultSeconds} exceeds inputHold.ceilingSeconds {settings.CeilingSeconds}.");
        }

        if (settings.Participants is null) {
            return;
        }

        var bodies = new HashSet<int>();

        for (var index = 0; (index < settings.Participants.Count); index++) {
            var participant = settings.Participants[index];
            var path = $"inputHold.participants[{index}]";

            if ((participant.BodyIndex < 0) || (participant.BodyIndex >= populationCapacity)) {
                errors.Add(item: $"{path}.bodyIndex {participant.BodyIndex} is outside 0..{(populationCapacity - 1)} for the authored population capacity.");
            } else if (!bodies.Add(item: participant.BodyIndex)) {
                errors.Add(item: $"{path}.bodyIndex {participant.BodyIndex} is duplicated.");
            }

            if (!float.IsFinite(f: participant.Seconds)) {
                errors.Add(item: $"{path}.seconds {participant.Seconds} must be a finite number.");

                continue;
            }

            if (participant.Seconds > settings.CeilingSeconds) {
                errors.Add(item: $"{path}.seconds {participant.Seconds} exceeds inputHold.ceilingSeconds {settings.CeilingSeconds}.");
            }
            if (!FitsCompiledRange(seconds: participant.Seconds, ratePerSecond: ratePerSecond)) {
                errors.Add(item: $"{path}.seconds {participant.Seconds} compiles to more simulation ticks than the runtime's compiled field can hold at {ratePerSecond} Hz.");
            }
        }
    }

    // Whether an authored seconds value compiles to a tick count the runtime's int-typed compiled fields (the checked
    // casts in WorldInputHoldAuthoring.Compile) can hold. Delegates to the same ulong-typed conversion Compile uses
    // (WorldSimulationTickConversion.DurationTicks) so int overflow is a plain comparison, never a duplicated
    // rounding rule. The catch covers a value large enough to overflow that conversion's own ulong arithmetic.
    private static bool FitsCompiledRange(float seconds, uint ratePerSecond) {
        try {
            return (WorldSimulationTickConversion.DurationTicks(seconds: seconds, ratePerSecond: ratePerSecond) <= int.MaxValue);
        } catch (OverflowException) {
            return false;
        }
    }

    private readonly record struct AssetCheck((string Path, string Message)[] Violations, string? CanonicalHash);

    private static AssetCheck? CheckTune(WorldTune tune) => CheckAsset(
        document: tune.Document,
        source: tune.Id,
        validate: static document => AudioCanonicalizer.Validate(document: document),
        path: static violation => violation.Path,
        message: static violation => violation.Message,
        canonicalHash: static (document, source) => AudioCanonicalizer.Canonicalize(document: document, source: source).Hash
    );

    private static AssetCheck? CheckPatch(WorldPatch patch) => CheckAsset(
        document: patch.Document,
        source: patch.Id,
        validate: static document => SynthPatchCanonicalizer.Validate(document: document),
        path: static violation => violation.Path,
        message: static violation => violation.Message,
        canonicalHash: static (document, source) => SynthPatchCanonicalizer.Canonicalize(document: document, source: source).Hash
    );

    private static AssetCheck? CheckAsset<TDocument, TViolation>(
        TDocument? document,
        string source,
        Func<TDocument, IEnumerable<TViolation>> validate,
        Func<TViolation, string> path,
        Func<TViolation, string> message,
        Func<TDocument, string, string> canonicalHash
    ) where TDocument : class {
        if (document is null) {
            return null;
        }

        var violations = validate(document).Select(violation => (path(violation), message(violation))).ToArray();

        return new AssetCheck(Violations: violations, CanonicalHash: ((violations.Length == 0) ? canonicalHash(document, source) : null));
    }

    private static HashSet<string> ValidateAssets<T>(IReadOnlyList<T> rows, string section, Func<T, string> id, Func<T, string> hash, Func<T, AssetCheck?> check, List<string> errors) where T : class {
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (rows is null) {
            errors.Add(item: $"{section} is required.");

            return ids;
        }

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"{section}[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            var rowId = id(row);
            if (string.IsNullOrWhiteSpace(value: rowId)) {
                errors.Add(item: $"{path}.id is required.");
            } else if (!ids.Add(item: rowId)) {
                errors.Add(item: $"{path}.id '{rowId}' is duplicated.");
            }

            if (check(row) is not { } result) {
                errors.Add(item: $"{path}.document is required.");

                continue;
            }

            if (result.Violations.Length > 0) {
                foreach (var violation in result.Violations) {
                    errors.Add(item: $"{path}.document.{violation.Path}: {violation.Message}");
                }

                continue;
            }

            var rowHash = hash(row);
            if (!string.Equals(a: rowHash, b: result.CanonicalHash, comparisonType: StringComparison.Ordinal)) {
                errors.Add(item: $"{path}.hash '{rowHash}' does not match the canonical sha256 '{result.CanonicalHash}'.");
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
                    ValidateAnchor(anchor: anchoredSpeaker.Anchor, placements: definition.Placements, placementIds: placementIds, creations: definition.Creations, populationCapacity: definition.Population.Capacity, path: $"{path}.anchor", errors: errors);

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
                    if (!float.IsFinite(f: bed.InnerRadius) || (bed.InnerRadius < 0f) ||
                        (float.IsFinite(f: bed.Radius) && (bed.InnerRadius >= bed.Radius))) {
                        errors.Add(item: $"{path}.innerRadius {bed.InnerRadius} must be finite, non-negative, and less than radius {bed.Radius}.");
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

                if ((attenuation.Curve is { } curve) && !IsAudioCurve(curve: curve)) {
                    errors.Add(item: $"{path}.attenuation.curve '{curve}' must be '{WorldAudioDefaults.CurveSmoothstep}', '{WorldAudioDefaults.CurveLinear}', or null.");
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

        if (!IsAudioCurve(curve: audio.DefaultCurve)) {
            errors.Add(item: $"audio.defaultCurve '{audio.DefaultCurve ?? "(absent)"}' must be '{WorldAudioDefaults.CurveSmoothstep}' or '{WorldAudioDefaults.CurveLinear}'.");
        }

        var listener = audio.Listener;

        if (string.IsNullOrWhiteSpace(value: listener)) {
            errors.Add(item: "audio.listener is required ('focus', 'seat:<n>', or a declared camera name).");
        } else if (!string.Equals(a: listener, b: WorldAudioDefaults.ListenerFocus, comparisonType: StringComparison.Ordinal) && !cameras.Contains(item: listener)) {
            if (listener.StartsWith(value: WorldAudioDefaults.ListenerSeatPrefix, comparisonType: StringComparison.Ordinal)) {
                if (!int.TryParse(s: listener.AsSpan(start: WorldAudioDefaults.ListenerSeatPrefix.Length), result: out var seat) ||
                    (seat < 1) || (seat > WorldPopulationLimits.LocalSeatCount)) {
                    errors.Add(item: $"audio.listener '{listener}' names no seat (expected seat:1..seat:{WorldPopulationLimits.LocalSeatCount}).");
                }
            } else {
                errors.Add(item: $"audio.listener '{listener}' is not 'focus', 'seat:<n>', or a declared camera name.");
            }
        }

        ValidateCues(cues: audio.Cues, patchIds: patchIds, speakerNames: speakerNames, errors: errors);
    }

    private static void ValidatePlayerDefaults(WorldPlayerDefaults defaults, List<string> errors) {
        // Control feel is REQUIRED and has no engine fallback: a document either states what its seats should feel
        // like or it does not load. An absent row is refused HERE by name rather than silently becoming a number
        // nobody chose — the whole reason the document-wide singleton and its baked default were retired.
        if (defaults.SeatLook is null) {
            errors.Add(item: "playerDefaults.seatLook is required.");
        } else {
            ValidateSeatLook(seatLook: defaults.SeatLook, path: "playerDefaults.seatLook", errors: errors);
        }

        if (defaults.Identities is not { Count: > 0 }) {
            errors.Add(item: "playerDefaults.identities must contain at least one row.");
        } else {
            var ids = new HashSet<string>(comparer: StringComparer.Ordinal);
            var names = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
            // Each seed becomes an owned-world DOCUMENT ON DISK, stored under WorldOwnedWorldFileName's id→file-name
            // mapping. profile.Id is a WorldSafeName — it CANNOT hold a value that mapping would refuse — so the only
            // thing left to check here is ORDINAL UNIQUENESS: distinct safe ids always address distinct files (the
            // mapping is injective over WorldSafeName), so no separate "does it survive the mapping" check can ever
            // fire once the id has already been through document parse.
            for (var index = 0; index < defaults.Identities.Count; index++) {
                var profile = defaults.Identities[index];
                var path = $"playerDefaults.identities[{index}]";

                if (profile is null) {
                    errors.Add(item: $"{path} is required.");
                    continue;
                }

                if (!ids.Add(item: profile.Id)) {
                    errors.Add(item: $"{path}.id '{profile.Id}' is duplicated.");
                }

                if (string.IsNullOrWhiteSpace(value: profile.Name) || !names.Add(item: profile.Name)) {
                    errors.Add(item: $"{path}.name is required and unique ignoring case.");
                }

                if (!IsHexColor(value: profile.Color)) {
                    errors.Add(item: $"{path}.color must be #RRGGBB.");
                }
            }
        }

        if (!IsHexColor(value: defaults.NeutralColor)) {
            errors.Add(item: "playerDefaults.neutralColor must be #RRGGBB.");
        }

        if (!IsHexColor(value: defaults.PickerNeutralColor)) {
            errors.Add(item: "playerDefaults.pickerNeutralColor must be #RRGGBB.");
        }
        ValidateSequence(sequence: defaults.ColorSequence, path: "playerDefaults.colorSequence", minIndex: 0, errors: errors, WorldSequence.Additive, WorldSequence.R1);
        RequireUnitInterval(value: defaults.Saturation, name: "playerDefaults.saturation", errors: errors);
        RequireUnitInterval(value: defaults.Value, name: "playerDefaults.value", errors: errors);
        RequireUnitInterval(value: defaults.NoseFactor, name: "playerDefaults.noseFactor", errors: errors);
        RequireUnitInterval(value: defaults.PickerThreshold, name: "playerDefaults.pickerThreshold", errors: errors);
        RequireUnitInterval(value: defaults.PickerNeutralBlend, name: "playerDefaults.pickerNeutralBlend", errors: errors);

        if (defaults.ColorSearchLimit < 1) {
            errors.Add(item: "playerDefaults.colorSearchLimit must be positive.");
        }
    }

    private static bool IsHexColor(string? value) {
        return (value is { Length: 7 }) && (value[0] == '#')
            && int.TryParse(s: value.AsSpan(start: 1), style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out _);
    }

    private static bool IsAudioCurve(string? curve) =>
        string.Equals(a: curve, b: WorldAudioDefaults.CurveSmoothstep, comparisonType: StringComparison.Ordinal) ||
        string.Equals(a: curve, b: WorldAudioDefaults.CurveLinear, comparisonType: StringComparison.Ordinal);

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

            if ((cue.GainThousandths is { } gain) && ((gain < 0) || (gain > (int)(Puck.Forge.Authoring.CreationSoundDocument.MaxLevel * 1000f)))) {
                errors.Add(item: $"{path}.gainThousandths {gain} must be within [0, {(int)(Puck.Forge.Authoring.CreationSoundDocument.MaxLevel * 1000f)}].");
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
    // (Puck.Forge.Authoring.CreationSoundDocument.MaxLevel — one vocabulary for every audio gain-shaped field).
    private static void RequireGain(float value, string name, List<string> errors) {
        if (!float.IsFinite(f: value) || (value < 0f) || (value > Puck.Forge.Authoring.CreationSoundDocument.MaxLevel)) {
            errors.Add(item: $"{name} {value} must be within [0, {Puck.Forge.Authoring.CreationSoundDocument.MaxLevel}].");
        }
    }

    // Named spawn poses: id presence/uniqueness and finite coordinates.
    private static HashSet<string> ValidateSpawnPoints(IReadOnlyList<WorldSpawnPoint> spawnPoints, List<string> errors) {
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

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

            RequireFinite(value: spawn.YawDegrees, name: $"spawnPoints[{index}].yawDegrees", errors: errors);
        }

        return ids;
    }

    // Seat 0 is refused OnDemand — the session always needs a first player — and the array must name exactly
    // LocalSeatCount seats, the same shape ValidateSeatSpawns enforces for the sibling per-seat row.
    private static void ValidateSeatActivation(IReadOnlyList<SeatActivationPolicy> seatActivation, List<string> errors) {
        if (seatActivation is not { Count: WorldPopulationLimits.LocalSeatCount }) {
            errors.Add(item: $"population.seatActivation must contain exactly {WorldPopulationLimits.LocalSeatCount} entries.");

            return;
        }

        if (seatActivation[0] != SeatActivationPolicy.Eager) {
            errors.Add(item: "population.seatActivation[0] must be 'eager' — the session's first seat cannot start on-demand.");
        }
    }

    private static void ValidateSeatSpawns(IReadOnlyList<string> seatSpawns, HashSet<string> spawnPointIds, List<string> errors) {
        if (seatSpawns is not { Count: WorldPopulationLimits.LocalSeatCount }) {
            errors.Add(item: $"population.seatSpawns must contain exactly {WorldPopulationLimits.LocalSeatCount} spawn-point names.");

            return;
        }

        for (var index = 0; (index < seatSpawns.Count); index++) {
            if (string.IsNullOrWhiteSpace(value: seatSpawns[index]) || !spawnPointIds.Contains(item: seatSpawns[index])) {
                errors.Add(item: $"population.seatSpawns[{index}] '{seatSpawns[index]}' names no spawn point.");
            }
        }
    }

    private static void ValidatePopulationVariation(WorldPopulationVariation variation, string path, int minIndex, List<string> errors) {
        if (variation is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        ValidateSequence(sequence: variation.Phase, path: $"{path}.phase", minIndex: minIndex, errors: errors, WorldSequence.Additive);
        ValidateSequence(sequence: variation.Weave, path: $"{path}.weave", minIndex: minIndex, errors: errors, WorldSequence.Additive, WorldSequence.R1);
        ValidateSequence(sequence: variation.Activity, path: $"{path}.activity", minIndex: minIndex, errors: errors, WorldSequence.R2);
    }

    private static void ValidateSequence(WorldSequence sequence, string path, int minIndex, List<string> errors, params string[] allowed) {
        if (sequence is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if (!allowed.Contains(value: sequence.Name, comparer: StringComparer.Ordinal)) {
            errors.Add(item: $"{path}.name '{sequence.Name ?? "(absent)"}' must be one of: {string.Join(separator: ", ", values: allowed)}.");
        }

        if (((long)minIndex + sequence.Offset) < 0L) {
            errors.Add(item: $"{path}.offset {sequence.Offset} makes the first consumed index {minIndex} negative.");
        }

        if (string.Equals(a: sequence.Name, b: WorldSequence.Additive, comparisonType: StringComparison.Ordinal)) {
            RequirePositive(value: sequence.Step, name: $"{path}.step", errors: errors);
        } else if (sequence.Step != 0f) {
            errors.Add(item: $"{path}.step must be 0 for the '{sequence.Name}' sequence.");
        }
    }

    private static void ValidateDistribution(WorldDistribution distribution, string path, HashSet<string> spawnPointIds, bool allowDisc, bool allowPoints, bool allowLattice, bool allowZeroDisc, List<string> errors) {
        if ((distribution is null) || (distribution.Region is null)) {
            errors.Add(item: $"{path} requires region and fill members.");

            return;
        }

        switch (distribution.Region) {
            case WorldDistributionRegion.Disc disc when allowDisc:
                if (allowZeroDisc) {
                    RequireNonNegative(value: disc.Radius, name: $"{path}.region.radius", errors: errors);
                } else {
                    RequirePositive(value: disc.Radius, name: $"{path}.region.radius", errors: errors);
                }

                if ((disc.SampleCount is { } samples) && (samples < 1)) {
                    errors.Add(item: $"{path}.region.sampleCount must be positive when present.");
                }

                ValidateSequence(sequence: distribution.Fill, path: $"{path}.fill", minIndex: 0, errors: errors, WorldSequence.Additive);
                break;
            case WorldDistributionRegion.Points points when allowPoints:
                if (points.Names is not { Count: > 0 }) {
                    errors.Add(item: $"{path}.region.names must contain at least one spawn-point name.");
                } else {
                    for (var index = 0; (index < points.Names.Count); index++) {
                        if (string.IsNullOrWhiteSpace(value: points.Names[index]) || !spawnPointIds.Contains(item: points.Names[index])) {
                            errors.Add(item: $"{path}.region.names[{index}] '{points.Names[index]}' names no spawn point.");
                        }
                    }
                }

                RequireNonNegative(value: points.HalfExtent, name: $"{path}.region.halfExtent", errors: errors);
                ValidateSequence(sequence: distribution.Fill, path: $"{path}.fill", minIndex: 0, errors: errors, WorldSequence.R2);
                break;
            case WorldDistributionRegion.Lattice lattice when allowLattice:
                ValidateLattice(lattice: lattice, path: $"{path}.region", errors: errors);
                ValidateSequence(sequence: distribution.Fill, path: $"{path}.fill", minIndex: 0, errors: errors, WorldSequence.None);
                break;
            default:
                errors.Add(item: $"{path}.region kind '{distribution.Region.GetType().Name}' is not supported here.");
                break;
        }
    }

    private static void ValidateLattice(WorldDistributionRegion.Lattice lattice, string path, List<string> errors) {
        if ((lattice.CountA < 1) || (lattice.CountB < 1)) {
            errors.Add(item: $"{path} counts must be at least 1.");
        }

        if ((lattice.CountA > SdfProgramBuilder.MaxInstances) || (lattice.CountB > SdfProgramBuilder.MaxInstances)) {
            errors.Add(item: $"{path} counts cannot exceed the {SdfProgramBuilder.MaxInstances}-instance engine ceiling.");
        }

        if (!IsFinite(value: lattice.StepA)) {
            errors.Add(item: $"{path}.stepA must contain finite coordinates.");
        }

        if (!IsFinite(value: lattice.StepB)) {
            errors.Add(item: $"{path}.stepB must contain finite coordinates.");
        }

        if ((lattice.CountA > 1) && (lattice.StepA.LengthSquared() <= 0f)) {
            errors.Add(item: $"{path}.stepA must be nonzero when countA exceeds 1.");
        }

        if ((lattice.CountB > 1) && (lattice.StepB.LengthSquared() <= 0f)) {
            errors.Add(item: $"{path}.stepB must be nonzero when countB exceeds 1.");
        }

        if ((lattice.CountA > 1) && (lattice.CountB > 1) &&
            (Vector3.Cross(vector1: lattice.StepA, vector2: lattice.StepB).LengthSquared() <= 0f)) {
            errors.Add(item: $"{path} steps must be independent when both counts exceed 1.");
        }
    }

    // The LOOK rows (PRESENTATION-ONLY): name presence/uniqueness (mirroring the kit-name rule), a source over the
    // closed catalog|creation set with a loud unknown default, a resolvable creation reference, a positive scale under
    // the GPU-safety MaxLookScale ceiling, and non-negative motion values — rejecting a zero-hold replay (an infinite
    // loop) and a timeline replay on a catalog source (no timeline to replay) LOUDLY, never silently. Returns the
    // resolved look-name set (a future Inhabit facet resolves its Look against it).
    private static HashSet<string> ValidateLooks(IReadOnlyList<WorldLook> looks, HashSet<string> creationIds, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

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
                        RequireIntRange(value: catalogIndex, min: 0, max: (WorldLookSource.Catalog.RigCount - 1), name: $"{path}.source.index", errors: errors);
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

    // The look assignment policy (PRESENTATION-ONLY): a table needs a non-empty cycle whose every entry resolves to a
    // declared look name. Reuses the shared row-assignment gate verbatim.
    private static void ValidateLookAssignment(WorldRowAssignment assignment, HashSet<string> lookNames, List<string> errors) {
        ValidateRowAssignment(assignment: assignment, section: "lookAssignment", rowNoun: "look", rowNames: lookNames, errors: errors);
    }

    // The shared sequence/row-view gate for kit and look assignment.
    private static void ValidateRowAssignment(WorldRowAssignment assignment, string section, string rowNoun, HashSet<string> rowNames, List<string> errors) {
        ValidateSequence(sequence: assignment.Sequence, path: $"{section}.sequence", minIndex: 0, errors: errors, WorldSequence.Index, WorldSequence.Additive, WorldSequence.R1);

        for (var index = 0; (index < assignment.Rows.Count); index++) {
            if (!rowNames.Contains(item: assignment.Rows[index])) {
                errors.Add(item: $"{section}.rows[{index}] '{assignment.Rows[index]}' names no {rowNoun} row.");
            }
        }
    }

    // The channel table (SIM-AFFECTING — the PlayerIntent vector's vocabulary): name uniqueness; exactly one
    // consumer per row (a role XOR a composition trigger); role channels are bipolar only; channel-count ceiling;
    // threshold range on binary rows; motion-model role completeness (Grounded needs move-forward/move-strafe/turn,
    // Free needs all six). Returns the composition-channel name set kit Actions maps resolve against; composition
    // channels carry no shape restriction.
    private static (HashSet<string> AllNames, HashSet<string> CompositionNames) ValidateChannels(WorldDefinition definition, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var compositionNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var roleClaims = new Dictionary<ChannelRole, string>();
        var channels = (definition.Channels ?? []);

        if (channels.Count > ChannelLimits.MaxChannels) {
            errors.Add(item: $"channels declares {channels.Count} rows, exceeding the {ChannelLimits.MaxChannels}-channel ceiling.");
        }

        for (var index = 0; (index < channels.Count); index++) {
            var channel = channels[index];
            var path = $"channels[{index}]";

            if (channel is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: channel.Name)) {
                errors.Add(item: $"{path} requires a non-empty name.");
            } else if (!names.Add(item: channel.Name)) {
                errors.Add(item: $"{path} duplicates the name '{channel.Name}'.");
            }

            if (!Enum.IsDefined(value: channel.Shape)) {
                errors.Add(item: $"{path}.shape '{channel.Shape}' is not a defined ChannelShape.");
            }

            var hasRole = (channel.Role is not null);

            if (hasRole == channel.Composition) {
                errors.Add(item: $"{path} must be exactly one of a role or a composition channel.");
            } else if (hasRole) {
                var role = channel.Role!.Value;

                if (!Enum.IsDefined(value: role)) {
                    errors.Add(item: $"{path}.role '{role}' is not a defined ChannelRole.");
                } else if (!roleClaims.TryAdd(key: role, value: (channel.Name ?? path))) {
                    errors.Add(item: $"{path}.role '{role}' is already claimed by channel '{roleClaims[role]}'.");
                }

                // A role is a signed axis by construction — reverse/left/down are half the domain, not a degenerate
                // case — so a non-bipolar shape is meaningless to the motion model, never merely unusual. Refusing it
                // here makes WorldBody.Clamped's and SeatController.HeldIntent's hardcoded [-1,1] role range a
                // CONSEQUENCE of this rule instead of a lucky coincidence with the fold's shape-driven range
                // (Puck.Maths.FixedContributionFold, whose minimum/maximum WorldServer derives from this shape).
                if (Enum.IsDefined(value: channel.Shape) && (channel.Shape != ChannelShape.Bipolar)) {
                    errors.Add(item: $"{path}.shape '{channel.Shape}' on channel '{(channel.Name ?? path)}' must be '{ChannelShape.Bipolar}' for role '{role}' — every role channel is a signed axis.");
                }
            } else {
                if (!string.IsNullOrWhiteSpace(value: channel.Name)) {
                    _ = compositionNames.Add(item: channel.Name);
                }
            }

            if (channel.Threshold is { } threshold) {
                if (channel.Shape != ChannelShape.Binary) {
                    errors.Add(item: $"{path}.threshold is only meaningful on a binary channel.");
                } else if (!float.IsFinite(f: threshold)) {
                    errors.Add(item: $"{path}.threshold {threshold} is not a finite number.");
                } else {
                    // The fold compares the QUANTIZED raw threshold, never the authored float (WorldChannelTable.Compile
                    // runs the identical FixedQ4816.FromDouble conversion) — an authored value that quantizes to raw 0
                    // would make bit(v) = (v >= T) true for a NEGATIVE trusted delta on a neutral channel, since the
                    // authored-float check "(0, 1]" alone cannot see the representation the fold actually compares.
                    var quantizedThreshold = FixedQ4816.FromDouble(value: threshold);

                    if ((quantizedThreshold.Value < 1L) || (quantizedThreshold > FixedQ4816.One)) {
                        errors.Add(item: $"{path}.threshold {threshold} quantizes to raw {quantizedThreshold.Value}, outside [1, {FixedQ4816.One.Value}] raw units.");
                    }
                }
            }
        }

        foreach (var kit in (definition.Kits ?? [])) {
            if (kit is null) {
                continue;
            }

            if (!programs.TryGetValue(key: kit.BodyMotionProgram, value: out var program)) {
                continue;
            }

            foreach (var role in Enum.GetValues<ChannelRole>()) {
                if (program.RequiresRole(role: role) && !roleClaims.ContainsKey(key: role)) {
                    errors.Add(item: $"kit '{kit.Name}' body motion program '{program.Name}' requires channel role '{role}', but no declared channel claims it.");
                }
            }
        }

        return (AllNames: names, CompositionNames: compositionNames);
    }

    private static HashSet<string> ValidateTargetRegisters(IReadOnlyList<WorldTargetRegister> registers, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < registers.Count); index++) {
            var register = registers[index];
            var path = $"targetRegisters[{index}]";

            if (register is null) {
                errors.Add(item: $"{path} is required.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(value: register.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (!names.Add(item: register.Name)) {
                errors.Add(item: $"{path}.name '{register.Name}' is duplicated.");
            }
            RequirePositive(value: register.MaximumRange, name: $"{path}.maximumRange", errors: errors);
            ValidateHalfAngle(value: register.MaximumHalfAngleDegrees, name: $"{path}.maximumHalfAngleDegrees", errors: errors);
        }

        return names;
    }

    private static Dictionary<string, CompiledBodyMotionProgram> ValidateBodyMotionPrograms(IReadOnlyList<BodyMotionProgram> programs, ISet<string> targetRegisterNames, List<string> errors) {
        var compiled = new Dictionary<string, CompiledBodyMotionProgram>(comparer: StringComparer.Ordinal);

        if (programs is not { Count: > 0 }) {
            errors.Add(item: "bodyMotionPrograms requires at least one row.");

            return compiled;
        }

        for (var index = 0; index < programs.Count; index++) {
            var program = programs[index];
            var path = $"bodyMotionPrograms[{index}]";

            if (program is null) {
                errors.Add(item: $"{path} is required.");
                continue;
            }
            if (compiled.ContainsKey(key: program.Name)) {
                errors.Add(item: $"{path}.name '{program.Name}' is duplicated.");
                continue;
            }

            try {
                var compiledProgram = CompiledBodyMotionProgram.Compile(program: program);
                compiled.Add(key: program.Name, value: compiledProgram);

                if (compiledProgram.Kind == BodyProgramKind.Producer) {
                    var senses = compiledProgram.Contains(operation: BodyMotionOp.SenseNearestInCone);
                    if (compiledProgram.Contains(operation: BodyMotionOp.ProduceAttendIntent) && !senses) {
                        errors.Add(item: $"{path} producer opcode '{BodyMotionOp.ProduceAttendIntent}' requires '{BodyMotionOp.SenseNearestInCone}'.");
                    }
                    if (compiledProgram.Contains(operation: BodyMotionOp.FaceSensorTarget) && !compiledProgram.Contains(operation: BodyMotionOp.ProduceAttendIntent)) {
                        errors.Add(item: $"{path} producer opcode '{BodyMotionOp.FaceSensorTarget}' requires '{BodyMotionOp.ProduceAttendIntent}'.");
                    }
                    if (senses && (compiledProgram.Target is null)) {
                        errors.Add(item: $"{path}.target is required by '{BodyMotionOp.SenseNearestInCone}'.");
                    } else if (!senses && (compiledProgram.Target is not null)) {
                        errors.Add(item: $"{path}.target requires '{BodyMotionOp.SenseNearestInCone}'.");
                    }
                    switch (compiledProgram.Target) {
                        case BodyTargetSource.Sensed sensed:
                            if (!Enum.IsDefined(value: sensed.Scope)) {
                                errors.Add(item: $"{path}.target.scope '{sensed.Scope}' is not a defined BodyTargetScope.");
                            }
                            RequirePositive(value: sensed.Range, name: $"{path}.target.range", errors: errors);
                            ValidateHalfAngle(value: sensed.HalfAngleDegrees, name: $"{path}.target.halfAngleDegrees", errors: errors);
                            break;
                        case BodyTargetSource.Designated designated when string.IsNullOrWhiteSpace(value: designated.Register) || !targetRegisterNames.Contains(item: designated.Register):
                            errors.Add(item: $"{path}.target.register '{designated.Register}' names no target register.");
                            break;
                    }
                } else if (compiledProgram.Target is not null) {
                    errors.Add(item: $"{path}.target is only admitted on a Producer program.");
                }
            } catch (BodyMotionProgramException exception) {
                errors.Add(item: $"{path} {exception.Message}");
            }
        }

        return compiled;
    }

    private static void ValidateHalfAngle(float value, string name, List<string> errors) {
        if (!float.IsFinite(f: value) || (value <= 0f) || (value > 180f)) {
            errors.Add(item: $"{name} {value} must be finite and within (0, 180].");
        }
    }

    // The kit rows (SIM-AFFECTING): name presence/uniqueness, one motion program, producer parameters, and actions.
    private static HashSet<string> ValidateKits(WorldDefinition definition, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, ISet<string> allChannelNames, ISet<string> compositionChannelNames, IReadOnlyDictionary<string, WorldStateRow> stateRows, List<string> errors) {
        var kitNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var targetRegisterNames = definition.TargetRegisters.Select(register => register.Name).ToHashSet(comparer: StringComparer.Ordinal);

        if (definition.Kits is not { Count: > 0 } kits) {
            errors.Add(item: "kits requires at least one row.");

            return kitNames;
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

            // Resolved only when the program name is defined AND Motion-kind — ValidateMotionModel's coherence check
            // needs a real program to walk; a bad bodyMotionProgram is already refused above, so it skips coherence
            // rather than compounding the refusal with a second, derived one.
            CompiledBodyMotionProgram? motionProgram = null;

            if (string.IsNullOrWhiteSpace(value: kit.BodyMotionProgram) || !programs.TryGetValue(key: kit.BodyMotionProgram, value: out var resolvedProgram)) {
                errors.Add(item: $"{path}.bodyMotionProgram '{kit.BodyMotionProgram}' names no body motion program.");
            } else if (resolvedProgram.Kind != BodyProgramKind.Motion) {
                errors.Add(item: $"{path}.bodyMotionProgram '{kit.BodyMotionProgram}' names a {resolvedProgram.Kind} program, not Motion.");
            } else {
                motionProgram = resolvedProgram;
            }

            ValidateMotionModel(model: kit.Motion, program: motionProgram, path: $"{path}.motion", channelNames: compositionChannelNames, hasWater: (definition.Water is not null), errors: errors);
            ValidateProducerParameters(producers: kit.Producers, programs: programs, channelNames: allChannelNames, path: $"{path}.producers", errors: errors);

            // Actions is a channel-NAME-keyed map now (never a fixed Primary/Secondary pair): a kit naming an
            // undeclared or non-composition channel is a dead reference; a declared composition channel with no
            // entry here stays legal and inert per body (ActionSpec.Compile leaves that ordinal unbound).
            if (kit.Actions is not null) {
                var stateSlots = new Dictionary<string, ActionStateSlot>(comparer: StringComparer.Ordinal);

                foreach (var (channelName, spec) in kit.Actions) {
                    if (spec is null) {
                        continue;
                    }

                    for (var stateIndex = 0; (stateIndex < (spec.State?.Count ?? 0)); stateIndex++) {
                        var state = spec.State![stateIndex];
                        var statePath = $"{path}.actions[{channelName}].state[{stateIndex}]";

                        ValidateActionStateSlot(state: state, path: statePath, errors: errors);

                        if (!string.IsNullOrWhiteSpace(value: state.Name) && stateSlots.TryGetValue(key: state.Name, value: out var prior) && prior != state) {
                            errors.Add(item: $"{statePath} redeclares shared state '{state.Name}' with a different shape.");
                        } else if (!string.IsNullOrWhiteSpace(value: state.Name)) {
                            stateSlots[state.Name] = state;
                        }
                    }
                }

                foreach (var (channelName, spec) in kit.Actions) {
                    if (string.IsNullOrWhiteSpace(value: channelName) || !compositionChannelNames.Contains(item: channelName)) {
                        errors.Add(item: $"{path}.actions names '{channelName}', which is not a declared composition channel.");

                        continue;
                    }

                    ValidateActionSpec(spec: spec, stateSlots: stateSlots, targetRegisterNames: targetRegisterNames, stateRows: stateRows, path: $"{path}.actions[{channelName}]", errors: errors);
                }
            }

            ValidateCollider(collider: kit.Collider, creations: definition.Creations, path: $"{path}.collider", errors: errors);
        }

        if (!kitNames.Contains(item: definition.DefaultSeatKit)) {
            errors.Add(item: $"defaultSeatKit '{definition.DefaultSeatKit}' names no kit row.");
        }

        return kitNames;
    }

    private static void ValidateDefaultPeerSource(WorldDefinition definition, List<string> errors) {
        var source = definition.Population.DefaultPeerSource;

        if (source.IsLive || source.IsIdle) {
            return;
        }
        if (!source.IsProducer || (source.ProducerName is not { } producerName)) {
            errors.Add(item: $"population.defaultPeerSource '{source}' is not a defined IntentSource.");

            return;
        }

        IEnumerable<WorldKit> assignedKits = definition.Kits;
        if (definition.Assignment.Rows.Count > 0) {
            assignedKits = definition.Assignment.Rows
                .Distinct(comparer: StringComparer.Ordinal)
                .Select(name => WorldDefinitionRows.FindKit(kits: definition.Kits, name: name))
                .OfType<WorldKit>();
        }

        foreach (var kit in assignedKits) {
            if ((kit.Producers is null) || !kit.Producers.ContainsKey(key: producerName)) {
                errors.Add(item: $"population.defaultPeerSource names producer '{producerName}', but assigned kit '{kit.Name}' declares no parameters for it.");
            }
        }
    }

    private static readonly string[] s_wanderScalars = [
        "forward", "softRadius", "weaveAmplitude", "inwardGain", "turnScale",
        "weaveFrequencyBase", "weaveFrequencyRange", "altitudeGain", "activityRateBase", "activityRateRange",
        "strafeWave", "turnWave", "upWave", "pitchWave", "rollTurn", "pressThreshold", "altitudeBase", "altitudeRange",
    ];
    private static readonly string[] s_attendScalars = ["standoffRadius", "approach", "orbit"];

    private static void ValidateProducerParameters(IReadOnlyDictionary<string, BodyProgramParameters> producers, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, ISet<string> channelNames, string path, List<string> errors) {
        if (producers is null) {
            errors.Add(item: $"{path} is required.");
            return;
        }

        foreach (var (name, parameters) in producers) {
            var itemPath = $"{path}[{name}]";
            if (string.IsNullOrWhiteSpace(value: name) || !programs.TryGetValue(key: name, value: out var program)) {
                errors.Add(item: $"{itemPath} names no program.");
                continue;
            }
            if (program.Kind != BodyProgramKind.Producer) {
                errors.Add(item: $"{itemPath} names a {program.Kind} program, not Producer.");
                continue;
            }
            if (parameters is null) {
                errors.Add(item: $"{itemPath} is required.");
                continue;
            }
            if ((parameters.Scalars is null) || (parameters.Channels is null)) {
                errors.Add(item: $"{itemPath}.scalars and {itemPath}.channels are required.");
                continue;
            }

            var required = new HashSet<string>(comparer: StringComparer.Ordinal);
            if (program.Contains(operation: BodyMotionOp.ProduceWanderIntent)) {
                required.UnionWith(other: s_wanderScalars);
            }
            if (program.Contains(operation: BodyMotionOp.ProduceAttendIntent)) {
                required.UnionWith(other: s_attendScalars);
                if (program.Target is BodyTargetSource.Sensed) {
                    required.Add(item: "releaseRadius");
                }
            }

            foreach (var scalar in required) {
                if (!parameters.Scalars.ContainsKey(key: scalar)) {
                    errors.Add(item: $"{itemPath}.scalars is missing instruction parameter '{scalar}'.");
                }
            }
            foreach (var (scalar, value) in parameters.Scalars) {
                if (!required.Contains(item: scalar)) {
                    errors.Add(item: $"{itemPath}.scalars contains unknown instruction parameter '{scalar}'.");
                } else {
                    RequireFinite(value: value, name: $"{itemPath}.scalars[{scalar}]", errors: errors);
                }
            }
            foreach (var (argument, channel) in parameters.Channels) {
                if (!string.Equals(a: argument, b: "press", comparisonType: StringComparison.Ordinal) || !program.Contains(operation: BodyMotionOp.ProduceWanderIntent)) {
                    errors.Add(item: $"{itemPath}.channels contains unknown instruction parameter '{argument}'.");
                } else if (string.IsNullOrWhiteSpace(value: channel) || !channelNames.Contains(item: channel)) {
                    errors.Add(item: $"{itemPath}.channels[{argument}] '{channel}' names no channel.");
                }
            }

            if (program.Contains(operation: BodyMotionOp.ProduceWanderIntent)) {
                RequirePositiveScalar(parameters, "softRadius", itemPath, errors);
                RequirePositiveScalar(parameters, "turnScale", itemPath, errors);
                RequireNonNegativeScalar(parameters, "weaveFrequencyBase", itemPath, errors);
                RequireNonNegativeScalar(parameters, "weaveFrequencyRange", itemPath, errors);
                RequireNonNegativeScalar(parameters, "activityRateBase", itemPath, errors);
                RequireNonNegativeScalar(parameters, "activityRateRange", itemPath, errors);
                RequireNonNegativeScalar(parameters, "pressThreshold", itemPath, errors);
                RequireNonNegativeScalar(parameters, "altitudeRange", itemPath, errors);
            }
            if ((program.Target is BodyTargetSource.Sensed sensed) &&
                TryScalar(parameters, "releaseRadius", out var release) && TryScalar(parameters, "standoffRadius", out var standoff)) {
                RequirePositive(value: release, name: $"{itemPath}.scalars[releaseRadius]", errors: errors);
                RequirePositive(value: standoff, name: $"{itemPath}.scalars[standoffRadius]", errors: errors);
                if (!((release > sensed.Range) && (sensed.Range >= standoff))) {
                    errors.Add(item: $"{itemPath} radii must satisfy releaseRadius > the target source range >= standoffRadius.");
                }
            }
            if (program.Contains(operation: BodyMotionOp.ProduceAttendIntent)) {
                RequirePositiveScalar(parameters, "standoffRadius", itemPath, errors);
                if (TryScalar(parameters, "approach", out var approach)) {
                    RequireUnitInterval(value: approach, name: $"{itemPath}.scalars[approach]", errors: errors);
                }
                if (TryScalar(parameters, "orbit", out var orbit)) {
                    RequireUnitInterval(value: orbit, name: $"{itemPath}.scalars[orbit]", errors: errors);
                }
            }
        }
    }

    private static bool TryScalar(BodyProgramParameters parameters, string name, out float value) => parameters.Scalars.TryGetValue(key: name, value: out value);
    private static void RequirePositiveScalar(BodyProgramParameters parameters, string name, string path, List<string> errors) {
        if (TryScalar(parameters, name, out var value)) {
            RequirePositive(value, $"{path}.scalars[{name}]", errors);
        }
    }
    private static void RequireNonNegativeScalar(BodyProgramParameters parameters, string name, string path, List<string> errors) {
        if (TryScalar(parameters, name, out var value)) {
            RequireNonNegative(value, $"{path}.scalars[{name}]", errors);
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

    // The data-side addon descriptors: non-empty unique names, a required module pin, and no two addons declaring the
    // SAME slot (null slots are not dedup-checked, since PlayerRoster.TryClaimSlot seats an unset one at the first
    // free slot not claimed by a seat). Returns the name set — threaded forward to ValidateGrants so a
    // document-authored grant row naming addon:<name> can be resolved against what the document actually declares.
    private static HashSet<string> ValidateAddons(IReadOnlyList<WorldAddonRow> addons, int populationCapacity, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var addon in addons) {
            if ((addon is null) || string.IsNullOrWhiteSpace(value: addon.Name)) {
                errors.Add(item: "an addon requires a name.");

                continue;
            }

            if (!names.Add(item: addon.Name)) {
                errors.Add(item: $"addon name '{addon.Name}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(value: addon.ModulePath)) {
                errors.Add(item: $"addon '{addon.Name}' requires a modulePath.");
            }

            // The hash is REQUIRED — there is no no-pin sentinel any more. An unpinned module makes the state the
            // guest touches depend on a file on disk: a determinism hole first, a security one second.
            if (string.IsNullOrEmpty(value: addon.Hash)) {
                errors.Add(item: $"addon '{addon.Name}' requires a hash — an unpinned module makes authoritative state depend on a file on disk.");
            } else if (!IsValidAddonHash(hash: addon.Hash)) {
                errors.Add(item: $"addon '{addon.Name}' hash '{addon.Hash}' must match sha256-64/{{16 hex}}.");
            }

            // WorldAddonRuntime narrows this ulong to a long FuelPerTick (0 means "use the host default", mirrored
            // as null there); an unbounded value silently wraps negative on that cast, handing the guest an effectively
            // infinite budget that never traps OutOfFuel and hangs the sim-tick thread. Reject it here instead.
            if (addon.Fuel > long.MaxValue) {
                errors.Add(item: $"addon '{addon.Name}' fuel {addon.Fuel} exceeds the maximum of {long.MaxValue}.");
            }

            // The manifest — what this addon ASKS for (WorldCapabilityRequest). A request is a designation only (see
            // its own remarks): bounds-checked exactly like a document-authored grant subject, never checked for
            // whether it will actually be honored — that is Requests' whole point, decided later by the settled
            // grant table (WorldAddonRuntime's mount-time report), never here.
            if (addon.Requests is { } requests) {
                for (var index = 0; (index < requests.Count); index++) {
                    ValidateGrantSubjectBounds(subject: requests[index].Subject, populationCapacity: populationCapacity, path: $"addon '{addon.Name}' requests[{index}]", errors: errors);
                }
            }

            // The machine-memory-watch rows (the fifth event family): screen/address are non-negative, length is a
            // bounded byte range that fits the wire's single i64 value lane (see WorldAddonMemoryWatch's own doc).
            if (addon.MemoryWatches is { } watches) {
                for (var index = 0; (index < watches.Count); index++) {
                    var watch = watches[index];
                    var path = $"addon '{addon.Name}' memoryWatches[{index}]";

                    if (watch.Screen < 0) {
                        errors.Add(item: $"{path}.screen {watch.Screen} must be non-negative.");
                    }

                    if (watch.Address < 0) {
                        errors.Add(item: $"{path}.address {watch.Address} must be non-negative.");
                    }

                    if ((watch.Length < 1) || (watch.Length > 8)) {
                        errors.Add(item: $"{path}.length {watch.Length} must be 1..8.");
                    }
                }
            }
        }

        return names;
    }

    // GrantSubjectJsonConverter already validates a subject's grammar/shape via WorldGrantCommandModule.TryParseSubject
    // at parse time; it has no population figure to check a body:<n> token against (WorldGrants.IsLegitimateSubject
    // bounds it later, at grant time, and only for an actual grant), so that bound is checked here instead, for both
    // a request and an authored grant.
    private static void ValidateGrantSubjectBounds(GrantSubject subject, int populationCapacity, string path, List<string> errors) {
        if ((subject.Kind == GrantSubjectKind.Body) && ((subject.Value < 0) || (subject.Value >= populationCapacity))) {
            errors.Add(item: $"{path}.subject body:{subject.Value} is outside 0..{populationCapacity - 1} for the authored population capacity.");
        }
    }

    // Document-authored grant rows (WorldDefinition.Grants). Console and Seat principals are already canonical per
    // WorldGrantCommandModule.TryParsePrincipal's grammar. An Addon principal's name is resolved against addonNames;
    // a Peer's index is checked against the reserved peer slice (defense in depth against a programmatically
    // constructed definition, since the JSON converter's shared parser already enforces it). An exclusive 'all'
    // reservation is refused, and no two rows may name the identical (principal, capability, subject) triple —
    // unlike the ordinary idempotent re-grant a live world.grant tolerates. Whether a legitimate, non-conflicting row
    // is actually held — including Budget legitimacy — is WorldGrants.TryGrant's decision alone, made once at boot;
    // this pass does not re-derive it.
    // A group member must be a real actor: Seat/Console/Addon/Peer. Group is refused (members are flat, never
    // nested); World/Document are refused (neither is a real actor).
    private static bool IsLegitimateGroupMember(WorldPrincipal member) => member.Kind is
        PrincipalKind.Seat or PrincipalKind.Console or PrincipalKind.Addon or PrincipalKind.Peer;

    // Whether two kinds are identical in every BEHAVIOR-BEARING field — the guard against a "size-only kind": a pair
    // differing ONLY in Capacity is a capacity VALUE, not a kind, and is refused by name below. Roles compares as an
    // ORDERED sequence of (name, capability-set) pairs — two kinds that merely declare their roles in a different
    // order are legitimately different authored data, never coalesced here.
    private static bool SameBehavior(WorldGroupKind a, WorldGroupKind b) {
        if ((a.Roles.Count != b.Roles.Count) ||
            (a.OwnershipPolicy != b.OwnershipPolicy) ||
            (a.Lifetime != b.Lifetime) ||
            (a.EvictionPolicy != b.EvictionPolicy) ||
            !string.Equals(a: a.SharedStateScope, b: b.SharedStateScope, comparisonType: StringComparison.Ordinal)) {
            return false;
        }

        for (var index = 0; (index < a.Roles.Count); index++) {
            var left = a.Roles[index];
            var right = b.Roles[index];

            if (!string.Equals(a: left.Name, b: right.Name, comparisonType: StringComparison.Ordinal) ||
                (left.Capabilities.Count != right.Capabilities.Count)) {
                return false;
            }

            var rightCapabilities = new HashSet<WorldCapability>(collection: right.Capabilities);

            foreach (var capability in left.Capabilities) {
                if (!rightCapabilities.Contains(item: capability)) {
                    return false;
                }
            }
        }

        return true;
    }

    // Validates the GROUP + MEMBERSHIP binding substrate — the group-kind policy catalog and the group roster rows.
    // Returns the declared group-id set so ValidateGrants can check a document-authored group: principal row against
    // it (the SAME forward-threading addonNames already rides). A null section (the document declared no `groups`
    // section at all — OPTIONAL, like `rules`) validates as empty.
    private static HashSet<string> ValidateGroups(WorldGroupsSection? groups, Dictionary<string, WorldStateRow> stateRows, List<string> errors) {
        var groupIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (groups is null) {
            return groupIds;
        }

        if (groups.Kinds.Count > WorldGroupCapacity.MaxKinds) {
            errors.Add(item: $"groups.kinds count {groups.Kinds.Count} exceeds the maximum of {WorldGroupCapacity.MaxKinds}.");
        }

        var kindsByName = new Dictionary<string, WorldGroupKind>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < groups.Kinds.Count); index++) {
            var kind = groups.Kinds[index];
            var path = $"groups.kinds[{index}]";

            if (kind is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: kind.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (!kindsByName.TryAdd(key: kind.Name, value: kind)) {
                errors.Add(item: $"{path}.name '{kind.Name}' is duplicated.");
            }

            if ((kind.Capacity < 1) || (kind.Capacity > WorldGroupCapacity.MaxMembersPerGroup)) {
                errors.Add(item: $"{path}.capacity {kind.Capacity} is outside 1..{WorldGroupCapacity.MaxMembersPerGroup}.");
            }

            var roleNames = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var roleIndex = 0; (roleIndex < kind.Roles.Count); roleIndex++) {
                var role = kind.Roles[roleIndex];
                var rolePath = $"{path}.roles[{roleIndex}]";

                if (role is null) {
                    errors.Add(item: $"{rolePath} is required.");

                    continue;
                }

                if (string.IsNullOrWhiteSpace(value: role.Name)) {
                    errors.Add(item: $"{rolePath}.name is required.");
                } else if (!roleNames.Add(item: role.Name)) {
                    errors.Add(item: $"{rolePath}.name '{role.Name}' is duplicated.");
                }

                if (role.Capabilities.Count == 0) {
                    errors.Add(item: $"{rolePath}.capabilities is empty — a role reaching no capability could not exist without lying about what it is for; omit the role instead.");
                }
            }

            if ((kind.SharedStateScope is { } scope) && !stateRows.ContainsKey(key: scope)) {
                errors.Add(item: $"{path}.sharedStateScope '{scope}' names no declared state row.");
            }
        }

        // The size-only-kind guard: every PAIR of declared kinds must differ in at least one behavior-bearing field.
        var declared = kindsByName.Values.ToArray();

        for (var left = 0; (left < declared.Length); left++) {
            for (var right = (left + 1); (right < declared.Length); right++) {
                if (SameBehavior(a: declared[left], b: declared[right]) && (declared[left].Capacity != declared[right].Capacity)) {
                    errors.Add(item: $"groups.kinds '{declared[left].Name}' and '{declared[right].Name}' differ ONLY in capacity — a kind that differs from another only in capacity is a capacity VALUE, not a kind (rename one usage to author the same kind with a different member cap instead).");
                }
            }
        }

        if (groups.Groups.Count > WorldGroupCapacity.MaxGroups) {
            errors.Add(item: $"groups.groups count {groups.Groups.Count} exceeds the maximum of {WorldGroupCapacity.MaxGroups}.");
        }

        for (var index = 0; (index < groups.Groups.Count); index++) {
            var row = groups.Groups[index];
            var path = $"groups.groups[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            // row.Id already crossed the WorldSafeName door at JSON parse — it CANNOT hold a value the
            // id-to-instance-name composition (WorldSessionResolver.MintInstanceName) would refuse (the identical
            // reasoning WorldIdentitySeed.Id's own validator comment states: the mapping is injective over
            // WorldSafeName, so no separate "does it survive the mapping" check can ever fire here). The only thing
            // left to check is ORDINAL UNIQUENESS within this document.
            if (!groupIds.Add(item: row.Id)) {
                errors.Add(item: $"{path}.id '{row.Id}' is duplicated.");
            }

            // A row's own tags — what a scope=group `tagged` destination selector matches against (see
            // ValidateGroupSelector/WorldGroup.Tags). Absent means none; present-but-empty is refused rather than
            // silently treated as absent, the same "author it or omit it" discipline the section's other optional
            // lists (kind.SharedStateScope, this section's own Ownership) follow.
            if (row.Tags is { Count: 0 }) {
                errors.Add(item: $"{path}.tags is present but empty — omit the member instead of authoring an empty list.");
            } else if (row.Tags is { Count: > 0 } tags) {
                var seenTags = new HashSet<string>(comparer: StringComparer.Ordinal);

                for (var tagIndex = 0; (tagIndex < tags.Count); tagIndex++) {
                    var tag = tags[tagIndex];
                    var tagPath = $"{path}.tags[{tagIndex}]";

                    if (string.IsNullOrWhiteSpace(value: tag)) {
                        errors.Add(item: $"{tagPath} must be non-empty.");
                    } else if (!seenTags.Add(item: tag)) {
                        errors.Add(item: $"{tagPath} '{tag}' is duplicated.");
                    }
                }
            }

            if (!kindsByName.TryGetValue(key: row.KindName ?? string.Empty, value: out var kind)) {
                errors.Add(item: $"{path}.kindName '{row.KindName}' names no declared group kind.");

                continue;
            }

            if (row.Members.Count > kind.Capacity) {
                errors.Add(item: $"{path} has {row.Members.Count} member(s), exceeding kind '{kind.Name}''s capacity of {kind.Capacity}.");
            }

            var seenMembers = new HashSet<WorldPrincipal>();

            for (var memberIndex = 0; (memberIndex < row.Members.Count); memberIndex++) {
                var member = row.Members[memberIndex];
                var memberPath = $"{path}.members[{memberIndex}]";

                if (!IsLegitimateGroupMember(member: member)) {
                    errors.Add(item: (member.Kind == PrincipalKind.Group)
                        ? $"{memberPath} is '{member.Describe()}' — FLAT ONLY: a group member is a principal, never a group."
                        : $"{memberPath} is '{member.Describe()}' — {member.Kind} is not a real actor and cannot hold membership.");
                } else if (!seenMembers.Add(item: member)) {
                    errors.Add(item: $"{memberPath} '{member.Describe()}' is duplicated within the group.");
                }
            }
        }

        // One row per subject — the structural half of the escrow/transfer lane's refusal obligation (see
        // WorldMutation.OfferOwnership/SettleOwnership's own remarks): a subject with TWO ownership rows would have
        // two answers to "who owns it", which is exactly the "owned by two principals" shape the invariant forbids,
        // reachable here only through hand-authored duplication (no live mutation kind can produce it — every arm
        // REPLACES the one row naming a subject, never appends a second).
        var seenSubjects = new HashSet<OwnershipSubject>();

        for (var index = 0; (index < groups.Ownership.Count); index++) {
            var row = groups.Ownership[index];
            var path = $"groups.ownership[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!seenSubjects.Add(item: row.Subject)) {
                errors.Add(item: $"{path}.subject '{row.Subject.Describe()}' is duplicated — a subject may carry exactly one ownership row.");
            }

            switch (row.Subject.Kind) {
                case OwnershipSubjectKind.Group:
                    if (!groupIds.Contains(item: row.Subject.Id ?? string.Empty)) {
                        errors.Add(item: $"{path}.subject names group '{row.Subject.Id}', which no declared group row carries.");
                    }

                    break;
                default:
                    errors.Add(item: $"{path}.subject.kind {row.Subject.Kind} is not a declared subject kind.");

                    break;
            }

            switch (row.Owner.Kind) {
                case OwnershipOwnerKind.Principal:
                    if (row.Owner.Principal is not { } owner) {
                        errors.Add(item: $"{path}.owner.kind is Principal but carries no principal.");
                    } else if (!IsLegitimateGroupMember(member: owner)) {
                        errors.Add(item: $"{path}.owner '{owner.Describe()}' is not a legitimate owner principal — group ownership rides owner.kind Group instead.");
                    }

                    if (row.Owner.GroupId is not null) {
                        errors.Add(item: $"{path}.owner.kind is Principal but also carries a groupId.");
                    }

                    if (row.Owner.Escrow is not null) {
                        errors.Add(item: $"{path}.owner.kind is Principal but also carries an escrow.");
                    }

                    break;
                case OwnershipOwnerKind.Group:
                    if (row.Owner.GroupId is not { } ownerGroupId) {
                        errors.Add(item: $"{path}.owner.kind is Group but carries no groupId.");
                    } else if (!groupIds.Contains(item: ownerGroupId)) {
                        errors.Add(item: $"{path}.owner names group '{ownerGroupId}', which no declared group row carries.");
                    }

                    if (row.Owner.Principal is not null) {
                        errors.Add(item: $"{path}.owner.kind is Group but also carries a principal.");
                    }

                    if (row.Owner.Escrow is not null) {
                        errors.Add(item: $"{path}.owner.kind is Group but also carries an escrow.");
                    }

                    break;
                case OwnershipOwnerKind.Escrow:
                    if (row.Owner.Escrow is not { } escrow) {
                        errors.Add(item: $"{path}.owner.kind is Escrow but carries no escrow.");
                    } else {
                        if (!IsLegitimateGroupMember(member: escrow.Offerer)) {
                            errors.Add(item: $"{path}.owner.escrow.offerer '{escrow.Offerer.Describe()}' is not a legitimate actor principal.");
                        }

                        if (!IsLegitimateGroupMember(member: escrow.Recipient)) {
                            errors.Add(item: $"{path}.owner.escrow.recipient '{escrow.Recipient.Describe()}' is not a legitimate actor principal.");
                        }

                        if (escrow.Offerer == escrow.Recipient) {
                            errors.Add(item: $"{path}.owner.escrow offers to its own offerer {escrow.Offerer.Describe()} — an offer to oneself is not a trade.");
                        }

                        if (escrow.DeadlineTick < 0) {
                            errors.Add(item: $"{path}.owner.escrow.deadlineTick {escrow.DeadlineTick} is negative.");
                        }
                    }

                    if (row.Owner.Principal is not null) {
                        errors.Add(item: $"{path}.owner.kind is Escrow but also carries a principal.");
                    }

                    if (row.Owner.GroupId is not null) {
                        errors.Add(item: $"{path}.owner.kind is Escrow but also carries a groupId.");
                    }

                    break;
                default:
                    errors.Add(item: $"{path}.owner.kind {row.Owner.Kind} is not a declared owner kind.");

                    break;
            }
        }

        return groupIds;
    }

    private static void ValidateGrants(IReadOnlyList<WorldGrant> grants, HashSet<string> addonNames, HashSet<string> groupIds, int populationCapacity, List<string> errors) {
        var seen = new HashSet<(WorldPrincipal, WorldCapability, GrantSubject)>();

        for (var index = 0; (index < grants.Count); index++) {
            var grant = grants[index];
            var path = $"grants[{index}]";

            // A `world` row is refused HERE, at the document's own door, rather than only at the boot replay: the
            // grant table refuses it on EVERY boot (WorldGrants.Conflicts rule (-1) — the world's authority is
            // STRUCTURAL, so a row for it would be accepted-and-inert), which made this a document that validates
            // against itself and then loses a row it declared, every single time it loads. A document may not carry
            // a row nothing will ever hold.
            if (grant.Principal.Kind == PrincipalKind.World) {
                errors.Add(item: $"{path}.principal is 'world' — the world's own authored program (a rules effect, a kit's generate effect) holds no grant rows: its authority is STRUCTURAL, admitted before the table is consulted at all, so the grant table refuses this row on every boot and the document would validate against itself.");
            } else if ((grant.Principal.Kind == PrincipalKind.Addon) && !addonNames.Contains(item: grant.Principal.Name ?? string.Empty)) {
                errors.Add(item: $"{path}.principal addon:{grant.Principal.Name} names no declared addon row.");
            } else if ((grant.Principal.Kind == PrincipalKind.Peer) && ((uint)(grant.Principal.Index - WorldPopulationLimits.LocalSeatCount) >= (uint)(populationCapacity - WorldPopulationLimits.LocalSeatCount))) {
                errors.Add(item: $"{path}.principal peer:{grant.Principal.Index} is outside {WorldPopulationLimits.LocalSeatCount}..{populationCapacity - 1} for the authored population capacity.");
            } else if ((grant.Principal.Kind == PrincipalKind.Group) && !groupIds.Contains(item: grant.Principal.Name ?? string.Empty)) {
                // The SAME "validates then loses the row" trap the world/addon/peer checks above already close: the
                // live table refuses an unknown-group grant row too (Server.WorldGrants.Conflicts' reachability
                // check), so a document that validates against itself here would lose the row on every boot.
                errors.Add(item: $"{path}.principal group:{grant.Principal.Name} names no declared group row.");
            }

            ValidateGrantSubjectBounds(subject: grant.Subject, populationCapacity: populationCapacity, path: path, errors: errors);

            if (grant.Exclusive && (grant.Subject.Kind == GrantSubjectKind.All)) {
                errors.Add(item: $"{path} is exclusive over 'all' — an exclusive reservation must name a concrete subject.");
            }

            if (!seen.Add(item: (grant.Principal, grant.Capability, grant.Subject))) {
                errors.Add(item: $"{path} duplicates an earlier row naming the same principal, capability, and subject.");
            }
        }
    }

    // The admission section: which identities/issuers the TCP door admits (WorldAdmissionDoor, Puck.World.Server's
    // WorldTcpHost), and what each is minted. Crypto-shape rules reuse Puck.Carriage's TrustListEntry.Validate()
    // directly rather than re-deriving them. Grant TEMPLATE rows are checked against the same subject-bounds/
    // exclusive-over-all rules ValidateGrants applies; Budget/exclusivity legitimacy is WorldServer.Grant's decision
    // at admission time, not this pass's.
    private static void ValidateAdmission(IReadOnlyList<WorldAdmissionEntry>? entries, int populationCapacity, List<string> errors) {
        if (entries is not { Count: > 0 } rows) {
            return;
        }

        var seen = new HashSet<(string Domain, string? Subject, WorldAdmissionTrustMode Mode)>();

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"admission[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!seen.Add(item: (row.Domain, row.Subject, row.Mode))) {
                errors.Add(item: $"{path} duplicates an earlier row naming the same domain, subject, and mode.");
            }

            byte[]? spki = null;

            try {
                spki = Convert.FromBase64String(s: (row.PublicKey ?? string.Empty));
            } catch (FormatException) {
                errors.Add(item: $"{path}.publicKey is not valid base64.");
            }

            if (spki is { Length: > 0 }) {
                try {
                    var pinnedId = new KeyId {
                        Algorithm = (row.Algorithm ?? string.Empty),
                        Domain = (row.Domain ?? string.Empty),
                        KeyHash = KeyId.ComputeKeyHash(subjectPublicKeyInfo: spki),
                        Subject = ((row.Mode == WorldAdmissionTrustMode.Vouches) ? null : row.Subject),
                    };
                    var entry = new TrustListEntry(
                        PinnedId: pinnedId,
                        PublicKeySubjectPublicKeyInfo: spki,
                        Mode: ((row.Mode == WorldAdmissionTrustMode.Vouches) ? CarriageTrustMode.Vouches : CarriageTrustMode.SignsDirectly),
                        Reach: WorldAdmissionEmptyReach,
                        MaximumAge: null
                    );

                    entry.Validate();
                } catch (ArgumentException exception) {
                    errors.Add(item: $"{path}: {exception.Message}");
                }
            } else if (spki is not null) {
                errors.Add(item: $"{path}.publicKey decodes to zero bytes.");
            }

            if ((row.Mode == WorldAdmissionTrustMode.SignsDirectly) && string.IsNullOrWhiteSpace(value: row.Subject)) {
                errors.Add(item: $"{path}.subject is required for mode 'signsDirectly'.");
            }

            if ((row.Mode == WorldAdmissionTrustMode.Vouches) && (row.Subject is not null)) {
                errors.Add(item: $"{path}.subject must be absent for mode 'vouches' — a vouching root's chain resolves its own subject; it does not pin one here.");
            }

            var grants = (row.Grants ?? []);

            for (var grantIndex = 0; (grantIndex < grants.Count); grantIndex++) {
                var grant = grants[grantIndex];
                var grantPath = $"{path}.grants[{grantIndex}]";

                ValidateGrantSubjectBounds(subject: grant.Subject, populationCapacity: populationCapacity, path: grantPath, errors: errors);

                if (grant.Exclusive && (grant.Subject.Kind == GrantSubjectKind.All)) {
                    errors.Add(item: $"{grantPath} is exclusive over 'all' — an exclusive reservation must name a concrete subject.");
                }
            }
        }
    }

    // Mirrors WorldAdmissionDoor's own s_noReach: this section's authorization vocabulary is
    // WorldAdmissionEntry.Grants, never Puck.Carriage's slot-reach mechanism, so every entry validates against an
    // empty reach set here too.
    private static readonly IReadOnlySet<string> WorldAdmissionEmptyReach = new HashSet<string>(comparer: StringComparer.Ordinal);

    // Hand-rolled rather than System.Text.Regex — the canonical form is a "sha256-64/" prefix followed by exactly
    // 16 lowercase hex digits (AssetContentHash.ToString's "x16" format).
    private static bool IsValidAddonHash(string hash) {
        const string prefix = "sha256-64/";

        if (!hash.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal) || (hash.Length != (prefix.Length + 16))) {
            return false;
        }

        for (var index = prefix.Length; (index < hash.Length); index++) {
            if (!char.IsAsciiHexDigitLower(c: hash[index])) {
                return false;
            }
        }

        return true;
    }

    // THIS document's channel table, for the binding-overlay vocabulary check — or null when the channels section is
    // itself too malformed to compile (a null row, or more rows than ordinals exist). Null is safe rather than
    // permissive: every condition that produces it is already an error ValidateChannels added, so the document is
    // refused on the channel section instead of being judged against a table that would misreport which names are
    // declared.
    private static WorldChannelTable? CompilableChannelTable(IReadOnlyList<WorldChannel> channels) {
        if ((channels is null) || (channels.Count > ChannelLimits.MaxChannels)) {
            return null;
        }

        for (var index = 0; (index < channels.Count); index++) {
            if (channels[index] is null) {
                return null;
            }
        }

        return WorldChannelTable.Compile(channels: channels);
    }

    // The per-world binding overlays: non-empty unique ids, and the COMPOSED result (engine default ⊕ every
    // overlay) passes the existing binding compiler — a partial overlay page that only makes sense post-merge still
    // gates against the real runtime artifact, and the binding validator is never reimplemented. The vocabulary half
    // resolves channel names against THIS document's own table (the `channels` parameter), never a process-global.
    private static void ValidateBindingOverlays(IReadOnlyList<WorldBindingOverlay> overlays, WorldChannelTable? channels, List<string> errors) {
        if (overlays is null) {
            errors.Add(item: "bindingOverlays is required.");

            return;
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);
        var layers = new List<BindingProfileDocument?> { BindingVocabularyHook.BuildDefaultDocument() };

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

                // The vocabulary half, per overlay so the finding names WHICH overlay carries the dead reference.
                // Skipped (never silently passed) when no vocabulary is installed — an offline/pre-container caller
                // has no registry to ask; the composition root's post-build sweep re-covers the boot documents.
                if (channels is { } table) {
                    var vocabularyErrors = new List<string>();

                    BindingVocabularyHook.VocabularyCheck?.Invoke(overlay.Document, table, vocabularyErrors);

                    foreach (var error in vocabularyErrors) {
                        errors.Add(item: $"{path} ('{overlay.Id}') {error}");
                    }
                }
            }

            ValidateBindingBar(authoring: overlay.BindingBar, path: $"{path}.bindingBar", errors: errors);
        }

        try {
            _ = BindingProfile.Compile(document: WorldBindingComposer.Compose(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: layers)));
        } catch (ArgumentException exception) {
            errors.Add(item: $"bindingOverlays do not compose into a valid mapping: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }
    }

    private static void ValidateBindingBar(WorldBindingBarAuthoring? authoring, string path, List<string> errors) {
        if (authoring is null) {
            return;
        }

        if (!float.IsFinite(f: authoring.HideAfterRestSeconds) || (authoring.HideAfterRestSeconds < 0f)) {
            errors.Add(item: $"{path}.hideAfterRestSeconds {authoring.HideAfterRestSeconds} must be finite and non-negative.");
        }

        if (authoring.Layout is not { } layout) {
            return;
        }

        RequirePositive(value: layout.ButtonSize, name: $"{path}.layout.buttonSize", errors: errors);
        RequireNonNegative(value: layout.CenterGap, name: $"{path}.layout.centerGap", errors: errors);

        if (!float.IsFinite(f: layout.AnchorOffsetY) || (layout.AnchorOffsetY < 0f) || (layout.AnchorOffsetY > 1f)) {
            errors.Add(item: $"{path}.layout.anchorOffsetY {layout.AnchorOffsetY} is outside 0..1.");
        }

        RequireNonNegative(value: layout.GlyphOffsetRatio, name: $"{path}.layout.glyphOffsetRatio", errors: errors);
        RequirePositive(value: layout.GlyphSizeRatio, name: $"{path}.layout.glyphSizeRatio", errors: errors);
        RequirePositive(value: layout.Scale, name: $"{path}.layout.scale", errors: errors);
    }

    // The hud section: schema caps (MaxWorldPanels; MaxElementsPerPanel, or the tighter MaxElementsPerSeatPanel for
    // an owned identity's seat panel — see isIdentityScope), id uniqueness (panels; elements within their panel),
    // rect sanity, WorldHudLayer.Replace refused for a seat panel, and the closed HudBindingVocabulary — including
    // whether a state.<name> binding resolves against the state rows validated just before this call. Throws an
    // enum-reasoned HudValidationException at the first violation, caught here and folded into the whole-document
    // errors list.
    private static void ValidateHud(WorldHudSection hud, IReadOnlyDictionary<string, WorldStateRow> stateRows, bool isIdentityScope, List<string> errors) {
        if (hud is null) {
            errors.Add(item: "hud is required.");

            return;
        }

        try {
            ValidateHudCore(hud: hud, stateRows: stateRows, isIdentityScope: isIdentityScope);
        } catch (HudValidationException exception) {
            errors.Add(item: $"hud.{exception.Reason}: {exception.Message}");
        }
    }

    private static void ValidateHudCore(WorldHudSection hud, IReadOnlyDictionary<string, WorldStateRow> stateRows, bool isIdentityScope) {
        var panels = hud.Panels;
        var maxElements = (isIdentityScope ? WorldHudCapacity.MaxElementsPerSeatPanel : WorldHudCapacity.MaxElementsPerPanel);

        if (hud.Defaults is { Cursor: { } cursor }) {
            if (!float.IsFinite(f: cursor.HoverRadius) || (cursor.HoverRadius <= 0f) || (cursor.HoverRadius > 1024f)) {
                throw new HudValidationException(reason: HudRefusal.CursorInvalid, message: $"hud.defaults.cursor.hoverRadius must be finite, positive, and at most 1024 world units (got {cursor.HoverRadius}).");
            }

            if (!float.IsFinite(f: cursor.SizePx) || (cursor.SizePx <= 0f) || (cursor.SizePx > 64f)) {
                throw new HudValidationException(reason: HudRefusal.CursorInvalid, message: $"hud.defaults.cursor.sizePx must be finite, positive, and at most 64 pixels (got {cursor.SizePx}).");
            }

            if (!Enum.IsDefined(value: cursor.Role)) {
                throw new HudValidationException(reason: HudRefusal.CursorInvalid, message: $"hud.defaults.cursor.role value {(int)cursor.Role} is not a defined cursor role.");
            }
        }

        if (panels.Count > WorldHudCapacity.MaxWorldPanels) {
            throw new HudValidationException(reason: HudRefusal.TooManyPanels, message: $"hud.panels count {panels.Count} exceeds the maximum of {WorldHudCapacity.MaxWorldPanels}.");
        }

        var panelIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var panelIndex = 0; (panelIndex < panels.Count); panelIndex++) {
            var panel = panels[panelIndex];
            var panelPath = $"hud.panels[{panelIndex}]";

            if (string.IsNullOrWhiteSpace(value: panel.Id)) {
                throw new HudValidationException(reason: HudRefusal.DuplicatePanelId, message: $"{panelPath}.id is required.");
            }

            if (!panelIds.Add(item: panel.Id)) {
                throw new HudValidationException(reason: HudRefusal.DuplicatePanelId, message: $"{panelPath}.id '{panel.Id}' is duplicated.");
            }

            if (isIdentityScope && (panel.Layer == WorldHudLayer.Replace)) {
                throw new HudValidationException(reason: HudRefusal.SeatPanelReplaceRefused, message: $"{panelPath}.layer 'replace' is not meaningful for an identity-owned seat panel confined to one seat's viewport — use under or over.");
            }

            HudRowValidation.ValidateRect(rect: panel.Rect, path: $"{panelPath}.rect");
            HudRowValidation.ValidateElements(elements: panel.Elements, panelPath: $"{panelPath} ('{panel.Id}')", maxElements: maxElements, stateRows: stateRows);
        }
    }

    // The state section: schema cap (MaxRows), name uniqueness, both-or-neither Min/Max range (applied to every
    // cell's value), text-cell length against MaxTextValueLength, and per-row cell-count ceiling (MaxCellsPerRow,
    // optionally narrowed by an authored Capacity). WorldCellName already refuses an empty/unsafe/dotted row name at
    // JSON parse, so this pass checks only uniqueness. Returns the declared rows by name so ValidateHud can refuse
    // an unknown state.<row>/state.<row>.<key> binding.
    private static Dictionary<string, WorldStateRow> ValidateState(IReadOnlyList<WorldStateRow> rows, IReadOnlyList<WorldGeneratorRow>? generators, List<string> errors) {
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

            if (!byName.TryAdd(key: row.Name, value: row)) {
                errors.Add(item: $"{path}.name '{row.Name}' is duplicated.");
            }

            // The reserved prefix is ENGINE-MINTED ONLY, and the rule lives HERE — in the validator every ingress
            // passes (boot, live mutation, undo replay), never in one door a hand-authored file walks around.
            // Nothing mints a state ROW, so the prefix is refused outright on a row name; that is also what keeps a
            // reserved rule channel ($tick/$population/$region:) from ever being shadowed by a real row.
            if (row.Name.Value.StartsWith(value: WorldStateRow.ReservedNamePrefix, comparisonType: StringComparison.Ordinal)) {
                errors.Add(item: $"{path}.name '{row.Name}' starts with the reserved prefix '{WorldStateRow.ReservedNamePrefix}' — reserved for engine-minted names and the rules section's own channels ({WorldRuleFacts.Tick}, {WorldRuleFacts.Population}, {WorldRuleFacts.RegionPrefix}<placementId>).");
            }

            if (!Enum.IsDefined(value: row.Kind)) {
                errors.Add(item: $"{path}.kind '{row.Kind}' is not a defined CellKind.");

                continue;
            }

            var numeric = ((row.Kind == CellKind.Int) || (row.Kind == CellKind.Fixed));

            // Min/Max/NonNegative are envelope traits over a NUMBER — legitimate only for Int/Fixed, the same rule a
            // scalar row's range always followed, now stated once instead of per case.
            if (!numeric && ((row.Min is not null) || (row.Max is not null))) {
                errors.Add(item: $"{path} ('{row.Name}') declares min/max on a {DescribeKind(kind: row.Kind)} row — only int/fixed rows carry a range.");
            } else if ((row.Min is null) != (row.Max is null)) {
                errors.Add(item: $"{path} declares only one of min/max — a range is authored as a pair or not at all.");
            } else if ((row.Min is { } lo) && (row.Max is { } hi) && (lo >= hi)) {
                errors.Add(item: $"{path} min {DescribeValue(kind: row.Kind, raw: lo)} must be less than max {DescribeValue(kind: row.Kind, raw: hi)}.");
            }

            if (!numeric && row.NonNegative) {
                errors.Add(item: $"{path} ('{row.Name}') declares nonNegative on a {DescribeKind(kind: row.Kind)} row — only int/fixed rows carry a floor.");
            }

            // GatesDrive is the composition-lane's drive-admission gate (WorldGrants.TryGetDriveGate) — a nonzero
            // per-body cell there refuses that body's drive/action intents regardless of any grant held. It reads a
            // cell as zero/nonzero, so a text row has no honest reading for it, and it is read per BODY (one cell
            // per entity index), so only a keyed (table) row — one declaring Capacity — has a body to address; a
            // slot has exactly one value shared by every body, which is not what a per-body gate means.
            if ((row.Kind == CellKind.Text) && row.GatesDrive) {
                errors.Add(item: $"{path} ('{row.Name}') declares gatesDrive on a text row — a drive gate reads a cell as zero/nonzero, which a text cell has no honest reading for.");
            }

            if (row.GatesDrive && (row.Capacity is null)) {
                errors.Add(item: $"{path} ('{row.Name}') declares gatesDrive without a capacity — a drive gate is read per body (one cell keyed by the body's entity index), which only a keyed (table) row can carry; a slot has no ONE body to gate.");
            }

            // Evicts is the row's own overflow policy: drop-oldest instead of refuse. It reads exactly one bound —
            // Capacity — so the only shape it can legitimately name is a keyed row that declares one; a slot never
            // declares Capacity (WorldStateRow.IsSlot), so this one check refuses both "no capacity at all" and "on a
            // slot row" by the same name, with the remedy spelled out.
            if (row.Evicts && (row.Capacity is null)) {
                errors.Add(item: $"{path} ('{row.Name}') declares evicts without a capacity — eviction drops the oldest cell once a write would exceed the declared bound, which only a keyed (table) row declaring capacity can carry; a slot has no bound to evict against. Declare a capacity, or drop evicts.");
            }

            if ((row.Capacity is { } declaredCapacity) && ((declaredCapacity < 1) || (declaredCapacity > WorldStateCapacity.MaxCellsPerRow))) {
                errors.Add(item: $"{path}.capacity {declaredCapacity} must be between 1 and {WorldStateCapacity.MaxCellsPerRow}.");
            }

            ValidateDraw(row: row, generators: generators, path: path, errors: errors);
            ValidateAdvance(row: row, numeric: numeric, path: path, errors: errors);
            var effectiveCapacity = Math.Clamp(value: (row.Capacity ?? WorldStateCapacity.MaxCellsPerRow), min: 1, max: WorldStateCapacity.MaxCellsPerRow);
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
                } else if (reservesSlotKey && (cell.Key == WorldStateRow.SlotKey)) {
                    errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' uses the reserved slot key '{WorldStateRow.SlotKey}' as an authored cell key.");
                } else if (!WorldStateReservedCells.TryValidateReservedCell(row: row, key: cell.Key, reason: out var reservedReason)) {
                    // Any other reserved-prefix key is refused unless it is exactly the engine-minted key legitimate
                    // for this row's shape, carrying a value the engine could have written (a non-negative cursor; a
                    // deck mask inside its context's alternative count under a deck mode). The rule lives in
                    // WorldGeneratorCells so UpsertStateCell's compose arm refuses the identical shape from the
                    // identical code.
                    errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' {reservedReason}.");
                }

                if (cell.Advance is { } cellAdvance) {
                    ValidateCellAdvance(row: row, cell: cell, advance: cellAdvance, numeric: numeric, cellPath: cellPath, errors: errors);
                }

                if ((cell.Provenance is { } provenance) && (provenance.Length > WorldStateCapacity.MaxProvenanceLength)) {
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
                if (row.NonNegative && (cell.Value < 0)) {
                    errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' value {DescribeValue(kind: row.Kind, raw: cell.Value)} is negative — this row's floor is non-negative.");
                }

                if (rangeDeclared && ((cell.Value < row.Min) || (cell.Value > row.Max))) {
                    errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' value {DescribeValue(kind: row.Kind, raw: cell.Value)} is outside its declared range {DescribeValue(kind: row.Kind, raw: row.Min!.Value)}..{DescribeValue(kind: row.Kind, raw: row.Max!.Value)}.");
                }

                // An INT cell is read as fixed point wherever the engine reads it at all (a world rule's gate, its
                // comparand, its live copy operand), and that lift throws outside FixedQ4816's integer band. Refused
                // HERE so every ingress meets the same door: without it an ordinary authored number kills the process
                // on the first tick a rule touches the row, with no refusal anywhere. A FIXED cell carries raw bits
                // and spans the whole long, so it is exempt by construction.
                if ((row.Kind == CellKind.Int) && ((cell.Value < WorldStateCapacity.MinIntCellValue) || (cell.Value > WorldStateCapacity.MaxIntCellValue))) {
                    errors.Add(item: $"{path} ('{row.Name}') cell '{cell.Key}' value {cell.Value} is outside the representable int range {WorldStateCapacity.MinIntCellValue}..{WorldStateCapacity.MaxIntCellValue} — every engine read of an int cell lifts it to fixed point.");
                }
            }
        }

        return byName;
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

            if (row.Name.Value.StartsWith(value: WorldStateRow.ReservedNamePrefix, comparisonType: StringComparison.Ordinal)) {
                errors.Add(item: $"{path}.name '{row.Name}' starts with the reserved prefix '{WorldStateRow.ReservedNamePrefix}'.");
            }

            if (row.Generator is null) {
                errors.Add(item: $"{path}.generator is required.");

                continue;
            }

            ValidateSource(generator: row.Generator, path: path, errors: errors);
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

        // Each source's fields are BOTH-OR-NEITHER against the fields the others own — a foreign field present is
        // refused by name rather than silently ignored, the same "no dual discriminator" discipline WorldStateRow
        // itself already follows for value/cells.
        var declaresMarkovFields = ((generator.Start is not null) || (generator.Contexts is not null));
        var declaresRangeFields = ((generator.RangeMin is not null) || (generator.RangeMax is not null));
        var declaresWeighted = (generator.Weighted is not null);

        // Bound and Mode are Markov-only too, but they are NON-NULLABLE, so the both-or-neither sweep above cannot
        // see them and a numeric source carrying either would parse, validate, and then be silently ignored at fire
        // time. Refused against the DECLARED DEFAULT — the most a non-nullable field can distinguish, and exactly the
        // set of values that could mislead.
        if (generator.Source != WorldGeneratorSource.Markov) {
            if (generator.Bound != WorldGenerator.DefaultBound) {
                errors.Add(item: $"{path}.source={DescribeGeneratorSource(source: generator.Source)} declares bound {generator.Bound} — a numeric source is always exactly ONE draw, and 'bound' belongs to source=markov.");
            }

            if (generator.Mode != WorldGeneratorMode.WithReplacement) {
                errors.Add(item: $"{path}.source={DescribeGeneratorSource(source: generator.Source)} declares mode={generator.Mode.ToString().ToLowerInvariant()} — a numeric source never deals, and 'mode' belongs to source=markov.");
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

                ValidateMarkovSource(generator: generator, path: path, errors: errors);

                return;
            case WorldGeneratorSource.UniformRange:
                if (declaresMarkovFields) {
                    errors.Add(item: $"{path} declares source=uniformRange beside start/contexts, which belong to source=markov.");
                }

                if (declaresWeighted) {
                    errors.Add(item: $"{path} declares source=uniformRange beside 'weighted', which belongs to source=weightedNumeric.");
                }

                if ((generator.RangeMin is not { } rangeMin) || (generator.RangeMax is not { } rangeMax)) {
                    errors.Add(item: $"{path} declares source=uniformRange without both rangeMin and rangeMax — a range is authored as a pair or not at all.");

                    return;
                }

                if ((rangeMin < WorldGeneratorCapacity.MinRangeBound) || (rangeMin > WorldGeneratorCapacity.MaxRangeBound)) {
                    errors.Add(item: $"{path}.rangeMin {rangeMin} must be between {WorldGeneratorCapacity.MinRangeBound} and {WorldGeneratorCapacity.MaxRangeBound}.");
                }

                if ((rangeMax < WorldGeneratorCapacity.MinRangeBound) || (rangeMax > WorldGeneratorCapacity.MaxRangeBound)) {
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

                for (var index = 0; (index < outcomes.Count); index++) {
                    if (outcomes[index] is null) {
                        errors.Add(item: $"{path}.weighted[{index}] is required.");

                        continue;
                    }

                    anyOutcomeWeight |= (outcomes[index].Weight != 0UL);
                }

                if (!anyOutcomeWeight) {
                    errors.Add(item: $"{path}.weighted declares no non-zero weight — a table that can pick nothing is a stall.");
                }

                return;
            case WorldGeneratorSource.StreamDraw:
                if (declaresMarkovFields || declaresRangeFields || declaresWeighted) {
                    errors.Add(item: $"{path} declares source=streamDraw beside start/contexts/rangeMin/rangeMax/weighted — a stream draw reads none of them.");
                }

                return;
            default:
                return;
        }
    }

    /// <summary>Describes the authored spelling of a source shape, for a refusal message.</summary>
    private static string DescribeGeneratorSource(WorldGeneratorSource source) =>
        (char.ToLowerInvariant(c: source.ToString()[0]) + source.ToString()[1..]);

    private static void ValidateMarkovSource(WorldGenerator generator, string path, List<string> errors) {
        if (generator.Contexts is not { Count: > 0 } contexts) {
            errors.Add(item: $"{path}.contexts must declare at least one context for source=markov.");

            return;
        }

        if (contexts.Count > WorldGeneratorCapacity.MaxContexts) {
            errors.Add(item: $"{path}.contexts count {contexts.Count} exceeds the maximum of {WorldGeneratorCapacity.MaxContexts}.");
        }

        if ((generator.Bound < 1) || (generator.Bound > WorldGeneratorCapacity.MaxEmissionBound)) {
            errors.Add(item: $"{path}.bound {generator.Bound} must be between 1 and {WorldGeneratorCapacity.MaxEmissionBound}.");
        }

        if (!Enum.IsDefined(value: generator.Mode)) {
            errors.Add(item: $"{path}.mode '{generator.Mode}' is not a defined WorldGeneratorMode.");
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

            if (context.Key.Value.StartsWith(value: WorldStateRow.ReservedNamePrefix, comparisonType: StringComparison.Ordinal)) {
                errors.Add(item: $"{contextPath}.key '{context.Key}' starts with the reserved prefix '{WorldStateRow.ReservedNamePrefix}'.");
            }

            var alternatives = (context.Alternatives ?? []);

            if (alternatives.Count > WorldGeneratorCapacity.MaxAlternativesPerContext) {
                errors.Add(item: $"{contextPath}.alternatives count {alternatives.Count} exceeds the maximum of {WorldGeneratorCapacity.MaxAlternativesPerContext} (one deck-mask bit per alternative).");
            }

            var anyWeight = false;

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

                anyWeight |= (entry.Weight != 0UL);
            }

            if ((alternatives.Count > 0) && !anyWeight) {
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
                if ((entry is not null) && !keys.Contains(item: entry.Next)) {
                    errors.Add(item: $"{path}.contexts[{index}] alternative '{entry.Token}' names next context '{entry.Next}', which is not declared.");
                }
            }
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
        } else if (bootOnly && (draw.Timing != WorldDrawTiming.Boot)) {
            errors.Add(item: $"{path}.timing={draw.Timing.ToString().ToLowerInvariant()} — this is a BOOT-ONLY document field, read once at composition; nothing could observe a later redraw, so only timing=boot is admissible here.");
        }

        if (!WorldGeneratorEngine.TryResolveSource(generators: generators, draw: draw, generator: out var generator, reason: out var resolveReason)) {
            errors.Add(item: $"{path} {resolveReason}.");

            return;
        }

        // An INLINE source is shaped here (a declared one was already shaped by ValidateGenerators) — the identical
        // rules either way, so inlining is sugar and never a second, laxer door.
        if (draw.Generator is not null) {
            ValidateSource(generator: generator, path: $"{path}.generator", errors: errors);
        }

        // The ONE kind predicate, shared with every firing door.
        if (!WorldGeneratorEngine.TryCheckTargetKind(source: generator.Source, targetKind: targetKind, reason: out var kindReason)) {
            errors.Add(item: $"{path} {kindReason}.");

            return;
        }

        // A dealing source at a settle-and-clear boot site declares state across draws that this site can never have:
        // it draws once and its facet is erased, so the deck could not survive to be dealt from again.
        if (bootOnly && (generator.Mode != WorldGeneratorMode.WithReplacement)) {
            errors.Add(item: $"{path} draws from a source declaring mode={generator.Mode.ToString().ToLowerInvariant()} — a boot-only site draws once and its facet is cleared, so a deck has no second draw to deal into.");
        }

        if (WorldGeneratorEngine.WritesText(source: generator.Source)) {
            return;
        }

        ValidateSourceDomain(generator: generator, domainLow: domainLow, domainHigh: domainHigh, path: path, errors: errors);
    }

    /// <summary>Narrows a numeric source's own declared band against the site's admissible domain — see
    /// <see cref="ValidateDrawSite"/>'s remarks for why this is an authoring refusal rather than a boot-time
    /// one.</summary>
    private static void ValidateSourceDomain(WorldGenerator generator, long domainLow, long domainHigh, string path, List<string> errors) {
        switch (generator.Source) {
            case WorldGeneratorSource.UniformRange:
                if ((generator.RangeMin is { } rangeMin) && (generator.RangeMax is { } rangeMax) && ((rangeMin < domainLow) || (rangeMax > domainHigh))) {
                    errors.Add(item: $"{path} draws {rangeMin}..{rangeMax}, which is outside the site's admissible domain {domainLow}..{domainHigh}.");
                }

                break;
            case WorldGeneratorSource.WeightedNumeric:
                foreach (var outcome in (generator.Weighted ?? [])) {
                    if ((outcome is not null) && ((outcome.Value < domainLow) || (outcome.Value > domainHigh))) {
                        errors.Add(item: $"{path} draws outcome {outcome.Value}, which is outside the site's admissible domain {domainLow}..{domainHigh}.");
                    }
                }

                break;
            case WorldGeneratorSource.StreamDraw:
                // A raw draw's band is the generator's own and nothing narrows it, so a site that cannot hold the
                // whole 32-bit band is refused HERE rather than by whatever it happened to roll.
                if ((domainLow > 0L) || (domainHigh < uint.MaxValue)) {
                    errors.Add(item: $"{path} draws source=streamDraw, whose raw band 0..{uint.MaxValue} is outside the site's admissible domain {domainLow}..{domainHigh} — author a uniformRange or weightedNumeric source inside the site's range.");
                }

                break;
            default:
                break;
        }
    }

    /// <summary>Validates a state row's authored <see cref="WorldDraw"/> site — its own shape rules, then the shared
    /// site rule with the row's own envelope as the admissible domain.</summary>
    private static void ValidateDraw(WorldStateRow row, IReadOnlyList<WorldGeneratorRow>? generators, string path, List<string> errors) {
        if (row.Draw is not { } draw) {
            if (row.DrawCursor != 0L) {
                errors.Add(item: $"{path} ('{row.Name}') declares drawCursor without draw — drawCursor is engine bookkeeping for a draw site alone.");
            }

            if (row.DrawDecks is { Count: > 0 }) {
                errors.Add(item: $"{path} ('{row.Name}') declares drawDecks without draw — drawDecks is engine bookkeeping for a draw site alone.");
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
            domainLow = Math.Max(val1: domainLow, val2: 0L);
        }

        if (row.Min is { } declaredMinimum) {
            domainLow = Math.Max(val1: domainLow, val2: declaredMinimum);
        }

        if (row.Max is { } declaredMaximum) {
            domainHigh = Math.Min(val1: domainHigh, val2: declaredMaximum);
        }

        ValidateDrawSite(draw: draw, generators: generators, targetKind: row.Kind, bootOnly: false, domainLow: domainLow, domainHigh: domainHigh, path: $"{path}.draw", errors: errors);
    }

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
            errors.Add(item: $"{path} ('{row.Name}') declares advance on a {DescribeKind(kind: row.Kind)} row — only int/fixed rows accumulate.");
        }

        if (advance.RateDenominator <= 0) {
            errors.Add(item: $"{path}.advance.rateDenominator {advance.RateDenominator} must be positive.");
        }

        if (advance.EpochTick < 0) {
            errors.Add(item: $"{path}.advance.epochTick {advance.EpochTick} must be non-negative.");
        }

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
            errors.Add(item: $"{cellPath} ('{row.Name}'.'{cell.Key}') declares advance on a {DescribeKind(kind: row.Kind)} cell — only int/fixed cells accumulate.");
        }

        if (advance.RateDenominator <= 0) {
            errors.Add(item: $"{cellPath}.advance.rateDenominator {advance.RateDenominator} must be positive.");
        }

        if (advance.EpochTick < 0) {
            errors.Add(item: $"{cellPath}.advance.epochTick {advance.EpochTick} must be non-negative.");
        }
    }

    // Fixed-kind values speak DECIMAL in refusal text — never the raw Q48.16 bit pattern — matching the document
    // JSON, console verb, and read-back conventions the same value crosses.
    private static string DescribeValue(CellKind kind, long raw) =>
        ((kind == CellKind.Fixed) ? FixedQ4816.FromRawBits(value: raw).ToString() : raw.ToString(provider: CultureInfo.InvariantCulture));

    private static string DescribeKind(CellKind kind) => kind.ToString().ToLowerInvariant();

    // The editor/authoring policy row: every field finite/positive with a sane ceiling. The BOOT-CONSUMED
    // headroom fields are additionally capped against the engine's own limits — see
    // WorldAuthoringDefaults' remarks for which fields are boot-consumed vs. live-consumed — so a bad authored value
    // can never reach a boot's frozen render-envelope probe (a live-consumed field's bad value is caught the same
    // way, on every mutation, since the validator re-runs on every composed candidate).
    private static void ValidateAuthoring(WorldAuthoringDefaults authoring, List<string> errors) {
        RequireIntRange(value: authoring.AuthoringHeadroomScreens, min: 0, max: SdfProgramBuilder.MaxScreenSurfaces, name: "authoring.authoringHeadroomScreens", errors: errors);
        RequireIntRange(value: authoring.AuthoringHeadroomPlacements, min: 0, max: 256, name: "authoring.authoringHeadroomPlacements", errors: errors);
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
        // The derived-face reserve: the slots boot-registered at [DerivedFaceBase, DerivedFaceBase + count). The
        // ceiling is the remaining span in the engine's screen table.
        RequireIntRange(value: authoring.DerivedFaceScreens, min: 0, max: WorldPlacementPolicy.MaxDerivedFaceScreens, name: "authoring.derivedFaceScreens", errors: errors);
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

            var violations = Puck.Forge.Authoring.CreationCanonicalizer.Validate(document: creation.Document);

            if (violations.Count > 0) {
                foreach (var violation in violations) {
                    errors.Add(item: $"{path}.doc.{violation.Path}: {violation.Message}");
                }

                continue;
            }

            // The hash pin: recompute through the ONE pipeline and compare — the only accepted hash is the one the
            // pipeline itself computes over this document's canonical bytes.
            var canonical = Puck.Forge.Authoring.CreationCanonicalizer.Canonicalize(document: creation.Document, source: creation.Id);

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
    // scale envelope, the lattice distribution's positive counts and finite steps, the mirror plane, and the animated-row
    // constraints (static-only facets; the reserved replay-pool ceiling, word-exact). Returns the resolved id set for
    // the anchor-union gate (a WorldAnchor.Placement resolves against it).
    private static HashSet<string> ValidatePlacements(IReadOnlyList<WorldPlacement> placements, WorldDefinition definition, HashSet<string> creationIds, HashSet<string> lookNames, HashSet<string> kitNames, WorldAuthoringDefaults authoring, HashSet<string> patchIds, bool requiresField, HashSet<string> destinationNames, List<string> errors) {
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);
        var creations = definition.Creations;

        if (placements is null) {
            errors.Add(item: "placements is required.");

            return ids;
        }

        // The stamp-pool charge: every row that renders through Client.WorldStampPool's reserved registrations rather
        // than as a static stamp — an ANIMATED row (a framed creation) or an ATTACHED one (rooted on a live body).
        var stampRegistrationCount = 0;
        // The document-global dynamic-instance total (WorldDynamicGeometryCeilings.MaxContributedDynamicInstances):
        // every animated placement's single replay instance plus every inhabited placement's declared body count,
        // summed across the whole document — ValidateInhabit only bounds one row's count to the peer capacity, never
        // totals across rows. The runtime entity table separately caps actual occupancy at population capacity.
        var dynamicInstanceCount = 0;
        var staticPlacementInstanceCount = 0L;
        var solidPlacementColliderCount = 0L;
        // The one face derivation, read for both the per-face portal refusals below and the screen budget after the
        // loop — never a second walk of (placements x declared faces) to answer the same questions.
        var faces = WorldFaceCatalog.For(definition: definition);

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

            if (placement.Distribution is { } distribution) {
                ValidateDistribution(distribution: distribution, path: $"{path}.distribution", spawnPointIds: [], allowDisc: false, allowPoints: false, allowLattice: true, allowZeroDisc: false, errors: errors);
            }

            if (placement.Mirror is { } mirror) {
                if (!IsFinite(value: mirror.Normal) || (mirror.Normal.LengthSquared() <= 0f)) {
                    errors.Add(item: $"{path}.mirror.normal must be finite and nonzero.");
                }

                RequireFinite(value: mirror.Offset, name: $"{path}.mirror.offset", errors: errors);
            }

            // The emission facet binds to the placement root under a distribution (documented on WorldPlacement) — no
            // per-copy constraint to gate; only patch resolution and the shared gain/radius bounds.
            ValidateEmission(emission: placement.Emission, patchIds: patchIds, path: $"{path}.emission", errors: errors);

            if (placement.Solid is { } placementSolid) {
                RequireFinite(value: placementSolid.Margin, name: $"{path}.solid.margin", errors: errors);

                if (!requiresField && (WorldDefinitionRows.FindCreation(creations: creations, id: placement.CreationId) is { } solidCreation)) {
                    var copies = CreationStampLattice.MaterializedCopyCount(
                        pattern: WorldPlacementStamp.PatternFor(placement: placement),
                        mirror: WorldPlacementStamp.MirrorFor(placement: placement),
                        ceiling: (WorldPlacementPolicy.MaxSolidPlacementColliders + 1L)
                    );
                    var contribution = CreationStampLattice.MultiplySaturated(
                        left: copies,
                        right: (solidCreation.Document.Shapes?.Count ?? 0),
                        ceiling: (WorldPlacementPolicy.MaxSolidPlacementColliders + 1L)
                    );
                    var previousColliderCount = solidPlacementColliderCount;
                    solidPlacementColliderCount = Math.Min(
                        val1: (WorldPlacementPolicy.MaxSolidPlacementColliders + 1L),
                        val2: (solidPlacementColliderCount + contribution)
                    );

                    if ((previousColliderCount <= WorldPlacementPolicy.MaxSolidPlacementColliders) && (solidPlacementColliderCount > WorldPlacementPolicy.MaxSolidPlacementColliders)) {
                        errors.Add(item: $"{path}.solid expands the document past the {WorldPlacementPolicy.MaxSolidPlacementColliders}-collider analytic-placement ceiling; reduce lattice counts, mirror copies, or creation shapes.");
                    }

                }
            }

            // The animated-row constraints: a placement of a framed creation replays through the reserved dynamic
            // pool — single copy only (pattern/mirror are static-stamp facets), and at most the reserved pool count.
            var isAnimated = (WorldDefinitionRows.FindCreation(creations: creations, id: placement.CreationId) is { Document.Frames.Count: > 0 });
            if (isAnimated) {
                stampRegistrationCount++;
                dynamicInstanceCount++;

                if ((placement.Distribution is not null) || (placement.Mirror is not null)) {
                    errors.Add(item: $"{path} is ANIMATED (its creation carries timeline frames) — distribution/mirror facets are static-stamp-only.");
                }
            } else if (placement.Attach is not null) {
                // An ATTACHED row renders through the SAME reserved stamp pool (rooted on its body instead of a static
                // transform — Client.WorldStampPool), so it charges the same ceiling. Counted in the `else` because an
                // attached row of a FRAMED creation is one registration, not two.
                stampRegistrationCount++;
                dynamicInstanceCount++;
            }

            // The INHABIT facet: a placement's binding to live population bodies (Arc 7). Resolve its kit, gate its
            // source/look/count, and reject the lattice facets (one body cannot represent a placement distribution).
            if (placement.Inhabit is { } inhabit) {
                ValidateInhabit(inhabit: inhabit, placement: placement, path: $"{path}.inhabit", definition: definition, kitNames: kitNames, lookNames: lookNames, errors: errors);

                if ((placement.Distribution is not null) || (placement.Mirror is not null)) {
                    errors.Add(item: $"{path} INHABITS — placement distribution/mirror facets are incompatible with a live body.");
                }

                if (inhabit.Count > 0) {
                    dynamicInstanceCount += inhabit.Count;
                }
            }

            if (!isAnimated && (placement.Inhabit is null)) {
                var contribution = CreationStampLattice.MaterializedCopyCount(
                    pattern: WorldPlacementStamp.PatternFor(placement: placement),
                    mirror: WorldPlacementStamp.MirrorFor(placement: placement),
                    ceiling: (SdfProgramBuilder.MaxInstances + 1L)
                );
                var previousInstanceCount = staticPlacementInstanceCount;
                staticPlacementInstanceCount = Math.Min(
                    val1: (SdfProgramBuilder.MaxInstances + 1L),
                    val2: (staticPlacementInstanceCount + contribution)
                );

                if ((previousInstanceCount <= SdfProgramBuilder.MaxInstances) && (staticPlacementInstanceCount > SdfProgramBuilder.MaxInstances)) {
                    errors.Add(item: $"{path} expands the document past the {SdfProgramBuilder.MaxInstances}-instance engine ceiling; reduce lattice counts or mirror copies.");
                }
            }

            // The per-instance FACE overrides: each names a declared creation face, no duplicates. The View source's
            // camera name is resolved LENIENTLY (a derived creation-camera name is unknown to the document validator; the
            // binder lights an unresolved feed with its no-signal card, never a hard reject).
            ValidateFaceSources(faceSources: placement.FaceSources, placement: placement, creations: creations, faces: faces, destinationNames: destinationNames, path: $"{path}.faceSources", errors: errors);

            // The REGION facet: a named sensing volume (see WorldPlacementRegion's own doc). The name IS the
            // placement's own id, so no separate uniqueness check is owed — the id duplication check above already
            // covers it.
            if (placement.Region is { } region) {
                if (!float.IsFinite(f: region.Radius) || (region.Radius <= 0f)) {
                    errors.Add(item: $"{path}.region.radius {region.Radius} must be finite and positive.");
                }
            }

            // The ATTACH facet binds the row's resolved pose to a live population body (see WorldPlacementAttach).
            // BodyIndex uses the same 0-based entity indexing as WorldAnchor.Entity and the body:<n> grant subject —
            // never the 1-based player.* seat number. Region, Solid (analytic contact provider), and Emission all
            // read the resolved dynamic pose (WorldEventFeed.CollectRegions, WorldColliderSet.RefreshAttached,
            // WorldStampPool.TryShapePosition/RootPose), so an equipped item's aura/hitbox/voice tracks the carrier.
            // Distribution/mirror and inhabit stay refused (a row cannot both spawn its own bodies and ride
            // another's); FaceSources stays permitted (a content selector, not a transform).
            if (placement.Attach is { } attach) {
                if ((attach.BodyIndex < 0) || (attach.BodyIndex >= definition.Population.Capacity)) {
                    errors.Add(item: $"{path}.attach.bodyIndex {attach.BodyIndex} is outside 0..{(definition.Population.Capacity - 1)} for the authored population capacity (0-based entity index — body:1 is player.* seat 2, not body index 1).");
                }

                if (!IsFinite(value: attach.LocalOffset)) {
                    errors.Add(item: $"{path}.attach.localOffset must contain finite coordinates.");
                }

                RequireFinite(value: attach.LocalYawDegrees, name: $"{path}.attach.localYawDegrees", errors: errors);

                if ((placement.Distribution is not null) || (placement.Mirror is not null)) {
                    errors.Add(item: $"{path} ATTACHES — placement distribution/mirror facets are static-stamp-only (the same rule an animated or inhabited row already enforces), incompatible with a live-following root.");
                }

                // Only the FIELD contact provider still refuses solid+attach: it compiles every solid row's geometry
                // once into ONE SdfProgram (WorldSolidField.TryBuild) and is never rebuilt per tick, so an attached
                // row's collider would silently stop tracking the body. The analytic provider's WorldColliderSet
                // recomputes an attached row's colliders every tick instead (RefreshAttached) — see requiresField's
                // own derivation (WorldContactSelection.RequiresField reads collision.requirements).
                if (requiresField && (placement.Solid is not null)) {
                    errors.Add(item: $"{path} ATTACHES under the FIELD contact provider (collision.requirements is non-empty) — WorldSolidField compiles every solid row's geometry once and never rebuilds it per tick; the analytic provider tracks an attached solid instead.");
                }

                if (placement.Inhabit is not null) {
                    errors.Add(item: $"{path} ATTACHES — a row cannot both INHABIT (spawn its own driven bodies) and ATTACH (ride another body's pose).");
                }
            }
        }

        if (stampRegistrationCount > WorldPlacementPolicy.MaxStampRegistrations) {
            errors.Add(item: $"{stampRegistrationCount} animated + attached placements exceed the {WorldPlacementPolicy.MaxStampRegistrations}-slot replay pool.");
        }

        // The document-global dynamic-instance ceiling (WorldDynamicGeometryCeilings.MaxContributedDynamicInstances,
        // the measured CPU/grid bound — see its remarks): the sum above, not any single row, is what pays the
        // per-frame instance-grid rebuild cost, so it is gated once here rather than per row.
        if (dynamicInstanceCount > WorldDynamicGeometryCeilings.MaxContributedDynamicInstances) {
            errors.Add(item: $"{dynamicInstanceCount} contributed dynamic instances (animated placements + inhabited placement bodies) exceed the {WorldDynamicGeometryCeilings.MaxContributedDynamicInstances}-instance document-global dynamic-geometry ceiling.");
        }

        // The derived-face SCREEN budget, refused here because it is provable from the document alone: a face whose
        // resolved source renders nothing claims no slot, so this counts only the faces that actually ask for one.
        // Overflow that reaches the running engine can only darken a face, never remove a door — but a document that
        // over-claims statically is an authoring error, and naming it at validation beats discovering it as a dark
        // screen.
        if (faces.ClaimingFaceCount > faces.SlotCapacity) {
            errors.Add(item: $"{faces.ClaimingFaceCount} derived creation face(s) resolve to a source that renders something, but authoring.derivedFaceScreens reserves only {faces.SlotCapacity} slot(s); raise the reservation or give a face a 'none' source.");
        }

        // No census-fit rule: networkPlayers is a remote admission CAP, not a static reservation an
        // inhabitant competes with. An inhabitant is a peer that JOINS a free slot; total occupancy is bounded by the
        // entity table itself, and a genuinely full table is rejected loudly at JOIN time (a runtime fact the static
        // document validator cannot know), never pre-rejected here.
        return ids;
    }

    // The INHABIT facet: the kit must resolve (its explicit kit name OR the creation's Locomotion token as a kit name),
    // a producer source must resolve on that kit, a named look must be declared, and count/distribution are bounded.
    private static void ValidateInhabit(WorldPlacementInhabit inhabit, WorldPlacement placement, string path, WorldDefinition definition, HashSet<string> kitNames, HashSet<string> lookNames, List<string> errors) {
        var resolvedKit = (inhabit.Kit ?? ResolveLocomotionKit(definition: definition, creationId: placement.CreationId));

        if ((resolvedKit is null) || !kitNames.Contains(item: resolvedKit)) {
            errors.Add(item: $"{path} names no kit; the world declares: {string.Join(separator: ", ", values: kitNames)}.");
        } else if (inhabit.Source.IsProducer && (inhabit.Source.ProducerName is { } producer) &&
            !definition.Kits.First(kit => string.Equals(a: kit.Name, b: resolvedKit, comparisonType: StringComparison.Ordinal)).Producers.ContainsKey(key: producer)) {
            errors.Add(item: $"{path}.source names producer '{producer}', but kit '{resolvedKit}' declares no parameters for it.");
        }

        if (!inhabit.Source.IsLive && !inhabit.Source.IsIdle && !inhabit.Source.IsProducer) {
            errors.Add(item: $"{path}.source '{inhabit.Source}' is not a defined IntentSource.");
        }

        if ((inhabit.Look is { Length: > 0 } lookName) && !lookNames.Contains(item: lookName)) {
            errors.Add(item: $"{path}.look '{lookName}' names no look row.");
        }

        var peerCapacity = Math.Max(val1: 0, val2: (definition.Population.Capacity - WorldPopulationLimits.LocalSeatCount));
        if ((inhabit.Count < 1) || (inhabit.Count > peerCapacity)) {
            errors.Add(item: $"{path}.count {inhabit.Count} is outside 1..{peerCapacity} for the authored population capacity.");
        }

        ValidateDistribution(distribution: inhabit.Distribution!, path: $"{path}.distribution", spawnPointIds: [], allowDisc: true, allowPoints: false, allowLattice: false, allowZeroDisc: true, errors: errors);
    }

    // The creation's Locomotion token, resolved as a kit name (the creator's rule; null when the creation/token is absent).
    private static string? ResolveLocomotionKit(WorldDefinition definition, string creationId) {
        foreach (var creation in definition.Creations) {
            if ((creation is not null) && string.Equals(a: creation.Id, b: creationId, comparisonType: StringComparison.Ordinal)) {
                return creation.Document.Behavior?.Locomotion;
            }
        }

        return null;
    }

    // The per-instance face overrides: each names a declared creation face, no duplicate face names.
    private static void ValidateFaceSources(IReadOnlyList<WorldPlacementFace>? faceSources, WorldPlacement placement, IReadOnlyList<WorldCreation> creations, WorldFaceCatalog faces, HashSet<string> destinationNames, string path, List<string> errors) {
        if (faceSources is not { Count: > 0 } sources) {
            return;
        }

        var creation = WorldDefinitionRows.FindCreation(creations: creations, id: placement.CreationId);
        var faceNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var face in (creation?.Document.Behavior?.Faces ?? [])) {
            _ = faceNames.Add(item: face.Name);
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        string? portalFaceName = null;

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
            } else if (source.Source is WorldScreenSource.Session session) {
                ValidateSessionSource(session: session, destinationNames: destinationNames, portal: source.Portal, path: facePath, errors: errors);
            }

            if (source.Portal is { } portal) {
                ValidatePortal(portal: portal, destinationNames: destinationNames, path: $"{facePath}.portal", errors: errors);

                // A door is geometry the trigger scan derives ONCE per revision from the row's authored transform
                // (WorldFaceCatalog). A row whose resolved pose moves every tick has no such derivation, so a portal
                // facet on one is refused rather than silently triggering at a stale place.
                if (placement.Attach is not null) {
                    errors.Add(item: $"{facePath}.portal sits on a placement that ATTACHES to a live body — a portal's frame is derived once per document revision from the row's own authored transform, which an attached row does not have; move the door onto a static placement.");
                } else if (placement.Inhabit is not null) {
                    errors.Add(item: $"{facePath}.portal sits on an INHABITED placement — its stamp rides a live body's pose rather than the row's authored transform, so the door's frame would be stale every tick; move the door onto a static placement.");
                } else if (creation is { Document.Frames.Count: > 0 }) {
                    errors.Add(item: $"{facePath}.portal sits on an ANIMATED placement (creation '{placement.CreationId}' carries timeline frames) — a replaying stamp's surface moves on the render clock while the derived frame does not; move the door onto a static placement.");
                }

                // The derived face itself: its shape kind must map onto a region arm (WorldFaceApertureKind), and its
                // frame must be yaw-only. A face may be DRAWN on any primitive at any orientation; a DOOR is narrower.
                if (faces.TryFind(placementId: placement.Id, faceName: source.Face, out var portalRow)) {
                    if (portalRow.ShapeType is not { } shapeType) {
                        errors.Add(item: $"{facePath}.portal names face '{source.Face}', which declares no concrete shape (shapeId {(portalRow.ShapeId?.ToString(provider: CultureInfo.InvariantCulture) ?? "null")}) — a door needs a surface to open, so it has no aperture mapping.");
                    } else if (portalRow.Aperture == WorldFaceApertureKind.None) {
                        errors.Add(item: $"{facePath}.portal names face '{source.Face}', whose shape is a {shapeType} — only Box maps onto a walkable aperture today; a curved or unbounded face has no aperture mapping.");
                    }

                    if (!portalRow.Frame.IsYawOnly) {
                        errors.Add(item: $"{facePath}.portal names face '{source.Face}', whose derived frame carries pitch or roll (its up axis is not world +Y once the placement yaw and the shape rotation compose) — the mapped-arrival isometry is a rotation about world up only and cannot map such a frame; level the shape's rotation or drop the portal facet.");
                    }
                }

                // One portal face per placement is refused rather than assumed: the coalesced group key carries
                // (placement, face), so a party straddling two portal faces of one placement would split into two
                // transfers, breaking whole-party atomicity. The destination for a two-sided door is one transfer
                // whose members map through their own entry face (per-member source frames), which PendingTransfer
                // cannot carry yet.
                if (portalFaceName is { } firstPortalFace) {
                    errors.Add(item: $"{facePath}.portal is placement '{placement.Id}'s second portal face ('{firstPortalFace}' already carries one) — a party spanning two faces of one placement would split its transfer atomicity; one portal face per placement until per-member source frames land.");
                } else {
                    portalFaceName = source.Face;
                }
            }
        }
    }

    // The PORTAL facet: destination must name an existing destinations row. Travel/Arrival enum shape is already
    // refused at strict parse. This pass owns what parse cannot: that the named destination row exists, and that
    // arrival/counterpart cohere (mapped requires a counterpart, a counterpart requires mapped, and its shape
    // parses). A counterpart's cross-document existence is NOT checked here — the named document is not resolved at
    // boot; Puck.World.WorldInstanceHost resolves it against the destination's delivered definition at transfer
    // time (see WorldPortalCounterpart).
    private static void ValidatePortal(WorldPlacementPortal portal, HashSet<string> destinationNames, string path, List<string> errors) {
        if (string.IsNullOrWhiteSpace(value: portal.Destination) || !destinationNames.Contains(item: portal.Destination)) {
            errors.Add(item: (destinationNames.Count > 0)
                ? $"{path}.destination '{portal.Destination}' names no destinations row; the world declares: {string.Join(separator: ", ", values: destinationNames)}."
                : $"{path}.destination '{portal.Destination}' names no destinations row; the world declares none.");
        }

        if (portal.Arrival == WorldPortalArrival.Mapped) {
            if (string.IsNullOrWhiteSpace(value: portal.Counterpart)) {
                errors.Add(item: $"{path}.arrival 'mapped' requires {path}.counterpart (the destination document's border placementId/face) — none was authored.");
            } else if (!WorldPortalCounterpart.TryParse(counterpart: portal.Counterpart, placementId: out _, face: out _)) {
                errors.Add(item: $"{path}.counterpart '{portal.Counterpart}' is malformed — expected '<placementId>/<face>'.");
            }
        } else if (!string.IsNullOrWhiteSpace(value: portal.Counterpart)) {
            errors.Add(item: $"{path}.counterpart '{portal.Counterpart}' is authored but {path}.arrival is not 'mapped' — a counterpart names a mapped arrival's destination frame only.");
        }

        if (portal.Capacity is { } capacity) {
            RequireIntRange(value: capacity, min: 1, max: WorldPopulationLimits.CapacityCeiling, name: $"{path}.capacity", errors: errors);
        }
    }

    private static void ValidatePortals(WorldPortalsSection? portals, List<string> errors) {
        if (portals is null) {
            return;
        }

        var defaults = portals.PortalDefaults;

        if (!Enum.IsDefined(value: defaults.Travel)) {
            errors.Add(item: $"portals.portalDefaults.travel '{defaults.Travel}' is not a defined WorldPortalTravel.");
        }

        if (!double.IsFinite(d: defaults.HoldSeconds) || (defaults.HoldSeconds <= 0.0)) {
            errors.Add(item: $"portals.portalDefaults.holdSeconds {defaults.HoldSeconds} must be finite and positive.");
        } else if (!FixedTickConversion.TryDurationEngineTicksExact(seconds: (decimal)defaults.HoldSeconds, ticks: out _)) {
            errors.Add(item: $"portals.portalDefaults.holdSeconds {defaults.HoldSeconds} does not convert to an exact whole tick across the {FixedTickConversion.TicksPerSecond} engine-tick bridge.");
        }

        if (!Enum.IsDefined(value: defaults.Full)) {
            errors.Add(item: $"portals.portalDefaults.full '{defaults.Full}' is not a defined WorldTransferFullPolicy.");
        }
    }

    private static void ValidateAdjacencies(WorldDefinition definition, HashSet<string> destinationNames, IWorldNeighbourResolver? neighbours, bool proveNeighbours, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var resolutions = new Dictionary<string, WorldNeighbourResolution>(comparer: StringComparer.Ordinal);
        var channels = WorldChannelTable.Compile(channels: definition.Channels);

        foreach (var adjacency in (definition.Adjacencies ?? [])) {
            if (adjacency is null) {
                errors.Add(item: "adjacencies contains a null row.");
                continue;
            }

            var path = $"adjacencies[{adjacency.Name}]";
            if (!names.Add(item: adjacency.Name.Value)) {
                errors.Add(item: $"{path}.name is duplicated.");
            }
            if (!destinationNames.Contains(item: adjacency.Destination) || WorldDefinitionRows.FindDestination(destinations: definition.Destinations, name: adjacency.Destination) is not { } destination) {
                errors.Add(item: $"{path}.destination '{adjacency.Destination}' names no destinations row.");
                continue;
            }
            if ((destination.Scope != WorldDestinationScope.Global) || (destination.Durability != WorldDestinationDurability.Persisted)) {
                errors.Add(item: $"{path}.destination '{destination.Name}' must be global and persisted — adjacency names one stable neighbouring authority.");
            }
            if (!WorldSafeName.TryParse(candidate: adjacency.Counterpart, name: out _, reason: out var counterpartReason)) {
                errors.Add(item: $"{path}.counterpart '{adjacency.Counterpart}' is invalid — {counterpartReason}.");
            }
            if ((adjacency.Boundary is not { } boundary) || !IsFinite(value: boundary.Center) ||
                !float.IsFinite(f: boundary.OutwardYawDegrees) ||
                !float.IsFinite(f: boundary.Width) || (boundary.Width <= 0f) ||
                !float.IsFinite(f: boundary.Height) || (boundary.Height <= 0f)) {
                errors.Add(item: $"{path}.boundary must have a finite center/yaw and positive finite width/height.");
                continue;
            }
            if (!Enum.IsDefined(value: adjacency.Unavailable)) {
                errors.Add(item: $"{path}.unavailable '{adjacency.Unavailable}' is not defined.");
            }
            if ((adjacency.OnUnavailable is { } onUnavailable) &&
                (string.IsNullOrWhiteSpace(value: onUnavailable) || !channels.TryGetOrdinal(name: onUnavailable, ordinal: out _))) {
                errors.Add(item: $"{path}.onUnavailable '{onUnavailable}' names no declared channel.");
            }
            if (!proveNeighbours) {
                continue;
            }
            if (WorldDefinitionRows.FindReference(references: definition.References, name: destination.Reference) is not { } reference) {
                continue;
            }
            if (neighbours is null) {
                errors.Add(item: $"{path} cannot be proven because no neighbour resolver was supplied.");
                continue;
            }
            if (!resolutions.TryGetValue(key: reference.Document, value: out var resolution)) {
                resolution = neighbours.Resolve(document: reference.Document);
                resolutions[reference.Document] = resolution;
            }
            if (resolution.Kind != WorldNeighbourResolutionKind.Resolved) {
                errors.Add(item: $"{path} cannot reach neighbour '{reference.Document}' — {resolution.Reason}.");
                continue;
            }

            if (resolution.Definition is not { } neighbour) {
                errors.Add(item: $"{path} resolver returned no neighbour document for '{reference.Document}'.");
                continue;
            }
            if (WorldDefinitionRows.FindAdjacency(adjacencies: neighbour.Adjacencies, name: adjacency.Counterpart) is not { } counterpart) {
                errors.Add(item: $"{path}.counterpart '{adjacency.Counterpart}' names no adjacency in neighbour '{reference.Document}'.");
                continue;
            }
            if (!string.Equals(a: counterpart.Counterpart, b: adjacency.Name.Value, comparisonType: StringComparison.Ordinal)) {
                errors.Add(item: $"{path} is not reciprocal — neighbour '{reference.Document}'/'{counterpart.Name}' points to '{counterpart.Counterpart}', not '{adjacency.Name}'.");
            }

            var localFrame = boundary.CompileFrame();
            if (counterpart.Boundary is not { } counterpartBoundary) {
                errors.Add(item: $"{path}.counterpart '{adjacency.Counterpart}' has no boundary.");
                continue;
            }
            var neighbourFrame = counterpartBoundary.CompileFrame();
            if ((localFrame.HalfWidth != neighbourFrame.HalfWidth) || (localFrame.HalfHeight != neighbourFrame.HalfHeight)) {
                errors.Add(item: $"{path}.boundary is {(double)localFrame.HalfWidth * 2:0.#####}x{(double)localFrame.HalfHeight * 2:0.#####}, but neighbour '{reference.Document}'/'{counterpart.Name}' is {(double)neighbourFrame.HalfWidth * 2:0.#####}x{(double)neighbourFrame.HalfHeight * 2:0.#####}.");
            }
            if (!WorldAdjacencyPolicy.TryDeriveOverlap(local: definition, neighbour: neighbour, depth: out _, reason: out var overlapReason)) {
                errors.Add(item: $"{path} overlap cannot be derived — {overlapReason}.");
            }
        }

        if (proveNeighbours && (neighbours is not null)) {
            ValidateDerivedAdjacencyCorners(definition: definition, neighbours: neighbours, resolutions: resolutions, errors: errors);
        }
    }

    private static void ValidateDerivedAdjacencyCorners(
        WorldDefinition definition,
        IWorldNeighbourResolver neighbours,
        Dictionary<string, WorldNeighbourResolution> resolutions,
        List<string> errors
    ) {
        var rows = (definition.Adjacencies ?? []).Where(predicate: static row => row is not null).ToArray();
        for (var leftIndex = 0; leftIndex < rows.Length; leftIndex++) {
            var left = rows[leftIndex]!;
            if (WorldAdjacencyPolicy.DestinationDocument(definition: definition, destinationName: left.Destination) is not { } leftDocument ||
                !TryResolved(document: leftDocument, neighbours: neighbours, resolutions: resolutions, definition: out var leftDefinition)) {
                continue;
            }

            for (var rightIndex = (leftIndex + 1); rightIndex < rows.Length; rightIndex++) {
                var right = rows[rightIndex]!;
                if (WorldAdjacencyPolicy.DestinationDocument(definition: definition, destinationName: right.Destination) is not { } rightDocument ||
                    !TryResolved(document: rightDocument, neighbours: neighbours, resolutions: resolutions, definition: out var rightDefinition) ||
                    !WorldAdjacencyPolicy.TrySharedCorner(
                        left: leftDefinition!,
                        leftBack: left.Counterpart,
                        right: rightDefinition!,
                        rightBack: right.Counterpart,
                        document: out var cornerDocument,
                        leftEdge: out var leftEdge,
                        rightEdge: out var rightEdge)) {
                    continue;
                }

                var path = $"adjacencies[{left.Name}]+adjacencies[{right.Name}]";
                if (WorldAdjacencyPolicy.GlobalDestinationForDocument(definition: definition, document: cornerDocument) is null) {
                    errors.Add(item: $"{path} derives corner neighbour '{cornerDocument}', but this document declares no global persisted destination/reference for that authority.");
                    continue;
                }

                if (!TryResolved(document: cornerDocument, neighbours: neighbours, resolutions: resolutions, definition: out var cornerDefinition)) {
                    var resolution = resolutions[cornerDocument];
                    errors.Add(item: $"{path} cannot reach derived corner neighbour '{cornerDocument}' — {resolution.Reason}.");
                    continue;
                }

                ValidateCornerPath(path: path, viaDocument: leftDocument, via: leftDefinition!, viaEdge: leftEdge!, cornerDocument: cornerDocument, corner: cornerDefinition!, errors: errors);
                ValidateCornerPath(path: path, viaDocument: rightDocument, via: rightDefinition!, viaEdge: rightEdge!, cornerDocument: cornerDocument, corner: cornerDefinition!, errors: errors);
                ValidateCornerDiamond(
                    path: path,
                    leftSourceEdge: left,
                    left: leftDefinition!,
                    leftCornerEdge: leftEdge!,
                    rightSourceEdge: right,
                    right: rightDefinition!,
                    rightCornerEdge: rightEdge!,
                    cornerDocument: cornerDocument,
                    corner: cornerDefinition!,
                    errors: errors);
            }
        }
    }

    private static void ValidateCornerDiamond(
        string path,
        WorldAdjacency leftSourceEdge,
        WorldDefinition left,
        WorldAdjacency leftCornerEdge,
        WorldAdjacency rightSourceEdge,
        WorldDefinition right,
        WorldAdjacency rightCornerEdge,
        string cornerDocument,
        WorldDefinition corner,
        List<string> errors
    ) {
        if ((WorldDefinitionRows.FindAdjacency(adjacencies: left.Adjacencies, name: leftSourceEdge.Counterpart) is not { } leftBack) ||
            (WorldDefinitionRows.FindAdjacency(adjacencies: right.Adjacencies, name: rightSourceEdge.Counterpart) is not { } rightBack) ||
            (WorldDefinitionRows.FindAdjacency(adjacencies: corner.Adjacencies, name: leftCornerEdge.Counterpart) is not { } cornerToLeft) ||
            (WorldDefinitionRows.FindAdjacency(adjacencies: corner.Adjacencies, name: rightCornerEdge.Counterpart) is not { } cornerToRight)) {
            return;
        }

        var cornerOrigin = cornerToLeft.Boundary.CompileFrame().Origin;
        var probes = new[] {
            cornerOrigin,
            (cornerOrigin + new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)),
            (cornerOrigin + new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One)),
        };

        foreach (var probe in probes) {
            var viaLeft = MapTwoStages(
                point: probe,
                firstSource: cornerToLeft.Boundary.CompileFrame(),
                firstDestination: leftCornerEdge.Boundary.CompileFrame(),
                secondSource: leftBack.Boundary.CompileFrame(),
                secondDestination: leftSourceEdge.Boundary.CompileFrame());
            var viaRight = MapTwoStages(
                point: probe,
                firstSource: cornerToRight.Boundary.CompileFrame(),
                firstDestination: rightCornerEdge.Boundary.CompileFrame(),
                secondSource: rightBack.Boundary.CompileFrame(),
                secondDestination: rightSourceEdge.Boundary.CompileFrame());

            if (viaLeft != viaRight) {
                errors.Add(item: $"{path} does not close its transform diamond for corner '{cornerDocument}' — the left path maps {probe} to {viaLeft}, while the right path maps it to {viaRight}.");
                return;
            }
        }
    }

    private static FixedVector3 MapTwoStages(FixedVector3 point, WorldFaceFrame firstSource, WorldFaceFrame firstDestination, WorldFaceFrame secondSource, WorldFaceFrame secondDestination) {
        var intermediate = WorldFrameIsometry.MapPoint(point: point, source: in firstSource, destination: in firstDestination);
        return WorldFrameIsometry.MapPoint(point: intermediate, source: in secondSource, destination: in secondDestination);
    }

    private static bool TryResolved(
        string document,
        IWorldNeighbourResolver neighbours,
        Dictionary<string, WorldNeighbourResolution> resolutions,
        out WorldDefinition? definition
    ) {
        if (!resolutions.TryGetValue(key: document, value: out var resolution)) {
            resolution = neighbours.Resolve(document: document);
            resolutions[document] = resolution;
        }

        definition = resolution.Definition;
        return ((resolution.Kind == WorldNeighbourResolutionKind.Resolved) && (definition is not null));
    }

    private static void ValidateCornerPath(string path, string viaDocument, WorldDefinition via, WorldAdjacency viaEdge, string cornerDocument, WorldDefinition corner, List<string> errors) {
        if (WorldDefinitionRows.FindAdjacency(adjacencies: corner.Adjacencies, name: viaEdge.Counterpart) is not { } counterpart) {
            errors.Add(item: $"{path} reaches '{cornerDocument}' through '{viaDocument}'/'{viaEdge.Name}', but corner counterpart '{viaEdge.Counterpart}' does not exist.");
            return;
        }

        if (!string.Equals(a: counterpart.Counterpart, b: viaEdge.Name.Value, comparisonType: StringComparison.Ordinal)) {
            errors.Add(item: $"{path} reaches '{cornerDocument}' through '{viaDocument}'/'{viaEdge.Name}', but corner '{counterpart.Name}' points to '{counterpart.Counterpart}', not '{viaEdge.Name}'.");
        }

        var viaFrame = viaEdge.Boundary.CompileFrame();
        var cornerFrame = counterpart.Boundary.CompileFrame();
        if ((viaFrame.HalfWidth != cornerFrame.HalfWidth) || (viaFrame.HalfHeight != cornerFrame.HalfHeight)) {
            errors.Add(item: $"{path} reaches '{cornerDocument}' through '{viaDocument}'/'{viaEdge.Name}', whose boundary dimensions do not match corner '{counterpart.Name}'.");
        }

        if (!WorldAdjacencyPolicy.TryDeriveOverlap(local: via, neighbour: corner, depth: out _, reason: out var overlapReason)) {
            errors.Add(item: $"{path} derived corner overlap cannot be compiled — {overlapReason}.");
        }
    }

    // The WorldAnchor union: the shared pose-target vocabulary a camera and speaker ride.
    // Entity/EntityPart are index bounded; an EntityPart also requires the authored id its look resolves at runtime.
    // Placement resolves its row and, when ShapeId is present, that the id
    // names a real shape in the referenced placement's creation document, the same rule
    // Puck.Forge.Authoring.CreationCameraDocument enforces.
    private static void ValidateAnchor(WorldAnchor anchor, IReadOnlyList<WorldPlacement> placements, HashSet<string> placementIds, IReadOnlyList<WorldCreation> creations, int populationCapacity, string path, List<string> errors) {
        switch (anchor) {
            case null:
                errors.Add(item: $"{path} is required.");

                break;
            case WorldAnchor.Entity entity:
                if ((entity.Index < 0) || (entity.Index >= populationCapacity)) {
                    errors.Add(item: $"{path}.index {entity.Index} is outside 0..{(populationCapacity - 1)} for the authored population capacity.");
                }

                break;
            case WorldAnchor.EntityPart part:
                if ((part.Index < 0) || (part.Index >= populationCapacity)) {
                    errors.Add(item: $"{path}.index {part.Index} is outside 0..{(populationCapacity - 1)} for the authored population capacity.");
                }

                if (string.IsNullOrWhiteSpace(value: part.PartId)) {
                    errors.Add(item: $"{path}.partId is required.");
                }

                break;
            case WorldAnchor.Placement placement:
                if (string.IsNullOrWhiteSpace(value: placement.PlacementId) || !placementIds.Contains(item: placement.PlacementId)) {
                    errors.Add(item: $"{path}.placementId '{placement.PlacementId}' names no placement row.");

                    break;
                }

                if (placement.ShapeId is { } shapeId) {
                    var row = WorldDefinitionRows.FindPlacement(placements: placements, id: placement.PlacementId);
                    var creation = ((row is null) ? null : WorldDefinitionRows.FindCreation(creations: creations, id: row.CreationId));

                    if ((creation?.Document.Shapes is not { } shapes) || !ShapesContain(shapes: shapes, id: shapeId)) {
                        errors.Add(item: $"{path}.shapeId {shapeId} names no shape in placement '{placement.PlacementId}''s creation.");
                    }
                }

                break;
            case WorldAnchor.Group group:
                if (group.Indices is { } indices) {
                    for (var index = 0; (index < indices.Count); index++) {
                        if ((indices[index] < 0) || (indices[index] >= populationCapacity)) {
                            errors.Add(item: $"{path}.indices[{index}] {indices[index]} is outside 0..{(populationCapacity - 1)} for the authored population capacity.");
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

    // Camera motion, aim, lens, and tracks are presentation-only authoring state.
    private static void ValidateRig(WorldCameraRig rig, string path, List<string> errors) {
        if (rig is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if ((rig.Lens is null) || !float.IsFinite(f: rig.Lens.FieldOfViewRadians) || (rig.Lens.FieldOfViewRadians <= 0f) || (rig.Lens.FieldOfViewRadians >= MathF.PI)) {
            errors.Add(item: $"{path}.lens.fieldOfViewRadians must be finite and between 0 and pi.");
        }

        if (!float.IsFinite(f: rig.SmoothRate) || (rig.SmoothRate < 0f)) {
            errors.Add(item: $"{path}.smoothRate must be finite and non-negative.");
        }

        switch (rig.Motion) {
            case WorldCameraMotion.Follow follow:
                if (!IsFinite(value: follow.Offset)) {
                    errors.Add(item: $"{path}.motion.offset must contain finite coordinates.");
                }

                if (!float.IsFinite(f: follow.SpreadPullback)) {
                    errors.Add(item: $"{path}.motion.spreadPullback must be finite.");
                }

                break;
            case WorldCameraMotion.Orbit orbit:
                if (!float.IsFinite(f: orbit.Distance) || (orbit.Distance <= 0f)) {
                    errors.Add(item: $"{path}.motion.distance must be positive and finite.");
                }

                if (!float.IsFinite(f: orbit.Yaw) || !float.IsFinite(f: orbit.Pitch) || !IsFinite(value: orbit.PivotOffset)) {
                    errors.Add(item: $"{path}.motion needs a finite yaw, pitch, and pivot offset.");
                }

                break;
            case WorldCameraMotion.Static value:
                if (!IsFinite(value: value.Position)) {
                    errors.Add(item: $"{path}.motion.position must contain finite coordinates.");
                }

                break;
            case WorldCameraMotion.Track track:
                ValidateTrack(track: track, path: $"{path}.motion", errors: errors);
                break;
            default:
                errors.Add(item: $"{path}.motion is an unknown camera motion kind.");

                break;
        }

        switch (rig.Aim) {
            case WorldCameraAim.Anchor anchor:
                if (!IsFinite(value: anchor.Offset)) {
                    errors.Add(item: $"{path}.aim.offset must contain finite coordinates.");
                }

                break;
            case WorldCameraAim.Forward forward:
                if (!float.IsFinite(f: forward.FocusDistance) || (forward.FocusDistance < 0f)) {
                    errors.Add(item: $"{path}.aim.focusDistance must be finite and non-negative.");
                }

                break;
            case WorldCameraAim.WorldPoint worldPoint:
                if (!IsFinite(value: worldPoint.Target)) {
                    errors.Add(item: $"{path}.aim.target must contain finite coordinates.");
                }

                break;
            default:
                errors.Add(item: $"{path}.aim is an unknown camera aim kind.");

                break;
        }
    }

    private static void ValidateTrack(WorldCameraMotion.Track track, string path, List<string> errors) {
        if ((track.Definition is null) || (track.Playback is null)) {
            errors.Add(item: $"{path} requires definition and playback state.");

            return;
        }
        if (!Enum.IsDefined(value: track.Definition.ClockDomain) || !Enum.IsDefined(value: track.Definition.Interpolation) || !Enum.IsDefined(value: track.Playback.LoopMode)) {
            errors.Add(item: $"{path} contains an unknown clock, interpolation, or loop mode.");
        }
        var keyframes = track.Definition.Keyframes;
        if ((keyframes is null) || (keyframes.Count < 2)) {
            errors.Add(item: $"{path}.definition.keyframes requires at least two rows.");

            return;
        }
        for (var index = 0; index < keyframes.Count; index++) {
            var keyframe = keyframes[index];
            if (keyframe is null) {
                errors.Add(item: $"{path}.definition.keyframes[{index}] requires a finite position.");

                continue;
            }
            if (!IsFinite(value: keyframe.Position)) {
                errors.Add(item: $"{path}.definition.keyframes[{index}] requires a finite position.");
            }
            if ((index > 0) && (keyframes[index - 1] is { } previous) && (keyframe.Tick <= previous.Tick)) {
                errors.Add(item: $"{path}.definition.keyframes[{index}].tick must be greater than the preceding tick.");
            }
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
        ValidateSeatControl(control: views.SeatControl, path: "views.seatControl", errors: errors);
        if (views.SeatRig?.Motion is not WorldCameraMotion.Orbit) {
            errors.Add(item: "views.seatRig.motion must be orbit because seatControl declares live yaw/pitch input; use cameras for non-interactive authored views.");
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var layouts = views.Layouts;

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

            var slots = layout.Slots;

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

    // A seat's control feel (PRESENTATION-ONLY, REQUIRED): pointer sensitivities and the stick look rate finite and
    // non-negative, the pitch clamp finite, ordered, and within a half-turn either side of level, and the arming a
    // declared enum value. The member itself is required — an absent row is refused by the caller before this runs,
    // never silently defaulted.
    private static void ValidateSeatLook(WorldSeatLook seatLook, string path, List<string> errors) {
        if (!float.IsFinite(f: seatLook.YawSensitivity) || (seatLook.YawSensitivity < 0f)) {
            errors.Add(item: $"{path}.yawSensitivity must be finite and non-negative.");
        }

        if (!float.IsFinite(f: seatLook.PitchSensitivity) || (seatLook.PitchSensitivity < 0f)) {
            errors.Add(item: $"{path}.pitchSensitivity must be finite and non-negative.");
        }

        if (!float.IsFinite(f: seatLook.StickLookRate) || (seatLook.StickLookRate < 0f)) {
            errors.Add(item: $"{path}.stickLookRate must be finite and non-negative.");
        }

        if (!Enum.IsDefined(value: seatLook.Arming)) {
            errors.Add(item: $"{path}.arming is an unknown seat-look arming mode.");
        }
    }

    private static void ValidateSeatControl(WorldSeatViewControl control, string path, List<string> errors) {
        if (control is null) {
            errors.Add(item: $"{path} is required.");
            return;
        }
        if (!Enum.IsDefined(value: control.YawReference)) {
            errors.Add(item: $"{path}.yawReference is unknown.");
        }
        if (!float.IsFinite(f: control.MinPitch) || !float.IsFinite(f: control.MaxPitch) ||
            (control.MinPitch < (-MathF.PI / 2f)) || (control.MaxPitch > (MathF.PI / 2f))) {
            errors.Add(item: $"{path}.minPitch and {path}.maxPitch must be finite and within [-pi/2, pi/2].");
        } else if (control.MinPitch >= control.MaxPitch) {
            errors.Add(item: $"{path}.minPitch must be less than {path}.maxPitch.");
        }
    }

    private static bool ShapesContain(IReadOnlyList<Puck.Forge.Authoring.ShapeDocument> shapes, int id) {
        for (var index = 0; (index < shapes.Count); index++) {
            if (shapes[index].Id == id) {
                return true;
            }
        }

        return false;
    }

    // The storage host-section: an AUTHORED endpoint (and, identically, an authored discovery endpoint) must be an
    // absolute URI — a connection string carries account secrets and a document travels, so connection strings are
    // CLI-reflection-only (--storage-uri / --storage-discovery-uri, the dev/emulator path) and refuse here by
    // design. A user-id must be non-empty when present. All null (WorldStorageDefaults.None) is the built-in —
    // cloud unwired, identity declined, discovery unset.
    private static void ValidateStorage(WorldStorageDefaults storage, List<string> errors) {
        if (storage is null) {
            errors.Add(item: "storage is required.");

            return;
        }

        if ((storage.Endpoint is { } endpoint) &&
            (string.IsNullOrWhiteSpace(value: endpoint) || !Uri.TryCreate(uriString: endpoint, uriKind: UriKind.Absolute, result: out _))) {
            errors.Add(item: $"storage.endpoint '{endpoint}' must be an absolute URI or null.");
        }

        if ((storage.DiscoveryEndpoint is { } discoveryEndpoint) &&
            (string.IsNullOrWhiteSpace(value: discoveryEndpoint) || !Uri.TryCreate(uriString: discoveryEndpoint, uriKind: UriKind.Absolute, result: out _))) {
            errors.Add(item: $"storage.discoveryEndpoint '{discoveryEndpoint}' must be an absolute URI or null.");
        }

        if ((storage.UserId is { } userId) && string.IsNullOrWhiteSpace(value: userId)) {
            errors.Add(item: "storage.userId must be non-empty or null.");
        }
    }

    // The host section (PRESENTATION-ONLY): window extents bounded, exit/pacing non-negative, the closed engine enums
    // in range (a mutation can carry an out-of-range cast the JSON converter alone would not catch), and the surface
    // format not the Unknown hole. Genlock is SHAPE-only (null or non-whitespace) — unlike storage.endpoint (nothing yet
    // consumes it), genlock IS wired at boot into the external-clock election, which tolerates an unknown source id.
    /// <summary>Refuses, at authoring, any token the backend site could emit that names no backend. Left to the
    /// settle-time parse alone this would be a coin-flip refusal: a weighted table carrying one bad token boots fine
    /// on every seed that does not draw it, so whether the world starts would move with the world seed and the
    /// instance identity. Every reachable token is checked instead — the same reason a numeric site's distribution is
    /// narrowed against its domain rather than against what it happened to roll.</summary>
    private static void ValidateBackendTokens(WorldDraw draw, IReadOnlyList<WorldGeneratorRow>? generators, List<string> errors) {
        if (!WorldGeneratorEngine.TryResolveSource(generators: generators, draw: draw, generator: out var generator, reason: out _)) {
            return;
        }

        foreach (var context in (generator.Contexts ?? [])) {
            foreach (var alternative in ((context?.Alternatives) ?? [])) {
                if ((alternative is not null) && (WorldHostTokens.ParseBackend(token: alternative.Token) is null)) {
                    errors.Add(item: $"host.backendDraw could emit token '{alternative.Token}', which names no backend ('{WorldHostTokens.BackendAuto}', '{WorldHostTokens.BackendDirectX}', or '{WorldHostTokens.BackendVulkan}').");
                }
            }
        }
    }

    private static void ValidateHost(WorldHostDefaults host, IReadOnlyList<WorldGeneratorRow>? generators, List<string> errors) {
        if (!Enum.IsDefined(value: host.Presentation)) {
            errors.Add(item: $"host.presentation '{host.Presentation}' is not a defined WorldHostPresentation.");
        }

        RequireIntRange(value: host.Width, min: 1, max: 16384, name: "host.width", errors: errors);
        RequireIntRange(value: host.Height, min: 1, max: 16384, name: "host.height", errors: errors);
        RequireIntRange(value: host.ExitAfterSeconds, min: 0, max: int.MaxValue, name: "host.exitAfterSeconds", errors: errors);

        if (!double.IsFinite(d: host.TargetHertz) || (host.TargetHertz < 0.0)) {
            errors.Add(item: $"host.targetHertz {host.TargetHertz} must be finite and non-negative (0 = automatic display pacing).");
        }

        if ((host.Backend is { } backend) && !Enum.IsDefined(value: backend)) {
            errors.Add(item: $"host.backend '{backend}' is not a defined WorldBackendPreference.");
        }

        // The honest XOR this site can afford: WorldHostDefaults is a CLASS, so a null Backend is distinguishable
        // from an authored one and declaring both is refused BY NAME (population.capacityDraw's struct-typed site
        // cannot do this — see its own remarks). Declaring NEITHER stays legitimate and reads as 'auto'.
        if ((host.Backend is not null) && (host.BackendDraw is not null)) {
            errors.Add(item: "host declares both 'backend' and 'backendDraw' — the backend is an authored literal or a draw, never both.");
        }

        if (host.BackendDraw is { } backendDraw) {
            // A TEXT site: the backend is drawn BY NAME, so the source is held to the same kind predicate as any
            // other text site. No numeric domain applies, hence the full band.
            ValidateDrawSite(draw: backendDraw, generators: generators, targetKind: CellKind.Text, bootOnly: true, domainLow: long.MinValue, domainHigh: long.MaxValue, path: "host.backendDraw", errors: errors);
            ValidateBackendTokens(draw: backendDraw, generators: generators, errors: errors);
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

        // Listen is SHAPE-only too: null (loopback-only, the default) or a non-whitespace "host:port" pair.
        // Server.WorldTcpHost is what actually parses/binds it; the validator only refuses an obviously malformed
        // value so a typo fails loudly at boot rather than surfacing as a silent "never listening".
        if ((host.Listen is { } listen)) {
            if (string.IsNullOrWhiteSpace(value: listen)) {
                errors.Add(item: "host.listen must be a non-whitespace \"host:port\" pair or null.");
            } else {
                var separator = listen.LastIndexOf(value: ':');

                if ((separator <= 0) || (separator == (listen.Length - 1)) || !int.TryParse(s: listen[(separator + 1)..], result: out var port) || (port <= 0) || (port > 65535)) {
                    errors.Add(item: $"host.listen '{listen}' must be a \"host:port\" pair with a port 1..65535.");
                }
            }
        }

        ValidateHostEndpoint(value: host.Authority, path: "host.authority", errors: errors);
    }

    private static void ValidateHostEndpoint(string? value, string path, List<string> errors) {
        if (value is null) {
            return;
        }

        if (string.IsNullOrWhiteSpace(value: value)) {
            errors.Add(item: $"{path} must be a non-whitespace \"host:port\" pair or null.");

            return;
        }

        var separator = value.LastIndexOf(value: ':');

        if ((separator <= 0) || (separator == (value.Length - 1)) || !int.TryParse(s: value[(separator + 1)..], result: out var port) || (port <= 0) || (port > 65535)) {
            errors.Add(item: $"{path} '{value}' must be a \"host:port\" pair with a port 1..65535.");
        }
    }

    // The world motion defaults: positive speeds and correction smoothing distance.
    private static void ValidateMotionDefaults(in WorldMotionDefaults motion, string path, List<string> errors) {
        RequirePositive(value: motion.MoveSpeed, name: $"{path}.moveSpeed", errors: errors);
        RequirePositive(value: motion.TurnSpeed, name: $"{path}.turnSpeed", errors: errors);
        RequirePositive(value: motion.MaxSmoothError, name: $"{path}.maxSmoothError", errors: errors);
    }

    /// <summary>The tuning facets a body motion program's selected operations read from a kit's declared
    /// <see cref="WorldMotionModel"/> — the validator's own mapping (never convention; see
    /// <see cref="RequiredMotionTuningFacets"/>/<see cref="SuppliedMotionTuningFacets"/>) that a new operation or a new
    /// model arm must extend. A declared model missing a facet an operation still reads refuses by name at
    /// validation instead of the operation reading a silent zero at runtime.</summary>
    [Flags]
    private enum MotionTuningFacet : ushort {
        None = 0,

        /// <summary>MoveSpeed/TurnSpeed — read unconditionally by every Motion-kind body motion program.</summary>
        Speed = 1,

        /// <summary>RiseGravity/FallGravity/MaxFallSpeed, the full gravity arc (<see cref="BodyMotionOp.ApplyVerticalGravity"/>) —
        /// the same op <see cref="CompiledBodyMotionProgram.OwnsVerticalContactState"/> keys off, at runtime, to decide
        /// whether contact resolution may write back into a body's vertical channel.</summary>
        GravityArc = 2,

        /// <summary>RiseGravity alone, read as a symmetric bleed rate (<see cref="BodyMotionOp.ApplyVerticalDecay"/>).</summary>
        GravityBleed = 4,

        /// <summary>The velocity-response table (<see cref="BodyMotionOp.ShapePlanarVelocity"/>).</summary>
        PlanarResponse = 8,

        /// <summary>SprintMultiplier/SprintChannel (<see cref="BodyMotionOp.ComputePlanarTargetVelocity"/>).</summary>
        Sprint = 16,

        /// <summary>MoveFrame/FacingSnap (<see cref="BodyMotionOp.ResolveYawAttitudeAndPlanarFrame"/>/
        /// <see cref="BodyMotionOp.SnapYawToPlanarIntent"/>).</summary>
        WorldFrame = 32,

        /// <summary>The anisotropic drive family — longitudinal accel/brake/coast, lateral grip/drift, and
        /// speed-scaled steering (<see cref="BodyMotionOp.ResolveVehicleFrame"/>/
        /// <see cref="BodyMotionOp.ShapeVehicleVelocity"/>).</summary>
        VehicleDrive = 64,

        /// <summary>VerticalThrustFraction (<see cref="BodyMotionOp.ComputeSwimTargetVelocity"/>).</summary>
        SwimThrust = 128,

        /// <summary>Buoyancy/MaxRiseSpeed/MaxSinkSpeed/SurfaceSettleRate/FloatDepth — the medium dynamics
        /// (<see cref="BodyMotionOp.ApplyBuoyancyAndSurface"/>).</summary>
        SwimBuoyancy = 256,
    }

    // The op→facet mapping: walks a COMPILED program's selected operations (never the authored list — compilation
    // already rejected an unknown/inadmissible opcode) and unions the facets each one reads. Speed is unconditional
    // for every Motion-kind program (WorldBody resolves MoveSpeed/TurnSpeed before op dispatch, independent of which
    // operations a program selects) — callers only reach here once the kit's program has already been confirmed
    // Motion-kind (see ValidateKits).
    private static MotionTuningFacet RequiredMotionTuningFacets(CompiledBodyMotionProgram program) {
        var facets = MotionTuningFacet.Speed;

        foreach (var op in Enum.GetValues<BodyMotionOp>()) {
            if (!program.Contains(operation: op)) {
                continue;
            }

            facets |= op switch {
                BodyMotionOp.ApplyVerticalGravity => MotionTuningFacet.GravityArc,
                BodyMotionOp.ApplyVerticalDecay => MotionTuningFacet.GravityBleed,
                BodyMotionOp.ShapePlanarVelocity => MotionTuningFacet.PlanarResponse,
                BodyMotionOp.ComputePlanarTargetVelocity => MotionTuningFacet.Sprint,
                BodyMotionOp.ResolveYawAttitudeAndPlanarFrame or BodyMotionOp.SnapYawToPlanarIntent => MotionTuningFacet.WorldFrame,
                BodyMotionOp.ResolveVehicleFrame or BodyMotionOp.ShapeVehicleVelocity => MotionTuningFacet.VehicleDrive,
                // The swim program's planar half rides the SAME ShapePlanarVelocity op (and PlanarResponse facet)
                // grounded uses; thrust additionally reads the sprint pair. The vertical channel has no op-specific
                // facet of its own here — ApplyBuoyancyAndSurface below is the ONE vertical owner and already
                // requires SwimBuoyancy, which subsumes it.
                BodyMotionOp.ComputeSwimTargetVelocity => (MotionTuningFacet.Sprint | MotionTuningFacet.SwimThrust),
                BodyMotionOp.ApplyBuoyancyAndSurface => MotionTuningFacet.SwimBuoyancy,
                _ => MotionTuningFacet.None,
            };
        }

        return facets;
    }

    // The model→facet mapping: what each WorldMotionModel arm supplies. A new arm (swim/vehicle) is a localized
    // addition here, alongside its record arm (WorldDefinition.cs), its WorldBody integrator, and any new
    // BodyMotionOp cases RequiredMotionTuningFacets needs — never a hunt. Grounded supplies every facet defined
    // today because it is, today, also the only arm the world's "free" body motion program authors (see
    // WorldMotionModel.Grounded's remarks) — a strict superset of what free's operations read.
    private static MotionTuningFacet SuppliedMotionTuningFacets(WorldMotionModel model) => model switch {
        WorldMotionModel.Grounded => (MotionTuningFacet.Speed | MotionTuningFacet.GravityArc | MotionTuningFacet.GravityBleed
            | MotionTuningFacet.PlanarResponse | MotionTuningFacet.Sprint | MotionTuningFacet.WorldFrame),
        // The vehicle arm carries its own gravity trio (contact-pinned variants run ApplyVerticalGravity; flying
        // variants bleed impulses through ApplyVerticalDecay) but none of grounded's planar-shaping facets — pairing
        // a vehicle model with ShapePlanarVelocity/ComputePlanarTargetVelocity/the yaw-frame ops refuses by name.
        WorldMotionModel.Vehicle => (MotionTuningFacet.Speed | MotionTuningFacet.GravityArc | MotionTuningFacet.GravityBleed
            | MotionTuningFacet.VehicleDrive),
        // No gravity facets: a swim kit selecting ApplyVerticalGravity/ApplyVerticalDecay refuses here BY NAME —
        // the medium owns the vertical channel, and the missing-facet line is the door that says so.
        WorldMotionModel.Swim => (MotionTuningFacet.Speed | MotionTuningFacet.PlanarResponse | MotionTuningFacet.Sprint
            | MotionTuningFacet.WorldFrame | MotionTuningFacet.SwimThrust | MotionTuningFacet.SwimBuoyancy),
        _ => MotionTuningFacet.None,
    };

    /// <summary>Determines whether <paramref name="model"/> supplies every tuning facet <paramref name="program"/>'s
    /// selected operations read — the one check <see cref="ValidateMotionModel"/> (at boot) and the runtime
    /// body-motion-program switch (the <c>player.motion</c> door) share, so a document-legal kit can never
    /// runtime-switch into a program its declared model cannot back.</summary>
    /// <param name="model">The kit's declared locomotion model.</param>
    /// <param name="program">The compiled body motion program the switch targets.</param>
    /// <param name="reason">On failure, the refusal detail naming the missing facet(s) and the program; <c>""</c> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="model"/> supplies every facet <paramref name="program"/> requires.</returns>
    public static bool TryValidateProgramCoherence(WorldMotionModel model, CompiledBodyMotionProgram program, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: model);
        ArgumentNullException.ThrowIfNull(argument: program);

        var missing = (RequiredMotionTuningFacets(program: program) & ~SuppliedMotionTuningFacets(model: model));

        if (missing == MotionTuningFacet.None) {
            reason = "";

            return true;
        }

        reason = $"motion model '{model.GetType().Name}' does not supply {missing} required by body motion program '{program.Name}'.";

        return false;
    }

    // The kit.motion gate: required (a kit with no declared model is a dead kit), coherent with its body motion
    // program's selected operations (program is null when ValidateKits already refused bodyMotionProgram, in which
    // case coherence has nothing sound to check against), and per-arm valid. A new arm is a new case below.
    private static void ValidateMotionModel(WorldMotionModel? model, CompiledBodyMotionProgram? program, string path, ISet<string> channelNames, bool hasWater, List<string> errors) {
        if (model is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if ((program is not null) && !TryValidateProgramCoherence(model: model, program: program, reason: out var reason)) {
            errors.Add(item: $"{path} {reason}");
        }

        switch (model) {
            case WorldMotionModel.Grounded grounded:
                ValidateGroundedMotion(tuning: grounded, path: path, channelNames: channelNames, errors: errors);

                break;
            case WorldMotionModel.Vehicle vehicle:
                ValidateVehicleMotion(tuning: vehicle, path: path, channelNames: channelNames, errors: errors);

                break;
            case WorldMotionModel.Swim swim:
                ValidateSwimMotion(tuning: swim, path: path, channelNames: channelNames, hasWater: hasWater, errors: errors);

                break;
            default:
                errors.Add(item: $"{path} is an unknown motion model kind '{model.GetType().Name}'.");

                break;
        }
    }

    // A kit's vehicle drive tuning: every convergence rate positive, the steering authority curve well-formed, and
    // the held drift/boost channel names resolving to declared channels (a misspelled name is otherwise a silent,
    // permanent no-op). An owned world's identity-carried locomotion state rows feed WorldIdentity's live speed
    // reads RAW at construction — no verb door runs on a document load — so a non-positive persisted value is
    // refused here; identity.motion refuses the same range live.
    private static void ValidateIdentityMotionState(WorldIdentityDefinition? identity, IReadOnlyDictionary<string, WorldStateRow> stateRows, List<string> errors) {
        if (identity is null) {
            return;
        }

        RequirePositiveFixedSlot(stateRows: stateRows, name: identity.MoveSpeedState, path: "identity.moveSpeedState", errors: errors);
        RequirePositiveFixedSlot(stateRows: stateRows, name: identity.TurnSpeedState, path: "identity.turnSpeedState", errors: errors);
    }

    private static void RequirePositiveFixedSlot(IReadOnlyDictionary<string, WorldStateRow> stateRows, string name, string path, List<string> errors) {
        if (stateRows.TryGetValue(key: name, value: out var row) && (row is { Kind: CellKind.Fixed, IsSlot: true }) && (row.Cells![0].Value <= 0L)) {
            errors.Add(item: $"{path} row '{name}' carries {(double)FixedQ4816.FromRawBits(value: row.Cells[0].Value):0.####} — a locomotion rate must be positive.");
        }
    }

    private static void ValidateVehicleMotion(WorldMotionModel.Vehicle tuning, string path, ISet<string> channelNames, List<string> errors) {
        RequirePositive(value: tuning.TopSpeed, name: $"{path}.topSpeed", errors: errors);
        RequireNonNegative(value: tuning.ReverseTopSpeed, name: $"{path}.reverseTopSpeed", errors: errors);
        RequirePositive(value: tuning.Accel, name: $"{path}.accel", errors: errors);
        RequirePositive(value: tuning.Brake, name: $"{path}.brake", errors: errors);
        RequirePositive(value: tuning.CoastDrag, name: $"{path}.coastDrag", errors: errors);
        RequirePositive(value: tuning.Grip, name: $"{path}.grip", errors: errors);
        RequirePositive(value: tuning.SteerRate, name: $"{path}.steerRate", errors: errors);
        RequirePositive(value: tuning.SteerReferenceSpeed, name: $"{path}.steerReferenceSpeed", errors: errors);
        RequireNonNegative(value: tuning.PitchRate, name: $"{path}.pitchRate", errors: errors);
        RequirePositive(value: tuning.RiseGravity, name: $"{path}.riseGravity", errors: errors);
        RequirePositive(value: tuning.FallGravity, name: $"{path}.fallGravity", errors: errors);
        RequirePositive(value: tuning.MaxFallSpeed, name: $"{path}.maxFallSpeed", errors: errors);
        RequirePositive(value: tuning.DriftSteerScale, name: $"{path}.driftSteerScale", errors: errors);
        RequirePositive(value: tuning.BoostMultiplier, name: $"{path}.boostMultiplier", errors: errors);

        if (!float.IsFinite(f: tuning.SteerFalloff) || (tuning.SteerFalloff < 0f) || (tuning.SteerFalloff > 1f)) {
            errors.Add(item: $"{path}.steerFalloff {tuning.SteerFalloff} must be within [0, 1].");
        }

        if (tuning.DriftChannel is { Length: > 0 } driftChannel) {
            if (!channelNames.Contains(item: driftChannel)) {
                errors.Add(item: $"{path}.driftChannel '{driftChannel}' names no declared composition channel.");
            }

            if (!float.IsFinite(f: tuning.DriftGrip) || (tuning.DriftGrip <= 0f)) {
                errors.Add(item: $"{path}.driftGrip {tuning.DriftGrip} must be positive when a drift channel is declared.");
            }
        }

        if ((tuning.BoostChannel is { Length: > 0 } boostChannel) && !channelNames.Contains(item: boostChannel)) {
            errors.Add(item: $"{path}.boostChannel '{boostChannel}' names no declared composition channel.");
        }

        // Well-formedness only (finite min/max, min <= max) — deliberately NOT the own-value-in-range check
        // ValidateScalarEnvelope also applies to grounded's moveSpeed. Grounded's baseline is a profileless fallback
        // the live-clamped read (Profile's own speed) can diverge from, so an own-value check keeps that fallback
        // sane. The vehicle arm has no such second channel: topSpeed itself IS the live-clamped read (world.row.set
        // retunes it in place), so requiring it inside its own envelope would refuse the exact retune-past-the-cap
        // this envelope exists to catch. A malformed envelope (min > max, non-finite) still refuses.
        if (tuning.TopSpeedEnvelope is { } topSpeedEnvelope) {
            ValidateEnvelopeShape(envelope: topSpeedEnvelope, path: $"{path}.topSpeedEnvelope", errors: errors);
        }
    }

    // A kit's full grounded locomotion tuning: speeds, gravity, and the velocity-response table every body
    // integrates under.
    private static void ValidateGroundedMotion(WorldMotionModel.Grounded tuning, string path, ISet<string> channelNames, List<string> errors) {
        RequirePositive(value: tuning.MoveSpeed, name: $"{path}.moveSpeed", errors: errors);
        RequirePositive(value: tuning.TurnSpeed, name: $"{path}.turnSpeed", errors: errors);
        RequirePositive(value: tuning.RiseGravity, name: $"{path}.riseGravity", errors: errors);
        RequirePositive(value: tuning.FallGravity, name: $"{path}.fallGravity", errors: errors);
        RequirePositive(value: tuning.MaxFallSpeed, name: $"{path}.maxFallSpeed", errors: errors);
        ValidateResponse(response: tuning.Response, path: $"{path}.response", errors: errors);
        RequirePositive(value: tuning.SprintMultiplier, name: $"{path}.sprintMultiplier", errors: errors);

        // The sprint channel needs the same "must resolve" bar
        // ValidateRoute holds engageChannel to, for the identical reason (a misspelled name would otherwise be a
        // silent, permanent no-op — the button never sprints and nothing here would have said why).
        if ((tuning.SprintChannel is { Length: > 0 } sprintChannel) && !channelNames.Contains(item: sprintChannel)) {
            errors.Add(item: $"{path}.sprintChannel '{sprintChannel}' names no declared composition channel.");
        }

        if (!Enum.IsDefined(value: tuning.MoveFrame)) {
            errors.Add(item: $"{path}.moveFrame '{tuning.MoveFrame}' is not a defined MotionMoveFrame.");
        }

        // Absent, this envelope is wide-open (unclamped). Another arm's own overridable scalar walks the same gate.
        if (tuning.MoveSpeedEnvelope is { } moveSpeedEnvelope) {
            ValidateScalarEnvelope(envelope: moveSpeedEnvelope, ownValue: tuning.MoveSpeed, ownValueName: "moveSpeed", path: $"{path}.moveSpeedEnvelope", errors: errors);
        }
    }

    // The shape every authored envelope must have regardless of arm: min/max finite, min <= max (FixedQ4816.Clamp's
    // own precondition, refused here so it never throws at seat-resolve time), min non-negative — every consumer
    // bounds a speed magnitude, and reverse travel is its own positive scalar (reverseTopSpeed), so a negative
    // endpoint would only widen the clamp past the bound's apparent intent. Returns whether the shape held, so a
    // caller layering an additional check can skip it once the bound is already malformed.
    private static bool ValidateEnvelopeShape(MotionScalarEnvelope envelope, string path, List<string> errors) {
        if (!float.IsFinite(f: envelope.Min) || !float.IsFinite(f: envelope.Max)) {
            errors.Add(item: $"{path} must have a finite min and max.");

            return false;
        }

        if (envelope.Min > envelope.Max) {
            errors.Add(item: $"{path}.min ({envelope.Min}) is greater than {path}.max ({envelope.Max}).");

            return false;
        }

        if (envelope.Min < 0f) {
            errors.Add(item: $"{path}.min ({envelope.Min}) is negative — an envelope bounds a speed magnitude, so a negative endpoint admits magnitudes past the bound's own max; reverse travel is authored as its own positive scalar, never a negative speed.");

            return false;
        }

        return true;
    }

    // Layered over ValidateEnvelopeShape: the kit's own authored value for the bounded scalar must also sit inside
    // its own declared envelope — a world that pins a scalar narrower than the baseline it authors for profileless
    // stand-ins is self-contradictory. Meaningful only where the bounded value is a fallback a separate, unvalidated
    // live read (a seated profile) can diverge from — see ValidateVehicleMotion's remarks for the arm that
    // deliberately skips this layer.
    private static void ValidateScalarEnvelope(MotionScalarEnvelope envelope, float ownValue, string ownValueName, string path, List<string> errors) {
        if (!ValidateEnvelopeShape(envelope: envelope, path: path, errors: errors)) {
            return;
        }

        if ((ownValue < envelope.Min) || (ownValue > envelope.Max)) {
            errors.Add(item: $"{path} [{envelope.Min}, {envelope.Max}] does not contain the kit's own {ownValueName} ({ownValue}).");
        }
    }

    // A kit's full swim locomotion tuning: thrust, medium dynamics, and the shared response/sprint/frame vocabulary.
    // A swim kit in a world without a water section refuses HERE — the medium is the model's whole premise, and a
    // silent dry-world swimmer would integrate against a waterline that does not exist.
    private static void ValidateSwimMotion(WorldMotionModel.Swim tuning, string path, ISet<string> channelNames, bool hasWater, List<string> errors) {
        if (!hasWater) {
            errors.Add(item: $"{path} declares a swim model but the world authors no water section.");
        }

        RequirePositive(value: tuning.ThrustSpeed, name: $"{path}.thrustSpeed", errors: errors);
        RequirePositive(value: tuning.TurnSpeed, name: $"{path}.turnSpeed", errors: errors);
        RequirePositive(value: tuning.VerticalThrustFraction, name: $"{path}.verticalThrustFraction", errors: errors);
        ValidateResponse(response: tuning.Response, path: $"{path}.response", errors: errors);

        if (!float.IsFinite(f: tuning.Buoyancy)) {
            errors.Add(item: $"{path}.buoyancy must be finite (was {tuning.Buoyancy}).");
        }

        RequirePositive(value: tuning.MaxRiseSpeed, name: $"{path}.maxRiseSpeed", errors: errors);
        RequirePositive(value: tuning.MaxSinkSpeed, name: $"{path}.maxSinkSpeed", errors: errors);
        RequirePositive(value: tuning.SurfaceSettleRate, name: $"{path}.surfaceSettleRate", errors: errors);
        RequirePositive(value: tuning.FloatDepth, name: $"{path}.floatDepth", errors: errors);
        RequirePositive(value: tuning.SprintMultiplier, name: $"{path}.sprintMultiplier", errors: errors);

        if ((tuning.SprintChannel is { Length: > 0 } sprintChannel) && !channelNames.Contains(item: sprintChannel)) {
            errors.Add(item: $"{path}.sprintChannel '{sprintChannel}' names no declared composition channel.");
        }

        if (!Enum.IsDefined(value: tuning.MoveFrame)) {
            errors.Add(item: $"{path}.moveFrame '{tuning.MoveFrame}' is not a defined MotionMoveFrame.");
        }

        // The seat-time speed clamp, this arm's own scalar — the SAME reusable gate Grounded.MoveSpeedEnvelope walks.
        if (tuning.ThrustSpeedEnvelope is { } thrustSpeedEnvelope) {
            ValidateScalarEnvelope(envelope: thrustSpeedEnvelope, ownValue: tuning.ThrustSpeed, ownValueName: "thrustSpeed", path: $"{path}.thrustSpeedEnvelope", errors: errors);
        }
    }

    // A kit/motion velocity-response table (SIM-AFFECTING): each row's engage/release rates must be positive (a zero
    // rate never converges — a stuck body, not a feel), each gate is a body-fact-only predicate (the lane-scoped
    // action-state predicates are rejected by name), and a null (always) gate before the final row makes every
    // later row unreachable.
    private static void ValidateResponse(IReadOnlyList<MotionResponse> response, string path, List<string> errors) {
        // A required-with-no-default constructor parameter the JSON never supplied binds null, not an empty list.
        // Name the absent section rather than faulting on it.
        if (response is null) {
            errors.Add(item: $"{path} is required.");

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
    // CompareState/TimerElapsed are rejected by name ("action-state predicates apply only to action triggers"); an
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
            case ActionPredicate.CompareState:
            case ActionPredicate.TimerElapsed:
                errors.Add(item: $"{path} is an action-state predicate ('{PredicateKind(predicate: predicate)}') — action-state predicates apply only to action triggers, not a motion response gate.");
                break;
            default:
                errors.Add(item: $"{path} is an unknown predicate kind.");
                break;
        }
    }

    private static string PredicateKind(ActionPredicate predicate) => predicate switch {
        ActionPredicate.CompareState => "compareState",
        ActionPredicate.TimerElapsed => "timerElapsed",
        _ => "?",
    };

    // The contact-solver tuning (SIM-AFFECTING). ContactSkin positive; MaxIterations 1..8 (above 8 is a solver
    // pathology, not a choice); requirements are unique; MaxSlopeDegrees in (0, 90) — 0 grounds nothing, 90 grounds a
    // wall; GradientProbe non-negative, and > 0 requires a field-selecting requirement.
    private static void ValidateCollision(WorldCollision collision, List<string> errors) {
        RequirePositive(value: collision.ContactSkin, name: "collision.contactSkin", errors: errors);
        RequireIntRange(value: collision.MaxIterations, min: 1, max: 8, name: "collision.maxIterations", errors: errors);

        if (collision.Requirements is null) {
            errors.Add(item: "collision.requirements is required.");
        } else {
            var requirements = new HashSet<WorldContactRequirement>();

            for (var index = 0; (index < collision.Requirements.Count); index++) {
                var requirement = collision.Requirements[index];

                if (!Enum.IsDefined(value: requirement)) {
                    errors.Add(item: $"collision.requirements[{index}] '{requirement}' is not defined.");
                } else if (!requirements.Add(item: requirement)) {
                    errors.Add(item: $"collision.requirements[{index}] duplicates '{requirement}'.");
                }
            }
        }

        if (!float.IsFinite(f: collision.MaxSlopeDegrees) || (collision.MaxSlopeDegrees <= 0f) || (collision.MaxSlopeDegrees >= 90f)) {
            errors.Add(item: $"collision.maxSlopeDegrees must be in (0, 90) (was {collision.MaxSlopeDegrees}).");
        }

        RequireNonNegative(value: collision.GradientProbe, name: "collision.gradientProbe", errors: errors);

        // A tiny positive step collapses every central-difference pair to equal taps (see SdfFieldEvaluator's
        // GradientEpsilon remarks on the quantize-to-equal floor), silently disabling contact resolution rather than
        // refusing loudly — so an authored non-zero step must clear the measured floor with headroom, not merely be
        // non-negative.
        if (float.IsFinite(f: collision.GradientProbe) && (collision.GradientProbe >= 0f) && (collision.GradientProbe != 0f) && (collision.GradientProbe < 0.001f)) {
            errors.Add(item: $"collision.gradientProbe must be exactly 0 (take the evaluator default) or >= 0.001 (was {collision.GradientProbe}).");
        }

        if ((collision.Requirements is { Count: 0 }) && float.IsFinite(f: collision.GradientProbe) && (collision.GradientProbe > 0f)) {
            errors.Add(item: "collision.gradientProbe > 0 requires at least one field-contact requirement.");
        }
    }

    // The water section: null IS the dry world, so the only refusable shape is a non-finite level. JSON cannot spell
    // NaN/Infinity, so this guards the programmatic-construction path a future consumer would compile from.
    private static void ValidateWater(WorldWaterSection? water, List<string> errors) {
        if ((water is not null) && !float.IsFinite(f: water.Level)) {
            errors.Add(item: $"water.level must be finite (was {water.Level}).");
        }
    }

    // The market section: null IS today's no-market world. A declared section validates its config (formats/fee/
    // duration bounds/admission tiers) and its live listing ledger. A listing's item/currency rows must already be
    // declared, Int-kind, capacity-bounded (keyed-table intent) state rows — the same rows the compose-time doors in
    // Server.WorldServer re-check before every escrow move, so an authored (or engine-composed) listing can never
    // outlive the row it depends on without this pass catching it first.
    private static void ValidateMarket(WorldMarketSection? market, Dictionary<string, WorldStateRow> stateRows, List<string> errors) {
        if (market is null) {
            return;
        }

        if ((market.FeeBasisPoints < 0) || (market.FeeBasisPoints > WorldMarketCapacity.MaxFeeBasisPoints)) {
            errors.Add(item: $"market.feeBasisPoints {market.FeeBasisPoints} is outside 0..{WorldMarketCapacity.MaxFeeBasisPoints}.");
        }

        var minDuration = market.MinDurationSeconds;
        var maxDuration = market.MaxDurationSeconds;

        if (!float.IsFinite(f: minDuration) || (minDuration < WorldMarketCapacity.MinDurationFloorSeconds) || (minDuration > WorldMarketCapacity.MaxDurationCeilingSeconds)) {
            errors.Add(item: $"market.minDurationSeconds {minDuration} is outside {WorldMarketCapacity.MinDurationFloorSeconds}..{WorldMarketCapacity.MaxDurationCeilingSeconds}.");
        }

        if (!float.IsFinite(f: maxDuration) || (maxDuration < WorldMarketCapacity.MinDurationFloorSeconds) || (maxDuration > WorldMarketCapacity.MaxDurationCeilingSeconds)) {
            errors.Add(item: $"market.maxDurationSeconds {maxDuration} is outside {WorldMarketCapacity.MinDurationFloorSeconds}..{WorldMarketCapacity.MaxDurationCeilingSeconds}.");
        }

        if (float.IsFinite(f: minDuration) && float.IsFinite(f: maxDuration) && (minDuration > maxDuration)) {
            errors.Add(item: $"market.minDurationSeconds {minDuration} exceeds market.maxDurationSeconds {maxDuration}.");
        }

        var retention = market.RetentionSeconds;

        if (!float.IsFinite(f: retention) || (retention < WorldMarketCapacity.MinRetentionSeconds) || (retention > WorldMarketCapacity.MaxRetentionSeconds)) {
            errors.Add(item: $"market.retentionSeconds {retention} is outside {WorldMarketCapacity.MinRetentionSeconds}..{WorldMarketCapacity.MaxRetentionSeconds}.");
        }

        if (market.Formats is { } formats) {
            var seenFormats = new HashSet<WorldMarketFormat>();

            for (var index = 0; (index < formats.Count); index++) {
                if (!seenFormats.Add(item: formats[index])) {
                    errors.Add(item: $"market.formats[{index}] '{formats[index]}' is duplicated.");
                }
            }
        }

        if (market.AdmissionTiers is { } tiers) {
            if (tiers.Count > WorldMarketCapacity.MaxAdmissionTiers) {
                errors.Add(item: $"market.admissionTiers count {tiers.Count} exceeds the maximum of {WorldMarketCapacity.MaxAdmissionTiers}.");
            }

            var seenTierNames = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < tiers.Count); index++) {
                var tier = tiers[index];
                var path = $"market.admissionTiers[{index}]";

                if (string.IsNullOrWhiteSpace(value: tier.Name)) {
                    errors.Add(item: $"{path}.name is required.");
                } else if (tier.Name.Length > WorldStateCapacity.MaxTextValueLength) {
                    errors.Add(item: $"{path}.name length {tier.Name.Length} exceeds the maximum of {WorldStateCapacity.MaxTextValueLength}.");
                } else if (!seenTierNames.Add(item: tier.Name)) {
                    errors.Add(item: $"{path}.name '{tier.Name}' is duplicated.");
                }
            }
        }

        var listings = (market.Listings ?? []);

        if (listings.Count > WorldMarketCapacity.MaxListings) {
            errors.Add(item: $"market.listings count {listings.Count} exceeds the maximum of {WorldMarketCapacity.MaxListings}.");
        }

        var seenIds = new HashSet<long>();
        var maxSeenId = 0L;

        for (var index = 0; (index < listings.Count); index++) {
            var listing = listings[index];
            var path = $"market.listings[{index}]";

            if (listing is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!seenIds.Add(item: listing.Id)) {
                errors.Add(item: $"{path}.id {listing.Id} is duplicated.");
            }

            maxSeenId = Math.Max(val1: maxSeenId, val2: listing.Id);

            if ((listing.Seller.Kind != PrincipalKind.Seat) && (listing.Seller.Kind != PrincipalKind.Peer)) {
                errors.Add(item: $"{path}.seller {listing.Seller.Describe()} must be a seat or peer.");
            }

            if (listing.Quantity <= 0) {
                errors.Add(item: $"{path}.quantity {listing.Quantity} must be positive.");
            }

            ValidateMarketRow(path: $"{path}.itemRow", rowName: listing.ItemRow, stateRows: stateRows, errors: errors);
            ValidateMarketRow(path: $"{path}.currencyRow", rowName: listing.CurrencyRow, stateRows: stateRows, errors: errors);

            switch (listing.Format) {
                case WorldMarketFormat.English:
                    if (listing.StartPrice <= 0) {
                        errors.Add(item: $"{path}.startPrice {listing.StartPrice} must be positive for an English listing.");
                    }

                    break;
                case WorldMarketFormat.Buyout:
                    if (listing.CurrentBid != 0) {
                        errors.Add(item: $"{path} is a buyout listing but carries a nonzero currentBid — buyout takes no incremental bids.");
                    }

                    if (listing.CurrentBidder is not null) {
                        errors.Add(item: $"{path} is a buyout listing but carries a currentBidder — buyout takes no incremental bids.");
                    }

                    // startPrice is the English minimum-opening-bid field; a buyout listing never reads it (see
                    // WorldMarketListing.StartPrice's remarks), but it is still a carried long, not an omittable
                    // one — refused by name to its documented inert value (market.list's own help text: "unused by
                    // buyout, pass 0") rather than left representable-but-meaningless, the same door-not-type
                    // instinct currentBid/currentBidder above already apply to this arm.
                    if (listing.StartPrice != 0) {
                        errors.Add(item: $"{path}.startPrice {listing.StartPrice} is nonzero but buyout takes no incremental bids — startPrice is unused and must be 0.");
                    }

                    break;
            }

            if ((listing.BuyoutPrice is { } buyoutPrice) && (buyoutPrice <= 0)) {
                errors.Add(item: $"{path}.buyoutPrice {buyoutPrice} must be positive.");
            }

            if ((listing.Format == WorldMarketFormat.Buyout) && (listing.BuyoutPrice is null)) {
                errors.Add(item: $"{path} is a buyout listing but declares no buyoutPrice.");
            }

            if (listing.DeadlineTick < 0) {
                errors.Add(item: $"{path}.deadlineTick {listing.DeadlineTick} must be non-negative.");
            }

            if (listing.CurrentBid < 0) {
                errors.Add(item: $"{path}.currentBid {listing.CurrentBid} must be non-negative.");
            }

            if ((listing.CurrentBid > 0) != (listing.CurrentBidder is not null)) {
                errors.Add(item: $"{path}.currentBid and .currentBidder must be set together.");
            }

            if ((listing.CurrentBidder is { } bidder) && (bidder.Kind != PrincipalKind.Seat) && (bidder.Kind != PrincipalKind.Peer)) {
                errors.Add(item: $"{path}.currentBidder {bidder.Describe()} must be a seat or peer.");
            }

            // The retention sweep's own age basis: a terminal row must carry the tick it resolved at (so the sweep
            // has a basis to age it from), and an active row must carry none (it has not resolved yet).
            if ((listing.Status == WorldMarketListingStatus.Active) != (listing.ResolvedTick is null)) {
                errors.Add(item: $"{path}.resolvedTick must be set exactly when .status is not active (status={listing.Status}).");
            }

            if ((listing.ResolvedTick is { } resolvedTick) && (resolvedTick < 0)) {
                errors.Add(item: $"{path}.resolvedTick {resolvedTick} must be non-negative.");
            }
        }

        if (market.NextListingId <= maxSeenId) {
            errors.Add(item: $"market.nextListingId {market.NextListingId} must exceed every listing's id (highest seen: {maxSeenId}).");
        }

        if (market.NextListingId < 1) {
            errors.Add(item: $"market.nextListingId {market.NextListingId} must be positive.");
        }

        if (market.FeeReserve < 0) {
            errors.Add(item: $"market.feeReserve {market.FeeReserve} must be non-negative.");
        }
    }

    // A listing's item/currency row must already be a declared, Int-kind, capacity-bounded state row — the SAME
    // "declaring a capacity is declaring table intent" rule WorldStateRow's own remarks state, since a listing
    // always writes a per-holder cell, never a slot.
    private static void ValidateMarketRow(string path, WorldCellName rowName, Dictionary<string, WorldStateRow> stateRows, List<string> errors) {
        if (!stateRows.TryGetValue(key: rowName, value: out var row)) {
            errors.Add(item: $"{path} '{rowName}' names no declared state row.");

            return;
        }

        if (row.Kind != CellKind.Int) {
            errors.Add(item: $"{path} '{rowName}' is kind {row.Kind}, not int.");
        }

        if (row.Capacity is null) {
            errors.Add(item: $"{path} '{rowName}' declares no capacity — a market row must be an explicitly keyed table (declaring capacity is declaring table intent).");
        }
    }

    // The references section: null names nothing. Each row's Name already crossed WorldSafeName at JSON parse, so
    // this pass owns only uniqueness within the section and a non-empty Document — no boot-time file-existence
    // check (resolving a reference's Document is a future consumer's job). Returns the validated name set so a
    // later pass (a placement face's portal facet) can refuse an undeclared destination by name.
    private static HashSet<string> ValidateReferences(IReadOnlyList<WorldReference>? references, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (references is not { Count: > 0 } rows) {
            return names;
        }

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"references[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!names.Add(item: row.Name)) {
                errors.Add(item: $"{path}.name '{row.Name}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(value: row.Document)) {
                errors.Add(item: $"{path}.document is required.");
            }
        }

        return names;
    }

    // The destinations section: null names nothing. Each row's Name already crossed WorldSafeName; Durability/Scope
    // already crossed their strict-token converters; an unrecognized Selector $type already failed JSON parse. This
    // pass owns uniqueness within the section, a destinations section with no references section to name, each
    // row's Reference resolving to a declared references row, and the scope/selector pairing (ValidateGroupSelector).
    // Returns the validated name set so a later pass can refuse an undeclared destination by name.
    private static HashSet<string> ValidateDestinations(IReadOnlyList<WorldDestination>? destinations, IReadOnlyList<WorldReference>? references, HashSet<string> referenceNames, HashSet<string> groupIds, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (destinations is not { Count: > 0 } rows) {
            return names;
        }

        if (references is not { Count: > 0 }) {
            errors.Add(item: "destinations declares rows, but the world declares no references section for them to name.");
        }

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"destinations[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!names.Add(item: row.Name)) {
                errors.Add(item: $"{path}.name '{row.Name}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(value: row.Reference) || !referenceNames.Contains(item: row.Reference)) {
                errors.Add(item: (referenceNames.Count > 0)
                    ? $"{path}.reference '{row.Reference}' names no references row; the world declares: {string.Join(separator: ", ", values: referenceNames)}."
                    : $"{path}.reference '{row.Reference}' names no references row; the world declares none.");
            }

            if (row.Scope == WorldDestinationScope.Group) {
                if (row.Selector is null) {
                    errors.Add(item: $"{path}.scope is '{WorldDestinationTokens.ScopeGroup}', which requires a selector.");
                } else {
                    ValidateGroupSelector(selector: row.Selector, groupIds: groupIds, path: $"{path}.selector", errors: errors);
                }
            } else if (row.Selector is not null) {
                errors.Add(item: $"{path}.selector is admitted only when scope is '{WorldDestinationTokens.ScopeGroup}' (this row declares scope '{WorldDestinationTokens.ScopeToken(scope: row.Scope)}').");
            }
        }

        return names;
    }

    // A scope=group destination's selector: a `named` arm must resolve to a declared groups.groups[].id (the
    // named/tagged split docs/world-model.md "Durability, scope and generation" describes); a `tagged` arm names no
    // particular group up front — resolution walks the ACTING traveler's own memberships at transfer time (a later
    // lane's job), so this pass only holds the tag itself to the same non-empty discipline WorldGroup.Tags entries
    // already carry.
    private static void ValidateGroupSelector(WorldGroupSelector selector, HashSet<string> groupIds, string path, List<string> errors) {
        switch (selector) {
            case WorldGroupSelector.Named named:
                if (string.IsNullOrWhiteSpace(value: named.Group) || !groupIds.Contains(item: named.Group)) {
                    errors.Add(item: (groupIds.Count > 0)
                        ? $"{path} names group '{named.Group}', which names no groups.groups row; the world declares: {string.Join(separator: ", ", values: groupIds)}."
                        : $"{path} names group '{named.Group}', which names no groups.groups row; the world declares none.");
                }
                break;

            case WorldGroupSelector.Tagged tagged:
                if (string.IsNullOrWhiteSpace(value: tagged.Tag)) {
                    errors.Add(item: $"{path}.tag must be non-empty.");
                }
                break;

            default:
                errors.Add(item: $"{path} is an unrecognized selector kind.");
                break;
        }
    }

    // A lane binding: both trigger channels are optional, but a present trigger's latch must be non-negative and its
    // effects non-empty, and its gate structurally sound.
    private static void ValidateActionStateSlot(ActionStateSlot state, string path, List<string> errors) {
        if (state is null) {
            errors.Add(item: $"{path} is required.");
            return;
        }
        if (string.IsNullOrWhiteSpace(value: state.Name)) {
            errors.Add(item: $"{path}.name must be non-empty.");
        }
        if (!Enum.IsDefined(value: state.Kind)) {
            errors.Add(item: $"{path}.kind '{state.Kind}' is not a defined ActionStateKind.");
        }
        if (!Enum.IsDefined(value: state.Lifetime)) {
            errors.Add(item: $"{path}.lifetime '{state.Lifetime}' is not a defined ActionStateLifetime.");
        }
        if ((state.Lifetime == ActionStateLifetime.Durable) && (state.ResetFact is not null)) {
            errors.Add(item: $"{path}.resetFact is not admitted for durable state; durable values enter only through the tick input seam.");
        }
        if (state.PlayerWritable && (state.Lifetime != ActionStateLifetime.Durable)) {
            errors.Add(item: $"{path}.playerWritable requires durable lifetime.");
        }
        if (state.PlayerWritable && (state.Envelope is null)) {
            errors.Add(item: $"{path}.envelope is required when playerWritable is true.");
        }
        if (!state.PlayerWritable && (state.Envelope is not null)) {
            errors.Add(item: $"{path}.envelope is admitted only when playerWritable is true.");
        }
        if (!float.IsFinite(f: state.Initial) || ((state.Kind == ActionStateKind.Timer) && (state.Initial < 0f))) {
            errors.Add(item: $"{path}.initial must be finite and non-negative for a timer.");
        }
        if ((state.ResetFact is { } reset) && !Enum.IsDefined(value: reset)) {
            errors.Add(item: $"{path}.resetFact '{reset}' is not a defined ActionFact.");
        }

        switch (state.Envelope) {
            case null:
                break;
            case ActionStateEnvelope.Range range:
                if (!float.IsFinite(f: range.Minimum) || !float.IsFinite(f: range.Maximum) || (range.Minimum > range.Maximum)) {
                    errors.Add(item: $"{path}.envelope range must have finite minimum <= maximum.");
                } else if ((state.Kind == ActionStateKind.Timer) && (range.Minimum < 0f)) {
                    errors.Add(item: $"{path}.envelope range minimum must be non-negative for a timer.");
                } else if ((state.Initial < range.Minimum) || (state.Initial > range.Maximum)) {
                    errors.Add(item: $"{path}.initial must lie inside its envelope.");
                }
                break;
            case ActionStateEnvelope.Set set:
                if (set.Values is not { Count: > 0 }) {
                    errors.Add(item: $"{path}.envelope set must be non-empty.");
                    break;
                }
                if (set.Values.Any(value => !float.IsFinite(f: value) || ((state.Kind == ActionStateKind.Timer) && (value < 0f)))) {
                    errors.Add(item: $"{path}.envelope set values must be finite and non-negative for a timer.");
                }
                if (set.Values.Distinct().Count() != set.Values.Count) {
                    errors.Add(item: $"{path}.envelope set values must be unique.");
                }
                if (!set.Values.Contains(value: state.Initial)) {
                    errors.Add(item: $"{path}.initial must belong to its envelope set.");
                }
                break;
            default:
                errors.Add(item: $"{path}.envelope is an unknown envelope kind.");
                break;
        }
    }

    /// <summary>The per-action fact-trigger ceiling — one <see cref="ActionTriggerMode.Edge"/> latch bit each in the
    /// runtime's 64-bit lane word.</summary>
    private const int MaxFactTriggersPerAction = 64;

    private static void ValidateActionSpec(ActionSpec? spec, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, ISet<string> targetRegisterNames, IReadOnlyDictionary<string, WorldStateRow> stateRows, string path, List<string> errors) {
        if (spec is null) {
            return;
        }

        ValidateTrigger(trigger: spec.OnPress, stateSlots: stateSlots, targetRegisterNames: targetRegisterNames, stateRows: stateRows, latchLegitimate: true, path: $"{path}.onPress", errors: errors);
        ValidateTrigger(trigger: spec.OnRelease, stateSlots: stateSlots, targetRegisterNames: targetRegisterNames, stateRows: stateRows, latchLegitimate: false, path: $"{path}.onRelease", errors: errors);
        if (spec.OnFact is { } onFact) {
            // The edge latch is one bit per fact trigger in a 64-bit lane word (WorldBody.LaneActionRuntime) — the
            // same shape every other mask in this engine uses. Refused by name rather than silently un-edged.
            if (onFact.Count > MaxFactTriggersPerAction) {
                errors.Add(item: $"{path}.onFact declares {onFact.Count} triggers; the maximum is {MaxFactTriggersPerAction} (one edge-latch bit each).");
            }

            for (var index = 0; (index < onFact.Count); index++) {
                var rule = onFact[index];
                if (!Enum.IsDefined(value: rule.Fact)) {
                    errors.Add(item: $"{path}.onFact[{index}].fact '{rule.Fact}' is not a defined ActionFact.");
                }
                if (!Enum.IsDefined(value: rule.Mode)) {
                    errors.Add(item: $"{path}.onFact[{index}].mode '{rule.Mode}' is not a defined ActionTriggerMode.");
                }
                ValidatePredicate(predicate: rule.Gate, stateSlots: stateSlots, path: $"{path}.onFact[{index}].gate", errors: errors);
                if (rule.Effects is not { Count: > 0 }) {
                    errors.Add(item: $"{path}.onFact[{index}].effects must be non-empty.");
                    continue;
                }
                for (var effect = 0; (effect < rule.Effects.Count); effect++) {
                    ValidateEffect(effect: rule.Effects[effect], stateSlots: stateSlots, targetRegisterNames: targetRegisterNames, stateRows: stateRows, path: $"{path}.onFact[{index}].effects[{effect}]", errors: errors);
                }
            }
        }
    }

    private static void ValidateTrigger(ActionTrigger? trigger, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, ISet<string> targetRegisterNames, IReadOnlyDictionary<string, WorldStateRow> stateRows, bool latchLegitimate, string path, List<string> errors) {
        if (trigger is null) {
            return;
        }

        RequireNonNegative(value: trigger.LatchSeconds, name: $"{path}.latchSeconds", errors: errors);

        // The release channel latches nothing — the runtime reads LatchSeconds on the press arm alone. An authored
        // value here would be parsed and silently discarded, so it is refused BY NAME instead. (0 stays legal: it is
        // the field's own default and now means what it always documented, "this tick only".)
        if (!latchLegitimate && (trigger.LatchSeconds != 0f)) {
            errors.Add(item: $"{path}.latchSeconds {trigger.LatchSeconds} is refused — the release channel latches nothing, so only 0 is legitimate here (a press buffer is authored on onPress).");
        }

        ValidatePredicate(predicate: trigger.Gate, stateSlots: stateSlots, path: $"{path}.gate", errors: errors);

        if (trigger.Effects is not { Count: > 0 } effects) {
            errors.Add(item: $"{path}.effects must be non-empty on a present trigger.");

            return;
        }

        for (var index = 0; (index < effects.Count); index++) {
            ValidateEffect(effect: effects[index], stateSlots: stateSlots, targetRegisterNames: targetRegisterNames, stateRows: stateRows, path: $"{path}.effects[{index}]", errors: errors);
        }
    }

    private static void ValidatePredicate(ActionPredicate? predicate, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, string path, List<string> errors) {
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
            case ActionPredicate.CompareState compare:
                // A per-body action-state slot is not keyed; `key` addresses a WORLD state row's cell and is
                // legitimate only in a world rule. Refused rather than parsed and discarded.
                if (compare.Key is not null) {
                    errors.Add(item: $"{path}.key '{compare.Key}' is refused at body scope — a per-body action-state slot is not keyed (a 'key' addresses a world state row's cell, in the rules section).");
                }
                // A comparand ROW reference addresses a world state row or a reserved per-tick channel; a per-body
                // action-state slot has neither, so the second spelling is legitimate only in a world rule.
                if ((compare.ComparandState is not null) || (compare.ComparandKey is not null)) {
                    errors.Add(item: $"{path}.comparandState/comparandKey is refused at body scope — a comparand row reference addresses a world state row or a reserved channel, legitimate only in a world rule.");
                }
                if (!stateSlots.TryGetValue(key: compare.State, value: out var compareSlot)) {
                    errors.Add(item: $"{path}.state '{compare.State}' names no declared action state.");
                } else if (compareSlot.Kind != ActionStateKind.Counter) {
                    errors.Add(item: $"{path}.state '{compare.State}' is a timer; compareState requires a counter.");
                }
                if (!Enum.IsDefined(value: compare.Comparison)) {
                    errors.Add(item: $"{path}.comparison '{compare.Comparison}' is not a defined ActionStateComparison.");
                }
                if (compare.Value is not { } compareValue) {
                    errors.Add(item: $"{path}.value is required at body scope — a per-body predicate names an authored constant (a comparand row reference is legitimate only in a world rule).");
                } else {
                    RequireFinite(value: compareValue, name: $"{path}.value", errors: errors);
                }
                break;
            case ActionPredicate.TimerElapsed elapsed:
                if (!stateSlots.TryGetValue(key: elapsed.State, value: out var timerSlot)) {
                    errors.Add(item: $"{path}.state '{elapsed.State}' names no declared action state.");
                } else if (timerSlot.Kind != ActionStateKind.Timer) {
                    errors.Add(item: $"{path}.state '{elapsed.State}' is a counter; timerElapsed requires a timer.");
                }
                break;
            case ActionPredicate.All all:
                if (all.Predicates is not { Count: > 0 } inner) {
                    errors.Add(item: $"{path}.all must contain at least one predicate.");

                    break;
                }

                for (var index = 0; (index < inner.Count); index++) {
                    ValidatePredicate(predicate: inner[index], stateSlots: stateSlots, path: $"{path}.all[{index}]", errors: errors);
                }

                break;
            default:
                errors.Add(item: $"{path} is an unknown predicate kind.");
                break;
        }
    }

    private static void ValidateEffect(ActionEffect effect, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, ISet<string> targetRegisterNames, IReadOnlyDictionary<string, WorldStateRow> stateRows, string path, List<string> errors) {
        if ((effect is not null) && !Enum.IsDefined(value: TargetOf(effect))) {
            errors.Add(item: $"{path}.target '{TargetOf(effect)}' is not a defined ActionTarget.");
        }

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
                } else {
                    // The runtime rides BodyDirection AS AUTHORED — it is never normalized, only rotated and scaled by
                    // Speed (WorldBody's PlanarImpulse op) — so an unnormalized direction silently rescales the impulse:
                    // an author who typo'd (3, 0, 4) meaning +X gets a 5x speed, not a refusal.
                    var magnitude = impulse.BodyDirection.Length();

                    if (MathF.Abs(magnitude - 1f) > PlanarImpulseUnitDirectionTolerance) {
                        errors.Add(item: $"{path}.bodyDirection {impulse.BodyDirection} has magnitude {magnitude}, not 1 — PlanarImpulse rides BodyDirection as authored (never normalized), so a non-unit direction silently rescales Speed ({impulse.Speed}).");
                    }
                }

                break;
            case ActionEffect.SetState set:
                RefuseKey(key: set.Key, verb: "setState");
                RefuseFromOperand(fromState: set.FromState, fromKey: set.FromKey, verb: "setState");
                RefuseValueSeconds(valueSeconds: set.ValueSeconds, verb: "setState");
                ValidateCounterState(name: set.State, value: set.Value);
                break;
            case ActionEffect.AddState add:
                RefuseKey(key: add.Key, verb: "addState");
                RefuseFromOperand(fromState: add.FromState, fromKey: add.FromKey, verb: "addState");
                RefuseValueSeconds(valueSeconds: add.ValueSeconds, verb: "addState");
                ValidateCounterState(name: add.State, value: add.Value);
                break;
            case ActionEffect.CountdownState:
                errors.Add(item: $"{path} authors a WORLD state-row countdown, which has no body-scope meaning — admissible only inside a world rule's own effects.");
                break;
            case ActionEffect.StartTimer timer:
                if (!stateSlots.TryGetValue(key: timer.State, value: out var timerSlot)) {
                    errors.Add(item: $"{path}.state '{timer.State}' names no declared action state.");
                } else if (timerSlot.Kind != ActionStateKind.Timer) {
                    errors.Add(item: $"{path}.state '{timer.State}' is a counter; startTimer requires a timer.");
                }
                RequireNonNegative(value: timer.Seconds, name: $"{path}.seconds", errors: errors);
                break;
            case ActionEffect.Designate designate:
                if (string.IsNullOrWhiteSpace(value: designate.Register)) {
                    errors.Add(item: $"{path}.register must be non-empty.");
                } else if (!targetRegisterNames.Contains(item: designate.Register)) {
                    errors.Add(item: $"{path}.register '{designate.Register}' names no target register.");
                }
                if (designate.Target != ActionTarget.AffectingSubject) {
                    errors.Add(item: $"{path}.target must be AffectingSubject.");
                }
                break;
            case ActionEffect.Generate generate:
                // The ONE effect admissible at both scopes: its names address world `state` rows, so they resolve
                // against the SAME row map a world rule's own generate effect resolves against. Refusing here means a
                // kit naming a dead generator refuses at LOAD, not at first fire.
                ValidateGenerateEffect(row: generate.Row, stateRows: stateRows, path: path, errors: errors);
                break;
            // upsertHudPanel/removeHudPanel/upsertPlacement/removePlacement author WORLD document rows, and save
            // performs WORLD-scope engine I/O — a per-body action has none of either, so all five are refused BY NAME
            // here (this is the check that actually surfaces: it runs before CompiledBodyMotionProgram.Compile's own
            // mirroring refusal in WorldDefinition.cs, which a passing ValidateEffect never lets a candidate reach).
            case ActionEffect.UpsertHudPanel or ActionEffect.RemoveHudPanel or ActionEffect.UpsertPlacement or ActionEffect.RemovePlacement:
                errors.Add(item: $"{path} authors a WORLD document row, which has no body-scope meaning — admissible only inside a world rule's own effects.");
                break;
            case ActionEffect.Save:
                errors.Add(item: $"{path} has no body-scope meaning — a per-body action has no world file of its own to save, and is admissible only inside a world rule's own effects.");
                break;
            default:
                errors.Add(item: $"{path} is an unknown effect kind.");
                break;
        }

        void RefuseKey(string? key, string verb) {
            if (key is not null) {
                errors.Add(item: $"{path}.key '{key}' is refused at body scope — '{verb}' writes a per-body action-state slot, which is not keyed (a 'key' addresses a world state row's cell, in the rules section).");
            }
        }

        // setState/addState's live copy source ('fromState'/'fromKey') addresses a WORLD state row or reserved
        // channel — a per-body action-state slot has neither, so it is refused here on the same terms RefuseKey
        // already refuses a per-body 'key' (legitimate only in a world rule, via WorldRuleCompiler).
        void RefuseFromOperand(string? fromState, string? fromKey, string verb) {
            if ((fromState is not null) || (fromKey is not null)) {
                errors.Add(item: $"{path}.fromState/fromKey are refused at body scope — '{verb}' writes a per-body action-state slot, which has no world state row to copy from (a live copy source is legitimate only in a world rule).");
            }
        }

        // 'valueSeconds' authors an engine-tick countdown against a WORLD state row a companion countdownState effect
        // consumes once per tick — a per-body action-state slot has no such row, so it is refused here on the same
        // terms RefuseFromOperand already refuses a per-body live copy source.
        void RefuseValueSeconds(decimal? valueSeconds, string verb) {
            if (valueSeconds is not null) {
                errors.Add(item: $"{path}.valueSeconds is refused at body scope — '{verb}' writes a per-body action-state slot via 'value', or starts a proper timer via 'startTimer'; 'valueSeconds' is legitimate only in a world rule.");
            }
        }

        void ValidateCounterState(string name, float? value) {
            if (!stateSlots.TryGetValue(key: name, value: out var slot)) {
                errors.Add(item: $"{path}.state '{name}' names no declared action state.");
            } else if (slot.Kind != ActionStateKind.Counter) {
                errors.Add(item: $"{path}.state '{name}' is a timer; this effect requires a counter.");
            }

            if (value is not { } constant) {
                errors.Add(item: $"{path}.value is required at body scope — a live copy source ('fromState') is legitimate only in a world rule.");
            } else {
                RequireFinite(value: constant, name: $"{path}.value", errors: errors);
            }
        }

        static ActionTarget TargetOf(ActionEffect value) => value switch {
            ActionEffect.SetVerticalVelocity item => item.Target,
            ActionEffect.ScaleVerticalVelocity item => item.Target,
            ActionEffect.PlanarImpulse item => item.Target,
            ActionEffect.SetState item => item.Target,
            ActionEffect.AddState item => item.Target,
            ActionEffect.StartTimer item => item.Target,
            ActionEffect.Designate item => item.Target,
            _ => ActionTarget.Self,
        };
    }

    /// <summary>Returns the one <c>generate</c>-effect name check, shared by both scopes that can author one: a kit action
    /// (here, through <c>ValidateEffect</c>) and a world rule (through <see cref="WorldRuleCompiler"/>, which refuses
    /// by throwing). One rule, two callers — never two readings of the same requirement.</summary>
    private static void ValidateGenerateEffect(string row, IReadOnlyDictionary<string, WorldStateRow> stateRows, string path, List<string> errors) {
        if (!stateRows.TryGetValue(key: (row ?? string.Empty), value: out var destination)) {
            errors.Add(item: $"{path}.row '{row}' names no state row.");

            return;
        }

        if (destination.Draw is not { } draw) {
            errors.Add(item: $"{path}.row '{row}' declares no draw — 'generate' redraws a draw site.");

            return;
        }

        if (draw.Timing == WorldDrawTiming.Boot) {
            errors.Add(item: $"{path}.row '{row}' declares timing=boot — it draws once at first fill and is never redrawn.");
        }

    }

    /// <summary>Validates the <c>rules</c> section by compiling it — <see cref="WorldRuleCompiler"/> owns which
    /// predicate/effect kinds are admissible at world scope and which names resolve, so this pass calls it and
    /// reports its by-name refusal rather than restating the rule set (the exact division
    /// <c>BodyMotionProgramException</c> already has for kit programs).</summary>
    private static void ValidateRules(IReadOnlyList<WorldRule>? rules, WorldDefinition definition, List<string> errors) {
        if (rules is not { Count: > 0 }) {
            return;
        }

        for (var index = 0; (index < rules.Count); index++) {
            var rule = rules[index];
            var path = $"rules[{index}]";

            if (rule is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!Enum.IsDefined(value: rule.Mode)) {
                errors.Add(item: $"{path}.mode '{rule.Mode}' is not a defined ActionTriggerMode.");
            }

            if (rule.Effects is not { Count: > 0 }) {
                errors.Add(item: $"{path}.effects must be non-empty — a rule that does nothing is a rule nothing can read back.");
            }
        }

        try {
            _ = WorldRuleCompiler.CompileAll(definition: definition);
        } catch (WorldRuleException exception) {
            errors.Add(item: exception.Message);
        }
    }

    /// <summary>Validates the <c>properties</c> section — the group-kind-name validated-vocabulary pattern
    /// (<see cref="ValidateGroups"/>) applied to a carrier property name: unique, a legitimate identifier, and backed
    /// by a declared keyed <c>int</c> <c>state</c> row of the same name (see
    /// <see cref="WorldPropertyRegistrySection"/>'s remarks for why storage rides the state substrate rather than a
    /// second one).</summary>
    private static void ValidateProperties(WorldPropertyRegistrySection? properties, Dictionary<string, WorldStateRow> stateRows, List<string> errors) {
        if (properties is null) {
            return;
        }

        if (properties.Names.Count > WorldPropertyCapacity.MaxProperties) {
            errors.Add(item: $"properties.names count {properties.Names.Count} exceeds the maximum of {WorldPropertyCapacity.MaxProperties}.");
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < properties.Names.Count); index++) {
            var name = properties.Names[index];
            var path = $"properties.names[{index}]";

            if (string.IsNullOrWhiteSpace(value: name)) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!WorldCellName.TryParse(candidate: name, name: out _, reason: out var nameReason)) {
                errors.Add(item: $"{path} '{name}' {nameReason}.");

                continue;
            }

            if (!seen.Add(item: name)) {
                errors.Add(item: $"{path} '{name}' is duplicated.");

                continue;
            }

            if (!stateRows.TryGetValue(key: name, value: out var row)) {
                errors.Add(item: $"{path} '{name}' names no declared state row — a property's per-carrier tags are stored in a keyed int state row of the SAME name; declare it first with world.row.set state.");
            } else if (row.Kind != CellKind.Int) {
                errors.Add(item: $"{path} '{name}' names state row '{name}', which is kind={row.Kind.ToString().ToLowerInvariant()} — a property's per-carrier tags are stored as kind=int.");
            } else if (!row.IsKeyed) {
                errors.Add(item: $"{path} '{name}' names state row '{name}', which is not keyed — a property's per-carrier tags are one cell per carrier (a keyed row, exactly like an argmax-eligible tally); author it with a 'capacity' (or several cells) so it is keyed.");
            }
        }
    }

    /// <summary>Validates the <c>interactions</c> section by compiling it — <see cref="WorldRuleCompiler.CompileAllInteractions"/>
    /// owns which co-occurrence/effect kinds are admissible and which names resolve (the property registry, a region
    /// placement), so this pass calls it and reports its by-name refusal, mirroring <see cref="ValidateRules"/>'s own
    /// division against <see cref="WorldRuleCompiler.CompileAll"/>.</summary>
    private static void ValidateInteractions(WorldInteractionsSection? interactions, WorldDefinition definition, List<string> errors) {
        var rows = (interactions?.Interactions ?? []);

        if (rows.Count == 0) {
            return;
        }

        if (rows.Count > WorldInteractionCapacity.MaxInteractions) {
            errors.Add(item: $"interactions count {rows.Count} exceeds the maximum of {WorldInteractionCapacity.MaxInteractions}.");
        }

        for (var index = 0; (index < rows.Count); index++) {
            var interaction = rows[index];
            var path = $"interactions[{index}]";

            if (interaction is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!Enum.IsDefined(value: interaction.Mode)) {
                errors.Add(item: $"{path}.mode '{interaction.Mode}' is not a defined ActionTriggerMode.");
            }

            if (!Enum.IsDefined(value: interaction.CoOccurrence)) {
                errors.Add(item: $"{path}.coOccurrence '{interaction.CoOccurrence}' is not a defined WorldInteractionCoOccurrence.");
            }

            if (interaction.Effects is not { Count: > 0 }) {
                errors.Add(item: $"{path}.effects must be non-empty — an interaction that does nothing is one nothing can read back.");
            }

            if ((interaction.CoOccurrence == WorldInteractionCoOccurrence.Distance) && (interaction.Range < 0f)) {
                errors.Add(item: $"{path}.range {interaction.Range} is negative — a distance threshold cannot be negative.");
            }
        }

        try {
            _ = WorldRuleCompiler.CompileAllInteractions(definition: definition);
        } catch (WorldRuleException exception) {
            errors.Add(item: exception.Message);
        }
    }

    // The one screen-source gate, shared by a declared source and every magazine entry — a pure extraction that closes a
    // real duplication risk (a magazine entry could otherwise name an undeclared camera). Returns whether the source is a
    // live CONSOLE (the caller counts these against the one-live ceiling).
    private static bool ValidateScreenSource(WorldScreenSource source, string path, HashSet<string> cameras, HashSet<string> destinationNames, List<string> errors) {
        switch (source) {
            case null:
                errors.Add(item: $"{path} is required.");

                return false;
            case WorldScreenSource.Machine machine:
                if (string.IsNullOrWhiteSpace(value: machine.Engine)) {
                    errors.Add(item: $"{path}.machine.engine is required.");
                } else if (!WorldExtensionVocabularyHook.IsRegisteredScreenMachineEngine(engineId: machine.Engine)) {
                    // Deny-by-default: an engine key the host never registered refuses HERE, at load, by name — not a
                    // per-slot boot fault discovered only once WorldMachineHost tries to resolve it (screen.state
                    // reported the fault, but boot itself succeeded regardless). The hook is REQUIRED, never skipped
                    // when absent: an unchecked key is the one outcome this refusal exists to prevent.
                    errors.Add(item: $"{path}.machine.engine '{machine.Engine}' names no registered screen-machine engine.");
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
            case WorldScreenSource.Qr qr:
                ValidateQr(qr: qr, path: path, errors: errors);

                return false;
            case WorldScreenSource.Session session:
                // No placement face reaches here (a top-level screens row or magazine entry) — portal:null makes
                // ValidateSessionSource refuse Window unconditionally, which is correct: there is no face for a
                // portal facet to sit on, so no counterpart can ever pair with a window authored at this position.
                ValidateSessionSource(session: session, destinationNames: destinationNames, portal: null, path: path, errors: errors);

                return false;
            default:
                return false;
        }
    }

    // The session-source gate, shared by a declared/magazine-entry source (which carries the current document's
    // destinationNames) and a placement face override (ValidateFaceSources, which already threads destinationNames
    // for the PORTAL facet on the same row). Destination must name a declared destinations row — the row's own
    // resolution (reference/instance/generation) is a bind-time fact this pass cannot see (see docs/world-model.md).
    // Camera, when present, is validated only as non-empty here — the destination's own definition is not joined at
    // boot, so an unknown camera name is a loud bind-time refusal (WorldScreenBinder), never a boot refusal.
    private static void ValidateSessionSource(WorldScreenSource.Session session, HashSet<string> destinationNames, WorldPlacementPortal? portal, string path, List<string> errors) {
        if (string.IsNullOrWhiteSpace(value: session.Destination) || !destinationNames.Contains(item: session.Destination)) {
            errors.Add(item: (destinationNames.Count > 0)
                ? $"{path}.session.destination '{session.Destination}' names no destinations row; the world declares: {string.Join(separator: ", ", values: destinationNames)}."
                : $"{path}.session.destination '{session.Destination}' names no destinations row; the world declares none.");
        }

        if ((session.CameraName is { } camera) && string.IsNullOrWhiteSpace(value: camera)) {
            errors.Add(item: $"{path}.session.camera must be non-empty when present.");
        }

        // WINDOW needs the SAME face's own portal facet: the aperture (WorldFaceCatalog) and the isometry that maps
        // the viewer's eye through it both come from the SAME mapped border pair, so a face with no counterpart has
        // no destination-space frame to fit an off-axis frustum against. A top-level screens row or magazine entry
        // passes portal:null unconditionally (see the two call sites) and is refused here for the identical reason.
        if (session.Projection == WorldScreenProjection.Window) {
            if (portal is not { Arrival: WorldPortalArrival.Mapped, Counterpart: not null }) {
                errors.Add(item: $"{path}.session.projection 'window' requires THIS SAME face's portal facet to author arrival 'mapped' with a counterpart — a window has no destination-space aperture to fit a frustum against without a mapped border pair.");
            }
        }

        if (session.Resolution is { } resolution) {
            if ((resolution.Width <= 0) || (resolution.Height <= 0) ||
                (resolution.Width > MaxSurfaceDimension) || (resolution.Height > MaxSurfaceDimension)) {
                errors.Add(item: $"{path}.session.resolution [{resolution.Width}, {resolution.Height}] must be within 1..{MaxSurfaceDimension} on each axis.");
            }
        }
    }

    // Document-wide: the simultaneous-window ceiling (WorldSessionWindowCapacity.MaxSimultaneousWindows), refused BY
    // NAME rather than silently degrading — a window is an UNBUDGETED render (Puck.SdfVm.Views.WorldSessionView pays
    // a full offscreen submit every produced frame for one), so the count is a real, additive per-frame GPU cost the
    // engine cannot absorb past that ceiling. A standalone pass (not folded into ValidateFaceSources) because it
    // needs the WHOLE document's placements at once, not one placement's own rows.
    private static void ValidateSessionWindowBudget(IReadOnlyList<WorldPlacement> placements, List<string> errors) {
        var windows = new List<string>();

        foreach (var placement in placements) {
            if (placement is null) {
                continue;
            }

            foreach (var face in (placement.FaceSources ?? [])) {
                if ((face is not null) && (face.Source is WorldScreenSource.Session { Projection: WorldScreenProjection.Window })) {
                    windows.Add(item: $"{placement.Id}/{face.Face}");
                }
            }
        }

        if (windows.Count > WorldSessionWindowCapacity.MaxSimultaneousWindows) {
            errors.Add(item: $"placements author {windows.Count} 'window'-projection session face(s) ({string.Join(separator: ", ", values: windows)}), exceeding the simultaneous-window budget of {WorldSessionWindowCapacity.MaxSimultaneousWindows} — a window renders every produced frame regardless of whether anyone is looking through it, so this ceiling is a real GPU cost bound, not a taste one. Drop a window to a plain 'camera' projection or remove a face.");
        }
    }

    // The QR source gate: a non-empty payload that FITS the encoder's supported version range at the requested level, a
    // recognized EC-level letter, and a non-negative quiet zone. The capacity question is asked of the ENCODER
    // (QrEncoder.TryFindVersion) rather than re-derived here, so an authoring-time refusal names the identical byte
    // count and capacity a live screen.source <index> qr refusal names — one arithmetic, one message, no drift.
    private static void ValidateQr(WorldScreenSource.Qr qr, string path, List<string> errors) {
        if (string.IsNullOrEmpty(value: qr.Payload)) {
            errors.Add(item: $"{path}.qr.payload is required.");

            return;
        }

        if (!QrErrorCorrection.TryParse(text: qr.EcLevel, level: out var level)) {
            errors.Add(item: $"{path}.qr.ecLevel '{qr.EcLevel}' must be one of {QrErrorCorrection.Vocabulary}.");

            return;
        }

        if (qr.QuietZoneModules < 0) {
            errors.Add(item: $"{path}.qr.quietZoneModules {qr.QuietZoneModules} must be non-negative.");
        }

        if (!QrEncoder.TryFindVersion(payloadByteCount: Encoding.UTF8.GetByteCount(s: qr.Payload), level: level, version: out _, error: out var capacityError)) {
            errors.Add(item: $"{path}.qr.payload: {capacityError}.");
        }
    }

    // The engage-route policy: a finite non-negative radius, plus authored channel names (kebab-case, non-empty),
    // plus the context-routes widening's two route-row fields: the channel MASK (channelNames must resolve) and the
    // authored TRANSLATION table (each row's channel must resolve to a defined WorldPadElement). engageChannel is
    // CONSUMED (WorldServer.ResolveEngageProbes resolves it against the same declared-channel ordinal table), so it
    // is held to the same "must resolve" bar — a misspelled name is otherwise a silent, permanent no-op. cycleChannel
    // stays unconsumed (no reader exists yet) and keeps its lighter kebab-case-only bar.
    private static void ValidateRoute(WorldScreenRoute route, string path, ISet<string> channelNames, List<string> errors) {
        if (!float.IsFinite(f: route.EngageRadius) || (route.EngageRadius < 0f)) {
            errors.Add(item: $"{path}.engageRadius {route.EngageRadius} must be finite and non-negative.");
        }

        ValidateChannel(channel: route.EngageChannel, name: $"{path}.engageChannel", errors: errors);
        ValidateChannel(channel: route.CycleChannel, name: $"{path}.cycleChannel", errors: errors);

        if ((route.EngageChannel is { Length: > 0 } engageChannel) && !channelNames.Contains(item: engageChannel)) {
            errors.Add(item: $"{path}.engageChannel '{engageChannel}' names no declared channel.");
        }

        if (!route.Engageable && ((route.EngageChannel is not null) || (route.CycleChannel is not null))) {
            errors.Add(item: $"{path} names an engageChannel/cycleChannel but engageable is false — a screen cannot answer a gesture it can never be engaged from.");
        }

        if (route.Channels is { } mask) {
            if (mask.Count == 0) {
                errors.Add(item: $"{path}.channels omit the field for 'reach everything' instead of an empty list — an authored empty mask reaches nothing by accident.");
            }

            for (var index = 0; (index < mask.Count); index++) {
                if (!channelNames.Contains(item: mask[index])) {
                    errors.Add(item: $"{path}.channels[{index}] '{mask[index]}' names no declared channel.");
                }
            }
        }

        if (route.Translation is { } translation) {
            for (var index = 0; (index < translation.Count); index++) {
                var row = translation[index];

                if (!channelNames.Contains(item: row.Channel)) {
                    errors.Add(item: $"{path}.translation[{index}].channel '{row.Channel}' names no declared channel.");
                }

                if (!Enum.IsDefined(value: row.Element)) {
                    errors.Add(item: $"{path}.translation[{index}].element '{row.Element}' is not a defined WorldPadElement.");
                }
            }
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
    private static void ValidateMagazine(WorldScreenMagazine? magazine, string path, HashSet<string> cameras, HashSet<string> destinationNames, List<string> errors) {
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
            _ = ValidateScreenSource(source: value.Entries[index], path: $"{path}.entries[{index}]", cameras: cameras, destinationNames: destinationNames, errors: errors);
        }
    }

    // The cable links: name required/kebab/unique; two or more screens; every index declared; no duplicate within a link;
    // no screen in two links. NOT validated: engine identity of the members — that is a RUNTIME fact (a screen.insert
    // changes it), so the binder reports a dormant link with a reason rather than the validator rejecting the row.
    private static void ValidateLinks(IReadOnlyList<WorldScreenLink> links, HashSet<int> screenIndices, List<string> errors) {
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

    private static void ValidateCollider(WorldCollider? collider, IReadOnlyList<WorldCreation> creations, string path, List<string> errors) {
        if (collider is null) {
            return;
        }

        switch (collider) {
            case WorldCollider.Sphere sphere:
                RequirePositive(value: sphere.Radius, name: $"{path}.radius", errors: errors);
                break;
            case WorldCollider.Capsule capsule:
                RequirePositive(value: capsule.Radius, name: $"{path}.radius", errors: errors);

                if (!IsFinite(value: capsule.Endpoint) || (capsule.Endpoint.LengthSquared() <= 0f)) {
                    errors.Add(item: $"{path}.endpoint must be finite and nonzero; use a sphere for a zero-length capsule.");
                }
                break;
            case WorldCollider.Box box:
                if (!IsFinite(value: box.HalfExtents) || (box.HalfExtents.X <= 0f) || (box.HalfExtents.Y <= 0f) || (box.HalfExtents.Z <= 0f)) {
                    errors.Add(item: $"{path}.halfExtents must contain finite positive coordinates.");
                }

                var rotationLength = box.Rotation.LengthSquared();
                if (!float.IsFinite(f: rotationLength) || (rotationLength <= 0f)) {
                    errors.Add(item: $"{path}.rotation must be finite and nonzero.");
                }
                break;
            case WorldCollider.FromCreation fromCreation:
                if (string.IsNullOrWhiteSpace(value: fromCreation.CreationId) ||
                    (WorldDefinitionRows.FindCreation(creations: creations, id: fromCreation.CreationId) is not { } creation)) {
                    errors.Add(item: $"{path}.creationId '{fromCreation.CreationId}' names no creation row.");
                    break;
                }

                var shapes = (creation.Document.Shapes ?? []);
                if (shapes.Count < 1) {
                    errors.Add(item: $"{path} creation '{fromCreation.CreationId}' emits no body-collider volumes.");
                } else if (shapes.Count > WorldCollider.MaxVolumes) {
                    errors.Add(item: $"{path} creation '{fromCreation.CreationId}' emits {shapes.Count} volumes, exceeding the {WorldCollider.MaxVolumes}-volume body-collider ceiling.");
                }

                for (var index = 0; (index < shapes.Count); index++) {
                    if (shapes[index].Type == AvatarPrimitive.Plane) {
                        errors.Add(item: $"{path} creation '{fromCreation.CreationId}' shape {index} is an unbounded plane, not a finite body volume.");
                    }
                }
                break;
            default:
                errors.Add(item: $"{path} has an unknown collider kind.");
                break;
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
