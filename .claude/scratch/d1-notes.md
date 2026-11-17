# D1 completed

Branch lane/d1. Integrated lead/authoring through c052ed25a4 (merge eb38457955).
Core: 163e8c19ec. Verification fixes: 78c7ef5504. Final hand-back carries Brief: d1 done.

## Invariant and enforcement

WorldGrants.IsOperator is live: Console/World structurally; everyone else needs
an eligible effective Operate/all hold. Remote tier and generation are checked
at use. WorldGrants.Authority.cs validates the before/proposed grants, roles,
groups and ownership using the actor's authority before the transaction.
Only its PreparedAuthorityTransition installs live grant/group state; raw
installers are private. Composition checks batch prefixes and the final graph.
Rebuild, checkpoint restore and admission minting use the same transition.

The group schema has literal members/roles. Existing reference collection also
guards state dependencies if a membership declaration carries them; there is
no state-backed membership syntax to exercise in the current schema. The
fold's host-locus contribution trust is separate from operator authority.

## Final evidence

- Release builds: CLI, World, Commands.Tests, Server.Tests, Silo.Tests,
  World.Tests and their touched project dependencies; zero warnings/errors.
- Dashboard portal: lockfile restore, TypeScript check and production build passed.
- 25 OperatorGrantLawTests passed: seat/peer hidden reads, declaration,
  administration/programs, both template orders, retained command sessions and
  program handles, no-write cursor reseed, group/batch/ownership/laundering
  refusals, below-Replica peers, own-body consent and explicit wire tags.
  Real world.peers suppresses identity; real world.peer.disconnect closes the
  existing command session. Ownership acceptance is refused after an operator's offer.
- One SiloOperatorLawTests and two HostAudienceLawTests passed.
- Original disclosure probe passed: six runs, 702 events each to four
  principals; every variant and control equal. Kept at d1-disclosure-final.
- Operator-observer fixture: three actual Worlds, exit 2, INVALID with
  'a disclosure observer holds Operate'. Kept at d1-invalid-final.
- Headless verbs regenerated: 374 verbs. GPU-only retained entries corrected
  to Host from their declarations. audio.render is Host because it exports a
  WAV to a host path; final regeneration confirmed that classification.
- Headless debug World start 8ce6a7b383e74437a3db90775e09d67a reached process
  6872, attachment 9a81f2b6124f48eeb5430d012e53022b. Authored simulation ran;
  puck_world_stop returned exit 0. No D1 World, watcher, build or lease remains.
- Schema check and docs links passed. The lead merge's two stale generated
  prototype descriptions were regenerated along with D1 metadata.
- Exact duplicate scan against lead/authoring through HEAD at 78c7ef5504:
  85 neighbours, zero helper flags; every record read and acted on below.

## Systems considered and duplicate disposition

Reuse: role-qualified membership/ownership indexes (TryRoleGroupExpansion,
RebuildGroups), existing grant payloads and masks, WorldStateDocumentValues
reference collection, CommandRegistry, WorldDeliveryCursor, and the ordered
PeerDisconnected event. Searches by these capabilities and by codec techniques
(TryWritePeerEventEntry, WriteAdmissionVerdict, WorldWireTags) identify the
existing homes. No second operator role system, stream or transport lifecycle.

The real TryGrant/TryGrantSet duplicate is unified: a single row delegates to
the set transition. The other scan records are CommandDefinition factory use,
explicit mappings for different wire enums, serializers of different fields
through existing WireReader/Writer array and leaf codecs, unrelated switch
projections, constructor assignments, and disposal of unrelated resources.
Shared peer-entry codecs already serve checkpoint and replay. No duplicate is
parked in the work packet. The authority token validates a world authority
transition; filesystem InstallTransaction and field-assignment constructors
cannot own that validation.

## Resolved runtime findings and process notes

The first debug boot exposed missing explicit budgets on authored seat Drive
rows. Migrated those exhibits and removed a duplicate skirmish state grant.
An asynchronous start discarded its build after source edits during the queue;
delegate now says to finish source/catalog changes before starting a World.
The final run waited through the lead's quiet reservation and then completed.
Keep each active private CLI copy immutable on Windows. Final tools ran from
d1-cli-final; logs are d1-*.log here. No GPU leg was requested or run.
