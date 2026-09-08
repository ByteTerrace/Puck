using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Puck.Actors.HealthChecks;
using Puck.Actors.Utilities;

using CryptographyClient = Azure.Security.KeyVault.Keys.Cryptography.CryptographyClient;

namespace Puck.Actors;

public static class Extensions
{
    public static BlobContainerClient GetUserBlobContainerClient(
        this TokenCredential tokenCredential,
        string? endpoint,
        OnBehalfOfOptions onBehalfOfOptions,
        string? userAssertion,
        string? userObjectId
    ) {
        return !Guid.TryParseExact(
                format: "D",
                input: userObjectId,
                result: out _
            )
            ? throw new InvalidOperationException(message: "User identity must be a valid GUID in \"D\" format.")
            : !Uri.TryCreate(
                result: out var endpointUri,
                uriKind: UriKind.Absolute,
                uriString: $"{endpoint}/{userObjectId}"
            )
            ? throw new InvalidOperationException(message: "Blob storage endpoint must be a valid URI.")
            : new(
                blobContainerUri: endpointUri,
                credential: tokenCredential.ToOnBehalfOfCredential(
                    options: onBehalfOfOptions,
                    userAssertion: userAssertion
                )
            );
    }
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
                        );
                },
                optional: optional
            );
            services.AddAzureAppConfiguration();
        }

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
        else {
            // Local development: ephemeral key ring so IDataProtectionProvider still resolves.
            services
                .AddDataProtection()
                .SetApplicationName(applicationName: applicationName);
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

    public static ISiloBuilder UsePuckOrleansClustering(
        this ISiloBuilder siloBuilder,
        IConfiguration configuration,
        TokenCredential? tokenCredential = null,
        string defaultServiceId = "puck-actors"
    ) {
        tokenCredential ??= new Azure.Identity.DefaultAzureCredential();

        siloBuilder.Configure<Orleans.Configuration.ClusterOptions>(configureOptions: clusterOptions => {
            clusterOptions.ClusterId = (configuration.GetValue<string>(key: "Orleans:ClusterId") ?? "byteterrace");
            clusterOptions.ServiceId = (configuration.GetValue<string>(key: "Orleans:ServiceId") ?? defaultServiceId);
        });

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

        if (isAzureHosted) {
            siloBuilder
                .Configure<Orleans.Configuration.EndpointOptions>(configureOptions: endpointOptions => {
                    endpointOptions.GatewayPort = (configuration.GetValue<int?>(key: "Orleans:GatewayPort") ?? Orleans.Configuration.EndpointOptions.DEFAULT_GATEWAY_PORT);
                    endpointOptions.SiloPort = (configuration.GetValue<int?>(key: "Orleans:SiloPort") ?? Orleans.Configuration.EndpointOptions.DEFAULT_SILO_PORT);
                })
                .UseAzureStorageClustering(configureOptions: clusteringOptions => {
                    clusteringOptions.TableServiceClient = new Azure.Data.Tables.TableServiceClient(
                        endpoint: privateTableEndpointUri,
                        tokenCredential: tokenCredential
                    );
                })
                .UseAzureTableReminderService(configure: reminderOptions => {
                    reminderOptions.TableServiceClient = new Azure.Data.Tables.TableServiceClient(
                        endpoint: privateTableEndpointUri,
                        tokenCredential: tokenCredential
                    );
                })
                .AddAzureBlobGrainStorage(
                    Constants.UserStateStorageName,
                    (Action<Orleans.Configuration.AzureBlobStorageOptions>)(blobStorageOptions => {
                        blobStorageOptions.BlobServiceClient = new BlobServiceClient(
                            credential: tokenCredential,
                            serviceUri: privateBlobEndpointUri
                        );
                        blobStorageOptions.ContainerName = (configuration.GetValue<string>(key: "Orleans:GrainStateContainerName") ?? "orleans-state");
                    })
                );
        }
        else {
            siloBuilder
                .UseLocalhostClustering()
                .UseInMemoryReminderService()
                .AddMemoryGrainStorage(name: Constants.UserStateStorageName);
        }

        return siloBuilder;
    }
}


