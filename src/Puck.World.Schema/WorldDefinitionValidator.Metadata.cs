using System.Numerics;
using System.Text.Json;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // A control character (newline/carriage-return included) or a literal ']' would let an authored string forge
    // the world.metadata verb's own bracketed console line — refused here rather than escaped at the verb.
    private static bool HasForgeableCharacter(string value) {
        foreach (var ch in value) {
            if (
                char.IsControl(c: ch) ||
                (ch == ']')
            ) {
                return true;
            }
        }

        return false;
    }
    // Linear scan over every divisor of 50400 (108 of them) — run at most once per Validate call, so simplicity beats
    // a precomputed table here.
    private static (int? Lower, int? Upper) NearestRateDivisors(int rate) {
        int? lower = null;
        int? upper = null;

        for (var candidate = 1; (((ulong)candidate) <= FixedTickConversion.TicksPerSecond); candidate++) {
            if ((FixedTickConversion.TicksPerSecond % ((ulong)candidate)) != 0UL) {
                continue;
            }

            if (candidate <= rate) {
                lower = candidate;
            }
            if (
                (candidate >= rate) &&
                (upper is null)
            ) {
                upper = candidate;
            }
        }

        return (lower, upper);
    }
    // Walks a metadata.custom value's whole object/array tree for '$drop'/'$replace' member names — the same
    // compose-time row vocabulary WorldDocumentBasis.MergeObject refuses at every depth of an overlay, refused here
    // too so authoring a nested offender surfaces at validation rather than as a deferred compose-time exception.
    private static void RefuseComposeVocabularyAtAnyDepth(JsonElement element, string path, List<string> errors) {
        if (element.ValueKind == JsonValueKind.Object) {
            foreach (var property in element.EnumerateObject()) {
                var memberPath = $"{path}.{property.Name}";

                if (
                    string.Equals(
                    a: property.Name,
                    b: WorldDocumentBasis.DropMemberName,
                    comparisonType: StringComparison.Ordinal
                ) ||
                    string.Equals(
                    a: property.Name,
                    b: WorldDocumentBasis.ReplaceMemberName,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    errors.Add(item: $"{memberPath} is compose-time row vocabulary ('{WorldDocumentBasis.DropMemberName}'/'{WorldDocumentBasis.ReplaceMemberName}'), not an authorable key.");
                } else {
                    RefuseComposeVocabularyAtAnyDepth(
                        element: property.Value,
                        errors: errors,
                        path: memberPath
                    );
                }
            }
        } else if (element.ValueKind == JsonValueKind.Array) {
            var index = 0;

            foreach (var item in element.EnumerateArray()) {
                RefuseComposeVocabularyAtAnyDepth(
                    element: item,
                    errors: errors,
                    path: $"{path}[{index}]"
                );
                index++;
            }
        }
    }
    private static void RequirePositiveFixedSlot(IReadOnlyDictionary<string, WorldStateRow> stateRows, string name, string path, List<string> errors) {
        if (
            stateRows.TryGetValue(
            key: name,
            value: out var row
        ) &&
            (row is { Kind: CellKind.Fixed, IsSlot: true }) &&
            (row.Cells![0].Value <= 0L)
        ) {
            errors.Add(item: $"{path} row '{name}' carries {((double)FixedQ4816.FromRawBits(value: row.Cells[0].Value)):0.####} — a locomotion rate must be positive.");
        }
    }
    // The gravitational field (SIM-AFFECTING). The constant is non-negative and the softening length positive (a
    // zero softening lets a coincident pair diverge); every attractor names a live placement, exactly once, with a
    // non-negative mass. An inactive section still validates: authoring the constants before the sources is legal.
    private static void ValidateGravity(WorldGravity gravity, IReadOnlyList<WorldPlacement> placements, List<string> errors) {
        if (!Enum.IsDefined(value: gravity.Solver)) {
            errors.Add(item: $"gravity.solver '{gravity.Solver}' is not defined.");
        }

        RequireNonNegative(
            value: gravity.GravitationalConstant,
            name: "gravity.gravitationalConstant",
            errors: errors
        );
        RequirePositive(
            value: gravity.SofteningLength,
            name: "gravity.softeningLength",
            errors: errors
        );

        if (gravity.Attractors is null) {
            errors.Add(item: "gravity.attractors is required.");

            return;
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < gravity.Attractors.Count); index++) {
            var attractor = gravity.Attractors[index];
            var path = $"gravity.attractors[{index}]";

            if (string.IsNullOrWhiteSpace(value: attractor.PlacementId)) {
                errors.Add(item: $"{path}.placementId is required.");
            } else {
                if (!seen.Add(item: attractor.PlacementId)) {
                    errors.Add(item: $"{path}.placementId duplicates '{attractor.PlacementId}'.");
                }

                if (WorldDefinitionRows.FindPlacement(
                    id: attractor.PlacementId,
                    placements: placements
                ) is null) {
                    errors.Add(item: $"{path}.placementId '{attractor.PlacementId}' resolves to no placement.");
                }
            }

            RequireNonNegative(
                value: attractor.Mass,
                name: $"{path}.mass",
                errors: errors
            );
        }
    }
    // The contact-solver tuning (SIM-AFFECTING). ContactSkin positive; MaxIterations 1..8 (above 8 is a solver
    // pathology, not a choice); requirements are unique; MaxSlopeDegrees in (0, 90) — 0 grounds nothing, 90 grounds a
    // wall; GradientProbe non-negative, and > 0 requires a field-selecting requirement.
    private static void ValidateCollision(WorldCollision collision, List<string> errors) {
        RequirePositive(
            value: collision.ContactSkin,
            name: "collision.contactSkin",
            errors: errors
        );
        RequireIntRange(
            value: collision.MaxIterations,
            min: 1,
            max: 8,
            name: "collision.maxIterations",
            errors: errors
        );

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

        RequireRange(
            value: collision.MaxSlopeDegrees,
            min: 0f,
            max: 90f,
            name: "collision.maxSlopeDegrees",
            errors: errors,
            minExclusive: true,
            maxExclusive: true
        );

        RequireNonNegative(
            value: collision.GradientProbe,
            name: "collision.gradientProbe",
            errors: errors
        );

        // A tiny positive step collapses every central-difference pair to equal taps (see SdfFieldEvaluator's
        // GradientEpsilon remarks on the quantize-to-equal floor), silently disabling contact resolution rather than
        // refusing loudly — so an authored non-zero step must clear the measured floor with headroom, not merely be
        // non-negative.
        if (
            float.IsFinite(f: collision.GradientProbe) &&
            (collision.GradientProbe >= 0f) &&
            (collision.GradientProbe != 0f) &&
            (collision.GradientProbe < 0.001f)
        ) {
            errors.Add(item: $"collision.gradientProbe must be exactly 0 (take the evaluator default) or >= 0.001 (was {collision.GradientProbe}).");
        }

        if (
            (collision.Requirements is { Count: 0 }) &&
            float.IsFinite(f: collision.GradientProbe) &&
            (collision.GradientProbe > 0f)
        ) {
            errors.Add(item: "collision.gradientProbe > 0 requires at least one field-contact requirement.");
        }
    }
    private static void ValidateDistribution(WorldDistribution distribution, string path, HashSet<string> spawnPointIds, bool allowDisc, bool allowPoints, bool allowLattice, bool allowZeroDisc, List<string> errors) {
        if (
            (distribution is null) ||
            (distribution.Region is null)
        ) {
            errors.Add(item: $"{path} requires region and fill members.");

            return;
        }

        switch (distribution.Region) {
            case WorldDistributionRegion.Disc disc when allowDisc:
                if (allowZeroDisc) {
                    RequireNonNegative(
                        value: disc.Radius,
                        name: $"{path}.region.radius",
                        errors: errors
                    );
                } else {
                    RequirePositive(
                        value: disc.Radius,
                        name: $"{path}.region.radius",
                        errors: errors
                    );
                }

                if (
                    (disc.SampleCount is { } samples) &&
                    (samples < 1)
                ) {
                    errors.Add(item: $"{path}.region.sampleCount must be positive when present.");
                }

                ValidateSequence(
                    sequence: distribution.Fill,
                    path: $"{path}.fill",
                    minIndex: 0,
                    errors: errors,
                    WorldSequence.Additive
                );
                break;
            case WorldDistributionRegion.Points points when allowPoints:
                if (points.Names is not { Count: > 0 }) {
                    errors.Add(item: $"{path}.region.names must contain at least one spawn-point name.");
                } else {
                    for (var index = 0; (index < points.Names.Count); index++) {
                        if (
                            string.IsNullOrWhiteSpace(value: points.Names[index]) ||
                            !spawnPointIds.Contains(item: points.Names[index])
                        ) {
                            errors.Add(item: $"{path}.region.names[{index}] '{points.Names[index]}' names no spawn point.");
                        }
                    }
                }

                RequireNonNegative(
                    value: points.HalfExtent,
                    name: $"{path}.region.halfExtent",
                    errors: errors
                );
                ValidateSequence(
                    sequence: distribution.Fill,
                    path: $"{path}.fill",
                    minIndex: 0,
                    errors: errors,
                    WorldSequence.R2
                );
                break;
            case WorldDistributionRegion.Lattice lattice when allowLattice:
                ValidateLattice(
                    errors: errors,
                    lattice: lattice,
                    path: $"{path}.region"
                );
                ValidateSequence(
                    sequence: distribution.Fill,
                    path: $"{path}.fill",
                    minIndex: 0,
                    errors: errors,
                    WorldSequence.None
                );
                break;
            default:
                errors.Add(item: $"{path}.region kind '{distribution.Region.GetType().Name}' is not supported here.");
                break;
        }
    }
    // The hud section: schema caps (MaxWorldPanels; MaxElementsPerPanel, or the tighter MaxElementsPerSeatPanel for
    // an owned identity's seat panel — see isIdentityScope; MaxFrameSources at either scope), id uniqueness (panels;
    // elements within their panel), rect sanity, WorldHudLayer.Replace refused for a seat panel, the closed
    // HudBindingVocabulary — including whether a state.<name> binding resolves against the state rows validated just
    // before this call — and (for a Frame element) the shared ValidateFrameSource gate against definition/cameras.
    private static void ValidateHud(WorldDefinition definition, HashSet<string> cameras, WorldHudSection hud, IReadOnlyDictionary<string, WorldStateRow> stateRows, bool isIdentityScope, List<string> errors) {
        if (hud is null) {
            errors.Add(item: "hud is required.");

            return;
        }

        ValidateHudCore(
            cameras: cameras,
            definition: definition,
            errors: errors,
            hud: hud,
            isIdentityScope: isIdentityScope,
            stateRows: stateRows
        );
    }
    private static void ValidateHudCore(WorldDefinition definition, HashSet<string> cameras, WorldHudSection hud, IReadOnlyDictionary<string, WorldStateRow> stateRows, bool isIdentityScope, List<string> errors) {
        var panels = hud.Panels;
        var maxElements = (isIdentityScope
            ? WorldHudCapacity.MaxElementsPerSeatPanel
            : WorldHudCapacity.MaxElementsPerPanel
        );
        var maxPanels = (isIdentityScope
            ? WorldHudCapacity.MaxSeatPanels
            : WorldHudCapacity.MaxWorldPanels
        );

        if (hud.Defaults is { Cursor: { } cursor }) {
            if (
                !float.IsFinite(f: cursor.HoverRadius) ||
                (cursor.HoverRadius <= 0f) ||
                (cursor.HoverRadius > 1024f)
            ) {
                HudRowValidation.Refuse(
                    errors: errors,
                    reason: HudRefusal.CursorInvalid,
                    message: $"hud.defaults.cursor.hoverRadius must be finite, positive, and at most 1024 world units (got {cursor.HoverRadius})."
                );
            }

            if (
                !float.IsFinite(f: cursor.SizePx) ||
                (cursor.SizePx <= 0f) ||
                (cursor.SizePx > 64f)
            ) {
                HudRowValidation.Refuse(
                    errors: errors,
                    reason: HudRefusal.CursorInvalid,
                    message: $"hud.defaults.cursor.sizePx must be finite, positive, and at most 64 pixels (got {cursor.SizePx})."
                );
            }

            if (!Enum.IsDefined(value: cursor.Role)) {
                HudRowValidation.Refuse(
                    errors: errors,
                    reason: HudRefusal.CursorInvalid,
                    message: $"hud.defaults.cursor.role value {((int)cursor.Role)} is not a defined cursor role."
                );
            }
        }

        ValidateHudVisible(
            definition: (isIdentityScope ? null : definition),
            errors: errors,
            path: "hud.defaults.visible",
            predicate: hud.Defaults?.Visible
        );
        ValidateHudVisible(
            definition: (isIdentityScope ? null : definition),
            errors: errors,
            path: "hud.defaults.cursor.visible",
            predicate: hud.Defaults?.Cursor?.Visible
        );

        if (panels.Count > maxPanels) {
            HudRowValidation.Refuse(
                errors: errors,
                reason: HudRefusal.TooManyPanels,
                message: $"hud.panels count {panels.Count} exceeds the maximum of {maxPanels} ({(isIdentityScope
                ? "WorldHudCapacity.MaxSeatPanels — an identity-owned world authors one seat panel"
                : "WorldHudCapacity.MaxWorldPanels")})."
            );
        }

        // Record equality is not structural for IReadOnlyList members such as Camera.Controls.Vendor. The generated
        // wire form is the source declaration's canonical structural identity, so independently deserialized copies
        // consume one capacity slot just as a reused object does.
        var frameSources = new HashSet<string>(comparer: StringComparer.Ordinal);
        var panelIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var panelIndex = 0; (panelIndex < panels.Count); panelIndex++) {
            var panel = panels[panelIndex];
            var panelPath = $"hud.panels[{panelIndex}]";

            if (string.IsNullOrWhiteSpace(value: panel.Id)) {
                HudRowValidation.Refuse(
                    errors: errors,
                    message: $"{panelPath}.id is required.",
                    reason: HudRefusal.DuplicatePanelId
                );
            } else if (!panelIds.Add(item: panel.Id)) {
                HudRowValidation.Refuse(
                    errors: errors,
                    reason: HudRefusal.DuplicatePanelId,
                    message: $"{panelPath}.id '{panel.Id}' is duplicated."
                );
            }

            if (
                isIdentityScope &&
                (panel.Layer == WorldHudLayer.Replace)
            ) {
                HudRowValidation.Refuse(
                    errors: errors,
                    message: $"{panelPath}.layer 'replace' is not meaningful for an identity-owned seat panel confined to one seat's viewport — use under or over.",
                    reason: HudRefusal.SeatPanelReplaceRefused
                );
            }

            HudRowValidation.ValidateRect(
                errors: errors,
                rect: panel.Rect,
                path: $"{panelPath}.rect"
            );
            HudRowValidation.ValidateElements(
                cameras: cameras,
                definition: definition,
                elements: panel.Elements,
                errors: errors,
                panelPath: $"{panelPath} ('{panel.Id}')",
                maxElements: maxElements,
                stateRows: stateRows
            );
            ValidateHudVisible(
                definition: (isIdentityScope ? null : definition),
                errors: errors,
                path: $"{panelPath}.visible",
                predicate: panel.Visible
            );

            foreach (var element in panel.Elements) {
                if (element.Kind != WorldHudElementKind.Frame) {
                    continue;
                }

                // Every candidate counts: a ranked element may show any of them, and the overlay's slot table is
                // sized for the distinct sources a section can reach, not the ones winning this frame.
                foreach (var candidate in element.FrameCandidates) {
                    if ((candidate?.Source is not { } source) || !frameSources.Add(item: JsonSerializer.Serialize(
                        value: source,
                        jsonTypeInfo: WorldJsonContext.Default.WorldFrameSource
                    ))) {
                        continue;
                    }

                    if (frameSources.Count > WorldHudCapacity.MaxFrameSources) {
                        HudRowValidation.Refuse(
                            errors: errors,
                            reason: HudRefusal.TooManyFrameSources,
                            message: $"{panelPath} ('{panel.Id}') element '{element.Id}' introduces distinct frame source {frameSources.Count}, exceeding WorldHudCapacity.MaxFrameSources ({WorldHudCapacity.MaxFrameSources}). Repeated elements and candidates may share the same source."
                        );
                    }
                }
            }
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

        RequirePositiveFixedSlot(
            stateRows: stateRows,
            name: identity.MoveSpeedState,
            path: "identity.moveSpeedState",
            errors: errors
        );
        RequirePositiveFixedSlot(
            stateRows: stateRows,
            name: identity.TurnSpeedState,
            path: "identity.turnSpeedState",
            errors: errors
        );
    }
    private static void ValidateLattice(WorldDistributionRegion.Lattice lattice, string path, List<string> errors) {
        if (
            (lattice.CountA < 1) ||
            (lattice.CountB < 1)
        ) {
            errors.Add(item: $"{path} counts must be at least 1.");
        }

        if (
            (lattice.CountA > SdfProgramBuilder.MaxInstances) ||
            (lattice.CountB > SdfProgramBuilder.MaxInstances)
        ) {
            errors.Add(item: $"{path} counts cannot exceed the {SdfProgramBuilder.MaxInstances}-instance engine ceiling.");
        }

        if (!IsFinite(value: lattice.StepA)) {
            errors.Add(item: $"{path}.stepA must contain finite coordinates.");
        }

        if (!IsFinite(value: lattice.StepB)) {
            errors.Add(item: $"{path}.stepB must contain finite coordinates.");
        }

        if (
            (lattice.CountA > 1) &&
            (lattice.StepA.LengthSquared() <= 0f)
        ) {
            errors.Add(item: $"{path}.stepA must be nonzero when countA exceeds 1.");
        }

        if (
            (lattice.CountB > 1) &&
            (lattice.StepB.LengthSquared() <= 0f)
        ) {
            errors.Add(item: $"{path}.stepB must be nonzero when countB exceeds 1.");
        }

        if (
            (lattice.CountA > 1) &&
            (lattice.CountB > 1) &&
            (Vector3.Cross(
            vector1: lattice.StepA,
            vector2: lattice.StepB
        ).LengthSquared() <= 0f)
        ) {
            errors.Add(item: $"{path} steps must be independent when both counts exceed 1.");
        }
    }
    // Vocabulary only — the shallow half of the shallow-then-deep split WorldScreenSource.Machine's Options string
    // already uses: an id naming no shipped shader set (no puck.shader.v1 manifest with that stem) refuses here, at
    // load, by name and index — not a silent no-op discovered only once the composition root tries to compose the
    // chain. Each entry's own Config is opaque here; the manifest's declared config schema validates it at boot,
    // refusing with the set id and reason on a malformed value.
    private static void ValidateRenderExtensions(IReadOnlyList<WorldRenderExtensionEntry>? extensions, List<string> errors) {
        if (extensions is null) {
            return;
        }

        for (var index = 0; (index < extensions.Count); index++) {
            var entry = extensions[index];
            var path = $"render.extensions[{index}]";

            if (entry is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: entry.Id)) {
                errors.Add(item: $"{path}.id is required.");
            } else if (!WorldExtensionVocabularyHook.IsRegisteredPostRenderExtension(extensionId: entry.Id)) {
                errors.Add(item: $"{path}.id '{entry.Id}' names no registered post-render extension.");
            }
        }
    }
    private static void ValidateRenderLighting(WorldDefinition definition, WorldRenderLighting? lighting, List<string> errors, string path = "render.lighting") {
        if (lighting is null) {
            return;
        }

        if (lighting.Sun is { } sun) {
            if (sun.Direction is { } direction) {
                if (!IsFinite(value: direction)) {
                    errors.Add(item: $"{path}.sun.direction must contain finite coordinates.");
                } else if (direction.LengthSquared() <= 0f) {
                    errors.Add(item: $"{path}.sun.direction must be nonzero.");
                }
            }

            if (sun.Weight is { } weight) {
                RequireNonNegative(
                    value: weight,
                    name: $"{path}.sun.weight",
                    errors: errors
                );
            }

            if (
                (sun.Color is { } color) &&
                !IsColor(
                definition: definition,
                value: color
            )
            ) {
                errors.Add(item: $"{path}.sun.color '{color}' {WorldColor.Grammar}.");
            }
        }

        if (lighting.Ambient is { } ambient) {
            if (ambient.Base is { } ambientBase) {
                RequireNonNegative(
                    value: ambientBase,
                    name: $"{path}.ambient.base",
                    errors: errors
                );
            }

            if (ambient.Hemisphere is { } hemisphere) {
                RequireFinite(
                    value: hemisphere,
                    name: $"{path}.ambient.hemisphere",
                    errors: errors
                );
            }

            if (
                (ambient.Color is { } color) &&
                !IsColor(
                definition: definition,
                value: color
            )
            ) {
                errors.Add(item: $"{path}.ambient.color '{color}' {WorldColor.Grammar}.");
            }
        }
    }
    private static void ValidateRenderSky(WorldDefinition definition, WorldRenderSky? sky, List<string> errors, string path = "render.sky") {
        if (sky is null) {
            return;
        }

        if (
            (sky.Zenith is { } zenith) &&
            !IsColor(
            definition: definition,
            value: zenith
        )
        ) {
            errors.Add(item: $"{path}.zenith '{zenith}' {WorldColor.Grammar}.");
        }

        if (
            (sky.Horizon is { } horizon) &&
            !IsColor(
            definition: definition,
            value: horizon
        )
        ) {
            errors.Add(item: $"{path}.horizon '{horizon}' {WorldColor.Grammar}.");
        }

        if (
            (sky.Ground is { } ground) &&
            !IsColor(
            definition: definition,
            value: ground
        )
        ) {
            errors.Add(item: $"{path}.ground '{ground}' {WorldColor.Grammar}.");
        }

        if (sky.FogDensity is { } fogDensity) {
            RequireNonNegative(
                value: fogDensity,
                name: $"{path}.fogDensity",
                errors: errors
            );
        }

        if (sky.Sun is { } sun) {
            RequireRange(
                value: sun.DiscRadians,
                min: 0f,
                max: (MathF.PI / 2f),
                name: $"{path}.sun.discRadians",
                errors: errors,
                minExclusive: true
            );

            RequireNonNegative(
                value: sun.Intensity,
                name: $"{path}.sun.intensity",
                errors: errors
            );
        }

        if (sky.Stars is { } stars) {
            RequirePositive(
                value: stars.Density,
                name: $"{path}.stars.density",
                errors: errors
            );

            RequireNonNegative(
                value: stars.Brightness,
                name: $"{path}.stars.brightness",
                errors: errors
            );

            if (stars.Twinkle is { } twinkle) {
                RequireUnitInterval(
                    value: twinkle.Share,
                    name: $"{path}.stars.twinkle.share",
                    errors: errors
                );

                RequireUnitInterval(
                    value: twinkle.Depth,
                    name: $"{path}.stars.twinkle.depth",
                    errors: errors
                );

                RequirePositive(
                    value: twinkle.Rate,
                    name: $"{path}.stars.twinkle.rate",
                    errors: errors
                );
            }
        }

        if (sky.Clouds is { } clouds) {
            RequireUnitInterval(
                value: clouds.Coverage,
                name: $"{path}.clouds.coverage",
                errors: errors
            );

            RequireRange(
                value: clouds.Softness,
                min: 0f,
                max: 1f,
                name: $"{path}.clouds.softness",
                errors: errors,
                minExclusive: true
            );

            RequirePositive(
                value: clouds.Scale,
                name: $"{path}.clouds.scale",
                errors: errors
            );

            if (
                (clouds.Color is { } cloudColor) &&
                !IsColor(
                definition: definition,
                value: cloudColor
            )
            ) {
                errors.Add(item: $"{path}.clouds.color '{cloudColor}' {WorldColor.Grammar}.");
            }

            if (
                (clouds.Drift is { } drift) &&
                (!float.IsFinite(f: drift.X) || !float.IsFinite(f: drift.Y))
            ) {
                errors.Add(item: $"{path}.clouds.drift must contain finite coordinates.");
            }

            if (clouds.Spin is { } spin) {
                RequireFinite(
                    value: spin,
                    name: $"{path}.clouds.spin",
                    errors: errors
                );
            }

            if (clouds.Curl is { } curl) {
                RequireFinite(
                    value: curl,
                    name: $"{path}.clouds.curl",
                    errors: errors
                );
            }

            if (
                (clouds.Shear is { } shear) &&
                (!float.IsFinite(f: shear.X) || !float.IsFinite(f: shear.Y))
            ) {
                errors.Add(item: $"{path}.clouds.shear must contain finite coordinates.");
            }
        }
    }
    private static void ValidateMetadata(WorldMetadataSection? metadata, List<string> errors) {
        if (metadata is null) {
            return;
        }

        if (metadata.Title is { } title) {
            if (HasForgeableCharacter(value: title)) {
                errors.Add(item: "metadata.title must not contain a control character or ']'.");
            }

            if (title.Length > WorldMetadataCapacity.MaxTitleLength) {
                errors.Add(item: $"metadata.title is {title.Length} characters, past the {WorldMetadataCapacity.MaxTitleLength}-character cap.");
            }
        }

        if (metadata.Description is { } description) {
            if (HasForgeableCharacter(value: description)) {
                errors.Add(item: "metadata.description must not contain a control character or ']'.");
            }

            if (description.Length > WorldMetadataCapacity.MaxDescriptionLength) {
                errors.Add(item: $"metadata.description is {description.Length} characters, past the {WorldMetadataCapacity.MaxDescriptionLength}-character cap.");
            }
        }

        if (metadata.Authors is { } authors) {
            if (authors.Count > WorldMetadataCapacity.MaxAuthors) {
                errors.Add(item: $"metadata.authors declares {authors.Count} rows, past the {WorldMetadataCapacity.MaxAuthors}-row cap.");
            }

            for (var index = 0; (index < authors.Count); index++) {
                var author = authors[index];
                var path = $"metadata.authors[{index}]";

                if (author is null) {
                    errors.Add(item: $"{path} is required.");

                    continue;
                }

                if (
                    string.IsNullOrEmpty(value: author.Name) ||
                    HasForgeableCharacter(value: author.Name)
                ) {
                    errors.Add(item: $"{path}.name must be non-empty and free of control characters/']'.");
                } else if (author.Name.Length > WorldMetadataCapacity.MaxAuthorNameLength) {
                    errors.Add(item: $"{path}.name is {author.Name.Length} characters, past the {WorldMetadataCapacity.MaxAuthorNameLength}-character cap.");
                }

                if (
                    (author.Oid is { } oid) &&
                    !WorldEntraObjectId.IsValid(value: oid)
                ) {
                    errors.Add(item: $"{path}.oid '{oid}' is not a well-formed Entra object id.");
                }
            }
        }

        if (metadata.Tags is { } tags) {
            if (tags.Count > WorldMetadataCapacity.MaxTags) {
                errors.Add(item: $"metadata.tags declares {tags.Count} rows, past the {WorldMetadataCapacity.MaxTags}-row cap.");
            }

            var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < tags.Count); index++) {
                var tag = tags[index];
                var path = $"metadata.tags[{index}]";

                if (
                    string.IsNullOrEmpty(value: tag) ||
                    HasForgeableCharacter(value: tag)
                ) {
                    errors.Add(item: $"{path} must be non-empty and free of control characters/']'.");
                } else if (tag.Length > WorldMetadataCapacity.MaxTagLength) {
                    errors.Add(item: $"{path} is {tag.Length} characters, past the {WorldMetadataCapacity.MaxTagLength}-character cap.");
                } else if (!seen.Add(item: tag)) {
                    errors.Add(item: $"{path} '{tag}' duplicates an earlier tag.");
                }
            }
        }

        if (metadata.Custom is { } custom) {
            foreach (var (key, value) in custom) {
                var path = $"metadata.custom['{key}']";

                if (
                    string.Equals(
                    a: key,
                    b: WorldDocumentBasis.DropMemberName,
                    comparisonType: StringComparison.Ordinal
                ) ||
                    string.Equals(
                    a: key,
                    b: WorldDocumentBasis.ReplaceMemberName,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    errors.Add(item: $"{path} is compose-time row vocabulary ('{WorldDocumentBasis.DropMemberName}'/'{WorldDocumentBasis.ReplaceMemberName}'), not an authorable key.");
                } else {
                    RefuseComposeVocabularyAtAnyDepth(
                        element: value,
                        errors: errors,
                        path: path
                    );
                }
            }

            var bytes = WorldMetadataSection.CustomUtf8ByteCount(custom: custom);

            if (bytes > WorldMetadataCapacity.MaxCustomBytes) {
                errors.Add(item: $"metadata.custom is {bytes} UTF-8 bytes, past the {WorldMetadataCapacity.MaxCustomBytes}-byte cap.");
            }
        }
    }
    private static void ValidateUpdate(WorldUpdateDefaults? update, List<string> errors) {
        if (update is null) {
            return;
        }

        if (
            (update.Channel is not null) &&
            string.IsNullOrWhiteSpace(value: update.Channel)
        ) {
            errors.Add(item: "update.channel must be non-whitespace when authored.");
        }

        if (
            (update.CacheRoot is not null) &&
            string.IsNullOrWhiteSpace(value: update.CacheRoot)
        ) {
            errors.Add(item: "update.cacheRoot must be non-whitespace when authored.");
        }

        if (
            (update.CheckIntervalSeconds is { } checkIntervalSeconds) &&
            (checkIntervalSeconds < 0)
        ) {
            errors.Add(item: $"update.checkIntervalSeconds must not be negative, was {checkIntervalSeconds}.");
        }

        if (
            (update.KeepVersions is { } keepVersions) &&
            (keepVersions < 0)
        ) {
            errors.Add(item: $"update.keepVersions must not be negative, was {keepVersions}.");
        }
    }
    private static void ValidateObserverDisclosure(WorldObserverDisclosure? disclosure, List<string> errors) {
        if (disclosure is not { } row) {
            return;
        }

        if (!Enum.IsDefined(value: row.Mode)) {
            errors.Add(item: $"population.disclosure.mode '{row.Mode}' is not defined.");

            return;
        }

        if (row.Mode == WorldObserverDisclosureMode.Radius) {
            if (row.Radius is not { } radius) {
                errors.Add(item: "population.disclosure.radius is required for mode 'radius'.");
            } else {
                RequirePositive(
                    value: radius,
                    name: "population.disclosure.radius",
                    errors: errors
                );
            }
        } else if (row.Radius is not null) {
            errors.Add(item: $"population.disclosure.radius must be absent for mode '{row.Mode}' — only 'radius' reads one.");
        }
    }
    private static void ValidatePlayerDefaults(WorldPlayerDefaults defaults, List<string> errors) {
        ValidateSeatLook(
            seatLook: defaults.SeatLook,
            path: "playerDefaults.seatLook",
            errors: errors
        );

        if (defaults.Identities is { Count: > 0 }) {
            var ids = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
            // Each seed becomes an owned-world document on disk under WorldOwnedWorldFileName's id→file-name mapping,
            // and that mapping is injective into file-name STRINGS, not into storage locations: NTFS and default APFS
            // resolve a name case-insensitively, so 'Amber' and 'amber' address one file. Ids are therefore unique
            // IGNORING CASE here, the same rule Server.WorldOwnedWorlds holds over the directory itself.
            for (var index = 0; (index < defaults.Identities.Count); index++) {
                var profile = defaults.Identities[index];
                var path = $"playerDefaults.identities[{index}]";

                if (profile is null) {
                    errors.Add(item: $"{path} is required.");
                    continue;
                }

                if (!ids.Add(item: profile.Id)) {
                    errors.Add(item: $"{path}.id '{profile.Id}' is duplicated — owned-world ids are unique ignoring case, since each addresses one '{WorldOwnedWorldFileName.For(id: profile.Id)}' file.");
                }

                if (
                    string.IsNullOrWhiteSpace(value: profile.Name) ||
                    !names.Add(item: profile.Name)
                ) {
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
        ValidateSequence(
            sequence: defaults.ColorSequence,
            path: "playerDefaults.colorSequence",
            minIndex: 0,
            errors: errors,
            WorldSequence.Additive,
            WorldSequence.R1
        );
        RequireUnitInterval(
            value: defaults.Saturation,
            name: "playerDefaults.saturation",
            errors: errors
        );
        RequireUnitInterval(
            value: defaults.Value,
            name: "playerDefaults.value",
            errors: errors
        );
        RequireUnitInterval(
            value: defaults.NoseFactor,
            name: "playerDefaults.noseFactor",
            errors: errors
        );
        RequireUnitInterval(
            value: defaults.PickerThreshold,
            name: "playerDefaults.pickerThreshold",
            errors: errors
        );
        RequireUnitInterval(
            value: defaults.PickerNeutralBlend,
            name: "playerDefaults.pickerNeutralBlend",
            errors: errors
        );

        if (defaults.ColorSearchLimit < 1) {
            errors.Add(item: "playerDefaults.colorSearchLimit must be positive.");
        }
    }
    private static void ValidatePopulationVariation(WorldPopulationVariation variation, string path, int minIndex, List<string> errors) {
        if (variation is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        ValidateSequence(
            sequence: variation.Phase,
            path: $"{path}.phase",
            minIndex: minIndex,
            errors: errors,
            WorldSequence.Additive
        );
        ValidateSequence(
            sequence: variation.Weave,
            path: $"{path}.weave",
            minIndex: minIndex,
            errors: errors,
            WorldSequence.Additive,
            WorldSequence.R1
        );
        ValidateSequence(
            sequence: variation.Activity,
            path: $"{path}.activity",
            minIndex: minIndex,
            errors: errors,
            WorldSequence.R2
        );
    }
    // Seat 0 is refused OnDemand — the session always needs a first player — and the array must name exactly
    // population.localSeats entries, the same shape ValidateSeatSpawns enforces for the sibling per-seat row. A
    // zero-seat world has no seat 0, so the eager-first-seat rule is vacuous rather than refusing an empty table.
    private static void ValidateSeatActivation(IReadOnlyList<SeatActivationPolicy> seatActivation, int localSeats, List<string> errors) {
        if (
            (seatActivation is not { Count: var count }) ||
            (count != localSeats)
        ) {
            errors.Add(item: $"population.seatActivation must contain exactly {localSeats} entries.");

            return;
        }

        if (
            (localSeats > 0) &&
            (seatActivation[0] != SeatActivationPolicy.Eager)
        ) {
            errors.Add(item: "population.seatActivation[0] must be 'eager' — the session's first seat cannot start on-demand.");
        }
    }
    private static void ValidateSeatSpawns(IReadOnlyList<string> seatSpawns, HashSet<string> spawnPointIds, int localSeats, List<string> errors) {
        if (
            (seatSpawns is not { Count: var count }) ||
            (count != localSeats)
        ) {
            errors.Add(item: $"population.seatSpawns must contain exactly {localSeats} spawn-point names.");

            return;
        }

        for (var index = 0; (index < seatSpawns.Count); index++) {
            if (
                string.IsNullOrWhiteSpace(value: seatSpawns[index]) ||
                !spawnPointIds.Contains(item: seatSpawns[index])
            ) {
                errors.Add(item: $"population.seatSpawns[{index}] '{seatSpawns[index]}' names no spawn point.");
            }
        }
    }
    // An authored simulation rate must be exactly 0 (resident, non-stepping — see WorldSimulationDefaults.RateHz) or
    // a positive divisor of the fixed 50400 engine-tick base (FixedTickConversion.TicksPerSecond; duplicated here
    // because Puck.World.Schema cannot reference Puck.Hosting, where EngineTicks lives), so EngineTicks.PerRate always
    // derives a whole engine-tick step width. A negative rate is refused outright; the divisor check applies only to
    // a positive rate (0 divides nothing).
    private static void ValidateSimulation(WorldSimulationDefaults? simulation, List<string> errors) {
        if (simulation is null) {
            // Unauthored — WorldDefinition.SimulationRateHz reads 0 (a resident, non-stepping world; the standard
            // 240 Hz is authored in standard.world.json). Nothing to check.
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

        if ((FixedTickConversion.TicksPerSecond % ((ulong)rate)) != 0UL) {
            var (lower, upper) = NearestRateDivisors(rate: rate);
            var nearest = ((lower, upper) switch {
                ( { } low, { } high) => $"{low} and {high}",
                ( { } low, null) => $"{low}",
                (null, { } high) => $"{high}",
                (null, null) => "none", // unreachable: 1 and 50400 always bracket a positive rate.
            });

            errors.Add(item: $"simulation.rateHz {rate} does not divide {FixedTickConversion.TicksPerSecond} engine ticks per second exactly; nearest valid rates are {nearest} Hz.");
        }
    }
    // Named spawn poses: id presence/uniqueness and finite coordinates.
    private static HashSet<string> ValidateSpawnPoints(IReadOnlyList<WorldSpawnPoint> spawnPoints, List<string> errors) {
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < spawnPoints.Count); index++) {
            var spawn = spawnPoints[index];

            RequireUniqueName(
                value: spawn.Id,
                seen: ids,
                path: $"spawnPoints[{index}]",
                field: "id",
                errors: errors
            );

            if (!IsFinite(value: spawn.Position)) {
                errors.Add(item: $"spawnPoints[{index}].position must contain finite coordinates.");
            }

            RequireFinite(
                value: spawn.YawDegrees,
                name: $"spawnPoints[{index}].yawDegrees",
                errors: errors
            );
        }

        return ids;
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

        if (
            (storage.Endpoint is { } endpoint) &&
            (string.IsNullOrWhiteSpace(value: endpoint) || !Uri.TryCreate(
            result: out _,
            uriKind: UriKind.Absolute,
            uriString: endpoint
        ))
        ) {
            errors.Add(item: $"storage.endpoint '{endpoint}' must be an absolute URI or null.");
        }

        if (
            (storage.DiscoveryEndpoint is { } discoveryEndpoint) &&
            (string.IsNullOrWhiteSpace(value: discoveryEndpoint) || !Uri.TryCreate(
            result: out _,
            uriKind: UriKind.Absolute,
            uriString: discoveryEndpoint
        ))
        ) {
            errors.Add(item: $"storage.discoveryEndpoint '{discoveryEndpoint}' must be an absolute URI or null.");
        }

        if (
            (storage.UserId is { } userId) &&
            string.IsNullOrWhiteSpace(value: userId)
        ) {
            errors.Add(item: "storage.userId must be non-empty or null.");
        }
    }
    // A malformed visible predicate is a hud.validate verdict like every other HUD refusal; identity scope passes no
    // definition, so subject and state references are admitted unresolved there.
    private static void ValidateHudVisible(OverlayPredicate? predicate, string path, WorldDefinition? definition, List<string> errors) {
        var predicateErrors = new List<string>();

        ValidateOverlayPredicate(
            definition: definition,
            errors: predicateErrors,
            path: path,
            predicate: predicate
        );

        if (predicateErrors.Count > 0) {
            HudRowValidation.Refuse(
                errors: errors,
                message: string.Join(separator: " ", values: predicateErrors),
                reason: HudRefusal.VisiblePredicateInvalid
            );
        }
    }
}
