using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World.Protocol;

/// <summary>Whether an admission entry pins one individual's own signing key, or a domain root that vouches for a
/// two-hop chain beneath it — <c>Puck.Carriage.CarriageTrustMode</c>'s own two members, mirrored here as an authored
/// document token so this project need not reference a leaf project's internal enum shape directly from JSON: a
/// document field is a closed, versioned vocabulary of its own, never a re-export.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldAdmissionTrustMode>))]
public enum WorldAdmissionTrustMode : byte {
    /// <summary>The pinned key signs the connecting peer's claim directly — no chain travels with it. Pins one
    /// individual (a friend, a known peer) without trusting whatever domain minted them.</summary>
    SignsDirectly,

    /// <summary>The pinned key is a domain root that vouches for an issuing key, which vouches for the connecting
    /// peer's subject key — a two-hop chain travels with the claim.</summary>
    Vouches,
}

/// <summary>One capability a verified admission entry mints for the connecting peer once its identity checks out —
/// the same fields <see cref="WorldGrant"/> carries for a Peer principal, minus <see cref="WorldGrant.Principal"/>
/// itself (unknowable until <c>Server.WorldPopulation.TryAdmitRemotePeer</c> assigns the connection's body index and
/// generation) and minus the co-driving payloads (<see cref="WorldGrant.Reach"/>/<see cref="WorldGrant.Consent"/>/
/// <see cref="WorldGrant.Ceiling"/>), which are seat-authored pool mechanics that presuppose a body already exists.
/// <c>Server.WorldServer.TryAdmitPeerConnection</c> rebinds each template onto
/// <c>WorldPrincipal.Peer(index, generation)</c> the moment the body is admitted, through the same
/// <c>Server.WorldServer.Grant</c> door the document's own <c>grants</c> section goes through — an admission-minted
/// grant is subject to the identical budget/exclusivity rules a live <c>world.grant</c> row is.</summary>
/// <param name="Capability">The capability minted.</param>
/// <param name="Subject">The subject it scopes to.</param>
/// <param name="Exclusive">Whether the mint is exclusive.</param>
/// <param name="Budget">The untrusted-principal per-tick dispatch budget — required by the live grant door on a
/// Drive/Observe row, and on an untrusted <c>Mutate</c>/<c>section:&lt;name&gt;</c> row, exactly as for any other
/// Peer/Addon grant; an admission entry that omits it on such a row mints a row the door refuses at admission
/// time.</param>
/// <param name="EventBudget">The untrusted-principal per-tick event-push budget for an Observe row over an
/// event-bearing subject.</param>
/// <param name="KindMask">The verb-scoped narrowing beneath a <c>Mutate</c>/<c>section:&lt;name&gt;</c> row —
/// required by the live grant door on an untrusted principal's such a row (an absent mask there is refused rather
/// than read as full reach).</param>
public readonly record struct WorldAdmissionGrant(WorldCapability Capability, GrantSubject Subject, bool Exclusive = false, ushort? Budget = null, ushort? EventBudget = null, MutationKindMask? KindMask = null);

/// <summary>One row of the <c>admission</c> section — durable configuration naming one identity or issuer this world
/// admits over its TCP socket, and what a peer verified under it is minted (see <see cref="Grants"/>). Never a live
/// grant row itself: <see cref="WorldAdmissionDoor"/> is the only consumer, and only at the pre-population Hello
/// handshake, off the tick thread — this section carries no <see cref="WorldSection"/> axis and nothing mutates it
/// live, exactly like <see cref="WorldReference"/>/<see cref="WorldPortalsSection"/>. Absent (the default) admits no
/// remote peer at all — deny by default, the same posture an empty <c>Puck.Carriage.TrustList</c> already carries.</summary>
/// <param name="Domain">The trusted key's own id domain — a lowercase-hex SHA-256 fingerprint (64 characters). For
/// <see cref="WorldAdmissionTrustMode.Vouches"/> this must be <see cref="PublicKey"/>'s own fingerprint (a root is
/// self-certifying). For <see cref="WorldAdmissionTrustMode.SignsDirectly"/> it names the domain namespace this
/// individual is pinned under, which need not equal their own key's hash.</param>
/// <param name="Subject">The platform user id this entry pins, required for <see cref="WorldAdmissionTrustMode.SignsDirectly"/>
/// and refused for <see cref="WorldAdmissionTrustMode.Vouches"/> (a root vouches for every subject its two-hop chain
/// resolves, never one named here).</param>
/// <param name="Mode">Whether this entry signs directly or vouches for a chain.</param>
/// <param name="Algorithm">Exactly <c>ecdsa-p256-sha256</c>, the only signing algorithm enabled by the
/// admission door's mandatory <c>carriage-v1-base</c> profile. Sealing algorithms and optional signing
/// extensions are refused by document validation.</param>
/// <param name="PublicKey">The pinned key's actual <c>SubjectPublicKeyInfo</c> bytes, base64-encoded — carried
/// alongside the id because offline verification needs the real bytes, never a fetch (docs/vision.md, "Signed
/// carriage": consulting the issuer at verification time is a ruled-out design).</param>
/// <param name="Grants">What a peer verified under this entry is minted, INSTEAD OF the blanket
/// <c>Control</c>/<c>all</c> every admitted peer used to receive unconditionally. Empty (never null) is a legitimate
/// authored choice: a verified-but-granted-nothing identity, admitted onto the connection table and able to hold a
/// socket open, but unable to submit anything the grant table would honor.</param>
public sealed record WorldAdmissionEntry(string Domain, string? Subject, WorldAdmissionTrustMode Mode, string Algorithm, string PublicKey, IReadOnlyList<WorldAdmissionGrant> Grants);
