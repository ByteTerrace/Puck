using System.Net.Http.Headers;
using Azure.Core;

namespace Puck.World.Server;

/// <summary>Publishes this world's own counterpart claim payload to the platform oracle after a successful
/// <see cref="WorldOwnedWorldSync.Push"/> — the addressable-later half of a border proof: the document write is the
/// primary effect and a claim-post failure never fails the push itself (see
/// <see cref="WorldOwnedWorldSync.PushOne"/>'s own remarks).</summary>
public interface ICounterpartPublisher {
    /// <summary>Posts one world's counterpart claim payload.</summary>
    /// <param name="worldId">The owned-world id the platform route names — the same spelling
    /// <see cref="WorldOwnedWorldFileName"/> would produce, never the escaped filename.</param>
    /// <param name="payload">The exact <see cref="WorldCounterpartAttestationProtocol.Payload"/> bytes to sign and store.</param>
    /// <param name="detail">What happened — a short line the push outcome echoes either way.</param>
    /// <returns><see langword="true"/> when the platform accepted the claim.</returns>
    bool TryPublish(string worldId, ReadOnlyMemory<byte> payload, out string detail);
}
/// <summary>
/// The live <see cref="ICounterpartPublisher"/>: <c>POST /api/worlds/{worldId}/counterpart</c> under the same
/// ambient <c>DefaultAzureCredential</c> posture <see cref="WorldApiCounterpartResolver"/> reads with — there is no
/// Puck app registration; the platform app pre-authorizes Azure CLI/VS Code for <c>user_impersonation</c>.
/// </summary>
public sealed class HttpCounterpartPublisher : ICounterpartPublisher {
    private const string PlatformApiScope = "api://e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7/user_impersonation";

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(seconds: 15);

    private readonly TokenCredential m_credential;
    private readonly HttpClient m_httpClient;

    /// <summary>Initializes the publisher.</summary>
    /// <param name="httpClient">The API client — base address the API root. Owned by the caller; not disposed here.</param>
    /// <param name="credential">The ambient platform-API credential.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> or <paramref name="credential"/> is <see langword="null"/>.</exception>
    public HttpCounterpartPublisher(HttpClient httpClient, TokenCredential credential) {
        ArgumentNullException.ThrowIfNull(argument: httpClient);
        ArgumentNullException.ThrowIfNull(argument: credential);

        m_credential = credential;
        m_httpClient = httpClient;
    }

    /// <inheritdoc/>
    public bool TryPublish(string worldId, ReadOnlyMemory<byte> payload, out string detail) {
        using var timeout = new CancellationTokenSource(delay: OperationTimeout);
        AccessToken token;

        try {
            token = m_credential.GetToken(
                cancellationToken: timeout.Token,
                requestContext: new TokenRequestContext(scopes: [PlatformApiScope])
            );
        } catch (Exception exception) {
            detail = $"platform API token acquisition failed — {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        try {
            using var request = new HttpRequestMessage(
                method: HttpMethod.Post,
                requestUri: $"api/worlds/{worldId}/counterpart"
            ) {
                Content = new ByteArrayContent(content: payload.ToArray()),
                Headers = { Authorization = new AuthenticationHeaderValue(scheme: "Bearer", parameter: token.Token) },
            };
            using var response = m_httpClient.Send(
                cancellationToken: timeout.Token,
                request: request
            );

            if (!response.IsSuccessStatusCode) {
                detail = $"answered {((int)response.StatusCode)} {response.StatusCode}";

                return false;
            }

            detail = "accepted";

            return true;
        } catch (OperationCanceledException) {
            detail = $"timed out after {OperationTimeout.TotalSeconds:0}s";

            return false;
        } catch (Exception exception) {
            detail = $"transport error — {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
}
