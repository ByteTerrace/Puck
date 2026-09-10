using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Puck.Azure;
using Puck.Azure.Functions;
using Puck.Azure.Functions.HttpDelegatingHandlers;
using Puck.Azure.Functions.Services;
using Puck.Storage;

var builder = FunctionsApplication.CreateBuilder(args: args);
var configuration = builder.Configuration;
var services = builder.Services;
var tokenCredential = new DefaultAzureCredential();
builder.ConfigureFunctionsWebApplication();
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
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter(configureAzureMonitor: azureMonitorOptions => {
            azureMonitorOptions.Credential = tokenCredential;
        })
        .WithTracing(configure: static tracerProviderBuilder => {
            tracerProviderBuilder.AddHttpClientInstrumentation();
        });
}
services
    .AddAuthorization()
    .AddAuthentication(defaultScheme: JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(configureOptions: jwtBearerOptions => {
        configuration
            .GetSection(key: "Authorization:JwtBearer")
            .Bind(instance: jwtBearerOptions);

        jwtBearerOptions.Events.OnMessageReceived = context => {
            var authorization = context.Request.Headers[key: "ClientAuthorization"].ToString();

            if (string.IsNullOrEmpty(value: authorization)) {
                authorization = context.Request.Headers.Authorization.ToString();
            }

            if (!string.IsNullOrEmpty(value: authorization)) {
                const string BearerPrefix = $"{JwtBearerDefaults.AuthenticationScheme} ";

                context.Token = (authorization.StartsWith(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: BearerPrefix
                    )
                    ? authorization[BearerPrefix.Length..]
                    : authorization);
            }

            return Task.CompletedTask;
        };
        jwtBearerOptions.SaveToken = true;
    });
services.AddHealthChecks();
// The silo requires the "Actors.Invoke" app role on the shared app registration, granted to this
// app's runtime identity — so this scope must stay an app-only one.
var actorsTokenRequestContext = new TokenRequestContext(scopes: ["https://api.byteterrace.com/.default"]);
var resourceManagerTokenRequestContext = new TokenRequestContext(scopes: ["https://management.azure.com/.default"]);
services
    .AddHttpClient(name: "Actors")
    .AddHttpMessageHandler(configureHandler: serviceProvider => new BearerTokenDelegatingHandler(
        tokenCredential: serviceProvider.GetRequiredKeyedService<TokenCredential>(serviceKey: "Default"),
        tokenRequestContext: actorsTokenRequestContext
    ));
services
    .AddHttpClient(name: "AzureResourceManager")
    .AddHttpMessageHandler(configureHandler: serviceProvider => new BearerTokenDelegatingHandler(
        tokenCredential: serviceProvider.GetRequiredKeyedService<TokenCredential>(serviceKey: "Default"),
        tokenRequestContext: resourceManagerTokenRequestContext
    ));
services.AddHybridCache(setupAction: static hybridCacheOptions => {
    hybridCacheOptions.MaximumKeyLength = 64;
    hybridCacheOptions.MaximumPayloadBytes = 32768;
});
services.TryAddConfigurationStore(
    configurationManager: configuration,
    optional: true,
    tokenCredential: tokenCredential
);
services.TryAddDataProtection(
    applicationName: (configuration.GetValue<string>(key: "AZURE_CLIENT_ID") ?? builder.Environment.ApplicationName),
    configuration: configuration
);
services.TryAddRedisCache(
    configuration: configuration
);
services
    .AddOptions<PublicStorageOptions>()
    .Bind(config: configuration.GetSection(key: "PublicStorage"));
services
    .AddOptions<BlobSasUriOptions>()
    .Bind(config: configuration.GetSection(key: "BlobSasUris"))
    .Configure<IOptions<PublicStorageOptions>>(configureOptions: static (blobSasUriOptions, publicStorageOptions) => {
        blobSasUriOptions.DefaultEndpoint ??= publicStorageOptions.Value.Endpoint;
    });
services
    .AddOptions<IssuingKeyOptions>()
    .Bind(config: configuration.GetSection(key: "IssuingKey"));
services.TryAddKeyedSingleton<TokenCredential>(
    instance: tokenCredential,
    serviceKey: "Default"
);
services.TryAddScoped<IUserCredentialContext, UserCredentialContext>();
services.AddOptions<OnBehalfOfOptions>().Bind(config: configuration.GetSection(key: "OnBehalfOf"));
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
}
services.AddOptions<PartitioningOptions>().Bind(config: configuration.GetSection(key: "Partitioning"));
services.TryAddSingleton<IPartitionResolver, DefaultPartitionResolver>();
services.TryAddSingleton<IUserStorageLocationService, DefaultUserStorageLocationService>();
// The clock a trigger reads at its admission boundary. Injected rather than reached for statically because
// the signed-carriage wire specification (§9) makes the ORIGIN of "now" normative, and an injected one can
// be stated, substituted, and recorded.
services.TryAddSingleton<TimeProvider>(instance: TimeProvider.System);
services.TryAddSingleton<IBindingService, DefaultBindingService>();
services.TryAddSingleton<IBlobSasUriService, DefaultBlobSasUriService>();
builder
    .UseAuthorization()
    .UseTriggerTargetingContext()
    .UseAzureAppConfiguration()
    .UseFeatureGate()
    .UseHttpExceptionHandler();
builder.Build().Run();

