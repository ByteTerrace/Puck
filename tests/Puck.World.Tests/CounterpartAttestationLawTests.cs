using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

using Azure.Core;

using Xunit;

using Puck.Attestation;
using Puck.World.Protocol;
using Puck.World.Server;

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

        Assert.True(condition: WorldCounterpartAttestation.TryCompose(attestation: out var attested, definition: east, document: "east.world.json", reason: out var composeReason), userMessage: composeReason);

        var codec = new CborAttestationCodec();

        Assert.True(condition: TryVerifySigned(attestation: attested!, codec: codec, domain: domain, entries: [trust], key: key, reason: out var verifyReason, subject: "counterpart", verified: out var verified), userMessage: verifyReason);
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: west, reason: out var accepted, neighbours: new StubResolver(attestation: verified!)), userMessage: accepted);

        // A counterpart that widens its half of the seam after signing does not match what this side authored.
        var tampered = (verified! with {
            Edges = [(verified.Edges[0] with { Boundary = (verified.Edges[0].Boundary with { Width = 9f }) })],
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: west, reason: out var refused, neighbours: new StubResolver(attestation: tampered)));
        Assert.Contains(actualString: refused, comparisonType: StringComparison.Ordinal, expectedSubstring: "but neighbour 'east.world.json'/'east' is");

        // A counterpart that stops pointing back refuses by its own name, not by the extent one.
        var nonReciprocal = (verified with { Edges = [(verified.Edges[0] with { Counterpart = "elsewhere" })] });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: west, reason: out var broken, neighbours: new StubResolver(attestation: nonReciprocal)));
        Assert.Contains(actualString: broken, comparisonType: StringComparison.Ordinal, expectedSubstring: "is not reciprocal");
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

        Assert.True(condition: WorldCounterpartAttestation.TryCompose(attestation: out var attested, definition: east, document: "east.world.json", reason: out _));

        var codec = new CborAttestationCodec();

        Assert.False(condition: TryVerifySigned(attestation: attested!, codec: codec, domain: domain, entries: [trust], key: stranger, reason: out var strangerReason, subject: "counterpart", verified: out _));
        Assert.NotEmpty(collection: strangerReason);

        // The control: the trusted key over the same payload does verify, so the refusal is about the signer.
        Assert.True(condition: TryVerifySigned(attestation: attested!, codec: codec, domain: domain, entries: [trust], key: trusted, reason: out var trustedReason, subject: "counterpart", verified: out _), userMessage: trustedReason);

        // A world with no key-bearing admission row believes no border claim at all.
        Assert.False(condition: TryVerifySigned(attestation: attested!, codec: codec, domain: domain, entries: [], key: trusted, reason: out var noTrust, subject: "counterpart", verified: out _));
        Assert.Contains(actualString: noTrust, comparisonType: StringComparison.Ordinal, expectedSubstring: "no key-bearing admission entries");
    }
    // H2: the attestation's own signed payload is what a peer without the document reads its geometry back from —
    // this proves the wire round trip preserves it exactly, over multiple edges, independent of any signing key.
    [Fact]
    public void AttestedEdgeBoundaryRoundTripsToTheSameCompiledFrame() {
        var definition = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(SafeName.Parse(candidate: "east-ref"), "east.world.json"),
                new WorldReference(SafeName.Parse(candidate: "north-ref"), "north.world.json"),
            ],
            Destinations = [
                new WorldDestination(SafeName.Parse(candidate: "east"), "east-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(SafeName.Parse(candidate: "north"), "north-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(SafeName.Parse(candidate: "east-edge"), "east", "west-edge", new WorldAdjacencyBoundary(Center: new Vector3(x: 10f, y: 0f, z: 0f), OutwardYawDegrees: 90f, OutwardPitchDegrees: 0f, Width: 8f, Height: 6f)),
                new WorldAdjacency(SafeName.Parse(candidate: "north-edge"), "north", "south-edge", new WorldAdjacencyBoundary(Center: new Vector3(x: 0f, y: 0f, z: 10f), OutwardYawDegrees: 0f, OutwardPitchDegrees: 30f, Width: 6f, Height: 6f)),
            ],
        };

        Assert.True(condition: WorldCounterpartAttestation.TryCompose(attestation: out var attestation, definition: definition, document: "self.world.json", reason: out var composeReason), userMessage: composeReason);

        var payload = WorldCounterpartAttestationProtocol.Payload(attestation: attestation!);
        var roundTripped = JsonSerializer.Deserialize(utf8Json: payload, jsonTypeInfo: WorldJsonContext.Default.WorldCounterpartAttestation);

        Assert.NotNull(@object: roundTripped);
        Assert.Equal(attestation!.Document, roundTripped!.Document);
        Assert.Equal(attestation.Edges.Count, roundTripped.Edges.Count);

        for (var index = 0; (index < attestation.Edges.Count); index++) {
            Assert.Equal(attestation.Edges[index].Boundary.CompileFrame(), roundTripped.Edges[index].Boundary.CompileFrame());
        }
    }
    // The oracle-fed path end to end: an owner-arm reference resolves through WorldApiCounterpartResolver against
    // an in-process fake platform oracle (a real root->issuing->subject Vouches chain, a fake HTTP handler serving
    // the wrapped claim), and the derived corner still validates — proving the whole chain from authored owner-arm
    // reference through NeighbourKey, resolver dispatch, verification, subject-vs-owner binding, to the validator's
    // own acceptance of VerifiedAttested.
    [Fact]
    public void OwnerNamedNeighbourProvesItsCornerThroughTheApiResolver() {
        var owner = Guid.NewGuid();
        const string worldName = "quilt-se";
        var oracle = new FakeOracle();

        var (source, left, right, corner) = OwnerCorner(
            owner: owner,
            world: worldName
        );

        Assert.True(condition: WorldCounterpartAttestation.TryCompose(attestation: out var attestation, definition: corner, document: $"owner/{owner:D}/{worldName}", reason: out var composeReason), userMessage: composeReason);

        var wrapper = oracle.SignCounterpartClaim(
            attestation: attestation!,
            subject: owner.ToString(format: "D")
        );
        var handler = new FakeCounterpartHandler();

        handler.Publish(
            owner: owner,
            world: worldName,
            wrapper: wrapper
        );

        using var httpClient = new HttpClient(handler: handler) { BaseAddress = new Uri(uriString: "https://fake-platform.example/") };
        var apiResolver = new WorldApiCounterpartResolver(
            admissionEntries: [oracle.VouchingAdmissionEntry],
            credential: new FakeTokenCredential(),
            httpClient: httpClient
        );
        var resolver = WorldCompositeNeighbourResolver.Compose(new DictResolver(definitions: new Dictionary<string, WorldDefinition> {
            ["left.world.json"] = left,
            ["right.world.json"] = right,
        }), apiResolver)!;

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: source, neighbours: resolver, reason: out var accepted), userMessage: accepted);
        Assert.Contains(expected: $"owner/{owner:D}/{worldName}", collection: handler.RequestedPaths);
    }
    // The subject binding is load-bearing, not decorative: a claim genuinely vouched-for by the SAME trusted root,
    // but for a DIFFERENT onboarded subject than the reference names, must never satisfy that reference.
    [Fact]
    public void ApiResolverRefusesAClaimWhoseSubjectIsNotTheReferenceOwner() {
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        const string worldName = "quilt-se";
        var oracle = new FakeOracle();

        var (_, _, _, corner) = OwnerCorner(
            owner: owner,
            world: worldName
        );

        Assert.True(condition: WorldCounterpartAttestation.TryCompose(attestation: out var attestation, definition: corner, document: $"owner/{owner:D}/{worldName}", reason: out var composeReason), userMessage: composeReason);

        // The stranger's own claim verifies cleanly against the same root — it's a different onboarded user, not a
        // forger — but its subject is not the oid this key is being resolved under.
        var wrapper = oracle.SignCounterpartClaim(
            attestation: attestation!,
            subject: stranger.ToString(format: "D")
        );
        var handler = new FakeCounterpartHandler();

        handler.Publish(
            owner: owner,
            world: worldName,
            wrapper: wrapper
        );

        using var httpClient = new HttpClient(handler: handler) { BaseAddress = new Uri(uriString: "https://fake-platform.example/") };
        var apiResolver = new WorldApiCounterpartResolver(
            admissionEntries: [oracle.VouchingAdmissionEntry],
            credential: new FakeTokenCredential(),
            httpClient: httpClient
        );

        var outcome = apiResolver.Resolve(document: $"owner/{owner:D}/{worldName}");

        Assert.Equal(expected: WorldNeighbourResolutionKind.Unavailable, actual: outcome.Kind);
        Assert.Contains(expectedSubstring: "does not name the reference's owner", actualString: outcome.Reason, comparisonType: StringComparison.Ordinal);
    }
    // A claim vouched for by a root the reading world never admitted refuses by name — the resolver trusts only
    // ITS OWN admission rows, never whatever chain the oracle happened to sign.
    [Fact]
    public void ApiResolverRefusesAClaimFromAnUnadmittedRoot() {
        var owner = Guid.NewGuid();
        const string worldName = "quilt-se";
        var untrustedOracle = new FakeOracle();
        var trustedOracle = new FakeOracle();

        var (_, _, _, corner) = OwnerCorner(
            owner: owner,
            world: worldName
        );

        Assert.True(condition: WorldCounterpartAttestation.TryCompose(attestation: out var attestation, definition: corner, document: $"owner/{owner:D}/{worldName}", reason: out var composeReason), userMessage: composeReason);

        var wrapper = untrustedOracle.SignCounterpartClaim(
            attestation: attestation!,
            subject: owner.ToString(format: "D")
        );
        var handler = new FakeCounterpartHandler();

        handler.Publish(
            owner: owner,
            world: worldName,
            wrapper: wrapper
        );

        using var httpClient = new HttpClient(handler: handler) { BaseAddress = new Uri(uriString: "https://fake-platform.example/") };
        // The reading world only trusts trustedOracle's root — a different, unrelated Vouches chain.
        var apiResolver = new WorldApiCounterpartResolver(
            admissionEntries: [trustedOracle.VouchingAdmissionEntry],
            credential: new FakeTokenCredential(),
            httpClient: httpClient
        );

        var outcome = apiResolver.Resolve(document: $"owner/{owner:D}/{worldName}");

        Assert.Equal(expected: WorldNeighbourResolutionKind.Unavailable, actual: outcome.Kind);
    }

    private static (WorldDefinition Source, WorldDefinition Left, WorldDefinition Right, WorldDefinition Corner) OwnerCorner(Guid owner, string world) {
        var boundary = static (float yaw) => new WorldAdjacencyBoundary(Center: Vector3.Zero, OutwardYawDegrees: yaw, OutwardPitchDegrees: 0f, Width: 8f, Height: 8f);
        var cornerReference = new WorldReference(Name: SafeName.Parse(candidate: "corner-ref"), Owner: owner, World: SafeName.Parse(candidate: world));
        var source = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(SafeName.Parse(candidate: "left-ref"), "left.world.json"),
                new WorldReference(SafeName.Parse(candidate: "right-ref"), "right.world.json"),
                cornerReference,
            ],
            Destinations = [
                new WorldDestination(SafeName.Parse(candidate: "left"), "left-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(SafeName.Parse(candidate: "right"), "right-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(SafeName.Parse(candidate: "corner"), "corner-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(SafeName.Parse(candidate: "left-edge"), "left", "source-edge", boundary(90f)),
                new WorldAdjacency(SafeName.Parse(candidate: "right-edge"), "right", "source-edge", boundary(0f)),
            ],
        };
        var left = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(SafeName.Parse(candidate: "source-ref"), "source.world.json"),
                cornerReference,
            ],
            Destinations = [
                new WorldDestination(SafeName.Parse(candidate: "source"), "source-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(SafeName.Parse(candidate: "corner"), "corner-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(SafeName.Parse(candidate: "source-edge"), "source", "left-edge", boundary(-90f)),
                new WorldAdjacency(SafeName.Parse(candidate: "corner-edge"), "corner", "left-edge", boundary(0f)),
            ],
        };
        var right = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(SafeName.Parse(candidate: "source-ref"), "source.world.json"),
                cornerReference,
            ],
            Destinations = [
                new WorldDestination(SafeName.Parse(candidate: "source"), "source-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(SafeName.Parse(candidate: "corner"), "corner-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(SafeName.Parse(candidate: "source-edge"), "source", "right-edge", boundary(180f)),
                new WorldAdjacency(SafeName.Parse(candidate: "corner-edge"), "corner", "right-edge", boundary(90f)),
            ],
        };
        var corner = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(SafeName.Parse(candidate: "left-ref"), "left.world.json"),
                new WorldReference(SafeName.Parse(candidate: "right-ref"), "right.world.json"),
            ],
            Destinations = [
                new WorldDestination(SafeName.Parse(candidate: "left"), "left-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(SafeName.Parse(candidate: "right"), "right-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(SafeName.Parse(candidate: "left-edge"), "left", "corner-edge", boundary(180f)),
                new WorldAdjacency(SafeName.Parse(candidate: "right-edge"), "right", "corner-edge", boundary(-90f)),
            ],
        };

        return (source, left, right, corner);
    }

    /// <summary>Resolves plainly-named ("document"-shaped) neighbours from an in-memory dictionary — the local half
    /// of the composite this test wires beside <see cref="WorldApiCounterpartResolver"/>, mirroring the production
    /// two-resolver composition (a local/storage resolver plus the API resolver).</summary>
    private sealed class DictResolver(IReadOnlyDictionary<string, WorldDefinition> definitions) : IWorldNeighbourResolver {
        public WorldNeighbourResolution Resolve(string document) =>
            (definitions.TryGetValue(key: document, value: out var definition)
                ? WorldNeighbourResolution.Resolved(definition: definition)
                : WorldNeighbourResolution.Unavailable(reason: $"no local document named '{document}'"));
    }
    /// <summary>An in-process stand-in for the platform's oracle — a real root-issuing-subject Vouches chain minted
    /// with <see cref="AttestationSigner"/>, never a shortcut through it. Legitimate for proving Puck's own shape
    /// end to end (the wire spec's own conformance framing); NOT evidence about the platform's actual CBOR bytes —
    /// that is a live-smoke fact, not a hermetic-test one.</summary>
    private sealed class FakeOracle {
        private readonly IAttestationCodec m_codec = new CborAttestationCodec();
        private readonly ECDsa m_issuingKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        private readonly ECDsa m_rootKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

        private readonly string m_rootDomain;

        public FakeOracle() {
            m_rootDomain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: m_rootKey.ExportSubjectPublicKeyInfo());
        }

        /// <summary>The admission row a reading world authors to trust every claim this oracle vouches for.</summary>
        public WorldAdmissionEntry VouchingAdmissionEntry => new(
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            Domain: m_rootDomain,
            Grants: [],
            Mode: WorldAdmissionTrustMode.Vouches,
            PublicKey: Convert.ToBase64String(inArray: m_rootKey.ExportSubjectPublicKeyInfo()),
            Subject: null);

        /// <summary>Mints a fresh subject key, its issuing->subject binding, and a counterpart claim signed by that
        /// subject key — the wire-transport-ready envelope <see cref="WorldApiCounterpartResolver"/> decodes.</summary>
        public byte[] SignCounterpartClaim(WorldCounterpartAttestation attestation, string subject) {
            var now = DateTimeOffset.UtcNow;
            var seconds = now.ToUnixTimeSeconds();
            using var subjectKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
            var subjectSpki = subjectKey.ExportSubjectPublicKeyInfo();
            var subjectId = KeyId.ForSubject(
                algorithm: AttestationAlgorithms.EcdsaP256Sha256,
                domain: m_rootDomain,
                subject: subject,
                subjectPublicKeyInfo: subjectSpki
            );
            var rootToIssuing = AttestationSigner.SignKeyBinding(
                codec: m_codec,
                domain: m_rootDomain,
                notAfter: (seconds + 3600L),
                notBefore: seconds,
                signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
                signerKey: m_rootKey,
                targetId: KeyId.ForIssuing(
                    algorithm: AttestationAlgorithms.EcdsaP256Sha256,
                    domain: m_rootDomain,
                    subjectPublicKeyInfo: m_issuingKey.ExportSubjectPublicKeyInfo()
                ),
                targetSubjectPublicKeyInfo: m_issuingKey.ExportSubjectPublicKeyInfo()
            );
            var issuingToSubject = AttestationSigner.SignKeyBinding(
                codec: m_codec,
                domain: m_rootDomain,
                notAfter: (seconds + 3600L),
                notBefore: seconds,
                signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
                signerKey: m_issuingKey,
                targetId: subjectId,
                targetSubjectPublicKeyInfo: subjectSpki
            );
            var claim = AttestationSigner.SignClaim(
                audience: WorldCounterpartAttestationProtocol.Audience,
                claimBytes: WorldCounterpartAttestationProtocol.Payload(attestation: attestation),
                codec: m_codec,
                domain: m_rootDomain,
                notAfter: (seconds + 300L),
                notBefore: seconds,
                purpose: WorldCounterpartAttestationProtocol.Purpose,
                sequence: null,
                signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
                signerKey: subjectKey,
                subject: subject
            );

            return AttestationChainEnvelope.Encode(
                chain: [m_codec.EncodeAttestation(attestation: rootToIssuing), m_codec.EncodeAttestation(attestation: issuingToSubject)],
                claim: m_codec.EncodeAttestation(attestation: claim)
            );
        }
    }
    /// <summary>A fake platform API: serves a published wrapper at the exact route
    /// <see cref="WorldApiCounterpartResolver"/> requests, 404 otherwise, and records every path asked for so a
    /// test can prove the resolver genuinely made the call rather than short-circuiting.</summary>
    private sealed class FakeCounterpartHandler : HttpMessageHandler {
        private readonly Dictionary<string, byte[]> m_responses = new(comparer: StringComparer.Ordinal);
        private readonly List<string> m_requestedPaths = [];

        public IReadOnlyList<string> RequestedPaths => m_requestedPaths;

        public void Publish(Guid owner, string world, byte[] wrapper) => m_responses[$"owner/{owner:D}/{world}"] = wrapper;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) {
            var segments = request.RequestUri!.AbsolutePath.Trim(trimChar: '/').Split(separator: '/');

            // "api/worlds/{owner}/{world}/counterpart"
            if (
                (segments.Length != 5) ||
                !string.Equals(a: segments[0], b: "api", comparisonType: StringComparison.Ordinal) ||
                !string.Equals(a: segments[1], b: "worlds", comparisonType: StringComparison.Ordinal) ||
                !string.Equals(a: segments[4], b: "counterpart", comparisonType: StringComparison.Ordinal)
            ) {
                return new HttpResponseMessage(statusCode: HttpStatusCode.BadRequest);
            }

            var found = m_responses.Keys.FirstOrDefault(predicate: candidate => candidate.StartsWith(value: $"owner/{segments[2]}/", comparisonType: StringComparison.Ordinal));

            if (found is null) {
                m_requestedPaths.Add(item: $"owner/{segments[2]}/{segments[3]}");

                return new HttpResponseMessage(statusCode: HttpStatusCode.NotFound);
            }

            m_requestedPaths.Add(item: found);

            return new HttpResponseMessage(statusCode: HttpStatusCode.OK) {
                Content = new ByteArrayContent(content: m_responses[found]),
            };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(result: Send(
                cancellationToken: cancellationToken,
                request: request
            ));
    }
    /// <summary>A fake platform-API credential — never actually checked by <see cref="FakeCounterpartHandler"/>, so
    /// any non-empty token proves the resolver attaches a bearer header without asserting its content.</summary>
    private sealed class FakeTokenCredential : TokenCredential {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(accessToken: "fake-token", expiresOn: DateTimeOffset.UtcNow.AddHours(hours: 1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(result: GetToken(cancellationToken: cancellationToken, requestContext: requestContext));
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

        return WorldCounterpartAttestationProtocol.TryVerify(entries: entries, codec: codec, claim: claim, chain: [], now: now, attestation: out verified, subject: out _, reason: out reason);
    }
    private static WorldDefinition Quilt(string name, string counterpart, string document, Vector3 center, float yaw, IReadOnlyList<WorldAdmissionEntry> admission) {
        var baseDocument = Fixtures.BuildDocument();

        return (baseDocument with {
            Admission = admission,
            References = [new WorldReference(Name: SafeName.Parse(candidate: "neighbour"), Document: document)],
            Destinations = [new WorldDestination(
                Name: SafeName.Parse(candidate: "neighbour"),
                Reference: SafeName.Parse(candidate: "neighbour"),
                Scope: WorldDestinationScope.Global,
                Durability: WorldDestinationDurability.Persisted)],
            Adjacencies = [new WorldAdjacency(
                Name: SafeName.Parse(candidate: name),
                Destination: "neighbour",
                Counterpart: counterpart,
                Boundary: new WorldAdjacencyBoundary(Center: center, Height: 6f, OutwardPitchDegrees: 0f, OutwardYawDegrees: yaw, Width: 8f))],
        });
    }

    private sealed class StubResolver(WorldCounterpartAttestation attestation) : IWorldNeighbourResolver {
        public WorldNeighbourResolution Resolve(string document) =>
            (string.Equals(a: document, b: attestation.Document, comparisonType: StringComparison.Ordinal)
                ? WorldNeighbourResolution.Attested(attestation: attestation)
                : WorldNeighbourResolution.Unavailable(reason: $"no attestation for '{document}'"));
    }
}
