using System.Globalization;
using Puck.Abstractions.Presentation;
using Puck.World.Authoring;
using Puck.SignedDistance;
using Puck.Text;
using Puck.Physics.Motion;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The null-tolerant face of the keyed row lookups: a malformed row whose id/name never parsed resolves to
    // nothing — the missing-key refusal is already recorded by the caller's own required-field check — exactly as
    // the linear scan these dictionaries replaced treated a null key.
    private static bool TryFindRow<TRow>(IReadOnlyDictionary<string, TRow> map, string? key, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out TRow? row) where TRow : class {
        if (key is null) {
            row = null;

            return false;
        }

        return map.TryGetValue(
            key: key,
            value: out row
        );
    }
    // The creation's Locomotion token, resolved as a kit name (the creator's rule; null when the creation/token is absent).
    // Looks up the SAME name-keyed map ValidatePlacements builds once per whole-document validate (see its own
    // remarks) instead of rescanning definition.Creations per call.
    private static string? ResolveLocomotionKit(IReadOnlyDictionary<string, WorldPrototype> creationsById, string? prototypeId) => (TryFindRow(
        key: prototypeId,
        map: creationsById,
        row: out var creation
    )
        ? creation.Document.Behavior?.Locomotion
        : null);
    private static bool ShapesContain(IReadOnlyList<Puck.World.Authoring.ShapeDocument> shapes, int id) {
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
    // Puck.World.Authoring.CreationCameraDocument enforces.
    private static void ValidateAnchor(WorldAnchor anchor, IReadOnlyList<WorldPlacement> placements, HashSet<string> placementIds, IReadOnlyList<WorldPrototype> creations, int populationCapacity, string path, List<string> errors) {
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
                            id: row.PrototypeId
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
            case WorldAnchor.Seat seat:
                if ((seat.Number is { } number) && ((number < 1) || (number > WorldBodiesLimits.LocalSeatCount))) {
                    errors.Add(item: $"{path}.number {number} is outside 1..{WorldBodiesLimits.LocalSeatCount}.");
                }

                if ((seat.PartId is { } seatPart) && string.IsNullOrWhiteSpace(value: seatPart)) {
                    errors.Add(item: $"{path}.partId must not be blank when present.");
                }

                break;
            case WorldAnchor.RecentSpeaker speaker:
                if ((speaker.PartId is { } speakerPart) && string.IsNullOrWhiteSpace(value: speakerPart)) {
                    errors.Add(item: $"{path}.partId must not be blank when present.");
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
    // WorldPlacementPolicyDefaults' remarks for which fields are boot-consumed vs. live-consumed — so a bad authored value
    // can never reach a boot's frozen render-envelope probe (a live-consumed field's bad value is caught the same
    // way, on every mutation, since the validator re-runs on every composed candidate).
    private static void ValidateAuthoring(WorldPlacementPolicyDefaults authoring, List<string> errors) {
        RequireIntRange(
            value: authoring.AuthoringHeadroomScreens,
            min: 0,
            max: SdfProgramBuilder.MaxScreenSurfaces,
            name: "placements.policy.authoringHeadroomScreens",
            errors: errors
        );
        RequireIntRange(
            value: authoring.AuthoringHeadroomPlacements,
            min: 0,
            max: 256,
            name: "placements.policy.authoringHeadroomPlacements",
            errors: errors
        );
        RequirePositive(
            value: authoring.MinPlacementScale,
            name: "placements.policy.minPlacementScale",
            errors: errors
        );
        RequirePositive(
            value: authoring.MaxPlacementScale,
            name: "placements.policy.maxPlacementScale",
            errors: errors
        );

        if (
            float.IsFinite(f: authoring.MinPlacementScale) &&
            float.IsFinite(f: authoring.MaxPlacementScale) &&
            (authoring.MinPlacementScale > authoring.MaxPlacementScale)
        ) {
            errors.Add(item: $"placements.policy.minPlacementScale {authoring.MinPlacementScale} exceeds authoring.maxPlacementScale {authoring.MaxPlacementScale}.");
        }

        RequirePositive(
            value: authoring.CandidateRadius,
            name: "placements.policy.candidateRadius",
            errors: errors
        );
        RequireIntRange(
            value: authoring.CandidateCap,
            min: 1,
            max: 256,
            name: "placements.policy.candidateCap",
            errors: errors
        );

        RequireIntRange(
            value: authoring.PreviewDeadlineFrames,
            min: 1,
            max: 600,
            name: "placements.policy.previewDeadlineFrames",
            errors: errors
        );
        // The derived-face reserve: the slots boot-registered at [DerivedFaceBase, DerivedFaceBase + count). The
        // ceiling is the remaining span in the engine's screen table.
        RequireIntRange(
            value: authoring.DerivedFaceScreens,
            min: 0,
            max: WorldPlacementPolicy.MaxDerivedFaceScreens,
            name: "placements.policy.derivedFaceScreens",
            errors: errors
        );
    }
    private static void ValidateCollider(WorldCollider? collider, IReadOnlyList<WorldPrototype> creations, string path, List<string> errors) {
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
                    string.IsNullOrWhiteSpace(value: fromCreation.PrototypeId) ||
                    (WorldDefinitionRows.FindCreation(
                    creations: creations,
                    id: fromCreation.PrototypeId
                ) is not { } creation)
                ) {
                    errors.Add(item: $"{path}.prototypeId '{fromCreation.PrototypeId}' names no creation row.");
                    break;
                }

                var shapes = (creation.Document.Shapes ?? []);
                if (shapes.Count < 1) {
                    errors.Add(item: $"{path} creation '{fromCreation.PrototypeId}' emits no body-collider volumes.");
                } else if (shapes.Count > WorldCollider.MaxVolumes) {
                    errors.Add(item: $"{path} creation '{fromCreation.PrototypeId}' emits {shapes.Count} volumes, exceeding the {WorldCollider.MaxVolumes}-volume body-collider ceiling.");
                }

                for (var index = 0; (index < shapes.Count); index++) {
                    if (shapes[index].Type == SdfSolidPrimitive.Plane) {
                        errors.Add(item: $"{path} creation '{fromCreation.PrototypeId}' shape {index} is an unbounded plane, not a finite body volume.");
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
    // The animation facets that reach outside the creation: a curve waveform names a row of this world's curves, a
    // state signal names a numeric row of its state — neither is knowable to the portable document alone.
    private static void ValidateCreationBindings(WorldDefinition definition, Puck.World.Authoring.CreationDocument document, List<string> errors, string path) {
        var drivers = (document.Drivers ?? []);

        for (var index = 0; (index < drivers.Count); index++) {
            var signal = drivers[index].Signal;

            if (!Puck.World.Authoring.CreationDriverDocument.IsStateSignal(signal: signal)) {
                continue;
            }
            if (
                !WorldColor.TryParseBinding(
                key: out _,
                row: out var rowName,
                value: signal!
            ) ||
                (WorldDefinitionRows.FindStateRow(
                rows: definition.State,
                name: rowName
            ) is not { } row)
            ) {
                errors.Add(item: $"{path}.drivers[{index}].signal '{signal}' names no declared state row.");
            } else if (row.Kind is not (CellKind.Int or CellKind.Fixed)) {
                errors.Add(item: $"{path}.drivers[{index}].signal '{signal}' names a {WorldRefusalSpelling.Kind(kind: row.Kind)} row; a signal reads an int or fixed cell.");
            }
        }

        var effectors = (document.Effectors ?? []);

        for (var index = 0; (index < effectors.Count); index++) {
            var target = effectors[index].Target;

            if (
                (target is null) ||
                !string.Equals(
                a: target.Kind,
                b: Puck.World.Authoring.CreationEffectorTargetDocument.KindState,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                continue;
            }
            if (
                !WorldColor.TryParseBinding(
                key: out _,
                row: out var targetRow,
                value: (target.Reference ?? string.Empty)
            ) ||
                (WorldDefinitionRows.FindStateRow(
                rows: definition.State,
                name: targetRow
            ) is not { } cell)
            ) {
                errors.Add(item: $"{path}.effectors[{index}].target.reference '{target.Reference}' names no declared state row.");
            } else if (cell.Kind != CellKind.Text) {
                errors.Add(item: $"{path}.effectors[{index}].target.reference '{target.Reference}' names a {WorldRefusalSpelling.Kind(kind: cell.Kind)} row; a state target reads a text cell spelling [x, y, z].");
            }
        }

        var shapes = (document.Shapes ?? []);

        for (var index = 0; (index < shapes.Count); index++) {
            var shape = shapes[index];
            var swings = (shape.Swings ?? []);
            var slides = (shape.Slides ?? []);

            for (var i = 0; (i < swings.Count); i++) {
                RequireCurveRow(definition: definition, errors: errors, path: $"{path}.shapes[{index}].swings[{i}].wave", wave: swings[i].Wave);
            }
            for (var i = 0; (i < slides.Count); i++) {
                RequireCurveRow(definition: definition, errors: errors, path: $"{path}.shapes[{index}].slides[{i}].wave", wave: slides[i].Wave);
            }
        }
    }
    private static void RequireCurveRow(WorldDefinition definition, List<string> errors, string path, string? wave) {
        if (!Puck.World.Authoring.CreationWave.TryCurveName(
            name: out var name,
            wave: wave
        )) {
            return;
        }
        if (WorldDefinitionRows.FindCurve(
            curves: definition.Curves,
            name: name
        ) is null) {
            errors.Add(item: $"{path} '{wave}' names no declared curves row.");
        }
    }
    private static HashSet<string> ValidateCreations(WorldDefinition definition, IReadOnlyList<WorldPrototype> creations, HashSet<string> fontNames, bool hasTextCatalog, List<string> errors) {
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (creations is null) {
            errors.Add(item: "creations is required.");

            return ids;
        }

        for (var index = 0; (index < creations.Count); index++) {
            var creation = creations[index];
            var path = $"prototypes[{index}]";

            if (creation is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            RequireUniqueName(
                value: creation.Id,
                seen: ids,
                path: path,
                field: "id",
                errors: errors
            );

            if (creation.Document is null) {
                errors.Add(item: $"{path}.doc is required.");

                continue;
            }

            var violations = Puck.World.Authoring.CreationCanonicalizer.Validate(document: creation.Document);

            if (violations.Count > 0) {
                foreach (var violation in violations) {
                    errors.Add(item: $"{path}.doc.{violation.Path}: {violation.Message}");
                }

                continue;
            }

            // The hash pin: recompute through the ONE pipeline and compare — the only accepted hash is the one the
            // pipeline itself computes over this document's canonical bytes.
            var canonical = Puck.World.Authoring.CreationCanonicalizer.Canonicalize(
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
            ValidateCreationBindings(
                definition: definition,
                document: creation.Document,
                errors: errors,
                path: $"{path}.doc"
            );

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

            // A creation-level field op cannot span the per-shape dynamic instances the stamp pool emits for a framed
            // creation — noise is a static-stamp facet.
            if (
                (creation.Document.Noise is not null) &&
                (creation.Document.Frames is { Count: > 0 })
            ) {
                errors.Add(item: $"{path}.doc.noise is refused on an animated (framed) creation — noise relief is a static-stamp facet.");
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
    private static void ValidateFaceSources(WorldDefinition definition, IReadOnlyList<WorldPlacementFace>? faceSources, WorldPlacement placement, IReadOnlyDictionary<string, WorldPrototype> creationsById, WorldFaceCatalog faces, ValidationScope scope, string path, List<string> errors) {
        if (faceSources is not { Count: > 0 } sources) {
            return;
        }

        var destinationNames = scope.DestinationNames;
        var fontNames = scope.FontNames;
        var hasTextCatalog = scope.HasTextCatalog;

        var creation = (TryFindRow(
            key: placement.PrototypeId,
            map: creationsById,
            row: out var faceSourceCreation
        )
            ? faceSourceCreation
            : null);
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
                errors.Add(item: $"{facePath}.face '{source.Face}' names no declared face on creation '{placement.PrototypeId}'.");
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
                    errors.Add(item: $"{facePath}.portal sits on an ANIMATED placement (creation '{placement.PrototypeId}' carries timeline frames) — a replaying stamp's surface moves on the render clock while the derived frame does not; move the door onto a static placement.");
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
    private static void ValidateInhabit(WorldPlacementInhabit inhabit, WorldPlacement placement, string path, WorldDefinition definition, IReadOnlyDictionary<string, WorldPrototype> creationsById, IReadOnlyDictionary<string, WorldKit> kitsByName, HashSet<string> kitNames, HashSet<string> lookNames, List<string> errors) {
        var resolvedKit = (inhabit.Kit ?? ResolveLocomotionKit(
            creationsById: creationsById,
            prototypeId: placement.PrototypeId
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
            // resolvedKit is already confirmed a member of kitNames above, and kit names are validated unique in
            // ValidateKits, so this is the same unique row .First() found — just an O(1) lookup instead of a scan.
            !kitsByName[resolvedKit].Producers.ContainsKey(key: producer)
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
    private static HashSet<string> ValidateKits(WorldDefinition definition, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, ISet<string> allChannelNames, ISet<string> compositionChannelNames, ISet<string> dynamicsNames, HashSet<string> targetRegisterNames, HashSet<string> judgeRowNames, IReadOnlyDictionary<string, WorldStateRow> stateRows, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, List<string> errors) {
        var kitNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var programRows = BodyMotionProgramRows(programs: definition.BodyMotionPrograms);

        var kits = definition.Kits;
        var hasMedium = HasMediumField(definition: definition);

        // A kit is required exactly when the census implies a body to move (a derived refusal, not a flat floor):
        // zero declared capacity needs no kit at all.
        if (
            (kits.Count == 0) &&
            (definition.Population.Capacity > 0)
        ) {
            errors.Add(item: $"kits requires at least one row when bodies.capacity ({definition.Population.Capacity}) is nonzero.");

            return kitNames;
        }

        for (var index = 0; (index < kits.Count); index++) {
            var kit = kits[index];
            var path = $"kits[{index}]";

            if (kit is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            RequireUniqueName(
                value: kit.Name,
                seen: kitNames,
                path: path,
                field: "",
                errors: errors
            );

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
                dynamicsNames: dynamicsNames,
                hasMedium: hasMedium,
                simulationRateHz: definition.SimulationRateHz,
                stateSlots: stateSlots,
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
            section: "looks.assignment"
        );
    }
    // The LOOK rows (PRESENTATION-ONLY): name presence/uniqueness (mirroring the kit-name rule), a source over the
    // closed catalog|creation set with a loud unknown default, a resolvable creation reference, a positive scale under
    // the GPU-safety MaxLookScale ceiling, and non-negative motion values — rejecting a zero-hold replay (an infinite
    // loop) and a timeline replay on a catalog source (no timeline to replay) LOUDLY, never silently. Returns the
    // resolved look-name set (a future Inhabit facet resolves its Look against it).
    private static HashSet<string> ValidateLooks(IReadOnlyList<WorldLook> looks, HashSet<string> prototypeIds, IReadOnlyList<WorldPrototype> creations, ISet<string> dynamicsNames, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < looks.Count); index++) {
            var look = looks[index];
            var path = $"looks[{index}]";

            if (look is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            RequireUniqueName(
                value: look.Name,
                seen: names,
                path: path,
                field: "",
                errors: errors
            );

            var isCatalog = false;
            WorldPrototype? resolvedCreation = null;

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
                        string.IsNullOrWhiteSpace(value: creation.PrototypeId) ||
                        !prototypeIds.Contains(item: creation.PrototypeId)
                    ) {
                        errors.Add(item: $"{path}.source.prototypeId '{creation.PrototypeId}' names no creation row.");
                    } else {
                        resolvedCreation = WorldDefinitionRows.FindCreation(
                            creations: creations,
                            id: creation.PrototypeId
                        );

                        if (resolvedCreation is { Document.Noise: not null }) {
                            errors.Add(item: $"{path}.source.prototypeId '{creation.PrototypeId}' carries noise relief — a static-stamp facet the body stamp pool cannot render.");
                        }
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

            if (look.Motion.Cues is { } cues) {
                var frames = ((!isCatalog && (look.Source is WorldLookSource.Creation cueCreation))
                    ? WorldDefinitionRows.FindCreation(creations: creations, id: cueCreation.PrototypeId)?.Document.Frames
                    : null
                );

                if (isCatalog) {
                    errors.Add(item: $"{path}.motion.cues cannot be set on a catalog source — there is no timeline to cue.");
                }

                for (var cueIndex = 0; (cueIndex < cues.Count); cueIndex++) {
                    var cue = cues[cueIndex];
                    var cuePath = $"{path}.motion.cues[{cueIndex}]";

                    if (cue is null) {
                        errors.Add(item: $"{cuePath} is required.");

                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(value: cue.Frame)) {
                        errors.Add(item: $"{cuePath}.frame is required.");
                    } else if (!isCatalog && (frames is not null) && !frames.Any(predicate: frame => string.Equals(a: frame?.Name, b: cue.Frame, comparisonType: StringComparison.Ordinal))) {
                        errors.Add(item: $"{cuePath}.frame '{cue.Frame}' names no frame of the look's creation timeline.");
                    }

                    if (!float.IsFinite(f: cue.HoldSeconds) || (cue.HoldSeconds <= 0f)) {
                        errors.Add(item: $"{cuePath}.holdSeconds must be positive and finite.");
                    }

                    if ((cue.MinSeconds is null) != (cue.MaxSeconds is null)) {
                        errors.Add(item: $"{cuePath} needs both minSeconds and maxSeconds, or neither (a cue that fires only on demand).");
                    } else if ((cue.MinSeconds is { } min) && (cue.MaxSeconds is { } max) && (!float.IsFinite(f: min) || !float.IsFinite(f: max) || (min < 0f) || (max < min))) {
                        errors.Add(item: $"{cuePath} needs 0 <= minSeconds <= maxSeconds, finite.");
                    }
                }
            }

            if (look.Motion.Dynamics is { } lookDynamics) {
                if (lookDynamics.Length == 0) {
                    errors.Add(item: $"{path}.motion.dynamics is empty — name a dynamics row or omit it.");
                } else {
                    RequireDeclared(
                        declaredSet: dynamicsNames,
                        errors: errors,
                        field: "motion.dynamics",
                        path: path,
                        rowNoun: "dynamics",
                        value: lookDynamics
                    );
                }
            }

            if (look.Motion.PartDynamics is { } partDynamics) {
                if (isCatalog) {
                    errors.Add(item: $"{path}.motion.partDynamics cannot be set on a catalog source — a catalog rig exports no parts.");
                }

                foreach (var (partId, partRow) in partDynamics) {
                    if (string.IsNullOrWhiteSpace(value: partId)) {
                        errors.Add(item: $"{path}.motion.partDynamics has an empty part id.");

                        continue;
                    }

                    if (
                        !isCatalog &&
                        (resolvedCreation is { } partCreation) &&
                        !(partCreation.Document.Parts ?? []).Any(predicate: part => string.Equals(a: part.Id, b: partId, comparisonType: StringComparison.Ordinal))
                    ) {
                        errors.Add(item: $"{path}.motion.partDynamics['{partId}'] names no part of creation '{partCreation.Id}'.");
                    }

                    RequireDeclared(
                        declaredSet: dynamicsNames,
                        errors: errors,
                        field: "",
                        path: $"{path}.motion.partDynamics['{partId}']",
                        rowNoun: "dynamics",
                        value: partRow
                    );
                }
            }
        }

        return names;
    }
    // The placement INSTANCE rows: id presence/uniqueness, the creation reference, finite transform, the policy
    // scale envelope, the lattice distribution's positive counts and finite steps, the mirror plane, and the animated-row
    // constraints (static-only facets; the reserved replay-pool ceiling, word-exact). Returns the resolved id set for
    // the anchor-union gate (a WorldAnchor.Placement resolves against it).
    private static HashSet<string> ValidatePlacements(IReadOnlyList<WorldPlacement> placements, WorldDefinition definition, WorldPlacementPolicyDefaults authoring, bool requiresField, ValidationScope scope, List<string> errors) {
        var prototypeIds = scope.PrototypeIds;
        var lookNames = scope.LookNames;
        var kitNames = scope.KitNames;
        var patchIds = scope.PatchIds;
        var destinationNames = scope.DestinationNames;
        var fontNames = scope.FontNames;
        var hasTextCatalog = scope.HasTextCatalog;
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);
        var creations = definition.Creations;

        if (placements is null) {
            errors.Add(item: "placements is required.");

            return ids;
        }

        // The creation/kit row lookups the per-placement loop below resolves against, built ONCE per whole-document
        // validate rather than rescanned per placement. TryAdd in authored order mirrors WorldDefinitionRows.Find's
        // linear scan exactly: both return the FIRST row matching by id/name, and creation ids/kit names are already
        // validated unique elsewhere (ValidateCreations/ValidateKits), so a duplicate resolves identically either way.
        var creationsById = new Dictionary<string, WorldPrototype>(comparer: StringComparer.Ordinal);

        for (var creationIndex = 0; (creationIndex < creations.Count); creationIndex++) {
            if (creations[creationIndex] is { Id: not null } creationRow) {
                _ = creationsById.TryAdd(
                    key: creationRow.Id,
                    value: creationRow
                );
            }
        }

        var kitsByName = new Dictionary<string, WorldKit>(comparer: StringComparer.Ordinal);

        for (var kitIndex = 0; (kitIndex < definition.Kits.Count); kitIndex++) {
            if (definition.Kits[kitIndex] is { Name: not null } kitRow) {
                _ = kitsByName.TryAdd(
                    key: kitRow.Name,
                    value: kitRow
                );
            }
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

            RequireUniqueName(
                value: placement.Id,
                seen: ids,
                path: path,
                field: "id",
                errors: errors
            );

            RequireDeclared(
                value: placement.PrototypeId,
                declaredSet: prototypeIds,
                path: path,
                field: "prototypeId",
                rowNoun: "creation",
                errors: errors
            );

            if (!IsFinite(value: placement.Position)) {
                errors.Add(item: $"{path}.position must contain finite coordinates.");
            }

            RequireFinite(
                value: placement.YawDegrees,
                name: $"{path}.yawDegrees",
                errors: errors
            );

            if (!float.IsFinite(f: placement.Scale) || (placement.Scale <= 0f)) {
                // Refused before the envelope: a zero (or negative) scale is invisible content with degenerate
                // colliders, and a zero-width envelope (a rowless derived policy, a degenerate authored one) would
                // otherwise ACCEPT exactly 0 — a placement that boots green and renders nothing. A finite positive
                // scale never contributes an envelope refusal under an unauthored policy: the derived envelope
                // spans exactly the rows' own scales (WorldPlacementPolicyDefaults.DeriveFrom), so only a DECLARED
                // policy can put an authored row outside it.
                errors.Add(item: $"{path}.scale {placement.Scale} must be a finite positive value.");
            } else if (
                (placement.Scale < authoring.MinPlacementScale) ||
                (placement.Scale > authoring.MaxPlacementScale)
            ) {
                errors.Add(item: ((authoring.MaxPlacementScale <= 0f)
                    ? $"{path}.scale {placement.Scale}: this world's declared placements.policy has no scale envelope (0..0) — author positive minPlacementScale/maxPlacementScale, or delete the policy block to derive the envelope from the rows' own scales."
                    : $"{path}.scale {placement.Scale} is outside {authoring.MinPlacementScale}..{authoring.MaxPlacementScale}."));
            }

            if (placement.Distribution is { } distribution) {
                ValidateDistribution(
                    allowDisc: false,
                    allowLattice: true,
                    allowNoise: true,
                    allowPoints: false,
                    allowScatter: true,
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

                if (TryFindRow(
                    key: placement.PrototypeId,
                    map: creationsById,
                    row: out var solidCreation
                )) {
                    // A shape carrying domain ops compiles one collider PER EXPANDED COPY, so the ceiling counts the
                    // expansion, not the authored shape count. A fold with no rigid-copy expansion has no contact
                    // geometry at all under EITHER provider — the analytic one would collide against one copy of
                    // geometry the renderer draws several times, and the field one throws out of
                    // CreationStampEmitter.EmitFixed at boot — so the expansion refusal is ungated while the
                    // analytic collider ceiling below is not.
                    //
                    // A row carrying a response facet (WorldPlacementResponse) can show any of several creations at
                    // runtime — the ceiling counts the WORST CASE across every variant (the row's own base plus each
                    // response entry's target), never just the authored one, so a swap can never push live collision
                    // past what validation admitted.
                    var solidVariantIds = new List<string> { placement.PrototypeId };

                    if (placement.Respond is { Count: > 0 } solidResponses) {
                        foreach (var solidResponse in solidResponses) {
                            if (solidResponse?.PrototypeId is { } responsePrototypeId) {
                                solidVariantIds.Add(item: responsePrototypeId);
                            }
                        }
                    }

                    var shapeColliders = 0L;

                    foreach (var variantId in solidVariantIds) {
                        if (!TryFindRow(
                            key: variantId,
                            map: creationsById,
                            row: out var variantCreation
                        )) {
                            continue;
                        }

                        var variantColliders = 0L;

                        foreach (var solidShape in (variantCreation.Document.Shapes ?? [])) {
                            if (!ShapeDomainOps.TryExpand(
                                domain: solidShape.Domain,
                                frames: out var solidFrames,
                                refusal: out var solidRefusal
                            )) {
                                errors.Add(item: $"{path}.solid names creation '{variantId}', whose shape {solidShape.Id} carries {solidRefusal} — a solid row needs contact geometry for every copy its fold draws.");

                                continue;
                            }

                            variantColliders += solidFrames.Length;
                        }

                        shapeColliders = Math.Max(val1: shapeColliders, val2: variantColliders);
                    }

                    // The field provider compiles every solid row into ONE program instead of one collider per copy,
                    // so the analytic ceiling does not describe what it costs.
                    if (!requiresField) {
                        var copies = WorldPlacementStamp.MaterializedCopyCeiling(
                            ceiling: (WorldPlacementPolicy.MaxSolidPlacementColliders + 1L),
                            placement: placement
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
            _ = TryFindRow(
                key: placement.PrototypeId,
                map: creationsById,
                row: out var animatedCreation
            );

            var isAnimated = (animatedCreation is { Document.Frames.Count: > 0 });

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

                if (animatedCreation is { Document.Noise: not null }) {
                    errors.Add(item: $"{path} ATTACHES — its creation's noise relief is a static-stamp facet the stamp pool cannot render.");
                }
            }

            // The INHABIT facet: a placement's binding to live population bodies (Arc 7). Resolve its kit, gate its
            // source/look/count, and reject the lattice facets (one body cannot represent a placement distribution).
            if (placement.Inhabit is { } inhabit) {
                ValidateInhabit(
                    creationsById: creationsById,
                    definition: definition,
                    errors: errors,
                    inhabit: inhabit,
                    kitNames: kitNames,
                    kitsByName: kitsByName,
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

                if (animatedCreation is { Document.Noise: not null }) {
                    errors.Add(item: $"{path} INHABITS — its creation's noise relief is a static-stamp facet the stamp pool cannot render.");
                }

                if (inhabit.Count > 0) {
                    dynamicInstanceCount += inhabit.Count;
                }
            }

            if (
                !isAnimated &&
                (placement.Inhabit is null)
            ) {
                // A scope-free static stamp materializes one engine instance PER SHAPE (the tight-bound emission
                // split — Puck.World.Authoring.CreationStampEmitter.PerCopyInstanceCount), so the ceiling charges
                // copies × that factor.
                var perCopyInstances = ((animatedCreation is { } staticCreation)
                    ? Puck.World.Authoring.CreationStampEmitter.PerCopyInstanceCount(document: staticCreation.Document)
                    : 1
                );
                var contribution = checked((WorldPlacementStamp.MaterializedCopyCeiling(
                    ceiling: (SdfProgramBuilder.MaxInstances + 1L),
                    placement: placement
                ) * perCopyInstances));
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
                creationsById: creationsById,
                faces: faces,
                scope: scope,
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

            // The GRIP facet overrides the world's default climb policy for this row's compiled surface(s) (see
            // WorldPlacementGrip) — meaningful only alongside a solidity facet, since nothing else compiles a
            // collider for the override to apply to.
            if (
                (placement.Grip is not null) &&
                (placement.Solid is null)
            ) {
                errors.Add(item: $"{path}.grip requires .solid — a placement with no solidity facet compiles no collider for a grip override to apply to.");
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
                    definition: definition,
                    errors: errors,
                    path: $"{path}.contribution",
                    placement: placement,
                    prototypeIds: prototypeIds
                );
            }

            // The RESPOND facet: an ordered, state-driven prototype swap (see WorldPlacementResponse).
            if (placement.Respond is not null) {
                ValidatePlacementResponse(
                    definition: definition,
                    errors: errors,
                    placement: placement,
                    placementPath: path,
                    prototypeIds: prototypeIds
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
        RequireDeclaredListing(
            declaredSet: destinationNames,
            errors: errors,
            rowNoun: "destinations row",
            subject: $"{path}.destination '{portal.Destination}'",
            value: portal.Destination
        );

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
                max: WorldBodiesLimits.CapacityCeiling,
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
            RequireDeclared(
                value: assignment.Rows[index],
                declaredSet: rowNames,
                path: $"{section}.rows[{index}]",
                field: "",
                rowNoun: rowNoun,
                errors: errors
            );
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

            RequireUniqueName(
                value: font.Name,
                seen: names,
                path: path,
                field: "name",
                errors: errors
            );

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
