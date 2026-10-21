# Security review reference

Start with the [researcher brief](security-review.md) for orientation, priorities,
and a first local run. This reference supplies the detailed source map, repaired
defects, remaining assumptions, and wider test selections. The local Windows preflight
consists of source review and targeted executable regressions. Deployment exposure,
cloud policy, sustained fuzzing, and native-runtime exploit resistance need
separate evidence. Passing these tests is not a security certification.

## Trust boundaries

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
   transfer. The existing [open items](../plans/open-items.md#standalone) identify
   dynamic transform disclosure as a recorded weakness and the authored-binding
   escalation witness as missing executable evidence. The latter is not a
   demonstrated bypass. This preflight does not establish closure of either.
3. **Release crash consistency.** Compare replay-store advancement, staging,
   rollout eligibility, and the install pointer as one recovery problem.
   The recoverable install intent and writer leases have local fault-injection
   and separate-instance contention cases. Extend these to real process kills,
   stub rollback races, and storage/power faults: `AtomicFile` flushes file
   contents before rename, which alone does not establish durable directory
   ordering on every supported filesystem. Verify install-root ownership and
   adversarial symlink/reparse behavior under each supported OS.
4. **Deployed identity policy.** Inspect actual JWT audience/issuer/tenant
   settings, app roles, consent and delegated scopes, storage ACLs, key custody,
   endpoint exposure, and revocation procedures. Local generated-key and HTTP
   fixtures do not prove the deployed configuration.
5. **Hostile resource use.** Measure aggregate unauthenticated connections,
   authenticated invalid-signature traffic, key enumeration, document depth and
   expansion, WASM compilation, multiple guest memories, and guest lifetime.
   Per-message or per-memory caps do not establish an aggregate process budget.
   World pre-admission has a 64-slot limit and a deadline; authenticated
   federation connections have no equivalent active-connection ceiling.
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
| A failed update consumed its sequence | Rollout exclusion, staging failure, or an interrupted install could prevent retrying the same signed release. | Rollout and staging precede a persisted install intent and exact-manifest replay reservation. Recovery revalidates current trust, checks content, and refuses newer marks/pointers. `UpdateInstallTransactionTests` injects failures around reservation and pointer activation. |
| Restaging inherited files outside the signed file set | After an interruption before intent persistence, a replacement signed manifest for the same version could omit a file that remained in the version directory and was activated. This requires two genuinely signed manifests; no local attacker write is needed. | Staging rebuilds the inactive version directory from the complete declared file set under the install lease. `UpdateInstallTransactionTests.InterruptedStage_ReplacementManifestCannotInheritFilesItDoesNotDeclare` fails on the former behavior and passes with cleanup. |

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

The remaining resource and identity questions have specific entry points:

- `WasmModuleLoader.Load` reads and compiles the whole module before mount
  admission. Its cache limits entries, not source bytes, compiler work, or code
  bytes. Execution fuel does not bound compilation.
- `AddonInstance.Instantiate` limits each memory to 16 MiB. Multiple memories,
  tables, and compiled code need an aggregate budget. Existing tests deliberately
  accept an additional hidden memory within its individual ceiling.
- The module cache uses `AssetContentHash`, the first 64 bits of SHA-256, as its
  sole identity. A collision would alias modules; no collision or poisoning
  exploit was generated. The inspected mounting route comes from an authored
  document and local assets under operator control. No ordinary remote-player
  upload-to-mount route was established. Treat the truncated hash as a
  determinism identifier when reviewing hostile-code approval.

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
the selected Server cases include a child-process helper that skips when run
without its parent fixture. Read the test output for any other skip or failure.

```powershell
dotnet build tests/Puck.Networking.Tests -c Release -nodeReuse:false
dotnet tests/Puck.Networking.Tests/bin/Release/net11.0/Puck.Networking.Tests.dll --progress off

dotnet build tests/Puck.Azure.Tests -c Release -nodeReuse:false
dotnet tests/Puck.Azure.Tests/bin/Release/net11.0/Puck.Azure.Tests.dll --progress off

dotnet build tests/Puck.Mcp.Tests -c Release -nodeReuse:false
dotnet tests/Puck.Mcp.Tests/bin/Release/net11.0/Puck.Mcp.Tests.dll --filter-not-trait Category=Gpu --progress off

dotnet build tests/Puck.World.Presentation.Tests -c Release -nodeReuse:false
dotnet tests/Puck.World.Presentation.Tests/bin/Release/net11.0/Puck.World.Presentation.Tests.dll --filter-class '*FederationTransferLawTests' --filter-class '*FederationOnwardRouteLawTests' --filter-class '*SessionObservationLawTests' --filter-not-trait Category=Gpu --progress off

dotnet build tests/Puck.World.Server.Tests -c Release -nodeReuse:false
dotnet tests/Puck.World.Server.Tests/bin/Release/net11.0/Puck.World.Server.Tests.dll --filter-class '*Admission*' --filter-class '*Grant*' --filter-class '*Disclosure*' --filter-class '*Transfer*' --filter-class '*Recovery*' --filter-class '*SessionPrincipalLawTests' --filter-class '*AddonGuestLawTests' --filter-class '*AddonPrepareGateLawTests' --filter-not-trait Category=Gpu --progress off

dotnet build tests/Puck.World.Azure.Tests -c Release -nodeReuse:false
dotnet tests/Puck.World.Azure.Tests/bin/Release/net11.0/Puck.World.Azure.Tests.dll --filter-not-trait Category=Gpu --progress off
```

A full presentation or launcher run includes unrelated runtime work. See
[verification](../../.claude/skills/verification/SKILL.md) for CPU/GPU selection
and the build-before-run rule. Unix transport behavior, real process-kill
recovery, and deployed policy need their own environments and experiments.

The preflight's source searches use ordinary text search after the repository
search/semantic tools failed in the session. Text matches are navigation aids,
not proof that all semantic callers have been found. Exact commands and results
belong in the accompanying commits; untested areas remain explicit above.
