using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Puck.Mcp;

/// <summary>OAuth-protected Streamable HTTP extension for Console targets and explicitly installed services.</summary>
public static partial class RemoteMcpServer {
    /// <summary>The independently validated caller bearer scheme used by MCP.</summary>
    public const string AuthenticationScheme = "PuckMcpBearer";
    /// <summary>Reads bounded, strict deployment configuration and serves until cancellation.</summary>
    /// <param name="configurationPath">A JSON file containing RemoteMcpOptions; relative paths are resolved beside it.</param>
    /// <param name="cancellationToken">Stops the gateway and all its attachments, leaving World running.</param>
    public static async Task RunAsync(string configurationPath, CancellationToken cancellationToken = default) {
        var options = await ReadOptionsAsync(cancellationToken: cancellationToken, configurationPath: configurationPath).ConfigureAwait(continueOnCapturedContext: false);

        if (options.Target is not null || options.Services is not null) { throw new ArgumentException(message: "A named target requires an in-process host composition."); }
        await using var app = Build(options);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token1: cancellationToken, token2: app.Lifetime.ApplicationStopping);
        var monitor = WatchConfigurationAsync(app, configurationPath, options, stop.Token);

        try { await app.RunAsync(token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false); } finally { await stop.CancelAsync().ConfigureAwait(continueOnCapturedContext: false); await monitor.ConfigureAwait(continueOnCapturedContext: false); }
    }
    /// <summary>Builds the HTTP resource server with strict OAuth validation and explicit Operator grants.</summary>
    /// <param name="options">Validated deployment settings. The gateway owns its in-memory attachments.</param>
    /// <param name="configureBuilder">Optional trusted composition customization before the application is built.</param>
    /// <returns>The application; its owner starts and asynchronously disposes it.</returns>
    public static WebApplication Build(RemoteMcpOptions options, Action<WebApplicationBuilder>? configureBuilder = null) {
        ArgumentNullException.ThrowIfNull(options);
        options = options.Validate();
        var listen = new Uri(options.ListenUrl!);
        var builder = WebApplication.CreateSlimBuilder(options: new WebApplicationOptions { Args = [] });

        builder.WebHost.ConfigureKestrel(options: server => {
            server.Limits.MaxRequestBodySize = (64 * 1024);
            server.Limits.MaxRequestHeadersTotalSize = (16 * 1024);
            server.Limits.MaxConcurrentConnections = 64;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(seconds: 10);
            server.Listen(IPAddress.Parse(ipString: listen.Host.Trim('[', ']')), listen.Port, endpoint => {
                if (listen.Scheme == "https") {
                    var password = ((options.CertificatePasswordEnvironmentVariable is { } name)
                        ? (Environment.GetEnvironmentVariable(variable: name) ?? throw new InvalidOperationException(message: "The configured certificate-password environment variable is unset.")) : null);

                    endpoint.UseHttps(fileName: options.CertificatePath!, password: password);
                }
            });
        });
        AddServices(builder.Services, options);
        configureBuilder?.Invoke(builder);
        var app = builder.Build();
        ConfigurePipeline(app);
        return app;
    }

    /// <summary>Registers the current MCP SDK in an existing HTTP host without opening a listener.</summary>
    /// <param name="services">The owning host's services. Register its RemoteMcpHost before building.</param>
    /// <param name="options">Resource identity and explicit grants; listener settings are unnecessary.</param>
    public static void AddServices(IServiceCollection services, RemoteMcpOptions options) {
        ArgumentNullException.ThrowIfNull(services);
        options = options.Validate(requireListener: false);
        var resource = new Uri(uriString: options.PublicUrl);
        var origins = options.AllowedOrigins.Select(selector: value => new Uri(uriString: value).GetLeftPart(part: UriPartial.Authority))
            .Append(element: resource.GetLeftPart(part: UriPartial.Authority)).ToFrozenSet(comparer: StringComparer.OrdinalIgnoreCase);
        var access = new RemoteMcpAccessPolicy();

        access.Replace(subjects: options.AllowedSubjects);
        var authorizationScope = (options.AuthorizationScope ?? options.Scope);
        var metadataUri = new Uri(baseUri: resource, relativeUri: "/.well-known/oauth-protected-resource/mcp");
        services.AddSingleton(implementationInstance: options);
        services.AddSingleton(implementationInstance: access);
        services.AddSingleton<RemoteMcpDiagnostics>();
        services.TryAddSingleton<RemoteMcpHost, LocalRemoteMcpHost>();
        services.TryAddSingleton(instance: TimeProvider.System);
        services.AddRouting();
        services.AddSingleton<RemoteAttachmentPool>();
        services.AddKeyedSingleton("PuckMcp", (_, _) => new ConcurrencyLimiter(new() { PermitLimit = 4, QueueLimit = 0 }));
        services.AddCors(setupAction: cors => cors.AddPolicy("PuckMcp", policy => policy.WithOrigins([.. origins]).WithMethods("GET", "POST", "OPTIONS").AllowAnyHeader().WithExposedHeaders("WWW-Authenticate", "MCP-Protocol-Version")));
        services.AddAuthentication().AddJwtBearer(authenticationScheme: AuthenticationScheme, configureOptions: jwt => {
            jwt.Authority = options.Issuer;
            jwt.RequireHttpsMetadata = true;
            jwt.MapInboundClaims = false;
            jwt.SaveToken = true;
            jwt.IncludeErrorDetails = false;
            jwt.TokenValidationParameters = TokenValidation(options.Issuer, options.Audience);
            jwt.Events = new() {
                OnForbidden = context => {
                    if (!HasScope(principal: context.HttpContext.User, scope: options.Scope)) {
                        context.Response.Headers.WWWAuthenticate = $"Bearer error=\"insufficient_scope\", scope=\"{authorizationScope}\", resource_metadata=\"{metadataUri}\"";
                    }
                    return Task.CompletedTask;
                },
            };
        }).AddMcp(configureOptions: mcp => {
            mcp.ForwardAuthenticate = AuthenticationScheme;
            mcp.ForwardForbid = AuthenticationScheme;
            mcp.ResourceMetadataUri = metadataUri;
            mcp.ResourceMetadata = new() { AuthorizationServers = [options.Issuer], Resource = resource.AbsoluteUri, ScopesSupported = [authorizationScope] };
        });
        if (options.TrustedProxy is { } proxy) { AddProxy(services, proxy); }
        ConfigureTools(services, options, access);
    }

    private static TokenValidationParameters TokenValidation(string issuer, string audience) => new() {
                ClockSkew = TimeSpan.Zero,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512],
                ValidAudience = audience,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidateIssuer = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
    };

    private static void ConfigureTools(IServiceCollection services, RemoteMcpOptions options, RemoteMcpAccessPolicy access) {
        services.AddAuthorization(configure: authorization => authorization.AddPolicy("PuckMcp", policy => policy.AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser().RequireAssertion(handler: context =>
            (HasScope(principal: context.User, scope: options.Scope) && (SingleClaim(context.User, options.SubjectClaim) is { } subject) && access.Allows(subject: subject) &&
            ((options.TenantId is null) || (SingleClaim(context.User, "tid") == options.TenantId))))));
        services.AddMcpServer().WithHttpTransport(configureOptions: http => {
            http.SessionMode = HttpServerSessionMode.Stateless;
            http.ConfigureSessionOptions = async (context, server, configurationToken) => {
                var remoteHost = context.RequestServices.GetRequiredService<RemoteMcpHost>();
                var caller = new RemoteMcpCaller(SingleClaim(context.User, options.SubjectClaim)!, options.Issuer, options.TenantId,
                    DateTimeOffset.FromUnixTimeSeconds(long.Parse(SingleClaim(context.User, "exp")!, CultureInfo.InvariantCulture)),
                    (await context.GetTokenAsync(AuthenticationScheme, "access_token").ConfigureAwait(false)) ?? throw new InvalidOperationException("The validated caller token is unavailable."));
                var tools = new RemoteMcpTools(context.RequestServices.GetRequiredService<RemoteAttachmentPool>(), caller.Subject,
                    options.IdleTimeoutSeconds, context.RequestServices.GetRequiredService<RemoteMcpDiagnostics>(), remoteHost, caller);

                server.ServerInfo = new() { Name = remoteHost.SupportsAttachments ? "puck-remote-operator" : "puck-services", Version = "1.0.0" };
                server.ProtocolVersion = "2026-07-28";
                server.ServerInstructions = remoteHost.SupportsAttachments
                    ? "Explicitly authorized remote Operator: full Console and composed-frame authority for this World. Call puck_attach first, keep attachmentId private, and call serially per attachment. Attachments preserve world.wait across HTTP requests; idle expiry, token expiry, disconnect and cancellation can invalidate them. Unknown outcomes must never be automatically replayed. Attach explicitly and inspect state before retrying. This gateway owns live attachments; route a handle back to this same gateway. OAuth identity is checked on every request. Additional service tools appear only when explicitly installed and separately granted; they use the current authenticated request. Participant tools are not installed."
                    : "Request-scoped Puck services. Each tool runs under this request's authenticated caller and explicit grants. No Console attachments or durable background operations are available.";
                server.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(result: RemoteMcpTools.List(remoteHost));
                server.Handlers.CallToolHandler = (request, token) => tools.CallAsync(parameters: request.Params, token: token);
                server.Filters.Message.IncomingFilters.Add(item: next => (message, token) => {
                    OperatorMcpJson.ValidateParameters(message: message.JsonRpcMessage);
                    return next(message, token);
                });
            };
        });
    }

    /// <summary>Builds an SDK request pipeline for a host that dispatches HTTP requests itself.</summary>
    /// <param name="services">The owning host's root provider; requests retain their own scoped RequestServices.</param>
    /// <returns>A delegate to invoke with the original request context and cancellation token.</returns>
    public static RequestDelegate CreateRequestDelegate(IServiceProvider services) {
        var app = new ApplicationBuilder(services);
        ConfigurePipeline(app);
        var pipeline = app.Build();
        return async context => {
            var endpoint = context.GetEndpoint();
            var routeValues = context.Request.RouteValues;
            context.SetEndpoint(null);
            context.Request.RouteValues = [];
            try { await pipeline(context).ConfigureAwait(false); }
            finally { context.SetEndpoint(endpoint); context.Request.RouteValues = routeValues; }
        };
    }

    private static void ConfigurePipeline(IApplicationBuilder app) {
        var options = app.ApplicationServices.GetRequiredService<RemoteMcpOptions>();
        var resource = new Uri(options.PublicUrl);
        var origins = options.AllowedOrigins.Select(value => new Uri(value).GetLeftPart(UriPartial.Authority))
            .Append(resource.GetLeftPart(UriPartial.Authority)).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        var access = app.ApplicationServices.GetRequiredService<RemoteMcpAccessPolicy>();
        var stopping = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        UseAdmission(app, stopping);
        app.Use(middleware: async (context, next) => {
            if (!context.Request.IsHttps && (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))) {
                context.Response.StatusCode = StatusCodes.Status400BadRequest; return;
            }
            if (!Uri.TryCreate(("https://" + context.Request.Host.Value), UriKind.Absolute, out var host) || !string.Equals(a: host.Authority, b: resource.Authority, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                context.Response.StatusCode = StatusCodes.Status400BadRequest; return;
            }
            if ((context.Request.Headers.Origin is { Count: > 0 } origin) && ((origin.Count != 1) || !origins.Contains(item: origin.ToString()))) {
                context.Response.StatusCode = StatusCodes.Status403Forbidden; return;
            }
            // Public HTTPS is fixed deployment policy. Plaintext ingress can only be local to a TLS proxy;
            // arbitrary forwarded headers cannot alter discovery, resource identity, or trusted hosts.
            context.Request.Scheme = "https";
            context.Response.Headers.CacheControl = "no-store";
            await next(context).ConfigureAwait(continueOnCapturedContext: false);
        });
        app.UseRouting();
        if (options.TrustedProxy is { } proxy) { UseProxy(app, proxy); }
        app.UseCors("PuckMcp");
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(middleware: async (context, next) => {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: context.RequestAborted);
            var maximum = TimeSpan.FromSeconds(seconds: 125);

            if ((SingleClaim(context.User, "exp") is { } expiry) && long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) {
                var now = DateTimeOffset.UtcNow;

                if (seconds <= now.ToUnixTimeSeconds()) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
                if (seconds < (now.ToUnixTimeSeconds() + 125)) { maximum = (DateTimeOffset.FromUnixTimeSeconds(seconds: seconds) - now); }
            }
            if (maximum <= TimeSpan.Zero) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            deadline.CancelAfter(delay: maximum);
            var original = context.RequestAborted;

            context.RequestAborted = deadline.Token;
            using var abort = deadline.Token.Register(callback: context.Abort);

            var subject = SingleClaim(context.User, options.SubjectClaim);
            void Revoke() { if (subject is not null && !access.Allows(subject)) { _ = deadline.CancelAsync(); } }
            access.Changed += Revoke;
            try {
                Revoke();
                deadline.Token.ThrowIfCancellationRequested();
                await WithBoundedBodyAsync(context, next).ConfigureAwait(false);
            } finally { access.Changed -= Revoke; context.RequestAborted = original; }
        });
        app.UseEndpoints(endpoints => {
        endpoints.MapMcp(pattern: resource.AbsolutePath).RequireAuthorization("PuckMcp");
        endpoints.MapGet(pattern: "/healthz", requestDelegate: (HttpContext context) => {
            context.Response.StatusCode = (context.RequestServices.GetRequiredService<RemoteMcpHost>().IsReady ? 200 : 503);
            return Task.CompletedTask;
        });
        });
    }

    private static bool HasScope(ClaimsPrincipal principal, string scope) {
        foreach (var claim in principal.Claims) {
            if (claim.Type is not ("scope" or "scp")) { continue; }
            var value = claim.Value.AsSpan();

            foreach (var range in value.Split(separator: ' ')) {
                if (!value[range].IsEmpty && value[range].SequenceEqual(other: scope)) { return true; }
            }
        }
        return false;
    }
    private static string? SingleClaim(ClaimsPrincipal principal, string name) {
        using var claims = principal.FindAll(type: name).GetEnumerator();

        if (!claims.MoveNext()) { return null; }
        var value = claims.Current.Value;

        return ((claims.MoveNext() || string.IsNullOrEmpty(value: value)) ? null : value);
    }
}
