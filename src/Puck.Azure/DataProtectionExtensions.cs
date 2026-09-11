using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using CryptographyClient = Azure.Security.KeyVault.Keys.Cryptography.CryptographyClient;

namespace Puck.Azure;

public static class DataProtectionExtensions {
    /// <summary>
    /// Registers the shared Data Protection key ring described by a configuration section, and returns
    /// whether that section named both a key-ring blob and a Key Vault key. Hosts that share protected
    /// payloads must pass the same <paramref name="applicationName" />; without one a payload protected
    /// by one host cannot be unprotected by another.
    /// </summary>
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
        } else {
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
    /// <summary>
    /// Registers the shared Data Protection key ring from a named configuration section, and returns
    /// whether that section named both a key-ring blob and a Key Vault key.
    /// </summary>
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
}
