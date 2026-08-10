namespace Puck.World;

/// <summary>Load-time, one-shot normalizations applied to a freshly parsed document before
/// <see cref="WorldDefinitionValidator"/> ever sees it — never a read-side tolerance the validator itself carries,
/// per the supergreen "migrate once" doctrine (no compat aliases, no lingering leniency, no read-side tolerance for
/// a retired shape). Each migration targets a document shape that predates a field the validator now requires, and
/// rewrites it to a deterministic value so the document is thereafter well-formed on every subsequent load, save,
/// and validate — indistinguishable from a document authored today. Called from every place a document's raw bytes
/// become a live <see cref="WorldDefinition"/>: <see cref="WorldDefinitionFileSource.TryLoad"/> (every file load —
/// boot, <c>world.load</c>/<c>world.reload</c>, an owned world's own resume) and
/// <see cref="WorldDefinitionSerialization.Deserialize"/> (a replay tape's embedded document rehydration) — so a
/// stale save migrates exactly once, at the moment it first becomes live, never on every read thereafter.</summary>
public static class WorldDefinitionMigrations {
    /// <summary>Applies every load-time migration to <paramref name="definition"/>, returning the same reference
    /// when none apply — a document already in today's shape allocates nothing here.</summary>
    /// <param name="definition">The freshly parsed document, not yet validated.</param>
    /// <returns>The migrated document, ready for <see cref="WorldDefinitionValidator"/>.</returns>
    public static WorldDefinition Apply(WorldDefinition definition) {
        return StampTerminalListingResolvedTicks(definition: definition);
    }

    /// <summary>Stamps <see cref="WorldMarketListing.ResolvedTick"/> on every terminal
    /// (<see cref="WorldMarketListingStatus.Settled"/>/<see cref="WorldMarketListingStatus.Cancelled"/>/
    /// <see cref="WorldMarketListingStatus.Expired"/>) listing that carries none — the shape every terminal listing
    /// had before this field existed, which <see cref="WorldDefinitionValidator"/>'s
    /// "resolvedTick set exactly when not active" invariant now refuses outright. The listing's true resolution tick
    /// is unrecoverable from the document alone, so zero — the earliest possible tick — is the deterministic
    /// fallback age basis: it makes the row immediately eligible for the engine's own per-tick retention sweep
    /// (<c>Server.WorldServer</c>'s <c>PruneMarketListings</c> compose arm) the moment the world starts ticking,
    /// rather than pinning an unaged row in place under an invented resolution time. The validator invariant stays
    /// strict; this is the one place a pre-field document gets rewritten into the shape it has always required.</summary>
    /// <param name="definition">The freshly parsed document, not yet validated.</param>
    /// <returns>The migrated document, or the same reference when no terminal listing needed stamping.</returns>
    private static WorldDefinition StampTerminalListingResolvedTicks(WorldDefinition definition) {
        if ((definition.Market?.Listings) is not { Count: > 0 } listings) {
            return definition;
        }

        List<WorldMarketListing>? migrated = null;

        for (var index = 0; (index < listings.Count); index++) {
            var listing = listings[index];

            if ((listing.Status != WorldMarketListingStatus.Active) && (listing.ResolvedTick is null)) {
                migrated ??= new List<WorldMarketListing>(collection: listings);
                migrated[index] = (listing with { ResolvedTick = 0L });
            }
        }

        return (migrated is null ? definition : (definition with { Market = (definition.Market! with { Listings = migrated }) }));
    }
}
