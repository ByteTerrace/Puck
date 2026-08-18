using System.Security.Cryptography;
using Puck.Assets.Documents;
using Puck.Attestation;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Covers <see cref="ReleaseCanonicalizer"/>'s round-trip identity (canonical bytes → hash → parse →
/// re-canonicalize → identical bytes and hash), its structural refusals, and <see cref="ReleaseTrustAnchor"/>'s
/// <see cref="TrustListEntry"/> shape.</summary>
public sealed class ReleaseManifestTests {
    private static ReleaseManifest ValidDocument() => new(
        App: "puck.world",
        Channel: "stable",
        MinimumSupported: "1.0.0",
        Notes: "  first release  ",
        Payloads: [
            new ReleasePayload(Rid: "win-x64", Files: [
                new ReleasePayloadFile(Path: "Puck.World.exe", Hash: $"sha256/{new string(c: '0', count: 64)}", Size: 1024),
                new ReleasePayloadFile(Path: "Puck.World.dll", Hash: $"sha256/{new string(c: '1', count: 64)}", Size: 2048),
            ]),
        ],
        Revoked: ["0.9.0", "0.9.0"],
        Rollout: new ReleaseRollout(Percent: 100),
        Schema: ReleaseManifest.CurrentSchema,
        Signature: null,
        StateGeneration: 1,
        Version: "1.0.1"
    );

    [Fact]
    public void Canonicalize_RoundTrips_ByteIdentically() {
        var first = ReleaseCanonicalizer.Canonicalize(document: ValidDocument());
        var reparsed = System.Text.Json.JsonSerializer.Deserialize<ReleaseManifest>(utf8Json: first.Bytes, options: DocumentJsonOptions.Shared)!;
        var second = ReleaseCanonicalizer.Canonicalize(document: reparsed);

        Assert.Equal(expected: first.Hash, actual: second.Hash);
        Assert.Equal(expected: first.Bytes, actual: second.Bytes);
    }
    [Fact]
    public void Normalize_IsIdempotent() {
        var once = ReleaseCanonicalizer.Normalize(document: ValidDocument());
        var twice = ReleaseCanonicalizer.Normalize(document: once);

        Assert.Equal(expected: ReleaseCanonicalizer.Canonicalize(document: once).Bytes, actual: ReleaseCanonicalizer.Canonicalize(document: twice).Bytes);
    }
    [Fact]
    public void Normalize_SortsPayloadsAndDedupesRevoked() {
        var unordered = ValidDocument() with {
            Payloads = [
                new ReleasePayload(Rid: "win-x64", Files: [
                    new ReleasePayloadFile(Path: "b.dll", Hash: $"sha256/{new string(c: '2', count: 64)}", Size: 1),
                    new ReleasePayloadFile(Path: "a.dll", Hash: $"sha256/{new string(c: '3', count: 64)}", Size: 1),
                ]),
                new ReleasePayload(Rid: "linux-x64", Files: [
                    new ReleasePayloadFile(Path: "a", Hash: $"sha256/{new string(c: '4', count: 64)}", Size: 1),
                ]),
            ],
        };
        var normalized = ReleaseCanonicalizer.Normalize(document: unordered);

        Assert.Equal(expected: "linux-x64", actual: normalized.Payloads[0].Rid);
        Assert.Equal(expected: "win-x64", actual: normalized.Payloads[1].Rid);
        Assert.Equal(expected: "a.dll", actual: normalized.Payloads[1].Files[0].Path);
        Assert.Equal(expected: "b.dll", actual: normalized.Payloads[1].Files[1].Path);
        Assert.Single(collection: normalized.Revoked!);
    }
    [Fact]
    public void Validate_RefusesAbsentSchema() {
        var errors = ReleaseCanonicalizer.Validate(document: (ValidDocument() with { Schema = null }));

        Assert.Single(collection: errors);
        Assert.Equal(expected: "schema", actual: errors[0].Path);
    }
    [Fact]
    public void Validate_RefusesEmptyPayloads() {
        var errors = ReleaseCanonicalizer.Validate(document: (ValidDocument() with { Payloads = [] }));

        Assert.Contains(collection: errors, filter: error => (error.Path == "payloads"));
    }
    [Fact]
    public void Validate_RefusesMalformedContentHash() {
        var bad = ValidDocument() with {
            Payloads = [new ReleasePayload(Rid: "win-x64", Files: [new ReleasePayloadFile(Hash: "not-a-hash", Path: "a.dll", Size: 1)])],
        };
        var errors = ReleaseCanonicalizer.Validate(document: bad);

        Assert.Contains(collection: errors, filter: error => error.Path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".hash"));
    }
    [Fact]
    public void Validate_RefusesRolloutPercentOutOfRange() {
        var bad = ValidDocument() with { Rollout = new ReleaseRollout(Percent: 101) };
        var errors = ReleaseCanonicalizer.Validate(document: bad);

        Assert.Contains(collection: errors, filter: error => (error.Path == "rollout.percent"));
    }
    [Fact]
    public void Validate_RefusesUnknownExtensionMember() {
        var document = ValidDocument();

        document.Extensions = new Dictionary<string, System.Text.Json.JsonElement> {
            ["app"] = System.Text.Json.JsonDocument.Parse(json: "\"shadowed\"").RootElement,
        };

        var errors = ReleaseCanonicalizer.Validate(document: document);

        Assert.Contains(collection: errors, filter: error => (error.Path == "extensions.app"));
    }
    [Fact]
    public void Canonicalize_Throws_OnStructuralViolation() =>
        Assert.Throws<DocumentValidationException>(testCode: () => ReleaseCanonicalizer.Canonicalize(document: (ValidDocument() with { App = "" })));
    [Fact]
    public void ReleaseTrustAnchor_BuildsValidatingTrustListEntry() {
        using var rootKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

        var spki = rootKey.ExportSubjectPublicKeyInfo();
        var domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: spki);
        var anchor = new ReleaseTrustAnchor(
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            Domain: domain,
            PublicKeySubjectPublicKeyInfoBase64: Convert.ToBase64String(inArray: spki)
        );
        var entry = anchor.ToTrustListEntry(maximumAge: null, reach: new HashSet<string>(comparer: StringComparer.Ordinal) { "release" });

        entry.Validate();

        Assert.Equal(expected: AttestationTrustMode.Vouches, actual: entry.Mode);
        Assert.True(condition: entry.PinnedId.IsRoot);
    }
}
