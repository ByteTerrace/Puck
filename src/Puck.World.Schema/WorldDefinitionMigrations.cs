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
    /// when none apply — a document already in today's shape allocates nothing here. No migration is currently
    /// registered; this is the hook the next one attaches to.</summary>
    /// <param name="definition">The freshly parsed document, not yet validated.</param>
    /// <returns>The migrated document, ready for <see cref="WorldDefinitionValidator"/>.</returns>
    public static WorldDefinition Apply(WorldDefinition definition) {
        return definition;
    }
}
