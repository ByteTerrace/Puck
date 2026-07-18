using Puck.Commands;
using Puck.Storage;

namespace Puck.Demo;

/// <summary>
/// Loads and saves the local player's <see cref="BindingProfileDocument"/> through <see cref="IJsonObjectBlobStore"/>
/// (a local-file target under the user's application-data directory). First run seeds the built-in default with
/// <see cref="ObjectBlobWriteMode.CreateOnly"/>, so the on-disk JSON is immediately visible, editable, and the
/// single source of truth from then on — edit it and relaunch, or a future in-game editor saves through
/// <see cref="SaveAsync"/> and hot-reloads via <see cref="PagedInputBindings.Reload"/>.
/// </summary>
internal sealed class BindingProfileDocumentStore {
    // The stable identity of "the local player's settings" in blob storage; the file lands at
    // <BasePath>/<LocalSettingsId>/bindings/gamepad.json.
    private static readonly Guid LocalSettingsId = new(g: "b1d5c0de-0001-4000-8000-000000000001");
    private static readonly ObjectBlobAddress Address = new(Key: "bindings/gamepad.json", ObjectId: LocalSettingsId);
    private readonly ProfileDocumentStore<BindingProfileDocument> m_inner;
    private readonly LocalFileObjectStorageTarget m_target;

    /// <summary>Initializes a new instance of the <see cref="BindingProfileDocumentStore"/> class.</summary>
    /// <param name="store">The JSON blob store to persist through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public BindingProfileDocumentStore(IJsonObjectBlobStore store) {
        ArgumentNullException.ThrowIfNull(store);

        m_target = new LocalFileObjectStorageTarget(BasePath: Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData), path2: "Puck", path3: "Demo"));
        m_inner = new ProfileDocumentStore<BindingProfileDocument>(store: store, target: m_target, address: Address);
    }

    /// <summary>Gets the on-disk path of the profile document (for diagnostics — "edit this file").</summary>
    public string FilePath => Path.Combine(path1: m_target.BasePath, path2: LocalSettingsId.ToString(), path3: "bindings", path4: "gamepad.json");

    /// <summary>Loads the stored profile, seeding storage with <paramref name="default"/> when none exists yet.</summary>
    /// <param name="default">The built-in default document to seed and fall back to.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The stored document, or <paramref name="default"/> when storage is empty.</returns>
    public async ValueTask<BindingProfileDocument> LoadOrCreateAsync(BindingProfileDocument @default, CancellationToken cancellationToken = default) {
        // The version token the seam now carries is unused here — the demo's binding profile is a single local blob with
        // no optimistic-concurrency need; unwrap the document.
        return (await m_inner.LoadOrCreateAsync(@default: @default, cancellationToken: cancellationToken)).Value;
    }

    /// <summary>Saves a profile document, overwriting the stored one (an editor's save path).</summary>
    /// <param name="document">The document to persist.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns><see langword="true"/> when the write succeeded.</returns>
    public async ValueTask<bool> SaveAsync(BindingProfileDocument document, CancellationToken cancellationToken = default) {
        return (await m_inner.SaveAsync(document: document, cancellationToken: cancellationToken)).Succeeded;
    }
}
