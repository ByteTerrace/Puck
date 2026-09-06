using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace Puck.Azure.Functions.Services;

public sealed class BlobSasUriOptions
{
    public string[]? AllowedEndpointSuffixes { get; set; }
    public string? DefaultEndpoint { get; set; }
    public string? DefaultPreauthorizedAgentObjectId { get; set; }

    public void Deconstruct(
        out string[] allowedEndpointSuffixes,
        out string? defaultEndpoint,
        out string? defaultPreauthorizedAgentObjectId
    ) {
        allowedEndpointSuffixes = (AllowedEndpointSuffixes ?? []);
        defaultEndpoint = DefaultEndpoint;
        defaultPreauthorizedAgentObjectId = DefaultPreauthorizedAgentObjectId;
    }
}

public sealed class GenerateBlobSasUriRequest
{
    public string? BlobName { get; set; }
    public string? BlobVersionId { get; set; }
    public string? ContainerName { get; set; }
    public string? CorrelationId { get; set; }
    // User-bound SAS (sduoid): storage only honors the token when the request
    // also carries a bearer token whose oid claim matches this value.
    public string? DelegatedUserObjectId { get; set; }
    public string? Endpoint { get; set; }
    public DateTimeOffset? ExpiresOn { get; set; }
    public string? IpRange { get; set; }
    public BlobSasPermissions? Permissions { get; set; }
    public string? PreauthorizedAgentObjectId { get; set; }

    public void Deconstruct(
        out string? blobName,
        out string? blobVersionId,
        out string? containerName,
        out string? correlationId,
        out string? delegatedUserObjectId,
        out string? endpoint,
        out DateTimeOffset expiresOn,
        out string? ipRange,
        out BlobSasPermissions permissions,
        out string? preauthorizedAgentObjectId
    ) {
        blobName = BlobName;
        blobVersionId = BlobVersionId;
        containerName = ContainerName;
        correlationId = CorrelationId;
        delegatedUserObjectId = DelegatedUserObjectId;
        expiresOn = (ExpiresOn ?? DateTimeOffset.UtcNow.AddMinutes(minutes: 1d));
        endpoint = Endpoint;
        ipRange = IpRange;
        permissions = (Permissions ?? BlobSasPermissions.Read);
        preauthorizedAgentObjectId = PreauthorizedAgentObjectId;
    }
}

public interface IBlobSasUriService {
    Task<Uri> GenerateAsync(
        GenerateBlobSasUriRequest request,
        TokenCredential tokenCredential,
        CancellationToken cancellationToken
    );
}

public sealed class DefaultBlobSasUriService(
    IOptionsMonitor<BlobSasUriOptions> blobSasUriOptions,
    IUserStorageLocationService userStorageLocationService
) : IBlobSasUriService
{
    public async Task<Uri> GenerateAsync(
        GenerateBlobSasUriRequest request,
        TokenCredential tokenCredential,
        CancellationToken cancellationToken
    ) {
        var (
            allowedEndpointSuffixes,
            defaultEndpoint,
            defaultPreauthorizedAgentObjectId
        ) = blobSasUriOptions.CurrentValue;
        var (
            blobName,
            blobVersionId,
            containerName,
            correlationId,
            delegatedUserObjectId,
            endpoint,
            expiresOn,
            ipRange,
            permissions,
            preauthorizedAgentObjectId
        ) = request;

        // The container name is the user's oid; resolve the account that actually HOLDS their
        // container (the grain's recorded home, not the locally computed partition — they differ
        // while a migration is pending) unless the caller pinned an explicit endpoint. A
        // resolver-derived endpoint is trusted, so it skips the allowed-suffix check below (that
        // guard only exists for a caller-supplied endpoint).
        var endpointIsResolverDerived = false;

        if (string.IsNullOrWhiteSpace(value: endpoint)) {
            endpoint = Guid.TryParse(input: containerName, result: out var containerObjectId)
                ? (await userStorageLocationService.GetAsync(
                    cancellationToken: cancellationToken,
                    userObjectId: containerObjectId.ToString(format: "D")
                )).BlobEndpoint
                : defaultEndpoint;
            endpointIsResolverDerived = (endpoint is not null);
        }

        preauthorizedAgentObjectId ??= defaultPreauthorizedAgentObjectId;

        // User delegation keys are capped at seven days; clamp instead of letting
        // the key request fail, and never honor an arbitrarily long client value.
        var maximumExpiresOn = DateTimeOffset.UtcNow.AddDays(days: 7d);

        if (maximumExpiresOn < expiresOn) {
            expiresOn = maximumExpiresOn;
        }

        if (string.IsNullOrWhiteSpace(value: containerName) ||
            !Uri.TryCreate(
                result: out var endpointUri,
                uriKind: UriKind.Absolute,
                uriString: endpoint
            ) ||
            (Uri.UriSchemeHttps != endpointUri.Scheme) ||
            (!endpointIsResolverDerived && (
                (allowedEndpointSuffixes is null) ||
                !allowedEndpointSuffixes.Any(predicate: s => (
                    endpointUri.Host.Equals(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: s
                    ) ||
                    endpointUri.Host.EndsWith(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: $".{(s.StartsWith(value: '.') ? s[1..] : s)}"
                    )
                ))
            ))
        ) {
            throw new ArgumentException(message: "Bad request."); // TODO: Refine this, a lot.
        }

        var blobSasBuilder = new BlobSasBuilder(
            expiresOn: expiresOn,
            permissions: permissions
        ) {
            BlobContainerName = containerName,
            BlobName = "",
            Protocol = SasProtocol.Https,
        };

        if (!string.IsNullOrWhiteSpace(value: blobName)) {
            blobSasBuilder.BlobName = blobName;
        }

        if (!string.IsNullOrWhiteSpace(value: blobVersionId)) {
            blobSasBuilder.BlobVersionId = blobVersionId;
        }

        if (!string.IsNullOrWhiteSpace(value: correlationId)) {
            blobSasBuilder.CorrelationId = correlationId;
        }

        if (!string.IsNullOrWhiteSpace(value: delegatedUserObjectId)) {
            blobSasBuilder.DelegatedUserObjectId = delegatedUserObjectId;
        }

        if (!string.IsNullOrWhiteSpace(value: ipRange)) {
            blobSasBuilder.IPRange = SasIPRange.Parse(s: ipRange); // TODO: Handle potential exception with bad request response.
        }

        if (!string.IsNullOrWhiteSpace(value: preauthorizedAgentObjectId)) {
            blobSasBuilder.PreauthorizedAgentObjectId = preauthorizedAgentObjectId;
        }

        var blobServiceClient = new BlobServiceClient(
            credential: tokenCredential,
            options: default,
            serviceUri: endpointUri
        );

        return (("" == blobSasBuilder.BlobName)
            ? blobServiceClient
               .GetBlobContainerClient(blobContainerName: containerName)
               .GenerateUserDelegationSasUri(
                   builder: blobSasBuilder,
                   userDelegationKey: (await blobServiceClient
                       .GetUserDelegationKeyAsync(
                           cancellationToken: cancellationToken,
                           expiresOn: expiresOn,
                           startsOn: default
                       ))
                       .Value
               )
            : blobServiceClient
               .GetBlobContainerClient(blobContainerName: containerName)
               .GetBlobClient(blobName: blobName)
               .GenerateUserDelegationSasUri(
                   builder: blobSasBuilder,
                   userDelegationKey: (await blobServiceClient
                       .GetUserDelegationKeyAsync(
                           cancellationToken: cancellationToken,
                           expiresOn: expiresOn,
                           startsOn: default
                       ))
                       .Value
               )
        );
    }
}

