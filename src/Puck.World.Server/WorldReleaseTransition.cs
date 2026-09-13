namespace Puck.World.Server;

/// <summary>Describes the small, explicit authored transition allowed by the first release workflow.</summary>
public readonly record struct WorldReleaseDefinitionChange(string World, string BeforeHash, string AfterHash);

/// <summary>Checks release pairs and prepares a reversible authored definition delta.</summary>
public static class WorldReleaseTransitionPolicy {
    /// <summary>Computes the definition delta requiring package-bound qualification. Changed definitions
    /// require a metadata-aware coordinator; the runner must verify the actual metadata-only preservation rule
    /// in both directions. This manifest-level check does not authorize deployment.</summary>
    public static bool TryPrepare(WorldReleaseManifest source, WorldReleaseManifest target, out IReadOnlyList<WorldReleaseDefinitionChange> changes, out string reason) {
        if (!WorldReleaseManifest.TryValidate(source, out reason) || !WorldReleaseManifest.TryValidate(target, out reason) ||
            !WorldReleaseCompatibility.TryCheckStructuralCompatibility(source, target, out reason)) {
            changes = [];
            return false;
        }
        var result = new List<WorldReleaseDefinitionChange>();
        foreach (var world in source.Definitions.Keys.OrderBy(static value => value, StringComparer.Ordinal)) {
            var before = source.Definitions[world];
            var after = target.Definitions[world];
            if (!string.Equals(before, after, StringComparison.Ordinal)) {
                result.Add(new(world, before, after));
            }
        }
        if (result.Count != 0 && !WorldReleaseCompatibility.TryRequireMetadataCoordinator(source, target, out reason)) {
            changes = [];
            return false;
        }
        changes = result;
        reason = string.Empty;
        return true;
    }

    /// <summary>Returns the reverse of an admitted definition delta for progress-preserving rollback.</summary>
    public static IReadOnlyList<WorldReleaseDefinitionChange> Reverse(IReadOnlyList<WorldReleaseDefinitionChange> changes) {
        ArgumentNullException.ThrowIfNull(changes);
        return changes.Select(static change => new WorldReleaseDefinitionChange(change.World, change.AfterHash, change.BeforeHash)).ToArray();
    }
}
