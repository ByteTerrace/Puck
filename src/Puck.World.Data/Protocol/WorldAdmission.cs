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

    /// <summary><see cref="WorldAdmissionEntry.Domain"/> names an authenticated federation authority namespace (or
    /// <see cref="WorldAdmissionEntry.AnyAuthority"/>), and the row says what a traveler that authority hands over is
    /// minted. The proof behind it is <c>Server.WorldFederationSecurity</c>'s shared-secret handshake rather than a
    /// carriage claim, so such a row carries no <see cref="WorldAdmissionEntry.Algorithm"/> and no
    /// <see cref="WorldAdmissionEntry.PublicKey"/>, and <see cref="WorldAdmissionDoor"/> skips it when building its
    /// trust list. An arriving traveler's body index, profile id, and display name are all supplied by the handing
    /// authority; which authority is speaking is the only verified fact, so it is the only one trust is authored
    /// against.</summary>
    FederatedAuthority,
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
/// <param name="Subject">The subject it scopes to; <see langword="null"/> means the body this admission assigns,
/// resolved by <see cref="SubjectFor"/> once that index is known. An authored template cannot name a body index —
/// the door runs before the population picks one — so a row that must follow the admitted body omits its subject.</param>
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
public readonly record struct WorldAdmissionGrant(WorldCapability Capability, GrantSubject? Subject = null, bool Exclusive = false, ushort? Budget = null, ushort? EventBudget = null, MutationKindMask? KindMask = null) {
    /// <summary>Returns the concrete subject this template mints over for a body index.</summary>
    /// <param name="bodyIndex">The 0-based entity index admission assigned.</param>
    public GrantSubject SubjectFor(int bodyIndex) => (Subject ?? GrantSubject.Body(index: bodyIndex));
}

/// <summary>What one admission decision authorizes. Only <see cref="WorldAdmissionDoor"/> produces one — from a
/// verified carriage claim, from an already-verified identity re-matched against a candidate document, or from an
/// authenticated federation authority's namespace — so no ingress can mint authority by assembling grant rows of its
/// own.</summary>
public sealed record WorldAdmissionVerdict {
    internal WorldAdmissionVerdict(string identityDomain, string identitySubject, IReadOnlyList<WorldAdmissionGrant> templates) {
        IdentityDomain = identityDomain;
        IdentitySubject = identitySubject;
        Templates = templates;
    }

    /// <summary>Gets the verified identity's domain.</summary>
    public string IdentityDomain { get; }

    /// <summary>Gets the verified identity's subject; empty when the admitting entry pins none.</summary>
    public string IdentitySubject { get; }

    /// <summary>Gets the admitting entry's own authored grant templates.</summary>
    public IReadOnlyList<WorldAdmissionGrant> Templates { get; }
}

/// <summary>One row of the <c>admission</c> section — durable configuration naming one identity or issuer this world
/// admits over its TCP socket, and what a peer verified under it is minted (see <see cref="Grants"/>). Never a live
/// grant row itself: <see cref="WorldAdmissionDoor"/> is the only consumer, and only at the pre-population Hello
/// handshake, off the tick thread — this section carries no <see cref="WorldSection"/> axis and nothing mutates it
/// live, exactly like <see cref="WorldReference"/>/<see cref="WorldPortalsSection"/>. Absent (the default) admits no
/// remote peer at all — deny by default, the same posture an empty <c>Puck.Carriage.TrustList</c> already carries.</summary>
/// <param name="Domain">The trusted key's own id domain — a lowercase-hex SHA-256 fingerprint (64 characters). For
/// <see cref="WorldAdmissionTrustMode.Vouches"/> this must be <see cref="PublicKey"/>'s own fingerprint (a root is
/// self-certifying). For <see cref="WorldAdmissionTrustMode.SignsDirectly"/> it names the domain namespace this
/// individual is pinned under, which need not equal their own key's hash. For
/// <see cref="WorldAdmissionTrustMode.FederatedAuthority"/> it is not a key id at all: it names the authenticated
/// source-authority namespace, or <see cref="WorldAdmissionEntry.AnyAuthority"/> for any authority that completes the
/// federation handshake.</param>
/// <param name="Subject">The platform user id this entry pins, required for <see cref="WorldAdmissionTrustMode.SignsDirectly"/>
/// and refused for <see cref="WorldAdmissionTrustMode.Vouches"/> (a root vouches for every subject its two-hop chain
/// resolves, never one named here).</param>
/// <param name="Mode">Whether this entry signs directly or vouches for a chain.</param>
/// <param name="Algorithm">One of <c>Puck.Carriage.CarriageAlgorithms</c>'s SIGNING names (e.g.
/// <c>ecdsa-p256-sha256</c>) — never a sealing algorithm; a sealing key can never admit a claim.</param>
/// <param name="PublicKey">The pinned key's actual <c>SubjectPublicKeyInfo</c> bytes, base64-encoded — carried
/// alongside the id because offline verification needs the real bytes, never a fetch (docs/world-model.md, "Signed
/// carriage": consulting the issuer at verification time is a ruled-out design).</param>
/// <param name="Grants">What a peer verified under this entry is minted, INSTEAD OF the blanket
/// <c>Control</c>/<c>all</c> every admitted peer used to receive unconditionally. Empty (never null) is a legitimate
/// authored choice: a verified-but-granted-nothing identity, admitted onto the connection table and able to hold a
/// socket open, but unable to submit anything the grant table would honor.</param>
public sealed record WorldAdmissionEntry(string Domain, string? Subject, WorldAdmissionTrustMode Mode, string Algorithm, string PublicKey, IReadOnlyList<WorldAdmissionGrant> Grants) {
    /// <summary>The <see cref="Domain"/> value a <see cref="WorldAdmissionTrustMode.FederatedAuthority"/> row uses to
    /// name every authority that completes the federation handshake rather than one namespace.</summary>
    public const string AnyAuthority = "*";
}
