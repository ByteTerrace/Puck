using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.FeatureManagement;
using Puck.Azure.Functions.Middleware;

namespace Puck.Azure.Functions.HttpTriggers;

public sealed record class FeatureCheckResponse(
    string AuthenticationType,
    IEnumerable<string> Features,
    bool IsAuthenticated,
    string ObjectId
);

public sealed class FeatureCheck(IFeatureManagerSnapshot featureManagerSnapshot)
{
    [FeatureGate(features: nameof(FeatureCheck))]
    [Function(name: nameof(FeatureCheck))]
    public async Task<IActionResult> Run(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "get",
            Route = "feature-check"
        )] HttpRequest httpRequest,
        FunctionContext functionContext
    ) {
        var cancellationToken = functionContext.CancellationToken;
        var features = new HashSet<string>();
        var identity = httpRequest.HttpContext.User.Identity;

        await foreach (var name in featureManagerSnapshot
            .GetFeatureNamesAsync()
            .WithCancellation(cancellationToken: cancellationToken)
        ) {
            if (await featureManagerSnapshot.IsEnabledAsync(feature: name)) {
                features.Add(item: name);
            }
        }

        return new OkObjectResult(value: new FeatureCheckResponse(
            AuthenticationType: (identity?.AuthenticationType ?? "none"),
            Features: features,
            IsAuthenticated: (identity?.IsAuthenticated ?? false),
            ObjectId: (identity?.Name ?? Guid.Empty.ToString())
        ));
    }
}

