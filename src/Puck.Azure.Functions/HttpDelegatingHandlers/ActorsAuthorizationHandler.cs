using Azure.Core;
using System.Net.Http.Headers;

namespace Puck.Azure.Functions.HttpDelegatingHandlers;

public sealed class ActorsAuthorizationHandler(
    TokenCredential tokenCredential
) : DelegatingHandler
{
    // App-only token for the shared app registration; the silo requires the
    // "Actors.Invoke" app role, which is granted to this app's runtime identity.
    private static readonly TokenRequestContext TokenRequestContext = new(scopes: ["https://api.byteterrace.com/.default"]);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            scheme: "Bearer",
            parameter: (await tokenCredential.GetTokenAsync(
                cancellationToken: cancellationToken,
                requestContext: TokenRequestContext
            )).Token
        );

        return await base.SendAsync(request: request, cancellationToken: cancellationToken);
    }
}

