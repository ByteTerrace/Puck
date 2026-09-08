using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;
using StackExchange.Redis;
using System.Reflection;
using Puck.Azure.Functions.HealthChecks;
using Puck.Azure.Functions.Middleware;

using CryptographyClient = Azure.Security.KeyVault.Keys.Cryptography.CryptographyClient;

namespace Puck.Azure.Functions;

public static class Extensions
{
    private static IFunctionsWorkerApplicationBuilder UseWhenHttpTrigger<T>(
        this IFunctionsWorkerApplicationBuilder builder
    ) where T : class, IFunctionsWorkerMiddleware =>
        builder.UseWhen<T>(predicate: static context => ("httpTrigger" == context.GetTriggerType()));

    public static T? GetAttribute<T>(this FunctionContext context) where T : Attribute {
        var attribute = default(T);
        var entryPoint = context.FunctionDefinition.EntryPoint;
        var lastSegmentIndex = entryPoint.LastIndexOf(value: '.');

        if (0 <= lastSegmentIndex) {
            attribute = Type
                .GetType(typeName: entryPoint[..lastSegmentIndex])
                ?.GetMethod(
                    bindingAttr: (BindingFlags.Instance | BindingFlags.Public),
                    name: entryPoint[(lastSegmentIndex + 1)..]
                )
                ?.GetCustomAttribute<T>();
        }

        return attribute;
    }
    public static Guid GetCorrelationId(this FunctionContext context) =>
        Guid.Parse(input: context
            .TraceContext
            .TraceParent
            .Split(
                options: StringSplitOptions.None,
                separator: '-'
            )[1]
        );
    public static string? GetTriggerType(this FunctionContext context) =>
        context
            .FunctionDefinition
            .InputBindings
            .Values
            .FirstOrDefault(bindingMetadata => bindingMetadata
                .Type
                .EndsWith(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: "Trigger"
                )
            )
            ?.Type;
    public static bool TryAddConfigurationStore(
        this IServiceCollection services,
        IConfigurationManager configurationManager,
        TokenCredential tokenCredential,
        bool optional = false,
        string sectionKey = "ConfigurationStore"
    ) {
        const string EndpointKey = "Endpoint";
        const string RefreshIntervalSecondsKey = "RefreshIntervalSeconds";
        const string SentinelKey = "Sentinel";

        var configurationSection = configurationManager.GetSection(key: sectionKey);
        var isConfigured = Uri.TryCreate(
            result: out var endpointUri,
            uriKind: UriKind.Absolute,
            uriString: configurationSection.GetValue<string>(key: EndpointKey)
        );

        if (isConfigured) {
            var refreshInterval = TimeSpan.FromSeconds(value: (configurationSection.GetValue<int?>(key: RefreshIntervalSecondsKey) ?? 180));
            var sentinel = configurationSection.GetValue<string>(key: SentinelKey);
            var label = configurationSection.GetValue<string>(key: "Label") ?? LabelFilter.Null;

            services
                .AddAzureClients(configureClients: clientFactoryBuilder => {
                    clientFactoryBuilder.AddConfigurationClient(configurationUri: endpointUri);
                });
            configurationManager.AddAzureAppConfiguration(
                action: appConfigOptions => {
                    appConfigOptions
                        .Connect(
                            credential: tokenCredential,
                            endpoint: endpointUri
                        )
                        .ConfigureKeyVault(configure: keyVaultOptions => {
                            keyVaultOptions.SetCredential(credential: tokenCredential);
                        })
                        .ConfigureRefresh(configure: refreshOptions => {
                            refreshOptions
                                .Register(
                                    key: (sentinel ?? "Version"),
                                    label: label,
                                    refreshAll: true
                                )
                                .SetRefreshInterval(refreshInterval: refreshInterval);
                        })
                        .ConfigureStartupOptions(configure: startupOptions => {
                            startupOptions.Timeout = TimeSpan.FromSeconds(value: 17);
                        })
                        .Select(
                            keyFilter: KeyFilter.Any,
                            labelFilter: label,
                            tagFilters: default
                        )
                        .UseFeatureFlags(configure: featureFlagOptions => {
                            featureFlagOptions
                                .Select(
                                    featureFlagFilter: KeyFilter.Any,
                                    labelFilter: label,
                                    tagFilters: default
                                )
                                .SetRefreshInterval(refreshInterval: refreshInterval);
                        });
                },
                optional: optional
            );
        }

        services
            .AddAzureAppConfiguration()
            .AddFeatureManagement()
            .WithTargeting<TriggerTargetingContextMiddleware>();
        services
            .AddHealthChecks()
            .AddAzureAppConfiguration(name: "ConfigurationStore");

        return isConfigured;
    }
    public static bool TryAddDataProtection(
        this IServiceCollection services,
        string applicationName,
        IConfigurationSection configurationSection
    ) {
        const string BlobUriKey = "BlobUri";
        const string ClientName = "DataProtection";
        const string KeyUriKey = "KeyUri";

        var blobUriIsValid = Uri.TryCreate(
            result: out var blobUri,
            uriKind: UriKind.Absolute,
            uriString: configurationSection.GetValue<string>(key: BlobUriKey)
        );
        var keyUriIsValid = Uri.TryCreate(
            result: out var keyUri,
            uriKind: UriKind.Absolute,
            uriString: configurationSection.GetValue<string>(key: KeyUriKey)
        );
        var isConfigured = (blobUriIsValid && keyUriIsValid);

        if (isConfigured) {
            var blobContainer = blobUri!.Segments[1][0..^1];
            var blobName = string.Join(
                separator: "",
                values: blobUri.Segments.Skip(count: 2)
            );

            services
                .AddAzureClients(configureClients: clientFactoryBuilder => {
                    clientFactoryBuilder
                        .AddBlobServiceClient(serviceUri: new(
                            baseUri: blobUri,
                            relativeUri: $"/{blobContainer}"
                        ))
                        .WithName(name: ClientName);
                    clientFactoryBuilder
                        .AddCryptographyClient(vaultUri: keyUri)
                        .WithName(name: ClientName);
                });
            services
                .AddDataProtection()
                .PersistKeysToAzureBlobStorage(
                    blobClientFactory: serviceProvider =>
                        serviceProvider
                            .GetRequiredService<IAzureClientFactory<BlobServiceClient>>()
                            .CreateClient(name: ClientName)
                            .GetBlobContainerClient(blobContainerName: blobContainer)
                            .GetBlobClient(blobName: blobName)
                )
                .ProtectKeysWithAzureKeyVault(
                    keyIdentifier: keyUri,
                    keyResolverFactory: static serviceProvider =>
                        new StaticKeyResolver(
                            keyEncryptionKey: serviceProvider
                                .GetRequiredService<IAzureClientFactory<CryptographyClient>>()
                                .CreateClient(name: ClientName)
                        )
                )
                .SetApplicationName(applicationName: applicationName)
                .UseCryptographicAlgorithms(configuration: new() {
                    EncryptionAlgorithm = EncryptionAlgorithm.AES_256_GCM,
                });
        }

        services
            .AddHealthChecks()
            .AddCheck<DataProtectionHealthCheck>(name: ClientName);

        return isConfigured;
    }
    public static bool TryAddDataProtection(
        this IServiceCollection services,
        string applicationName,
        IConfiguration configuration,
        string sectionKey = "DataProtection"
    ) =>
        services.TryAddDataProtection(
            applicationName: applicationName,
            configurationSection: configuration.GetSection(key: sectionKey)
        );
    public static bool TryAddRedisCache(
        this IServiceCollection services,
        IConfigurationSection configurationSection,
        string clientName = "Default"
    ) {
        const string ConfigurationKey = "Configuration";

        var configuration = configurationSection.GetValue<string>(key: ConfigurationKey);
        var isConfigured = !string.IsNullOrWhiteSpace(value: configuration);

        if (isConfigured) {
            services
                .AddAzureClients(configureClients: clientFactoryBuilder => {
                    clientFactoryBuilder
                        .AddClient<IConnectionMultiplexer, RedisCacheOptions>(factory: static (redisCacheOptions, tokenCredential, serviceProvider) =>
                            ConnectionMultiplexer
                                .ConnectAsync(configuration: ConfigurationOptions
                                    .Parse(configuration: redisCacheOptions.Configuration!)
                                    .ConfigureForAzureWithTokenCredentialAsync(tokenCredential: tokenCredential)
                                    .GetAwaiter()
                                    .GetResult()
                                )
                                .GetAwaiter()
                                .GetResult()
                        )
                        .ConfigureOptions(configureOptions: (redisCacheOptions, serviceProvider) => {
                            redisCacheOptions.Configuration = configuration;
                            redisCacheOptions.ConnectionMultiplexerFactory = () =>
                                Task.FromResult(result: serviceProvider
                                    .GetRequiredService<IAzureClientFactory<IConnectionMultiplexer>>()
                                    .CreateClient(name: clientName)
                            );
                        })
                        .WithName(name: clientName);
                });
            services
                .Add(item: ServiceDescriptor.Singleton<IDistributedCache, RedisCache>(
                    implementationFactory: serviceProvider =>
                        new(optionsAccessor: serviceProvider
                            .GetRequiredService<IOptionsMonitor<RedisCacheOptions>>()
                            .Get(name: clientName)
                        )
                ));
        }

        services
            .Configure<RedisCacheHealthCheckOptions>(configureOptions: healthCheckOptions => {
                healthCheckOptions.ClientName = clientName;
            })
            .AddHealthChecks()
            .AddCheck<RedisCacheHealthCheck>(name: $"RedisCache:{clientName}");

        return isConfigured;
    }
    public static bool TryAddRedisCache(
        this IServiceCollection services,
        IConfiguration configuration,
        string clientName = "Default",
        string sectionKey = "RedisCache"
    ) =>
        services.TryAddRedisCache(
            clientName: clientName,
            configurationSection: configuration.GetSection(key: sectionKey)
        );
    public static IFunctionsWorkerApplicationBuilder UseAuthorization(
        this IFunctionsWorkerApplicationBuilder builder
    ) =>
        builder.UseWhenHttpTrigger<AuthorizationMiddleware>();
    public static IFunctionsWorkerApplicationBuilder UseHttpExceptionHandler(
        this IFunctionsWorkerApplicationBuilder builder
    ) =>
        builder.UseWhenHttpTrigger<HttpExceptionMiddleware>();
    public static IFunctionsWorkerApplicationBuilder UseFeatureGate(
        this IFunctionsWorkerApplicationBuilder builder
    ) =>
        builder.UseWhenHttpTrigger<FeatureGateMiddleware>();
    public static IFunctionsWorkerApplicationBuilder UseTriggerTargetingContext(
        this IFunctionsWorkerApplicationBuilder builder
    ) =>
        builder.UseWhenHttpTrigger<TriggerTargetingContextMiddleware>();
}

