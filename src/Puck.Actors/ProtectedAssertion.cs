using Microsoft.AspNetCore.DataProtection;
using Puck.Actors.Grains;

namespace Puck.Actors;

/// <summary>
/// Silo-side counterpart of the edge's ProtectedToken: rehydrates the user assertion from
/// the in-band escrow. The purpose chain must match the edge exactly ("users.tokens" +
/// [oid, discriminator]), which also cryptographically binds the escrow to the grain key —
/// a blob protected for a different oid fails to unprotect.
/// </summary>
public static class ProtectedAssertion
{
    private const string Purpose = "users.tokens";

    public static string Unprotect(
        this TokenEscrow tokenEscrow,
        IDataProtectionProvider dataProtectionProvider,
        string userObjectId
    ) {
        return dataProtectionProvider
            .CreateProtector(
                purpose: Purpose,
                subPurposes: [
                    userObjectId,
                    tokenEscrow.TokenDiscriminator,
                ]
            )
            .Unprotect(protectedData: tokenEscrow.ProtectedAssertion);
    }
}

