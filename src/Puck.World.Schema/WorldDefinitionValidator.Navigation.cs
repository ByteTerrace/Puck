using Puck.Maths;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Returns the domain names a navigated target or flock may name and the cost profile names a navigated target may
    // select.
    private static (HashSet<string> Domains, HashSet<string> Profiles) ValidateNavigation(WorldDefinition definition, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var domains = definition.Navigation.Rows;
        var totalCells = 0L;

        if (domains.Count > WorldNavigationCapacity.MaxDomains) {
            errors.Add(item: $"navigation.domains declares {domains.Count} rows; the maximum is {WorldNavigationCapacity.MaxDomains}.");
        }

        for (var index = 0; (index < domains.Count); index++) {
            var domain = domains[index];
            var path = $"navigation.domains[{index}]";

            if (domain.Parent is { } parentId) {
                var placementsById = WorldPlacementIndex.ById(placements: definition.Placements);
                var parent = placementsById.GetValueOrDefault(key: parentId);
                var remaining = (definition.Placements.Count + 1);

                while (
                    (parent is not null) &&
                    (remaining-- > 0) &&
                    (parent.Scale == 1f) &&
                    (parent.Attach is null) &&
                    (parent.Inhabit is null) &&
                    (parent.Mirror is null) &&
                    ((parent.Distribution is null) || (parent.Deal is not null)) &&
                    (parent.Parent is { } ancestor)
                ) {
                    parent = placementsById.GetValueOrDefault(key: ancestor);
                }
                if (
                    (parent is null) ||
                    (parent.Scale != 1f) ||
                    (parent.Attach is not null) ||
                    (parent.Inhabit is not null) ||
                    (parent.Mirror is not null) ||
                    ((parent.Distribution is not null) && (parent.Deal is null)) ||
                    (remaining <= 0)
                ) {
                    errors.Add(item: $"{path}.parent '{parentId}' must name a static, unit-scale placement with one frame.");
                }
            }
            if ((domain.Network is { } network) && !SafeName.TryParse(candidate: network, name: out _, reason: out var networkReason)) {
                errors.Add(item: $"{path}.network {networkReason}");
            }

            if (!SafeName.TryParse(
                candidate: domain.Name,
                name: out _,
                reason: out var nameReason
            )) {
                errors.Add(item: $"{path}.name {nameReason}");
            } else if (!names.Add(item: domain.Name)) {
                errors.Add(item: $"{path}.name '{domain.Name}' is duplicated.");
            }

            if (!VectorFunctions.IsFinite(vector: domain.Origin)) {
                errors.Add(item: $"{path}.origin must be finite.");
            }
            RequirePositive(
                value: domain.CellSize,
                name: new Subject(Member: ".cellSize", Path: path),
                errors: errors
            );
            RequirePositive(
                value: domain.AgentRadius,
                name: new Subject(Member: ".agentRadius", Path: path),
                errors: errors
            );
            RequirePositive(
                value: domain.ArrivalDistance,
                name: new Subject(Member: ".arrivalDistance", Path: path),
                errors: errors
            );
            RequireQuantizesNonZero(
                value: domain.CellSize,
                name: new Subject(Member: ".cellSize", Path: path),
                errors: errors
            );
            RequireQuantizesNonZero(
                value: domain.AgentRadius,
                name: new Subject(Member: ".agentRadius", Path: path),
                errors: errors
            );
            RequireQuantizesNonZero(
                value: domain.ArrivalDistance,
                name: new Subject(Member: ".arrivalDistance", Path: path),
                errors: errors
            );
            if (
                float.IsFinite(f: domain.ArrivalDistance) &&
                float.IsFinite(f: domain.CellSize) &&
                (domain.ArrivalDistance > domain.CellSize)
            ) {
                errors.Add(item: $"{path}.arrivalDistance ({domain.ArrivalDistance}) cannot exceed cellSize ({domain.CellSize}).");
            }
            if (!Enum.IsDefined(value: domain.Kind)) {
                errors.Add(item: $"{path}.kind '{domain.Kind}' is not defined.");
            }
            if (!Enum.IsDefined(value: domain.Connectivity)) {
                errors.Add(item: $"{path}.connectivity '{domain.Connectivity}' is not defined.");
            }

            if (domain.Kind == WorldNavigationKind.Surface) {
                RequirePositive(
                    value: domain.ProbeUp,
                    name: new Subject(Member: ".probeUp", Path: path),
                    errors: errors
                );
                RequirePositive(
                    value: domain.ProbeDown,
                    name: new Subject(Member: ".probeDown", Path: path),
                    errors: errors
                );
                RequirePositive(
                    value: domain.AgentHeight,
                    name: new Subject(Member: ".agentHeight", Path: path),
                    errors: errors
                );
                RequireQuantizesNonZero(
                    value: domain.ProbeUp,
                    name: new Subject(Member: ".probeUp", Path: path),
                    errors: errors
                );
                RequireQuantizesNonZero(
                    value: domain.ProbeDown,
                    name: new Subject(Member: ".probeDown", Path: path),
                    errors: errors
                );
                RequireQuantizesNonZero(
                    value: domain.AgentHeight,
                    name: new Subject(Member: ".agentHeight", Path: path),
                    errors: errors
                );
                RequireNonNegative(
                    value: domain.MaxStepHeight,
                    name: new Subject(Member: ".maxStepHeight", Path: path),
                    errors: errors
                );
                RequireRange(
                    value: domain.MaxSlopeDegrees,
                    min: 0f,
                    max: 89f,
                    name: new Subject(Member: ".maxSlopeDegrees", Path: path),
                    errors: errors
                );
                if (domain.Layers != 1) {
                    errors.Add(item: $"{path}.layers must be 1 for a surface domain.");
                }
                if (domain.Medium is not null) {
                    errors.Add(item: $"{path}.medium is only valid for a medium domain.");
                }
                if (
                    float.IsFinite(f: domain.AgentHeight) &&
                    float.IsFinite(f: domain.AgentRadius) &&
                    (domain.AgentHeight < (2f * domain.AgentRadius))
                ) {
                    errors.Add(item: $"{path}.agentHeight ({domain.AgentHeight}) must be at least twice agentRadius ({domain.AgentRadius}).");
                }
                if (
                    float.IsFinite(f: domain.AgentHeight) &&
                    float.IsFinite(f: domain.AgentRadius) &&
                    (domain.AgentHeight > (32f * domain.AgentRadius))
                ) {
                    errors.Add(item: $"{path}.agentHeight ({domain.AgentHeight}) cannot exceed 32 times agentRadius ({domain.AgentRadius}); that keeps the agent's capsule within the field contact sweep's {Puck.Physics.FixedFieldContactSolver.MaximumCapsuleSweepPieces} core pieces.");
                }
            } else {
                if (domain.Layers <= 0) {
                    errors.Add(item: $"{path}.layers must be positive for a volume or medium domain.");
                }
                if (
                    (domain.ProbeUp != 0f) ||
                    (domain.ProbeDown != 0f) ||
                    (domain.AgentHeight != 0f) ||
                    (domain.MaxStepHeight != 0f) ||
                    (domain.MaxSlopeDegrees != 0f)
                ) {
                    errors.Add(item: $"{path} volume domains must leave probeUp, probeDown, agentHeight, maxStepHeight, and maxSlopeDegrees at zero.");
                }
                if (domain.Kind == WorldNavigationKind.Medium) {
                    var mediumRow = definition.State.FirstOrDefault(predicate: row => string.Equals(
                        a: row.Name,
                        b: domain.Medium,
                        comparisonType: StringComparison.Ordinal
                    ));

                    if (
                        string.IsNullOrWhiteSpace(value: domain.Medium) ||
                        (mediumRow?.Field?.Medium is null) ||
                        (mediumRow.EffectiveDomain is not StateDomain.CellsOf mediumCellsOf)
                    ) {
                        errors.Add(item: $"{path}.medium '{domain.Medium}' names no lattice field carrying a medium trait.");
                    } else {
                        var topology = definition.StateRaw?.Lattices?.FirstOrDefault(predicate: row => string.Equals(
                            a: row.Name,
                            b: mediumCellsOf.Topology,
                            comparisonType: StringComparison.Ordinal
                        ));

                        if (
                            (topology is not null) &&
                            float.IsFinite(f: domain.CellSize) &&
                            float.IsFinite(f: topology.CellSize) &&
                            (domain.CellSize > (topology.CellSize * (WorldNavigationCapacity.MaxMediumSegmentSubdivisions / 2f)))
                        ) {
                            errors.Add(item: $"{path}.cellSize ({domain.CellSize}) cannot exceed {(WorldNavigationCapacity.MaxMediumSegmentSubdivisions / 2)} times medium lattice cellSize ({topology.CellSize}); that bounds live edge containment to {WorldNavigationCapacity.MaxMediumSegmentSubdivisions} subsegments.");
                        }
                        if (
                            (topology is not null) &&
                            float.IsFinite(f: domain.AgentRadius) &&
                            float.IsFinite(f: topology.CellSize) &&
                            ((2f * domain.AgentRadius) > topology.CellSize)
                        ) {
                            errors.Add(item: $"{path}.agentRadius ({domain.AgentRadius}) cannot exceed half medium lattice cellSize ({topology.CellSize}); that bounds whole-agent live medium containment to at most 27 voxel checks per swept subsegment.");
                        }
                    }
                } else if (domain.Medium is not null) {
                    errors.Add(item: $"{path}.medium is only valid when kind is Medium.");
                }
            }
            if (
                (domain.Width <= 0) ||
                (domain.Depth <= 0) ||
                (domain.Layers <= 0)
            ) {
                errors.Add(item: $"{path}.width, depth, and layers must all be positive.");
                continue;
            }
            if (
                (domain.Width > WorldNavigationCapacity.MaxCellsPerDomain) ||
                (domain.Depth > WorldNavigationCapacity.MaxCellsPerDomain) ||
                (domain.Layers > WorldNavigationCapacity.MaxCellsPerDomain)
            ) {
                errors.Add(item: $"{path} dimensions necessarily exceed the per-domain maximum of {WorldNavigationCapacity.MaxCellsPerDomain} cells.");
                totalCells = (WorldNavigationCapacity.MaxCellsPerWorld + 1L);
                continue;
            }

            var cells = ((((long)domain.Width) * domain.Depth) * domain.Layers);

            totalCells += cells;
            if (cells > WorldNavigationCapacity.MaxCellsPerDomain) {
                errors.Add(item: $"{path} declares {cells} cells; the per-domain maximum is {WorldNavigationCapacity.MaxCellsPerDomain}.");
            }
            if (
                (domain.MaxExpandedNodes <= 0) ||
                (domain.MaxExpandedNodes > cells)
            ) {
                errors.Add(item: $"{path}.maxExpandedNodes must be from 1 through its {cells} cells.");
            }
            if (
                (domain.MaxPathNodes <= 0) ||
                (domain.MaxPathNodes > Math.Min(
                val1: cells,
                val2: WorldNavigationCapacity.MaxPathNodes
            ))
            ) {
                errors.Add(item: $"{path}.maxPathNodes must be from 1 through {Math.Min(
                    val1: cells,
                    val2: WorldNavigationCapacity.MaxPathNodes
                )}.");
            }

            if (float.IsFinite(f: domain.CellSize)) {
                var maxCoordinate = ((double)CurvatureSpline.MaxCoordinate);
                var maxX = (((double)domain.Origin.X) + (((double)domain.CellSize) * (domain.Width - 1L)));
                var maxY = (((double)domain.Origin.Y) + (((double)domain.CellSize) * (domain.Layers - 1L)));
                var maxZ = (((double)domain.Origin.Z) + (((double)domain.CellSize) * (domain.Depth - 1L)));

                if (
                    (Math.Abs(value: domain.Origin.X) > maxCoordinate) ||
                    (Math.Abs(value: domain.Origin.Y) > maxCoordinate) ||
                    (Math.Abs(value: domain.Origin.Z) > maxCoordinate) ||
                    (Math.Abs(value: maxX) > maxCoordinate) ||
                    (Math.Abs(value: maxY) > maxCoordinate) ||
                    (Math.Abs(value: maxZ) > maxCoordinate)
                ) {
                    errors.Add(item: $"{path} grid centers must remain within ±{CurvatureSpline.MaxCoordinate} world units after fixed-point compilation.");
                }
            }
        }

        if (totalCells > WorldNavigationCapacity.MaxCellsPerWorld) {
            errors.Add(item: $"navigation.domains declares {totalCells} total cells; the world maximum is {WorldNavigationCapacity.MaxCellsPerWorld}.");
        }
        return (names, ValidateNavigationAreas(definition: definition, errors: errors));
    }
    // The area types, the areas that mark them, each domain's own type and the cost profiles that price them; returns the
    // profile names a navigated target may select.
    private static HashSet<string> ValidateNavigationAreas(WorldDefinition definition, List<string> errors) {
        var section = definition.Navigation;
        var types = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (section.AreaTypeRows.Count > Puck.Physics.Navigation.NavigationAreaSet.MaxTypes) {
            errors.Add(item: $"navigation.areaTypes declares {section.AreaTypeRows.Count} types; the maximum is {Puck.Physics.Navigation.NavigationAreaSet.MaxTypes}.");
        }
        for (var index = 0; (index < section.AreaTypeRows.Count); index++) {
            var type = section.AreaTypeRows[index];

            if (!SafeName.TryParse(candidate: type, name: out _, reason: out var reason)) {
                errors.Add(item: $"navigation.areaTypes[{index}] {reason}");
            } else if (!types.Add(item: type)) {
                errors.Add(item: $"navigation.areaTypes[{index}] '{type}' is duplicated.");
            }
        }
        for (var index = 0; (index < section.Rows.Count); index++) {
            if ((section.Rows[index].Area is { } area) && !types.Contains(item: area)) {
                errors.Add(item: $"navigation.domains[{index}].area '{area}' names no navigation.areaTypes entry.");
            }
        }

        var placements = ((section.AreaRows.Count == 0) ? null : WorldPlacementIndex.ById(placements: definition.Placements));

        for (var index = 0; (index < section.AreaRows.Count); index++) {
            var area = section.AreaRows[index];
            var path = $"navigation.areas[{index}]";

            if (!types.Contains(item: area.Type)) {
                errors.Add(item: $"{path}.type '{area.Type}' names no navigation.areaTypes entry.");
            }
            if ((area.Placement is null) == (area.Region is null)) {
                errors.Add(item: $"{path} must author exactly one of placement and region.");
            }
            if (area.Region is { } region) {
                ValidateRegion(region: region, path: $"{path}.region", errors: errors);
            }
            if ((area.Placement is { } id) && (placements!.GetValueOrDefault(key: id) is not { Region: not null, Attach: null, Deal: null })) {
                errors.Add(item: $"{path}.placement '{id}' must name a placement with a region that is neither attached nor dealt.");
            }
        }

        var profiles = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < section.ProfileRows.Count); index++) {
            var profile = section.ProfileRows[index];
            var path = $"navigation.profiles[{index}]";

            if (!SafeName.TryParse(candidate: profile.Name, name: out _, reason: out var reason)) {
                errors.Add(item: $"{path}.name {reason}");
            } else if (!profiles.Add(item: profile.Name)) {
                errors.Add(item: $"{path}.name '{profile.Name}' is duplicated.");
            }
            foreach (var (type, cost) in profile.Costs ?? new Dictionary<string, float>()) {
                if (!types.Contains(item: type)) {
                    errors.Add(item: $"{path}.costs.{type} names no navigation.areaTypes entry.");
                }
                if (!float.IsFinite(f: cost) || (cost <= 0f) || (cost > WorldNavigationCapacity.MaxAreaCost) || (FixedQ4816.FromDouble(value: cost) <= FixedQ4816.Zero)) {
                    errors.Add(item: $"{path}.costs.{type} {cost} must be positive, at least one fixed-point step, and at most {WorldNavigationCapacity.MaxAreaCost}.");
                }
                if (profile.Exclude?.Contains(value: type) ?? false) {
                    errors.Add(item: $"{path}.costs.{type} prices a type the profile excludes.");
                }
            }
            foreach (var type in profile.Exclude ?? []) {
                if (!types.Contains(item: type)) {
                    errors.Add(item: $"{path}.exclude '{type}' names no navigation.areaTypes entry.");
                }
            }
        }
        return profiles;
    }
}
