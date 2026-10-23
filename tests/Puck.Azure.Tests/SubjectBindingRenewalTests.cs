using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Puck.Attestation;
using Puck.Azure.Functions.Services;
using Xunit;

namespace Puck.Azure.Tests;

public sealed class SubjectBindingRenewalTests {
    private static readonly DateTimeOffset Issued = DateTimeOffset.FromUnixTimeSeconds(seconds: 1_800_000_000);

    [Theory]
    [InlineData("subject")]
    [InlineData("key")]
    [InlineData("algorithm")]
    public async Task RenewalRefusesABindingCopiedFromAnotherSubjectOrKey(string mismatch) {
        using var fixture = new BindingFixture();
        var original = await fixture.Service.MintSubjectKeyBindingAsync(fixture.Request, Issued, TestContext.Current.CancellationToken);
        using var otherKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var requested = new MintSubjectKeyBindingRequest {
            SubjectId = (mismatch == "subject" ? "another-account" : fixture.Request.SubjectId),
            PublicKey = (mismatch == "key" ? otherKey.ExportSubjectPublicKeyInfo() : fixture.Request.PublicKey),
            KeyType = (mismatch == "algorithm" ? "ecdh" : fixture.Request.KeyType),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReattestAsync(
            original.Binding, requested, Issued.AddDays(days: 31), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ecdsa")]
    [InlineData("ecdh")]
    public async Task OwnerCanRenewTheirExactKeyAfterTheOriginalBindingExpires(string keyType) {
        using var fixture = new BindingFixture(keyType);
        var original = await fixture.Service.MintSubjectKeyBindingAsync(fixture.Request, Issued, TestContext.Current.CancellationToken);
        var now = Issued.AddDays(days: 31);
        var renewed = await fixture.Service.ReattestAsync(original.Binding, fixture.Request, now, TestContext.Current.CancellationToken);
        var codec = new CborAttestationCodec();
        var before = codec.DecodeAttestation(original.Binding);
        var after = codec.DecodeAttestation(renewed.Binding);

        Assert.True(original.NotAfter < now);
        Assert.Equal(original.KeyId, renewed.KeyId);
        Assert.Equal(now, renewed.NotBefore);
        Assert.Equal(now.AddDays(days: 30), renewed.NotAfter);
        Assert.Equal(before.PayloadBytes.ToArray(), after.PayloadBytes.ToArray());
        Assert.True(fixture.Issuer.VerifyData(after.SignedPortion.Span, after.Signature.Span,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private sealed class BindingFixture : IDisposable {
        public ECDsa Issuer { get; } = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        public DefaultBindingService Service { get; }
        public MintSubjectKeyBindingRequest Request { get; }

        public BindingFixture(string keyType = "ecdsa") {
            using var root = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
            using var subject = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
            const string Password = "generated-test-key";
            var options = new IssuingKeyOptions {
                RootPublicKeyPem = root.ExportSubjectPublicKeyInfoPem(),
                IssuingPrivateKeyPem = Issuer.ExportEncryptedPkcs8PrivateKeyPem(Password,
                    new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, iterationCount: 1000)),
                IssuingPrivateKeyPassword = Password,
            };

            Service = new(new FixedOptions(options));
            Request = new() { SubjectId = "key-owner", PublicKey = subject.ExportSubjectPublicKeyInfo(), KeyType = keyType };
        }

        public void Dispose() => Issuer.Dispose();
    }

    private sealed class FixedOptions(IssuingKeyOptions value) : IOptionsMonitor<IssuingKeyOptions> {
        public IssuingKeyOptions CurrentValue => value;
        public IssuingKeyOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<IssuingKeyOptions, string?> listener) => null;
    }
}
