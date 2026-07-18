using Microsoft.Extensions.DependencyInjection;
using Puck.Storage.DependencyInjection;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// Composition-root wiring for the world's player-profile persistence. Registers the routed storage core, the
/// fixed-address profile store, and the live <see cref="WorldProfiles"/> catalog — loading the document once at startup
/// (blocking), reseeding a version mismatch in place, and falling back to the built-in default on any load failure
/// (naming the file to fix or delete on stderr).
/// </summary>
internal static class WorldProfileRegistration {
    /// <summary>Registers the storage core, the profile store, and the live profile catalog.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddWorldProfiles(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        // The routed blob store (local-file + Azure backends behind one seam) and the fixed-address profile store.
        PuckStorageServiceRegistration.AddCore(services: services);
        services.AddSingleton<WorldProfileStore>();
        // The live catalog, loaded once at startup. A stored document from an older schema is reseeded in place (the
        // on-disk file must never lie about what a profile means); a malformed document falls back to the built-in
        // default rather than taking the game down, naming the file to fix or delete.
        services.AddSingleton(implementationFactory: static serviceProvider => {
            var store = serviceProvider.GetRequiredService<WorldProfileStore>();
            var @default = WorldPlayerDocument.BuildDefault();

            try {
                var load = store.LoadOrMigrateAsync(@default: @default).AsTask().GetAwaiter().GetResult();
                var document = load.Document;
                var lastUsedId = load.LastUsedId;
                var lastSyncedRevision = load.LastSyncedRevision;
                var versionToken = load.VersionToken;

                if (!string.Equals(a: document.Schema, b: @default.Schema, comparisonType: StringComparison.Ordinal)) {
                    Console.Error.WriteLine(value: $"[profiles] stored document is {document.Schema}; reseeding as {@default.Schema} (customizations reset) at {store.FilePath}.");
                    versionToken = store.SaveAsync(document: @default).AsTask().GetAwaiter().GetResult().VersionToken;
                    document = @default;
                    lastUsedId = @default.Profiles[0].Id;
                }

                // The one thick gate: a malformed catalog (bad schema/ids/names/colors/speeds or a binding section that
                // does not compile) falls back to the built-in default rather than taking the game down.
                if (!WorldPlayerDocumentValidator.TryValidate(document: document, reason: out var reason)) {
                    Console.Error.WriteLine(value: $"[profiles] stored document rejected ({reason}); using the built-in default. Fix or delete {store.FilePath} to re-seed.");
                    document = @default;
                    lastUsedId = @default.Profiles[0].Id;
                }

                Console.Error.WriteLine(value: $"[profiles] catalog loaded ({document.Profiles.Count} profiles, revision {document.Revision}, boot={lastUsedId}); edit {store.FilePath} to customize.");

                return new WorldProfiles(document: document, lastUsedId: lastUsedId, lastSyncedRevision: lastSyncedRevision, versionToken: versionToken, store: store);
            } catch (Exception exception) {
                // A malformed or unreadable document must never take the world down — fall back to the built-in
                // default and tell the player which file to fix (or delete, to re-seed).
                Console.Error.WriteLine(value: $"[profiles] stored document rejected ({exception.Message}); using the built-in default. Fix or delete {store.FilePath} to re-seed.");

                return new WorldProfiles(document: @default, lastUsedId: @default.Profiles[0].Id, lastSyncedRevision: 0L, versionToken: null, store: store);
            }
        });

        return services;
    }
}
