using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Puck.Mcp;

public static partial class RemoteMcpServer {
    private static readonly object CallerKey = new();
    private static readonly JsonTypeInfo<JsonRpcMessage> MessageTypeInfo = ((JsonTypeInfo<JsonRpcMessage>)McpJsonUtilities.DefaultOptions.GetTypeInfo(type: typeof(JsonRpcMessage)));
    private static readonly JsonTypeInfo<CallToolRequestParams> CallTypeInfo = ((JsonTypeInfo<CallToolRequestParams>)McpJsonUtilities.DefaultOptions.GetTypeInfo(type: typeof(CallToolRequestParams)));

    // One caller per request: the delegation step and the stateless MCP session it precedes share it, so the state the
    // host's authorization returned is what its dispatched call reads.
    private static async ValueTask<RemoteMcpCaller> CallerAsync(HttpContext context, RemoteMcpOptions options) {
        if (context.Items[CallerKey] is RemoteMcpCaller existing) { return existing; }
        var issuedAt = ((long.TryParse(
            SingleClaim(
                context.User,
                "iat"
            ),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var issued
        ))
            ? DateTimeOffset.FromUnixTimeSeconds(seconds: issued)
            : (DateTimeOffset?)null
        );
        var caller = new RemoteMcpCaller(
            SingleClaim(
                context.User,
                options.SubjectClaim
            )!,
            options.Issuer,
            options.TenantId,
            DateTimeOffset.FromUnixTimeSeconds(seconds: long.Parse(
                SingleClaim(
                    context.User,
                    "exp"
                )!,
                CultureInfo.InvariantCulture
            )),
            issuedAt,
            ((await context.GetTokenAsync(
                scheme: AuthenticationScheme,
                tokenName: "access_token"
            ).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new InvalidOperationException(message: "The validated caller token is unavailable."))
        );

        context.Items[CallerKey] = caller;
        return caller;
    }
    // A bearer challenge must be decided before the MCP transport can commit the response status: it flushes headers
    // a fixed grace period after dispatch so long calls stream promptly. Every challenge is therefore decided here,
    // ahead of dispatch, and is always a complete 401 response: a downstream rejection the caller's earlier call
    // reported after dispatch challenges this request, and the host resolves each tools/call's delegated authorization.
    private static void UseDelegatedAuthorization(IApplicationBuilder app, RemoteMcpOptions options) {
        var endpoint = new PathString(value: new Uri(uriString: options.PublicUrl).AbsolutePath);
        var metadata = new Uri(
            baseUri: new Uri(uriString: options.PublicUrl),
            relativeUri: "/.well-known/oauth-protected-resource/mcp"
        );
        var scope = (options.AuthorizationScope ?? options.Scope);

        void Challenge(HttpContext context, string? encodedClaims) {
            var claims = ((encodedClaims is { } value)
                ? $", error=\"insufficient_claims\", claims=\"{value}\""
                : ", error=\"invalid_token\""
            );

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{metadata}\", scope=\"{scope}\"{claims}";
        }

        app.Use(middleware: async (context, next) => {
            if (
                !HttpMethods.IsPost(method: context.Request.Method) ||
                (context.Request.Path != endpoint) ||
                (context.GetEndpoint() is null)
            ) {
                await next(context).ConfigureAwait(continueOnCapturedContext: false);
                return;
            }
            var caller = await CallerAsync(
                context: context,
                options: options
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (context.RequestServices.GetRequiredService<RemoteMcpChallenges>().TryChallenge(
                caller: caller,
                encodedClaims: out var pending
            )) {
                Challenge(
                    context: context,
                    encodedClaims: pending
                );
                return;
            }
            if (await ReadToolCallAsync(context: context).ConfigureAwait(continueOnCapturedContext: false) is not { } call) {
                await next(context).ConfigureAwait(continueOnCapturedContext: false);
                return;
            }
            try {
                caller.Authorization = await context.RequestServices.GetRequiredService<RemoteMcpHost>().AuthorizeAsync(
                    caller: caller,
                    cancellationToken: context.RequestAborted,
                    request: call
                ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (RemoteMcpAuthorizationException challenge) {
                Challenge(
                    context: context,
                    encodedClaims: challenge.EncodedClaims
                );
                return;
            }
            await next(context).ConfigureAwait(continueOnCapturedContext: false);
        });
    }
    // Reads the request as the MCP transport will, then rewinds the buffered body for it. Anything that is not one
    // well-formed tools/call passes through untouched; the transport owns rejecting it.
    private static async ValueTask<CallToolRequestParams?> ReadToolCallAsync(HttpContext context) {
        var body = context.Request.Body;

        if (!body.CanSeek) { return null; }
        try {
            var message = await context.Request.ReadFromJsonAsync(
                cancellationToken: context.RequestAborted,
                jsonTypeInfo: MessageTypeInfo
            ).ConfigureAwait(continueOnCapturedContext: false);

            return ((message is JsonRpcRequest { Method: RequestMethods.ToolsCall, Params: { } parameters })
                ? parameters.Deserialize(jsonTypeInfo: CallTypeInfo)
                : null
            );
        } catch (Exception error) when ((error is JsonException or InvalidOperationException or NotSupportedException)) {
            return null;
        } finally { body.Position = 0; }
    }
}
