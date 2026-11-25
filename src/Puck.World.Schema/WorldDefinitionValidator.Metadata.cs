using System.Numerics;
using Puck.Assets.Documents;
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
            if (!FixedTickConversion.IsRate(ratePerSecond: ((ulong)candidate))) {
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
            (row.Cells![0].Value.AsFixed <= 0L)
        ) {
            errors.Add(item: $"{path} row '{name}' carries {((double)FixedQ4816.FromRawBits(value: row.Cells[0].Value.AsFixed)):0.####} — a locomotion rate must be positive.");
        }
    }
    // The gravitational field (SIM-AFFECTING). The constant is non-negative and the softening length positive (a
    // zero softening lets a coincident pair diverge); every attractor names a live placement, exactly once, with a
    // non-negative mass. An inactive section still validates: authoring the constants before the sources is legal.
    private static void RequireGravityMagnitude(float value, string path, List<string> errors) {
        if (!float.IsFinite(f: value) || (value == 0f)) {
            errors.Add(item: $"{path}.acceleration.magnitude must be finite and nonzero: positive pulls toward the form, negative pushes away from it.");
        }
    }
    private static void RequireGravityDirection(DocumentVector3? value, string name, List<string> errors) {
        if ((value is { } direction) && (!VectorFunctions.IsFinite(vector: direction) || (direction.LengthSquared() <= 0f))) {
            errors.Add(item: $"{name} must be a finite direction of nonzero length.");
        }
    }
    // A gravity row's scalar binding: a declared Fixed slot row, read each tick.
    private static void RequireGravityScalarRow(string? name, string path, IReadOnlyList<WorldStateRow> state, List<string> errors) {
        if (name is null) {
            return;
        }

        if (WorldDefinitionRows.FindStateRow(name: name, rows: state) is not { } row) {
            errors.Add(item: $"{path} '{name}' names no state row.");
        } else if ((row.Kind != CellKind.Fixed) || !row.IsSlot) {
            errors.Add(item: $"{path} '{name}' must be a Fixed slot row.");
        }
    }
    // Orbits (SIM-AFFECTING): a unique name, a live focus placement, a positive axis, an eccentricity the fixed-point
    // Kepler solve settles for, a period of whole engine ticks, and a riding row that exists.
    private static void ValidateGravityOrbits(WorldGravity gravity, IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldStateRow> state, List<string> errors) {
        if (gravity.Orbits is not { } orbits) {
            return;
        }

        if (orbits.Count > WorldGravityCapacity.MaxOrbits) {
            errors.Add(item: $"gravity.orbits declares {orbits.Count} rows, past the {WorldGravityCapacity.MaxOrbits}-row cap.");
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var riders = new HashSet<int>();

        for (var index = 0; (index < orbits.Count); index++) {
            var orbit = orbits[index];
            var path = $"gravity.orbits[{index}]";

            if (orbit is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: orbit.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (!names.Add(item: orbit.Name)) {
                errors.Add(item: $"{path}.name duplicates '{orbit.Name}'.");
            }

            if (string.IsNullOrWhiteSpace(value: orbit.Around)) {
                errors.Add(item: $"{path}.around is required.");
            } else if (!WorldPlacementIndex.ById(placements: placements).ContainsKey(key: orbit.Around)) {
                errors.Add(item: $"{path}.around '{orbit.Around}' resolves to no placement.");
            }

            RequirePositive(
                value: orbit.SemiMajorAxis,
                name: new Subject(Member: ".semiMajorAxis", Path: path),
                errors: errors
            );

            var maxEccentricity = ((float)((double)FixedConicOrbit.MaxEccentricity));

            if (!float.IsFinite(f: orbit.Eccentricity) || (orbit.Eccentricity < 0f) || (orbit.Eccentricity >= maxEccentricity)) {
                errors.Add(item: $"{path}.eccentricity {orbit.Eccentricity} must be zero or more and below {maxEccentricity:0.##}: a closed orbit the fixed-point solve settles for.");
            }

            if (!float.IsFinite(f: orbit.InclinationDegrees) || !float.IsFinite(f: orbit.AscendingNodeDegrees) || !float.IsFinite(f: orbit.PeriapsisDegrees) || !float.IsFinite(f: orbit.Phase)) {
                errors.Add(item: $"{path} must author finite angles and a finite phase.");
            }

            if (!float.IsFinite(f: orbit.JoinSpeed) || (orbit.JoinSpeed < 0f)) {
                errors.Add(item: $"{path}.joinSpeed must be a finite speed of zero or more.");
            }

            if ((orbit.PeriodSeconds <= 0m) || !FixedTickConversion.TryDurationEngineTicksExact(seconds: orbit.PeriodSeconds, ticks: out _)) {
                errors.Add(item: $"{path}.periodSeconds {orbit.PeriodSeconds} must be a positive whole number of engine ticks ({FixedTickConversion.TicksPerSecond} a second).");
            }

            if (orbit.BodyIndex is { } body) {
                if (body < 0) {
                    errors.Add(item: $"{path}.bodyIndex {body} must be zero or more.");
                } else if (!riders.Add(item: body)) {
                    errors.Add(item: $"{path}.bodyIndex {body} already rides another orbit.");
                }
            } else if (orbit.Riding is not null) {
                errors.Add(item: $"{path}.riding names the row that keeps a body on the orbit; this orbit names no bodyIndex.");
            }

            if ((orbit.Riding is { } riding) && (WorldDefinitionRows.FindStateRow(name: riding, rows: state) is not { IsSlot: true, Kind: CellKind.Int or CellKind.Bool or CellKind.Fixed })) {
                errors.Add(item: $"{path}.riding '{riding}' must name an Int, Bool or Fixed slot row.");
            }
        }
    }
    // Tides (SIM-AFFECTING): each source names exactly one of a placement and an orbit, states a positive mass in the
    // observed quantities, and the section authors a field of its own for the tide to add to.
    private static void ValidateGravityTides(WorldGravity gravity, IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldStateRow> state, List<string> errors) {
        if ((gravity.TideOrigin is { } origin) && !WorldPlacementIndex.ById(placements: placements).ContainsKey(key: origin)) {
            errors.Add(item: $"gravity.tideOrigin '{origin}' resolves to no placement.");
        }

        if (gravity.Tides is not { Count: > 0 } tides) {
            return;
        }

        if (tides.Count > WorldGravityCapacity.MaxTides) {
            errors.Add(item: $"gravity.tides declares {tides.Count} rows, past the {WorldGravityCapacity.MaxTides}-row cap.");
        }

        if ((gravity.UniformAcceleration == Vector3.Zero) && (gravity.Areas is not { Count: > 0 }) && !(gravity.GravitationalConstant > 0f)) {
            errors.Add(item: "gravity.tides adds to the world's own field, and this section authors none: declare a uniform, an area or a source for the tide to add to.");
        }

        for (var index = 0; (index < tides.Count); index++) {
            var tide = tides[index];
            var path = $"gravity.tides[{index}]";

            if (tide is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if ((tide.PlacementId is null) == (tide.Orbit is null)) {
                errors.Add(item: $"{path} must name exactly one of placementId and orbit: where the source stands.");
            } else if ((tide.PlacementId is { } placementId) && !WorldPlacementIndex.ById(placements: placements).ContainsKey(key: placementId)) {
                errors.Add(item: $"{path}.placementId '{placementId}' resolves to no placement.");
            } else if ((tide.Orbit is { } orbitName) && !(gravity.Orbits?.Any(predicate: orbit => string.Equals(a: orbit?.Name, b: orbitName, comparisonType: StringComparison.Ordinal)) ?? false)) {
                errors.Add(item: $"{path}.orbit '{orbitName}' names no gravity.orbits row.");
            }

            RequirePositive(
                value: tide.SurfaceGravity,
                name: new Subject(Member: ".surfaceGravity", Path: path),
                errors: errors
            );
            RequirePositive(
                value: tide.ReferenceRadius,
                name: new Subject(Member: ".referenceRadius", Path: path),
                errors: errors
            );

            if ((tide.SurfaceGravity > 0f) && (tide.ReferenceRadius > 0f) && !FixedWorldGravityTide.TryStrength(strength: out _, tide: tide)) {
                errors.Add(item: $"{path} cannot lower surfaceGravity times referenceRadius squared to Q48.16 without underflow or overflow.");
            }

            RequireGravityScalarRow(name: tide.Scale, path: $"{path}.scale", state: state, errors: errors);
        }
    }
    private static void ValidateGravity(WorldGravity gravity, IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldStateRow> state, WorldCollision collision, ulong worldSeed, List<string> errors) {
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

                if (!WorldPlacementIndex.ById(placements: placements).ContainsKey(key: attractor.PlacementId)) {
                    errors.Add(item: $"{path}.placementId '{attractor.PlacementId}' resolves to no placement.");
                }
            }

            RequireNonNegative(
                value: attractor.Mass,
                name: new Subject(Member: ".mass", Path: path),
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

                    if (!WorldPlacementIndex.ById(placements: placements).ContainsKey(key: point.PlacementId)) {
                        errors.Add(item: $"{path}.placementId '{point.PlacementId}' resolves to no placement.");
                    }
                }

                RequirePositive(
                    value: point.SurfaceGravity,
                    name: new Subject(Member: ".surfaceGravity", Path: path),
                    errors: errors
                );
                RequirePositive(
                    value: point.ReferenceRadius,
                    name: new Subject(Member: ".referenceRadius", Path: path),
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

        ValidateGravityOrbits(gravity: gravity, placements: placements, state: state, errors: errors);
        ValidateGravityTides(gravity: gravity, placements: placements, state: state, errors: errors);

        if (gravity.Areas is not { } areas) {
            return;
        }

        if (areas.Count > WorldGravityCapacity.MaxAreas) {
            errors.Add(item: $"gravity.areas declares {areas.Count} rows, past the {WorldGravityCapacity.MaxAreas}-row cap.");
        }

        var frames = WorldPlacementFrameCompilation.Compile(placements: placements, worldSeed: worldSeed);

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
            } else if ((placement = WorldPlacementIndex.ById(placements: placements).GetValueOrDefault(key: area.PlacementId)) is null) {
                errors.Add(item: $"{path}.placementId '{area.PlacementId}' resolves to no placement.");
            }

            if (!Enum.IsDefined(value: area.Mode)) {
                errors.Add(item: $"{path}.mode '{area.Mode}' is not defined.");
            }

            switch (area.Bounds) {
                case WorldGravityAreaBounds.SphereBounds sphere:
                    RequirePositive(
                        value: sphere.Radius,
                        name: new Subject(Member: ".bounds.radius", Path: path),
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
                    if (!VectorFunctions.IsFinite(vector: directional.Value)) {
                        errors.Add(item: $"{path}.acceleration.value must contain finite coordinates.");
                    }
                    break;
                case WorldGravityAreaAcceleration.Radial radial:
                    RequireGravityMagnitude(value: radial.Magnitude, path: path, errors: errors);
                    break;
                case WorldGravityAreaAcceleration.Axial axial:
                    RequireGravityMagnitude(value: axial.Magnitude, path: path, errors: errors);
                    RequireGravityDirection(value: axial.Axis, name: $"{path}.acceleration.axis", errors: errors);
                    break;
                case WorldGravityAreaAcceleration.Planar planar:
                    RequireGravityMagnitude(value: planar.Magnitude, path: path, errors: errors);
                    RequireGravityDirection(value: planar.Normal, name: $"{path}.acceleration.normal", errors: errors);
                    break;
                case WorldGravityAreaAcceleration.PointMass point:
                    if (!float.IsFinite(f: point.SurfaceGravity) || (point.SurfaceGravity == 0f)) {
                        errors.Add(item: $"{path}.acceleration.surfaceGravity must be finite and nonzero: positive pulls toward the placement, negative pushes away.");
                    }
                    RequirePositive(
                        value: point.ReferenceRadius,
                        name: new Subject(Member: ".acceleration.referenceRadius", Path: path),
                        errors: errors
                    );
                    break;
                case WorldGravityAreaAcceleration.Surface surface:
                    RequireGravityMagnitude(value: surface.Magnitude, path: path, errors: errors);
                    if (!collision.Requirements.Contains(value: WorldContactRequirement.GradientDerivedUp)) {
                        errors.Add(item: $"{path}.acceleration is 'surface', which pulls against the contact field's gradient: add GradientDerivedUp to collision.requirements so the world's contact provider answers it.");
                    }
                    break;
                case null:
                    errors.Add(item: $"{path}.acceleration is required.");
                    break;
                default:
                    errors.Add(item: $"{path}.acceleration has an unsupported shape.");
                    break;
            }

            if (!float.IsFinite(f: area.Blend) || (area.Blend < 0f)) {
                errors.Add(item: $"{path}.blend must be a finite depth of zero or more.");
            }

            RequireGravityScalarRow(name: area.Weight, path: $"{path}.weight", state: state, errors: errors);
            RequireGravityScalarRow(name: area.Scale, path: $"{path}.scale", state: state, errors: errors);

            if (area.Vector is { } vector) {
                if (area.Acceleration is not (null or WorldGravityAreaAcceleration.Directional)) {
                    errors.Add(item: $"{path}.vector supplies a directional area's vector; this area's acceleration is not directional.");
                }

                RequireGravityScalarRow(name: vector.X, path: $"{path}.vector.x", state: state, errors: errors);
                RequireGravityScalarRow(name: vector.Y, path: $"{path}.vector.y", state: state, errors: errors);
                RequireGravityScalarRow(name: vector.Z, path: $"{path}.vector.z", state: state, errors: errors);
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

        RequireRange(value: bodyContacts.ContactHertz, min: 0f, max: ((float)FixedQ4816.MaxValue),
            maxExclusive: true, name: "collision.bodyContacts.contactHertz", errors: errors);
        RequireRange(value: bodyContacts.ContactDampingRatio, min: 0f, max: ((float)FixedQ4816.MaxValue),
            maxExclusive: true, name: "collision.bodyContacts.contactDampingRatio", errors: errors);
        RequireRange(value: bodyContacts.ContactSpeed, min: 0f, max: ((float)FixedQ4816.MaxValue),
            maxExclusive: true, name: "collision.bodyContacts.contactSpeed", errors: errors);

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
            value: bodyContacts.RigidRestSpeed,
            name: "collision.bodyContacts.rigidRestSpeed",
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
                        name: new Subject(Member: ".region.radius", Path: path),
                        errors: errors
                    );
                } else {
                    RequirePositive(
                        value: disc.Radius,
                        name: new Subject(Member: ".region.radius", Path: path),
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
                    name: new Subject(Member: ".region.halfExtent", Path: path),
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
                if (
                    (noise.Octaves < 1) ||
                    (noise.Octaves > 4)
                ) {
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
                if (
                    (scatter.Radius < 1) ||
                    ((2 * scatter.Radius) > scatter.Spacing)
                ) {
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
    private static void ValidateHud(WorldDefinition definition, HashSet<string> cameras, WorldHudSection hud, IReadOnlyDictionary<string, WorldStateRow> stateRows, bool isIdentityScope, List<string> errors, bool delivered = false) {
        if (hud is null) {
            errors.Add(item: "hud is required.");

            return;
        }

        ValidateHudCore(
            cameras: cameras,
            definition: definition,
            delivered: delivered,
            errors: errors,
            hud: hud,
            isIdentityScope: isIdentityScope,
            stateRows: stateRows
        );
    }
    private static void ValidateHudCore(WorldDefinition definition, HashSet<string> cameras, WorldHudSection hud, IReadOnlyDictionary<string, WorldStateRow> stateRows, bool isIdentityScope, List<string> errors, bool delivered) {
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
            definition: (isIdentityScope
            ? null
            : definition),
            errors: errors,
            path: "hud.defaults.visible",
            predicate: hud.Defaults?.Visible,
            stateRows: stateRows
        );
        ValidateHudVisible(
            definition: (isIdentityScope
            ? null
            : definition),
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
                definition: (isIdentityScope
                ? null
                : definition),
                errors: errors,
                path: $"{panelPath}.visible",
                predicate: panel.Visible,
                stateRows: stateRows
            );

            foreach (var element in panel.Elements) {
                if (element.Voice is { } voice) {
                    var voicePath = $"{panelPath}.elements['{element.Id}'].voice";

                    if (element.Kind != WorldHudElementKind.Text) {
                        errors.Add(item: $"{voicePath} is only available on Text elements.");
                    }
                    ValidateVoiceProfile(voice, definition.Patches.Select(selector: row => row.Name).ToHashSet(comparer: StringComparer.Ordinal), voicePath, true, errors, definition.DocumentDirectory, delivered);
                }
                if (element.Kind != WorldHudElementKind.Frame) {
                    continue;
                }

                // Every candidate counts: a ranked element may show any of them, and the overlay's slot table is
                // sized for the distinct sources a section can reach, not the ones winning this frame.
                foreach (var candidate in element.FrameCandidates) {
                    if (
                        (candidate?.Source is not { } source) ||
                        !frameSources.Add(item: JsonSerializer.Serialize(
                        value: source,
                        jsonTypeInfo: WorldJsonContext.Default.WorldFrameSource
                    ))
                    ) {
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
    private static void ValidateIdentityVoiceProfile(WorldIdentityDefinition? identity, HashSet<string> patchIds, List<string> errors, string? documentDirectory, bool delivered) {
        if (identity?.Voice is not { } voice) {
            return;
        }

        ValidateVoiceProfile(delivered: delivered, documentDirectory: documentDirectory, errors: errors, patchIds: patchIds, path: "identity.voice", required: false, voice: voice);
    }
    // A delivered document's sample set is a path another process authored, which is never opened: its voice is checked
    // when its reader loads it.
    private static void ValidateVoiceProfile(WorldVoiceProfile voice, HashSet<string> patchIds, string path, bool required, List<string> errors, string? documentDirectory, bool delivered) {
        if (required && (((voice.PatchId is null) && (voice.SampleSet is null)) || (voice.CadenceTicks is null))) {
            errors.Add(item: $"{path} requires patchId or sampleSet, and cadenceTicks.");
        }
        if ((voice.Hash is not null) && (voice.SampleSet is null)) {
            errors.Add(item: $"{path}.hash requires sampleSet.");
        }
        if (voice.SampleSet is { } sampleSet) {
            if (voice.PatchId is not null) { errors.Add(item: $"{path} must choose patchId or sampleSet, not both."); }
            if ((voice.Hash is { } voiceHash) && !WorldAssetRowLoader.TryPin(hash: voiceHash, pin: out _)) {
                errors.Add(item: $"{path}.hash '{voiceHash}' is not a valid SHA-256 content pin.");
            } else if (delivered) {
                // A delivered voice is read from the content its pin names; one with no pin travels without its samples.
                if ((voice.Hash is { } deliveredHash) && (WorldVoiceSampleLoader.ValidateDelivered(source: sampleSet, hash: deliveredHash) is { } deliveredError)) {
                    errors.Add(item: $"{path}: {deliveredError}");
                }
            } else if (!WorldVoiceSampleLoader.TryLoad(documentDirectory: documentDirectory, error: out var sampleError, hash: voice.Hash, rate: out _, samples: out _, source: sampleSet)) {
                errors.Add(item: $"{path}: {sampleError}");
            }
        }
        if (
            (voice.PatchId is { } patchId) &&
            (string.IsNullOrWhiteSpace(value: patchId) || !patchIds.Contains(item: patchId))
        ) {
            errors.Add(item: $"{path}.patchId '{patchId}' names no patch row.");
        }

        if (
            (voice.CadenceTicks is { } cadenceTicks) &&
            ((cadenceTicks <= 0) || (required && (cadenceTicks > 240)))
        ) {
            errors.Add(item: $"{path}.cadenceTicks {cadenceTicks} must be {(required ? "in [1, 240]" : "positive")}.");
        }
        if ((voice.PitchThousandths is < 250 or > 4000) || (voice.FormantThousandths is < 250 or > 4000) || (voice.PaceThousandths is < 250 or > 4000)) {
            errors.Add(item: $"{path} pitchThousandths, formantThousandths and paceThousandths must each be in [250, 4000].");
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

        if (!VectorFunctions.IsFinite(vector: lattice.StepA)) {
            errors.Add(item: $"{path}.stepA must contain finite coordinates.");
        }

        if (!VectorFunctions.IsFinite(vector: lattice.StepB)) {
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
            (CreationStampSampling.NoiseInstanceCeiling(
            depth: depth,
            width: width
        ) > SdfProgramBuilder.MaxInstances)
        ) {
            errors.Add(item: $"{path} width x depth ({width}x{depth}) worst-case exceeds the {SdfProgramBuilder.MaxInstances}-instance engine ceiling.");
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

        if (!WorldUpdateDefaults.IsConfinedCachePath(path: update.CacheRoot)) {
            errors.Add(item: "update.cacheRoot must be a portable relative subdirectory without parent or dot segments; absolute install roots belong in trusted local update configuration.");
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
            path: "seatDefaults.seatCameraFeel",
            errors: errors
        );

        ValidateIdentitySeeds(
            errors: errors,
            identities: defaults.Identities
        );

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
            errors.Add(item: $"bodies.seatSpawns must contain between 1 and {localSeats} spawn-point names: one for each local seat, the last name serving every seat past the list.");

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
    // a positive divisor of the fixed 50400 engine-tick base (FixedTickConversion.TicksPerSecond), so FixedTickConversion.PerRate always
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

        if (!FixedTickConversion.IsRate(ratePerSecond: ((ulong)rate))) {
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

            if (!VectorFunctions.IsFinite(vector: spawn.Position)) {
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
    // The storage host-section: a user-id must be non-empty when present. Null (WorldStorageDefaults.None) is the
    // built-in: identity declined. The store is deployment configuration and never reaches the document.
    private static void ValidateStorage(WorldStorageDefaults storage, List<string> errors) {
        if (storage is null) {
            errors.Add(item: "storage is required.");

            return;
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
                message: string.Join(
                    separator: " ",
                    values: predicateErrors
                ),
                reason: HudRefusal.VisiblePredicateInvalid
            );
        }
    }
}
