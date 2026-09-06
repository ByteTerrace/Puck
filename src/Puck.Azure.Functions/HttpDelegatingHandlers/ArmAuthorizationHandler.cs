using Azure.Core;
using System.Net.Http.Headers;

namespace Puck.Azure.Functions.HttpDelegatingHandlers;

public sealed class ArmAuthorizationHandler(
    TokenCredential tokenCredential
) : DelegatingHandler
{
    private static readonly TokenRequestContext TokenRequestContext = new(scopes: ["https://management.azure.com/.default"]);

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

