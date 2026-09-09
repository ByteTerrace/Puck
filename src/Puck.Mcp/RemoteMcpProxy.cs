using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Puck.Mcp;

public static partial class RemoteMcpServer {
    /// <summary>The independent origin-token scheme, installed only when a trusted proxy is configured.</summary>
    public const string ProxyAuthenticationScheme = "PuckMcpProxy";

    private static void AddProxy(IServiceCollection services, RemoteMcpProxyOptions proxy) {
        services.AddAuthentication().AddJwtBearer(ProxyAuthenticationScheme, jwt => {
            // Reuse the strict signature/lifetime policy, never the caller's token-selection events.
            jwt.TokenValidationParameters = TokenValidation(proxy.Issuer, proxy.Audience);
            jwt.Authority = proxy.Issuer;
            jwt.RequireHttpsMetadata = true;
            jwt.MapInboundClaims = false;
            jwt.SaveToken = false;
            jwt.IncludeErrorDetails = false;
        });
        services.Configure<JwtBearerOptions>(AuthenticationScheme, jwt => jwt.Events.OnMessageReceived = context => {
            var values = context.Request.Headers["ClientAuthorization"];
            if (values.Count != 1 || !values[0]!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(values[0]![7..])) {
                // No fallback to the proxy's Authorization token, including anonymous discovery requests.
                context.NoResult();
            } else { context.Token = values[0]![7..].Trim(); }
            return Task.CompletedTask;
        });
    }

    private static void UseProxy(IApplicationBuilder app, RemoteMcpProxyOptions proxy) => app.Use(async (context, next) => {
        var origin = await context.AuthenticateAsync(ProxyAuthenticationScheme).ConfigureAwait(false);
        if (!origin.Succeeded || SingleClaim(origin.Principal!, proxy.SubjectClaim) != proxy.Subject ||
            (proxy.TenantId is not null && SingleClaim(origin.Principal!, "tid") != proxy.TenantId)) {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context).ConfigureAwait(false);
    });
}
