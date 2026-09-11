using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Graph;
using System.Net;
using Puck.Azure.Functions.Services;
using Puck.Azure.Functions.Utilities;

namespace Puck.Azure.Functions.HttpTriggers;

public sealed class Shares(
    IBlobSasUriService blobSasUriService,
    GraphServiceClient graphServiceClient,
    IUserCredentialContext userCredentialContext
) {
    public sealed class CreateShareRequest {
        public string? BlobName { get; set; }
        public DateTimeOffset? ExpiresOn { get; set; }
        public string? RecipientIdentifier { get; set; }
    }

    // Stateless: the returned SAS is user-bound (sduoid) — storage only honors
    // it when the request also carries a bearer token whose oid matches the
    // resolved recipient, so possession of the link alone grants nothing.
    [Function(name: nameof(CreateShare))]
    public async Task<HttpResponseData> CreateShare(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "post",
            Route = "shares"
        )] HttpRequestData httpRequestData,
        FunctionContext functionContext
    ) {
        var cancellationToken = functionContext.CancellationToken;
        var sharerObjectId = functionContext.GetDelegatedUserObjectId();

        if (sharerObjectId is null) {
            return httpRequestData.CreateResponse(statusCode: HttpStatusCode.Forbidden);
        }

        var request = await httpRequestData.ReadFromJsonAsync<CreateShareRequest>(cancellationToken: cancellationToken);
        var blobName = request?.BlobName?.Trim();
        var recipientIdentifier = request?.RecipientIdentifier?.Trim();

        if (string.IsNullOrWhiteSpace(value: blobName) ||
            string.IsNullOrWhiteSpace(value: recipientIdentifier) ||
            recipientIdentifier.Contains(value: '\'')
        ) {
            return httpRequestData.CreateResponse(statusCode: HttpStatusCode.BadRequest);
        }

        var matches = await graphServiceClient
            .Users
            .GetAsync(
                cancellationToken: cancellationToken,
                requestConfiguration: requestConfiguration => {
                    requestConfiguration.QueryParameters.Filter =
                        $"mail eq '{recipientIdentifier}' or userPrincipalName eq '{recipientIdentifier}'";
                    requestConfiguration.QueryParameters.Select = ["displayName", "id"];
                    requestConfiguration.QueryParameters.Top = 1;
                }
            );
        var recipient = matches?.Value?.FirstOrDefault();

        if (recipient?.Id is null) {
            return httpRequestData.CreateResponse(statusCode: HttpStatusCode.NotFound);
        }

        var sasUri = await blobSasUriService.GenerateAsync(
            cancellationToken: cancellationToken,
            request: new() {
                BlobName = blobName,
                ContainerName = sharerObjectId,
                CorrelationId = functionContext
                    .GetCorrelationId()
                    .ToString(format: "D"),
                DelegatedUserObjectId = recipient.Id.ToLowerInvariant(),
                ExpiresOn = (request!.ExpiresOn ?? DateTimeOffset.UtcNow.AddDays(days: 7d)),
                // Explicitly opt out of the configured default preauthorized
                // agent; recipient binding is carried by DelegatedUserObjectId.
                PreauthorizedAgentObjectId = "",
            },
            tokenCredential: userCredentialContext.UserContext
        );
        var response = httpRequestData.CreateResponse();

        await response.WriteAsJsonAsync(
            cancellationToken: cancellationToken,
            instance: new {
                RecipientDisplayName = (recipient.DisplayName ?? recipientIdentifier),
                SasUri = sasUri.ToString(),
            }
        );

        return response;
    }
}

