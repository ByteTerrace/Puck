using System.Globalization;
using Puck.Maths;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.SignedDistance;
using Puck.Text;
using Puck.Physics.Motion;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Editable instances pay their actual cost; the template reserves only unoccupied slots.
    private static long ReservedPlacementCopies(WorldPlacement placement, IReadOnlyDictionary<string, int> childCounts, long ceiling) {
        var copies = WorldPlacementStamp.MaterializedCopyCeiling(ceiling: ceiling, placement: placement);

        if ((copies < ceiling) && (placement.Deal?.Preserve is { Prototype: true } or { Facets: true })) {
            copies = Math.Max(val1: 0, val2: (copies - childCounts.GetValueOrDefault(key: placement.Id)));
        }
        return copies;
    }
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
                    var row = WorldPlacementIndex.ById(placements: placements).GetValueOrDefault(key: placement.PlacementId);
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
    private static void ValidateAssignment(WorldDefinition definition, WorldRowAssignment assignment, HashSet<string> kitNames, List<string> errors) {
        if (assignment is null) {
            errors.Add(item: "kits.assignment is required.");

            return;
        }

        ValidateRowAssignment(
            assignment: assignment,
            definition: definition,
            errors: errors,
            rowNames: kitNames,
            rowNoun: "kit",
            section: "kits.assignment"
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
            max: SdfProgramBuilder.MaxScreenIdentities,
            name: "placements.policy.authoringHeadroomScreens",
            errors: errors
        );
        RequireIntRange(
            value: authoring.AuthoringHeadroomInstances,
            min: 0,
            max: SdfProgramBuilder.MaxInstances,
            name: "placements.policy.authoringHeadroomInstances",
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
    }
    // A rigid kit's mass properties derive from its own collider shape (WorldRigid.cs), so the collider it names must
    // have exactly one compiled primitive with closed-form inertia — a compound creation is refused rather than
    // silently approximated by its first primitive.
    private static void ValidateRigid(WorldRigid? rigid, WorldCollider? collider, IReadOnlyList<WorldPrototype> creations,
        WorldBodyContactMode bodyContact, string path, List<string> errors) {
        if (rigid is null) {
            return;
        }

        if (collider is null) {
            errors.Add(item: $"{path} requires a collider to derive mass properties from.");
        }

        if (bodyContact != WorldBodyContactMode.Solid) {
            errors.Add(item: $"{path} requires bodyContact 'solid' — a rigid body that never depenetrates is inert.");
        }

        RequirePositive(
            value: rigid.Mass,
            name: new Subject(Member: ".mass", Path: path),
            errors: errors
        );
        RequireRange(
            value: rigid.Restitution,
            min: 0f,
            max: 1f,
            name: new Subject(Member: ".restitution", Path: path),
            errors: errors
        );
        // Friction is a Coulomb coefficient (unbounded above — over 1 is physically ordinary), rolling resistance a
        // lever in metres, and both damping rates per-second decay rates (applied as 1 - rate·dt, clamped at apply
        // time). All four share the same bound here: non-negative, never bounded by 1.
        foreach (var (value, name) in new (float Value, string Name)[] {
            (rigid.Friction, "friction"),
            (rigid.RollingResistance, "rollingResistance"),
            (rigid.LinearDamping, "linearDamping"),
            (rigid.AngularDamping, "angularDamping"),
        }) {
            if (
                !float.IsFinite(f: value) ||
                (value < 0f)
            ) {
                errors.Add(item: $"{path}.{name} must be finite and non-negative.");
            }
        }

        if (
            ColliderCanDeriveRigidMass(collider: collider, creations: creations) &&
            (FixedWorldCollider.Compile(collider: collider, creations: creations) is { } fixedCollider) &&
            !FixedWorldRigid.TryCompile(
                collider: fixedCollider,
                compiled: out _,
                reason: out var reason,
                rigid: rigid
            )
        ) {
            errors.Add(item: $"{path} cannot compile deterministic mass properties: {reason}");
        }
    }
    private static bool ColliderCanDeriveRigidMass(WorldCollider? collider, IReadOnlyList<WorldPrototype> creations) => (collider switch {
        WorldCollider.Sphere sphere => (float.IsFinite(f: sphere.Radius) && (sphere.Radius > 0f)),
        WorldCollider.Capsule capsule => (float.IsFinite(f: capsule.Radius) &&
            (capsule.Radius > 0f) &&
            VectorFunctions.IsFinite(vector: capsule.Endpoint) &&
            (capsule.Endpoint.LengthSquared() > 0f)),
        WorldCollider.Box box => (VectorFunctions.IsFinite(vector: box.HalfExtents) &&
            (box.HalfExtents.X > 0f) &&
            (box.HalfExtents.Y > 0f) &&
            (box.HalfExtents.Z > 0f) &&
            float.IsFinite(f: box.Rotation.LengthSquared()) &&
            (box.Rotation.LengthSquared() > 0f)),
        WorldCollider.FromCreation fromCreation => (WorldDefinitionRows.FindCreation(creations: creations,
            id: fromCreation.PrototypeId) is { Document.Shapes.Count: > 0 }),
        _ => false,
    });
    private static void ValidateCarry(WorldCarry? carry, string path, List<string> errors) {
        if (carry is null) {
            return;
        }

        if (!VectorFunctions.IsFinite(vector: carry.Offset)) {
            errors.Add(item: $"{path}.offset must be finite.");
        }

        RequirePositive(
            value: carry.MassEquivalent,
            name: new Subject(Member: ".massEquivalent", Path: path),
            errors: errors
        );

        if (
            !float.IsFinite(f: carry.MaxCarryFraction) ||
            (carry.MaxCarryFraction < 0f)
        ) {
            errors.Add(item: $"{path}.maxCarryFraction must be finite and non-negative.");
        }

        RequirePositive(
            value: carry.MaxReach,
            name: new Subject(Member: ".maxReach", Path: path),
            errors: errors
        );

        if (!FixedWorldCarry.TryCompile(carry: carry, compiled: out _, reason: out var reason)) {
            errors.Add(item: $"{path} cannot compile deterministically: {reason}");
        }
    }
    // A kit's tether facet: the aim/rope tuning, and every named channel/state slot resolvable. Absence refuses
    // body.attach/body.detach/body.reel by name (see PlayerCommandModule.Tether.cs).
    private static void ValidateTether(WorldTether? tether, string path, ISet<string> channelNames, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, List<string> errors) {
        if (tether is not { } facet) {
            return;
        }

        RequireNonNegativeFixed(
            errors: errors,
            name: new Subject(Member: ".maxAnchorDistance", Path: path),
            value: facet.MaxAnchorDistance
        );
        RequireRange(
            errors: errors,
            max: 180f,
            min: 0f,
            name: new Subject(Member: ".aimHalfAngleDegrees", Path: path),
            value: facet.AimHalfAngleDegrees
        );
        if (float.IsFinite(f: facet.AimHalfAngleDegrees) && (facet.AimHalfAngleDegrees > 0f) && (FixedQ4816.FromDegrees(degrees: facet.AimHalfAngleDegrees) <= FixedQ4816.Zero)) {
            errors.Add(item: $"{path}.aimHalfAngleDegrees {facet.AimHalfAngleDegrees} is positive but quantizes to zero radians in Q48.16.");
        }
        RequireNonNegativeFixed(
            errors: errors,
            name: new Subject(Member: ".lengthRate", Path: path),
            value: facet.LengthRate
        );
        RequireNonNegativeFixed(
            errors: errors,
            name: new Subject(Member: ".minLength", Path: path),
            value: facet.MinLength
        );
        RequireNonNegativeFixed(
            errors: errors,
            name: new Subject(Member: ".releaseVelocityScale", Path: path),
            value: facet.ReleaseVelocityScale
        );

        // Every channel name is OPTIONAL (a null lane is simply unreachable), but an AUTHORED one must resolve —
        // the same "declared or the field is pointless" door a kit's own speed.held channel already opens.
        if (facet.AttachChannel is { } attachChannel) {
            if (string.IsNullOrWhiteSpace(value: attachChannel)) {
                errors.Add(item: $"{path}.attachChannel is empty — name a declared composition channel or omit it.");
            } else {
                _ = RequireDeclared(
                    declaredSet: channelNames,
                    errors: errors,
                    field: string.Empty,
                    path: $"{path}.attachChannel",
                    rowNoun: "channel",
                    value: attachChannel
                );
            }
        }
        if (facet.DetachChannel is { } detachChannel) {
            if (string.IsNullOrWhiteSpace(value: detachChannel)) {
                errors.Add(item: $"{path}.detachChannel is empty — name a declared composition channel or omit it.");
            } else {
                _ = RequireDeclared(
                    declaredSet: channelNames,
                    errors: errors,
                    field: string.Empty,
                    path: $"{path}.detachChannel",
                    rowNoun: "channel",
                    value: detachChannel
                );
            }
        }
        if (facet.ReelChannel is { } reelChannel) {
            if (string.IsNullOrWhiteSpace(value: reelChannel)) {
                errors.Add(item: $"{path}.reelChannel is empty — name a declared composition channel or omit it.");
            } else {
                _ = RequireDeclared(
                    declaredSet: channelNames,
                    errors: errors,
                    field: string.Empty,
                    path: $"{path}.reelChannel",
                    rowNoun: "channel",
                    value: reelChannel
                );
            }
        }
        if (facet.ModeState is { } modeState) {
            if (string.IsNullOrWhiteSpace(value: modeState)) {
                errors.Add(item: $"{path}.modeState is empty — name a declared Counter slot or omit it.");
            } else if (!stateSlots.TryGetValue(
                key: modeState,
                value: out var slot
            )) {
                errors.Add(item: $"{path}.modeState '{modeState}' names no declared body or identity state slot.");
            } else if (slot.Kind != ActionStateKind.Counter) {
                errors.Add(item: $"{path}.modeState '{modeState}' is a {slot.Kind} slot — a tether mode flag writes a Counter.");
            }
        }
    }
    // The creation ASSET rows: id presence/uniqueness, the document's own strict schema + structural invariants
    // through CreationCanonicalizer (the ONE pipeline — never a re-implementation), the hash pin (the carried
    // hash must equal the canonical hash — a tampered/corrupt row rejects loudly). Returns the resolved id set for the
    // placement gate.
    // The animation facets that reach outside the creation: a curve waveform names a row of this world's curves, a
    // state signal names a numeric row of its state — neither is knowable to the portable document alone.
    private static void ValidateCreationBindings(WorldDefinition definition, Puck.World.Authoring.CreationDocument document, List<string> errors, string path) {
        if (document.SizeBy is { } sizeBy) {
            RequireNumericStateReference(definition: definition, errors: errors, path: $"{path}.sizeBy", reference: sizeBy, subject: "a size");
        }
        var drivers = (document.Drivers ?? []);

        for (var index = 0; (index < drivers.Count); index++) {
            RequireGateTokens(
                definition: definition,
                errors: errors,
                gate: drivers[index].When,
                path: $"{path}.drivers[{index}].when"
            );

            var signal = drivers[index].Signal;

            if (
                (drivers[index].Dynamics is { Length: > 0 } dynamics) &&
                (Puck.State.StateRows.FindDynamics(
                dynamics: definition.Dynamics,
                name: dynamics
            ) is null)
            ) {
                errors.Add(item: $"{path}.drivers[{index}].dynamics '{dynamics}' names no declared dynamics row.");
            }

            if (drivers[index].Lead is { Length: > 0 } lead) {
                var channel = definition.Channels.FirstOrDefault(predicate: row => string.Equals(a: row?.Name, b: lead, comparisonType: StringComparison.Ordinal));

                if (channel is null) {
                    errors.Add(item: $"{path}.drivers[{index}].lead '{lead}' names no declared channel.");
                } else if (channel.Shape is ChannelShape.Relative or ChannelShape.Text) {
                    errors.Add(item: $"{path}.drivers[{index}].lead '{lead}' names a {channel.Shape.ToString().ToLowerInvariant()} channel, which holds no level a guess could show; lead names a button or an axis.");
                }
            }
            RequireFunctionCurveRow(definition: definition, errors: errors, path: $"{path}.drivers[{index}].cadenceBy.curve", curve: drivers[index].CadenceBy?.Curve);
            RequireFunctionCurveRow(definition: definition, errors: errors, path: $"{path}.drivers[{index}].weightBy.curve", curve: drivers[index].WeightBy?.Curve);
            if (Puck.World.Authoring.CreationDriverDocument.IsStateSignal(signal: signal)) {
                RequireNumericStateReference(
                    definition: definition,
                    errors: errors,
                    path: $"{path}.drivers[{index}].signal",
                    reference: signal!,
                    subject: "a signal"
                );
            }
        }

        var effectors = (document.Effectors ?? []);

        for (var index = 0; (index < effectors.Count); index++) {
            RequireGateTokens(
                definition: definition,
                errors: errors,
                gate: effectors[index].When,
                path: $"{path}.effectors[{index}].when"
            );

            var target = effectors[index].Target;

            RequireFunctionCurveRow(definition: definition, errors: errors, path: $"{path}.effectors[{index}].weightBy.curve", curve: effectors[index].WeightBy?.Curve);
            if ((target?.Kind is Puck.World.Authoring.CreationEffectorTargetDocument.KindBody) && (target.Reference is { } selector)) {
                RequireNumericStateReference(definition: definition, errors: errors, path: $"{path}.effectors[{index}].target.reference", reference: selector, subject: "a body target");
            }
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
                (StateBinding.Parse(token: target.Reference) is not (var targetRow, _, _)) ||
                (WorldDefinitionRows.FindStateRow(
                rows: definition.State,
                name: targetRow
            ) is not { } cell)
            ) {
                errors.Add(item: $"{path}.effectors[{index}].target.reference '{target.Reference}' names no declared state row.");
            } else if (cell.Kind != CellKind.Text) {
                errors.Add(item: $"{path}.effectors[{index}].target.reference '{target.Reference}' names a {StateSpelling.Kind(kind: cell.Kind)} row; a state target reads a text cell spelling [x, y, z].");
            }
        }

        foreach (var term in CreationRig.Of(document: document).AllTerms) {
            if (term.Track is not null) { continue; }
            RequireCurveRow(definition: definition, errors: errors, path: $"{path}.shapes[{term.Shape}].{((term.Kind == CreationRigTermKind.Turn) ? "swings" : "slides")}[{term.Source}].wave", wave: term.Wave);
        }
    }
    // The gate vocabulary is split across two assemblies on purpose — the fact names are the simulation's — so the
    // creation's own canonicalizer can only judge a gate's shape. This validator sees both, and a token naming no
    // fact is refused here by name rather than left to gate its driver off silently at the consumer.
    // A state reference a presentation consumer reads as a number: the row must exist and be numeric; the key is not
    // checked against the row's cells, since '$body' resolves per reading body and an absent cell reads zero.
    private static void RequireNumericStateReference(WorldDefinition definition, string reference, List<string> errors, string path, string subject) {
        if (
            (StateBinding.Parse(token: reference) is not (var rowName, _, _)) ||
            (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: rowName
        ) is not { } row)
        ) {
            errors.Add(item: $"{path} '{reference}' names no declared state row.");
        } else if (row.Kind is not (CellKind.Int or CellKind.Fixed)) {
            errors.Add(item: $"{path} '{reference}' names a {StateSpelling.Kind(kind: row.Kind)} row; {subject} reads an int or fixed cell.");
        } else if ((StateBinding.Parse(token: reference) is { Key: null }) && row.IsKeyed) {
            errors.Add(item: $"{path} '{reference}' needs a key for keyed row '{rowName}'.");
        } else if ((StateBinding.Parse(token: reference) is { Key: { } key }) && row.IsSlot && (key != StateRow.SlotKey.Value)) {
            errors.Add(item: $"{path} '{reference}' names a key on slot row '{rowName}'.");
        }
    }
    private static void RequireGateTokens(WorldDefinition definition, IReadOnlyList<string>? gate, List<string> errors, string path) {
        if (gate is null) {
            return;
        }

        for (var index = 0; (index < gate.Count); index++) {
            var token = gate[index];

            if (Puck.World.Authoring.CreationDriverDocument.IsStateSignal(signal: token)) {
                RequireNumericStateReference(
                    definition: definition,
                    errors: errors,
                    path: $"{path}[{index}]",
                    reference: token,
                    subject: "a gate token"
                );

                continue;
            }

            if (
                string.Equals(
                a: token,
                b: CreationDriverDocument.WhenAlways,
                comparisonType: StringComparison.Ordinal
            ) ||
                string.Equals(
                a: token,
                b: CreationDriverDocument.TokenMoving,
                comparisonType: StringComparison.Ordinal
            ) ||
                string.Equals(
                a: token,
                b: CreationDriverDocument.TokenStill,
                comparisonType: StringComparison.Ordinal
            ) ||
                BodyFactVocabulary.TryResolve(
                gate: out _,
                name: token
            )
            ) {
                continue;
            }

            errors.Add(item: $"{path}[{index}] '{token}' names no body fact; a gate token is a BodyFacts name, \"{CreationDriverDocument.TokenMoving}\", \"{CreationDriverDocument.TokenStill}\", \"{CreationDriverDocument.WhenAlways}\", or a state.<row>[.<key>] reference.");
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
    // A driver curve is read as a function, its height at a given X, which needs one point at every X: an open row
    // whose knots advance along X with their tangents pointing that way. Bisecting arc length for an X then has one
    // answer.
    private static void RequireFunctionCurveRow(WorldDefinition definition, List<string> errors, string path, string? curve) {
        if (curve is not { Length: > 0 } name) {
            return;
        }
        if (WorldDefinitionRows.FindCurve(
            curves: definition.Curves,
            name: name
        ) is not { } row) {
            errors.Add(item: $"{path} '{name}' names no declared curves row.");

            return;
        }

        var knots = row.Knots;
        var drawnLeftToRight = !row.Closed;

        for (var index = 0; (index < knots.Count); index++) {
            drawnLeftToRight &= ((MathF.Cos(x: knots[index].TangentYaw) > 0f) && ((index == 0) || (knots[index].Position.Value.X > knots[(index - 1)].Position.Value.X)));
        }
        if (!drawnLeftToRight) {
            errors.Add(item: $"{path} '{name}' is not drawn left to right; a driver curve is open, each knot further along X than the last, each tangent pointing toward +X.");
        }
    }
    // The prototypes partition: every creation row (a null list) or the listed rows. A row is judged alone: the one
    // fact it shares with the rows beside it is that no other row carries its id.
    // A row may be judged as another value than the document holds at its index (a bound row resolved at the write's
    // clock), through replacement.
    private static void ValidateCreationRows(WorldDefinition definition, IReadOnlyList<int>? rows, List<string> errors, WorldPrototype? replacement = null) {
        var creations = definition.Creations;
        var text = definition.Text;
        var fontNames = DeclaredNames(rows: text?.Fonts, name: static font => font.Name);

        if (rows is null) {
            var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < creations.Count); index++) {
                ValidateCreationRow(creation: creations[index], definition: definition, errors: errors, fontNames: fontNames, hasTextCatalog: (text is not null), path: $"prototypes[{index}]", seen: ids);
            }

            // Every distinct text paint takes one of the renderer's surface text slots (WorldPresentationManifest).
            var textPaints = new HashSet<Puck.World.Authoring.PalettePaintDocument>();

            foreach (var creation in creations) {
                foreach (var entry in (creation?.Document?.Palette ?? [])) {
                    foreach (var paint in (entry?.Paints ?? [])) {
                        if (paint?.Text is not null) { _ = textPaints.Add(item: paint); }
                    }
                }
            }
            if (textPaints.Count > Puck.SignedDistance.SdfSurfaceTextLayout.MaxTexts) {
                errors.Add(item: $"prototypes author {textPaints.Count} distinct text paints, exceeding the {Puck.SignedDistance.SdfSurfaceTextLayout.MaxTexts} surface texts one world draws.");
            }
            return;
        }

        var counts = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var creation in creations) {
            if (!string.IsNullOrWhiteSpace(value: creation?.Id.Value)) { counts[creation.Id.Value] = (counts.GetValueOrDefault(key: creation.Id.Value) + 1); }
        }
        foreach (var index in rows) {
            var creation = (replacement ?? creations[index]);
            // The ids of the other rows, as far as this row's own uniqueness asks: its id when another row carries it.
            var others = new HashSet<string>(comparer: StringComparer.Ordinal);

            if ((creations[index]?.Id.Value is { } id) && (counts.GetValueOrDefault(key: id) > 1)) { _ = others.Add(item: id); }
            ValidateCreationRow(creation: creation, definition: definition, errors: errors, fontNames: fontNames, hasTextCatalog: (text is not null), path: $"prototypes[{index}]", seen: others);
        }
    }
    // One creation row: its id, the document's own schema and invariants through CreationCanonicalizer (the one
    // pipeline), the hash pin, the facets that reach outside the creation, and its inline mesh.
    private static void ValidateCreationRow(WorldDefinition definition, WorldPrototype? creation, string path, HashSet<string> seen, HashSet<string> fontNames, bool hasTextCatalog, List<string> errors) {

        if (creation is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        RequireUniqueName(
            value: creation.Id,
            seen: seen,
            path: path,
            field: "id",
            errors: errors
        );

        if (creation.Document is null) {
            errors.Add(item: $"{path}.doc is required.");

            return;
        }
        if (creation.Mesh is { } mesh) {
            ValidatePrototypeMesh(
                errors: errors,
                mesh: mesh,
                paletteCount: (creation.Document.Palette?.Count ?? 0),
                path: $"{path}.mesh"
            );
        }

        {
            var violations = Puck.World.Authoring.CreationCanonicalizer.Validate(document: creation.Document);

            if (violations.Count > 0) {
                foreach (var violation in violations) {
                    errors.Add(item: $"{path}.doc.{violation.Path}: {violation.Message}");
                }

                return;
            }

            // The hash pin: the only accepted hash is the one the pipeline itself computes over this document's
            // canonical bytes, which the row computes once and caches.
            var canonicalHash = creation.CanonicalHash;

            if (!string.Equals(
                a: creation.Hash,
                b: canonicalHash,
                comparisonType: StringComparison.Ordinal
            )) {
                errors.Add(item: $"{path}.hash '{creation.Hash}' does not match the canonical sha256 '{canonicalHash}'.");
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

            // Relief spans one composed field. Root motion preserves it; independently posed shapes do not.
            if (
                (creation.Document.Noise is not null) &&
                CreationRig.Of(document: creation.Document).HasPartMotion
            ) {
                errors.Add(item: $"{path}.doc.noise requires motion of the creation as a whole — frames, shape drivers or effectors that move its parts cannot share one relief field.");
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

            // A text paint draws through this world's fonts and reads this world's state.
            for (var slot = 0; (slot < palette.Count); slot++) {
                var paints = (palette[slot].Paints ?? []);

                for (var paint = 0; (paint < paints.Count); paint++) {
                    if (paints[paint]?.Text is not { } paintText) {
                        continue;
                    }

                    var textPath = $"{path}.doc.palette[{slot}].paints[{paint}].text";

                    if (!hasTextCatalog) {
                        errors.Add(item: $"{textPath} requires the world to declare a text font catalog.");
                    } else if ((paintText.Font is { } paintFont) && !fontNames.Contains(item: paintFont)) {
                        errors.Add(item: $"{textPath} font '{paintFont}' names no text.fonts row.");
                    }
                    if (paintText.Template is { } template) {
                        _ = TryValidateTextTemplate(definition: definition, errors: errors, line: template, literalRunes: out _, path: $"{textPath}.template");
                    }
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
    }
    // The per-instance face overrides: each names a declared creation face, no duplicate face names.
    private static void ValidateFaceSources(WorldDefinition definition, IReadOnlyList<WorldPlacementFace>? faceSources, WorldPlacement placement, IReadOnlyDictionary<string, WorldPrototype> creationsById, ValidationScope scope, string path, List<string> errors) {
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

            if (!Enum.IsDefined(value: source.Filter)) {
                errors.Add(item: $"{facePath}.filter {((uint)source.Filter)} is not a sampler filter; a face samples its source Nearest or Linear.");
            }

            ValidateScreenMaterialName(
                definition: definition,
                errors: errors,
                material: source.Material,
                path: $"{facePath}.material",
                source: source.Source
            );

            if (source.Source is null) {
                errors.Add(item: $"{facePath}.source is required.");
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
                if (portal.Open is WorldPlacementResponseCondition.StateCondition open) {
                    ValidateStateCondition(condition: open, definition: definition, entryPath: $"{facePath}.portal.open", errors: errors);
                } else if (portal.Open is not null) {
                    errors.Add(item: $"{facePath}.portal.open must be a state condition — a door opens on a cell its rules write, read wherever the door stands.");
                }
                if (portal.Gate is { } gate) {
                    if (!gate.Comparison.IsComparison()) {
                        errors.Add(item: $"{facePath}.portal.gate.comparison '{gate.Comparison}' is unknown.");
                    }
                    if (!float.IsFinite(f: gate.Value)) {
                        errors.Add(item: $"{facePath}.portal.gate.value must carry a finite value.");
                    }
                    if (WorldDefinitionRows.FindStateRow(rows: definition.State, name: gate.State) is not { } gateRow) {
                        errors.Add(item: $"{facePath}.portal.gate.state '{gate.State}' names no state row.");
                    } else if (gateRow.IsSlot || (gateRow.Kind is not (CellKind.Int or CellKind.Fixed or CellKind.Bool))) {
                        errors.Add(item: $"{facePath}.portal.gate.state '{gate.State}' must be a keyed int, fixed or bool row — a gate reads one cell per body, at the traveller's index.");
                    }
                }

                // A door is geometry the trigger scan derives ONCE per revision from the row's authored transform
                // (WorldFaceCatalog). A row whose resolved pose moves every tick has no such derivation, so a portal
                // facet on one is refused rather than silently triggering at a stale place.
                if (placement.Attach is not null) {
                    errors.Add(item: $"{facePath}.portal sits on a placement that ATTACHES to a live body — a portal's frame is derived once per document revision from the row's own authored transform, which an attached row does not have; move the door onto a static placement.");
                } else if (placement.Inhabit is not null) {
                    errors.Add(item: $"{facePath}.portal sits on an INHABITED placement — its stamp rides a live body's pose rather than the row's authored transform, so the door's frame would be stale every tick; move the door onto a static placement.");
                } else if (creation is { Document.Frames.Count: > 0 } or { Document.Drivers.Count: > 0 }) {
                    errors.Add(item: $"{facePath}.portal sits on an ANIMATED placement (creation '{placement.PrototypeId}' carries timeline frames or drivers) — a replaying stamp's surface moves on the render clock while the derived frame does not; move the door onto a static placement.");
                }

                // The derived face itself: its shape kind must open an aperture (WorldFaceApertures). A face may be
                // drawn on any primitive; a door is narrower. Its orientation is checked against its counterpart's at
                // transfer time (WorldFrameIsometry.PreservesWorldUp), where the counterpart's document resolves.
                if (TryFindRow(key: placement.ShownPrototypeId, map: creationsById, row: out var shown) &&
                    (shown.Document.Behavior?.Faces?.FirstOrDefault(face => face.Name == source.Face) is { } portalFace)) {
                    var shape = WorldFaceCatalog.FindShape(document: shown.EngineDocument, id: portalFace.ShapeId);
                    if (shape is null) {
                        errors.Add(item: $"{facePath}.portal names face '{source.Face}', which declares no concrete shape (shapeId {(portalFace.ShapeId?.ToString(provider: CultureInfo.InvariantCulture) ?? "null")}) — a door needs a surface to open, so it has no aperture mapping.");
                    } else if (WorldFaceApertures.For(primitive: shape.Type) is null) {
                        var shapeType = shape.Type;
                        errors.Add(item: $"{facePath}.portal names face '{source.Face}', whose shape is a {shapeType} — only Box maps onto a walkable aperture today; a curved or unbounded face has no aperture mapping.");
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
        var count = inhabit.ResolvedCount;

        if (count.Row is { } countRow) {
            var countRowDeclaration = WorldDefinitionRows.FindStateRow(definition.State, countRow);

            if ((countRowDeclaration is not { Kind: CellKind.Int }) || (countRowDeclaration.IsKeyed != (count.Key is not null))) {
                errors.Add(item: $"{path}.count must name a declared Int row with a matching key shape.");
            }
        } else if (
            (count.Literal is not { } literal) ||
            (literal < 1) ||
            (literal > peerCapacity)
        ) {
            errors.Add(item: $"{path}.count {count.Literal} is outside 1..{peerCapacity} for the authored population capacity.");
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
    private static HashSet<string> ValidateKits(WorldDefinition definition, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, ISet<string> allChannelNames, ISet<string> compositionChannelNames, ISet<string> dynamicsNames, HashSet<string> targetRegisterNames, IReadOnlyDictionary<string, WorldStateRow> stateRows, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, List<string> errors) {
        var kitNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var programRows = BodyMotionProgramRows(programs: definition.BodyMotionPrograms);

        var kits = definition.Kits;
        var hasMoveUpChannel = definition.Channels.Any(predicate: channel => (channel.Role == ChannelRole.MoveUp));
        // What every kit reads of the channels, read once: the held channels a producer may name, and the relative ones
        // a pad may not.
        var heldChannelNames = new HashSet<string>(collection: definition.Channels.Where(predicate: channel => (channel is { Shape: not ChannelShape.Relative })).Select(selector: channel => channel.Name), comparer: StringComparer.Ordinal);
        var relativeChannelNames = new HashSet<string>(collection: definition.Channels.Where(predicate: channel => (channel is { Shape: ChannelShape.Relative })).Select(selector: channel => channel.Name), comparer: StringComparer.Ordinal);

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

            // Resolved only when the program name is defined AND Motion-kind — ValidateMotionRow's coherence check
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

            ValidateMotionRow(
                motion: kit.Motion,
                program: motionProgram,
                path: $"{path}.motion",
                channelNames: compositionChannelNames,
                dynamicsNames: dynamicsNames,
                hasMoveUpChannel: hasMoveUpChannel,
                simulationRateHz: definition.SimulationRateHz,
                stateSlots: stateSlots,
                errors: errors
            );
            ValidateProducerParameters(
                producers: kit.Producers,
                programs: programs,
                programRows: programRows,
                channelNames: heldChannelNames,
                path: $"{path}.producers",
                errors: errors
            );
            ValidateNavigatedProducerMobility(
                definition: definition,
                errors: errors,
                hasMoveUpChannel: hasMoveUpChannel,
                kit: kit,
                motionProgram: motionProgram,
                path: $"{path}.producers",
                programRows: programRows
            );
            ValidateFlockMotion(definition: definition, errors: errors, kit: kit, motion: motionProgram, path: path);
            RequireRange(
                value: kit.Autonomy.MotionSeconds,
                min: 0f,
                max: WorldAutonomyCadence.MaximumSeconds,
                name: new Subject(Member: ".autonomy.motionSeconds", Path: path),
                errors: errors
            );
            RequireRange(
                value: kit.Autonomy.SteeringSeconds,
                min: 0f,
                max: WorldAutonomyCadence.MaximumSeconds,
                name: new Subject(Member: ".autonomy.steeringSeconds", Path: path),
                errors: errors
            );
            if (
                (kit.BodyContact == WorldBodyContactMode.Solid) &&
                (kit.Autonomy.MotionSeconds > 0f)
            ) {
                errors.Add(item: $"{path}.autonomy.motionSeconds must be 0 when bodyContact is Solid; a deferred body cannot preserve per-tick dynamic-contact semantics.");
            }
            if (kit.Autonomy.Far is { } far) {
                if (!(float.IsFinite(f: far.Beyond) && (far.Beyond >= 0f))) {
                    errors.Add(item: $"{path}.autonomy.far.beyond {far.Beyond} must be a finite distance of at least 0.");
                }
                // A far body is never updated more often than a near one: the tier only coarsens.
                RequireRange(
                    value: far.MotionSeconds,
                    min: kit.Autonomy.MotionSeconds,
                    max: WorldAutonomyCadence.MaximumSeconds,
                    name: new Subject(Member: ".autonomy.far.motionSeconds", Path: path),
                    errors: errors
                );
                RequireRange(
                    value: far.SteeringSeconds,
                    min: kit.Autonomy.SteeringSeconds,
                    max: WorldAutonomyCadence.MaximumSeconds,
                    name: new Subject(Member: ".autonomy.far.steeringSeconds", Path: path),
                    errors: errors
                );
                if ((kit.BodyContact == WorldBodyContactMode.Solid) && (far.MotionSeconds > 0f)) {
                    errors.Add(item: $"{path}.autonomy.far.motionSeconds must be 0 when bodyContact is Solid; a deferred body cannot preserve per-tick dynamic-contact semantics.");
                }
            }

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
                        path: $"{path}.actions[{channelName}]",
                        spec: spec,
                        stateRows: stateRows,
                        stateSlots: stateSlots,
                        targetRegisterNames: targetRegisterNames
                    );
                }
            }

            // The pad map is the SAME channel-name vocabulary actions key off, resolved at the other destination a
            // control application can carry a kit to: a screen's booted machine. Any declared level channel may bind
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

                    if (relativeChannelNames.Contains(item: channelName)) {
                        errors.Add(item: $"{path}.pad['{channelName}'] requires a held level, not relative displacement.");
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
            ValidateRigid(
                rigid: kit.Rigid,
                collider: kit.Collider,
                creations: definition.Creations,
                bodyContact: kit.BodyContact,
                path: $"{path}.rigid",
                errors: errors
            );
            ValidateCarry(
                carry: kit.Carry,
                path: $"{path}.carry",
                errors: errors
            );
            if ((kit.Mount is { } mount) && !(VectorFunctions.IsFinite(vector: mount.Seat) && VectorFunctions.IsFinite(vector: mount.Exit))) {
                errors.Add(item: $"{path}.mount seat and exit must be finite.");
            }
            if (kit.Mount?.Board is { } board) {
                if (!compositionChannelNames.Contains(item: board.Channel)) {
                    errors.Add(item: $"{path}.mount.board.channel '{board.Channel}' names no declared composition channel.");
                }
                if (!(float.IsFinite(f: board.Radius) && (board.Radius > 0f))) {
                    errors.Add(item: $"{path}.mount.board.radius must be finite and positive.");
                }
            }
            ValidateTether(
                channelNames: compositionChannelNames,
                errors: errors,
                path: $"{path}.tether",
                stateSlots: stateSlots,
                tether: kit.Tether
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
    private static void ValidateLookAssignment(WorldDefinition definition, WorldRowAssignment assignment, HashSet<string> lookNames, List<string> errors) {
        ValidateRowAssignment(
            assignment: assignment,
            definition: definition,
            errors: errors,
            rowNames: lookNames,
            rowNoun: "look",
            section: "looks.assignment"
        );
    }
    // A look names a catalog rig or a creation and admits the motion and poses that source supports.
    private static void ValidateLookRows(WorldDefinition definition, IReadOnlyList<int>? rows, List<string> errors) {
        var looks = definition.Looks;
        var creations = definition.Creations;
        var prototypeIds = DeclaredNames(creations, static row => row.Id?.Value);
        var dynamicsNames = DeclaredNames(definition.Dynamics, static row => row.Name);

        foreach (var index in RowOrdinals(looks.Count, rows)) {
            var look = looks[index];
            var path = $"looks[{index}]";

            if (look is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

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

                        if ((resolvedCreation is { Document.Noise: not null }) && (look.Motion.PartDynamics is { Count: > 0 })) {
                            errors.Add(item: $"{path}.motion.partDynamics cannot move parts of a creation carrying noise relief — its composed field must ride one root.");
                        }
                    }

                    break;
                default:
                    errors.Add(item: $"{path}.source is an unknown kind '{(look.Source?.GetType().Name ?? "(null)")}'.");

                    break;
            }

            RequirePositive(
                value: look.Scale,
                name: new Subject(Member: ".scale", Path: path),
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
                name: new Subject(Member: ".motion.gaitAmplitude", Path: path),
                errors: errors
            );
            RequireNonNegative(
                value: look.Motion.SecondsPerFrame,
                name: new Subject(Member: ".motion.secondsPerFrame", Path: path),
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

            if (look.Motion.Lanes is { Count: > 4 }) {
                errors.Add(item: $"looks row '{look.Name}' carries more than four render lanes.");
            }
            // A body-state read in a lane names the body and a declared slot; the lane would otherwise read zero
            // forever.
            for (var lane = 0; (lane < (look.Motion.Lanes?.Count ?? 0)); lane++) {
                foreach (var instruction in (look.Motion.Lanes![lane]?.Instructions ?? [])) {
                    // The lane evaluator reads any other operator as zero, which would leave the lane dark silently.
                    if (!WorldLookLaneVocabulary.Supports(instruction: instruction)) {
                        errors.Add(item: $"{path}.motion.lanes[{lane}] uses '{instruction.Operation}', which a render lane cannot evaluate; a lane takes constants, state reads, + - * /, negation, abs, sign, min, max and clamp.");
                    }
                    if (
                        (instruction.Payload is InstructionPayload.State { Name.Call.Channel: WorldBodyStateReads.ChannelName } operand) &&
                        !(
                            WorldBodyStateReads.TryBinding(
                            binding: out var binding,
                            key: operand.Key,
                            name: operand.Name
                        ) &&
                            WorldBodyStateReads.TryResolveSlot(
                            definition: definition,
                            kind: out _,
                            name: binding.Row[WorldBodyStateReads.RowPrefix.Length..],
                            slot: out _
                        )
                        )
                    ) {
                        errors.Add(item: $"{path}.motion.lanes[{lane}] reads '{operand.Name.Spelling}', which does not spell bodyState($body, <slot>) or bodyState(body, <n>, <slot>) with a slot state.body or state.identity declares.");
                    }
                }
            }
            if (look.Motion.Poses is { } poses) {
                var poseFrames = ((!isCatalog && (look.Source is WorldLookSource.Creation poseCreation))
                    ? WorldDefinitionRows.FindCreation(creations: creations, id: poseCreation.PrototypeId)?.Document.Frames
                    : null
                );

                if (isCatalog) {
                    errors.Add(item: $"{path}.motion.poses cannot be set on a catalog source — there is no timeline to select from.");
                }

                foreach (var (frame, reference) in poses) {
                    var posePath = $"{path}.motion.poses['{frame}']";

                    if (!isCatalog && !(poseFrames ?? []).Any(predicate: candidate => string.Equals(a: candidate?.Name, b: frame, comparisonType: StringComparison.Ordinal))) {
                        errors.Add(item: $"{posePath} names no frame of the look's creation timeline.");
                    }

                    if (!Puck.World.Authoring.CreationDriverDocument.IsStateSignal(signal: reference)) {
                        errors.Add(item: $"{posePath} '{reference}' must be a state.<row>[.<key>] reference.");
                    } else {
                        RequireNumericStateReference(
                            definition: definition,
                            errors: errors,
                            path: posePath,
                            reference: reference,
                            subject: "a pose selector"
                        );
                    }
                }
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
                        (resolvedCreation is { } partCreation)
                    ) {
                        var part = (partCreation.Document.Parts ?? []).FirstOrDefault(predicate: part => string.Equals(a: part.Id, b: partId, comparisonType: StringComparison.Ordinal));

                        if (part is null) {
                            errors.Add(item: $"{path}.motion.partDynamics['{partId}'] names no part of creation '{partCreation.Id}'.");
                        } else if ((partCreation.Document.Shapes ?? []).Any(predicate: shape => ((shape.Id == part.ShapeId) && (shape.Domain is { Count: > 0 })))) {
                            // A domain-bearing shape's slot carries its parent's delta frame, not a pose of its own
                            // (Client.WorldCreationInstances.PackTransforms), so a follower has no pose to ease — the same
                            // reason such a shape refuses its own swing/slide and a frame pose.
                            errors.Add(item: $"{path}.motion.partDynamics['{partId}'] names part '{partId}' of creation '{partCreation.Id}', whose shape {part.ShapeId} carries domain operators — a fold rides its parent's frame and has no pose of its own to ease.");
                        }
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


    }
    // A placement's scale: finite and positive, then inside the policy envelope. The one rule both the whole-document
    // placement walk and a dealt child's install check (TryValidateDealtChild) refuse by.
    private static void ValidatePlacementScale(WorldPlacement placement, WorldPlacementPolicyDefaults authoring, string path, List<string> errors) {
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
    }
    // The placement INSTANCE rows: id presence/uniqueness, the creation reference, finite transform, the policy
    // scale envelope, the lattice distribution's positive counts and finite steps, the mirror plane, and the animated-row
    // constraints (static-only facets; the reserved replay-pool ceiling, word-exact). Returns the resolved id set for
    // the anchor-union gate (a WorldAnchor.Placement resolves against it).
    private static void ValidatePlacements(WorldDefinition definition, IReadOnlyList<int>? rows, List<string> errors) {
        var placements = definition.Placements;
        var scope = DeclaredPlacementScope(definition);
        var authoring = definition.Authoring;
        var creationsById = FirstRowsByName(rows: definition.Creations, name: static row => row.Id?.Value);
        var kitsByName = FirstRowsByName(rows: definition.Kits, name: static row => row.Name);
        var placementsById = WorldPlacementIndex.ById(placements: placements);
        var slotCounts = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var index in RowOrdinals(placements.Count, rows)) {
            var placement = placements[index];
            var path = $"placements[{index}]";

            if (placement is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            ValidatePlacementSpatial(
                errors: errors,
                path: path,
                placement: placement
            );
            if (!WorldPlacementStamp.TryValidateSurface(definition: definition, placement: placement, reason: out var surfaceReason)) {
                errors.Add(item: surfaceReason);
            }

            // A dealt child is the one row whose id may spell the child separator, and only in the exact shape the
            // sweep mints — <template>/<cellKey> under a parent carrying the deal facet.
            _ = TryFindRow(
                key: placement.Parent,
                map: placementsById,
                row: out var parentRow
            );

            var isDealtChild = WorldPlacementDeal.IsChild(
                parent: parentRow,
                placement: placement
            );

            if (isDealtChild) {
                if (placement.Deal is not null) {
                    errors.Add(item: $"{path}.deal cannot turn a source-owned child into another deal template.");
                }
                if (!slotCounts.TryGetValue(key: parentRow!.Id, value: out var slots)) {
                    slots = WorldPlacementDeal.InstanceCount(template: parentRow, worldSeed: (definition.Generation?.WorldSeed ?? 0UL));
                    slotCounts.Add(key: parentRow.Id, value: slots);
                }
                if ((placement.DealSlot is not { } slot) || (slot < 0) || (slot >= slots)) {
                    errors.Add(item: $"{path}.dealSlot must name a slot in parent '{parentRow!.Id}' (0..{(slots - 1)}).");
                }
            } else if (placement.DealSlot is not null) {
                errors.Add(item: $"{path}.dealSlot is reserved for a child of a deal template.");
            }
            if (WorldPlacementAudiences.DealtAncestor(placement: placement, placementsById: placementsById) is { } dealtAncestor) {
                RefuseUndisclosingMembers(definition: definition, errors: errors, path: path, placement: placement, template: dealtAncestor);
            }

            if (
                !isDealtChild &&
                (placement.Id is not null) &&
                WorldPlacementDeal.IsChildId(id: placement.Id)
            ) {
                errors.Add(item: $"{path}.id '{placement.Id}' spells the dealt-child separator '{WorldPlacementDeal.ChildSeparator}' — only a deal sweep mints an id of that shape (<template>{WorldPlacementDeal.ChildSeparator}<cellKey> under a parent carrying .deal); author the id without it.");
            }
            if (
                (placement.Id is not null) &&
                !WorldPlacement.TryValidateId(
                id: placement.Id,
                reason: out var idReason
            )
            ) {
                errors.Add(item: $"{path}.id: {idReason}.");
            }

            RequireDeclared(
                value: placement.PrototypeId,
                declaredSet: scope.PrototypeIds,
                path: path,
                field: "prototypeId",
                rowNoun: "creation",
                errors: errors
            );

            if (placement.Position is null) {
                errors.Add(item: $"{path}.position is required.");
                continue;
            }

            if (!placement.Position.IsFinite) {
                errors.Add(item: $"{path}.position must contain finite coordinates.");
            }

            RequireFinite(
                value: placement.YawDegrees,
                name: new Subject(Member: ".yawDegrees", Path: path),
                errors: errors
            );

            ValidatePlacementScale(
                authoring: authoring,
                errors: errors,
                path: path,
                placement: placement
            );

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
                    !VectorFunctions.IsFinite(vector: mirror.Normal) ||
                    (mirror.Normal.LengthSquared() <= 0f)
                ) {
                    errors.Add(item: $"{path}.mirror.normal must be finite and nonzero.");
                }

                RequireFinite(
                    value: mirror.Offset,
                    name: new Subject(Member: ".mirror.offset", Path: path),
                    errors: errors
                );
            }

            // The emission facet binds to the placement root under a distribution (documented on WorldPlacement) — no
            // per-copy constraint to gate; only patch resolution and the shared gain/radius bounds.
            ValidateEmission(
                emission: placement.Emission,
                patchIds: scope.PatchIds,
                path: $"{path}.emission",
                errors: errors
            );

            if (placement.Solid is { } placementSolid) {
                RequireFinite(
                    value: placementSolid.Margin,
                    name: new Subject(Member: ".solid.margin", Path: path),
                    errors: errors
                );

                foreach (var variantId in SolidVariantIds(placement: placement)) {
                    if (!TryFindRow(key: variantId, map: creationsById, row: out var variantCreation)) { continue; }
                    if ((StateBinding.Parse(token: variantCreation.Document.SizeBy) is { Key: StateBinding.BodyKey }) && (placement.Attach?.BodyIndex is null)) {
                        errors.Add(item: $"{path}.solid creation '{variantId}' sizeBy needs a body attached by 'bodyIndex' for '$body'.");
                    }
                    _ = RefuseSolidVariantShapes(creation: variantCreation, errors: errors, path: path, variantId: variantId);
                }
            }

            // The animated-row constraints: a placement of a creation with frames or drivers replays through the reserved dynamic
            // pool — single copy only (pattern/mirror are static-stamp facets), and at most the reserved pool count.
            _ = TryFindRow(
                key: placement.PrototypeId,
                map: creationsById,
                row: out var animatedCreation
            );

            var isAnimated = (animatedCreation is { Document.Frames.Count: > 0 } or { Document.Drivers.Count: > 0 });

            if (isAnimated) {



                if (
                    (placement.Distribution is not null) ||
                    (placement.Mirror is not null)
                ) {
                    errors.Add(item: $"{path} is ANIMATED (its creation carries timeline frames or drivers) — distribution/mirror facets are static-stamp-only.");
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
                    kitNames: scope.KitNames,
                    kitsByName: kitsByName,
                    lookNames: scope.LookNames,
                    path: $"{path}.inhabit",
                    placement: placement
                );

                if (
                    (placement.Distribution is not null) ||
                    (placement.Mirror is not null)
                ) {
                    errors.Add(item: $"{path} INHABITS — placement distribution/mirror facets are incompatible with a live body.");
                }


            }

            // The per-instance FACE overrides: each names a declared creation face, no duplicates. The View source's
            // camera name is resolved LENIENTLY (a derived creation-camera name is unknown to the document validator; the
            // binder shows an unresolved feed as unbound glass, never a hard reject).
            ValidateFaceSources(
                definition: definition,
                faceSources: placement.FaceSources,
                placement: placement,
                creationsById: creationsById,
                scope: scope,
                path: $"{path}.faceSources",
                errors: errors
            );

            // The REGION facet: a named sensing volume (see WorldPlacementRegion's own doc). The name IS the
            // placement's own id, so no separate uniqueness check is owed — the id duplication check above already
            // covers it.
            if (placement.Region is { } region) {
                ValidateRegion(region: region, path: $"{path}.region", errors: errors);
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

            // The BOARD facet anchors a discrete Grid topology's frame to this placement — the tabletop primitive.
            ValidateBoardFacet(
                board: placement.Board,
                definition: definition,
                path: path,
                errors: errors
            );

            // The ATTACH facet binds the row's resolved pose to a live population body (see WorldPlacementAttach).
            // BodyIndex uses the same 0-based entity indexing as WorldAnchor.Entity and the body:<n> grant subject —
            // never the 1-based player.* seat number. Region, Solid (analytic contact provider), and Emission all
            // read the resolved dynamic pose (WorldEventFeed.CollectRegions, WorldColliderSet.RefreshAttached,
            // WorldAnchorPoses), so an equipped item's aura/hitbox/voice tracks the carrier.
            // Distribution/mirror and inhabit stay refused (a row cannot both spawn its own bodies and ride
            // another's); FaceSources stays permitted (a content selector, not a transform).
            if (placement.Attach is { } attach) {
                if ((attach.BodyIndex is null) == (attach.Placement is null)) {
                    errors.Add(item: $"{path}.attach names its carrier once: 'bodyIndex', or the inhabited 'placement' whose body the row rides.");
                } else if (attach.BodyIndex is { } bodyIndex) {
                    if ((bodyIndex < 0) || (bodyIndex >= definition.Population.Capacity)) {
                        errors.Add(item: $"{path}.attach.bodyIndex {bodyIndex} is outside 0..{(definition.Population.Capacity - 1)} for the authored population capacity (0-based entity index — body:1 is player.* seat 2, not body index 1).");
                    }
                } else if (WorldDefinitionRows.FindPlacement(placements: definition.Placements, id: attach.Placement!) is not { Inhabit: not null }) {
                    errors.Add(item: $"{path}.attach.placement '{attach.Placement}' names no inhabited placement: the row rides the body that placement's inhabit facet spawns.");
                }

                if (!VectorFunctions.IsFinite(vector: attach.LocalOffset)) {
                    errors.Add(item: $"{path}.attach.localOffset must contain finite coordinates.");
                }

                RequireFinite(
                    value: attach.LocalYawDegrees,
                    name: new Subject(Member: ".attach.localYawDegrees", Path: path),
                    errors: errors
                );

                if (
                    (placement.Distribution is not null) ||
                    (placement.Mirror is not null)
                ) {
                    errors.Add(item: $"{path} ATTACHES — placement distribution/mirror facets are static-stamp-only (the same rule an animated or inhabited row already enforces), incompatible with a live-following root.");
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
                    prototypeIds: scope.PrototypeIds
                );
            }

            // The RESPOND facet: an ordered, state-driven prototype swap (see WorldPlacementResponse).
            if (placement.Respond is not null) {
                ValidatePlacementResponse(
                    definition: definition,
                    errors: errors,
                    placement: placement,
                    placementPath: path,
                    prototypeIds: scope.PrototypeIds
                );
            } else if (placement.Holding != 0) {
                errors.Add(item: $"{path}.holding is {placement.Holding} on a row without respond — it records which response entries hold.");
            }

            // The deal facet: the row is a template whose children are dealt from a keyed state row (see
            // WorldPlacementDeal).
            if (placement.Deal is { } deal) {
                ValidatePlacementDeal(
                    deal: deal,
                    definition: definition,
                    errors: errors,
                    path: path,
                    placement: placement,
                    prototypeIds: scope.PrototypeIds
                );
            }
        }
    }

    // Reservations depend on the placement set. They use declarations and counts without validating sibling rows.
    private static HashSet<string> ValidatePlacementReservations(WorldDefinition definition, List<string> errors) {
        var placements = definition.Placements;
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);
        var topologies = new HashSet<string>(comparer: StringComparer.Ordinal);
        var occupiedSlots = new HashSet<(string Parent, int Slot)>();
        var byId = WorldPlacementIndex.ById(placements);
        var childCounts = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var analytic = !WorldContactSelection.RequiresField(definition.Collision);
        var colliders = 0L;
        const long Limit = WorldPlacementPolicy.MaxSolidPlacementColliders + 1L;
        var creationCounts = new Dictionary<string, long>(comparer: StringComparer.Ordinal);
        var creations = FirstRowsByName(rows: definition.Creations, name: static row => row.Id?.Value);

        if (analytic) {
            foreach (var child in placements) {
                if ((child is { Id: not null, Parent: { } parent }) && byId.TryGetValue(parent, out var template) &&
                    WorldPlacementDeal.IsChild(parent: template, placement: child)) {
                    childCounts[parent] = childCounts.GetValueOrDefault(parent) + 1;
                }
            }
        }
        for (var index = 0; index < placements.Count; index++) {
            if (placements[index] is not { } placement) { continue; }
            var path = $"placements[{index}]";
            RequireUniqueName(value: placement.Id, seen: ids, path: path, field: "id", errors: errors);
            if (placement.Board is { Topology: { Length: > 0 } topology } && !topologies.Add(topology)) {
                errors.Add(item: $"{path}.board.topology '{topology}' is already carried by another placement.");
            }
            var parent = placement.Parent is { } parentId ? byId.GetValueOrDefault(parentId) : null;
            var dealt = WorldPlacementDeal.IsChild(parent: parent, placement: placement);
            if (dealt && placement.DealSlot is { } slot && !occupiedSlots.Add((parent!.Id, slot))) {
                errors.Add(item: $"{path}.dealSlot {slot} is already occupied under parent '{parent.Id}'.");
            }
            if (!analytic || placement.Solid is null || colliders == Limit ||
                (dealt && parent!.Deal!.Preserve is not ({ Prototype: true } or { Facets: true }))) { continue; }

            var shapeColliders = 0L;

            foreach (var variant in SolidVariantIds(placement)) {
                if (!creationCounts.TryGetValue(variant, out var count)) {
                    count = creations.TryGetValue(variant, out var creation)
                        ? RefuseSolidVariantShapes(creation: creation, errors: null, path: path, variantId: variant)
                        : 0;
                    creationCounts.Add(variant, count);
                }
                shapeColliders = Math.Max(shapeColliders, count);
            }
            var copies = ReservedPlacementCopies(ceiling: Limit, childCounts: childCounts, placement: placement);
            colliders = Math.Min(Limit, colliders + CreationStampLattice.MultiplySaturated(ceiling: Limit, left: copies, right: shapeColliders));
            if (colliders == Limit) {
                errors.Add(item: $"{path}.solid expands the document past the {WorldPlacementPolicy.MaxSolidPlacementColliders}-collider analytic-placement ceiling; reduce lattice counts, mirror copies, or creation shapes.");
            }
        }
        if (!WorldPlacementFrameCompilation.TryValidate(placements: placements, reason: out var parentReason)) {
            errors.Add(item: $"placements: {parentReason}");
        }
        return ids;
    }

    // The deal facet (see WorldPlacementDeal): the dealt row and the variant row must be declared keyed rows, the
    // region must materialize a fixed instance count at least the dealt row's capacity, every prototype a child could
    // show must be a declared static creation, and the template carries none of the facets a child cannot copy.
    private static void ValidatePlacementDeal(WorldPlacementDeal deal, WorldPlacement placement, WorldDefinition definition, HashSet<string> prototypeIds, string path, List<string> errors) {
        var dealPath = $"{path}.deal";

        if (deal.Reflow is { } reflow) {
            if ((deal.Preserve?.Transform != true) || !HasOccupationSpatial(placement: placement)) {
                errors.Add(item: $"{dealPath}.reflow requires preserve.transform and an occupation spatial volume.");
            }
            if ((reflow.CandidateBudget is < 1 or > 65536) || (reflow.CostPerMove < 0)) {
                errors.Add(item: $"{dealPath}.reflow requires candidateBudget 1..65536 and nonnegative costPerMove.");
            }
            if (reflow.CostRow is { } costRow) {
                var payer = WorldDefinitionRows.FindStateRow(definition.State, costRow);

                if ((payer is not { Kind: CellKind.Int }) || (payer.IsKeyed != (reflow.CostKey is not null))) {
                    errors.Add(item: $"{dealPath}.reflow.costRow must name an Int row with a matching costKey shape.");
                }
            } else if ((reflow.CostPerMove != 0) || (reflow.CostKey is not null)) {
                errors.Add(item: $"{dealPath}.reflow requires costRow for payment.");
            }
        }

        if (
            (placement.Inhabit is not null) ||
            (placement.Attach is not null) ||
            (placement.Respond is not null) ||
            (placement.Mirror is not null) ||
            (placement.FaceSources is not null)
        ) {
            errors.Add(item: $"{dealPath} is refused alongside inhabit/attach/respond/mirror/faceSources — a dealt template is a layout, and its children are plain static stamps of its prototype, solid, grip, region, and emission.");
        }

        RefuseUndisclosingMembers(definition: definition, errors: errors, path: path, placement: placement, template: placement);

        RequireStaticCreation(
            creations: definition.Creations,
            errors: errors,
            path: $"{path}.prototypeId",
            prototypeId: placement.PrototypeId,
            prototypeIds: prototypeIds
        );

        var instanceCount = -1;

        switch (placement.Distribution?.Region) {
            case null:
                errors.Add(item: $"{dealPath} requires {path}.distribution — the region is what lays the dealt children out.");

                break;
            case WorldDistributionRegion.Lattice or WorldDistributionRegion.Noise or WorldDistributionRegion.Scatter:
                instanceCount = WorldPlacementDeal.InstanceCount(
                    template: placement,
                    worldSeed: (definition.Generation?.WorldSeed ?? 0UL)
                );

                break;
            case { } region:
                errors.Add(item: $"{dealPath} requires a distribution whose region materializes a fixed instance count (lattice, noise, scatter); '{region.GetType().Name.ToLowerInvariant()}' does not.");

                break;
        }

        if (RequireDealtRow(
            definition: definition,
            errors: errors,
            name: deal.Row,
            path: $"{dealPath}.row",
            row: out var dealtRow
        ) && (instanceCount >= 0) && (dealtRow.CellCeiling > instanceCount)) {
            errors.Add(item: $"{dealPath}.row '{deal.Row}' has capacity {dealtRow.CellCeiling}, but {path}.distribution materializes {instanceCount} instance(s) — a dealt row can never hold more cells than the region has offsets.");
        }

        if (deal.Variants is not { } variants) {
            return;
        }

        var variantsPath = $"{dealPath}.variants";

        _ = RequireDealtRow(
            definition: definition,
            errors: errors,
            name: variants.Row,
            path: $"{variantsPath}.row",
            row: out _
        );

        if (variants.Map is not { } map) {
            errors.Add(item: $"{variantsPath}.map is required.");

            return;
        }

        foreach (var (text, prototypeId) in map) {
            var entryPath = $"{variantsPath}.map['{text}']";

            if (string.IsNullOrEmpty(value: text)) {
                errors.Add(item: $"{variantsPath}.map carries an empty key — a variant is selected by a cell's text.");
            }

            if (!RequireDeclared(
                declaredSet: prototypeIds,
                errors: errors,
                field: string.Empty,
                path: entryPath,
                rowNoun: "creation",
                value: prototypeId
            )) {
                continue;
            }

            RequireStaticCreation(
                creations: definition.Creations,
                errors: errors,
                path: entryPath,
                prototypeId: prototypeId,
                prototypeIds: prototypeIds
            );
        }
    }
    private static bool HasOccupationSpatial(WorldPlacement placement) =>
        (placement.Spatial?.Any(predicate: static volume => (volume.Role == WorldPlacementSpatialRole.Occupation)) == true);
    // A deal's row reference: a declared state.world row that is keyed (a slot row deals nothing), of any cell kind.
    private static bool RequireDealtRow(string? name, WorldDefinition definition, string path, List<string> errors, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldStateRow? row) {
        row = null;

        if (string.IsNullOrWhiteSpace(value: name)) {
            errors.Add(item: $"{path} is required.");

            return false;
        }

        if (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: name
        ) is not { } declared) {
            errors.Add(item: $"{path} names state row '{name}', which state.world does not declare.");

            return false;
        }

        if (!declared.IsKeyed) {
            errors.Add(item: $"{path} names state row '{name}', which is a slot — a deal reads a keyed row, one child per cell.");

            return false;
        }

        row = declared;

        return true;
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
                max: WorldBodiesLimits.IndexLimit,
                min: 1,
                name: new Subject(Member: ".capacity", Path: path),
                value: capacity
            );
        }
    }
    // The shared sequence/row-view gate for kit and look assignment.
    // A selector reads a body's own register the way WorldPopulation.SyncBodySelections does: a state.body counter, whose
    // value each case compares as a whole number, and every case's row declared in the table the assignment distributes.
    private static void ValidateRowSelector(WorldDefinition definition, WorldRowSelector select, string path, string rowNoun, HashSet<string> rowNames, List<string> errors) {
        // A register is document-wide, so every body (props and pieces included) holds its initial value until something
        // writes it: a case naming that value would select every body and re-kit the whole world.
        float? initial = null;

        if (string.IsNullOrEmpty(value: select.BodyState)) {
            errors.Add(item: $"{path}.bodyState is required.");
        } else if (definition.BodyState.FirstOrDefault(predicate: slot => string.Equals(
            a: slot.Name,
            b: select.BodyState,
            comparisonType: StringComparison.Ordinal
        )) is not { } slot) {
            errors.Add(item: $"{path}.bodyState names register '{select.BodyState}', which state.body does not declare.");
        } else if (slot.Kind != ActionStateKind.Counter) {
            errors.Add(item: $"{path}.bodyState names register '{select.BodyState}', a {slot.Kind}; a selector reads a Counter.");
        } else {
            initial = slot.Initial;
        }

        if (select.Cases is not { Count: > 0 } cases) {
            errors.Add(item: $"{path}.cases must name at least one value.");

            return;
        }

        var values = new HashSet<long>();

        for (var index = 0; (index < cases.Count); index++) {
            if (cases[index] is not { } selectCase) {
                errors.Add(item: $"{path}.cases[{index}] is required.");

                continue;
            }

            if (!values.Add(item: selectCase.Value)) {
                errors.Add(item: $"{path}.cases[{index}] repeats value {selectCase.Value}.");
            }

            if ((initial is { } born) && (born == selectCase.Value)) {
                errors.Add(item: $"{path}.cases[{index}] value {selectCase.Value} is register '{select.BodyState}' initial value, which every body holds until it is written; the case would select every body. Leave the initial value to the sequence's own row and name only the values that deviate.");
            }

            RequireDeclared(
                value: selectCase.Row,
                declaredSet: rowNames,
                path: $"{path}.cases[{index}].row",
                field: "",
                rowNoun: rowNoun,
                errors: errors
            );
        }
    }
    private static void ValidateRowAssignment(WorldDefinition definition, WorldRowAssignment assignment, string section, string rowNoun, HashSet<string> rowNames, List<string> errors) {
        if (assignment.Select is { } select) {
            ValidateRowSelector(
                definition: definition,
                errors: errors,
                path: $"{section}.select",
                rowNames: rowNames,
                rowNoun: rowNoun,
                select: select
            );
        }

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
            } else if (!Puck.Confinement.PortablePath.IsRelative(allowParentSegments: true, path: font.Source)) {
                errors.Add(item: $"{path}.source '{font.Source}' must be a portable relative path, resolved beside the document (forward slashes, no empty or dot segments).");
            }

            if (!IsValidFontHash(hash: font.Hash)) {
                errors.Add(item: $"{path}.hash '{font.Hash}' must match sha256/{{64 lowercase hex}}; bare digests and sha256-64/ are refused.");
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
    // A region volume's shape: a sphere of finite positive radius or a box of finite positive half-extents, never both,
    // about a finite center and yaw. Placement regions and navigation areas share it.
    private static void ValidateRegion(WorldPlacementRegion region, string path, List<string> errors) {
        if (region.HalfExtents is { } halfExtents) {
            if (region.Radius != 0f) {
                errors.Add(item: $"{path} authors both radius and halfExtents; a region is a sphere (radius) or a box (halfExtents).");
            }
            if (
                !float.IsFinite(f: halfExtents.X) || (halfExtents.X <= 0f) ||
                !float.IsFinite(f: halfExtents.Y) || (halfExtents.Y <= 0f) ||
                !float.IsFinite(f: halfExtents.Z) || (halfExtents.Z <= 0f)
            ) {
                errors.Add(item: $"{path}.halfExtents ({halfExtents.X}, {halfExtents.Y}, {halfExtents.Z}) must each be finite and positive.");
            }
        } else if (
            !float.IsFinite(f: region.Radius) ||
            (region.Radius <= 0f)
        ) {
            errors.Add(item: $"{path}.radius {region.Radius} must be finite and positive (or author halfExtents for a box).");
        }
        if (
            !float.IsFinite(f: region.Center.X) || !float.IsFinite(f: region.Center.Y) || !float.IsFinite(f: region.Center.Z) ||
            !float.IsFinite(f: region.YawDegrees)
        ) {
            errors.Add(item: $"{path}.center and .yawDegrees must be finite.");
        }
        if (region.Capacity is < 1) {
            errors.Add(item: $"{path}.capacity {region.Capacity} must be at least 1: it is the occupants a rule's sweep over the region is priced at.");
        }
    }
}
