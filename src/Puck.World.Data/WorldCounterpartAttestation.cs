using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Attestation;
using Puck.Maths;

using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The overlap arithmetic's inputs from one side of a seam. Both sides derive the same depth from the pair, so a
/// neighbour across a trust boundary proves its half by attesting these five numbers rather than by handing over the
/// document they were computed from.
/// </summary>
/// <remarks>Every distance rides as raw Q48.16 bits rather than the document's decimal-string spelling: this payload
/// is machine-composed and signed over, so the bits both sides feed the overlap arithmetic must be the bits that
/// crossed, with no parse in between.</remarks>
/// <param name="BodyReachRaw">The greatest collider-center reach the document's kits declare, in metres.</param>
/// <param name="InteractionReachRaw">The greatest declared interaction/targeting range, in metres.</param>
/// <param name="SpeedCeilingRaw">The greatest speed a body can carry through a boundary, in metres per second.</param>
/// <param name="SimulationRateHz">The authoritative step rate, in Hz.</param>
/// <param name="HysteresisRaw">The reciprocal handoff deadband, in metres.</param>
/// <param name="SettleDeadbandRaw">The floor/ceiling settle deadband, in metres.</param>
public sealed record WorldOverlapTerms(
    long BodyReachRaw,
    long InteractionReachRaw,
    long SpeedCeilingRaw,
    int SimulationRateHz,
    long HysteresisRaw,
    long SettleDeadbandRaw
) {
    /// <summary>Gets the floor/ceiling settle deadband.</summary>
    [JsonIgnore]
    public FixedQ4816 SettleDeadband => FixedQ4816.FromRawBits(value: SettleDeadbandRaw);

    /// <summary>Gets the greatest collider-center reach.</summary>
    [JsonIgnore]
    public FixedQ4816 BodyReach => FixedQ4816.FromRawBits(value: BodyReachRaw);

    /// <summary>Gets the greatest declared interaction/targeting range.</summary>
    [JsonIgnore]
    public FixedQ4816 InteractionReach => FixedQ4816.FromRawBits(value: InteractionReachRaw);

    /// <summary>Gets the greatest speed a body can carry through a boundary.</summary>
    [JsonIgnore]
    public FixedQ4816 SpeedCeiling => FixedQ4816.FromRawBits(value: SpeedCeilingRaw);

    /// <summary>Gets the reciprocal handoff deadband.</summary>
    [JsonIgnore]
    public FixedQ4816 Hysteresis => FixedQ4816.FromRawBits(value: HysteresisRaw);

    /// <summary>Derives a document's own overlap terms.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="terms">The terms on success.</param>
    /// <param name="reason">The named reason on failure.</param>
    /// <returns><see langword="true"/> when every term derives.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static bool TryDerive(WorldDefinition definition, out WorldOverlapTerms? terms, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        terms = null;

        if (!WorldAdjacencyPolicy.TryBodyReach(definition: definition, reach: out var bodyReach, reason: out reason) ||
            !WorldAdjacencyPolicy.TryReciprocalHysteresis(definition: definition, depth: out var hysteresis, reason: out reason) ||
            !WorldAdjacencyPolicy.TryVerticalSettleDeadband(definition: definition, depth: out var settle, reason: out reason)) {
            return false;
        }

        terms = new WorldOverlapTerms(
            BodyReachRaw: bodyReach.Value,
            InteractionReachRaw: WorldAdjacencyPolicy.InteractionReach(definition: definition).Value,
            SpeedCeilingRaw: WorldFacePortalPolicy.SpeedCeiling(definition: definition).Value,
            SimulationRateHz: definition.SimulationRateHz,
            HysteresisRaw: hysteresis.Value,
            SettleDeadbandRaw: settle.Value);
        reason = string.Empty;

        return true;
    }
}

/// <summary>One boundary a counterpart declares, as it appears to the other side of the seam.</summary>
/// <param name="Name">The counterpart adjacency row's own name.</param>
/// <param name="Counterpart">The row this edge points back at across the seam.</param>
/// <param name="Destination">The counterpart's own destination row name.</param>
/// <param name="Boundary">The authored boundary rectangle and its outward orientation.</param>
public sealed record WorldAttestedEdge(WorldSafeName Name, string Counterpart, string Destination, WorldAdjacencyBoundary Boundary);

/// <summary>
/// A neighbouring authority's signed statement of what it declares at a shared seam: its edges, and its own overlap
/// terms. Everything a reciprocity proof needs and nothing else — a validator that consumes one never reads the
/// neighbour's document, so proving a border costs neither side its contents.
/// </summary>
/// <param name="Document">The neighbour document name this attests, as a local <c>references</c> row spells it.</param>
/// <param name="Edges">Every adjacency row the neighbour declares.</param>
/// <param name="Overlap">The neighbour's own overlap terms.</param>
public sealed record WorldCounterpartAttestation(string Document, IReadOnlyList<WorldAttestedEdge> Edges, WorldOverlapTerms Overlap) {
    /// <summary>The document schema tag a signed attestation payload carries.</summary>
    public const string SchemaVersion = "puck.world.counterpart.v1";

    /// <summary>Gets the schema tag.</summary>
    public string Schema { get; init; } = SchemaVersion;

    /// <summary>Reduces a document to what it declares at its seams.</summary>
    /// <param name="definition">The document to attest.</param>
    /// <param name="document">The document name a peer's <c>references</c> row spells.</param>
    /// <param name="attestation">The attestation on success.</param>
    /// <param name="reason">The named reason on failure.</param>
    /// <returns><see langword="true"/> when the attestation composes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static bool TryCompose(WorldDefinition definition, string document, out WorldCounterpartAttestation? attestation, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        attestation = null;

        if (!WorldOverlapTerms.TryDerive(definition: definition, terms: out var terms, reason: out reason) || (terms is null)) {
            return false;
        }

        var edges = new List<WorldAttestedEdge>();

        foreach (var row in (definition.Adjacencies ?? [])) {
            if (row is null) {
                continue;
            }

            edges.Add(item: new WorldAttestedEdge(Name: row.Name, Counterpart: row.Counterpart, Destination: row.Destination, Boundary: row.Boundary));
        }

        attestation = new WorldCounterpartAttestation(Document: document, Edges: edges, Overlap: terms);
        reason = string.Empty;

        return true;
    }

    /// <summary>Finds the edge a local adjacency's <c>counterpart</c> names.</summary>
    /// <param name="name">The counterpart edge name.</param>
    /// <returns>The edge, or <see langword="null"/> when this counterpart declares none by that name.</returns>
    public WorldAttestedEdge? FindEdge(string name) {
        foreach (var edge in Edges) {
            if (string.Equals(a: edge.Name.Value, b: name, comparisonType: StringComparison.Ordinal)) {
                return edge;
            }
        }

        return null;
    }
}

/// <summary>
/// The signed attestation a counterpart claim travels in. The claim's payload is the attestation's own
/// canonical JSON, and the trust list is built from the reading world's <c>admission</c> entries — the same keys that
/// decide who may connect decide whose border claim is worth believing.
/// </summary>
public static class WorldCounterpartAttestationProtocol {
    /// <summary>The fixed purpose every counterpart attestation claim declares.</summary>
    public const string Purpose = "puck.world.counterpart-attestation";

    /// <summary>The fixed audience every counterpart attestation claim is directed at.</summary>
    public const string Audience = "puck.world";

    /// <summary>The hard cap on one attestation payload, applied before parsing.</summary>
    public const int MaxPayloadBytes = (256 * 1024);

    /// <summary>Serializes an attestation to the exact payload bytes a claim signs over.</summary>
    /// <param name="attestation">The attestation.</param>
    /// <returns>The canonical payload bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attestation"/> is <see langword="null"/>.</exception>
    public static byte[] Payload(WorldCounterpartAttestation attestation) {
        ArgumentNullException.ThrowIfNull(argument: attestation);

        return JsonSerializer.SerializeToUtf8Bytes(value: attestation, jsonTypeInfo: WorldJsonContext.Default.WorldCounterpartAttestation);
    }

    /// <summary>Verifies a presented attestation against a world's own admission entries and returns what it
    /// attests.</summary>
    /// <param name="entries">The reading world's <c>admission</c> rows — the trust list this claim is checked
    /// against.</param>
    /// <param name="codec">The attestation serialisation the claim/chain bytes were decoded with.</param>
    /// <param name="claim">The counterpart's signed claim.</param>
    /// <param name="chain">The presented chain (0, 1, or 2 bindings).</param>
    /// <param name="now">The verification instant — a validation-boundary read, never a mid-tick one.</param>
    /// <param name="attestation">The attestation on success.</param>
    /// <param name="reason">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the claim verifies and its payload parses.</returns>
    /// <exception cref="ArgumentNullException">Any of <paramref name="codec"/>, <paramref name="claim"/>, or
    /// <paramref name="chain"/> is <see langword="null"/>.</exception>
    public static bool TryVerify(
        IReadOnlyList<WorldAdmissionEntry>? entries,
        IAttestationCodec codec,
        SignedAttestation claim,
        IReadOnlyList<SignedAttestation> chain,
        DateTimeOffset now,
        out WorldCounterpartAttestation? attestation,
        out string reason
    ) {
        ArgumentNullException.ThrowIfNull(argument: codec);
        ArgumentNullException.ThrowIfNull(argument: claim);
        ArgumentNullException.ThrowIfNull(argument: chain);

        attestation = null;

        if (!WorldAdmissionDoor.TryBuildTrustList(entries: entries, trustList: out var trustList, reason: out reason)) {
            return false;
        }

        var result = AttestationProfile.Base.VerifyChain(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: trustList!,
            now: now,
            expectedPurpose: Purpose,
            expectedAudience: Audience);

        if (!result.Verified) {
            reason = (result.RefusalReason ?? "the counterpart attestation did not verify");

            return false;
        }

        if (claim.PayloadKind != AttestationPayloadKind.Opaque) {
            reason = "the counterpart attestation claim carries no opaque payload";

            return false;
        }

        var payload = claim.PayloadBytes.Span;

        if (payload.Length > MaxPayloadBytes) {
            reason = $"the counterpart attestation payload is {payload.Length} bytes; cap is {MaxPayloadBytes}";

            return false;
        }

        try {
            attestation = JsonSerializer.Deserialize(utf8Json: payload, jsonTypeInfo: WorldJsonContext.Default.WorldCounterpartAttestation);
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            reason = $"the counterpart attestation payload does not parse — {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (attestation is null) {
            reason = "the counterpart attestation payload deserialized to null";

            return false;
        }

        if (!string.Equals(a: attestation.Schema, b: WorldCounterpartAttestation.SchemaVersion, comparisonType: StringComparison.Ordinal)) {
            reason = $"counterpart attestation schema '{attestation.Schema}' is not {WorldCounterpartAttestation.SchemaVersion}";
            attestation = null;

            return false;
        }

        reason = string.Empty;

        return true;
    }
}
