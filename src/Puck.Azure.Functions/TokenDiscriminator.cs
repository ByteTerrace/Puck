using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;

namespace Puck.Azure.Functions;

public readonly struct TokenDiscriminator
{
    private const char UnitSeparator = '\u001F';

    public static TokenDiscriminator FromJwt(string? jwt) {
        string? audiences;
        string? issuer;
        string? objectId;
        string? scopes;
        TimeSpan timeToLive;

        try {
            ArgumentNullException.ThrowIfNull(argument: jwt);

            var jwtValue = new JwtSecurityTokenHandler().ReadJwtToken(token: jwt);

            audiences = string.Join(
                separator: UnitSeparator,
                values: jwtValue
                    .Audiences
                    .Order()
            );
            issuer = jwtValue.Issuer;
            objectId = jwtValue
                .Claims
                .SingleOrDefault(predicate: claim =>
                    ("oid" == claim.Type) ||
                    ("http://schemas.microsoft.com/identity/claims/objectidentifier" == claim.Type)
                )
                ?.Value;
            scopes = string.Join(
                separator: UnitSeparator,
                values: jwtValue
                    .Claims
                    .Where(predicate: claim =>
                        ("scp" == claim.Type) ||
                        ("http://schemas.microsoft.com/identity/claims/scope" == claim.Type)
                    )
                    .Select(selector: claim => claim.Value)
                    .SelectMany(selector: scope => scope.Split(
                        options: StringSplitOptions.RemoveEmptyEntries,
                        separator: ' '
                    ))
                    .Order()
            );
            timeToLive = (jwtValue.ValidTo - DateTime.UtcNow).Add(ts: TimeSpan.FromMinutes(minutes: -3));
        }
        catch (Exception) {
            throw new InvalidOperationException(message: "Unhandled exception while parsing JWT.");
        }

        ArgumentException.ThrowIfNullOrEmpty(argument: audiences);
        ArgumentException.ThrowIfNullOrEmpty(argument: issuer);
        ArgumentException.ThrowIfNullOrEmpty(argument: objectId);
        ArgumentException.ThrowIfNullOrEmpty(argument: scopes);

        return TimeSpan.Zero >= timeToLive
            ? throw new InvalidOperationException(message: "Access token is near expiration or has already expired.")
            : !Guid.TryParseExact(
                format: "D",
                input: objectId,
                result: out var objectIdGuid
            )
            ? throw new InvalidOperationException(message: "Access token does not have a valid object id.")
            : new() {
                ObjectId = objectIdGuid.ToString(format: "D").ToLowerInvariant(),
                TimeToLive = timeToLive,
                Value = SHA256.HashData(source: Encoding.UTF8.GetBytes(s: string.Join(
                    separator: UnitSeparator,
                    values: [
                        audiences,
                        issuer,
                        objectId,
                        scopes
                    ]
                ))),
            };
    }

    public string ObjectId { get; init; } = "";
    public TimeSpan TimeToLive { get; init; } = TimeSpan.Zero;
    public byte[] Value { get; init; } = [];

    public TokenDiscriminator() { }

    public string ToBase64String() => Convert.ToBase64String(inArray: Value);
}

