namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static WorldRuleCompilation? ValidateCore(WorldDefinition definition, IWorldNeighbourResolver? neighbours, bool validateAdjacencyClaims, bool retainCompilation = false) =>
        ValidateCore(definition, neighbours, validateAdjacencyClaims, retainCompilation, throwOnErrors: true, errorSink: null, deferredSink: null);
    private static bool ValidatePoolExpansion(WorldDefinition definition, bool throwOnErrors, ICollection<string>? errorSink) {
        if (TryValidatePoolExpansion(definition: definition, reason: out var reason)) {
            return true;
        }
        if (throwOnErrors) {
            RefuseCollected(errors: [reason]);
        }
        errorSink?.Add(item: reason);
        return false;
    }
    // Expansion precedes every consumer of State. Invalid declarations must become validation refusals,
    // including on collecting APIs, rather than escaping from the expanded-row property midway through a walk.
    private static bool TryValidatePoolExpansion(WorldDefinition definition, out string reason) {
        if (definition.AuthoredState.Any(predicate: row => (row is { Generated: true }))) {
            reason = "authored state rows cannot claim the generated storage marker";
            return false;
        }
        if ((definition.StateRaw is not { Records.Count: > 0 } and not { Pools.Count: > 0 } and not { PairPools.Count: > 0 }) &&
            (definition.Identity?.Records is not { Count: > 0 }) && (definition.Properties?.Carriers is not { Count: > 0 })) {
            reason = string.Empty;
            return true;
        }
        try {
            _ = definition.StateCatalog;
            _ = definition.State;
            _ = WorldIdentityRecords.Select(document: definition);
            ValidatePoolCarriers(definition: definition);
            reason = string.Empty;
            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message;
            return false;
        }
    }
    private static void ValidatePoolCarriers(WorldDefinition definition) {
        var carriers = (definition.Properties?.Carriers ?? []);
        var dynamicPools = new HashSet<CellName>();

        foreach (var carrier in carriers) {
            if ((carrier is null) || !definition.StateCatalog.TryGetPool(name: carrier.Pool, pool: out var pool) || (pool is null)) {
                throw new InvalidOperationException(message: "pool carrier names an undeclared pool");
            }
            if (!dynamicPools.Add(item: carrier.Pool)) {
                throw new InvalidOperationException(message: $"pool carrier '{carrier.Pool}' is duplicated");
            }
            _ = WorldPoolBodyBindings.Compile(definition: definition, pool: pool);
        }

        var pools = definition.StateCatalog.Pools.Select(selector: static pool => pool.Name.Value).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var property in (definition.Properties?.Names ?? [])) {
            if (pools.Contains(item: property)) {
                throw new InvalidOperationException(message: $"property '{property}' collides with a state pool of the same name");
            }
        }
    }
}
