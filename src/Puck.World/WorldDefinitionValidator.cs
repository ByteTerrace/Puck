using System.Numerics;
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
    private const float MinimumBasisLengthSquared = 1e-8f;

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

        if ((definition.Population.NetworkPlayers < 0) || (definition.Population.NetworkPlayers > WorldPopulation.MaxSimulated)) {
            errors.Add(item: $"population.networkPlayers {definition.Population.NetworkPlayers} is outside 0..{WorldPopulation.MaxSimulated}.");
        }

        ValidateScene(scene: definition.Scene, errors: errors);
        ValidateSpawnPoints(spawnPoints: definition.SpawnPoints, errors: errors);

        if (definition.Render is null) {
            errors.Add(item: "render is required.");
        }

        var kitNames = ValidateKits(definition: definition, errors: errors);

        ValidateAssignment(assignment: definition.Assignment, kitNames: kitNames, errors: errors);
        ValidateAddons(addons: definition.Addons, errors: errors);
        ValidateBindingOverlays(overlays: definition.BindingOverlays, errors: errors);
        ValidateStorage(storage: definition.Storage, errors: errors);

        // The editor/authoring policy row (P5.5): absent-in-JSON coalesces to the built-in default HERE (the
        // WorldStorageDefaults absence convention) so every downstream read below sees a concrete row, never null.
        var authoring = (definition.Authoring ?? WorldAuthoringDefaults.Default);

        ValidateAuthoring(authoring: authoring, errors: errors);

        var creationIds = ValidateCreations(creations: definition.Creations, errors: errors);

        ValidatePlacements(placements: definition.Placements, creations: definition.Creations, creationIds: creationIds, authoring: authoring, errors: errors);

        var cameras = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (definition.Cameras is not { } authoredCameras) {
            errors.Add(item: "cameras is required.");
        } else {
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

                switch (camera) {
                    case WorldCamera.Fixed fixedCamera when !IsFinite(value: fixedCamera.Position) || !IsFinite(value: fixedCamera.LookAt) || (fixedCamera.Position == fixedCamera.LookAt):
                        errors.Add(item: $"{path} needs finite, distinct position and lookAt points.");
                        break;
                    case WorldCamera.Anchored anchoredCamera:
                        if ((anchoredCamera.AnchorIndex < 0) || (anchoredCamera.AnchorIndex >= WorldPopulation.MaxPopulation)) {
                            errors.Add(item: $"{path}.anchorIndex {anchoredCamera.AnchorIndex} is outside 0..{(WorldPopulation.MaxPopulation - 1)}.");
                        }

                        if (!IsFinite(value: anchoredCamera.Offset)) {
                            errors.Add(item: $"{path}.offset must contain finite coordinates.");
                        }

                        break;
                }

                if ((camera.RenderWidth == 0U) || (camera.RenderHeight == 0U) ||
                    (camera.RenderWidth > MaxSurfaceDimension) || (camera.RenderHeight > MaxSurfaceDimension)) {
                    errors.Add(item: $"{path} render dimensions must be within 1..{MaxSurfaceDimension}.");
                }

                if (!float.IsFinite(f: camera.FieldOfViewRadians) || (camera.FieldOfViewRadians <= 0f) || (camera.FieldOfViewRadians >= MathF.PI)) {
                    errors.Add(item: $"{path}.fieldOfViewRadians must be finite and between 0 and pi.");
                }
            }
        }

        var screenIndices = new HashSet<int>();

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

                switch (screen.Source) {
                    case null:
                        errors.Add(item: $"{path}.source is required.");
                        break;
                    case WorldScreenSource.Machine machine:
                        if (string.IsNullOrWhiteSpace(value: machine.Engine)) {
                            errors.Add(item: $"{path}.source.machine.engine is required.");
                        }

                        // An empty contentPath is a valid "unconfigured" screen (the built-in default for the native AGB
                        // screen — asset-free, no owner-local path baked in): the binder faults the slot gracefully at
                        // boot rather than crashing here. A present-but-missing file is likewise a runtime fact, not a
                        // structural authoring error, so only WorldScreenBinder checks existence.
                        break;
                    case WorldScreenSource.TestPattern pattern:
                        if ((pattern.Width <= 0) || (pattern.Height <= 0) ||
                            (pattern.Width > MaxSurfaceDimension) || (pattern.Height > MaxSurfaceDimension)) {
                            errors.Add(item: $"{path}.source test-pattern dimensions must be within 1..{MaxSurfaceDimension}.");
                        }

                        break;
                    case WorldScreenSource.Camera camera:
                        ValidateProfile(profile: camera.Profile, path: $"{path}.source.camera", errors: errors);

                        break;
                    case WorldScreenSource.Capture capture:
                        // Selector: monitor mode validates the index; window mode requires a title (its unused counterpart).
                        if (capture.MonitorIndex is { } monitorIndex) {
                            if (monitorIndex < 0) {
                                errors.Add(item: $"{path}.source.capture.monitorIndex must be non-negative.");
                            }
                        } else if (string.IsNullOrWhiteSpace(value: capture.WindowTitle)) {
                            errors.Add(item: $"{path}.source.capture.windowTitle is required.");
                        }

                        ValidateProfile(profile: capture.Profile, path: $"{path}.source.capture", errors: errors);

                        break;
                    case WorldScreenSource.View view when !cameras.Contains(item: view.CameraName):
                        errors.Add(item: $"{path}.source.view references undeclared camera '{view.CameraName}'.");

                        break;
                }
            }
        }

        if (errors.Count > 0) {
            throw new InvalidOperationException(message: $"Invalid WorldDefinition:{Environment.NewLine} - {string.Join(separator: $"{Environment.NewLine} - ", values: errors)}");
        }
    }

    // The static scene (PRESENTATION-ONLY): albedos and row geometry are gated only for structural GPU safety
    // (finite colors, finite centers, positive extents, non-negative blends) plus row-id presence and uniqueness.
    private static void ValidateScene(WorldScene scene, List<string> errors) {
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

            switch (row) {
                case WorldSceneRow.Boulder boulder:
                    RequirePositive(value: boulder.Radius, name: $"{path}.radius", errors: errors);
                    RequireNonNegative(value: boulder.Smooth, name: $"{path}.smooth", errors: errors);

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

                    break;
                default:
                    errors.Add(item: $"{path} is an unknown scene-row kind.");

                    break;
            }
        }
    }

    // The seat spawns (SIM-AFFECTING placement): id presence/uniqueness plus finite positions, and enough rows to cover
    // every local slot (order maps slots).
    private static void ValidateSpawnPoints(IReadOnlyList<WorldSpawnPoint> spawnPoints, List<string> errors) {
        if (spawnPoints is null) {
            errors.Add(item: "spawnPoints is required.");

            return;
        }

        if (spawnPoints.Count < WorldPopulation.LocalSeatCount) {
            errors.Add(item: $"spawnPoints provides {spawnPoints.Count} entries; {WorldPopulation.LocalSeatCount} local slots require at least that many.");
        }

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
        }
    }

    // The kit rows (SIM-AFFECTING): name presence/uniqueness, the seat-kit reference, a defined motion model, and the
    // tuning/flavor/action rows that compile to fixed point. Returns the resolved kit-name set for the assignment gate.
    private static HashSet<string> ValidateKits(WorldDefinition definition, List<string> errors) {
        var kitNames = new HashSet<string>(comparer: StringComparer.Ordinal);

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

            if (!Enum.IsDefined(value: kit.Model)) {
                errors.Add(item: $"{path}.model '{kit.Model}' is not a defined MotionModel.");
            }

            ValidateMotionTuning(tuning: kit.Tuning, path: $"{path}.tuning", errors: errors);
            ValidateWanderFlavor(flavor: kit.Flavor, path: $"{path}.flavor", errors: errors);
            ValidateActionSpec(spec: kit.PrimaryAction, path: $"{path}.primaryAction", errors: errors);
            ValidateActionSpec(spec: kit.SecondaryAction, path: $"{path}.secondaryAction", errors: errors);
        }

        if (!kitNames.Contains(item: definition.DefaultSeatKit)) {
            errors.Add(item: $"defaultSeatKit '{definition.DefaultSeatKit}' names no kit row.");
        }

        return kitNames;
    }

    // The kit assignment policy (SIM-AFFECTING): hash needs nothing more; table needs a non-empty cycle whose every
    // entry resolves to a declared kit name.
    private static void ValidateAssignment(WorldKitAssignment assignment, HashSet<string> kitNames, List<string> errors) {
        if (assignment is null) {
            errors.Add(item: "assignment is required.");

            return;
        }

        var isHash = string.Equals(a: assignment.Policy, b: WorldKitAssignment.HashPolicy, comparisonType: StringComparison.Ordinal);
        var isTable = string.Equals(a: assignment.Policy, b: WorldKitAssignment.TablePolicy, comparisonType: StringComparison.Ordinal);

        if (!isHash && !isTable) {
            errors.Add(item: $"assignment.policy '{assignment.Policy ?? "(absent)"}' must be '{WorldKitAssignment.HashPolicy}' or '{WorldKitAssignment.TablePolicy}'.");
        }

        var table = (assignment.Table ?? []);

        if (isTable && (table.Count == 0)) {
            errors.Add(item: $"assignment.table must be non-empty under the '{WorldKitAssignment.TablePolicy}' policy.");
        }

        for (var index = 0; (index < table.Count); index++) {
            if (!kitNames.Contains(item: table[index])) {
                errors.Add(item: $"assignment.table[{index}] '{table[index]}' names no kit row.");
            }
        }
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

    // The per-world binding overlays (§2.4): non-empty unique ids, and the COMPOSED result (engine default ⊕ every
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

    // The editor/authoring policy row (P5.5): every field finite/positive with a sane ceiling. The BOOT-CONSUMED
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
    }

    // The creation ASSET rows (§D6): id presence/uniqueness, the document's own strict schema + structural invariants
    // through CreationCanonicalizer (the ONE pipeline — never a re-implementation), the UIE-6 hash pin (the carried
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
        }

        return ids;
    }

    // The placement INSTANCE rows (§D6): id presence/uniqueness, the creation reference, finite transform, the policy
    // scale envelope, the repeat facet's positive counts / finite spacings, the mirror token, and the animated-row
    // constraints (static-only facets; the reserved replay-pool ceiling, word-exact).
    private static void ValidatePlacements(IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldCreation> creations, HashSet<string> creationIds, WorldAuthoringDefaults authoring, List<string> errors) {
        if (placements is null) {
            errors.Add(item: "placements is required.");

            return;
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);
        var animatedCount = 0;

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

            // The animated-row constraints: a placement of a framed creation replays through the reserved dynamic
            // pool — single copy only (repeat/mirror are static-stamp facets), and at most the reserved pool count.
            if (FindCreation(creations: creations, id: placement.CreationId) is { Document.Frames.Count: > 0 }) {
                animatedCount++;

                if ((placement.Repeat is not null) || (placement.Mirror is not null)) {
                    errors.Add(item: $"{path} is ANIMATED (its creation carries timeline frames) — repeat/mirror facets are static-stamp-only.");
                }
            }
        }

        if (animatedCount > WorldPlacementPolicy.MaxAnimatedPlacements) {
            errors.Add(item: $"{animatedCount} animated placements exceed the {WorldPlacementPolicy.MaxAnimatedPlacements}-slot replay pool.");
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

    // The storage host-section (§2.5.5, RESERVED): an endpoint must be an absolute URI when present; a user-id must be
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
