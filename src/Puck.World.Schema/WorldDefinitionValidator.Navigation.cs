using Puck.Maths;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static HashSet<string> ValidateNavigation(WorldDefinition definition, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var domains = definition.Navigation.Rows;
        var totalCells = 0L;
        var sharedCells = 0L;
        var sharedWork = 0L;

        if (domains.Count > WorldNavigationCapacity.MaxDomains) {
            errors.Add(item: $"navigation.domains declares {domains.Count} rows; the maximum is {WorldNavigationCapacity.MaxDomains}.");
        }

        for (var index = 0; index < domains.Count; index++) {
            var domain = domains[index];
            var path = $"navigation.domains[{index}]";
            if (domain.Shared is { } sharing) {
                RequireRange(sharing.GoalCapacity, 1, WorldNavigationCapacity.MaxSharedGoals, $"{path}.shared.goalCapacity", errors);
                RequireRange(sharing.ExpandedNodesPerTick, 1, WorldNavigationCapacity.MaxSharedExpandedPerTick, $"{path}.shared.expandedNodesPerTick", errors);
                sharedWork += sharing.ExpandedNodesPerTick;
            }

            if (!SafeName.TryParse(candidate: domain.Name, name: out _, reason: out var nameReason)) {
                errors.Add(item: $"{path}.name {nameReason}");
            } else if (!names.Add(item: domain.Name)) {
                errors.Add(item: $"{path}.name '{domain.Name}' is duplicated.");
            }

            if (!float.IsFinite(f: domain.Origin.X) || !float.IsFinite(f: domain.Origin.Y) || !float.IsFinite(f: domain.Origin.Z)) {
                errors.Add(item: $"{path}.origin must be finite.");
            }
            RequirePositive(value: domain.CellSize, name: $"{path}.cellSize", errors: errors);
            RequirePositive(value: domain.AgentRadius, name: $"{path}.agentRadius", errors: errors);
            RequirePositive(value: domain.ArrivalDistance, name: $"{path}.arrivalDistance", errors: errors);
            RequireNavigationFixedPositive(value: domain.CellSize, name: $"{path}.cellSize", errors: errors);
            RequireNavigationFixedPositive(value: domain.AgentRadius, name: $"{path}.agentRadius", errors: errors);
            RequireNavigationFixedPositive(value: domain.ArrivalDistance, name: $"{path}.arrivalDistance", errors: errors);
            if (float.IsFinite(f: domain.ArrivalDistance) && float.IsFinite(f: domain.CellSize) && domain.ArrivalDistance > domain.CellSize) {
                errors.Add(item: $"{path}.arrivalDistance ({domain.ArrivalDistance}) cannot exceed cellSize ({domain.CellSize}).");
            }
            if (!Enum.IsDefined(value: domain.Kind)) {
                errors.Add(item: $"{path}.kind '{domain.Kind}' is not defined.");
            }
            if (!Enum.IsDefined(value: domain.Connectivity)) {
                errors.Add(item: $"{path}.connectivity '{domain.Connectivity}' is not defined.");
            }

            if (domain.Kind == WorldNavigationKind.Surface) {
                RequirePositive(value: domain.ProbeUp, name: $"{path}.probeUp", errors: errors);
                RequirePositive(value: domain.ProbeDown, name: $"{path}.probeDown", errors: errors);
                RequirePositive(value: domain.AgentHeight, name: $"{path}.agentHeight", errors: errors);
                RequireNavigationFixedPositive(value: domain.ProbeUp, name: $"{path}.probeUp", errors: errors);
                RequireNavigationFixedPositive(value: domain.ProbeDown, name: $"{path}.probeDown", errors: errors);
                RequireNavigationFixedPositive(value: domain.AgentHeight, name: $"{path}.agentHeight", errors: errors);
                RequireNonNegative(value: domain.MaxStepHeight, name: $"{path}.maxStepHeight", errors: errors);
                RequireRange(value: domain.MaxSlopeDegrees, min: 0f, max: 89f, name: $"{path}.maxSlopeDegrees", errors: errors);
                if (domain.Layers != 1) {
                    errors.Add(item: $"{path}.layers must be 1 for a surface domain.");
                }
                if (domain.Medium is not null) {
                    errors.Add(item: $"{path}.medium is only valid for a medium domain.");
                }
                if (float.IsFinite(f: domain.AgentHeight) && float.IsFinite(f: domain.AgentRadius) && domain.AgentHeight < (2f * domain.AgentRadius)) {
                    errors.Add(item: $"{path}.agentHeight ({domain.AgentHeight}) must be at least twice agentRadius ({domain.AgentRadius}).");
                }
                if (float.IsFinite(f: domain.AgentHeight) && float.IsFinite(f: domain.AgentRadius) && domain.AgentHeight > (32f * domain.AgentRadius)) {
                    errors.Add(item: $"{path}.agentHeight ({domain.AgentHeight}) cannot exceed 32 times agentRadius ({domain.AgentRadius}); that bounds transition clearance to {WorldNavigationCapacity.MaxSurfaceClearanceSweeps} parallel sweeps.");
                }
            } else {
                if (domain.Layers <= 0) {
                    errors.Add(item: $"{path}.layers must be positive for a volume or medium domain.");
                }
                if (domain.ProbeUp != 0f || domain.ProbeDown != 0f || domain.AgentHeight != 0f || domain.MaxStepHeight != 0f || domain.MaxSlopeDegrees != 0f) {
                    errors.Add(item: $"{path} volume domains must leave probeUp, probeDown, agentHeight, maxStepHeight, and maxSlopeDegrees at zero.");
                }
                if (domain.Kind == WorldNavigationKind.Medium) {
                    var mediumRow = definition.State.FirstOrDefault(predicate: row => string.Equals(a: row.Name, b: domain.Medium, comparisonType: StringComparison.Ordinal));
                    if (string.IsNullOrWhiteSpace(value: domain.Medium) || mediumRow?.Field?.Medium is null || mediumRow.EffectiveDomain is not WorldStateDomain.CellsOf mediumCellsOf) {
                        errors.Add(item: $"{path}.medium '{domain.Medium}' names no lattice field carrying a medium trait.");
                    } else {
                        var topology = definition.StateRaw?.Lattices?.FirstOrDefault(predicate: row => string.Equals(a: row.Name, b: mediumCellsOf.Topology, comparisonType: StringComparison.Ordinal));
                        if (topology is not null && float.IsFinite(f: domain.CellSize) && float.IsFinite(f: topology.CellSize) && domain.CellSize > (topology.CellSize * (WorldNavigationCapacity.MaxMediumSegmentSubdivisions / 2f))) {
                            errors.Add(item: $"{path}.cellSize ({domain.CellSize}) cannot exceed {WorldNavigationCapacity.MaxMediumSegmentSubdivisions / 2} times medium lattice cellSize ({topology.CellSize}); that bounds live edge containment to {WorldNavigationCapacity.MaxMediumSegmentSubdivisions} subsegments.");
                        }
                        if (topology is not null && float.IsFinite(f: domain.AgentRadius) && float.IsFinite(f: topology.CellSize) && (2f * domain.AgentRadius) > topology.CellSize) {
                            errors.Add(item: $"{path}.agentRadius ({domain.AgentRadius}) cannot exceed half medium lattice cellSize ({topology.CellSize}); that bounds whole-agent live medium containment to at most 27 voxel checks per swept subsegment.");
                        }
                    }
                } else if (domain.Medium is not null) {
                    errors.Add(item: $"{path}.medium is only valid when kind is Medium.");
                }
            }
            if (domain.Width <= 0 || domain.Depth <= 0 || domain.Layers <= 0) {
                errors.Add(item: $"{path}.width, depth, and layers must all be positive.");
                continue;
            }
            if (domain.Width > WorldNavigationCapacity.MaxCellsPerDomain || domain.Depth > WorldNavigationCapacity.MaxCellsPerDomain || domain.Layers > WorldNavigationCapacity.MaxCellsPerDomain) {
                errors.Add(item: $"{path} dimensions necessarily exceed the per-domain maximum of {WorldNavigationCapacity.MaxCellsPerDomain} cells.");
                totalCells = (WorldNavigationCapacity.MaxCellsPerWorld + 1L);
                continue;
            }

            var cells = ((long)domain.Width * domain.Depth * domain.Layers);
            totalCells += cells;
            if (domain.Shared is { GoalCapacity: > 0 and <= WorldNavigationCapacity.MaxSharedGoals } shared) {
                sharedCells += cells * shared.GoalCapacity;
            }
            if (cells > WorldNavigationCapacity.MaxCellsPerDomain) {
                errors.Add(item: $"{path} declares {cells} cells; the per-domain maximum is {WorldNavigationCapacity.MaxCellsPerDomain}.");
            }
            if (domain.MaxExpandedNodes <= 0 || domain.MaxExpandedNodes > cells) {
                errors.Add(item: $"{path}.maxExpandedNodes must be from 1 through its {cells} cells.");
            }
            if (domain.MaxPathNodes <= 0 || domain.MaxPathNodes > Math.Min(cells, WorldNavigationCapacity.MaxPathNodes)) {
                errors.Add(item: $"{path}.maxPathNodes must be from 1 through {Math.Min(cells, WorldNavigationCapacity.MaxPathNodes)}.");
            }

            if (float.IsFinite(f: domain.CellSize)) {
                var maxCoordinate = ((double)CurvatureSpline.MaxCoordinate);
                var maxX = ((double)domain.Origin.X + ((double)domain.CellSize * (domain.Width - 1L)));
                var maxY = ((double)domain.Origin.Y + ((double)domain.CellSize * (domain.Layers - 1L)));
                var maxZ = ((double)domain.Origin.Z + ((double)domain.CellSize * (domain.Depth - 1L)));
                if (Math.Abs(value: domain.Origin.X) > maxCoordinate || Math.Abs(value: domain.Origin.Y) > maxCoordinate || Math.Abs(value: domain.Origin.Z) > maxCoordinate ||
                    Math.Abs(value: maxX) > maxCoordinate || Math.Abs(value: maxY) > maxCoordinate || Math.Abs(value: maxZ) > maxCoordinate) {
                    errors.Add(item: $"{path} grid centers must remain within ±{CurvatureSpline.MaxCoordinate} world units after fixed-point compilation.");
                }
            }
        }

        if (totalCells > WorldNavigationCapacity.MaxCellsPerWorld) {
            errors.Add(item: $"navigation.domains declares {totalCells} total cells; the world maximum is {WorldNavigationCapacity.MaxCellsPerWorld}.");
        }
        if (sharedCells > WorldNavigationCapacity.MaxSharedCellsPerWorld) {
            errors.Add($"navigation shared trees require {sharedCells} cells; the world maximum is {WorldNavigationCapacity.MaxSharedCellsPerWorld}.");
        }
        if (sharedWork > WorldNavigationCapacity.MaxSharedExpandedPerTick) {
            errors.Add($"navigation shared trees declare {sharedWork} expansions per tick; the world maximum is {WorldNavigationCapacity.MaxSharedExpandedPerTick}.");
        }
        return names;
    }

    private static void RequireNavigationFixedPositive(float value, string name, List<string> errors) {
        if (float.IsFinite(f: value) && value > 0f && FixedQ4816.FromDouble(value: value) <= FixedQ4816.Zero) {
            errors.Add(item: $"{name} {value} is positive but quantizes to zero in Q48.16.");
        }
    }
}
