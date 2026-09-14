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

/// <summary>Request-confined Entra OBO access to existing onboarding, inventory, and metrics services.</summary>
public sealed class AzureDelegatedServices : IDisposable {
    private readonly string m_application;
    private readonly TokenCredential m_assertionCredential;
    private readonly HttpClient m_http;
    private readonly FrozenDictionary<string, AzureDelegatedObservation> m_observations;
    private readonly Uri? m_onboarding;
    private readonly bool m_ownsCredential;
    private readonly string m_tenant;
    private readonly HttpPipelineTransport? m_transport;

    private int m_disposed;

    /// <summary>Names installed by the trusted deployment; providers and credentials are never returned.</summary>
    public IReadOnlyCollection<string> Names => m_observations.Keys;
    /// <summary>Whether the existing platform onboarding API is configured.</summary>
    public bool SupportsOnboarding => (m_onboarding is not null);

    private static AzureDelegatedObservation Bound(AzureDelegatedObservation row) {
        var settings = (JsonNode.Parse(row.Settings.GetRawText())?.AsObject() ?? throw new ArgumentException(message: "Observation settings must be an object."));

        static void Limit(JsonObject value, string name, int maximum) {
            if (
                (value[name] is { } setting) &&
                ((setting.GetValue<int>() is var size) && ((size < 1) || (size > maximum)))
            ) {
                throw new ArgumentException(message: $"Delegated observation {name} must be 1..{maximum}.");
            }
            value[name] ??= JsonValue.Create(maximum);
        }
        Limit(
            maximum: 65536,
            name: "maximumResponseBytes",
            value: settings
        );
        if (row.Kind == "inventory") { Limit(
            maximum: 8,
            name: "maximumPages",
            value: settings
        ); }
        return row with { Settings = JsonElement.Parse(settings.ToJsonString()) };
    }
    private IWorldExtensionObservationSource Create(AzureDelegatedObservation row, TokenCredential credential) => row.Kind switch {
        "inventory" => new AzureResourceInventory(
        credential,
        ArmEnvironment.AzurePublicCloud,
        row.Settings,
        row.MaximumItems,
        m_transport
    ),
        "metrics" => new AzureResourceMetrics(
        credential,
        ArmEnvironment.AzurePublicCloud,
        row.Settings,
        row.MaximumItems,
        m_transport
    ),
        _ => throw new ArgumentException(message: "Delegated observation kind must be inventory or metrics."),
    };
    private OnBehalfOfCredential CreateCredential(string userAssertion) {
        var options = new OnBehalfOfCredentialOptions { DisableInstanceDiscovery = true };

        options.Diagnostics.IsLoggingEnabled = false;
        if (m_transport is not null) { options.Transport = m_transport; }
        return new(
            m_tenant,
            m_application,
            async token => (await m_assertionCredential.GetTokenAsync(
                new(["api://AzureADTokenExchange"]),
                token
            ).ConfigureAwait(continueOnCapturedContext: false)).Token,
            userAssertion,
            options
        );
    }

    /// <summary>Releases an owned confidential-client credential. Borrowed test or deployment credentials remain caller-owned.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) != 0) { return; }
        m_http.Dispose();
        if (m_ownsCredential) { (m_assertionCredential as IDisposable)?.Dispose(); }
    }
    /// <summary>Deposits fresh user escrow through the existing idempotent onboarding endpoint, using an OBO token. Never provisions users locally.</summary>
    /// <param name="userAssertion">The validated MCP API access token.</param>
    /// <param name="expiresAt">Its validated expiry.</param>
    /// <param name="cancellationToken">The request lifetime.</param>
    /// <returns>Ready, Migrating, or Onboarding, as reported by the platform.</returns>
    public async ValueTask<string> EnsureOnboardedAsync(string userAssertion, DateTimeOffset expiresAt, CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(
            condition: (Volatile.Read(location: ref m_disposed) != 0),
            instance: this
        );
        if (m_onboarding is null) { throw new InvalidOperationException(message: "Platform onboarding is not configured."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(userAssertion);
        var remaining = (expiresAt - DateTimeOffset.UtcNow);

        if (remaining <= TimeSpan.Zero) { throw new AuthenticationFailedException(message: "The delegated assertion has expired."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

        deadline.CancelAfter(delay: ((remaining < TimeSpan.FromSeconds(seconds: 120))
            ? remaining
            : TimeSpan.FromSeconds(seconds: 120)));
        var credential = CreateCredential(userAssertion: userAssertion);

        try {
            // Entra requires the GUID resource when the confidential client and API share an application (AADSTS90009).
            var access = await credential.GetTokenAsync(
                new([$"{m_application}/.default"]),
                deadline.Token
            ).ConfigureAwait(continueOnCapturedContext: false);
            using var request = new HttpRequestMessage(
                method: HttpMethod.Post,
                requestUri: m_onboarding
            );

            request.Headers.Authorization = new(
                "Bearer",
                access.Token
            );
            using var response = await m_http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token
            ).ConfigureAwait(continueOnCapturedContext: false);

            AzureDelegatedAuthenticationException.ThrowIfChallenge(
                ((int)response.StatusCode),
                response.Headers.WwwAuthenticate.ToString()
            );
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) { throw new UnauthorizedAccessException(message: "Platform onboarding access was refused."); }
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(
                4096,
                deadline.Token
            ).ConfigureAwait(continueOnCapturedContext: false);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken: deadline.Token).ConfigureAwait(continueOnCapturedContext: false));

            if (
                (json.RootElement.ValueKind != JsonValueKind.Object) ||
                (!json.RootElement.TryGetProperty(
                propertyName: "State",
                value: out var value
            ) && !json.RootElement.TryGetProperty(
                propertyName: "state",
                value: out value
            )) ||
                (value.ValueKind != JsonValueKind.String)
            ) { throw new InvalidDataException(message: "Missing platform onboarding state."); }
            var state = value.GetString();

            return ((state is "Ready" or "Migrating" or "Onboarding")
                ? state
                : throw new InvalidDataException(message: "Unknown platform onboarding state.")
            );
        } catch (AuthenticationFailedException error) when ((AzureDelegatedAuthenticationException.From(error: error) is { } challenge)) {
            throw challenge;
        } finally { (credential as IDisposable)?.Dispose(); }
    }
    /// <summary>Returns only observation names explicitly granted to this subject.</summary>
    /// <param name="subject">Validated subject in this service's tenant.</param>
    /// <returns>Ordinally ordered observation names, never their resource settings.</returns>
    public string[] NamesFor(string subject) => m_observations.Values.Where(predicate: row => row.Subjects.Contains(
        subject,
        StringComparer.Ordinal
    )).Select(selector: row => row.Name).Order(comparer: StringComparer.Ordinal).ToArray();
    /// <summary>Exchanges this validated user's API assertion and reads only an approved observation. No token or assertion is persisted.</summary>
    /// <param name="name">An installed observation name.</param>
    /// <param name="subject">The validated oid in this instance's tenant.</param>
    /// <param name="userAssertion">The validated API access token, never a downstream token.</param>
    /// <param name="expiresAt">The validated assertion's expiration.</param>
    /// <param name="cancellationToken">Request deadline, revocation, or disconnect.</param>
    /// <returns>A complete bounded disclosure snapshot.</returns>
    public async ValueTask<IReadOnlyList<WorldExtensionObservationItem>> ReadAsync(string name, string subject, string userAssertion,
        DateTimeOffset expiresAt, CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(
            condition: (Volatile.Read(location: ref m_disposed) != 0),
            instance: this
        );
        if (
            !m_observations.TryGetValue(
            key: name,
            value: out var row
        ) ||
            !row.Subjects.Contains(
            subject,
            StringComparer.Ordinal
        )
        ) { throw new UnauthorizedAccessException(message: "No delegated observation grant for this caller."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(userAssertion);
        var remaining = (expiresAt - DateTimeOffset.UtcNow);

        if (remaining <= TimeSpan.Zero) { throw new AuthenticationFailedException(message: "The delegated assertion has expired."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

        deadline.CancelAfter(delay: ((remaining < TimeSpan.FromSeconds(seconds: 120))
            ? remaining
            : TimeSpan.FromSeconds(seconds: 120)));
        var credential = CreateCredential(userAssertion: userAssertion);

        try {
            using var source = Create(
                credential: credential,
                row: row
            );
            var items = await source.ReadAsync(cancellationToken: deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
            var characters = 0;

            foreach (var item in items) {
                characters += item.Key.Length;
                foreach (var field in item.Fields) {
                    if (field.Value.Length > 4096) { throw new InvalidDataException(message: "Delegated observation field exceeds disclosure budget."); }
                    characters = checked(((characters + field.Key.Length) + field.Value.Length));
                    if (characters > 65536) { throw new InvalidDataException(message: "Delegated observation exceeds disclosure budget."); }
                }
            }
            return items;
        } catch (AuthenticationFailedException error) when ((AzureDelegatedAuthenticationException.From(error: error) is { } challenge)) {
            throw challenge;
        } finally { (credential as IDisposable)?.Dispose(); }
    }

    /// <summary>Validates a deployment without network I/O. The ingress must validate tenant-specific v2 tokens for this application before calling ReadAsync.</summary>
    /// <param name="tenantId">Exact Entra tenant accepted by the ingress.</param>
    /// <param name="applicationId">The API application ID, which is also the validated inbound audience.</param>
    /// <param name="settings">Explicit managed identity and observation grants.</param>
    /// <param name="assertionCredential">Optional trusted confidential-client credential; defaults to the explicitly configured managed identity.</param>
    /// <param name="transport">Optional trusted test transport for token exchange and ARM. Must not follow redirects.</param>
    /// <param name="onboardingHandler">Optional trusted test handler for the onboarding endpoint. Must not follow redirects.</param>
    public AzureDelegatedServices(string tenantId, string applicationId, JsonElement settings,
        TokenCredential? assertionCredential = null, HttpPipelineTransport? transport = null, HttpMessageHandler? onboardingHandler = null) {
        m_tenant = Guid.Parse(input: tenantId).ToString(format: "D");
        m_application = Guid.Parse(input: applicationId).ToString(format: "D");
        var configuration = (settings.Deserialize(AzureDelegatedJson.Default.AzureDelegatedSettings) ?? throw new ArgumentException(message: "Delegated observation settings are required."));

        if (configuration.Observations.Length > 16) { throw new ArgumentException(message: "Configure at most 16 delegated observations."); }
        if (configuration.OnboardingUrl is { } onboarding) {
            if (
                !Uri.TryCreate(
                onboarding,
                UriKind.Absolute,
                out m_onboarding
            ) ||
                (m_onboarding.Scheme != "https") ||
                (m_onboarding.AbsolutePath != "/api/self-onboard") ||
                !string.IsNullOrEmpty(value: m_onboarding.UserInfo) ||
                !string.IsNullOrEmpty(value: m_onboarding.Query) ||
                !string.IsNullOrEmpty(value: m_onboarding.Fragment)
            ) {
                throw new ArgumentException(message: "onboardingUrl must be the existing HTTPS /api/self-onboard endpoint.");
            }
        }
        m_assertionCredential = (assertionCredential ?? new ManagedIdentityCredential(id: ManagedIdentityId.FromUserAssignedClientId(id: Guid.Parse(configuration.ManagedIdentityClientId).ToString(format: "D"))));
        m_ownsCredential = (assertionCredential is null);
        m_http = new HttpClient(handler: (onboardingHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(minutes: 5) })) { Timeout = Timeout.InfiniteTimeSpan };
        m_transport = new ChallengeTransport(inner: (transport ?? new HttpClientTransport(client: m_http)));
        try {
            m_observations = configuration.Observations.Select(Bound).ToFrozenDictionary(
                row => row.Name,
                StringComparer.Ordinal
            );
            foreach (var row in m_observations.Values) {
                if (
                    !SafeName.TryParse(
                    candidate: row.Name,
                    name: out _,
                    reason: out _
                ) ||
                    (row.Subjects.Length is < 1 or > 64) ||
                    row.Subjects.Any(predicate: value => !Guid.TryParse(
                    input: value,
                    result: out _
                )) ||
                    (row.MaximumItems is < 1 or > 128)
                ) { throw new ArgumentException(message: "Observations need a valid name, 1..64 Entra object IDs, and 1..128 items."); }
                // Construction validates the existing adapter's resource binding and field allowlist without acquiring a token.
                using var source = Create(
                    credential: m_assertionCredential,
                    row: row
                );
            }
        } catch {
            m_http.Dispose();
            if (m_ownsCredential) { (m_assertionCredential as IDisposable)?.Dispose(); }
            throw;
        }
    }

    // Surface downstream challenges before the SDK's bearer policy can silently retry a user operation.
    // Token endpoint 400 responses remain with MSAL, which classifies interaction-required errors.
    private sealed class ChallengeTransport(HttpPipelineTransport inner) : HttpPipelineTransport {
        private static void Check(HttpMessage message) => AzureDelegatedAuthenticationException.ThrowIfChallenge(
            message.Response.Status,
            (message.Response.Headers.TryGetValue(
                name: "WWW-Authenticate",
                value: out var header
            )
            ? header
            : null)
        );

        public override Request CreateRequest() => inner.CreateRequest();
        public override void Process(HttpMessage message) { inner.Process(message: message); Check(message: message); }
        public override async ValueTask ProcessAsync(HttpMessage message) { await inner.ProcessAsync(message: message).ConfigureAwait(continueOnCapturedContext: false); Check(message: message); }
    }
}

internal sealed record AzureDelegatedSettings(string ManagedIdentityClientId, AzureDelegatedObservation[] Observations, string? OnboardingUrl = null);
internal sealed record AzureDelegatedObservation(string Name, string Kind, string[] Subjects, JsonElement Settings, int MaximumItems = 64);
[JsonSerializable(typeof(AzureDelegatedSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
internal sealed partial class AzureDelegatedJson : JsonSerializerContext;
