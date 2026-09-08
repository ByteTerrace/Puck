using Microsoft.AspNetCore.DataProtection;

namespace Puck.Azure.Functions;

public readonly struct ProtectedToken
{
    private const string Purpose = "users.tokens";

    public static ProtectedToken FromJwt(
        string? accessToken,
        IDataProtectionProvider dataProtectionProvider
    ) {
        var tokenDiscriminator = TokenDiscriminator.FromJwt(jwt: accessToken);

        return new(
            tokenDiscriminator: tokenDiscriminator,
            value: dataProtectionProvider
                .CreateProtector(
                    purpose: Purpose,
                    subPurposes: [
                        tokenDiscriminator.ObjectId,
                        tokenDiscriminator.ToBase64String(),
                    ]
                )
                .Protect(plaintext: accessToken!)
        );
    }

    public TokenDiscriminator TokenDiscriminator { get; }
    public string Value { get; }

    private ProtectedToken(
        TokenDiscriminator tokenDiscriminator,
        string value
    ) {
        TokenDiscriminator = tokenDiscriminator;
        Value = value;
    }

    public string Unprotect(IDataProtectionProvider dataProtectionProvider) {
        return dataProtectionProvider
            .CreateProtector(
                purpose: Purpose,
                subPurposes: [
                    TokenDiscriminator.ObjectId,
                    TokenDiscriminator.ToBase64String(),
                ]
            )
            .Unprotect(protectedData: Value);
    }
}

