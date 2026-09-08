using Azure.Identity;
using Microsoft.Identity.Client;
using System.Text;
using System.Text.Json;

namespace Puck.World.Azure;

/// <summary>A downstream user authentication challenge. It contains no access token, authority override, or downstream scope.</summary>
public sealed class AzureDelegatedAuthenticationException : AuthenticationFailedException {
    /// <summary>Creates a challenge requiring user interaction.</summary>
    /// <param name="claims">Optional JSON claims request from the trusted downstream service or Entra.</param>
    public AzureDelegatedAuthenticationException(string? claims = null) : base("Delegated access requires user interaction.") {
        if (claims is not null) {
            if (Encoding.UTF8.GetByteCount(claims) > 4096) { throw new InvalidDataException("Claims challenge exceeds its byte budget."); }
            using var document = JsonDocument.Parse(claims);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("Claims challenge must be a JSON object."); }
            Claims = document.RootElement.GetRawText();
        }
    }
    /// <summary>The validated JSON claims request, or null when consent/sign-in alone is required.</summary>
    public string? Claims { get; }

    internal static AzureDelegatedAuthenticationException? From(AuthenticationFailedException error) {
        if (error is AzureDelegatedAuthenticationException challenge) { return challenge; }
        for (var current = error.InnerException; current is not null; current = current.InnerException) {
            if (current is MsalUiRequiredException ui) { return new(ui.Claims); }
        }
        return null;
    }

    internal static void ThrowIfChallenge(int status, string? header) {
        if (status is not (401 or 403)) { return; }
        if (header is { Length: > 8192 }) { throw new InvalidDataException("Authentication challenge exceeds its header budget."); }
        if (!string.IsNullOrEmpty(header)) {
            if (header.Any(c => c is '\r' or '\n')) { throw new InvalidDataException("Invalid authentication header."); }
            using var response = new HttpResponseMessage();
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", header);
            var claims = WwwAuthenticateParameters.GetClaimChallengeFromResponseHeaders(response.Headers, "Bearer");
            if (!string.IsNullOrEmpty(claims)) {
                throw new AzureDelegatedAuthenticationException(claims.AsSpan().TrimStart().StartsWith("{") ? claims : Encoding.UTF8.GetString(Convert.FromBase64String(claims)));
            }
        }
        if (status == 401) { throw new AzureDelegatedAuthenticationException(); }
    }
}
