using System.Globalization;
using Puck.Abstractions.Presentation;
using Puck.Forge.Authoring;
using Puck.SignedDistance;
using Puck.Text;
using Puck.Physics.Motion;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The creation's Locomotion token, resolved as a kit name (the creator's rule; null when the creation/token is absent).
    private static string? ResolveLocomotionKit(WorldDefinition definition, string creationId) {
        foreach (var creation in definition.Creations) {
            if (
                (creation is not null) &&
                string.Equals(
                a: creation.Id,
                b: creationId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return creation.Document.Behavior?.Locomotion;
            }
        }

        return null;
    }
    private static bool ShapesContain(IReadOnlyList<Puck.Forge.Authoring.ShapeDocument> shapes, int id) {
        for (var index = 0; (index < shapes.Count); index++) {
            if (shapes[index].Id == id) {
                return true;
            }
        }

        return false;
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
                if (
                    (entity.Index < 0) ||
                    (entity.Index >= populationCapacity)
                ) {
                    errors.Add(item: $"{path}.index {entity.Index} is outside 0..{(populationCapacity - 1)} for the authored population capacity.");
                }

                break;
            case WorldAnchor.EntityPart part:
                if (
                    (part.Index < 0) ||
                    (part.Index >= populationCapacity)
                ) {
                    errors.Add(item: $"{path}.index {part.Index} is outside 0..{(populationCapacity - 1)} for the authored population capacity.");
                }

                if (string.IsNullOrWhiteSpace(value: part.PartId)) {
                    errors.Add(item: $"{path}.partId is required.");
                }

                break;
            case WorldAnchor.Placement placement:
                if (
                    string.IsNullOrWhiteSpace(value: placement.PlacementId) ||
                    !placementIds.Contains(item: placement.PlacementId)
                ) {
                    errors.Add(item: $"{path}.placementId '{placement.PlacementId}' names no placement row.");

                    break;
                }

                if (placement.ShapeId is { } shapeId) {
                    var row = WorldDefinitionRows.FindPlacement(
                        placements: placements,
                        id: placement.PlacementId
                    );
                    var creation = ((row is null)
                        ? null
                        : WorldDefinitionRows.FindCreation(
                            creations: creations,
                            id: row.CreationId
                        )
                    );

                    if (
                        (creation?.Document.Shapes is not { } shapes) ||
                        !ShapesContain(
                        id: shapeId,
                        shapes: shapes
                    )
                    ) {
                        errors.Add(item: $"{path}.shapeId {shapeId} names no shape in placement '{placement.PlacementId}''s creation.");
                    }
                }

                break;
            case WorldAnchor.Group group:
                if (group.Indices is { } indices) {
                    for (var index = 0; (index < indices.Count); index++) {
                        if (
                            (indices[index] < 0) ||
                            (indices[index] >= populationCapacity)
                        ) {
                            errors.Add(item: $"{path}.indices[{index}] {indices[index]} is outside 0..{(populationCapacity - 1)} for the authored population capacity.");
                        }
                    }
                }

                if (
                    !float.IsFinite(f: group.SmoothRate) ||
                    (group.SmoothRate <= 0f)
                ) {
                    errors.Add(item: $"{path}.smoothRate must be positive and finite.");
                }

                break;
            default:
                errors.Add(item: $"{path} is an unknown anchor kind.");

                break;
        }
    }
    // The kit assignment policy (SIM-AFFECTING): hash needs nothing more; table needs a non-empty cycle whose every
    // entry resolves to a declared kit name.
    private static void ValidateAssignment(WorldRowAssignment assignment, HashSet<string> kitNames, List<string> errors) {
        if (assignment is null) {
            errors.Add(item: "assignment is required.");

            return;
        }

        ValidateRowAssignment(
            assignment: assignment,
            errors: errors,
            rowNames: kitNames,
            rowNoun: "kit",
            section: "assignment"
        );
    }
    // The editor/authoring policy row: every field finite/positive with a sane ceiling. The BOOT-CONSUMED
    // headroom fields are additionally capped against the engine's own limits — see
    // WorldAuthoringDefaults' remarks for which fields are boot-consumed vs. live-consumed — so a bad authored value
    // can never reach a boot's frozen render-envelope probe (a live-consumed field's bad value is caught the same
    // way, on every mutation, since the validator re-runs on every composed candidate).
    private static void ValidateAuthoring(WorldAuthoringDefaults authoring, List<string> errors) {
        RequireIntRange(
            value: authoring.AuthoringHeadroomScreens,
            min: 0,
            max: SdfProgramBuilder.MaxScreenSurfaces,
            name: "authoring.authoringHeadroomScreens",
            errors: errors
        );
        RequireIntRange(
            value: authoring.AuthoringHeadroomPlacements,
            min: 0,
            max: 256,
            name: "authoring.authoringHeadroomPlacements",
            errors: errors
        );
        RequirePositive(
            value: authoring.MinPlacementScale,
            name: "authoring.minPlacementScale",
            errors: errors
        );
        RequirePositive(
            value: authoring.MaxPlacementScale,
            name: "authoring.maxPlacementScale",
            errors: errors
        );

        if (
            float.IsFinite(f: authoring.MinPlacementScale) &&
            float.IsFinite(f: authoring.MaxPlacementScale) &&
            (authoring.MinPlacementScale > authoring.MaxPlacementScale)
        ) {
            errors.Add(item: $"authoring.minPlacementScale {authoring.MinPlacementScale} exceeds authoring.maxPlacementScale {authoring.MaxPlacementScale}.");
        }

        RequirePositive(
            value: authoring.CandidateRadius,
            name: "authoring.candidateRadius",
            errors: errors
        );
        RequireIntRange(
            value: authoring.CandidateCap,
            min: 1,
            max: 256,
            name: "authoring.candidateCap",
            errors: errors
        );

        RequireIntRange(
            value: authoring.PreviewDeadlineFrames,
            min: 1,
            max: 600,
            name: "authoring.previewDeadlineFrames",
            errors: errors
        );
        // The derived-face reserve: the slots boot-registered at [DerivedFaceBase, DerivedFaceBase + count). The
        // ceiling is the remaining span in the engine's screen table.
        RequireIntRange(
            value: authoring.DerivedFaceScreens,
            min: 0,
            max: WorldPlacementPolicy.MaxDerivedFaceScreens,
            name: "authoring.derivedFaceScreens",
            errors: errors
        );
    }
    private static void ValidateCollider(WorldCollider? collider, IReadOnlyList<WorldCreation> creations, string path, List<string> errors) {
        if (collider is null) {
            return;
        }

        switch (collider) {
            case WorldCollider.Sphere sphere:
                RequirePositive(
                    value: sphere.Radius,
                    name: $"{path}.radius",
                    errors: errors
                );
                break;
            case WorldCollider.Capsule capsule:
                RequirePositive(
                    value: capsule.Radius,
                    name: $"{path}.radius",
                    errors: errors
                );

                if (
                    !IsFinite(value: capsule.Endpoint) ||
                    (capsule.Endpoint.LengthSquared() <= 0f)
                ) {
                    errors.Add(item: $"{path}.endpoint must be finite and nonzero; use a sphere for a zero-length capsule.");
                }
                break;
            case WorldCollider.Box box:
                if (
                    !IsFinite(value: box.HalfExtents) ||
                    (box.HalfExtents.X <= 0f) ||
                    (box.HalfExtents.Y <= 0f) ||
                    (box.HalfExtents.Z <= 0f)
                ) {
                    errors.Add(item: $"{path}.halfExtents must contain finite positive coordinates.");
                }

                var rotationLength = box.Rotation.LengthSquared();
                if (
                    !float.IsFinite(f: rotationLength) ||
                    (rotationLength <= 0f)
                ) {
                    errors.Add(item: $"{path}.rotation must be finite and nonzero.");
                }
                break;
            case WorldCollider.FromCreation fromCreation:
                if (
                    string.IsNullOrWhiteSpace(value: fromCreation.CreationId) ||
                    (WorldDefinitionRows.FindCreation(
                    creations: creations,
                    id: fromCreation.CreationId
                ) is not { } creation)
                ) {
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
                    if (shapes[index].Type == SdfSolidPrimitive.Plane) {
                        errors.Add(item: $"{path} creation '{fromCreation.CreationId}' shape {index} is an unbounded plane, not a finite body volume.");
                    }
                }
                break;
            default:
                errors.Add(item: $"{path} has an unknown collider kind.");
                break;
        }

    }
    // The creation ASSET rows: id presence/uniqueness, the document's own strict schema + structural invariants
    // through CreationCanonicalizer (the ONE pipeline — never a re-implementation), the hash pin (the carried
    // hash must equal the canonical hash — a tampered/corrupt row rejects loudly), and the per-stamp shape budget
    // (word-exact ceiling). Returns the resolved id set for the placement gate.
    private static HashSet<string> ValidateCreations(WorldDefinition definition, IReadOnlyList<WorldCreation> creations, HashSet<string> fontNames, bool hasTextCatalog, List<string> errors) {
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
            var canonical = Puck.Forge.Authoring.CreationCanonicalizer.Canonicalize(
                document: creation.Document,
                source: creation.Id
            );

            if (!string.Equals(
                a: creation.Hash,
                b: canonical.Hash,
                comparisonType: StringComparison.Ordinal
            )) {
                errors.Add(item: $"{path}.hash '{creation.Hash}' does not match the canonical sha256 '{canonical.Hash}'.");
            }

            // A palette entry bound to state resolves against THIS world; the canonicalizer admits only the syntax.
            var palette = (creation.Document.Palette ?? []);

            for (var slot = 0; (slot < palette.Count); slot++) {
                if (!IsColor(
                    definition: definition,
                    value: palette[slot].Color
                )) {
                    errors.Add(item: $"{path}.doc.palette[{slot}].color {WorldColor.Grammar}.");
                }
            }

            var stampShapes = creation.Document.StampShapeCount();

            if (stampShapes > WorldPlacementPolicy.MaxShapesPerStamp) {
                errors.Add(item: $"{path} stamps {stampShapes} shapes, exceeding the {WorldPlacementPolicy.MaxShapesPerStamp}-shape per-stamp budget.");
            }

            foreach (var run in (creation.Document.TextRuns ?? [])) {
                if (!hasTextCatalog) {
                    errors.Add(item: $"{path}.doc.textRuns requires the world to declare a text font catalog.");

                    break;
                }

                if (
                    (run.Font is { } font) &&
                    !fontNames.Contains(item: font)
                ) {
                    errors.Add(item: $"{path}.doc.textRuns font '{font}' names no text.fonts row.");
                }
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
    // The per-instance face overrides: each names a declared creation face, no duplicate face names.
    private static void ValidateFaceSources(WorldDefinition definition, IReadOnlyList<WorldPlacementFace>? faceSources, WorldPlacement placement, IReadOnlyList<WorldCreation> creations, WorldFaceCatalog faces, HashSet<string> destinationNames, HashSet<string> fontNames, bool hasTextCatalog, string path, List<string> errors) {
        if (faceSources is not { Count: > 0 } sources) {
            return;
        }

        var creation = WorldDefinitionRows.FindCreation(
            creations: creations,
            id: placement.CreationId
        );
        var faceNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var face in (creation?.Document.Behavior?.Faces ?? [])) {
            _ = faceNames.Add(item: face.Name);
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        string? portalFaceName = null;

        for (var index = 0; (index < sources.Count); index++) {
            var source = sources[index];
            var facePath = $"{path}[{index}]";

            if (
                (source is null) ||
                string.IsNullOrWhiteSpace(value: source.Face)
            ) {
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
            } else if (source.Source is WorldScreenSource.Machine { Cable: not null }) {
                // The same rule ValidateScreenSource applies to magazine entries: a cable port rides a declared
                // screens row's own source, never a face's — a face-hosted machine has no stable screen identity
                // for a cable group to fold back onto.
                errors.Add(item: $"{facePath}.source.machine.cable is only legal on a declared screens row's own source — a face source cannot plug a cable.");
            } else if (source.Source is WorldScreenSource.Session session) {
                ValidateSessionSource(
                    session: session,
                    destinationNames: destinationNames,
                    portal: source.Portal,
                    path: facePath,
                    errors: errors
                );
            } else if (source.Source is WorldScreenSource.Text text) {
                ValidateTextSource(
                    definition: definition,
                    errors: errors,
                    fontNames: fontNames,
                    hasTextCatalog: hasTextCatalog,
                    path: facePath,
                    text: text
                );
            }

            if (source.Portal is { } portal) {
                ValidatePortal(
                    destinationNames: destinationNames,
                    errors: errors,
                    path: $"{facePath}.portal",
                    portal: portal
                );

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

                // The derived face itself: its shape kind must open an aperture (WorldFaceApertures), and its frame
                // must be yaw-only. A face may be DRAWN on any primitive at any orientation; a DOOR is narrower.
                if (faces.TryFind(
                    placementId: placement.Id,
                    faceName: source.Face,
                    out var portalRow
                )) {
                    if (portalRow.ShapeType is not { } shapeType) {
                        errors.Add(item: $"{facePath}.portal names face '{source.Face}', which declares no concrete shape (shapeId {(portalRow.ShapeId?.ToString(provider: CultureInfo.InvariantCulture) ?? "null")}) — a door needs a surface to open, so it has no aperture mapping.");
                    } else if (portalRow.Aperture is null) {
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
    // The INHABIT facet: the kit must resolve (its explicit kit name OR the creation's Locomotion token as a kit name),
    // a producer source must resolve on that kit, a named look must be declared, and count/distribution are bounded.
    private static void ValidateInhabit(WorldPlacementInhabit inhabit, WorldPlacement placement, string path, WorldDefinition definition, HashSet<string> kitNames, HashSet<string> lookNames, List<string> errors) {
        var resolvedKit = (inhabit.Kit ?? ResolveLocomotionKit(
            definition: definition,
            creationId: placement.CreationId
        ));

        if (
            (resolvedKit is null) ||
            !kitNames.Contains(item: resolvedKit)
        ) {
            errors.Add(item: $"{path} names no kit; the world declares: {string.Join(
                separator: ", ",
                values: kitNames
            )}.");
        } else if (
            inhabit.Source.IsProducer &&
            (inhabit.Source.ProducerName is { } producer) &&
            !definition.Kits.First(predicate: kit => string.Equals(
            a: kit.Name,
            b: resolvedKit,
            comparisonType: StringComparison.Ordinal
        )).Producers.ContainsKey(key: producer)
        ) {
            errors.Add(item: $"{path}.source names producer '{producer}', but kit '{resolvedKit}' declares no parameters for it.");
        }

        if (
            !inhabit.Source.IsLive &&
            !inhabit.Source.IsIdle &&
            !inhabit.Source.IsProducer
        ) {
            errors.Add(item: $"{path}.source '{inhabit.Source}' is not a defined IntentSource.");
        }

        if (
            (inhabit.Look is { Length: > 0 } lookName) &&
            !lookNames.Contains(item: lookName)
        ) {
            errors.Add(item: $"{path}.look '{lookName}' names no look row.");
        }

        var peerCapacity = Math.Max(
            val1: 0,
            val2: (definition.Population.Capacity - definition.Population.LocalSeats)
        );

        if (
            (inhabit.Count < 1) ||
            (inhabit.Count > peerCapacity)
        ) {
            errors.Add(item: $"{path}.count {inhabit.Count} is outside 1..{peerCapacity} for the authored population capacity.");
        }

        ValidateDistribution(
            distribution: inhabit.Distribution!,
            path: $"{path}.distribution",
            spawnPointIds: [],
            allowDisc: true,
            allowPoints: false,
            allowLattice: false,
            allowZeroDisc: true,
            errors: errors
        );
    }
    // The kit rows (SIM-AFFECTING): name presence/uniqueness, one motion program, producer parameters, actions, and
    // the machine-pad map.
    private static HashSet<string> ValidateKits(WorldDefinition definition, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, ISet<string> allChannelNames, ISet<string> compositionChannelNames, IReadOnlyDictionary<string, WorldStateRow> stateRows, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, List<string> errors) {
        var kitNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var programRows = BodyMotionProgramRows(programs: definition.BodyMotionPrograms);
        var targetRegisterNames = definition.TargetRegisters.Select(selector: register => register.Name).ToHashSet(comparer: StringComparer.Ordinal);
        var judgeRowNames = (definition.Judges ?? []).Select(selector: row => row.Name).ToHashSet(comparer: StringComparer.Ordinal);

        var kits = definition.Kits;

        // A kit is required exactly when the census implies a body to move (a derived refusal, not a flat floor):
        // zero declared capacity needs no kit at all.
        if (
            (kits.Count == 0) &&
            (definition.Population.Capacity > 0)
        ) {
            errors.Add(item: $"kits requires at least one row when population.capacity ({definition.Population.Capacity}) is nonzero.");

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

            if (
                string.IsNullOrWhiteSpace(value: kit.BodyMotionProgram) ||
                !programs.TryGetValue(
                key: kit.BodyMotionProgram,
                value: out var resolvedProgram
            )
            ) {
                errors.Add(item: $"{path}.bodyMotionProgram '{kit.BodyMotionProgram}' names no body motion program.");
            } else if (resolvedProgram.Kind != BodyProgramKind.Motion) {
                errors.Add(item: $"{path}.bodyMotionProgram '{kit.BodyMotionProgram}' names a {resolvedProgram.Kind} program, not Motion.");
            } else {
                motionProgram = resolvedProgram;
            }

            ValidateMotionModel(
                model: kit.Motion,
                program: motionProgram,
                path: $"{path}.motion",
                channelNames: compositionChannelNames,
                hasWater: (definition.Water is not null),
                errors: errors
            );
            ValidateProducerParameters(
                producers: kit.Producers,
                programs: programs,
                programRows: programRows,
                channelNames: allChannelNames,
                path: $"{path}.producers",
                errors: errors
            );

            // Actions is a channel-NAME-keyed map now (never a fixed Primary/Secondary pair): a kit naming an
            // undeclared or non-composition channel is a dead reference; a declared composition channel with no
            // entry here stays legal and inert per body (ActionSpec.Compile leaves that ordinal unbound).
            if (kit.Actions is not null) {
                foreach (var (channelName, spec) in kit.Actions) {
                    if (
                        string.IsNullOrWhiteSpace(value: channelName) ||
                        !compositionChannelNames.Contains(item: channelName)
                    ) {
                        errors.Add(item: $"{path}.actions names '{channelName}', which is not a declared composition channel.");

                        continue;
                    }

                    ValidateActionSpec(
                        errors: errors,
                        judgeRowNames: judgeRowNames,
                        path: $"{path}.actions[{channelName}]",
                        spec: spec,
                        stateRows: stateRows,
                        stateSlots: stateSlots,
                        targetRegisterNames: targetRegisterNames
                    );
                }
            }

            // The pad map is the SAME channel-name vocabulary actions key off, resolved at the other destination a
            // control application can carry a kit to: a screen's booted machine. Any declared channel may bind
            // (a movement role reaches a stick, a composition channel reaches a button), unlike actions, which are
            // composition-only.
            if (kit.PadRaw is not null) {
                foreach (var (channelName, element) in kit.Pad) {
                    if (
                        string.IsNullOrWhiteSpace(value: channelName) ||
                        !allChannelNames.Contains(item: channelName)
                    ) {
                        errors.Add(item: $"{path}.pad names '{channelName}', which is not a declared channel.");

                        continue;
                    }

                    if (!Enum.IsDefined(value: element)) {
                        errors.Add(item: $"{path}.pad['{channelName}'] element '{element}' is not a defined WorldPadElement.");
                    }
                }
            }

            ValidateCollider(
                collider: kit.Collider,
                creations: definition.Creations,
                path: $"{path}.collider",
                errors: errors
            );
        }

        // ABSENT (kitNames empty) derives DefaultSeatKit to "" (WorldDefinition.DefaultSeatKit), which names no kit
        // by construction — checking membership then would refuse the very absence this validator otherwise admits.
        if (
            (kitNames.Count > 0) &&
            !kitNames.Contains(item: definition.DefaultSeatKit)
        ) {
            errors.Add(item: $"defaultSeatKit '{definition.DefaultSeatKit}' names no kit row.");
        }

        return kitNames;
    }
    // The look assignment policy (PRESENTATION-ONLY): a table needs a non-empty cycle whose every entry resolves to a
    // declared look name. Reuses the shared row-assignment gate verbatim.
    private static void ValidateLookAssignment(WorldRowAssignment assignment, HashSet<string> lookNames, List<string> errors) {
        ValidateRowAssignment(
            assignment: assignment,
            errors: errors,
            rowNames: lookNames,
            rowNoun: "look",
            section: "lookAssignment"
        );
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
                        RequireIntRange(
                            errors: errors,
                            max: (WorldLookSource.Catalog.RigCount - 1),
                            min: 0,
                            name: $"{path}.source.index",
                            value: catalogIndex
                        );
                    }

                    break;
                case WorldLookSource.Creation creation:
                    if (
                        string.IsNullOrWhiteSpace(value: creation.CreationId) ||
                        !creationIds.Contains(item: creation.CreationId)
                    ) {
                        errors.Add(item: $"{path}.source.creationId '{creation.CreationId}' names no creation row.");
                    }

                    break;
                default:
                    errors.Add(item: $"{path}.source is an unknown kind '{(look.Source?.GetType().Name ?? "(null)")}'.");

                    break;
            }

            RequirePositive(
                value: look.Scale,
                name: $"{path}.scale",
                errors: errors
            );

            if (
                float.IsFinite(f: look.Scale) &&
                (look.Scale > MaxLookScale)
            ) {
                errors.Add(item: $"{path}.scale {look.Scale} exceeds the {MaxLookScale} look-scale ceiling.");
            }

            RequireNonNegative(
                value: look.Motion.GaitAmplitude,
                name: $"{path}.motion.gaitAmplitude",
                errors: errors
            );
            RequireNonNegative(
                value: look.Motion.SecondsPerFrame,
                name: $"{path}.motion.secondsPerFrame",
                errors: errors
            );

            if (
                look.Motion.ReplayFrames &&
                isCatalog
            ) {
                errors.Add(item: $"{path}.motion.replayFrames cannot be set on a catalog source — there is no timeline to replay.");
            }

            if (
                look.Motion.ReplayFrames &&
                (!float.IsFinite(f: look.Motion.SecondsPerFrame) || (look.Motion.SecondsPerFrame <= 0f))
            ) {
                errors.Add(item: $"{path}.motion.replayFrames requires a positive secondsPerFrame (a zero-hold replay is an infinite loop).");
            }
        }

        return names;
    }
    // The placement INSTANCE rows: id presence/uniqueness, the creation reference, finite transform, the policy
    // scale envelope, the lattice distribution's positive counts and finite steps, the mirror plane, and the animated-row
    // constraints (static-only facets; the reserved replay-pool ceiling, word-exact). Returns the resolved id set for
    // the anchor-union gate (a WorldAnchor.Placement resolves against it).
    private static HashSet<string> ValidatePlacements(IReadOnlyList<WorldPlacement> placements, WorldDefinition definition, HashSet<string> creationIds, HashSet<string> lookNames, HashSet<string> kitNames, WorldAuthoringDefaults authoring, HashSet<string> patchIds, bool requiresField, HashSet<string> destinationNames, HashSet<string> fontNames, bool hasTextCatalog, List<string> errors) {
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

            if (
                string.IsNullOrWhiteSpace(value: placement.CreationId) ||
                !creationIds.Contains(item: placement.CreationId)
            ) {
                errors.Add(item: $"{path}.creationId '{placement.CreationId}' names no creation row.");
            }

            if (!IsFinite(value: placement.Position)) {
                errors.Add(item: $"{path}.position must contain finite coordinates.");
            }

            RequireFinite(
                value: placement.YawDegrees,
                name: $"{path}.yawDegrees",
                errors: errors
            );

            if (
                !float.IsFinite(f: placement.Scale) ||
                (placement.Scale < authoring.MinPlacementScale) ||
                (placement.Scale > authoring.MaxPlacementScale)
            ) {
                errors.Add(item: $"{path}.scale {placement.Scale} is outside {authoring.MinPlacementScale}..{authoring.MaxPlacementScale}.");
            }

            if (placement.Distribution is { } distribution) {
                ValidateDistribution(
                    allowDisc: false,
                    allowLattice: true,
                    allowPoints: false,
                    allowZeroDisc: false,
                    distribution: distribution,
                    errors: errors,
                    path: $"{path}.distribution",
                    spawnPointIds: []
                );
            }

            if (placement.Mirror is { } mirror) {
                if (
                    !IsFinite(value: mirror.Normal) ||
                    (mirror.Normal.LengthSquared() <= 0f)
                ) {
                    errors.Add(item: $"{path}.mirror.normal must be finite and nonzero.");
                }

                RequireFinite(
                    value: mirror.Offset,
                    name: $"{path}.mirror.offset",
                    errors: errors
                );
            }

            // The emission facet binds to the placement root under a distribution (documented on WorldPlacement) — no
            // per-copy constraint to gate; only patch resolution and the shared gain/radius bounds.
            ValidateEmission(
                emission: placement.Emission,
                patchIds: patchIds,
                path: $"{path}.emission",
                errors: errors
            );

            if (placement.Solid is { } placementSolid) {
                RequireFinite(
                    value: placementSolid.Margin,
                    name: $"{path}.solid.margin",
                    errors: errors
                );

                if (WorldDefinitionRows.FindCreation(
                    creations: creations,
                    id: placement.CreationId
                ) is { } solidCreation) {
                    // A shape carrying domain ops compiles one collider PER EXPANDED COPY, so the ceiling counts the
                    // expansion, not the authored shape count. A fold with no rigid-copy expansion has no contact
                    // geometry at all under EITHER provider — the analytic one would collide against one copy of
                    // geometry the renderer draws several times, and the field one throws out of
                    // CreationStampEmitter.EmitFixed at boot — so the expansion refusal is ungated while the
                    // analytic collider ceiling below is not.
                    var shapeColliders = 0L;

                    foreach (var solidShape in (solidCreation.Document.Shapes ?? [])) {
                        if (!ShapeDomainOps.TryExpand(
                            domain: solidShape.Domain,
                            frames: out var solidFrames,
                            refusal: out var solidRefusal
                        )) {
                            errors.Add(item: $"{path}.solid names creation '{placement.CreationId}', whose shape {solidShape.Id} carries {solidRefusal} — a solid row needs contact geometry for every copy its fold draws.");

                            continue;
                        }

                        shapeColliders += solidFrames.Length;
                    }

                    // The field provider compiles every solid row into ONE program instead of one collider per copy,
                    // so the analytic ceiling does not describe what it costs.
                    if (!requiresField) {
                        var copies = CreationStampLattice.MaterializedCopyCount(
                            pattern: WorldPlacementStamp.PatternFor(placement: placement),
                            mirror: WorldPlacementStamp.MirrorFor(placement: placement),
                            ceiling: (WorldPlacementPolicy.MaxSolidPlacementColliders + 1L)
                        );
                        var contribution = CreationStampLattice.MultiplySaturated(
                            ceiling: (WorldPlacementPolicy.MaxSolidPlacementColliders + 1L),
                            left: copies,
                            right: shapeColliders
                        );
                        var previousColliderCount = solidPlacementColliderCount;

                        solidPlacementColliderCount = Math.Min(
                            val1: (WorldPlacementPolicy.MaxSolidPlacementColliders + 1L),
                            val2: (solidPlacementColliderCount + contribution)
                        );

                        if (
                            (previousColliderCount <= WorldPlacementPolicy.MaxSolidPlacementColliders) &&
                            (solidPlacementColliderCount > WorldPlacementPolicy.MaxSolidPlacementColliders)
                        ) {
                            errors.Add(item: $"{path}.solid expands the document past the {WorldPlacementPolicy.MaxSolidPlacementColliders}-collider analytic-placement ceiling; reduce lattice counts, mirror copies, or creation shapes.");
                        }
                    }
                }
            }

            // The animated-row constraints: a placement of a framed creation replays through the reserved dynamic
            // pool — single copy only (pattern/mirror are static-stamp facets), and at most the reserved pool count.
            var isAnimated = (WorldDefinitionRows.FindCreation(
                creations: creations,
                id: placement.CreationId
            ) is { Document.Frames.Count: > 0 });

            if (isAnimated) {
                stampRegistrationCount++;
                dynamicInstanceCount++;

                if (
                    (placement.Distribution is not null) ||
                    (placement.Mirror is not null)
                ) {
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
                ValidateInhabit(
                    definition: definition,
                    errors: errors,
                    inhabit: inhabit,
                    kitNames: kitNames,
                    lookNames: lookNames,
                    path: $"{path}.inhabit",
                    placement: placement
                );

                if (
                    (placement.Distribution is not null) ||
                    (placement.Mirror is not null)
                ) {
                    errors.Add(item: $"{path} INHABITS — placement distribution/mirror facets are incompatible with a live body.");
                }

                if (inhabit.Count > 0) {
                    dynamicInstanceCount += inhabit.Count;
                }
            }

            if (
                !isAnimated &&
                (placement.Inhabit is null)
            ) {
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

                if (
                    (previousInstanceCount <= SdfProgramBuilder.MaxInstances) &&
                    (staticPlacementInstanceCount > SdfProgramBuilder.MaxInstances)
                ) {
                    errors.Add(item: $"{path} expands the document past the {SdfProgramBuilder.MaxInstances}-instance engine ceiling; reduce lattice counts or mirror copies.");
                }
            }

            // The per-instance FACE overrides: each names a declared creation face, no duplicates. The View source's
            // camera name is resolved LENIENTLY (a derived creation-camera name is unknown to the document validator; the
            // binder lights an unresolved feed with its no-signal card, never a hard reject).
            ValidateFaceSources(
                definition: definition,
                faceSources: placement.FaceSources,
                placement: placement,
                creations: creations,
                faces: faces,
                destinationNames: destinationNames,
                fontNames: fontNames,
                hasTextCatalog: hasTextCatalog,
                path: $"{path}.faceSources",
                errors: errors
            );

            // The REGION facet: a named sensing volume (see WorldPlacementRegion's own doc). The name IS the
            // placement's own id, so no separate uniqueness check is owed — the id duplication check above already
            // covers it.
            if (placement.Region is { } region) {
                if (
                    !float.IsFinite(f: region.Radius) ||
                    (region.Radius <= 0f)
                ) {
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
                if (
                    (attach.BodyIndex < 0) ||
                    (attach.BodyIndex >= definition.Population.Capacity)
                ) {
                    errors.Add(item: $"{path}.attach.bodyIndex {attach.BodyIndex} is outside 0..{(definition.Population.Capacity - 1)} for the authored population capacity (0-based entity index — body:1 is player.* seat 2, not body index 1).");
                }

                if (!IsFinite(value: attach.LocalOffset)) {
                    errors.Add(item: $"{path}.attach.localOffset must contain finite coordinates.");
                }

                RequireFinite(
                    value: attach.LocalYawDegrees,
                    name: $"{path}.attach.localYawDegrees",
                    errors: errors
                );

                if (
                    (placement.Distribution is not null) ||
                    (placement.Mirror is not null)
                ) {
                    errors.Add(item: $"{path} ATTACHES — placement distribution/mirror facets are static-stamp-only (the same rule an animated or inhabited row already enforces), incompatible with a live-following root.");
                }

                // Only the FIELD contact provider still refuses solid+attach: it compiles every solid row's geometry
                // once into ONE SdfProgram (WorldSolidField.TryBuild) and is never rebuilt per tick, so an attached
                // row's collider would silently stop tracking the body. The analytic provider's WorldColliderSet
                // recomputes an attached row's colliders every tick instead (RefreshAttached) — see requiresField's
                // own derivation (WorldContactSelection.RequiresField reads collision.requirements).
                if (
                    requiresField &&
                    (placement.Solid is not null)
                ) {
                    errors.Add(item: $"{path} ATTACHES under the FIELD contact provider (collision.requirements is non-empty) — WorldSolidField compiles every solid row's geometry once and never rebuilds it per tick; the analytic provider tracks an attached solid instead.");
                }

                if (placement.Inhabit is not null) {
                    errors.Add(item: $"{path} ATTACHES — a row cannot both INHABIT (spawn its own driven bodies) and ATTACH (ride another body's pose).");
                }
            }

            // The CONTRIBUTION facet: a host-authored slot a federation partner fills (see WorldPlacementContribution).
            if (placement.Contribution is { } slotContribution) {
                ValidateContribution(
                    contribution: slotContribution,
                    creationIds: creationIds,
                    definition: definition,
                    errors: errors,
                    path: $"{path}.contribution",
                    placement: placement
                );
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
    // The PORTAL facet: destination must name an existing destinations row. Travel/Arrival enum shape is already
    // refused at strict parse. This pass owns what parse cannot: that the named destination row exists, and that
    // arrival/counterpart cohere (mapped requires a counterpart, a counterpart requires mapped, and its shape
    // parses). A counterpart's cross-document existence is NOT checked here — the named document is not resolved at
    // boot; Puck.World.WorldInstanceHost resolves it against the destination's delivered definition at transfer
    // time (see WorldPortalCounterpart).
    private static void ValidatePortal(WorldPlacementPortal portal, HashSet<string> destinationNames, string path, List<string> errors) {
        if (
            string.IsNullOrWhiteSpace(value: portal.Destination) ||
            !destinationNames.Contains(item: portal.Destination)
        ) {
            errors.Add(item: ((destinationNames.Count > 0)
                ? $"{path}.destination '{portal.Destination}' names no destinations row; the world declares: {string.Join(
                    separator: ", ",
                    values: destinationNames
                )}."
                : $"{path}.destination '{portal.Destination}' names no destinations row; the world declares none."));
        }

        if (portal.Arrival == WorldPortalArrival.Mapped) {
            if (string.IsNullOrWhiteSpace(value: portal.Counterpart)) {
                errors.Add(item: $"{path}.arrival 'mapped' requires {path}.counterpart (the destination document's border placementId/face) — none was authored.");
            } else if (!WorldPortalCounterpart.TryParse(
                counterpart: portal.Counterpart,
                placementId: out _,
                face: out _
            )) {
                errors.Add(item: $"{path}.counterpart '{portal.Counterpart}' is malformed — expected '<placementId>/<face>'.");
            }
        } else if (!string.IsNullOrWhiteSpace(value: portal.Counterpart)) {
            errors.Add(item: $"{path}.counterpart '{portal.Counterpart}' is authored but {path}.arrival is not 'mapped' — a counterpart names a mapped arrival's destination frame only.");
        }

        if (portal.Capacity is { } capacity) {
            RequireIntRange(
                errors: errors,
                max: WorldPopulationLimits.CapacityCeiling,
                min: 1,
                name: $"{path}.capacity",
                value: capacity
            );
        }
    }
    // The shared sequence/row-view gate for kit and look assignment.
    private static void ValidateRowAssignment(WorldRowAssignment assignment, string section, string rowNoun, HashSet<string> rowNames, List<string> errors) {
        ValidateSequence(
            sequence: assignment.Sequence,
            path: $"{section}.sequence",
            minIndex: 0,
            errors: errors,
            WorldSequence.Index,
            WorldSequence.Additive,
            WorldSequence.R1
        );

        for (var index = 0; (index < assignment.Rows.Count); index++) {
            if (!rowNames.Contains(item: assignment.Rows[index])) {
                errors.Add(item: $"{section}.rows[{index}] '{assignment.Rows[index]}' names no {rowNoun} row.");
            }
        }
    }
    // Document-wide: the simultaneous-window ceiling is OffscreenRenderBudget.PerProducedFrame (the presentation budget
    // the view stack's refresh share also reads), refused BY NAME — a window is an unbudgeted render, a full offscreen
    // submit every produced frame. Standalone (not in ValidateFaceSources): it needs the WHOLE document's placements.
    private static void ValidateSessionWindowBudget(IReadOnlyList<WorldPlacement> placements, List<string> errors) {
        var windows = new List<string>();

        foreach (var placement in placements) {
            if (placement is null) {
                continue;
            }

            foreach (var face in (placement.FaceSources ?? [])) {
                if (
                    (face is not null) &&
                    (face.Source is WorldScreenSource.Session { Projection: WorldScreenProjection.Window })
                ) {
                    windows.Add(item: $"{placement.Id}/{face.Face}");
                }
            }
        }

        if (windows.Count > OffscreenRenderBudget.PerProducedFrame) {
            errors.Add(item: $"placements author {windows.Count} 'window'-projection session face(s) ({string.Join(
                separator: ", ",
                values: windows
            )}), exceeding the simultaneous-window budget of {OffscreenRenderBudget.PerProducedFrame} — a window renders every produced frame regardless of whether anyone is looking through it, so this ceiling is a real GPU cost bound, not a taste one. Drop a window to a plain 'camera' projection or remove a face.");
        }
    }
    private static HashSet<string> ValidateTextCatalog(TextFontCatalogDefinition? text, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (text is null) {
            return names;
        }

        if (
            (text.Fonts is null) ||
            (text.Fonts.Count == 0)
        ) {
            errors.Add(item: "text.fonts must declare at least one font row.");

            return names;
        }

        for (var index = 0; (index < text.Fonts.Count); index++) {
            var font = text.Fonts[index];
            var path = $"text.fonts[{index}]";

            if (font is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: font.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (!names.Add(item: font.Name)) {
                errors.Add(item: $"{path}.name '{font.Name}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(value: font.Source)) {
                errors.Add(item: $"{path}.source is required.");
            } else {
                var portableSource = font.Source.Replace(
                    newChar: '/',
                    oldChar: '\\'
                );
                var segments = portableSource.Split(
                    options: StringSplitOptions.None,
                    separator: '/'
                );

                if (
                    font.Source.Contains(value: '\\') ||
                    portableSource.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "/"
                ) ||
                    portableSource.Contains(value: ':') ||
                    segments.Any(predicate: static segment => (segment is "" or "." or ".."))
                ) {
                    errors.Add(item: $"{path}.source '{font.Source}' must be a portable relative path contained beneath the world document directory (forward slashes, no empty/dot/parent segments).");
                }
            }

            if (!IsValidAddonHash(hash: font.Hash)) {
                errors.Add(item: $"{path}.hash '{font.Hash}' must match sha256-64/{{16 lowercase hex}}.");
            }

            if (
                (font.CodePointRanges is null) ||
                (font.CodePointRanges.Count == 0)
            ) {
                errors.Add(item: $"{path}.codePointRanges must declare at least one Unicode scalar or range.");
            } else {
                try {
                    _ = UnicodeCodePointRangeExpander.Expand(
                        ranges: font.CodePointRanges,
                        wildcardSelected: out _
                    );
                } catch (ArgumentException exception) {
                    errors.Add(item: $"{path}.codePointRanges is invalid: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
                }
            }

            var options = font.ToGenerationOptions();

            if (options.FaceIndex < 0) {
                errors.Add(item: $"{path}.faceIndex must not be negative.");
            }

            if (
                (options.FontPixelSize <= 0) ||
                (options.FontPixelSize > 512)
            ) {
                errors.Add(item: $"{path}.pixelSize must be in 1..512.");
            }

            if (
                !float.IsFinite(f: options.DistanceRange) ||
                (options.DistanceRange <= 0f) ||
                (options.DistanceRange > 64f)
            ) {
                errors.Add(item: $"{path}.distanceRange must be finite and in (0, 64].");
            }

            if (
                (options.Padding < MathF.Ceiling(x: options.DistanceRange)) ||
                (options.Padding > 256)
            ) {
                errors.Add(item: $"{path}.padding must be at least ceil(distanceRange) and at most 256.");
            }

            if (
                (options.Columns <= 0) ||
                (options.Columns > 4096)
            ) {
                errors.Add(item: $"{path}.columns must be in 1..4096.");
            }
        }

        if (
            string.IsNullOrWhiteSpace(value: text.DefaultFont) ||
            !names.Contains(item: text.DefaultFont)
        ) {
            errors.Add(item: $"text.defaultFont '{text.DefaultFont}' names no text.fonts row.");
        }

        return names;
    }
}
