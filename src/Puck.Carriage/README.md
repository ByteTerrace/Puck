# Puck.Carriage

The signed-carriage envelope: how a claim — "this identity says X" — travels
between worlds and is verified offline by whoever receives it. This project is
both the reference implementation (a library referenced by `Puck.World`,
`Puck.World.Data`, and `Puck.World.Server`, whose admission door verifies a
connecting peer's signed claim against the world document's trust list) and its
own proof: the same csproj builds a console harness whose run is the
verification story.

This README is the home of everything signed-carriage: what the mechanism is
and why it is shaped this way ([Signed carriage](#signed-carriage)), and the
normative wire specification — byte layout, canonicality rule, refusal set,
and verify algorithm — as its closing section. The sources' XML docs carry the
per-member contracts.

## Running the harness

```text
dotnet run --project src/Puck.Carriage -c Release
```

Every scenario prints `PASS` or `FAIL`; the process exits `0` only when all
passed. The matrix covers both codecs and pairs every refusal with an accepting
control: algorithm confusion, purpose replay, cross-domain claims, chain-depth
attacks, window boundaries, sequence-mark atomicity under contention, parser
laxity, and sealed-carriage AAD tampering.

Two more verbs drive the cross-implementation check (wire spec §17):

```text
dotnet run --project src/Puck.Carriage -c Release -- export <directory>
dotnet run --project src/Puck.Carriage -c Release -- verify <directory>
```

`export` mints a chain, a claim, and a sealed claim to files; `verify` pins the
exported root and verifies a fixture the other implementation minted. Exit
codes are normative: `0` all checks passed, `1` at least one failed, `2` the
command line was not understood — a crash is never a permitted way to report a
failed check.

## Signed carriage

*Issuer-signed slots* and *an authored trust list* both rest on this, and it is
one mechanism rather than several. It is also where
[world-model invariant 5](../../docs/vision.md#the-invariants) becomes
concrete: the engine carries proof and enforces capabilities, while whether a
claim *counts* stays the receiving world's policy.

**An id is domain, subject, algorithm, key** — and the domain *is* its root
key's fingerprint, so identity needs no registry and cannot be squatted: taking
another's requires its private half. The platform is not a tier under this,
only a domain with many users, while a self-hoster is a domain with one. The id
names the algorithm rather than the role because the algorithm implies the role
while the reverse does not, and because two signing algorithms would otherwise
collide at one path. Verification needs no fetch: a claim travels with the
bindings leading to it, so a verifier walks from a pinned id down to the key
that signed using only what arrived. Every id contains its key's hash, so each
hop is self-certifying against the one above it. A claim that arrives without
its chain is refused rather than resolved — going to look for the rest is the
online dependency this whole design exists to remove, re-entering by a side
door.

A root is the base case of that shape: no domain above it and no subject, only
its own fingerprint. It is provisioned once per domain and **never escrowed** —
the property that makes identity-by-fingerprint survivable is that a root stays
cold, which a key held online cannot be.

**Everything signed uses one envelope.** A canonical context header is always
part of the signing input — domain, subject, algorithm, purpose, validity
window, and optionally an audience and a sequence — and only the payload
differs. This is the associated-data half of AEAD applied to signatures: bind
the context, not just the content, so a signature cannot be lifted into a
situation it was never minted for. The purpose field stops a binding signature
being replayed as a claim; the algorithm field stops a sealing key being
accepted where a signing key belongs; the domain field stops one trusted root
signing for another's subjects. A key binding is not a separate artifact under
this — it is the envelope with `purpose: key-binding` and a key id as its
payload. **One envelope means one verify path**, which is the whole reason to
do it this way rather than adding a field per problem as each appears.

**The algorithm is always taken from the pinned key, never from the envelope.**
A verifier that lets the message choose is how JOSE deployments died —
`alg: none`, and RS256 verified as HS256 against the public key. The field is
there to be *checked against* the pin, never to select behaviour.

The envelope is a **specification, implemented on each side** rather than a
shared library. The byte layout is all that must agree, and disagreement fails
loudly because signatures stop verifying. Trust evaluation is deliberately not
shared: each side trusts different things. The serialisation is CBOR, committed
to by both independent implementations. Reach is what decided it: CBOR lets a
third party reach for a library instead of a spec, at the price of a package
reference (`System.Formats.Cbor` is Microsoft-authored but arrives inbox only
through the ASP.NET Core shared framework) and of closing by hand every degree
of freedom it offers, because a signature is over bytes and one model must have
exactly one encoding. The fixed layout — which keeps parsing away from
unauthenticated bytes and gets canonicality for free, since every field is
fixed-width or minimally length-prefixed — stays shelved but alive in the
specification's closing section, implemented and harness-covered. The field
list is identical either way. Claims are ephemeral and constrain nothing.
The wire specification below is the normative text both independent
implementations are written against and conform to.

Sealed carriage is the same envelope with the payload encrypted and the header
as associated data — literal AEAD, and the reason two keypairs are provisioned
rather than one. The agreement is *ephemeral to static*, so sealing proves
nothing about who sealed: anyone holding the recipient's public sealing key can
produce a payload that opens cleanly. Sealed carriage is confidentiality only,
and where the recipient needs to know who sent it, the sealed payload rides
inside an ordinary signed envelope and the signature is what names the sender.

**Audience is the authored trade.** Binding one determines whether replay costs
anything:

| | Audience | Replay *elsewhere* | Replay *at the audience* |
|---|---|---|---|
| **Directed** — valid at one world | bound | free; the signature simply fails anywhere else | a sequence, or accepted |
| **Bearer** — travels anywhere | absent | a durable sequence high-water mark | the same mark |

Portability and statelessness are exclusive, so the author picks. Same-world
replay needs the sequence either way; binding an audience shrinks the problem
rather than deleting it. **Audience and sequence are therefore independent
fields, not alternatives:** only a bearer claim *requires* a sequence, but a
directed claim may carry one, and a verifier checks and advances the mark
whenever a claim carries one at all. A directed claim without a sequence is
replayable at its own audience, which is an authored choice — correct for a
claim whose effect is idempotent, wrong for one that is not.

The mark is durable keyed state — one sequence per issuer-and-subject pair — so
bearer claims are gated on the same keyed-table primitive threat tables want,
slots being scalars today. It is written at admission through the ordered
submission domain like any other durable write, which is what keeps it
tick-stamped and taped rather than a mid-tick read of storage. **Retention is
coupled to the window, and the coupling is load-bearing:** a mark must outlive
the receiver's acceptance window for its pair, or evicting it reopens replay
for a claim that is still valid. That coupling is also what bounds the table,
since a mark whose claims can no longer be accepted can be dropped.

**A trust entry pins an id** and says whether that key signs directly or may
*vouch* for others, plus which slots it reaches. A vouching entry is a domain,
so trusting a domain and pinning a key are one act.

**A chain is at most two hops, because one cannot hold.** A root vouching for
every subject directly would sign once per signup forever, and a key that signs
continuously is warm — so depth one costs the cold root at exactly the domain
with the most to lose. Instead a root vouches for an *issuing* key and the
issuing key vouches for subjects: the root signs approximately never, while the
warm key is replaceable without touching anything anyone pinned. A domain with
one user still mints both hops — and a root that vouches for *itself* as the
issuing key is refused, being depth one in a two-hop costume, back to signing
per subject. A chain therefore has exactly two admissible lengths: **two**
bindings under a trusted domain root, and **zero** when the trust entry pins
one subject's own key directly, which vouches for nothing and so has nothing to
walk. One is a broken chain; three is an unbounded one. What stays refused is
the *unbounded* chain — path discovery, cross-certification, a verifier that
follows wherever a claim points. Two is a number a verifier hard-codes, not an
engine it runs.

An empty list honours no foreign claim, deny by default like every other
capability. **The engine compiles in no root.** A shipped game ships its
publisher's; a blank template ships none. Every world verifies against its own
list, so admission negotiates nothing.

**Validity is authored at both ends.** The issuer sets a window when it mints;
a verifying world sets the maximum age it will accept, and the tighter of the
two governs. Neither can loosen the other — an author cannot reach past what
was signed, and an issuer cannot force a world to honour something stale. The
window is not the only lever, and conflating them oversizes it: removing an
issuer from the trust list revokes its standing at once and for everything it
ever signed, while the window governs only how long a claim from a
*still-trusted* issuer stays good. The list revokes an issuer, the window
expires a claim — so neither should be sized to do the other's job. Within its
own scope the window is the whole story, which makes it the longest a
compromised subject key stays honoured. The cost of shortening it is easy to
miss: verifying is offline but re-attesting is online, so a tight window
quietly makes long offline play impossible. A world wanting that sets a
permissive ceiling; a high-stakes world sets a tight one; both read the same
signed binding.

A short window is only affordable because **re-attestation is routine**: the
issuer re-signs the same binding with a fresh window, and its natural trigger
is every authenticated session start — the one moment the subject is provably
online anyway — so the window need only cover the longest stretch *between*
logins, not the life of a key. Re-attestation cannot shorten retroactively: an
earlier binding stays good until its own window ends, which is what keeps
replaying one pointless and is exactly why the window bounds a compromise. It
is the issuer's operation, not the engine's, and today it does not exist — the
platform mints pairs and signs nothing — so it is the piece of the window model
most likely to be discovered late.

**What remains ours.** Issuance is not. Where a domain issues for its users the
private half is escrowed: it is sealed under a per-key random password, and the
wrapped password travels *with* the ciphertext, so what the domain actually
holds is the ring that unwraps it rather than any password. An identity cannot
sign without the domain, and the secret that must never leak is that ring. That
splits the halves in the direction this design needs: **issuing a claim is
online, verifying one is local**, and the issuer is by definition the party who
is online. It also bounds what a signature proves — that a domain issued this
for an identity, which is the trust already assumed, and not that a person
personally did. Nothing here should be built on the stronger reading. The
engine consumes a PKI rather than operating one. Signing is randomised, so
minting happens outside the tick and a claim enters taped like any foreign
value; verifying is deterministic but far too slow for 240 Hz, so it happens at
admission and on a schedule with the verdict held. Beyond that:
re-verification across a session, since checking once at join honours a claim
past its expiry — and expiry is a wall-clock event, so by
[world-model invariant 2](../../docs/vision.md#the-invariants) it enters
at the boundary tick-stamped and taped, never as a mid-tick read of a clock. A
verdict is state like any other. Beyond that again, a decision about what a
world *does* on revocation rather than only detecting it.

## Ruled out

| Rejected | Why |
|---|---|
| **Consulting the issuer at verification time** | whether to ask if a claim still holds or to fetch the key that checks it, both restore the online dependency the signature exists to remove: a world must verify while the issuer is unreachable, asleep or gone. Offline decoupling is the requirement, and it is what makes a signature load-bearing rather than an optimisation. A public key is not anonymously readable as provisioned either, so carrying it inline against a pinned id needs no fetch and no exposure |
| **Trusting a domain's label** | a friendly name is display only. Two domains can carry the same label and only the fingerprint separates them; a label that decides anything is a name pretending to be a key |
| **Peer-minted claims** | a claim minted by its own subject attests only that they said so. Where a domain issues for its users the private half never leaves it, so a peer cannot mint one and gains nothing by wanting to |
| **Carrying mutable balances signed** | a signature pins a value at a moment; balances stay owned by their issuer and change by write-back |

## Signed carriage — wire specification v1

**Normative.** This section is the contract between independent implementations
of the signed carriage envelope described in
[Signed carriage](#signed-carriage) above. The envelope is a
specification implemented on each side, never a shared library, so this text —
not any one codebase — is what the two sides agree on. It is written to be
implementable from prose alone, in any language, with only a CBOR encoder, an
ECDSA implementation, and SHA-256.

Two implementations exist today and both conform: `src/Puck.Carriage` (this
repository) and `BindingCarriage` (Web.Functions). They share no source.

Keywords: **MUST**, **MUST NOT**, **MAY**, **REFUSE**. *Refuse* means: produce a
negative verdict without side effects. It never means throw-or-return — that is
a language choice.

### 0. What is fixed and what is not

Fixed by this document: the byte layout, the canonicality rule, the signature
encoding, the set of conditions that MUST refuse, and the order in which
security-relevant work happens.

Not fixed, deliberately: which refusal an implementation *reports* when several
apply, the text of any message, the shape of the trust list beyond the two
fields §7 names, and what an accepting verifier then permits. Trust evaluation
is not shared — each side trusts different things. **Two conforming
implementations always agree on accept-versus-refuse and MAY disagree on the
reason.** A cross-check that compares reasons is testing something this
specification does not promise.

### 1. Roles and identities

An **id** is `(domain, subject, algorithm, keyHash)`.

- `domain` — the SHA-256 fingerprint of the root key at the top of this id's
  chain, 32 bytes. Constant across every key that chain vouches for. Never a
  name.
- `subject` — the platform user id this key belongs to, or absent for a **root**
  or an **issuing** key.
- `algorithm` — a name from the registry in §4. It fully determines the curve
  and the hash; nothing else may appear here.
- `keyHash` — SHA-256 of this key's `SubjectPublicKeyInfo` (SPKI) DER encoding,
  32 bytes. Always derived from actual key bytes, never accepted as an
  independent claim.

A **root** id satisfies `domain == keyHash` and has no subject — no flag records
this, the shape proves it. An **issuing** id shares its root's domain and has no
subject. A **subject** id shares its root's domain and carries the user id.

Ids are rendered as lowercase hex in human-facing surfaces and as raw bytes on
the wire (§2). The two are the same value; nothing on the wire carries hex.

### 2. Encoding

CBOR (RFC 8949), deterministically encoded (§3). Three data items are defined:
the **envelope**, the **signed portion**, and the **payload**.

```cddl
envelope = [ signedPortion: bstr, signature: bstr ]

; the content of signedPortion, encoded independently, is:
signed-portion = [
    formatVersion: uint,          ; MUST be 1
    domain:        bstr,          ; exactly 32 bytes
    subject:       tstr / null,
    algorithm:     tstr,          ; a §4 registry name
    purpose:       tstr,
    notBefore:     int,           ; Unix seconds, plain integer — NOT tag 1
    notAfter:      int,           ; Unix seconds, plain integer — NOT tag 1
    audience:      tstr / null,
    sequence:      uint / null,
    payloadKind:   uint,          ; 1 opaque, 2 key-binding, 3 sealed
    payload:       bstr,
]

; payloadKind 2 (key binding); the content of payload is:
key-binding-payload = [
    targetDomain:    bstr,        ; exactly 32 bytes
    targetSubject:   tstr / null,
    targetAlgorithm: tstr,        ; a §4 registry name
    targetKeyHash:   bstr,        ; exactly 32 bytes
    targetKey:       bstr,        ; SPKI DER of the vouched-for public key
]

; payloadKind 3 (sealed); the content of payload is:
sealed-payload = [
    ephemeralKey: bstr,           ; SPKI DER of the sender's one-time key
    nonce:        bstr,           ; exactly 12 bytes
    tag:          bstr,           ; exactly 16 bytes
    ciphertext:   bstr,
]
```

payloadKind 1 (opaque) is caller-defined bytes. No implementation interprets
them. The set {1, 2, 3} is closed, and a value outside it MUST be refused by the
**decoder** rather than left to the verifier: implementations naturally model
the kind as a single byte, and a wider wire value would silently truncate into a
legitimate kind (258 becomes 2). The canonicality rule below catches that too,
but only as a second line — a kind outside the set is not a canonicality
problem, it is not a kind.

**The signed portion travels as an opaque byte string.** The exact bytes that
were signed arrive verbatim; a verifier MUST check the signature against those
bytes, never against a re-encoding of what it parsed out of them. §3 makes the
two identical anyway, and that is the point — the wrapping makes it structural
rather than a property of somebody's encoder.

**All eleven signed-portion elements are inside the signature, format version
included.** A version outside the signature is a field an attacker rewrites for
free, and the version is what decides how every later byte is read.

**A key binding is not a separate artifact.** It is this envelope with
`purpose = "key-binding"` and `payloadKind = 2`. One envelope means one verify
path.

**`subject` always names the SIGNER, never the key being vouched for.** It is
therefore `null` on both bindings of a chain — a root and an issuing key are not
platform users — and carries the user id only on a claim. The key a binding
vouches for lives entirely in its payload. This is the single easiest field to
get backwards, and getting it backwards produces bindings that verify against
their own implementation and refuse everywhere else, because §11 step 4 checks
`subject` against the *pinned signer's* id.

**Optional fields are encoded as CBOR `null`, never omitted.** Every array has
its fixed element count. An absent-by-omission field would give one model two
encodings, which §3 forbids.

### 3. The canonicality rule

**One model, exactly one encoding. A decoder MUST REFUSE any other.**

Normatively: after decoding, re-encode what was decoded by the rules of this
section; if the result is not byte-identical to what arrived, REFUSE. That check
is sufficient and is the one an implementation should write, because it cannot
drift from the encoder beside it.

An encoder MUST produce, and therefore a decoder MUST require:

1. Definite lengths for every array and string. No indefinite-length items.
2. The smallest possible head for every integer, length, and array count
   (`0x82` for a 2-element array, never `0x98 0x02`).
3. No CBOR tags anywhere. Times are plain integers.
4. Text strings in UTF-8.
5. Nothing after the outer data item, at any nesting level. Trailing bytes are
   REFUSED, never ignored — a decoder that ignores them hands an attacker a
   family of distinct wire forms that all decode to one accepted claim.

Rules 1 and 2 are what a CBOR library's canonical/deterministic mode already
gives on the write side; few give it on the read side, which is why rule 5 and
the re-encode check are stated as obligations of the *decoder*.

Why this matters more here than in most formats: a signature is over BYTES.
Without one encoding per model, an honest claim has many wire forms, a receiver
deduplicating on bytes sees one claim as many, and a verifier that re-derives
the signing input from a parsed model refuses honest bytes for the wrong reason.

### 4. Algorithm registry

| Name | Role | Curve | Signature hash |
|---|---|---|---|
| `ecdsa-p256-sha256` | signing | NIST P-256 | SHA-256 |
| `ecdsa-p256-sha384` | signing | NIST P-256 | SHA-384 |
| `ecdh-p256-hkdf-sha256-aes256gcm` | sealing | NIST P-256 | — |

The name pins curve *and* hash, because a P-256 key can sign under either digest
and the curve alone does not pin the scheme.

A name outside this table MUST be REFUSED wherever it appears — an envelope's
`algorithm`, a binding payload's `targetAlgorithm`, or a trust entry's pinned
id. An implementation MAY support a subset of the table (Web.Functions signs
only `ecdsa-p256-sha256`); it MUST NOT accept a name that is not in it.

**A sealing algorithm can never admit a claim.** A trust entry pinning one MUST
be REFUSED at construction.

### 5. Signature encoding

ECDSA, IEEE P1363 fixed-field concatenation: `r || s`, each padded to the
curve's field width. For P-256 that is exactly 64 bytes. Any other length MUST
REFUSE, which forecloses DER as an alternate encoding of the same `(r, s)`.

A verifier MUST additionally check that the imported key's own curve is the one
the pinned algorithm names. An SPKI blob carries its own curve, and a name
promising P-256 must not verify against a key on some other curve.

**Signatures are not identities.** ECDSA is malleable: `(r, s)` and `(r, n-s)`
are both valid over the same message, so two distinct envelopes can carry one
claim. No low-`s` rule is imposed, because platform signers do not canonicalize
`s` and requiring it would refuse honest output about half the time. Replay
defence therefore rests on the sequence mark and the audience (§8) and MUST NOT
rest on having-seen-these-bytes.

### 6. The algorithm-from-pin rule

**The algorithm used to verify always comes from the PINNED key — from the trust
list, or from the binding one hop up — never from the envelope being verified.**

The envelope's `algorithm` field exists to be *checked against* the pin, and an
inequality MUST REFUSE. It is never read to select behavior. A verifier that
lets the message choose is how JOSE deployments died (`alg: none`; RS256
verified as HS256 against the public key).

### 7. Trust entries

A trust entry pins an id **and** the actual key bytes it names — offline
verification cannot resolve a hash into a key. An entry declares one of two
modes:

- **signs-directly** — the pinned id MUST carry a subject. Claims from this key
  arrive with **zero** bindings.
- **vouches** — the pinned id MUST be a root (`domain == keyHash`, no subject).
  Claims under it arrive with **exactly two** bindings.

An entry whose key bytes do not hash to its own pinned `keyHash` MUST be
REFUSED at construction: otherwise the bytes do the verifying while the pin sits
there decorative.

A verifier MAY carry more per-entry policy — a maximum claim age (§9), which
slots the entry reaches, anything else. Only a maximum age affects the verdict,
and only by tightening. Everything else is the receiving side's business and is
outside this specification. An empty trust list honours nothing; deny by
default.

A direct pin is strictly more specific than a domain root, so a verifier MUST
consult direct pins first.

### 8. Purpose, audience, and sequence

**Purpose** separates signature *uses*. `key-binding` is reserved: an envelope
declaring it MUST be REFUSED wherever a claim is expected, and a caller MUST NOT
ask for it as an expected claim purpose. Every other purpose is game-defined,
and a mismatch with what the caller expects MUST REFUSE.

**Payload kind** separates what the bytes *mean*, and is checked separately from
purpose. A claim's kind MUST be 1 or 3; a binding's MUST be 2. A kind outside
{1, 2, 3} MUST REFUSE — deny by default.

**Audience and sequence are independent, not alternatives.**

| | Audience | Replay elsewhere | Replay at the audience |
|---|---|---|---|
| Directed | present | the signature simply fails elsewhere | a sequence, or accepted |
| Bearer | absent | a durable sequence high-water mark | the same mark |

- A directed claim's `audience` MUST equal the verifier's own audience identity.
- A bearer claim (no audience) with no `sequence` MUST REFUSE — it would have no
  replay defence at all.
- **Whenever `sequence` is present — directed or bearer — the verifier MUST
  check it against a durable high-water mark per `(domain, subject)`, REFUSE if
  it does not strictly exceed the mark, and advance the mark on acceptance.**
  Binding an audience defends against replay *elsewhere* and never against
  replay at the audience itself.
- **The compare and the advance MUST be one atomic operation** with respect to
  every other verification of the same `(domain, subject)` pair — a lock, a
  conditional update, a compare-and-swap, or a transaction. Two concurrent
  presentations of the SAME claim MUST produce exactly one acceptance.
  A verifier that reads the mark, compares, and then writes is a check-then-act
  race on the one check whose entire purpose is to make a claim usable once:
  both readers see the old mark, both find the sequence higher, both accept, and
  the mark ends up recording a replay that it was supposed to refuse. This is
  not a remote hazard — a verifier serving a network is concurrent by
  construction, and the single-threaded case is the special one.
- The advance MUST be durable before the claim is admitted. A mark lost to a
  crash after acceptance reopens exactly the replay it just refused.
- A claim carrying a `sequence` presented to a verifier with no mark store MUST
  REFUSE. A declared replay defence is never skipped because the receiver has
  nowhere to record it.
- **A mark store that cannot decide REFUSES the claim.** Unreachable,
  unreadable, unable to persist the advance before answering, or unable to
  settle the atomic compare — a timeout, an aborted transaction, a lock nobody
  won — every one of these is a refusal, never an admission. "Durable before
  admission" implies it, and implying it is not enough: *accept because the
  store is down* is a reading someone takes, and it admits exactly the replay
  the mark exists to refuse. An unavailable store means the declared defence is
  absent, and the bullet above already refuses a claim whose declared defence
  cannot be honoured; there is no exception for the store being the thing that
  failed. Deny by default.
- Nothing is consumed by such a refusal, so the same claim MAY be presented
  again once the store recovers, and a verifier MAY retry internally before
  refusing. What it MUST NOT do is admit the claim in the meantime, or report an
  advance it has not durably recorded.
- The failure MUST NOT escape the verifier as something other than a verdict
  either. A receiver whose store blinked must still produce accept-or-refuse for
  every input; a verifier that propagates the storage fault instead has stopped
  answering the question it was asked, which is a third outcome this
  specification does not have.

Retention of the mark is coupled to the acceptance window: a mark MUST outlive
the window for its pair, or evicting it reopens replay for a claim still valid.

### 9. The validity window

`notBefore` and `notAfter` are Unix seconds authored by the issuer. A verifier
MAY additionally impose a maximum age. The tighter of the two governs; neither
loosens the other.

REFUSE when any of these hold:

- `notAfter < notBefore` (malformed window)
- `now < notBefore` (not yet valid)
- `now > notAfter` (expired)
- `now - notBefore > maximumAge`, when the verifier authored one

**Where `now` comes from is a requirement, not a style note.** The verifier MUST
NOT read a clock; that much is easy. But a caller that reads one *immediately
before the call* has changed nothing except which stack frame the wall-clock
read happens in. For an implementation replaying a recorded input tape — as
Puck's engine does — `now` MUST be a value captured at the admission boundary
and recorded alongside the claim, so that a replay of the same tape reaches the
same verdict. Reading the clock at the call site makes the verdict depend on
when the replay is run, which is the failure the parameter exists to prevent.

The same reasoning bars verification from a simulation step at all: it consults
wall-clock time *and*, whenever a claim carries a `sequence`, both reads and
writes durable storage (§8). An implementation with a deterministic tick MUST
verify at the boundary and carry the verdict inward, never verify inside the
tick. An implementation with no such constraint — a request-scoped web service —
is free to read its clock at the boundary it already has, which is the same
rule with a boundary that happens to be the request.

**The wire carries whole seconds**, so an implementation holding a
higher-precision time type MUST compare against the value it decoded from the
wire, never against an in-memory copy that kept its sub-second part. Otherwise
two verifiers disagree inside a one-second band at each boundary, and the
minting side disagrees with everyone about its own window.

Both boundaries are **inclusive**, and there is deliberately **no clock-skew
grace**. An issuer wanting slack backdates `notBefore`, which is authored,
auditable, and signed — unlike a verifier-side grace window, which every
verifier would size differently and which silently widens every window in the
system by twice its size.

The verifier's maximum age applies to **every hop**, bindings included: a
binding is the longest a compromised subject key stays honoured, so a ceiling
that did not reach it would not be a ceiling.

#### The issuer re-attestation profile

Re-attestation is the issuer re-signing a binding it already vouches for with a
fresh window. Its ordinary case is a session resuming after an absence, so the
binding it acts on has usually **lapsed** — the issuer must be able to verify one
whose window has closed.

An implementation that mints MAY therefore offer a reduced profile in which the
four window checks above are skipped **and every other check in this document
still runs in full**. It MUST be a distinct, explicitly named mode requested by
the caller — never a defaulted flag, never inferred. A verifier admitting a
foreign claim MUST NOT offer it.

### 10. Chain depth

A chain has exactly two admissible lengths and no others:

- **zero** bindings, under a signs-directly entry. Nothing is vouched for, so
  there is nothing to walk. Bindings arriving anyway MUST REFUSE — accepting
  unexamined bindings lets an attacker attach whatever they like to a claim that
  verifies.
- **two** bindings, under a vouching root: `chain[0]` is root-vouches-issuing,
  `chain[1]` is issuing-vouches-subject.

One binding is a broken chain; three is an unbounded one. Both MUST REFUSE.
Two is a number a verifier hard-codes, not an engine it runs: there is no path
discovery and no cross-certification.

A root that vouches for **itself** as the issuing key MUST REFUSE. It is depth
one in a two-hop costume — the cold root is back to signing once per signup,
which is the entire cost the two-hop shape exists to remove, and the warm key
stops being replaceable without touching what everyone pinned.

A claim arriving without its chain MUST REFUSE and MUST NOT be resolved by
fetching. Going to look for the rest is the online dependency the design exists
to remove, re-entering by a side door.

### 11. Verifying one envelope

Given an envelope's wire bytes, a pinned id, that key's SPKI bytes, and the
caller's expectations:

1. Decode by §2 and §3. Any failure REFUSES.
2. Check the envelope's `algorithm` equals the pinned id's algorithm (§6).
   REFUSE on inequality.
3. Check `domain` equals **the pinned entry's own `domain`** — the domain of the
   id this verification is pinned on: the trust entry's, for a claim; the id the
   hop above yielded, for a hop. REFUSE on inequality.
4. Check `subject` equals the pinned id's subject (both may be absent). REFUSE
   on inequality.
5. Check `purpose` equals what the caller expects. REFUSE on inequality.
6. Check `payloadKind` is what that purpose requires (§8). REFUSE otherwise.
7. Resolve the pinned algorithm to curve and hash (§4). Import the pinned SPKI;
   REFUSE if the key's curve is not the algorithm's (§5).
8. Verify the signature over the signed-portion bytes **as they arrived** (§5).
   REFUSE on failure.
9. Apply the window (§9), unless the caller requested the re-attestation
   profile.
10. **Only now** decode the payload.

**Step 3's right-hand side is normative, and reading it as the envelope's own
field is the failure mode.** An earlier wording said "the domain the caller
expects", which names no value the caller supplies; read literally it compares
`domain` with itself, always passes, and checks nothing — and one of the two
implementations of this specification had implemented exactly that. The
right-hand side is always something the verifier already holds. Note where the
work actually happens: §13 finds its trust entry by looking the claim's `domain`
up, so a claim naming a domain nothing pins is refused *there* ("not a trusted
vouching root") and never reaches step 3. Step 3 then carries that same pinned
domain down every hop, which is what refuses a binding minted for another domain
whose own signature verifies perfectly well.

**Step 10's position is normative.** Every payload byte is attacker-supplied,
and the only thing that makes it safe to read is that the pinned signer
committed to it. Steps 2–7 are cheap rejections that may run in any order; step
8 MUST precede step 10.

### 12. Verifying a key-binding hop

A binding hop is §11 with `purpose = "key-binding"`, `payloadKind = 2`, and the
pinned key of the hop above. After step 10 yields a key-binding payload:

1. REFUSE if `targetAlgorithm` is not in the §4 registry.
2. REFUSE if `SHA-256(targetKey) != targetKeyHash` — the payload is not
   self-certifying. This is what lets the next hop trust `targetKey`: a hash
   alone cannot carry the next hop's verification key, so the binding conveys
   both and the verifier catches a lie by recomputing.
3. REFUSE if `targetDomain` is not the chain's domain (cross-domain).

The hop yields `(targetId, targetKey)` for the hop below.

### 13. Verifying a claim and its chain

Inputs: the claim envelope, its chain (0 or 2 bindings), a trust list, `now`,
the expected purpose, the verifier's own audience identity, and a sequence mark
store.

1. REFUSE if the caller's expected purpose is blank or `key-binding` (a
   programming error, not an attack).
2. REFUSE if the claim's `purpose` is `key-binding` — a binding presented as a
   claim.
3. REFUSE if the claim's `purpose` is not the expected one.
4. REFUSE if the claim's `payloadKind` is not 1 or 3.
5. Look up a **signs-directly** entry for `(domain, subject)`. If one exists:
   - REFUSE if any binding accompanied the claim (§10).
   - Verify the claim by §11 against that entry's pinned id and key.
   - Go to step 8, with the entry's maximum age.
6. Otherwise look up a **vouches** entry for `domain`. REFUSE if there is none
   ("not a trusted vouching root"), if the chain is absent or empty ("missing
   chain"), or if it does not hold exactly two bindings ("broken chain").
7. Walk:
   1. Verify `chain[0]` by §12, pinned on the root entry. REFUSE if its target
      carries a subject (an issuing key must carry none), if the target's domain
      is not the claim's, or if the target's `keyHash` equals the root's own
      (§10, root self-vouching).
   2. Verify `chain[1]` by §12, pinned on the issuing id and key that step 7.1
      yielded. REFUSE if its target carries **no** subject, if the target's
      domain is not the claim's, or if the target's subject is not the claim's
      subject.
   3. Verify the claim by §11 against the subject id and key that step 7.2
      yielded — which is where the claim's own `algorithm` is checked against
      the pin.
8. Apply the window to the claim (§9). Every binding hop has already had it
   applied with the same maximum age.
9. Apply audience and sequence (§8).
10. Accept.

Refusals in steps 5–7 that come from §11 or §12 propagate as refusals of the
whole chain.

**Where the domain is actually checked.** Steps 5 and 6 key their lookup on the
claim's own `domain`. That is what makes §11 step 3 non-circular rather than what
makes it circular: an entry *found* this way pins that domain by construction, an
entry *not found* refuses here, and it is the pinned domain — never the claim's
field re-read — that step 3 compares against at every hop below. A verifier that
re-read `domain` from the envelope at each hop would compare it with itself three
times and establish nothing, while looking exactly like a verifier that checked.

### 14. Sealed carriage

The same envelope with `payloadKind = 3`: the payload is AEAD ciphertext.
Key agreement is ECDH P-256, **ephemeral to static**, feeding HKDF-SHA256 to an
AES-256-GCM key; the associated data is the encoding of the **context header**
alone — signed-portion elements 1 through 9, with no `payloadKind` and no
`payload`. Tampering any header byte changes the AAD, so decryption fails closed.

**The header's encoding differs by wire format, and §14 is the one place §§4–15
do NOT apply unchanged to §16's layout.** The two are stated separately because
"a 9-element array" is exact for CBOR and meaningless for a format that has no
array head:

- **CBOR (§2).** A definite-length 9-element array holding signed-portion
  elements 1 through 9 in order, encoded by §3's rules. It is emphatically *not*
  a prefix of the signed portion: that array's head says eleven elements and this
  one's says nine, so the two differ in their very first byte. The header MUST be
  encoded, never sliced.
- **Fixed layout (§16).** Precisely a prefix of the signed portion — the
  format-version byte through the `sequence` field inclusive, stopping
  immediately before the payload-kind byte, by §16's rules. Here it *is* a slice,
  because the layout carries no framing to disagree about, and an implementation
  MAY obtain it by slicing the bytes that arrived — the same discipline §2
  already requires for the signed portion itself.

These are two different byte strings for one header, which is not a defect: a
payload is sealed under exactly one wire format, and the AAD is that format's
encoding of that header. Sealing under one and opening under the other fails as a
tag mismatch, correctly.

**The derivation and the AEAD construction are fixed in all five of their
inputs**, and every one of them MUST be exactly this:

| Parameter | Value |
|---|---|
| HKDF input keying material | the **raw** secret agreement — the shared point's X coordinate, exactly as the curve produces it, **not** hashed first |
| HKDF salt | **absent** (zero-length; the all-zero default of RFC 5869) |
| HKDF info | the ASCII bytes `puck.carriage.sealed.v1`, 23 bytes, no terminator |
| Output length | **32** bytes — the AES-256-GCM key |
| AEAD tag length | **16** bytes. Wherever the AEAD construction takes a tag length — `AesGcm(key, tagSizeInBytes)` and its equivalents — 16 is what MUST be passed |

The last row is derivable from §2's "tag: exactly 16 bytes" and is stated as a
construction input anyway, because a library that lets you *name* a tag length is
a library that lets you name the wrong one — and getting it wrong fails
identically to every other row here, as a tag mismatch with nothing pointing at
the cause. An implementation that dutifully refused a wire tag of the wrong width
and then opened with a 12-byte tag would refuse honest ciphertext forever while
its format checks all passed. That argument is the same one the four rows above
already rest on; the row was simply missing.

None of these appears anywhere in the ciphertext, so a disagreement about any one
of them is invisible until the far end fails — and it fails as an AEAD tag
mismatch, which is *byte-identical to the failure a tampered payload produces*.
An implementation cannot tell an interoperability bug from an attack, so leaving
any of the five to the implementer is not under-specification, it is a
misdiagnosis waiting to happen. This is also the one place a platform crypto
library is likely to disagree by default: several ECDH APIs (including .NET's
`DeriveKeyFromHmac` and `DeriveKeyMaterial`) hash the agreement before returning
it. The raw agreement is what feeds HKDF here.

Because none of it is observable from a signed envelope alone, the interchange
fixture (§17) carries a sealed artifact and the recipient's private sealing key,
so an implementation can conform against bytes rather than against this prose.

The ephemeral key travels on the wire and is therefore attacker-chosen: an
implementation MUST check both its **key type** and its **curve** before handing
it to the agreement, or the recipient's static key becomes an oracle for its own
private scalar.

- **Key type.** The SPKI's `AlgorithmIdentifier` MUST be `id-ecPublicKey` (OID
  `1.2.840.10045.2.1`) carrying a named-curve parameter. Any other key type — an
  RSA key, an Ed25519 or X25519 key, anything under a different OID — MUST
  REFUSE, and MUST be refused *before* the curve is consulted, because a non-EC
  SPKI has no curve to consult.
- **Curve.** The named curve MUST be P-256, the only curve the sealing algorithm
  names.

There is deliberately nothing else to check here, and that is worth saying
because a platform whose ECDSA and ECDH APIs are separate types invites the
belief that the key bytes are separate too. They are not: an EC public key's SPKI
is byte-identical in shape whether its holder means to sign with it or to agree
with it, so an ephemeral key records no signing-versus-agreement intent and an
implementation MUST NOT invent one to check. What separates a signing key from a
sealing key in this specification is the §4 algorithm *name* in the envelope and
the trust list, never the key bytes — which is also why §7 refuses a trust entry
pinning a sealing algorithm rather than trying to tell the keys apart.

A nonce that is not 12 bytes and a tag that is not 16 MUST REFUSE as format
errors, before any cryptographic call.

Sealing proves nothing about who sealed — anyone holding the recipient's public
sealing key can produce a payload that opens cleanly. Sealed carriage is
confidentiality only; where the recipient must know who sent it, the sealed
payload rides inside an ordinary signed envelope and the signature names the
sender.

### 15. Conformance summary

An implementation conforms when, for every input, it produces the same
accept-or-refuse verdict as this document requires. The conditions that MUST
refuse, gathered:

| # | Condition |
|---|---|
| 1 | Ill-formed CBOR, wrong major type, wrong array length, or an unknown `formatVersion` |
| 2 | Non-canonical encoding (§3), including trailing bytes at any level |
| 3 | `domain`, `targetDomain`, or `targetKeyHash` not exactly 32 bytes |
| 4 | `payloadKind` outside {1, 2, 3}; a claim outside {1, 3}; a binding not 2 |
| 5 | `algorithm` or `targetAlgorithm` outside the §4 registry |
| 6 | `algorithm` not equal to the pinned key's algorithm (§6) |
| 7 | An imported key whose curve is not the pinned algorithm's |
| 8 | A signature that is not exactly `2 × fieldWidth` bytes, or does not verify |
| 9 | `purpose` = `key-binding` presented as a claim, or any purpose mismatch |
| 10 | `domain` mismatch against the expected domain; `subject` mismatch against the pin |
| 11 | Window failures (§9), unless the re-attestation profile was requested |
| 12 | Chain length outside {0, 2}; bindings attached to a directly-pinned claim |
| 13 | Root vouching for itself as the issuing key |
| 14 | An issuing target carrying a subject; a subject target carrying none |
| 15 | A binding payload that is not self-certifying (§12.2) |
| 16 | A cross-domain binding target |
| 17 | A claim subject that is not the chain's subject |
| 18 | `sequence` present and not strictly above the mark, or present with no store |
| 19 | Bearer claim (no audience) with no `sequence` |
| 20 | Directed claim whose `audience` is not the verifier's |
| 21 | A trust entry whose key bytes do not hash to its pinned id, whose mode does not match its id's shape, or that pins a sealing algorithm |
| 22 | Sealed payload with a nonce that is not 12 bytes, a tag that is not 16, or an ephemeral key whose SPKI is not an `id-ecPublicKey` key on P-256 — key type and curve are separate checks in that order (§14) |
| 23 | A fixed-layout presence flag that is neither `0x00` nor `0x01` (§16) — an instance of row 2, listed separately because the natural "non-zero means present" reader gets it wrong silently |
| 24 | A sequence mark store that cannot decide: unreachable, unreadable, unable to persist the advance, or unable to settle the atomic compare (§8) |

Two obligations in this document are not refusal conditions and so cannot appear
in the table, but conformance depends on them just as much. Both are invisible to
any single-input test, which is exactly why they are called out here:

- **The signature is checked over the signed-portion bytes as they arrived**
  (§2), never over a re-encoding of the parsed model. An implementation that
  re-encodes still produces the right verdict on every honest input, and turns
  every decoder laxity anywhere in its stack into an accepted alternate wire form
  for a real claim.
- **The sequence compare-and-advance is atomic** (§8). A non-atomic
  implementation is correct on every sequential input and admits a bearer claim
  twice under concurrency.

#### Demonstrating the two

Calling them out is not enough. Conformance to precisely the two rules this
section names as most important would otherwise be unfalsifiable, since neither
can fail on the inputs a conformance run naturally has. **An implementation MUST
therefore demonstrate each, and the demonstrations MUST be part of what it
routinely runs** — the same obligation §17 places on a deliberately broken
fixture, and for the same reason: a verifier only ever shown correct input proves
nothing.

- **A concurrency demonstration.** One claim carrying a `sequence`, presented by
  at least two verifications that reach the mark store *simultaneously*, of which
  **exactly one** MUST be accepted. The simultaneity MUST be arranged — a
  rendezvous, a barrier, a store that holds every caller at the door until all
  have arrived — so that a non-atomic store fails deterministically. A test that
  passes because the threads happened to serialise has demonstrated luck. The
  discriminating property is that the SAME implementation with the compare and
  the advance split into two store calls MUST fail the demonstration.
- **A mutated-but-parseable demonstration.** Wire bytes that differ from an
  honest envelope, still decode, decode to the **same model**, and MUST be
  refused. These inputs are the only ones that separate a verifier checking the
  arrived bytes from one checking a re-encoding — an honest input cannot tell the
  two designs apart, which is the whole difficulty. At least one such input per
  wire format the implementation supports: for CBOR, a non-minimal array head or
  an indefinite-length item (§3 rules 1 and 2); for the fixed layout, an optional
  field's presence flag written as `0x02` (§16). An implementation that verifies
  over a re-encoding accepts every one of them.

Neither demonstration can be carried by the §17 fixture — one needs two threads
and the other needs bytes minted against a specific implementation's own honest
output — so both live with the implementation rather than in the interchange
directory.

### 16. Shelved alternative — the fixed layout

CBOR is the format above. A fixed field-by-field layout was implemented and
measured against it, and it stays on the shelf for a context that cannot carry a
CBOR implementation at all, or that wants every byte hand-specified with no
library between the spec and the wire.

Same field list, same order, same semantics — §§4–15 apply unchanged, **with one
stated exception**: §14's associated data is defined per wire format, because "a
9-element array" describes nothing here. This layout's definition lives in §14
beside CBOR's, where the two can be read against each other. Only §2, §3, and
that one paragraph differ:

- Format version: 1 byte.
- `domain`, `targetDomain`, `targetKeyHash`: 32 raw bytes, no length prefix.
- Strings: 4-byte big-endian length prefix, then UTF-8 bytes. Optional strings:
  a 1-byte presence flag first.
- `notBefore`, `notAfter`: 8-byte big-endian signed.
- `sequence`: 1-byte presence flag, then 8-byte big-endian unsigned.
- `payloadKind`: 1 byte. The closed set {1, 2, 3} and its decoder-side refusal
  (§2) apply here exactly as they do to CBOR: a byte outside the set MUST be
  refused by the decoder, never cast.
- `payload`, `signature`, and every variable-width byte field: 4-byte big-endian
  length prefix, then bytes.
- The envelope is the signed portion followed directly by the length-prefixed
  signature — there is no outer wrapper, because there is nothing to frame.
- Nonce (12) and tag (16) are raw, being fixed-width.

**A presence flag MUST be exactly `0x00` or `0x01`, and a decoder MUST REFUSE
any other byte.** This is the layout's one genuine canonicality trap, and it is
worth stating in its own right because the natural implementation gets it wrong:
a reader that returns "non-zero means present" accepts 255 distinct wire forms
for every optional field, so one model has many encodings and §3 is broken in
the format that was supposed to get §3 for nothing.

Canonicality is therefore **cheap** here rather than free. Every field is
fixed-width or minimally length-prefixed, so one model does have exactly one
encoding — but that is a property of the *reader* refusing everything else, not
a property the format enforces on its own, and "by construction" is precisely
the phrase that stops anyone checking. A decoder MUST therefore apply §3's
re-encode identity check here as well as its rule 5: re-encode what was decoded
and REFUSE unless the result is byte-identical to what arrived. It is the same
check the CBOR side already writes, it costs one encode, and it cannot drift
from the encoder because it *is* the encoder.

§2's rule that **the signature is verified over the signed-portion bytes as they
arrived** applies unchanged and is easier here than in CBOR: the signed portion
is simply the envelope's prefix, everything before the signature's length prefix.
A verifier MUST slice it out of the received bytes. Re-encoding a parsed model to
recover the signing input would hide every decoder laxity behind a normalisation
step — a forged byte would be edited away before the signature ever saw it, and
the envelope would verify as a claim nobody signed.

Measured on a representative claim plus a two-binding chain, the two encodings
land within noise of each other on the wire (CBOR came out 4% smaller). Hardened
to the same standard the CBOR codec is **373 lines against 434** — the fixed
layout is about 16% *larger*.

That number is worth dwelling on, because an earlier revision of this section
recorded 353 against 368 and argued the fixed layout got §3 and §15 row 3 "for
nothing". It did not. It got them for nothing only because they had not been
implemented: the presence-flag rule was missing, so one model had 255 wire forms
per optional field; the payload-kind set was not checked at the decoder; and
there was no re-encode identity check at all. Adding the three closed the gap and
then reversed it. **The saving attributed to a hand-specified format was
measuring absent checks, not absent code** — which is the more general lesson,
since every one of those checks is invisible until someone attacks it.

**Reach is still what decided it**: a third party meets a CBOR spec with a
library in any language, while the fixed layout obliges them to hand-write a
parser from this prose — and, as the line count now shows, to hand-write the
canonicality rules the library would have made cheap.

### 17. The interchange fixture

Cross-verification runs on bytes, not on agreement about prose. The reference
fixture is a directory of seven files, minted by one implementation and verified
by the other:

| File | What it is |
|---|---|
| `root.spki` | the root key's SPKI DER. The verifying side recomputes the domain from it rather than trusting the manifest |
| `binding-1.envelope` | root vouches issuing |
| `binding-2.envelope` | issuing vouches subject |
| `claim.envelope` | one signed claim by the subject key |
| `sealed.envelope` | one sealed claim: `payloadKind = 3`, signed by the same subject key |
| `recipient-sealing.pkcs8` | the recipient's PRIVATE sealing key, PKCS#8 DER — a throwaway fixture key, minted per export, belonging to no identity |
| `manifest.txt` | `key=value` lines naming what the verifier must expect — format and key set below |

The sealed pair is not optional. §14 fixes five construction inputs that appear
nowhere in any signed envelope, and a disagreement about any of them surfaces
only as an AEAD tag mismatch — the same failure tampering produces. A fixture
carrying only signed envelopes cross-verifies §§1–13 and leaves §14 resting on
both sides having read the same paragraph the same way. Shipping the recipient's
private key is what makes the derivation checkable at all.

A verifying implementation MUST also check that a deliberately broken fixture
FAILS. A verifier that skipped the sealed artifact entirely would otherwise pass
every check in this section.

#### What the two envelopes carry

The manifest names the values a verifier must *expect* (§11 steps 3–5), and the
fields it does not name are fixed here rather than left to be discovered from
somebody's exported bytes:

- **`claim.envelope` is directed AND carries a sequence.** Its `audience` is the
  manifest's `audience` and its `sequence` is the manifest's `sequence`. Carrying
  both exercises §8's two defences at once, and neither is optional in the
  fixture: a bearer claim would exercise one of them and a claim without a
  sequence neither.
- **`sealed.envelope` carries the SAME `audience` as the claim, and NO
  `sequence`.** It cannot be a bearer claim — §8 refuses a claim with neither an
  audience nor a sequence — so its audience has to be *something*, and making it
  the claim's is what stops the fixture growing a second value that says the same
  thing.

Those two facts are stated here rather than added to the manifest as
`sealed-audience` and `sealed-sequence` keys, and the choice is deliberate. The
manifest is **not signed**. A key describing a signed field is an unauthenticated
second source of truth about it: an implementation that took its expectation from
such a key would be trusting a line anybody can edit, and one that took it from
the envelope instead would make the key decorative. Neither reading is good, and
a value both sides already know needs no channel. `sealed-purpose` and
`sealed-plaintext` earn their keys because they genuinely differ per fixture and
the verifier cannot derive them; an audience equal to one already in the file and
a sequence that is always absent do not.

**A verifier MUST use a sequence-mark store scoped to the verification run** —
fresh each time, discarded after — rather than its production store. The claim
carries `sequence`, so §8 requires the mark to advance on acceptance; against a
store that outlived the run, the *second* verification of the same fixture file
would be refused as a replay, and be right to. This is why the sealed envelope
carries no sequence at all: one artifact demonstrating the mark is enough, and a
second would double the chance of a fixture that verifies exactly once.

#### `manifest.txt`

The manifest is a text file, and its format is fixed here in full. An
under-specified manifest is the cheapest possible way to lose a day: it parses
under everyone's reader and means slightly different things.

- **Encoding** is UTF-8. A byte-order mark MUST NOT be written; a reader MAY
  ignore one if present. Bytes that are not valid UTF-8 REFUSE the manifest —
  never substitute a replacement character, which silently changes a value.
- **Lines** are terminated by `LF` (`0x0A`), including the last one. A reader
  MUST additionally accept `CRLF`, discarding a single `CR` immediately before
  the `LF`, and MUST accept a final line with no terminator. Writing LF and
  accepting both is what keeps a fixture minted on any platform readable on any
  other; a platform's native newline is not part of this contract.
- **Empty lines are ignored.** Every other line MUST be a `key=value` pair, split
  at the **first** `=` on the line, with at least one character before it. A line
  that is neither empty nor such a pair REFUSES the manifest. Skipping it
  silently is what turns a mistyped key into an absent one, and an absent key
  into a check that quietly did not run.
- **No trimming.** Whitespace anywhere in a key or a value is part of it.
- **Keys** are case-sensitive. A key appearing **more than once** REFUSES the
  manifest — which line governs is undefined, and undefined is not resolved by
  order.
- **Values** are text. `=` needs no escape, since only the first one splits.
  Exactly three escapes exist: `\\` for a backslash, `\n` for `LF`, `\r` for
  `CR`. A backslash followed by anything else — or ending the line — REFUSES the
  manifest, so one escaped value has exactly one unescaped reading. There is no
  other escaping and no quoting.
- **Unknown keys MUST be ignored, never refused.** The key set is open: a reader
  that treated the required set as closed would reject a conforming fixture the
  moment the minting side added a note to it.

Required keys — all eight MUST be present with a **non-empty** value:

| Key | Value |
|---|---|
| `domain` | the chain's domain, lowercase hex, 64 characters. The verifier recomputes it from `root.spki` and REFUSES on disagreement rather than trusting this |
| `subject` | the claim's `subject`, verbatim |
| `algorithm` | a §4 registry name. A name outside the registry REFUSES |
| `purpose` | the claim's `purpose`. MUST NOT be `key-binding` (§8) |
| `audience` | the audience BOTH envelopes are directed at |
| `sequence` | the claim's `sequence`, decimal digits only — no sign, no separators, no leading zeros |
| `sealed-purpose` | `sealed.envelope`'s `purpose` |
| `sealed-plaintext` | what a correct unseal of `sealed.envelope` MUST produce, as **UTF-8 text** after unescaping — not hex. The three escapes are what let it hold a line break |

Optional keys carry no obligation on either side. `minted-by` names the
implementation that exported the fixture and is worth writing, because the first
question about a failing cross-check is whose bytes these are.

#### The tool protocol

Two implementations can agree on every byte in the table above and still never
cross-check, because nothing so far says how one of them is *run*. Two verbs,
each taking exactly one argument — the fixture directory:

| Verb | What it does |
|---|---|
| `export <directory>` | mints a fresh chain and writes the seven files, creating the directory if absent |
| `verify <directory>` | reads the fixture, pins the root recomputed from `root.spki`, and runs every check this section requires |

The exit code is the machine-readable verdict, and it is normative:

| Code | Meaning |
|---|---|
| `0` | every check passed |
| non-zero | at least one check failed |

`2` is reserved for a command line that was not understood — an unknown verb, a
missing or extra argument. It is a non-zero exit like any other, and a caller
scripting the cross-check needs no more than zero-versus-non-zero; the
reservation exists so that a tool that wants to distinguish "I did not run" from
"I ran and something failed" has one place to do it, rather than two
implementations picking different codes for it.

A verb SHOULD also report each check in a human-readable form naming the check —
the text is not fixed (§0), only the exit code is.

**A crash is not a permitted way to signal failure.** Every file in the directory
is input, and input that is missing, truncated, corrupt, or simply not what it
claims to be is a **failed check**: the verb MUST report it, name it, and exit
non-zero. Terminating abnormally — an unhandled exception, an abort, a signal —
does not satisfy this contract however unambiguous the stack trace looks, because
the reader on the other end cannot distinguish *your bytes are bad* from *your
tool fell over*, and those are different verdicts leading to different next
steps. An implementation is easy to get wrong here in one specific way: the
fixture it tests itself against is the one it just minted, so the malformed-input
path is the one path its own round trip never takes. Feed it a corrupted copy on
purpose.

`export` obeys the same rule: a directory it cannot create or a file it cannot
write is a reported failure and a non-zero exit, not a stack trace.
