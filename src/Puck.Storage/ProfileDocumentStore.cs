namespace Puck.Storage;

/// <summary>A loaded document paired with the opaque version token it was read (or seeded) at — the if-match input a
/// subsequent optimistic <see cref="ProfileDocumentStore{T}.SaveAsync"/> hands back (§2.5.2).</summary>
/// <typeparam name="T">The document type.</typeparam>
/// <param name="Value">The document.</param>
/// <param name="VersionToken">The version token the document was read/seeded at, or <see langword="null"/>.</param>
public readonly record struct StoredDocument<T>(T Value, string? VersionToken);

/// <summary>
/// Loads and saves a JSON profile document through <see cref="IJsonObjectBlobStore"/> at a fixed address: first
/// load seeds the built-in default with <see cref="ObjectBlobWriteMode.CreateOnly"/> (so the on-disk document is
/// immediately visible and editable), and every subsequent load reads back whatever is on disk — the stored
/// document is the single source of truth from then on. Reads and writes carry the version token so a caller can drive
/// optimistic concurrency (§2.5.2).
/// </summary>
/// <typeparam name="T">The document type persisted through this store.</typeparam>
public sealed class ProfileDocumentStore<T> {
    private readonly ObjectBlobAddress m_address;
    private readonly IJsonObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;

    /// <summary>Initializes a new instance of the <see cref="ProfileDocumentStore{T}"/> class.</summary>
    /// <param name="store">The JSON blob store to persist through.</param>
    /// <param name="target">The storage target (e.g. a local-file base path) the address resolves against.</param>
    /// <param name="address">The fixed object/key address this document lives at.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    public ProfileDocumentStore(IJsonObjectBlobStore store, ObjectStorageTarget target, ObjectBlobAddress address) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(target);

        m_address = address;
        m_store = store;
        m_target = target;
    }

    /// <summary>Loads the stored document, seeding storage with <paramref name="default"/> when none exists yet.</summary>
    /// <param name="default">The built-in default document to seed and fall back to.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The stored (or seeded) document and the version token it carries.</returns>
    public async ValueTask<StoredDocument<T>> LoadOrCreateAsync(T @default, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(@default);

        var result = await m_store.ReadAsync<T>(
            address: m_address,
            cancellationToken: cancellationToken,
            target: m_target
        );

        if (result is { Found: true, Value: not null }) {
            return new StoredDocument<T>(Value: result.Value, VersionToken: result.VersionToken);
        }

        // First run: seed the default so an editor has a file to work from. CreateOnly loses gracefully to a
        // concurrent writer — whoever wins, the document on disk is authoritative next load. The seed write hands back
        // the new token (null when a concurrent writer won the race).
        var seed = await m_store.WriteAsync(
            address: m_address,
            cancellationToken: cancellationToken,
            mode: ObjectBlobWriteMode.CreateOnly,
            target: m_target,
            value: @default
        );

        return new StoredDocument<T>(Value: @default, VersionToken: seed.VersionToken);
    }

    /// <summary>Saves a document, overwriting the stored one (an editor's save path), optionally guarded by an if-match
    /// version token so a stale writer is refused rather than clobbering a newer copy.</summary>
    /// <param name="document">The document to persist.</param>
    /// <param name="ifMatchVersion">The version token the stored blob must still carry, or <see langword="null"/> for an
    /// unconditional overwrite.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The write outcome (landed / precondition-failed) and the new token.</returns>
    public ValueTask<ObjectBlobWriteResult> SaveAsync(T document, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(document);

        return m_store.WriteAsync(
            address: m_address,
            cancellationToken: cancellationToken,
            ifMatchVersion: ifMatchVersion,
            mode: ObjectBlobWriteMode.Overwrite,
            target: m_target,
            value: document
        );
    }
}
