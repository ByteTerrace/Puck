using System.Security.Cryptography;
using System.Text;

namespace Puck.Storage;

/// <summary>A revocable storage capability for one host-selected object namespace. Callers supply logical keys,
/// never a storage target, object id, or host path. Possession grants only the configured read/write reach.</summary>
public sealed class ObjectBlobNamespace : IDisposable {
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;
    private readonly Guid m_objectId;
    private readonly string m_prefix;
    private readonly int m_maximumBlobBytes;
    private readonly bool m_writable;
    private bool m_revoked;
    private readonly ObjectBlobNamespace? m_parent;
    private readonly Func<bool>? m_isActive;

    /// <summary>Creates a capability at a trusted hosting boundary.</summary>
    /// <param name="store">The routed store; never exposed to the capability's consumer.</param>
    /// <param name="target">The host-selected private storage target.</param>
    /// <param name="objectId">The host-selected object.</param>
    /// <param name="name">A nonempty, case-sensitive opaque namespace name, limited to 1024 UTF-8 bytes.</param>
    /// <param name="maximumBlobBytes">The positive per-blob read/write ceiling.</param>
    /// <param name="writable">Whether this capability permits writes.</param>
    /// <exception cref="ArgumentNullException">The store or target is null.</exception>
    /// <exception cref="ArgumentException">The name is empty, oversized, or invalid Unicode.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The byte ceiling is not positive.</exception>
    public ObjectBlobNamespace(IObjectBlobStore store, ObjectStorageTarget target, Guid objectId, string name,
        int maximumBlobBytes, bool writable = false) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBlobBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        m_store = store;
        m_target = target;
        m_objectId = objectId;
        var encoding = new UTF8Encoding(false, true);
        if (encoding.GetByteCount(name) > 1024) { throw new ArgumentException("Namespace name exceeds its byte budget.", nameof(name)); }
        var nameBytes = encoding.GetBytes(name);
        // Fixed-depth opaque names cannot overlap through path nesting, case folding, or filesystem aliases.
        m_prefix = "namespaces/" + Convert.ToHexStringLower(SHA256.HashData(nameBytes)) + "/";
        m_maximumBlobBytes = maximumBlobBytes;
        m_writable = writable;
    }

    private ObjectBlobNamespace(ObjectBlobNamespace parent, Func<bool> isActive) {
        m_store = parent.m_store; m_target = parent.m_target; m_objectId = parent.m_objectId;
        m_prefix = parent.m_prefix; m_maximumBlobBytes = parent.m_maximumBlobBytes; m_writable = parent.m_writable;
        m_parent = parent; m_isActive = isActive;
    }

    /// <summary>Adds a lifetime restriction without widening this capability. Parent revocation also revokes the child.</summary>
    /// <param name="isActive">Host lifetime check evaluated on every operation.</param>
    /// <returns>A capability subject to both the parent's restrictions and the supplied check.</returns>
    /// <exception cref="ArgumentNullException">The lifetime check is null.</exception>
    /// <exception cref="ObjectDisposedException">This capability has been revoked.</exception>
    public ObjectBlobNamespace WithLifetime(Func<bool> isActive) {
        ArgumentNullException.ThrowIfNull(isActive);
        CheckActive();
        return new(this, isActive);
    }

    /// <summary>Reads within this namespace, or returns null when the key is absent.</summary>
    /// <param name="key">A relative logical blob key.</param>
    /// <param name="cancellationToken">Cancels storage work.</param>
    /// <returns>The bounded content and version token.</returns>
    /// <exception cref="ArgumentException">The key is unsafe or exceeds the key limits.</exception>
    /// <exception cref="ObjectDisposedException">This capability has been revoked.</exception>
    /// <exception cref="IOException">Storage fails or the blob exceeds its byte ceiling.</exception>
    public async ValueTask<ObjectBlobContent?> ReadAsync(string key, CancellationToken cancellationToken = default) {
        CheckActive();
        var result = await m_store.ReadAsync(m_target, Address(key), cancellationToken).ConfigureAwait(false);
        CheckActive();
        if (result?.Content.Length > m_maximumBlobBytes) { throw new IOException("Namespace blob exceeds its byte budget."); }
        return result;
    }

    /// <summary>Writes within this namespace after checking its write capability and byte ceiling.</summary>
    /// <param name="key">A relative logical blob key.</param>
    /// <param name="content">The bounded content, copied before asynchronous work.</param>
    /// <param name="mode">Create-only or overwrite.</param>
    /// <param name="ifMatchVersion">Optional compare-and-swap token.</param>
    /// <param name="cancellationToken">Cancels storage work.</param>
    /// <returns>The existing store's write verdict.</returns>
    /// <exception cref="ArgumentException">The key is unsafe or exceeds the key limits.</exception>
    /// <exception cref="ObjectDisposedException">This capability has been revoked.</exception>
    /// <exception cref="UnauthorizedAccessException">This capability is read-only.</exception>
    /// <exception cref="IOException">Storage fails or the content exceeds its byte ceiling.</exception>
    public ValueTask<ObjectBlobWriteResult> WriteAsync(string key, ReadOnlyMemory<byte> content,
        ObjectBlobWriteMode mode = ObjectBlobWriteMode.CreateOnly, string? ifMatchVersion = null,
        CancellationToken cancellationToken = default) {
        CheckActive();
        if (!m_writable) { throw new UnauthorizedAccessException("This storage namespace is read-only."); }
        if (content.Length > m_maximumBlobBytes) { throw new IOException("Namespace blob exceeds its byte budget."); }
        return m_store.WriteAsync(m_target, Address(key), content.ToArray(), mode, ifMatchVersion, cancellationToken);
    }

    private ObjectBlobAddress Address(string key) => new(m_objectId,
        m_prefix + ObjectBlobAddressPath.GetNormalizedKey(new(m_objectId, key)));
    private void CheckActive() {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref m_revoked), this);
        m_parent?.CheckActive();
        ObjectDisposedException.ThrowIf(m_isActive is not null && !m_isActive(), this);
    }
    /// <summary>Revokes future requests. Already-admitted writes may still complete.</summary>
    public void Dispose() => Volatile.Write(ref m_revoked, true);
}
