using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.FeatureFilters;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Puck.Azure.Functions.Middleware;

[AttributeUsage(
    validOn: AttributeTargets.Method,
    AllowMultiple = false,
    Inherited = false
)]
public sealed class FeatureGateAttribute : Attribute
{
    public string[] Features { get; }
    public bool RequireAll { get; init; } = true;

    public FeatureGateAttribute(params string[] features) {
        ArgumentNullException.ThrowIfNull(argument: features);

        if (0 == features.Length) {
            throw new ArgumentException(
                message: "At least one feature must be specified.",
                paramName: nameof(features)
            );
        }

        Features = features;
    }
}

public static partial class FeatureGateLog
{
    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Information,
        Message = "Access to {Resource} denied for {UserId} by feature(s): [{Features}]."
    )]
    public static partial void AccessDenied(ILogger logger, string features, string resource, string userId);
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Access to {Resource} granted for {UserId} by feature(s): [{Features}]."
    )]
    public static partial void AccessGranted(ILogger logger, string features, string resource, string userId);
}

public sealed class FeatureGateMiddleware(ILogger<FeatureGateMiddleware> logger) : IFunctionsWorkerMiddleware
{
    private static readonly ConcurrentDictionary<string, FeatureGateAttribute?> AttributeCache = new();

    private static bool TryGetFeatureGateAttribute(
        FunctionContext context,
        [NotNullWhen(returnValue: true)] out FeatureGateAttribute? attribute
    ) {
        attribute = AttributeCache.GetOrAdd(
            key: context.FunctionDefinition.Name,
            valueFactory: _ => context.GetAttribute<FeatureGateAttribute>()
        );

        return (attribute is not null);
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next) {
        try {
            var featuresLogValue = "<none>";
            var functionName = context.FunctionDefinition.Name;
            var isAllowed = true;
            var targetingContext = await context
                .InstanceServices
                .GetRequiredService<ITargetingContextAccessor>()
                .GetContextAsync();
            var userId = (targetingContext?.UserId ?? "<unknown>");

            if (TryGetFeatureGateAttribute(
                attribute: out var attribute,
                context: context
            )) {
                var featureManagerSnapshot = context
                    .InstanceServices
                    .GetRequiredService<IFeatureManagerSnapshot>();
                var features = attribute.Features;
                var requireAll = attribute.RequireAll;

                if (!requireAll) {
                    isAllowed = false;
                }

                foreach (var feature in features) {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    featuresLogValue = feature;

                    var isEnabled = await featureManagerSnapshot.IsEnabledAsync(feature: feature);

                    if (requireAll) {
                        isAllowed &= isEnabled;

                        if (!isAllowed) { break; }
                    }
                    else {
                        isAllowed |= isEnabled;

                        if (isAllowed) { break; }
                    }
                }

                if (isAllowed && requireAll) {
                    featuresLogValue = string.Join(
                        separator: ',',
                        values: features
                    );
                }
            }

            if (isAllowed) {
                FeatureGateLog.AccessGranted(
                    features: featuresLogValue,
                    logger: logger,
                    resource: functionName,
                    userId: userId
                );

                await next(context: context);
            }
            else {
                FeatureGateLog.AccessDenied(
                    features: featuresLogValue,
                    logger: logger,
                    resource: functionName,
                    userId: userId
                );

                context.GetHttpContext()!.Response.StatusCode = StatusCodes.Status403Forbidden;
            }
        }
        catch (Exception e) {
            logger.LogError(
                exception: e,
                message: "Unhandled error occurred during Feature Gate middleware execution."
            );

            context.GetHttpContext()!.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }
}

