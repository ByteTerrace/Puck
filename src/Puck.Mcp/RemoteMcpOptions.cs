using System.Net;

namespace Puck.Mcp;

/// <summary>Explicit deployment and delegated-Operator policy for the remote MCP resource server.</summary>
public sealed record RemoteMcpOptions {
    /// <summary>Preserves optional deployment defaults during source-generated JSON construction.</summary>
    /// <param name="attachmentPath">A local capability path, or empty for an in-process target.</param>
    /// <param name="subjectClaim">The signed subject claim, defaulting to sub.</param>
    /// <param name="allowedOrigins">Additional browser origins; omitted means none.</param>
    /// <param name="idleTimeoutSeconds">Idle attachment lifetime, defaulting to five minutes.</param>
    [System.Text.Json.Serialization.JsonConstructor]
    public RemoteMcpOptions(string attachmentPath = "", string subjectClaim = "sub", string[]? allowedOrigins = null, int idleTimeoutSeconds = 300) {
        AttachmentPath = attachmentPath;
        SubjectClaim = subjectClaim;
        AllowedOrigins = allowedOrigins ?? [];
        IdleTimeoutSeconds = idleTimeoutSeconds;
    }
    /// <summary>The running World's local capability file, accessible only to the gateway's OS user.</summary>
    public string AttachmentPath { get; init; } = "";
    /// <summary>The exact Console target in an in-process host; mutually exclusive with attachmentPath.</summary>
    public string? Target { get; init; }
    /// <summary>Optional service settings interpreted by the trusted host composition. Changes require restart.</summary>
    public System.Text.Json.JsonElement? Services { get; init; }
    /// <summary>Optional authenticated reverse proxy. When set, only its signed origin requests may carry ClientAuthorization.</summary>
    public RemoteMcpProxyOptions? TrustedProxy { get; init; }
    /// <summary>The canonical externally reachable HTTPS resource URL, ending in /mcp.</summary>
    public required string PublicUrl { get; init; }
    /// <summary>An IP-literal listener URL, omitted when composed into an existing HTTP host. HTTP is permitted only on loopback behind a TLS proxy.</summary>
    public string? ListenUrl { get; init; }
    /// <summary>The exact HTTPS OAuth/OIDC issuer. For Entra use the tenant-specific v2.0 issuer.</summary>
    public required string Issuer { get; init; }
    /// <summary>The access-token audience registered for this API, never a downstream service audience.</summary>
    public required string Audience { get; init; }
    /// <summary>The delegated scope required in the signed scope or scp claim.</summary>
    public required string Scope { get; init; }
    /// <summary>The OAuth scope clients request; defaults to scope. Entra commonly needs api://application-id/scope here while scp contains only the short scope.</summary>
    public string? AuthorizationScope { get; init; }
    /// <summary>Subjects explicitly granted full Console and framebuffer authority for this World.</summary>
    public required string[] AllowedSubjects { get; init; }
    /// <summary>The subject claim: sub for standard OIDC, or oid with a required tenantId for Entra.</summary>
    public string SubjectClaim { get; init; } = "sub";
    /// <summary>An optional exact tid claim, required when subjectClaim is oid.</summary>
    public string? TenantId { get; init; }
    /// <summary>Additional HTTPS browser origins. The public resource's own origin is always allowed.</summary>
    public string[] AllowedOrigins { get; init; } = [];
    /// <summary>A server certificate PFX path, required for a direct HTTPS listener.</summary>
    public string? CertificatePath { get; init; }
    /// <summary>The environment variable containing the optional PFX password; no password is written to this document.</summary>
    public string? CertificatePasswordEnvironmentVariable { get; init; }
    /// <summary>Idle attachment lifetime in seconds, from 10 through 3600; active calls are governed by request deadlines.</summary>
    public int IdleTimeoutSeconds { get; init; } = 300;

    internal RemoteMcpOptions Validate(bool requireListener = true) {
        static Uri Https(string value, string name) {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) {
                throw new ArgumentException($"{name} must be an absolute HTTPS URL without credentials, query or fragment.");
            }
            return uri;
        }
        var resource = Https(PublicUrl, nameof(PublicUrl));
        if (resource.AbsolutePath != "/mcp") { throw new ArgumentException("publicUrl must end in /mcp."); }
        _ = Https(Issuer, nameof(Issuer));
        TrustedProxy?.Validate();
        if (requireListener && (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var listen) || listen.Scheme is not ("http" or "https") || !IPAddress.TryParse(listen.Host.Trim('[', ']'), out var address) ||
            listen.AbsolutePath != "/" || !string.IsNullOrEmpty(listen.UserInfo) || !string.IsNullOrEmpty(listen.Query) || !string.IsNullOrEmpty(listen.Fragment) ||
            (listen.Scheme == "http" && !IPAddress.IsLoopback(address)))) { throw new ArgumentException("listenUrl must use an IP literal; plaintext HTTP is restricted to loopback behind a TLS proxy."); }
        if (requireListener && new Uri(ListenUrl!).Scheme == "https" && string.IsNullOrWhiteSpace(CertificatePath)) { throw new ArgumentException("A direct HTTPS listener requires certificatePath."); }
        if (string.IsNullOrWhiteSpace(Audience) || (!string.IsNullOrWhiteSpace(AttachmentPath) && !string.IsNullOrWhiteSpace(Target)) ||
            (string.IsNullOrWhiteSpace(AttachmentPath) && string.IsNullOrWhiteSpace(Target) && Services is null)) { throw new ArgumentException("audience and one attachment source or an explicit service composition are required."); }
        if (string.IsNullOrEmpty(Scope) || Scope.Any(c => c is < '!' or > '~' or '"' or '\\')) { throw new ArgumentException("scope must be one OAuth scope token."); }
        if (AuthorizationScope is { } requested && (requested.Length == 0 || requested.Any(c => c is < '!' or > '~' or '"' or '\\'))) { throw new ArgumentException("authorizationScope must be one OAuth scope token."); }
        if (SubjectClaim is not ("sub" or "oid") || (SubjectClaim == "oid" && !Guid.TryParse(TenantId, out _))) { throw new ArgumentException("subjectClaim must be sub, or oid with an explicit tenantId UUID."); }
        if (AllowedSubjects is null || AllowedSubjects.Length > 64 || AllowedSubjects.Any(string.IsNullOrWhiteSpace)) { throw new ArgumentException("Explicit allowedSubjects are required (0..64); an empty list denies all callers."); }
        if (AllowedOrigins is null || AllowedOrigins.Length > 16) { throw new ArgumentException("At most 16 additional browser origins are allowed."); }
        foreach (var origin in AllowedOrigins) {
            var parsed = Https(origin, nameof(AllowedOrigins));
            if (parsed.AbsolutePath != "/") { throw new ArgumentException("Browser origins cannot contain paths."); }
        }
        if (IdleTimeoutSeconds is < 10 or > 3600) { throw new ArgumentOutOfRangeException(nameof(IdleTimeoutSeconds)); }
        // Callers cannot mutate the live authorization policy by retaining an array from configuration.
        return this with { AllowedSubjects = [.. AllowedSubjects], AllowedOrigins = [.. AllowedOrigins] };
    }
}
