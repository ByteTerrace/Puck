using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Puck.Storage;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Explicit composition for trusted service adapters and scoped caller capabilities. Owns bounded
/// dispatch, polling, recovery, and revocation; never discovers executable code or credentials from the filesystem.</summary>
/// <remarks>Keep one host and journal per live authority lineage. Recreate the host and its clients after an
/// explicitly reopened replay epoch. Providers remain trusted code; the capability API is not a CLR sandbox.</remarks>
public sealed partial class WorldExtensionHost : IAsyncDisposable {
    private readonly WorldServer m_server;
    private readonly WorldExternalOperationJournal m_journal;
    private readonly string m_lineage;
    private readonly Func<string> m_captureCause;
    private readonly FrozenDictionary<string, WorldExtensionOperation> m_operations;
    private readonly WorldExtensionHostOptions m_options;
    private readonly TimeProvider m_time;
    private readonly Dictionary<string, WorldExtensionClient> m_clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> m_due = new(StringComparer.Ordinal);
    private readonly Lock m_gate = new();
    private readonly SemaphoreSlim m_submissions;
    private readonly SemaphoreSlim m_pump = new(1);
    private readonly CancellationTokenSource m_stop = new();
    private Task? m_worker;
    private bool m_disposed;
    private int m_cursor;
    private string? m_lastFailure;

    /// <summary>Registers an explicit set of trusted operations without starting work.</summary>
    /// <param name="server">The live world authority.</param>
    /// <param name="journal">Its private, non-rewindable external-operation history.</param>
    /// <param name="lineage">Stable host-selected authority lineage. Forks must use another namespace and bindings.</param>
    /// <param name="operations">The explicitly registered service operations.</param>
    /// <param name="captureCause">Host-only capture at a settled boundary; no provider or caller receives its result.</param>
    /// <param name="options">Bounded worker policy, or the small-host defaults.</param>
    /// <param name="timeProvider">Host time for scheduling; never used in simulation evaluation.</param>
    /// <exception cref="ArgumentException">A required argument, registration, or capacity is invalid.</exception>
    /// <exception cref="JsonException">An operation's input schema is malformed JSON.</exception>
    public WorldExtensionHost(WorldServer server, WorldExternalOperationJournal journal, string lineage,
        IEnumerable<WorldExtensionOperation> operations, Func<string> captureCause,
        WorldExtensionHostOptions? options = null, TimeProvider? timeProvider = null) {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(captureCause);
        ArgumentException.ThrowIfNullOrWhiteSpace(lineage);
        m_server = server; m_journal = journal; m_lineage = lineage; m_captureCause = captureCause;
        m_options = options ?? WorldExtensionHostOptions.Default;
        m_time = timeProvider ?? TimeProvider.System;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(m_options.MaximumClients);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(m_options.MaximumConcurrentOperations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(m_options.MaximumConcurrentSubmissions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(m_options.MaximumInputBytes);
        if (m_options.PollInterval <= TimeSpan.Zero || m_options.OperationTimeout <= TimeSpan.Zero ||
            m_options.OperationTimeout.TotalMilliseconds > uint.MaxValue - 1 || m_options.PollInterval.TotalMilliseconds > uint.MaxValue - 1) {
            throw new ArgumentOutOfRangeException(nameof(options), "Worker intervals must be positive and fit the timer range.");
        }
        m_operations = operations.ToFrozenDictionary(operation => operation.Description.Name, StringComparer.Ordinal);
        foreach (var operation in m_operations.Values) {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation.Description.Name);
            ArgumentNullException.ThrowIfNull(operation.Description.Description);
            ArgumentNullException.ThrowIfNull(operation.Provider);
            ArgumentNullException.ThrowIfNull(operation.CreateRequest);
            using var schema = JsonDocument.Parse(operation.Description.InputSchema);
            if (schema.RootElement.ValueKind != JsonValueKind.Object) { throw new ArgumentException("Operation schema must be an object.", nameof(operations)); }
        }
        m_submissions = new(m_options.MaximumConcurrentSubmissions);
    }

    /// <summary>Gets the last worker failure's type, without credentials, SDK messages, or recovery images.</summary>
    public string? LastFailure => Volatile.Read(ref m_lastFailure);

    /// <summary>Issues one caller capability. The root grants service access explicitly; world grants alone never
    /// grant cloud access. Disposing the returned client revokes it.</summary>
    /// <param name="principal">An authenticated ingress identity chosen by the host.</param>
    /// <param name="allowedOperations">Exact operation names this client may discover and invoke.</param>
    /// <param name="worldRequests">The normal manifest limiting observations and gameplay contributions.</param>
    /// <param name="maximumPendingContributions">The ordinary recorded-contribution queue ceiling.</param>
    /// <param name="storage">Optional separately scoped storage capability. Its lifetime transfers to this client.</param>
    /// <returns>A caller-bound API with no ambient authority.</returns>
    /// <exception cref="ArgumentException">The identity, service grant, or world manifest is invalid.</exception>
    /// <exception cref="InvalidOperationException">Client capacity is exhausted, the principal already has a live client, or live extensions are suppressed.</exception>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public WorldExtensionClient CreateClient(WorldPrincipal principal, IEnumerable<string> allowedOperations,
        IEnumerable<WorldCapabilityRequest> worldRequests, int maximumPendingContributions = 32, ObjectBlobNamespace? storage = null) {
        ArgumentNullException.ThrowIfNull(allowedOperations);
        ArgumentNullException.ThrowIfNull(worldRequests);
        if (principal.Kind is not (PrincipalKind.Seat or PrincipalKind.Console or PrincipalKind.Addon or PrincipalKind.Peer) ||
            !WorldPrincipal.TryParse(principal.Describe(), out var canonical) || canonical != principal) {
            throw new ArgumentException("A canonical ingress principal is required.", nameof(principal));
        }
        var selected = allowedOperations.Distinct(StringComparer.Ordinal).ToFrozenDictionary(name => name,
            name => m_operations.TryGetValue(name, out var operation) ? operation : throw new ArgumentException("Unknown operation grant.", nameof(allowedOperations)),
            StringComparer.Ordinal);
        var prefix = "puck-extension/" + Hash(m_lineage, principal.Describe()) + "/";
        lock (m_gate) {
            ObjectDisposedException.ThrowIf(m_disposed, this);
            var replacing = m_clients.TryGetValue(prefix, out var previous);
            if (previous?.Runtime.IsActive == true) { throw new InvalidOperationException("Revoke this principal's existing client before replacing its policy."); }
            if (!replacing && m_clients.Count >= m_options.MaximumClients) { throw new InvalidOperationException("Extension client capacity is exhausted."); }
            var runtime = new WorldRecordedExtension(m_server, principal, worldRequests, maximumPendingContributions);
            var dispatcher = new WorldExternalOperationDispatcher(runtime, m_journal,
                selected.ToDictionary(pair => pair.Key, pair => pair.Value.Provider, StringComparer.Ordinal));
            var client = new WorldExtensionClient(this, runtime, dispatcher, selected, prefix, storage);
            m_clients[prefix] = client;
            return client;
        }
    }

    internal WorldExtensionOperationHandle GetOperation(WorldExtensionClient client, string name, string requestKey) {
        client.CheckActive();
        if (!client.Operations.ContainsKey(name)) { throw new UnauthorizedAccessException("This client cannot invoke that operation."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        if (Encoding.UTF8.GetByteCount(requestKey) > 1024) { throw new ArgumentException("Request key exceeds its byte budget.", nameof(requestKey)); }
        return new(this, client, client.Prefix + Hash(name, requestKey), name);
    }

    internal async ValueTask<WorldExtensionOperationHandle> InvokeAsync(WorldExtensionClient client, string name, string requestKey,
        string input, CancellationToken cancellationToken) {
        var handle = GetOperation(client, name, requestKey);
        ArgumentNullException.ThrowIfNull(input);
        if (Encoding.UTF8.GetByteCount(input) > m_options.MaximumInputBytes) { throw new ArgumentException("Operation input exceeds its byte budget.", nameof(input)); }
        if (!await m_submissions.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { throw new InvalidOperationException("Extension submission capacity is exhausted."); }
        try {
            using var admission = client.Runtime.BeginDispatch();
            var registration = client.Operations[name];
            var request = registration.CreateRequest(handle.Id, input);
            if (request.Id != handle.Id || request.Binding != name || request.BindingIdentity != registration.Provider.Identity || request.Payload != input) {
                throw new InvalidOperationException("The operation factory changed its host-bound identity or input.");
            }
            var existing = (await m_journal.ReadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(entry => entry.Operation.Id == handle.Id);
            if (existing is not null) {
                if (existing.Operation != request) { throw new InvalidOperationException("A request key was reused with different input or binding identity."); }
                return handle;
            }
            await client.Dispatcher.CommitAsync(request, m_captureCause(), cancellationToken).ConfigureAwait(false);
            return handle;
        } finally { m_submissions.Release(); }
    }

    internal async ValueTask<WorldExtensionOperationSnapshot?> ReadAsync(WorldExtensionClient client, string id, CancellationToken cancellationToken) {
        client.CheckActive();
        if (!id.StartsWith(client.Prefix, StringComparison.Ordinal)) { throw new UnauthorizedAccessException("This operation belongs to another client."); }
        var entries = await m_journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        client.CheckActive();
        var entry = entries.FirstOrDefault(entry => entry.Operation.Id == id);
        if (entry is null) { return null; }
        if (!client.Operations.ContainsKey(entry.Operation.Binding)) { throw new UnauthorizedAccessException("This operation is no longer granted."); }
        return new(entry.Operation.Id, entry.Operation.Binding, entry.Status, entry.Result);
    }

    private static string Hash(params string[] fields) {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true), leaveOpen: true)) { foreach (var field in fields) { writer.Write(field); } }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
}
