# D1 progress

Read delegate and d1-common first; clean lane/d1 at start. Read state,
puck-world, verification, boy-scout and recurring hazards.

Invariant before implementation: WorldGrants owns a live operator predicate
and effective-authority transition admission. A new grant/group installer
must need a prepared transition minted only there; raw writes become private.
Admission uses the actor's authority at transaction start and checks every
batch prefix and the final candidate. Operate is not exposed until the entire
escalation closure exists.

Initial evidence: Principal.IsAuthority is kind keyed; WorldGrants.IsTrusted
includes seats; WorldGrants.TryGrant, Revoke and SyncGroups are public. The
existing role-qualified index in WorldGrants.RoleAuthority.cs is the reuse
candidate for effective group authority. Document installation owns the group
resync; direct grants use WorldGrants.Application.cs.

First CLI build could not initialize .NET telemetry inside the sandbox
(registry SecurityException); normal-access build admitted and running in
exec session 82647. No World or standalone background watcher started.

## Hand-back status: blocked WIP

This run did not complete the central authority transition. That is unfinished
implementation, not a product decision or a claimed external source blocker.
No Operate capability was added. No seat or peer operator capability is restored.
The invariant above remains a design requirement; nothing enforces it yet.

Built groundwork:

- Host audience admitted only for Console at the existing registry dispatch
  boundary, authored Host calls rejected by AuthoredCommandSet, silo Host calls
  excluded. Reclassified host resources, disk-backed replay, load/save, instance
  lifecycle, pipeline loading and wire diagnostics. Operator remains Console.
- Peer report formatter separates population/connection/tier/refusal data from
  host endpoint and verified identity fields. Population includes live control
  sessions without a QUIC listener. The nonhost branch is preparatory: current
  Operator dispatch still cannot reach it from a seat or peer.
- Host dispatch/authored-call laws added; existing World audience law adjusted.
- Security packet and D1 build table explicitly retain the unfinished work.

Remaining, to land atomically: live predicate; replacement of all kind-keyed
trust; effective-authority admission for direct grants, group/role/ownership
and grants-section writes (including both batch orders and state-fed membership);
own-body consent; single-subject admission and laundering restrictions; peer
disconnect and silo remote operator admission; tick-correct revocation reseeding;
disclosure observers with Operate INVALID; all positive operator and refusal laws.

## Continuation map and systems considered

Used state, puck-world, verification, boy-scout and symbol-analysis skills.
Their useful routing was to the existing grant indexes, document composition
and registry boundary; no new parallel authorization system was built.

`puck references IsAuthority --containing Puck.Commands.Principal --no-doc`
finds Principal.cs:66 and consumers across WorldDocument.Apply/Compose/
Compose.Batch/Rebuild, StateCommands, checkpoint/replay, rule queries,
StateReadView/disclosure and console population/mutation/view/history modules.
The qualified symbol query `references Principal.IsAuthority` was not valid;
the simple-name query above is the useful one.
`puck references IsTrusted --containing WorldGrants --no-doc` reaches application,
grant validation and WorldServer.Arena. `references HoldsForAdministration
--containing WorldGrants --no-doc` reaches both direct administration branches.
These reference searches established scope; they did not replace any consumer.

- src/Puck.World.Server/WorldGrants.RoleAuthority.cs:174: public SyncGroups;
  reuse its role-qualified membership and ownership indexes for candidate closure.
- src/Puck.World.Server/WorldGrants.cs:1411/1476: HoldsForAdministration and
  IsTrusted remain kind-based. TryGrant, Revoke and restore paths also need
  inaccessible raw writes, not a helper every caller remembers.
- src/Puck.World.Server/WorldDocument.Apply.cs:1132/1145: Install performs group
  synchronization; detached composition, batch prefixes, rebuild, checkpoint
  and replay installation must carry the prepared transition authority.
- Existing WorldStateDocumentValues.CollectReferencedRows/ReferencesRow is the
  candidate for state-fed membership dependency collection; do not add another
  reflection walker. Authored grant seeding currently iterates declarations,
  so whole-candidate validation cannot rely on declaration order.
- Sessions currently carry no retained tier entitlement in their checkpoint;
  live operator qualification must not invent a tier from holding Operate.
- src/Puck.World.Server/WorldPopulation.Admission.cs:446/462: parked peers stay
  admitted. src/Puck.World.Silo/SiloControlSessionHost.cs:15 checks admission and
  generation but not parked. Inference: peer disconnect must close transport
  and command admission; parking alone is insufficient. Added to Security packet.

Duplicate scan of the working tree: 23 changed declarations, 25 neighbours,
0 helper flags. Read every neighbour record. Twenty-three are existing command
registration factory shapes (GpuCapture/GpuFaults/Counters against Rows,
Annotation and Passthrough; Inspection against Mode/Placement/History/Identify/
Editor; Registry built-ins against Annotation/DebugView/Flag/Gesture/Rows;
Replay against Recording). They already use the shared CommandDefinition
factories and differ in actual handlers and forms; no duplicate mechanism is
introduced by changing an audience argument. Describe vs DescribeFault is an
enum-to-prose switch over unrelated domains. Silo Allows vs machine declaration
equality shares boolean syntax, not an operation. No helper extraction is warranted.
The exact committed-head scan result is recorded below after the WIP commit.

## Checks and lifecycle

Release builds passed with zero warnings/errors: Puck.Cli, Puck.World,
Puck.Commands.Tests and Puck.World.Tests (transitively includes touched Launcher,
Commands, Console and Silo). The final prose-only command description was edited
after those builds; final build result is recorded below.
Integration tip remained 61af8b6e96; git merge lead/authoring was already up to date.

Focused Command laws did not run: heavy admission waited with under 10 GB free
RAM; cancelled original waiter PID 16680, then bounded retry PID 3740 refused
after 20 seconds with freeRAM=5.5GB. Both exited. World focused laws only built.
Headless debug start ef1412b340d448cf990262543efc1107 reached its 60-second
deadline at building; adapter reported any launched World stopped. No attached
World existed. Catalog generation PID 4228 waited for the shared build lease
held by that start and was cancelled; it exited. Catalog remains unregenerated,
including windowed-only Host classifications; this is unfinished WIP.
Disclosure probe has not run. No green runtime or D1-law claim is made.
`puck docs links` passed after the initial documentation updates; final rerun
is recorded below. No background watcher or GPU leg was started.
