using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using Puck.Azure.Functions.Middleware;
using Puck.Azure.Functions.Services;
using Puck.Storage;

namespace Puck.Azure.Functions.HttpTriggers;

/// <summary>
/// The session-start re-attestation hook: for each of the caller's own key-pair blobs, mints a
/// <c>binding.cbor</c> if none exists yet, or re-signs the existing one with a fresh validity
/// window. The natural trigger for this is every authenticated session start — the one moment
/// the subject is provably online, which is what makes a short validity window affordable at
/// all without making long offline play impossible.
///
/// This trigger only re-attests keys the platform already minted and stored for the caller; it
/// does not mint key pairs itself (<c>GenerateKeyPair</c> does that). Onboarding does not
/// call this yet — see the report for the one obvious call that would wire it in.
/// </summary>
public sealed class AttestKeys(
    IBindingService bindingService,
    IOptionsMonitor<PublicStorageOptions> publicStorageOptions,
    TimeProvider timeProvider,
    IUserCredentialContext userCredentialContext
) {
    [FeatureGate(features: nameof(AttestKeys))]
    [Function(name: nameof(AttestKeys))]
    public async Task<IActionResult> Run(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "post",
            Route = "attest-keys"
        )] HttpRequest httpRequest,
        FunctionContext functionContext
    ) {
        // THE ADMISSION BOUNDARY (wire specification §9). One instant is captured here, when the request
        // arrives, and carried into every mint and re-attestation below — rather than each of them reading
        // a clock of its own. §9's wording exists to stop a caller reading UtcNow immediately before an
        // envelope call, which moves the wall-clock read one stack frame and changes nothing else; for a
        // request-scoped service the request IS the boundary the rule asks for, and this is where it is.
        // A second effect that matters on its own: every binding minted while serving one request shares
        // one authored window, instead of drifting by however long the loop below takes.
        var now = timeProvider.GetUtcNow();
        var cancellationToken = functionContext.CancellationToken;
        var httpContext = httpRequest.HttpContext;
        var userObjectId = httpContext.User.Identity?.Name;

        ArgumentException.ThrowIfNullOrWhiteSpace(argument: userObjectId);

        var endpoint = publicStorageOptions.CurrentValue.Endpoint;

        if (!Uri.TryCreate(
            result: out var containerUri,
            uriKind: UriKind.Absolute,
            uriString: $"{endpoint}/{userObjectId}"
        )) {
            throw new InvalidOperationException(message: "Public storage endpoint is not a valid absolute URI.");
        }

        var containerClient = new BlobContainerClient(
            blobContainerUri: containerUri,
            credential: userCredentialContext.UserContext
        );
        var attestations = new List<object>();

        await foreach (var blobItem in containerClient.GetBlobsAsync(
            cancellationToken: cancellationToken,
            prefix: "private/keys/",
            states: BlobStates.None,
            traits: BlobTraits.None
        )) {
            // Expected shape: private/keys/{type}/fingerprints/sha256:{fp}/public.pem
            if (!blobItem.Name.EndsWith(comparisonType: StringComparison.Ordinal, value: "/public.pem")) {
                continue;
            }

            var segments = blobItem.Name.Split(separator: '/');

            if (6 != segments.Length) {
                continue;
            }

            var keyType = segments[2];
            var fingerprintSegment = segments[4]; // "sha256:{fp}"
            var keyPath = string.Join(separator: '/', value: segments[1..5]); // "keys/{type}/fingerprints/sha256:{fp}"
            var publicKeyBlobClient = containerClient.GetBlobClient(blobName: blobItem.Name);
            var publicKeyPem = Encoding.UTF8.GetString(bytes: (await publicKeyBlobClient
                .DownloadContentAsync(cancellationToken: cancellationToken))
                .Value
                .Content
                .ToArray()
            );
            var pemFields = PemEncoding.Find(pemData: publicKeyPem);
            var publicKey = new byte[pemFields.DecodedDataLength];

            Convert.TryFromBase64Chars(
                bytes: publicKey,
                bytesWritten: out _,
                chars: publicKeyPem.AsSpan(range: pemFields.Base64Data)
            );

            var bindingBlobClient = containerClient.GetBlobClient(blobName: $"private/{keyPath}/binding.cbor");
            BindingResult result;
            bool reattested;

            try {
                var existingBinding = (await bindingBlobClient
                    .DownloadContentAsync(cancellationToken: cancellationToken))
                    .Value
                    .Content
                    .ToArray();

                result = await bindingService.ReattestAsync(
                    cancellationToken: cancellationToken,
                    existingBinding: existingBinding,
                    now: now
                );
                reattested = true;
            } catch (RequestFailedException e)
              when ((404 == e.Status)) {
                result = await bindingService.MintSubjectKeyBindingAsync(
                    cancellationToken: cancellationToken,
                    now: now,
                    request: new() {
                        KeyType = keyType,
                        PublicKey = publicKey,
                        SubjectId = userObjectId,
                    }
                );
                reattested = false;
            }

            await bindingBlobClient.UploadAsync(
                cancellationToken: cancellationToken,
                content: BinaryData.FromBytes(data: result.Binding),
                options: new BlobUploadOptions {
                    HttpHeaders = new() {
                        ContentType = "application/cbor",
                    },
                }
            );

            attestations.Add(item: new {
                KeyId = result.KeyId,
                KeyType = keyType,
                NotAfter = result.NotAfter,
                NotBefore = result.NotBefore,
                Reattested = reattested,
                StoredKeyId = $"users/{userObjectId}/keys/{keyType}/fingerprints/{fingerprintSegment}",
            });
        }

        return new OkObjectResult(value: new {
            Attestations = attestations,
        });
    }
}

