using System.Text.Json;
using Puck.Storage;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Owns a validated deployment assembled from registered provider types and configuration data.</summary>
/// <remarks>Construct before starting workers. Call Pump at closed simulation boundaries. Dispose before the
/// borrowed world authority and storage. Configuration never grants itself world mutation authority.</remarks>
public sealed partial class WorldConfiguredExtensions : IAsyncDisposable {
    private readonly WorldServer m_server;
    private readonly Func<string> m_captureCause;
    private readonly WorldExtensionConfiguration m_configuration;
    private readonly Dictionary<WorldPrincipal, WorldExtensionClient> m_clients = [];
    private readonly List<IWorldConfiguredProvider> m_providers = [];
    private readonly CancellationTokenSource m_stop = new();
    private readonly Dictionary<string, string> m_observed = new(StringComparer.Ordinal);
    private Task m_work = Task.CompletedTask;
    private ulong m_lastScan;
    private bool m_scanned;
    private bool m_disposed;
    private string? m_lastFailure;

    private WorldConfiguredExtensions(WorldExtensionConfiguration configuration, WorldServer server, Func<string> captureCause) {
        m_configuration = configuration; m_server = server; m_captureCause = captureCause;
    }

    /// <summary>Gets the shared worker and its diagnostics. This is a host-only API.</summary>
    public WorldExtensionHost Host { get; private set; } = null!;
    /// <summary>Gets the most recent connection failure, without provider exception messages or recovery images.</summary>
    public string? LastFailure => Volatile.Read(ref m_lastFailure) ?? Host.LastFailure;
    /// <summary>Gets the immutable connection declarations; no provider settings are included.</summary>
    public IReadOnlyList<WorldExtensionConnection> Connections => m_configuration.Connections.ToArray();
    /// <summary>Gets configured operation names for host diagnostics, without provider settings.</summary>
    public IReadOnlyList<string> OperationNames => m_configuration.Operations.Select(operation => operation.Name).ToArray();

    /// <summary>Validates configuration and constructs providers, grants, and private history without dispatching effects.</summary>
    /// <param name="configuration">Host-authorized deployment data.</param>
    /// <param name="types">Explicitly installed provider types, using the existing keyed extension registry.</param>
    /// <param name="server">The selected live authority.</param>
    /// <param name="store">The private routed store.</param>
    /// <param name="target">The host-selected private persistence target.</param>
    /// <param name="captureCause">Capture at a closed simulation boundary, before asynchronous work.</param>
    /// <returns>An owned composition; call Pump to observe collections and requests, and the host's Start when operations are configured.</returns>
    /// <exception cref="ArgumentException">A name, reference, capacity, or world binding is invalid.</exception>
    /// <exception cref="InvalidOperationException">A required capability or state table is unavailable.</exception>
    public static WorldConfiguredExtensions Create(WorldExtensionConfiguration configuration,
        WorldExtensionRegistry<WorldExtensionProviderType> types, WorldServer server, IObjectBlobStore store,
        ObjectStorageTarget target, Func<string> captureCause) {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(captureCause);
        configuration = WorldExtensionConfiguration.Parse(JsonSerializer.SerializeToUtf8Bytes(configuration,
            WorldExtensionConfigurationJson.Default.WorldExtensionConfiguration));
        if (configuration.World != server.Definition.DocumentId || string.IsNullOrWhiteSpace(configuration.World)) {
            throw new ArgumentException("Extension configuration must name this world's exact document ID.");
        }
        if (configuration.Lineage == Guid.Empty || configuration.MaximumEntries <= 0 || configuration.MaximumBytes <= 0 ||
            configuration.ScanEveryTicks <= 0 || configuration.Recovery is not ("checkpoint" or "recording") ||
            configuration.Providers.Count > 64 || configuration.Operations.Count > 1024 || configuration.Connections.Count > 1024) {
            throw new ArgumentException("Invalid extension namespace, recovery policy, or capacity.");
        }
        var owner = new WorldConfiguredExtensions(configuration, server, captureCause);
        try {
            var providers = new Dictionary<string, IWorldConfiguredProvider>(StringComparer.Ordinal);
            foreach (var row in configuration.Providers) {
                ValidateName(row.Name);
                if (providers.ContainsKey(row.Name)) { throw new ArgumentException($"Duplicate provider '{row.Name}'."); }
                if (!types.TryGet(row.Type, out var type)) { throw new ArgumentException($"Provider '{row.Name}' selects uninstalled type '{row.Type}'."); }
                if (row.Settings.ValueKind != JsonValueKind.Object) { throw new ArgumentException($"Provider '{row.Name}' needs object settings."); }
                var provider = type.Create(row.Settings);
                owner.m_providers.Add(provider);
                providers.Add(row.Name, provider);
            }
            var operations = new Dictionary<string, WorldExtensionOperation>(StringComparer.Ordinal);
            foreach (var row in configuration.Operations) {
                ValidateName(row.Name);
                if (!providers.TryGetValue(row.Provider, out var provider)) { throw new ArgumentException($"Operation '{row.Name}' names unknown provider '{row.Provider}'."); }
                if (row.Settings.ValueKind != JsonValueKind.Object) { throw new ArgumentException($"Operation '{row.Name}' needs object settings."); }
                var operation = provider.Bind(row.Name, row.Description, row.Settings);
                if (operation.Description.Name != row.Name || !operations.TryAdd(row.Name, operation)) { throw new ArgumentException($"Invalid or duplicate operation '{row.Name}'."); }
            }
            var journal = new WorldExternalOperationJournal(store, target, new(configuration.Lineage, "external/operations.json"),
                configuration.MaximumEntries, configuration.MaximumBytes, 8);
            owner.Host = new(server, journal, configuration.Lineage.ToString("D"), operations.Values,
                captureCause, configuration.Worker);
            foreach (var row in configuration.Clients) {
                var principal = ParsePrincipal(row.Principal);
                if (owner.m_clients.ContainsKey(principal)) { throw new ArgumentException($"Duplicate client '{row.Principal}'."); }
                ObjectBlobNamespace? storage = row.StorageBytes is { } limit
                    ? new(store, target, configuration.Lineage, row.Principal, limit, row.StorageWritable) : null;
                try { owner.m_clients.Add(principal, owner.Host.CreateClient(principal, row.Operations, row.Requests.Select(ParseRequest), storage: storage)); }
                catch { storage?.Dispose(); throw; }
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            var outputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var connection in configuration.Connections) {
                ValidateName(connection.Name);
                if (!names.Add(connection.Name)) { throw new ArgumentException($"Duplicate connection '{connection.Name}'."); }
                var client = owner.Client(ParsePrincipal(connection.Client));
                _ = client.GetOperation(connection.Operation, "validation");
                owner.RequireTable(connection.Requests, CellKind.Text);
                owner.RequireTable(connection.Status, CellKind.Int);
                if (!outputs.Add(connection.Status)) { throw new ArgumentException("Connections must not overwrite each other's status tables."); }
                if (connection.Results is { } result) {
                    owner.RequireTable(result, CellKind.Text);
                    if (!outputs.Add(result)) { throw new ArgumentException("Connections must not overwrite each other's result tables."); }
                }
                _ = owner.ReadRequests(client, connection);
                _ = owner.ReadTable(client, connection.Status, CellKind.Int);
                if (connection.Results is { } resultTable) { _ = owner.ReadTable(client, resultTable, CellKind.Text); }
            }
            owner.ConfigureObservations(providers, outputs);
            if (configuration.Connections.Any(connection => outputs.Contains(connection.Requests))) {
                throw new ArgumentException("A connection output cannot also be a request table; use authored rules to initiate a new request.");
            }
            return owner;
        } catch {
            foreach (var observation in owner.m_observations) { observation.Source.Dispose(); }
            foreach (var client in owner.m_clients.Values) { client.Dispose(); }
            foreach (var provider in owner.m_providers.AsEnumerable().Reverse()) { provider.Dispose(); }
            owner.m_stop.Dispose();
            throw;
        }
    }

    /// <summary>Resolves only the authenticated caller's configured capability, never a caller-selected impersonation.</summary>
    /// <param name="principal">Identity supplied by the trusted ingress.</param>
    /// <returns>The configured caller capability.</returns>
    /// <exception cref="UnauthorizedAccessException">No client policy exists for this identity.</exception>
    public WorldExtensionClient Client(WorldPrincipal principal) => m_clients.TryGetValue(principal, out var client)
        ? client : throw new UnauthorizedAccessException("No extension policy is configured for this principal.");

    private static void ValidateName(string name) {
        if (!SafeName.TryParse(name, out _, out var reason)) { throw new ArgumentException($"Invalid extension name: {reason}"); }
    }

    private static WorldPrincipal ParsePrincipal(string text) => WorldPrincipal.TryParse(text, out var principal) && principal.Describe() == text
        ? principal : throw new ArgumentException($"Invalid principal '{text}'.");

    private static WorldCapabilityRequest ParseRequest(WorldExtensionWorldRequest request) {
        if (!Enum.TryParse<WorldCapability>(request.Capability, true, out var capability) || !Enum.IsDefined(capability) ||
            !string.Equals(capability.ToString(), request.Capability, StringComparison.OrdinalIgnoreCase) ||
            !GrantSubject.TryParse(request.Subject, out var subject)) { throw new ArgumentException("Invalid world capability request."); }
        return new(capability, subject);
    }

    /// <summary>Requires an ordinary table of any cell kind and returns which kind it declares.</summary>
    private CellKind RequireTable(string name) {
        var row = m_server.Definition.State.FirstOrDefault(row => row.Name.Value == name);
        if (row is null || row.IsSlot || row.PhaseOf is not null || row.Advance is not null || row.Dynamics is not null || row.Cycle is not null) {
            throw new InvalidOperationException($"Extension state '{name}' must be an ordinary table.");
        }
        return row.Kind;
    }

    private void RequireTable(string name, CellKind kind) {
        if (RequireTable(name) != kind) { throw new InvalidOperationException($"Extension state '{name}' must be an ordinary {kind} table."); }
    }

    private IReadOnlyList<WorldObservedCell> ReadRequests(WorldExtensionClient client, WorldExtensionConnection connection) {
        return ReadTable(client, connection.Requests, CellKind.Text);
    }

    private IReadOnlyList<WorldObservedCell> ReadTable(WorldExtensionClient client, string name, CellKind kind) {
        var answer = client.Observe(new WorldQuery.StateObservations(name));
        if (answer.Refused || answer.Payload is not WorldObservedRow[] rows || rows.SingleOrDefault() is not { } row || row.Kind != kind) {
            throw new InvalidOperationException($"Extension table '{name}' is not observable; check manifest, grants, and visibility.");
        }
        return row.Cells.Where(cell => !cell.Hidden).ToArray();
    }

    /// <summary>Cancels and drains connection and observation work, drains the shared host, then disposes owned providers.</summary>
    public async ValueTask DisposeAsync() {
        if (m_disposed) { return; }
        m_disposed = true;
        await m_stop.CancelAsync().ConfigureAwait(false);
        await m_work.ConfigureAwait(false);
        try { await Task.WhenAll(m_observations.Where(source => source.Pending is not null).Select(source => source.Pending!)).ConfigureAwait(false); }
        catch (Exception) { /* Each source failure is diagnostic; disposal must still retire every source. */ }
        foreach (var observation in m_observations) { observation.Source.Dispose(); }
        await Host.DisposeAsync().ConfigureAwait(false);
        foreach (var provider in m_providers.AsEnumerable().Reverse()) { provider.Dispose(); }
        m_stop.Dispose();
    }
}
