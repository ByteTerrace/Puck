using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Puck.Actors;
using Puck.Actors.Grains;
using Puck.Actors.Services;
using Puck.Azure;
using Puck.Storage;

using Constants = Puck.Actors.Constants;

var builder = WebApplication.CreateBuilder(args: args);
var configuration = builder.Configuration;
var services = builder.Services;
var tokenCredential = new DefaultAzureCredential();
var configurationStoreIsConfigured = services.TryAddConfigurationStore(
    configurationManager: configuration,
    optional: true,
    tokenCredential: tokenCredential
);
services.AddAzureClients(configureClients: clientFactoryBuilder => {
    clientFactoryBuilder.UseCredential(tokenCredential: tokenCredential);
    clientFactoryBuilder
        .AddClient<BlobServiceClient, BlobClientOptions>(
            factory: (blobClientOptions, tokenCredential, serviceProvider) => {
                var endpoint = serviceProvider
                    .GetRequiredService<IOptionsMonitor<PublicStorageOptions>>()
                    .CurrentValue
                    .Endpoint;

                return (!Uri.TryCreate(
                        result: out var endpointUri,
                        uriKind: UriKind.Absolute,
                        uriString: endpoint
                    )
                    ? throw new ArgumentException(message: $"Current public storage endpoint \"{endpoint}\" is not a valid absolute URI.")
                    : new BlobServiceClient(
                        credential: tokenCredential,
                        options: blobClientOptions,
                        serviceUri: endpointUri
                    ));
            }
        )
        .WithName(name: "PublicStorage");
    clientFactoryBuilder
        .AddClient<GraphServiceClient, GraphClientOptions>(factory: (_, tokenCredential) =>
            new(tokenCredential: tokenCredential)
        );
});
if (!string.IsNullOrWhiteSpace(value: configuration.GetValue<string>(key: "APPLICATIONINSIGHTS_CONNECTION_STRING"))) {
    builder
        .Logging
        .AddOpenTelemetry(configure: static loggerOptions => {
            loggerOptions.IncludeFormattedMessage = true;
            loggerOptions.IncludeScopes = true;
        });

    services
        .AddOpenTelemetry()
        .UseAzureMonitorExporter(configureAzureMonitor: azureMonitorOptions => {
            azureMonitorOptions.Credential = tokenCredential;
        })
        .WithMetrics(configure: static meterProviderBuilder => {
            meterProviderBuilder.AddMeter(names: "Microsoft.Orleans");
        })
        .WithTracing(configure: static tracerProviderBuilder => {
            tracerProviderBuilder
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource(names: [
                    "Microsoft.Orleans.Application",
                    "Microsoft.Orleans.Runtime",
                ]);
        });
}
services.AddHealthChecks();
services.TryAddDataProtection(
    applicationName: (configuration.GetValue<string>(key: "DataProtection:ApplicationName") ?? builder.Environment.ApplicationName),
    configuration: configuration
);
services
    .AddOptions<OnboardingOptions>()
    .Bind(config: configuration.GetSection(key: "Onboarding"));
services
    .AddOptions<OnBehalfOfOptions>()
    .Bind(config: configuration.GetSection(key: "OnBehalfOf"));
services
    .AddOptions<PublicStorageOptions>()
    .Bind(config: configuration.GetSection(key: "PublicStorage"));
services
    .AddOptions<PartitioningOptions>()
    .Bind(config: configuration.GetSection(key: "Partitioning"));
services.TryAddSingleton<IPartitionResolver, DefaultPartitionResolver>();
services.TryAddKeyedSingleton<TokenCredential>(
    instance: tokenCredential,
    serviceKey: "Default"
);
var umiClientAssertionId = configuration.GetValue<string>(key: "OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID");
if (!string.IsNullOrWhiteSpace(value: umiClientAssertionId)) {
    services.TryAddKeyedSingleton<TokenCredential>(
        instance: new ManagedIdentityCredential(
            id: ManagedIdentityId.FromUserAssignedClientId(
                id: umiClientAssertionId
            )
        ),
        serviceKey: IdentityUtilities.ClientAssertionCredentialKey
    );
} else {
    services.TryAddKeyedSingleton<TokenCredential>(
        instance: tokenCredential,
        serviceKey: IdentityUtilities.ClientAssertionCredentialKey
    );
}
services.TryAddSingleton<GraphServiceClient>(implementationFactory: static serviceProvider =>
    serviceProvider
        .GetRequiredService<IAzureClientFactory<GraphServiceClient>>()
        .CreateClient(name: "Default")
);
services.TryAddSingleton<IKeyPairService, DefaultKeyPairService>();
services.TryAddSingleton<IUserProvisioningService, DefaultUserProvisioningService>();
var privateBlobEndpointIsValid = Uri.TryCreate(
    result: out var privateBlobEndpointUri,
    uriKind: UriKind.Absolute,
    uriString: configuration.GetValue<string>(key: "PrivateStorage:BlobEndpoint")
);
var privateTableEndpointIsValid = Uri.TryCreate(
    result: out var privateTableEndpointUri,
    uriKind: UriKind.Absolute,
    uriString: configuration.GetValue<string>(key: "PrivateStorage:TableEndpoint")
);
var isAzureHosted = (privateBlobEndpointIsValid && privateTableEndpointIsValid);
// The internal API is Entra-secured: the Functions edge sends an app-only token for the shared
// application registration carrying the "Actors.Invoke" app role. No shared secrets. Local
// development (no Azure fabric configured) runs open.
if (isAzureHosted && string.IsNullOrWhiteSpace(value: configuration.GetValue<string>(key: "Authorization:JwtBearer:Authority"))) {
    throw new InvalidOperationException(message: "Authorization:JwtBearer must be configured when Azure-hosted.");
}
services
    .AddAuthorization(configure: static authorizationOptions => {
        authorizationOptions.AddPolicy(
            name: Constants.InternalApiPolicyName,
            configurePolicy: static policyBuilder => {
                policyBuilder
                    .RequireAuthenticatedUser()
                    .RequireAssertion(handler: static context =>
                        context
                            .User
                            .Claims
                            .Any(predicate: static claim =>
                                ((("roles" == claim.Type) || (System.Security.Claims.ClaimTypes.Role == claim.Type)) &&
                                ("Actors.Invoke" == claim.Value))
                            )
                    );
            }
        );
    });
builder
    .Host
    .UseOrleans(configureDelegate: (_, siloBuilder) => {
        siloBuilder.UsePuckOrleansClustering(
            configuration: configuration,
            tokenCredential: tokenCredential
        );
    });
// MUST come after UseOrleans: registering the JwtBearer authentication scheme before Orleans
// breaks grain-manifest resolution at silo startup ("Could not find an implementation for
// interface Orleans.IReminderTableGrain").
services
    .AddAuthentication(defaultScheme: JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(configureOptions: jwtBearerOptions => {
        configuration
            .GetSection(key: "Authorization:JwtBearer")
            .Bind(instance: jwtBearerOptions);
    });
var app = builder.Build();
if (configurationStoreIsConfigured) {
    app.UseAzureAppConfiguration();
}
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks(pattern: "/healthz");
var usersApi = app.MapGroup(prefix: "/users");
if (isAzureHosted) {
    usersApi.RequireAuthorization(policyNames: Constants.InternalApiPolicyName);
}
usersApi.MapPost(
    handler: async (
        Guid oid,
        EnsureProvisionedRequest request,
        IGrainFactory grainFactory
    ) => {
        if (string.IsNullOrWhiteSpace(value: request.ProtectedAssertion) ||
            string.IsNullOrWhiteSpace(value: request.TokenDiscriminator)
        ) {
            return Results.BadRequest(error: new {
                Detail = "Both protectedAssertion and tokenDiscriminator are required.",
            });
        }

        try {
            var provisioningState = await grainFactory
                .GetGrain<IUserGrain>(primaryKey: oid)
                .EnsureProvisionedAsync(tokenEscrow: new(
                    ExpiresAt: request.AssertionExpiresAt,
                    ProtectedAssertion: request.ProtectedAssertion,
                    TokenDiscriminator: request.TokenDiscriminator
                ));

            return Results.Ok(value: ProvisioningStateResponse.From(provisioningState: provisioningState));
        } catch (InvalidOperationException e) {
            return Results.BadRequest(error: new { Detail = e.Message, });
        }
    },
    pattern: "/{oid:guid}/ensure-provisioned"
);
usersApi.MapGet(
    handler: async (
        Guid oid,
        IGrainFactory grainFactory
    ) => {
        var provisioningState = await grainFactory
            .GetGrain<IUserGrain>(primaryKey: oid)
            .GetProvisioningStateAsync();

        return Results.Ok(value: ProvisioningStateResponse.From(provisioningState: provisioningState));
    },
    pattern: "/{oid:guid}/provisioning-state"
);
usersApi.MapGet(
    handler: async (
        Guid oid,
        IGrainFactory grainFactory
    ) => Results.Ok(value: await grainFactory
        .GetGrain<IUserGrain>(primaryKey: oid)
        .GetStorageLocationAsync()
    ),
    pattern: "/{oid:guid}/storage-location"
);
usersApi.MapPut(
    handler: async (
        Guid oid,
        SetGuestAccessRequest request,
        IGrainFactory grainFactory
    ) => {
        try {
            var provisioningState = await grainFactory
                .GetGrain<IUserGrain>(primaryKey: oid)
                .SetGuestAccessAsync(guestAccess: request.GuestAccess);

            return Results.Ok(value: ProvisioningStateResponse.From(provisioningState: provisioningState));
        } catch (InvalidOperationException e) {
            return Results.BadRequest(error: new { Detail = e.Message, });
        }
    },
    pattern: "/{oid:guid}/guest-access"
);
usersApi.MapPut(
    handler: async (
        Guid oid,
        SetStorageAccessRequest request,
        IGrainFactory grainFactory
    ) => {
        try {
            var provisioningState = await grainFactory
                .GetGrain<IUserGrain>(primaryKey: oid)
                .SetStorageAccessAsync(
                    canRead: request.CanRead,
                    canWrite: request.CanWrite
                );

            return Results.Ok(value: ProvisioningStateResponse.From(provisioningState: provisioningState));
        } catch (InvalidOperationException e) {
            return Results.BadRequest(error: new { Detail = e.Message, });
        }
    },
    pattern: "/{oid:guid}/storage-access"
);
app.Run();

public sealed record EnsureProvisionedRequest(
    string ProtectedAssertion,
    string TokenDiscriminator,
    DateTimeOffset AssertionExpiresAt
);
public sealed record SetGuestAccessRequest(
    string GuestAccess
);
public sealed record SetStorageAccessRequest(
    bool CanRead,
    bool CanWrite
);
public sealed record ProvisioningStateResponse(
    string Status,
    string[] CompletedSteps,
    DateTimeOffset? UpdatedAt,
    string? FaultReason,
    bool StorageReadEnabled,
    bool StorageWriteEnabled,
    string GuestAccess
) {
    public static ProvisioningStateResponse From(ProvisioningState provisioningState) =>
        new(
            CompletedSteps: Enum
                .GetValues<ProvisioningStep>()
                .Where(predicate: step =>
                    ((ProvisioningStep.None != step) &&
                    provisioningState.CompletedSteps.HasFlag(flag: step))
                )
                .Select(selector: static step => step.ToString())
                .ToArray(),
            FaultReason: provisioningState.FaultReason,
            GuestAccess: provisioningState.GuestAccess,
            Status: provisioningState.Status,
            StorageReadEnabled: provisioningState.StorageReadEnabled,
            StorageWriteEnabled: provisioningState.StorageWriteEnabled,
            UpdatedAt: provisioningState.UpdatedAt
        );
}

