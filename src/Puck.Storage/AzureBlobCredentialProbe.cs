using Azure.Core;
using Azure.Identity;

namespace Puck.Storage;

/// <summary>The verdict of an ambient-credential probe: whether a storage token could be issued, when it expires, and
/// one line of detail a console verb can echo.</summary>
/// <param name="Available">Whether the ambient credential issued a storage token.</param>
/// <param name="Detail">The credential type that answered, or the first line of why none could.</param>
/// <param name="ExpiresOn">The token's expiry when one was issued; <see langword="null"/> otherwise.</param>
public readonly record struct AzureBlobCredentialStatus(bool Available, string Detail, DateTimeOffset? ExpiresOn);

/// <summary>
/// Asks the ambient Azure credential — the same <c>DefaultAzureCredential</c> chain
/// <see cref="AzureBlobObjectBlobStoreBackend"/> authenticates its service-URI targets with — whether it can issue a
/// blob-storage token right now. No app registration is involved, by design: a player's machine authenticates
/// ambiently (developer tooling, the OS broker, a shared token cache) and a hosted server runs as a user-assigned
/// managed identity, so one credential type covers both and neither needs a client id.
/// </summary>
/// <remarks>
/// This is a presence check, not a sign-in: it never prompts and never mutates anything. It answers the one question
/// an operator cannot otherwise answer before a push fails — "would the cloud let me in from this machine". A
/// connection-string target authenticates with the account key instead, so the answer is informational there.
/// A fresh <c>DefaultAzureCredential</c> is built per probe rather than borrowed from the backend: the chain caches
/// inside one instance, and a probe that could answer from a cache would be reporting the past.
/// </remarks>
public static class AzureBlobCredentialProbe {
    // Wide enough for the chain's own first line intact — that sentence ends in the troubleshooting URL, and half a
    // URL is worse than none.
    private const int DetailLengthLimit = 240;

    private static readonly string[] StorageScopes = ["https://storage.azure.com/.default"];

    /// <summary>Probes the ambient credential for a blob-storage token, bounded by <paramref name="timeout"/>.</summary>
    /// <param name="timeout">How long to wait before giving up — the caller is on a frame loop, so this is short.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The verdict.</returns>
    public static async ValueTask<AzureBlobCredentialStatus> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken = default) {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        var credential = new DefaultAzureCredential();

        timeoutSource.CancelAfter(delay: timeout);

        try {
            var token = await credential.GetTokenAsync(
                cancellationToken: timeoutSource.Token,
                requestContext: new TokenRequestContext(scopes: StorageScopes)
            );

            return new AzureBlobCredentialStatus(
                Available: true,
                Detail: "the ambient credential issued a storage token",
                ExpiresOn: token.ExpiresOn
            );
        } catch (OperationCanceledException) {
            return new AzureBlobCredentialStatus(
                Available: false,
                Detail: (cancellationToken.IsCancellationRequested
                    ? "canceled"
                    : $"timed out after {timeout.TotalSeconds:0}s"),
                ExpiresOn: null
            );
        } catch (Exception exception) {
            // The chain reports every attempted credential in one multi-line message; the first line is the verdict
            // and the rest is a per-credential transcript nobody wants inside a console echo.
            return new AzureBlobCredentialStatus(
                Available: false,
                Detail: Summarize(message: exception.Message),
                ExpiresOn: null
            );
        } finally {
            if (credential is IDisposable disposable) {
                disposable.Dispose();
            }
        }
    }

    private static string Summarize(string message) {
        var lineBreak = message.AsSpan().IndexOfAny(value0: '\r', value1: '\n');
        var line = ((lineBreak >= 0) ? message[..lineBreak] : message).Trim();

        return ((line.Length <= DetailLengthLimit) ? line : $"{line[..DetailLengthLimit]}…");
    }
}
