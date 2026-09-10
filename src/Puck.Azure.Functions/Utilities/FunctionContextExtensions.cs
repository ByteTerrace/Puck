using Microsoft.Azure.Functions.Worker;

namespace Puck.Azure.Functions.Utilities;

internal static class FunctionContextExtensions {
    /// <summary>
    /// Returns the lower-cased object id of the delegated user behind the request, or <c>null</c> when
    /// the caller is not one. App-only tokens (the Front Door origin identity, which the worker's
    /// JwtBearer fallback accepts on anonymous edge requests) carry no scope claim, and an owner-scoped
    /// operation must only ever run for a delegated user principal.
    /// </summary>
    public static string? GetDelegatedUserObjectId(this FunctionContext functionContext) {
        var user = functionContext
            .GetHttpContext()!
            .User;
        var hasScopes = user
            .Claims
            .Any(predicate: static claim =>
                (("scp" == claim.Type) ||
                ("http://schemas.microsoft.com/identity/claims/scope" == claim.Type))
            );

        return (hasScopes
            ? user
                .Identity
                ?.Name
                ?.ToLowerInvariant()
            : null);
    }
}
