using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class EntraWorldAuthenticatorTests {
    private const string Audience = "22222222-2222-2222-2222-222222222222";
    private const string Group = "33333333-3333-3333-3333-333333333333";
    private const string Scope = "user_impersonation";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string User = "44444444-4444-4444-4444-444444444444";

    private static JsonElement Settings => JsonSerializer.SerializeToElement(new {
        tenantId = Tenant,
        audience = Audience,
        groupId = Group,
        scope = Scope,
        remoteKeyHash = new string(c: 'a', count: 64),
    });

    [InlineData("valid", true)]
    [InlineData("group", false)]
    [InlineData("audience", false)]
    [InlineData("issuer", false)]
    [InlineData("tenant", false)]
    [InlineData("scope", false)]
    [InlineData("app-only", false)]
    [InlineData("expired", false)]
    [InlineData("nonce", false)]
    [InlineData("signature", false)]
    [InlineData("overage", false)]
    [Theory]
    public void OnlyDelegatedApiUsersAreAdmitted(string fault, bool accepted) {
        using var signing = RSA.Create(keySizeInBits: 2048);
        using var untrusted = RSA.Create(keySizeInBits: 2048);
        var key = new RsaSecurityKey(rsa: signing) { KeyId = "known" };
        var metadata = new OpenIdConnectConfiguration();

        metadata.SigningKeys.Add(item: key);
        var server = new EntraWorldAuthenticator(Settings, false,
            configuration: new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration: metadata));
        var claims = new Dictionary<string, object> {
            ["tid"] = ((fault == "tenant") ? Group : Tenant),
            ["oid"] = User,
            ["groups"] = new[] { ((fault == "group") ? User : Group) },
            ["scp"] = ((fault == "scope") ? "another_scope" : Scope),
        };

        if (fault == "app-only") { claims.Remove(key: "scp"); claims["roles"] = new[] { Scope }; }
        if (fault == "overage") { claims.Remove(key: "groups"); claims["hasgroups"] = true; }
        var token = new JsonWebTokenHandler().CreateToken(tokenDescriptor: new SecurityTokenDescriptor {
            Issuer = $"https://login.microsoftonline.com/{((fault == "issuer") ? Group : Tenant)}/v2.0",
            Audience = ((fault == "audience") ? Group : Audience),
            Claims = claims,
            IssuedAt = DateTime.UtcNow.AddMinutes(value: -10),
            NotBefore = DateTime.UtcNow.AddMinutes(value: -10),
            Expires = DateTime.UtcNow.AddMinutes(value: ((fault == "expired") ? -1 : 5)),
            SigningCredentials = new SigningCredentials(((fault == "signature") ? new RsaSecurityKey(rsa: untrusted) { KeyId = "known" } : key), SecurityAlgorithms.RsaSha256),
        });
        var client = new EntraWorldAuthenticator(Settings, true, credential: new FixedCredential(token: token));
        var challenge = server.NewChallenge();
        var proof = client.Prove(challenge);

        if (fault == "nonce") { challenge = server.NewChallenge(); }
        Assert.Equal(accepted, server.TryVerify(challenge, proof, out var subject));
        Assert.Equal((accepted ? client.UserIdentity() : null), subject);
    }
    [Fact]
    public void HostFingerprintIsExactAndAmbientUserCannotChangeMidSession() {
        var credential = new FixedCredential(token: UnvalidatedIdentity(user: User));
        var client = new EntraWorldAuthenticator(Settings, true, credential: credential);

        Assert.True(client.AcceptsRemoteIdentity(new string(c: 'a', count: 64)));
        Assert.False(client.AcceptsRemoteIdentity(new string(c: 'b', count: 64)));
        Assert.StartsWith((User + "/"), client.UserIdentity());
        Assert.Equal(client.UserIdentity(), client.UserIdentity());
        Assert.NotEqual(client.UserIdentity(), new EntraWorldAuthenticator(Settings, true, credential: credential).UserIdentity());
        credential.Token = UnvalidatedIdentity(user: Group);
        Assert.Throws<InvalidOperationException>(testCode: () => client.Prove(new byte[32]));
    }

    private static string UnvalidatedIdentity(string user) => new JsonWebTokenHandler().CreateToken(tokenDescriptor: new SecurityTokenDescriptor {
        Claims = new Dictionary<string, object> { ["oid"] = user },
    });

    private sealed class FixedCredential(string token) : TokenCredential {
        public string Token { get; set; } = token;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) {
            Assert.Equal($"api://{Audience}/.default", Assert.Single(collection: requestContext.Scopes));
            return new(accessToken: Token, expiresOn: DateTimeOffset.UtcNow.AddMinutes(minutes: 1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(result: GetToken(cancellationToken: cancellationToken, requestContext: requestContext));
    }
}
