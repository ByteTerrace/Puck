using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Puck.Azure.Functions.Middleware;
using Puck.Azure.Functions.Services;

namespace Puck.Azure.Functions.HttpTriggers;

public sealed class GenerateBlobSasUri(
    IBlobSasUriService blobSasUriService,
    IUserCredentialContext userCredentialContext
)
{
    [FeatureGate(features: nameof(GenerateBlobSasUri))]
    [Function(name: nameof(GenerateBlobSasUri))]
    public async Task<IActionResult> Run(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "post",
            Route = "generate-blob-sas-uri"
        )] HttpRequest httpRequest,
        FunctionContext functionContext
    ) {
        var cancellationToken = functionContext.CancellationToken;
        var request = await httpRequest.ReadFromJsonAsync<GenerateBlobSasUriRequest>(cancellationToken: cancellationToken);

        ArgumentNullException.ThrowIfNull(argument: request);

        if (string.IsNullOrWhiteSpace(value: request.CorrelationId)) {
            request.CorrelationId = functionContext
                .GetCorrelationId()
                .ToString(format: "D");
        }

        return new OkObjectResult(value: new {
            SasUri = (await blobSasUriService
                .GenerateAsync(
                    cancellationToken: cancellationToken,
                    request: request,
                    tokenCredential: userCredentialContext.UserContext
                ))
                .ToString(),
        });
    }
}

