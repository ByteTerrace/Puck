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
/// sidecar (boot seating + sync cursor, which must NOT roam) sits beside it. This is the only on-disk layout the
/// store understands: anything else is absent, and an absent catalog seeds the built-in default. The routed store
/// carries an Azure backend behind the same seam.
/// </summary>
internal sealed class WorldProfileStore {
    // Blob-storage identity of the local player catalog; the files land under <BasePath>/<LocalProfilesId>/world/.
    private static readonly Guid LocalProfilesId = new(g: "b1d5c0de-0002-4000-8000-000000000001");
    // The per-profile split layout: a catalog blob plus one blob per profile, and the machine-local sidecar.
    private static readonly ObjectBlobAddress CatalogAddress = new(Key: "world/player.json", ObjectId: LocalProfilesId);
    private static readonly ObjectBlobAddress LocalAddress = new(Key: "world/local.json", ObjectId: LocalProfilesId);
    private readonly IJsonObjectBlobStore m_store;
    private readonly ProfileDocumentStore<WorldPlayerLocal> m_local;
    private readonly LocalFileObjectStorageTarget m_target;

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

    /// <summary>Loads the player document, assembling it from the catalog blob and its per-profile blobs, and seeding
    /// the built-in default when no catalog exists.</summary>
    /// <param name="default">The built-in default document to seed when nothing exists.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The loaded or seeded document, the boot-seat id, the sync cursor, and the catalog version token.</returns>
    public async ValueTask<WorldPlayerLoad> LoadAsync(WorldPlayerDocument @default, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: @default);

        var catalog = await m_store.ReadAsync<WorldPlayerCatalog>(address: CatalogAddress, cancellationToken: cancellationToken, target: m_target);

        if (catalog is { Found: true, Value: { } catalogValue }) {
            var document = await ComposeAsync(catalog: catalogValue, cancellationToken: cancellationToken);
            var (lastUsedId, lastSynced) = await ResolveLocalAsync(document: document, cancellationToken: cancellationToken);

            return new WorldPlayerLoad(Document: document, LastUsedId: lastUsedId, LastSyncedRevision: lastSynced, VersionToken: catalog.VersionToken);
        }

        // No catalog on disk: seed the default (CreateOnly) and its boot seat.
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

    // Resolve the boot seat and sync cursor from the local sidecar. An absent/empty sidecar falls back to the first
    // catalog entry and revision 0.
    private async ValueTask<(string LastUsedId, long LastSyncedRevision)> ResolveLocalAsync(WorldPlayerDocument document, CancellationToken cancellationToken) {
        var sidecar = await m_store.ReadAsync<WorldPlayerLocal>(address: LocalAddress, cancellationToken: cancellationToken, target: m_target);

        if (sidecar is { Found: true, Value: { LastUsedId: { } id } value } && !string.IsNullOrEmpty(value: id)) {
            return (id, value.LastSyncedRevision);
        }

        var bootId = document.Profiles[0].Id;

        _ = await m_local.SaveAsync(document: new WorldPlayerLocal(LastUsedId: bootId), cancellationToken: cancellationToken);

        return (bootId, 0L);
    }

    private string PathOf(ObjectBlobAddress address) {
        return Path.Combine(path1: m_target.BasePath, path2: address.ObjectId.ToString(), path3: address.Key.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
    }
}
