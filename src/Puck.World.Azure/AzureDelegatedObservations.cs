using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.ResourceManager;
using Puck.World.Server;
using Puck.State;

namespace Puck.World.Azure;

/// <summary>Request-confined Entra OBO reads through the existing inventory and metrics adapters, with explicit caller and disclosure grants.</summary>
public sealed class AzureDelegatedObservations : IDisposable {
    private readonly string m_tenant;
    private readonly string m_application;
    private readonly TokenCredential m_assertionCredential;
    private readonly FrozenDictionary<string, AzureDelegatedObservation> m_observations;
    private readonly HttpPipelineTransport? m_transport;
    private readonly bool m_ownsCredential;
    private int m_disposed;
    /// <summary>Names installed by the trusted deployment; providers and credentials are never returned.</summary>
    public IReadOnlyCollection<string> Names => m_observations.Keys;

    /// <summary>Validates a deployment without network I/O. The ingress must validate tenant-specific v2 tokens for this application before calling ReadAsync.</summary>
    /// <param name="tenantId">Exact Entra tenant accepted by the ingress.</param>
    /// <param name="applicationId">The API application ID, which is also the validated inbound audience.</param>
    /// <param name="settings">Explicit managed identity and observation grants.</param>
    /// <param name="assertionCredential">Optional trusted confidential-client credential; defaults to the explicitly configured managed identity.</param>
    /// <param name="transport">Optional trusted test transport for token exchange and ARM. Must not follow redirects.</param>
    public AzureDelegatedObservations(string tenantId, string applicationId, JsonElement settings,
        TokenCredential? assertionCredential = null, HttpPipelineTransport? transport = null) {
        m_tenant = Guid.Parse(tenantId).ToString("D");
        m_application = Guid.Parse(applicationId).ToString("D");
        var configuration = settings.Deserialize(AzureDelegatedJson.Default.AzureDelegatedSettings) ?? throw new ArgumentException("Delegated observation settings are required.");
        if (configuration.Observations.Length is < 1 or > 16) { throw new ArgumentException("Configure 1..16 delegated observations."); }
        m_assertionCredential = assertionCredential ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(Guid.Parse(configuration.ManagedIdentityClientId).ToString("D")));
        m_ownsCredential = assertionCredential is null;
        m_transport = transport;
        m_observations = configuration.Observations.Select(Bound).ToFrozenDictionary(row => row.Name, StringComparer.Ordinal);
        foreach (var row in m_observations.Values) {
            if (!SafeName.TryParse(row.Name, out _, out _) || row.Subjects.Length is < 1 or > 64 || row.Subjects.Any(value => !Guid.TryParse(value, out _)) ||
                row.MaximumItems is < 1 or > 128) { throw new ArgumentException("Observations need a valid name, 1..64 Entra object IDs, and 1..128 items."); }
            // Construction validates the existing adapter's resource binding and field allowlist without acquiring a token.
            using var source = Create(row, m_assertionCredential);
        }
    }

    /// <summary>Exchanges this validated user's API assertion and reads only an approved observation. No token or assertion is persisted.</summary>
    /// <param name="name">An installed observation name.</param>
    /// <param name="subject">The validated oid in this instance's tenant.</param>
    /// <param name="userAssertion">The validated API access token, never a downstream token.</param>
    /// <param name="expiresAt">The validated assertion's expiration.</param>
    /// <param name="cancellationToken">Request deadline, revocation, or disconnect.</param>
    /// <returns>A complete bounded disclosure snapshot.</returns>
    public async ValueTask<IReadOnlyList<WorldExtensionObservationItem>> ReadAsync(string name, string subject, string userAssertion,
        DateTimeOffset expiresAt, CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref m_disposed) != 0, this);
        if (!m_observations.TryGetValue(name, out var row) || !row.Subjects.Contains(subject, StringComparer.Ordinal)) { throw new UnauthorizedAccessException("No delegated observation grant for this caller."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(userAssertion);
        var remaining = expiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) { throw new AuthenticationFailedException("The delegated assertion has expired."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining < TimeSpan.FromSeconds(120) ? remaining : TimeSpan.FromSeconds(120));
        var options = new OnBehalfOfCredentialOptions { DisableInstanceDiscovery = true };
        options.Diagnostics.IsLoggingEnabled = false;
        if (m_transport is not null) { options.Transport = m_transport; }
        var credential = new OnBehalfOfCredential(m_tenant, m_application,
            async token => (await m_assertionCredential.GetTokenAsync(new(["api://AzureADTokenExchange"]), token).ConfigureAwait(false)).Token,
            userAssertion, options);
        try {
            using var source = Create(row, credential);
            var items = await source.ReadAsync(deadline.Token).ConfigureAwait(false);
            var characters = 0;
            foreach (var item in items) {
                characters += item.Key.Length;
                foreach (var field in item.Fields) {
                    if (field.Value.Length > 4096) { throw new InvalidDataException("Delegated observation field exceeds disclosure budget."); }
                    characters = checked(characters + field.Key.Length + field.Value.Length);
                    if (characters > 65536) { throw new InvalidDataException("Delegated observation exceeds disclosure budget."); }
                }
            }
            return items;
        } finally { (credential as IDisposable)?.Dispose(); }
    }

    /// <summary>Releases an owned confidential-client credential. Borrowed test or deployment credentials remain caller-owned.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(ref m_disposed, 1) == 0 && m_ownsCredential) { (m_assertionCredential as IDisposable)?.Dispose(); }
    }

    private static AzureDelegatedObservation Bound(AzureDelegatedObservation row) {
        var settings = JsonNode.Parse(row.Settings.GetRawText())?.AsObject() ?? throw new ArgumentException("Observation settings must be an object.");
        static void Limit(JsonObject value, string name, int maximum) {
            if (value[name] is { } setting && (setting.GetValue<int>() is var size && (size < 1 || size > maximum))) {
                throw new ArgumentException($"Delegated observation {name} must be 1..{maximum}.");
            }
            value[name] ??= JsonValue.Create(maximum);
        }
        Limit(settings, "maximumResponseBytes", 65536);
        if (row.Kind == "inventory") { Limit(settings, "maximumPages", 8); }
        return row with { Settings = JsonElement.Parse(settings.ToJsonString()) };
    }

    private IWorldExtensionObservationSource Create(AzureDelegatedObservation row, TokenCredential credential) => row.Kind switch {
        "inventory" => new AzureResourceInventory(credential, ArmEnvironment.AzurePublicCloud, row.Settings, row.MaximumItems, m_transport),
        "metrics" => new AzureResourceMetrics(credential, ArmEnvironment.AzurePublicCloud, row.Settings, row.MaximumItems, m_transport),
        _ => throw new ArgumentException("Delegated observation kind must be inventory or metrics."),
    };
}

internal sealed record AzureDelegatedSettings(string ManagedIdentityClientId, AzureDelegatedObservation[] Observations);
internal sealed record AzureDelegatedObservation(string Name, string Kind, string[] Subjects, JsonElement Settings, int MaximumItems = 64);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AzureDelegatedSettings))]
internal sealed partial class AzureDelegatedJson : JsonSerializerContext;
