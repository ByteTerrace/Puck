using Puck.Storage;

namespace Puck.World.Server;

/// <summary>The outcome of loading the player catalog: the assembled document, the machine-local boot seat, the last
/// -synced revision cursor, and the catalog blob's storage version token (the clobber-guard input <c>storage.status</c>
/// echoes).</summary>
/// <param name="Document">The loaded/migrated/seeded player document.</param>
/// <param name="LastUsedId">The machine-local boot-seat profile id.</param>
/// <param name="LastSyncedRevision">The last-synced revision cursor from the local sidecar (0 with no cloud wired).</param>
/// <param name="VersionToken">The catalog blob's version token, or <see langword="null"/> when unavailable.</param>
internal readonly record struct WorldPlayerLoad(WorldPlayerDocument Document, string LastUsedId, long LastSyncedRevision, string? VersionToken);

/// <summary>
/// Loads and saves the world's <see cref="WorldPlayerDocument"/> through <see cref="IJsonObjectBlobStore"/> (a
/// local-file target under the user's application-data directory) as a PER-PROFILE blob layout: a catalog blob
/// at <c>world/player.json</c> plus one <c>world/profiles/&lt;id&gt;.json</c> per profile — the same address model the
/// cloud uses, so two devices editing DIFFERENT profiles are independent. The machine-local <c>world/local.json</c>
/// sidecar (boot seating + sync cursor, which must NOT roam) sits beside it. A one-time migration absorbs BOTH the
/// pre-split single-file layout (<c>profiles/player.json</c>) and the discontinued <c>puck.world.profiles.v1</c>
/// catalog (<c>profiles/profiles.json</c>), reading each superseded blob once, writing the split layout, then
/// deleting its key (loud line). The routed store carries an Azure backend behind the same seam.
/// </summary>
internal sealed class WorldProfileStore {
    // Blob-storage identity of the local player catalog; the files land under <BasePath>/<LocalProfilesId>/world/.
    private static readonly Guid LocalProfilesId = new(g: "b1d5c0de-0002-4000-8000-000000000001");
    // The per-profile split layout: a catalog blob plus one blob per profile, and the machine-local sidecar.
    private static readonly ObjectBlobAddress CatalogAddress = new(Key: "world/player.json", ObjectId: LocalProfilesId);
    private static readonly ObjectBlobAddress LocalAddress = new(Key: "world/local.json", ObjectId: LocalProfilesId);
    // The pre-split single-file layouts, each read once by the migrator then deleted (not compat paths).
    private static readonly ObjectBlobAddress PhaseThreeDocAddress = new(Key: "profiles/player.json", ObjectId: LocalProfilesId);
    private static readonly ObjectBlobAddress PhaseThreeLocalAddress = new(Key: "profiles/local.json", ObjectId: LocalProfilesId);
    private static readonly ObjectBlobAddress LegacyAddress = new(Key: "profiles/profiles.json", ObjectId: LocalProfilesId);
    private readonly IJsonObjectBlobStore m_store;
    private readonly ProfileDocumentStore<WorldPlayerLocal> m_local;
    private readonly LocalFileObjectStorageTarget m_target;

    // The discontinued puck.world.profiles.v1 on-disk shape — read ONLY by the migrator (not a compat path; it is
    // deleted after the one-time read). Carries Name/Color/MoveSpeed/TurnSpeed/InvertLookX per profile.
    private sealed record LegacyDocument(string Version, string LastUsed, IReadOnlyList<LegacyProfile> Profiles);
    private sealed record LegacyProfile(string Name, string Color, float MoveSpeed = 4f, float TurnSpeed = 2.5f, bool InvertLookX = false);

    /// <summary>Initializes a new instance of the <see cref="WorldProfileStore"/> class.</summary>
    /// <param name="store">The JSON blob store to persist through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public WorldProfileStore(IJsonObjectBlobStore store) {
        ArgumentNullException.ThrowIfNull(argument: store);

        m_store = store;
        m_target = new LocalFileObjectStorageTarget(BasePath: Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData), path2: "Puck", path3: "World"));
        m_local = new ProfileDocumentStore<WorldPlayerLocal>(store: store, target: m_target, address: LocalAddress);
    }

    /// <summary>Gets the on-disk path of the catalog blob (for diagnostics — "edit this file").</summary>
    public string FilePath => PathOf(address: CatalogAddress);

    /// <summary>Loads the player document, seeding the default on first run and migrating a pre-split single-file layout
    /// once (read it → write the split layout → delete it, loud note). Assembles the document from the catalog blob and
    /// its per-profile blobs.</summary>
    /// <param name="default">The built-in default document to seed when nothing exists.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The loaded/migrated/seeded document, the boot-seat id, the sync cursor, and the catalog version token.</returns>
    public async ValueTask<WorldPlayerLoad> LoadOrMigrateAsync(WorldPlayerDocument @default, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: @default);

        // 1. The current split layout: a catalog blob exists → assemble the document from it and its per-profile blobs.
        var catalog = await m_store.ReadAsync<WorldPlayerCatalog>(address: CatalogAddress, cancellationToken: cancellationToken, target: m_target);

        if (catalog is { Found: true, Value: { } catalogValue }) {
            var document = await ComposeAsync(catalog: catalogValue, cancellationToken: cancellationToken);
            var (lastUsedId, lastSynced) = await ResolveLocalAsync(document: document, cancellationToken: cancellationToken);

            return new WorldPlayerLoad(Document: document, LastUsedId: lastUsedId, LastSyncedRevision: lastSynced, VersionToken: catalog.VersionToken);
        }

        // 2. The pre-split single-file layout: a whole document at profiles/player.json → split it into the new layout.
        var phaseThree = await m_store.ReadAsync<WorldPlayerDocument>(address: PhaseThreeDocAddress, cancellationToken: cancellationToken, target: m_target);

        if (phaseThree is { Found: true, Value: { Profiles: not null } single }) {
            var normalized = NormalizeStamp(document: single);

            if (WorldPlayerDocumentValidator.TryValidate(document: normalized, reason: out var reason)) {
                var token = await WriteSplitAsync(document: normalized, mode: ObjectBlobWriteMode.Overwrite, cancellationToken: cancellationToken);
                var (lastUsedId, lastSynced) = await ResolveLocalAsync(document: normalized, cancellationToken: cancellationToken);

                DeleteFile(address: PhaseThreeDocAddress);
                Console.Error.WriteLine(value: $"[profiles] migrated the Phase 3 single-file layout ({normalized.Profiles.Count} profile(s)) to the per-profile layout at {FilePath}; profiles/player.json was deleted.");

                return new WorldPlayerLoad(Document: normalized, LastUsedId: lastUsedId, LastSyncedRevision: lastSynced, VersionToken: token);
            }

            Console.Error.WriteLine(value: $"[profiles] the Phase 3 profiles/player.json did not migrate cleanly ({reason}); seeding the built-in default and leaving the old file in place for inspection.");
        }

        // 3. The discontinued puck.world.profiles.v1 catalog.
        var legacy = await m_store.ReadAsync<LegacyDocument>(address: LegacyAddress, cancellationToken: cancellationToken, target: m_target);

        if (legacy is { Found: true, Value: { Profiles: not null } old }) {
            var migrated = Migrate(legacy: old);

            if (WorldPlayerDocumentValidator.TryValidate(document: migrated, reason: out var reason)) {
                var bootId = BootIdOf(legacy: old, migrated: migrated);
                var token = await WriteSplitAsync(document: migrated, mode: ObjectBlobWriteMode.Overwrite, cancellationToken: cancellationToken);

                _ = await m_local.SaveAsync(document: new WorldPlayerLocal(LastUsedId: bootId), cancellationToken: cancellationToken);
                DeleteFile(address: LegacyAddress);
                Console.Error.WriteLine(value: $"[profiles] migrated {old.Profiles.Count} profile(s) from the retired puck.world.profiles.v1 to {FilePath}; the old profiles.json was deleted.");

                return new WorldPlayerLoad(Document: migrated, LastUsedId: bootId, LastSyncedRevision: 0L, VersionToken: token);
            }

            Console.Error.WriteLine(value: $"[profiles] the retired puck.world.profiles.v1 file did not migrate cleanly ({reason}); seeding the built-in default and leaving the old file in place for inspection.");
        }

        // 4. Neither a stored layout nor a migratable old one: seed the default (CreateOnly) and its boot seat.
        var seededToken = await WriteSplitAsync(document: @default, mode: ObjectBlobWriteMode.CreateOnly, cancellationToken: cancellationToken);
        var seedBootId = @default.Profiles[0].Id;

        _ = await m_local.SaveAsync(document: new WorldPlayerLocal(LastUsedId: seedBootId), cancellationToken: cancellationToken);

        return new WorldPlayerLoad(Document: @default, LastUsedId: seedBootId, LastSyncedRevision: 0L, VersionToken: seededToken);
    }

    /// <summary>Saves the player document as the split layout — the catalog blob plus every per-profile blob — returning
    /// the catalog write result (the ordering anchor, carrying the new version token). Unconditional overwrite on the
    /// local backend (last-writer-wins); a cloud-backed store layers an if-match round-trip on the same seam.</summary>
    /// <param name="document">The document to persist.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The catalog blob write result.</returns>
    public ValueTask<ObjectBlobWriteResult> SaveAsync(WorldPlayerDocument document, CancellationToken cancellationToken = default) {
        return WriteSplitResultAsync(document: document, mode: ObjectBlobWriteMode.Overwrite, cancellationToken: cancellationToken);
    }

    /// <summary>Persists the machine-local sidecar (boot seating + sync cursor) synchronously; a failure is swallowed
    /// loudly (a missing sidecar just falls back to the first catalog entry and revision 0 next boot).</summary>
    /// <param name="local">The sidecar to persist.</param>
    public void SaveLocal(WorldPlayerLocal local) {
        try {
            _ = m_local.SaveAsync(document: local).AsTask().GetAwaiter().GetResult();
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[profiles] could not save the boot-seat sidecar ({exception.Message}); it stays live in memory.");
        }
    }

    // Assemble the document from the catalog blob and one per-profile blob per listed id. A listed id whose blob is
    // missing is skipped loudly (a corrupted split leaves the game playable on whatever profiles survive).
    private async ValueTask<WorldPlayerDocument> ComposeAsync(WorldPlayerCatalog catalog, CancellationToken cancellationToken) {
        var profiles = new List<WorldPlayerProfile>(capacity: catalog.ProfileIds.Count);

        foreach (var id in catalog.ProfileIds) {
            var profile = await m_store.ReadAsync<WorldPlayerProfile>(address: ProfileAddress(id: id), cancellationToken: cancellationToken, target: m_target);

            if (profile is { Found: true, Value: { } value }) {
                profiles.Add(item: value);
            } else {
                Console.Error.WriteLine(value: $"[profiles] the catalog lists profile '{id}' but its blob is missing at {PathOf(address: ProfileAddress(id: id))}; skipping it.");
            }
        }

        return new WorldPlayerDocument(Schema: catalog.Schema, Revision: catalog.Revision, UpdatedAtUtc: catalog.UpdatedAtUtc, Profiles: profiles) {
            Extensions = catalog.Extensions,
        };
    }

    // Write the split layout and return the catalog write result (the ordering anchor). The catalog carries the
    // document-level revision/stamp/extensions and the ordered id list; each profile body lands in its own blob.
    private async ValueTask<ObjectBlobWriteResult> WriteSplitResultAsync(WorldPlayerDocument document, ObjectBlobWriteMode mode, CancellationToken cancellationToken) {
        var catalog = new WorldPlayerCatalog(
            Schema: document.Schema,
            Revision: document.Revision,
            UpdatedAtUtc: document.UpdatedAtUtc,
            ProfileIds: document.Profiles.Select(selector: static profile => profile.Id).ToList()
        ) {
            Extensions = document.Extensions,
        };
        var result = await m_store.WriteAsync(address: CatalogAddress, cancellationToken: cancellationToken, mode: mode, target: m_target, value: catalog);

        // Human-cadence save of a tiny catalog (four profiles by default): writing every profile blob each save is
        // trivially cheap and keeps the on-disk split a faithful snapshot; the per-profile grain is what buys a
        // cloud-backed store's changed-only uploads, not a local write-skip.
        foreach (var profile in document.Profiles) {
            _ = await m_store.WriteAsync(address: ProfileAddress(id: profile.Id), cancellationToken: cancellationToken, mode: mode, target: m_target, value: profile);
        }

        return result;
    }

    private async ValueTask<string?> WriteSplitAsync(WorldPlayerDocument document, ObjectBlobWriteMode mode, CancellationToken cancellationToken) {
        return (await WriteSplitResultAsync(document: document, mode: mode, cancellationToken: cancellationToken)).VersionToken;
    }

    // The per-profile blob address. The profile id is a stable, filename-safe segment (the validator gates its shape);
    // it is the same key the cloud container uses.
    private static ObjectBlobAddress ProfileAddress(string id) {
        return new ObjectBlobAddress(Key: $"world/profiles/{id}.json", ObjectId: LocalProfilesId);
    }

    // The boot-seat id after a legacy migration: the legacy LastUsed name IS the migrated profile's id when it still
    // resolves, else the first migrated profile.
    private static string BootIdOf(LegacyDocument legacy, WorldPlayerDocument migrated) {
        foreach (var profile in migrated.Profiles) {
            if (string.Equals(a: profile.Id, b: legacy.LastUsed, comparisonType: StringComparison.Ordinal)) {
                return profile.Id;
            }
        }

        return migrated.Profiles[0].Id;
    }

    // Map the discontinued catalog shape to the current one: each name becomes the stable id + identity name, speeds map to Motion,
    // bindings start null (the engine default), revision starts at 1, stamp at the epoch default.
    private static WorldPlayerDocument Migrate(LegacyDocument legacy) {
        var profiles = new List<WorldPlayerProfile>(capacity: legacy.Profiles.Count);

        foreach (var profile in legacy.Profiles) {
            profiles.Add(item: new WorldPlayerProfile(
                Id: profile.Name,
                Identity: new WorldPlayerIdentity(Name: profile.Name, Color: profile.Color),
                Motion: new WorldPlayerMotion(MoveSpeed: profile.MoveSpeed, TurnSpeed: profile.TurnSpeed, InvertLookX: profile.InvertLookX)
            ));
        }

        return new WorldPlayerDocument(Schema: WorldPlayerDocument.SchemaVersion, Revision: 1L, UpdatedAtUtc: WorldPlayerDocument.DefaultUpdatedAtUtc, Profiles: profiles);
    }

    // A pre-split document deserialized from disk predates the UpdatedAtUtc field, so STJ leaves it null — coalesce it to
    // the epoch default so the migrated document validates and round-trips.
    private static WorldPlayerDocument NormalizeStamp(WorldPlayerDocument document) {
        return (string.IsNullOrWhiteSpace(value: document.UpdatedAtUtc)
            ? (document with { UpdatedAtUtc = WorldPlayerDocument.DefaultUpdatedAtUtc })
            : document);
    }

    // Resolve the boot seat and sync cursor from the local sidecar, migrating the pre-split sidecar (profiles/local.json)
    // to the current key once. An absent/empty sidecar falls back to the first catalog entry and revision 0.
    private async ValueTask<(string LastUsedId, long LastSyncedRevision)> ResolveLocalAsync(WorldPlayerDocument document, CancellationToken cancellationToken) {
        var sidecar = await m_store.ReadAsync<WorldPlayerLocal>(address: LocalAddress, cancellationToken: cancellationToken, target: m_target);

        if (sidecar is { Found: true, Value: { LastUsedId: { } id } value } && !string.IsNullOrEmpty(value: id)) {
            return (id, value.LastSyncedRevision);
        }

        // Try the pre-split sidecar once, migrate it forward, and delete its key.
        var legacySidecar = await m_store.ReadAsync<WorldPlayerLocal>(address: PhaseThreeLocalAddress, cancellationToken: cancellationToken, target: m_target);

        if (legacySidecar is { Found: true, Value: { LastUsedId: { } legacyId } legacyValue } && !string.IsNullOrEmpty(value: legacyId)) {
            _ = await m_local.SaveAsync(document: legacyValue, cancellationToken: cancellationToken);
            DeleteFile(address: PhaseThreeLocalAddress);

            return (legacyId, legacyValue.LastSyncedRevision);
        }

        var bootId = document.Profiles[0].Id;

        _ = await m_local.SaveAsync(document: new WorldPlayerLocal(LastUsedId: bootId), cancellationToken: cancellationToken);

        return (bootId, 0L);
    }

    private void DeleteFile(ObjectBlobAddress address) {
        try {
            var path = PathOf(address: address);

            if (File.Exists(path: path)) {
                File.Delete(path: path);
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[profiles] migrated the catalog but could not delete {address.Key} ({exception.Message}); delete it by hand.");
        }
    }

    private string PathOf(ObjectBlobAddress address) {
        return Path.Combine(path1: m_target.BasePath, path2: address.ObjectId.ToString(), path3: address.Key.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
    }
}
