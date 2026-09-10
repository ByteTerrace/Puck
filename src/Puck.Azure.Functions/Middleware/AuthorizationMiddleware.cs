using Azure.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Puck.Azure.Functions.Middleware;

public sealed class AuthorizationMiddleware(
    ILogger<AuthorizationMiddleware> logger
) : IFunctionsWorkerMiddleware {
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next) {
        try {
            var httpContext = context.GetHttpContext()!;
            var authenticateResult = await httpContext.AuthenticateAsync(scheme: JwtBearerDefaults.AuthenticationScheme);

            if (authenticateResult.Succeeded) {
                httpContext.User = authenticateResult.Principal;

                var instanceServices = context.InstanceServices;
                var clientAssertionCredential = instanceServices.GetKeyedService<TokenCredential>(serviceKey: IdentityUtilities.ClientAssertionCredentialKey);
                var onBehalfOfOptions = httpContext.RequestServices.GetService<IOptions<OnBehalfOfOptions>>();

                if ((clientAssertionCredential is not null) && (onBehalfOfOptions is not null)) {
                    var userCredentialContext = instanceServices.GetRequiredService<IUserCredentialContext>();

                    userCredentialContext.UserContext = clientAssertionCredential.ToOnBehalfOfCredential(
                        options: onBehalfOfOptions.Value,
                        userAssertion: await httpContext.GetTokenAsync(tokenName: "access_token")
                    );
                }

                await next(context: context);
            } else {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
        } catch (Exception e) {
            logger.LogError(
                exception: e,
                message: "Unhandled error occurred during Authorization middleware execution."
            );

            context.GetHttpContext()!.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }
}

