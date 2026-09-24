using System.Buffers;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Puck.Networking;

namespace Puck.Mcp;

public static partial class RemoteMcpServer {
    // The longest any request may run: the longest tool deadline plus time to write its reply.
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(seconds: 125);

    private static void UseAdmission(IApplicationBuilder app, TimeProvider clock, CancellationToken stopping) => app.Use(middleware: async (context, next) => {
        using var lease = ((context.Request.Path == "/healthz")
            ? null
            : context.RequestServices.GetRequiredKeyedService<ConcurrencyLimiter>(serviceKey: "PuckMcp").AttemptAcquire()
        );

        if (lease is { IsAcquired: false }) { context.Response.StatusCode = StatusCodes.Status429TooManyRequests; context.Response.Headers.RetryAfter = "1"; return; }
        using var deadline = new OperationDeadline(
            caller: context.RequestAborted,
            lifetime: stopping,
            timeout: RequestDeadline,
            timeProvider: clock
        );
        var original = context.RequestAborted;

        context.RequestAborted = deadline.Token;
        using var abort = deadline.Token.Register(callback: context.Abort);

        try { await next(context).ConfigureAwait(continueOnCapturedContext: false); } finally { context.RequestAborted = original; }
    });
    private static async Task WithBoundedBodyAsync(HttpContext context, RequestDelegate next) {
        // The limit belongs to MCP even when another host owns Kestrel. Read one byte past the
        // bound so chunked requests cannot evade Content-Length validation. No temporary files.
        const int Limit = (64 * 1024);

        if (!HttpMethods.IsPost(method: context.Request.Method)) { await next(context).ConfigureAwait(continueOnCapturedContext: false); return; }
        if (context.Request.ContentLength > Limit) { context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
        var buffer = ArrayPool<byte>.Shared.Rent(minimumLength: (Limit + 1));
        var original = context.Request.Body;

        try {
            var count = await original.ReadAtLeastAsync(
                buffer.AsMemory(
                    length: (Limit + 1),
                    start: 0
                ),
                (Limit + 1),
                false,
                context.RequestAborted
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (count > Limit) { context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
            using var body = new MemoryStream(
                buffer,
                0,
                count,
                writable: false
            );

            context.Request.Body = body;
            await next(context).ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            context.Request.Body = original; ArrayPool<byte>.Shared.Return(
            buffer,
            clearArray: true
        );
        }
    }
}
