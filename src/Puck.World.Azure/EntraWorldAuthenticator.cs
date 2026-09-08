using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Puck.Networking;

namespace Puck.World.Azure;

/// <summary>Authenticates API users at the federation boundary. Azure tokens never enter world documents or simulation.</summary>
public sealed class EntraWorldAuthenticator : IAuthenticator, IRemoteIdentityVerifier {
    private const string Prefix = "puck.entra.v1\n";

    private readonly string m_tenant;
    private readonly string m_audience;
    private readonly string m_group;
    private readonly string m_scope;
    private readonly string? m_remoteKey;
    private readonly TokenCredential? m_credential;
    private readonly IAuthenticator? m_federation;

    private string? m_userId;

    private readonly string m_session = Guid.NewGuid().ToString(format: "N");
    private readonly Lock m_tokenGate = new();

    private AccessToken m_cachedToken;

    private readonly IConfigurationManager<OpenIdConnectConfiguration> m_configuration;

    private readonly JsonWebTokenHandler m_tokens = new() { MapInboundClaims = false, MaximumTokenSizeInBytes = 16384 };

    /// <summary>Creates the installed extension from its deployment-owned settings.</summary>
    /// <param name="settings">tenantId, audience, groupId, scope; clients additionally require remoteKeyHash.</param>
    /// <param name="client">Whether to acquire the current user's API token. Servers only validate tokens.</param>
    /// <param name="federation">The world's existing attestation authenticator, retained for explicitly trusted world peers.</param>
    /// <param name="credential">Optional client credential; defaults to the existing ambient Azure credential chain.</param>
    /// <param name="configuration">Optional issuer metadata provider for isolated verification.</param>
    public EntraWorldAuthenticator(JsonElement settings, bool client, IAuthenticator? federation = null, TokenCredential? credential = null,
        IConfigurationManager<OpenIdConnectConfiguration>? configuration = null) {
        foreach (var property in settings.EnumerateObject()) {
            if (property.Name is not ("tenantId" or "audience" or "groupId" or "scope" or "remoteKeyHash")) { throw new ArgumentException(message: $"Unknown API authentication setting '{property.Name}'."); }
        }
        m_tenant = Guid.Parse(input: settings.GetProperty(propertyName: "tenantId").GetString()!).ToString(format: "D");
        m_group = Guid.Parse(input: settings.GetProperty(propertyName: "groupId").GetString()!).ToString(format: "D");
        m_audience = Guid.Parse(input: settings.GetProperty(propertyName: "audience").GetString()!).ToString(format: "D");
        m_scope = settings.GetProperty(propertyName: "scope").GetString()!;
        ArgumentException.ThrowIfNullOrWhiteSpace(m_scope);
        if (m_scope.Contains(value: ' ')) { throw new ArgumentException(message: "scope must name one delegated API scope."); }
        if (client) {
            m_remoteKey = settings.GetProperty(propertyName: "remoteKeyHash").GetString();
            if ((m_remoteKey is null) || (m_remoteKey.Length != 64) || !m_remoteKey.All(predicate: static c => char.IsAsciiHexDigitLower(c: c))) { throw new ArgumentException(message: "A client requires the server's lowercase SHA-256 peer-key fingerprint."); }
            m_credential = (credential ?? new DefaultAzureCredential());
        }
        m_federation = federation;
        m_configuration = (configuration ?? new ConfigurationManager<OpenIdConnectConfiguration>(
            $"https://login.microsoftonline.com/{m_tenant}/v2.0/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever()));
    }

    /// <inheritdoc/>
    public int ChallengeBytes => 32;
    /// <inheritdoc/>
    public bool IsConfigured => true;

    /// <inheritdoc/>
    public byte[] NewChallenge() => RandomNumberGenerator.GetBytes(count: ChallengeBytes);
    /// <inheritdoc/>
    public bool AcceptsRemoteIdentity(string keyHash) => ((m_credential is null) || string.Equals(a: m_remoteKey, b: keyHash, comparisonType: StringComparison.Ordinal));
    /// <summary>Names this client session beneath the ambient API user's object ID.</summary>
    /// <returns>The account-scoped session namespace subsequently verified by the server. Transport reconnects retain it.</returns>
    public string UserIdentity() => $"{UserId(token: ReadClientToken())}/{m_session}";

    private string ReadClientToken() {
        if (m_credential is null) { throw new InvalidOperationException(message: "This host does not acquire user tokens."); }
        lock (m_tokenGate) {
            if (m_cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(minutes: 2)) { return m_cachedToken.Token; }
            using var timeout = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 15));
            var access = m_credential.GetToken(new TokenRequestContext([$"api://{m_audience}/.default"]), timeout.Token);
            var token = access.Token;
            var subject = UserId(token: token);
            var previous = Interlocked.CompareExchange(comparand: null, location1: ref m_userId, value: subject);

            if ((previous is not null) && (previous != subject)) { throw new InvalidOperationException(message: "The ambient user changed; reconnect with a fresh world session."); }
            m_cachedToken = access;
            return token;
        }
    }
    private string UserId(string token) {
        var jwt = m_tokens.ReadJsonWebToken(token: token);

        if (!jwt.TryGetClaim(key: "oid", value: out var claim) || !Guid.TryParse(input: claim.Value, result: out var id)) { throw new InvalidOperationException(message: "The ambient API credential is not a user identity."); }
        return id.ToString(format: "D");
    }

    /// <inheritdoc/>
    public byte[] Prove(ReadOnlySpan<byte> challenge) {
        if (m_credential is null) { return (m_federation?.Prove(challenge: challenge) ?? throw new InvalidOperationException(message: "No signing credential is installed.")); }
        if (challenge.Length != ChallengeBytes) { throw new ArgumentException(message: "Invalid federation challenge."); }
        return Encoding.UTF8.GetBytes(s: (((((Prefix + Convert.ToBase64String(challenge)) + "\n") + m_session) + "\n") + ReadClientToken()));
    }
    /// <inheritdoc/>
    public bool TryVerify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof, out string? sourceAuthority) {
        sourceAuthority = null;
        if (proof.Length > 18000) { return false; }
        var wire = Encoding.UTF8.GetString(bytes: proof);

        if (!wire.StartsWith(comparisonType: StringComparison.Ordinal, value: Prefix)) { return (m_federation?.TryVerify(challenge: challenge, proof: proof, sourceAuthority: out sourceAuthority) == true); }
        if ((m_credential is not null) || (challenge.Length != ChallengeBytes)) { return false; }
        var parts = wire[Prefix.Length..].Split('\n');

        if ((parts.Length != 3) || (parts[0] != Convert.ToBase64String(challenge)) || !Guid.TryParseExact(parts[1], "N", out var session)) { return false; }
        try {
            using var timeout = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 10));
            var metadata = m_configuration.GetConfigurationAsync(cancel: timeout.Token).GetAwaiter().GetResult();
            var validation = m_tokens.ValidateTokenAsync(token: parts[2], validationParameters: new TokenValidationParameters {
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
            }).GetAwaiter().GetResult();

            if (!validation.IsValid) {
                if (validation.Exception is SecurityTokenSignatureKeyNotFoundException) { m_configuration.RequestRefresh(); }
                return false;
            }
            var identity = validation.ClaimsIdentity;

            if ((identity.FindAll(type: "tid").SingleOrDefault()?.Value != m_tenant) ||
                !identity.FindAll(type: "groups").Any(predicate: claim => string.Equals(a: claim.Value, b: m_group, comparisonType: StringComparison.OrdinalIgnoreCase)) ||
                (identity.FindAll(type: "scp").SingleOrDefault()?.Value.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ').Contains(m_scope, StringComparer.Ordinal) != true) ||
                !Guid.TryParse(input: identity.FindAll(type: "oid").SingleOrDefault()?.Value, result: out var oid)) { return false; }
            sourceAuthority = $"{oid:D}/{session:N}";
            return true;
        } catch (Exception error) when ((error is SecurityTokenException or ArgumentException or InvalidOperationException or HttpRequestException or OperationCanceledException)) {
            return false;
        }
    }
}
