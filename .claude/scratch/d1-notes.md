# D1 continuation 2

Branch lane/d1. Integrated lead/authoring through c052ed25a4 (merge eb38457955).
Core implementation is committed as 163e8c19ec; final verification fixes follow.

## Invariant and enforcement

WorldGrants.IsOperator is the live predicate: Console/World structurally,
otherwise an eligible effective Operate/all hold. Remote entitlement and
generation are checked at use. WorldGrants.Authority.cs validates authority
at transaction start against the before/proposed grants, roles and ownership.
Only its PreparedAuthorityTransition installs live grant/group state. Raw
installers are private. Composition checks batch prefixes and the final graph.
Rebuild, checkpoint restoration and admission minting use the same transition.

Reuse: role-qualified membership/ownership indexes, grant payloads and masks,
WorldStateDocumentValues reference collection, CommandRegistry dispatch,
WorldDeliveryCursor, and the existing ordered PeerDisconnected event. No new
operator role system, observation stream or transport lifecycle was introduced.
The group schema currently has literal members/roles; reference collection
protects any state-backed holders in that graph as well as the literal rows.
The fold's host-locus contributor trust remains a separate mechanism.

## Evidence so far

- Release builds passed for World, CLI, Server.Tests and Silo.Tests.
- 25 OperatorGrantLawTests passed after the merge (seat/peer administration and program
  authoring, both template orders, retained command sessions, cursor reseed,
  group/batch/laundering refusals, below-Replica peers, own-body consent,
  retained peer program handles, wire tags, real peer-report identity redaction
  and the disconnect command closing an existing command session).
  The ownership law also executes an operator's offer and refuses the
  recipient's acceptance, exercising the effective-authority guard itself.
- Two HostAudienceLawTests passed after the merge.
- SiloOperatorLawTests passed: discovery/dispatch share SiloControlAdmission;
  live Operate admits operator commands and never Host; revoke/disconnect deny.
- Regenerated schema, model shape and dashboard types with the private CLI.
- First headless debug start c568509a8e2544b58401cc40121e6536 reached process
  31020, which exited on an authored seat Drive row lacking a budget. Updated
  the affected debug exhibits to explicit budgets, and removed the skirmish
  fixture's duplicate seat state grant. This was a real migration defect,
  not memory admission. The process exited; no World is running.
- CLI copy: .claude/scratch/d1-cli. Build/test logs: d1-*-build.log,
  d1-laws.log and d1-silo-laws.log in this directory. No watcher or GPU hold.

## Remaining finish work

Finish observer-INVALID run and final metadata/docs cleanup. Current post-merge
builds passed for CLI, Server.Tests, Silo.Tests, Commands.Tests and World.Tests.
Headless debug start 2d04f7d80f524ffc9a1d12996962daca was discarded because
the source changed during its queued build; no World was started. The original
disclosure probe passed: six runs, 702 events each to four principals, every
variant and control equal. Observer-INVALID is queued behind lead window2.
Headless verbs generation passed (374 verbs). Corrected the retained GPU-only
catalog entries to Host from their declarations; audio.render's host-path export
was a missed Host classification, now fixed. Rebuild and regenerate once more,
then start/stop the fixed source tree. Finish
duplicates --against lead/authoring --head HEAD (act on every hit), docs links.
Final commit carries Brief: d1 done only after the finish line is observed.

## Duplicate scan disposition and systems considered

Read every record of the working-tree scan: 81 neighbours, zero helper flags.
The real TryGrant/TryGrantSet overlap is unified: the single row delegates to
the same set transition. Other matches are command registration through the
existing CommandDefinition factories, explicit wire tags for different enums,
checkpoint field serialization through existing WireReader/Writer array and
leaf codecs, switch projections of unrelated data, constructor field assignment,
and disposal of unrelated owned resources. They do not implement the same
mechanism. Shared peer-entry codecs already serve checkpoint and replay.
No duplicate is parked in the work packet.

Capability/technique searches: TryRoleGroupExpansion and RebuildGroups identify
the existing role/ownership home; CollectReferencedRows supplies authored-state
dependency traversal; CommandAudience/CommandRegistry supplies command admission;
WorldDeliveryCursor supplies image reseeding; PeerDisconnected and Dispose
supply transport teardown; TryWritePeerEventEntry, WriteAdmissionVerdict and
WorldWireTags identify shared codecs. The scan's generic-looking transition token
is the authority door's one-use result, not another transaction framework. Its
validation cannot be replaced by a filesystem InstallTransaction or a constructor
that merely assigns fields. Documentation links passed after updating removed
raw-API names and two shifted citations.
