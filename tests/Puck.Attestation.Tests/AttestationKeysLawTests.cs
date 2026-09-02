using System.Security.Cryptography;

using Xunit;

namespace Puck.Attestation.Tests;

public sealed class AttestationKeysLawTests {
    private static byte[] ExportFreshKey(ECCurve curve) {
        using var key = ECDsa.Create(curve: curve);

        return key.ExportPkcs8PrivateKey();
    }

    [Fact]
    public void ImportPkcs8PrivateKey_P256Key_RoundTripsThePublicKey() {
        using var original = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var pkcs8 = original.ExportPkcs8PrivateKey();

        using var imported = AttestationKeys.ImportPkcs8PrivateKey(algorithm: AttestationAlgorithms.EcdsaP256Sha256, pkcs8: pkcs8);

        Assert.Equal(expected: original.ExportSubjectPublicKeyInfo(), actual: imported.ExportSubjectPublicKeyInfo());
        Assert.True(condition: AttestationCurves.IsNistP256(curve: imported.ExportParameters(includePrivateParameters: false).Curve));
    }
    [Fact]
    public void ImportPkcs8PrivateKey_TrailingByte_IsRefused() {
        var pkcs8 = ExportFreshKey(curve: ECCurve.NamedCurves.nistP256);
        var padded = new byte[(pkcs8.Length + 1)];

        pkcs8.CopyTo(array: padded, index: 0);

        var exception = Assert.Throws<ArgumentException>(testCode: () => AttestationKeys.ImportPkcs8PrivateKey(algorithm: AttestationAlgorithms.EcdsaP256Sha256, pkcs8: padded));

        Assert.Equal(expected: "pkcs8", actual: exception.ParamName);
        Assert.Contains(expectedSubstring: "trailing", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void ImportPkcs8PrivateKey_P384Key_IsRefused() {
        var pkcs8 = ExportFreshKey(curve: ECCurve.NamedCurves.nistP384);

        var exception = Assert.Throws<ArgumentException>(testCode: () => AttestationKeys.ImportPkcs8PrivateKey(algorithm: AttestationAlgorithms.EcdsaP256Sha256, pkcs8: pkcs8));

        Assert.Equal(expected: "pkcs8", actual: exception.ParamName);
        Assert.Contains(expectedSubstring: "curve", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void ImportPkcs8PrivateKey_Garbage_ThrowsCryptographicException() {
        var garbage = new byte[] { 0x30, 0x03, 0x02, 0x01, 0x00, 0xFF };

        _ = Assert.Throws<CryptographicException>(testCode: () => AttestationKeys.ImportPkcs8PrivateKey(algorithm: AttestationAlgorithms.EcdsaP256Sha256, pkcs8: garbage));
    }
    [Fact]
    public void ImportPkcs8PrivateKey_SealingOrUnknownAlgorithm_IsRefusedBeforeTheBytesAreRead() {
        var garbage = new byte[] { 0xFF };

        var sealing = Assert.Throws<ArgumentException>(testCode: () => AttestationKeys.ImportPkcs8PrivateKey(algorithm: AttestationAlgorithms.EcdhP256HkdfSha256Aes256Gcm, pkcs8: garbage));
        var unknown = Assert.Throws<ArgumentException>(testCode: () => AttestationKeys.ImportPkcs8PrivateKey(algorithm: "not-an-algorithm", pkcs8: garbage));

        Assert.Equal(expected: "algorithm", actual: sealing.ParamName);
        Assert.Equal(expected: "algorithm", actual: unknown.ParamName);
    }
}
