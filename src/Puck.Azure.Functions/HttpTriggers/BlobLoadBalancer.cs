using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Diagnostics.CodeAnalysis;
using Puck.Azure.Functions.Middleware;
using Puck.Maths;

namespace Puck.Azure.Functions.HttpTriggers;

public sealed class BlobLoadBalancer
{
    private static bool TryValidatePartitionerInputs(
        IQueryCollection query,
        [NotNullWhen(returnValue: false)]
        out IActionResult? errorAction,
        out int bucketCount,
        out Guid value
    ) {
        var isValid = true;

        errorAction = default;
        value = default;

        if (
            !int.TryParse(
                result: out bucketCount,
                s: query["bucketCount"]
            )
            || (bucketCount is < 1 or > 1024)
        ) {
            errorAction = new BadRequestObjectResult(error: "bucketCount must be between 1 and 1024");
            isValid = false;
        }
        else if (!Guid.TryParseExact(
            input: query["value"],
            format: "D",
            result: out value
        )) {
            errorAction = new BadRequestObjectResult(error: "value must be a valid GUID");
            isValid = false;
        }

        return isValid;
    }

    [FeatureGate(features: nameof(BlobLoadBalancer))]
    [Function(name: $"{nameof(BlobLoadBalancer)}-{nameof(GetMetrics)}")]
    public IActionResult GetMetrics(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "get",
            Route = "blob/load-balancer/metrics"
        )] HttpRequest httpRequest
    ) {
        if (TryValidatePartitionerInputs(
            bucketCount: out var bucketCount,
            errorAction: out var result,
            query: httpRequest.Query,
            value: out var value
        )) {
            result = new OkObjectResult(value: JsonPayload.Create(value: MonotonicPartitioner.GetMetrics(
                bucketCount: bucketCount,
                value: value
            )));
        }

        return result;
    }
    [FeatureGate(features: nameof(BlobLoadBalancer))]
    [Function(name: $"{nameof(BlobLoadBalancer)}-{nameof(GetSlot)}")]
    public IActionResult GetSlot(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "get",
            Route = "blob/load-balancer/slot"
        )] HttpRequest httpRequest
    ) {
        if (TryValidatePartitionerInputs(
            bucketCount: out var bucketCount,
            errorAction: out var result,
            query: httpRequest.Query,
            value: out var value
        )) {
            result = new OkObjectResult(value: JsonPayload.Create(value: MonotonicPartitioner.GetBucketIdDangerous(
                bucketCount: bucketCount,
                value: value
            )));
        }

        return result;
    }
}

