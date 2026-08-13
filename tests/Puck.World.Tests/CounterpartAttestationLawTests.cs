using System.Numerics;
using System.Security.Cryptography;

using Xunit;

using Puck.Attestation;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// Proves a border can be settled without either side reading the other's world: a signed counterpart attestation
/// validates the same reciprocity, extents, frame, and overlap the resolved-document path proves, and a tampered
/// extent refuses by the same name. The positive control runs first, so a refusal below means the tamper was caught
/// rather than that the pair never validated.
/// </summary>
public sealed class CounterpartAttestationLawTests {
    [Fact]
    public void SignedAttestation_SettlesReciprocity_AndRefusesATamperedExtent() {
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: spki);
        var trust = new WorldAdmissionEntry(
            Domain: domain,
            Subject: "counterpart",
            Mode: WorldAdmissionTrustMode.SignsDirectly,
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            PublicKey: Convert.ToBase64String(inArray: spki),
            Grants: []);

        var west = Quilt(name: "west", counterpart: "east", document: "east.world.json", center: new Vector3(x: 10f, y: 0f, z: 0f), yaw: 90f, admission: [trust]);
        var east = Quilt(name: "east", counterpart: "west", document: "west.world.json", center: new Vector3(x: -10f, y: 0f, z: 0f), yaw: -90f, admission: [trust]);

        Assert.True(WorldCounterpartAttestation.TryCompose(definition: east, document: "east.world.json", attestation: out var attested, reason: out var composeReason), composeReason);

        var codec = new CborAttestationCodec();

        Assert.True(TryVerifySigned(codec: codec, key: key, domain: domain, subject: "counterpart", entries: [trust], attestation: attested!, verified: out var verified, reason: out var verifyReason), verifyReason);
        Assert.True(WorldDefinitionValidator.TryValidate(definition: west, reason: out var accepted, neighbours: new StubResolver(attestation: verified!)), accepted);

        // A counterpart that widens its half of the seam after signing does not match what this side authored.
        var tampered = (verified! with {
            Edges = [(verified.Edges[0] with { Boundary = (verified.Edges[0].Boundary with { Width = 9f }) })],
        });

        Assert.False(WorldDefinitionValidator.TryValidate(definition: west, reason: out var refused, neighbours: new StubResolver(attestation: tampered)));
        Assert.Contains(expectedSubstring: "but neighbour 'east.world.json'/'east' is", actualString: refused, comparisonType: StringComparison.Ordinal);

        // A counterpart that stops pointing back refuses by its own name, not by the extent one.
        var nonReciprocal = (verified with { Edges = [(verified.Edges[0] with { Counterpart = "elsewhere" })] });

        Assert.False(WorldDefinitionValidator.TryValidate(definition: west, reason: out var broken, neighbours: new StubResolver(attestation: nonReciprocal)));
        Assert.Contains(expectedSubstring: "is not reciprocal", actualString: broken, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsignedOrForeignClaim_NeverBecomesAnAttestation() {
        using var trusted = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var spki = trusted.ExportSubjectPublicKeyInfo();
        var domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: spki);
        var trust = new WorldAdmissionEntry(
            Domain: domain,
            Subject: "counterpart",
            Mode: WorldAdmissionTrustMode.SignsDirectly,
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            PublicKey: Convert.ToBase64String(inArray: spki),
            Grants: []);
        var east = Quilt(name: "east", counterpart: "west", document: "west.world.json", center: new Vector3(x: -10f, y: 0f, z: 0f), yaw: -90f, admission: [trust]);

        Assert.True(WorldCounterpartAttestation.TryCompose(definition: east, document: "east.world.json", attestation: out var attested, reason: out _));

        var codec = new CborAttestationCodec();

        Assert.False(TryVerifySigned(codec: codec, key: stranger, domain: domain, subject: "counterpart", entries: [trust], attestation: attested!, verified: out _, reason: out var strangerReason));
        Assert.NotEmpty(strangerReason);

        // The control: the trusted key over the same payload does verify, so the refusal is about the signer.
        Assert.True(TryVerifySigned(codec: codec, key: trusted, domain: domain, subject: "counterpart", entries: [trust], attestation: attested!, verified: out _, reason: out var trustedReason), trustedReason);

        // A world with no key-bearing admission row believes no border claim at all.
        Assert.False(TryVerifySigned(codec: codec, key: trusted, domain: domain, subject: "counterpart", entries: [], attestation: attested!, verified: out _, reason: out var noTrust));
        Assert.Contains(expectedSubstring: "no key-bearing admission entries", actualString: noTrust, comparisonType: StringComparison.Ordinal);
    }

    private static bool TryVerifySigned(
        IAttestationCodec codec,
        ECDsa key,
        string domain,
        string subject,
        IReadOnlyList<WorldAdmissionEntry> entries,
        WorldCounterpartAttestation attestation,
        out WorldCounterpartAttestation? verified,
        out string reason
    ) {
        var now = DateTimeOffset.UtcNow;
        var seconds = now.ToUnixTimeSeconds();
        var claim = AttestationSigner.SignClaim(
            codec: codec,
            domain: domain,
            subject: subject,
            signerKey: key,
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            purpose: WorldCounterpartAttestationProtocol.Purpose,
            notBefore: (seconds - 60L),
            notAfter: (seconds + 60L),
            audience: WorldCounterpartAttestationProtocol.Audience,
            sequence: null,
            claimBytes: WorldCounterpartAttestationProtocol.Payload(attestation: attestation));

        return WorldCounterpartAttestationProtocol.TryVerify(entries: entries, codec: codec, claim: claim, chain: [], now: now, attestation: out verified, reason: out reason);
    }

    private static WorldDefinition Quilt(string name, string counterpart, string document, Vector3 center, float yaw, IReadOnlyList<WorldAdmissionEntry> admission) {
        var baseDocument = Fixtures.BuildDocument();

        return (baseDocument with {
            Admission = admission,
            References = [new WorldReference(Name: WorldSafeName.Parse(candidate: "neighbour"), Document: document)],
            Destinations = [new WorldDestination(
                Name: WorldSafeName.Parse(candidate: "neighbour"),
                Reference: WorldSafeName.Parse(candidate: "neighbour"),
                Scope: WorldDestinationScope.Global,
                Durability: WorldDestinationDurability.Persisted)],
            Adjacencies = [new WorldAdjacency(
                Name: WorldSafeName.Parse(candidate: name),
                Destination: "neighbour",
                Counterpart: counterpart,
                Boundary: new WorldAdjacencyBoundary(Center: center, OutwardYawDegrees: yaw, OutwardPitchDegrees: 0f, Width: 8f, Height: 6f))],
        });
    }

    private sealed class StubResolver(WorldCounterpartAttestation attestation) : IWorldNeighbourResolver {
        public WorldNeighbourResolution Resolve(string document) =>
            (string.Equals(a: document, b: attestation.Document, comparisonType: StringComparison.Ordinal)
                ? WorldNeighbourResolution.Attested(attestation: attestation)
                : WorldNeighbourResolution.Unavailable(reason: $"no attestation for '{document}'"));
    }
}
