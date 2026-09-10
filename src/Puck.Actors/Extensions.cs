using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Puck.Azure;

namespace Puck.Actors;

public static class Extensions {
    public static BlobContainerClient GetUserBlobContainerClient(
        this TokenCredential tokenCredential,
        string? endpoint,
        OnBehalfOfOptions onBehalfOfOptions,
        string? userAssertion,
        string? userObjectId
    ) {
        return (!Guid.TryParseExact(
                format: "D",
                input: userObjectId,
                result: out _
            )
            ? throw new InvalidOperationException(message: "User identity must be a valid GUID in \"D\" format.")
            : (!Uri.TryCreate(
                result: out var endpointUri,
                uriKind: UriKind.Absolute,
                // Uri.ToString() includes a trailing slash; a doubled separator loses the SDK's container name.
                uriString: $"{endpoint?.TrimEnd(trimChar: '/')}/{userObjectId}"
            )
            ? throw new InvalidOperationException(message: "Blob storage endpoint must be a valid URI.")
            : new(
                blobContainerUri: endpointUri,
                credential: tokenCredential.ToOnBehalfOfCredential(
                    options: onBehalfOfOptions,
                    userAssertion: userAssertion
                )
            )));
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
                                    refreshAll: true
                                )
                                .SetRefreshInterval(refreshInterval: refreshInterval);
                        })
                        .ConfigureStartupOptions(configure: startupOptions => {
                            startupOptions.Timeout = TimeSpan.FromSeconds(value: 17);
                        })
                        .Select(
                            keyFilter: KeyFilter.Any,
                            labelFilter: LabelFilter.Null,
                            tagFilters: default
                        );
                },
                optional: optional
            );
            services.AddAzureAppConfiguration();
        }

        return isConfigured;
    }
    public static ISiloBuilder UsePuckOrleansClustering(
        this ISiloBuilder siloBuilder,
        IConfiguration configuration,
        TokenCredential? tokenCredential = null,
        string defaultServiceId = "puck-actors"
    ) {
        tokenCredential ??= new DefaultAzureCredential();

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
                    clusteringOptions.TableServiceClient = new TableServiceClient(
                        endpoint: privateTableEndpointUri,
                        tokenCredential: tokenCredential
                    );
                })
                .UseAzureTableReminderService(configure: reminderOptions => {
                    reminderOptions.TableServiceClient = new TableServiceClient(
                        endpoint: privateTableEndpointUri,
                        tokenCredential: tokenCredential
                    );
                })
                .AddAzureBlobGrainStorage(
                    Constants.UserStateStorageName,
                    ((Action<Orleans.Configuration.AzureBlobStorageOptions>)(blobStorageOptions => {
                        blobStorageOptions.BlobServiceClient = new BlobServiceClient(
                            credential: tokenCredential,
                            serviceUri: privateBlobEndpointUri
                        );
                        blobStorageOptions.ContainerName = (configuration.GetValue<string>(key: "Orleans:GrainStateContainerName") ?? "orleans-state");
                    }))
                );
        } else {
            siloBuilder
                .UseLocalhostClustering()
                .UseInMemoryReminderService()
                .AddMemoryGrainStorage(name: Constants.UserStateStorageName);
        }

        return siloBuilder;
    }
}


