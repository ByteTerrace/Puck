using Puck.Storage;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A host-issued caller capability. It fixes identity, discoverable operations, world reach, and optional
/// private storage. It exposes no credentials, journal addresses, authority recovery images, or service providers.</summary>
public sealed class WorldExtensionClient : IDisposable {
    private readonly WorldExtensionHost m_host;
    private readonly ObjectBlobNamespace? m_storageOwner;
    internal WorldRecordedExtension Runtime { get; }
    internal WorldExternalOperationDispatcher Dispatcher { get; }
    internal IReadOnlyDictionary<string, WorldExtensionOperation> Operations { get; }
    internal string Prefix { get; }

    internal WorldExtensionClient(WorldExtensionHost host, WorldRecordedExtension runtime, WorldExternalOperationDispatcher dispatcher,
        IReadOnlyDictionary<string, WorldExtensionOperation> operations, string prefix, ObjectBlobNamespace? storage) {
        m_host = host;
        Runtime = runtime;
        Dispatcher = dispatcher;
        Operations = operations;
        Prefix = prefix;
        m_storageOwner = storage;
        Storage = storage?.WithLifetime(() => runtime.IsActive);
    }

    /// <summary>Gets this client's fixed acting identity.</summary>
    public WorldPrincipal Principal => Runtime.Principal;
    /// <summary>Gets the separately granted private storage capability, if any.</summary>
    public ObjectBlobNamespace? Storage { get; }
    /// <summary>Gets the operations this client may discover and invoke.</summary>
    /// <returns>Detached operation descriptions.</returns>
    public IReadOnlyList<WorldExtensionOperationDescription> Discover() {
        CheckActive();
        return Operations.Values.Select(operation => operation.Description).ToArray();
    }

    /// <summary>Validates and durably queues one request. Completion is observed through the returned handle.</summary>
    /// <param name="operation">A name returned by discovery.</param>
    /// <param name="requestKey">A stable request generation key; reuse returns the original operation, never a new effect.</param>
    /// <param name="input">The operation's input JSON or permitted empty payload.</param>
    /// <param name="cancellationToken">Cancels admission. Read back an uncertain commit using the same key.</param>
    /// <returns>A caller-bound operation handle.</returns>
    /// <exception cref="UnauthorizedAccessException">The operation is not granted.</exception>
    /// <exception cref="ObjectDisposedException">The client has been revoked or suppressed.</exception>
    /// <exception cref="ArgumentException">The key or input is invalid or oversized.</exception>
    /// <exception cref="InvalidOperationException">Admission is full, recovery capture is unavailable, or a request identity conflicts.</exception>
    public ValueTask<WorldExtensionOperationHandle> InvokeAsync(string operation, string requestKey, string input = "",
        CancellationToken cancellationToken = default) => m_host.InvokeAsync(this, operation, requestKey, input, cancellationToken);

    /// <summary>Reconstructs a caller-bound handle after restart without sending an external request.</summary>
    /// <param name="operation">The registered operation name.</param>
    /// <param name="requestKey">The original request generation key.</param>
    /// <returns>A handle whose read-back is null if the request was never committed.</returns>
    /// <exception cref="UnauthorizedAccessException">The operation is not granted.</exception>
    /// <exception cref="ObjectDisposedException">The client has been revoked or suppressed.</exception>
    /// <exception cref="ArgumentException">The key is invalid or oversized.</exception>
    public WorldExtensionOperationHandle GetOperation(string operation, string requestKey) => m_host.GetOperation(this, operation, requestKey);

    /// <summary>Observes world data through the existing manifest, visibility, and authority gates.</summary>
    /// <param name="query">The typed query.</param>
    /// <returns>The ordinary authority read-back.</returns>
    public QueryAnswer Observe(WorldQuery query) => Runtime.Observe(query);
    /// <summary>Submits a gameplay contribution through the existing recorded mutation boundary.</summary>
    /// <param name="mutation">A mutation bearing this client's principal.</param>
    /// <returns>A correlation id, not an application verdict.</returns>
    public long Submit(WorldMutation mutation) => Runtime.Submit(mutation);

    internal void CheckActive() {
        if (!Runtime.IsActive) { throw new ObjectDisposedException(nameof(WorldExtensionClient), "The client is revoked or its timeline is suppressed."); }
    }

    /// <summary>Revokes this client and its private storage capability. Already-sent external effects remain durable.</summary>
    public void Dispose() { Runtime.Dispose(); m_storageOwner?.Dispose(); Storage?.Dispose(); }
}

/// <summary>A caller-bound durable operation handle. It cannot read another caller's operations or recovery image.</summary>
public sealed class WorldExtensionOperationHandle {
    private readonly WorldExtensionHost m_host;
    private readonly WorldExtensionClient m_client;
    internal WorldExtensionOperationHandle(WorldExtensionHost host, WorldExtensionClient client, string id, string name) {
        m_host = host; m_client = client; Id = id; Name = name;
    }
    /// <summary>Gets the host-derived durable id.</summary>
    public string Id { get; }
    /// <summary>Gets the registered operation name.</summary>
    public string Name { get; }
    /// <summary>Reads the current durable status, or null when no request was committed.</summary>
    /// <param name="cancellationToken">Cancels the storage read.</param>
    /// <returns>The operation status without authority recovery evidence.</returns>
    /// <exception cref="ObjectDisposedException">The originating client has been revoked or suppressed.</exception>
    public ValueTask<WorldExtensionOperationSnapshot?> ReadAsync(CancellationToken cancellationToken = default) =>
        m_host.ReadAsync(m_client, Id, cancellationToken);
}
