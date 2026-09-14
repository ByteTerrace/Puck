using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Serialization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Puck.Azure;
using Puck.Storage;

namespace Puck.Actors.Services;

public sealed class OnboardingOptions {
    public string AllUsersGroupObjectId { get; set; } = "6997d638-98e6-4738-a507-7d960bc1e537";
    public string AttributeName { get; set; } = "ObjectId";
    public string AttributeSetName { get; set; } = "ByteTerraceUsers";
}

/// <summary>
/// The four provisioning steps. Every step is idempotent; EscrowKeysAsync doubles as the
/// ABAC-propagation probe — it fails with 403 until the directory attribute is visible
/// to storage.
/// </summary>
public interface IUserProvisioningService {
    Task StampIdentityAsync(
        string userObjectId,
        CancellationToken cancellationToken
    );
    Task CreateStorageAsync(
        int partition,
        string userObjectId,
        CancellationToken cancellationToken
    );
    Task<JsonObject> EscrowKeysAsync(
        int partition,
        string userAssertion,
        string userObjectId,
        CancellationToken cancellationToken
    );
    Task FinalizeAsync(
        int partition,
        string userObjectId,
        CancellationToken cancellationToken
    );
    Task SetStorageAccessAsync(
        bool canRead,
        bool canWrite,
        int partition,
        string userObjectId,
        CancellationToken cancellationToken
    );
    /// <summary>
    /// Sets the container's GuestAccess metadata key — the tenant-facing lever for non-owner
    /// access to their public/ prefix (None | Read | ReadWrite; missing behaves as Read).
    /// </summary>
    Task SetGuestAccessAsync(
        string guestAccess,
        int partition,
        string userObjectId,
        CancellationToken cancellationToken
    );
    /// <summary>
    /// Sets the container's Frozen metadata key — the migration write-freeze lever, distinct from
    /// the CanRead/CanWrite suspension levers. Frozen=True blocks user writes via ABAC while
    /// leaving reads (the copy) and deletes (the drain) intact.
    /// </summary>
    Task SetStorageFrozenAsync(
        bool frozen,
        int partition,
        string userObjectId,
        CancellationToken cancellationToken
    );
    /// <summary>
    /// Server-side copies one page of blobs under <paramref name="prefix"/> from the user's
    /// container in <paramref name="sourcePartition"/> to the same-named container in
    /// <paramref name="targetPartition"/>, as the HOST — valid only for non-private prefixes
    /// (system/), which the host condition permits on both ends. Bounded per call so the grain
    /// stays responsive; resume with the returned continuation token.
    /// </summary>
    Task<MigrationBatch> CopyPrefixAsHostAsync(
        int sourcePartition,
        int targetPartition,
        string prefix,
        string userObjectId,
        string? continuationToken,
        CancellationToken cancellationToken
    );
    /// <summary>
    /// Same page-bounded server-side copy, but running entirely as the user via the escrowed
    /// assertion — the only authority that can read private/ data. Already-present same-length
    /// blobs are skipped (the source is frozen for the whole migration, so present implies copied).
    /// </summary>
    Task<MigrationBatch> CopyPrefixAsUserAsync(
        int sourcePartition,
        int targetPartition,
        string prefix,
        string userAssertion,
        string userObjectId,
        string? continuationToken,
        CancellationToken cancellationToken
    );
    /// <summary>Deletes one page of the user's blobs under <paramref name="prefix"/>, as the host (non-private prefixes only).</summary>
    Task<MigrationBatch> DeletePrefixAsHostAsync(
        int partition,
        string prefix,
        string userObjectId,
        CancellationToken cancellationToken
    );
    /// <summary>Deletes one page of the user's blobs under <paramref name="prefix"/>, as the user.</summary>
    Task<MigrationBatch> DeletePrefixAsUserAsync(
        int partition,
        string prefix,
        string userAssertion,
        string userObjectId,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// One bounded slice of a migration copy or drain. <see cref="IsCompleted"/> means the prefix has
/// been fully processed; otherwise call again (passing <see cref="ContinuationToken"/> for copies —
/// drains re-enumerate, since deletion shrinks the listing underneath any token).
/// </summary>
public readonly record struct MigrationBatch(
    int ProcessedCount,
    bool IsCompleted,
    string? ContinuationToken = null
);

/// <summary>
/// A key pair as reported to the caller: identity plus public half only. Private key material
/// never leaves the user's own container.
/// </summary>
internal sealed record UserKey(
    string Id,
    byte[] PublicKey
);


public sealed class DefaultUserProvisioningService(
    GraphServiceClient graphServiceClient,
    [FromKeyedServices(key: "Default")] TokenCredential hostCredential,
    IKeyPairService keyPairService,
    IOptionsMonitor<OnboardingOptions> onboardingOptions,
    IOptionsMonitor<OnBehalfOfOptions> onBehalfOfOptions,
    IPartitionResolver partitionResolver,
    IOptionsMonitor<PublicStorageOptions> publicStorageOptions,
    [FromKeyedServices(key: IdentityUtilities.ClientAssertionCredentialKey)] TokenCredential tokenCredential
) : IUserProvisioningService {
    // A user's container lives in an explicit partition — never recomputed here, because during a
    // migration the recorded home and the computed partition differ. The host identity reaches every
    // partition account with the same non-private ABAC grant.
    private BlobContainerClient GetHostContainerClient(int partition, string userObjectId) =>
        new(
            blobContainerUri: new(
                baseUri: partitionResolver.GetBlobEndpoint(partition: partition),
                relativeUri: userObjectId
            ),
            credential: hostCredential
        );
    // The user's own credential, which is the only thing that can touch private/ data.
    private BlobContainerClient GetUserContainerClient(
        int partition,
        TokenCredential userCredential,
        string userObjectId
    ) =>
        new(
            blobContainerUri: new(
                baseUri: partitionResolver.GetBlobEndpoint(partition: partition),
                relativeUri: userObjectId
            ),
            credential: userCredential
        );

    public async Task StampIdentityAsync(
        string userObjectId,
        CancellationToken cancellationToken
    ) {
        var options = onboardingOptions.CurrentValue;

        await Task.WhenAll(
            AddObjectIdSecurityAttribute(
                attributeName: options.AttributeName,
                attributeSetName: options.AttributeSetName,
                cancellationToken: cancellationToken,
                graphServiceClient: graphServiceClient,
                objectId: userObjectId
            ),
            AddToAllUsersGroup(
                cancellationToken: cancellationToken,
                graphServiceClient: graphServiceClient,
                groupObjectId: options.AllUsersGroupObjectId,
                memberObjectId: userObjectId
            )
        );

        static async Task AddObjectIdSecurityAttribute(
            GraphServiceClient graphServiceClient,
            string attributeName,
            string attributeSetName,
            string objectId,
            CancellationToken cancellationToken = default
        ) {
            await graphServiceClient
                .Users[objectId]
                .PatchAsync(
                    body: new() {
                        CustomSecurityAttributes = new CustomSecurityAttributeValue {
                            AdditionalData = new Dictionary<string, object> {{
                                attributeSetName, new UntypedObject(properties: new Dictionary<string, UntypedNode> {
                                    { "@odata.type", new UntypedString(value: "#Microsoft.DirectoryServices.CustomSecurityAttributeValue") },
                                    { attributeName, new UntypedString(value: objectId) },
                                })
                            }},
                        },
                    },
                    cancellationToken: cancellationToken
                );
        }
        static async Task AddToAllUsersGroup(
            GraphServiceClient graphServiceClient,
            string groupObjectId,
            string memberObjectId,
            CancellationToken cancellationToken = default
        ) {
            try {
                await graphServiceClient
                    .Groups[groupObjectId]
                    .Members
                    .Ref
                    .PostAsync(
                        body: new() { OdataId = $"https://graph.microsoft.com/v1.0/users/{memberObjectId}", },
                        cancellationToken: cancellationToken
                    );
            } catch (ODataError e)
              when (400 == e.ResponseStatusCode) { } // User is already a member of the group, ignore.
        }
    }

    public async Task CreateStorageAsync(
        int partition,
        string userObjectId,
        CancellationToken cancellationToken
    ) {
        _ = await GetHostContainerClient(partition: partition, userObjectId: userObjectId)
            .CreateIfNotExistsAsync(
                cancellationToken: cancellationToken,
                metadata: new Dictionary<string, string> {
                    { "CanRead", "Enabled" },
                    { "CanWrite", "Enabled" },
                    { "Frozen", "False" },
                    { "GuestAccess", "Read" },
                    { "State", "Unknown" },
                },
                publicAccessType: PublicAccessType.None
            );
    }

    public async Task<JsonObject> EscrowKeysAsync(
        int partition,
        string userAssertion,
        string userObjectId,
        CancellationToken cancellationToken
    ) {
        var (defaultUploadOptions, _) = publicStorageOptions.CurrentValue;
        var userBlobContainerClient = tokenCredential.GetUserBlobContainerClient(
            endpoint: partitionResolver.GetBlobEndpoint(partition: partition).ToString(),
            onBehalfOfOptions: onBehalfOfOptions.CurrentValue,
            userAssertion: userAssertion,
            userObjectId: userObjectId
        );
        var ensureEcdh = EnsureKey(type: "ecdh");
        var ensureEcdsa = EnsureKey(type: "ecdsa");

        await Task.WhenAll(
            ensureEcdh,
            ensureEcdsa
        );

        var ecdhKey = ensureEcdh.Result;
        var ecdsaKey = ensureEcdsa.Result;

        return new() {
            ["encryption"] = new JsonObject {
                ["id"] = ecdhKey.Id,
                ["publicKey"] = JsonValue.Create(value: ecdhKey.PublicKey),
            },
            ["signing"] = new JsonObject {
                ["id"] = ecdsaKey.Id,
                ["publicKey"] = JsonValue.Create(value: ecdsaKey.PublicKey),
            },
        };

        // Idempotence: key paths are fingerprint-derived, so minting again would leave an
        // additional orphan pair rather than overwrite. Adopt whatever the user's container
        // already holds — this is what keeps re-onboarding (grain state lost, a resumed
        // fault, a repeated POST) from reissuing keys the user already has.
        async Task<UserKey> EnsureKey(string type) {
            var existingKey = await TryGetExistingKeyAsync(
                cancellationToken: cancellationToken,
                type: type,
                userBlobContainerClient: userBlobContainerClient,
                userObjectId: userObjectId
            );

            if (existingKey is not null) {
                return existingKey;
            }

            var keyPair = await keyPairService.GenerateAsync(
                cancellationToken: cancellationToken,
                request: new() { Type = type },
                userObjectId: userObjectId
            );

            await keyPair.ExportToUserBlobStorageAsync(
                cancellationToken: cancellationToken,
                tags: defaultUploadOptions?.Tags,
                userBlobContainerClient: userBlobContainerClient
            );

            return new(
                Id: keyPair.Id,
                PublicKey: keyPair.PublicKey
            );
        }
    }

    /// <summary>
    /// Returns the key pair already present in the user's container for <paramref name="type"/>,
    /// or null when none is complete. A pair counts only when both PEMs are present, so a
    /// half-written export from an interrupted run is reissued rather than adopted. The listing
    /// is itself ABAC-gated on the user's own container, so it doubles as the propagation probe.
    /// </summary>
    private static async Task<UserKey?> TryGetExistingKeyAsync(
        BlobContainerClient userBlobContainerClient,
        string type,
        string userObjectId,
        CancellationToken cancellationToken
    ) {
        const string PrivateKeyFileName = "private.pem";
        const string PrivatePrefix = "private/";
        const string PublicKeyFileName = "public.pem";

        var fileNamesByDirectory = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);

        await foreach (var blob in userBlobContainerClient.GetBlobsAsync(
            cancellationToken: cancellationToken,
            prefix: $"{PrivatePrefix}keys/{type}/fingerprints/",
            states: BlobStates.None,
            traits: BlobTraits.None
        )) {
            var separatorIndex = blob.Name.LastIndexOf(value: '/');

            if (0 > separatorIndex) {
                continue;
            }

            var directory = blob.Name[..separatorIndex];

            if (!fileNamesByDirectory.TryGetValue(
                key: directory,
                value: out var fileNames
            )) {
                fileNames = new(comparer: StringComparer.Ordinal);
                fileNamesByDirectory[directory] = fileNames;
            }

            fileNames.Add(item: blob.Name[(separatorIndex + 1)..]);
        }

        // Ordinal sort so a container that somehow holds more than one complete pair resolves
        // to the same key on every call instead of flip-flopping.
        var keyDirectory = fileNamesByDirectory
            .Where(predicate: entry =>
                entry.Value.Contains(item: PrivateKeyFileName) &&
                entry.Value.Contains(item: PublicKeyFileName)
            )
            .Select(selector: entry => entry.Key)
            .Order(comparer: StringComparer.Ordinal)
            .FirstOrDefault();

        if (keyDirectory is null) {
            return null;
        }

        var publicKeyPem = (await userBlobContainerClient
            .GetBlobClient(blobName: $"{keyDirectory}/{PublicKeyFileName}")
            .DownloadContentAsync(cancellationToken: cancellationToken))
            .Value
            .Content
            .ToString();
        var pemFields = PemEncoding.Find(pemData: publicKeyPem);

        return new(
            // The stored path is authoritative: the key was minted with this exact id, so it is
            // reconstructed from the blob path rather than recomputed from the key material.
            Id: $"users/{userObjectId}/{keyDirectory[PrivatePrefix.Length..]}",
            PublicKey: Convert.FromBase64String(s: publicKeyPem[pemFields.Base64Data])
        );
    }

    public async Task FinalizeAsync(
        int partition,
        string userObjectId,
        CancellationToken cancellationToken
    ) {
        await GetHostContainerClient(partition: partition, userObjectId: userObjectId)
            .SetMetadataAsync(
                cancellationToken: cancellationToken,
                metadata: new Dictionary<string, string> {
                    { "CanRead", "Enabled" },
                    { "CanWrite", "Enabled" },
                    { "Frozen", "False" },
                    { "GuestAccess", "Read" },
                    { "State", "Ready" },
                }
            );
    }

    public async Task SetStorageAccessAsync(
        bool canRead,
        bool canWrite,
        int partition,
        string userObjectId,
        CancellationToken cancellationToken
    ) {
        // The container metadata CanRead/CanWrite keys are the ABAC suspend levers: CanRead gates
        // every private/ and system/ read, CanWrite gates every mutation. Read-merge so the State
        // key (and anything else) survives; the two access keys are the only ones we touch.
        var containerClient = GetHostContainerClient(partition: partition, userObjectId: userObjectId);
        var properties = await containerClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        var metadata = new Dictionary<string, string>(dictionary: properties.Value.Metadata) {
            ["CanRead"] = (canRead ? "Enabled" : "Disabled"),
            ["CanWrite"] = (canWrite ? "Enabled" : "Disabled"),
        };

        await containerClient.SetMetadataAsync(
            cancellationToken: cancellationToken,
            metadata: metadata
        );
    }

    public async Task SetGuestAccessAsync(
        string guestAccess,
        int partition,
        string userObjectId,
        CancellationToken cancellationToken
    ) {
        // Same read-merge discipline as the other levers: GuestAccess is the only key touched.
        var containerClient = GetHostContainerClient(partition: partition, userObjectId: userObjectId);
        var properties = await containerClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        var metadata = new Dictionary<string, string>(dictionary: properties.Value.Metadata) {
            ["GuestAccess"] = guestAccess,
        };

        await containerClient.SetMetadataAsync(
            cancellationToken: cancellationToken,
            metadata: metadata
        );
    }

    public async Task SetStorageFrozenAsync(
        bool frozen,
        int partition,
        string userObjectId,
        CancellationToken cancellationToken
    ) {
        // Same read-merge discipline as SetStorageAccessAsync: Frozen is the only key touched, so
        // the suspension levers and State survive.
        var containerClient = GetHostContainerClient(partition: partition, userObjectId: userObjectId);
        var properties = await containerClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        var metadata = new Dictionary<string, string>(dictionary: properties.Value.Metadata) {
            ["Frozen"] = (frozen ? "True" : "False"),
        };

        await containerClient.SetMetadataAsync(
            cancellationToken: cancellationToken,
            metadata: metadata
        );
    }

    public async Task<MigrationBatch> CopyPrefixAsHostAsync(
        int sourcePartition,
        int targetPartition,
        string prefix,
        string userObjectId,
        string? continuationToken,
        CancellationToken cancellationToken
    ) {
        var storageToken = await hostCredential.GetTokenAsync(
            cancellationToken: cancellationToken,
            requestContext: new(scopes: ["https://storage.azure.com/.default"])
        );

        return await CopyPrefixCoreAsync(
            cancellationToken: cancellationToken,
            continuationToken: continuationToken,
            prefix: prefix,
            sourceContainerClient: GetHostContainerClient(partition: sourcePartition, userObjectId: userObjectId),
            sourceReadToken: storageToken.Token,
            targetContainerClient: GetHostContainerClient(partition: targetPartition, userObjectId: userObjectId)
        );
    }

    public async Task<MigrationBatch> CopyPrefixAsUserAsync(
        int sourcePartition,
        int targetPartition,
        string prefix,
        string userAssertion,
        string userObjectId,
        string? continuationToken,
        CancellationToken cancellationToken
    ) {
        var userCredential = tokenCredential.ToOnBehalfOfCredential(
            options: onBehalfOfOptions.CurrentValue,
            userAssertion: userAssertion
        );
        // One token for the copy's source authorization — the storage service reads the source
        // blob itself, so nothing transits this process.
        var storageToken = await userCredential.GetTokenAsync(
            cancellationToken: cancellationToken,
            requestContext: new(scopes: ["https://storage.azure.com/.default"])
        );

        return await CopyPrefixCoreAsync(
            cancellationToken: cancellationToken,
            continuationToken: continuationToken,
            prefix: prefix,
            sourceContainerClient: GetUserContainerClient(
                partition: sourcePartition,
                userCredential: userCredential,
                userObjectId: userObjectId
            ),
            sourceReadToken: storageToken.Token,
            targetContainerClient: GetUserContainerClient(
                partition: targetPartition,
                userCredential: userCredential,
                userObjectId: userObjectId
            )
        );
    }

    public Task<MigrationBatch> DeletePrefixAsHostAsync(
        int partition,
        string prefix,
        string userObjectId,
        CancellationToken cancellationToken
    ) =>
        DeletePrefixCoreAsync(
            cancellationToken: cancellationToken,
            containerClient: GetHostContainerClient(partition: partition, userObjectId: userObjectId),
            prefix: prefix
        );

    public Task<MigrationBatch> DeletePrefixAsUserAsync(
        int partition,
        string prefix,
        string userAssertion,
        string userObjectId,
        CancellationToken cancellationToken
    ) =>
        DeletePrefixCoreAsync(
            cancellationToken: cancellationToken,
            containerClient: GetUserContainerClient(
                partition: partition,
                userCredential: tokenCredential.ToOnBehalfOfCredential(
                    options: onBehalfOfOptions.CurrentValue,
                    userAssertion: userAssertion
                ),
                userObjectId: userObjectId
            ),
            prefix: prefix
        );

    // Put Blob From URL rejects sources over 5000 MiB; fail with an actionable message instead of
    // a generic storage error so the paused migration's log says exactly which blob is stuck.
    private const long MaxSyncCopySourceLength = (5_000L * 1024 * 1024);
    private const int MigrationPageSize = 32;

    private static async Task<MigrationBatch> CopyPrefixCoreAsync(
        BlobContainerClient sourceContainerClient,
        BlobContainerClient targetContainerClient,
        string prefix,
        string sourceReadToken,
        string? continuationToken,
        CancellationToken cancellationToken
    ) {
        await foreach (var page in sourceContainerClient
            .GetBlobsAsync(
                cancellationToken: cancellationToken,
                prefix: prefix,
                states: BlobStates.None,
                traits: BlobTraits.Metadata
            )
            .AsPages(
                continuationToken: continuationToken,
                pageSizeHint: MigrationPageSize
            )
        ) {
            var processedCount = 0;

            foreach (var blob in page.Values) {
                // Accounts with a hierarchical namespace surface directory stubs in flat listings;
                // they cannot be copied as blobs, and the children's copies recreate their paths
                // implicitly. Flat-namespace accounts never produce the marker.
                if (blob.Metadata?.ContainsKey(key: "hdi_isfolder") ?? false) {
                    ++processedCount;

                    continue;
                }

                var targetBlobClient = targetContainerClient.GetBlockBlobClient(blobName: blob.Name);

                // The source is frozen for the whole migration, so a blob already at the
                // destination with the same length was copied by a previous slice — skip it.
                if (await targetBlobClient.ExistsAsync(cancellationToken: cancellationToken)) {
                    var targetProperties = await targetBlobClient.GetPropertiesAsync(cancellationToken: cancellationToken);

                    if (targetProperties.Value.ContentLength == blob.Properties.ContentLength) {
                        ++processedCount;

                        continue;
                    }
                }

                if (MaxSyncCopySourceLength < (blob.Properties.ContentLength ?? 0L)) {
                    throw new InvalidOperationException(message: $"Blob \"{blob.Name}\" is larger than the 5000 MiB synchronous-copy limit; it must be moved manually before this migration can proceed.");
                }

                await targetBlobClient.SyncUploadFromUriAsync(
                    cancellationToken: cancellationToken,
                    copySource: sourceContainerClient.GetBlobClient(blobName: blob.Name).Uri,
                    options: new BlobSyncUploadFromUriOptions {
                        CopySourceBlobProperties = true,
                        SourceAuthentication = new(parameter: sourceReadToken, scheme: "Bearer"),
                    }
                );

                ++processedCount;
            }

            var nextContinuationToken = (string.IsNullOrEmpty(value: page.ContinuationToken)
                ? null
                : page.ContinuationToken);

            return new(
                ContinuationToken: nextContinuationToken,
                IsCompleted: (nextContinuationToken is null),
                ProcessedCount: processedCount
            );
        }

        return new(
            IsCompleted: true,
            ProcessedCount: 0
        );
    }

    private static async Task<MigrationBatch> DeletePrefixCoreAsync(
        BlobContainerClient containerClient,
        string prefix,
        CancellationToken cancellationToken
    ) {
        // No continuation across slices: deletion shrinks the listing underneath any token, so each
        // slice re-enumerates from the front and the set converges to empty.
        await foreach (var page in containerClient
            .GetBlobsAsync(
                cancellationToken: cancellationToken,
                prefix: prefix,
                states: BlobStates.None,
                traits: BlobTraits.None
            )
            .AsPages(pageSizeHint: MigrationPageSize)
        ) {
            var deletedCount = 0;
            var deferredCount = 0;

            foreach (var blob in page.Values) {
                try {
                    await containerClient
                        .GetBlobClient(blobName: blob.Name)
                        .DeleteIfExistsAsync(
                            cancellationToken: cancellationToken,
                            snapshotsOption: DeleteSnapshotsOption.IncludeSnapshots
                        );

                    ++deletedCount;
                } catch (RequestFailedException e)
                  when ((409 == e.Status) && ("DirectoryIsNotEmpty" == e.ErrorCode)) {
                    // Hierarchical-namespace directory stub whose children are still present.
                    // Directories sort before their children, so the children fall later in this
                    // or a subsequent slice; once they're gone, re-enumeration deletes the stub.
                    ++deferredCount;
                }
            }

            return new(
                IsCompleted: ((0 == deletedCount) && (0 == deferredCount) && string.IsNullOrEmpty(value: page.ContinuationToken)),
                ProcessedCount: deletedCount
            );
        }

        return new(
            IsCompleted: true,
            ProcessedCount: 0
        );
    }
}

