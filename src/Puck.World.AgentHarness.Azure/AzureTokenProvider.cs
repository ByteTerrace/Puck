using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Core;

namespace Puck.World.Agents.Harness.Azure;

/// <summary>Presents an Azure <see cref="TokenCredential"/> as the <see cref="AuthenticationTokenProvider"/> the OpenAI
/// client's bearer policy asks for. The two types meet only through strings and times, so a host that carries a
/// different <c>System.ClientModel</c> than this extension never hands the policy an object built against another
/// copy of the contract.</summary>
/// <param name="credential">The Azure credential that signs in.</param>
/// <param name="scope">The token scope requested when the policy supplies none.</param>
internal sealed class AzureTokenProvider(TokenCredential credential, string scope) : AuthenticationTokenProvider {
    private string[] Scopes(GetTokenOptions options) => ((options.Properties.TryGetValue(
        key: GetTokenOptions.ScopesPropertyName,
        value: out var value
    ) && (value is string[] { Length: > 0 } scopes))
        ? scopes
        : [scope]
    );
    private static AuthenticationToken Convert(AccessToken token) => new(
        token.Token,
        token.TokenType,
        token.ExpiresOn,
        token.RefreshOn
    );

    /// <inheritdoc/>
    public override GetTokenOptions? CreateTokenOptions(IReadOnlyDictionary<string, object> properties) => new(properties: properties);
    /// <inheritdoc/>
    public override AuthenticationToken GetToken(GetTokenOptions options, CancellationToken cancellationToken) => Convert(token: credential.GetToken(
        cancellationToken: cancellationToken,
        requestContext: new TokenRequestContext(scopes: Scopes(options: options))
    ));
    /// <inheritdoc/>
    public override async ValueTask<AuthenticationToken> GetTokenAsync(GetTokenOptions options, CancellationToken cancellationToken) => Convert(token: await credential.GetTokenAsync(
        cancellationToken: cancellationToken,
        requestContext: new TokenRequestContext(scopes: Scopes(options: options))
    ).ConfigureAwait(continueOnCapturedContext: false));
}
