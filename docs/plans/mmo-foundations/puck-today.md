# Puck as it is

Lane E of [the MMO foundations round](../mmo-foundations.md): what the code at
this tree does for identity worlds, write-back, attestation, adjacency and
crossing, federation, companion custody, disclosure, durable images and draws.
It records evidence only. It holds no options and no recommendations; the other
lanes and the synthesis own those. It answers attack question 7 and the code
side of question 6 of [the attack catalogue](attacks.md#4-questions-for-the-research-lanes),
and re-checks every **[checked]** citation in that catalogue.

Every claim carries a label. **[opened]** means the file was opened at this
tree and the quoted words are pasted from it, with the `file:line` the tool
printed; a `file:line` is a lead for this revision, as every plan's is.
**[inference]** means reasoning from opened facts that was not observed
running. **[unverified]** marks a premise this lane could not settle from the
code. No web source was used, so no claim here is a **[summary]**.

## Summary

The catalogue's broad picture holds, with two corrections that change what the
round is attacking. First, home adoption of a traveller's facts and records is
colocated only: a federated arrival is always a peer admission, so a world on
another machine can write a visitor's travelling projection but nothing it
writes is ever adopted into the player's identity file; what reaches the
identity file is a write by a world in the same process, or the player's own
edit. Second, the Azure path has no binding-based user identity in use: an API
user authenticates with a bearer Entra access token that the silo validates and
re-validates every second, and world authorities sign with a key file of their
own; the platform's 30-day key bindings exist in `Puck.Azure.Functions` but no
world code presents one. No revocation exists in `Puck.Attestation`; the
levers are expiry, live admission edits (which evict a live connection within a
second) and Entra group membership. The resolver's generation counter is not
persisted, but no host both restores rows and spawns resolver instances, and
recovery never addresses a destination by generation, so A13's split is not
reachable today. Seam contact applies a sum of per-neighbour overlap
corrections with no per-tick cap, from poses and collider shapes the neighbour
delivers, and the code itself names that input as not replay-deterministic. No
avatar claim exists, so A12's bake question is moot until H10. Companion
custody steps 1 to 5 are in the code; the records for steps 6 and 7 decode and
fold but nothing mints them. Branch `lane/companion-v1` is superseded for
custody and trust and holds unlanded step 9 and 10 prior art.

## 1. Identity worlds

### The identity document

A character is an owned `puck.world.definition.v1` document with an `identity`
section, held as a `WorldIdentity` **[opened]**
(`src/Puck.World.Schema/WorldIdentity.cs:27-28`):

```csharp
/// <summary>A live identity backed by one owned <see cref="WorldDefinition"/>.</summary>
public sealed partial class WorldIdentity {
```

What leaves home with it is the projection, and only the projection
**[opened]** (`src/Puck.World.Schema/WorldIdentity.cs:10-14`, `:26`):

```csharp
/// Everything that travels with an identity across a seam, on every path a crossing takes and in every leaf it writes:
/// the appearance a destination renders it with, the motion-envelope rates it claims, its explicitly owned records and
/// its facts. Other owned state — chat allow-list grants, controller history, cross-game state rows, bindings, the
/// private HUD panel, the seat look — stays at home, so walking into a stranger's world discloses none of it. A field
```

```csharp
public readonly record struct WorldIdentityProjection(string Id, string Name, string ColorHex, FixedQ4816? MoveSpeed, FixedQ4816? TurnSpeed, WorldStateSection? Records = null, WorldStateRow? Facts = null);
```

The projection carries no look, kit or creation: a destination renders a
visitor with its own kits and the carried color **[inference]** from the record
above.

### Facts and records

Facts are one keyed integer row. `WorldIdentity.TrySetFact` refuses a row that
is not a keyed int row and refuses a new key past the row's capacity, and
otherwise writes **[opened]** (`src/Puck.World.Schema/WorldIdentity.cs:503-508`,
`:539-543`):

```csharp
        if (row is { } declared) {
            if (declared is not { Kind: CellKind.Int, IsKeyed: true }) {
                reason = $"state row '{definition.State}' is not a keyed int row";

                return false;
            }
```

```csharp
            if (cells.Count >= definition.Capacity) {
                reason = $"facts row '{definition.State}' holds {cells.Count} of {definition.Capacity} facts; '{key}' does not fit";

                return false;
            }
```

The write lands on the owned document, or on the travelling row of an identity
rebuilt from a projection **[opened]** (`src/Puck.World.Schema/WorldIdentity.cs:569-578`):

```csharp
    // The facts row lives on the owned document, or on the travelling row of an identity that arrived as a projection.
    private void WriteFacts(WorldStateRow row) {
        if (Document is null) {
            m_travelFacts = row;
            m_factsRevision++;

            return;
        }
```

The catalog's door, `WorldOwnedWorlds.TrySetFact`, is shared by rule effects
and the console, saves an owned identity, keeps a visitor's fact on its
travelling row, and refuses an owned document another catalog owns
**[opened]** (`src/Puck.World.Server/WorldOwnedWorlds.cs:1289-1293`):

```csharp
    /// <summary>Writes one fact on an identity's facts row, the one door a rule effect and the console share. An
    /// identity this catalog owns is persisted when the row changed, through the same save every other identity edit
    /// takes. A visitor — an identity another authority's traveler arrived as — keeps the fact on its travelling row,
    /// which its next crossing carries on and its own authority adopts when it comes home; nothing is saved here. An
    /// identity carrying an owned document this catalog does not own is refused by name and left unchanged.</summary>
```

Its two callers are the rule effect (`WorldRuleHost.PersistIdentityFact`,
`src/Puck.World.Server/WorldRuleHost.IdentityFacts.cs:214-230`) and the console
verb `identity.fact.set` (`src/Puck.World/IdentityCommandModule.cs:289-297`)
**[opened]**. The console verb writes only an identity the local catalog owns,
and a seat may write only its own identity **[opened]**
(`src/Puck.World/IdentityCommandModule.cs:267-275`):

```csharp
        if (
            (acting.Kind == PrincipalKind.Seat) &&
            (acting.Index != PlayerRoster.SlotFromDisplay(number: player))
        ) {
            return CommandResult.Error(output: $"[identity.fact.set: refused — {acting.Describe()} writes only its own identity's facts, not p{player}'s]");
        }
        if (m_worlds.FindById(id: identity!.Id) is not { } owned) {
            return CommandResult.Error(output: $"[identity.fact.set: p{player} world:{identity.Id} is not an owned world here]");
```

So on a visitor, the only writer of facts is a rule's `setIdentityFact` effect
**[inference]** from the two callers above.

Records are the identity's explicitly owned record pools
(`WorldIdentity.RecordState`, `src/Puck.World.Schema/WorldIdentity.Records.cs:12-24`)
**[opened]**. `WorldIdentity.TryWriteRecord` has one caller in `src/`, which
is `WorldIdentity.TryAdopt` (`src/Puck.World.Schema/WorldIdentity.Records.cs:141`;
a search for `TryWriteRecord(` over `src/` finds only its definition at `:70`
and that call) **[opened]**. No rule effect or verb writes an identity record
on a visitor today, although the architecture page says a destination's
"record write changes that copy" (`docs/architecture/worlds.md:795-796`)
**[inference]** from the search.

A destination reading a carried fact into its rule lane skips non-integers,
because the profile is foreign **[opened]**
(`src/Puck.World.Server/WorldRuleHost.IdentityFacts.cs:153-158`):

```csharp
        foreach (var carriedCell in carried) {
            // A profile arrives from another authority, so a fact that is not an integer is skipped rather than
            // read: the lane stores integers, and a foreign document is not trusted to agree.
            if (carriedCell.Value.Kind != CellKind.Int) {
                continue;
            }
```

### Adoption and `Decide`

`WorldIdentity.TryAdopt` writes every carried fact through `TrySetFact` and
every field of the owned record pools through `TryWriteRecord`, and records
the first refusal **[opened]** (`src/Puck.World.Schema/WorldIdentity.Records.cs:127-133`):

```csharp
        var refused = string.Empty;

        foreach (var cell in (carried.Facts?.Cells ?? [])) {
            if (!TrySetFact(key: cell.Key, value: cell.Value.AsInt, changed: out _, reason: out var factReason) && (refused.Length == 0)) {
                refused = $"fact '{cell.Key}': {factReason}";
            }
        }
```

`WorldOwnedWorlds.TryAdopt` refuses an identity the catalog does not own, then
adopts and saves (`src/Puck.World.Server/WorldOwnedWorlds.cs:1341-1364`)
**[opened]**. `WorldOwnedWorlds.Decide` is a different door: it gates a write
by another document into an owned identity's durable slot on a grant naming
that document, `Mutate` and the slot, plus a required write mask
**[opened]** (`src/Puck.World.Server/WorldOwnedWorlds.cs:307-322`):

```csharp
        var principal = Grantee.Document(id: submission.SourceDocumentId);
        var subject = GrantSubject.State(name: submission.Slot);
        var grant = document.Grants.FirstOrDefault(predicate: candidate =>
            ((candidate.Grantee == principal) &&
            (candidate.Capability == WorldCapability.Mutate) &&
            (candidate.Subject == subject)));
```

`Decide` refuses when the owner document is not in the local catalog
(`src/Puck.World.Server/WorldOwnedWorlds.cs:291-298`) **[opened]**, so a
durable-slot write reaches an identity only from a world in the same process
**[inference]**. Facts written by `setIdentityFact` never pass `Decide`
**[inference]** from the callers above.

### Where identity files live

The account directory is `<state root>/owned-worlds/`, one `<id>.world.json`
per identity (the suffix is `WorldDocumentName.DocumentSuffix`,
`src/Puck.World.Schema/WorldDocumentName.cs:30`) **[opened]**
(`src/Puck.World/WorldOwnedWorldRegistration.cs:16-17`):

```csharp
            var root = serviceProvider.GetRequiredService<WorldStateRoot>();
            var directory = root.PathOf(name: "owned-worlds");
```

The state root is `--state-dir`, or the `world` subdirectory of the per-user
Puck directory **[opened]** (`src/Puck.World.Server/WorldStateRoot.cs:7-9`):

```csharp
/// carries its own and two of them in one process never share one. The World executable resolves its root from
/// <c>--state-dir</c>, falling back to the <c>world</c> subdirectory of the per-user Puck directory
/// (<see cref="Puck.Abstractions.PuckUserDirectory"/>) only in its own composition root; a hosted silo takes its
```

Each non-boot instance has its own catalog directory under `instances/<name>/owned-worlds`
(`WorldInstanceHost.OwnedWorldsDirectory`, `src/Puck.World.Server/WorldInstanceHost.cs:472-483`)
**[opened]**. `WorldOwnedWorlds.TrySave` writes the file with no signature or
seal (`src/Puck.World.Server/WorldOwnedWorlds.cs:1126-1194`) **[opened]**.

### How they sync

`WorldOwnedWorldSync` pushes and pulls whole documents to a per-user blob
container, with a version token so a stale writer is refused **[opened]**
(`src/Puck.World.Server/WorldOwnedWorldSync.cs:16-19`):

```csharp
/// The owned-world cloud sync engine: pushes and pulls whole world documents against the per-user container, one blob
/// per world under <c>puck/worlds/</c>, carrying the storage version token so a stale writer is refused rather than
/// clobbering a newer copy (refuse-and-surface; a refusal names the remedy). First contact is fail-closed: a push with
/// no tracked token uses create-only, so a cloud copy this catalog has never seen is never overwritten. Tokens persist
```

A push re-saves the live identity first, writes with `if-match` or
create-only, and then publishes a counterpart claim when a publisher is
configured (`src/Puck.World.Server/WorldOwnedWorldSync.cs:671-747`)
**[opened]**. No class implements `ICounterpartPublisher` (the
`Puck.World.Azure` README says "no publisher implements the counterpart-publication seam",
`src/Puck.World.Azure/README.md:100-102`), so the push reports
`"no counterpart publisher configured"` (`src/Puck.World.Server/WorldOwnedWorldSync.cs:321-324`)
**[opened]**. The token protects the cloud copy against a stale push; nothing
in the sync checks what the document says **[inference]**.

## 2. Write-back and the travelling projection

### What a destination may write onto a visitor

A visitor's identity at a destination is rebuilt from its projection with no
owned document (`WorldIdentity.FromProjection`, `src/Puck.World.Schema/WorldIdentity.cs:208-217`)
**[opened]**. The `setIdentityFact` effect writes the body's rule lane and the
identity's facts row together **[opened]**
(`src/Puck.World.Schema/WorldActionVocabulary.cs:144-147`):

```csharp
    /// <summary>Writes one fact on the identity a world-addressed body drives under: the body's cell in the world's
    /// reserved <see cref="WorldIdentityFactLane"/> row and the identity's facts row, together. An owned identity's row
    /// is persisted; a visitor's is its travelling row, which its own authority adopts when it comes home. Exactly
    /// one of <paramref name="Value"/> and <paramref name="Expression"/> is authored; a body driving under no
```

The name, color and rate setters change the in-memory identity, and its owned
document when it has one (`WorldIdentity.SetIdentity`, `SetMoveSpeed`,
`SetTurnSpeed`, `src/Puck.World.Schema/WorldIdentity.cs:347-391`)
**[opened]**. Their reachable callers do not touch a visitor: `identity.motion`
refuses an identity the local catalog does not own
(`src/Puck.World/IdentityCommandModule.cs:389-394`), the session request
`SetIdentity` selects a local seat's profile from the local catalog
(`src/Puck.World.Server/WorldGrants.Admission.cs:665-668`), and a search for
`.SetIdentity(` over `src/` finds no other call **[opened]**. So a destination
can change a visitor's facts, and nothing else of its identity, through the
engine's doors **[inference]**. Facts carry no record of which world wrote them
**[inference]** from the projection record in section 1.

### What home adopts, and when

Home adopts only a colocated seat of its own coming home. `WorldTransferEscrow.AdoptHomeProfiles`
asks `HomeIdentity` for each traveller (`src/Puck.World.Server/WorldTransferEscrow.cs:1357-1366`),
and `HomeIdentity` answers only for a non-peer arrival **[opened]**
(`src/Puck.World.Server/WorldTransferEscrow.cs:1428-1440`):

```csharp
    // The owned identity a local seat of this authority left with, when traveler `index` is that seat coming home: a
    // colocated arrival of this process's own local seats (a remote one admits peers, and a remote incarnation claim is
    // unauthenticated) at the seat this authority minted it at (WorldServer.HomeSeatIdentity).
    private WorldIdentity? HomeIdentity(WorldCrossingArrival arrival, int index, string id) => ((
        !arrival.Request.PeerAdmission &&
        (arrival.Request.Members[index].Mobility is { } mobility)
    )
```

Every reservation decoded from the federation wire is a peer admission
**[opened]** (`src/Puck.World.Server/WorldFederationCodec.cs:1357-1363`,
`:679-681`):

```csharp
        request = new WorldTransferReservationRequest(
            Border: border,
            BorderCapacity: capacity,
            DeadlineSourceTick: deadline,
            Members: members,
            PartyAllOrNothing: party,
            PeerAdmission: true,
```

`WorldServer.HomeSeatIdentity` further requires a local seat slot whose
incarnation this authority minted at that slot
(`src/Puck.World.Server/WorldServer.Crossings.cs:43-58`) **[opened]**. The
architecture page states the consequence **[opened]**
(`docs/architecture/worlds.md:804-806`):

```text
Remote round trips do not adopt: a remote incarnation claim is unauthenticated, and a federation
reservation has no field that could ask for a local seat, so it is always a peer admission. Remote
home adoption remains deferred. Checkpoint restore rebinds a local home seat to its restored owned
```

A silo hosts no local seats (`WorldInstanceHost` built with
`seats: WorldEmbodiedSeats.None`, `src/Puck.World.Silo/WorldSiloHost.cs:164-170`)
**[opened]**, so a silo never adopts **[inference]**. Adoption runs on a
desktop, between its boot instance and the instances it spawns, after every
landing succeeds and the arrival record is durable
(`src/Puck.World.Server/WorldTransferEscrow.cs:1349-1352`) **[opened]**.
Recovery also applies a logged departure's facts and records to the owned
identity before live writes resume
(`src/Puck.World.Server/WorldInstanceHost.CrossingLog.cs:260-281`)
**[opened]**.

Consequences **[inference]**: a federated destination's writes on a visitor
live and die with the remote copy; a player who crosses to another machine and
back arrives home as a peer, not at the seat it left; and the writes that reach
the identity file are the player's own edits and writes by worlds in the same
process.

## 3. Attestation

### What `Puck.Attestation` provides

The library signs and verifies claims and key bindings over
`(domain, subject, algorithm, keyHash)`, with audience, an optional sequence
whose replay commit the receiver must transact, a signed validity window,
two-hop domain chains and sealed payloads (`docs/reference/attestation.md:246-282`)
**[opened]**. It never fetches material **[opened]**
(`docs/reference/attestation.md:261-262`):

```text
Attestation performs no path discovery and fetches no missing certificates or
bindings.
```

A search for `revok`, `revoc`, `denylist`, `deny list`, `refusal list` and
`blocklist` in `src/Puck.Attestation` and `docs/reference/attestation.md` finds
nothing **[opened]**.

### What world code signs and verifies

Signers in world code: `LocalKeySigningOracle` (the only `ISigningOracle`
implementation in `src/`) signs the federation identity claim, and
`PeerIdentity.SignClaim` signs the transport handshake
(`src/Puck.World.Protocol/Codecs/ISigningOracle.cs:27`, `src/Puck.Networking/Peers/PeerIdentity.cs:145`)
**[opened]**. The interface describes a remote-issuer shape that nothing in
`src/` implements **[opened]** (`src/Puck.World.Protocol/Codecs/ISigningOracle.cs:7-12`):

```csharp
/// Signs a federation-identity claim over a challenge nonce. <see cref="WorldAttestedAuthenticator"/> never
/// distinguishes which shape produced the bytes it returns — a locally held per-authority key
/// (<see cref="LocalKeySigningOracle"/>, a <see cref="AttestationTrustMode.SignsDirectly"/> claim with no chain) or
/// a remote issuer consulted per connection attempt (a <see cref="AttestationTrustMode.Vouches"/> claim with a
/// root-issuing-subject chain, obtained from the platform at connect time) both hand back the same wrapped
/// claim+chain envelope (<see cref="AttestationChainEnvelope"/>).
```

Verifiers in world code, from a search for `VerifyChain(` outside
`src/Puck.Attestation` **[opened]**:

| Verifier | Purpose | Where |
|---|---|---|
| `WorldAttestedAuthenticator.TryVerify` | federation identity proof | `src/Puck.World.Protocol/Codecs/WorldAttestedAuthenticator.cs:79-150` |
| `WorldAdmissionDoor.TryAdmit` | interactive peer admission (`puck.world.tcp-admission`) | `src/Puck.World.Schema/WorldAdmissionDoor.cs:115-196` |
| `WorldCounterpartAttestationProtocol.TryVerify` | a counterpart's border terms | `src/Puck.World.Schema/WorldCounterpartAttestation.cs:232-321` |
| `PeerAuthenticator`, `PeerLink` | transport peer identity | `src/Puck.Networking/Peers/PeerAuthenticator.cs:97`, `src/Puck.Networking/Peers/PeerLink.cs:275` |

`WorldAdmissionDoor.TryAdmit` verifies a client claim, but no code in `src/`
writes one: `HandshakeWireFormat.WriteHelloIdentityAsync` has no caller outside
its definition (`src/Puck.Networking/HandshakeWireFormat.cs:240`), and the
purpose string `puck.world.tcp-admission` appears only at its declaration
(`src/Puck.World.Schema/WorldAdmissionDoor.cs:401`) **[opened]**. No fact,
record, projection or arrival is signed anywhere **[inference]** from the
signer list.

The federation proof's subject becomes the source namespace, and the claim's
payload must equal the bound challenge **[opened]**
(`src/Puck.World.Protocol/Codecs/WorldAttestedAuthenticator.cs:140-147`):

```csharp
        if (
            (claim.PayloadKind != AttestationPayloadKind.Opaque) ||
            !challenge.SequenceEqual(other: claim.PayloadBytes.Span)
        ) {
            return false;
        }

        sourceAuthority = claim.Header.Subject;
```

The challenge binds both nonces, both transport key hashes, the direction and
the destination namespace (`WorldFederationCodec.BindProofChallenge`,
`src/Puck.World.Server/WorldFederationCodec.Authority.cs:10-21`) **[opened]**.

### Keys and lifetimes on each path

**World authorities.** A silo row reads a PKCS8 key from `federation.keyFile`
and signs `SignsDirectly` claims valid for `MaximumClaimAge`, five minutes
(`src/Puck.World.Protocol/Codecs/WorldAttestedAuthenticator.cs:36-38`)
**[opened]** (`src/Puck.World.Silo/WorldSiloHost.cs:804-831`):

```csharp
                : new LocalKeySigningOracle(
                                key: key,
                                subject: subject,
                                validity: WorldAttestedAuthenticator.MaximumClaimAge
```

The desktop does the same from `--federation-key-file` **[opened]**
(`src/Puck.World/Program.cs:177-180`):

```csharp
    var federationKeyFileOption = new Option<string?>(name: "--federation-key-file") {
        DefaultValueFactory = static _ => null,
        Description = "A deployment-secret file holding this authority's own PKCS8 ECDSA P-256 private key (raw DER bytes) — the SignsDirectly signing identity peers pin against this world's host.authority (or its \"boot\" instance identity when host.authority is absent). Absent disables federation while leaving ordinary admitted-peer listening available.",
    };
```

There is no issuance service for these keys: the operator makes the file and
peers pin its key in their `admission` rows **[inference]** from the two
sites above.

**API users on the Azure path.** `EntraWorldAuthenticator` sends the user's
Entra access token as the proof **[opened]**
(`src/Puck.World.Azure/EntraWorldAuthenticator.cs:137-141`):

```csharp
    public byte[] Prove(ReadOnlySpan<byte> challenge) {
        if (m_credential is null) { return (m_federation?.Prove(challenge: challenge) ?? throw new InvalidOperationException(message: "No signing credential is installed.")); }
        if (challenge.Length != ChallengeBytes) { throw new ArgumentException(message: "Invalid federation challenge."); }
        return Encoding.UTF8.GetBytes(s: (((((Prefix + Convert.ToBase64String(challenge)) + "\n") + m_session) + "\n") + ReadClientToken()));
    }
```

The server validates issuer, audience, signature and lifetime with no clock
skew, requires the configured tenant, group and scope, and names the source
`"{oid}/{session}"` **[opened]** (`src/Puck.World.Azure/EntraWorldAuthenticator.cs:202-221`):

```csharp
            if (
                (identity.FindAll(type: "tid").SingleOrDefault()?.Value != m_tenant) ||
                !identity.FindAll(type: "groups").Any(predicate: claim => string.Equals(
                a: claim.Value,
                b: m_group,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) ||
```

```csharp
            sourceAuthority = $"{oid:D}/{session:N}";
```

The token is echoed beside the challenge, not signed over it, so the proof is
a bearer credential for the token's lifetime **[inference]** from the
`Prove` concatenation. The client caches the token until two minutes before
expiry (`src/Puck.World.Azure/EntraWorldAuthenticator.cs:85`), and the session
segment is a per-process GUID the client mints (`:31`) **[opened]**. The token
lifetime is Entra's policy and is not in this repository **[unverified]**. The
deployed silo selects this provider with one group **[opened]**
(`src/Puck.Azure.Resources/main.bicepparam:597-605`):

```text
  worldSilo: {
    authentication: {
      type: 'azure.api-users'
      settings: {
        tenantId: 'e09734be-ca09-41ec-b70d-98f5536fb774'
        audience: 'e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7'
        groupId: '6997d638-98e6-4738-a507-7d960bc1e537' // ByteTerrace API Users are Puck users.
        scope: 'user_impersonation'
```

**Platform key bindings.** Onboarding generates a user key pair on the server
(`DefaultUserProvisioningService`, `src/Puck.Actors/Services/DefaultUserProvisioningService.cs:310-320`)
and stores the private key encrypted in the user's container with a protected
password in blob metadata (`src/Puck.Actors/Services/DefaultKeyPairService.cs:33-50`)
**[opened]**. `DefaultBindingService` mints and re-attests a subject key
binding with a validity window that defaults to 30 days **[opened]**
(`src/Puck.Azure.Functions/Services/IssuingKeyOptions.cs:27-28`):

```csharp
    /// <summary>The validity window authored onto every subject key binding this service mints or re-attests.</summary>
    public TimeSpan SubjectBindingValidity { get; set; } = TimeSpan.FromDays(value: 30);
```

The trigger that calls it, `AttestKeys`, is feature-gated and not yet wired
into onboarding **[opened]** (`src/Puck.Azure.Functions/HttpTriggers/AttestKeys.cs:24-26`,
`:34`):

```csharp
/// This trigger only re-attests keys the platform already minted and stored for the caller; it
/// does not mint key pairs itself (<c>GenerateKeyPair</c> does that). Onboarding does not
/// call this yet — see the report for the one obvious call that would wire it in.
```

The committed App Configuration file lists feature flags `FeatureCheck`,
`GenerateBlobSasUri`, `HealthCheck` and `SelfOnboard`, and no `AttestKeys`
(`src/Puck.Azure.Functions/configuration.json:5-26`) **[opened]**; whether the
deployed configuration enables it is outside the repository **[unverified]**.
No world code presents a binding chain **[inference]** from the signer list.

**Counterpart claims.** `WorldApiCounterpartResolver` fetches an owner's
published claim, verifies it against the reader's admission rows with a
one-hour age ceiling, and requires the verified subject to equal the
reference's owner (`src/Puck.World.Azure/WorldApiCounterpartResolver.cs:31-34`,
`:225-251`) **[opened]**. No shipped host constructs it
(`src/Puck.World.Azure/README.md:107-108`) **[opened]**.

### Trust lists and re-verification

The admission door's trust list sets no per-entry, root-binding or
subject-binding age ceiling and no replay horizon **[opened]**
(`src/Puck.World.Schema/WorldAdmissionDoor.cs:86-97`):

```csharp
                Reach: NoReach,
                MaximumAge: null
            ));
        }

        return new TrustList(
            defaultMaximumAge: defaultMaximumAge,
            defaultRootBindingMaximumAge: null,
            defaultSubjectBindingMaximumAge: null,
            entries: list,
            replayAcceptanceHorizon: null
        );
```

A live connection is re-verified when the clock second or the admission
policy changes, swept every second, and evicted for good when it fails
**[opened]** (`src/Puck.World.Server/WorldPeerHost.cs:2059-2061`, `:58`):

```csharp
    // Verification is repeated only when its clock second or immutable policy changes. This preserves the
    // attestation verifier's Unix-second windows without doing signature work for every simulation tick.
    // Once a credential fails, a later re-grant cannot revive its already-evicted session.
```

For a federation connection the re-check replays the original proof and also
requires the namespace's arrival row to persist
(`src/Puck.World.Server/WorldPeerHost.cs:476-489`) **[opened]**. A live
connection therefore ends when its claim or token expires, or within a second
of an admission edit that no longer admits it **[inference]**.

## 4. Adjacency and crossing

### The contact field and the settlement

`WorldCrossAuthoritySettlement.LocalResponds` picks one responder per pair and
interaction by an FNV-1a hash of both entity addresses **[opened]**
(`src/Puck.World.Server/WorldCrossAuthoritySettlement.cs:6-9`):

```csharp
/// <summary>Deterministically selects one entity owner to settle a cross-authority interaction. Both authorities
/// evaluate the same canonical address pair and interaction name, so there is exactly one responder without a host
/// leader, packet-order race, or topology preference. Entity generations make the choice sticky only for the
/// lifetime of those concrete occupants.</summary>
```

Its one caller is the adjacency contact field, for `"physical-contact"`
(`src/Puck.World.Server/WorldAdjacencyContactField.cs:106-110`; a search for
`LocalResponds(` over `src/` finds only that call and the definition)
**[opened]**. The field corrects the local body out of each solid neighbour
body the pair hash assigns to it, adding the corrections one after another
**[opened]** (`src/Puck.World.Server/WorldAdjacencyContactField.cs:121-134`):

```csharp
                if (!FixedDynamicBodyContacts.TryCorrection(
                    leftPosition: neighbourPosition,
                    leftOrientation: neighbourOrientation,
                    leftVolumes: volumes,
                    rightPosition: remotePosition,
                    rightOrientation: FixedQuaternion.FromQuaternion(value: neighbour.CurrentOrientation(index: entity)).Normalize(),
                    rightVolumes: neighbourCollider.Volumes,
                    tieBreaker: entity,
                    correction: out var correction
                )) {
                    continue;
                }

                neighbourPosition += correction;
```

Neighbour colliders are compiled from the neighbour's delivered definition,
and positions come from its snapshots (`src/Puck.World.Protocol/Protocol/WorldSessionMirror.cs:479-489`,
`:380-388`) **[opened]**. The field's own remarks name the input as outside
replay determinism **[opened]** (`src/Puck.World.Server/WorldAdjacencyContactField.cs:25-29`):

```csharp
/// <para><b>Replay boundary.</b> Neighbour poses presently arrive as presentation-float snapshot fields and are
/// converted to fixed point below at delivery timing. Ground and dynamic-contact correction are therefore not yet
/// replay-deterministic across network schedules. Track 3 replaces this input with tick-addressed taped neighbour
/// records; until that transport exists, this conversion is the named nondeterministic boundary rather than an
/// implied simulation guarantee.</para>
```

Interactions (`WorldInteraction`) are rows one authority evaluates over its own
carriers (`src/Puck.World.Schema/WorldInteractions.cs:57-66`) **[opened]**; no
interaction other than contact has a cross-authority rule **[inference]**.

### Transfer escrow, leases, tombstones and consumed epochs

The escrow's fences are the architecture page's rules
(`docs/architecture/worlds.md:1189-1230`) **[opened]**: one commit per
ownership epoch, a lease deadline, a fenced log, a key leased at most once,
`Missing` final behind a tombstone, outcomes kept until the source cannot
ask, and a bounded ledger. The code paths the catalogue cites hold at this
tree: the consumed-epoch refusal (`src/Puck.World.Server/WorldTransferEscrow.cs:2104-2112`),
the admission map keyed by source and incarnation (`:2452-2465`), and the
limits (`src/Puck.World.Server/WorldTransferLimits.cs:6`, `:12`, `:17`, `:21`)
**[opened]**. The escrow section of a checkpoint carries leases, commits,
mobility leases, mobility admissions, spent keys, floors and custody
(`src/Puck.World.Server/WorldAuthorityCheckpointCodec.Escrow.cs:273`, `:294`,
`:308`, `:331`, `:342`, `:349`) **[opened]**.

A titled incarnation without its companion leaf is refused
(`src/Puck.World.Server/WorldTransferEscrow.cs:2093-2097`) **[opened]**. A
search for a comparison of `Incarnation.Authority` with `SourceAuthority` in
`src/Puck.World.Server` finds none, so a source names its travellers'
incarnations itself **[inference]** from the search.

### The crossing log and which hosts install one

The log interface **[opened]** (`src/Puck.World.Server/WorldCrossingRecord.cs:171-174`):

```csharp
/// <summary>The durable log an authority writes its crossing records ahead through. An implementation answers only
/// once it knows what became of the record, and refuses an append from an activation its store has fenced off, so a
/// superseded authority can never claim a step its successor will not recover. An authority with no log installed
/// records nothing and keeps no crossing beyond its own process.</summary>
public interface IWorldCrossingLog {
```

`WorldServer.InstallCrossingLog` has one caller, the silo, which installs a
`RowCrossingLog` per activation (`src/Puck.World.Silo/WorldSiloHost.cs:1474-1479`)
**[opened]**. Its append blocks until the store answers
(`src/Puck.World.Silo/WorldSiloHost.Crossings.cs:24-37`) **[opened]**. The
desktop installs none (`docs/architecture/worlds.md:1295-1296`) **[opened]**.

## 5. Federation

### The identity proof and namespaces

Section 3 covers the proof. A `SignsDirectly` claim's subject is the row's
`host.authorityNamespace` or the world id on a silo
(`WorldSiloHost.FederationSubject`, `src/Puck.World.Silo/WorldSiloHost.cs:854-855`),
and an Entra user's is `"{oid}/{session}"` (section 3) **[opened]**. The
namespace is the only verified fact about an arrival **[opened]**
(`src/Puck.World.Schema/WorldAdmission.cs:24-26`):

```csharp
    /// trust list. An arriving traveler's body index, profile id, and display name are all supplied by the handing
    /// authority; which authority is speaking is the only verified fact, so it is the only one trust is authored
    /// against.</summary>
```

### Admission rows

Four modes exist: `SignsDirectly`, `Vouches`, `FederatedAuthority` and `OAuth`
(`src/Puck.World.Schema/WorldAdmission.cs:9-32`) **[opened]**. `OAuth` rows
are matched by the silo's control sessions only
(`WorldAdmissionDoor.TryMatchOAuthEntry`, called from `src/Puck.World.Silo/SiloControlSessionHost.cs:16`)
**[opened]**. `WorldAdmissionDoor.TryAdmitArrival` maps an authenticated
namespace to grants by an exact row, else the `*` row
(`src/Puck.World.Schema/WorldAdmissionDoor.cs:206-260`) **[opened]**. Because
the Entra session segment is minted per process, only a `*` row can admit an
API user's arrivals **[inference]**.

The deployed debug world admits every authenticated authority, with the whole
document **[opened]** (`worlds/debug/debug.puck:80-97`):

```text
admission [
  {
    disclosure: Replica
    domain: "*"
    grants [
      {
        budget: 8
        capability: Drive
        exclusive: true
      }
      {
        budget: 64
        capability: Observe
        subject: "all"
      }
    ]
    mode: FederatedAuthority
  }
]
```

The garden and the night field author the same row
(`worlds/debug/garden.puck:34-51`, `worlds/debug/night.puck:28-45`)
**[opened]**. With the deployed provider, every member of the configured
Entra group who connects is such an authority **[inference]**.

### Membership proof on the wire

No federation codec, reservation member or admission path carries a group
membership claim: a search for `membership` over the federation codec, the
peer host, the admission door and the escrow finds only unrelated comments
**[opened]**. The resolver says group scope resolves against local seats only
**[opened]** (`src/Puck.World.Schema/WorldSessionResolver.cs:671-673`):

```csharp
    /// <param name="sourceDefinition">The cohort's own world document — group membership resolves against its
    /// <c>groups</c> section (local seats only; docs/architecture/worlds.md names remote/foreign membership proof as an
    /// open gap).</param>
```

The one group check on any wire is the Entra `groups` claim against the single
deployment group (section 3) **[opened]**; it gates connection, not a world's
`groups` **[inference]**.

## 6. The companion custody protocol

### Which build-plan steps are in the code

The plan says steps 1 to 5 are built **[opened]**
(`docs/plans/companion-redesign.md:551-554`):

```text
Steps 1 to 5 are built. Durable record kinds for later steps already exist in
`WorldCrossingRecord.cs`; their presence does not complete the transitions
below. The remaining build work is steps 6 to 10, using the existing model and
real authority fixtures as evidence:
```

The code agrees. Hops, hop queries and per-member verdicts exist: the escrow
refuses a companion `Unverified` when home did not confirm a current grant
(`src/Puck.World.Server/WorldTransferEscrow.cs:1896-1927`), the return door
checks custody through the escrow's custody reducer (`:1978-1979`), and
`WorldCompanionCustody.VerifyReturn` answers `NotMyTitle`, `Stale`, `Released`
or `Pending` (`src/Puck.World.Server/WorldCompanionCustody.cs:552-576`)
**[opened]**. `Recall`, `Noticed`, `Release`, `Retire`, `Stranded` and
`GraceElapsed` records are declared (`src/Puck.World.Server/WorldCrossingRecord.cs:127-150`)
and folded by the reducer (`src/Puck.World.Server/WorldCompanionCustody.cs:274-311`),
but the only `new WorldCrossingRecord.Recall(`, `Release(`, `Stranded(`,
`GraceElapsed(`, `Noticed(` and `Retire(` in `src/` are the codec's decoders
(`src/Puck.World.Server/WorldAuthorityCheckpointCodec.Crossings.cs:573-626`)
**[opened]**. Nothing mints them, so steps 6 (separation, stranding, shed,
retire) and 7 (recall and release) are not in the code **[inference]**. Step
8's explicit stop is built (`WorldInstanceHost.Stop`, section 8); its reaping
widening is not checked here. Step 9's `worlds/standard/` row declaring
`accept` is in the tree (`worlds/standard/companions.puck:3-8`) **[opened]**;
`TryRoleVerdict` is not (a search over `src/` finds no match) **[opened]**.
Step 10, the debug world's faerie trip, is not checked here **[unverified]**.

### Branch `lane/companion-v1`

`git log --oneline lead/velocity..lane/companion-v1` lists eight commits, three
of them merges of the integration line of that time, branched from an earlier
tip of the integration line **[opened]**. Its two substantive commits build role
bindings, role-derived driver grants (`TryRoleVerdict` in `WorldServer.Roles.cs`), visiting
drivers mounted per pin under `accept`, an HMAC custody token for re-adoption,
and a split of the debug world's faerie into `companion-body.puck`
**[opened]** (the commit messages and `git diff --stat` of the merge base
against the branch tip).

Compared with this tree **[opened]**:

- Landed by another route: `WorldRole`, `roles.schema.json`,
  `WorldDefinitionValidator.Roles` and `WorldInstanceHost.Companions` exist
  here, from the protocol build's step 2 commit, in a smaller form (this
  tree's `WorldRole` has `Name`, `Driver`, `Accept`, `Kit`, `Look`,
  `src/Puck.World.Schema/WorldRole.cs:15-21`; `git diff --stat lane/companion-v1 lead/velocity`
  over those three schema files shows 18 insertions and 90 deletions).
- Superseded: the branch's custody design (an HMAC custody token, a
  separation sweep, a stranded companion waiting for its home) is replaced by
  title, hop and epoch records and the verification door above.
- Unlanded: `WorldServer.Roles.cs` (`TryRoleVerdict`),
  `WorldPopulation.Companions.cs`, `WorldTransferEscrow.Companions.cs`,
  `CompanionTrustLawTests`, `CompanionRoleLawTests` and
  `worlds/debug/companion-body.puck` do not exist in this tree.

The repository already records this verdict **[opened]**
(`docs/plans/open-items.md:951-958`):

```text
- [low] Branch `lane/companion-v1` on `origin` (companions v1) is superseded
  for custody and trust by the protocol build, but holds step 9 and 10 prior art
  no other branch has: `WorldServer.Roles.cs` (`TryRoleVerdict`, visiting
  drivers mounted per pin under `accept`), the `WorldRole` driver, accept, fuel
  and budget fields, `CompanionTrustLawTests`' driver laws, and the debug world's
  faerie split (`companion-body.puck`, role rows in `night` and `garden`). Step 9
  ports them onto the custody bindings (`WorldCompanionCustody.TryBinding`); keep
  the branch until then.
```

## 7. Disclosure

### What filters a recipient's view

Three filters exist **[opened]**:

- The admission tier, decided once per verified row: `Frames`, `Presentation`
  (the default) or `Replica` (`src/Puck.World.Schema/WorldAdmission.cs:37-47`).
- The per-observer body policy `population.disclosure`, `All` (default),
  `Radius` or `SelfOnly`, applied at the output hub's sink boundary
  (`src/Puck.World.Schema/WorldObserverDisclosure.cs:8-22`).
- State visibility: `WorldStateDisclosure` composes a recipient's rows, and
  `WorldStateReadView` is the console's per-principal read
  (`src/Puck.World.Schema/WorldStateDisclosure.cs:57-61`,
  `src/Puck.World.Server/WorldStateReadView.cs:5-10`).

The disclosure plan's own inventory says the chokepoint is partial and every
delivery sink discloses by hand (`docs/plans/disclosure.md:80-150`)
**[opened]**. `WorldRecipientView` does not exist (a search over `src/` finds
none) **[opened]**.

### What the authority sees

Everything. The operator reads the live document whole **[opened]**
(`src/Puck.World.Server/WorldStateReadView.cs:5-9`):

```csharp
/// <summary>One principal's read of a live world's state: the document as <see cref="WorldStateDisclosure"/>
/// discloses it to that principal, and the refusal a read of anything withheld answers with.
/// <para>A console read-back verb reads state through this view rather than through <see cref="WorldServer.Definition"/>,
/// so a seat, an addon or a peer is shown what its visibility admits and nothing else. The operator — the
/// <see cref="PrincipalKind.Console"/> principal — reads the live document whole.</para></summary>
```

Local and colocated sinks take every body **[opened]**
(`src/Puck.World.Server/WorldOutputHub.cs:11-15`):

```csharp
public readonly record struct WorldSinkDisclosure(WorldObserverDisclosure Policy, int ObserverBodyIndex) {
    /// <summary>Gets the unfiltered delivery — every active body, with no per-sink copy taken at all. Local and
    /// colocated sinks attach with this: colocated trust is home trust.</summary>
    public static WorldSinkDisclosure Full { get; } = new(
        Policy: WorldObserverDisclosure.Default,
```

The plan states the engine's C# is trusted (`docs/plans/disclosure.md:56-58`)
and keeps local presentation unfiltered (`docs/plans/disclosure.md:182-188`)
**[opened]**. A `Replica` row hands its whole document, verbatim, to the
peer, and the deployed debug world authors one for `*` (section 5)
**[opened]**.

## 8. Durable images and what persists

### `WorldInstanceHost.DurableImages`

The property is declared and read, never assigned **[opened]**
(`src/Puck.World.Server/WorldInstanceHost.Stop.cs:29-33`; a search for
`DurableImages` over `src/**/*.cs` finds only this file):

```csharp
    /// <summary>Gets or sets where an explicit stop writes a row's image: once when the stop is accepted, so a restart
    /// resumes it, and once more as the final image before the row retires. <see langword="null"/> for a host whose rows
    /// keep no durable image: a stop there never retires a row that still holds custody. It refuses what only another
    /// authority ends and waits for what its drains settle.</summary>
    public WorldDurableImageWriter? DurableImages { get; set; }
```

Without it, a stop refuses a row holding a title abroad
(`src/Puck.World.Server/WorldInstanceHost.Stop.cs:121-124`) **[opened]**. A
killed process takes no stop path at all **[inference]**.

### The silo's checkpoints

A silo captures each row every minute of engine time, and at once on
`silo.checkpoint`, retirement or shutdown **[opened]**
(`src/Puck.World.Server/WorldAuthorityCheckpointCadence.cs:3-7`, `:12-16`):

```csharp
/// <summary>The cadence a hosted row's checkpoint is captured on — a representation/bookkeeping constant, never a
/// feel value: one minute of engine time, independent of any row's own simulation rate.</summary>
public static class WorldAuthorityCheckpointCadence {
    /// <summary>The number of engine ticks between cadence-armed capture requests (one minute).</summary>
    public const ulong EngineTicks = (60UL * Puck.Maths.FixedTickConversion.TicksPerSecond);
```

Between checkpoints, mutations are journaled asynchronously with a bounded lag
(`src/Puck.World.Silo/README.md:313-315`), while crossing records are
appended synchronously (section 4) **[opened]**. A crash can therefore lose
recent mutations but no crossing step a peer saw **[inference]**.

### What a desktop host persists

From the consumers of `WorldStateRoot.PathOf` **[opened]**: `owned-worlds/`
(identities; `src/Puck.World/WorldOwnedWorldRegistration.cs:17`),
`instances/<name>/owned-worlds` (`src/Puck.World.Server/WorldInstanceHost.cs:472-483`),
`Replays` (`src/Puck.World.Server/WorldReplayTape.cs:294`), `HistoryShadows`
(`src/Puck.World.Server/WorldHistory.Shadow.cs:75`), `machine.id`, `screenshots`,
`extensions`, `updates` and pipeline caches. The world document is written
only by `world.save` or a `save` effect, through `WorldServer.SaveEffectTap`
(`src/Puck.World.Server/WorldServer.cs:418-431`) **[opened]**. No crossing
log, escrow section or authority checkpoint is written to disk on a desktop
**[inference]** from sections 4 and 8.

## 9. Draws and seeds

### Seeding

A draw site's seed folds the document seed, the running instance's identity
and the site descriptor **[opened]**
(`src/Puck.State.Generators/GeneratorEngine.Streams.cs:5-10`):

```csharp
/// <summary>A draw site's seed-ladder fold and stream id: everything a draw of the site seeks from besides its cursor.
/// Both depend only on the document seed, the running instance's identity and the site descriptor, so a host computes
/// the pair once per site and every draw it makes afterwards costs the same whatever those strings hold.</summary>
/// <param name="State">The <c>Pcg32XshRr</c> starting state (see <see cref="GeneratorEngine.ComputeSeedState"/>).</param>
/// <param name="Stream">The <c>Pcg32XshRr</c> stream id (see <see cref="GeneratorEngine.ComputeStreamId"/>).</param>
public readonly record struct DrawSeed(ulong State, ulong Stream);
```

The document seed is `generation.worldSeed`
(`src/Puck.World.Schema/WorldDraw.cs:9-11`; read as
`definition.Generation?.WorldSeed` at `src/Puck.World.Server/WorldServer.Arena.cs:264`)
**[opened]**.

### Storage and advance

The position lives on the site as `StateRow.DrawCursor`, advanced by the raw
words each draw consumed (`src/Puck.State/Draw.cs:39-43`,
`src/Puck.World.Server/WorldDocument.Compose.Generate.cs:106`, `:222`)
**[opened]**. A `Boot` site draws once; `TickPeriod` and `Event` sites redraw
through the `generate` effect or mutation (`src/Puck.State/Draw.cs:7-28`)
**[opened]**. The cursor persists wherever the document does: in a silo
checkpoint, or on a desktop only through `world.save` **[inference]** from
section 8.

### Private draws

A site may carry an authority-provisioned secret; each sample is then an
HMAC of the cursor under that key **[opened]**
(`src/Puck.State/Draw.cs:53`, `src/Puck.State.Generators/PrivateDraw.cs:6-8`):

```csharp
/// <param name="Secret">An authority-provisioned 256-bit secret for an independently keyed streamDraw sample at each cursor. Never sent in observations.</param>
```

```csharp
/// <summary>Deterministic private stream samples. The authority provisions the key before simulation; simulation
/// consumes no system entropy. Cursor addressing preserves constant-time resume.</summary>
internal static class PrivateDraw {
```

Restricted readers have cursor metadata zeroed
(`WorldStateDisclosure.WithoutStorageMetadata`, `src/Puck.World.Schema/WorldStateDisclosure.cs:458-465`)
**[opened]**. The code site that strips `Secret` from observations was not
found by a search for `.Secret` over the world and state projects
**[unverified]**. A `Replica` tier sends the document verbatim, which would
include an authored secret **[inference]** from `WorldDisclosureTier.Replica`.

### Who can change them

The seed, the secret and the cursor are document data, so whoever can change
the authority's document can change them: the operator through its console
and file, and any principal whose grants admit the mutation **[inference]**.
No participant contributes entropy to any seed **[inference]** from the seed
fold above.

## Premise verdicts

### Attack question 7

**A13, the generation-counter consequence: refuted for this tree's hosts, with
a latent hazard.** The counter is process memory (`src/Puck.World.Schema/WorldSessionResolver.cs:75`),
and `TryAdopt` mints from it (`:577`) **[opened]**. Nothing restores the
resolver's map. But **[opened]**:

- A continuation's generation id is narration only, never re-verified
  (`src/Puck.World.Server/WorldInstanceHost.Transfers.cs:2331-2334`):

  ```csharp
    /// <param name="FrozenGenerationId">The resolver-issued generation id the resolve produced — carried purely for
    /// <see cref="ApplyTransfer"/>'s own tape narration (<see cref="WorldReplayTape.NoteTransfer"/>); never
    /// re-verified (the scope-key re-derivation above is what proves the resolution still holds).
  ```

- Recovery binds an in-doubt transfer to a destination by authority identity,
  and never re-resolves (`src/Puck.World.Server/WorldInstanceHost.TransferRecovery.cs:21-32`,
  `:273-274`).
- The silo restores rows but cannot spawn one
  (`src/Puck.World.Server/WorldInstanceHost.cs:1143-1146`,
  `src/Puck.World.Silo/WorldSiloHost.cs:164-165`); the desktop spawns
  resolver instances but restores none (section 8).

So no restart today can send a reservation to a different ephemeral world than
the one displayed **[inference]**. The hazard is latent: ephemeral instance
names embed the generation and are distinct only within one process
(`src/Puck.World.Schema/WorldSessionResolver.cs:201-205`), and a running
instance is reused by name when its document matches
(`src/Puck.World.Server/WorldInstanceHost.TransferResolution.cs:367-405`)
**[opened]**. A desktop that restores images and spawns ephemeral instances
would let a fresh generation resolve to a restored old instance of the same
name **[inference]**.

**A10, a per-tick bound on seam correction: confirmed absent.** The field adds
one `TryCorrection` per solid neighbour body the pair hash assigns locally,
with no cap (`src/Puck.World.Server/WorldAdjacencyContactField.cs:100-145`)
**[opened]**. Each correction is the deepest volume overlap between the local
collider and the neighbour's (`src/Puck.Physics/FixedDynamicBodyContacts.cs:282-322`)
**[opened]**, so it is bounded by the two colliders' sizes, and the
neighbour's collider shapes come from the definition the neighbour delivers
**[inference]** from `WorldSessionMirror`. The correction applies only inside
the overlap band (`TryMapIntoNeighbour`, `:234-266`) **[opened]**. The pair
hash is over addresses the neighbour reports, so a neighbour can choose
generations that assign a pair to the local side **[inference]**. The input is
also named as not replay-deterministic (`:25-29`) **[opened]**.

**Critique 1.2, group membership proof on the federation wire: confirmed
absent.** No codec or reservation field carries one, and the resolver names
remote membership proof as an open gap (section 5) **[opened]**. The only
group check is the Entra token's single deployment group **[opened]**.

**A12, avatar cost measured before the bake: moot today.** No avatar claim
exists: a search for `avatarClaim`, `admitPolicy` and `CreationCost` over
`src/` finds nothing, and the projection carries no look (section 1)
**[opened]**. The plan orders the measurement as an input to the admit policy
at arrival and the bake after install (`docs/plans/hub-and-saves.md:221-229`),
and leaves bake time open as P2 (`:1016-1019`) **[opened]**. The other A12
vectors stand as cited: lease length `WorldTransferLimits.LeaseSeconds`
(`src/Puck.World.Server/WorldTransferLimits.cs:6`) and eight pending
deliveries per projection subscription (`docs/architecture/worlds.md:849-853`)
**[opened]**.

**A9, Azure key issuance and binding lifetimes: answered.** API users carry a
bearer Entra access token, validated at connect and every second after, with
lifetime set by Entra **[opened]**/**[unverified]** for the lifetime value.
World authorities sign five-minute `SignsDirectly` claims from a key file
**[opened]**. Platform subject bindings last 30 days by default and renew at
each `AttestKeys` call, which is feature-gated and not wired into onboarding,
and no world code presents one **[opened]**. Counterpart claims verify under a
one-hour ceiling in a resolver no host constructs **[opened]**. A banned user
loses access when the group change reaches their next token and their current
token expires **[inference]**.

### Attack question 6, code side

No revocation list, status check or key-compromise mechanism exists in
`Puck.Attestation` or the world code (section 3) **[opened]**. The levers that
exist **[opened]**, with their reach **[inference]**:

| Lever | Where | Reach |
|---|---|---|
| Claim validity window | `LocalKeySigningOracle` (five minutes), `MaximumClaimAge` | ends a live federation connection at expiry |
| Admission edit | `WorldPeerHost.Credential.IsValid` re-verifies on a policy change | evicts a live connection within a second; per world |
| Entra token lifetime and group | `EntraWorldAuthenticator.TryVerify` | per tenant; a removed user keeps any unexpired token |
| Binding expiry | `IssuingKeyOptions.SubjectBindingValidity` (30 days) | unused by world code today |
| Grant revoke | `world.revoke` (`src/Puck.World.Console/WorldGrantCommandModule.cs:61-65`) | one world's grants, not identity |

## Citation re-check

Every **[checked]** citation in [the attack catalogue](attacks.md) was reopened
at this tree. All `src/` citations hold. The architecture page gained 17 lines
in its plain-terms section after the catalogue was written, so every
`docs/architecture/worlds.md` citation past line 105 has moved; one quote
there also changed wording. The table lists every citation that no longer
holds as written, with the current location. A row marked "claim narrower"
cites correct lines whose conclusion this lane's reading narrows.

| Catalogue place | Cited (`docs/architecture/worlds.md` unless a file is named) | Status | Current |
|---|---|---|---|
| 1.1 "unsigned" | lines 785-786 | moved | `docs/architecture/worlds.md:802-803` |
| 1.1 items | line 390 | moved | `docs/architecture/worlds.md:407` |
| 1.1 desktop log | lines 1278-1279 | moved | `docs/architecture/worlds.md:1295-1296` |
| 1.1 co-location | lines 1384-1397 and 1444 | moved | `docs/architecture/worlds.md:1401-1414`, `:1461` |
| 1.1 cluster search | "finds one unrelated comment" | count off | four unrelated hits in two files (`src/Puck.World.Server/WorldDiagnosticReport.cs:37`, `:86`, `:129`; `src/Puck.World.Server/WorldPopulation.Query.cs:109`) |
| 1.2 generations | lines 1281-1284 | moved | `docs/architecture/worlds.md:1298-1301` |
| 1.2 remote evidence | lines 739-741 | wrong lines (off before the move too) | `docs/architecture/worlds.md:760-762` |
| 1.2 domain | line 403 | moved | `docs/architecture/worlds.md:420` |
| 1.3 invariant 5 | lines 415-417 | moved | `docs/architecture/worlds.md:433-434` |
| A1 projection | lines 766-770 | moved | `docs/architecture/worlds.md:782-786` |
| A1 `TrySetFact` | `src/Puck.World.Schema/WorldIdentity.cs:497-522` | lines hold; claim incomplete | also refuses a new key past capacity, `src/Puck.World.Schema/WorldIdentity.cs:539-543` |
| A1 model answer | lines 84-88 | moved and reworded | `docs/architecture/worlds.md:86-88`: "so a copy gains nothing", not "a copied claim gains nothing" |
| A2 write-back | lines 778-781 | moved | `docs/architecture/worlds.md:795-797` |
| A2 adoption chain | `src/Puck.World.Server/WorldTransferEscrow.cs:1357-1409` | lines hold; claim narrower | adoption is colocated only: `HomeIdentity` at `src/Puck.World.Server/WorldTransferEscrow.cs:1431-1440`, federated arrivals peer-admitted at `src/Puck.World.Server/WorldFederationCodec.cs:1363`, stated at `docs/architecture/worlds.md:804-806` |
| A3 departure | lines 1160-1170 | moved | `docs/architecture/worlds.md:1177-1187` |
| A3 "loses position" | lines 1358-1361 | moved | `docs/architecture/worlds.md:1375-1378` |
| A3 "home adopts on return" | (A2 chain) | claim narrower | a federated destination's writes are never adopted, returned or not (row A2 above) |
| A4 video | line 1480 | moved | `docs/architecture/worlds.md:1497` |
| A5 draws | line 1455 | moved | `docs/architecture/worlds.md:1472` |
| A6 lockstep | lines 1390-1393 | moved | `docs/architecture/worlds.md:1407-1410` |
| A7 departure | lines 1218-1226 | moved | `docs/architecture/worlds.md:1235-1243` |
| A8 fences | lines 1180-1200 | moved | `docs/architecture/worlds.md:1197-1217` |
| A11 ledger | lines 1202-1213 | moved | `docs/architecture/worlds.md:1219-1230` |
| A12 backpressure | lines 832-836 | moved | `docs/architecture/worlds.md:849-853` |
| A13 split | lines 589-593 | moved | `docs/architecture/worlds.md:606-610` |
| A16 projection | lines 753-757 | moved | `docs/architecture/worlds.md:770-774` |
| 3 fetch | lines 888-895 | moved | `docs/architecture/worlds.md:905-912` |
| 3 transfer keys | lines 1172-1213 | moved | `docs/architecture/worlds.md:1189-1230` |

Every other **[checked]** citation holds at the cited lines: the `src/` files
named in sections 1 to 3 of the catalogue, `docs/plans/companion-redesign.md`,
`docs/plans/hub-and-saves.md`, `docs/plans/disclosure.md`,
`docs/plans/mmo-foundations.md`, `docs/development/hazards.md:225-228`,
`docs/development/security-review-reference.md:143` and
`docs/reference/attestation.md`. Two holding citations are narrower than their
prose: `src/Puck.World.Server/WorldAdjacencyFields.cs:486-498` declares the
local arrays, while the copy into them is the pinned snapshot path; and
`src/Puck.World.Server/WorldAuthorityCheckpointCodec.Escrow.cs:308-314` shows
mobility admissions, with leases at `:273` and `:294`.

## Open questions

- Whether the deployed App Configuration enables `AttestKeys`, and the Entra
  access-token lifetime for the platform's app: outside the repository; the
  owner or an Azure read can settle both.
- Where `Draw.Secret` is kept out of observations, if anywhere: a focused read
  of the projection and sink composers, or a run that observes a secret row as
  a restricted recipient, settles it; this lane did neither.
- Whether a body step after contact bounds displacement elsewhere (a speed or
  step clamp downstream of the contact field): a read of `WorldBody.Step`
  after `ResolveEntitySweep` settles it.
- Whether the debug world's faerie crosses today (build-plan step 10): a
  headless run settles it.

## Sources

Code and documents opened at this tree:
`docs/architecture/worlds.md`, `docs/plans/mmo-foundations.md`,
`docs/plans/mmo-foundations/attacks.md`, `docs/plans/companion-redesign.md`,
`docs/plans/hub-and-saves.md`, `docs/plans/disclosure.md`,
`docs/plans/open-items.md`, `docs/decisions/companions.md`,
`docs/development/hazards.md`, `docs/development/security-review-reference.md`,
`docs/reference/attestation.md`, `src/Puck.World.Schema/` (`WorldIdentity*.cs`,
`WorldAdmission.cs`, `WorldAdmissionDoor.cs`, `WorldCounterpartAttestation.cs`,
`WorldSessionResolver.cs`, `WorldStateDisclosure.cs`, `WorldObserverDisclosure.cs`,
`WorldActionVocabulary.cs`, `WorldRole.cs`, `WorldDraw.cs`,
`WorldDefinitionValidator.Disclosure.cs`, `WorldInteractions.cs`,
`WorldAdjacencies.cs`, `WorldProjection.cs`), `src/Puck.World.Server/`
(`WorldOwnedWorlds.cs`, `WorldOwnedWorldSync.cs`, `WorldTransferEscrow.cs`,
`WorldTransferLimits.cs`, `WorldFederationCodec*.cs`, `WorldPeerHost.cs`,
`WorldCrossAuthoritySettlement.cs`, `WorldAdjacencyContactField.cs`,
`WorldAdjacencyFields.cs`, `WorldCrossingRecord.cs`, `WorldServer.Crossings.cs`,
`WorldServer.cs`, `WorldCompanionCustody.cs`, `WorldInstanceHost*.cs`,
`WorldRuleHost.IdentityFacts.cs`, `WorldStateReadView.cs`, `WorldOutputHub.cs`,
`WorldStateRoot.cs`, `WorldAuthorityCheckpointCadence.cs`,
`WorldAuthorityCheckpointCodec.Escrow.cs`, `WorldReleaseRewindBoundary.cs`,
`WorldStateHashComposition.Pools.cs`), `src/Puck.World.Silo/` (`WorldSiloHost*.cs`,
`README.md`), `src/Puck.World.Protocol/` (`Codecs/WorldAttestedAuthenticator.cs`,
`Codecs/ISigningOracle.cs`, `Protocol/WorldSessionMirror.cs`),
`src/Puck.World.Azure/` (`EntraWorldAuthenticator.cs`,
`WorldApiCounterpartResolver.cs`, `CounterpartApiPolicy.cs`,
`AzureSiloExtensions.cs`, `README.md`), `src/Puck.Azure.Functions/`
(`HttpTriggers/AttestKeys.cs`, `Services/DefaultBindingService.cs`,
`Services/IssuingKeyOptions.cs`, `Middleware/FeatureGateMiddleware.cs`,
`configuration.json`), `src/Puck.Actors/Services/` (`DefaultKeyPairService.cs`,
`DefaultUserProvisioningService.cs`), `src/Puck.Networking/Peers/`
(`PeerIdentity.cs`, `PeerAuthenticator.cs`, `QuicPeerTransport.cs`),
`src/Puck.Physics/FixedDynamicBodyContacts.cs`, `src/Puck.State/Draw.cs`,
`src/Puck.State.Generators/` (`GeneratorEngine.Streams.cs`, `PrivateDraw.cs`),
`src/Puck.World/` (`Program.cs`, `IdentityCommandModule.cs`,
`WorldOwnedWorldRegistration.cs`, `WorldConnectionAuthentication.cs`,
`WorldBootComposition.cs`), `src/Puck.Azure.Resources/main.bicepparam`,
`worlds/debug/debug.puck`, `worlds/debug/garden.puck`,
`worlds/debug/night.puck`, `worlds/standard/companions.puck`, and the history
of `lane/companion-v1` against `lead/velocity`.

---

[Plans](../README.md) · [MMO foundations](../mmo-foundations.md) · [Attacks](attacks.md) · [The world model](../../architecture/worlds.md)
