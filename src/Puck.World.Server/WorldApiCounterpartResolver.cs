using System.Net;
using System.Net.Http.Headers;
using Azure.Core;
using Puck.Attestation;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The cross-owner <see cref="IWorldNeighbourResolver"/> for an owner-named <see cref="WorldReference"/> — the API
/// is the only cross-user read path (worlds ARE users; a peer's own storage container is never reachable directly).
/// Resolves a <see cref="WorldReference.NeighbourKey"/> shaped <c>"owner/{oid:D}/{world}"</c> by fetching that
/// owner's published counterpart claim, verifying its chain against THIS reading world's own admission entries, and
/// binding the verified subject to the reference's owner before ever returning
/// <see cref="WorldNeighbourResolutionKind.VerifiedAttested"/> — the one non-<see cref="WorldNeighbourResolutionKind.Resolved"/>
/// outcome a derived-corner proof accepts. Never composes or validates the counterpart's own world: it proves
/// authenticated consistency of the signed statement within its signed validity window, never the attester's
/// geometry or freshness beyond that window (see <see cref="WorldCounterpartAttestationProtocol.TryVerify"/>'s own
/// remarks).
/// </summary>
/// <remarks>
/// Subject binding is decided here, not inside <see cref="WorldCounterpartAttestationProtocol.TryVerify"/>: that
/// verifier proves a valid chain-of-trust signed the claim, never that the specific reference's named owner did — a
/// second onboarded user's own validly-signed claim verifies against the same root-vouching entry every other
/// onboarded user's does, so comparing the verified subject to the reference's <see cref="WorldReference.Owner"/> is
/// load-bearing, not decorative.
/// </remarks>
public sealed class WorldApiCounterpartResolver : IWorldNeighbourResolver {
    /// <summary>The verifier-side ceiling on a fetched counterpart claim's own age — the API is a static GET
    /// artifact with no per-resolution nonce, so this window (not a freshness proof) bounds how long a stale claim
    /// stays acceptable after a narrower one is issued. Kept short and deliberately not described as freshness.</summary>
    public static readonly TimeSpan MaximumClaimAge = TimeSpan.FromHours(value: 1);

    private const string OwnerKeyPrefix = "owner/";
    /// <summary>The platform API's exposed-scope request — read from the app registration's client id
    /// (<c>e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7</c>, <c>src/Web.Functions/configuration.json</c>'s own audience);
    /// this repository carries no independent record of the App ID URI, so this is asserted from that client id per
    /// the standard <c>api://{clientId}/{scope}</c> exposed-API convention, not independently verified against a
    /// live app registration.</summary>
    private const string PlatformApiScope = "api://e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7/user_impersonation";

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(seconds: 15);
    private static readonly IAttestationCodec Codec = new CborAttestationCodec();

    private readonly IReadOnlyList<WorldAdmissionEntry>? m_admissionEntries;
    private readonly TokenCredential m_credential;
    private readonly HttpClient m_httpClient;
    private readonly Func<DateTimeOffset> m_now;

    /// <summary>Initializes the resolver.</summary>
    /// <param name="httpClient">The API client — base address the API root. Owned by the caller; not disposed here.</param>
    /// <param name="admissionEntries">The reading world's own <c>admission</c> rows — a snapshot, exactly like
    /// <see cref="WorldStorageNeighbourResolver"/>'s captured identity, rebuilt at the same document-load moments
    /// as the rest of this world's wiring, never read live mid-resolution.</param>
    /// <param name="credential">The ambient platform-API credential (<c>DefaultAzureCredential</c> — no Puck app
    /// registration exists; the platform app pre-authorizes Azure CLI/VS Code for <c>user_impersonation</c>).</param>
    /// <param name="now">The verification-boundary wall-clock read, overridable for tests.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> or <paramref name="credential"/> is <see langword="null"/>.</exception>
    public WorldApiCounterpartResolver(HttpClient httpClient, IReadOnlyList<WorldAdmissionEntry>? admissionEntries, TokenCredential credential, Func<DateTimeOffset>? now = null) {
        ArgumentNullException.ThrowIfNull(argument: httpClient);
        ArgumentNullException.ThrowIfNull(argument: credential);

        m_admissionEntries = admissionEntries;
        m_credential = credential;
        m_httpClient = httpClient;
        m_now = (now ?? (static () => DateTimeOffset.UtcNow));
    }

    /// <summary>Parses the owner-named neighbour key shape this resolver recognizes.</summary>
    /// <param name="neighbourKey">The candidate key.</param>
    /// <param name="owner">The parsed owner oid on success.</param>
    /// <param name="world">The world id segment on success.</param>
    /// <returns><see langword="true"/> when the key is owner-shaped and its owner segment parses as a GUID.</returns>
    internal static bool TryParseOwnerKey(string neighbourKey, out Guid owner, out string world) {
        owner = default;
        world = string.Empty;

        if (!neighbourKey.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: OwnerKeyPrefix
        )) {
            return false;
        }

        var remainder = neighbourKey.AsSpan(start: OwnerKeyPrefix.Length);
        var separator = remainder.IndexOf(value: '/');

        if (separator < 0) {
            return false;
        }

        if (!Guid.TryParseExact(
            format: "D",
            input: remainder[..separator],
            result: out owner
        )) {
            return false;
        }

        world = remainder[(separator + 1)..].ToString();

        return (world.Length > 0);
    }

    /// <inheritdoc/>
    public WorldNeighbourResolution Resolve(string document) {
        if (!TryParseOwnerKey(
            neighbourKey: document,
            owner: out var owner,
            world: out var world
        )) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{document}' is not an owner-named neighbour key");
        }

        using var timeout = new CancellationTokenSource(delay: OperationTimeout);

        AccessToken token;

        try {
            token = m_credential.GetToken(
                cancellationToken: timeout.Token,
                requestContext: new TokenRequestContext(scopes: [PlatformApiScope])
            );
        } catch (OperationCanceledException) {
            return WorldNeighbourResolution.Unavailable(reason: $"timed out after {OperationTimeout.TotalSeconds:0}s acquiring the platform API token");
        } catch (Exception exception) {
            return WorldNeighbourResolution.Unavailable(reason: $"platform API token acquisition failed — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        HttpResponseMessage response;

        try {
            using var request = new HttpRequestMessage(
                method: HttpMethod.Get,
                requestUri: $"api/worlds/{owner:D}/{world}/counterpart"
            ) {
                Headers = { Authorization = new AuthenticationHeaderValue(scheme: "Bearer", parameter: token.Token) },
            };

            response = m_httpClient.Send(
                cancellationToken: timeout.Token,
                request: request
            );
        } catch (OperationCanceledException) {
            return WorldNeighbourResolution.Unavailable(reason: $"timed out after {OperationTimeout.TotalSeconds:0}s reading the counterpart claim for '{document}'");
        } catch (Exception exception) {
            return WorldNeighbourResolution.Unavailable(reason: $"transport error reading the counterpart claim for '{document}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        using (response) {
            if (response.StatusCode == HttpStatusCode.NotFound) {
                return WorldNeighbourResolution.Unavailable(reason: $"no counterpart claim published for '{document}'");
            }

            if (!response.IsSuccessStatusCode) {
                return WorldNeighbourResolution.Unavailable(reason: $"counterpart claim fetch for '{document}' answered {((int)response.StatusCode)} {response.StatusCode}");
            }

            byte[] wrapper;

            try {
                wrapper = response.Content.ReadAsByteArrayAsync(cancellationToken: timeout.Token).GetAwaiter().GetResult();
            } catch (Exception exception) {
                return WorldNeighbourResolution.Unavailable(reason: $"could not read the counterpart claim response body for '{document}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }

            if (!AttestationChainEnvelope.TryDecode(
                chain: out var chainBytes,
                claim: out var claimBytes,
                reason: out var envelopeReason,
                wire: wrapper
            ) ||
                (claimBytes is null) ||
                (chainBytes is null)
            ) {
                return WorldNeighbourResolution.Unavailable(reason: $"'{document}' counterpart claim transport envelope does not decode — {envelopeReason}");
            }

            SignedAttestation claim;
            var chain = new SignedAttestation[chainBytes.Length];

            try {
                claim = AttestationProfile.Base.DecodeAttestation(
                    codec: Codec,
                    wire: claimBytes
                );

                for (var index = 0; (index < chainBytes.Length); index++) {
                    chain[index] = AttestationProfile.Base.DecodeAttestation(
                        codec: Codec,
                        wire: chainBytes[index]
                    );
                }
            } catch (FormatException exception) {
                return WorldNeighbourResolution.Unavailable(reason: $"'{document}' counterpart claim/chain does not decode — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }

            if (!WorldCounterpartAttestationProtocol.TryVerify(
                attestation: out var attestation,
                chain: chain,
                claim: claim,
                codec: Codec,
                entries: m_admissionEntries,
                maximumAge: MaximumClaimAge,
                now: m_now(),
                reason: out var verifyReason,
                subject: out var subject
            ) ||
                (attestation is null)
            ) {
                return WorldNeighbourResolution.Unavailable(reason: $"'{document}' counterpart claim did not verify — {verifyReason}");
            }

            if (
                !Guid.TryParseExact(
                format: "D",
                input: subject,
                result: out var subjectOwner
            ) ||
                (subjectOwner != owner)
            ) {
                return WorldNeighbourResolution.Unavailable(reason: $"'{document}' counterpart claim's verified subject '{subject}' does not name the reference's owner '{owner:D}'");
            }

            return WorldNeighbourResolution.VerifiedAttested(
                attestation: attestation,
                subject: subject
            );
        }
    }
}
