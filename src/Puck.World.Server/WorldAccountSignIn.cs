using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Attestation;
using Puck.Networking;

namespace Puck.World.Server;

/// <summary>
/// The engine's own sign-in provider, <c>puck.accounts</c>: a host lists the accounts it vouches for, each a subject
/// and its public key, and a player signs in by signing the issuance exchange's bound challenge with that account's
/// private key. It needs no identity service, so a player-hosted world can vouch for the friends it lists and a
/// rehearsal can stand in for a deployment's provider; a deployment selects another provider by name.
/// </summary>
/// <remarks>Issuer settings: <c>accounts</c>, an object of one to <see cref="MaximumAccounts"/> subjects, each mapped
/// to the base64 DER SubjectPublicKeyInfo of its P-256 key. Client settings: <c>subject</c>, the account to sign in as,
/// and <c>keyFile</c>, the path of that account's PKCS#8 private key.</remarks>
public static class WorldAccountSignIn {
    /// <summary>The most accounts one issuer lists.</summary>
    public const int MaximumAccounts = 4096;

    /// <summary>Gets the provider registration.</summary>
    public static WorldSignInProvider Provider { get; } = new(
        "puck.accounts",
        static (settings, _) => new Verifier(accounts: ReadAccounts(settings: settings)),
        static (settings, _) => ReadClient(settings: settings)
    );

    private static FrozenDictionary<string, byte[]> ReadAccounts(JsonElement settings) {
        const string Usage = "puck.accounts issuer settings name exactly one accounts object mapping 1..4096 subjects to base64 P-256 SubjectPublicKeyInfo keys.";
        var accounts = WorldExtensionSettings.OnlySetting(name: "accounts", settings: settings);

        if (accounts.ValueKind != JsonValueKind.Object) { throw new ArgumentException(message: Usage); }
        var keys = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal);

        foreach (var account in accounts.EnumerateObject()) {
            if (
                !WorldSignIn.IsWellFormed(subject: account.Name) ||
                (account.Value.ValueKind != JsonValueKind.String) ||
                (keys.Count >= MaximumAccounts)
            ) { throw new ArgumentException(message: Usage); }
            byte[] spki;

            try {
                spki = Convert.FromBase64String(s: account.Value.GetString()!);
            } catch (FormatException exception) {
                throw new ArgumentException(message: $"{Usage} Account '{account.Name}': {exception.Message}", innerException: exception);
            }
            if (!AttestationKeys.IsPublicKey(algorithm: AttestationAlgorithms.EcdsaP256Sha256, publicKeySubjectPublicKeyInfo: spki)) {
                throw new ArgumentException(message: $"{Usage} Account '{account.Name}' names no P-256 key.");
            }
            keys[account.Name] = spki;
        }
        if (keys.Count == 0) { throw new ArgumentException(message: Usage); }
        return keys.ToFrozenDictionary(comparer: StringComparer.Ordinal);
    }
    private static Client ReadClient(JsonElement settings) {
        const string Usage = "puck.accounts client settings name exactly subject and keyFile.";
        string? subject = null;
        string? keyFile = null;

        if (settings.ValueKind != JsonValueKind.Object) { throw new ArgumentException(message: Usage); }
        foreach (var property in settings.EnumerateObject()) {
            if (property.Value.ValueKind != JsonValueKind.String) { throw new ArgumentException(message: Usage); }
            switch (property.Name) {
                case "subject" when (subject is null): subject = property.Value.GetString(); break;
                case "keyFile" when (keyFile is null): keyFile = property.Value.GetString(); break;
                default: throw new ArgumentException(message: Usage);
            }
        }
        if (!WorldSignIn.IsWellFormed(subject: subject) || string.IsNullOrWhiteSpace(value: keyFile)) { throw new ArgumentException(message: Usage); }
        try {
            return new Client(
                key: AttestationKeys.ImportPkcs8PrivateKey(algorithm: AttestationAlgorithms.EcdsaP256Sha256, pkcs8: File.ReadAllBytes(path: keyFile)),
                subject: subject!
            );
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException) {
            throw new ArgumentException(message: $"puck.accounts keyFile '{keyFile}' could not be read as a PKCS#8 P-256 private key: {exception.Message}", innerException: exception);
        }
    }

    private static byte[] Encode(string subject, ReadOnlySpan<byte> signature) {
        var writer = new WireWriter();

        writer.WriteString(value: subject);
        writer.WriteBlock(value: signature);
        return writer.ToArray();
    }

    private sealed class Verifier(FrozenDictionary<string, byte[]> accounts) : IWorldSignInVerifier {
        public ValueTask<WorldSignIn?> VerifyAsync(ReadOnlyMemory<byte> credential, ReadOnlyMemory<byte> challenge, CancellationToken cancellationToken) {
            var reader = new WireReader(bytes: credential.Span);
            var subject = reader.ReadRequiredString(field: "account subject", maxBytes: 128);
            var signature = reader.ReadBlock(field: "account signature", maxBytes: AttestationResourceLimits.SignatureBytes);

            return new((
                reader.TryFinish(failure: out _) &&
                accounts.TryGetValue(key: subject, value: out var spki) &&
                AttestationKeys.VerifySignature(algorithm: AttestationAlgorithms.EcdsaP256Sha256, data: challenge.Span, publicKeySubjectPublicKeyInfo: spki, signature: signature)
            )
                ? new WorldSignIn(Subject: subject)
                : null
            );
        }
    }
    private sealed class Client(ECDsa key, string subject) : IWorldSignInCredential {
        public byte[] Present(ReadOnlySpan<byte> challenge, CancellationToken cancellationToken) => Encode(
            signature: AttestationKeys.Sign(algorithm: AttestationAlgorithms.EcdsaP256Sha256, data: challenge, key: key),
            subject: subject
        );
    }
}
