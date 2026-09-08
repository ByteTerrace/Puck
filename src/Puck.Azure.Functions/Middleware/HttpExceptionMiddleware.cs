using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace Puck.Azure.Functions.Middleware;

public sealed record class ExceptionResponse(
    string Message
);

public sealed class HttpExceptionMiddleware(ILogger<HttpExceptionMiddleware> logger) : IFunctionsWorkerMiddleware
{
    private const string UnhandledExceptionMessage = "Unhandled exception during HTTP trigger.";

    private static readonly ExceptionResponse ExceptionResponse = new(Message: UnhandledExceptionMessage);

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next) {
        try {
            await next(context: context);
        }
        catch (Exception e) {
            if (logger.IsEnabled(logLevel: LogLevel.Error)) {
                logger.LogError(
                    exception: e,
                    message: UnhandledExceptionMessage
                );
            }

            var httpContext = context.GetHttpContext()!;

            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

            await httpContext.Response.WriteAsJsonAsync(
                cancellationToken: context.CancellationToken,
                value: ExceptionResponse
            );
        }
    }
}

