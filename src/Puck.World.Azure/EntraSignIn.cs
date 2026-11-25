using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Puck.World.Server;

namespace Puck.World.Azure;

/// <summary>
/// The <c>azure.api-users</c> sign-in: a player proves membership of the deployment's API user group with a delegated
/// Entra token for the deployment's API, and the issuer that receives it vouches for the player's own key under the
/// token's object ID. The token reaches only the issuer, over a transport the client pinned; no world ever sees it,
/// and it enters no document, checkpoint, log or simulation.
/// </summary>
/// <remarks>Settings: <c>tenantId</c>, <c>audience</c> (the API application ID), <c>groupId</c> (the user group a
/// token's <c>groups</c> claim must carry) and <c>scope</c> (the one delegated scope a token's <c>scp</c> claim must
/// carry). The client acquires its token from the ambient Azure sign-in with the API's <c>/.default</c> scope.</remarks>
public sealed class EntraSignIn : IWorldSignInVerifier, IWorldSignInCredential {
    private readonly TimeSpan m_acquisitionTimeout;
    private readonly string m_audience;
    private readonly TimeProvider m_clock;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> m_configuration;
    private readonly TokenCredential? m_credential;
    private readonly string m_group;
    private readonly string m_scope;
    private readonly string m_tenant;

    private AccessToken m_cachedToken;

    private readonly Lock m_tokenGate = new();
    private readonly JsonWebTokenHandler m_tokens = new() { MapInboundClaims = false, MaximumTokenSizeInBytes = 16384 };

    /// <summary>Initializes a new instance of the <see cref="EntraSignIn"/> class from its deployment-owned settings.</summary>
    /// <param name="settings">The <c>tenantId</c>, <c>audience</c>, <c>groupId</c> and <c>scope</c> settings.</param>
    /// <param name="client">Whether this instance acquires the current user's token; an issuer only validates.</param>
    /// <param name="credential">The client credential; <see langword="null"/> is the ambient Azure credential chain.</param>
    /// <param name="configuration">The issuer metadata provider, for isolated verification; <see langword="null"/>
    /// reads the tenant's published OpenID configuration.</param>
    /// <param name="timeProvider">The host clock the token acquisition and metadata deadlines, and the cached token's
    /// refresh window, run on; <see langword="null"/> is <see cref="TimeProvider.System"/>. A token's own lifetime is
    /// validated by the token handler on system time.</param>
    /// <exception cref="ArgumentException">A setting is unknown, missing or malformed.</exception>
    public EntraSignIn(JsonElement settings, bool client, TokenCredential? credential = null,
        IConfigurationManager<OpenIdConnectConfiguration>? configuration = null, TimeProvider? timeProvider = null) {
        m_clock = (timeProvider ?? TimeProvider.System);
        foreach (var property in settings.EnumerateObject()) {
            if (property.Name is not ("tenantId" or "audience" or "groupId" or "scope")) { throw new ArgumentException(message: $"Unknown API sign-in setting '{property.Name}'."); }
        }
        m_tenant = Guid.Parse(input: settings.GetProperty(propertyName: "tenantId").GetString()!).ToString(format: "D");
        m_group = Guid.Parse(input: settings.GetProperty(propertyName: "groupId").GetString()!).ToString(format: "D");
        m_audience = Guid.Parse(input: settings.GetProperty(propertyName: "audience").GetString()!).ToString(format: "D");
        m_scope = settings.GetProperty(propertyName: "scope").GetString()!;
        ArgumentException.ThrowIfNullOrWhiteSpace(m_scope);
        if (m_scope.Contains(value: ' ')) { throw new ArgumentException(message: "scope must name one delegated API scope."); }
        if (client) {
            // A player without a developer sign-in (no Azure CLI, no environment credential) signs in through the
            // system browser. The default chain excludes it, so it follows the chain here; its token cache persists so
            // a later start renews silently. Its default client is the Azure CLI's, which the deployment preauthorizes.
            m_credential = (credential ?? new ChainedTokenCredential(
                new DefaultAzureCredential(),
                new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions {
                    TenantId = m_tenant,
                    TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "puck.world.azure" },
                })
            ));
            m_acquisitionTimeout = ((credential is null) ? InteractiveTokenAcquisitionTimeout : TokenAcquisitionTimeout);
        }
        m_configuration = (configuration ?? new ConfigurationManager<OpenIdConnectConfiguration>(
            $"https://login.microsoftonline.com/{m_tenant}/v2.0/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever()
        ));
    }

    /// <summary>Gets how long, on the host clock, a client with a supplied credential waits for its user token.</summary>
    public static TimeSpan TokenAcquisitionTimeout { get; } = TimeSpan.FromSeconds(seconds: 15);
    /// <summary>Gets how long, on the host clock, a client on the default chain waits for its user token, which may
    /// mean a person signing in through the system browser.</summary>
    public static TimeSpan InteractiveTokenAcquisitionTimeout { get; } = TimeSpan.FromMinutes(minutes: 3);
    /// <summary>Gets how long, on the host clock, an issuer waits for the tenant's signing metadata.</summary>
    public static TimeSpan MetadataTimeout { get; } = TimeSpan.FromSeconds(seconds: 10);

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">This instance acquires no tokens, or the ambient credential
    /// produced none.</exception>
    public void Acquire(CancellationToken cancellationToken) => _ = Token(cancellationToken: cancellationToken);
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">This instance acquires no tokens, or the ambient credential
    /// produced none.</exception>
    public byte[] Present(ReadOnlySpan<byte> challenge, CancellationToken cancellationToken) => Encoding.UTF8.GetBytes(s: Token(cancellationToken: cancellationToken));

    // The cached delegated token, acquired again once less than two minutes of it remain.
    private string Token(CancellationToken cancellationToken) {
        if (m_credential is null) { throw new InvalidOperationException(message: "This host does not acquire user tokens."); }
        lock (m_tokenGate) {
            if (m_cachedToken.ExpiresOn <= m_clock.GetUtcNow().AddMinutes(minutes: 2)) {
                using var timeout = new CancellationTokenSource(delay: m_acquisitionTimeout, timeProvider: m_clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

                try {
                    m_cachedToken = m_credential.GetToken(new TokenRequestContext([$"api://{m_audience}/.default"]), linked.Token);
                } catch (AuthenticationFailedException error) {
                    throw new InvalidOperationException(message: $"azure.api-users could not obtain a user token: {error.Message}", innerException: error);
                }
            }
            return m_cachedToken.Token;
        }
    }
    /// <inheritdoc/>
    public async ValueTask<WorldSignIn?> VerifyAsync(ReadOnlyMemory<byte> credential, ReadOnlyMemory<byte> challenge, CancellationToken cancellationToken) {
        if ((m_credential is not null) || (credential.Length > m_tokens.MaximumTokenSizeInBytes)) { return null; }
        try {
            using var timeout = new CancellationTokenSource(MetadataTimeout, m_clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var metadata = await m_configuration.GetConfigurationAsync(linked.Token).ConfigureAwait(false);
            var validation = await m_tokens.ValidateTokenAsync(
                token: Encoding.UTF8.GetString(bytes: credential.Span),
                validationParameters: new TokenValidationParameters {
                    ClockSkew = TimeSpan.Zero,
                    IssuerSigningKeys = metadata.SigningKeys,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidAudience = m_audience,
                    ValidIssuer = $"https://login.microsoftonline.com/{m_tenant}/v2.0",
                    ValidateAudience = true,
                    ValidateIssuer = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                }
            ).ConfigureAwait(false);

            if (!validation.IsValid) {
                if (validation.Exception is SecurityTokenSignatureKeyNotFoundException) { m_configuration.RequestRefresh(); }
                return null;
            }
            var identity = validation.ClaimsIdentity;

            if (
                (identity.FindAll(type: "tid").SingleOrDefault()?.Value != m_tenant) ||
                !identity.FindAll(type: "groups").Any(predicate: claim => string.Equals(
                a: claim.Value,
                b: m_group,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) ||
                (identity.FindAll(type: "scp").SingleOrDefault()?.Value.Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: ' '
            ).Contains(
                m_scope,
                StringComparer.Ordinal
            ) != true) ||
                !Guid.TryParse(
                input: identity.FindAll(type: "oid").SingleOrDefault()?.Value,
                result: out var oid
            )
            ) { return null; }
            return new WorldSignIn(Subject: oid.ToString(format: "D"));
        } catch (Exception error) when ((error is SecurityTokenException or ArgumentException or InvalidOperationException or HttpRequestException or IOException or OperationCanceledException)) {
            return null;
        }
    }
}
