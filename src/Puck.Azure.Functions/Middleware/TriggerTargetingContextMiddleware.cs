using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.FeatureManagement.FeatureFilters;

namespace Puck.Azure.Functions.Middleware;

public sealed class TriggerTargetingContextMiddleware : IFunctionsWorkerMiddleware, ITargetingContextAccessor
{
    private static readonly AsyncLocal<TargetingContext?> m_targetingContext = new();

    public ValueTask<TargetingContext> GetContextAsync() =>
        new(result: (m_targetingContext.Value ?? new TargetingContext()));
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next) {
        var httpContext = context.GetHttpContext();

        if (httpContext is not null) {
            var user = httpContext.User;

            m_targetingContext.Value = new() {
                Groups = [..user
                    .Claims
                    .Where(predicate: claim => ("groups".Equals(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: claim.Type
                    )))
                    .Select(selector: claim => claim.Value)
                ],
                UserId = user.Identity?.Name,
            };
        }

        await next(context: context);
    }
}

