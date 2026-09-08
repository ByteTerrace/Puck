using System.Text;
using System.Text.Json;

namespace Puck.Mcp;

/// <summary>A trusted service's request for interactive user authorization, translated to an HTTP bearer challenge.</summary>
public sealed class RemoteMcpAuthorizationException : Exception {
    /// <summary>Creates a bounded authorization challenge. The resource server retains its configured issuer and scope.</summary>
    /// <param name="claims">Optional JSON claims request; never a raw header or token.</param>
    public RemoteMcpAuthorizationException(string? claims = null) : base("User authorization is required before this operation can proceed.") {
        if (claims is not null) {
            if (Encoding.UTF8.GetByteCount(claims) > 4096) { throw new ArgumentException("Claims challenge exceeds its byte budget.", nameof(claims)); }
            using var document = JsonDocument.Parse(claims);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new ArgumentException("Claims challenge must be a JSON object.", nameof(claims)); }
            EncodedClaims = Convert.ToBase64String(Encoding.UTF8.GetBytes(document.RootElement.GetRawText()));
        }
    }
    internal string? EncodedClaims { get; }
}
