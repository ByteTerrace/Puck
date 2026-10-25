# Security review reference

Start with the [researcher brief](security-review.md) for orientation, priorities,
and a first local run. This reference supplies the detailed source map, repaired
defects, remaining assumptions, and wider test selections. The local preflight
consists of source review and targeted executable regressions on Windows, with
attestation, networking and release selections also run on Linux in WSL. Deployment exposure,
cloud policy, sustained fuzzing, and native-runtime exploit resistance need
separate evidence. Passing these tests is not a security certification.

## Trust boundaries

Azure Front Door configuration is out of scope under the owner's ruling. The
`api` rule set's authorization-header handling is assumed correct. Azure
findings must stand independently of a proxy misconfiguration.

| Actor | Authority to assume | Boundary to try to cross |
|---|---|---|
| Unauthenticated network peer | Can create its own key and complete the generic QUIC peer handshake. | World admission, an expected federation namespace, or unbounded pre-admission work. |
| Admitted player | Only its stamped principal, current grants, and disclosed state. | Another player's effects, hidden state, console-only operations, or grants surviving retirement. |
| Federated authority | Can sign claims as its trusted namespace; claims can still be dishonest. | Another source's transfer, stale ownership, duplicate arrivals, or unauthorized forwarded commands. |
| Account controlling its own blob storage | Can replace its public key files and copy public attestations. | Renewal of another account's binding or excessive issuer work. |
| Release source or CDN | Can suppress, reorder, substitute, truncate, or enlarge responses. | Installation without the expected signed app/channel, payload hash/length, replay policy, and confined paths. |
| Addon author | Controls guest bytecode and every output cell. | Host effects beyond granted channels, host memory, or excessive compilation/runtime resources. |
| Another process under the same OS user | Already shares the local-control trust boundary. | Treat separately from a remote peer or a different OS user. |

The peer certificate proves possession of a key; authority names require
application authentication. An attestation establishes a signed statement under
a receiver's trust policy. It does not establish that a remote simulation ran
honestly. For sequenced claims, verification returns a replay requirement that
the receiving effect must commit durably.

## Entry points and regression map

| Area | Start reading | Properties and executable witnesses |
|---|---|---|
| Attestation | [Verifier](../../src/Puck.Attestation/AttestationVerifier.cs), [profile](../../src/Puck.Attestation/AttestationProfile.cs), [CBOR codec](../../src/Puck.Attestation/CborAttestationCodec.cs), [sealing](../../src/Puck.Attestation/SealedAttestation.cs) | Both public verification entry points enforce the selected profile. `ConformanceProfileTests.VerifierEntryPoint_EnforcesTheSelectedProfilesPayloadCeiling` accepts the exact limit and refuses one extra byte. `ArrivedBytesTests` sweeps signature/input mutations; `ChainTests`, `ClockAndSequenceTests`, and `SealedAttestationTests` cover trust, replay transactions, and authenticated decryption. |
| Peer transport and wire | [Peer handshake](../../src/Puck.Networking/Peers/PeerHandshake.cs), [peer link](../../src/Puck.Networking/Peers/PeerLink.cs), [wire codec](../../src/Puck.Networking/WireCodec.cs) | `ChannelBindingTests`, `IdentityKeyTests`, `RefusalTests`, timeout/lifecycle tests, and `FrameLawTests` exercise wrong certificates/keys, malformed claims, framing limits, and bounded stalled operations. |
| World authority and federation | [World peer host](../../src/Puck.World.Server/WorldPeerHost.cs), [remote authority](../../src/Puck.World.Server/WorldRemoteAuthority.cs), [escrow](../../src/Puck.World.Server/WorldTransferEscrow.cs) | Review proof-to-principal mapping, both directions of federation authentication, grants at the actual effect, mobility generations/epochs, source-scoped transfer keys, and recovery after an uncertain commit. |
| Key issuance and MCP | [Key endpoint](../../src/Puck.Azure.Functions/HttpTriggers/AttestKeys.cs), [binding service](../../src/Puck.Azure.Functions/Services/DefaultBindingService.cs), [MCP](../../src/Puck.Mcp/README.md), [local capability](../../src/Puck.Networking/LocalEndpointCapability.cs) | Trace the authenticated account through storage ownership and the exact renewed subject/key. MCP attachment, row retirement, role, token, and downstream claims-challenge checks belong to the effect's authorization path. |
| Releases | [Verifier](../../src/Puck.Launcher/Release/AttestationReleaseVerifier.cs), [update service](../../src/Puck.Launcher/Release/UpdateService.cs), [stager](../../src/Puck.Launcher/Release/ContentAddressedUpdateStager.cs), [applier](../../src/Puck.Launcher/Release/FileUpdateApplier.cs), [stub](../../src/Puck.Launcher.Stub/Program.cs) | Follow the signed manifest through app/channel checks, replay admission, download bounds, exact payload identity, portable paths, current-pointer replacement, and rollback. |
| Guest execution | [Scripting](../../src/Puck.Scripting/README.md), [addons](../../src/Puck.World.Addons/README.md) | Inspect import rejection, requested/granted channel intersection, host-stamped principals, live grants, cell parsing, pointer bounds, fuel, aggregate memory, and compilation work before admission. |

## Independent review priorities

1. **Federation failure schedules.** Interrupt at each reserve, detach, commit,
   acknowledgement, checkpoint, and publication boundary. Reorder and duplicate
   requests across reconnection and process restart. An authenticated hostile
   authority must not obtain two live owners or operate with a retired epoch.
   Exercise both directly pinned keys and root/issuer/subject chains, and the
   explicitly pinned Entra transport path. Authentication and the federation
   disclosure ceiling are selected when a persistent connection opens. Establish
   when trust removal, token expiry, or an admission-policy change must terminate
   or restrict an existing connection. Immediate revocation is not established
   by these checks; per-effect grants are a separate gate.
2. **Authorization and disclosure composition.** Follow actions to their final
   receiving world rather than stopping at signature verification. Include live
   grant changes, entity-slot reuse, forwarded commands, and observation after
   transfer. Challenge the resolved transform dependencies, runtime privacy
   checks, and intentional disclosure through a rule's own writes. The authored
   binding witness installs the real command registry and uses a principal with
   only a binding-overlay mutation grant; it covers that admission path, not
   whether every shipped command has the right bindability classification.
3. **Release crash consistency.** Compare replay-store advancement, staging,
   rollout eligibility, and the install pointer as one recovery problem.
   The recoverable install intent and writer leases have local fault-injection
   and separate-instance contention cases. Real child-process termination covers
   both sides of replay reservation and pointer activation. Extend these to
   stub rollback races and storage/power faults: `AtomicFile` flushes file
   contents before rename, which alone does not establish durable directory
   ordering on every supported filesystem. Verify install-root ownership and
   adversarial symlink/reparse behavior under each supported OS.
4. **Deployed identity policy.** Inspect actual JWT audience/issuer/tenant
   settings, app roles, consent and delegated scopes, storage ACLs, key custody,
   endpoint exposure, and revocation procedures. Local generated-key and HTTP
   fixtures do not prove the deployed configuration.
5. **Hostile resource use.** Measure aggregate unauthenticated connections,
   authenticated invalid-signature traffic, key enumeration, document depth and
   expansion, WASM compilation, aggregate guest resources, and guest lifetime.
   Per-message or per-memory caps do not establish an aggregate process budget.
   World pre-admission has a 64-slot limit and a deadline; authenticated
   federation sessions now have a 256-connection ceiling per host, also bounding
   remote snapshot fanout. Aggregate retained bytes across hosts remain open.
6. **Cryptographic composition and portability.** Review exact signed bytes,
   purpose/role separation, curve import behavior across platforms, key rotation,
   replay-horizon changes, and the custom sealing construction. Puck uses
   ECDSA P-256/SHA-256 and ephemeral/static ECDH with HKDF and AES-GCM; it does
   not implement the HPKE wire protocol. Its static recipient key can decrypt
   recorded payloads after compromise, a limitation shared with the public-key
   recipient model discussed in [RFC 9180 section 9.7.4](https://www.rfc-editor.org/rfc/rfc9180.html#section-9.7.4).

## Local findings and regression cases

These are source-confirmed defects corrected by the preflight. The conditions
matter: malformed input, a trusted but hostile authority, and an attacker with
someone else's private key are different threat models.

| Defect | Previous behavior and reachability | Current check and regression |
|---|---|---|
| Profile limits differed between entry points | A caller using `AttestationVerifier.VerifyChain` directly could accept a payload above the selected profile's ceiling. | Both public entry points use the same profile validation. `ConformanceProfileTests.VerifierEntryPoint_EnforcesTheSelectedProfilesPayloadCeiling` covers the exact boundary and one extra byte. |
| Federation destination identity lacked proof | A peer completing the generic transport handshake could acknowledge an expected authority name without proving that authority. The source proof also lacked connection binding. | Reciprocal application proofs bind both nonces, ordered transport-key fingerprints, direction, and the destination name; an explicit transport pin is the alternative for bearer-token clients. `FederationTransferLawTests` covers unsigned/unknown/wrong-subject destinations and altered transcript components over loopback QUIC. |
| Transfer arithmetic could partially mutate admission | A trusted source could send extreme lease values that overflowed, or an exhausted mobility epoch that threw after admitting a traveler. | Wide deadline arithmetic refuses values outside the destination range; reserve refuses an epoch that cannot advance. Hostile wire-decoded cases live in `AdmissionArrivalLawTests`. |
| Binding renewal did not bind the caller | A user could copy another account's issuer-signed public binding into their own storage and renew its validity. Impersonation additionally requires the matching private key and an accepted trust chain. | Renewal matches the authenticated subject, algorithm, fingerprint, and exact public key. `SubjectBindingRenewalTests` includes legitimate expired-key renewal controls. |
| Key-material downloads were unbounded | A user controlling their own key blobs could force whole-blob buffering before parsing. | PEM and binding reads are bounded independently of storage range support. `StoredKeyMaterialTests` proves reads stop after one excess byte. |
| Counterpart HTTP intake preceded size checks | The public [counterpart resolver](../../src/Puck.World.Azure/WorldApiCounterpartResolver.cs) buffered complete responses, including error bodies, before decoding limits applied. A custom host's configured API could supply excessive bytes. No shipped host wiring or deployed attacker-controlled publication route was found. | Headers are read first, error bodies are skipped, and successful bodies admit at most 196,653 bytes, with one extra byte read to detect excess. The original operation deadline covers streamed reads. [Transport cases](../../tests/Puck.World.Azure.Tests/CounterpartAttestationLawTests.Transport.cs) cover misleading/missing lengths, error statuses, a legitimate signed response, maximum component sizes, and stalled-read cancellation. |
| Release paths and null shapes were insufficiently validated | Signed malformed paths could escape intended directories on Windows; version metadata could carry traversal. Unsigned null-shaped responses could throw during verification. | Portable relative paths, single-component versions, case aliases, devices, and null members are checked before filesystem use. `ReleaseManifestTests` and `AttestationReleaseVerifierTests` cover these inputs. |
| Release channel substitution | A source could serve a correctly signed release for another channel of the same app. | The configured channel must match the signed manifest. `UpdateServiceTests` uses a real generated signing chain. |
| Release replay retention and comparison | Changing signer discarded the previous signer's persisted mark; racing verification could both accept or lower a mark. | Each signer retains its mark; file-backed compare-and-advance excludes other writers. `ReleaseSequenceStoreTests` and `AttestationReleaseVerifierTests` cover reopen, corruption, and contention. |
| Release bytes exceeded their contract | HTTP eagerly buffered responses and ignored signed payload lengths; staging trusted cache length implicitly. | Manifest intake has a byte cap; payloads stream under their signed exact length and staging checks size plus SHA-256. `HttpReleaseSourceTests` covers missing length headers, truncation, and excess bytes. |
| Successive applies could downgrade | The running process's installed version could lag the already updated on-disk pointer. | Apply compares against the current pointer before replacing it. `FileUpdateApplierTests` covers equal and older versions after an apply. |
| A failed update consumed its sequence | Rollout exclusion, staging failure, or an interrupted install could prevent retrying the same signed release. | Rollout and staging precede a persisted install intent and exact-manifest replay reservation. Recovery revalidates current trust, checks content, and refuses newer marks/pointers. `UpdateInstallTransactionTests` injects failures and [terminates real updater processes](../../tests/Puck.Launcher.Tests/Release/UpdateInstallTransactionTests.Process.cs) around reservation and pointer activation. |
| Restaging inherited files outside the signed file set | After an interruption before intent persistence, a replacement signed manifest for the same version could omit a file that remained in the version directory and was activated. This requires two genuinely signed manifests; no local attacker write is needed. | Staging rebuilds the inactive version directory from the complete declared file set under the install lease. `UpdateInstallTransactionTests.InterruptedStage_ReplacementManifestCannotInheritFilesItDoesNotDeclare` fails on the former behavior and passes with cleanup. |
| Transform dependencies omitted hidden inputs | Authored transforms could use hidden selectors, control conditions or source membership to influence public output. A principal already granted mutation and edit rights could also sort a public row after one of its cells became private. | Resolved dependencies and the shared arena preflight check whole read rows, runtime cell visibility and containing zones before writes. [Compiler/evaluator cases](../../tests/Puck.State.Rules.Tests/TransformDisclosureLawTests.cs) include rollback; [server cases](../../tests/Puck.World.Server.Tests/TransformDisclosureLawTests.cs) exercise a restricted principal and a confined control. This is conservative admission, not precise dynamic taint tracking. |
| Addon identity used truncated SHA-256 | The compilation cache and authored integrity pins used only the first 64 bits. Distinct approved bytes could alias if such a collision were supplied; no cryptographic collision was generated. | Addon approval pins and cache keys use full SHA-256. [Asset cases](../../tests/Puck.Assets.Tests/BoundedAssetLoadingTests.cs) use synthetic digests with equal short prefixes; [guest cases](../../tests/Puck.World.Server.Tests/AddonGuestLawTests.cs) refuse the former short pin and a changed full-pin suffix, with a valid-pin control. |
| Addon intake and guest resources lacked combined limits | Whole source files reached native parsing/compilation before a size check. A guest could declare additional hidden memories within each memory's individual ceiling. This requires an authorized addon mount; no ordinary remote-player upload route was found. | Bounded source reads and declaration checks precede native compilation. The store limits instance, memory and table counts and growth; [guest cases](../../tests/Puck.World.Server.Tests/AddonGuestLawTests.cs) include hidden resources, boundary controls and cache-eviction lifetime. Native compiler workspace and aggregate host budgets remain separate questions. |
| Release signing subject could name another app | A signing key admitted for another subject under the trusted root could sign canonical bytes naming the target app. A CDN without that key cannot forge this claim. | The replay requirement's subject must equal the manifest app before sequence reservation. `AttestationReleaseVerifierTests.Verify_Refuses_ClaimSubjectMismatchingManifestApp` failed with the check withheld and passed with it. |
| Release directory names admitted ambiguous metadata | Channels admitted traversal/separators and leading whitespace; version names admitted leading spaces that pointer readers trim. Exploitation of signed metadata requires an authorized signer. | Channels are single portable components and path segments reject leading spaces. `ReleaseManifestTests` has four rejected-channel cases and a leading-space version case, each failing without the fix. |
| Signed payload could occupy install metadata | A signer could declare `state-generation` as a payload file; activation would overwrite those already-hashed bytes with install metadata. | The filename is reserved case-insensitively. `Validate_RefusesReservedStateGenerationPayloadFile` failed without the exclusion and passed with it. |
| Rollback left a stale minimum-supported check | An interrupted valid signed update could resume after rollback below its minimum supported version because recovery checked the former installed version. | Recovery checks the current pointer before staging. `Recovery_RefusesWhenRollbackDropsCurrentBelowPendingIntentsMinimumSupported` reached staging without the fix and refuses with it. |
| Case-alias release could reuse an active directory | Case-sensitive prerelease ordering could choose a case alias of current or last-good on a case-insensitive filesystem. Requires a signed alias release and an existing active directory. | The service refuses before staging and the applier independently refuses activation. `Apply_RefusesCaseInsensitiveVersionCollisionWithActiveInstall` and both `Apply_RefusesCaseAliasOfAnActiveDirectory` cases failed without the checks and pass with them. |
| Rule-local evaluation escaped transform control checks | A hidden local can affect whether evaluation reaches a transform, including through expression failure. Requires an authored rule, or a later runtime visibility restriction. | The compiler includes all compiled local reads in transform controls. Both `HiddenRuleLocalsCannotControlAPublicTransform` cases failed without the fix and pass with it. This remains conservative audience enforcement, not precise taint analysis. |
| Push-ray disclosure used field indices as row ordinals | An authored pool transform checked unrelated arena rows and omitted its pool domain. | The check resolves domain and backing-field rows through the pool catalog. `PushRayDisclosureChecksThePoolsOwnBackingRows` failed with an incorrect refusal without the fix and passes with it; this witness proves the mapping defect, not an end-to-end disclosure exploit. |
| Parenthesized vector subject named the operand instead of its row | An authored transform reported `(secretVec[a])` as its read subject, obscuring the actual row from consumers. | Balanced enclosing parentheses are stripped before subject extraction. `ParenthesizedVectorCellOperandListsUnderlyingRowAsReadSubject` failed without stripping and passes with it; the witness does not establish a complete remote authorization bypass. |
| Decision transforms omitted remaining local controls | Decision-only effects did not inherit every local evaluation dependency. An authored hidden local can fault before effects; the restriction is conservative compiler policy. | World compilation collects compiled local reads for decision transforms. `DecisionDisclosureTests.LocalEvaluationCannotControlATransformOutsideItsAudience` fails for the public destination without the fix and passes with it; the confined control passes both. Direct local-score reads were already protected. |

The `codex-security` follow-up withdrew the unverified WIP before reintroducing
individually proven changes. Its original prose about pruning and untrimmed stub
pointer parsing did not match the patch and was removed. The stub whitespace
change remains withdrawn; the open-items entry records why. Decision-local checks
have their own schema regression. Full launcher, state-rules, state and world-schema
suites passed on the merged integration head after fresh Release builds on Windows.
No World was booted and no GPU or deployment check was performed. Remaining review
candidates are tracked separately and are not established by these runs.

The SAS trigger uses the authenticated caller's on-behalf-of storage credential.
Accepting a container, blob and permission selection is not by itself evidence
that storage honors authority the caller lacks. Initial key issuance also binds
the caller's subject, not an arbitrary target account. Deployment policy and any
proof-of-possession requirement need separate evidence. The Entra path uses a
bearer token with an explicitly pinned server transport key; the earlier review
did not demonstrate a bypass of that pin. See the scoped questions in
[open items](../plans/open-items.md#standalone).

Install recovery treats a protected local intent as already-authorized work.
It checks the current trust policy at the original verification instant; expiry
alone does not cancel that work. The complete signed manifest is pinned with
SHA-256, a network response cannot replace a pending intent, and a newer replay
mark prevents the older intent from resuming. Fresh network checks always use
the current verification instant. The stub shares the updater's writer lease
and cancels a matching rejected-version intent before rolling back, while
preserving unrelated newer pending work. These are explicit policy choices for the
researcher to review beside the fault-injection cases.

## Guest and control-plane limits

The reviewed WASM path rejects imports, freezes declared channels, validates
whole output batches, stamps the addon principal in the host, and checks live
grants before effects. No host-authority escape was established in those paths.
`AddonGuestLawTests` and `AddonPrepareGateLawTests` exercise guest confinement
and mount admission; they do not audit Wasmtime's native implementation.

`WasmModuleLoader.Load` requests a bounded asset read and checks the full
authored SHA-256 pin before parsing or cache lookup. Source and converted binary
each have a 16 MiB ceiling. Imports and excessive declared memory/table
resources refuse before native compilation. One guest store admits one instance,
one 32-bit memory up to 16 MiB, and one table up to 65,536 elements, including
growth. GC heaps, typed function references and multiple memories are disabled.
The [scripting reference](../reference/scripting.md) owns these limits.

Document admission also caps addon count, total per-tick fuel, capability
requests and memory watches (`WorldAddonLimits`). Native source loading, WAT
conversion and compilation share four process-wide slots; a full loader refuses
with a retryable capacity error and releases its slot on every exit. Transfer
escrow caps lease duration at 120 seconds, retained transactions at 16,384 and
mobility admissions at 65,536 (`WorldTransferLimits`). Full history refuses new
work while preserving exact commit retries and consumed epochs; acknowledgement
and traveler retirement release capacity. The resource tests exercise these
boundaries and recovery; these counts do not establish a native CPU deadline.

The remaining resource questions have specific entry points:

- WAT conversion and `Module.FromBytes` still execute native code without a
  proven CPU deadline or peak-workspace bound. Execution fuel covers the guest,
  not its compiler.
- Per-store limits and the module cache's entry count do not establish an
  aggregate retained-byte budget across worlds, guests and compiled modules.
  Cache eviction retains the lifetime of modules used by active guests; native
  reclamation is reference/finalizer driven.
- An expected integrity pin must come from a trusted publication or authoring
  path. Full SHA-256 identifies approved bytes; it does not decide who may
  approve an addon. The inspected mount path uses authored documents and local
  assets; an ordinary remote-player upload-to-mount route was not established.

The [binding-authority witness](../../tests/Puck.World.Presentation.Tests/AuthoredBindingAuthorityLawTests.cs)
uses a host-stamped addon with exactly one budgeted binding-overlay grant. It
submits a real mutation naming shipped unbindable `world.grant`, requires refusal
without changing the document, and applies a bindable control. Weakening the
guard makes that witness fail. It proves metadata admission, not every command's
classification or a physical input gesture.

MCP review covers token policy, attachment ownership and retirement, downstream
authorization, and the local descriptor/HMAC path. No additional authorization
bypass was confirmed. Equal or absent token `iat` during claims-challenge
recovery remains a policy question: same-second tokens can be legitimate, and
downstream services still authorize their effects. Key enumeration per account,
issuance rate limits, and the deployed issuing-key custody require operational
evidence. Hostile document/package parsing outside the named paths was not
exhaustively reviewed.

## Running local checks

Build the selected test project in Release with `-nodeReuse:false`, then run
that build's executable test assembly. For example:

```text
dotnet build tests/Puck.Attestation.Tests -c Release -nodeReuse:false
dotnet tests/Puck.Attestation.Tests/bin/Release/net11.0/Puck.Attestation.Tests.dll --progress off
```

The brief also gives the release selection. For the remaining boundaries, run
the relevant build/run pair below from the repository root in Windows
PowerShell. A successful build must precede each run. The tests supply their
own temporary state and loopback peers; these selections require no deployed
world or cloud account. Two Networking cases require Unix and skip on Windows;
the selected Server, Presentation and Release cases include child-process helpers
that skip when run without their parent fixtures. The parents run them explicitly.
On Linux, the Windows ACL case skips instead. Read the test output for any other
skip or failure.

```powershell
dotnet build tests/Puck.Networking.Tests -c Release -nodeReuse:false
dotnet tests/Puck.Networking.Tests/bin/Release/net11.0/Puck.Networking.Tests.dll --progress off

dotnet build tests/Puck.Azure.Tests -c Release -nodeReuse:false
dotnet tests/Puck.Azure.Tests/bin/Release/net11.0/Puck.Azure.Tests.dll --progress off

dotnet build tests/Puck.Mcp.Tests -c Release -nodeReuse:false
dotnet tests/Puck.Mcp.Tests/bin/Release/net11.0/Puck.Mcp.Tests.dll --filter-not-trait Category=Gpu --progress off

dotnet build tests/Puck.World.Presentation.Tests -c Release -nodeReuse:false
dotnet tests/Puck.World.Presentation.Tests/bin/Release/net11.0/Puck.World.Presentation.Tests.dll --filter-class '*FederationTransferLawTests' --filter-class '*FederationOnwardRouteLawTests' --filter-class '*SessionObservationLawTests' --filter-class '*AuthoredBindingAuthorityLawTests' --filter-not-trait Category=Gpu --progress off

dotnet build tests/Puck.State.Rules.Tests -c Release -nodeReuse:false
dotnet tests/Puck.State.Rules.Tests/bin/Release/net11.0/Puck.State.Rules.Tests.dll --filter-class '*TransformDisclosureLawTests' --progress off

dotnet build tests/Puck.World.Server.Tests -c Release -nodeReuse:false
dotnet tests/Puck.World.Server.Tests/bin/Release/net11.0/Puck.World.Server.Tests.dll --filter-class '*Admission*' --filter-class '*Grant*' --filter-class '*Disclosure*' --filter-class '*Transfer*' --filter-class '*Recovery*' --filter-class '*SessionPrincipalLawTests' --filter-class '*AddonGuestLawTests' --filter-class '*AddonPrepareGateLawTests' --filter-not-trait Category=Gpu --progress off

dotnet build tests/Puck.World.Azure.Tests -c Release -nodeReuse:false
dotnet tests/Puck.World.Azure.Tests/bin/Release/net11.0/Puck.World.Azure.Tests.dll --filter-not-trait Category=Gpu --progress off
```

A full presentation or launcher run includes unrelated runtime work. See
[verification](../../.claude/skills/verification/SKILL.md) for CPU/GPU selection
and the build-before-run rule. The same attestation, networking and release
assembly selections run on Linux with the pinned runtime. Child-process tests
require that runtime's `dotnet` on PATH. WSL results cover those fixtures and
Linux temporary filesystem operations, not every production Linux distribution,
filesystem, native package or deployment policy.

The preflight's source searches use ordinary text search after the repository
search/semantic tools failed in the session. Text matches are navigation aids,
not proof that all semantic callers have been found. Exact commands and results
belong in the accompanying commits; untested areas remain explicit above.
