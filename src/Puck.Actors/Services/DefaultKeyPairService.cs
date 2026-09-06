using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace Puck.Actors.Services;

public interface IKeyPairService
{
    Task<GenerateKeyPairResponse> GenerateAsync(
        GenerateKeyPairRequest request,
        string? userObjectId,
        CancellationToken cancellationToken
    );
}

public sealed class GenerateKeyPairRequest
{
    public string? Type { get; set; }
}

public sealed record class GenerateKeyPairResponse(
    string Fingerprint,
    string Id,
    byte[] ProtectedPassword,
    byte[] ProtectedPrivateKey,
    byte[] PublicKey
)
{
    public async Task ExportToUserBlobStorageAsync(
        BlobContainerClient userBlobContainerClient,
        IDictionary<string, string>? tags,
        CancellationToken cancellationToken
    ) {
        var keyPath = string.Join(
            separator: '/',
            values: Id.Split(separator: '/').Skip(count: 2)
        );
        var privateKeyUpload = userBlobContainerClient
            .GetBlobClient(blobName: $"private/{keyPath}/private.pem")
            .UploadAsync(
                cancellationToken: cancellationToken,
                content: BinaryData.FromBytes(data: PemEncoding.WriteUtf8(
                    data: ProtectedPrivateKey,
                    utf8Label: "ENCRYPTED PRIVATE KEY"u8
                )),
                options: new() {
                    HttpHeaders = new() {
                        ContentDisposition = $"attachment; filename*=UTF-8''{Fingerprint}-private.pem",
                        ContentType = "application/x-pem-file",
                    },
                    Metadata = new Dictionary<string, string> {
                        { "KeyProtector", Convert.ToBase64String(inArray: ProtectedPassword) },
                    },
                    Tags = tags,
                }
            );
        var publicKeyUpload = userBlobContainerClient
            .GetBlobClient(blobName: $"private/{keyPath}/public.pem")
            .UploadAsync(
                cancellationToken: cancellationToken,
                content: BinaryData.FromBytes(data: PemEncoding.WriteUtf8(
                    data: PublicKey,
                    utf8Label: "PUBLIC KEY"u8
                )),
                options: new() {
                    HttpHeaders = new() {
                        ContentDisposition = $"attachment; filename*=UTF-8''{Fingerprint}-public.pem",
                        ContentType = "application/x-pem-file",
                    },
                    Tags = tags,
                }
            );

        await Task.WhenAll(
            privateKeyUpload,
            publicKeyUpload
        );
    }
}

public sealed class DefaultKeyPairService(IDataProtectionProvider dataProtectionProvider) : IKeyPairService
{
    private static ECAlgorithm CreateAlgorithm(string algorithmType) {
        return algorithmType switch {
            "ecdh" => ECDiffieHellman.Create(curve: ECCurve.NamedCurves.nistP256),
            "ecdsa" => ECDsa.Create(curve: ECCurve.NamedCurves.nistP256),
            _ => throw new InvalidOperationException(message: $"Unsupported algorithm: {algorithmType}.")
        };
    }

    public Task<GenerateKeyPairResponse> GenerateAsync(
        GenerateKeyPairRequest request,
        string? userObjectId,
        CancellationToken cancellationToken
    ) {
        var type = request.Type;

        ArgumentException.ThrowIfNullOrWhiteSpace(argument: type);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: userObjectId);

        type = type.ToLowerInvariant();

        using var algorithm = CreateAlgorithm(algorithmType: type);

        var publicKey = algorithm.ExportSubjectPublicKeyInfo();
        var fingerprint = Convert.ToHexStringLower(bytes: SHA256.HashData(source: publicKey));
        var dataProtector = dataProtectionProvider.CreateProtector(
            purpose: "users.keys.fingerprints",
            subPurposes: [
                userObjectId,
                type,
                fingerprint,
            ]
        );
        var password = RandomNumberGenerator.GetBytes(count: 32);

        return Task.FromResult(result: new GenerateKeyPairResponse(
            Fingerprint: fingerprint,
            Id: $"users/{userObjectId}/keys/{type}/fingerprints/sha256:{fingerprint}",
            ProtectedPassword: dataProtector.Protect(plaintext: password),
            ProtectedPrivateKey: algorithm.ExportEncryptedPkcs8PrivateKey(
                passwordBytes: password,
                pbeParameters: new(
                    encryptionAlgorithm: PbeEncryptionAlgorithm.Aes256Cbc,
                    hashAlgorithm: HashAlgorithmName.SHA256,
                    iterationCount: 100_000
                )
            ),
            PublicKey: publicKey
        ));
    }
}

