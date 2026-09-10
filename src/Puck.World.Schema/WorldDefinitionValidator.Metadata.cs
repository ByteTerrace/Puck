using System.Numerics;
using System.Text.Json;
using Puck.World.Authoring;
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

        if (gravity.Points is { Count: > 0 } points) {
            if (!(gravity.GravitationalConstant > 0f)) {
                errors.Add(item: "gravity.gravitationalConstant must be positive when gravity.points declares a source.");
            }

            for (var index = 0; (index < points.Count); index++) {
                var point = points[index];
                var path = $"gravity.points[{index}]";

                if (point is null) {
                    errors.Add(item: $"{path} is required.");

                    continue;
                }

                if (string.IsNullOrWhiteSpace(value: point.PlacementId)) {
                    errors.Add(item: $"{path}.placementId is required.");
                } else {
                    if (!seen.Add(item: point.PlacementId)) {
                        errors.Add(item: $"{path}.placementId duplicates gravity source '{point.PlacementId}'.");
                    }

                    if (WorldDefinitionRows.FindPlacement(
                        id: point.PlacementId,
                        placements: placements
                    ) is null) {
                        errors.Add(item: $"{path}.placementId '{point.PlacementId}' resolves to no placement.");
                    }
                }

                RequirePositive(
                    value: point.SurfaceGravity,
                    name: $"{path}.surfaceGravity",
                    errors: errors
                );
                RequirePositive(
                    value: point.ReferenceRadius,
                    name: $"{path}.referenceRadius",
                    errors: errors
                );

                if (
                    float.IsFinite(f: point.SurfaceGravity) &&
                    (point.SurfaceGravity > 0f) &&
                    float.IsFinite(f: point.ReferenceRadius) &&
                    (point.ReferenceRadius > 0f) &&
                    float.IsFinite(f: gravity.GravitationalConstant) &&
                    (gravity.GravitationalConstant > 0f) &&
                    float.IsFinite(f: gravity.SofteningLength) &&
                    (gravity.SofteningLength > 0f) &&
                    !FixedWorldGravity.TryCompilePointMass(
                        gravitationalConstant: gravity.GravitationalConstant,
                        mass: out _,
                        point: point,
                        softeningLength: gravity.SofteningLength
                    )
                ) {
                    errors.Add(item: $"{path} cannot lower its surfaceGravity/referenceRadius promise through gravity's Q48.16 Plummer kernel without underflow or overflow.");
                }
            }
        }

        if (gravity.Areas is not { } areas) {
            return;
        }

        if (areas.Count > WorldGravityCapacity.MaxAreas) {
            errors.Add(item: $"gravity.areas declares {areas.Count} rows, past the {WorldGravityCapacity.MaxAreas}-row cap.");
        }

        var frames = WorldPlacementFrameCompilation.Compile(placements: placements);

        for (var index = 0; (index < areas.Count); index++) {
            var area = areas[index];
            var path = $"gravity.areas[{index}]";

            if (area is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            WorldPlacement? placement = null;

            if (string.IsNullOrWhiteSpace(value: area.PlacementId)) {
                errors.Add(item: $"{path}.placementId is required.");
            } else if ((placement = WorldDefinitionRows.FindPlacement(
                id: area.PlacementId,
                placements: placements
            )) is null) {
                errors.Add(item: $"{path}.placementId '{area.PlacementId}' resolves to no placement.");
            }

            if (!Enum.IsDefined(value: area.Mode)) {
                errors.Add(item: $"{path}.mode '{area.Mode}' is not defined.");
            }

            switch (area.Bounds) {
                case WorldGravityAreaBounds.SphereBounds sphere:
                    RequirePositive(
                        value: sphere.Radius,
                        name: $"{path}.bounds.radius",
                        errors: errors
                    );
                    break;
                case WorldGravityAreaBounds.BoxBounds box:
                    RequirePositive(
                        value: box.HalfExtents.X,
                        name: $"{path}.bounds.halfExtents[0]",
                        errors: errors
                    );
                    RequirePositive(
                        value: box.HalfExtents.Y,
                        name: $"{path}.bounds.halfExtents[1]",
                        errors: errors
                    );
                    RequirePositive(
                        value: box.HalfExtents.Z,
                        name: $"{path}.bounds.halfExtents[2]",
                        errors: errors
                    );
                    break;
                case null:
                    errors.Add(item: $"{path}.bounds is required.");
                    break;
                default:
                    errors.Add(item: $"{path}.bounds has an unsupported shape.");
                    break;
            }

            switch (area.Acceleration) {
                case WorldGravityAreaAcceleration.Directional directional:
                    if (!IsFinite(value: directional.Value)) {
                        errors.Add(item: $"{path}.acceleration.value must contain finite coordinates.");
                    }
                    break;
                case WorldGravityAreaAcceleration.Radial radial:
                    RequirePositive(
                        value: radial.Magnitude,
                        name: $"{path}.acceleration.magnitude",
                        errors: errors
                    );
                    break;
                case null:
                    errors.Add(item: $"{path}.acceleration is required.");
                    break;
                default:
                    errors.Add(item: $"{path}.acceleration has an unsupported shape.");
                    break;
            }

            if (
                (placement is not null) &&
                !FixedWorldGravityArea.TryCompile(
                    area: area,
                    authoredIndex: index,
                    compiled: out _,
                    frame: frames[placement.Id],
                    placement: placement
                )
            ) {
                errors.Add(item: $"{path} cannot lower its placement-relative bounds and acceleration through the Q48.16 evaluator without underflow or overflow.");
            }
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

        // 0 bakes no grid; a non-zero cell must sit inside the addressable range — below the floor a grid over a room
        // outgrows memory, above the ceiling its bound is looser than any query can use.
        if (
            !float.IsFinite(f: collision.GridCellSize) ||
            (collision.GridCellSize < 0f) ||
            ((collision.GridCellSize != 0f) && (collision.GridCellSize < WorldCollision.MinGridCellSize)) ||
            (collision.GridCellSize > WorldCollision.MaxGridCellSize)
        ) {
            errors.Add(item: $"collision.gridCellSize must be exactly 0 (bake no grid) or within [{WorldCollision.MinGridCellSize}, {WorldCollision.MaxGridCellSize}] (was {collision.GridCellSize}).");
        }

        if (
            (collision.Requirements is { Count: 0 }) &&
            float.IsFinite(f: collision.GradientProbe) &&
            (collision.GradientProbe > 0f)
        ) {
            errors.Add(item: "collision.gradientProbe > 0 requires at least one field-contact requirement.");
        }

        var events = collision.Events;
        RequireIntRange(
            value: events.CandidateBudget,
            min: 1,
            max: WorldCollisionEvents.MaximumCandidateBudget,
            name: "collision.events.candidateBudget",
            errors: errors
        );
        RequireIntRange(
            value: events.MaxPairsPerBody,
            min: 0,
            max: WorldCollisionEvents.MaximumPairsPerBody,
            name: "collision.events.maxPairsPerBody",
            errors: errors
        );
        RequireIntRange(
            value: events.BeginBudget,
            min: 1,
            max: WorldCollisionEvents.MaximumBeginBudget,
            name: "collision.events.beginBudget",
            errors: errors
        );
        if (events.CandidateBudget < events.MaxPairsPerBody) {
            errors.Add(item: $"collision.events.candidateBudget must be >= maxPairsPerBody ({events.CandidateBudget} < {events.MaxPairsPerBody}).");
        }

        var bodyContacts = collision.BodyContacts;
        RequireIntRange(
            value: bodyContacts.CandidateBudget,
            min: 1,
            max: WorldBodyContactPolicy.MaximumCandidateBudget,
            name: "collision.bodyContacts.candidateBudget",
            errors: errors
        );
        RequireIntRange(
            value: bodyContacts.MaxPairsPerBody,
            min: 1,
            max: WorldBodyContactPolicy.MaximumPairsPerBody,
            name: "collision.bodyContacts.maxPairsPerBody",
            errors: errors
        );
        if (bodyContacts.CandidateBudget < bodyContacts.MaxPairsPerBody) {
            errors.Add(item: $"collision.bodyContacts.candidateBudget must be >= maxPairsPerBody ({bodyContacts.CandidateBudget} < {bodyContacts.MaxPairsPerBody}).");
        }
        RequireIntRange(
            value: bodyContacts.RigidSubstepCeiling,
            min: 1,
            max: WorldBodyContactPolicy.MaximumRigidSubstepCeiling,
            name: "collision.bodyContacts.rigidSubstepCeiling",
            errors: errors
        );
        RequireNonNegative(
            value: bodyContacts.RigidRestLinearSpeed,
            name: "collision.bodyContacts.rigidRestLinearSpeed",
            errors: errors
        );
        RequireNonNegative(
            value: bodyContacts.RigidRestAngularSpeed,
            name: "collision.bodyContacts.rigidRestAngularSpeed",
            errors: errors
        );
        RequireNonNegative(
            value: bodyContacts.RigidRestHoldSeconds,
            name: "collision.bodyContacts.rigidRestHoldSeconds",
            errors: errors
        );
        RequirePositive(
            value: bodyContacts.RigidSubstepTravelFraction,
            name: "collision.bodyContacts.rigidSubstepTravelFraction",
            errors: errors
        );
        RequirePositive(
            value: bodyContacts.RigidSubstepMinimumTravel,
            name: "collision.bodyContacts.rigidSubstepMinimumTravel",
            errors: errors
        );
        RequireNonNegative(
            value: bodyContacts.RigidPairRestitutionSpeed,
            name: "collision.bodyContacts.rigidPairRestitutionSpeed",
            errors: errors
        );
        RequireIntRange(
            value: bodyContacts.RigidManifoldIterations,
            min: 1,
            max: WorldBodyContactPolicy.MaximumRigidManifoldIterations,
            name: "collision.bodyContacts.rigidManifoldIterations",
            errors: errors
        );
        RequireIntRange(
            value: bodyContacts.RigidPairIterationCeiling,
            min: 1,
            max: WorldBodyContactPolicy.MaximumRigidPairIterationCeiling,
            name: "collision.bodyContacts.rigidPairIterationCeiling",
            errors: errors
        );
        RequireIntRange(
            value: bodyContacts.RigidPairIterationBudget,
            min: 1,
            max: WorldBodyContactPolicy.MaximumRigidPairIterationBudget,
            name: "collision.bodyContacts.rigidPairIterationBudget",
            errors: errors
        );
    }
    private static void ValidateDistribution(WorldDistribution distribution, string path, HashSet<string> spawnPointIds, bool allowDisc, bool allowPoints, bool allowLattice, bool allowZeroDisc, List<string> errors, bool allowNoise = false, bool allowScatter = false) {
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
            case WorldDistributionRegion.Noise noise when allowNoise:
                ValidateSampledGrid(
                    cellSize: noise.CellSize,
                    depth: noise.Depth,
                    errors: errors,
                    path: $"{path}.region",
                    width: noise.Width
                );

                if (
                    !float.IsFinite(f: noise.Threshold) ||
                    (noise.Threshold < 0f) ||
                    (noise.Threshold >= 1f)
                ) {
                    errors.Add(item: $"{path}.region.threshold must be in [0, 1) (was {noise.Threshold}).");
                }
                if (noise.Frequency < 1) {
                    errors.Add(item: $"{path}.region.frequency must be at least 1 (noise-cell edge in grid cells; was {noise.Frequency}).");
                }
                if ((noise.Octaves < 1) || (noise.Octaves > 4)) {
                    errors.Add(item: $"{path}.region.octaves must be in 1..4 (was {noise.Octaves}).");
                }

                ValidateSequence(
                    sequence: distribution.Fill,
                    path: $"{path}.fill",
                    minIndex: 0,
                    errors: errors,
                    WorldSequence.None
                );
                break;
            case WorldDistributionRegion.Scatter scatter when allowScatter:
                ValidateSampledGrid(
                    cellSize: scatter.CellSize,
                    depth: scatter.Depth,
                    errors: errors,
                    path: $"{path}.region",
                    width: scatter.Width
                );

                if (scatter.Spacing < 2) {
                    errors.Add(item: $"{path}.region.spacing must be at least 2 cells (was {scatter.Spacing}).");
                }
                if ((scatter.Radius < 1) || ((2 * scatter.Radius) > scatter.Spacing)) {
                    errors.Add(item: $"{path}.region.radius must be at least 1 and at most spacing/2 (a jittered point never leaves its block; was {scatter.Radius} against spacing {scatter.Spacing}).");
                }

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
            predicate: hud.Defaults?.Visible,
            stateRows: stateRows
        );
        ValidateHudVisible(
            definition: (isIdentityScope ? null : definition),
            errors: errors,
            path: "hud.defaults.cursor.visible",
            predicate: hud.Defaults?.Cursor?.Visible,
            stateRows: stateRows
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
                predicate: panel.Visible,
                stateRows: stateRows
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
    // An owned world's identity-carried locomotion state rows feed WorldIdentity's live speed reads raw at
    // construction — no verb door runs on a document load — so a non-positive persisted value is refused here;
    // identity.motion refuses the same range live.
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
    // The voice-babble document vocabulary: both selectors are optional (ABSENT means the identity has no
    // authored voice yet — see WorldVoiceProfile's remarks), but a DECLARED one must resolve/range-check, the same
    // posture ValidateIdentityMotionState takes for its two state-slot fields. patchIds is the same asset-row id
    // set every other patchId reference (speaker sources, emission facets, cue rows, embellishments) resolves
    // against, computed once before row validation runs (see this file's caller).
    private static void ValidateIdentityVoiceProfile(WorldIdentityDefinition? identity, HashSet<string> patchIds, List<string> errors) {
        if (identity?.Voice is not { } voice) {
            return;
        }

        if (
            (voice.PatchId is { } patchId) &&
            (string.IsNullOrWhiteSpace(value: patchId) || !patchIds.Contains(item: patchId))
        ) {
            errors.Add(item: $"identity.voice.patchId '{patchId}' names no patch row.");
        }

        if (
            (voice.CadenceTicks is { } cadenceTicks) &&
            (cadenceTicks <= 0)
        ) {
            errors.Add(item: $"identity.voice.cadenceTicks {cadenceTicks} must be positive.");
        }
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
    // The Noise/Scatter placement-local grid both regions declare (see WorldDistributionRegion.Noise/.Scatter):
    // width/depth bound the worst-case materialized copy count against the engine's static-instance ceiling
    // (WorldPlacementStamp.MaterializedCopyCeiling), so a refusal here is what keeps that count computable without
    // resolving the actual hash-sampled offsets during validation.
    private static void ValidateSampledGrid(float cellSize, int width, int depth, string path, List<string> errors) {
        if (
            !float.IsFinite(f: cellSize) ||
            (cellSize <= 0f)
        ) {
            errors.Add(item: $"{path}.cellSize must be finite and positive (was {cellSize}).");
        }

        if (width < 1) {
            errors.Add(item: $"{path}.width must be at least 1 (was {width}).");
        }

        if (depth < 1) {
            errors.Add(item: $"{path}.depth must be at least 1 (was {depth}).");
        }

        if (
            (width >= 1) &&
            (depth >= 1) &&
            (CreationStampSampling.NoiseInstanceCeiling(depth: depth, width: width) > SdfProgramBuilder.MaxInstances)
        ) {
            errors.Add(item: $"{path} width x depth ({width}x{depth}) worst-case exceeds the {SdfProgramBuilder.MaxInstances}-instance engine ceiling.");
        }
    }
    // Vocabulary only — the shallow half of the shallow-then-deep split WorldScreenSource.Machine's Options string
    // already uses: an id naming no shipped shader set (no puck.shader.v1 manifest with that stem) refuses here, at
    // load, by name and index — not a silent no-op discovered only once the composition root tries to compose the
    // chain. Each entry's own Config is opaque here; the manifest's declared config schema validates it at boot,
    // refusing with the set id and reason on a malformed value.
    private static void ValidateRenderExtensions(IReadOnlyList<WorldRenderExtensionEntry>? extensions, List<string> errors, ICollection<string>? deferred) {
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
            } else {
                switch (WorldExtensionVocabularyHook.IsRegisteredPostRenderExtension(extensionId: entry.Id)) {
                    case false:
                        errors.Add(item: $"{path}.id '{entry.Id}' names no registered post-render extension.");

                        break;
                    case null:
                        deferred?.Add(item: $"{path}.id: post-render extension '{entry.Id}' registration deferred — this host carries no post-render extension catalog.");

                        break;
                }
            }
        }
    }
    // The far distance is the depth every camera march ends at; the band is the representable one (see the constants'
    // remarks), refused by name so a world authoring 0, a negative, or a depth past float's epsilon reach never boots
    // into a renderer whose cone proofs would rest on rounding. Absent resolves to the engine's pinned default.
    private static void ValidateRenderFarDistance(float? farDistance, List<string> errors) {
        if (farDistance is { } value) {
            RequireRange(
                value: value,
                min: WorldRenderDefaults.MinFarDistance,
                max: WorldRenderDefaults.MaxFarDistance,
                name: "render.farDistance",
                errors: errors
            );
        }
    }
    // shape non-null = a render.cycle key: it may move a light only in a slot the statics author, with the same kind,
    // and never add or remove one — the lane blend between two keys holds kinds and counts, so a key that changed the
    // list shape would blend one light's lanes into another's.
    private static void ValidateRenderLighting(WorldDefinition definition, WorldRenderLighting? lighting, List<string> errors, string path = "render.lighting", WorldRenderLighting? shape = null) {
        if (lighting is null) {
            return;
        }

        if (lighting.Lights is { } lights) {
            if (lights.Count > SdfEnvironment.MaxLights) {
                errors.Add(item: $"{path}.lights carries {lights.Count} lights; at most {SdfEnvironment.MaxLights} fit the environment.");
            }

            if (shape is not null) {
                var authored = (shape.Lights?.Count ?? 0);

                if (lights.Count != authored) {
                    errors.Add(item: $"{path}.lights carries {lights.Count} lights but the static render.lighting authors {authored}; a cycle key moves lights by slot and may not add or remove one.");
                }
            }

            var shadowing = 0;

            for (var index = 0; (index < lights.Count); index++) {
                var light = lights[index];
                var lightPath = $"{path}.lights[{index}]";

                if (light is null) {
                    errors.Add(item: $"{lightPath} must be a light.");

                    continue;
                }

                if ((shape?.Lights is { } authoredLights) && (index < authoredLights.Count) && (authoredLights[index]?.GetType() != light.GetType())) {
                    errors.Add(item: $"{lightPath} must keep the kind the static render.lighting authors in that slot.");
                }

                switch (light) {
                    case WorldRenderLight.Directional directional: {
                            if (directional.Direction is { } direction) {
                                if (!IsFinite(value: direction)) {
                                    errors.Add(item: $"{lightPath}.direction must contain finite coordinates.");
                                } else if (direction.LengthSquared() <= 0f) {
                                    errors.Add(item: $"{lightPath}.direction must be nonzero.");
                                }
                            }

                            if (directional.Weight is { } weight) {
                                RequireNonNegative(
                                    errors: errors,
                                    name: $"{lightPath}.weight",
                                    value: weight
                                );
                            }

                            if (directional.AngularRadius is { } angularRadius) {
                                RequireRange(
                                    value: angularRadius,
                                    min: 0f,
                                    max: MathF.Atan(x: SdfEnvironment.MaxPenumbraSlope),
                                    name: $"{lightPath}.angularRadius",
                                    errors: errors
                                );
                            }

                            if (directional.Shadows == true) {
                                shadowing++;
                            }

                            break;
                        }
                    case WorldRenderLight.Hemisphere hemisphere: {
                            if (hemisphere.Base is { } ambientBase) {
                                RequireNonNegative(
                                    errors: errors,
                                    name: $"{lightPath}.base",
                                    value: ambientBase
                                );
                            }

                            if (hemisphere.Gradient is { } gradient) {
                                RequireFinite(
                                    errors: errors,
                                    name: $"{lightPath}.gradient",
                                    value: gradient
                                );
                            }

                            break;
                        }
                    case WorldRenderLight.Rim rim: {
                            if (rim.Weight is { } weight) {
                                RequireNonNegative(
                                    errors: errors,
                                    name: $"{lightPath}.weight",
                                    value: weight
                                );
                            }

                            if (rim.Power is { } power) {
                                RequireNonNegative(
                                    errors: errors,
                                    name: $"{lightPath}.power",
                                    value: power
                                );
                            }

                            break;
                        }
                }

                if (
                    (LightColor(light: light) is { } color) &&
                    !color.IsAuthorable(definition: definition)
                ) {
                    errors.Add(item: $"{lightPath}.color '{color.Raw}' {BindableColor.Grammar}.");
                }
            }

            if (shadowing > 1) {
                errors.Add(item: $"{path}.lights names {shadowing} shadowing lights; the soft-shadow march runs once per lit pixel, so at most one light shadows.");
            }
        }

        if (lighting.Curvature is { } curvature) {
            foreach (var (name, value) in new (string, float?)[] { ("cavity", curvature.Cavity), ("rim", curvature.Rim), ("ink", curvature.Ink) }) {
                if (value is { } gain) {
                    RequireNonNegative(
                        errors: errors,
                        name: $"{path}.curvature.{name}",
                        value: gain
                    );
                }
            }

            if (curvature.InkLow is { } inkLow) {
                RequireNonNegative(
                    errors: errors,
                    name: $"{path}.curvature.inkLow",
                    value: inkLow
                );
            }

            if (curvature.InkHigh is { } inkHigh) {
                RequireNonNegative(
                    errors: errors,
                    name: $"{path}.curvature.inkHigh",
                    value: inkHigh
                );
            }

            // The outline is a smoothstep across the band, which needs a positive width to have any inside. Judged
            // where the band RESOLVES: an absent end takes the engine default, so authoring only the other end past
            // it inverts the band just as surely. A cycle key's band resolves through inheritance instead
            // (ValidateRenderCycleResolution).
            if (shape is null) {
                var low = (curvature.InkLow ?? SdfEnvironment.DefaultCurvatureInkLow);
                var high = (curvature.InkHigh ?? SdfEnvironment.DefaultCurvatureInkHigh);

                if (low >= high) {
                    errors.Add(item: $"{path}.curvature.inkLow ({low}) must be below {path}.curvature.inkHigh ({high}); an absent end is the engine default.");
                }
            }

            if (
                (curvature.InkColor is { } inkColor) &&
                !inkColor.IsAuthorable(definition: definition)
            ) {
                errors.Add(item: $"{path}.curvature.inkColor '{inkColor.Raw}' {BindableColor.Grammar}.");
            }
        }
    }
    private static BindableColor? LightColor(WorldRenderLight light) => (light switch {
        WorldRenderLight.Directional directional => directional.Color,
        WorldRenderLight.Hemisphere hemisphere => hemisphere.Color,
        WorldRenderLight.Rim rim => rim.Color,
        _ => null,
    });
    // shape non-null = a render.cycle key: it may move a layer only of a kind the statics author, and a gradient only
    // through the stops the statics author, by index.
    private static void ValidateRenderSky(WorldDefinition definition, WorldRenderSky? sky, List<string> errors, string path = "render.sky", WorldRenderSky? shape = null, WorldRenderLighting? lighting = null) {
        if (sky?.Layers is not { } layers) {
            return;
        }

        var seen = new HashSet<Type>();

        for (var index = 0; (index < layers.Count); index++) {
            var layer = layers[index];
            var layerPath = $"{path}.layers[{index}]";

            if (layer is null) {
                errors.Add(item: $"{layerPath} must be a layer.");

                continue;
            }

            if (!seen.Add(item: layer.GetType())) {
                errors.Add(item: $"{layerPath} repeats a layer kind; each kind appears at most once.");
            }

            if ((shape is not null) && !(shape.Layers?.Any(predicate: authored => authored?.GetType() == layer.GetType()) ?? false)) {
                errors.Add(item: $"{layerPath} moves a layer kind the static render.sky does not author; a cycle key may not add a layer.");
            }

            switch (layer) {
                case WorldRenderSkyLayer.Gradient gradient: {
                        var stops = gradient.Stops;

                        if (stops is null) {
                            if (shape is null) {
                                errors.Add(item: $"{layerPath}.stops must carry two to {SdfEnvironment.MaxSkyStops} stops.");
                            }

                            break;
                        }

                        if (shape is null) {
                            if ((stops.Count < 2) || (stops.Count > SdfEnvironment.MaxSkyStops)) {
                                errors.Add(item: $"{layerPath}.stops carries {stops.Count} stops; a gradient carries two to {SdfEnvironment.MaxSkyStops}.");
                            }
                        } else {
                            var authoredStops = ((shape.Layers?.OfType<WorldRenderSkyLayer.Gradient>().FirstOrDefault()?.Stops?.Count) ?? 0);

                            if (stops.Count != authoredStops) {
                                errors.Add(item: $"{layerPath}.stops carries {stops.Count} stops but the static gradient authors {authoredStops}; a cycle key moves stops by index and may not add or remove one.");
                            }
                        }

                        float? previous = null;

                        for (var stopIndex = 0; (stopIndex < stops.Count); stopIndex++) {
                            var stop = stops[stopIndex];
                            var stopPath = $"{layerPath}.stops[{stopIndex}]";

                            if (stop is null) {
                                errors.Add(item: $"{stopPath} must be a stop.");

                                continue;
                            }

                            if (stop.Elevation is { } elevation) {
                                RequireRange(
                                    value: elevation,
                                    min: -1f,
                                    max: 1f,
                                    name: $"{stopPath}.elevation",
                                    errors: errors
                                );

                                if ((previous is { } last) && (elevation <= last)) {
                                    errors.Add(item: $"{stopPath}.elevation must exceed the previous stop's.");
                                }

                                previous = elevation;
                            } else if (shape is null) {
                                errors.Add(item: $"{stopPath}.elevation is required.");
                            }

                            if (stop.Color is { } color) {
                                if (!color.IsAuthorable(definition: definition)) {
                                    errors.Add(item: $"{stopPath}.color '{color.Raw}' {BindableColor.Grammar}.");
                                }
                            } else if (shape is null) {
                                errors.Add(item: $"{stopPath}.color is required.");
                            }
                        }

                        break;
                    }
                case WorldRenderSkyLayer.Fog fog: {
                        if (fog.Density is { } density) {
                            RequireNonNegative(
                                errors: errors,
                                name: $"{layerPath}.density",
                                value: density
                            );
                        }

                        break;
                    }
                case WorldRenderSkyLayer.SunDisc disc: {
                        if (disc.Radius is { } radius) {
                            RequireRange(
                                value: radius,
                                min: 0f,
                                max: (MathF.PI / 2f),
                                name: $"{layerPath}.radius",
                                errors: errors,
                                minExclusive: true
                            );
                        }

                        if (disc.Intensity is { } intensity) {
                            RequireNonNegative(
                                errors: errors,
                                name: $"{layerPath}.intensity",
                                value: intensity
                            );
                        }

                        if (disc.Light is { } lightIndex) {
                            var lights = lighting?.Lights;

                            if ((lights is null) || (lightIndex < 0) || (lightIndex >= lights.Count) || (lights[lightIndex] is not WorldRenderLight.Directional)) {
                                errors.Add(item: $"{layerPath}.light must name a directional light's slot in render.lighting.lights.");
                            }
                        }

                        break;
                    }
                case WorldRenderSkyLayer.Stars stars: {
                        if (stars.Density is { } density) {
                            RequirePositive(
                                value: density,
                                name: $"{layerPath}.density",
                                errors: errors
                            );
                        }

                        if (stars.Brightness is { } brightness) {
                            RequireNonNegative(
                                value: brightness,
                                name: $"{layerPath}.brightness",
                                errors: errors
                            );
                        }

                        if (stars.Twinkle is { } twinkle) {
                            if (twinkle.Share is { } share) {
                                RequireUnitInterval(
                                    value: share,
                                    name: $"{layerPath}.twinkle.share",
                                    errors: errors
                                );
                            }

                            if (twinkle.Depth is { } depth) {
                                RequireUnitInterval(
                                    value: depth,
                                    name: $"{layerPath}.twinkle.depth",
                                    errors: errors
                                );
                            }

                            if (twinkle.Rate is { } rate) {
                                RequirePositive(
                                    value: rate,
                                    name: $"{layerPath}.twinkle.rate",
                                    errors: errors
                                );
                            }
                        }

                        break;
                    }
                case WorldRenderSkyLayer.Clouds clouds: {
                        if (clouds.Coverage is { } coverage) {
                            RequireUnitInterval(
                                value: coverage,
                                name: $"{layerPath}.coverage",
                                errors: errors
                            );
                        }

                        if (clouds.Softness is { } softness) {
                            RequireRange(
                                value: softness,
                                min: 0f,
                                max: 1f,
                                name: $"{layerPath}.softness",
                                errors: errors,
                                minExclusive: true
                            );
                        }

                        if (clouds.Scale is { } scale) {
                            RequirePositive(
                                value: scale,
                                name: $"{layerPath}.scale",
                                errors: errors
                            );
                        }

                        if (
                            (clouds.Color is { } cloudColor) &&
                            !cloudColor.IsAuthorable(definition: definition)
                        ) {
                            errors.Add(item: $"{layerPath}.color '{cloudColor.Raw}' {BindableColor.Grammar}.");
                        }

                        if (
                            (clouds.Drift is { } drift) &&
                            (!float.IsFinite(f: drift.X) || !float.IsFinite(f: drift.Y))
                        ) {
                            errors.Add(item: $"{layerPath}.drift must contain finite coordinates.");
                        }

                        if (
                            (clouds.Shear is { } shear) &&
                            (!float.IsFinite(f: shear.X) || !float.IsFinite(f: shear.Y))
                        ) {
                            errors.Add(item: $"{layerPath}.shear must contain finite coordinates.");
                        }

                        if (clouds.Spin is { } spin) {
                            RequireFinite(
                                errors: errors,
                                name: $"{layerPath}.spin",
                                value: spin
                            );
                        }

                        if (clouds.Curl is { } curl) {
                            RequireFinite(
                                errors: errors,
                                name: $"{layerPath}.curl",
                                value: curl
                            );
                        }

                        break;
                    }
            }
        }
    }
    private static void ValidateRenderEnvironment(WorldDefinition definition, WorldRenderEnvironment? environment, List<string> errors, string path = "render.environment") {
        if (environment is null) {
            return;
        }

        if (environment.Softboxes is { } softboxes) {
            if (softboxes.Count > SdfEnvironment.MaxSoftboxes) {
                errors.Add(item: $"{path}.softboxes carries {softboxes.Count} softboxes; at most {SdfEnvironment.MaxSoftboxes} fit the environment.");
            }

            for (var index = 0; (index < softboxes.Count); index++) {
                var softbox = softboxes[index];
                var softboxPath = $"{path}.softboxes[{index}]";

                if (softbox is null) {
                    errors.Add(item: $"{softboxPath} must be a softbox.");

                    continue;
                }

                var direction = softbox.Direction;

                if (!float.IsFinite(f: direction.X) || !float.IsFinite(f: direction.Y) || !float.IsFinite(f: direction.Z)) {
                    errors.Add(item: $"{softboxPath}.direction must contain finite coordinates.");
                } else if (((direction.X * direction.X) + (direction.Y * direction.Y) + (direction.Z * direction.Z)) <= 0f) {
                    errors.Add(item: $"{softboxPath}.direction must be nonzero.");
                }

                var size = softbox.Size;

                if (!float.IsFinite(f: size.X) || !float.IsFinite(f: size.Y)) {
                    errors.Add(item: $"{softboxPath}.size must contain finite coordinates.");
                } else if ((size.X <= 0f) || (size.Y <= 0f)) {
                    errors.Add(item: $"{softboxPath}.size must be strictly positive on both axes.");
                }

                if (softbox.Weight is { } weight) {
                    RequireNonNegative(
                        value: weight,
                        name: $"{softboxPath}.weight",
                        errors: errors
                    );
                }

                if (softbox.Blur is { } blur) {
                    RequireNonNegative(
                        value: blur,
                        name: $"{softboxPath}.blur",
                        errors: errors
                    );
                }

                if (
                    (softbox.Color is { } color) &&
                    !color.IsAuthorable(definition: definition)
                ) {
                    errors.Add(item: $"{softboxPath}.color '{color.Raw}' {BindableColor.Grammar}.");
                }
            }
        }

        if (environment.Horizon is { } horizon) {
            if (
                (horizon.Low is { } low) &&
                !low.IsAuthorable(definition: definition)
            ) {
                errors.Add(item: $"{path}.horizon.low '{low.Raw}' {BindableColor.Grammar}.");
            }

            if (
                (horizon.High is { } high) &&
                !high.IsAuthorable(definition: definition)
            ) {
                errors.Add(item: $"{path}.horizon.high '{high.Raw}' {BindableColor.Grammar}.");
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
                    !WorldObjectId.IsValid(value: oid)
                ) {
                    errors.Add(item: $"{path}.oid '{oid}' is not a well-formed object id.");
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
            errors.Add(item: $"bodies.disclosure.mode '{row.Mode}' is not defined.");

            return;
        }

        if (row.Mode == WorldObserverDisclosureMode.Radius) {
            if (row.Radius is not { } radius) {
                errors.Add(item: "bodies.disclosure.radius is required for mode 'radius'.");
            } else {
                RequirePositive(
                    errors: errors,
                    name: "bodies.disclosure.radius",
                    value: radius
                );
            }
        } else if (row.Radius is not null) {
            errors.Add(item: $"bodies.disclosure.radius must be absent for mode '{row.Mode}' — only 'radius' reads one.");
        }
        RequireRange(
            errors: errors,
            max: WorldObserverDisclosure.MaximumUpdateSeconds,
            min: 0f,
            name: "bodies.disclosure.updateSeconds",
            value: row.UpdateSeconds
        );
    }
    private static void ValidatePlayerDefaults(WorldPlayerDefaults defaults, List<string> errors) {
        ValidateSeatLook(
            seatLook: defaults.SeatLook,
            path: "seatDefaults.seatLook",
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
                var path = $"seatDefaults.identities[{index}]";

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
            errors.Add(item: "seatDefaults.neutralColor must be #RRGGBB.");
        }

        if (!IsHexColor(value: defaults.PickerNeutralColor)) {
            errors.Add(item: "seatDefaults.pickerNeutralColor must be #RRGGBB.");
        }
        ValidateSequence(
            sequence: defaults.ColorSequence,
            path: "seatDefaults.colorSequence",
            minIndex: 0,
            errors: errors,
            WorldSequence.Additive,
            WorldSequence.R1
        );
        RequireUnitInterval(
            value: defaults.Saturation,
            name: "seatDefaults.saturation",
            errors: errors
        );
        RequireUnitInterval(
            value: defaults.Value,
            name: "seatDefaults.value",
            errors: errors
        );
        RequireUnitInterval(
            value: defaults.NoseFactor,
            name: "seatDefaults.noseFactor",
            errors: errors
        );
        RequireUnitInterval(
            value: defaults.PickerThreshold,
            name: "seatDefaults.pickerThreshold",
            errors: errors
        );
        RequireUnitInterval(
            value: defaults.PickerNeutralBlend,
            name: "seatDefaults.pickerNeutralBlend",
            errors: errors
        );

        if (defaults.ColorSearchLimit < 1) {
            errors.Add(item: "seatDefaults.colorSearchLimit must be positive.");
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
            errors.Add(item: $"bodies.seatActivation must contain exactly {localSeats} entries.");

            return;
        }

        if (
            (localSeats > 0) &&
            (seatActivation[0] != SeatActivationPolicy.Eager)
        ) {
            errors.Add(item: "bodies.seatActivation[0] must be 'eager' — the session's first seat cannot start on-demand.");
        }
    }
    private static void ValidateSeatSpawns(IReadOnlyList<string> seatSpawns, HashSet<string> spawnPointIds, int localSeats, List<string> errors) {
        if (
            (seatSpawns is not { Count: var count }) ||
            (count != localSeats)
        ) {
            errors.Add(item: $"bodies.seatSpawns must contain exactly {localSeats} spawn-point names.");

            return;
        }

        for (var index = 0; (index < seatSpawns.Count); index++) {
            if (
                string.IsNullOrWhiteSpace(value: seatSpawns[index]) ||
                !spawnPointIds.Contains(item: seatSpawns[index])
            ) {
                errors.Add(item: $"bodies.seatSpawns[{index}] '{seatSpawns[index]}' names no spawn point.");
            }
        }
    }
    // An authored simulation rate must be exactly 0 (resident, non-stepping — see WorldSimulationDefaults.RateHz) or
    // a positive divisor of the fixed 50400 engine-tick base (FixedTickConversion.TicksPerSecond), so EngineTicks.PerRate always
    // derives a whole engine-tick step width. A negative rate is refused outright; the divisor check applies only to
    // a positive rate (0 divides nothing).
    private static void ValidateSimulation(WorldSimulationDefaults? simulation, List<string> errors) {
        if (simulation is null) {
            // Unauthored — WorldDefinition.SimulationRateHz reads UnauthoredSimulationRateHz, a valid divisor by
            // construction. Nothing to check.
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
    private static void ValidateHudVisible(OverlayPredicate? predicate, string path, WorldDefinition? definition, List<string> errors, IReadOnlyDictionary<string, WorldStateRow>? stateRows = null) {
        var predicateErrors = new List<string>();

        ValidateOverlayPredicate(
            definition: definition,
            errors: predicateErrors,
            path: path,
            predicate: predicate,
            stateRows: stateRows
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
